using Mutagen.Bethesda;
using Mutagen.Bethesda.Pex;
using Mutagen.Bethesda.Plugins;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for the native-function pairing audit (plan
/// dev/plans/SKSE_NATIVE_PAIRING_AUDIT_PLAN_2026-07-16.md, Wave 1.4). Three parts, no live order:
///
///   Part 1 — the PURE extractor (<see cref="NativePairing.ExtractNativeClasses"/>) over a synthetic PexFile:
///   the native flag is RAW BIT1 (Mutagen's enum names sit one off — the documented trap this arm exists to pin),
///   Global (bit0) alone is NOT native, both-bits counts, a native property accessor surfaces as Prop.Get, and an
///   object with no natives yields NOTHING (the common case).
///
///   Part 2 — the classification + pairing primitives (service internals via InternalsVisibleTo):
///   IsOfficialArchive (ini-base marker / BaseMaster owner / third-party owner), HasOfficialProvider (an official
///   BSA anywhere in the chain marks ENGINE even under a winning loose override — the SKSE-overrides-Actor.pex
///   fixture), the §4c ladder (same-mod / chain-mod / unpaired), and the runtime compare
///   (<see cref="SksePluginReader.RuntimeCompatible"/> — zero-padded numeric equality, garbage never PASSES a lock).
///
///   Part 3 — the WIRE renderer (<c>NativePairingWire.Render</c>) over synthetic
///   <see cref="NativePairingAuditData"/>: PAIRED-BUT-DEAD leads and adjudicates a locked mismatch when the runtime
///   is known, the runtime-unknown degrade says 'verify', UNPAIRED is framed a verify flag (never 'broken'),
///   the baseline accounting carries the no-loader sanity note, the all-clear branch, filter= full detail, and the
///   did-you-mean pool spanning every Match axis (the tier-B lesson).
///
/// Run: dotnet run --project src/housecarl-generator -- native-pairing-guard
/// </summary>
public static class NativePairingProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — native-function pairing audit  ################");
        Console.WriteLine();

        int fails = 0;
        void Check(string label, bool ok) { Console.WriteLine($"   {(ok ? "PASS" : "FAIL")}  {label}"); if (!ok) fails++; }

        // ════ Part 1 — the pure .pex native-class extractor ════
        Console.WriteLine("── Part 1: ExtractNativeClasses over a synthetic PexFile ──");
        {
            var pex = new PexFile(GameCategory.Skyrim);
            var obj = new PexObject { Name = "StorageUtil" };
            var st = new PexObjectState();
            st.Functions.Add(Fn("SetIntValue", flags: 0x3));   // Global|Native — both bits
            st.Functions.Add(Fn("GetIntValue", flags: 0x2));   // Native only (bit1 — THE off-by-one pin)
            st.Functions.Add(Fn("LocalHelper", flags: 0x1));   // Global only — NOT native
            st.Functions.Add(Fn("PlainFunc", flags: 0x0));     // neither
            obj.States.Add(st);
            pex.Objects.Add(obj);

            var plain = new PexObject { Name = "PlainQuestScript" };
            var pst = new PexObjectState();
            pst.Functions.Add(Fn("OnInit", flags: 0x0));
            plain.States.Add(pst);
            pex.Objects.Add(plain);

            var decls = NativePairing.ExtractNativeClasses(pex);
            Check("one native class extracted (the all-Papyrus object yields nothing)",
                decls.Count == 1 && decls[0].ClassName == "StorageUtil");
            Check("native = raw bit1: SetIntValue + GetIntValue in, Global-only + plain OUT",
                decls.Count == 1 && decls[0].NativeFunctions.Count == 2
                && decls[0].NativeFunctions.Contains("SetIntValue") && decls[0].NativeFunctions.Contains("GetIntValue"));
        }
        {
            // A native-flagged property accessor is a native declaration too — surfaced as Prop.Get, never dropped.
            var pex = new PexFile(GameCategory.Skyrim);
            var obj = new PexObject { Name = "NativeProps" };
            obj.Properties.Add(new PexObjectProperty { Name = "Version", ReadHandler = new PexObjectFunction { Flags = (FunctionFlags)0x2 } });
            pex.Objects.Add(obj);
            var decls = NativePairing.ExtractNativeClasses(pex);
            Check("native property accessor → 'Version.Get' declared",
                decls.Count == 1 && decls[0].NativeFunctions.SequenceEqual(new[] { "Version.Get" }));
        }

        // ════ Part 2 — classification + pairing primitives ════
        Console.WriteLine();
        Console.WriteLine("── Part 2: provenance anchor, ladder, runtime compare ──");
        {
            var baseMasters = Mutagen.Bethesda.Plugins.Implicits.Get(Mutagen.Bethesda.GameRelease.SkyrimSE).BaseMasters;
            Check("ini-base archive (Skyrim - Misc.bsa) is OFFICIAL",
                LoadOrderService.IsOfficialArchive(new ActiveArchive(@"D:\g\Data\Skyrim - Misc.bsa", ArchiveDiscovery.IniArchiveOwner, 0), baseMasters));
            Check("Dawnguard.esm-owned archive is OFFICIAL (BaseMasters, by construction)",
                LoadOrderService.IsOfficialArchive(new ActiveArchive(@"D:\g\Data\Dawnguard.bsa", "Dawnguard.esm", 5), baseMasters));
            Check("a mod plugin's archive is NOT official",
                !LoadOrderService.IsOfficialArchive(new ActiveArchive(@"D:\m\Campfire.bsa", "Campfire.esm", 40), baseMasters));

            var official = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Skyrim - Misc.bsa" };
            // THE fixture: SKSE's loose Actor.pex WINS over the vanilla archive copy — still ENGINE.
            Check("loose override winning over an official BSA copy → still ENGINE (chain presence)",
                LoadOrderService.HasOfficialProvider(new[]
                {
                    new SkseProvider("Skyrim Script Extender (SKSE64)", "loose"),
                    new SkseProvider("Skyrim - Misc.bsa", "BSA"),
                }, official));
            Check("loose-only chain (StringUtil.pex) → NOT engine",
                !LoadOrderService.HasOfficialProvider(new[] { new SkseProvider("Skyrim Script Extender (SKSE64)", "loose") }, official));
            Check("a mod BSA in the chain is not mistaken for official",
                !LoadOrderService.HasOfficialProvider(new[] { new SkseProvider("Campfire.bsa", "BSA") }, official));
        }
        {
            var dll = new NativePairedDll(@"SKSE\Plugins\PapyrusUtil.dll", "PapyrusUtil.dll", "", "PapyrusUtil AE", null, null);
            var modDlls = new Dictionary<string, List<NativePairedDll>>(StringComparer.OrdinalIgnoreCase)
            { ["PapyrusUtil AE"] = new() { dll } };

            var (r1, m1, d1) = LoadOrderService.Ladder(new[] { new SkseProvider("PapyrusUtil AE", "loose") }, modDlls);
            Check("rung 1: winning provider ships the DLL → SameMod",
                r1 == NativePairingRung.SameMod && m1 == "PapyrusUtil AE" && d1.Count == 1);

            // The Campfire fixture: a bundler wins nothing here — PapyrusUtil wins, but flip it: Campfire wins the
            // script, PapyrusUtil (with the DLL) sits beneath in the chain → ChainMod.
            var (r2, m2, _) = LoadOrderService.Ladder(new[]
            {
                new SkseProvider("Campfire", "loose"),
                new SkseProvider("PapyrusUtil AE", "loose"),
            }, modDlls);
            Check("rung 2: framework beneath the winner in the chain → ChainMod (the bundling case)",
                r2 == NativePairingRung.ChainMod && m2 == "PapyrusUtil AE");

            var (r3, m3, d3) = LoadOrderService.Ladder(new[] { new SkseProvider("Some Scripts-Only Mod", "loose") }, modDlls);
            Check("rung 3: nobody in the chain ships a DLL → Unpaired",
                r3 == NativePairingRung.Unpaired && m3 is null && d3.Count == 0);
        }
        {
            // BSA→shipper translation (live-gate finding: moreHUD's scripts ride its BSA while the DLL is loose in
            // the SAME mod — the archive filename must translate to the mod for pairing identity).
            Check("archive under mods\\<mod>\\ → that mod",
                LoadOrderService.ShipperOfArchivePath(@"E:\mo2\mods\moreHUD SE\AHZmoreHUD.bsa", @"E:\mo2\mods", @"E:\mo2\overwrite", @"D:\g\Data") == "moreHUD SE");
            Check("archive in the overwrite layer → 'overwrite'",
                LoadOrderService.ShipperOfArchivePath(@"E:\mo2\overwrite\X.bsa", @"E:\mo2\mods", @"E:\mo2\overwrite", @"D:\g\Data") == "overwrite");
            Check("archive in game Data → 'Data'",
                LoadOrderService.ShipperOfArchivePath(@"D:\g\Data\Skyrim - Misc.bsa", @"E:\mo2\mods", @"E:\mo2\overwrite", @"D:\g\Data") == "Data");
            Check("archive nowhere under the roots → null (no translation)",
                LoadOrderService.ShipperOfArchivePath(@"C:\elsewhere\X.bsa", @"E:\mo2\mods", @"E:\mo2\overwrite", @"D:\g\Data") is null);
        }
        {
            Check("versions equal under zero-padding: 1.6.1170 == 1.6.1170.0",
                SksePluginReader.VersionsEqual("1.6.1170", "1.6.1170.0"));
            Check("versions differ: 1.6.640 != 1.6.1170.0",
                !SksePluginReader.VersionsEqual("1.6.640", "1.6.1170.0"));
            Check("garbage segment never PASSES a lock (Q3)",
                !SksePluginReader.VersionsEqual("1.6.x", "1.6.0"));
            var locked = Ver(independent: false, compat: new[] { "1.5.97", "1.6.640" });
            Check("locked plugin + unlisted runtime → NOT compatible",
                !SksePluginReader.RuntimeCompatible(locked, "1.6.1170.0"));
            Check("locked plugin + listed runtime (padded) → compatible",
                SksePluginReader.RuntimeCompatible(locked, "1.6.640.0"));
            Check("version-independent plugin → compatible anywhere",
                SksePluginReader.RuntimeCompatible(Ver(independent: true, compat: Array.Empty<string>()), "9.9.9"));
        }

        // ════ Part 3 — the wire renderer over synthetic data ════
        Console.WriteLine();
        Console.WriteLine("── Part 3: NativePairingWire.Render arms ──");

        var lockedInfo = new SksePluginReader.SksePluginInfo("OldPlugin.dll", SksePluginReader.SksePluginKind.Modern, true,
            Ver(independent: false, compat: new[] { "1.5.97" }), null);
        var indepInfo = new SksePluginReader.SksePluginInfo("PapyrusUtil.dll", SksePluginReader.SksePluginKind.Modern, true,
            Ver(independent: true, compat: Array.Empty<string>()), null);

        NativeClassEntry Cls(string name, NativeProvenance prov, NativePairingRung? rung, string? pairedMod,
            IReadOnlyList<NativePairedDll>? dlls = null, string? winner = "SomeMod") =>
            new($@"Scripts\{name}.pex", name, 2, new[] { "FnA", "FnB" }, winner, "loose", 1,
                new[] { new SkseProvider(winner ?? "(none)", "loose") }, prov, rung, pairedMod, dlls ?? Array.Empty<NativePairedDll>());

        NativePairingAuditData Data(IReadOnlyList<NativeClassEntry> classes, string? runtime, bool loaderSeen = true,
            IReadOnlyList<NativeUnreadablePex>? unreadable = null) =>
            new(classes, 1000, unreadable ?? Array.Empty<NativeUnreadablePex>(), loaderSeen, runtime,
                Array.Empty<string>(), false, Array.Empty<string>(), "TestProfile");

        {
            // Arm A: locked mismatch with the runtime KNOWN → PAIRED BUT DEAD leads, adjudicated.
            var deadDll = new NativePairedDll(@"SKSE\Plugins\OldPlugin.dll", "OldPlugin.dll", "", "OldMod", lockedInfo, null);
            var s = Render(Data(new[] { Cls("OldUtil", NativeProvenance.ThirdParty, NativePairingRung.SameMod, "OldMod", new[] { deadDll }) }, "1.6.1170.0"));
            Check("A: locked-mismatch + known runtime → 'PAIRED BUT DEAD' + 'will NOT load' + both versions named",
                s.Contains("PAIRED BUT DEAD") && s.Contains("will NOT load") && s.Contains("1.5.97") && s.Contains("1.6.1170.0"));
        }
        {
            // Arm B: same data, runtime UNKNOWN → the honest degrade ('verify'), NOT a dead claim.
            var deadDll = new NativePairedDll(@"SKSE\Plugins\OldPlugin.dll", "OldPlugin.dll", "", "OldMod", lockedInfo, null);
            var s = Render(Data(new[] { Cls("OldUtil", NativeProvenance.ThirdParty, NativePairingRung.SameMod, "OldMod", new[] { deadDll }) }, runtime: null));
            Check("B: runtime unknown → degrades to 'verify', never claims dead",
                !s.Contains("PAIRED BUT DEAD") && s.Contains("verify") && s.Contains("could not be resolved"));
        }
        {
            // Arm C: a static blocker (BSA-only) is dead regardless of runtime knowledge.
            var bsaDll = new NativePairedDll(@"SKSE\Plugins\X.dll", "X.dll", "", "XMod", null,
                "provided only inside a BSA — the SKSE loader scans loose DLLs only, so it will not load");
            var s = Render(Data(new[] { Cls("XUtil", NativeProvenance.ThirdParty, NativePairingRung.SameMod, "XMod", new[] { bsaDll }) }, runtime: null));
            Check("C: static blocker (BSA-only) → DEAD even with runtime unknown",
                s.Contains("PAIRED BUT DEAD") && s.Contains("[DEAD]") && s.Contains("loose DLLs only"));
        }
        {
            // Arm D: UNPAIRED is a verify flag, framed as such; healthy + baseline accounting present; no-loader note.
            var okDll = new NativePairedDll(@"SKSE\Plugins\PapyrusUtil.dll", "PapyrusUtil.dll", "", "PapyrusUtil AE", indepInfo, null);
            var s = Render(Data(new[]
            {
                Cls("Actor", NativeProvenance.Engine, null, null, winner: "Skyrim Script Extender (SKSE64)"),
                Cls("StringUtil", NativeProvenance.SkseCore, null, null, winner: "Skyrim Script Extender (SKSE64)"),
                Cls("StorageUtil", NativeProvenance.ThirdParty, NativePairingRung.SameMod, "PapyrusUtil AE", new[] { okDll }, winner: "PapyrusUtil AE"),
                Cls("OrphanUtil", NativeProvenance.ThirdParty, NativePairingRung.Unpaired, null, winner: "Scripts Only Mod"),
            }, "1.6.1170.0", loaderSeen: false));
            Check("D: UNPAIRED framed as verify (explicitly NOT 'broken'), with the declaration-copy explanation",
                s.Contains("UNPAIRED") && s.Contains("VERIFY flag") && s.Contains("not 'broken'") && s.Contains("declaration copy"));
            Check("D: baseline accounting (1 engine + 1 SKSE-core) + paired-healthy group",
                s.Contains("1 engine class(es)") && s.Contains("1 SKSE-core class(es)") && s.Contains("PapyrusUtil AE: StorageUtil"));
            Check("D: no-loader sanity note fires when SKSE-core classes exist without a visible loader",
                s.Contains("no skse64 loader is visible"));
        }
        {
            // Arm E: the all-clear branch + unreadable pex is a named note.
            var okDll = new NativePairedDll(@"SKSE\Plugins\PapyrusUtil.dll", "PapyrusUtil.dll", "", "PapyrusUtil AE", indepInfo, null);
            var s = Render(Data(new[] { Cls("StorageUtil", NativeProvenance.ThirdParty, NativePairingRung.SameMod, "PapyrusUtil AE", new[] { okDll }) }, "1.6.1170.0",
                unreadable: new[] { new NativeUnreadablePex(@"Scripts\Broken.pex", "BadMod", "Mutagen cannot read it (EndOfStreamException)") }));
            Check("E: all third-party healthy → the ✓ all-clear headline",
                s.Contains("✓ every third-party native class pairs"));
            Check("E: unreadable .pex is a NAMED note, not counted clean",
                s.Contains("Broken.pex") && s.Contains("NOT counted as native-free"));
        }
        {
            // Arm F: filter= full detail + the did-you-mean pool spans class/mod/DLL axes.
            var okDll = new NativePairedDll(@"SKSE\Plugins\PapyrusUtil.dll", "PapyrusUtil.dll", "", "PapyrusUtil AE", indepInfo, null);
            var data = Data(new[] { Cls("StorageUtil", NativeProvenance.ThirdParty, NativePairingRung.SameMod, "PapyrusUtil AE", new[] { okDll }, winner: "PapyrusUtil AE") }, "1.6.1170.0");
            var s = Render(data, "storageutil");
            Check("F: filter= shows full detail (functions, rung, DLL verdict)",
                s.Contains("FnA, FnB") && s.Contains("paired to its own provider") && s.Contains("[LOADS]"));
            var miss = Render(data, "StorageUtl");   // typo'd CLASS name (edit distance 1) → suggestion from the pool
            Check("F: typo'd filter → did-you-mean from the multi-axis pool",
                miss.Contains("no native-declaring class matched") && miss.Contains("did you mean", StringComparison.OrdinalIgnoreCase)
                && miss.Contains("StorageUtil", StringComparison.OrdinalIgnoreCase));
        }

        Console.WriteLine();
        Console.WriteLine($"=== native-pairing-guard: {(fails == 0 ? "PASS" : "FAIL")} ({fails} failing) ===");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>The MANUAL real-data harness (the live gate, tier-B RunReal pattern): runs the whole pairing audit
    /// against a live MO2 instance and prints the render + factual timing. NOT part of ci-all.</summary>
    public static int RunReal(string[] args)
    {
        string? mo2 = ArgVal(args, "--mo2");
        string? filter = ArgVal(args, "--filter");
        int max = int.TryParse(ArgVal(args, "--max"), out var m) ? m : 80_000;
        if (mo2 is null) { Console.WriteLine("native-pairing-real needs --mo2 <MO2 instance folder>"); return 2; }

        var store = new UserConfigStore(Path.Combine(Path.GetTempPath(), "hc-native-pairing-" + Guid.NewGuid().ToString("N") + ".json"));
        using var svc = LoadOrderService.WithInstance(mo2, 0, store);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var data = svc.NativePairingAudit();
        sw.Stop();

        Console.WriteLine(NativePairingWire.Render(data, filter, max));
        Console.WriteLine($"\n[timing] NativePairingAudit over {data.PexScanned} compiled scripts " +
            $"({data.Classes.Count} native classes, {data.Unreadable.Count} unreadable, runtime {data.InstalledRuntime ?? "(unresolved)"}) " +
            $"in {sw.ElapsedMilliseconds} ms");
        return 0;
    }

    static string? ArgVal(string[] a, string key)
    {
        int i = Array.IndexOf(a, key);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    static PexObjectNamedFunction Fn(string name, uint flags) =>
        new() { FunctionName = name, Function = new PexObjectFunction { Flags = (FunctionFlags)flags } };

    static SksePluginReader.SkseVersionInfo Ver(bool independent, IReadOnlyList<string> compat) =>
        new("Test Plugin", "tester", "", "1.0.0",
            UsesAddressLibrary: independent, UsesSignatureScanning: false,
            UsesUpdatedStructs: false, DeclaresNoStructs: false,
            CompatibleVersions: compat, MinimumXseVersion: null);

    // The REAL renderer, internal to housecarl-mcp — reachable via InternalsVisibleTo("housecarl-generator")
    // (the PR #208 Part-3 mechanism).
    static string Render(NativePairingAuditData d, string? filter = null) => NativePairingWire.Render(d, filter, 80_000);
}

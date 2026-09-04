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
    [CiProbe("native-pairing-guard")]
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
            static PlacementSource Loose(string mod) => new(mod, AssetKind.Loose, @"D:\x", null, "");
            static PlacementSource Bsa(string archive) => new(archive, AssetKind.Bsa, null, @"D:\x.bsa", "");
            // THE fixture: SKSE's loose Actor.pex WINS over the vanilla archive copy — still ENGINE. Keys on the
            // AssetKind ENUM, never the render label (review finding).
            Check("loose override winning over an official BSA copy → still ENGINE (chain presence)",
                LoadOrderService.HasOfficialSource(new[] { Loose("Skyrim Script Extender (SKSE64)"), Bsa("Skyrim - Misc.bsa") }, official));
            Check("loose-only chain (StringUtil.pex) → NOT engine",
                !LoadOrderService.HasOfficialSource(new[] { Loose("Skyrim Script Extender (SKSE64)") }, official));
            Check("a mod BSA in the chain is not mistaken for official",
                !LoadOrderService.HasOfficialSource(new[] { Bsa("Campfire.bsa") }, official));

            var shipper = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["AHZmoreHUD.bsa"] = "moreHUD SE" };
            Check("BSA source translates to its shipping mod (pairing identity)",
                LoadOrderService.PairingIdentity(Bsa("AHZmoreHUD.bsa"), shipper) == "moreHUD SE");
            Check("loose source's identity is its provider name",
                LoadOrderService.PairingIdentity(Loose("PapyrusUtil AE"), shipper) == "PapyrusUtil AE");
            Check("an untranslatable archive keeps its own name",
                LoadOrderService.PairingIdentity(Bsa("Unknown.bsa"), shipper) == "Unknown.bsa");
        }
        {
            var okDll = new NativePairedDll(@"SKSE\Plugins\PapyrusUtil.dll", "PapyrusUtil.dll", "", "PapyrusUtil AE", null, null);
            var deadDll = new NativePairedDll(@"SKSE\Plugins\Helper\junk.dll", "junk.dll", "Helper", "Bundler",
                null, "in subfolder 'Helper' — not on SKSE's loader path");
            var modDlls = new Dictionary<string, List<NativePairedDll>>(StringComparer.OrdinalIgnoreCase)
            { ["PapyrusUtil AE"] = new() { okDll }, ["Bundler"] = new() { deadDll } };

            var (r1, m1, d1) = LoadOrderService.Ladder(new[] { "PapyrusUtil AE" }, modDlls);
            Check("rung 1: winning identity ships the DLL → SameMod",
                r1 == NativePairingRung.SameMod && m1 == "PapyrusUtil AE" && d1.Count == 1);

            var (r2, m2, _) = LoadOrderService.Ladder(new[] { "Campfire", "PapyrusUtil AE" }, modDlls);
            Check("rung 2: framework beneath the winner in the chain → ChainMod (the bundling case)",
                r2 == NativePairingRung.ChainMod && m2 == "PapyrusUtil AE");

            var (r3, m3, d3) = LoadOrderService.Ladder(new[] { "Some Scripts-Only Mod" }, modDlls);
            Check("rung 3: nobody in the chain ships a DLL → Unpaired",
                r3 == NativePairingRung.Unpaired && m3 is null && d3.Count == 0);

            // Review finding: a bundler whose only candidate is statically DEAD must not mask the loadable framework
            // beneath it (the false PAIRED-BUT-DEAD class); with no loadable candidate anywhere, the shallowest pairs.
            var (r4, m4, _) = LoadOrderService.Ladder(new[] { "Bundler", "PapyrusUtil AE" }, modDlls);
            Check("dead-candidate bundler does NOT mask the loadable framework beneath → ChainMod to the framework",
                r4 == NativePairingRung.ChainMod && m4 == "PapyrusUtil AE");
            var (r5, m5, _) = LoadOrderService.Ladder(new[] { "Bundler", "Scriptless" }, modDlls);
            Check("no loadable candidate anywhere → the shallowest with ANY candidate pairs (its deadness is the finding)",
                r5 == NativePairingRung.SameMod && m5 == "Bundler");

            // The Classify decision order: engine wins outright; pairing beats the SKSE-CORE rescue; the rescue takes
            // an UNPAIRED class whose WINNING identity ships an engine-class copy; a non-pool winner stays third-party.
            var pool = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Skyrim Script Extender (SKSE64)" };
            Check("Classify: engine short-circuits everything",
                LoadOrderService.Classify(true, new[] { "PapyrusUtil AE" }, modDlls, pool).Provenance == NativeProvenance.Engine);
            Check("Classify: pairing evidence beats the rescue",
                LoadOrderService.Classify(false, new[] { "PapyrusUtil AE" }, modDlls, pool) is { Provenance: NativeProvenance.ThirdParty, Rung: NativePairingRung.SameMod });
            Check("Classify: unpaired + winner in the engine-provider pool → SKSE CORE (the StringUtil rescue)",
                LoadOrderService.Classify(false, new[] { "Skyrim Script Extender (SKSE64)" }, modDlls, pool).Provenance == NativeProvenance.SkseCore);
            Check("Classify: unpaired + winner NOT in the pool → stays UNPAIRED third-party",
                LoadOrderService.Classify(false, new[] { "Scripts Only Mod" }, modDlls, pool) is { Provenance: NativeProvenance.ThirdParty, Rung: NativePairingRung.Unpaired });
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
            Check("AE runtime boundary: 1.6.1170 IS AE, 1.5.97 is NOT, garbage is NOT (unknown never claims)",
                SksePluginReader.IsAeRuntime("1.6.1170.0") && !SksePluginReader.IsAeRuntime("1.5.97.0") && !SksePluginReader.IsAeRuntime("bogus"));
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
            new($@"Scripts\{name}.pex", name, new[] { "FnA", "FnB" },
                new[] { new SkseProvider(winner ?? "(none)", "loose") }, prov, rung, pairedMod, dlls ?? Array.Empty<NativePairedDll>());

        NativePairingAuditData Data(IReadOnlyList<NativeClassEntry> classes, string? runtime, bool? loaderSeen = true,
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
            // Arm A2 (tier D, Aaron-go 2026-07-17): the DEBUG-build blocker — the audit's 7th, and the one that used to
            // read as healthy. This DLL is loose, top-level, x64, readable and version-INDEPENDENT: every other check
            // passes it, so before this the audit rendered [LOADS] while the inventory family called the same DLL broken.
            // The blocker rides LoadBlocker, so it lands in PAIRED-BUT-DEAD by construction — no Judge arm needed.
            // Drives the REAL service chain (LoadOrderService.LooseDllBlocker), not a hand-built blocker — the wiring is
            // the one link no live gate can reach here (ARR has zero debug plugins; the dev box HAS the debug runtime,
            // so it can't produce the dead path either), and a hand-built fixture would pin the render while leaving
            // the production line uncovered. The probe is injected because no single machine exercises both halves.
            var dbgInfo = new SksePluginReader.SksePluginInfo("Dbg.dll", SksePluginReader.SksePluginKind.Modern, true,
                Ver(independent: true, compat: Array.Empty<string>()), null, new[] { "kernel32.dll", "vcruntime140d.dll" });
            var crtBlocker = LoadOrderService.LooseDllBlocker(dbgInfo, _ => false);
            Check("A2a: the service's loose-DLL chain blocks a debug build (the production wiring, not a fixture)",
                crtBlocker is { Length: > 0 });
            Check("A2b: …and on a box WITH the debug runtime the same chain blocks nothing (it truly loads there)",
                LoadOrderService.LooseDllBlocker(dbgInfo, _ => true) is null);
            // dbgInfo, NOT indepInfo: the rendered candidate must carry the SAME info the blocker was derived from.
            // They are equivalent today (both version-independent) and the verdict reads LoadBlocker, so the arm passes
            // either way — but if Judge ever re-derived from Info instead of trusting the blocker, an indepInfo fixture
            // would keep passing while production broke. The fixture should not be the reason a regression hides.
            var dbgDll = new NativePairedDll(@"SKSE\Plugins\Dbg.dll", "Dbg.dll", "", "DbgMod", dbgInfo, crtBlocker);
            var s = Render(Data(new[] { Cls("DbgUtil", NativeProvenance.ThirdParty, NativePairingRung.SameMod, "DbgMod", new[] { dbgDll }) }, "1.6.1170.0"));
            Check("A2: a DEBUG-built version-INDEPENDENT DLL → 'PAIRED BUT DEAD', not healthy (the tier-D blind spot)",
                s.Contains("PAIRED BUT DEAD") && s.Contains("DEBUG build") && s.Contains("error 126")
                && !s.Contains("paired healthy (1"));
        }
        {
            // Arm A3 (#417): the SAME debug build on the AUTHOR's own box, where the debug runtime resolves — so
            // LooseDllBlocker returns null and every other check passes it. The verdict is right (it does load here) and
            // stays LOADS; what used to be missing is any mention of the debug build, leaving the author who ships one
            // told everything is healthy. The filter= view is the one that renders a healthy class's DLL line.
            var dbgInfo = new SksePluginReader.SksePluginInfo("Dbg.dll", SksePluginReader.SksePluginKind.Modern, true,
                Ver(independent: true, compat: Array.Empty<string>()), null, new[] { "kernel32.dll", "vcruntime140d.dll" });
            Check("A3a: on a box WITH the debug runtime the DLL has no blocker (the author's own machine)",
                LoadOrderService.LooseDllBlocker(dbgInfo, _ => true) is null);
            var dbgDll = new NativePairedDll(@"SKSE\Plugins\Dbg.dll", "Dbg.dll", "", "DbgMod", dbgInfo, null);
            var data = Data(new[] { Cls("DbgUtil", NativeProvenance.ThirdParty, NativePairingRung.SameMod, "DbgMod", new[] { dbgDll }) }, "1.6.1170.0");
            var s = Render(data, filter: "DbgUtil");
            Check("A3b: it still renders [LOADS] — the verdict is untouched, because it does load here",
                s.Contains("[LOADS]") && !s.Contains("[DEAD]"));
            Check("A3c: …and the line now names the debug CRT and error 126 for everyone else (#417)",
                s.Contains("vcruntime140d.dll") && s.Contains("error 126") && s.Contains("debug CRT"));
            // The DEFAULT view is the one the author runs first, and it prints no DLL lines for a loading class — so
            // the finding has to reach it as its own section, or #417's whole scenario survives the fix.
            var whole = Render(data);
            Check("A3e: the default (unfiltered) view names the debug build as a finding, not only under filter=",
                whole.Contains("DEBUG BUILD") && whole.Contains("vcruntime140d.dll") && whole.Contains("error 126"));
            Check("A3f: …and it no longer claims the clean bill of health over that same file",
                !whole.Contains("nothing dead, nothing unpaired") && !whole.Contains("paired healthy (1 class"));
            // The healthy roster must not gain the sentence: it lists class names, not DLL lines, so a leak there would
            // mean the note escaped Judge into the summary.
            var cleanData = Data(new[] { Cls("CleanUtil", NativeProvenance.ThirdParty, NativePairingRung.SameMod, "CleanMod",
                new[] { new NativePairedDll(@"SKSE\Plugins\PapyrusUtil.dll", "PapyrusUtil.dll", "", "CleanMod", indepInfo, null) }) }, "1.6.1170.0");
            Check("A3d: a clean version-independent DLL gets no debug note",
                !Render(cleanData, filter: "CleanUtil").Contains("debug CRT"));
            Check("A3g: …and it still earns the clean bill of health in the default view",
                Render(cleanData).Contains("nothing dead, nothing unpaired")
                && !Render(cleanData).Contains("DEBUG BUILD"));
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
            // Arm C2 (review finding): a LegacyQuery pairing on an AE runtime is DEAD (the AE loader loads only
            // version-data plugins); on an SE runtime it loads; with the runtime unknown it is a VERIFY, never a claim.
            var legacyInfo = new SksePluginReader.SksePluginInfo("OldSe.dll", SksePluginReader.SksePluginKind.LegacyQuery, true, null, "legacy");
            var legacyDll = new NativePairedDll(@"SKSE\Plugins\OldSe.dll", "OldSe.dll", "", "OldSeMod", legacyInfo, null);
            var cls = Cls("OldSeUtil", NativeProvenance.ThirdParty, NativePairingRung.SameMod, "OldSeMod", new[] { legacyDll });
            var ae = Render(Data(new[] { cls }, "1.6.1170.0"));
            Check("C2: LegacyQuery + AE runtime → PAIRED BUT DEAD (query-only plugins don't load on AE)",
                ae.Contains("PAIRED BUT DEAD") && ae.Contains("query-only"));
            var se = Render(Data(new[] { cls }, "1.5.97.0"));
            Check("C2: LegacyQuery + SE runtime → healthy (loads on SE)",
                se.Contains("✓") && se.Contains("OldSeMod: OldSeUtil"));
            var unk = Render(Data(new[] { cls }, runtime: null));
            Check("C2: LegacyQuery + unknown runtime → verify, never a dead claim",
                !unk.Contains("PAIRED BUT DEAD") && unk.Contains("verify"));
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
            Check("E: the all-clear headline is QUALIFIED by the unexamined unreadables (review finding)",
                s.Contains("1 unreadable .pex NOT examined"));
        }
        {
            // Arm G (review finding): the loader note is tri-state — false = the definite checked-and-absent note,
            // null = "could not check", never the definite claim off a failed check (Q3).
            var core = Cls("StringUtil", NativeProvenance.SkseCore, null, null, winner: "Skyrim Script Extender (SKSE64)");
            var absent = Render(Data(new[] { core }, "1.6.1170.0", loaderSeen: false));
            Check("G: loaderSeen=false → the definite no-loader note",
                absent.Contains("no skse64 loader is visible"));
            var unchecked_ = Render(Data(new[] { core }, "1.6.1170.0", loaderSeen: null));
            Check("G: loaderSeen=null → 'could not be checked', never the definite absence claim",
                !unchecked_.Contains("no skse64 loader is visible") && unchecked_.Contains("could not be checked"));
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

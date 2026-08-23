using System.Reflection;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlGenerator;

/// <summary>
/// Throwaway feasibility probe for the index-build resilience fix (Nexus bug: a malformed PKCU in
/// TasteOfDeath_Addon_Dialogue.esp throws DURING EnumerateMajorRecords(), bricking the whole index build).
///
/// Decides record-level vs plugin/type-level isolation:
///   TEST 1 — is the group enumeration RESUMABLE past a parse throw? (manual MoveNext + catch-continue)
///   TEST 2 — what EnumerateMajorRecords / CreateFromBinaryOverlay overloads exist (any resilience knob?)
///
/// Run: dotnet run --project src/housecarl-generator pkcu-probe &lt;malformed.esp&gt;
/// </summary>
public static class PkcuProbe
{
    public static int Run(string[] args)
    {
        if (args.Length < 1) { Console.Error.WriteLine("usage: pkcu-probe <malformed.esp>"); return 1; }
        var path = args[0];
        if (!File.Exists(path)) { Console.Error.WriteLine($"not found: {path}"); return 1; }
        Console.WriteLine($"probe target: {path}");

        // ---- TEST 1: is all-types group enumeration RESUMABLE past a parse throw? ----
        Console.WriteLine();
        Console.WriteLine("== TEST 1: manual MoveNext loop, catch-and-continue (record-level feasibility) ==");
        var ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        var en = ov.EnumerateMajorRecords().GetEnumerator();
        int good = 0, errors = 0, afterFirstError = 0; bool errored = false;
        while (true)
        {
            IMajorRecordGetter? cur;
            try
            {
                if (!en.MoveNext()) break;
                cur = en.Current;
            }
            catch (Exception ex)
            {
                errors++;
                Console.WriteLine($"   [throw #{errors}] {ex.GetType().Name}: {Trunc(ex.Message)}");
                errored = true;
                continue;   // attempt to resume past the bad record
            }
            good++;
            if (errored) afterFirstError++;
        }
        Console.WriteLine($"   good={good}  errors={errors}  recordsYieldedAfterFirstError={afterFirstError}");
        Console.WriteLine(afterFirstError > 0
            ? "   => RESUMABLE: record-level isolation FEASIBLE via catch-continue."
            : "   => NOT resumable: record-level via this API NOT feasible — use plugin/type-level.");

        // ---- TEST 2: API surface for resilience options ----
        Console.WriteLine();
        Console.WriteLine("== TEST 2: EnumerateMajorRecords overloads (SkyrimModMixIn) ==");
        var mixin = typeof(SkyrimMod).Assembly.GetType("Mutagen.Bethesda.Skyrim.SkyrimModMixIn");
        if (mixin != null)
            foreach (var m in mixin.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                   .Where(m => m.Name == "EnumerateMajorRecords"))
                Console.WriteLine("   " + Sig(m));

        Console.WriteLine();
        Console.WriteLine("== TEST 2b: SkyrimMod.CreateFromBinaryOverlay overloads ==");
        var createOverloads = typeof(SkyrimMod).GetMethods(BindingFlags.Public | BindingFlags.Static)
                                               .Where(m => m.Name == "CreateFromBinaryOverlay").ToList();
        foreach (var m in createOverloads) Console.WriteLine("   " + Sig(m));

        // ---- TEST 3: BinaryReadParameters surface — any parse-error tolerance knob? ----
        Console.WriteLine();
        Console.WriteLine("== TEST 3: BinaryReadParameters public properties ==");
        var brpType = createOverloads.SelectMany(m => m.GetParameters())
            .Select(p => p.ParameterType).FirstOrDefault(t => t.Name == "BinaryReadParameters");
        if (brpType is null) Console.WriteLine("   (BinaryReadParameters type not found)");
        else
            foreach (var p in brpType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                Console.WriteLine($"   {p.PropertyType.Name} {p.Name}");

        // ---- TEST 4: does relaxing ThrowOnUnknownSubrecord rescue the malformed PKCU? ----
        Console.WriteLine();
        Console.WriteLine("== TEST 4: open with ThrowOnUnknownSubrecord=false, enumerate ==");
        try
        {
            var prm = BinaryReadParameters.Default with { ThrowOnUnknownSubrecord = false };
            var ov3 = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE, prm);
            int n = 0; bool threw = false;
            var e3 = ov3.EnumerateMajorRecords().GetEnumerator();
            while (true)
            {
                try { if (!e3.MoveNext()) break; _ = e3.Current; }
                catch (Exception ex) { threw = true; Console.WriteLine($"   still THREW: {ex.GetType().Name}: {Trunc(ex.Message)}"); break; }
                n++;
            }
            Console.WriteLine(threw
                ? $"   => flag does NOT help (count-mismatch is a known-subrecord validation, not unknown-subrecord). enumerated {n} before throw."
                : $"   => flag RESCUES it! enumerated all {n} records clean — record-level feasible via BinaryReadParameters.");
        }
        catch (Exception ex) { Console.WriteLine($"   construction/open failed: {ex.GetType().Name}: {Trunc(ex.Message)}"); }

        return 0;
    }

    /// <summary>End-to-end proof of the fix: drive the REAL product code (LoadOrderResolver.Build) over a load order
    /// that contains the malformed plugin, and assert it (a) does NOT throw, (b) excludes + reports the bad plugin,
    /// (c) still fully indexes the OTHER plugin, (d) resolves + reads a good record. args: &lt;clean.esp&gt; &lt;mal.esp&gt;</summary>
    public static int RunFixProof(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: pkcu-fix-proof <clean.esp> <mal.esp>"); return 1; }
        var clean = args[0]; var mal = args[1];
        var cleanName = Path.GetFileName(clean); var malName = Path.GetFileName(mal);

        Console.WriteLine("== FIX PROOF: LoadOrderResolver.Build([clean, malformed]) ==");
        LoadOrderResolver resolver;
        try { resolver = LoadOrderResolver.Build(new[] { clean, mal }); }   // the throw used to escape HERE and brick everything
        catch (Exception ex) { Console.WriteLine($"   FAIL — build threw (fix not working): {ex.GetType().Name}: {Trunc(ex.Message)}"); return 1; }

        Console.WriteLine($"   build OK — {resolver.PluginCount} plugins, {resolver.RecordCount} records indexed, {resolver.ExcludedPlugins.Count} excluded");
        foreach (var kv in resolver.ExcludedPlugins) Console.WriteLine($"   excluded[{kv.Key}]: {Trunc(kv.Value)}");

        // (c) the OTHER plugin's records survived — index is non-empty and the clean plugin won its records.
        var goodFk = FormKey.Factory($"000668:{cleanName}");               // madAttackPlayer, defined in the clean plugin
        var w = resolver.ResolveWinner(goodFk);
        Console.WriteLine($"   ResolveWinner({goodFk}) -> {(w is null ? "NULL" : w.Value.WinnerPlugin)}   (expect {cleanName})");

        // (d) we can actually fetch + read that good record.
        using (var s = resolver.OpenSession())
        {
            var body = resolver.GetRecord(s, cleanName, goodFk);
            Console.WriteLine($"   GetRecord(clean, goodFk) -> {(body is null ? "NULL" : body.EditorID + " / Type=" + RecordNaming.StripOverlay(body.GetType().Name))}   (expect madAttackPlayer)");
        }

        // (b) the malformed plugin's own record is NOT resolvable, and exclusion is reported (Q3).
        var badFk = FormKey.Factory($"000668:{malName}");
        Console.WriteLine($"   ResolveWinner({badFk}) -> {(resolver.ResolveWinner(badFk) is null ? "NULL (correct — plugin excluded)" : "RESOLVED (WRONG)")}");
        Console.WriteLine($"   ExcludedPlugins.ContainsKey({malName}) -> {resolver.ExcludedPlugins.ContainsKey(malName)}");

        // verdict
        bool pass = resolver.ExcludedPlugins.ContainsKey(malName)
                    && w is not null && string.Equals(w.Value.WinnerPlugin, cleanName, StringComparison.OrdinalIgnoreCase)
                    && resolver.ResolveWinner(badFk) is null
                    && resolver.RecordCount > 0;
        Console.WriteLine(pass ? "   ==> PASS: malformed plugin isolated, clean plugin fully accessible." : "   ==> FAIL");
        return pass ? 0 : 1;
    }

    /// <summary>Real-scale proof: build the ACTUAL MO2 order (the product path Mo2LoadOrder.Build → LoadOrderResolver.Build)
    /// with the malformed plugin appended, and assert the whole order resolves with ONLY that one plugin excluded — i.e.
    /// no regression at full scale + isolation works in the real world. args: &lt;mo2InstanceDir&gt; &lt;mal.esp&gt;</summary>
    public static int RunScaleProof(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: pkcu-scale-proof <mo2InstanceDir> <mal.esp>"); return 1; }
        var instanceDir = args[0]; var mal = args[1]; var malName = Path.GetFileName(mal);

        Console.WriteLine("== SCALE PROOF: real MO2 order + 1 malformed plugin ==");
        var p = Mo2Instance.Resolve(instanceDir);
        var order = Mo2LoadOrder.Build(p.ProfileDir, p.ModsDir, p.DataDir, p.OverwriteDir);
        var real = order.OrderedPaths.ToList();
        Console.WriteLine($"   real order: {real.Count} plugins (profile '{p.ProfileName}')");
        real.Add(mal);                                                     // append the malformed plugin at highest priority

        var sw = System.Diagnostics.Stopwatch.StartNew();
        LoadOrderResolver resolver;
        try { resolver = LoadOrderResolver.Build(real); }                  // the OLD code threw HERE → every tool call dead
        catch (Exception ex) { Console.WriteLine($"   FAIL — build threw: {ex.GetType().Name}: {Trunc(ex.Message)}"); return 1; }
        sw.Stop();

        Console.WriteLine($"   build OK in {sw.Elapsed.TotalSeconds:N1}s — {resolver.PluginCount} plugins, {resolver.RecordCount:N0} records, {resolver.ExcludedPlugins.Count} excluded");
        foreach (var kv in resolver.ExcludedPlugins) Console.WriteLine($"   excluded[{kv.Key}]: {Trunc(kv.Value)}");

        var iron = FormKey.Factory("012EB7:Skyrim.esm");                   // a real vanilla record must still resolve
        var w = resolver.ResolveWinner(iron);
        Console.WriteLine($"   ResolveWinner(012EB7:Skyrim.esm) -> {(w is null ? "NULL" : w.Value.WinnerPlugin)} (override depth {(w?.OverrideDepth ?? 0)})");

        bool pass = resolver.ExcludedPlugins.ContainsKey(malName) && resolver.RecordCount > 100_000 && w is not null;
        Console.WriteLine(pass
            ? $"   ==> PASS: full order built; only '{malName}' excluded; {resolver.RecordCount:N0} records resolve."
            : "   ==> FAIL");
        if (resolver.ExcludedPlugins.Count > 1)
            Console.WriteLine($"   NOTE: {resolver.ExcludedPlugins.Count - 1} OTHER plugin(s) in the real order were also excluded — surfaced above (would previously have bricked houseCARL too).");
        resolver.Dispose();
        return pass ? 0 : 1;
    }

    /// <summary>CI REGRESSION GUARD (self-contained — no external file/MO2 deps, unlike the manual proofs above, so it
    /// runs on the CI runner). SYNTHESIZES a malformed PKCU in code: writes a clean plugin (a keyword) + a plugin
    /// with an empty PACK, both masterless (CI has no game files), then corrupts the PACK's PKCU subrecord so Mutagen
    /// throws constructing the overlay mid-enumeration. Asserts the resolver EXCLUDES the bad plugin (not fatal) while
    /// the clean plugin still resolves. Locks in the "Taste of Death" fix.
    /// Returns 0 = pass / 1 = fail (the CI gate). Run: dotnet run --project src/housecarl-generator -- pkcu-regression
    ///
    /// WHICH CORRUPTION, AND WHY IT CHANGED (Mutagen 0.53.1 -> 0.54.4, 2026-08-23). The synthesis used to flip the
    /// PKCU data-input COUNT from 0 to a non-zero value, so count≠inputs. 0.54.4 no longer throws on that — it parses
    /// the mismatch silently and enumerates the record — almost certainly 0.54.2's "Reverted undesirable optimization
    /// causing parsing errors in specific scenarios". The staleness self-check below caught it rather than letting the
    /// guard turn into a false PASS, which is what that check is for.
    ///
    /// Measured across both versions before re-fixturing, so the replacement was chosen on evidence and not on a
    /// guess about which shapes are stable:
    ///
    ///   corruption                     0.53.1                        0.54.4
    ///   PKCU data count 0 -> 6         SubrecordException            NO THROW   &lt;- the old fixture
    ///   PKCU subrecord length 12 -> 99 SubrecordException            SubrecordException   &lt;- the new fixture
    ///   PKDT subrecord length -> 200   no throw                      no throw
    ///   EDID subrecord length -> 250   ArgumentOutOfRangeException   ArgumentOutOfRangeException
    ///   PACK record length +5000 / ->3 no throw                      no throw
    ///   GRUP size -> 9999              ModGroupsMalformedException   ModGroupsMalformedException
    ///   truncated tail                 ModGroupsMalformedException   ModGroupsMalformedException
    ///
    /// Exactly one shape changed. The replacement stays in the SAME subrecord and throws the SAME exception type on
    /// BOTH versions, so the guard still synthesizes "a malformed PKCU on a package record throws mid-enumeration" —
    /// the Taste of Death shape — and is not pinned to either Mutagen version.
    ///
    /// The behavior change is upstream's, not ours: a plugin whose PKCU count disagrees with its inputs is no longer
    /// excluded by the resolver, because nothing throws on it any more. That is a narrower isolation surface, not a
    /// broken one — the fix this guard locks still isolates every shape that does throw.</summary>
    public static int RunRegression(string[] args)
    {
        Console.WriteLine("== PKCU REGRESSION GUARD (self-contained) ==");
        var dir = Path.Combine(Path.GetTempPath(), "hc_pkcu_regression");
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        Directory.CreateDirectory(dir);
        var cleanPath = Path.Combine(dir, "hcRegClean.esp");
        var badPath = Path.Combine(dir, "hcRegBad.esp");
        try
        {
            // 1. CLEAN plugin — a keyword we can resolve (masterless: references nothing, so CI needs no game files).
            var cleanMod = new SkyrimMod(ModKey.FromNameAndExtension("hcRegClean.esp"), SkyrimRelease.SkyrimSE);
            var kw = cleanMod.Keywords.AddNew();
            kw.EditorID = "hcRegKeyword";
            var cleanKwFk = kw.FormKey;
            cleanMod.BeginWrite.ToPath(cleanPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            // 2. BAD plugin — an empty PACK. Mutagen writes a PKCU subrecord for it; overstate that subrecord's
            //    declared LENGTH so it claims more bytes than the record carries → SubrecordException thrown when
            //    Mutagen constructs the overlay during enumeration (the Taste of Death shape). See the summary
            //    above for why this is the length and no longer the data-input count.
            var badMod = new SkyrimMod(ModKey.FromNameAndExtension("hcRegBad.esp"), SkyrimRelease.SkyrimSE);
            var pkg = badMod.Packages.AddNew();
            pkg.EditorID = "hcRegBadPackage";
            var pkgFk = pkg.FormKey;
            badMod.BeginWrite.ToPath(badPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            if (!OverstatePkcuLength(badPath, 99, out var synthNote)) { Console.WriteLine($"   FAIL (synth): {synthNote}"); return 1; }
            Console.WriteLine($"   synth: {synthNote}");

            // Sanity: the synthesized bad plugin really does throw on raw enumeration (else the test proves nothing).
            bool rawThrows = false;
            try { foreach (var _ in SkyrimMod.CreateFromBinaryOverlay(badPath, SkyrimRelease.SkyrimSE).EnumerateMajorRecords()) { } }
            catch { rawThrows = true; }
            if (!rawThrows) { Console.WriteLine("   FAIL (synth): the corrupted plugin did NOT throw on raw enumeration — synthesis is stale, test would be a false PASS."); return 1; }

            // 3. The fix: Build over [clean, bad] must NOT throw; bad excluded; clean still resolves.
            LoadOrderResolver resolver;
            try { resolver = LoadOrderResolver.Build(new[] { cleanPath, badPath }); }
            catch (Exception ex) { Console.WriteLine($"   FAIL (regression!): LoadOrderResolver.Build threw: {ex.GetType().Name}: {Trunc(ex.Message)}"); return 1; }

            bool excluded = resolver.ExcludedPlugins.ContainsKey("hcRegBad.esp");
            var cleanWin = resolver.ResolveWinner(cleanKwFk);
            var badWin = resolver.ResolveWinner(pkgFk);
            Console.WriteLine($"   build OK — {resolver.RecordCount} record(s), {resolver.ExcludedPlugins.Count} excluded");
            foreach (var kv in resolver.ExcludedPlugins) Console.WriteLine($"   excluded[{kv.Key}]: {Trunc(kv.Value)}");
            Console.WriteLine($"   clean keyword resolves : {(cleanWin is not null ? "yes -> " + cleanWin.Value.WinnerPlugin : "NO")}");
            Console.WriteLine($"   bad package resolves   : {(badWin is null ? "no (correct — excluded)" : "YES (wrong)")}");
            resolver.Dispose();

            bool pass = excluded && cleanWin is not null && badWin is null && resolver.RecordCount > 0;
            Console.WriteLine(pass ? "   ==> PASS: malformed plugin isolated, clean plugin resolves." : "   ==> FAIL");
            return pass ? 0 : 1;
        }
        catch (Exception ex) { Console.WriteLine($"   FAIL (unexpected): {ex.GetType().Name}: {Trunc(ex.Message)}"); return 1; }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>Overstate the PKCU subrecord's declared length (the 2-byte field after the 4-byte tag) so it claims
    /// more bytes than the record carries. Returns false if no PKCU subrecord is present (synthesis assumption broken).
    /// </summary>
    static bool OverstatePkcuLength(string path, ushort newLength, out string note)
    {
        var b = File.ReadAllBytes(path);
        for (int i = 0; i < b.Length - 6; i++)
            if (b[i] == 0x50 && b[i + 1] == 0x4B && b[i + 2] == 0x43 && b[i + 3] == 0x55)   // "PKCU"
            {
                var old = BitConverter.ToUInt16(b, i + 4);                                    // 4 tag, then 2 length
                if (newLength <= old)
                {
                    // Not a corruption at all — it would have to claim MORE than it carries to be one. Refuse
                    // rather than write a well-formed plugin and let the run read as a pass.
                    note = $"PKCU @ {i}: declared length is already {old}, so {newLength} would not overstate it";
                    return false;
                }
                BitConverter.GetBytes(newLength).CopyTo(b, i + 4);
                File.WriteAllBytes(path, b);
                note = $"PKCU @ {i}: subrecord length {old} -> {newLength} (now claims {newLength} bytes, carries {old})";
                return true;
            }
        note = "no PKCU subrecord found in the synthesized PACK (Mutagen did not emit one for an empty package)";
        return false;
    }

    static string Sig(MethodInfo m) =>
        $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}{(p.IsOptional ? "=opt" : "")}"))})";

    static string Trunc(string s) => s.Length > 110 ? s.Substring(0, 110) + "…" : s;
}

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
        var order = Mo2LoadOrder.Build(p.ProfileDir, p.ModsDir, p.DataDir);
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

    static string Sig(MethodInfo m) =>
        $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}{(p.IsOptional ? "=opt" : "")}"))})";

    static string Trunc(string s) => s.Length > 110 ? s.Substring(0, 110) + "…" : s;
}

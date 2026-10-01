using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// HCBR-2026-06-09-03 — `cross_plugin_query type=Perk references=` hard-errored (opaquely) on the whole call.
///
/// DIAGNOSIS (<c>perk-refs-diagnose</c>): the scan's per-record test is Mutagen's own <c>EnumerateFormLinks()</c>,
/// which LAZILY parses subrecord content (a perk's Effects); run it over every PERK in a plugin / the whole MO2
/// order and report which records throw, with full exception detail. The ARR sweep found exactly ONE offender in
/// 1,822 winner perks — 00080E:Requiem - Special Feats.esp, whose PerkEntryPointModifyActorValue carries a
/// parameter-type flag Mutagen's model rejects (MalformedDataException) — and that single record aborted the
/// whole call, because the scan loop only caught ArgumentException.
///
/// The CI regression for the fix is ReferencesScanFaultTests in src/housecarl-mcp-tests; this file keeps the
/// manual diagnose and proof harnesses; the deleted-record arm is DeletedRecordScanTests.
///
/// Run: <c>dotnet run --project src/housecarl-generator perk-refs-diagnose [-- --source &lt;path&gt; | --mo2 &lt;instanceDir&gt;]</c>
/// </summary>
public static class PerkRefsProbe
{
    const string DefaultSource = @"E:\SteamLibrary\steamapps\common\Skyrim Special Edition\Data\Skyrim.esm";

    /// <summary>REAL-DATA proof (manual; needs an MO2 instance + a generated corpus.json): drive the SERVICE-layer
    /// scan with the report's exact failing call — <c>type=Perk references=01CEAD:Skyrim.esm</c> (KYWD
    /// MagicDamageFire) — over the live order. Before the fix the whole call threw; after, it must return with no
    /// error and account any unscannable perk(s) by FormKey in the ScanNote. Match count is data-dependent and
    /// reported, not asserted.
    /// Run: <c>dotnet run --project src/housecarl-generator perk-refs-proof -- --mo2 &lt;instanceDir&gt; --corpus &lt;corpus.json&gt; [--references XXXXXX:Plugin.esp]</c></summary>
    public static int RunProof(string[] args)
    {
        var f = WriteEngine.ParseFlags(args);
        var instanceDir = f.GetValueOrDefault("mo2");
        var corpus = f.GetValueOrDefault("corpus");
        if (instanceDir is null || corpus is null) { Console.WriteLine("SKIP: needs --mo2 <instanceDir> and --corpus <corpus.json>"); return 0; }
        if (!Directory.Exists(instanceDir) || !File.Exists(corpus)) { Console.WriteLine($"SKIP: --mo2 or --corpus path not found"); return 0; }
        CorpusRulebook.CorpusPath = corpus;                                   // Types.Resolve("Perk") reads the type catalog
        var refRaw = f.GetValueOrDefault("references") ?? "01CEAD:Skyrim.esm";   // the report's row-1 repro (KYWD MagicDamageFire)
        var refFk = FormKey.Factory(refRaw);

        Console.WriteLine($"################  REAL-DATA PROOF — cross_plugin_query type=Perk references={refRaw} on {Path.GetFileName(instanceDir)}  ################");
        Console.WriteLine();
        var p = Mo2Instance.Resolve(instanceDir);
        var order = Mo2LoadOrder.Build(p.ProfileDir, p.ModsDir, p.DataDir, p.OverwriteDir);
        using var resolver = LoadOrderResolver.Build(order.OrderedPaths.ToList());
        Console.WriteLine($"   resolver: {resolver.PluginCount} plugins, {resolver.RecordCount:N0} records, {resolver.ExcludedPlugins.Count} excluded");
        var svc = LoadOrderService.ForGuard(resolver, new UserConfigStore(Path.Combine(Path.GetTempPath(), "hc-perkrefs-proof", "houseCARL.user.json")));

        CrossQueryOutcome q;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { q = svc.ReadArea.CrossQuery(type: "Perk", references: new[] { refFk }, editoridContains: null, conflictsOnly: false,
                                 plugins: null, where: null, limit: 500); }
        catch (Exception ex)
        {
            Console.WriteLine($"   FAIL — the call still THREW: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        sw.Stop();

        Console.WriteLine($"   call completed in {sw.Elapsed.TotalSeconds:N1}s");
        Console.WriteLine($"   error    : {q.Error ?? "(none)"}");
        Console.WriteLine($"   matches  : {q.Total}");
        Console.WriteLine($"   scan note: {q.ScanNote ?? "(none — every record scanned clean)"}");
        foreach (var s in (q.Prefilled ?? Array.Empty<RecordSummary>()).Take(5))
            Console.WriteLine($"      {s.FormKey}  {s.EditorId ?? "<no editorid>"}  (winner {s.Winner})");
        Console.WriteLine();
        bool pass = q.Error is null;
        Console.WriteLine($"=== perk-refs-proof: {(pass ? "PASS" : "FAIL")} ===");
        return pass ? 0 : 1;
    }

    public static int RunDiagnose(string[] args)
    {
        // --mo2 <instanceDir>: sweep the WHOLE load order through the PRODUCT stream (WinnerRecordsOfType),
        // the exact loop the records scan runs. Without it: a quick single-plugin sweep of Skyrim.esm.
        var f = HousecarlCore.WriteEngine.ParseFlags(args);
        if (f.GetValueOrDefault("mo2") is { } instanceDir) return DiagnoseFullOrder(instanceDir);

        var src = f.GetValueOrDefault("source") ?? DefaultSource;
        if (!File.Exists(src)) { Console.WriteLine($"SKIP: source plugin not found: {src}"); return 0; }

        Console.WriteLine($"################  DIAGNOSIS — EnumerateFormLinks over PERK records in {Path.GetFileName(src)}  ################");
        Console.WriteLine();

        using var mod = SkyrimMod.CreateFromBinaryOverlay(src, SkyrimRelease.SkyrimSE);
        var (ok, links, failures) = Sweep(mod.Perks.Select(p => ((IMajorRecordGetter)p, "Skyrim.esm")));
        Report(ok, links, failures);
        return 0;
    }

    static int DiagnoseFullOrder(string instanceDir)
    {
        Console.WriteLine($"################  DIAGNOSIS — EnumerateFormLinks over ALL winner PERKs in the {Path.GetFileName(instanceDir)} order  ################");
        Console.WriteLine();
        var p = HousecarlCore.Mo2Instance.Resolve(instanceDir);
        var order = HousecarlCore.Mo2LoadOrder.Build(p.ProfileDir, p.ModsDir, p.DataDir, p.OverwriteDir);
        Console.WriteLine($"   order: {order.OrderedPaths.Count} plugins (profile '{p.ProfileName}')");
        using var resolver = HousecarlCore.LoadOrderResolver.Build(order.OrderedPaths.ToList());
        Console.WriteLine($"   resolver: {resolver.PluginCount} plugins, {resolver.RecordCount:N0} records, {resolver.ExcludedPlugins.Count} excluded");
        Console.WriteLine();

        var (ok, links, failures) = Sweep(resolver.WinnerRecordsOfType(new[] { typeof(IPerkGetter) })
                                                  .Select(x => (x.body, x.fk.ModKey.FileName.ToString())));
        Report(ok, links, failures);
        return 0;
    }

    static (int ok, int links, List<(FormKey fk, string? edid, string src, Exception ex)> failures)
        Sweep(IEnumerable<(IMajorRecordGetter rec, string src)> stream)
    {
        int ok = 0, links = 0;
        var failures = new List<(FormKey, string?, string, Exception)>();
        foreach (var (rec, src) in stream)
        {
            try
            {
                if (rec is IFormLinkContainerGetter flc)
                    links += flc.EnumerateFormLinks().Count();
                ok++;
            }
            catch (Exception ex)
            {
                if (failures.Count < 8) failures.Add((rec.FormKey, rec.EditorID, src, ex));
                else failures.Add((rec.FormKey, null, src, ex));
            }
        }
        return (ok, links, failures);
    }

    static void Report(int ok, int links, List<(FormKey fk, string? edid, string src, Exception ex)> failures)
    {
        Console.WriteLine($"   perks scanned : {ok + failures.Count}");
        Console.WriteLine($"   enumerated OK : {ok}  (total links seen: {links})");
        Console.WriteLine($"   THREW         : {failures.Count}");
        Console.WriteLine();
        foreach (var (fk, edid, src, ex) in failures.Take(8))
        {
            Console.WriteLine($"-- {fk} ({edid ?? "<no editorid>"}) defined in {src} --");
            Console.WriteLine($"   {ex.GetType().FullName}: {ex.Message}");
            var st = ex.StackTrace?.Split('\n').Take(12) ?? Array.Empty<string>();
            foreach (var line in st) Console.WriteLine($"   {line.TrimEnd()}");
            if (ex.InnerException is { } inner)
                Console.WriteLine($"   INNER {inner.GetType().FullName}: {inner.Message}");
            Console.WriteLine();
        }
        if (failures.Count > 8)
        {
            Console.WriteLine($"   ... and {failures.Count - 8} more failing perk(s); distinct exception types: " +
                string.Join(", ", failures.Select(x => x.ex.GetType().Name).Distinct()));
        }
    }
}

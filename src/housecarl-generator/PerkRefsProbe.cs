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
/// REGRESSION GUARD (<c>perk-refs-guard</c>, standing CI instrument, self-contained): synthesizes the failure —
/// a plugin with a target perk, a good perk that references it (NextPerk), and a perk whose written EPFT
/// (entry-point parameter-type flag) byte is then corrupted so Mutagen's ParseEffect throws — and drives the REAL
/// service-layer scan (<see cref="LoadOrderService.CrossQuery"/> via the ForGuard seam; the first CI coverage of
/// the mcp layer). Asserts: the call SUCCEEDS, the good match is found, and the unscannable record is ACCOUNTED
/// by FormKey in the ScanNote (Q3 — excluded, never silent). A control proves the corrupted perk really does
/// throw from EnumerateFormLinks (so a GREEN is meaningful). RED before the fix, GREEN after.
///
/// Run: <c>dotnet run --project src/housecarl-generator perk-refs-guard</c>
///      <c>dotnet run --project src/housecarl-generator perk-refs-diagnose [-- --source &lt;path&gt; | --mo2 &lt;instanceDir&gt;]</c>
/// </summary>
public static class PerkRefsProbe
{
    const string DefaultSource = @"E:\SteamLibrary\steamapps\common\Skyrim Special Edition\Data\Skyrim.esm";

    [CiProbe("perk-refs-guard")]
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — references= scan fault isolation (HCBR-2026-06-09-03)  ################");
        Console.WriteLine();

        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-perkrefs-guard");
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        Directory.CreateDirectory(tmpDir);
        var modKey = new ModKey("HcPerkRefsGuard", ModType.Plugin);
        string espPath = Path.Combine(tmpDir, modKey.FileName.String);

        // --- Setup: target perk + a BAD perk with one entry-point effect (whose EPFT parameter-type flag byte we
        //     corrupt after writing — the exact malformation class the ARR sweep found), THEN a GOOD perk
        //     referencing the target. Order is load-bearing (PR #27 review): the bad perk gets the LOWER FormID,
        //     so it enumerates BEFORE the good match — the guard then pins not just "one bad record doesn't kill
        //     the call" but "the scan CONTINUES past the fault" (a stop-at-first-fault regression silently drops
        //     every later match — the Q3 class this fix closes; RED re-proven against that simulation). ---
        FormKey targetFk, goodFk, badFk;
        {
            var mod = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE);
            var target = mod.Perks.AddNew(); target.EditorID = "HcPerkRefsGuard_Target"; targetFk = target.FormKey;
            var bad = mod.Perks.AddNew(); bad.EditorID = "HcPerkRefsGuard_Bad"; badFk = bad.FormKey;
            bad.Effects.Add(new PerkEntryPointModifyActorValue
            {
                EntryPoint = APerkEntryPointEffect.EntryType.CalculateWeaponDamage,
                ActorValue = ActorValue.OneHanded,
                Value = 1f,
                Modification = PerkEntryPointModifyActorValue.ModificationType.AddAVMult,
            });
            var good = mod.Perks.AddNew(); good.EditorID = "HcPerkRefsGuard_Good"; goodFk = good.FormKey;
            good.NextPerk.SetTo(targetFk);
            mod.BeginWrite.ToPath(espPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }
        int corrupted = ProbeBytes.CorruptEpftBytes(espPath);
        Console.WriteLine($"-- setup: wrote {modKey.FileName} (target + referencing + entry-point perks); corrupted {corrupted} EPFT flag byte(s) --");
        if (corrupted != 1) { Console.WriteLine($"=== perk-refs-guard: FAIL (expected exactly 1 EPFT subrecord to corrupt, found {corrupted}) ==="); return 1; }

        // --- CONTROL: the corruption must reproduce the crash class — EnumerateFormLinks on the bad perk THROWS. ---
        bool controlThrew = false; string controlMsg = "(no throw)";
        using (var ov = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE))
        {
            var badOv = ov.Perks.First(p => p.FormKey == badFk);
            try { _ = ((IFormLinkContainerGetter)badOv).EnumerateFormLinks().Count(); }
            catch (Exception ex) { controlThrew = true; controlMsg = $"{ex.GetType().Name}: {ex.Message}"; }
        }
        Console.WriteLine($"   CONTROL (bad perk throws from EnumerateFormLinks) : {(controlThrew ? "PASS" : "FAIL")}  [{controlMsg}]");

        // --- FIX: drive the REAL service-layer scan over the order containing the bad perk. Scoped by plugins=
        //     (the same scan loop type= runs; no corpus needed). Before the fix this whole call THREW. ---
        using var resolver = LoadOrderResolver.Build(new[] { espPath });
        var svc = LoadOrderService.ForGuard(resolver, new UserConfigStore(Path.Combine(tmpDir, "houseCARL.user.json")));
        CrossQueryOutcome q;
        try { q = svc.CrossQuery(type: null, references: new[] { targetFk }, editoridContains: null, conflictsOnly: false,
                                 plugins: new[] { modKey.FileName.String }, where: null, limit: 500); }
        catch (Exception ex)
        {
            Console.WriteLine($"   scan call completed (no escape)                    : FAIL — the call still THREW: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine();
            Console.WriteLine("=== perk-refs-guard: FAIL ===");
            return 1;
        }

        bool noError = q.Error is null;
        bool foundGood = q.Total == 1 && q.Keys.Count == 1 && q.Keys[0] == goodFk;
        bool accounted = q.ScanNote is not null
                         && q.ScanNote.Contains(badFk.ToString(), StringComparison.OrdinalIgnoreCase)
                         && q.ScanNote.Contains("1 record instance(s)", StringComparison.Ordinal);

        Console.WriteLine($"   scan call completed (no escape, no error)          : {(noError ? "PASS" : $"FAIL [{q.Error}]")}");
        Console.WriteLine($"   good referencing perk matched                      : {(foundGood ? "PASS" : $"FAIL (total={q.Total})")}");
        Console.WriteLine($"   unscannable perk ACCOUNTED by FormKey (ScanNote)   : {(accounted ? "PASS" : $"FAIL [{q.ScanNote ?? "(no note)"}]")}");
        if (accounted) Console.WriteLine($"      {q.ScanNote}");
        Console.WriteLine();

        bool pass = controlThrew && noError && foundGood && accounted;
        Console.WriteLine($"=== perk-refs-guard: {(pass ? "PASS" : "FAIL")} ===");
        return pass ? 0 : 1;
    }

    /// <summary>REGRESSION GUARD (<c>deleted-record-scan-guard</c>, standing CI instrument, self-contained) for #276 —
    /// a DELETED record with a residual, malformed body made a references=/where= scan end in a raw
    /// "NullReferenceException … could not be scanned and were skipped" note (the wild repro: deleted PACKs in a
    /// follower mod). A deleted record carries no live body by engine rule, so the fix EXCLUDES it from the content
    /// filters before the reference walk — it should be a clean non-match, not an "unscannable" skip whose cause
    /// reads as a parser hole (Q3).
    ///
    /// Reproduces the wild shape by the same corruption path as <see cref="RunGuard"/> (a perk whose EPFT byte is
    /// corrupted so a lazy Effects parse throws) PLUS setting the record's Deleted header flag ON DISK — Mutagen
    /// serialises a model-deleted record with an EMPTY body, so the flag is byte-patched onto a normally-written
    /// (still-bodied) record to get "deleted but still carrying a throwing body". A CONTROL proves the bad perk
    /// reads as Deleted AND still throws from EnumerateFormLinks, so a GREEN means the scan SKIPPED a record that
    /// WOULD have thrown, not a clean one. The discriminator: the deleted record's FormKey is ABSENT from the
    /// ScanNote — RED before the fix (it threw and was accounted there), GREEN after (excluded before the walk).
    ///
    /// Run: <c>dotnet run --project src/housecarl-generator deleted-record-scan-guard</c></summary>
    [CiProbe("deleted-record-scan-guard")]
    public static int RunDeletedGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — a DELETED record is excluded from a references= scan, not accounted as unscannable (#276)  ################");
        Console.WriteLine();

        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-deleted-scan-guard");
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        Directory.CreateDirectory(tmpDir);
        var modKey = new ModKey("HcDeletedScanGuard", ModType.Plugin);
        string espPath = Path.Combine(tmpDir, modKey.FileName.String);

        // Same fixture shape as perk-refs-guard: target + a GOOD referencing perk + a BAD perk with an entry-point
        // effect. The BAD perk gets the lower FormID so it enumerates BEFORE the good match (a stop-at-fault
        // regression would drop the good one). It is then made BOTH malformed (EPFT corrupted → lazy parse throws)
        // AND Deleted-on-disk — the exact wild shape #276 saw on deleted PACKs.
        FormKey targetFk, goodFk, badFk;
        {
            var mod = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE);
            var target = mod.Perks.AddNew(); target.EditorID = "HcDeletedScanGuard_Target"; targetFk = target.FormKey;
            var bad = mod.Perks.AddNew(); bad.EditorID = "HcDeletedScanGuard_Bad"; badFk = bad.FormKey;
            bad.Effects.Add(new PerkEntryPointModifyActorValue
            {
                EntryPoint = APerkEntryPointEffect.EntryType.CalculateWeaponDamage,
                ActorValue = ActorValue.OneHanded,
                Value = 1f,
                Modification = PerkEntryPointModifyActorValue.ModificationType.AddAVMult,
            });
            var good = mod.Perks.AddNew(); good.EditorID = "HcDeletedScanGuard_Good"; goodFk = good.FormKey;
            good.NextPerk.SetTo(targetFk);
            mod.BeginWrite.ToPath(espPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }
        int corrupted = ProbeBytes.CorruptEpftBytes(espPath);
        int deleted = ProbeBytes.SetDeletedFlag(espPath, "PERK", badFk.ID);   // master-less fixture ⇒ the on-disk FormID IS the object id
        Console.WriteLine($"-- setup: wrote {modKey.FileName}; corrupted {corrupted} EPFT byte(s); flagged {deleted} record Deleted on disk --");
        if (corrupted != 1 || deleted != 1)
        {
            Console.WriteLine($"=== deleted-record-scan-guard: FAIL (expected 1 EPFT + 1 deleted flag, got {corrupted}/{deleted}) ===");
            return 1;
        }

        // CONTROL: the fixture must reproduce BOTH conditions — the bad perk reads as Deleted AND still throws from
        // EnumerateFormLinks. If it doesn't throw, a GREEN below would be meaningless (the guard would be skipping a
        // record the scan handles fine anyway).
        bool isDeleted = false, throws = false; string ctlMsg = "(no throw)";
        using (var ov = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE))
        {
            var badOv = ov.Perks.First(p => p.FormKey == badFk);
            isDeleted = badOv.IsDeleted;
            try { _ = ((IFormLinkContainerGetter)badOv).EnumerateFormLinks().Count(); }
            catch (Exception ex) { throws = true; ctlMsg = $"{ex.GetType().Name}: {ex.Message}"; }
        }
        Console.WriteLine($"   CONTROL: bad perk reads as Deleted                    : {(isDeleted ? "PASS" : "FAIL")}");
        Console.WriteLine($"   CONTROL: bad perk STILL throws from EnumerateFormLinks : {(throws ? "PASS" : "FAIL")}  [{ctlMsg}]");

        // Drive the REAL service-layer references= scan. WITHOUT the fix the deleted+throwing perk lands in the
        // ScanNote as an unscannable skip (its cause a raw exception — the #276 report). WITH the fix it's excluded
        // as deleted BEFORE the reference walk, so it never throws, is absent from the ScanNote, and the good match
        // still returns.
        using var resolver = LoadOrderResolver.Build(new[] { espPath });
        var svc = LoadOrderService.ForGuard(resolver, new UserConfigStore(Path.Combine(tmpDir, "houseCARL.user.json")));
        CrossQueryOutcome q;
        try { q = svc.CrossQuery(type: null, references: new[] { targetFk }, editoridContains: null, conflictsOnly: false,
                                 plugins: new[] { modKey.FileName.String }, where: null, limit: 500); }
        catch (Exception ex)
        {
            Console.WriteLine($"   scan call completed (no escape)                       : FAIL — the call THREW: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine("=== deleted-record-scan-guard: FAIL ===");
            return 1;
        }

        bool noError = q.Error is null;
        bool foundGood = q.Total == 1 && q.Keys.Count == 1 && q.Keys[0] == goodFk;
        // The discriminator: the deleted record is EXCLUDED, so its FormKey is NOT accounted in the ScanNote (with
        // no other unscannable record, the note is null). RED before the fix — the deleted perk threw and was
        // accounted; GREEN after — excluded before the walk.
        bool notAccounted = !(q.ScanNote?.Contains(badFk.ToString(), StringComparison.OrdinalIgnoreCase) ?? false);

        Console.WriteLine($"   scan call completed (no escape, no error)             : {(noError ? "PASS" : $"FAIL [{q.Error}]")}");
        Console.WriteLine($"   good referencing perk still matched                   : {(foundGood ? "PASS" : $"FAIL (total={q.Total})")}");
        Console.WriteLine($"   deleted record EXCLUDED, not in ScanNote (Q3)         : {(notAccounted ? "PASS" : $"FAIL [{q.ScanNote}]")}");
        Console.WriteLine();

        bool pass = isDeleted && throws && noError && foundGood && notAccounted;
        Console.WriteLine($"=== deleted-record-scan-guard: {(pass ? "PASS" : "FAIL")} ===");
        return pass ? 0 : 1;
    }

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
        CorpusRulebook.CorpusPath = corpus;                                   // ResolveTypeFilter("Perk") reads the type catalog
        var refRaw = f.GetValueOrDefault("references") ?? "01CEAD:Skyrim.esm";   // the report's row-1 repro (KYWD MagicDamageFire)
        var refFk = FormKey.Factory(refRaw);

        Console.WriteLine($"################  REAL-DATA PROOF — cross_plugin_query type=Perk references={refRaw} on {Path.GetFileName(instanceDir)}  ################");
        Console.WriteLine();
        var p = Mo2Instance.Resolve(instanceDir);
        var order = Mo2LoadOrder.Build(p.ProfileDir, p.ModsDir, p.DataDir, p.OverwriteDir);
        using var resolver = LoadOrderResolver.Build(order.OrderedPaths.ToList());
        Console.WriteLine($"   resolver: {resolver.PluginCount} plugins, {resolver.RecordCount:N0} records, {resolver.ExcludedPlugins.Count} excluded");
        var svc = LoadOrderService.ForGuard(resolver, new UserConfigStore(Path.Combine(Path.GetTempPath(), "hc-perkrefs-proof.user.json")));

        CrossQueryOutcome q;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { q = svc.CrossQuery(type: "Perk", references: new[] { refFk }, editoridContains: null, conflictsOnly: false,
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
        // the exact loop cross_plugin_query runs. Without it: a quick single-plugin sweep of Skyrim.esm.
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

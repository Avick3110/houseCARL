using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for the LOAD-ORDER INTEGRITY SWEEP (housecarl_check_errors — audit A1). Drives the
/// REAL product path (<see cref="ErrorCheck.Run"/> — what housecarl_check_errors calls through the thin service wrapper)
/// against a SYNTHESIZED 4-plugin order in TEMP — NO Skyrim.esm, so it runs in CI.
///
/// THE GAP (reproduced by construction): no general "validate every record" verb existed — validate_dialogue is
/// DIAL/QUST-only. The sweep walks every record's FormLinks and reports dangling refs, missing masters, and parse
/// failures, the data-layer twin of the CK's "Check For Errors".
///
/// FIXTURE — four plugins, two of them masters, one omitted from the loaded order ON PURPOSE:
///   • HcCeMaster.esm  — defines a Race (HcCeMasterRace). Present in the order.
///   • HcCeGhost.esm   — defines a Race (HcCeGhostRace). DECLARED by HcCeBad as a master, but NOT loaded → the
///                       missing-master fixture, and every ref into it is therefore also dangling.
///   • HcCeClean.esp   — masters [HcCeMaster]; one NPC whose Race → HcCeMasterRace (a VALID ref). The clean control:
///                       a Mutagen-written plugin whose masters all resolve must produce ZERO findings.
///   • HcCeBad.esp     — masters [HcCeMaster, HcCeGhost]; NPC1 Race → HcCeGhostRace (missing-master + dangling), NPC2
///                       Race → 0F0F0F:HcCeMaster.esm (dangling, master PRESENT — isolates "dangling" from "missing").
/// The order built for the resolver is [Master, Clean, Bad] — Ghost on disk but NOT in the order.
///
/// Arms (ALL required — a GREEN must mean "the contract holds"):
///   CLEAN-CONTROL   — HcCeClean.esp produces NO report (valid ref + a fresh NPC's default-null links are not flagged:
///                     the no-false-positive teeth, and the proof a null FormLink is treated as a legal optional).
///   DANGLING-GHOST  — HcCeBad's dangling set contains the ref to HcCeGhostRace (a ref into an absent master).
///   DANGLING-DEAD   — HcCeBad's dangling set contains the ref to 0F0F0F:HcCeMaster.esm (master present, target absent).
///   DANGLING-TOTAL  — exactly 2 dangling refs across the sweep (no stray links from the fresh NPCs).
///   SOURCE-ATTRIB   — each dangling ref names its SOURCE record (editorid + "Npc" type), not just the target.
///   MISSING-MASTER  — HcCeBad lists HcCeGhost.esm as a missing master; HcCeMaster.esm (present) is NOT listed.
///   MISSING-ISOLATE — no plugin reports HcCeMaster.esm missing (a present master is never a false missing-master).
///   SCANNED         — the whole-order sweep scanned 3 plugins (Master, Clean, Bad — Ghost is not in the order).
///   SCOPE           — scope=[HcCeBad.esp] reports only Bad (1 plugin scanned), Clean absent.
///   SCOPE-Q3        — scope=[a name not in the order] fails LOUD ("not in the load order"), no reports (never a silent skip).
///   CAP             — limit=1 over Bad's 2 dangling refs returns 1 but reports the TRUE total (2) and Capped.
///
/// COVERAGE NOTE (Q3 — name what this guard LEANS ON rather than re-proves): PARSE failures are not synthesized here.
///   Whole-plugin exclusion rides on the index build's ExcludedPlugins machinery (exercised across the suite), and the
///   per-record link-walk fault isolation is the SAME try/catch idiom proven by effect-chain-guard + the cross_plugin_query
///   scan. The sweep surfaces both verbatim (ExcludedPlugins + the unscannable accounting), it does not re-implement them.
///
/// Run: dotnet run --project src/housecarl-generator -- check-errors-guard
/// </summary>
public static class CheckErrorsProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("check-errors-guard — load-order integrity sweep (housecarl_check_errors, audit A1)");
        Console.WriteLine();
        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-check-errors-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try { return RunChecks(tmpDir); }
        finally { try { Directory.Delete(tmpDir, recursive: true); } catch { /* best-effort */ } }
    }

    static int RunChecks(string tmpDir)
    {
        int failures = 0;
        void Check(string label, bool ok, string? detail = null)
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}{(ok || detail is null ? "" : $"\n        -> {detail}")}");
            if (!ok) failures++;
        }

        string masterPath = Path.Combine(tmpDir, "HcCeMaster.esm");
        string ghostPath  = Path.Combine(tmpDir, "HcCeGhost.esm");
        string cleanPath  = Path.Combine(tmpDir, "HcCeClean.esp");
        string badPath    = Path.Combine(tmpDir, "HcCeBad.esp");
        var deadFk = FormKey.Factory("0F0F0F:HcCeMaster.esm");   // an object id HcCeMaster.esm does NOT define (master present, target absent)
        FormKey masterRaceFk, ghostRaceFk;
        try
        {
            var master = new SkyrimMod(new ModKey("HcCeMaster", ModType.Master), SkyrimRelease.SkyrimSE);
            var mRace = master.Races.AddNew(); mRace.EditorID = "HcCeMasterRace"; masterRaceFk = mRace.FormKey;
            master.BeginWrite.ToPath(masterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            var ghost = new SkyrimMod(new ModKey("HcCeGhost", ModType.Master), SkyrimRelease.SkyrimSE);
            var gRace = ghost.Races.AddNew(); gRace.EditorID = "HcCeGhostRace"; ghostRaceFk = gRace.FormKey;
            ghost.BeginWrite.ToPath(ghostPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            // Clean dependent: one NPC, Race → a VALID master race. WithLoadOrder([master]) declares HcCeMaster only.
            var clean = new SkyrimMod(new ModKey("HcCeClean", ModType.Plugin), SkyrimRelease.SkyrimSE);
            var cNpc = clean.Npcs.AddNew(); cNpc.EditorID = "HcCeCleanNpc"; cNpc.Race.SetTo(masterRaceFk);
            clean.BeginWrite.ToPath(cleanPath).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

            // Broken dependent: NPC1 → Ghost's race (missing master + dangling); NPC2 → a dead id in the present master.
            // Referencing both masters makes Mutagen declare [HcCeMaster, HcCeGhost] in the header.
            var bad = new SkyrimMod(new ModKey("HcCeBad", ModType.Plugin), SkyrimRelease.SkyrimSE);
            var bGhost = bad.Npcs.AddNew(); bGhost.EditorID = "HcCeBadGhostNpc"; bGhost.Race.SetTo(ghostRaceFk);
            var bDead  = bad.Npcs.AddNew(); bDead.EditorID  = "HcCeBadDeadNpc";  bDead.Race.SetTo(deadFk);
            bad.BeginWrite.ToPath(badPath).WithLoadOrder(new ISkyrimModGetter[] { master, ghost }).Write();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: could not synthesize fixtures: {ex.GetType().Name}: {(ex.InnerException ?? ex).Message}");
            return 1;
        }

        // Build the order WITHOUT Ghost — it is the missing master.
        using var r = LoadOrderResolver.Build(new[] { masterPath, cleanPath, badPath });

        var all = ErrorCheck.Run(r, null, 1000);
        if (!all.Success) { Console.Error.WriteLine($"error: whole-order sweep failed: {all.Error}"); return 1; }

        PluginErrors? Bad() => all.Reports.FirstOrDefault(p => p.Plugin == "HcCeBad.esp");
        var bad2 = Bad();

        Check("CLEAN-CONTROL: HcCeClean.esp produces NO report (valid ref + fresh NPC's null links are not flagged)",
            all.Reports.All(p => p.Plugin != "HcCeClean.esp"),
            $"clean report present={all.Reports.Any(p => p.Plugin == "HcCeClean.esp")}");

        Check("DANGLING-GHOST: HcCeBad's dangling set contains the ref into the absent master (HcCeGhostRace)",
            bad2 is not null && bad2.Dangling.Any(d => d.Target == ghostRaceFk),
            $"bad={(bad2 is null ? "<no report>" : string.Join(",", bad2.Dangling.Select(d => d.Target.ToString())))}");

        Check("DANGLING-DEAD: HcCeBad's dangling set contains the ref to a dead id in the present master (0F0F0F:HcCeMaster.esm)",
            bad2 is not null && bad2.Dangling.Any(d => d.Target == deadFk),
            $"bad={(bad2 is null ? "<no report>" : string.Join(",", bad2.Dangling.Select(d => d.Target.ToString())))}");

        Check("DANGLING-TOTAL: exactly 2 dangling refs across the sweep (no stray links from the fresh NPCs)",
            all.TotalDangling == 2, $"total={all.TotalDangling}");

        Check("SOURCE-ATTRIB: each dangling ref names its SOURCE record (editorid + 'Npc' type)",
            bad2 is not null
            && bad2.Dangling.Any(d => d.SourceEditorId == "HcCeBadGhostNpc" && d.SourceType == "Npc")
            && bad2.Dangling.Any(d => d.SourceEditorId == "HcCeBadDeadNpc"  && d.SourceType == "Npc"),
            bad2 is null ? "<no report>" : string.Join(",", bad2.Dangling.Select(d => $"{d.SourceEditorId}/{d.SourceType}")));

        Check("MISSING-MASTER: HcCeBad lists HcCeGhost.esm as missing; HcCeMaster.esm (present) is NOT listed",
            bad2 is not null
            && bad2.MissingMasters.Contains("HcCeGhost.esm", StringComparer.OrdinalIgnoreCase)
            && !bad2.MissingMasters.Contains("HcCeMaster.esm", StringComparer.OrdinalIgnoreCase),
            bad2 is null ? "<no report>" : string.Join(",", bad2.MissingMasters));

        Check("MISSING-ISOLATE: no plugin reports HcCeMaster.esm missing (a present master is never a false missing-master)",
            all.Reports.All(p => !p.MissingMasters.Contains("HcCeMaster.esm", StringComparer.OrdinalIgnoreCase)),
            string.Join(" | ", all.Reports.Select(p => $"{p.Plugin}:[{string.Join(",", p.MissingMasters)}]")));

        Check("SCANNED: the whole-order sweep scanned 3 plugins (Master, Clean, Bad — Ghost not in the order)",
            all.PluginsScanned == 3, $"scanned={all.PluginsScanned}");

        // ---- SCOPE: only the named plugin. ----
        var scoped = ErrorCheck.Run(r, new[] { "HcCeBad.esp" }, 1000);
        Check("SCOPE: scope=[HcCeBad.esp] reports only Bad (1 plugin scanned), Clean absent",
            scoped.Success && scoped.PluginsScanned == 1 && scoped.Reports.Count == 1
            && scoped.Reports[0].Plugin == "HcCeBad.esp",
            $"success={scoped.Success} scanned={scoped.PluginsScanned} reports={scoped.Reports.Count}");

        var q3 = ErrorCheck.Run(r, new[] { "HcCeNotReal.esp" }, 1000);
        Check("SCOPE-Q3: an unknown scope name fails LOUD ('not in the load order'), no reports",
            !q3.Success && q3.Reports.Count == 0 && q3.Error is not null
            && q3.Error.Contains("not in the load order", StringComparison.Ordinal),
            $"success={q3.Success} reports={q3.Reports.Count} err=[{q3.Error}]");

        // ---- CAP: limit=1 over Bad's 2 dangling refs. ----
        var capped = ErrorCheck.Run(r, new[] { "HcCeBad.esp" }, 1);
        Check("CAP: limit=1 returns 1 dangling ref but reports the TRUE total (2) and Capped",
            capped.TotalDangling == 2 && capped.Capped
            && capped.Reports.Count == 1 && capped.Reports[0].Dangling.Count == 1,
            $"total={capped.TotalDangling} capped={capped.Capped} collected={(capped.Reports.Count > 0 ? capped.Reports[0].Dangling.Count : -1)}");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "check-errors-guard: ALL PASS" : $"check-errors-guard: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }
}

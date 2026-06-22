using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for the SEQ staleness/coverage lint (1.3.1 item 7 — housecarl_validate_dialogue).
/// Drives the REAL core path (DialogueValidate.Run) over a synthetic load order of base masters — each with a
/// start-game-enabled quest — plus planted / omitted / aged .seq files under a temp Data root. NO Skyrim.esm, so it
/// runs in CI. Asserts the per-quest SeqLintFinding the validator attaches to the report.
///
/// A start-game-enabled quest whose .seq is MISSING, does NOT list it, or is OLDER than the plugin is dormant on a
/// fresh save — its dialogue never shows (the silent-failure class houseCARL refuses, Q3). Arms (ALL required):
///   SEQ-MISSING       — an SGE quest whose defining plugin has NO .seq → SeqExists=false (the coverage teeth).
///   SEQ-NOT-LISTED    — an SGE quest absent from its plugin's (present) .seq → SeqContainsQuest=false.
///   SEQ-STALE         — an SGE quest listed in a .seq OLDER than the plugin → SeqNewerThanPlugin=false (mtime teeth).
///   SEQ-COVERED-OK    — an SGE quest listed in a FRESH .seq → exists & contains & newer (no warning; the positive lock).
///   SEQ-CLEAN-NO-FLAG — a NON-SGE quest yields NO lint (SeqLint null) — fires ONLY for SGE quests, never nags.
///
/// Run: dotnet run --project src/housecarl-generator -- seq-staleness-guard
/// </summary>
public static class SeqStalenessProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — SEQ staleness/coverage lint (1.3.1 item 7)  ################");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var root = Path.Combine(Path.GetTempPath(), "hc-seq-staleness-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var dataDir = Path.Combine(root, "Data"); Directory.CreateDirectory(Path.Combine(dataDir, "SEQ"));

            // MISS: one SGE quest, NO .seq planted.
            string missPath = Path.Combine(root, "HcDvSeqMiss.esm");
            FormKey qMissFk = default;
            WriteMaster(missPath, "HcDvSeqMiss", m =>
            { var q = m.Quests.AddNew(); q.EditorID = "HcDvSeqMissQ"; q.Flags = Quest.Flag.StartGameEnabled; qMissFk = q.FormKey; });

            // STALE: one SGE quest, a .seq listing it but OLDER than the plugin.
            string stalePath = Path.Combine(root, "HcDvSeqStale.esm");
            FormKey qStaleFk = default;
            WriteMaster(stalePath, "HcDvSeqStale", m =>
            { var q = m.Quests.AddNew(); q.EditorID = "HcDvSeqStaleQ"; q.Flags = Quest.Flag.StartGameEnabled; qStaleFk = q.FormKey; });

            // OK: two SGE quests (qOk listed / qNotListed omitted) + a NON-SGE quest; a FRESH .seq listing qOk only.
            string okPath = Path.Combine(root, "HcDvSeqOk.esm");
            FormKey qOkFk = default, qNotListedFk = default, qPlainFk = default;
            WriteMaster(okPath, "HcDvSeqOk", m =>
            {
                var ok = m.Quests.AddNew(); ok.EditorID = "HcDvSeqOkQ"; ok.Flags = Quest.Flag.StartGameEnabled; qOkFk = ok.FormKey;
                var nl = m.Quests.AddNew(); nl.EditorID = "HcDvSeqNotListedQ"; nl.Flags = Quest.Flag.StartGameEnabled; qNotListedFk = nl.FormKey;
                var pl = m.Quests.AddNew(); pl.EditorID = "HcDvSeqPlainQ"; pl.Flags = Quest.Flag.RunOnce; qPlainFk = pl.FormKey;
            });

            // Plant the .seq files — the on-disk FormID is computed the SAME way the lint computes it.
            string staleSeq = Path.Combine(dataDir, "SEQ", "HcDvSeqStale.seq");
            File.WriteAllBytes(staleSeq, SeqFile.Serialize(new[] { SeqFile.OnDiskFormIdFromPlugin(stalePath, qStaleFk) }));
            string okSeq = Path.Combine(dataDir, "SEQ", "HcDvSeqOk.seq");
            File.WriteAllBytes(okSeq, SeqFile.Serialize(new[] { SeqFile.OnDiskFormIdFromPlugin(okPath, qOkFk) }));   // qNotListed deliberately omitted

            // mtimes: stale .seq OLDER than its plugin; ok .seq NEWER than its plugin.
            File.SetLastWriteTimeUtc(staleSeq, File.GetLastWriteTimeUtc(stalePath).AddHours(-1));
            File.SetLastWriteTimeUtc(okSeq, File.GetLastWriteTimeUtc(okPath).AddHours(1));

            using var resolver = LoadOrderResolver.Build(new[] { missPath, stalePath, okPath });
            using var assets = AssetResolver.Build("", "", dataDir, Array.Empty<string>(), Array.Empty<ActiveArchive>());
            Console.WriteLine($"-- setup: masters miss/stale/ok; .seq planted for stale (old) + ok (fresh, lists qOk only); none for miss --");
            Console.WriteLine();

            SeqLintFinding? Lint(FormKey fk) => DialogueValidate.Run(resolver, assets, fk).SeqLint;

            var miss = Lint(qMissFk);
            Check(miss is { QuestIsSge: true, SeqExists: false }, $"SEQ-MISSING SGE quest, no .seq → SeqExists=false — {Show(miss)}");

            var nl2 = Lint(qNotListedFk);
            Check(nl2 is { SeqExists: true, SeqContainsQuest: false }, $"SEQ-NOT-LISTED SGE quest absent from a present .seq → SeqContainsQuest=false — {Show(nl2)}");

            var stale = Lint(qStaleFk);
            Check(stale is { SeqExists: true, SeqContainsQuest: true, SeqNewerThanPlugin: false }, $"SEQ-STALE .seq older than plugin → SeqNewerThanPlugin=false — {Show(stale)}");

            var ok = Lint(qOkFk);
            Check(ok is { SeqExists: true, SeqContainsQuest: true, SeqNewerThanPlugin: true }, $"SEQ-COVERED-OK listed + fresh .seq → no warning — {Show(ok)}");

            var plain = Lint(qPlainFk);
            Check(plain is null, $"SEQ-CLEAN-NO-FLAG a non-SGE quest yields NO lint — {Show(plain)}");
        }
        catch (Exception ex) { Console.WriteLine("  FAIL  guard threw: " + ex); fail++; }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort temp cleanup */ } }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "ALL SEQ-STALENESS GUARD ARMS PASSED" : $"SEQ-STALENESS GUARD: {fail} ARM(S) FAILED");
        return fail == 0 ? 0 : 1;
    }

    static void WriteMaster(string path, string name, Action<SkyrimMod> build)
    {
        var m = new SkyrimMod(new ModKey(name, ModType.Master), SkyrimRelease.SkyrimSE);
        if (m.ModHeader.Stats.NextFormID < 0x800) m.ModHeader.Stats.NextFormID = 0x800;
        build(m);
        m.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();
    }

    static string Show(SeqLintFinding? s) => s is null ? "SeqLint=null"
        : $"sge={s.QuestIsSge} exists={s.SeqExists} contains={s.SeqContainsQuest?.ToString() ?? "?"} newer={s.SeqNewerThanPlugin?.ToString() ?? "?"} note=[{s.Note}]";
}

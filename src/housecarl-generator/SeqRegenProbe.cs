using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// COMPACT/MERGE Wave A3 — SEQ-REGEN guard for housecarl_compact_plugin (the asset-rename spine, third category).
/// Closes the LAST FormID-keyed gap a compact opens in the shipped tool: a .seq lists each start-game-enabled (SGE)
/// quest by its plugin-LOCAL, master-relative ON-DISK FormID, and a compact RENUMBERS every originating record — so a
/// pre-existing .seq goes STALE and its quests then silently never start (the exact failure SeqFile exists to prevent,
/// Q3). Unlike facegen (A1) / voice (A2), which RENAME files along the old→new map, the .seq is REGENERATED from the
/// already-renumbered P′ (SeqFile.Build, the housecarl_write_seq path) — the FormIDs come out correct because they're
/// read from the renumbered plugin. Drives the REAL <see cref="LoadOrderService.CompactPlugin"/> over a synthetic MO2
/// instance (the FacegenCarry / VoiceCarry pattern), so it pins the END-TO-END wiring, not the service in isolation.
///   NEW-FILE   — a source that SHIPPED a (stale) .seq is REFRESHED: compact writes a fresh &lt;modRoot&gt;\SEQ\&lt;plugin&gt;.seq
///                listing the renumbered quest's NEW on-disk FormID; the outcome reports 1 quest / Written=true.
///   IN-PLACE   — a STALE .seq (old FormID) beside the plugin is REPLACED in place: the refreshed .seq lists the NEW
///                on-disk FormID and the old one is GONE (the staleness fix A3 ships, proven directly).
///   MULTI-QUEST— two SGE quests both land in the refreshed .seq; a RunOnce-only quest is EXCLUDED (SGE filtering survives).
///   NO-SGE     — a plugin with no SGE quests writes NO .seq and is NOT a failure (SgeQuestCount 0, Written false, 0 WARN).
///   NO-SOURCE-SEQ — SGE quests but NO source .seq → a named WARN advising write_seq, NOT an invented file (the maintainer's
///                refresh-only narrowing): no .seq is written anywhere and the compact still succeeds.
///   SEPARATE-FOLDER-SEQ — a .seq in a DIFFERENT active mod folder (a prior write_seq's houseCARL_SEQ default) is still
///                detected by the VFS-aware gate → the refresh fires (re-review Finding 1: RED under a loose File.Exists).
///   SEQ-WARN   — a source .seq that exists but whose write FAILS (held under an exclusive lock) DEGRADES to a named WARN
///                (in the outcome AND the rendered output) while the compact STILL succeeds — A3's core Q3 contract.
/// Run: dotnet run --project src/housecarl-generator -- seq-regen-guard
/// </summary>
public static class SeqRegenProbe
{
    [CiProbe("seq-regen-guard")]
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  COMPACT Wave A3 — SEQ-regen guard (housecarl_compact_plugin)  ################");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var root = Path.Combine(Path.GetTempPath(), "hc-seq-regen-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            // ================= NEW-FILE: a source that SHIPPED a (stale) .seq is REFRESHED into the fresh output folder =================
            {
                var (mods, prof) = MakeInstance(Path.Combine(root, "newfile"));
                var key = new ModKey("SeqNf", ModType.Plugin);
                var qOld = new FormKey(key, 0x900);
                WriteMod(mods, "SeqNf", key, m => AddSgeQuest(m, qOld, "HcSeqQ"));
                WriteProfile(prof, new[] { key.FileName.String }, new[] { "*" + key.FileName }, new[] { "+SeqNf" });
                WriteSkyrimIni(prof);
                PlantSourceSeq(mods, "SeqNf", qOld);                       // the source mod SHIPPED a (now-stale) .seq → the refresh-only gate fires

                using var svc = LoadOrderService.WithInstance(Path.Combine(root, "newfile"), 0, new UserConfigStore(Path.Combine(root, "user-nf.json")));
                svc.Stats();

                var o = svc.CompactPlugin("SeqNf.esp");
                FormKey? qNew = null; bool seqOk = false, containsNew = false;
                if (o.Success && File.Exists(o.OutputPath))
                {
                    qNew = ReadQuestKey(o.OutputPath, "HcSeqQ");
                    var seqPath = Path.Combine(Path.GetDirectoryName(o.OutputPath)!, "SEQ", "SeqNf.seq");
                    if (qNew is { } nk && File.Exists(seqPath))
                    {
                        seqOk = new FileInfo(seqPath).Length > 0;
                        containsNew = SeqFile.SeqContains(File.ReadAllBytes(seqPath), SeqFile.OnDiskFormIdFromPlugin(o.OutputPath, nk));
                    }
                }
                var sr = o.SeqRegen;
                Check(o.Success && qNew is { } k && k.ID >= RemapEngine.EslFloor && k.ID <= RemapEngine.EslCeiling
                      && seqOk && containsNew
                      && sr is { SgeQuestCount: 1, Written: true } && sr.Failures.Count == 0,
                      $"NEW-FILE .seq regenerated for the renumbered SGE quest {qNew?.ID:X6} (file {seqOk}, lists-new {containsNew}, report {sr?.SgeQuestCount}q/written={sr?.Written}{(o.Success ? "" : "; ERR " + o.Error)})");
            }

            // ================= IN-PLACE: a STALE .seq beside the plugin is REPLACED with the renumbered FormIDs =================
            {
                var (mods, prof) = MakeInstance(Path.Combine(root, "inplace"));
                var key = new ModKey("SeqIp", ModType.Plugin);
                var qOld = new FormKey(key, 0x900);
                WriteMod(mods, "SeqIp", key, m => AddSgeQuest(m, qOld, "HcSeqQ"));
                WriteProfile(prof, new[] { key.FileName.String }, new[] { "*" + key.FileName }, new[] { "+SeqIp" });
                WriteSkyrimIni(prof);

                // plant a STALE .seq carrying the OLD on-disk FormID into the donor's own SEQ\ folder (the mod-author's
                // pre-compact .seq) — the regen must OVERWRITE it with the new id, else the quest never starts after compact.
                var srcPath = Path.Combine(mods, "SeqIp", "SeqIp.esp");
                var oldOnDisk = SeqFile.OnDiskFormIdFromPlugin(srcPath, qOld);
                var seqPath = Path.Combine(mods, "SeqIp", "SEQ", "SeqIp.seq");
                Directory.CreateDirectory(Path.GetDirectoryName(seqPath)!);
                File.WriteAllBytes(seqPath, SeqFile.Serialize(new[] { oldOnDisk }));

                using var svc = LoadOrderService.WithInstance(Path.Combine(root, "inplace"), 0, new UserConfigStore(Path.Combine(root, "user-ip.json")));
                svc.Stats();

                var o = svc.CompactPlugin("SeqIp.esp", inPlace: true, acknowledge: true);
                FormKey? qNew = null; bool hasNew = false, oldGone = false;
                if (o.Success)
                {
                    qNew = ReadQuestKey(o.OutputPath, "HcSeqQ");
                    if (qNew is { } nk)
                    {
                        var bytes = File.ReadAllBytes(seqPath);            // same path — in-place rewrites the donor's own .seq
                        hasNew = SeqFile.SeqContains(bytes, SeqFile.OnDiskFormIdFromPlugin(o.OutputPath, nk));
                        oldGone = !SeqFile.SeqContains(bytes, oldOnDisk);  // the stale FormID is GONE
                    }
                }
                var sr = o.SeqRegen;
                Check(o.Success && o.InPlace && hasNew && oldGone && sr is { SgeQuestCount: 1, Written: true },
                      $"IN-PLACE stale .seq REPLACED in place — now lists the new FormID {qNew?.ID:X6}, the old one gone (lists-new {hasNew}, old-gone {oldGone}, report written={sr?.Written}{(o.Success ? "" : "; ERR " + o.Error)})");
            }

            // ================= MULTI-QUEST: two SGE quests in the .seq; a RunOnce-only quest EXCLUDED (filtering survives) =================
            {
                var (mods, prof) = MakeInstance(Path.Combine(root, "multi"));
                var key = new ModKey("SeqMl", ModType.Plugin);
                var qa = new FormKey(key, 0x900);
                var qb = new FormKey(key, 0x901);
                var qPlain = new FormKey(key, 0x902);
                WriteMod(mods, "SeqMl", key, m => { AddSgeQuest(m, qa, "HcSeqA"); AddSgeQuest(m, qb, "HcSeqB"); AddPlainQuest(m, qPlain, "HcSeqPlain"); });
                WriteProfile(prof, new[] { key.FileName.String }, new[] { "*" + key.FileName }, new[] { "+SeqMl" });
                WriteSkyrimIni(prof);
                PlantSourceSeq(mods, "SeqMl", qa);                         // the source SHIPPED a .seq → the refresh-only gate fires

                using var svc = LoadOrderService.WithInstance(Path.Combine(root, "multi"), 0, new UserConfigStore(Path.Combine(root, "user-ml.json")));
                svc.Stats();

                var o = svc.CompactPlugin("SeqMl.esp");
                bool aIn = false, bIn = false, plainOut = false;
                if (o.Success && File.Exists(o.OutputPath))
                {
                    var seqPath = Path.Combine(Path.GetDirectoryName(o.OutputPath)!, "SEQ", "SeqMl.seq");
                    if (File.Exists(seqPath))
                    {
                        var bytes = File.ReadAllBytes(seqPath);
                        var aNew = ReadQuestKey(o.OutputPath, "HcSeqA");
                        var bNew = ReadQuestKey(o.OutputPath, "HcSeqB");
                        var pNew = ReadQuestKey(o.OutputPath, "HcSeqPlain");
                        if (aNew is { } ak) aIn = SeqFile.SeqContains(bytes, SeqFile.OnDiskFormIdFromPlugin(o.OutputPath, ak));
                        if (bNew is { } bk) bIn = SeqFile.SeqContains(bytes, SeqFile.OnDiskFormIdFromPlugin(o.OutputPath, bk));
                        if (pNew is { } pk) plainOut = !SeqFile.SeqContains(bytes, SeqFile.OnDiskFormIdFromPlugin(o.OutputPath, pk));
                    }
                }
                var sr = o.SeqRegen;
                Check(o.Success && aIn && bIn && plainOut && sr is { SgeQuestCount: 2, Written: true },
                      $"MULTI-QUEST both SGE quests in the .seq, the RunOnce quest excluded (A {aIn}, B {bIn}, plain-excluded {plainOut}, report {sr?.SgeQuestCount}q{(o.Success ? "" : "; ERR " + o.Error)})");
            }

            // ================= NO-SGE: a plugin with no start-game-enabled quests writes NO .seq and is NOT a failure =================
            {
                var (mods, prof) = MakeInstance(Path.Combine(root, "nosge"));
                var key = new ModKey("SeqNone", ModType.Plugin);
                var qPlain = new FormKey(key, 0x900);
                WriteMod(mods, "SeqNone", key, m => AddPlainQuest(m, qPlain, "HcSeqPlain"));
                WriteProfile(prof, new[] { key.FileName.String }, new[] { "*" + key.FileName }, new[] { "+SeqNone" });
                WriteSkyrimIni(prof);

                using var svc = LoadOrderService.WithInstance(Path.Combine(root, "nosge"), 0, new UserConfigStore(Path.Combine(root, "user-ns.json")));
                svc.Stats();

                var o = svc.CompactPlugin("SeqNone.esp");
                bool noSeq = false;
                if (o.Success && File.Exists(o.OutputPath))
                    noSeq = !File.Exists(Path.Combine(Path.GetDirectoryName(o.OutputPath)!, "SEQ", "SeqNone.seq"));
                var sr = o.SeqRegen;
                Check(o.Success && noSeq && sr is { SgeQuestCount: 0, Written: false } && sr.Failures.Count == 0,
                      $"NO-SGE no start-game-enabled quests → no .seq written, not a failure (noSeq {noSeq}, report {sr?.SgeQuestCount}q/written={sr?.Written}, warns {sr?.Failures.Count}{(o.Success ? "" : "; ERR " + o.Error)})");
            }

            // ================= NO-SOURCE-SEQ: SGE quests but NO source .seq → write nothing, advise (the refresh-only narrowing) =================
            // The maintainer's call: compaction never INVENTS a .seq. A mod with SGE quests that never shipped one gets a NAMED
            // advisory (run write_seq), not a silently-created file — and no .seq lands anywhere. This is the behavior under test.
            {
                var (mods, prof) = MakeInstance(Path.Combine(root, "nosrc"));
                var key = new ModKey("SeqNoSrc", ModType.Plugin);
                var qOld = new FormKey(key, 0x900);
                WriteMod(mods, "SeqNoSrc", key, m => AddSgeQuest(m, qOld, "HcSeqQ"));    // has an SGE quest, but NO source .seq is planted
                WriteProfile(prof, new[] { key.FileName.String }, new[] { "*" + key.FileName }, new[] { "+SeqNoSrc" });
                WriteSkyrimIni(prof);

                using var svc = LoadOrderService.WithInstance(Path.Combine(root, "nosrc"), 0, new UserConfigStore(Path.Combine(root, "user-nosrc.json")));
                svc.Stats();

                var o = svc.CompactPlugin("SeqNoSrc.esp");
                bool noSeqWritten = o.Success && File.Exists(o.OutputPath)
                                    && !File.Exists(Path.Combine(Path.GetDirectoryName(o.OutputPath)!, "SEQ", "SeqNoSrc.seq"));
                var sr = o.SeqRegen;
                var rendered = o.Success ? WriteTools.RenderCompact(o) : "";
                Check(o.Success && noSeqWritten && sr is { Written: false, SgeQuestCount: 1 } && sr.Failures.Count >= 1
                      && rendered.Contains("SEQ WARN") && rendered.Contains("but no .seq"),
                      $"NO-SOURCE-SEQ SGE quests but no source .seq → no file invented, advisory WARN, compact succeeds (no-file {noSeqWritten}, written {sr?.Written}, warns {sr?.Failures.Count}{(o.Success ? "" : "; ERR " + o.Error)})");
            }

            // ================= SEPARATE-FOLDER-SEQ (re-review Finding 1): a .seq in a DIFFERENT active mod folder is detected (VFS gate) =================
            // The refresh-only gate must see a .seq anywhere the engine reads (loose roots + BSAs), not just the source mod
            // folder — else the write_seq→compact workflow (write_seq files the .seq in its OWN houseCARL_SEQ mod folder for a
            // non-houseCARL plugin) wrongly advises, and the stale .seq elsewhere breaks the renumbered quests. Plant the .seq
            // in a SEPARATE active mod folder and prove the refresh STILL fires. RED under a loose File.Exists on the source folder.
            {
                var (mods, prof) = MakeInstance(Path.Combine(root, "sep"));
                var key = new ModKey("SeqSep", ModType.Plugin);
                var qOld = new FormKey(key, 0x900);
                WriteMod(mods, "SeqSep", key, m => AddSgeQuest(m, qOld, "HcSeqQ"));
                var sepSeqDir = Path.Combine(mods, "SeqSepSeq", "SEQ");     // a SEPARATE, plugin-less, ACTIVE mod folder holds the .seq
                Directory.CreateDirectory(sepSeqDir);
                File.WriteAllBytes(Path.Combine(sepSeqDir, "SeqSep.seq"),
                    SeqFile.Serialize(new[] { SeqFile.OnDiskFormIdFromPlugin(Path.Combine(mods, "SeqSep", "SeqSep.esp"), qOld) }));
                WriteProfile(prof, new[] { key.FileName.String }, new[] { "*" + key.FileName }, new[] { "+SeqSepSeq", "+SeqSep" });
                WriteSkyrimIni(prof);

                using var svc = LoadOrderService.WithInstance(Path.Combine(root, "sep"), 0, new UserConfigStore(Path.Combine(root, "user-sep.json")));
                svc.Stats();

                var o = svc.CompactPlugin("SeqSep.esp");
                FormKey? qNew = null; bool refreshed = false;
                if (o.Success && File.Exists(o.OutputPath))
                {
                    qNew = ReadQuestKey(o.OutputPath, "HcSeqQ");
                    var outSeq = Path.Combine(Path.GetDirectoryName(o.OutputPath)!, "SEQ", "SeqSep.seq");
                    if (qNew is { } nk && File.Exists(outSeq))
                        refreshed = SeqFile.SeqContains(File.ReadAllBytes(outSeq), SeqFile.OnDiskFormIdFromPlugin(o.OutputPath, nk));
                }
                var sr = o.SeqRegen;
                Check(o.Success && refreshed && sr is { Written: true, SgeQuestCount: 1 },
                      $"SEPARATE-FOLDER-SEQ a .seq in a different active mod folder is detected → refresh fires (refreshed {refreshed}, written {sr?.Written}{(o.Success ? "" : "; ERR " + o.Error)})");
            }

            // ================= SEQ-WARN: a source .seq exists but its write FAILS → DEGRADES to a named WARN, compact STILL succeeds =================
            // A3's load-bearing Q3 contract: a .seq it CANNOT write is a NAMED warning, never a silent stale/missing .seq, and
            // never a failure of the already-written compaction. Under the refresh-only gate the source must SHIP a .seq for the
            // refresh to be attempted, so keep a real source .seq AND hold an EXCLUSIVE lock on it (a realistic "file in use" —
            // MO2/the game holding it) so the write fails. Assert it degrades, not aborts, AND that the warning reaches the
            // user-visible tool output (RenderCompact). CI is windows-latest: File.Replace onto a FileShare.None-locked dest
            // throws a sharing violation, while File.Exists still satisfies the gate.
            {
                var (mods, prof) = MakeInstance(Path.Combine(root, "warn"));
                var key = new ModKey("SeqWarn", ModType.Plugin);
                var qOld = new FormKey(key, 0x900);
                WriteMod(mods, "SeqWarn", key, m => AddSgeQuest(m, qOld, "HcSeqQ"));
                WriteProfile(prof, new[] { key.FileName.String }, new[] { "*" + key.FileName }, new[] { "+SeqWarn" });
                WriteSkyrimIni(prof);
                var seqPath = PlantSourceSeq(mods, "SeqWarn", qOld);       // the source SHIPPED a .seq (gate satisfied) — but we lock it so the write fails

                using (new FileStream(seqPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))   // exclusive lock → the refresh write fails
                {
                    using var svc = LoadOrderService.WithInstance(Path.Combine(root, "warn"), 0, new UserConfigStore(Path.Combine(root, "user-warn.json")));
                    svc.Stats();

                    var o = svc.CompactPlugin("SeqWarn.esp", inPlace: true, acknowledge: true);
                    var sr = o.SeqRegen;
                    var rendered = WriteTools.RenderCompact(o);           // the WARN must reach the user-visible output, not just the outcome
                    Check(o.Success && sr is { Written: false, SgeQuestCount: 1 } && sr.Failures.Count >= 1
                          && rendered.Contains("SEQ WARN") && rendered.Contains("could not write") && !rendered.StartsWith("error:"),
                          $"SEQ-WARN locked .seq dest → named write-failure WARN, compact STILL succeeds (success {o.Success}, written {sr?.Written}, warns {sr?.Failures.Count}, rendered-warn {rendered.Contains("SEQ WARN")}{(o.Success ? "" : "; ERR " + o.Error)})");
                }
            }
        }
        finally { try { Directory.Delete(root, true); } catch { } }

        Console.WriteLine();
        Console.WriteLine($"=== seq-regen-guard: {(fail == 0 ? "PASS" : $"FAIL ({fail})")} ===");
        return fail == 0 ? 0 : 1;
    }

    // ---- fixture builders ----

    /// <summary>Add a Start-Game-Enabled quest with an EXPLICIT FormKey (so the readback can find it by EditorID and the
    /// renumber moves a known id) — the minimal SGE record SeqFile.Build includes in a .seq.</summary>
    static void AddSgeQuest(SkyrimMod m, FormKey questKey, string edid) =>
        m.Quests.Add(new Quest(questKey, SkyrimRelease.SkyrimSE) { EditorID = edid, Flags = Quest.Flag.StartGameEnabled });

    /// <summary>Add a RunOnce-only (NOT start-game-enabled) quest — an originating record so the plugin compacts, but one
    /// the .seq must EXCLUDE (SGE-flag filtering).</summary>
    static void AddPlainQuest(SkyrimMod m, FormKey questKey, string edid) =>
        m.Quests.Add(new Quest(questKey, SkyrimRelease.SkyrimSE) { EditorID = edid, Flags = Quest.Flag.RunOnce });

    /// <summary>Read P′ back and return the (renumbered) FormKey of the quest with <paramref name="edid"/>.</summary>
    static FormKey? ReadQuestKey(string pPrimePath, string edid)
    {
        using var pp = SkyrimMod.CreateFromBinaryOverlay(pPrimePath, SkyrimRelease.SkyrimSE);
        return pp.Quests.FirstOrDefault(q => q.EditorID == edid)?.FormKey;
    }

    /// <summary>Plant a (now-stale) source <c>.seq</c> at <c>&lt;mods&gt;\&lt;folder&gt;\SEQ\&lt;folder&gt;.seq</c> listing
    /// <paramref name="questKey"/>'s OLD on-disk FormID — the "the source mod SHIPPED a .seq" precondition the refresh-only
    /// gate checks (File.Exists on the source plugin's SEQ\&lt;basename&gt;.seq). Returns the planted path.</summary>
    static string PlantSourceSeq(string mods, string folder, FormKey questKey)
    {
        var pluginPath = Path.Combine(mods, folder, folder + ".esp");
        var seqPath = Path.Combine(mods, folder, "SEQ", folder + ".seq");
        Directory.CreateDirectory(Path.GetDirectoryName(seqPath)!);
        File.WriteAllBytes(seqPath, SeqFile.Serialize(new[] { SeqFile.OnDiskFormIdFromPlugin(pluginPath, questKey) }));
        return seqPath;
    }

    // ---- synthetic MO2 layout helpers (the FacegenCarry / VoiceCarry probe pattern) ----

    static (string mods, string prof) MakeInstance(string inst)
    {
        var mods = Path.Combine(inst, "mods");
        var data = Path.Combine(inst, "game", "Data");
        var prof = Path.Combine(inst, "profiles", "Default");
        foreach (var d in new[] { mods, data, prof }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(inst, "game").Replace(@"\", @"\\") + ")\r\n");
        return (mods, prof);
    }

    static void WriteMod(string mods, string folder, ModKey key, Action<SkyrimMod> build)
    {
        var dir = Path.Combine(mods, folder);
        Directory.CreateDirectory(dir);
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        build(m);
        m.BeginWrite.ToPath(Path.Combine(dir, key.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
    }

    static void WriteProfile(string prof, string[] loadorder, string[] plugins, string[] modlist)
    {
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", loadorder) + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), string.Join("\r\n", plugins) + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n" + string.Join("\r\n", modlist) + "\r\n");
    }

    static void WriteSkyrimIni(string prof) =>
        File.WriteAllText(Path.Combine(prof, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");
}

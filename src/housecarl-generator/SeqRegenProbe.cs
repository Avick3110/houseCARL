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
///   NEW-FILE   — compacting an SGE-quest mod writes a fresh, correct &lt;modRoot&gt;\SEQ\&lt;plugin&gt;.seq listing the
///                renumbered quest's new on-disk FormID; the outcome reports 1 quest / Written=true.
///   IN-PLACE   — a STALE .seq (old FormID) planted beside the plugin is REPLACED in place: the regenerated .seq lists
///                the NEW on-disk FormID and the old one is GONE (the staleness fix A3 ships, proven directly).
///   MULTI-QUEST— two SGE quests both land in the .seq; a RunOnce-only quest is EXCLUDED (SGE filtering survives compact).
///   NO-SGE     — a plugin with no SGE quests writes NO .seq and is NOT a failure (SgeQuestCount 0, Written false, 0 WARN).
/// Run: dotnet run --project src/housecarl-generator -- seq-regen-guard
/// </summary>
public static class SeqRegenProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  COMPACT Wave A3 — SEQ-regen guard (housecarl_compact_plugin)  ################");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var root = Path.Combine(Path.GetTempPath(), "hc-seq-regen-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            // ================= NEW-FILE: compact writes a fresh, correct .seq for the renumbered SGE quest =================
            {
                var (mods, prof) = MakeInstance(Path.Combine(root, "newfile"));
                var key = new ModKey("SeqNf", ModType.Plugin);
                var qOld = new FormKey(key, 0x900);
                WriteMod(mods, "SeqNf", key, m => AddSgeQuest(m, qOld, "HcSeqQ"));
                WriteProfile(prof, new[] { key.FileName.String }, new[] { "*" + key.FileName }, new[] { "+SeqNf" });
                WriteSkyrimIni(prof);

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

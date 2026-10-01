using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The <c>.seq</c> refresh a compact runs (<see cref="LoadOrderService.CompactPlugin"/>, from the
/// <c>seq-regen-guard</c> probe): a compact renumbers every start-game-enabled quest, so a source that shipped a
/// <c>.seq</c> gets it rebuilt from the renumbered plugin; with no source <c>.seq</c>, or a write that fails, the
/// compact still succeeds and the report carries a named SEQ WARN. Each test builds its own instance.</summary>
[Trait("tier", "integration")]
public sealed class CompactSeqRegenTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-compact-seq-regen-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    // NEW-FILE .seq regenerated for the renumbered SGE quest (file written, lists the new FormID, report 1 quest / written)
    [Fact]
    public void ANewFileCompactWritesAFreshSeqListingTheRenumberedQuest()
    {
        var (mods, inst) = Instance("newfile", "SeqNf", m => AddQuest(m, 0x900, "HcSeqQ", Quest.Flag.StartGameEnabled));
        PlantSourceSeq(mods, "SeqNf", 0x900);
        using var svc = Service(inst);

        var o = svc.CompactPlugin("SeqNf.esp");

        Assert.True(o.Success, o.Error);
        var qNew = QuestKey(o.OutputPath, "HcSeqQ");
        Assert.InRange(qNew.ID, RemapEngine.EslFloor, RemapEngine.EslCeiling);
        var seq = Path.Combine(Path.GetDirectoryName(o.OutputPath)!, "SEQ", "SeqNf.seq");
        Assert.True(SeqFile.SeqContains(File.ReadAllBytes(seq), SeqFile.OnDiskFormIdFromPlugin(o.OutputPath, qNew)));
        Assert.True(o.SeqRegen is { SgeQuestCount: 1, Written: true }, $"{o.SeqRegen}");
        Assert.Empty(o.SeqRegen.Failures);
    }

    // IN-PLACE stale .seq REPLACED in place — now lists the new FormID, the old one gone
    [Fact]
    public void AnInPlaceCompactReplacesTheStaleSeq()
    {
        var (mods, inst) = Instance("inplace", "SeqIp", m => AddQuest(m, 0x900, "HcSeqQ", Quest.Flag.StartGameEnabled));
        var (seq, oldOnDisk) = PlantSourceSeq(mods, "SeqIp", 0x900);
        using var svc = Service(inst);

        var o = svc.CompactPlugin("SeqIp.esp", inPlace: true, acknowledge: true);

        Assert.True(o.Success, o.Error);
        Assert.True(o.InPlace);
        var bytes = File.ReadAllBytes(seq);
        Assert.True(SeqFile.SeqContains(bytes, SeqFile.OnDiskFormIdFromPlugin(o.OutputPath, QuestKey(o.OutputPath, "HcSeqQ"))));
        Assert.False(SeqFile.SeqContains(bytes, oldOnDisk));
        Assert.True(o.SeqRegen is { SgeQuestCount: 1, Written: true }, $"{o.SeqRegen}");
    }

    // MULTI-QUEST both SGE quests in the .seq, the RunOnce quest excluded
    [Fact]
    public void BothSgeQuestsAreListedAndTheRunOnceQuestIsNot()
    {
        var (mods, inst) = Instance("multi", "SeqMl", m =>
        {
            AddQuest(m, 0x900, "HcSeqA", Quest.Flag.StartGameEnabled);
            AddQuest(m, 0x901, "HcSeqB", Quest.Flag.StartGameEnabled);
            AddQuest(m, 0x902, "HcSeqPlain", Quest.Flag.RunOnce);
        });
        PlantSourceSeq(mods, "SeqMl", 0x900);
        using var svc = Service(inst);

        var o = svc.CompactPlugin("SeqMl.esp");

        Assert.True(o.Success, o.Error);
        var bytes = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(o.OutputPath)!, "SEQ", "SeqMl.seq"));
        bool Listed(string edid) => SeqFile.SeqContains(bytes, SeqFile.OnDiskFormIdFromPlugin(o.OutputPath, QuestKey(o.OutputPath, edid)));
        Assert.True(Listed("HcSeqA"));
        Assert.True(Listed("HcSeqB"));
        Assert.False(Listed("HcSeqPlain"));
        Assert.True(o.SeqRegen is { SgeQuestCount: 2, Written: true }, $"{o.SeqRegen}");
    }

    // NO-SGE no start-game-enabled quests → no .seq written, not a failure
    [Fact]
    public void APluginWithNoSgeQuestsWritesNoSeqAndNoWarning()
    {
        var (_, inst) = Instance("nosge", "SeqNone", m => AddQuest(m, 0x900, "HcSeqPlain", Quest.Flag.RunOnce));
        using var svc = Service(inst);

        var o = svc.CompactPlugin("SeqNone.esp");

        Assert.True(o.Success, o.Error);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(o.OutputPath)!, "SEQ", "SeqNone.seq")));
        Assert.True(o.SeqRegen is { SgeQuestCount: 0, Written: false }, $"{o.SeqRegen}");
        Assert.Empty(o.SeqRegen.Failures);
    }

    // NO-SOURCE-SEQ SGE quests but no source .seq → no file invented, advisory WARN, compact succeeds
    [Fact]
    public void SgeQuestsWithNoSourceSeqGetAWarningNotAnInventedFile()
    {
        var (_, inst) = Instance("nosrc", "SeqNoSrc", m => AddQuest(m, 0x900, "HcSeqQ", Quest.Flag.StartGameEnabled));
        using var svc = Service(inst);

        var o = svc.CompactPlugin("SeqNoSrc.esp");

        Assert.True(o.Success, o.Error);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(o.OutputPath)!, "SEQ", "SeqNoSrc.seq")));
        Assert.True(o.SeqRegen is { Written: false, SgeQuestCount: 1 }, $"{o.SeqRegen}");
        Assert.NotEmpty(o.SeqRegen.Failures);
        var rendered = WriteTools.RenderCompact(o);
        Assert.Contains("SEQ WARN", rendered);
        Assert.Contains("but no .seq", rendered);
    }

    // SEPARATE-FOLDER-SEQ a .seq in a different active mod folder is detected → refresh fires
    [Fact]
    public void ASeqInAnotherActiveModFolderStillTriggersTheRefresh()
    {
        var (mods, inst) = Instance("sep", "SeqSep", m => AddQuest(m, 0x900, "HcSeqQ", Quest.Flag.StartGameEnabled),
            extraMods: new[] { "SeqSepSeq" });
        var sepSeq = Path.Combine(mods, "SeqSepSeq", "SEQ", "SeqSep.seq");
        Directory.CreateDirectory(Path.GetDirectoryName(sepSeq)!);
        File.WriteAllBytes(sepSeq, SeqFile.Serialize(new[] {
            SeqFile.OnDiskFormIdFromPlugin(Path.Combine(mods, "SeqSep", "SeqSep.esp"), new FormKey(Key("SeqSep"), 0x900)) }));
        using var svc = Service(inst);

        var o = svc.CompactPlugin("SeqSep.esp");

        Assert.True(o.Success, o.Error);
        var outSeq = Path.Combine(Path.GetDirectoryName(o.OutputPath)!, "SEQ", "SeqSep.seq");
        Assert.True(SeqFile.SeqContains(File.ReadAllBytes(outSeq), SeqFile.OnDiskFormIdFromPlugin(o.OutputPath, QuestKey(o.OutputPath, "HcSeqQ"))));
        Assert.True(o.SeqRegen is { Written: true, SgeQuestCount: 1 }, $"{o.SeqRegen}");
    }

    // SEQ-WARN locked .seq dest → named write-failure WARN, compact STILL succeeds
    [Fact]
    public void ASeqThatCannotBeWrittenDegradesToAWarning()
    {
        var (mods, inst) = Instance("warn", "SeqWarn", m => AddQuest(m, 0x900, "HcSeqQ", Quest.Flag.StartGameEnabled));
        var (seq, _) = PlantSourceSeq(mods, "SeqWarn", 0x900);

        using (new FileStream(seq, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var svc = Service(inst);
            var o = svc.CompactPlugin("SeqWarn.esp", inPlace: true, acknowledge: true);

            Assert.True(o.Success, o.Error);
            Assert.True(o.SeqRegen is { Written: false, SgeQuestCount: 1 }, $"{o.SeqRegen}");
            Assert.NotEmpty(o.SeqRegen.Failures);
            var rendered = WriteTools.RenderCompact(o);
            Assert.Contains("SEQ WARN", rendered);
            Assert.Contains("could not write", rendered);
            Assert.DoesNotContain("error:", rendered);
        }
    }

    static ModKey Key(string name) => new(name, ModType.Plugin);

    static void AddQuest(SkyrimMod m, uint id, string edid, Quest.Flag flags) =>
        m.Quests.Add(new Quest(new FormKey(m.ModKey, id), SkyrimRelease.SkyrimSE) { EditorID = edid, Flags = flags });

    static FormKey QuestKey(string pluginPath, string edid)
    {
        using var p = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE);
        return p.Quests.First(q => q.EditorID == edid).FormKey;
    }

    /// <summary>Plant a source <c>.seq</c> at <c>mods/&lt;name&gt;/SEQ/&lt;name&gt;.seq</c> listing the quest's
    /// pre-compact on-disk FormID; returns its path and that FormID.</summary>
    static (string Path, uint OnDisk) PlantSourceSeq(string mods, string name, uint id)
    {
        var onDisk = SeqFile.OnDiskFormIdFromPlugin(Path.Combine(mods, name, name + ".esp"), new FormKey(Key(name), id));
        var seq = Path.Combine(mods, name, "SEQ", name + ".seq");
        Directory.CreateDirectory(Path.GetDirectoryName(seq)!);
        File.WriteAllBytes(seq, SeqFile.Serialize(new[] { onDisk }));
        return (seq, onDisk);
    }

    /// <summary>A synthetic MO2 instance with one plugin <paramref name="name"/>.esp in its own mod folder, active, plus
    /// any plugin-less <paramref name="extraMods"/> folders enabled ahead of it.</summary>
    (string Mods, string Instance) Instance(string dir, string name, Action<SkyrimMod> build, string[]? extraMods = null)
    {
        var inst = Path.Combine(_root, dir);
        var mods = Path.Combine(inst, "mods");
        var prof = Path.Combine(inst, "profiles", "Default");
        foreach (var d in new[] { mods, Path.Combine(inst, "game", "Data"), prof, Path.Combine(mods, name) }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(inst, "game").Replace("\\", "\\\\") + ")\r\n");

        var m = new SkyrimMod(Key(name), SkyrimRelease.SkyrimSE);
        build(m);
        m.BeginWrite.ToPath(Path.Combine(mods, name, name + ".esp")).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var modlist = (extraMods ?? Array.Empty<string>()).Select(x => "+" + x).Append("+" + name);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), $"# header\r\n{name}.esp\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), $"*{name}.esp\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n" + string.Join("\r\n", modlist) + "\r\n");
        File.WriteAllText(Path.Combine(prof, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");
        return (mods, inst);
    }

    LoadOrderService Service(string inst)
    {
        var svc = LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(inst, "user.json")));
        svc.Stats();
        return svc;
    }
}

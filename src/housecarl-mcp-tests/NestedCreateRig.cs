using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A master with the shapes nested create nests under (a weapon, a topic with a quest, an interior cell) and a
/// voiced speaker chain (an NPC whose voice type is in the master), plus a load order over it and the create calls the
/// nested-create tests make against it.</summary>
public sealed class NestedCreateRig : IDisposable
{
    public const string MasterName = "HcNcGdMaster.esm";

    public WritePathRig Rig { get; } = new();
    public string MasterPath { get; }
    public LoadOrderResolver Order { get; }
    public FormKey Weapon { get; }
    public FormKey Topic { get; }
    public FormKey Cell { get; }
    public FormKey Voice { get; }
    public FormKey Npc { get; }
    public FormKey Quest { get; }

    public NestedCreateRig()
    {
        var m = new SkyrimMod(ModKey.FromFileName(MasterName), SkyrimRelease.SkyrimSE);
        var w = m.Weapons.AddNew(); w.EditorID = "HcNcGdWeap"; w.BasicStats = new WeaponBasicStats { Damage = 10 };
        Weapon = w.FormKey;
        var topic = m.DialogTopics.AddNew(); topic.EditorID = "HcNcGdTopic";
        Topic = topic.FormKey;
        var voice = m.VoiceTypes.AddNew(); voice.EditorID = "HcNcGdVoice";
        Voice = voice.FormKey;
        var quest = m.Quests.AddNew(); quest.EditorID = "HcNcGdQuest";
        Quest = quest.FormKey;
        var npc = m.Npcs.AddNew(); npc.EditorID = "HcNcGdNpc"; npc.Voice.SetTo(voice.FormKey);
        Npc = npc.FormKey;
        topic.Quest.SetTo(quest.FormKey);

        var cell = new Mutagen.Bethesda.Skyrim.Cell(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcNcGdCell" };
        Cell = cell.FormKey;
        var sub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        sub.Cells.Add(cell);
        var block = new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock };
        block.SubBlocks.Add(sub);
        m.Cells.Records.Add(block);

        MasterPath = Rig.Write(m);
        Order = Rig.Order(MasterPath);
    }

    public static WritePatchBuilder.CreateSpec Spec(string type, string edid, params WriteRequest[] edits)
        => new() { RecordType = type, EditorId = edid, Edits = edits };

    public static WritePatchBuilder.CreateSpec Under(string parent, string type, string edid, params WriteRequest[] edits)
        => new() { RecordType = type, EditorId = edid, ParentRef = parent, Edits = edits };

    public WritePatchBuilder.CreateOutcome CreateAt(string path, bool extend, params WritePatchBuilder.CreateSpec[] specs)
        => WritePatchBuilder.CreateRecords(Order, TestCorpus.Rulebook, specs, path, extend);

    /// <summary>Create into a fresh output file; returns the outcome and the file's path.</summary>
    public (WritePatchBuilder.CreateOutcome Outcome, string Path) Create(string outName, params WritePatchBuilder.CreateSpec[] specs)
    {
        var path = Rig.Out(outName);
        return (CreateAt(path, false, specs), path);
    }

    /// <summary>Create that must be refused with no file written; returns the refusal.</summary>
    public string Refused(string outName, params WritePatchBuilder.CreateSpec[] specs)
    {
        var (o, path) = Create(outName, specs);
        Assert.False(o.Success, "the create was accepted");
        Assert.False(File.Exists(path), "a refused create wrote a file");
        Assert.NotNull(o.Error);
        return o.Error!;
    }

    public ISkyrimModGetter Open(string path) => Rig.Open(path);

    /// <summary>The INFO with <paramref name="info"/> under any topic of the plugin, or null.</summary>
    public static IDialogResponsesGetter? Info(ISkyrimModGetter mod, FormKey info)
        => mod.DialogTopics.SelectMany(t => t.Responses).FirstOrDefault(i => i.FormKey == info);

    public static List<FormKey> Responses(ISkyrimModGetter mod, FormKey topic)
        => mod.DialogTopics.Single(t => t.FormKey == topic).Responses.Select(i => i.FormKey).ToList();

    public void Dispose() => Rig.Dispose();
}

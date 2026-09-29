using Mutagen.Bethesda.Plugins;
using HousecarlCore;
using Xunit;
using static HousecarlMcpTests.NestedCreateRig;

namespace HousecarlMcpTests;

/// <summary>A child record created under its parent: in the same call as the parent, under a parent in the load order,
/// into a named collection, and under a parent an earlier call wrote into the patch; and the refusals when the parent is
/// missing, cannot hold the child, leaves the collection ambiguous, or is declared later. Migrated from the
/// nested-create-guard probe (arms ONESHOT..EXTEND).</summary>
[Trait("tier", "integration")]
public sealed class NestedCreateSlotTests : IDisposable
{
    readonly NestedCreateRig _w = new();

    // ONESHOT: topic + first INFO in one call, both at the 0x800 floor, the INFO in the topic's Responses, distinct keys.
    [Fact]
    public void ATopicAndItsFirstLineInOneCallLandTheLineUnderTheTopic()
    {
        var (o, path) = _w.Create("HcNcOneShot.esp",
            Spec("DialogTopic", "HcNcOsTopic"),
            Under("HcNcOsTopic", "DialogResponses", "HcNcOsInfo"));
        Assert.True(o.Success, o.Error);
        Assert.Equal(2, o.Created.Count);
        Assert.All(o.Created, c => Assert.True(c.FormKey.ID >= 0x800));
        Assert.NotEqual(o.Created[0].FormKey, o.Created[1].FormKey);
        Assert.Contains(o.Created[1].FormKey, Responses(_w.Open(path), o.Created[0].FormKey));
    }

    // MULTICHILD: topic + two INFOs, the second with a Prompt edit; both under the topic, the edit only on the second.
    [Fact]
    public void TwoLinesUnderANewTopicKeepTheirOwnFieldEdits()
    {
        var (o, path) = _w.Create("HcNcMulti.esp",
            Spec("DialogTopic", "HcNcMTopic"),
            Under("HcNcMTopic", "DialogResponses", "HcNcML1"),
            Under("HcNcMTopic", "DialogResponses", "HcNcML2", WritePathRig.Req("DialogResponses", "Prompt", "Set", "houseCARL line two")));
        Assert.True(o.Success, o.Error);
        Assert.Equal(3, o.Created.Count);
        var mod = _w.Open(path);
        var under = Responses(mod, o.Created[0].FormKey);
        Assert.Contains(o.Created[1].FormKey, under);
        Assert.Contains(o.Created[2].FormKey, under);
        Assert.Equal("houseCARL line two", Info(mod, o.Created[2].FormKey)!.Prompt?.String);
        Assert.NotEqual("houseCARL line two", Info(mod, o.Created[1].FormKey)!.Prompt?.String);
    }

    // INTOTOPIC: an INFO under a master topic by FormKey lands local at the floor, in the topic override's Responses.
    [Fact]
    public void ALineUnderAMasterTopicLandsInTheTopicOverride()
    {
        var (o, path) = _w.Create("HcNcIntoTopic.esp", Under(_w.Topic.ToString(), "DialogResponses", "HcNcN2Info"));
        Assert.True(o.Success, o.Error);
        var fk = Assert.Single(o.Created).FormKey;
        Assert.True(fk.ID >= 0x800);
        Assert.Equal("HcNcIntoTopic.esp", fk.ModKey.FileName.String);
        Assert.Contains(fk, Responses(_w.Open(path), _w.Topic));
    }

    // INTOCELL: a PlacedObject into a master cell, collection named 'Persistent', lands in the cell override's Persistent.
    [Fact]
    public void APlacedObjectIntoANamedCellCollectionLandsThere()
    {
        var spec = Under(_w.Cell.ToString(), "PlacedObject", "HcNcN3Ref") with { IntoCollection = "Persistent" };
        var (o, path) = _w.Create("HcNcIntoCell.esp", spec);
        Assert.True(o.Success, o.Error);
        var fk = Assert.Single(o.Created).FormKey;
        Assert.True(fk.ID >= 0x800);
        Assert.Equal("HcNcIntoCell.esp", fk.ModKey.FileName.String);
        var cell = _w.Open(path).Cells.SelectMany(b => b.SubBlocks).SelectMany(s => s.Cells).Single(c => c.FormKey == _w.Cell);
        Assert.Contains(fk, cell.Persistent.Select(p => p.FormKey));
    }

    // REJ-NOPARENT: a nested type with no parent refuses, no file.
    [Fact]
    public void ANestedTypeWithNoParentIsRefused()
    {
        var err = _w.Refused("HcNcRejN4.esp", Spec("DialogResponses", "HcNcN4"));
        Assert.Contains("parent", err, StringComparison.OrdinalIgnoreCase);
    }

    // REJ-BADPARENT: an INFO under a Weapon refuses 'cannot be created under', no file.
    [Fact]
    public void ALineUnderAWeaponIsRefused()
    {
        var err = _w.Refused("HcNcRejN5.esp", Under(_w.Weapon.ToString(), "DialogResponses", "HcNcN5"));
        Assert.Contains("cannot be created under", err, StringComparison.OrdinalIgnoreCase);
    }

    // REJ-AMBIG: a PlacedObject into a Cell with no collection named refuses naming 'more than one' + 'Persistent'.
    [Fact]
    public void APlacedObjectIntoACellWithNoCollectionNamedIsRefused()
    {
        var err = _w.Refused("HcNcRejN6.esp", Under(_w.Cell.ToString(), "PlacedObject", "HcNcN6"));
        Assert.Contains("more than one", err, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Persistent", err, StringComparison.OrdinalIgnoreCase);
    }

    // REJ-FWDSIB: a child whose same-call parent is declared later refuses 'earlier in this call'.
    [Fact]
    public void AChildBeforeItsSameCallParentIsRefused()
    {
        var err = _w.Refused("HcNcRejN7.esp",
            Under("HcNcN7Topic", "DialogResponses", "HcNcN7Info"),
            Spec("DialogTopic", "HcNcN7Topic"));
        Assert.Contains("earlier in this call", err, StringComparison.OrdinalIgnoreCase);
    }

    // EXTEND: a topic created by a prior call is a parent for an extend call; the prior line and the new one are both under it.
    [Fact]
    public void AParentFromAnEarlierCallTakesANewLineAndKeepsTheOldOne()
    {
        var path = _w.Rig.Out("HcNcExtend.esp");
        var o1 = _w.CreateAt(path, false, Spec("DialogTopic", "HcNcExTopic"), Under("HcNcExTopic", "DialogResponses", "HcNcExL1"));
        Assert.True(o1.Success, o1.Error);
        var topic = o1.Created[0].FormKey;
        var o2 = _w.CreateAt(path, true, Under(topic.ToString(), "DialogResponses", "HcNcExL2"));
        Assert.True(o2.Success, o2.Error);
        var under = Responses(_w.Open(path), topic);
        Assert.Contains(o1.Created[1].FormKey, under);
        Assert.Contains(o2.Created[0].FormKey, under);
    }

    // EXTEND: a parent in neither the load order nor the patch still refuses, naming both.
    [Fact]
    public void AParentInNeitherTheOrderNorThePatchIsRefusedNamingBoth()
    {
        var path = _w.Rig.Out("HcNcExtendGhost.esp");
        var o1 = _w.CreateAt(path, false, Spec("DialogTopic", "HcNcExTopic"));
        Assert.True(o1.Success, o1.Error);
        var o = _w.CreateAt(path, true, Under("0F0F0F:" + MasterName, "DialogResponses", "HcNcExGhost"));
        Assert.False(o.Success);
        Assert.Contains("load order", o.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("patch", o.Error, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _w.Dispose();
}

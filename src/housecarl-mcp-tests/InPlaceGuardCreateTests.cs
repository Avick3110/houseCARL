using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;
using W = HousecarlMcpTests.InPlaceGuardWorld;

namespace HousecarlMcpTests;

/// <summary>The in-place CREATE lane at the builder (<c>WritePatchBuilder.CreateRecordsInPlace</c>): where a new record's
/// FormID comes from, the header it pulls in, and nesting under a parent the target does and does not own. Moved from
/// the <c>inplace-guard</c> probe.</summary>
[Trait("tier", "integration")]
[Collection(InPlaceGuardCollection.Name)]
public sealed class InPlaceGuardCreateTests
{
    readonly W _w;
    public InPlaceGuardCreateTests(W w) { _w = w; _w.UseCorpus(); }

    WritePatchBuilder.CreateOutcome Create(string target, string targetName, WritePatchBuilder.CreateSpec[] specs, params string[] order)
    {
        using var r = LoadOrderResolver.Build(order);
        return WritePatchBuilder.CreateRecordsInPlace(r, _w.Rulebook, specs, target, targetName);
    }

    static bool InUser(FormKey fk) => fk.ModKey.FileName.String.Equals(W.UserName, StringComparison.OrdinalIgnoreCase);

    // J create-into-target (fresh FormID in target, counter advances)
    [Fact]
    public void AnInPlaceCreateAllocatesInTheTargetAndAdvancesItsCounter()
    {
        var user = _w.FreshUser();
        var o = Create(user, W.UserName,
            new[] { new WritePatchBuilder.CreateSpec { RecordType = "Keyword", EditorId = "HcIP_NewKw", Edits = Array.Empty<WriteRequest>() } },
            _w.MasterPath, user, _w.HighPath);
        Assert.True(o.Success, o.Error);
        Assert.True(o.InPlace);
        var fk = Assert.Single(o.Created).FormKey;
        Assert.True(InUser(fk));
        Assert.True(fk.ID >= 0x800);
        Assert.Equal(fk.ID + 1, W.NextFormId(user));   // not the author's 0x123
        Assert.Equal("HcIP_NewKw", W.EditorIdAt(user, fk));
        Assert.Equal(20, W.Damage(user, _w.Weapon));
        Assert.Equal("UserSword", W.Name(user, _w.Weapon));
    }

    // O cross-master reference adds the master (xEdit-parity serialize)
    // Two referenced plugins, not one: Mutagen writes a lone master off the FormKey alone, so only a header it must
    // SORT needs the serialize's master set, and one reference passes with that set emptied.
    [Fact]
    public void AnInPlaceCreateReferencingOtherPluginsAddsThemAsMasters()
    {
        var bare = Path.Combine(_w.NewDir(), "HcInPlaceBare.esp");
        new SkyrimMod(new ModKey("HcInPlaceBare", ModType.Plugin), SkyrimRelease.SkyrimSE)
            .BeginWrite.ToPath(bare).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        WriteRequest AddItem(string id) => new() { RecordType = "FormList", Path = new[] { "Items" }, Verb = "Add", Value = id };
        var o = Create(bare, "HcInPlaceBare.esp",
            new[] { new WritePatchBuilder.CreateSpec { RecordType = "FormList", EditorId = "HcIP_RefFlst",
                Edits = new[] { AddItem(_w.WeaponId), AddItem($"{_w.HighKeyword.ID:X6}:{W.HighName}") } } },
            _w.MasterPath, _w.HighPath, bare);
        Assert.True(o.Success, o.Error);
        Assert.True(o.InPlace);
        Assert.True(W.HasMaster(bare, W.MasterName));
        Assert.True(W.HasMaster(bare, W.HighName));
    }

    // M nested under a FOREIGN parent works in place (parent overridden in + child)
    [Fact]
    public void AnInPlaceCreateUnderAForeignParentOverridesTheParentIn()
    {
        var user = _w.FreshUser();
        var o = Create(user, W.UserName,
            new[] { new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcIP_NewLine", Edits = Array.Empty<WriteRequest>(), ParentRef = _w.TopicId } },
            _w.MasterPath, user, _w.HighPath);
        Assert.True(o.Success, o.Error);
        Assert.True(o.InPlace);
        var fk = Assert.Single(o.Created).FormKey;
        Assert.True(InUser(fk) && fk.ID >= 0x800);
        Assert.True(W.Present(user, _w.Topic));
        Assert.Equal(20, W.Damage(user, _w.Weapon));
    }

    // P same-call nested unit (new topic + line) in place
    [Fact]
    public void AnInPlaceCreateOfATopicAndItsLineInOneCallLandsBoth()
    {
        var user = _w.FreshUser();
        var o = Create(user, W.UserName, new[]
        {
            new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HcIP_NewTopic", Edits = Array.Empty<WriteRequest>() },
            new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcIP_TopicLine", Edits = Array.Empty<WriteRequest>(), ParentRef = "HcIP_NewTopic" },
        }, _w.MasterPath, user);
        Assert.True(o.Success, o.Error);
        Assert.True(o.InPlace);
        Assert.Equal(2, o.Created.Count);
        Assert.All(o.Created, c => Assert.True(InUser(c.FormKey)));
        Assert.True(W.Present(user, o.Created[0].FormKey));
        Assert.True(W.Present(user, o.Created[1].FormKey));
    }

    // Q exterior cell in place under a foreign worldspace (LinkCacheFor + placement)
    [Fact]
    public void AnInPlaceExteriorCellUnderAForeignWorldspaceOverridesTheWorldspaceIn()
    {
        var user = _w.FreshUser();
        var o = Create(user, W.UserName,
            new[] { new WritePatchBuilder.CreateSpec { RecordType = "Cell", EditorId = "HcIP_ExtCell", Edits = Array.Empty<WriteRequest>(), ParentRef = _w.WorldId, Grid = "5,-12" } },
            _w.MasterPath, user);
        Assert.True(o.Success, o.Error);
        Assert.True(o.InPlace);
        var fk = Assert.Single(o.Created).FormKey;
        Assert.True(InUser(fk) && fk.ID >= 0x800);
        Assert.True(W.Present(user, _w.World));
    }
}

using Mutagen.Bethesda.Plugins;
using HousecarlCore;
using Xunit;
using W = HousecarlMcpTests.InPlaceGuardWorld;

namespace HousecarlMcpTests;

/// <summary>The in-place REMOVE lane at the builder (<c>WritePatchBuilder.RemoveRecordsInPlace</c>): it drops only what
/// the target carries, prunes a master the removal orphaned and keeps one still referenced. Moved from the
/// <c>inplace-guard</c> probe.</summary>
[Trait("tier", "integration")]
[Collection(InPlaceGuardCollection.Name)]
public sealed class InPlaceGuardRemoveTests
{
    readonly W _w;
    public InPlaceGuardRemoveTests(W w) { _w = w; _w.UseCorpus(); }

    static WritePatchBuilder.RemovalOutcome Remove(FormKey fk, string target, string targetName, params string[] order)
    {
        using var r = LoadOrderResolver.Build(order);
        return WritePatchBuilder.RemoveRecordsInPlace(r, new[] { fk }, target, targetName);
    }

    // R remove an override in place (record gone, orphaned master pruned, inert shell)
    [Fact]
    public void RemovingTheTargetsOnlyOverridePrunesTheMasterItOrphaned()
    {
        var user = _w.FreshUser();
        var o = Remove(_w.Weapon, user, W.UserName, _w.MasterPath, user);
        Assert.True(o.Success, o.Error);
        Assert.True(o.InPlace);
        Assert.Single(o.Removed);
        Assert.Equal(0, o.RemainingRecords);
        Assert.False(W.Present(user, _w.Weapon));
        Assert.False(W.HasMaster(user, W.MasterName));
        Assert.Equal(10, W.Damage(_w.MasterPath, _w.Weapon));
    }

    // S refuse-if-not-carried (remove only what the file owns)
    [Fact]
    public void RemovingARecordTheTargetDoesNotCarryIsRefused()
    {
        var user = _w.FreshUser();
        var before = File.ReadAllBytes(user);
        var o = Remove(_w.Weapon2, user, W.UserName, _w.MasterPath, user, _w.HighPath);
        Assert.False(o.Success);
        Assert.Contains("not carried by", o.Error);
        Assert.Equal(before, File.ReadAllBytes(user));
    }

    // T surgical remove (drop one own record, keep the rest + referenced master)
    [Fact]
    public void RemovingOneOwnRecordKeepsTheRestAndAStillReferencedMaster()
    {
        var user = _w.FreshUser();
        FormKey kw;
        using (var rc = LoadOrderResolver.Build(new[] { _w.MasterPath, user, _w.HighPath }))
        {
            var c = WritePatchBuilder.CreateRecordsInPlace(rc, _w.Rulebook,
                new[] { new WritePatchBuilder.CreateSpec { RecordType = "Keyword", EditorId = "HcIP_RmKw", Edits = Array.Empty<WriteRequest>() } },
                user, W.UserName);
            kw = Assert.Single(c.Created).FormKey;
        }
        var o = Remove(kw, user, W.UserName, _w.MasterPath, user, _w.HighPath);
        Assert.True(o.Success, o.Error);
        Assert.True(o.InPlace);
        Assert.Single(o.Removed);
        Assert.Equal(1, o.RemainingRecords);
        Assert.False(W.Present(user, kw));
        Assert.Equal(20, W.Damage(user, _w.Weapon));
        Assert.Equal("UserSword", W.Name(user, _w.Weapon));
        Assert.True(W.HasMaster(user, W.MasterName));
    }

    // LOC-C remove-in-place refuses a localized target with the refusal's own sentence, file untouched
    [Fact]
    public void RemovingFromALocalizedTargetIsRefusedInTheRefusalsOwnSentence()
    {
        var loc = _w.FreshLocalized();
        var before = File.ReadAllBytes(loc);
        var o = Remove(_w.Weapon, loc, W.LocName, _w.MasterPath, loc);
        Assert.False(o.Success);
        Assert.StartsWith("houseCARL did not write", o.Error, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(loc));
    }

    // LOC-D remove-in-place on the NON-localized twin still removes
    [Fact]
    public void RemovingFromTheNonLocalizedTwinStillRemoves()
    {
        var user = _w.FreshUser();
        var o = Remove(_w.Weapon, user, W.UserName, _w.MasterPath, user);
        Assert.True(o.Success, o.Error);
        Assert.Single(o.Removed);
    }
}

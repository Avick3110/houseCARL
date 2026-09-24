using HousecarlCore;
using Xunit;
using W = HousecarlMcpTests.InPlaceGuardWorld;

namespace HousecarlMcpTests;

/// <summary>The in-place EDIT lane at the builder (<c>WritePatchBuilder.ApplyInPlace</c>): what it sources, what it keeps
/// in the header, and the two refusals that leave the file byte-untouched. Moved from the <c>inplace-guard</c> probe.</summary>
[Trait("tier", "integration")]
[Collection(InPlaceGuardCollection.Name)]
public sealed class InPlaceGuardEditTests
{
    readonly W _w;
    public InPlaceGuardEditTests(W w) { _w = w; }

    WritePatchBuilder.PatchOutcome Edit(string target, string targetName, string[] path, string verb, string value, params string[] order)
    {
        using var r = LoadOrderResolver.Build(order);
        return WritePatchBuilder.ApplyInPlace(r, TestCorpus.Rulebook,
            new[] { new WritePatchBuilder.PatchEdit { Target = _w.Weapon, Path = path, Verb = verb, Value = value } },
            target, targetName);
    }

    WritePatchBuilder.PatchOutcome SetDamage(string target, string value, params string[] order) =>
        Edit(target, W.UserName, new[] { "BasicStats", "Damage" }, "Set", value, order);

    // A content-source (edit user body, not winner)
    [Fact]
    public void AnInPlaceEditKeepsTheTargetsOwnNameNotTheWinners()
    {
        var user = _w.FreshUser();
        var o = SetDamage(user, "55", _w.MasterPath, user, _w.HighPath);
        Assert.True(o.Success, o.Error);
        Assert.True(o.InPlace);
        Assert.Equal(55, W.Damage(user, _w.Weapon));
        Assert.Equal("UserSword", W.Name(user, _w.Weapon));
        Assert.Equal(99, W.Damage(_w.HighPath, _w.Weapon));
    }

    // C counter+masters preserved (no floor, no baseline)
    [Fact]
    public void AnInPlaceEditKeepsTheAuthorsCounterAndAddsNoBaselineMaster()
    {
        var user = _w.FreshUser();
        Assert.True(SetDamage(user, "55", _w.MasterPath, user, _w.HighPath).Success);
        Assert.Equal(0x123u, W.NextFormId(user));
        Assert.Equal(new[] { W.MasterName }, W.Masters(user));
    }

    // B refuse-if-undefined (edit only what the file owns)
    [Fact]
    public void AnInPlaceEditOfARecordTheTargetDoesNotDefineIsRefused()
    {
        var user = _w.FreshUser();
        var before = File.ReadAllBytes(user);
        using var r = LoadOrderResolver.Build(new[] { _w.MasterPath, user, _w.HighPath });
        var o = WritePatchBuilder.ApplyInPlace(r, TestCorpus.Rulebook,
            new[] { new WritePatchBuilder.PatchEdit { Target = _w.Weapon2, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = "7" } },
            user, W.UserName);
        Assert.False(o.Success);
        Assert.Contains("does not define", o.Error);
        Assert.Equal(before, File.ReadAllBytes(user));
    }

    // D flat lock (winner==target; ReleaseOverlay before swap)
    [Fact]
    public void AnInPlaceEditOfARecordTheTargetWinsLands()
    {
        var user = _w.FreshUser();
        var o = SetDamage(user, "42", _w.MasterPath, user);
        Assert.True(o.Success, o.Error);
        Assert.Equal(42, W.Damage(user, _w.Weapon));
    }

    // Y edit-lane master GROW (link to an active undeclared plugin lands + grows the header + re-sort note)
    [Fact]
    public void AnInPlaceEditLinkingAnActiveUndeclaredPluginGrowsTheMasters()
    {
        var user = _w.FreshUser();
        var o = Edit(user, W.UserName, new[] { "Keywords" }, "Add", $"{_w.HighKeyword.ID:X6}:{W.HighName}", _w.MasterPath, user, _w.HighPath);
        Assert.True(o.Success, o.Error);
        Assert.True(o.InPlace);
        Assert.True(W.WeaponHasKeyword(user, _w.Weapon, _w.HighKeyword));
        Assert.True(W.HasMaster(user, W.HighName));
        Assert.True(W.HasMaster(user, W.MasterName));
        Assert.False(W.HasMaster(user, "Skyrim.esm") || W.HasMaster(user, "Update.esm"));
        Assert.Contains("added as a master", o.Note, StringComparison.OrdinalIgnoreCase);
    }

    // Z edit-lane link to a NON-load-order plugin still refuses loud, file untouched
    [Fact]
    public void AnInPlaceEditLinkingAPluginOutsideTheOrderIsRefused()
    {
        var user = _w.FreshUser();
        var before = File.ReadAllBytes(user);
        var o = Edit(user, W.UserName, new[] { "Keywords" }, "Add", "000ABC:NotInOrder.esp", _w.MasterPath, user, _w.HighPath);
        Assert.False(o.Success);
        Assert.Contains("NOT active", o.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllBytes(user));
    }

    // LOC-A in-place REFUSES a localized target verbatim; plugin untouched, no staging residue
    [Fact]
    public void AnInPlaceEditOfALocalizedTargetIsRefusedBeforeStaging()
    {
        var loc = _w.FreshLocalized();
        var dir = Path.GetDirectoryName(loc)!;
        var before = File.ReadAllBytes(loc);
        var strings = W.StringsSnapshot(dir);
        var o = Edit(loc, W.LocName, new[] { "BasicStats", "Damage" }, "Set", "55", _w.MasterPath, loc);
        Assert.False(o.Success);
        // StartsWith: the refusal comes before the serialize, so it must not render behind the serialize failure's lead.
        Assert.StartsWith("houseCARL did not write", o.Error, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(loc));
        Assert.False(Directory.Exists(Path.Combine(dir, ".housecarl-tmp")));
        Assert.Equal(strings, W.StringsSnapshot(dir));
    }

    // LOC-B the same edit on the NON-localized twin still writes (the guard fires on localization only)
    [Fact]
    public void TheSameEditOnTheNonLocalizedTwinWrites()
    {
        var user = _w.FreshUser();
        var o = SetDamage(user, "55", _w.MasterPath, user);
        Assert.True(o.Success, o.Error);
        Assert.Equal(55, W.Damage(user, _w.Weapon));
    }
}

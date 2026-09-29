using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;
using Xunit;
using A = HousecarlMcpTests.LocalizedWriteGuardFixture.Arrangement;
using F = HousecarlMcpTests.LocalizedWriteGuardFixture;

namespace HousecarlMcpTests;

/// <summary>The in-place write of a localized plugin refuses in every arrangement of its tables, names where the text
/// is and the hazard that arrangement carries, asserts no absence it did not check, and leaves the plugin and its
/// tables byte-identical. Moved from the <c>localized-write-guard</c> probe (refusals, and a refusal leaves
/// everything alone).</summary>
[Trait("tier", "integration")]
public sealed class LocalizedInPlaceRefusalTests
{
    // Probe: <shape> refuses naming its own shape — where the text is, its hazard — and its refusal stages nothing.
    [Theory]
    [InlineData(A.LooseComplete, "Strings folder beside it", "English", "same breath")]
    [InlineData(A.LoosePartial, "French has no", "same breath")]
    [InlineData(A.LooseAndGameData, "two places", "same breath")]
    [InlineData(A.GameDataOnly, "not beside it", "does not reach your game's Data folder", "SHADOW")]
    [InlineData(A.Nowhere, "cannot find its text", "merges", "cannot see the files")]
    public void EachArrangementRefusesInPlaceInItsOwnWords(A v, params string[] words)
    {
        using var f = new F(v);
        var msg = f.WriteThrough(_ => { });
        Assert.NotNull(msg);
        foreach (var w in words) Assert.Contains(w, msg);
        // Ends on the settled sentence: the engine's own refusal names no lane.
        Assert.EndsWith("does not edit a localized plugin in place.", msg);
        Assert.False(Directory.Exists(f.StagingDir));
    }

    // Probe: <shadow shape>'s refusal does not describe replacing a table set it would never replace.
    [Theory]
    [InlineData(A.GameDataOnly)]
    [InlineData(A.MalformedBsa)]
    [InlineData(A.Nowhere)]
    public void AShadowShapeIsNotToldAboutTheScramble(A v)
    {
        using var f = new F(v);
        var msg = LocalizedStrings.RefusalFor(f.Plugin, "ZRef.esp", f.DataDir);
        Assert.False(string.IsNullOrEmpty(msg));
        Assert.DoesNotContain("same breath", msg);
        Assert.DoesNotContain("belongs to other records", msg);
    }

    // Probe: <loose shape>'s refusal DOES name the scramble, which is the real hazard for a set beside the plugin.
    [Theory]
    [InlineData(A.LooseComplete)]
    [InlineData(A.LoosePartial)]
    [InlineData(A.LooseAndGameData)]
    public void ALooseShapeIsToldAboutTheScramble(A v)
    {
        using var f = new F(v);
        Assert.Contains("belongs to other records", LocalizedStrings.RefusalFor(f.Plugin, "ZRef.esp", f.DataDir));
    }

    // Probe: <shape>'s refusal does not tell the caller to rearrange the files and retry.
    // Strengthened: the probe read a null (it wrote) as "no retry advice"; the refusal must exist first.
    [Theory]
    [InlineData(A.LooseComplete)]
    [InlineData(A.LoosePartial)]
    [InlineData(A.GameDataOnly)]
    [InlineData(A.LooseAndGameData)]
    public void NoRefusalPromisesARetryAfterRearranging(A v)
    {
        using var f = new F(v);
        var msg = f.WriteThrough(_ => { });
        Assert.NotNull(msg);
        Assert.DoesNotContain("then retry", msg, StringComparison.OrdinalIgnoreCase);
    }

    // Probe: an unreadable archive beside a localized plugin takes the plugin's own open down, and the pre-flight
    // every in-place lane runs first refuses it in its own words.
    [Fact]
    public void AnUnreadableArchiveBreaksTheOpenAndThePreflightRefusesIt()
    {
        using var f = new F(A.MalformedBsa);
        Assert.ThrowsAny<Exception>(() => SkyrimMod.CreateFromBinary(f.Plugin, SkyrimRelease.SkyrimSE));
        var msg = LocalizedStrings.RefusalFor(f.Plugin, "ZRef.esp", f.DataDir);
        Assert.NotNull(msg);
        Assert.Contains("could not be read", msg);
        Assert.Contains("does not edit a localized plugin in place", msg);
    }

    // Probe: <folder of a neighbour's tables / own tables in an unmodelled language>: the refusal describes the folder
    // and names its file(s) rather than asserting an absence it did not check; under the cap nothing is counted off.
    [Theory]
    [InlineData(A.NeighbourTablesOnly)]
    [InlineData(A.UnknownLanguageToken)]
    public void AFolderOfUnmatchedTablesIsDescribedNotDeclaredEmpty(A v)
    {
        using var f = new F(v);
        var msg = f.WriteThrough(_ => { });
        var files = LocalizedStrings.Assess(f.Plugin, f.DataDir).UnmatchedTables;
        Assert.NotNull(msg);
        Assert.Contains("matched none", msg);
        Assert.DoesNotContain("no Strings folder", msg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no .STRINGS files", msg);
        Assert.Equal(6, files.Total);
        Assert.Equal(files.Total, files.Names.Count);
        foreach (var n in files.Names) Assert.Contains(n, msg);
        Assert.DoesNotContain(" more —", msg);
    }

    // Probe: over the cap, the refusal reports the folder's TRUE count, says the list stopped with how many it did
    // not quote, and every name it quotes is actually there.
    [Fact]
    public void OverTheCapTheRefusalCountsTheFolderAndSaysTheListStopped()
    {
        using var f = new F(A.ManyNeighbourTables);
        var msg = f.WriteThrough(_ => { });
        var files = LocalizedStrings.Assess(f.Plugin, f.DataDir).UnmatchedTables;
        Assert.Equal(12, files.Total);
        Assert.Equal(UnmatchedTableFiles.Cap, files.Names.Count);
        Assert.NotNull(msg);
        Assert.Contains("holds 12 ", msg);
        Assert.Contains(", and 4 more", msg);
        foreach (var n in files.Names) Assert.Contains(n, msg);
    }

    // Probe: with the folder genuinely gone, the refusal DOES say there are no .STRINGS files beside it.
    // Also the probe's "a folder that really is gone still gets the checked absence" (unlistable-folder section).
    [Fact]
    public void AGoneFolderGetsTheCheckedAbsence()
    {
        using var f = new F(A.Nowhere);
        var msg = f.WriteThrough(_ => { });
        Assert.NotNull(msg);
        Assert.Contains("no .STRINGS files beside it", msg);
        Assert.DoesNotContain("matched none", msg);
    }

    // Probe: the archive refusal names the loose set beside the plugin too, not only the archive.
    [Fact]
    public void AnArchiveRefusalAlsoNamesTheLooseSetBesideIt()
    {
        using var f = new F(A.MalformedBsa);
        var msg = LocalizedStrings.RefusalFor(f.Plugin, "ZRef.esp", f.DataDir);
        Assert.NotNull(msg);
        Assert.Contains("ZRef.bsa", msg);
        Assert.Contains("and there is also", msg);
        Assert.Contains("English, French", msg);
    }

    // Probe: an archive with nothing loose beside it names one location, because there is one.
    [Fact]
    public void AnArchiveWithNothingLooseNamesOneLocation()
    {
        using var f = new F(A.GameDataBsa);
        var msg = LocalizedStrings.RefusalFor(f.Plugin, "ZRef.esp", f.DataDir);
        Assert.NotNull(msg);
        Assert.Contains("ZGame.bsa", msg);
        Assert.DoesNotContain("and there is also", msg);
    }

    // Probe: with no game-Data folder known the refusal says it was not searched, rather than claiming it was.
    // Strengthened: the other direction, a known folder, does claim the search.
    [Fact]
    public void AnUnknownGameFolderIsSaidToBeUnsearched()
    {
        using var f = new F(A.Nowhere);
        var unknown = LocalizedStrings.RefusalFor(f.Plugin, "ZRef.esp", dataDir: null);
        Assert.NotNull(unknown);
        Assert.DoesNotContain("or in your game folder", unknown);
        Assert.Contains("could not be determined", unknown);
        Assert.Contains("or in your game folder", LocalizedStrings.RefusalFor(f.Plugin, "ZRef.esp", f.DataDir));
    }

    // Probe: the write refuses, every file beside the plugin is byte-identical after the refusal, and both languages
    // still read their original values.
    // Strengthened: the snapshot is counted (plugin and six tables), so it cannot compare an empty set.
    [Fact]
    public void ARefusalLeavesThePluginAndItsTablesByteIdentical()
    {
        using var f = new F(A.LooseComplete);
        var before = f.Snapshot();
        var msg = f.WriteThrough(w => w.Name = F.Loc("EDITED 0", "FR EDITED 0"));
        Assert.NotNull(msg);
        Assert.Equal(7, before.Count);
        Assert.Equal(before, f.Snapshot());
        Assert.Equal("REF NAME 0", f.WeaponName(Language.English));
        Assert.Equal("FR NAME 0", f.WeaponName(Language.French));
    }
}

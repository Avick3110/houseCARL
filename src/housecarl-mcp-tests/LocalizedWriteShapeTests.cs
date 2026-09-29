using Xunit;
using A = HousecarlMcpTests.LocalizedWriteGuardFixture.Arrangement;
using F = HousecarlMcpTests.LocalizedWriteGuardFixture;

namespace HousecarlMcpTests;

/// <summary>Which shape the classifier puts a localized plugin in, for each arrangement of its .STRINGS tables, and
/// the two predicates read off a shape. Moved from the <c>localized-write-guard</c> probe (shape classification and
/// its ConfirmedLocalized walk).</summary>
[Trait("tier", "integration")]
public sealed class LocalizedWriteShapeTests
{
    // Probe: complete loose set beside the plugin classifies, both languages seen.
    [Fact]
    public void ACompleteLooseSetClassifiesWithBothLanguages()
    {
        using var f = new F(A.LooseComplete);
        var a = LocalizedStrings.Assess(f.Plugin, f.DataDir);
        Assert.Equal(LocalizedShape.LooseComplete, a.Shape);
        Assert.Equal(new[] { "English", "French" }, a.Languages);
    }

    // Probe: a language missing table kinds is LoosePartial and the missing kinds are named.
    [Fact]
    public void ALanguageMissingTableKindsIsPartialAndNamesTheKinds()
    {
        using var f = new F(A.LoosePartial);
        var a = LocalizedStrings.Assess(f.Plugin, f.DataDir);
        Assert.Equal(LocalizedShape.LoosePartial, a.Shape);
        Assert.Equal(new[] { "DLSTRINGS", "ILSTRINGS" }, a.IncompleteLanguages["French"]);
        Assert.False(a.IncompleteLanguages.ContainsKey("English"));
    }

    // Probe: a loose set duplicated in game-Data classifies as the duplicate shape.
    [Fact]
    public void ALooseSetDuplicatedInGameDataIsTheDuplicateShape()
    {
        using var f = new F(A.LooseAndGameData);
        var a = LocalizedStrings.Assess(f.Plugin, f.DataDir);
        Assert.Equal(LocalizedShape.LooseWithGameDataDuplicate, a.Shape);
        Assert.Equal(new[] { "English", "French" }, a.GameDataLanguages);
    }

    // Probe: strings resolving from game-Data only classifies.
    [Fact]
    public void TablesOnlyInGameDataClassifyAsGameDataOnly()
    {
        using var f = new F(A.GameDataOnly);
        Assert.Equal(LocalizedShape.GameDataOnly, LocalizedStrings.Assess(f.Plugin, f.DataDir).Shape);
    }

    // Probe: no findable strings source classifies.
    [Fact]
    public void NoFindableSourceClassifiesAsNowhere()
    {
        using var f = new F(A.Nowhere);
        Assert.Equal(LocalizedShape.Nowhere, LocalizedStrings.Assess(f.Plugin, f.DataDir).Shape);
    }

    // Probe: an archive that cannot be parsed is refused rather than assumed harmless.
    [Fact]
    public void AnUnparseableArchiveBesideThePluginIsEmbeddedAndUnreadable()
    {
        using var f = new F(A.MalformedBsa);
        var a = LocalizedStrings.Assess(f.Plugin, f.DataDir);
        Assert.Equal(LocalizedShape.BsaEmbedded, a.Shape);
        Assert.True(a.BsaUnreadable);
        Assert.False(a.BsaInGameData);
    }

    // Probe (TWO LOCATIONS fixture): an archive beside a plugin that also has a loose set carries the loose languages.
    [Fact]
    public void AnArchiveShapeStillCarriesTheLooseLanguagesBesideIt()
    {
        using var f = new F(A.MalformedBsa);
        Assert.Equal(2, LocalizedStrings.Assess(f.Plugin, f.DataDir).Languages.Count);
    }

    // Probe: a plugin whose name prefixes a sibling's claims only its OWN tables.
    // Strengthened: the probe's "no ZRef_extra file" held vacuously on an empty list; this also counts the six it owns.
    [Fact]
    public void APluginWhoseNamePrefixesASiblingsClaimsOnlyItsOwnTables()
    {
        using var f = new F(A.SiblingStem);
        var sibling = f.Plugin.Replace("ZRef.esp", "ZRef_extra.esp");
        Assert.Equal(2, LocalizedStrings.Assess(f.Plugin, f.DataDir).Languages.Count);
        Assert.Equal(2, LocalizedStrings.Assess(sibling, f.DataDir).Languages.Count);
        var mine = LocalizedStrings.OwnTableFiles(f.Plugin).Select(Path.GetFileName).ToList();
        Assert.Equal(6, mine.Count);
        Assert.DoesNotContain(mine, n => n!.StartsWith("ZRef_extra", StringComparison.OrdinalIgnoreCase));
    }

    // Probe: an unknown game-Data folder is recorded as unknown, not as checked.
    [Fact]
    public void AnUnknownGameDataFolderIsRecordedAsUnknown()
    {
        using var f = new F(A.LooseComplete);
        Assert.False(LocalizedStrings.Assess(f.Plugin, f.DataDir).GameDataUnknown);
        var a = LocalizedStrings.Assess(f.Plugin, dataDir: null);
        Assert.Equal(LocalizedShape.LooseComplete, a.Shape);
        Assert.True(a.GameDataUnknown);
    }

    // Probe: an archive found in the game folder is recorded as such, not as beside the plugin.
    [Fact]
    public void AnArchiveInTheGameFolderIsRecordedAsInGameData()
    {
        using var f = new F(A.GameDataBsa);
        var a = LocalizedStrings.Assess(f.Plugin, f.DataDir);
        Assert.Equal(LocalizedShape.BsaEmbedded, a.Shape);
        Assert.True(a.BsaInGameData);
    }

    // Probe: "houseCARL read the flag and it is set" is narrower than "refuse this" — ConfirmedLocalized per shape.
    [Theory]
    [InlineData(LocalizedShape.NotLocalized, false)]
    [InlineData(LocalizedShape.Unreadable, false)]
    [InlineData(LocalizedShape.LooseComplete, true)]
    [InlineData(LocalizedShape.LoosePartial, true)]
    [InlineData(LocalizedShape.LooseWithGameDataDuplicate, true)]
    [InlineData(LocalizedShape.BsaEmbedded, true)]
    [InlineData(LocalizedShape.GameDataOnly, true)]
    [InlineData(LocalizedShape.StringsFolderUnreadable, true)]
    [InlineData(LocalizedShape.ModFolderUnreadable, true)]
    [InlineData(LocalizedShape.Nowhere, true)]
    public void ConfirmedLocalizedIsTrueOnlyWhereTheFlagWasReadSet(LocalizedShape shape, bool confirmed)
        => Assert.Equal(confirmed, LocalizedStrings.ConfirmedLocalized(shape));

    // Probe: …and they disagree on exactly the shape this exists for (Unreadable).
    [Fact]
    public void ConfirmedLocalizedAndTheRefusalDisagreeOnlyOnUnreadable()
    {
        var disagree = Enum.GetValues<LocalizedShape>()
                           .Where(s => (s != LocalizedShape.NotLocalized) != LocalizedStrings.ConfirmedLocalized(s));
        Assert.Equal(new[] { LocalizedShape.Unreadable }, disagree);
    }
}

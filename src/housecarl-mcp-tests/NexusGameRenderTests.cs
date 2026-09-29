using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The rendered Nexus text carries the game that was asked for (#835): the mod page URL, the header line, the
/// search's game name and its zero-hit category note, and the update check's not-found label, with the Skyrim SE
/// default reading exactly as before. Migrated from the <c>nexus-game-guard</c> probe; each test carries the probe
/// arm's wording. Pure rendering, no network.</summary>
[Trait("tier", "unit")]
public sealed class NexusGameRenderTests
{
    static readonly NexusGame Bg3 = NexusClient.KnownGame("baldursgate3")!;

    static readonly NexusModDetail Detail = new(3479, "Aether's No Party Limits", "1.0", null, null, "Aether", "Gameplay",
        10, 100, null, null, false, "published", true,
        Array.Empty<NexusRequirement>(), Array.Empty<NexusFile>(), Array.Empty<string>());

    static readonly NexusSearchHit Hit =
        new(3479, "Aether's No Party Limits", "1.0", "Aether", 10, 100, null, false, null, "Gameplay");

    static NexusSearchResult OneHit => new(1, new[] { Hit });

    static NexusSearchResult NoHits => new(0, Array.Empty<NexusSearchHit>());

    // Probe: "a BG3 mod renders a baldursgate3 page URL".
    [Fact]
    public void ABg3ModRendersABaldursgate3PageUrl() =>
        Assert.Contains("https://www.nexusmods.com/baldursgate3/mods/3479", Render.Mod(Detail, Bg3));

    // Probe: "a BG3 mod renders no skyrimspecialedition URL".
    [Fact]
    public void ABg3ModRendersNoSkyrimSpecialEditionUrl() =>
        Assert.DoesNotContain("skyrimspecialedition", Render.Mod(Detail, Bg3));

    // Probe: "a non-default mod lookup names the game on its header line, not only in the URL".
    [Fact]
    public void ANonDefaultModLookupNamesTheGameOnItsHeaderLine() =>
        Assert.Contains("[id 3479 on Baldur's Gate 3]", Render.Mod(Detail, Bg3));

    // Probe: "the default game still renders a skyrimspecialedition page URL".
    [Fact]
    public void TheDefaultGameStillRendersASkyrimSpecialEditionPageUrl() =>
        Assert.Contains("https://www.nexusmods.com/skyrimspecialedition/mods/3479", Render.Mod(Detail, NexusClient.SkyrimSe));

    // Probe: "the default mod lookup's header line reads exactly as before (no game named)".
    [Fact]
    public void TheDefaultModLookupHeaderNamesNoGame() =>
        Assert.Contains("[id 3479]", Render.Mod(Detail, NexusClient.SkyrimSe));

    // Probe: "a search hit renders the searched game's page URL".
    [Fact]
    public void ASearchHitRendersTheSearchedGamesPageUrl() =>
        Assert.Contains("https://www.nexusmods.com/baldursgate3/mods/3479",
            Render.Search("party", null, "endorsements", OneHit, Bg3));

    // Probe: "a non-default search names the game it searched, by name rather than by URL slug".
    [Fact]
    public void ANonDefaultSearchNamesTheGameByName() =>
        Assert.Contains("on Baldur's Gate 3", Render.Search("party", null, "endorsements", OneHit, Bg3));

    // Probe: "a default search reads exactly as before (no game named)".
    [Fact]
    public void ADefaultSearchNamesNoGame() =>
        Assert.DoesNotContain("on skyrimspecialedition",
            Render.Search("party", null, "endorsements", OneHit, NexusClient.SkyrimSe));

    // Probe: "a zero-hit search with a category names the category and the game, never a bare 0 match(es)".
    [Fact]
    public void AZeroHitSearchWithACategoryNamesTheCategoryAndTheGame() =>
        Assert.Contains("'Armour' may not be a category on Baldur's Gate 3",
            Render.Search("armor", "Armour", "endorsements", NoHits, Bg3));

    // Probe: "the default game's zero-hit note reads exactly as before".
    [Fact]
    public void TheDefaultGamesZeroHitNoteReadsAsBefore()
    {
        var text = Render.Search("armor", "Armour", "endorsements", NoHits, NexusClient.SkyrimSe);
        Assert.DoesNotContain("may not be a category on", text);
        Assert.Contains("category matching is EXACT", text);
    }

    // Probe: "a zero-hit search with no category says nothing about categories".
    [Fact]
    public void AZeroHitSearchWithNoCategorySaysNothingAboutCategories() =>
        Assert.DoesNotContain("may not be a category", Render.Search("armor", null, "endorsements", NoHits, Bg3));

    static NexusUpdateStatus NotFound() => NexusClient.ComputeStatus(999, false, null, null, null, Array.Empty<int>(),
        new List<(int, string, string?, string, long)>());

    // Probe: "a not-found row names the game checked, not Skyrim SE".
    [Fact]
    public void ANotFoundRowNamesTheGameChecked() =>
        Assert.Contains("not found on Baldur's Gate 3 (wrong id, another game's mod, or a hidden/deleted page)",
            Render.Updates(new[] { NotFound() }, Bg3, Array.Empty<string>()));

    // Probe: "the default game keeps its own not-found label, LE hint included".
    [Fact]
    public void TheDefaultGameKeepsItsOwnNotFoundLabel() =>
        Assert.Contains("not found on Skyrim SE (wrong id, an LE/other-game mod, or a hidden/deleted page)",
            Render.Updates(new[] { NotFound() }, NexusClient.SkyrimSe, Array.Empty<string>()));
}

using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The offline half of the Nexus tools' <c>game=</c> parameter (#835): <c>NexusClient.KnownGame</c>, the
/// domain and id mapping with its Skyrim SE default and no default for an unknown value, and
/// <c>NexusTools.ParseModRef</c>, the mod reference grammar. Migrated from the <c>nexus-game-guard</c> probe; each
/// test carries the probe arm's wording. No network, no key, no MO2 instance.</summary>
[Trait("tier", "unit")]
public sealed class NexusGameMappingTests
{
    // Probe: "no game= → Skyrim SE (1704)".
    [Fact]
    public void NoGameIsSkyrimSe()
    {
        var g = NexusClient.KnownGame(null);
        Assert.Equal(1704, g?.Id);
        Assert.Equal("skyrimspecialedition", g?.Domain);
    }

    // Probe: "blank game= → Skyrim SE (1704)".
    [Fact]
    public void ABlankGameIsSkyrimSe() => Assert.Equal(1704, NexusClient.KnownGame("  ")?.Id);

    // Probe: "domain 'skyrimspecialedition' → 1704", "domain 'baldursgate3' → 3474", "domain 'cyberpunk2077' → 3333",
    // "domain 'starfield' → 4187".
    [Theory]
    [InlineData("skyrimspecialedition", 1704)]
    [InlineData("baldursgate3", 3474)]
    [InlineData("cyberpunk2077", 3333)]
    [InlineData("starfield", 4187)]
    public void EachKnownDomainMapsToItsIdWithNoNetworkCall(string domain, int id) =>
        Assert.Equal(id, NexusClient.KnownGame(domain)?.Id);

    // Probe: "domain match ignores case and surrounding space".
    [Fact]
    public void ADomainMatchIgnoresCaseAndSurroundingSpace() =>
        Assert.Equal(3474, NexusClient.KnownGame(" BaldursGate3 ")?.Id);

    // Probe: "id '3474' → baldursgate3", "id '3333' → cyberpunk2077", "id '4187' → starfield",
    // "id '1704' → skyrimspecialedition".
    [Theory]
    [InlineData("3474", "baldursgate3")]
    [InlineData("3333", "cyberpunk2077")]
    [InlineData("4187", "starfield")]
    [InlineData("1704", "skyrimspecialedition")]
    public void EachKnownIdMapsToTheDomainThePageUrlNeeds(string id, string domain)
    {
        var g = NexusClient.KnownGame(id);
        Assert.Equal(domain, g?.Domain);
        Assert.Equal(int.Parse(id), g?.Id);
    }

    // Probe: "an unmapped domain → null (resolved through the graph, not defaulted)", "an unmapped id → null (resolved
    // through the graph, not defaulted)", "junk → null, never Skyrim SE".
    [Theory]
    [InlineData("morrowind")]
    [InlineData("9999")]
    [InlineData("not a game")]
    public void AnUnmappedValueMapsToNothingRatherThanSkyrimSe(string game) => Assert.Null(NexusClient.KnownGame(game));

    // Probe: "bare id → id, no game (game= decides)".
    [Fact]
    public void ABareIdCarriesNoGame() => Assert.Equal((12604, (string?)null, (string?)null), NexusTools.ParseModRef("12604"));

    // Probe: "bare id tolerates surrounding space"; the non-breaking-space row needs the Trim, which int.TryParse lacks.
    [Theory]
    [InlineData(" 12604 ")]
    [InlineData(" 12604 ")]
    public void ABareIdToleratesSurroundingSpace(string mod) =>
        Assert.Equal((12604, (string?)null, (string?)null), NexusTools.ParseModRef(mod));

    // Probe: "SSE mod URL → id + its domain".
    [Fact]
    public void AnSseModUrlCarriesItsIdAndDomain() =>
        Assert.Equal((12604, "skyrimspecialedition", (string?)null),
            NexusTools.ParseModRef("https://www.nexusmods.com/skyrimspecialedition/mods/12604"));

    // Probe: "BG3 mod URL → id + baldursgate3 (another game's URL is no longer refused)".
    [Fact]
    public void ABg3ModUrlCarriesBaldursgate3() =>
        Assert.Equal((3479, "baldursgate3", (string?)null),
            NexusTools.ParseModRef("https://www.nexusmods.com/baldursgate3/mods/3479"));

    // Probe: "'/games/<domain>/mods/N' URL → id + its domain".
    [Fact]
    public void AGamesPrefixedUrlCarriesItsDomain() =>
        Assert.Equal((1234, "starfield", (string?)null),
            NexusTools.ParseModRef("https://www.nexusmods.com/games/starfield/mods/1234"));

    // Probe: "a URL's domain is lower-cased and a query string doesn't break the parse".
    [Fact]
    public void AUrlDomainIsLowerCasedAndAQueryStringIsIgnored() =>
        Assert.Equal((107, "cyberpunk2077", (string?)null),
            NexusTools.ParseModRef("https://www.nexusmods.com/CyberPunk2077/mods/107?tab=files"));

    // Probe: "loose '/mods/N' paste → id, no game".
    [Fact]
    public void ALooseModsPasteCarriesNoGame() =>
        Assert.Equal((456, (string?)null, (string?)null), NexusTools.ParseModRef("/mods/456"));

    // Probe: "unreadable reference → an error, not a guessed id".
    [Fact]
    public void AnUnreadableReferenceIsAnErrorNotAGuessedId()
    {
        var (modId, domain, error) = NexusTools.ParseModRef("Skyrim Script Extender");
        Assert.Equal(0, modId);
        Assert.Null(domain);
        Assert.NotNull(error);
    }
}

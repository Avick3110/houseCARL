using System.Net;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>What the Nexus client sends for a <c>game=</c> (#835): the requested game's id on the wire rather than the
/// Skyrim SE constant, the URL-against-<c>game=</c> mismatch refusal that sends nothing, and the resolve of an unmapped
/// game with its refusals kept apart (<c>GAME_NOT_FOUND</c> against an HTTP 404, an HTTP 500, another GraphQL error,
/// an unreachable endpoint). Migrated from the <c>nexus-game-guard</c> probe; each test carries the probe arm's
/// wording. A stub transport answers every request, so nothing reaches the network.</summary>
[Trait("tier", "unit")]
public sealed class NexusGameWireTests
{
    static readonly NexusGame Bg3 = NexusClient.KnownGame("baldursgate3")!;

    const string SseUrl = "https://www.nexusmods.com/skyrimspecialedition/mods/12604";

    const string Bg3Url = "https://www.nexusmods.com/baldursgate3/mods/3479";

    static (NexusClient client, StubHandler stub) Empty()
    {
        var stub = new StubHandler(_ => """{"data":{}}""");
        return (new NexusClient(new HttpClient(stub)), stub);
    }

    /// <summary>A domain no other test asks for: the resolve cache is process-wide, so a shared one would make the
    /// asked-once arm depend on test order.</summary>
    static string OwnDomain() => "nexusgametest" + Guid.NewGuid().ToString("N");

    static void SentOnlyThisGame(StubHandler stub, string id, string notId)
    {
        var body = Assert.Single(stub.Bodies);
        Assert.Contains(id, body);
        Assert.DoesNotContain(notId, body);
    }

    // Probe: "search sends the requested game's id (3474), not 1704".
    [Fact]
    public async Task SearchSendsTheRequestedGamesId()
    {
        var (client, stub) = Empty();
        await client.SearchAsync("party", null, "endorsements", 5, Bg3, default);
        SentOnlyThisGame(stub, "3474", "1704");
    }

    // Probe: "mod lookup sends the requested game's id (3474), not 1704".
    [Fact]
    public async Task ModLookupSendsTheRequestedGamesId()
    {
        var (client, stub) = Empty();
        await client.GetModAsync(3479, Bg3, default);
        SentOnlyThisGame(stub, "3474", "1704");
    }

    // Probe: "the update check sends the requested game's id (3474) in both its filter and its file aliases".
    [Fact]
    public async Task TheUpdateCheckSendsTheRequestedGamesId()
    {
        var (client, stub) = Empty();
        await client.CheckUpdatesAsync(new[] { (3479, (string?)null, (IReadOnlyList<int>)new[] { 11 }) }, Bg3, default);
        SentOnlyThisGame(stub, "3474", "1704");
    }

    // Probe: "the default game still sends 1704".
    [Fact]
    public async Task TheDefaultGameStillSends1704()
    {
        var (client, stub) = Empty();
        await client.SearchAsync("party", null, "endorsements", 5, NexusClient.SkyrimSe, default);
        Assert.Contains("1704", Assert.Single(stub.Bodies));
    }

    // Probe: "an SSE URL with game=baldursgate3 is refused, naming both games" and "the refused mismatch looks nothing
    // up — no request goes out".
    [Fact]
    public async Task AUrlAgainstADifferentGameIsRefusedNamingBothAndSendsNothing()
    {
        var (client, stub) = Empty();
        var text = await NexusTools.NexusMod(client, SseUrl, game: "baldursgate3");
        Assert.Contains("two different games", text);
        Assert.Contains("'skyrimspecialedition'", text);
        Assert.Contains("'baldursgate3'", text);
        Assert.Empty(stub.Bodies);
    }

    // Probe: "a game= naming the URL's own domain agrees and the mod is looked up on it".
    [Fact]
    public async Task AGameNamingTheUrlsOwnDomainAgrees()
    {
        var (client, stub) = Empty();
        var text = await NexusTools.NexusMod(client, SseUrl, game: "skyrimspecialedition");
        Assert.DoesNotContain("two different games", text);
        Assert.Contains("1704", Assert.Single(stub.Bodies));
    }

    // Probe: "game=1704 against a skyrimspecialedition URL agrees by id, not by spelling".
    [Fact]
    public async Task AGameIdAgreesWithTheUrlByIdNotBySpelling()
    {
        var (client, stub) = Empty();
        var text = await NexusTools.NexusMod(client, SseUrl, game: "1704");
        Assert.DoesNotContain("two different games", text);
        Assert.Contains("1704", Assert.Single(stub.Bodies));
    }

    // Probe: "a BG3 URL with game=3474 agrees and is looked up on BG3".
    [Fact]
    public async Task ABg3UrlWithItsIdAgreesAndIsLookedUpOnBg3()
    {
        var (client, stub) = Empty();
        var text = await NexusTools.NexusMod(client, Bg3Url, game: "3474");
        Assert.DoesNotContain("two different games", text);
        Assert.Contains("3474", Assert.Single(stub.Bodies));
    }

    // Probe: "a BG3 URL with no game= is looked up on BG3".
    [Fact]
    public async Task ABg3UrlWithNoGameIsLookedUpOnBg3()
    {
        var (client, stub) = Empty();
        await NexusTools.NexusMod(client, Bg3Url);
        SentOnlyThisGame(stub, "3474", "1704");
    }

    static StubHandler ResolvesTo(string domain, string nameJson) =>
        new(_ => "{\"data\":{\"game\":{\"id\":100,\"domainName\":\"" + domain + "\",\"name\":" + nameJson + "}}}");

    // Probe: "an unmapped domain resolves through the graph to its id and domain" and "resolving by domain asks
    // game(domainName:) once".
    [Fact]
    public async Task AnUnmappedDomainResolvesThroughTheGraphOnce()
    {
        var domain = OwnDomain();
        var stub = ResolvesTo(domain, "\"Test Only\"");
        var (ok, error, game) = await new NexusClient(new HttpClient(stub)).ResolveGameAsync(domain, default);
        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(100, game?.Id);
        Assert.Equal(domain, game?.Domain);
        Assert.Contains("game(domainName:", Assert.Single(stub.Bodies));
    }

    // Probe: "a resolved game is named by the graph's name, not its domain".
    [Fact]
    public async Task AResolvedGameIsNamedByTheGraphsName()
    {
        var domain = OwnDomain();
        var (_, _, game) = await new NexusClient(new HttpClient(ResolvesTo(domain, "\"Test Only\""))).ResolveGameAsync(domain, default);
        Assert.Equal("Test Only", game?.Display);
    }

    // Probe: "a game the graph gave no name for reads as its domain".
    [Fact]
    public void AGameWithNoNameReadsAsItsDomain() => Assert.Equal("somedomain", new NexusGame(100, "somedomain").Display);

    // Probe: "a domain already resolved is not asked again".
    [Fact]
    public async Task ADomainAlreadyResolvedIsNotAskedAgain()
    {
        var domain = OwnDomain();
        var stub = ResolvesTo(domain, "\"Test Only\"");
        var client = new NexusClient(new HttpClient(stub));
        await client.ResolveGameAsync(domain, default);
        stub.Bodies.Clear();
        var (ok, _, game) = await client.ResolveGameAsync(domain, default);
        Assert.True(ok);
        Assert.Equal(100, game?.Id);
        Assert.Empty(stub.Bodies);
    }

    static async Task<(bool ok, string? error, NexusGame? game)> Resolve(StubHandler stub, string asked) =>
        await new NexusClient(new HttpClient(stub)).ResolveGameAsync(asked, default);

    // Probe: "a game Nexus does not know is refused, naming what was asked".
    [Fact]
    public async Task AGameNexusDoesNotKnowIsRefusedNamingWhatWasAsked()
    {
        var asked = OwnDomain();
        var stub = new StubHandler(_ =>
            """{"errors":[{"message":"Game not found. Could not find Game x","extensions":{"code":"GAME_NOT_FOUND"}}],"data":{"game":null}}""");
        var (ok, error, game) = await Resolve(stub, asked);
        Assert.False(ok);
        Assert.Null(game);
        Assert.Contains(asked, error);
    }

    // Probe: "an unreachable Nexus is reported as itself, not as an unknown game".
    [Fact]
    public async Task AnUnreachableNexusIsReportedAsItself()
    {
        var (ok, error, _) = await Resolve(new StubHandler(_ => throw new HttpRequestException("no route to host")), OwnDomain());
        Assert.False(ok);
        Assert.Contains("couldn't reach Nexus Mods", error);
    }

    // Probe: "an HTTP 404 is reported as a failed request, not as an unknown game".
    [Fact]
    public async Task AnHttp404IsAFailedRequestNotAnUnknownGame()
    {
        var (ok, error, _) = await Resolve(new StubHandler(_ => "") { Status = HttpStatusCode.NotFound }, OwnDomain());
        Assert.False(ok);
        Assert.Contains("HTTP 404", error);
        Assert.DoesNotContain("has no game", error);
    }

    // Probe: "an HTTP 500 is reported as itself, not as an unknown game".
    [Fact]
    public async Task AnHttp500IsNotAnUnknownGame()
    {
        var (ok, error, _) = await Resolve(new StubHandler(_ => "") { Status = HttpStatusCode.InternalServerError }, OwnDomain());
        Assert.False(ok);
        Assert.NotNull(error);
        Assert.DoesNotContain("has no game", error);
    }

    // Probe: "a GraphQL error that is not GAME_NOT_FOUND is passed through as itself".
    [Fact]
    public async Task AnotherGraphqlErrorIsPassedThroughAsItself()
    {
        var stub = new StubHandler(_ =>
            """{"errors":[{"message":"Something else went wrong","extensions":{"code":"INTERNAL_ERROR"}}]}""");
        var (ok, error, _) = await Resolve(stub, OwnDomain());
        Assert.False(ok);
        Assert.Contains("Something else went wrong", error);
        Assert.DoesNotContain("has no game", error);
    }

    /// <summary>A stub transport: it answers every request from a function of the request body and records what was sent.</summary>
    sealed class StubHandler : HttpMessageHandler
    {
        readonly Func<string, string> _reply;
        public StubHandler(Func<string, string> reply) => _reply = reply;
        public List<string> Bodies { get; } = new();

        /// <summary>The status it answers with; 200 unless a test is about an HTTP failure.</summary>
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Bodies.Add(body);
            return new HttpResponseMessage(Status) { Content = new StringContent(_reply(body)) };
        }
    }
}

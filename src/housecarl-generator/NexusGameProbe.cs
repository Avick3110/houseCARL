using System.Net;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// nexus-game-guard — locks the pure, offline pieces of the Nexus tools' `game=` parameter (#835):
///   • NexusClient.KnownGame — which games map to an id WITHOUT a network call (the four in #835), that a numeric id
///     maps as well as a domain name, that no game= given means Skyrim SE, and that an unknown domain maps to NOTHING
///     here so the caller has to resolve it through the graph rather than silently getting Skyrim.
///   • NexusTools.ParseModRef — the mod reference grammar: a bare id carries no game, a mod URL carries its domain
///     segment (any game's, not just skyrimspecialedition), a '/games/&lt;domain&gt;/mods/N' URL reads the same, a loose
///     '/mods/N' paste carries no game, and junk is an error.
///   • Render — a rendered mod page URL carries the game that was asked for, and a rendered update check names it.
/// Network-free: the live resolve of an unknown domain is exercised by hand, not CI. No game data, no MO2 instance.
/// </summary>
internal static class NexusGameProbe
{
    [CiProbe("nexus-game-guard")]
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" nexus-game guard — game= domain/id mapping + mod URL parse + rendered game");
        Console.WriteLine("================================================================");
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        // DEFAULT — no game= is Skyrim SE, so an existing caller's call is unchanged.
        Check(NexusClient.KnownGame(null) is { Id: 1704, Domain: "skyrimspecialedition" }, "no game= → Skyrim SE (1704)");
        Check(NexusClient.KnownGame("  ") is { Id: 1704 }, "blank game= → Skyrim SE (1704)");

        // DOMAIN NAMES — the four #835 games map with no network call.
        Check(NexusClient.KnownGame("skyrimspecialedition") is { Id: 1704 }, "domain 'skyrimspecialedition' → 1704");
        Check(NexusClient.KnownGame("baldursgate3") is { Id: 3474 }, "domain 'baldursgate3' → 3474");
        Check(NexusClient.KnownGame("cyberpunk2077") is { Id: 3333 }, "domain 'cyberpunk2077' → 3333");
        Check(NexusClient.KnownGame("starfield") is { Id: 4187 }, "domain 'starfield' → 4187");
        Check(NexusClient.KnownGame(" BaldursGate3 ") is { Id: 3474 }, "domain match ignores case and surrounding space");

        // NUMERIC IDS — the same games map from the id form, and carry the domain the page URL needs.
        Check(NexusClient.KnownGame("3474") is { Id: 3474, Domain: "baldursgate3" }, "id '3474' → baldursgate3");
        Check(NexusClient.KnownGame("3333") is { Id: 3333, Domain: "cyberpunk2077" }, "id '3333' → cyberpunk2077");
        Check(NexusClient.KnownGame("4187") is { Id: 4187, Domain: "starfield" }, "id '4187' → starfield");
        Check(NexusClient.KnownGame("1704") is { Domain: "skyrimspecialedition" }, "id '1704' → skyrimspecialedition");

        // UNKNOWN — not mapped here, so the caller must resolve it through the graph; never quietly Skyrim SE.
        Check(NexusClient.KnownGame("morrowind") is null, "an unmapped domain → null (resolved through the graph, not defaulted)");
        Check(NexusClient.KnownGame("9999") is null, "an unmapped id → null (resolved through the graph, not defaulted)");
        Check(NexusClient.KnownGame("not a game") is null, "junk → null, never Skyrim SE");

        // MOD REFERENCE PARSE — a bare id leaves the game to game=; a URL names it itself.
        Check(NexusTools.ParseModRef("12604") is (12604, null, null), "bare id → id, no game (game= decides)");
        Check(NexusTools.ParseModRef(" 12604 ") is (12604, null, null), "bare id tolerates surrounding space");
        Check(NexusTools.ParseModRef("https://www.nexusmods.com/skyrimspecialedition/mods/12604")
              is (12604, "skyrimspecialedition", null), "SSE mod URL → id + its domain");
        Check(NexusTools.ParseModRef("https://www.nexusmods.com/baldursgate3/mods/3479")
              is (3479, "baldursgate3", null), "BG3 mod URL → id + baldursgate3 (another game's URL is no longer refused)");
        Check(NexusTools.ParseModRef("https://www.nexusmods.com/games/starfield/mods/1234")
              is (1234, "starfield", null), "'/games/<domain>/mods/N' URL → id + its domain");
        Check(NexusTools.ParseModRef("https://www.nexusmods.com/CyberPunk2077/mods/107?tab=files")
              is (107, "cyberpunk2077", null), "a URL's domain is lower-cased and a query string doesn't break the parse");
        Check(NexusTools.ParseModRef("/mods/456") is (456, null, null), "loose '/mods/N' paste → id, no game");
        var junk = NexusTools.ParseModRef("Skyrim Script Extender");
        Check(junk.modId == 0 && junk.domain is null && junk.error is not null, "unreadable reference → an error, not a guessed id");

        // RENDERED OUTPUT — the page URL carries the game that was asked for, not a hardcoded Skyrim SE one.
        var detail = new NexusModDetail(3479, "Aether's No Party Limits", "1.0", null, null, "Aether", "Gameplay",
            10, 100, null, null, false, "published", true,
            Array.Empty<NexusRequirement>(), Array.Empty<NexusFile>(), Array.Empty<string>());
        var bg3 = NexusClient.KnownGame("baldursgate3")!;
        var bg3Text = Render.Mod(detail, bg3);
        Check(bg3Text.Contains("https://www.nexusmods.com/baldursgate3/mods/3479", StringComparison.Ordinal),
              "a BG3 mod renders a baldursgate3 page URL");
        Check(!bg3Text.Contains("skyrimspecialedition", StringComparison.Ordinal),
              "a BG3 mod renders no skyrimspecialedition URL");
        Check(bg3Text.Contains("[id 3479 on Baldur's Gate 3]", StringComparison.Ordinal),
              "a non-default mod lookup names the game on its header line, not only in the URL");
        var sseText = Render.Mod(detail, NexusClient.SkyrimSe);
        Check(sseText.Contains("https://www.nexusmods.com/skyrimspecialedition/mods/3479", StringComparison.Ordinal),
              "the default game still renders a skyrimspecialedition page URL");
        Check(sseText.Contains("[id 3479]", StringComparison.Ordinal),
              "the default mod lookup's header line reads exactly as before (no game named)");

        var hit = new NexusSearchHit(3479, "Aether's No Party Limits", "1.0", "Aether", 10, 100, null, false, null, "Gameplay");
        var search = Render.Search("party", null, "endorsements", new NexusSearchResult(1, new[] { hit }), bg3);
        Check(search.Contains("https://www.nexusmods.com/baldursgate3/mods/3479", StringComparison.Ordinal),
              "a search hit renders the searched game's page URL");
        Check(search.Contains("on Baldur's Gate 3", StringComparison.Ordinal),
              "a non-default search names the game it searched, by name rather than by URL slug");
        Check(!Render.Search("party", null, "endorsements", new NexusSearchResult(1, new[] { hit }), NexusClient.SkyrimSe)
                     .Contains("on skyrimspecialedition", StringComparison.Ordinal),
              "a default search reads exactly as before (no game named)");

        // A CATEGORY THAT GAME DOES NOT USE — Nexus matches category names exactly and they differ per game, so a zero
        // on a non-default game says the category may not exist there rather than letting it read as "no such mods".
        var emptyWithCategory = Render.Search("armor", "Armour", "endorsements",
            new NexusSearchResult(0, Array.Empty<NexusSearchHit>()), bg3);
        Check(emptyWithCategory.Contains("'Armour' may not be a category on Baldur's Gate 3", StringComparison.Ordinal),
              "a zero-hit search with a category names the category and the game, never a bare 0 match(es)");
        var emptyDefault = Render.Search("armor", "Armour", "endorsements",
            new NexusSearchResult(0, Array.Empty<NexusSearchHit>()), NexusClient.SkyrimSe);
        Check(!emptyDefault.Contains("may not be a category on", StringComparison.Ordinal)
              && emptyDefault.Contains("category matching is EXACT", StringComparison.Ordinal),
              "the default game's zero-hit note reads exactly as before");
        Check(!Render.Search("armor", null, "endorsements", new NexusSearchResult(0, Array.Empty<NexusSearchHit>()), bg3)
                     .Contains("may not be a category", StringComparison.Ordinal),
              "a zero-hit search with no category says nothing about categories");

        // A not-found row names the game that was checked, so it never claims Skyrim SE for a BG3 check.
        var notFound = NexusClient.ComputeStatus(999, false, null, null, null, Array.Empty<int>(),
            new List<(int, string, string?, string, long)>());
        var updates = Render.Updates(new[] { notFound }, bg3, Array.Empty<string>());
        Check(updates.Contains("not found on Baldur's Gate 3 (wrong id, another game's mod, or a hidden/deleted page)",
                               StringComparison.Ordinal),
              "a not-found row names the game checked, not Skyrim SE");
        // The default game's label is the sentence main shipped, LE hint and all: an existing caller reads no change.
        var sseUpdates = Render.Updates(new[] { notFound }, NexusClient.SkyrimSe, Array.Empty<string>());
        Check(sseUpdates.Contains("not found on Skyrim SE (wrong id, an LE/other-game mod, or a hidden/deleted page)",
                                  StringComparison.Ordinal),
              "the default game keeps its own not-found label, LE hint included");

        // WHAT GOES ON THE WIRE — the requested game's id, not the Skyrim SE constant. A stub handler answers every
        // request, so this reaches the query text without a network.
        var stub = new StubHandler(_ => "{\"data\":{}}");
        var client = new NexusClient(new HttpClient(stub));

        client.SearchAsync("party", null, "endorsements", 5, bg3, default).GetAwaiter().GetResult();
        Check(stub.Bodies.Count == 1 && stub.Bodies[0].Contains("3474", StringComparison.Ordinal)
              && !stub.Bodies[0].Contains("1704", StringComparison.Ordinal),
              "search sends the requested game's id (3474), not 1704");

        stub.Bodies.Clear();
        client.GetModAsync(3479, bg3, default).GetAwaiter().GetResult();
        Check(stub.Bodies.Count == 1 && stub.Bodies[0].Contains("3474", StringComparison.Ordinal)
              && !stub.Bodies[0].Contains("1704", StringComparison.Ordinal),
              "mod lookup sends the requested game's id (3474), not 1704");

        stub.Bodies.Clear();
        client.CheckUpdatesAsync(new[] { (3479, (string?)null, (IReadOnlyList<int>)new[] { 11 }) }, bg3, default)
              .GetAwaiter().GetResult();
        Check(stub.Bodies.Count == 1 && stub.Bodies[0].Contains("3474", StringComparison.Ordinal)
              && !stub.Bodies[0].Contains("1704", StringComparison.Ordinal),
              "the update check sends the requested game's id (3474) in both its filter and its file aliases");

        stub.Bodies.Clear();
        client.SearchAsync("party", null, "endorsements", 5, NexusClient.SkyrimSe, default).GetAwaiter().GetResult();
        Check(stub.Bodies.Count == 1 && stub.Bodies[0].Contains("1704", StringComparison.Ordinal),
              "the default game still sends 1704");

        // RESOLVING AN UNMAPPED GAME — one graph call, and what Nexus does not know is refused naming what was asked.
        // The domain is this probe's own: the resolve cache is process-wide, so a domain another probe could ask for
        // would make the "asked once" arm depend on which probe ran first.
        const string Unmapped = "nexusgameprobeonly";
        var okStub = new StubHandler(_ =>
            "{\"data\":{\"game\":{\"id\":100,\"domainName\":\"" + Unmapped + "\",\"name\":\"Probe Only\"}}}");
        var okClient = new NexusClient(new HttpClient(okStub));
        var (rok, rerror, rgame) = okClient.ResolveGameAsync(Unmapped, default).GetAwaiter().GetResult();
        Check(rok && rgame is { Id: 100, Domain: Unmapped } && rerror is null,
              "an unmapped domain resolves through the graph to its id and domain");
        Check(rgame!.Display == "Probe Only", "a resolved game is named by the graph's name, not its domain");
        Check(new NexusGame(100, Unmapped).Display == Unmapped, "a game the graph gave no name for reads as its domain");
        Check(okStub.Bodies.Count == 1 && okStub.Bodies[0].Contains("domainName", StringComparison.Ordinal),
              "resolving by domain asks game(domainName:) once");
        okStub.Bodies.Clear();
        okClient.ResolveGameAsync(Unmapped, default).GetAwaiter().GetResult();
        Check(okStub.Bodies.Count == 0, "a domain already resolved is not asked again");

        var missStub = new StubHandler(_ =>
            "{\"errors\":[{\"message\":\"Game not found. Could not find Game nosuchgame\","
            + "\"extensions\":{\"code\":\"GAME_NOT_FOUND\"}}],\"data\":{\"game\":null}}");
        var (mok, merror, mgame) = new NexusClient(new HttpClient(missStub))
            .ResolveGameAsync("nosuchgame", default).GetAwaiter().GetResult();
        Check(!mok && mgame is null && merror is not null && merror.Contains("nosuchgame", StringComparison.Ordinal),
              "a game Nexus does not know is refused, naming what was asked");

        var downStub = new StubHandler(_ => throw new HttpRequestException("no route to host"));
        var (dok, derror, _) = new NexusClient(new HttpClient(downStub))
            .ResolveGameAsync("nosuchgame", default).GetAwaiter().GetResult();
        Check(!dok && derror is not null && derror.Contains("couldn't reach Nexus Mods", StringComparison.Ordinal),
              "an unreachable Nexus is reported as itself, not as an unknown game");

        // An HTTP 404's reason phrase is literally "Not Found": a failed REQUEST, never a missing game, so the refusal
        // reads the GraphQL code rather than the message text.
        var notFoundStub = new StubHandler(_ => "") { Status = HttpStatusCode.NotFound };
        var (hok, herror, _) = new NexusClient(new HttpClient(notFoundStub))
            .ResolveGameAsync("nosuchgame404", default).GetAwaiter().GetResult();
        Check(!hok && herror is not null && herror.Contains("HTTP 404", StringComparison.Ordinal)
              && !herror.Contains("has no game", StringComparison.Ordinal),
              "an HTTP 404 is reported as a failed request, not as an unknown game");

        var serverErrStub = new StubHandler(_ => "") { Status = HttpStatusCode.InternalServerError };
        var (sok, serror, _) = new NexusClient(new HttpClient(serverErrStub))
            .ResolveGameAsync("nosuchgame500", default).GetAwaiter().GetResult();
        Check(!sok && serror is not null && !serror.Contains("has no game", StringComparison.Ordinal),
              "an HTTP 500 is reported as itself, not as an unknown game");

        var otherGraphStub = new StubHandler(_ =>
            "{\"errors\":[{\"message\":\"Something else went wrong\",\"extensions\":{\"code\":\"INTERNAL_ERROR\"}}]}");
        var (ook, oerror, _) = new NexusClient(new HttpClient(otherGraphStub))
            .ResolveGameAsync("nosuchgameother", default).GetAwaiter().GetResult();
        Check(!ook && oerror is not null && oerror.Contains("Something else went wrong", StringComparison.Ordinal)
              && !oerror.Contains("has no game", StringComparison.Ordinal),
              "a GraphQL error that is not GAME_NOT_FOUND is passed through as itself");

        Console.WriteLine(fail == 0
            ? "[nexus-game] PASS - game= mapping, mod URL parse, wire game id and rendered game hold."
            : $"[nexus-game] FAIL ({fail})");
        return fail;
    }

    /// <summary>A stub transport: it answers every request from a function of the request body and records what was sent.</summary>
    sealed class StubHandler : HttpMessageHandler
    {
        readonly Func<string, string> _reply;
        public StubHandler(Func<string, string> reply) => _reply = reply;
        public List<string> Bodies { get; } = new();

        /// <summary>The status it answers with; 200 unless an arm is about an HTTP failure.</summary>
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Bodies.Add(body);
            return new HttpResponseMessage(Status) { Content = new StringContent(_reply(body)) };
        }
    }
}

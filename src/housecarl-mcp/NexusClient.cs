using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace HousecarlMcp;

/// <summary>
/// houseCARL's read-only bridge to the Nexus Mods public v2 GraphQL API (the QOL "smooth out Nexus" layer). It exists
/// so houseCARL can answer Nexus questions — search the catalog, look up a mod's version/requirements/newest MAIN file
/// — DIRECTLY instead of driving a browser to scrape a rendered page. It is deliberately:
///   • READ-ONLY — search + mod lookup; it never downloads, installs, endorses, or mutates anything. A download stays
///     the user's mod manager's job (the nxm "Mod Manager Download" handoff), exactly as before.
///   • KEYLESS — the v2 GraphQL read surface (search/mod/modFiles/requirements) is public and anonymous, so there is no
///     API key to configure (and a personal key in a public app is AUP-"unacceptable" anyway). One fewer onboarding step.
///   • OFFLINE-SAFE (Q3) — every failure mode (no connection, timeout, HTTP error, rate-limit, malformed body, GraphQL
///     error) is RETURNED as a plain message, never thrown. houseCARL's local (load-order) tools never depend on this and
///     keep working with no internet.
/// It lives in housecarl-mcp, NOT housecarl-core: core is the proven, deterministic, OFFLINE Mutagen engine and stays
/// network-free. This is the server's first and only outbound network dependency, isolated here.
/// Registered as a typed HttpClient (Program.cs, services.AddHttpClient&lt;NexusClient&gt;) so its lifetime/timeout are managed.
/// </summary>
public sealed class NexusClient
{
    /// <summary>Skyrim Special Edition's Nexus game id (domainName 'skyrimspecialedition'). houseCARL is SSE-only, so every
    /// query is scoped to this — the user never types a game id.</summary>
    public const int SkyrimSeGameId = 1704;

    const string Endpoint = "https://api.nexusmods.com/v2/graphql";

    readonly HttpClient _http;

    // PropertyNamingPolicy=null: GraphQL field/variable names are case-sensitive (gameId, modId, categoryName,
    // direction). We author them exactly and must NOT let a camelCase policy rewrite them.
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = null };

    public NexusClient(HttpClient http) => _http = http;

    // ──────────────────────────────────────────────────────────────────────────────────────────────────────────
    //  Public API — both return (ok, error, payload). ok==false ⇒ error is a user-facing message and payload is null.
    // ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Search Skyrim SE mods by a wildcard name term, sorted DESC by <paramref name="sortField"/> (a ModsSort
    /// field name), optionally narrowed to a category name, capped at <paramref name="count"/>.</summary>
    public async Task<(bool ok, string? error, NexusSearchResult? result)> SearchAsync(
        string term, string? category, string sortField, int count, CancellationToken ct)
    {
        var filter = new Dictionary<string, object>
        {
            ["gameId"] = new[] { new { value = SkyrimSeGameId.ToString(), op = "EQUALS" } },
            ["name"] = new[] { new { value = term, op = "WILDCARD" } },
        };
        if (!string.IsNullOrWhiteSpace(category))
            filter["categoryName"] = new[] { new { value = category!, op = "EQUALS" } };

        // sort is [{ <field>: { direction } }] — a single-key object, so build it from a dictionary. Name sorts
        // ASCending (A-Z, what "by name" means); every other field DESCending (most endorsements/downloads/recent first).
        var direction = sortField == "name" ? "ASC" : "DESC";
        var sort = new[] { new Dictionary<string, object> { [sortField] = new { direction } } };

        var (ok, error, data) = await PostAsync(SearchQuery, new { filter, sort, count }, ct);
        if (!ok) return (false, error, null);

        // Guard the root navigation: a 200 with an unexpected shape must STILL return cleanly, not throw (the class
        // contract is "every failure mode is returned, never thrown"). GetProperty would throw on a missing field.
        if (!data.TryGetProperty("mods", out var mods) || mods.ValueKind != JsonValueKind.Object
            || !mods.TryGetProperty("nodes", out var nodeList) || nodeList.ValueKind != JsonValueKind.Array)
            return (false, "Nexus Mods returned an unexpected response shape (no 'mods' results).", null);

        var hits = new List<NexusSearchHit>();
        foreach (var n in nodeList.EnumerateArray())
            hits.Add(new NexusSearchHit(
                Int(n, "modId"), Str(n, "name") ?? "", Str(n, "version"), Str(n, "author"),
                Int(n, "endorsements"), Int(n, "downloads"), Str(n, "updatedAt"),
                Bool(n, "adultContent"), Str(n, "summary"), Str(n, "category")));
        return (true, null, new NexusSearchResult(Int(mods, "totalCount"), hits));
    }

    /// <summary>Fetch one mod's full detail + its files, in a SINGLE request (mod + modFiles are separate root fields
    /// queried together). A non-existent modId comes back as a GraphQL error (mod is non-null in the schema), surfaced
    /// via ok==false.</summary>
    public async Task<(bool ok, string? error, NexusModDetail? mod)> GetModAsync(int modId, CancellationToken ct)
    {
        var (ok, error, data) = await PostAsync(
            ModQuery, new { modId = modId.ToString(), gameId = SkyrimSeGameId.ToString() }, ct);
        if (!ok) return (false, error, null);

        // Guard the root navigation (see SearchAsync) — a missing 'mod' on a 200 returns cleanly, never throws.
        if (!data.TryGetProperty("mod", out var m) || m.ValueKind != JsonValueKind.Object)
            return (false, "Nexus Mods returned an unexpected response shape (no 'mod').", null);

        var reqs = new List<NexusRequirement>();
        if (m.TryGetProperty("modRequirements", out var mr) && mr.ValueKind == JsonValueKind.Object
            && mr.TryGetProperty("nexusRequirements", out var nr) && nr.ValueKind == JsonValueKind.Object
            && nr.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in nodes.EnumerateArray())
                reqs.Add(new NexusRequirement(
                    Str(r, "modId") ?? "", Str(r, "modName") ?? "", Str(r, "url"),
                    Str(r, "notes"), Bool(r, "externalRequirement")));
        }

        var files = new List<NexusFile>();
        if (data.TryGetProperty("modFiles", out var mf) && mf.ValueKind == JsonValueKind.Array)
            foreach (var f in mf.EnumerateArray())
                files.Add(new NexusFile(
                    Int(f, "fileId"), Str(f, "name") ?? "", Str(f, "version"),
                    Str(f, "category") ?? "", Long(f, "date"), Str(f, "description"), StrList(f, "changelogText")));

        return (true, null, new NexusModDetail(
            Int(m, "modId"), Str(m, "name") ?? "", Str(m, "version"), Str(m, "summary"), Str(m, "description"),
            Str(m, "author"), Str(m, "category") ?? "", Int(m, "endorsements"), Int(m, "downloads"),
            Str(m, "updatedAt"), Str(m, "createdAt"), Bool(m, "adultContent"), Str(m, "status") ?? "",
            Bool(m, "directDownloadEnabled"), reqs, files));
    }

    /// <summary>Batch "is each of these mods behind?" — for a list of (modId, installedVersion) pairs, resolve each mod's
    /// name + newest MAIN file (the accurate version of record; a mod's version HEADER can lag its newest file) in as few
    /// requests as possible. One combined query PER CHUNK: an OR-batched <c>mods()</c> for names/headers + one aliased
    /// <c>modFiles()</c> per mod for the newest MAIN. modIds are integers (the tool parses them), so inlining them in the
    /// query text is injection-safe — no user STRING is concatenated in, and the installed version is compared LOCALLY,
    /// never sent to Nexus. A chunk that fails marks only its own mods Verdict=Error (with the reason) instead of sinking
    /// the whole batch; the call fails loud only if EVERY chunk failed (Q3).</summary>
    public async Task<(bool ok, string? error, IReadOnlyList<NexusUpdateStatus> results)> CheckUpdatesAsync(
        IReadOnlyList<(int modId, string? installed)> mods, CancellationToken ct)
    {
        // Dedupe by modId (keep the first installed), preserving order — a duplicate alias would collide in the query.
        var seen = new HashSet<int>();
        var ordered = new List<(int modId, string? installed)>();
        foreach (var m in mods) if (m.modId > 0 && seen.Add(m.modId)) ordered.Add(m);
        if (ordered.Count == 0) return (false, "no valid mod ids to check.", Array.Empty<NexusUpdateStatus>());

        const int ChunkSize = 25;   // OR-branches + modFiles aliases per request; conservative vs an unknown complexity cap
        var results = new List<NexusUpdateStatus>(ordered.Count);
        string? firstError = null;

        for (int i = 0; i < ordered.Count; i += ChunkSize)
        {
            var chunk = ordered.Skip(i).Take(ChunkSize).ToList();
            var g = SkyrimSeGameId;
            var branches = string.Join(",", chunk.Select(c =>
                $"{{gameId:{{value:\"{g}\",op:EQUALS}},modId:{{value:\"{c.modId}\",op:EQUALS}}}}"));
            var aliases = string.Join(" ", chunk.Select(c =>
                $"f{c.modId}:modFiles(modId:\"{c.modId}\",gameId:\"{g}\"){{ version category date }}"));
            // count MUST be >= the chunk size: the mods field defaults to a 20-item page, so without it any chunk of 21+
            // silently drops the overflow from nodes — and the verdict path reads an absent mod as NotFound, a confidently
            // WRONG answer at the tool's intended scale (live-proven: 21 matches → 20 nodes without count, 21 with).
            var query = $"query{{ mods(count:{chunk.Count}, filter:{{op:OR, filter:[{branches}]}}){{ nodes{{ modId name version }} }} {aliases} }}";

            var (ok, error, data) = await PostAsync(query, new { }, ct);
            if (!ok)
            {
                firstError ??= error;
                foreach (var c in chunk)
                    results.Add(new NexusUpdateStatus(c.modId, false, null, null, null, 0, c.installed, UpdateVerdict.Error, error));
                continue;
            }

            var nodeById = new Dictionary<int, (string? name, string? version)>();
            if (data.TryGetProperty("mods", out var mm) && mm.ValueKind == JsonValueKind.Object
                && mm.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
                foreach (var n in nodes.EnumerateArray())
                    nodeById[Int(n, "modId")] = (Str(n, "name"), Str(n, "version"));

            foreach (var c in chunk)
            {
                bool found = nodeById.TryGetValue(c.modId, out var meta);
                var (mainVer, mainDate) = NewestMainFromAlias(data, $"f{c.modId}");
                UpdateVerdict verdict =
                    !found                                     ? UpdateVerdict.NotFound :
                    mainVer is null                            ? UpdateVerdict.NoMainFile :
                    string.IsNullOrWhiteSpace(c.installed)     ? UpdateVerdict.LatestOnly :
                    string.Equals(c.installed.Trim(), mainVer.Trim(), StringComparison.OrdinalIgnoreCase)
                                                               ? UpdateVerdict.Current : UpdateVerdict.Differs;
                results.Add(new NexusUpdateStatus(
                    c.modId, found, meta.name, meta.version, mainVer, mainDate, c.installed, verdict));
            }
        }

        // Partial failures ride as Error rows; only an all-failed batch fails the call loud (Q3 — never a silent empty).
        if (firstError is not null && results.All(r => r.Verdict == UpdateVerdict.Error))
            return (false, firstError, Array.Empty<NexusUpdateStatus>());
        return (true, null, results);
    }

    /// <summary>Newest MAIN file (version + unix-seconds date) from an aliased <c>f&lt;modId&gt;</c> modFiles array in a
    /// batch response; (null, 0) when the mod has no MAIN file. The MAIN file is the accurate version of record.</summary>
    static (string? version, long date) NewestMainFromAlias(JsonElement data, string alias)
    {
        if (!data.TryGetProperty(alias, out var arr) || arr.ValueKind != JsonValueKind.Array) return (null, 0);
        string? bestVer = null; long bestDate = 0;
        foreach (var f in arr.EnumerateArray())
        {
            if (Str(f, "category") != "MAIN") continue;
            var d = Long(f, "date");
            if (bestVer is null || d > bestDate) { bestVer = Str(f, "version") ?? "?"; bestDate = d; }
        }
        return (bestVer, bestDate);
    }

    /// <summary>Identify uploaded files by MD5 hash — bulk (v2 <c>fileHashes(md5s: [String!]!)</c>, keyless). For each
    /// matched hash, return the mod (id + name), the file (name/version/category/size), and the game id (so a hash that
    /// belongs to a NON-Skyrim-SE file is flagged, never mis-attributed — Q3). Hashes with no match simply don't appear
    /// in the response; the tool maps them back to an explicit "no match". md5s go through a query VARIABLE (never
    /// concatenated into the query text).</summary>
    public async Task<(bool ok, string? error, IReadOnlyList<NexusFileHash> results)> IdentifyByHashAsync(
        IReadOnlyList<string> md5s, CancellationToken ct)
    {
        var (ok, error, data) = await PostAsync(FileHashQuery, new { md5s }, ct);
        if (!ok) return (false, error, Array.Empty<NexusFileHash>());

        var list = new List<NexusFileHash>();
        if (data.TryGetProperty("fileHashes", out var fh) && fh.ValueKind == JsonValueKind.Array)
            foreach (var h in fh.EnumerateArray())
            {
                int modId = 0; string? modName = null, fileVer = null, fileCat = null;
                if (h.TryGetProperty("modFile", out var mf) && mf.ValueKind == JsonValueKind.Object)
                {
                    modId = Int(mf, "modId"); fileVer = Str(mf, "version"); fileCat = Str(mf, "category");
                    if (mf.TryGetProperty("mod", out var mod) && mod.ValueKind == JsonValueKind.Object)
                    {
                        if (modId == 0) modId = Int(mod, "modId");
                        modName = Str(mod, "name");
                    }
                }
                list.Add(new NexusFileHash(
                    Str(h, "md5") ?? "", Str(h, "fileName") ?? "", Str(h, "fileType") ?? "",
                    Long(h, "fileSize"), Int(h, "gameId"), Int(h, "modFileId"), modId, modName, fileVer, fileCat));
            }
        return (true, null, list);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────────────────
    //  Core POST — the ONE place an exception can come from a Nexus call, so the ONE place Q3 turns every failure into
    //  a returned message. Returns the GraphQL `data` element (cloned to outlive the JsonDocument) on success.
    // ──────────────────────────────────────────────────────────────────────────────────────────────────────────
    async Task<(bool ok, string? error, JsonElement data)> PostAsync(string query, object variables, CancellationToken ct)
    {
        string body;
        int status;
        string? reason;
        bool success;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = JsonContent.Create(new { query, variables }, options: Json),
            };
            using var resp = await _http.SendAsync(req, ct);
            body = await resp.Content.ReadAsStringAsync(ct);
            status = (int)resp.StatusCode;
            reason = resp.ReasonPhrase;
            success = resp.IsSuccessStatusCode;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, "the Nexus Mods request timed out. Check your connection and try again — houseCARL's local "
                + "(load-order) tools are unaffected.", default);
        }
        catch (OperationCanceledException) { return (false, "the Nexus Mods request was cancelled.", default); }
        catch (HttpRequestException ex)
        {
            return (false, $"couldn't reach Nexus Mods ({ex.Message}). The Nexus tools need an internet connection; "
                + "houseCARL's local tools work offline.", default);
        }

        if (status == 429)
            return (false, "Nexus Mods is rate-limiting the connection (HTTP 429). Wait a moment and try again.", default);
        if (!success)
            return (false, $"Nexus Mods returned HTTP {status} ({reason}).", default);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (Exception ex) { return (false, $"Nexus Mods returned an unreadable response ({ex.Message}).", default); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0)
            {
                var msgs = errs.EnumerateArray()
                    .Select(e => e.TryGetProperty("message", out var mm) ? mm.GetString() : null)
                    .Where(s => !string.IsNullOrWhiteSpace(s));
                return (false, "Nexus Mods query error: " + string.Join("; ", msgs), default);
            }
            if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Object)
                return (false, "Nexus Mods returned no data.", default);
            return (true, null, dataEl.Clone());   // Clone: survive the using-dispose of doc.
        }
    }

    // ── tolerant JsonElement readers (a missing/typed-wrong field reads as null/0/false, never throws — Q3) ──
    static string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static int Int(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
    static long Long(JsonElement e, string p)
    {
        if (!e.TryGetProperty(p, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return l;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;   // tolerate date-as-string
        return 0;
    }
    static bool Bool(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;
    static IReadOnlyList<string> StrList(JsonElement e, string p)
    {
        if (!e.TryGetProperty(p, out var v) || v.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var x in v.EnumerateArray())
            if (x.ValueKind == JsonValueKind.String) { var s = x.GetString(); if (!string.IsNullOrWhiteSpace(s)) list.Add(s!); }
        return list;
    }

    // ── GraphQL documents (variable-based ⇒ user input is never string-concatenated into the query) ──
    const string SearchQuery =
        @"query Search($filter: ModsFilter!, $sort: [ModsSort!], $count: Int!) {
            mods(filter: $filter, sort: $sort, count: $count) {
              totalCount
              nodes { modId name version author endorsements downloads updatedAt adultContent summary category }
            }
          }";

    const string ModQuery =
        @"query ModDetail($modId: ID!, $gameId: ID!) {
            mod(modId: $modId, gameId: $gameId) {
              modId name version summary description author category endorsements downloads
              updatedAt createdAt adultContent status directDownloadEnabled
              modRequirements { nexusRequirements { nodes { modId modName url notes externalRequirement } } }
            }
            modFiles(modId: $modId, gameId: $gameId) {
              fileId name version category date description changelogText
            }
          }";

    const string FileHashQuery =
        @"query FileHashes($md5s: [String!]!) {
            fileHashes(md5s: $md5s) {
              md5 fileName fileType fileSize gameId modFileId
              modFile { modId version category name mod { modId name } }
            }
          }";
}

// ── result shapes (records ⇒ immutable, value-equal; the tools render these to text) ──

/// <summary>One row of a search result.</summary>
public sealed record NexusSearchHit(
    int ModId, string Name, string? Version, string? Author, int Endorsements, int Downloads,
    string? UpdatedAt, bool AdultContent, string? Summary, string? Category);

/// <summary>A search response: the true total match count plus the (capped) page of hits.</summary>
public sealed record NexusSearchResult(int TotalCount, IReadOnlyList<NexusSearchHit> Hits);

/// <summary>One Nexus requirement of a mod. <paramref name="ExternalRequirement"/> ⇒ an off-Nexus dependency (ModId/Url
/// point off-site); otherwise ModId is the required mod's numeric id on Nexus.</summary>
public sealed record NexusRequirement(string ModId, string ModName, string? Url, string? Notes, bool ExternalRequirement);

/// <summary>One uploaded file of a mod. <paramref name="Category"/> is MAIN / OPTIONAL / OLD_VERSION / ARCHIVED / …;
/// <paramref name="Date"/> is a unix-seconds timestamp. <paramref name="ChangelogText"/> is that file/version's changelog
/// lines (empty when the author wrote none — the caller reports "unknown", never "no changes", per Q3).</summary>
public sealed record NexusFile(
    int FileId, string Name, string? Version, string Category, long Date, string? Description, IReadOnlyList<string> ChangelogText);

/// <summary>Full detail for one mod plus its files. Note: <paramref name="Version"/> is the mod's version HEADER, which
/// can lag the newest MAIN file — the accurate "latest version" is the most recent MAIN entry in <paramref name="Files"/>.</summary>
public sealed record NexusModDetail(
    int ModId, string Name, string? Version, string? Summary, string? Description, string? Author, string Category,
    int Endorsements, int Downloads, string? UpdatedAt, string? CreatedAt, bool AdultContent, string Status,
    bool DirectDownloadEnabled, IReadOnlyList<NexusRequirement> NexusRequirements, IReadOnlyList<NexusFile> Files);

/// <summary>The verdict for one mod in a batch update check. <see cref="Differs"/> deliberately does NOT assert a
/// direction (behind vs ahead) — a freeform version string like '6.9' vs '6.11' isn't safely comparable, so the tool
/// reports both versions and lets the reader (or the triage skill) judge; asserting "you're behind" could be wrong when
/// the user is on a hotfix. <see cref="NoMainFile"/> / <see cref="NotFound"/> / <see cref="Error"/> are the Q3 honest
/// "couldn't decide" states, never silently folded into "current".</summary>
public enum UpdateVerdict { NotFound, NoMainFile, Current, Differs, LatestOnly, Error }

/// <summary>One mod's batch update-check result. <paramref name="LatestMainVersion"/>/<paramref name="LatestMainDate"/>
/// are the newest MAIN file (the accurate version of record); <paramref name="HeaderVersion"/> is the mod's version
/// header (which can lag). <paramref name="Note"/> carries the failure reason when <paramref name="Verdict"/> is Error.</summary>
public sealed record NexusUpdateStatus(
    int ModId, bool Found, string? Name, string? HeaderVersion,
    string? LatestMainVersion, long LatestMainDate, string? Installed, UpdateVerdict Verdict, string? Note = null);

/// <summary>One MD5-hash match: the Nexus file (name/type/size) and the mod it belongs to (id + name), plus the file's
/// version + category and the <paramref name="GameId"/> — a match on a non-Skyrim-SE game is flagged, not mis-attributed.</summary>
public sealed record NexusFileHash(
    string Md5, string FileName, string FileType, long FileSize, int GameId, int ModFileId,
    int ModId, string? ModName, string? FileVersion, string? FileCategory);

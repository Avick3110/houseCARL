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

    /// <summary>Batch FILE-LEVEL currency check — "is the exact file each of these mods installed still a current file?"
    /// For each (modId, installedVersion?, installedFileIds) it resolves every installed file id to its live category in
    /// the mod's file list: still a live file (MAIN/UPDATE/OPTIONAL/MISCELLANEOUS) ⇒ CURRENT; moved to OLD_VERSION/
    /// ARCHIVED ⇒ OUTDATED (and it points to the newest same-name file to update to); no longer on the page ⇒ FileGone (a
    /// loud UNKNOWN). This is immune to the multi-file-page false positive a mod-level "installed == newest MAIN" compare
    /// falls into — a Nexus page hosts many independently-versioned files, and the version of record is the file you
    /// actually installed, not the page's single newest main. When NO file id is available (a FOMOD/manual install) it
    /// degrades LOUDLY to NoFileId (never the old confidently-wrong mod-level compare — the very bug this fixes).
    /// Entries are GROUPED by modId (a page split across several mod folders — e.g. one Xtudo pack per creature — shares a
    /// modId; their file ids MERGE, so the page is queried ONCE and each installed file checked, never dropped). One
    /// combined query per chunk: an OR-batched <c>mods()</c> for names/headers + one aliased <c>modFiles()</c> per mod.
    /// modIds/fileIds are integers (parsed by the tool), so inlining them is injection-safe; the installed version is only
    /// ever compared LOCALLY. A chunk that fails marks only its own mods Error; the call fails loud only if EVERY chunk
    /// failed (Q3).</summary>
    public async Task<(bool ok, string? error, IReadOnlyList<NexusUpdateStatus> results)> CheckUpdatesAsync(
        IReadOnlyList<(int modId, string? installed, IReadOnlyList<int> fileIds)> mods, CancellationToken ct)
    {
        var (order, map) = GroupRequests(mods);
        if (order.Count == 0) return (false, "no valid mod ids to check.", Array.Empty<NexusUpdateStatus>());

        const int ChunkSize = 25;   // OR-branches + modFiles aliases per request; conservative vs an unknown complexity cap
        var results = new List<NexusUpdateStatus>(order.Count);
        string? firstError = null;

        for (int i = 0; i < order.Count; i += ChunkSize)
        {
            var chunk = order.Skip(i).Take(ChunkSize).ToList();
            var g = SkyrimSeGameId;
            var branches = string.Join(",", chunk.Select(id =>
                $"{{gameId:{{value:\"{g}\",op:EQUALS}},modId:{{value:\"{id}\",op:EQUALS}}}}"));
            // The alias now selects fileId + name too (was version/category/date) — the fields a FILE-level check needs to
            // join the installed file id to its live category and name.
            var aliases = string.Join(" ", chunk.Select(id =>
                $"f{id}:modFiles(modId:\"{id}\",gameId:\"{g}\"){{ fileId name version category date }}"));
            // count MUST be >= the chunk size: the mods field defaults to a 20-item page, so without it any chunk of 21+
            // silently drops the overflow from nodes — and the verdict path reads an absent mod as NotFound, a confidently
            // WRONG answer at the tool's intended scale (live-proven: 21 matches → 20 nodes without count, 21 with).
            var query = $"query{{ mods(count:{chunk.Count}, filter:{{op:OR, filter:[{branches}]}}){{ nodes{{ modId name version }} }} {aliases} }}";

            var (ok, error, data) = await PostAsync(query, new { }, ct);
            if (!ok)
            {
                firstError ??= error;
                foreach (var id in chunk)
                    results.Add(new NexusUpdateStatus(id, false, null, null, map[id].installed, UpdateVerdict.Error,
                        NoFiles, null, 0, 0, error));
                continue;
            }

            var nodeById = new Dictionary<int, (string? name, string? version)>();
            if (data.TryGetProperty("mods", out var mm) && mm.ValueKind == JsonValueKind.Object
                && mm.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
                foreach (var n in nodes.EnumerateArray())
                    nodeById[Int(n, "modId")] = (Str(n, "name"), Str(n, "version"));

            foreach (var id in chunk)
            {
                bool found = nodeById.TryGetValue(id, out var meta);
                var files = FilesFromAlias(data, $"f{id}");
                results.Add(ComputeStatus(id, found, meta.name, meta.version, map[id].installed, map[id].fileIds, files));
            }
        }

        // Partial failures ride as Error rows; only an all-failed batch fails the call loud (Q3 — never a silent empty).
        if (firstError is not null && results.All(r => r.Verdict == UpdateVerdict.Error))
            return (false, firstError, Array.Empty<NexusUpdateStatus>());
        return (true, null, results);
    }

    static readonly IReadOnlyList<InstalledFileCurrency> NoFiles = Array.Empty<InstalledFileCurrency>();

    /// <summary>Group the check-update requests by modId — NOT dedupe-drop. A Nexus page split across several MO2 mod
    /// folders (e.g. one Xtudo pack per creature) shares a modId, and each folder installed a DIFFERENT file; keeping only
    /// the first would silently un-check the rest (the multi-folder-page silent-drop class). So merge every entry's file
    /// ids (order-preserving, deduped) under its modId, and keep the FIRST non-empty installed version for the
    /// no-file-id fallback display. Returns the modId order (first-seen) + the per-modId merged state. Internal for the CI
    /// guard.</summary>
    internal static (List<int> order, Dictionary<int, (string? installed, List<int> fileIds)> map) GroupRequests(
        IReadOnlyList<(int modId, string? installed, IReadOnlyList<int> fileIds)> mods)
    {
        var order = new List<int>();
        var map = new Dictionary<int, (string? installed, List<int> fileIds)>();
        foreach (var m in mods)
        {
            if (m.modId <= 0) continue;
            if (!map.TryGetValue(m.modId, out var grp)) { grp = (null, new List<int>()); order.Add(m.modId); }
            if (grp.installed is null && !string.IsNullOrWhiteSpace(m.installed)) grp.installed = m.installed;
            if (m.fileIds is not null)
                foreach (var fid in m.fileIds) if (fid > 0 && !grp.fileIds.Contains(fid)) grp.fileIds.Add(fid);
            map[m.modId] = grp;
        }
        return (order, map);
    }

    /// <summary>Whether a file category is one of Nexus's two RETIREMENT buckets (OLD_VERSION / ARCHIVED) — the CLOSED
    /// superseded set. Every other category (MAIN/UPDATE/OPTIONAL/MISCELLANEOUS, or one Nexus adds later) is treated as
    /// live/offered, and the category string is always carried into the output, so an unfamiliar one is visible rather
    /// than silently mis-bucketed (Q3).</summary>
    static bool IsSuperseded(string category) => category is "OLD_VERSION" or "ARCHIVED";

    /// <summary>Resolve one mod's file-level currency from its installed file id(s) and its full file list. See
    /// <see cref="CheckUpdatesAsync"/> for what each verdict means. Internal (pure) for the CI guard. A mod absent from
    /// the mods() SEARCH (<paramref name="found"/> false) but whose direct modFiles lookup returned files — the
    /// manager-only (nxm) class Nexus hides from its search collection — is still resolved FROM those files, never
    /// stamped NotFound; only a mod that is both search-absent AND fileless is genuinely not found.</summary>
    internal static NexusUpdateStatus ComputeStatus(int modId, bool found, string? name, string? header, string? installed,
        IReadOnlyList<int> fileIds, List<(int fileId, string name, string? version, string category, long date)> files)
    {
        // NotFound only when the mod is BOTH absent from the mods() search AND returned no files from the direct modFiles
        // lookup. The search collection silently EXCLUDES manager-only (nxm) mods — a large, mainstream class — yet their
        // modFiles lookup resolves fine; gating NotFound on the search alone would discard those already-fetched files and
        // stamp a real, checkable mod "not found" (the same confidently-wrong class this whole check exists to kill). Files
        // present ⇒ the mod exists: fall through and check them (the friendly name may be null — the file rows carry names).
        // Load-bearing assumption: a genuinely-absent mod (wrong id / LE-only / hidden) returns an EMPTY modFiles list here
        // (not an error, not cross-game files) — that empty list is what makes NotFound genuine. Even if that ever broke and
        // files came back for a non-SSE mod, its installed fileid won't match one → FileGone (loud), never a silent "current".
        if (!found && files.Count == 0)
            return new NexusUpdateStatus(modId, false, name, header, installed, UpdateVerdict.NotFound, NoFiles, null, 0, 0);

        // Newest live MAIN + how many — context for LatestOnly and the no-file-id fallback (LiveMainCount>1 ⇒ a multi-main
        // page a version compare can't safely resolve: the false-positive root cause).
        string? mainVer = null; long mainDate = 0; int mainCount = 0;
        foreach (var f in files)
            if (f.category == "MAIN") { mainCount++; if (mainVer is null || f.date > mainDate) { mainVer = f.version ?? "?"; mainDate = f.date; } }

        // FILE-LEVEL — the exact installed file id(s) are the honest currency key.
        if (fileIds.Count > 0)
        {
            var detail = new List<InstalledFileCurrency>(fileIds.Count);
            foreach (var fid in fileIds)
            {
                int idx = files.FindIndex(f => f.fileId == fid);
                if (idx < 0) { detail.Add(new InstalledFileCurrency(fid, null, null, null, FileVerdict.Missing, null, null, 0)); continue; }
                var hit = files[idx];
                if (IsSuperseded(hit.category))
                {
                    // Point to the newest LIVE file with the SAME name (the variant line's replacement); left null if the
                    // author renamed it or dropped the variant — then the report says so rather than guess the wrong file.
                    string? rn = null, rv = null; long rd = 0;
                    foreach (var f in files)
                        if (!IsSuperseded(f.category) && string.Equals(f.name, hit.name, StringComparison.OrdinalIgnoreCase) && (rn is null || f.date > rd))
                            { rn = f.name; rv = f.version ?? "?"; rd = f.date; }
                    detail.Add(new InstalledFileCurrency(fid, hit.name, hit.version, hit.category, FileVerdict.Superseded, rn, rv, rd));
                }
                else detail.Add(new InstalledFileCurrency(fid, hit.name, hit.version, hit.category, FileVerdict.Live, null, null, 0));
            }
            var verdict = detail.Any(d => d.Verdict == FileVerdict.Superseded) ? UpdateVerdict.Outdated
                        : detail.Any(d => d.Verdict == FileVerdict.Missing)    ? UpdateVerdict.FileGone
                        :                                                        UpdateVerdict.Current;
            return new NexusUpdateStatus(modId, true, name, header, installed, verdict, detail, mainVer, mainDate, mainCount);
        }

        // NO FILE ID — degrade LOUDLY (never the old mod-level compare). Bare id (no version either) ⇒ just list newest.
        var fallback = string.IsNullOrWhiteSpace(installed) ? UpdateVerdict.LatestOnly : UpdateVerdict.NoFileId;
        return new NexusUpdateStatus(modId, true, name, header, installed, fallback, NoFiles, mainVer, mainDate, mainCount);
    }

    /// <summary>Parse an aliased <c>f&lt;modId&gt;</c> modFiles array from a batch response into (fileId, name, version,
    /// category, date) tuples; empty when the alias is absent or not an array.</summary>
    static List<(int fileId, string name, string? version, string category, long date)> FilesFromAlias(JsonElement data, string alias)
    {
        var list = new List<(int, string, string?, string, long)>();
        if (!data.TryGetProperty(alias, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var f in arr.EnumerateArray())
            list.Add((Int(f, "fileId"), Str(f, "name") ?? "", Str(f, "version"), Str(f, "category") ?? "", Long(f, "date")));
        return list;
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

/// <summary>Whether one installed file is still a LIVE file on its mod's page, has been SUPERSEDED (the author moved it
/// to OLD_VERSION/ARCHIVED), or is MISSING entirely (hidden/deleted — can't determine). The file-level currency signal a
/// mod-level version compare can't give: Nexus itself retires a file, so its category IS the honest "is my exact file
/// current" answer — immune to the multi-file-page confusion.</summary>
public enum FileVerdict { Live, Superseded, Missing }

/// <summary>One installed file's currency: the file (resolved from its id) and its verdict, plus — when
/// <see cref="Verdict"/> is <see cref="FileVerdict.Superseded"/> — the newest LIVE file with the SAME name (the same
/// variant line's replacement to update to; null when the author renamed/dropped that variant). <see cref="Name"/>/
/// <see cref="Version"/>/<see cref="Category"/> are null only for a <see cref="FileVerdict.Missing"/> file (not on the
/// page to resolve).</summary>
public sealed record InstalledFileCurrency(
    int FileId, string? Name, string? Version, string? Category, FileVerdict Verdict,
    string? NewestSameName, string? NewestSameVersion, long NewestSameDate);

/// <summary>The verdict for one mod in a batch FILE-LEVEL update check — "is the exact file I installed still current?"
/// <see cref="Current"/> = every installed file is still a live file; <see cref="Outdated"/> = at least one was retired
/// to OLD_VERSION/ARCHIVED (the per-file detail points to its same-name replacement); <see cref="FileGone"/> = an
/// installed file id is no longer on the page (hidden/deleted) — a loud UNKNOWN, never "current". <see cref="NoFileId"/>
/// = no file id was available (a FOMOD/manual install) so a file-level check can't run — a LOUD best-effort fallback,
/// never the old confidently-wrong mod-level compare. <see cref="LatestOnly"/> = only a mod id was given (no version, no
/// file id) → newest listed, no verdict. <see cref="NotFound"/> / <see cref="Error"/> are the Q3 honest "couldn't
/// decide" states, never silently folded into "current".</summary>
public enum UpdateVerdict { Current, Outdated, FileGone, NoFileId, LatestOnly, NotFound, Error }

/// <summary>One mod's batch update-check result. <paramref name="Files"/> is the per-installed-file currency detail
/// (present for the file-level verdicts Current/Outdated/FileGone; empty otherwise). <paramref name="LatestMainVersion"/>
/// /<paramref name="LatestMainDate"/> + <paramref name="LiveMainCount"/> are the newest live MAIN file — context for
/// LatestOnly and the NoFileId fallback (LiveMainCount &gt; 1 ⇒ a multi-main page a version compare can't safely
/// resolve). <paramref name="HeaderVersion"/> is the mod's version header (can lag). <paramref name="Installed"/> is the
/// caller's installed version string (for the NoFileId display). <paramref name="Note"/> carries the failure reason when
/// <paramref name="Verdict"/> is Error.</summary>
public sealed record NexusUpdateStatus(
    int ModId, bool Found, string? Name, string? HeaderVersion, string? Installed, UpdateVerdict Verdict,
    IReadOnlyList<InstalledFileCurrency> Files, string? LatestMainVersion, long LatestMainDate, int LiveMainCount,
    string? Note = null);

/// <summary>One MD5-hash match: the Nexus file (name/type/size) and the mod it belongs to (id + name), plus the file's
/// version + category and the <paramref name="GameId"/> — a match on a non-Skyrim-SE game is flagged, not mis-attributed.</summary>
public sealed record NexusFileHash(
    string Md5, string FileName, string FileType, long FileSize, int GameId, int ModFileId,
    int ModId, string? ModName, string? FileVersion, string? FileCategory);

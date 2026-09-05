using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HousecarlMcp;

/// <summary>Read-only bridge to the Nexus Mods public v2 GraphQL API — search the catalog and look up a mod's version,
/// requirements and files. It never downloads, installs, endorses or mutates anything; a download stays the mod
/// manager's job. Keyless: the v2 read surface is public and anonymous, so there is no API key to configure. Every
/// failure mode — no connection, timeout, HTTP error, rate limit, malformed body, GraphQL error — is returned as a
/// plain message and never thrown, so the local load-order tools keep working offline. This is the server's only
/// outbound network dependency, and it stays out of housecarl-core, which is network-free.</summary>
public sealed class NexusClient
{
    /// <summary>Skyrim Special Edition's Nexus game id (domainName 'skyrimspecialedition'). Every query is scoped to
    /// this, so the user never types a game id.</summary>
    public const int SkyrimSeGameId = 1704;

    const string Endpoint = "https://api.nexusmods.com/v2/graphql";

    readonly HttpClient _http;

    // PropertyNamingPolicy=null: GraphQL field and variable names are case-sensitive (gameId, modId, categoryName,
    // direction), so a naming policy must not rewrite them.
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = null };

    public NexusClient(HttpClient http) => _http = http;

    // ──────────────────────────────────────────────────────────────────────────────────────────────────────────
    //  Public API — each returns (ok, error, payload). ok==false means error is a user-facing message, payload null.
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

        // sort is [{ <field>: { direction } }] — a single-key object, hence the dictionary. Name sorts ascending
        // (A-Z); every other field descending, so most endorsements, downloads or most recent come first.
        var direction = sortField == "name" ? "ASC" : "DESC";
        var sort = new[] { new Dictionary<string, object> { [sortField] = new { direction } } };

        var (ok, error, data) = await PostAsync(SearchQuery, new { filter, sort, count }, ct);
        if (!ok) return (false, error, null);

        // Guard the root navigation: a 200 with an unexpected shape must still return cleanly rather than throw, and
        // GetProperty would throw on a missing field.
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

        // Guard the root navigation: a missing 'mod' on a 200 returns cleanly rather than throwing.
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

        // Page tags are author and community labels (Gameplay, Lore-Friendly, SKSE), returned as a list of { name }
        // objects rather than plain strings, hence not StrList.
        var tags = new List<string>();
        if (m.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Array)
            foreach (var t in tg.EnumerateArray())
            { var n = Str(t, "name"); if (!string.IsNullOrWhiteSpace(n)) tags.Add(n!); }

        return (true, null, new NexusModDetail(
            Int(m, "modId"), Str(m, "name") ?? "", Str(m, "version"), Str(m, "summary"), Str(m, "description"),
            Str(m, "author"), Str(m, "category") ?? "", Int(m, "endorsements"), Int(m, "downloads"),
            Str(m, "updatedAt"), Str(m, "createdAt"), Bool(m, "adultContent"), Str(m, "status") ?? "",
            Bool(m, "directDownloadEnabled"), reqs, files, tags));
    }

    /// <summary>Run a raw keyless query against the Nexus v2 endpoint — the completeness backstop behind the curated
    /// tools, exposing the whole public graph so a field they do not surface is still reachable. Read-only by
    /// contract: <see cref="IsMutatingQuery"/> refuses mutation and subscription. Returns the raw GraphQL <c>data</c>
    /// element; failures come back through <see cref="PostAsync"/> as messages. Variables ride the same variable
    /// channel the typed queries use and are never concatenated into the query text.</summary>
    public async Task<(bool ok, string? error, JsonElement data)> RawQueryAsync(string query, JsonElement? variables, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return (false, "no GraphQL query given.", default);
        if (IsMutatingQuery(query))
            return (false, "this tool is READ-ONLY: mutation/subscription operations are refused (the keyless Nexus "
                + "endpoint can't run them anyway). Pass a query{ ... }.", default);
        object vars = variables is { ValueKind: JsonValueKind.Object } v ? v : new { };
        return await PostAsync(query, vars, ct);
    }

    /// <summary>True when a GraphQL document contains a mutation or subscription operation — the keyword at document
    /// start or after a prior operation's closing '}'. Deliberately does not match a field that merely contains the
    /// word, such as a selection named 'mutationCount', so a legitimate read is never refused.</summary>
    internal static bool IsMutatingQuery(string query) =>
        Regex.IsMatch(query, @"(^|\})\s*(mutation|subscription)\b", RegexOptions.IgnoreCase);

    /// <summary>Batch file-level currency check: is the exact file each of these mods installed still current? Each
    /// installed file id resolves to its live category in the mod's file list — a live category (MAIN, UPDATE,
    /// OPTIONAL, MISCELLANEOUS) is Current; OLD_VERSION or ARCHIVED is Outdated and points at the newest same-name
    /// LIVE file; REMOVED or DELETED is FileRemoved, the author having withdrawn the file rather than superseded it,
    /// so a same-name live file is named as a lead and never as the replacement, and the row says to read the page;
    /// absent from the page is FileGone. A mod with both a withdrawn and a retired file reads FileRemoved: taking the
    /// newest same-name file answers the retirement and does not answer the withdrawal. This avoids the false
    /// positive a mod-level "installed == newest MAIN"
    /// compare hits, since a Nexus page hosts many independently-versioned files. With no file id available (a FOMOD
    /// or manual install) it degrades to NoFileId rather than falling back to that compare. Entries are grouped by
    /// modId, because one page split across several mod folders shares a modId and each folder may have installed a
    /// different file. One combined query per chunk: an OR-batched <c>mods()</c> for names and headers plus one
    /// aliased <c>modFiles()</c> per mod. modIds and fileIds are integers, so inlining them is injection-safe. A chunk
    /// that fails marks only its own mods Error; the call fails outright only if every chunk failed.</summary>
    public async Task<(bool ok, string? error, IReadOnlyList<NexusUpdateStatus> results)> CheckUpdatesAsync(
        IReadOnlyList<(int modId, string? installed, IReadOnlyList<int> fileIds)> mods, CancellationToken ct)
    {
        var (order, map) = GroupRequests(mods);
        if (order.Count == 0) return (false, "no valid mod ids to check.", Array.Empty<NexusUpdateStatus>());

        const int ChunkSize = 25;   // OR-branches and modFiles aliases per request; conservative against an unknown complexity cap
        var results = new List<NexusUpdateStatus>(order.Count);
        string? firstError = null;

        for (int i = 0; i < order.Count; i += ChunkSize)
        {
            var chunk = order.Skip(i).Take(ChunkSize).ToList();
            var g = SkyrimSeGameId;
            var branches = string.Join(",", chunk.Select(id =>
                $"{{gameId:{{value:\"{g}\",op:EQUALS}},modId:{{value:\"{id}\",op:EQUALS}}}}"));
            // fileId and name are what join an installed file id to its live category and name.
            var aliases = string.Join(" ", chunk.Select(id =>
                $"f{id}:modFiles(modId:\"{id}\",gameId:\"{g}\"){{ fileId name version category date }}"));
            // count must be at least the chunk size: the mods field defaults to a 20-item page, so without it a chunk
            // of 21 or more silently drops the overflow, and the verdict path reads an absent mod as NotFound.
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

        // Partial failures ride as Error rows; only an all-failed batch fails the call.
        if (firstError is not null && results.All(r => r.Verdict == UpdateVerdict.Error))
            return (false, firstError, Array.Empty<NexusUpdateStatus>());
        return (true, null, results);
    }

    static readonly IReadOnlyList<InstalledFileCurrency> NoFiles = Array.Empty<InstalledFileCurrency>();

    /// <summary>Group the check-update requests by modId, merging rather than dropping duplicates: a Nexus page split
    /// across several MO2 mod folders shares a modId, and each folder may have installed a different file, so keeping
    /// only the first would un-check the rest. File ids merge order-preserving and deduped; the first non-empty
    /// installed version is kept for the no-file-id fallback display. Returns the first-seen modId order and the
    /// per-modId merged state.</summary>
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

    /// <summary>Whether a file category is one of Nexus's two retirement buckets, OLD_VERSION or ARCHIVED — a closed
    /// set. Every other category, including one Nexus adds later, counts as live, and the category string is always
    /// carried into the output so an unfamiliar one is visible rather than mis-bucketed.</summary>
    static bool IsSuperseded(string category) => category is "OLD_VERSION" or "ARCHIVED";

    /// <summary>Whether a file category is one of Nexus's two withdrawal buckets, REMOVED or DELETED — a closed set,
    /// separate from the retirement ones. A withdrawn file was pulled from the page rather than superseded by a
    /// newer upload, so it is not an update the caller can simply take.</summary>
    static bool IsRemoved(string category) => category is "REMOVED" or "DELETED";

    /// <summary>Whether a category means the file is still offered — neither retired nor withdrawn. The one test a
    /// replacement search uses, so a withdrawn file can never be offered as the answer to a retired one.</summary>
    static bool IsLive(string category) => !IsSuperseded(category) && !IsRemoved(category);

    /// <summary>Resolve one mod's file-level currency from its installed file ids and its full file list; see
    /// <see cref="CheckUpdatesAsync"/> for what each verdict means. A mod absent from the mods() search
    /// (<paramref name="found"/> false) but whose direct modFiles lookup returned files — the manager-only (nxm) class
    /// Nexus hides from its search collection — is resolved from those files rather than stamped NotFound.</summary>
    internal static NexusUpdateStatus ComputeStatus(int modId, bool found, string? name, string? header, string? installed,
        IReadOnlyList<int> fileIds, List<(int fileId, string name, string? version, string category, long date)> files)
    {
        // NotFound only when the mod is both absent from the mods() search and returned no files from the direct
        // modFiles lookup: the search collection excludes manager-only (nxm) mods, whose modFiles lookup resolves
        // fine, so gating on the search alone would stamp a real, checkable mod "not found". Files present means the
        // mod exists — fall through and check them; the friendly name may be null, since the file rows carry names.
        // This relies on a genuinely-absent mod (wrong id, LE-only, hidden) returning an empty modFiles list rather
        // than an error or cross-game files. Even then an installed fileid that matches nothing yields FileGone.
        if (!found && files.Count == 0)
            return new NexusUpdateStatus(modId, false, name, header, installed, UpdateVerdict.NotFound, NoFiles, null, 0, 0);

        // Newest live MAIN and how many there are — context for LatestOnly and the no-file-id fallback. More than one
        // means a multi-main page, which a version compare cannot safely resolve.
        string? mainVer = null; long mainDate = 0; int mainCount = 0;
        foreach (var f in files)
            if (f.category == "MAIN") { mainCount++; if (mainVer is null || f.date > mainDate) { mainVer = f.version ?? "?"; mainDate = f.date; } }

        // The exact installed file ids are the currency key.
        if (fileIds.Count > 0)
        {
            var detail = new List<InstalledFileCurrency>(fileIds.Count);
            foreach (var fid in fileIds)
            {
                int idx = files.FindIndex(f => f.fileId == fid);
                if (idx < 0) { detail.Add(new InstalledFileCurrency(fid, null, null, null, FileVerdict.Missing, null, null, 0)); continue; }
                var hit = files[idx];
                if (IsSuperseded(hit.category) || IsRemoved(hit.category))
                {
                    // Point to the newest live file with the same name — the variant line's replacement. Left null if
                    // the author renamed or dropped the variant, rather than guessing the wrong file.
                    string? rn = null, rv = null; long rd = 0;
                    foreach (var f in files)
                        if (IsLive(f.category) && string.Equals(f.name, hit.name, StringComparison.OrdinalIgnoreCase) && (rn is null || f.date > rd))
                            { rn = f.name; rv = f.version ?? "?"; rd = f.date; }
                    // This branch serves both buckets: withdrawn by the author, or merely retired to OLD/ARCHIVED.
                    var fileVerdict = IsRemoved(hit.category) ? FileVerdict.Removed : FileVerdict.Superseded;
                    detail.Add(new InstalledFileCurrency(fid, hit.name, hit.version, hit.category, fileVerdict, rn, rv, rd));
                }
                else detail.Add(new InstalledFileCurrency(fid, hit.name, hit.version, hit.category, FileVerdict.Live, null, null, 0));
            }
            // A withdrawn file outranks a retired one: taking the newest same-name file answers the retirement, and
            // does NOT answer a file the author pulled — that one wants the page read before anything is installed.
            var verdict = detail.Any(d => d.Verdict == FileVerdict.Removed)     ? UpdateVerdict.FileRemoved
                        : detail.Any(d => d.Verdict == FileVerdict.Superseded)  ? UpdateVerdict.Outdated
                        : detail.Any(d => d.Verdict == FileVerdict.Missing)     ? UpdateVerdict.FileGone
                        :                                                         UpdateVerdict.Current;
            return new NexusUpdateStatus(modId, true, name, header, installed, verdict, detail, mainVer, mainDate, mainCount);
        }

        // No file id: degrade rather than fall back to a mod-level compare. A bare id with no version just lists the newest.
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

    /// <summary>Identify uploaded files by MD5 hash in bulk, via the keyless v2 <c>fileHashes(md5s: [String!]!)</c>.
    /// Each match returns the mod, the file, and the game id, so a hash belonging to a non-Skyrim-SE file is flagged
    /// rather than mis-attributed. Unmatched hashes simply do not appear in the response, and the caller maps them
    /// back to an explicit "no match". md5s go through a query variable, never the query text.</summary>
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
    //  Core POST — the one place an exception can come from a Nexus call, so the one place every failure turns into a
    //  returned message. Returns the GraphQL `data` element, cloned to outlive the JsonDocument, on success.
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

    // ── tolerant JsonElement readers: a missing or wrongly-typed field reads as null/0/false and never throws ──
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

    // ── GraphQL documents: variable-based, so user input is never concatenated into the query ──
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
              tags { name }
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

// ── result shapes; the tools render these to text ──

/// <summary>One row of a search result.</summary>
public sealed record NexusSearchHit(
    int ModId, string Name, string? Version, string? Author, int Endorsements, int Downloads,
    string? UpdatedAt, bool AdultContent, string? Summary, string? Category);

/// <summary>A search response: the true total match count plus the (capped) page of hits.</summary>
public sealed record NexusSearchResult(int TotalCount, IReadOnlyList<NexusSearchHit> Hits);

/// <summary>One Nexus requirement of a mod. With <paramref name="ExternalRequirement"/> set, the dependency is
/// off-Nexus and ModId/Url point off-site; otherwise ModId is the required mod's numeric id on Nexus.</summary>
public sealed record NexusRequirement(string ModId, string ModName, string? Url, string? Notes, bool ExternalRequirement);

/// <summary>One uploaded file of a mod. <paramref name="Category"/> is MAIN, OPTIONAL, OLD_VERSION, ARCHIVED and so
/// on; <paramref name="Date"/> is a unix-seconds timestamp. <paramref name="ChangelogText"/> is empty when the author
/// wrote none, which the caller reports as unknown rather than as "no changes".</summary>
public sealed record NexusFile(
    int FileId, string Name, string? Version, string Category, long Date, string? Description, IReadOnlyList<string> ChangelogText);

/// <summary>Full detail for one mod plus its files. <paramref name="Version"/> is the mod's version header, which can
/// lag the newest MAIN file — the accurate latest version is the most recent MAIN entry in
/// <paramref name="Files"/>.</summary>
public sealed record NexusModDetail(
    int ModId, string Name, string? Version, string? Summary, string? Description, string? Author, string Category,
    int Endorsements, int Downloads, string? UpdatedAt, string? CreatedAt, bool AdultContent, string Status,
    bool DirectDownloadEnabled, IReadOnlyList<NexusRequirement> NexusRequirements, IReadOnlyList<NexusFile> Files,
    IReadOnlyList<string> Tags);

/// <summary>Whether one installed file is still live on its mod's page, has been superseded (moved to OLD_VERSION or
/// ARCHIVED), or is missing entirely (hidden or deleted, so undeterminable). Nexus itself retires a file, so its
/// category answers "is my exact file current" where a mod-level version compare cannot.</summary>
public enum FileVerdict { Live, Superseded, Missing, Removed }

/// <summary>One installed file's currency: the file resolved from its id and its verdict, plus — when
/// <see cref="Verdict"/> is <see cref="FileVerdict.Superseded"/> or <see cref="FileVerdict.Removed"/> — the newest
/// live file with the same name, null when the author renamed or dropped that variant. For a removed file that name
/// is a lead, not a replacement: the author pulled the file, and why is on the page. <see cref="Name"/>, <see cref="Version"/> and
/// <see cref="Category"/> are null only for a <see cref="FileVerdict.Missing"/> file, which is not on the page to
/// resolve.</summary>
public sealed record InstalledFileCurrency(
    int FileId, string? Name, string? Version, string? Category, FileVerdict Verdict,
    string? NewestSameName, string? NewestSameVersion, long NewestSameDate);

/// <summary>The verdict for one mod in a batch file-level update check. <see cref="Current"/>: every installed file is
/// still live. <see cref="FileRemoved"/>: at least one was withdrawn by the author (REMOVED or DELETED) — read the
/// page before installing anything in its place. <see cref="Outdated"/>: at least one was retired to OLD_VERSION or
/// ARCHIVED, and the per-file detail points to its same-name replacement.
/// <see cref="FileGone"/>: an installed file id is no longer on the page.
/// <see cref="NoFileId"/>: no file id was available (a FOMOD or manual install), so a file-level check cannot run.
/// <see cref="LatestOnly"/>: only a mod id was given, so the newest is listed with no verdict.
/// <see cref="NotFound"/> and <see cref="Error"/> mean the check could not decide, and are never folded into
/// Current.</summary>
public enum UpdateVerdict { Current, Outdated, FileGone, NoFileId, LatestOnly, NotFound, Error, FileRemoved }

/// <summary>One mod's batch update-check result. <paramref name="Files"/> carries the per-installed-file currency
/// detail for the file-level verdicts and is empty otherwise. <paramref name="LatestMainVersion"/>,
/// <paramref name="LatestMainDate"/> and <paramref name="LiveMainCount"/> describe the newest live MAIN file — context
/// for LatestOnly and the NoFileId fallback, where a count above one means a multi-main page a version compare cannot
/// safely resolve. <paramref name="HeaderVersion"/> is the mod's version header, which can lag.
/// <paramref name="Note"/> carries the failure reason when <paramref name="Verdict"/> is Error.</summary>
public sealed record NexusUpdateStatus(
    int ModId, bool Found, string? Name, string? HeaderVersion, string? Installed, UpdateVerdict Verdict,
    IReadOnlyList<InstalledFileCurrency> Files, string? LatestMainVersion, long LatestMainDate, int LiveMainCount,
    string? Note = null);

/// <summary>One MD5-hash match: the Nexus file, the mod it belongs to, the file's version and category, and the
/// <paramref name="GameId"/>, so a match on a non-Skyrim-SE game is flagged rather than mis-attributed.</summary>
public sealed record NexusFileHash(
    string Md5, string FileName, string FileType, long FileSize, int GameId, int ModFileId,
    int ModId, string? ModName, string? FileVersion, string? FileCategory);

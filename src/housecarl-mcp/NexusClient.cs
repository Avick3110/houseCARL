using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HousecarlMcp;

/// <summary>Keyless read-only client for the Nexus Mods public v2 GraphQL API; contract in docs/architecture/nexus.md.</summary>
public sealed class NexusClient
{
    /// <summary>Skyrim Special Edition's Nexus game id; every query is scoped to it.</summary>
    public const int SkyrimSeGameId = 1704;

    const string Endpoint = "https://api.nexusmods.com/v2/graphql";

    readonly HttpClient _http;

    // PropertyNamingPolicy=null: GraphQL field and variable names are case-sensitive.
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = null };

    public NexusClient(HttpClient http) => _http = http;

    // Each public call returns (ok, error, payload); ok==false means error is a user-facing message and payload null.

    /// <summary>Search Skyrim SE mods by a wildcard name term, optionally narrowed to a category, capped at a count.</summary>
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

        // sort is [{ <field>: { direction } }], a single-key object; name sorts ascending, every other field descending.
        var direction = sortField == "name" ? "ASC" : "DESC";
        var sort = new[] { new Dictionary<string, object> { [sortField] = new { direction } } };

        var (ok, error, data) = await PostAsync(SearchQuery, new { filter, sort, count }, ct);
        if (!ok) return (false, error, null);

        // Guard the root navigation: a 200 with an unexpected shape returns cleanly rather than throwing.
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

    /// <summary>Fetch one mod's detail and its files in a single request; a non-existent modId comes back as ok==false.</summary>
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

        // Page tags come back as a list of { name } objects rather than plain strings, hence not StrList.
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

    /// <summary>Run a raw read-only query against the Nexus v2 endpoint and return its GraphQL <c>data</c> element.</summary>
    public async Task<(bool ok, string? error, JsonElement data)> RawQueryAsync(string query, JsonElement? variables, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return (false, "no GraphQL query given.", default);
        if (IsMutatingQuery(query))
            return (false, "this tool is READ-ONLY: mutation/subscription operations are refused (the keyless Nexus "
                + "endpoint can't run them anyway). Pass a query{ ... }.", default);
        object vars = variables is { ValueKind: JsonValueKind.Object } v ? v : new { };
        return await PostAsync(query, vars, ct);
    }

    /// <summary>True when a document has a mutation or subscription operation; `nexus-graphql-guard` pins the match.</summary>
    internal static bool IsMutatingQuery(string query) =>
        Regex.IsMatch(query, @"(^|\})\s*(mutation|subscription)\b", RegexOptions.IgnoreCase);

    /// <summary>Batch file-level currency check: is the exact file each mod installed still current? Contract in docs/architecture/nexus.md.</summary>
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
            // count must be at least the chunk size: the mods field otherwise returns a 20-item page and drops the overflow.
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

    /// <summary>Group the requests by modId, merging file ids rather than dropping duplicates; pinned by `nexus-file-check-guard`.</summary>
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

    /// <summary>Whether a file category is one of Nexus's two retirement buckets.</summary>
    static bool IsSuperseded(string category) => category is "OLD_VERSION" or "ARCHIVED";

    /// <summary>Whether a file category is one of Nexus's two withdrawal buckets.</summary>
    static bool IsRemoved(string category) => category is "REMOVED" or "DELETED";

    /// <summary>Whether a category means the file is still offered — neither retired nor withdrawn.</summary>
    static bool IsLive(string category) => !IsSuperseded(category) && !IsRemoved(category);

    /// <summary>Resolve one mod's file-level currency from its installed file ids and its full file list; verdicts in docs/architecture/nexus.md.</summary>
    internal static NexusUpdateStatus ComputeStatus(int modId, bool found, string? name, string? header, string? installed,
        IReadOnlyList<int> fileIds, List<(int fileId, string name, string? version, string category, long date)> files)
    {
        // NotFound only when absent from the search AND no files came back (pinned by `nexus-file-check-guard`); the upstream assumption is in docs/architecture/nexus.md.
        if (!found && files.Count == 0)
            return new NexusUpdateStatus(modId, false, name, header, installed, UpdateVerdict.NotFound, NoFiles, null, 0, 0);

        // Newest live MAIN and how many there are; more than one is a multi-main page a version compare cannot resolve.
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
                    // The newest live file with the same name, left null when the author renamed or dropped the variant.
                    string? rn = null, rv = null; long rd = 0;
                    foreach (var f in files)
                        if (IsLive(f.category) && string.Equals(f.name, hit.name, StringComparison.OrdinalIgnoreCase) && (rn is null || f.date > rd))
                            { rn = f.name; rv = f.version ?? "?"; rd = f.date; }
                    var fileVerdict = IsRemoved(hit.category) ? FileVerdict.Removed : FileVerdict.Superseded;
                    detail.Add(new InstalledFileCurrency(fid, hit.name, hit.version, hit.category, fileVerdict, rn, rv, rd));
                }
                else detail.Add(new InstalledFileCurrency(fid, hit.name, hit.version, hit.category, FileVerdict.Live, null, null, 0));
            }
            // A withdrawn file outranks a retired one; pinned by `nexus-file-check-guard`.
            var verdict = detail.Any(d => d.Verdict == FileVerdict.Removed)     ? UpdateVerdict.FileRemoved
                        : detail.Any(d => d.Verdict == FileVerdict.Superseded)  ? UpdateVerdict.Outdated
                        : detail.Any(d => d.Verdict == FileVerdict.Missing)     ? UpdateVerdict.FileGone
                        :                                                         UpdateVerdict.Current;
            return new NexusUpdateStatus(modId, true, name, header, installed, verdict, detail, mainVer, mainDate, mainCount);
        }

        // No file id: degrade rather than fall back to a mod-level compare.
        var fallback = string.IsNullOrWhiteSpace(installed) ? UpdateVerdict.LatestOnly : UpdateVerdict.NoFileId;
        return new NexusUpdateStatus(modId, true, name, header, installed, fallback, NoFiles, mainVer, mainDate, mainCount);
    }

    /// <summary>Parse an aliased <c>f&lt;modId&gt;</c> modFiles array from a batch response; empty when the alias is absent.</summary>
    static List<(int fileId, string name, string? version, string category, long date)> FilesFromAlias(JsonElement data, string alias)
    {
        var list = new List<(int, string, string?, string, long)>();
        if (!data.TryGetProperty(alias, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var f in arr.EnumerateArray())
            list.Add((Int(f, "fileId"), Str(f, "name") ?? "", Str(f, "version"), Str(f, "category") ?? "", Long(f, "date")));
        return list;
    }

    /// <summary>Identify uploaded files by MD5 hash in bulk; match semantics in docs/architecture/nexus.md.</summary>
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

    // Core POST: the one place a Nexus call can throw, so the one place every failure turns into a returned message.
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

/// <summary>One Nexus requirement: ModId is the required mod's numeric Nexus id, or off-site when ExternalRequirement is set.</summary>
public sealed record NexusRequirement(string ModId, string ModName, string? Url, string? Notes, bool ExternalRequirement);

/// <summary>One uploaded file of a mod; Date is unix seconds and ChangelogText is empty when the author wrote none.</summary>
public sealed record NexusFile(
    int FileId, string Name, string? Version, string Category, long Date, string? Description, IReadOnlyList<string> ChangelogText);

/// <summary>Full detail for one mod plus its files; Version is the mod's header, which can lag the newest MAIN file.</summary>
public sealed record NexusModDetail(
    int ModId, string Name, string? Version, string? Summary, string? Description, string? Author, string Category,
    int Endorsements, int Downloads, string? UpdatedAt, string? CreatedAt, bool AdultContent, string Status,
    bool DirectDownloadEnabled, IReadOnlyList<NexusRequirement> NexusRequirements, IReadOnlyList<NexusFile> Files,
    IReadOnlyList<string> Tags);

/// <summary>Whether one installed file is still live on its mod's page, retired, withdrawn, or missing from it.</summary>
public enum FileVerdict { Live, Superseded, Missing, Removed }

/// <summary>One installed file's currency; Name/Version/Category are null only for Missing, per docs/architecture/nexus.md.</summary>
public sealed record InstalledFileCurrency(
    int FileId, string? Name, string? Version, string? Category, FileVerdict Verdict,
    string? NewestSameName, string? NewestSameVersion, long NewestSameDate);

/// <summary>The verdict for one mod in a batch file-level update check; what each one means is in docs/architecture/nexus.md.</summary>
public enum UpdateVerdict { Current, Outdated, FileGone, NoFileId, LatestOnly, NotFound, Error, FileRemoved }

/// <summary>One mod's batch update-check result; LiveMainCount above one is a multi-main page, pinned by `nexus-file-check-guard`.</summary>
public sealed record NexusUpdateStatus(
    int ModId, bool Found, string? Name, string? HeaderVersion, string? Installed, UpdateVerdict Verdict,
    IReadOnlyList<InstalledFileCurrency> Files, string? LatestMainVersion, long LatestMainDate, int LiveMainCount,
    string? Note = null);

/// <summary>One MD5-hash match: the Nexus file, its mod, its version and category, and the game id it belongs to.</summary>
public sealed record NexusFileHash(
    string Md5, string FileName, string FileType, long FileSize, int GameId, int ModFileId,
    int ModId, string? ModName, string? FileVersion, string? FileCategory);

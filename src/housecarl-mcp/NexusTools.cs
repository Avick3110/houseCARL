using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>The Nexus Mods read tools over <see cref="NexusClient"/>: search the catalog, look up one mod's detail,
/// requirements and newest MAIN file, check installed files for updates, and identify a file by hash. None of them
/// downloads or installs — that stays the mod manager's nxm handoff. They need an internet connection and fail
/// cleanly without one, and they do not touch the MO2 instance, so they work with no load order configured.</summary>
[McpServerToolType]
public static class NexusTools
{
    [McpServerTool(Name = ToolNames.NexusSearch, ReadOnly = true, Title = "Search Nexus Mods"),
     Description(
         "Search Nexus Mods for Skyrim Special Edition mods by name/keywords, WITHOUT opening a browser — houseCARL " +
         "queries the Nexus catalog directly and returns a ranked list. Each hit gives the mod name, Nexus mod id, " +
         "version, author, endorsement/download counts, category, last-updated date, a one-line summary, and the page " +
         "URL. Sorted by endorsements by default (sort= downloads | recent | name | relevance), optionally narrowed to a " +
         "category=, capped at limit= (default 10, max 50). READ-ONLY and needs an internet connection — houseCARL's " +
         "local load-order tools are unaffected if offline. Does NOT download or install anything: to install a result, " +
         "open its page and use Nexus's 'Mod Manager Download' button as usual — houseCARL reads Nexus, your mod manager " +
         "does the download. For full details (requirements and the newest MAIN file — the accurate latest version) of " +
         "one result, pass its id to " + ToolNames.NexusMod + ".")]
    public static Task<string> NexusSearch(
        NexusClient nexus,
        [Description("Words to search for in mod names, e.g. 'archery overhaul' or 'true storms'. Matched as a wildcard against Skyrim SE mod names.")]
            string query,
        [Description("Optional. Narrow to a Nexus category name, e.g. 'Audio', 'Armour', 'Gameplay', 'Patches'. Omit to search all categories.")]
            string? category = null,
        [Description("Optional. Result ordering: 'endorsements' (default, best-regarded first), 'downloads' (most popular), 'recent' (recently updated), 'name' (A-Z), or 'relevance'.")]
            string sort = "endorsements",
        [Description("Optional. Max results to return (default 10, max 50).")]
            int limit = 10,
        CancellationToken ct = default) => Guard.Tool(ToolNames.NexusSearch, async () =>
    {
        if (string.IsNullOrWhiteSpace(query))
            return "error: no search term. Pass query= some words to look for in mod names (e.g. 'archery overhaul').";
        if (limit <= 0) limit = 10;
        if (limit > 50) limit = 50;

        var sortField = MapSort(sort);
        if (sortField is null)
            return $"error: unknown sort '{sort}'. Use one of: endorsements, downloads, recent, name, relevance.";

        var (ok, error, result) = await nexus.SearchAsync(query.Trim(), category, sortField, limit, ct);
        if (!ok) return "error: " + error;
        return Render.Search(query.Trim(), category, sort, result!);
    }, ct);

    [McpServerTool(Name = ToolNames.NexusMod, ReadOnly = true, Title = "Look up a Nexus mod"),
     Description(
         "Look up ONE Skyrim Special Edition mod on Nexus by its numeric mod id (e.g. 12604) OR a pasted mod URL — " +
         "without opening a browser. Returns the mod's name, version, author, status, endorsement/download counts, " +
         "category, last-updated date, summary, whether direct download is disabled (manager-only), its Nexus " +
         "REQUIREMENTS (each required mod's name + id + notes, off-site deps flagged), and its newest MAIN file's " +
         "version — the accurate 'latest version', because a mod's own version header can lag its newest file. " +
         "Pass description=true to ALSO get the mod's full page write-up (what it does, how it works, usage, " +
         "recommended INI settings, compatibility/conflict notes), cleaned of Nexus markup to plain text — off by default because it can run " +
         "several KB. Pass files=true to list EVERY uploaded file (not just the newest MAIN) — each variant's name, " +
         "version, category, and date — the accurate way to pin the version of a specific FOMOD/modular variant (an " +
         "'SE' vs 'AE' main, an optional patch, a texture-size option) rather than the single newest-main summary. " +
         "Pass changelog=true to read the mod's per-version CHANGELOG (what changed in each release); combine with " +
         "since='<your installed version>' to show ONLY entries newer than what you have — the 'is this update worth " +
         "installing' delta. A mod whose author wrote no changelog is reported UNKNOWN, never 'no changes', so a silent " +
         "gap is never read as 'safe'. " +
         "READ-ONLY and needs an internet connection (local tools unaffected offline). Does NOT download or install — " +
         "use your mod manager's 'Mod Manager Download' for that. To find a mod by name first, use " + ToolNames.NexusSearch + ".")]
    public static Task<string> NexusMod(
        NexusClient nexus,
        [Description("The mod to look up: a numeric Nexus mod id (e.g. 12604) or a full mod URL (e.g. https://www.nexusmods.com/skyrimspecialedition/mods/12604).")]
            string mod,
        [Description("Optional. When true, also include the mod's FULL page description — the long write-up of what it " +
            "does, how it works, usage, recommended INI settings, and compatibility/conflict notes — cleaned of Nexus BBCode/HTML markup to plain " +
            "text (capped, with an explicit marker if truncated). Default false: the lookup returns the compact summary, " +
            "requirements, and latest version only, because the full description can run several KB. Set true when you " +
            "need the detail, e.g. comparing two mods or understanding how one works.")]
            bool description = false,
        [Description("Optional. When true, list ALL of the mod's uploaded files — every MAIN, UPDATE, OPTIONAL, " +
            "MISCELLANEOUS, and archived/old file — with each one's name, version, category, and upload date, grouped by " +
            "category. Default false: the lookup shows only the newest MAIN file. Set true to pin the version of a " +
            "specific variant (e.g. which 'main' is the AE build, an optional add-on's version) — the fix for modular/FOMOD " +
            "mods where the single newest-main line isn't enough.")]
            bool files = false,
        [Description("Optional. When true, include the mod's per-version CHANGELOG — each release's changelog lines, " +
            "newest first. Default false. Versions whose author wrote no changelog are reported as UNKNOWN (never 'no " +
            "changes'). Pair with since= to see only what's newer than your installed version.")]
            bool changelog = false,
        [Description("Optional. Your currently-installed version (e.g. '5.2SE', '6.9'). When changelog=true, limits the " +
            "changelog to releases uploaded AFTER the file matching this version — the 'what changed since I installed it' " +
            "delta. Matching is by upload DATE (robust), so if this exact version string isn't found among the files, the " +
            "tool says so and shows the full changelog rather than guessing (Q3). Ignored unless changelog=true.")]
            string? since = null,
        CancellationToken ct = default) => Guard.Tool(ToolNames.NexusMod, async () =>
    {
        var (modId, parseError) = ResolveModId(mod);
        if (parseError is not null) return "error: " + parseError;

        var (ok, error, detail) = await nexus.GetModAsync(modId, ct);
        if (!ok) return "error: " + error;
        return Render.Mod(detail!, description, files, changelog, since);
    }, ct);

    [McpServerTool(Name = ToolNames.NexusGraphql, ReadOnly = true, Title = "Run a raw Nexus GraphQL query"),
     Description(
         "The COMPLETENESS BACKSTOP behind houseCARL's curated Nexus tools: run a RAW read-only query against the Nexus " +
         "Mods public v2 GraphQL API (keyless), so any field the opinionated tools don't surface yet is never invisible. " +
         "PREFER " + ToolNames.NexusSearch + " / " + ToolNames.NexusMod + " / " + ToolNames.NexusCheckUpdates + " for the common lookups — " +
         "they render results with houseCARL's honest semantics (newest-file-vs-header version, changelog UNKNOWN-not-" +
         "empty, manager-only flags) that a raw dump loses. Reach for THIS only for a field or query they don't cover — " +
         "e.g. a mod's page tags, or other Mod/File metadata. Pass a GraphQL query string (and optional variables as a " +
         "JSON object); it returns the raw JSON data, pretty-printed and bounded. READ-ONLY: mutation/subscription is " +
         "refused, and the keyless endpoint cannot change anything regardless. Needs an internet connection (local tools " +
         "work offline). The Skyrim SE gameId is 1704. Example: query{ mod(modId:\"51614\", gameId:\"1704\"){ name tags{ name } } }")]
    public static Task<string> NexusGraphql(
        NexusClient nexus,
        [Description("The GraphQL query to run against Nexus's v2 endpoint. Read-only — a query{ ... } document; " +
            "mutation/subscription is refused. Inline arguments or use variables=. The Skyrim SE gameId is 1704. " +
            "Example: 'query{ mod(modId:\"51614\", gameId:\"1704\"){ name summary tags{ name } } }'.")]
            string query,
        [Description("Optional. GraphQL variables as a JSON OBJECT string, e.g. '{\"modId\":\"51614\"}'. Omit when the " +
            "query inlines its arguments.")]
            string? variables = null,
        CancellationToken ct = default) => Guard.Tool(ToolNames.NexusGraphql, async () =>
    {
        JsonElement? vars = null;
        if (!string.IsNullOrWhiteSpace(variables))
        {
            try { using var doc = JsonDocument.Parse(variables); vars = doc.RootElement.Clone(); }
            catch (Exception ex) { return $"error: variables= isn't valid JSON ({ex.Message}). Pass a JSON object like '{{\"modId\":\"51614\"}}'."; }
            if (vars.Value.ValueKind != JsonValueKind.Object)
                return $"error: variables= must be a JSON OBJECT (e.g. '{{\"modId\":\"51614\"}}'), not {vars.Value.ValueKind}.";
        }
        var (ok, error, data) = await nexus.RawQueryAsync(query, vars, ct);
        if (!ok) return "error: " + error;
        return Render.Graphql(data);
    }, ct);

    [McpServerTool(Name = ToolNames.NexusCheckUpdates, ReadOnly = true, Title = "Batch-check Nexus mods for updates (file-level)"),
     Description(
         "Check MANY Skyrim Special Edition mods for updates in ONE call — at the FILE level, without a browser or an API " +
         "key. The accurate question is 'is the exact FILE I installed still current?', NOT 'does my version match the " +
         "page's newest main' — a Nexus page hosts many independently-versioned files (patch hubs, ENB pages, Xtudo " +
         "mega-packs), so comparing your file to the page's single newest main is confidently WRONG for those. Pass each " +
         "mod as 'id#fileid' — the fileid MO2 recorded for what you installed, which " + ToolNames.UpdateStatus + " prints per " +
         "row as a 'verify:' token (several files installed from one page → 'id#fileid1#fileid2'). houseCARL resolves each " +
         "installed file to its live status and reports per mod: CURRENT (your file is still a live file on the page), " +
         "OUTDATED (your file was RETIRED to OLD_VERSION/ARCHIVED — it names the newest same-name file to grab), FILE-GONE " +
         "(your file is no longer on the page — hidden/deleted, a loud unknown), or not-found (wrong id / LE/other-game). " +
         "If you pass only 'id=version' with NO fileid (a FOMOD/manual install), it degrades LOUDLY to a best-effort " +
         "'no-fileid' note — never a confident verdict, because the mod-level compare lies for multi-file pages. Batched " +
         "(dozens of mods per call). READ-ONLY, needs an internet connection (local tools work offline). Does NOT download " +
         "or update anything — it is a REPORT. Build the list cheaply with " + ToolNames.UpdateStatus + " (reads MO2's own local " +
         "cache, no network, and prints each mod's fileid), then " + ToolNames.NexusMod + " changelog=true on anything OUTDATED " +
         "to see what actually changed.")]
    public static Task<string> NexusCheckUpdates(
        NexusClient nexus,
        [Description("The mods to check — one entry per mod, separated by commas or newlines. Preferred (FILE-LEVEL) form: " +
            "'id#fileid', the mod id then '#' then the Nexus file id MO2 recorded for what you installed (" + ToolNames.UpdateStatus + " " +
            "prints this as the 'verify:' token); if you installed several files from one page, add more with '#': " +
            "'126608#533265, 99786#585300#585301'. Without a fileid you can pass 'id=version' (or 'id version') for a LOUD " +
            "best-effort no-fileid note, or a bare 'id' for its latest version only — e.g. '12604=6.9, 3863'. The " +
            "intra-fileid separator is '#', because ',' separates entries. Non-numeric junk is skipped and listed back to you.")]
            string mods,
        CancellationToken ct = default) => Guard.Tool(ToolNames.NexusCheckUpdates, async () =>
    {
        var (pairs, bad) = ParseUpdatePairs(mods);
        if (pairs.Count == 0)
            return "error: no mod ids found. Pass entries like '12604=6.9, 266=4.3.8a' (id, optional =installed version), "
                 + "comma/newline separated." + (bad.Count > 0 ? " Unreadable: " + string.Join(", ", bad) : "");

        var (ok, error, results) = await nexus.CheckUpdatesAsync(pairs, ct);
        if (!ok) return "error: " + error;
        return Render.Updates(results, bad);
    }, ct);

    /// <summary>Parse the check-updates input: entries separated by comma, newline or semicolon, in one of two forms —
    /// file-level <c>&lt;modid&gt;#&lt;fileid&gt;[#&lt;fileid&gt;…]</c>, or <c>&lt;modid&gt;[=&lt;version&gt;]</c> for
    /// the no-fileid FOMOD/manual fallback and the bare latest-only case. Returns
    /// (modId, installedVersion or null, fileIds) triples plus the tokens it could not read, which are surfaced back
    /// rather than dropped. The separator between fileids is '#', because ',' already splits entries.</summary>
    internal static (List<(int modId, string? installed, IReadOnlyList<int> fileIds)> pairs, List<string> bad) ParseUpdatePairs(string input)
    {
        var pairs = new List<(int, string?, IReadOnlyList<int>)>();
        var bad = new List<string>();
        if (string.IsNullOrWhiteSpace(input)) return (pairs, bad);
        foreach (var raw in input.Split(new[] { ',', '\n', ';', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = raw.Trim();
            if (entry.Length == 0) continue;

            // file-level form: <modid>#<fileid>[#<fileid>...]
            int hash = entry.IndexOf('#');
            if (hash >= 0)
            {
                if (!int.TryParse(entry[..hash].Trim(), out var mid) || mid <= 0) { bad.Add(entry); continue; }
                var fids = new List<int>();
                foreach (var tok in entry[(hash + 1)..].Split('#', StringSplitOptions.RemoveEmptyEntries))
                    if (int.TryParse(tok.Trim(), out var fid) && fid > 0) fids.Add(fid);
                if (fids.Count == 0) { bad.Add(entry); continue; }   // a '#' with no readable fileid: surface it, don't downgrade to a no-fileid verdict
                pairs.Add((mid, null, fids));
                continue;
            }

            // version / bare form: <modid>[ (= | : | space) <version> ]
            var m = Regex.Match(entry, @"^(\d+)\s*[:=\s]?\s*(.*)$");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var id) && id > 0)
            {
                var ver = m.Groups[2].Value.Trim();
                pairs.Add((id, ver.Length == 0 ? null : ver, Array.Empty<int>()));
            }
            else bad.Add(entry);
        }
        return (pairs, bad);
    }

    [McpServerTool(Name = ToolNames.NexusIdentify, ReadOnly = true, Title = "Identify a file on Nexus by MD5"),
     Description(
         "Identify which Nexus mod (and which uploaded file) a file came from, by its MD5 hash — without a browser or an " +
         "API key. Give one or more 32-char MD5 hashes; houseCARL returns, per hash, the matching mod (name + id) and the " +
         "file's name/version/category/size — or 'no match' when no Nexus file has that hash (a hand-edited, repacked, or " +
         "non-Nexus file). Matching is across ALL games, so a hash belonging to a non-Skyrim-SE file is FLAGGED as such " +
         "rather than mis-attributed to a same-hash SSE mod (Q3). Use it to trace a mystery loose file back to its source " +
         "mod. READ-ONLY, needs an internet connection. To get a file's MD5 first, hash it locally (e.g. PowerShell " +
         "Get-FileHash -Algorithm MD5).")]
    public static Task<string> NexusIdentify(
        NexusClient nexus,
        [Description("One or more MD5 hashes (32 hex characters each), separated by commas, spaces, or newlines. " +
            "Case-insensitive. Non-hash junk is skipped and listed back to you.")]
            string md5,
        CancellationToken ct = default) => Guard.Tool(ToolNames.NexusIdentify, async () =>
    {
        var (hashes, bad) = ParseHashes(md5);
        if (hashes.Count == 0)
            return "error: no valid MD5 hashes found — each must be 32 hex characters."
                 + (bad.Count > 0 ? " Unreadable: " + string.Join(", ", bad) : "");

        var (ok, error, results) = await nexus.IdentifyByHashAsync(hashes, ct);
        if (!ok) return "error: " + error;
        return Render.Identify(hashes, results, bad);
    }, ct);

    /// <summary>Parse the identify input into normalized lowercase 32-hex MD5 hashes, de-duplicated, plus the tokens
    /// that were not valid hashes, which are surfaced back rather than dropped.</summary>
    static (List<string> hashes, List<string> bad) ParseHashes(string input)
    {
        var hashes = new List<string>();
        var bad = new List<string>();
        if (string.IsNullOrWhiteSpace(input)) return (hashes, bad);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in input.Split(new[] { ',', '\n', ';', '\r', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var h = raw.Trim().ToLowerInvariant();
            if (Regex.IsMatch(h, "^[0-9a-f]{32}$")) { if (seen.Add(h)) hashes.Add(h); }
            else bad.Add(raw.Trim());
        }
        return (hashes, bad);
    }

    /// <summary>Map a friendly sort word to a ModsSort field name; null if unrecognised, which the tool reports.</summary>
    static string? MapSort(string s) => s.Trim().ToLowerInvariant() switch
    {
        "endorsements" or "endorsed" or "best" or "top" => "endorsements",
        "downloads" or "popular" or "most_downloaded" => "downloads",
        "recent" or "updated" or "latest" or "newest" => "updatedAt",
        "name" or "alphabetical" or "az" => "name",
        "relevance" or "relevant" => "relevance",
        _ => null,
    };

    /// <summary>Resolve the user's input to an SSE mod id from a bare numeric id or a Nexus mod URL. A URL for a
    /// different game (fallout4, skyrim, starfield) is rejected rather than resolving to a same-numbered Skyrim SE
    /// mod. Returns (modId, error), exactly one of which is set.</summary>
    static (int modId, string? error) ResolveModId(string s)
    {
        s = s.Trim();
        if (int.TryParse(s, out var id) && id > 0) return (id, null);

        // A Nexus mod URL carries the game domain right before /mods/<n> (optionally behind a 'games/' segment).
        var url = Regex.Match(s, @"nexusmods\.com/(?:games/)?([^/]+)/mods/(\d+)", RegexOptions.IgnoreCase);
        if (url.Success)
        {
            var game = url.Groups[1].Value.ToLowerInvariant();
            if (game != "skyrimspecialedition")
                return (0, $"that's a '{game}' Nexus URL — houseCARL is Skyrim Special Edition only. (Id {url.Groups[2].Value} "
                    + "on SSE would be a different mod, so I won't guess.) Pass an SSE mod id or a skyrimspecialedition URL.");
            if (!int.TryParse(url.Groups[2].Value, out var mid) || mid <= 0)
                return (0, $"'{url.Groups[2].Value}' isn't a valid mod id.");
            return (mid, null);
        }

        // Fallback: a bare '/mods/<n>' with no identifiable game, such as a partial paste — treat it as an SSE id.
        var loose = Regex.Match(s, @"/mods/(\d+)", RegexOptions.IgnoreCase);
        if (loose.Success && int.TryParse(loose.Groups[1].Value, out id) && id > 0) return (id, null);

        return (0, $"couldn't read a mod id from '{s}'. Pass a numeric Nexus mod id (e.g. 12604) or a Skyrim SE mod URL "
            + "(e.g. https://www.nexusmods.com/skyrimspecialedition/mods/12604).");
    }
}

/// <summary>Render the Nexus result records to readable text. Every mod ends with its page URL, so the user can click
/// through to the manager-download button — the install handoff is left to MO2.</summary>
static class Render
{
    const string ModUrlBase = "https://www.nexusmods.com/skyrimspecialedition/mods/";

    /// <summary>Max characters of cleaned description text to emit — enough for a typical full description, bounded so
    /// a giant page cannot dominate the response. An over-length body is cut at a word boundary with an explicit
    /// marker.</summary>
    const int DescriptionCap = 6000;

    /// <summary>Render a raw GraphQL <c>data</c> payload for the backstop tool: pretty-printed JSON, bounded with an
    /// explicit marker. Deliberately unopinionated — the backstop exists to show exactly what the graph returned,
    /// not a curated view of it.</summary>
    public static string Graphql(JsonElement data)
    {
        var json = JsonSerializer.Serialize(data, GraphqlJson);
        if (json.Length > GraphqlCap)
            return json.Substring(0, GraphqlCap)
                + $"\n\n… [output truncated at {GraphqlCap:N0} chars — narrow the query (fewer fields, a smaller page/list) to see the rest]";
        return json;
    }
    const int GraphqlCap = 40000;
    // UnsafeRelaxedJsonEscaping because the backstop must show exactly what the graph returned: the default encoder
    // escapes '+', '&', '<', '>' and all non-ASCII to \uXXXX, mangling a version like '1.0+SE' or an accented author
    // name. "Unsafe" here means HTML/JS injection sinks, and this output is read as terminal text, never re-parsed.
    static readonly JsonSerializerOptions GraphqlJson = new()
        { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Search(string term, string? category, string sort, NexusSearchResult r)
    {
        var sb = new StringBuilder();
        sb.Append("Nexus search: \"").Append(term).Append('"');
        if (!string.IsNullOrWhiteSpace(category)) sb.Append(" in '").Append(category).Append('\'');
        sb.Append(" — ").Append(r.TotalCount.ToString("N0")).Append(" match(es), by ").Append(sort)
          .Append(", showing ").Append(r.Hits.Count).Append(':');
        if (r.Hits.Count == 0)
        {
            sb.Append("\n(none)");
            // category= is a server-side case-sensitive EQUALS, so zero hits with a category filter is as likely a
            // casing or name miss as a real zero, and the response says so.
            if (!string.IsNullOrWhiteSpace(category))
                sb.Append("\nnote: category matching is EXACT and case-sensitive on Nexus's side ('Armour', not 'armour') — ")
                  .Append("0 matches with a category filter may mean the category name didn't match, not that no mods exist. ")
                  .Append("Retry without category= or with the exact Nexus category name.");
            return sb.ToString();
        }

        foreach (var h in r.Hits)
        {
            sb.Append("\n\n• ").Append(h.Name).Append("  [id ").Append(h.ModId).Append(']');
            if (h.AdultContent) sb.Append("  (ADULT)");
            sb.Append("\n  v").Append(h.Version ?? "?").Append(" · by ").Append(h.Author ?? "?")
              .Append(" · ").Append(h.Endorsements.ToString("N0")).Append(" endorsements · ")
              .Append(h.Downloads.ToString("N0")).Append(" downloads");
            if (!string.IsNullOrWhiteSpace(h.Category)) sb.Append(" · ").Append(h.Category);
            if (!string.IsNullOrWhiteSpace(h.UpdatedAt)) sb.Append(" · upd ").Append(Day(h.UpdatedAt!));
            if (!string.IsNullOrWhiteSpace(h.Summary)) sb.Append("\n  ").Append(OneLine(h.Summary!, 160));
            sb.Append("\n  ").Append(ModUrlBase).Append(h.ModId);
        }
        sb.Append("\n\n(To install one: open its page and use Nexus's \"Mod Manager Download\" — houseCARL reads Nexus, ")
          .Append("your mod manager does the download. Pass an id to " + ToolNames.NexusMod + " for requirements + latest version.)");
        return sb.ToString();
    }

    public static string Mod(NexusModDetail m, bool includeDescription = false, bool includeFiles = false,
                             bool includeChangelog = false, string? since = null)
    {
        var sb = new StringBuilder();
        sb.Append(m.Name).Append("  [id ").Append(m.ModId).Append(']');
        sb.Append("\nversion ").Append(m.Version ?? "?").Append(" · by ").Append(m.Author ?? "?")
          .Append(" · ").Append(m.Status);
        if (m.AdultContent) sb.Append(" · ADULT");
        sb.Append("\n").Append(m.Endorsements.ToString("N0")).Append(" endorsements · ")
          .Append(m.Downloads.ToString("N0")).Append(" downloads");
        if (!string.IsNullOrWhiteSpace(m.Category)) sb.Append(" · ").Append(m.Category);
        if (!string.IsNullOrWhiteSpace(m.UpdatedAt)) sb.Append(" · updated ").Append(Day(m.UpdatedAt!));
        if (m.Tags.Count > 0) sb.Append("\ntags: ").Append(string.Join(", ", m.Tags));
        if (!m.DirectDownloadEnabled)
            sb.Append("\nnote: the author disabled direct download — manager (nxm) download only.");
        if (!string.IsNullOrWhiteSpace(m.Summary)) sb.Append("\n\n").Append(m.Summary);

        // The accurate latest version is the newest MAIN file, not the mod's version header, which can lag. A mod
        // with no main file at all says so, or the possibly-lagging header would be left as the only signal.
        NexusFile? main = null;
        foreach (var f in m.Files)
            if (f.Category == "MAIN" && (main is null || f.Date > main.Date)) main = f;
        if (main is not null)
            sb.Append("\n\nlatest MAIN file: ").Append(main.Name).Append(" v").Append(main.Version ?? "?")
              .Append(" (").Append(Day(main.Date)).Append(')');
        else
            sb.Append("\n\nno MAIN file listed (files may all be optional/misc) — the version header above is the only version signal and can lag.");

        if (m.NexusRequirements.Count > 0)
        {
            sb.Append("\n\nrequires (").Append(m.NexusRequirements.Count).Append("):");
            foreach (var req in m.NexusRequirements)
            {
                sb.Append("\n  - ").Append(req.ModName);
                if (req.ExternalRequirement) sb.Append("  (off-site)");
                else if (!string.IsNullOrWhiteSpace(req.ModId)) sb.Append("  [id ").Append(req.ModId).Append(']');
                if (!string.IsNullOrWhiteSpace(req.Notes)) sb.Append(" — ").Append(OneLine(req.Notes!, 120));
            }
        }
        else sb.Append("\n\nno Nexus requirements listed.");

        // The full file list is opt-in, since a big mod has dozens of archived files. It is what pins a specific
        // variant's version on a modular or FOMOD mod, where the single newest-MAIN line cannot.
        if (includeFiles) AppendFiles(sb, m.Files);

        // The per-version changelog is opt-in; since= limits it to releases newer than the installed version. Files
        // with no changelog lines are reported unknown, because a missing changelog must not read as "nothing changed".
        if (includeChangelog) AppendChangelog(sb, m.Files, since);

        // The full description is opt-in, since it can run several KB of BBCode and HTML. A page with none says so
        // rather than omitting the section, which would read as a failure to fetch it.
        if (includeDescription)
        {
            var body = string.IsNullOrWhiteSpace(m.Description) ? null : StripMarkup(m.Description!, DescriptionCap);
            sb.Append("\n\n── description ──\n")
              .Append(string.IsNullOrEmpty(body) ? "(this mod's page has no description text.)" : body);
        }

        sb.Append("\n\n").Append(ModUrlBase).Append(m.ModId);
        return sb.ToString();
    }

    /// <summary>List every uploaded file, grouped by category (MAIN, UPDATE, OPTIONAL, MISCELLANEOUS, other,
    /// OLD_VERSION, ARCHIVED) and newest-first within each group. The section is bounded with an explicit cut, so a
    /// mod with hundreds of archived files cannot dominate the response. This is the per-variant version detail the
    /// newest-MAIN summary cannot give.</summary>
    static void AppendFiles(StringBuilder sb, IReadOnlyList<NexusFile> files)
    {
        sb.Append("\n\n── files (").Append(files.Count).Append(") ──");
        if (files.Count == 0) { sb.Append("\n(this mod has no uploaded files listed.)"); return; }

        // Display order for the categories Nexus emits. An unrecognised category sorts between MISCELLANEOUS and
        // OLD_VERSION rather than being dropped, so a category Nexus adds later is still shown.
        static int Order(string c) => c switch
        {
            "MAIN" => 0, "UPDATE" => 1, "OPTIONAL" => 2, "MISCELLANEOUS" => 3,
            "OLD_VERSION" => 5, "ARCHIVED" => 6, _ => 4,
        };
        const int Cap = 6000;   // bounds a pathological archived list. Measured as a per-section delta, not against
        int start = sb.Length;  // total sb.Length, or files=true plus changelog=true would let files starve the changelog.
        string? group = null;
        int shown = 0;
        foreach (var f in files.OrderBy(f => Order(f.Category)).ThenByDescending(f => f.Date))
        {
            if (sb.Length - start >= Cap) { sb.Append("\n  ... [").Append(files.Count - shown).Append(" more file(s) omitted — raise the ask or open the page.]"); break; }
            if (!string.Equals(f.Category, group, StringComparison.Ordinal))
            {
                group = f.Category;
                sb.Append("\n ").Append(string.IsNullOrWhiteSpace(group) ? "(uncategorised)" : group).Append(':');
            }
            sb.Append("\n  - ").Append(f.Name).Append("  v").Append(f.Version ?? "?")
              .Append("  (").Append(f.Date > 0 ? Day(f.Date) : "?").Append(')');
            shown++;
        }
    }

    /// <summary>Render MD5-identify results: one block per requested hash, so a no-match is shown explicitly rather
    /// than merely absent, each listing the matched mod and file or saying no Nexus file matches. A match on a
    /// non-Skyrim-SE game is flagged. Unreadable tokens are listed at the end.</summary>
    public static string Identify(IReadOnlyList<string> requested, IReadOnlyList<NexusFileHash> matches, IReadOnlyList<string> unreadable)
    {
        var byHash = matches.GroupBy(m => m.Md5, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        sb.Append("identify — ").Append(requested.Count).Append(" hash(es):");
        foreach (var h in requested)
        {
            sb.Append("\n\n").Append(h);
            if (!byHash.TryGetValue(h, out var ms) || ms.Count == 0)
            {
                sb.Append("\n  no Nexus file matches this md5 — a hand-edited/repacked file, or not from Nexus.");
                continue;
            }
            foreach (var m in ms)
            {
                if (m.GameId != NexusClient.SkyrimSeGameId)
                    sb.Append("\n  [!] this hash matches a NON-Skyrim-SE file (Nexus game id ").Append(m.GameId)
                      .Append(") — not part of your SSE load order:");
                sb.Append("\n  ").Append(m.ModName ?? "?").Append("  [id ").Append(m.ModId).Append(']');
                sb.Append("\n    file: ").Append(m.FileName);
                if (!string.IsNullOrWhiteSpace(m.FileVersion)) sb.Append("  v").Append(m.FileVersion);
                if (!string.IsNullOrWhiteSpace(m.FileCategory)) sb.Append("  [").Append(m.FileCategory).Append(']');
                if (m.FileSize > 0) sb.Append("  ").Append(HumanSize(m.FileSize));
                if (m.ModId > 0 && m.GameId == NexusClient.SkyrimSeGameId)
                    sb.Append("\n    ").Append(ModUrlBase).Append(m.ModId);
            }
        }
        if (unreadable.Count > 0) sb.Append("\n\nnot valid md5s (skipped): ").Append(string.Join(", ", unreadable));
        return sb.ToString();
    }

    /// <summary>Bytes as a compact human size (B, KB, MB) to one decimal.</summary>
    static string HumanSize(long bytes)
    {
        if (bytes >= 1L << 20) return (bytes / (double)(1 << 20)).ToString("0.#") + " MB";
        if (bytes >= 1L << 10) return (bytes / (double)(1 << 10)).ToString("0.#") + " KB";
        return bytes + " B";
    }

    /// <summary>Render a batch file-level update check: a one-line summary, then the mods grouped by verdict with the
    /// actionable ones first — outdated, file-gone, no-fileid, current, latest-only, not-found, error. Each row lists
    /// its installed files with the live, retired or missing verdict, and a retired file names its same-name
    /// replacement. Unreadable input tokens are listed back at the end.</summary>
    public static string Updates(IReadOnlyList<NexusUpdateStatus> results, IReadOnlyList<string> unreadable)
    {
        var sb = new StringBuilder();
        int outdated = results.Count(r => r.Verdict == UpdateVerdict.Outdated);
        int current = results.Count(r => r.Verdict == UpdateVerdict.Current);
        int fileGone = results.Count(r => r.Verdict == UpdateVerdict.FileGone);
        int noFileId = results.Count(r => r.Verdict == UpdateVerdict.NoFileId);
        int latest = results.Count(r => r.Verdict == UpdateVerdict.LatestOnly);
        int notFound = results.Count(r => r.Verdict == UpdateVerdict.NotFound);
        int errored = results.Count(r => r.Verdict == UpdateVerdict.Error);

        sb.Append("update check (file-level) — ").Append(results.Count).Append(" mod(s): ")
          .Append(outdated).Append(" outdated · ").Append(current).Append(" current · ")
          .Append(fileGone).Append(" file-gone · ").Append(noFileId).Append(" no-fileid · ")
          .Append(latest).Append(" latest-only · ").Append(notFound).Append(" not-found");
        if (errored > 0) sb.Append(" · ").Append(errored).Append(" error");

        AppendUpdateGroup(sb, "OUTDATED — an installed file was RETIRED to OLD/ARCHIVED (grab the newest same-name file)", results, UpdateVerdict.Outdated);
        AppendUpdateGroup(sb, "FILE GONE — your installed file is no longer on the page (hidden/deleted) — verify by hand, don't assume", results, UpdateVerdict.FileGone);
        AppendUpdateGroup(sb, "NO FILEID — couldn't verify at file level (FOMOD/manual install); best-effort only, not a verdict", results, UpdateVerdict.NoFileId);
        AppendUpdateGroup(sb, "current — the exact file you installed is still a live file on the page", results, UpdateVerdict.Current);
        AppendUpdateGroup(sb, "latest version (no installed version/fileid was given to compare)", results, UpdateVerdict.LatestOnly);
        AppendUpdateGroup(sb, "not found on Skyrim SE (wrong id, an LE/other-game mod, or a hidden/deleted page)", results, UpdateVerdict.NotFound);
        AppendUpdateGroup(sb, "check FAILED (surface, don't assume current)", results, UpdateVerdict.Error);

        if (unreadable.Count > 0)
            sb.Append("\n\nunreadable entries (skipped): ").Append(string.Join(", ", unreadable));
        return sb.ToString();
    }

    static void AppendUpdateGroup(StringBuilder sb, string label, IReadOnlyList<NexusUpdateStatus> all, UpdateVerdict v)
    {
        var rows = all.Where(r => r.Verdict == v).ToList();
        if (rows.Count == 0) return;
        sb.Append("\n\n").Append(label).Append(" (").Append(rows.Count).Append("):");
        foreach (var r in rows)
        {
            sb.Append("\n  [").Append(r.ModId).Append("] ").Append(r.Name ?? "?");
            switch (r.Verdict)
            {
                case UpdateVerdict.Outdated:
                case UpdateVerdict.FileGone:
                case UpdateVerdict.Current:
                    foreach (var f in r.Files) AppendFileCurrency(sb, f);
                    break;
                case UpdateVerdict.NoFileId:
                    sb.Append(" — installed v").Append(r.Installed ?? "?");
                    if (r.LiveMainCount == 1)
                        sb.Append("; newest MAIN v").Append(r.LatestMainVersion ?? "?")
                          .Append(r.LatestMainDate > 0 ? " (" + Day(r.LatestMainDate) + ")" : "")
                          .Append(" — VERSION compare only (single-main page; no fileid to verify the exact file)");
                    else if (r.LiveMainCount > 1)
                        sb.Append("; this page has ").Append(r.LiveMainCount)
                          .Append(" current MAIN files — can't tell which you have without a fileid (open the page)");
                    else
                        sb.Append("; no MAIN file listed — can't verify at file level without a fileid");
                    break;
                case UpdateVerdict.LatestOnly:
                    sb.Append(" — newest MAIN v").Append(r.LatestMainVersion ?? "?");
                    if (r.LatestMainDate > 0) sb.Append(" (").Append(Day(r.LatestMainDate)).Append(')');
                    if (r.LiveMainCount > 1) sb.Append(" [+").Append(r.LiveMainCount - 1).Append(" other current MAIN file(s)]");
                    break;
                case UpdateVerdict.Error:
                    sb.Append(" — ").Append(r.Note ?? "check failed");
                    break;
                case UpdateVerdict.NotFound:
                    break;   // the group label already says it
            }
        }
    }

    /// <summary>Render one installed file's currency line under its mod: its id, then — unless the file is gone — its
    /// name, version, category and live-or-retired verdict. A retired file names the newest same-name replacement, or
    /// says there is none. The category is always shown, so an unfamiliar Nexus category stays visible.</summary>
    static void AppendFileCurrency(StringBuilder sb, InstalledFileCurrency f)
    {
        sb.Append("\n      · file #").Append(f.FileId);
        if (f.Verdict == FileVerdict.Missing)
        {
            sb.Append(" — not on the page anymore (hidden/deleted; can't determine currency)");
            return;
        }
        sb.Append(" '").Append(f.Name ?? "?").Append("' v").Append(f.Version ?? "?").Append(" [").Append(f.Category ?? "?").Append(']');
        if (f.Verdict == FileVerdict.Live) sb.Append(" — current");
        else
        {
            sb.Append(" — RETIRED");
            if (f.NewestSameName is not null)
                sb.Append("; newest '").Append(f.NewestSameName).Append("' v").Append(f.NewestSameVersion ?? "?")
                  .Append(f.NewestSameDate > 0 ? " (" + Day(f.NewestSameDate) + ")" : "");
            else
                sb.Append("; no current file with that exact name — open the page to pick the replacement");
        }
    }

    /// <summary>The per-version changelog, newest release first. A release's changelog can sit on any of its files — a
    /// MAIN, an OLD_VERSION, an ARCHIVED copy — so the lines are gathered per version and ordered by the newest upload
    /// date in the group. <paramref name="since"/> keeps releases after the installed version's upload date: dates are
    /// monotonic where freeform version strings ('5.2SE', '6.9') are not comparable. A version with no changelog lines
    /// is reported unknown rather than omitted, and the section is bounded with an explicit cut.</summary>
    static void AppendChangelog(StringBuilder sb, IReadOnlyList<NexusFile> files, string? since)
    {
        var byVersion = new Dictionary<string, (long date, List<string> lines)>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();     // first-seen versions: a stable base order before the date sort
        foreach (var f in files)
        {
            var v = f.Version?.Trim();
            if (string.IsNullOrEmpty(v)) continue;   // a file with no version isn't a release
            if (!byVersion.TryGetValue(v, out var agg)) { agg = (0L, new List<string>()); order.Add(v); }
            if (f.Date > agg.date) agg.date = f.Date;
            foreach (var line in f.ChangelogText) if (!agg.lines.Contains(line)) agg.lines.Add(line);
            byVersion[v] = agg;
        }

        sb.Append("\n\n── changelog ──");
        if (byVersion.Count == 0) { sb.Append("\n(this mod has no versioned files to read a changelog from.)"); return; }

        var versions = order.OrderByDescending(v => byVersion[v].date).ToList();

        // since= keeps only releases uploaded after the installed version's file. If that version is not among the
        // uploads, say so and show the whole changelog rather than guessing a cutoff.
        if (!string.IsNullOrWhiteSpace(since))
        {
            var key = since.Trim();
            if (byVersion.TryGetValue(key, out var found))
            {
                var newer = versions.Where(v => byVersion[v].date > found.date).ToList();
                if (newer.Count == 0) { sb.Append("\nyou're on v").Append(key).Append(" — no newer release on Nexus (you're current)."); return; }
                sb.Append("\n(only releases newer than your v").Append(key).Append(")");
                versions = newer;
            }
            else sb.Append("\nnote: no file at version '").Append(key)
                    .Append("' among this mod's uploads — showing the full changelog rather than guessing a delta.");
        }

        const int Cap = 6000;
        int start = sb.Length;   // per-section delta, so a big file list can't blank the changelog
        int shown = 0;
        foreach (var v in versions)
        {
            if (sb.Length - start >= Cap) { sb.Append("\n  ... [").Append(versions.Count - shown).Append(" older release(s) omitted — narrow with since=, or open the page.]"); break; }
            var (date, lines) = byVersion[v];
            sb.Append("\n\nv").Append(v);
            if (date > 0) sb.Append("  (").Append(Day(date)).Append(')');
            sb.Append(':');
            if (lines.Count == 0) sb.Append("\n  (no changelog for this version — UNKNOWN, not necessarily unchanged)");
            else foreach (var line in lines) sb.Append("\n  - ").Append(OneLine(line, 300));
            shown++;
        }
    }

    /// <summary>Substring(0, n) that never splits a surrogate pair: an astral character (emoji, CJK extension B and
    /// beyond) is two UTF-16 chars, so a raw clamp can leave a lone high surrogate at the cut. If the char just before
    /// the cut is a high surrogate, back up one so the orphaned half is dropped.</summary>
    static string ClampChars(string s, int n)
    {
        if (s.Length <= n) return s;
        if (n > 0 && char.IsHighSurrogate(s[n - 1])) n--;
        return s[..n];
    }

    /// <summary>Collapse whitespace and cap a blurb to n chars with an ellipsis.</summary>
    internal static string OneLine(string s, int n)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Length <= n ? s : ClampChars(s, n).TrimEnd() + "…";
    }

    /// <summary>Turn a Nexus description — BBCode interleaved with HTML, e.g. "[size=5][b]…[/b][/size]&lt;br /&gt;" —
    /// into readable plain text: drop image and video embeds along with their inner text, which is a bare URL, unwrap
    /// [url=…]label[/url] to its label, turn list markers and HTML block and break tags into newlines, strip every
    /// remaining BBCode and HTML tag while keeping inner text, decode the HTML entities that actually appear, collapse
    /// runaway whitespace, and cap the result with an explicit truncation marker.</summary>
    internal static string StripMarkup(string raw, int cap)
    {
        const RegexOptions IC = RegexOptions.IgnoreCase;
        // Bound the input before any regex: the embed and [url] cleaners use a lazy `.*?` that backtracks quadratically
        // on many unclosed openers in author text, so a malformed page could hang the call for seconds. DescriptionCap
        // is too late — it only trims the cleaned output. cap*4 leaves headroom for a real description.
        var s = ClampChars(raw, cap * 4);

        // Embeds: remove the tag and its inner content, which is a bare URL or id. [img]…[/img], [youtube]…[/youtube].
        s = Regex.Replace(s, @"\[(img|youtube|video|media|embed)\b[^\]]*\].*?\[/\1\]", " ", IC | RegexOptions.Singleline);
        // Links: keep the human label, drop the target. [url=…]label[/url] or [url]label[/url].
        s = Regex.Replace(s, @"\[url\b[^\]]*\](.*?)\[/url\]", "$1", IC | RegexOptions.Singleline);
        // List items: [*] opens an item and becomes a bullet; some BBCode dialects also emit a [/*] close, which is
        // dropped. Neither is letter-led, so the general tag strip below would not catch them.
        s = Regex.Replace(s, @"\[\*\]", "\n• ", IC);
        s = Regex.Replace(s, @"\[/\*\]", "", IC);
        // Every remaining BBCode tag: [tag], [tag=value], [/tag] — keep inner text.
        s = Regex.Replace(s, @"\[/?[a-z][a-z0-9]*(=[^\]]*)?\]", "", IC);

        // HTML: line breaks and block boundaries → newlines, then strip all other tags.
        s = Regex.Replace(s, @"<\s*br\s*/?>", "\n", IC);
        s = Regex.Replace(s, @"<\s*/?\s*(p|div|li|ul|ol|h[1-6]|tr|table)\b[^>]*>", "\n", IC);
        s = Regex.Replace(s, @"<[^>]+>", "", RegexOptions.Singleline);

        // The entities that show up in Nexus descriptions. &amp; must be decoded LAST: it produces a literal '&', the
        // entity-introducing character, so decoding it first would let "&amp;lt;" become "&lt;" and then "<" — a
        // double-decode of text the author meant to show literally.
        s = s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
             .Replace("&#39;", "'").Replace("&apos;", "'").Replace("&nbsp;", " ")
             .Replace("&amp;", "&");

        // Strip zero-width and BOM characters authors paste in, which render as stray glyphs; normalise no-break spaces.
        s = s.Replace("\uFEFF", "").Replace("\u200B", "").Replace("\u200C", "").Replace("\u200D", "").Replace('\u00A0', ' ');

        // Whitespace: normalise newlines, collapse space runs, trim line edges, cap blank-line runs.
        s = s.Replace("\r\n", "\n").Replace('\r', '\n');
        s = Regex.Replace(s, @"[ \t]+", " ");
        s = Regex.Replace(s, @" *\n *", "\n");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        s = s.Trim();

        // Cap with an explicit marker, backing up to a nearby word boundary so the text is not cut mid-word.
        if (s.Length > cap)
        {
            var cut = ClampChars(s, cap);
            var sp = cut.LastIndexOf(' ');
            if (sp > cap - 200) cut = cut[..sp];
            s = cut.TrimEnd() + "\n…(description truncated — full text on the mod page.)";
        }
        return s;
    }

    /// <summary>An ISO-8601 datetime as just the date (yyyy-MM-dd).</summary>
    static string Day(string iso) => iso.Length >= 10 ? iso[..10] : iso;

    /// <summary>Unix seconds as yyyy-MM-dd.</summary>
    static string Day(long unixSeconds)
    {
        try { return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToString("yyyy-MM-dd"); }
        catch { return unixSeconds.ToString(); }
    }
}

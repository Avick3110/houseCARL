using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>
/// houseCARL's Nexus Mods read tools — the QOL layer that lets houseCARL answer Nexus questions DIRECTLY instead of
/// spinning up a browser to scrape a mod page. Two read-only tools over <see cref="NexusClient"/> (the public v2 GraphQL
/// read API, keyless): search the catalog, and look up one mod's detail + requirements + files. Neither downloads or
/// installs — that stays the mod manager's nxm handoff. Both need an internet connection and fail LOUD/clean when there
/// isn't one (Q3); they do NOT touch the MO2 instance, so they work even when houseCARL has no load order configured.
/// </summary>
[McpServerToolType]
public static class NexusTools
{
    [McpServerTool(Name = "housecarl_nexus_search", ReadOnly = true, Title = "Search Nexus Mods"),
     Description(
         "Search Nexus Mods for Skyrim Special Edition mods by name/keywords, WITHOUT opening a browser — houseCARL " +
         "queries the Nexus catalog directly and returns a ranked list. Each hit gives the mod name, Nexus mod id, " +
         "version, author, endorsement/download counts, category, last-updated date, a one-line summary, and the page " +
         "URL. Sorted by endorsements by default (sort= downloads | recent | name | relevance), optionally narrowed to a " +
         "category=, capped at limit= (default 10, max 50). READ-ONLY and needs an internet connection — houseCARL's " +
         "local load-order tools are unaffected if offline. Does NOT download or install anything: to install a result, " +
         "open its page and use Nexus's 'Mod Manager Download' button as usual — houseCARL reads Nexus, your mod manager " +
         "does the download. For full details (requirements, files, accurate latest version) of one result, pass its id " +
         "to housecarl_nexus_mod.")]
    public static async Task<string> NexusSearch(
        NexusClient nexus,
        [Description("Words to search for in mod names, e.g. 'archery overhaul' or 'true storms'. Matched as a wildcard against Skyrim SE mod names.")]
            string query,
        [Description("Optional. Narrow to a Nexus category name, e.g. 'Audio', 'Armour', 'Gameplay', 'Patches'. Omit to search all categories.")]
            string? category = null,
        [Description("Optional. Result ordering: 'endorsements' (default, best-regarded first), 'downloads' (most popular), 'recent' (recently updated), 'name' (A-Z), or 'relevance'.")]
            string sort = "endorsements",
        [Description("Optional. Max results to return (default 10, max 50).")]
            int limit = 10,
        CancellationToken ct = default)
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
    }

    [McpServerTool(Name = "housecarl_nexus_mod", ReadOnly = true, Title = "Look up a Nexus mod"),
     Description(
         "Look up ONE Skyrim Special Edition mod on Nexus by its numeric mod id (e.g. 12604) OR a pasted mod URL — " +
         "without opening a browser. Returns the mod's name, version, author, status, endorsement/download counts, " +
         "category, last-updated date, summary, whether direct download is disabled (manager-only), its Nexus " +
         "REQUIREMENTS (each required mod's name + id + notes, off-site deps flagged), and its newest MAIN file's " +
         "version — the accurate 'latest version', because a mod's own version header can lag its newest file. " +
         "READ-ONLY and needs an internet connection (local tools unaffected offline). Does NOT download or install — " +
         "use your mod manager's 'Mod Manager Download' for that. To find a mod by name first, use housecarl_nexus_search.")]
    public static async Task<string> NexusMod(
        NexusClient nexus,
        [Description("The mod to look up: a numeric Nexus mod id (e.g. 12604) or a full mod URL (e.g. https://www.nexusmods.com/skyrimspecialedition/mods/12604).")]
            string mod,
        CancellationToken ct = default)
    {
        if (!TryParseModId(mod, out int modId))
            return $"error: couldn't read a mod id from '{mod}'. Pass a numeric Nexus mod id (e.g. 12604) or a Skyrim SE "
                + "mod URL (e.g. https://www.nexusmods.com/skyrimspecialedition/mods/12604).";

        var (ok, error, detail) = await nexus.GetModAsync(modId, ct);
        if (!ok) return "error: " + error;
        return Render.Mod(detail!);
    }

    /// <summary>Map a friendly sort word to a ModsSort field name; null if unrecognised (the tool reports it — Q3).</summary>
    static string? MapSort(string s) => s.Trim().ToLowerInvariant() switch
    {
        "endorsements" or "endorsed" or "best" or "top" => "endorsements",
        "downloads" or "popular" or "most_downloaded" => "downloads",
        "recent" or "updated" or "latest" or "newest" => "updatedAt",
        "name" or "alphabetical" or "az" => "name",
        "relevance" or "relevant" => "relevance",
        _ => null,
    };

    /// <summary>Accept a bare numeric id or any URL containing '/mods/&lt;n&gt;'.</summary>
    static bool TryParseModId(string s, out int modId)
    {
        s = s.Trim();
        if (int.TryParse(s, out modId) && modId > 0) return true;
        var m = Regex.Match(s, @"/mods/(\d+)", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out modId) && modId > 0) return true;
        modId = 0;
        return false;
    }
}

/// <summary>Render the Nexus result records to compact, readable text. Every mod ends with its page URL so the user can
/// click through to the manager-download button (the install handoff houseCARL deliberately leaves to MO2).</summary>
static class Render
{
    const string ModUrlBase = "https://www.nexusmods.com/skyrimspecialedition/mods/";

    public static string Search(string term, string? category, string sort, NexusSearchResult r)
    {
        var sb = new StringBuilder();
        sb.Append("Nexus search: \"").Append(term).Append('"');
        if (!string.IsNullOrWhiteSpace(category)) sb.Append(" in '").Append(category).Append('\'');
        sb.Append(" — ").Append(r.TotalCount.ToString("N0")).Append(" match(es), by ").Append(sort)
          .Append(", showing ").Append(r.Hits.Count).Append(':');
        if (r.Hits.Count == 0) return sb.Append("\n(none)").ToString();

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
          .Append("your mod manager does the download. Pass an id to housecarl_nexus_mod for requirements + files.)");
        return sb.ToString();
    }

    public static string Mod(NexusModDetail m)
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
        if (!m.DirectDownloadEnabled)
            sb.Append("\nnote: the author disabled direct download — manager (nxm) download only.");
        if (!string.IsNullOrWhiteSpace(m.Summary)) sb.Append("\n\n").Append(m.Summary);

        // The accurate "latest version" is the newest MAIN file, not the mod's version header (which can lag).
        NexusFile? main = null;
        foreach (var f in m.Files)
            if (f.Category == "MAIN" && (main is null || f.Date > main.Date)) main = f;
        if (main is not null)
            sb.Append("\n\nlatest MAIN file: ").Append(main.Name).Append(" v").Append(main.Version ?? "?")
              .Append(" (").Append(Day(main.Date)).Append(')');

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

        sb.Append("\n\n").Append(ModUrlBase).Append(m.ModId);
        return sb.ToString();
    }

    /// <summary>Collapse whitespace and cap a blurb to n chars with an ellipsis (Q3: explicit cut, never silent garble).</summary>
    static string OneLine(string s, int n)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Length <= n ? s : s[..n].TrimEnd() + "…";
    }

    /// <summary>ISO-8601 datetime → just the date (yyyy-MM-dd).</summary>
    static string Day(string iso) => iso.Length >= 10 ? iso[..10] : iso;

    /// <summary>Unix-seconds → yyyy-MM-dd.</summary>
    static string Day(long unixSeconds)
    {
        try { return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToString("yyyy-MM-dd"); }
        catch { return unixSeconds.ToString(); }
    }
}

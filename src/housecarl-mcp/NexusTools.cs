using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>
/// houseCARL's Nexus Mods read tools — the QOL layer that lets houseCARL answer Nexus questions DIRECTLY instead of
/// spinning up a browser to scrape a mod page. Two read-only tools over <see cref="NexusClient"/> (the public v2 GraphQL
/// read API, keyless): search the catalog, and look up one mod's detail + requirements + newest MAIN file (the accurate
/// latest version). Neither downloads or installs — that stays the mod manager's nxm handoff. Both need an internet
/// connection and fail LOUD/clean when there isn't one (Q3); they do NOT touch the MO2 instance, so they work even when
/// houseCARL has no load order configured.
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
         "does the download. For full details (requirements and the newest MAIN file — the accurate latest version) of " +
         "one result, pass its id to housecarl_nexus_mod.")]
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
        CancellationToken ct = default) => Guard.Tool("housecarl_nexus_search", async () =>
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

    [McpServerTool(Name = "housecarl_nexus_mod", ReadOnly = true, Title = "Look up a Nexus mod"),
     Description(
         "Look up ONE Skyrim Special Edition mod on Nexus by its numeric mod id (e.g. 12604) OR a pasted mod URL — " +
         "without opening a browser. Returns the mod's name, version, author, status, endorsement/download counts, " +
         "category, last-updated date, summary, whether direct download is disabled (manager-only), its Nexus " +
         "REQUIREMENTS (each required mod's name + id + notes, off-site deps flagged), and its newest MAIN file's " +
         "version — the accurate 'latest version', because a mod's own version header can lag its newest file. " +
         "Pass description=true to ALSO get the mod's full page write-up (what it does, how it works, usage, " +
         "recommended INI settings, compatibility/conflict notes), cleaned of Nexus markup to plain text — off by default because it can run " +
         "several KB. READ-ONLY and needs an internet connection (local tools unaffected offline). Does NOT download or install — " +
         "use your mod manager's 'Mod Manager Download' for that. To find a mod by name first, use housecarl_nexus_search.")]
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
        CancellationToken ct = default) => Guard.Tool("housecarl_nexus_mod", async () =>
    {
        var (modId, parseError) = ResolveModId(mod);
        if (parseError is not null) return "error: " + parseError;

        var (ok, error, detail) = await nexus.GetModAsync(modId, ct);
        if (!ok) return "error: " + error;
        return Render.Mod(detail!, description);
    }, ct);

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

    /// <summary>Resolve the user's input to an SSE mod id. Accepts a bare numeric id or a Nexus mod URL. A Nexus URL for a
    /// DIFFERENT game (fallout4, skyrim, starfield, …) is REJECTED with a clear message rather than silently resolving to a
    /// same-numbered Skyrim SE mod (Q3: never a confidently-wrong answer). Returns (modId, error) — exactly one is set.</summary>
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

        // Fallback: a bare '/mods/<n>' with no identifiable game (a partial paste) — treat as an SSE id.
        var loose = Regex.Match(s, @"/mods/(\d+)", RegexOptions.IgnoreCase);
        if (loose.Success && int.TryParse(loose.Groups[1].Value, out id) && id > 0) return (id, null);

        return (0, $"couldn't read a mod id from '{s}'. Pass a numeric Nexus mod id (e.g. 12604) or a Skyrim SE mod URL "
            + "(e.g. https://www.nexusmods.com/skyrimspecialedition/mods/12604).");
    }
}

/// <summary>Render the Nexus result records to compact, readable text. Every mod ends with its page URL so the user can
/// click through to the manager-download button (the install handoff houseCARL deliberately leaves to MO2).</summary>
static class Render
{
    const string ModUrlBase = "https://www.nexusmods.com/skyrimspecialedition/mods/";

    /// <summary>Max characters of CLEANED description text to emit (≈1.5k tokens — enough for a typical full description,
    /// measured against real mod pages). Generous because the full description is opt-in, but bounded so a giant page
    /// can't dominate the response; an over-length body is cut at a word boundary with an explicit marker (Q3 — never a
    /// silent truncation, like <see cref="OneLine"/>).</summary>
    const int DescriptionCap = 6000;

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
            // category= is a server-side case-sensitive EQUALS (live-proven: 'Armour' 5041 matches vs 'armour' 0),
            // so a zero WITH a category filter is as likely a casing/name miss as a real zero — say so (Q3).
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
          .Append("your mod manager does the download. Pass an id to housecarl_nexus_mod for requirements + latest version.)");
        return sb.ToString();
    }

    public static string Mod(NexusModDetail m, bool includeDescription = false)
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
        // A mod with NO main file (everything filed as optional/misc/old) says so explicitly — otherwise the
        // section's absence silently leaves the possibly-lagging version header as the only signal (Q3).
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

        // Full description is opt-in (it can run several KB of BBCode/HTML). When asked, clean it to plain text; if the
        // page genuinely has none, SAY so rather than silently omitting (Q3 — an empty section reads as a missing one).
        if (includeDescription)
        {
            var body = string.IsNullOrWhiteSpace(m.Description) ? null : StripMarkup(m.Description!, DescriptionCap);
            sb.Append("\n\n── description ──\n")
              .Append(string.IsNullOrEmpty(body) ? "(this mod's page has no description text.)" : body);
        }

        sb.Append("\n\n").Append(ModUrlBase).Append(m.ModId);
        return sb.ToString();
    }

    /// <summary>Substring(0, n) that never splits a surrogate pair: an astral char (emoji, CJK extension B+, …) is two
    /// UTF-16 chars, so a raw clamp can leave a LONE high surrogate at the cut = a broken half-glyph. If the char just
    /// before the cut is a high surrogate (its low half sits at/after n), back up one so the orphaned half is dropped
    /// (Q3 — an explicit cut, never a silent garble).</summary>
    static string ClampChars(string s, int n)
    {
        if (s.Length <= n) return s;
        if (n > 0 && char.IsHighSurrogate(s[n - 1])) n--;
        return s[..n];
    }

    /// <summary>Collapse whitespace and cap a blurb to n chars with an ellipsis (Q3: explicit cut, never silent garble).</summary>
    internal static string OneLine(string s, int n)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Length <= n ? s : ClampChars(s, n).TrimEnd() + "…";
    }

    /// <summary>Turn a Nexus description (BBCode interleaved with HTML — e.g. "[size=5][b]…[/b][/size]&lt;br /&gt;") into
    /// readable plain text: drop image/video embeds wholesale (their inner text is a bare URL — noise as prose), unwrap
    /// [url=…]label[/url] to its label, turn list markers and HTML block/break tags into newlines, strip every remaining
    /// BBCode and HTML tag (keeping inner text), decode the handful of HTML entities that actually appear, collapse
    /// runaway whitespace, and cap the result with an explicit truncation marker (Q3 — never a silent cut).</summary>
    internal static string StripMarkup(string raw, int cap)
    {
        const RegexOptions IC = RegexOptions.IgnoreCase;
        // Bound the input BEFORE any regex: the embed/[url] cleaners use a lazy `.*?` that backtracks O(n²) on many
        // UNCLOSED openers in untrusted author text — a malformed page could hang the call for seconds (a Q3 degraded-
        // mode risk; DescriptionCap runs too late to help, it only trims the cleaned OUTPUT). cap*4 leaves ample
        // headroom (a real description cleans to < cap from far less raw) while capping the worst case to a fraction
        // of a second.
        var s = ClampChars(raw, cap * 4);

        // Embeds: remove tag AND inner content (a URL/id is meaningless as prose). [img]…[/img], [youtube]…[/youtube], …
        s = Regex.Replace(s, @"\[(img|youtube|video|media|embed)\b[^\]]*\].*?\[/\1\]", " ", IC | RegexOptions.Singleline);
        // Links: keep the human label, drop the target. [url=…]label[/url] or [url]label[/url].
        s = Regex.Replace(s, @"\[url\b[^\]]*\](.*?)\[/url\]", "$1", IC | RegexOptions.Singleline);
        // List items: [*] opens an item (→ bullet); some BBCode dialects also emit a [/*] close (→ drop). Neither is
        // letter-led, so the general tag strip below won't catch them — handle both here. (Real mod pages use both forms.)
        s = Regex.Replace(s, @"\[\*\]", "\n• ", IC);
        s = Regex.Replace(s, @"\[/\*\]", "", IC);
        // Every remaining BBCode tag: [tag], [tag=value], [/tag] — keep inner text.
        s = Regex.Replace(s, @"\[/?[a-z][a-z0-9]*(=[^\]]*)?\]", "", IC);

        // HTML: line breaks and block boundaries → newlines, then strip all other tags.
        s = Regex.Replace(s, @"<\s*br\s*/?>", "\n", IC);
        s = Regex.Replace(s, @"<\s*/?\s*(p|div|li|ul|ol|h[1-6]|tr|table)\b[^>]*>", "\n", IC);
        s = Regex.Replace(s, @"<[^>]+>", "", RegexOptions.Singleline);

        // Entities that actually show up in Nexus descriptions. &amp; is decoded LAST (after every other entity):
        // it produces a literal '&', the entity-introducing char, so undoing it first would let a double-encoded
        // input like "&amp;lt;" (author wanted the visible text "&lt;") become "&lt;" then "<" — a double-decode.
        // Keep &amp; at the tail so an already-decoded entity's '&' can never re-trigger another replacement.
        s = s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
             .Replace("&#39;", "'").Replace("&apos;", "'").Replace("&nbsp;", " ")
             .Replace("&amp;", "&");

        // Strip zero-width / BOM characters authors paste in (they render as stray glyphs); normalise no-break spaces.
        s = s.Replace("\uFEFF", "").Replace("\u200B", "").Replace("\u200C", "").Replace("\u200D", "").Replace('\u00A0', ' ');

        // Whitespace: normalise newlines, collapse space runs, trim line edges, cap blank-line runs.
        s = s.Replace("\r\n", "\n").Replace('\r', '\n');
        s = Regex.Replace(s, @"[ \t]+", " ");
        s = Regex.Replace(s, @" *\n *", "\n");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        s = s.Trim();

        // Cap with an explicit marker, backing up to a nearby word boundary so we don't cut mid-word.
        if (s.Length > cap)
        {
            var cut = ClampChars(s, cap);
            var sp = cut.LastIndexOf(' ');
            if (sp > cap - 200) cut = cut[..sp];
            s = cut.TrimEnd() + "\n…(description truncated — full text on the mod page.)";
        }
        return s;
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

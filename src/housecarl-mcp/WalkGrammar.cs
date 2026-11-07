using System.Text.Json;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

/// <summary>The one parse of a walk's values and of its seeds: every walk calls it and nothing else parses these
/// values, so their names, defaults and refusal words are the same on every walk; a bad seed's words differ by walk.
/// Contract in docs/architecture/walk-and-reverse.md, "One walk grammar".</summary>
public static class WalkGrammar
{
    /// <summary>The node budget when walk.max_nodes is unset, on every walk.</summary>
    internal const int DefaultMaxNodes = 2000;

    /// <summary>The records walk's hop limit when walk.depth is unset.</summary>
    internal const int RecordsDefaultDepth = 16;

    /// <summary>The asset walk's hop limit when walk.depth is unset: NPC → head part → its extra part's texture set.</summary>
    internal const int AssetDefaultDepth = 3;

    /// <summary>How a walk reads max_nodes: per seed (bounded by <see cref="RecordsTools.RecordsWalk.Ceiling"/>) or one
    /// budget shared by every seed (no ceiling).</summary>
    internal enum Budget { PerSeed, Shared }

    /// <summary>A walk's typed values: hops, the node budget, the types walk.through expands (display names, null when
    /// unset), and the exclusions (display names, each type once, first match wins).</summary>
    public sealed record Values(int Depth, int MaxNodes, HashSet<string>? Through, List<(string Match, bool Refuse)> Exclusions)
    {
        /// <summary>A walk's defaults, as walk=true or an empty walk object reads.</summary>
        public static Values Defaults(int depth) => new(depth, DefaultMaxNodes, null, new());
    }

    /// <summary>A refuse exclusion reached: the walk ends and the call returns nothing.</summary>
    internal sealed class WalkRefused(string message) : Exception(message);

    /// <summary>The line counting, per type, the records walk.through left out; "none" when it left none out.</summary>
    internal static string LeftOutLine(IEnumerable<string> leftOutTypes)
    {
        var counts = leftOutTypes.GroupBy(t => t, StringComparer.Ordinal)
                                 .Select(g => (Type: g.Key, Count: g.Count()))
                                 .OrderByDescending(g => g.Count).ThenBy(g => g.Type, StringComparer.Ordinal)
                                 .Select(g => $"{g.Count} {g.Type}").ToList();
        return "left out, not in walk.through: " + (counts.Count == 0 ? "none" : string.Join(", ", counts));
    }

    /// <summary>The walk values, or the one refusal (values null). <paramref name="through"/> has been read through the list door.</summary>
    internal static string? Parse(TypeLookup types, int? depth, int? maxNodes, string[]? through,
                                  IReadOnlyList<RecordsTools.RecordsWalkExclusion>? exclusions,
                                  int defaultDepth, Budget budget, out Values values)
    {
        values = null!;
        int d = defaultDepth, n = DefaultMaxNodes;
        if (depth is { } wd)
        {
            if (wd < 1) return $"error: walk.depth={wd} — depth must be >= 1 (hops from the seed).";
            d = wd;
        }
        if (maxNodes is { } wn)
        {
            if (wn < 1) return $"error: walk.max_nodes={wn} — the node budget must be >= 1.";
            // The bound is on the PER-SEED reading of this budget; docs/architecture/records-tool-front.md.
            if (budget == Budget.PerSeed && wn > RecordsTools.RecordsWalk.Ceiling)
                return $"error: walk.max_nodes={wn} — the node budget's hard upper bound is {RecordsTools.RecordsWalk.Ceiling}; pass that or less, which is above the whole closure a single seed reaches on a large order.";
            n = wn;
        }
        if (ParseExclusions(types, exclusions, "walk.exclusions", out var ex) is { } exErr) return exErr;
        HashSet<string>? thr = null;
        if (through is not null)
        {
            if (through.Length == 0)
                return "error: walk.through is empty — name the record types the walk expands through (e.g. [\"LeveledItem\", \"Outfit\"]), or omit it to walk every type.";
            thr = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in through)
            {
                if (types.DisplayNames(new[] { t ?? "" }) is not { } names) return NotAType("walk.through", t ?? "");
                thr.UnionWith(names);
            }
        }
        values = new(d, n, thr, ex);
        return null;
    }

    static readonly JsonSerializerOptions WalkJson = new(JsonSerializerDefaults.Web);

    /// <summary>asset_status's walk=: null values when it does not walk (unset or false), the defaults for true, the
    /// parsed values for a walk object, or the one refusal.</summary>
    internal static string? ReadAssetWalk(TypeLookup types, JsonElement? walk, out Values? values)
    {
        values = null;
        if (walk is not { } el || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False) return null;
        if (el.ValueKind == JsonValueKind.True) { values = Values.Defaults(AssetDefaultDepth); return null; }
        const string Shape = "walk=true, or walk={\"depth\": 3, \"max_nodes\": 2000, \"through\": [\"HeadPart\"], \"exclusions\": [{\"match\": \"Race\", \"severity\": \"stop\"}]} with any of those members";
        if (el.ValueKind != JsonValueKind.Object)
            return $"error: walk= takes {Shape}; got a {el.ValueKind.ToString().ToLowerInvariant()}.";
        RecordsTools.RecordsWalk w;
        try { w = el.Deserialize<RecordsTools.RecordsWalk>(WalkJson) ?? new(); }
        catch (JsonException ex) { return $"error: walk= could not be read as a walk object — {Guard.Flatten(ex.Message)} Pass {Shape}."; }
        // The records walk's own shaping values, each refused by name rather than dropped.
        if (w.seed_paths is not null)
            return "error: walk.seed_paths is a records-walk value — the asset walk starts from every link on its seed records; drop it, and seed the records you mean with records=.";
        if (w.follow is not null)
            return "error: walk.follow is a records-walk value — the asset walk follows every form link; narrow it with walk.through or walk.exclusions instead.";
        if (w.direction is not null)
            return $"error: walk.direction is a records-walk value — the asset walk walks forward only; what points at a record is {ToolNames.Records} walk={{\"direction\": \"reverse\"}}.";
        var throughIn = ListDoor.ReadItems(w.through, "walk.through", ListKind.Name, "walk={{\"through\": [{0}]}}");
        if (throughIn.Error is not null) return throughIn.Error;
        if (Parse(types, w.depth, w.max_nodes, throughIn.Tokens, w.exclusions, AssetDefaultDepth, Budget.Shared, out var v) is { } err) return err;
        values = v;
        return null;
    }

    /// <summary>The exclusions alone, as copy takes them under <paramref name="field"/>: each entry's type expanded to
    /// the display names a read reports, each name once at its first entry; or the one refusal.</summary>
    internal static string? ParseExclusions(TypeLookup types, IReadOnlyList<RecordsTools.RecordsWalkExclusion>? entries,
                                            string field, out List<(string Match, bool Refuse)> exclusions)
    {
        exclusions = new();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in entries ?? Array.Empty<RecordsTools.RecordsWalkExclusion>())
        {
            if (string.IsNullOrWhiteSpace(x?.match))
                return $"error: {(field.StartsWith("walk") ? "a" : "an")} {field} entry needs match= — the record type name a read reports (e.g. 'Race').";
            var sev = x.severity?.Trim().ToLowerInvariant();
            if (sev is not ("stop" or "refuse"))
                return $"error: {field} '{x.match}': severity='{x.severity}' — use 'stop' (prune, record the boundary) or 'refuse' (the whole walk fails loud).";
            // A name or a signature, matched as the type name a read reports.
            if (types.DisplayNames(new[] { x.match! }) is not { } names) return NotAType(field, x.match!);
            foreach (var name in names)
                if (seen.Add(name)) exclusions.Add((name, sev == "refuse"));
        }
        return null;
    }

    /// <summary>The refusal for a walk type field's entry that names no record type.</summary>
    static string NotAType(string field, string entry)
        => $"error: {field} '{entry}' is not a record type — pass a catalog name (e.g. 'Npc', 'LeveledItem') or a signature (e.g. 'NPC_', 'LVLI').";

    /// <summary>One seed token: its key, or why it is not a FormID.</summary>
    internal readonly record struct Seed(string Raw, FormKey? Key, string? Error);

    /// <summary>One seed token through <paramref name="parse"/>; a token that does not parse is its own error, never the
    /// end of the walk.</summary>
    internal static Seed ParseSeed(string raw, Func<string, FormKey> parse)
    {
        try { return new(raw, parse(raw), null); }
        catch (Exception ex) { return new(raw, null, $"bad FormID '{raw}': {ex.Message}"); }
    }
}

using System.Text.Json;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

/// <summary>The one parse of a walk's values and of its seeds: every walk calls it and nothing else parses these
/// values, so their names and defaults are the same on every walk; refusals touching edge kinds and a bad seed's words differ by walk.
/// Contract in docs/architecture/walk-and-reverse.md, "One walk grammar".</summary>
public static class WalkGrammar
{
    /// <summary>The node budget when walk.max_nodes is unset, on every walk.</summary>
    internal const int DefaultMaxNodes = 2000;

    /// <summary>The records walk's hop limit when walk.depth is unset.</summary>
    internal const int RecordsDefaultDepth = 16;

    /// <summary>The asset walk's hop limit when walk.depth is unset: a safety cap, since it follows part fields only.</summary>
    internal const int AssetDefaultDepth = RecordsDefaultDepth;

    /// <summary>walk.follow: unset (null paths, the walk's default), one path as a string, or a list of paths through
    /// the list door; or the one refusal. Both walks read it here.</summary>
    internal static string? ReadFollow(WalkFollow? follow, out string[]? paths)
    {
        paths = null;
        if (follow is null) return null;
        const string Shape = "walk.follow takes one field path as a string (e.g. \"Template\"), \"*\" for every link, or a list of field paths (e.g. [\"HeadParts\", \"WornArmor\"])";
        if (follow.Paths is not { } raw) return $"error: {Shape}; got {follow.Bad}.";
        // One path as a string reads as it always has: trimmed, and a blank one is the default.
        if (follow.Single)
        {
            paths = string.IsNullOrWhiteSpace(raw[0]) ? null : new[] { raw[0].Trim() };
            return null;
        }
        var read = ListDoor.ReadItems(raw, "walk.follow", ListKind.Name, "walk={{\"follow\": [{0}]}}");
        if (read.Error is not null) return read.Error;
        var tokens = read.Tokens!.Select(t => t.Trim()).ToArray();
        if (tokens.Length == 0 || tokens.Any(t => t.Length == 0)) return $"error: walk.follow is an empty list or holds an empty path — {Shape}.";
        if (tokens.Length > 1 && FollowsEveryLink(tokens))
            return "error: walk.follow holds \"*\" beside other paths — \"*\" already follows every form link, so pass it alone, or drop it and name the fields.";
        paths = tokens;
        return null;
    }

    /// <summary>Whether a follow list holds the every-link token.</summary>
    internal static bool FollowsEveryLink(IReadOnlyList<string>? paths) => paths?.Contains("*") == true;

    /// <summary>How a walk reads max_nodes: per seed (bounded by <see cref="RecordsTools.RecordsWalk.Ceiling"/>) or one
    /// budget shared by every seed (no ceiling).</summary>
    internal enum Budget { PerSeed, Shared }

    /// <summary>A walk's typed values: hops, the node budget, the types walk.through expands (display names, null when
    /// it names none), and the exclusions (display names, each type once, first match wins).</summary>
    public sealed record Values(int Depth, int MaxNodes, HashSet<string>? Through, List<(string Match, bool Refuse)> Exclusions)
    {
        /// <summary>The asset walk's edge kinds walk.through names, whose rows it shows (null when it names none).</summary>
        public HashSet<WalkEdgeKind>? Edges { get; init; }

        /// <summary>The asset walk's walk.follow paths ("*" every link); null for its default, the shipped part fields and child records.</summary>
        public IReadOnlyList<string>? Follow { get; init; }

        /// <summary>A walk's defaults, as walk=true or an empty walk object reads.</summary>
        public static Values Defaults(int depth) => new(depth, DefaultMaxNodes, null, new());
    }

    /// <summary>A refuse exclusion reached: the walk ends and the call returns nothing.</summary>
    internal sealed class WalkRefused(string message) : Exception(message);

    /// <summary>The line counting, per type, the records walk.through left out, then, per edge kind, the asset walk's
    /// rows it did not show; "none" when it left none out.</summary>
    internal static string LeftOutLine(IEnumerable<string> leftOutTypes, IEnumerable<string>? hiddenEdges = null)
    {
        static IEnumerable<(string Key, int Count)> Counted(IEnumerable<string> keys)
            => keys.GroupBy(t => t, StringComparer.Ordinal).Select(g => (g.Key, Count: g.Count()))
                   .OrderByDescending(g => g.Count).ThenBy(g => g.Key, StringComparer.Ordinal);
        var counts = Counted(leftOutTypes).Select(g => $"{g.Count} {g.Key}")
            .Concat(Counted(hiddenEdges ?? Array.Empty<string>()).Select(g => $"{g.Count} {g.Key}{WalkCut.Row}{(g.Count == 1 ? "" : "s")}")).ToList();
        return WalkCut.LeftOut + (counts.Count == 0 ? "none" : string.Join(", ", counts));
    }

    /// <summary>The walk values, or the one refusal (values null). <paramref name="through"/> has been read through the list door.</summary>
    internal static string? Parse(TypeLookup types, int? depth, int? maxNodes, string[]? through,
                                  IReadOnlyList<RecordsTools.RecordsWalkExclusion>? exclusions,
                                  int defaultDepth, Budget budget, out Values values, bool edgeKinds = false)
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
        if (ParseExclusions(types, exclusions, "walk.exclusions", out var ex, edgeKinds) is { } exErr) return exErr;
        HashSet<string>? thr = null;
        HashSet<WalkEdgeKind>? edges = null;
        if (through is not null)
        {
            if (through.Length == 0)
                return "error: walk.through is empty — name the record types the walk expands through (e.g. [\"LeveledItem\", \"Outfit\"])"
                       + (edgeKinds ? ", or the edge kinds whose rows to show (e.g. [\"texture\", \"script\"]), or omit it to walk every type but an NPC reached past a seed, kept as a boundary."
                                    : ", or omit it to walk every type.");
            // One token is a record type or, on the asset walk, an edge kind; the two spellings share no name.
            foreach (var t in through)
            {
                var token = t ?? "";
                if (types.DisplayNames(new[] { token }) is { } names) { (thr ??= new(StringComparer.OrdinalIgnoreCase)).UnionWith(names); continue; }
                var kind = WalkEdgeKinds.Parse(token);
                if (!edgeKinds)
                    return kind is { } k && k != WalkEdgeKind.Seed
                        ? $"error: walk.through '{token}' is an asset walk edge kind — the records walk follows form links only, so its through takes record types (e.g. 'Npc', 'LVLI'); name the file edge kinds on {ToolNames.AssetStatus} walk=."
                        : NotAType("walk.through", token);
                if (kind == WalkEdgeKind.Seed)
                    return "error: walk.through 'seed' — the seed rows are always shown; name the edge kinds whose rows to show beside them (e.g. [\"texture\", \"script\"]).";
                if (kind is not { } edge)
                    return $"error: walk.through '{token}' is neither a record type nor an edge kind — pass a catalog name (e.g. 'Npc', 'LeveledItem'), a signature (e.g. 'NPC_', 'LVLI'), or an edge kind: {EdgeKindList}.";
                (edges ??= new()).Add(edge);
            }
        }
        values = new(d, n, thr, ex) { Edges = edges };
        return null;
    }

    /// <summary>The edge kinds walk.through takes on the asset walk, as one comma list.</summary>
    internal static string EdgeKindList => string.Join(", ", WalkEdgeKinds.FileKinds.Select(k => k.Token()));

    static readonly JsonSerializerOptions WalkJson = new(JsonSerializerDefaults.Web);

    /// <summary>asset_status's walk=: null values when it does not walk (unset or false), the defaults for true, the
    /// parsed values for a walk object, or the one refusal.</summary>
    internal static string? ReadAssetWalk(TypeLookup types, JsonElement? walk, out Values? values)
    {
        values = null;
        if (walk is not { } el || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False) return null;
        if (el.ValueKind == JsonValueKind.True) { values = Values.Defaults(AssetDefaultDepth); return null; }
        const string Shape = "walk=true, or walk={\"follow\": [\"HeadParts\"], \"depth\": 16, \"max_nodes\": 2000, \"through\": [\"HeadPart\"], \"exclusions\": [{\"match\": \"Race\", \"severity\": \"stop\"}]} with any of those members";
        if (el.ValueKind != JsonValueKind.Object)
            return $"error: walk= takes {Shape}; got a {el.ValueKind.ToString().ToLowerInvariant()}.";
        RecordsTools.RecordsWalk w;
        try { w = el.Deserialize<RecordsTools.RecordsWalk>(WalkJson) ?? new(); }
        catch (JsonException ex) { return $"error: walk= could not be read as a walk object — {Guard.Flatten(ex.Message)} Pass {Shape}."; }
        // The records walk's own shaping values, each refused by name rather than dropped.
        if (w.seed_paths is not null)
            return "error: walk.seed_paths is a records-walk value — the asset walk follows its seed records' part fields and child records, and walk.follow names other fields; drop it, and seed the records you mean with records=.";
        if (ReadFollow(w.follow, out var follow) is { } followErr) return followErr;
        if (follow is not null && follow.Any(p => p.Split('.').Any(s => s.Trim().StartsWith("*parent", StringComparison.OrdinalIgnoreCase))))
            return "error: walk.follow '*parent' climbs to a containing record, and the asset walk walks forward only — seed the containing record with records= instead.";
        if (w.direction is not null)
            return $"error: walk.direction is a records-walk value — the asset walk walks forward only; what points at a record is {ToolNames.Records} walk={{\"direction\": \"reverse\"}}.";
        var throughIn = ListDoor.ReadItems(w.through, "walk.through", ListKind.Name, "walk={{\"through\": [{0}]}}");
        if (throughIn.Error is not null) return throughIn.Error;
        if (Parse(types, w.depth, w.max_nodes, throughIn.Tokens, w.exclusions, AssetDefaultDepth, Budget.Shared, out var v, edgeKinds: true) is { } err) return err;
        values = v with { Follow = follow };
        return null;
    }

    /// <summary>The exclusions alone, as copy takes them under <paramref name="field"/>: each entry's type expanded to
    /// the display names a read reports, each name once at its first entry; or the one refusal. On the asset walk
    /// (<paramref name="edgeKinds"/>) an edge kind is refused by name, pointing at walk.through.</summary>
    internal static string? ParseExclusions(TypeLookup types, IReadOnlyList<RecordsTools.RecordsWalkExclusion>? entries,
                                            string field, out List<(string Match, bool Refuse)> exclusions, bool edgeKinds = false)
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
            if (types.DisplayNames(new[] { x.match! }) is not { } names)
                return edgeKinds && WalkEdgeKinds.Parse(x.match!) is { } k && k != WalkEdgeKind.Seed
                    ? $"error: {field} '{x.match}' is an edge kind — exclusions take record types; name the edge kinds whose rows to show in walk.through (e.g. [\"texture\", \"script\"])."
                    : NotAType(field, x.match!);
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

/// <summary>walk.follow as sent: one path as a string or a list of paths, each kept as typed; any other JSON shape is
/// kept as <see cref="Bad"/> so <see cref="WalkGrammar.ReadFollow"/> refuses it in its own words.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(WalkFollowConverter))]
public sealed class WalkFollow
{
    /// <summary>The paths, or null when the value was neither a string nor a list of strings.</summary>
    public string[]? Paths { get; init; }

    /// <summary>Sent as one string, not a list.</summary>
    internal bool Single { get; init; }

    /// <summary>What was sent instead, for the refusal.</summary>
    internal string? Bad { get; init; }

    /// <summary>A C# caller passes one path as a string.</summary>
    public static implicit operator WalkFollow?(string? path) => path is null ? null : new WalkFollow { Paths = new[] { path }, Single = true };

    /// <summary>A C# caller passes a list of paths as an array.</summary>
    public static implicit operator WalkFollow?(string[]? paths) => paths is null ? null : new WalkFollow { Paths = paths };
}

/// <summary>Reads walk.follow's string or list, and writes it back in the shape it came in.</summary>
public sealed class WalkFollowConverter : System.Text.Json.Serialization.JsonConverter<WalkFollow>
{
    public override WalkFollow? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var el = doc.RootElement;
        return el.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => new WalkFollow { Paths = new[] { el.GetString()! }, Single = true },
            JsonValueKind.Array when el.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String)
                => new WalkFollow { Paths = el.EnumerateArray().Select(e => e.GetString()!).ToArray() },
            JsonValueKind.Array => new WalkFollow { Bad = "a list holding a non-string" },
            var k => new WalkFollow { Bad = "a " + k.ToString().ToLowerInvariant() },
        };
    }

    public override void Write(Utf8JsonWriter writer, WalkFollow value, JsonSerializerOptions options)
    {
        if (value.Single && value.Paths is { Length: 1 } one) writer.WriteStringValue(one[0]);
        else JsonSerializer.Serialize(writer, value.Paths ?? Array.Empty<string>(), options);
    }
}

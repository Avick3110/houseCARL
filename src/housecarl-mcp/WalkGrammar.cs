using System.Text.Json;
using System.Text.Json.Serialization;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

/// <summary>walk.follow as sent: one field path, or a list of them, on both walks.</summary>
[JsonConverter(typeof(WalkFollowJson))]
public sealed class WalkFollow(IReadOnlyList<string> paths)
{
    /// <summary>The paths as sent, one for a string.</summary>
    public IReadOnlyList<string> Paths { get; } = paths;

    public static implicit operator WalkFollow?(string? path) => path is null ? null : new(new[] { path });
    public static implicit operator WalkFollow?(string[]? paths) => paths is null ? null : new(paths);

    /// <summary>One path as sent, else the list joined, as a sentence or the envelope spells it.</summary>
    public override string ToString() => Paths.Count == 1 ? Paths[0] : string.Join(", ", Paths);
}

/// <summary>Reads walk.follow from a JSON string or an array of strings.</summary>
public sealed class WalkFollowJson : JsonConverter<WalkFollow>
{
    public override WalkFollow? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return new(new[] { reader.GetString()! });
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("walk.follow takes a field path or a list of them, e.g. \"Template\" or [\"HeadParts\", \"WornArmor\"].");
        var paths = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException("walk.follow's list takes field paths as strings, e.g. [\"HeadParts\", \"WornArmor\"].");
            paths.Add(reader.GetString()!);
        }
        return new(paths);
    }

    public override void Write(Utf8JsonWriter writer, WalkFollow value, JsonSerializerOptions options)
    {
        if (value.Paths.Count == 1) { writer.WriteStringValue(value.Paths[0]); return; }
        writer.WriteStartArray();
        foreach (var p in value.Paths) writer.WriteStringValue(p);
        writer.WriteEndArray();
    }
}

/// <summary>The one parse of a walk's values and of its seeds: every walk calls it and nothing else parses these
/// values, so their names and defaults are the same on every walk; refusals touching edge kinds and a bad seed's words differ by walk.
/// Contract in docs/architecture/walk-and-reverse.md, "One walk grammar".</summary>
public static class WalkGrammar
{
    /// <summary>The node budget when walk.max_nodes is unset, on every walk.</summary>
    internal const int DefaultMaxNodes = 2000;

    /// <summary>The records walk's hop limit when walk.depth is unset.</summary>
    internal const int RecordsDefaultDepth = 16;

    /// <summary>The asset walk's hop limit when walk.depth is unset: what the seed lists (DECISIONS 2026-11-08 (14)).</summary>
    internal const int AssetDefaultDepth = 1;

    /// <summary>How a walk reads max_nodes: per seed (bounded by <see cref="RecordsTools.RecordsWalk.Ceiling"/>) or one
    /// budget shared by every seed (no ceiling).</summary>
    internal enum Budget { PerSeed, Shared }

    /// <summary>A walk's typed values: hops, the node budget, the types walk.through expands (display names, null when
    /// it names none), and the exclusions (display names, each type once, first match wins).</summary>
    public sealed record Values(int Depth, int MaxNodes, HashSet<string>? Through, List<(string Match, bool Refuse)> Exclusions)
    {
        /// <summary>The asset walk's edge kinds walk.through names, whose rows it shows (null when it names none).</summary>
        public HashSet<WalkEdgeKind>? Edges { get; init; }

        /// <summary>The asset walk's walk.follow, each path split into its steps: links under them are followed past
        /// walk.depth (null when unset).</summary>
        public IReadOnlyList<string[]>? Follow { get; init; }

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
            .Concat(Counted(hiddenEdges ?? Array.Empty<string>()).Select(g => $"{g.Count} {g.Key} row{(g.Count == 1 ? "" : "s")}")).ToList();
        return "left out, not in walk.through: " + (counts.Count == 0 ? "none" : string.Join(", ", counts));
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
        const string Shape = "walk=true, or walk={\"depth\": 1, \"max_nodes\": 2000, \"follow\": [\"HeadParts\", \"WornArmor\"], \"through\": [\"HeadPart\"], \"exclusions\": [{\"match\": \"Race\", \"severity\": \"stop\"}]} with any of those members";
        if (el.ValueKind != JsonValueKind.Object)
            return $"error: walk= takes {Shape}; got a {el.ValueKind.ToString().ToLowerInvariant()}.";
        RecordsTools.RecordsWalk w;
        try { w = el.Deserialize<RecordsTools.RecordsWalk>(WalkJson) ?? new(); }
        catch (JsonException ex) { return $"error: walk= could not be read as a walk object — {Guard.Flatten(ex.Message)} Pass {Shape}."; }
        // The records walk's own shaping values, each refused by name rather than dropped.
        if (w.seed_paths is not null)
            return "error: walk.seed_paths is a records-walk value — the asset walk starts from every link on its seed records; drop it, and seed the records you mean with records=.";
        IReadOnlyList<string[]>? follow = null;
        if (w.follow is not null)
        {
            // Every link is followed to walk.depth already, and containment is the records walk's.
            if (w.follow.Paths.FirstOrDefault(p => p.Contains('*')) is { } star)
                return $"error: walk.follow '{star}' — on the asset walk follow names the link fields followed past walk.depth (e.g. [\"HeadParts\", \"WornArmor\", \"Armature\"]); every link is already followed to walk.depth, and '*parent' is the records walk's.";
            if (FollowPaths(w.follow, out var paths) is { } fErr) return fErr;
            follow = paths;
        }
        if (w.direction is not null)
            return $"error: walk.direction is a records-walk value — the asset walk walks forward only; what points at a record is {ToolNames.Records} walk={{\"direction\": \"reverse\"}}.";
        var throughIn = ListDoor.ReadItems(w.through, "walk.through", ListKind.Name, "walk={{\"through\": [{0}]}}");
        if (throughIn.Error is not null) return throughIn.Error;
        if (Parse(types, w.depth, w.max_nodes, throughIn.Tokens, w.exclusions, AssetDefaultDepth, Budget.Shared, out var v, edgeKinds: true) is { } err) return err;
        values = v with { Follow = follow };
        return null;
    }

    /// <summary>walk.follow's paths, each split into its steps, or the one refusal: an empty list, a blank path, or
    /// '*' beside another path. The records walk reads a single path as before (blank or '*' is every link).</summary>
    internal static string? FollowPaths(WalkFollow follow, out IReadOnlyList<string[]> paths)
    {
        paths = Array.Empty<string[]>();
        if (follow.Paths.Count == 0)
            return "error: walk.follow is empty — name the link fields to follow (e.g. [\"HeadParts\", \"WornArmor\"]), or omit it.";
        var split = new List<string[]>();
        foreach (var p in follow.Paths)
        {
            var segs = (p ?? "").Trim().Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segs.Length == 0) return $"error: walk.follow '{p}' is not a usable field path.";
            split.Add(segs);
        }
        if (split.Count > 1 && split.FirstOrDefault(s => s is ["*"]) is not null)
            return "error: walk.follow '*' follows every link, so it takes no other path beside it — pass \"*\" alone, or name the paths.";
        paths = split;
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

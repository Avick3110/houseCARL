using System.Text.Json;

namespace HousecarlCore;

/// <summary>The runtime-layer format source (runtime-formats.json): each format's discovery, order, conflict and grammar values, read by the server and printed into the skills by the build; contract in docs/architecture/runtime-formats.md.</summary>
public static class RuntimeFormats
{
    static string? _text;
    static SkyPatcherFormat? _skyPatcher;

    /// <summary>SkyPatcher's row of the embedded source (memoized); throws loudly on a missing or malformed resource.</summary>
    public static SkyPatcherFormat SkyPatcher => _skyPatcher ??= SkyPatcherFrom(SourceText());

    /// <summary>The embedded source's text (memoized), for the build's skill-text generation.</summary>
    public static string SourceText() => _text ??= EmbeddedJson.Read("runtime-formats.json", "runtime-format source");

    /// <summary>Parse SkyPatcher's row from source text; a value the code has no rule for throws, naming the row and the value.</summary>
    public static SkyPatcherFormat SkyPatcherFrom(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var row = doc.RootElement.GetProperty("formats").EnumerateArray()
            .FirstOrDefault(f => Str(f, "format", "formats") == "SkyPatcher");
        if (row.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("runtime-format source: no SkyPatcher row.");

        var d = row.GetProperty("discovery");
        Known(d, "typeFolder", "discovery", "firstComponent");
        Known(d, "tokenMatch", "discovery", "anywhere");
        Known(d, "endingCase", "discovery", "lower");
        var gate = d.GetProperty("gate");
        Known(gate, "kind", "discovery.gate", "pluginStem");
        Known(gate, "moreText", "discovery.gate", "neverRead");
        var reading = d.GetProperty("reading");
        Known(reading, "rootFiles", "discovery.reading", "notRead");
        if (!reading.GetProperty("looseOnly").GetBoolean()) throw Unknown("discovery.reading", "looseOnly", "false");

        var s = row.GetProperty("switches");
        Known(s, "missing", "switches", "off");
        Known(s, "value", "switches", "leadingInteger");

        var o = row.GetProperty("order");
        Known(o, "files", "order", "breadthFirst");
        Known(o, "compare", "order", "ordinalIgnoreCase");
        var passes = o.GetProperty("passes").EnumerateArray().Select(p => new SkyPatcherPass(
            Str(p, "folder", "order.passes"), Str(p, "name", "order.passes"),
            OptStr(p, "runs") switch
            {
                null => SkyPatcherPassRuns.InOrder,
                "early" => SkyPatcherPassRuns.Early,
                "noEffect" => SkyPatcherPassRuns.NoEffect,
                var v => throw Unknown("order.passes", "runs", v),
            },
            OptStr(p, "note"))).ToList();

        var c = row.GetProperty("conflict");
        Known(c, "set", "conflict", "lastReadWins");
        Known(c, "add", "conflict", "accumulate");

        var g = row.GetProperty("grammar");
        var gr = g.GetProperty("reading");
        var grammar = new SkyPatcherGrammar(
            Ch(g, "segment"), Ch(g, "assign"), Ch(g, "item"), Ch(g, "compound"), Ch(g, "address"), Ch(g, "comment"),
            g.GetProperty("trailingComment").GetBoolean(),
            gr.GetProperty("commentLeads").EnumerateArray().Select(x => Single(x.GetString(), "grammar.reading.commentLeads")).ToArray(),
            gr.GetProperty("labelLines").GetBoolean());

        return new SkyPatcherFormat(
            Str(row, "version", "SkyPatcher"),
            AssetResolver.Normalize(Str(d, "root", "discovery")),
            Str(d, "nameToken", "discovery"),
            Strs(gate, "extensions", "discovery.gate"),
            AssetResolver.Normalize(Str(s, "file", "switches")), Str(s, "section", "switches"), Str(s, "keyPrefix", "switches"), Str(s, "keySuffix", "switches"),
            Strs(s, "onWithoutKey", "switches"),
            passes, grammar);
    }

    static string Str(JsonElement el, string prop, string ctx)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!
            : throw new InvalidOperationException($"runtime-format source [{ctx}]: '{prop}' is missing or not a string.");

    static string? OptStr(JsonElement el, string prop) => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static string[] Strs(JsonElement el, string prop, string ctx)
        => el.TryGetProperty(prop, out var a) && a.ValueKind == JsonValueKind.Array && a.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String)
            ? a.EnumerateArray().Select(x => x.GetString()!).ToArray()
            : throw new InvalidOperationException($"runtime-format source [{ctx}]: '{prop}' is missing or not a list of strings.");

    static void Known(JsonElement el, string prop, string ctx, string only)
    {
        var v = Str(el, prop, ctx);
        if (v != only) throw Unknown(ctx, prop, v);
    }

    static char Ch(JsonElement el, string prop) => Single(Str(el, prop, "grammar"), "grammar." + prop);

    static char Single(string? s, string ctx)
        => s is { Length: 1 } ? s[0] : throw new InvalidOperationException($"runtime-format source [{ctx}]: '{s}' is not one character.");

    static InvalidOperationException Unknown(string ctx, string prop, string value)
        => new($"runtime-format source [{ctx}]: '{prop}' = '{value}' has no rule in the server; add the rule in code or use a value it knows.");
}

/// <summary>SkyPatcher's row: the values the discovery, the tokenizer and the skill text all take from the source.</summary>
public sealed record SkyPatcherFormat(
    string Version,
    string Root,
    string NameToken,
    IReadOnlyList<string> GateExtensions,
    string SwitchesFile,
    string SwitchesSection,
    string SwitchKeyPrefix,
    string SwitchKeySuffix,
    IReadOnlyList<string> OnWithoutKey,
    IReadOnlyList<SkyPatcherPass> Passes,
    SkyPatcherGrammar Grammar)
{
    /// <summary>Whether a type folder's patcher runs when SkyPatcher.ini has no key for it (case-insensitive, as folder names are).</summary>
    public bool RunsWithoutKey(string folder) => OnWithoutKey.Contains(folder, StringComparer.OrdinalIgnoreCase);
}

/// <summary>One patcher in run order: its type folder, its display name, and when it runs.</summary>
public sealed record SkyPatcherPass(string Folder, string Name, SkyPatcherPassRuns Runs, string? Note);

/// <summary>When a patcher runs: in the listed order, earlier than the rest, or not at all in effect.</summary>
public enum SkyPatcherPassRuns { InOrder, Early, NoEffect }

/// <summary>SkyPatcher's line delimiters; <see cref="CommentLeads"/> and <see cref="LabelLines"/> are readings upstream does not state.</summary>
public sealed record SkyPatcherGrammar(
    char Segment, char Assign, char Item, char Compound, char Address, char Comment,
    bool TrailingComment, IReadOnlyList<char> CommentLeads, bool LabelLines);

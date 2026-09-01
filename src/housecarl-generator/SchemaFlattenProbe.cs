using System.Text.Json;
using System.Text.Json.Nodes;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD for <see cref="ToolSchemas"/>'s <c>$ref</c> flattening (#451) — the MECHANISM, over synthetic
/// documents. <c>binding-shim-guard</c>'s SCHEMA arm covers the invariant on the real published surface; that
/// surface exercises only the one shape today's DTOs generate (a positional back-reference), so the cases below —
/// <c>$defs</c> recursion, mutual recursion, the placeholder-sibling merge, an unresolvable pointer — have no
/// end-to-end producer and would otherwise ship unproven.
///
/// Run: <c>dotnet run --project src/housecarl-generator schema-flatten-guard</c>
/// </summary>
public static class SchemaFlattenProbe
{
    static int _pass, _fail;

    static void Check(string label, bool ok, string? got = null)
    {
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        if (!ok && got is not null) Console.WriteLine($"          got: {(got.Length <= 400 ? got : got[..400] + " …")}");
        if (ok) _pass++; else _fail++;
    }

    static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;

    /// <summary>Every same-document <c>$ref</c> left in a document, by pointer.</summary>
    static List<string> Refs(JsonNode? node) => node switch
    {
        JsonObject o => o.SelectMany(kv => kv.Key == "$ref" && kv.Value?.GetValue<string>() is { } r && r.StartsWith('#')
            ? new List<string> { r } : Refs(kv.Value)).ToList(),
        JsonArray a => a.SelectMany(Refs).ToList(),
        _ => new List<string>(),
    };

    public static int RunGuard(string[] args)
    {
        _pass = _fail = 0;
        Console.WriteLine("[schema-flatten-guard] ToolSchemas.FlattenRefs — the $ref-inlining mechanism (#451)");

        PositionalCycleArm();
        DefsArm();
        MutualRecursionArm();
        SiblingArm();
        UnresolvableArm();
        TerminatorSentenceArm();

        Console.WriteLine();
        Console.WriteLine($"=== schema-flatten-guard: {_pass} passed, {_fail} failed -> {(_fail == 0 ? "PASS" : "FAIL")} ===");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>The published shape: a self-nesting object whose inner copy back-references an ancestor by
    /// position. The pointer goes; the shape is expanded, then closed by a typed open node.</summary>
    static void PositionalCycleArm()
    {
        Console.WriteLine("── ARM 1: a positional back-reference is expanded, then closed ──");
        var doc = Parse("""
        {"type":"object","properties":{"sets":{"type":"array","items":{"type":"object","properties":{
          "path":{"type":"string"},
          "compose":{"type":"object","properties":{"sets":{"description":"nested","$ref":"#/properties/sets"}}}}}}}}
        """);

        Check("the pass reports it changed the document", ToolSchemas.FlattenRefs(doc));
        Check("no same-document $ref survives", Refs(doc).Count == 0, doc.ToJsonString());

        var inner = doc["properties"]!["sets"]!["items"]!["properties"]!["compose"]!["properties"]!["sets"];
        Check("the referenced shape is INLINED, not deleted — the inner copy declares the array's element members",
            inner?["items"]?["properties"]?["path"] is not null, inner?.ToJsonString());

        var bound = inner?["items"]?["properties"]?["compose"]?["properties"]?["sets"];
        Check("one level deeper the chain closes on an open node: typed, no items, description says nesting continues",
            bound is JsonObject b && b["type"] is not null && b["items"] is null
            && b["description"]?.GetValue<string>() is { } d && d.StartsWith("nested", StringComparison.Ordinal)
            && d.Contains("Nesting continues", StringComparison.Ordinal),
            bound?.ToJsonString());

        // A recursive parameter carrying no description of its own must still say why the node stopped
        // constraining — otherwise the bound closes silently on exactly the schemas with least to go on.
        var bare = Parse("""
        {"properties":{"sets":{"type":"array","items":{"type":"object","properties":{
          "compose":{"type":"object","properties":{"sets":{"$ref":"#/properties/sets"}}}}}}}}
        """);
        ToolSchemas.FlattenRefs(bare);
        var bareBound = bare["properties"]!["sets"]!["items"]!["properties"]!["compose"]!["properties"]!["sets"]
            ?["items"]?["properties"]?["compose"]?["properties"]?["sets"];
        Check("a description-less recursive parameter still gets the clause, not a silent close",
            bareBound?["description"]?.GetValue<string>()?.StartsWith("Nesting continues", StringComparison.Ordinal) == true,
            bareBound?.ToJsonString());
    }

    /// <summary>The standard recursive spelling other generators emit. Leaving <c>$defs</c> published would keep
    /// the recursion visible to a validator that walks definitions, so it must not survive.</summary>
    static void DefsArm()
    {
        Console.WriteLine("── ARM 2: a $defs-based cycle is inlined and the definitions are dropped ──");
        var doc = Parse("""
        {"type":"object","$defs":{"Node":{"type":"object","properties":{"child":{"$ref":"#/$defs/Node"}}}},
         "properties":{"root":{"$ref":"#/$defs/Node"}}}
        """);

        ToolSchemas.FlattenRefs(doc);
        Check("no same-document $ref survives", Refs(doc).Count == 0, doc.ToJsonString());
        Check("$defs is gone — an unreferenced definition would still carry the cycle to a validator that walks it",
            doc["$defs"] is null, doc.ToJsonString());
        Check("the definition's body was inlined at the use site",
            doc["properties"]?["root"]?["properties"]?["child"] is not null, doc["properties"]?.ToJsonString());
    }

    /// <summary>A cycle no single pointer closes: the budget is per pointer, so two pointers must not defeat it.</summary>
    static void MutualRecursionArm()
    {
        Console.WriteLine("── ARM 3: mutual recursion terminates ──");
        var doc = Parse("""
        {"$defs":{"A":{"type":"object","properties":{"b":{"$ref":"#/$defs/B"}}},
                  "B":{"type":"object","properties":{"a":{"$ref":"#/$defs/A"}}}},
         "properties":{"root":{"$ref":"#/$defs/A"}}}
        """);

        ToolSchemas.FlattenRefs(doc);
        Check("no same-document $ref survives", Refs(doc).Count == 0, doc.ToJsonString());
        Check("both sides of the cycle are spelled out before it closes",
            doc["properties"]?["root"]?["properties"]?["b"]?["properties"]?["a"] is not null,
            doc["properties"]?.ToJsonString());
    }

    /// <summary>The generator leaves members beside a <c>$ref</c>: a real one (the parameter's own description)
    /// must win over the target's, and an empty placeholder must not overwrite what the target actually says.</summary>
    static void SiblingArm()
    {
        Console.WriteLine("── ARM 4: siblings of a $ref — the local statement wins, the placeholder does not ──");
        var doc = Parse("""
        {"$defs":{"Args":{"type":"array","description":"from the definition","items":{"type":"string"}}},
         "properties":{"args":{"$ref":"#/$defs/Args","description":"this parameter's own teaching","items":{}}}}
        """);

        ToolSchemas.FlattenRefs(doc);
        var args = doc["properties"]?["args"];
        Check("the ref node's own description survives the inline",
            args?["description"]?.GetValue<string>() == "this parameter's own teaching", args?.ToJsonString());
        Check("the empty items placeholder does not overwrite the definition's real items",
            args?["items"]?["type"]?.GetValue<string>() == "string", args?.ToJsonString());
    }

    /// <summary>A pointer that resolves nowhere is a broken rebase. Publishing an open node in its place would
    /// hide it behind a schema that looks finished, so it is left for the invariant arm to fail on.</summary>
    static void UnresolvableArm()
    {
        Console.WriteLine("── ARM 5: an unresolvable pointer is left in place, not papered over ──");
        var doc = Parse("""
        {"properties":{"broken":{"$ref":"#/$defs/Missing","description":"d"},
                       "fine":{"$ref":"#/properties/leaf"},
                       "leaf":{"type":"string"}}}
        """);

        ToolSchemas.FlattenRefs(doc);
        Check("the unresolvable pointer is still there, verbatim",
            doc["properties"]?["broken"]?["$ref"]?.GetValue<string>() == "#/$defs/Missing",
            doc["properties"]?["broken"]?.ToJsonString());
        Check("…and it did not stop the resolvable one beside it from being inlined",
            doc["properties"]?["fine"]?["type"]?.GetValue<string>() == "string",
            doc["properties"]?["fine"]?.ToJsonString());
    }

    /// <summary>The terminator writes a SENTENCE into the published schema — "nesting continues below this level
    /// … it is accepted but not spelled out again here". That is a claim about what the caller may send, so it is
    /// measured rather than asserted: the strict element reader the ToolSchemas header names is driven with a
    /// compose chain nested well past any bound, and the innermost node must survive the read intact.
    /// <para>There is no wire-side twin, deliberately: <c>ops</c> is declared <c>JsonElement</c>, so the SDK binder
    /// accepts any shape at any depth and an arm over it could not fail. This reader IS the gate. Nor does either
    /// check claim the deep struct is SEMANTICALLY buildable — that is content, decided by the engine, and a
    /// refusal about it names the content.</para></summary>
    static void TerminatorSentenceArm()
    {
        Console.WriteLine("── ARM 6: the sentence the terminator publishes is true of the reader ──");

        // 6 levels of compose: three past where the schema stops spelling the shape out.
        var (ops, innermost) = DeepComposeOps(6);
        var (items, error) = ListParams.Read<ApplyOp>(
            JsonDocument.Parse(ops).RootElement, "ops", "{formid, field_path, …}");

        Check("a compose nested past the bound is READ, not refused on its shape", error is null, error);

        var node = items?[0].Compose;
        for (var level = 1; level < 6 && node is not null; level++) node = node.Sets?[0].Compose;
        Check($"…and the innermost level survives the read intact (type '{innermost}')",
            node?.Type == innermost, node?.Type ?? "<the chain did not reach level 6>");

        // Past the JSON reader's OWN depth ceiling (STJ's MaxDepth, shared by the transport parse) the read must
        // still fail loudly. Parsed with headroom here so the READER is what answers rather than this probe's parse.
        var (past, _) = DeepComposeOps(30);
        var wide = JsonDocument.Parse(past, new JsonDocumentOptions { MaxDepth = 512 }).RootElement;
        var (deepItems, deepError) = ListParams.Read<ApplyOp>(wide, "ops", "{formid, field_path, …}");
        Check("past the reader's own depth ceiling it is a NAMED refusal, never a silent truncation",
            deepItems is null && deepError is not null && deepError.StartsWith("ops could not be parsed", StringComparison.Ordinal),
            deepError ?? "<read succeeded — the ceiling moved, so the arm below is measuring nothing>");
        Check("…and that refusal points AT the nesting it stopped on, not just at the call",
            deepError?.Contains("compose.sets[0].compose", StringComparison.Ordinal) == true, deepError);
    }

    /// <summary>One <c>ops</c> array whose single op carries <paramref name="levels"/> of nested compose, each
    /// level's type naming its own depth so the innermost is identifiable. Returns the JSON and that type name.</summary>
    internal static (string Json, string InnermostType) DeepComposeOps(int levels)
    {
        var innermost = "Level" + levels;
        var compose = "{\"type\":\"" + innermost + "\",\"fields\":{\"Number\":\"1\"}}";
        for (var level = levels - 1; level >= 1; level--)
            compose = "{\"type\":\"Level" + level + "\",\"sets\":[{\"path\":\"Data\",\"compose\":" + compose + "}]}";
        return ("[{\"formid\":\"012E46:Skyrim.esm\",\"field_path\":\"Ranks\",\"op\":\"Add\",\"compose\":" + compose + "}]",
                innermost);
    }
}

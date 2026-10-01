using System.Text.Json;
using System.Text.Json.Nodes;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The <c>$ref</c> flattening pass that publishes tool schemas without pointers: a positional back-reference,
/// a <c>$defs</c> cycle and mutual recursion are inlined and closed on an open node; the ref node's own members win
/// over an empty placeholder; a form the pass does not handle is left in place; the "nesting continues" sentence it
/// publishes is true of the strict reader; and the generator's real emission stays inside the grammar the pass reads.</summary>
[Trait("tier", "unit")]
public sealed class SchemaRefFlattenTests
{
    static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;

    /// <summary>Every same-document <c>$ref</c> left in a document.</summary>
    static List<string> Refs(JsonNode? node) => node switch
    {
        JsonObject o => o.SelectMany(kv => kv.Key == "$ref" && kv.Value is JsonValue v && v.TryGetValue<string>(out var r) && r.StartsWith('#')
            ? new List<string> { r } : Refs(kv.Value)).ToList(),
        JsonArray a => a.SelectMany(Refs).ToList(),
        _ => new List<string>(),
    };

    // Probe ARM 1: "the pass reports it changed the document", "no same-document $ref survives", "the referenced shape is
    // INLINED, not deleted", "one level deeper the chain closes on an open node: typed, no items, description says
    // nesting continues".
    [Fact]
    public void APositionalBackReferenceIsInlinedOnceThenClosedOnAnOpenNode()
    {
        var doc = Parse("""
        {"type":"object","properties":{"sets":{"type":"array","items":{"type":"object","properties":{
          "path":{"type":"string"},
          "compose":{"type":"object","properties":{"sets":{"description":"nested","$ref":"#/properties/sets"}}}}}}}}
        """);

        Assert.True(ToolSchemas.FlattenRefs(doc));
        Assert.Empty(Refs(doc));
        var inner = doc["properties"]!["sets"]!["items"]!["properties"]!["compose"]!["properties"]!["sets"];
        Assert.NotNull(inner?["items"]?["properties"]?["path"]);
        var bound = Assert.IsType<JsonObject>(inner?["items"]?["properties"]?["compose"]?["properties"]?["sets"]);
        Assert.NotNull(bound["type"]);
        Assert.Null(bound["items"]);
        var d = bound["description"]!.GetValue<string>();
        Assert.StartsWith("nested", d);
        Assert.Contains("Nesting continues", d);
    }

    // Probe ARM 1: "a description-less recursive parameter still gets the clause, not a silent close".
    [Fact]
    public void ADescriptionLessRecursiveParameterStillGetsTheNestingClause()
    {
        var bare = Parse("""
        {"properties":{"sets":{"type":"array","items":{"type":"object","properties":{
          "compose":{"type":"object","properties":{"sets":{"$ref":"#/properties/sets"}}}}}}}}
        """);
        ToolSchemas.FlattenRefs(bare);
        var bound = bare["properties"]!["sets"]!["items"]!["properties"]!["compose"]!["properties"]!["sets"]
            ?["items"]?["properties"]?["compose"]?["properties"]?["sets"];

        Assert.StartsWith("Nesting continues", bound?["description"]?.GetValue<string>());
    }

    // Probe ARM 2: "no same-document $ref survives", "$defs is gone", "the definition's body was inlined at the use site".
    [Fact]
    public void ADefsCycleIsInlinedAndTheDefinitionsAreDropped()
    {
        var doc = Parse("""
        {"type":"object","$defs":{"Node":{"type":"object","properties":{"child":{"$ref":"#/$defs/Node"}}}},
         "properties":{"root":{"$ref":"#/$defs/Node"}}}
        """);

        ToolSchemas.FlattenRefs(doc);
        Assert.Empty(Refs(doc));
        Assert.Null(doc["$defs"]);
        Assert.NotNull(doc["properties"]?["root"]?["properties"]?["child"]);
    }

    // Probe ARM 3: "no same-document $ref survives", "both sides of the cycle are spelled out before it closes".
    [Fact]
    public void MutualRecursionTerminatesWithBothSidesSpelledOut()
    {
        var doc = Parse("""
        {"$defs":{"A":{"type":"object","properties":{"b":{"$ref":"#/$defs/B"}}},
                  "B":{"type":"object","properties":{"a":{"$ref":"#/$defs/A"}}}},
         "properties":{"root":{"$ref":"#/$defs/A"}}}
        """);

        ToolSchemas.FlattenRefs(doc);
        Assert.Empty(Refs(doc));
        Assert.NotNull(doc["properties"]?["root"]?["properties"]?["b"]?["properties"]?["a"]);
    }

    // Probe ARM 4: "the ref node's own description survives the inline", "the empty items placeholder does not overwrite
    // the definition's real items".
    [Fact]
    public void TheRefNodesOwnDescriptionWinsAndAnEmptyItemsPlaceholderDoesNot()
    {
        var doc = Parse("""
        {"$defs":{"Args":{"type":"array","description":"from the definition","items":{"type":"string"}}},
         "properties":{"args":{"$ref":"#/$defs/Args","description":"this parameter's own teaching","items":{}}}}
        """);

        ToolSchemas.FlattenRefs(doc);
        var args = doc["properties"]?["args"];
        Assert.Equal("this parameter's own teaching", args?["description"]?.GetValue<string>());
        Assert.Equal("string", args?["items"]?["type"]?.GetValue<string>());
    }

    // Probe ARM 5: "the unresolvable pointer is still there, verbatim", "…and it did not stop the resolvable one beside
    // it from being inlined".
    [Fact]
    public void AnUnresolvablePointerIsLeftVerbatimAndTheResolvableOneBesideItIsInlined()
    {
        var doc = Parse("""
        {"properties":{"broken":{"$ref":"#/$defs/Missing","description":"d"},
                       "fine":{"$ref":"#/properties/leaf"},
                       "leaf":{"type":"string"}}}
        """);

        ToolSchemas.FlattenRefs(doc);
        Assert.Equal("#/$defs/Missing", doc["properties"]?["broken"]?["$ref"]?.GetValue<string>());
        Assert.Equal("string", doc["properties"]?["fine"]?["type"]?.GetValue<string>());
    }

    // Probe ARM 5: "a non-string $ref does not throw the pass", "…each non-string $ref is left exactly as it was", "…and
    // the string pointer beside them is still inlined".
    [Fact]
    public void NonStringRefsDoNotThrowAndAreLeftAsTheyWere()
    {
        var doc = Parse("""
        {"properties":{"b":{"$ref":true},"n":{"$ref":3},"o":{"$ref":{"a":1}},"a":{"$ref":[1]},
                       "fine":{"$ref":"#/properties/leaf"},
                       "leaf":{"type":"string"}}}
        """);

        ToolSchemas.FlattenRefs(doc);
        var p = doc["properties"]!;
        Assert.Equal(("true", "3", "{\"a\":1}", "[1]"),
            (p["b"]?["$ref"]?.ToJsonString(), p["n"]?["$ref"]?.ToJsonString(), p["o"]?["$ref"]?.ToJsonString(), p["a"]?["$ref"]?.ToJsonString()));
        Assert.Equal("string", p["fine"]?["type"]?.GetValue<string>());
    }

    /// <summary>One ops array whose single op carries <paramref name="levels"/> of nested compose, each level's type
    /// naming its depth so the innermost is identifiable.</summary>
    static (string Json, string InnermostType) DeepComposeOps(int levels)
    {
        var innermost = "Level" + levels;
        var compose = "{\"type\":\"" + innermost + "\",\"fields\":{\"Number\":\"1\"}}";
        for (var level = levels - 1; level >= 1; level--)
            compose = "{\"type\":\"Level" + level + "\",\"sets\":[{\"path\":\"Data\",\"compose\":" + compose + "}]}";
        return ("[{\"formid\":\"012E46:Skyrim.esm\",\"field_path\":\"Ranks\",\"op\":\"Add\",\"compose\":" + compose + "}]", innermost);
    }

    // Probe ARM 6: "a compose nested past the bound is READ, not refused on its shape", "…and the innermost level survives
    // the read intact".
    [Fact]
    public void AComposeNestedPastTheSchemaBoundIsReadWithItsInnermostLevelIntact()
    {
        var (ops, innermost) = DeepComposeOps(6);
        var (items, error) = ListParams.Read<ApplyOp>(JsonDocument.Parse(ops).RootElement, "ops", "{formid, field_path, …}");

        Assert.Null(error);
        var node = items?[0].Compose;
        for (var level = 1; level < 6 && node is not null; level++) node = node.Sets?[0].Compose;
        Assert.Equal(innermost, node?.Type);
    }

    // Probe ARM 6: "past the reader's own depth ceiling it is a NAMED refusal, never a silent truncation", "…and that
    // refusal points AT the nesting it stopped on".
    [Fact]
    public void PastTheReadersDepthCeilingTheRefusalIsNamedAndPointsAtTheNesting()
    {
        var (ops, _) = DeepComposeOps(30);
        var wide = JsonDocument.Parse(ops, new JsonDocumentOptions { MaxDepth = 512 }).RootElement;
        var (items, error) = ListParams.Read<ApplyOp>(wide, "ops", "{formid, field_path, …}");

        Assert.Null(items);
        Assert.StartsWith("ops could not be parsed", error);
        Assert.Contains("compose.sets[0].compose", error);
    }

    /// <summary>Every object carrying a <c>$ref</c>, split by whether it sits in a schema position or under a keyword
    /// whose value is data.</summary>
    static void CollectRefNodes(JsonNode? node, string path, string? valueKeyAbove,
                                List<(string, JsonObject)> refNodes, List<string> valuePositionRefs)
    {
        string[] nonSchema = { "default", "enum", "const", "examples" };
        switch (node)
        {
            case JsonObject obj:
                if (obj.ContainsKey("$ref"))
                {
                    if (valueKeyAbove is not null) valuePositionRefs.Add($"{path} (under {valueKeyAbove})");
                    else refNodes.Add((path, obj));
                }
                foreach (var kv in obj.ToList())
                    CollectRefNodes(kv.Value, $"{path}/{kv.Key}",
                        valueKeyAbove ?? (nonSchema.Contains(kv.Key) ? kv.Key : null), refNodes, valuePositionRefs);
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++) CollectRefNodes(arr[i], $"{path}/{i}", valueKeyAbove, refNodes, valuePositionRefs);
                break;
        }
    }

    static bool HasKey(JsonNode? node, string key) => node switch
    {
        JsonObject obj => obj.ContainsKey(key) || obj.Any(kv => HasKey(kv.Value, key)),
        JsonArray arr => arr.Any(n => HasKey(n, key)),
        _ => false,
    };

    // Probe ARM 7: "the pre-flatten surface yields the registered tools", "…and they carry $ref sites", "no tool's
    // generated schema emits $defs", "no $ref sits in a non-schema position", "every $ref is a STRING in pointer form",
    // "every reference token is non-empty and unescaped", "every ref node carries only the members the merge rule was
    // written for", "every items= sibling is the empty placeholder", "every target resolves to an OBJECT schema".
    // Strengthened: the input replays both union passes PublishSchemas runs before FlattenRefs (the probe replayed only
    // the @file union pass), so the grammar is checked on exactly what the pass reads.
    [Fact]
    public void TheGeneratorsEmissionStaysInsideTheGrammarTheFlattenPassReads()
    {
        var tools = PreFlattenSchemas.Read();
        var violations = new List<string>();
        var refSites = 0;

        foreach (var tool in tools)
        {
            var doc = (JsonObject)tool.Schema.DeepClone();
            var unions = ToolSchemas.FileListParams.Where(p => p.Tool == tool.Name).ToList();
            if (unions.Count > 0) ToolSchemas.RewriteFileListUnions(doc, unions);
            var shapes = ToolSchemas.ShapeUnionParams.Where(p => p.Tool == tool.Name).ToList();
            if (shapes.Count > 0) ToolSchemas.RewriteShapeUnions(doc, shapes);

            var refNodes = new List<(string Path, JsonObject Node)>();
            var valuePosition = new List<string>();
            CollectRefNodes(doc, "#", null, refNodes, valuePosition);
            if (HasKey(doc, "$defs")) violations.Add($"{tool.Name}: emits $defs");
            violations.AddRange(valuePosition.Select(v => $"{tool.Name} {v}: $ref in a non-schema position"));

            foreach (var (path, node) in refNodes)
            {
                refSites++;
                var where = $"{tool.Name} {path}";
                var spelling = node["$ref"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                if (spelling is null || !(spelling == "#" || spelling.StartsWith("#/", StringComparison.Ordinal)))
                {
                    violations.Add($"{where}: not a pointer-form string");
                    continue;
                }
                var tokens = spelling.Length > 1 ? spelling[1..].Split('/') : [];
                if (!tokens.Skip(1).All(t => t.Length > 0 && !t.Contains('%') && !t.Contains('~')))
                    violations.Add($"{where}: empty or escaped token in {spelling}");
                var extra = node.Select(kv => kv.Key).Where(k => k is not ("$ref" or "description" or "items")).ToList();
                if (extra.Count > 0) violations.Add($"{where}: extra members {string.Join(",", extra)}");
                if (node["items"] is not (null or JsonObject { Count: 0 })) violations.Add($"{where}: a real items sibling");
                if (ToolSchemas.Resolve(doc, spelling) is not JsonObject) violations.Add($"{where}: target is not an object schema");
            }
        }

        Assert.NotEmpty(tools);
        Assert.True(refSites > 0, "no $ref sites in the pass's input, so every per-site rule ran over nothing");
        Assert.Empty(violations);
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD for <see cref="ToolSchemas"/>'s <c>$ref</c> flattening (#451) — the MECHANISM, over synthetic
/// documents. <c>binding-shim-guard</c>'s SCHEMA arms cover the invariant on the real published surface, which
/// exercises only the one shape today's DTOs generate (a positional back-reference carrying a description and an
/// empty <c>items</c> placeholder). Three of the cases below — <c>$defs</c> recursion, mutual recursion, an
/// unresolvable pointer — have no end-to-end producer and would otherwise ship unproven. The placeholder-sibling
/// merge (ARM 4) DOES have one: 20 of the 30 pre-fix published refs carried that empty <c>items</c>, and deleting
/// the merge reddens <c>binding-shim-guard</c> unaided. It is kept here because this is where the merge rule is
/// stated, not because nothing else would catch it.
///
/// These arms pin the mechanism's stated behaviour; they do NOT establish general JSON Schema conformance. What is
/// deliberately outside that behaviour is listed on <see cref="ToolSchemas"/>.
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
        EmissionGrammarArm();

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
    /// is indifferent to the argument's SHAPE and an arm over that could not fail. (Not to its depth — see the
    /// transport ceiling noted below.) This reader IS the gate. Nor does either check claim the deep struct is
    /// SEMANTICALLY buildable — that is content, decided by the engine, and a refusal about it names the
    /// content.</para></summary>
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

        // Past the JSON reader's OWN depth ceiling (STJ's MaxDepth) the read must still fail loudly. Parsed with
        // headroom here so the READER is what answers rather than this probe's parse.
        // What this does NOT model, on the INLINE lane: a request nested that deep dies at the TRANSPORT parse
        // first — measured, a default-options JsonDocument.Parse throws from ~21 compose levels — so an inline
        // caller sees an SDK-voiced parse error, not the named refusal below. Both are loud, which is what the
        // published sentence claims; only this one is ours.
        // The refusal below is NOT unreachable, though, and this arm is not decoration: the @file lane never
        // crosses the transport parse. ListParams.Read takes File.ReadAllText straight into the strict reader —
        // what came over the wire was only the short "@<path>" string — so ops="@…" naming a manifest nested past
        // STJ's MaxDepth of 64 lands on exactly this refusal. Deep composes are not idiomatic (live ones nest
        // 1–2), but a generated manifest is where depth would come from, and that is the lane it arrives on.
        var (past, _) = DeepComposeOps(30);
        var wide = JsonDocument.Parse(past, new JsonDocumentOptions { MaxDepth = 512 }).RootElement;
        var (deepItems, deepError) = ListParams.Read<ApplyOp>(wide, "ops", "{formid, field_path, …}");
        Check("past the reader's own depth ceiling it is a NAMED refusal, never a silent truncation",
            deepItems is null && deepError is not null && deepError.StartsWith("ops could not be parsed", StringComparison.Ordinal),
            deepError ?? "<read succeeded — the ceiling moved, so the arm below is measuring nothing>");
        Check("…and that refusal points AT the nesting it stopped on, not just at the call",
            deepError?.Contains("compose.sets[0].compose", StringComparison.Ordinal) == true, deepError);
    }

    /// <summary>ARM 7 — the EMISSION GRAMMAR the pass depends on, over the real pre-flatten generated surface:
    /// every registered tool's <c>ProtocolTool.InputSchema</c> as the SDK's assembly scan produced it, which is
    /// the exact input <see cref="ToolSchemas.PublishSchemas"/> reads.
    /// <para>Everything the narrowed contract on <see cref="ToolSchemas"/> declares out of scope is safe only
    /// because this generator does not emit it. That is a precondition, and before this arm it was held by review
    /// attention alone — which never carries a completeness claim. So assert the grammar itself: no <c>$defs</c>,
    /// every <c>$ref</c> in pointer form with faithful tokens, every ref node carrying only the members the merge
    /// rule was written for, every target an object schema, and no <c>$ref</c>-shaped value in a NON-schema
    /// position. A generator that drifts on an SDK bump — the shape that would resurrect #451's outage class, or
    /// worse, silently mis-normalize a published schema — goes RED here rather than at a user's server start.</para>
    /// <para>The subjects are the SURFACE, not a list (#451, "derive, don't enumerate"). This arm used to re-derive
    /// standalone schemas for six hand-named DTO types through its own <c>CreateJsonSchema</c> call — a second
    /// emission that resembled the real one but was not it, so the drift claim above was never established for the
    /// path that actually drifts, and a seventh DTO would have been covered by nobody. Reading the registered tools
    /// makes coverage a consequence of registration.</para>
    /// <para>Results are aggregated per grammar rule rather than per subject: 51 tools would otherwise print
    /// hundreds of near-identical lines, and a rule that holds over the whole population is one claim. Each rule
    /// names its violators, and the population it ran over is stated so an empty sweep cannot read as a pass.</para>
    /// <para>Deliberately NOT paired with runtime fail-safe handling: catching a malformed emission and publishing
    /// the schema unflattened would trade a loud failure for quietly republishing the recursive form (Q3).</para></summary>
    static void EmissionGrammarArm()
    {
        Console.WriteLine("── ARM 7: the generator emission grammar the pass depends on ──");

        var nonSchema = new[] { "default", "enum", "const", "examples" };
        var withDefs = new List<string>();
        var inValuePosition = new List<string>();
        var notPointerForm = new List<string>();
        var badTokens = new List<string>();
        var extraMembers = new List<string>();
        var realItems = new List<string>();
        var nonObjectTarget = new List<string>();

        var tools = PreFlattenSurface.Read();
        var refSites = 0;
        var rawSites = 0;

        foreach (var tool in tools)
        {
            // The grammar precondition is about what FlattenRefs READS, which is the document after the @file
            // union pass — not the raw scan output. Two of the five ref-carrying tools (the ones whose list
            // parameters are declared JsonElement) emit no $ref at all until RewriteFileListUnions generates
            // their element-array arm, so a raw-only scan would leave 12 of the 30 sites the pass handles
            // ungoverned, and would not see a $defs the union pass introduced either. Replaying the pass in the
            // same order the server runs it is what makes this the real input rather than a resemblance of it.
            var doc = (JsonObject)tool.Schema.DeepClone();
            rawSites += PreFlattenSurface.RefNodes(tool.Schema).Count;
            var unions = ToolSchemas.FileListParams.Where(p => p.Tool == tool.Name).ToList();
            if (unions.Count > 0) ToolSchemas.RewriteFileListUnions(doc, unions);

            var refNodes = new List<(string Path, JsonObject Node)>();
            var valuePositionRefs = new List<string>();
            CollectRefNodes(doc, "#", null, nonSchema, refNodes, valuePositionRefs);

            if (FindKey(doc, "$defs") is { } defs) withDefs.Add($"{tool.Name}: {defs}");
            foreach (var v in valuePositionRefs) inValuePosition.Add($"{tool.Name} {v}");

            foreach (var (path, node) in refNodes)
            {
                refSites++;
                var where = $"{tool.Name} {path}";
                var spelling = node["$ref"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                if (spelling is null || !(spelling == "#" || spelling.StartsWith("#/", StringComparison.Ordinal)))
                {
                    notPointerForm.Add($"{where} = {node["$ref"]?.ToJsonString() ?? "<absent>"}");
                    continue;
                }

                var tokens = spelling.Length > 1 ? spelling[1..].Split('/') : [];
                if (!tokens.Skip(1).All(t => t.Length > 0 && !t.Contains('%') && !t.Contains('~')))
                    badTokens.Add($"{where} = {spelling}");

                var extra = node.Select(kv => kv.Key).Where(k => k is not ("$ref" or "description" or "items")).ToList();
                if (extra.Count > 0) extraMembers.Add($"{where}: {string.Join(",", extra)}");

                if (node["items"] is not (null or JsonObject { Count: 0 }))
                    realItems.Add($"{where}: {node["items"]?.ToJsonString()}");

                if (ToolSchemas.Resolve(doc, spelling) is not JsonObject)
                    nonObjectTarget.Add($"{where} -> {ToolSchemas.Resolve(doc, spelling)?.ToJsonString() ?? "<does not resolve>"}");
            }
        }

        // A grammar asserted over nothing is asserted over nothing. Both populations are stated and required
        // non-empty: no tools means the surface read broke, and no ref sites means every per-site rule below
        // passed without examining anything — which is the exact shape this arm was rebuilt to stop.
        Check($"the pre-flatten surface yields the registered tools ({tools.Count} scanned)", tools.Count > 0);
        Check($"…and they carry $ref sites for the per-site rules to run over ({refSites} in the pass's input; {rawSites} straight from the assembly scan, the rest added by the @file union pass)",
            refSites > 0);

        Check($"no tool's generated schema emits $defs — the hoist path the pass carries stays dead ({tools.Count} tools)",
            withDefs.Count == 0, string.Join(" | ", withDefs.Take(3)));
        Check($"no $ref sits in a non-schema position — default/enum/const/examples ({tools.Count} tools)",
            inValuePosition.Count == 0, string.Join(" | ", inValuePosition.Take(3)));
        Check($"every $ref is a STRING in pointer form (\"#/…\"), never a plain-name anchor ({refSites} sites)",
            notPointerForm.Count == 0, string.Join(" | ", notPointerForm.Take(3)));
        Check($"every reference token is non-empty and unescaped — Resolve tokenizes literally ({refSites} sites)",
            badTokens.Count == 0, string.Join(" | ", badTokens.Take(3)));
        Check($"every ref node carries only the members the merge rule was written for ({refSites} sites)",
            extraMembers.Count == 0, string.Join(" | ", extraMembers.Take(3)));
        Check($"every items= sibling is the empty placeholder, never a real constraint ({refSites} sites)",
            realItems.Count == 0, string.Join(" | ", realItems.Take(3)));
        Check($"every target resolves to an OBJECT schema, not a boolean schema ({refSites} sites)",
            nonObjectTarget.Count == 0, string.Join(" | ", nonObjectTarget.Take(3)));
    }

    /// <summary>Every object carrying a <c>$ref</c>, split by whether it sits in a schema position or under a
    /// keyword whose value is DATA (where the pass would rewrite a caller's literal as if it were a schema).</summary>
    static void CollectRefNodes(JsonNode? node, string path, string? valueKeyAbove, string[] nonSchema,
                                List<(string, JsonObject)> refNodes, List<string> valuePositionRefs)
    {
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
                        valueKeyAbove ?? (nonSchema.Contains(kv.Key) ? kv.Key : null), nonSchema, refNodes, valuePositionRefs);
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                    CollectRefNodes(arr[i], $"{path}/{i}", valueKeyAbove, nonSchema, refNodes, valuePositionRefs);
                break;
        }
    }

    /// <summary>The first path at which <paramref name="key"/> appears anywhere in the document, or null.</summary>
    static string? FindKey(JsonNode? node, string key, string path = "#") => node switch
    {
        JsonObject obj => obj.ContainsKey(key) ? $"{path}/{key}"
            : obj.Select(kv => FindKey(kv.Value, key, $"{path}/{kv.Key}")).FirstOrDefault(h => h is not null),
        JsonArray arr => Enumerable.Range(0, arr.Count).Select(i => FindKey(arr[i], key, $"{path}/{i}")).FirstOrDefault(h => h is not null),
        _ => null,
    };

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

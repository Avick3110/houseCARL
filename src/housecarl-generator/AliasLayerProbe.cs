using System.Text.Json;
using HousecarlMcp;
using ModelContextProtocol.Protocol;

namespace HousecarlGenerator;

// ======================================================================
//  AliasLayerProbe — CI guard for the 2.0 alias layer (tool-surface-2.0 W0):
//  ToolCallShim's rename pass inverted from hand-wired SynonymGroups into
//  AliasTable, the SPEC §5.3 old → new dictionary.
//
//  WHY UNIT-LEVEL (synthetic schemas, internals via InternalsVisibleTo), where
//  the stdio arms drive the real exe: most §5.3 rows are DORMANT on today's
//  surface by design — a rename fires only when a build wave has landed the new
//  canonical name in a tool's schema. The behaviours this guard pins (plugin= →
//  source= priority, the in_place naming correction, dissolution hints) have no
//  live tool to fire on until W2/W3 land — exactly why they must be proven
//  against synthetic 2.0-shaped schemas NOW, so each wave lands on a mechanism
//  already known to catch its callers. The end-to-end proof that today's surface
//  binds byte-identically moved to src/housecarl-mcp-tests at the 1.x cut, when
//  BindingShimProbe converted whole.
//
//  Retirement note: AliasTable is build scaffolding, deleted at 2.0.0 (clean
//  cut, CHARTER_PHASE4 §3.4a). This guard's table-driven arms retire with it;
//  the normalization-bridge and no-clobber arms describe permanent mechanism.
// ======================================================================
public static class AliasLayerProbe
{
    [CiProbe("alias-layer-guard")]
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("[alias-layer-guard] the §5.3 alias layer (tool-surface-2.0 W0)");
        int failures = 0;

        // ---- schemas: today-shaped and 2.0-shaped -------------------------------------------
        var scanToday   = Schema("""{"plugins":{"type":["array","null"]},"type":{"type":["string","null"]},"conflicts_only":{"type":["boolean","null"]}}""");
        var records20   = Schema("""{"formids":{"type":["array","null"]},"types":{"type":["array","null"]},"plugins":{"type":["array","null"]},"source":{"type":["string","null"]},"where":{"type":["array","null"]},"fields_source":{"type":["string","null"]}}""");
        var readToday   = Schema("""{"formid":{"type":"string"},"depth":{"type":["integer","null"]}}""", required: """["formid"]""");
        var batchToday  = Schema("""{"formids":{"type":"array"}}""", required: """["formids"]""");
        var placeAsset  = Schema("""{"formid":{"type":["string","null"]},"asset_path":{"type":["string","null"]},"source":{"type":["string","null"]},"patch_name":{"type":["string","null"]},"into":{"type":["string","null"]}}""");
        var write20     = Schema("""{"formids":{"type":"array"},"ops":{"type":["array","null"]},"patch":{"type":["string","null"]},"in_place":{"type":["string","null"]},"acknowledge":{"type":["boolean","null"]}}""", required: """["formids"]""");
        var writeToday  = Schema("""{"formid":{"type":"string"},"operations":{"type":["array","null"]},"patch_name":{"type":["string","null"]},"in_place":{"type":["boolean","null"]},"target":{"type":["string","null"]}}""", required: """["formid"]""");
        var nifSet20    = Schema("""{"paths":{"type":"array"},"element":{"type":["string","null"]},"op":{"type":["string","null"]},"in_place":{"type":["string","null"]}}""", required: """["paths"]""");
        var removeToday = Schema("""{"formid":{"type":"string"},"patch":{"type":["string","null"]}}""", required: """["formid"]""");
        var createPluginToday = Schema("""{"plugin_name":{"type":"string"},"esl":{"type":["boolean","null"]},"author":{"type":["string","null"]},"description":{"type":["string","null"]}}""", required: """["plugin_name"]""");
        var readPluginToday = Schema("""{"plugin":{"type":"string"},"formid":{"type":["string","null"]},"mod":{"type":["string","null"]}}""", required: """["plugin"]""");
        var compactToday = Schema("""{"plugin":{"type":"string"},"esl":{"type":["boolean","null"]},"in_place":{"type":["boolean","null"]},"repoint_externals":{"type":["boolean","null"]},"acknowledge":{"type":"boolean"},"patch_name":{"type":["string","null"]}}""", required: """["plugin"]""");
        var copy20 = Schema("""{"assignments":{"type":"array"},"patch":{"type":["string","null"]},"walk":{"type":["object","null"]}}""", required: """["assignments"]""");

        // ---- A: the load-bearing §5.3 row — plugin= → source=, by priority, on a tool declaring BOTH
        //      source and plugins (2.0's records). The amended-row contract: source, never a file form,
        //      never the scan scope.
        var a = P("housecarl_records", ("plugin", "\"Requiem.esp\""));
        ToolCallShim.ResolveAliases(a, records20);
        failures += Check("A §5.3 priority: plugin= renames to source= when both source and plugins are declared",
            Has(a, "source", "\"Requiem.esp\"") && !a.Arguments!.ContainsKey("plugin") && !Has(a, "plugins", null),
            Dump(a));

        // ---- B: today's #221 tolerance preserved — plugin= → plugins= on a scan tool that declares
        //      only plugins (the old SynonymGroups behaviour, now a table row; BindingShimProbe J1
        //      proves the same end-to-end over the real exe).
        var b = P("housecarl_cross_plugin_query", ("plugin", "\"Synthetic.esp\""));
        ToolCallShim.ResolveAliases(b, scanToday);
        failures += Check("B #221 preserved: plugin= renames to plugins= on a plugins-only tool",
            Has(b, "plugins", "\"Synthetic.esp\"") && !b.Arguments!.ContainsKey("plugin"), Dump(b));

        // ---- C: the place_asset exception — its source= is a file-path operand (the copy to place),
        //      NOT the version pole, so a stray plugin= must NOT silently become a path; it stays for
        //      UnknownParameters to name.
        var c = P("housecarl_place_asset", ("plugin", "\"Some.esp\""), ("asset_path", "\"textures/t.dds\""));
        ToolCallShim.ResolveAliases(c, placeAsset);
        failures += Check("C exception: plugin= is NOT renamed to place_asset's source=",
            c.Arguments!.ContainsKey("plugin") && !c.Arguments!.ContainsKey("source"), Dump(c));
        var cRefusal = ToolCallShim.UnknownParameters(c, placeAsset);
        failures += Check("C2 …and is then refused by name", Text(cRefusal).Contains("unknown parameter") && Text(cRefusal).Contains("plugin"),
            Text(cRefusal));

        // ---- D: formid ↔ formids, both directions (the build window is symmetric).
        var d1 = P("housecarl_batch_record_detail", ("formid", "\"0F1AC1:Skyrim.esm\""));
        ToolCallShim.ResolveAliases(d1, batchToday);
        failures += Check("D1 formid= renames to a declared formids=", Has(d1, "formids", null), Dump(d1));
        var d2 = P("housecarl_read_record", ("formids", "\"0F1AC1:Skyrim.esm\""));
        ToolCallShim.ResolveAliases(d2, readToday);
        failures += Check("D2 formids= renames to a declared formid=", Has(d2, "formid", null), Dump(d2));

        // ---- E: stop-not-fall-through — with source= explicitly supplied, a stray plugin= must NOT be
        //      reinterpreted onto the lower-priority plugins= (a different axis); it stays and is named.
        var e = P("housecarl_records", ("plugin", "\"A.esp\""), ("source", "\"B.esp\""));
        ToolCallShim.ResolveAliases(e, records20);
        failures += Check("E stop: plugin= with source= supplied does not fall through to plugins=",
            e.Arguments!.ContainsKey("plugin") && !e.Arguments!.ContainsKey("plugins") && Has(e, "source", "\"B.esp\""), Dump(e));

        // ---- E2 (final review finding 7): the kind check precedes the supplied-stop — an array plugin=
        //      could never have meant the supplied string source=, so it falls through to plugins= instead
        //      of stopping the entry.
        var e2 = P("housecarl_records", ("plugin", "[\"A.esp\"]"), ("source", "\"B.esp\""));
        ToolCallShim.ResolveAliases(e2, records20);
        failures += Check("E2 kind-before-stop: array plugin= with source= supplied still lands on plugins=",
            Has(e2, "plugins", "[\"A.esp\"]") && Has(e2, "source", "\"B.esp\""), Dump(e2));

        // ---- F: the normalization bridge (permanent mechanism) — form_id= → formid=.
        var f = P("housecarl_read_record", ("form_id", "\"0F1AC1:Skyrim.esm\""));
        ToolCallShim.ResolveAliases(f, readToday);
        failures += Check("F bridge: form_id= renames to formid=", Has(f, "formid", null), Dump(f));

        // ---- G: active drift fix — patch_name= binds on a tool that declares bare patch= (remove_record
        //      today; the §5 census's seventh spelling), and the reverse patch= binds on patch_name tools.
        var g1 = P("housecarl_remove_record", ("formid", "\"800:X.esp\""), ("patch_name", "\"Out.esp\""));
        ToolCallShim.ResolveAliases(g1, removeToday);
        failures += Check("G1 drift: patch_name= renames to a declared patch=", Has(g1, "patch", "\"Out.esp\""), Dump(g1));
        var g2 = P("housecarl_set_field", ("formid", "\"800:X.esp\""), ("patch", "\"Out.esp\""));
        ToolCallShim.ResolveAliases(g2, writeToday);
        failures += Check("G2 reverse: patch= renames to a declared patch_name=", Has(g2, "patch_name", "\"Out.esp\""), Dump(g2));

        // ---- H: ops/operations both directions.
        var h1 = P("housecarl_bulk_apply", ("formid", "\"800:X.esp\""), ("ops", "[]"));
        ToolCallShim.ResolveAliases(h1, writeToday);
        failures += Check("H1 ops= renames to a declared operations=", h1.Arguments!.ContainsKey("operations"), Dump(h1));
        var h2 = P("housecarl_apply", ("formids", "[\"800:X.esp\"]"), ("operations", "[]"));
        ToolCallShim.ResolveAliases(h2, write20);
        failures += Check("H2 operations= renames to a declared ops=", h2.Arguments!.ContainsKey("ops"), Dump(h2));

        // ---- I: LaneCorrections — dormant on the 1.x bool (today's surface byte-identical).
        var i1 = P("housecarl_set_field", ("formid", "\"800:X.esp\""), ("in_place", "true"), ("target", "\"X.esp\""));
        var i1r = ToolCallShim.LaneCorrections(i1, writeToday);
        failures += Check("I1 dormant: bool-declared in_place=true passes untouched (no refusal, no rewrite)",
            i1r is null && Has(i1, "in_place", "true") && Has(i1, "target", "\"X.esp\""), Dump(i1) + " " + Text(i1r));

        // ---- I2: the COMPLETE 1.x pair on a 2.0 string in_place — auto-mapped, old callers keep working.
        var i2 = P("housecarl_apply", ("formids", "[\"800:X.esp\"]"), ("in_place", "true"), ("target", "\"X.esp\""));
        var i2r = ToolCallShim.LaneCorrections(i2, write20);
        failures += Check("I2 auto-map: in_place=true + target=\"X.esp\" becomes in_place=\"X.esp\"",
            i2r is null && Has(i2, "in_place", "\"X.esp\"") && !i2.Arguments!.ContainsKey("target"), Dump(i2) + " " + Text(i2r));

        // ---- I3: the BARE bool — the §5.2 naming correction, never a type error.
        var i3 = P("housecarl_apply", ("formids", "[\"800:X.esp\"]"), ("in_place", "true"));
        var i3r = ToolCallShim.LaneCorrections(i3, write20);
        failures += Check("I3 correction: bare in_place=true is refused with the naming correction",
            Text(i3r).Contains("in_place=\"X.esp\"") && Text(i3r).Contains("FILE"), Text(i3r));

        // ---- I4: the quoted spelling — in_place="true" is the same mistake, not a file named true.
        var i4 = P("housecarl_apply", ("formids", "[\"800:X.esp\"]"), ("in_place", "\"true\""));
        var i4r = ToolCallShim.LaneCorrections(i4, write20);
        failures += Check("I4 correction: quoted in_place=\"true\" gets the same naming correction, never binds as a file",
            Text(i4r).Contains("in_place=\"X.esp\""), Text(i4r));

        // ---- I5: in_place=false = the 1.x default-lane spelling — dropped, call proceeds on the default.
        var i5 = P("housecarl_apply", ("formids", "[\"800:X.esp\"]"), ("in_place", "false"));
        var i5r = ToolCallShim.LaneCorrections(i5, write20);
        failures += Check("I5 default: bare in_place=false is dropped (the 2.0 default lane)",
            i5r is null && !i5.Arguments!.ContainsKey("in_place"), Dump(i5) + " " + Text(i5r));

        // ---- I6: false + stray target is contradictory — refused by name, not guessed at.
        var i6 = P("housecarl_apply", ("formids", "[\"800:X.esp\"]"), ("in_place", "false"), ("target", "\"X.esp\""));
        var i6r = ToolCallShim.LaneCorrections(i6, write20);
        failures += Check("I6 contradiction: in_place=false + target= is refused by name",
            Text(i6r).Contains("contradictory"), Text(i6r));

        // ---- I7: a real file name is this parameter's 2.0 meaning — untouched.
        var i7 = P("housecarl_apply", ("formids", "[\"800:X.esp\"]"), ("in_place", "\"X.esp\""));
        var i7r = ToolCallShim.LaneCorrections(i7, write20);
        failures += Check("I7 control: in_place=\"X.esp\" passes untouched", i7r is null && Has(i7, "in_place", "\"X.esp\""), Dump(i7));

        // ---- J: the full old nif_set spelling on the 2.0 schema — target= is the ELEMENT there (§5.2:
        //      element outranks in_place in the target entry), and the leftover bare in_place=true then
        //      gets the naming correction (the subject-implicit tools' in_place must NAME the subject).
        var j = P("housecarl_nif_set", ("paths", "[\"meshes/a.nif\"]"), ("target", "\"HeadParts\""), ("in_place", "true"));
        ToolCallShim.ResolveAliases(j, nifSet20);
        var jr = ToolCallShim.LaneCorrections(j, nifSet20);
        failures += Check("J nif_set: old target= becomes element=, and the bare in_place=true is then corrected by name",
            Has(j, "element", "\"HeadParts\"") && !j.Arguments!.ContainsKey("target") && Text(jr).Contains("in_place=\"X.esp\""),
            Dump(j) + " " + Text(jr));

        // ---- K: dissolution hints — gated on the replacement grammar's carrier being declared.
        var k1 = P("housecarl_records", ("formids", "[\"800:X.esp\"]"), ("editorid_contains", "\"Iron\""));
        var k1r = ToolCallShim.UnknownParameters(k1, records20);
        failures += Check("K1 hint: editorid_contains on a where-bearing tool carries the where=[…] migration hint",
            Text(k1r).Contains("unknown parameter") && Text(k1r).Contains("editorid contains"), Text(k1r));
        var k2 = P("housecarl_read_plugin_file", ("formid", "\"800:X.esp\""), ("editorid_contains", "\"Iron\""));
        var k2r = ToolCallShim.UnknownParameters(k2, readToday);
        failures += Check("K2 gate: the same stray on a tool with NO where= gets the plain unknown refusal, no hint",
            Text(k2r).Contains("unknown parameter") && !Text(k2r).Contains("editorid contains"), Text(k2r));
        var k3 = P("housecarl_records", ("formids", "[\"800:X.esp\"]"), ("winner_fields", "true"));
        var k3r = ToolCallShim.UnknownParameters(k3, records20);
        // Assert on wording unique to the HINT (review R2): records20 declares fields_source, so the
        // supported-parameter list alone would satisfy a Contains("fields_source") even with the hint gone.
        failures += Check("K3 hint: winner_fields on a fields_source-bearing tool points at the pole",
            Text(k3r).Contains("display pole spells it"), Text(k3r));

        // ---- L: a declared parameter is never treated as an alias (mechanism guard, J4's unit twin).
        var l = P("housecarl_read_plugin_file", ("formid", "\"800:X.esp\""));
        ToolCallShim.ResolveAliases(l, readToday);
        failures += Check("L declared: a real formid= is untouched", Has(l, "formid", "\"800:X.esp\""), Dump(l));

        // ---- M (review F1): the 1.x four-spelling clique is a full set of edges, as SynonymGroups had it —
        //      plugin= binds on create_plugin's plugin_name, and plugin_name= on the bare-plugin tools.
        var m1 = P("housecarl_create_plugin", ("plugin", "\"MyTrigger\""));
        ToolCallShim.ResolveAliases(m1, createPluginToday);
        failures += Check("M1 clique: plugin= renames to create_plugin's declared plugin_name=",
            Has(m1, "plugin_name", "\"MyTrigger\""), Dump(m1));
        var m2 = P("housecarl_read_plugin_file", ("plugin_name", "\"Skyrim.esm\""), ("formid", "\"0F1AC1:Skyrim.esm\""));
        ToolCallShim.ResolveAliases(m2, readPluginToday);
        failures += Check("M2 clique: plugin_name= renames to read_plugin_file's declared plugin=",
            Has(m2, "plugin", "\"Skyrim.esm\""), Dump(m2));
        var m3 = P("housecarl_create_plugin", ("plugins", "\"MyTrigger\""));
        ToolCallShim.ResolveAliases(m3, createPluginToday);
        failures += Check("M3 clique: plugins= renames to create_plugin's declared plugin_name=",
            Has(m3, "plugin_name", "\"MyTrigger\""), Dump(m3));

        // ---- N (review F2): a BARE stray target= on a 2.0 write tool must NOT be renamed onto in_place
        //      (that would engage the opt-in overwrite lane from a call that never spelled it) — it gets
        //      the naming correction instead.
        var n1 = P("housecarl_apply", ("formids", "[\"800:X.esp\"]"), ("target", "\"CoolWeapons.esp\""));
        ToolCallShim.ResolveAliases(n1, write20);
        failures += Check("N1 lane-safety: bare target= is not renamed onto in_place",
            n1.Arguments!.ContainsKey("target") && !n1.Arguments!.ContainsKey("in_place"), Dump(n1));
        var n1r = ToolCallShim.LaneCorrections(n1, write20);
        failures += Check("N1b …and is refused with the naming correction, not silently laned",
            Text(n1r).Contains("in_place=\"X.esp\"") && Text(n1r).Contains("does not select the lane"), Text(n1r));

        // ---- N2 (review F3): on today's compact_plugin (no target=, 1.x bool in_place=) the target row
        //      must not fire at all — the pre-PR named-unknown with the supported list is the answer.
        var n2 = P("housecarl_compact_plugin", ("plugin", "\"X.esp\""), ("target", "\"X.esp\""));
        ToolCallShim.ResolveAliases(n2, compactToday);
        var n2Lane = ToolCallShim.LaneCorrections(n2, compactToday);
        var n2r = ToolCallShim.UnknownParameters(n2, compactToday);
        failures += Check("N2 today-dormant: compact_plugin target= stays a named unknown (no rename, no lane pass)",
            Has(n2, "in_place", null) == false && n2Lane is null
            && Text(n2r).Contains("unknown parameter") && Text(n2r).Contains("target") && Text(n2r).Contains("repoint_externals"),
            Dump(n2) + " " + Text(n2r));

        // ---- O (review F5): a rename never fires into a guaranteed kind mismatch — the incompatible
        //      stray keeps its OWN name in the refusal, with the supported list intact.
        var o1 = P("housecarl_cross_plugin_query", ("types", "[\"ARMO\",\"WEAP\"]"));
        ToolCallShim.ResolveAliases(o1, scanToday);
        var o1r = ToolCallShim.UnknownParameters(o1, scanToday);
        failures += Check("O1 kind-gate: array types= is NOT renamed onto string type=; refusal names types",
            o1.Arguments!.ContainsKey("types") && Text(o1r).Contains("types"), Dump(o1) + " " + Text(o1r));
        var o2 = P("housecarl_read_record", ("formids", "[\"A\",\"B\"]"));
        ToolCallShim.ResolveAliases(o2, readToday);
        failures += Check("O2 kind-gate: array formids= is NOT renamed onto string formid=",
            o2.Arguments!.ContainsKey("formids") && !o2.Arguments!.ContainsKey("formid"), Dump(o2));
        var o3 = P("housecarl_records", ("plugin", "[\"A.esp\",\"B.esp\"]"));
        ToolCallShim.ResolveAliases(o3, records20);
        failures += Check("O3 kind fall-through: array plugin= skips the string source pole and lands on plugins=",
            Has(o3, "plugins", null) && !o3.Arguments!.ContainsKey("source"), Dump(o3));

        // ---- Q (review F6): target_formid is NOT a mechanical rename while target= means the in-place
        //      filename on the 1.x write tools; on the 2.0 copy shape it rides the assignments-gated hint.
        var q1 = P("housecarl_set_field", ("formid", "\"800:X.esp\""), ("target_formid", "\"0F1AC1:Skyrim.esm\""));
        ToolCallShim.ResolveAliases(q1, writeToday);
        var q1r = ToolCallShim.UnknownParameters(q1, writeToday);
        failures += Check("Q1 no-rename: target_formid= stays its own named unknown on a target-bearing write tool",
            q1.Arguments!.ContainsKey("target_formid") && !Has(q1, "target", "\"0F1AC1:Skyrim.esm\"")
            && Text(q1r).Contains("target_formid"), Dump(q1) + " " + Text(q1r));
        var q2 = P("housecarl_copy", ("target_formid", "\"0F1AC1:Skyrim.esm\""));
        var q2r = ToolCallShim.UnknownParameters(q2, copy20);
        // Hint-unique wording (review R2): copy20's supported list already prints "assignments", so that
        // substring proves nothing about the Dissolutions row.
        failures += Check("Q2 hint: on the assignments-bearing copy shape, target_formid= carries the zip hint",
            Text(q2r).Contains("zip carries target="), Text(q2r));

        // ---- R (re-review R1): the kind gate must NOT reach the normalization bridge — a case/underscore
        //      variant names the RIGHT parameter, so even an unbindable value renames, letting the type
        //      refusal name the real fault (the value) instead of denying the parameter exists.
        var r1 = P("housecarl_cross_plugin_query", ("conflicts_Only", "\"yes\""));
        ToolCallShim.ResolveAliases(r1, scanToday);
        failures += Check("R1 bridge ungated: conflicts_Only=\"yes\" still renames to conflicts_only despite the unbindable value",
            Has(r1, "conflicts_only", "\"yes\"") && !r1.Arguments!.ContainsKey("conflicts_Only"), Dump(r1));

        Console.WriteLine(failures == 0
            ? "[alias-layer-guard] PASS — the §5.3 alias layer renames, corrects, and hints as chartered."
            : $"[alias-layer-guard] FAIL — {failures} case(s) regressed.");
        return failures == 0 ? 0 : 1;
    }

    // ---- helpers --------------------------------------------------------------------------------

    static int Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
        if (!ok) Console.WriteLine($"      got: {detail}");
        return ok ? 0 : 1;
    }

    /// <summary>A published-schema-shaped InputSchema: object + properties + additionalProperties:false.</summary>
    static JsonElement Schema(string propertiesJson, string required = "[]")
    {
        using var doc = JsonDocument.Parse(
            $$"""{"type":"object","properties":{{propertiesJson}},"required":{{required}},"additionalProperties":false}""");
        return doc.RootElement.Clone();
    }

    /// <summary>A CallToolRequestParams with raw-JSON argument values.</summary>
    static CallToolRequestParams P(string tool, params (string Key, string RawJson)[] args)
    {
        var dict = new Dictionary<string, JsonElement>();
        foreach (var (key, raw) in args)
        {
            using var doc = JsonDocument.Parse(raw);
            dict[key] = doc.RootElement.Clone();
        }
        return new CallToolRequestParams { Name = tool, Arguments = dict };
    }

    /// <summary>Whether an argument exists (and, when rawJson is non-null, equals that raw JSON).</summary>
    static bool Has(CallToolRequestParams p, string key, string? rawJson)
        => p.Arguments is { } a && a.TryGetValue(key, out var v) && (rawJson is null || v.GetRawText() == rawJson);

    static string Dump(CallToolRequestParams p)
        => p.Arguments is not { Count: > 0 } a ? "(no arguments)"
           : string.Join(", ", a.Select(kv => $"{kv.Key}={kv.Value.GetRawText()}"));

    static string Text(CallToolResult? r)
    {
        if (r?.Content is not { } content) return "(null)";
        foreach (var block in content)
            if (block is TextContentBlock t) return t.Text;
        return "(no text block)";
    }
}

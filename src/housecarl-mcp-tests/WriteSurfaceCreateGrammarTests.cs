using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.WriteSurfaceReads;

namespace HousecarlMcpTests;

/// <summary>housecarl_create's records grammar: a set of one, many in one call, the nested one-shot, @file, and the
/// strict element reader's named refusals. Migrated from write-surface-guard's create-grammar arm.</summary>
[Trait("tier", "integration")]
public sealed class WriteSurfaceCreateGrammarTests : IClassFixture<WriteSurfaceWorld>
{
    readonly WriteSurfaceWorld _w;
    public WriteSurfaceCreateGrammarTests(WriteSurfaceWorld w) => _w = w;

    // probe: one record is a set of one: a single Keyword lands in a new patch with its editorid
    [Fact]
    public void OneRecordIsASetOfOne()
    {
        var r = CreateTools.Create(_w.Svc, records: Json("""[{"record_type":"Keyword","editorid":"W2KwOne"}]"""), patch: "W2One");
        var path = _w.ArtifactPathFrom(r);
        Assert.NotNull(path);
        Assert.Contains("W2KwOne", EditorIdsIn(path!));
    }

    // probe: many records in ONE call, with ops= setting the new record's fields
    [Fact]
    public void ManyRecordsInOneCallWithOps()
    {
        var r = CreateTools.Create(_w.Svc, records: Json("""
            [{"record_type":"Keyword","editorid":"W2KwA"},
             {"record_type":"Weapon","editorid":"W2WeapA","ops":[{"field_path":"Name","value":"Guard Blade"},
                                                                 {"field_path":"BasicStats.Damage","value":"33"}]}]
            """), patch: "W2Many");
        var path = _w.ArtifactPathFrom(r);
        Assert.NotNull(path);
        var ids = EditorIdsIn(path!);
        Assert.Contains("W2KwA", ids);
        Assert.Contains("W2WeapA", ids);
        Assert.Contains("Guard Blade", r);
    }

    // probe: the nested one-shot: a child parented on a same-call sibling, with an '@editorid' link value
    [Fact]
    public void NestedOneShotParentsOnASameCallSibling()
    {
        var r = CreateTools.Create(_w.Svc, records: Json("""
            [{"record_type":"DialogTopic","editorid":"W2Topic"},
             {"record_type":"DialogResponses","editorid":"W2Topic_L1","parent":"W2Topic",
              "ops":[{"field_path":"Topic","value":"@W2Topic"}]}]
            """), patch: "W2Nested");
        var path = _w.ArtifactPathFrom(r);
        Assert.NotNull(path);
        var ids = EditorIdsIn(path!);
        Assert.Contains("W2Topic", ids);
        Assert.Contains("W2Topic_L1", ids);
    }

    // probe: records="@<path>" reads the SAME array from a JSON manifest on disk
    [Fact]
    public void RecordsAtFileReadsTheManifest()
    {
        var manifest = Path.Combine(_w.Root, "records-atfile.json");
        File.WriteAllText(manifest, """[{"record_type":"Keyword","editorid":"W2KwFromFile"}]""");
        var r = CreateTools.Create(_w.Svc, records: Json($"\"@{JsonPath(manifest)}\""), patch: "W2File");
        var path = _w.ArtifactPathFrom(r);
        Assert.NotNull(path);
        Assert.Contains("W2KwFromFile", EditorIdsIn(path!));
    }

    // probe: a MIXED inline/@file records array is refused by name, never half-honored
    [Fact]
    public void MixedInlineAndAtFileIsRefused()
    {
        var manifest = Path.Combine(_w.Root, "records-mixed.json");
        File.WriteAllText(manifest, """[{"record_type":"Keyword","editorid":"W2KwFromFile2"}]""");
        var r = CreateTools.Create(_w.Svc,
            records: Json($$"""["@{{JsonPath(manifest)}}", {"record_type":"Keyword","editorid":"W2Mixed"}]"""));
        Assert.StartsWith("error:", r);
        Assert.Contains("cannot be mixed with inline elements", r);
    }

    // probe: records=[] is refused by name, not read as absent
    [Fact]
    public void EmptyRecordsArrayIsRefused()
    {
        var r = CreateTools.Create(_w.Svc, records: Json("[]"));
        Assert.StartsWith("error:", r);
        Assert.Contains("empty array", r);
    }

    // probe: no records= at all: refused naming the parameter and the @file alternative
    [Fact]
    public void NoRecordsIsRefusedNamingTheParameter()
    {
        var r = CreateTools.Create(_w.Svc, patch: "W2None");
        Assert.StartsWith("error:", r);
        Assert.Contains("records=[{record_type", r);
    }

    // probe: an element member the shape doesn't declare (operations) is refused BY NAME with the ops= correction
    [Fact]
    public void UndeclaredOperationsMemberIsRefusedByName()
    {
        var r = CreateTools.Create(_w.Svc,
            records: Json("""[{"record_type":"Keyword","editorid":"W2Old","operations":[{"field_path":"Name","value":"x"}]}]"""));
        Assert.StartsWith("error:", r);
        Assert.Contains("operations", r);
        Assert.Contains("ops", r);
    }

    // probe: formid= inside a create op is refused BY NAME, corrected with why a create has none
    [Fact]
    public void FormidInsideACreateOpIsRefused()
    {
        var r = CreateTools.Create(_w.Svc,
            records: Json($$"""[{"record_type":"Keyword","editorid":"W2Bad","ops":[{"formid":"{{_w.SubjectFid}}","field_path":"Name","value":"x"}]}]"""));
        Assert.StartsWith("error:", r);
        Assert.Contains("formid", r);
        Assert.Contains("auto-allocated", r);
    }

    // probe: from_source= inside a create op is refused BY NAME with the housecarl_apply route
    [Fact]
    public void FromSourceInsideACreateOpIsRefused()
    {
        var r = CreateTools.Create(_w.Svc,
            records: Json("""[{"record_type":"Keyword","editorid":"W2Bad2","ops":[{"field_path":"Name","from_source":"HcW2Master.esm"}]}]"""));
        Assert.StartsWith("error:", r);
        Assert.Contains("from_source", r);
        Assert.Contains("housecarl_apply", r);
    }

    // probe: a per-record refusal names the caller's own spelling: records[1], never the 1.x record[1]
    [Fact]
    public void PerRecordRefusalNamesRecordsIndex()
    {
        var r = CreateTools.Create(_w.Svc,
            records: Json("""[{"record_type":"Keyword","editorid":"W2Ok"},{"record_type":"NotARealType","editorid":"W2Nope"}]"""));
        Assert.StartsWith("error:", r);
        Assert.Contains("records[1]", r);
        Assert.DoesNotContain("record[1]:", r);
    }

    // probe (transport arm): create: a null op element is refused BY NAME, never as an 'internal failure — retry once'
    [Fact]
    public void NullOpElementIsRefusedByName()
    {
        var r = CreateTools.Create(_w.Svc, patch: "W2NullOp",
            records: Json("""[{"record_type":"Keyword","editorid":"W2NullOp","ops":[null]}]"""));
        Assert.StartsWith("error:", r);
        Assert.Contains("records[0]: ops[0] is null", r);
        Assert.DoesNotContain("internal houseCARL failure", r);
        Assert.DoesNotContain("retry once", r, StringComparison.OrdinalIgnoreCase);
    }

    // probe (transport arm): create: a malformed op is labelled ops[i] — the caller's own member — never op[i]
    [Fact]
    public void MalformedOpIsLabelledOpsIndex()
    {
        var r = CreateTools.Create(_w.Svc, patch: "W2OpLbl",
            records: Json("""[{"record_type":"Keyword","editorid":"W2OpLbl","ops":[{"value":"x"}]}]"""));
        Assert.Contains("records[0]: ops[0]:", r);
        Assert.DoesNotContain("op[0]:", r);
    }

    // probe (transport arm): create: the CopyFrom refusal names op="CopyFrom", not the undeclared from_plugin
    [Fact]
    public void CopyFromOnCreateNamesTheOpNotFromPlugin()
    {
        var r = CreateTools.Create(_w.Svc, patch: "W2CopyCre",
            records: Json("""[{"record_type":"Keyword","editorid":"W2CopyCre","ops":[{"field_path":"EditorID","op":"CopyFrom"}]}]"""));
        Assert.Contains("op=\"CopyFrom\" copies from an EXISTING record", r);
        Assert.DoesNotContain("from_plugin", r);
    }
}

using System.Text.Json;
using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.WriteSurfaceReads;

namespace HousecarlMcpTests;

/// <summary>TRANSPORT on the write tools: format=json is a document (refusals included), every response states its
/// epoch or why it has none, the lane value names the parameter, and max_chars= cuts rows with an executable remedy.
/// Migrated from write-surface-guard's transport arm; the in-place half is <see cref="WriteSurfaceInPlaceTransportTests"/>.</summary>
[Trait("tier", "integration")]
public sealed class WriteSurfaceTransportTests : IClassFixture<WriteSurfaceWorld>
{
    readonly WriteSurfaceWorld _w;
    public WriteSurfaceTransportTests(WriteSurfaceWorld w) => _w = w;

    static JsonElement Root(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    // probe: create format=json: a valid document with ok/lane/created and the epoch
    [Fact]
    public void CreateJsonIsADocumentWithTheEpoch()
    {
        var d = Root(CreateTools.Create(_w.Svc, patch: "W2Json",
            records: Json("""[{"record_type":"Keyword","editorid":"W2JsonKw"}]"""), format: "json"));
        Assert.True(d.GetProperty("ok").GetBoolean());
        Assert.Equal("W2JsonKw", d.GetProperty("created")[0].GetProperty("editorid").GetString());
        Assert.Equal(JsonValueKind.String, d.GetProperty("epoch").ValueKind);
    }

    // probe: {tool} format=json: the unconfigured-MO2 prompt is a DOCUMENT, not prose (create, remove, forward, apply, write_seq)
    [Theory]
    [InlineData("create")]
    [InlineData("remove")]
    [InlineData("forward")]
    [InlineData("apply")]
    [InlineData("write_seq")]
    public void UnconfiguredPromptIsAJsonDocument(string tool)
    {
        using var bare = LoadOrderService.WithInstance(null, 0,
            new UserConfigStore(Path.Combine(_w.Root, "hc-unconfigured-" + tool + ".user.json")));
        var render = tool switch
        {
            "create" => CreateTools.Create(bare, records: Json("""[{"record_type":"Keyword","editorid":"X"}]"""), format: "json"),
            "remove" => RemoveTools.Remove(bare, formids: new[] { _w.SubjectFid }, into: "X.esp", format: "json"),
            "forward" => ForwardTools.Forward(bare, formids: new[] { _w.SubjectFid }, source: _w.MasterName, format: "json"),
            "apply" => ApplyTools.Apply(bare, ops: Json($$"""[{"formid":"{{_w.SubjectFid}}","field_path":"Name","value":"x"}]"""), format: "json"),
            _ => SeqTools.WriteSeq(bare, source: _w.MasterName, format: "json"),
        };
        Assert.Contains("no Mod Organizer 2 instance configured", Root(render).GetProperty("error").GetString());
    }

    // probe: create text: the unconfigured-MO2 prompt stays the trained prose block
    [Fact]
    public void UnconfiguredPromptStaysProseOnText()
    {
        using var bare = LoadOrderService.WithInstance(null, 0, new UserConfigStore(Path.Combine(_w.Root, "hc-unconfigured-text.user.json")));
        var r = CreateTools.Create(bare, records: Json("""[{"record_type":"Keyword","editorid":"X"}]"""));
        Assert.Contains("no Mod Organizer 2 instance configured", r);
        Assert.False(r.TrimStart().StartsWith("{", StringComparison.Ordinal), r);
    }

    // probe: create format=json: a REFUSAL is a document carrying the reason, never an empty string
    [Fact]
    public void CreateJsonRefusalIsADocument()
    {
        var r = CreateTools.Create(_w.Svc, records: Json("[]"), format: "json");
        Assert.Contains("empty array", Root(r).GetProperty("error").GetString());
    }

    // probe: remove format=json: the no-lane refusal is a document too
    [Fact]
    public void RemoveJsonNoLaneRefusalIsADocument()
    {
        var r = RemoveTools.Remove(_w.Svc, formids: new[] { _w.SubjectFid }, format: "json");
        Assert.Contains("in_place=", Root(r).GetProperty("error").GetString());
    }

    // probe: forward format=json: forwarded rows carry source, prior_winner and the two per-record flags
    [Fact]
    public void ForwardJsonRowsCarrySourceAndFlags()
    {
        var d = Root(ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: _w.MasterName,
            patch: "W2FwdJson", format: "json"));
        var row = d.GetProperty("forwarded")[0];
        Assert.Equal(_w.MasterName, row.GetProperty("source").GetString());
        Assert.True(row.TryGetProperty("was_already_winner", out _));
        Assert.Equal(JsonValueKind.String, d.GetProperty("epoch").ValueKind);
    }

    // probe: a lane-resolution refusal is a document with ok:false and a NULL epoch (it consulted no build)
    [Fact]
    public void LaneResolutionRefusalHasNullEpoch()
    {
        var d = Root(RemoveTools.Remove(_w.Svc, formids: new[] { _w.SubjectFid }, into: "NoSuchPatch.esp", format: "json"));
        Assert.False(d.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, d.GetProperty("epoch").ValueKind);
    }

    // probe: a refusal decided AFTER the engine's capture carries that build's epoch
    [Fact]
    public void PostCaptureRefusalCarriesTheEpoch()
    {
        var made = CreateTools.Create(_w.Svc, patch: "W2Epoch", records: Json("""[{"record_type":"Keyword","editorid":"W2EpochKw"}]"""));
        var path = _w.ArtifactPathFrom(made);
        Assert.NotNull(path);
        var d = Root(RemoveTools.Remove(_w.Svc, formids: new[] { _w.SubjectFid }, into: Path.GetFileName(path!), format: "json"));
        Assert.False(d.GetProperty("ok").GetBoolean());
        Assert.Contains("not carried by patch", d.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.String, d.GetProperty("epoch").ValueKind);
    }

    // probe: json lane on the in-place CONSENT PROMPT says in_place, not the patch lane the caller never named
    [Fact]
    public void JsonLaneOnConsentPromptIsInPlace()
    {
        var d = Root(CreateTools.Create(_w.Svc, records: Json("""[{"record_type":"Keyword","editorid":"W2LaneKw2"}]"""),
            in_place: _w.MasterName, format: "json"));
        Assert.True(d.GetProperty("needs_acknowledge").GetBoolean());
        Assert.Equal("in_place", d.GetProperty("lane").GetString());
    }

    // probe: json lane on an into= REFUSAL says into, not the patch lane (Fail leaves the outcome flags at default)
    [Fact]
    public void JsonLaneOnIntoRefusalIsInto()
    {
        var d = Root(RemoveTools.Remove(_w.Svc, formids: new[] { _w.SubjectFid }, into: "NoSuchPatch2.esp", format: "json"));
        Assert.Equal("into", d.GetProperty("lane").GetString());
    }

    // probe: json lane on a service-side in-place refusal says in_place too
    [Fact]
    public void JsonLaneOnInPlaceRefusalIsInPlace()
    {
        var d = Root(ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: _w.MasterName,
            in_place: "NotAPlugin.esp", format: "json"));
        Assert.Equal("in_place", d.GetProperty("lane").GetString());
    }

    // probe: the into= lane is spelled the same on create / forward / remove — the parameter's own name
    [Fact]
    public void IntoLaneIsSpelledTheSameOnCreateAndForward()
    {
        var c = Root(CreateTools.Create(_w.Svc, records: Json("""[{"record_type":"Keyword","editorid":"W2LaneKw3"}]"""),
            into: "NoSuchPatch3.esp", format: "json"));
        var f = Root(ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: _w.MasterName,
            into: "NoSuchPatch3.esp", format: "json"));
        Assert.Equal("into", c.GetProperty("lane").GetString());
        Assert.Equal("into", f.GetProperty("lane").GetString());
    }

    (string file, List<string> ids) ThreeKeywords(string stem)
    {
        var made = CreateTools.Create(_w.Svc, patch: stem, records: Json($$"""
            [{"record_type":"Keyword","editorid":"{{stem}}A"},
             {"record_type":"Keyword","editorid":"{{stem}}B"},
             {"record_type":"Keyword","editorid":"{{stem}}C"}]
            """));
        var path = _w.ArtifactPathFrom(made);
        Assert.NotNull(path);
        var ids = FormIdsFrom(made);
        Assert.Equal(3, ids.Count);
        return (Path.GetFileName(path!), ids);
    }

    // probe: remove text render: max_chars= drops trailing rows with an explicit notice (never a silent host cut)
    // probe: remove's truncation remedy is executable — it does not prescribe the re-issue that gets refused
    [Fact]
    public void RemoveTextCapHasAnExecutableNotice()
    {
        var (file, ids) = ThreeKeywords("W2Cap");
        var r = RemoveTools.Remove(_w.Svc, formids: ids.ToArray(), into: file, max_chars: 100);
        Assert.Contains("[truncated:", r);
        Assert.Contains("max_chars=100", r);
        Assert.Contains("every one WAS removed", r);
        Assert.Contains("exactly the formids= you passed", r);
        Assert.Contains("a repeat is refused", r);
        Assert.DoesNotContain("raise max_chars", r);
    }

    // probe: …and the re-issue the OLD remedy prescribed really is refused (the dead end, proven)
    [Fact]
    public void RepeatedRemoveIsRefused()
    {
        var (file, ids) = ThreeKeywords("W2CapR");
        RemoveTools.Remove(_w.Svc, formids: ids.ToArray(), into: file, max_chars: 100);
        var repeat = RemoveTools.Remove(_w.Svc, formids: ids.ToArray(), into: file, max_chars: 400000);
        Assert.StartsWith("error:", repeat);
        Assert.Contains("NOTHING removed", repeat);
    }

    // probe: remove format=json: the truncation note carries the SAME remedy as its text twin (D2)
    [Fact]
    public void RemoveJsonCapNoteCarriesTheSameRemedy()
    {
        var (file, ids) = ThreeKeywords("W2CapJ");
        var d = Root(RemoveTools.Remove(_w.Svc, formids: ids.ToArray(), into: file, max_chars: 100, format: "json"));
        var note = d.GetProperty("truncated_note").GetString();
        Assert.Contains("exactly the formids= you passed", note);
        Assert.DoesNotContain("raise max_chars", note);
    }

    string CappedCreate(string stem) => CreateTools.Create(_w.Svc, patch: stem, max_chars: 130, records: Json($$"""
        [{"record_type":"Keyword","editorid":"{{stem}}A"},
         {"record_type":"Keyword","editorid":"{{stem}}B"},
         {"record_type":"Keyword","editorid":"{{stem}}C"}]
        """));

    // probe: create text render: max_chars= drops trailing created rows with an explicit notice
    [Fact]
    public void CreateTextCapDropsTrailingRows()
    {
        var r = CappedCreate("W2CreCap");
        Assert.Contains("[truncated:", r);
        Assert.Contains(WriteSentences.RowsCutOperationIntact(false, "created"), r);
        Assert.DoesNotContain("W2CreCapC", r);
    }

    // probe: create's truncation notice points at a READ, never at raising max_chars (a repeat would re-create)
    [Fact]
    public void CreateCapNoticePointsAtARead()
    {
        var r = CappedCreate("W2CreCapN");
        Assert.Contains("housecarl_records source=", r);
        Assert.Contains("types=[\"Keyword\"]", r);
        Assert.Contains("allocates the records AGAIN", WriteSentences.Twins.CreateReissueTrap);
        Assert.Contains(WriteSentences.Twins.CreateReissueTrap, r);
        Assert.DoesNotContain("raise max_chars", r);
    }

    // probe: create's truncation remedy, RUN as emitted, resolves and returns the row the render cut
    // probe: records: source= ALONE selects nothing, so a remedy without a SELECT term is a dead end
    [Fact]
    public void CreateCapRemedyRunsAsEmitted()
    {
        var r = CappedCreate("W2CreCapRun");
        var (file, types) = ParseReadBackCall(r);
        Assert.NotNull(file);
        Assert.NotNull(types);
        var remedy = RecordsTools.Records(_w.Svc, source: Json($"\"{file}\""), types: types);
        Assert.False(remedy.StartsWith("error:", StringComparison.Ordinal), remedy);
        Assert.Contains("W2CreCapRunC", remedy);

        var bare = RecordsTools.Records(_w.Svc, source: Json($"\"{file}\""));
        Assert.StartsWith("error:", bare);
        Assert.Contains("select something", bare);
    }

    // probe: create text render: with EVERY row cut, the render stops claiming a FormID it never printed
    [Fact]
    public void CreateWithEveryRowCutClaimsNoFormId()
    {
        var r = CreateTools.Create(_w.Svc, patch: "W2CreCut", max_chars: 1, records: Json("""
            [{"record_type":"Keyword","editorid":"W2CreCutA"},
             {"record_type":"Keyword","editorid":"W2CreCutB"}]
            """));
        Assert.Contains("truncated: 0 of 2", r);
        Assert.DoesNotContain("the new FormID above", r);
        Assert.Contains("all 2 WERE created", r);
    }

    // probe: create text render: without a cap every created row is listed
    [Fact]
    public void CreateUncappedListsEveryRow()
    {
        var r = CreateTools.Create(_w.Svc, patch: "W2CreFull", records: Json("""
            [{"record_type":"Keyword","editorid":"W2CreFullA"},
             {"record_type":"Keyword","editorid":"W2CreFullB"},
             {"record_type":"Keyword","editorid":"W2CreFullC"}]
            """));
        Assert.Contains("W2CreFullC", r);
        Assert.DoesNotContain("[truncated:", r);
    }

    // probe: forward text render: max_chars= drops trailing forwarded rows with an explicit notice
    // probe: forward's truncation remedy on the DEFAULT lane names into=, not a bare re-issue
    [Fact]
    public void ForwardTextCapOnDefaultLaneNamesInto()
    {
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: _w.MasterName, patch: "W2FwdCap", max_chars: 120);
        Assert.Contains("[truncated:", r);
        Assert.Contains("every one WAS forwarded", r);
        Assert.Contains("SECOND patch", r);
        Assert.Contains("pass into=", r);
    }

    // probe: forward's truncation remedy on into= is the plain one (a re-issue there is idempotent)
    [Fact]
    public void ForwardTextCapOnIntoIsThePlainRemedy()
    {
        var made = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: _w.MasterName, patch: "W2FwdInto");
        var file = Path.GetFileName(_w.ArtifactPathFrom(made));
        Assert.NotNull(file);
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: _w.MasterName, into: file, max_chars: 120);
        Assert.Contains("raise max_chars to see the rest", r);
        Assert.DoesNotContain("SECOND patch", r);
    }

    // probe: apply format=json: the truncation note carries the lane-aware remedy, not a bare 'raise max_chars'
    [Fact]
    public void ApplyJsonCapNoteIsLaneAware()
    {
        var d = Root(ApplyTools.Apply(_w.Svc, patch: "W2ApCap", format: "json", max_chars: 300,
            ops: Json($$"""[{"formid":"{{_w.SubjectFid}}","field_path":"Name","value":"W2ApCapName"}]""")));
        var note = d.GetProperty("truncated_note").GetString();
        Assert.Contains("pass into=", note);
        Assert.Contains("SECOND patch mod", note);
        Assert.DoesNotContain("raise max_chars to see the rest", note);
    }

    // probe: write_seq: patch= and into= together are refused BY NAME, never silently resolved to into=
    [Fact]
    public void WriteSeqPatchAndIntoIsRefused()
    {
        var r = SeqTools.WriteSeq(_w.Svc, source: _w.MasterName, patch: "HcSeqNew", into: "HcSeqExisting.esp");
        Assert.StartsWith("error:", r);
        Assert.Contains("HcSeqNew", r);
        Assert.Contains("HcSeqExisting.esp", r);
        Assert.Contains("exclusive", r);
    }

    // probe: write_seq format=json: the LANE refusal is a DOCUMENT carrying the reason, not prose
    [Fact]
    public void WriteSeqJsonLaneRefusalIsADocument()
    {
        var r = SeqTools.WriteSeq(_w.Svc, source: _w.MasterName, patch: "HcSeqNew", into: "HcSeqExisting.esp", format: "json");
        Assert.Contains("exclusive", Root(r).GetProperty("error").GetString());
    }

    // probe: write_seq: patch= ALONE is still honored (the refusal is the pair, not the parameter)
    [Fact]
    public void WriteSeqPatchAloneIsHonored()
    {
        var r = SeqTools.WriteSeq(_w.Svc, source: _w.MasterName, patch: "HcSeqOnlyNew");
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
    }

    // probe: write_seq format=json: epoch is explicitly null AND carries why (no build is consulted at all)
    [Fact]
    public void WriteSeqJsonEpochIsNullWithAReason()
    {
        var d = Root(SeqTools.WriteSeq(_w.Svc, source: _w.MasterName, format: "json"));
        Assert.Equal(JsonValueKind.Null, d.GetProperty("epoch").ValueKind);
        Assert.Contains("load-order-independent", d.GetProperty("epoch_note").GetString());
    }

    // probe: write_seq text: the no-SGE-quests no-op names the file AND the copy it was read from
    [Fact]
    public void WriteSeqNoOpNamesTheCopyRead()
    {
        var r = SeqTools.WriteSeq(_w.Svc, source: _w.MasterName);
        Assert.Contains("no start-game-enabled quests", r);
        Assert.Contains("read from", r);
    }
}

/// <summary>The in-place transport asserts; each test builds its own world because the call rewrites the replacer.</summary>
[Trait("tier", "integration")]
public sealed class WriteSurfaceInPlaceTransportTests : IDisposable
{
    readonly WriteSurfaceWorld _w = new();
    public void Dispose() => _w.Dispose();

    // probe: json: an in-place lane that FORCED the read-back reports readback_full:true, ask kept separately
    [Fact]
    public void ForcedReadbackIsReportedFull()
    {
        var raw = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: _w.MasterName,
            in_place: _w.ReplacerName, acknowledge: true, readback: false, format: "json");
        var d = JsonDocument.Parse(raw).RootElement;
        Assert.True(d.GetProperty("readback_full").GetBoolean());
        Assert.False(d.GetProperty("readback_requested").GetBoolean());
        var rb = d.GetProperty("readback");
        Assert.True(rb.GetArrayLength() > 0, raw);
        Assert.True(rb[0].TryGetProperty("fields", out _), raw);
    }

    // probe: forward's in-place truncation remedy points at a READ, never at re-writing the caller's original
    [Fact]
    public void InPlaceCapRemedyPointsAtARead()
    {
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: _w.MasterName,
            in_place: _w.ReplacerName, acknowledge: true, max_chars: 120);
        var notice = LineAfter(r, "every one WAS forwarded — ");
        Assert.NotNull(notice);
        Assert.Contains("housecarl_records source=", notice);
        Assert.Contains("re-serialize your ORIGINAL file", notice);
        Assert.DoesNotContain("raise max_chars", notice);
    }
}

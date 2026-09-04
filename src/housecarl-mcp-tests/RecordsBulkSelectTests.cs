using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The list lane of <c>housecarl_records</c>: the identity form's rows, the per-item error shape, the
/// named-plugin SOURCE pole, the record object's json contract, and link annotation.
/// </summary>
[Collection("bulk-records")]
[Trait("tier", "integration")]
public sealed class RecordsBulkSelectTests : BulkRecordsTestBase
{
    public RecordsBulkSelectTests(BulkRecordsFixture f) : base(f) { }

    const string BadFormid = "not-a-formid";

    string IdentityText() =>
        RecordsTools.Records(Svc, formids: new[] { Fid(W.W1), Fid(W.KwA), BadFormid }, project: Form("identity"));

    string IdentityJson() =>
        RecordsTools.Records(Svc, formids: new[] { Fid(W.W1), BadFormid }, project: Form("identity"), format: "json");

    JsonElement IdentityRows() => Doc(IdentityJson()).GetProperty("resolved");

    // ---- the identity form's rows -----------------------------------------------------------------

    [Fact]
    public void TheIdentityFormsRowCarriesTypeEditoridNameAndWinner() =>
        Served(IdentityText(), "type=Weapon", $"editorid={BulkRecordsWorld.W1WinnerEditorId}",
               $"name=\"{BulkRecordsWorld.W1Name}\"", $"winner={W.ReplName}");

    [Fact]
    public void AMalformedFormidIsAPerItemErrorRowWhileTheOtherRowsStillResolve()
    {
        var r = IdentityText();
        Assert.Contains($"{BadFormid}  error=bad FormID", r);
        Assert.Contains("editorid=HcBulkKwA", r);   // the row after the bad one still resolved
    }

    // The parse IS the assertion: a json-mode caller that gets prose is exactly the failure this pins.
    [Fact]
    public void TheIdentityFormsJsonRenderIsAValidDocument() => Doc(IdentityJson());

    [Fact]
    public void TheIdentityJsonCarriesOneResolvedRowPerInput() => Assert.Equal(2, IdentityRows().GetArrayLength());

    [Fact]
    public void TheIdentityJsonRowCarriesTheSameFourIdentityFactsTheTextRowPrints()
    {
        var row = IdentityRows()[0];
        Assert.Equal("Weapon", row.GetProperty("type").GetString());
        Assert.Equal(BulkRecordsWorld.W1WinnerEditorId, row.GetProperty("editorid").GetString());
        Assert.Equal(BulkRecordsWorld.W1Name, row.GetProperty("name").GetString());
        Assert.Equal(W.ReplName, row.GetProperty("winner").GetString());
    }

    [Fact]
    public void TheIdentityJsonRowForABadFormidCarriesErrorAndNoWinner()
    {
        var row = IdentityRows()[1];
        Assert.NotEmpty(row.GetProperty("error").GetString()!);
        Assert.False(row.TryGetProperty("winner", out _));
    }

    // ---- format= is parsed once, for every lane ---------------------------------------------------

    [Fact]
    public void AnUnrecognizedFormatIsRefusedLoudNamingTheParameter() =>
        Refused(RecordsTools.Records(Svc, formids: new[] { Fid(W.W1) }, format: "bogus"), "format");

    // ---- max_chars on the identity lane -----------------------------------------------------------

    /// <summary>max_chars caps the RENDER only — the complete result spills to an artifact, so no row is lost.</summary>
    [Fact]
    public void AnIdentityRenderOverMaxCharsSpillsTheCompleteResultInsteadOfDroppingRows()
    {
        var doc = Doc(RecordsTools.Records(Svc, formids: new[] { Fid(W.W1), Fid(W.KwA), Fid(W.W3) },
                                           project: Form("identity"), format: "json", max_chars: 60));
        Assert.True(doc.GetProperty("rendered").GetInt32() < doc.GetProperty("count").GetInt32());
        var spill = doc.GetProperty("spilled");
        Assert.True(spill.GetProperty("complete").GetBoolean());
        Assert.Equal(doc.GetProperty("count").GetInt32(), spill.GetProperty("row_count").GetInt32());
    }

    // ---- resolve_names: annotation is ADDED, never substituted ------------------------------------

    RecordsTools.RecordsProject KeywordsNamed =>
        new() { form = "fields", fields = new[] { "Keywords" }, depth = 2, resolve_names = true };

    string AnnotatedText() => RecordsTools.Records(Svc, formids: new[] { Fid(W.W1) }, project: KeywordsNamed);

    [Fact]
    public void ResolveNamesAnnotatesALinkWhileTheRawRoundTripTokenStandsUnchanged()
    {
        var r = AnnotatedText();
        Served(r, $"Keywords[0] = {Fid(W.KwA)}");   // the token a write can reuse, verbatim
        Assert.Contains("(→ HcBulkKwA)", r);        // the identity, alongside it
    }

    [Fact]
    public void AnUnresolvableLinkIsAnnotatedUnresolvedRatherThanDroppedOrGuessed()
    {
        var r = AnnotatedText();
        Served(r, $"Keywords[1] = {Fid(W.Ghost)}");
        Assert.Contains("(unresolved: no active plugin defines this target)", r);
    }

    [Fact]
    public void ResolveNamesInJsonKeepsTheRawTokenAndPutsTheIdentityInALinkSibling()
    {
        var rec = Doc(RecordsTools.Records(Svc, formids: new[] { Fid(W.W1) }, project: KeywordsNamed, format: "json"))
                  .GetProperty("records")[0];
        var el = Field(rec, "Keywords[0]");
        Assert.Equal(Fid(W.KwA), el.GetProperty("value").GetString());
        var link = el.GetProperty("link");
        Assert.True(link.GetProperty("resolved").GetBoolean());
        Assert.Equal("HcBulkKwA", link.GetProperty("editorid").GetString());
    }

    // ---- the record object's json contract --------------------------------------------------------

    string FieldsJson() =>
        RecordsTools.Records(Svc, formids: new[] { Fid(W.W1) }, format: "json", project: Fields("BasicStats.Damage"));

    JsonElement FieldsRecord() => Doc(FieldsJson()).GetProperty("records")[0];

    [Fact]
    public void TheFieldsFormsJsonRenderIsAValidDocument() => Doc(FieldsJson());

    /// <summary>Equality, not presence: an ADDED key is a contract change too, and a presence sweep would
    /// let one through.</summary>
    [Fact]
    public void TheRecordObjectCarriesExactlyTheContractedIdentityAndBodyKeys() =>
        Assert.Equal(new[] { "formid", "runtime_formid", "type", "editorid", "winner", "override_depth", "source", "fields" },
                     FieldsRecord().EnumerateObject().Select(p => p.Name).ToArray());

    [Fact]
    public void WithNoSourceNamedTheRecordObjectsIdentityIsTheWinners()
    {
        var rec = FieldsRecord();
        Assert.Equal("Weapon", rec.GetProperty("type").GetString());
        Assert.Equal(BulkRecordsWorld.W1WinnerEditorId, rec.GetProperty("editorid").GetString());
        Assert.Equal(W.ReplName, rec.GetProperty("winner").GetString());
    }

    [Fact]
    public void TheJsonFieldValueIsTheSameTokenTheTextRenderPrints()
    {
        Assert.Equal("15", Field(FieldsRecord(), "BasicStats.Damage").GetProperty("value").GetString());
        Assert.Contains("BasicStats.Damage = 15",
                        RecordsTools.Records(Svc, formids: new[] { Fid(W.W1) }, project: Fields("BasicStats.Damage")));
    }

    /// <summary>A starved whole-body dump must stay a DOCUMENT — the failure this guards is a string-cut
    /// render that no json caller can parse.</summary>
    [Fact]
    public void AWholeBodyDumpUnderATinyMaxCharsStaysValidJsonWithATruncationSentinelField()
    {
        var rec = Doc(RecordsTools.Records(Svc, formids: new[] { Fid(W.W1) }, format: "json",
                                           max_chars: 200, project: Form("everything")))
                  .GetProperty("records")[0];
        Assert.Contains("truncated at max_chars", Field(rec, "…").GetProperty("note").GetString());
    }

    [Fact]
    public void TheBatchJsonEnvelopeCarriesItsCountRowsAndRenderAccounting()
    {
        var doc = Doc(RecordsTools.Records(Svc, formids: new[] { Fid(W.W1), BadFormid },
                                           format: "json", project: Fields("BasicStats.Damage")));
        Assert.Equal(2, doc.GetProperty("count").GetInt32());
        Assert.Equal(2, doc.GetProperty("records").GetArrayLength());
        Assert.Equal(2, doc.GetProperty("rendered").GetInt32());
        Assert.False(doc.GetProperty("truncated").GetBoolean());
    }

    // ---- the named-plugin SOURCE pole in bulk -----------------------------------------------------

    string FromMaster(params string[] ids) =>
        RecordsTools.Records(Svc, formids: ids, source: Plugin(W.MasterName), project: Fields("BasicStats.Damage"));

    string FromRepl(params string[] ids) =>
        RecordsTools.Records(Svc, formids: ids, source: Plugin(W.ReplName), project: Fields("BasicStats.Damage"));

    [Fact]
    public void ANamedActivePluginPoleReadsThatPluginsOwnValueNotTheWinners()
    {
        var r = FromMaster(Fid(W.W1), Fid(W.W2));
        Served(r, "BasicStats.Damage = 10");
        Assert.DoesNotContain("BasicStats.Damage = 15", r);   // 15 is the live winner's, and was not asked for
    }

    [Fact]
    public void TheSameNamedPoleBatchAlsoReadsARecordOnlyThatPluginTouches() =>
        Served(FromMaster(Fid(W.W1), Fid(W.W2)), "BasicStats.Damage = 20");

    [Fact]
    public void NamingTheOverridingPluginReadsItsOverriddenValue()
    {
        var r = FromRepl(Fid(W.W1));
        Served(r, "BasicStats.Damage = 15");
        Assert.DoesNotContain("BasicStats.Damage = 10", r);
    }

    /// <summary>The touchers-naming half of this refusal is
    /// <c>RecordsListLaneTests.OnePoleActiveArm_AnUntouchedRecordIsAPerItemRefusalNamingTheActualTouchers</c>;
    /// what is asserted here is that the rest of the batch survives it.</summary>
    [Fact]
    public void ARowThePoleDoesNotTouchRefusesPerItemWhileTheOtherRowStillRendersItsValue()
    {
        var r = FromRepl(Fid(W.W2), Fid(W.W1));
        Assert.Contains("does not touch", r);
        Assert.Contains("BasicStats.Damage = 15", r);
    }

    [Fact]
    public void TheJsonRecordsSourceIsTheNamedPluginWhileItsWinnerStaysTheWinner()
    {
        var rec = Doc(RecordsTools.Records(Svc, formids: new[] { Fid(W.W1) }, source: Plugin(W.MasterName),
                                           format: "json", project: Fields("BasicStats.Damage")))
                  .GetProperty("records")[0];
        Assert.Equal(W.MasterName, rec.GetProperty("source").GetString());
        Assert.Equal(W.ReplName, rec.GetProperty("winner").GetString());
    }
}

/// <summary>
/// The two hardcoded engine references (PlayerRef 000014, Player 000007) resolve to their identity instead of
/// reading as unresolved, and the exemption stays exactly two forms wide.
///
/// <para>Its own world: the carrier plugin masters a Skyrim.esm deliberately kept out of the active order, so
/// both 000014 and 000015 fail ordinary winner resolution and only the exemption separates them.</para>
/// </summary>
[Trait("tier", "integration")]
public sealed class RecordsEngineImplicitLinkTests : IDisposable
{
    readonly EngineImplicitLinkWorld _w = new();
    public void Dispose() => _w.Dispose();

    string Annotated() =>
        RecordsTools.Records(_w.Svc, formids: new[] { BulkRecordsWorld.Fid(_w.Carrier) },
                             project: new RecordsTools.RecordsProject
                             { form = "fields", fields = new[] { "Keywords" }, depth = 2, resolve_names = true });

    [Fact]
    public void AnEngineImplicitLinkIsAnnotatedWithItsHardcodedIdentityNotUnresolved()
    {
        var r = Annotated();
        Assert.Contains($"Keywords[0] = {EngineImplicitLinkWorld.PlayerRefToken}", r);
        Assert.Contains("(→ PlayerRef)", r);
    }

    [Fact]
    public void TheNextSubO800FormStillAnnotatesUnresolved_TheExemptionIsTwoFormsNotARange()
    {
        var r = Annotated();
        Assert.Contains($"Keywords[1] = {EngineImplicitLinkWorld.ControlToken}", r);
        Assert.Contains("(unresolved: no active plugin defines this target)", r);
    }
}

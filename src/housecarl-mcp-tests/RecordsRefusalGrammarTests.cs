using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>One refusal shape per transport: a json caller gets a json refusal every time, a per-ROW failure
/// is never a refused call, and a served census does not answer to the refusal discriminant. The literals
/// (<c>ok</c>, <c>error</c>, the text lane's <c>error: </c> prefix) are spelled out rather than read off the
/// render side, so a rename fails these tests instead of moving with them.</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsRefusalGrammarTests : RecordsTestBase
{
    public RecordsRefusalGrammarTests(RecordsFixture f) : base(f) { }

    static RecordsTools.RecordsProject BadForm => new() { form = "notaform" };

    string RefusalJson() => RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) }, project: BadForm, format: "json");
    string RefusalText() => RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) }, project: BadForm);

    // A parse failure here IS the bug this lane exists for, so the parse is the assertion.
    JsonElement Doc(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void AJsonModeParameterRefusalIsAJsonDocumentNotProse() => Doc(RefusalJson());

    [Fact]
    public void AJsonRefusalDeclaresItselfRefusedWithOkFalse() =>
        Assert.Equal(JsonValueKind.False, Doc(RefusalJson()).GetProperty("ok").ValueKind);

    [Fact]
    public void AJsonRefusalCarriesTheSentenceInAnErrorString() =>
        Assert.NotEmpty(Doc(RefusalJson()).GetProperty("error").GetString()!);

    [Fact]
    public void TheSentenceIsTheOneTheCallerNeeds_ItNamesTheRuleItBroke() =>
        Assert.Contains("is not a form", Doc(RefusalJson()).GetProperty("error").GetString()!);

    // StartsWith rather than a [..7] slice: a sentence shorter than seven characters would throw
    // ArgumentOutOfRangeException instead of failing the assertion.
    [Fact]
    public void TheJsonSentenceCarriesNoTextLaneErrorPrefix_ThePropertyNameAlreadySaysWhatItIs() =>
        Assert.False(Doc(RefusalJson()).GetProperty("error").GetString()!.StartsWith("error: ", StringComparison.Ordinal),
                     "the json lane's error property opens with the text lane's 'error: ' prefix");

    [Fact]
    public void TheTextTwinIsStillProseOpeningErrorColon() =>
        Assert.StartsWith("error: ", RefusalText());

    // Same slice hazard: on a text lane that answers empty the range operator throws before the two sentences
    // are compared. Assert the prefix first, then slice what is known to carry it.
    [Fact]
    public void BothTransportsStateOneSentenceNotTwoSpellingsOfIt()
    {
        var text = RefusalText();
        Assert.StartsWith("error: ", text);
        Assert.Equal(Doc(RefusalJson()).GetProperty("error").GetString()!.Trim(),
                     text["error: ".Length..].Trim());
    }

    [Fact]
    public void ARefusalRaisedInANoTransportHelperStillReachesTheCallerAsJson() =>
        Assert.Equal(JsonValueKind.False,
                     Doc(RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) },
                                              source: Plugin("previous_provider"), format: "json"))
                         .GetProperty("ok").ValueKind);

    // ---- a per-ROW failure is NOT a refusal -----------------------------------------------------------

    JsonElement RowDoc() =>
        Doc(RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]), "FFFFF1:HcRecBase.esp" }, format: "json"));

    [Fact]
    public void ABatchWithOneUnresolvableRowStillRenders_TheCallSucceeded() =>
        Assert.True(RowDoc().TryGetProperty("records", out _));

    [Fact]
    public void TheUnresolvableRowReallyIsCarriedAsAFailedRow_TheFixtureBites() =>
        Assert.Contains(RowDoc().GetProperty("records").EnumerateArray(), row => row.TryGetProperty("error", out _));

    [Fact]
    public void TheDocumentCarriesNoOk_AFailedRowMustNeverReadAsARefusedCall() =>
        Assert.False(RowDoc().TryGetProperty("ok", out _));

    [Fact]
    public void NoRowCarriesOkEither_TheDiscriminantIsDocumentLevelOnly() =>
        Assert.DoesNotContain(RowDoc().GetProperty("records").EnumerateArray(), row => row.TryGetProperty("ok", out _));

    // ---- the counts_only census is a served document too ----------------------------------------------

    JsonElement CensusDoc() =>
        Doc(RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]), "FFFFF1:HcRecBase.esp" },
                                 counts_only: true, format: "json"));

    [Fact]
    public void ACountsOnlyCensusRendersAsAServedDocument() =>
        Assert.True(CensusDoc().TryGetProperty("count", out _));

    [Fact]
    public void TheUnresolvableInputIsReallyCountedAsAnError_TheFixtureBites() =>
        Assert.True(CensusDoc().GetProperty("errors").GetInt32() > 0);

    [Fact]
    public void AServedCensusCarriesNoOk_ItMustNotAnswerToTheRefusalDiscriminant() =>
        Assert.False(CensusDoc().TryGetProperty("ok", out _));

    [Fact]
    public void TheResolvedCountIsResolvedBesideErrorsSayingWhatItCounts() =>
        Assert.Equal(JsonValueKind.Number, CensusDoc().GetProperty("resolved").ValueKind);

    // ---- a POST-capture refusal is stamped with the build it consulted --------------------------------

    JsonElement OffOrderRefusal() =>
        Doc(RecordsTools.Records(Svc, types: new[] { "WEAP" }, source: Plugin(W.OldName),
                                 conflicts_only: true, format: "json"));

    [Fact]
    public void AnOffOrderScanRefusalReachesAJsonCallerAsARefusalDocument() =>
        Assert.Equal(JsonValueKind.False, OffOrderRefusal().GetProperty("ok").ValueKind);

    [Fact]
    public void TheOffOrderRefusalCarriesTheBuildItConsultedNotEpochNull() =>
        Assert.NotEmpty(OffOrderRefusal().GetProperty("epoch").GetString()!);
}

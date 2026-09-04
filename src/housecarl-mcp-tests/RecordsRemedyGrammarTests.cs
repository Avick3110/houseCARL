using System.Text.RegularExpressions;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A remedy predicts what a later call produces. <c>housecarl_records</c> spells its field selector
/// <c>project.fields=</c> while the 1.x read tools spell it <c>fields=</c>, and the json body renderers are
/// shared by both, so a remedy composed inside one can name the other's vocabulary. The theories are the
/// per-family sentence check and the lane × wrong-lever sweep.</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsRemedyGrammarTests : RecordsTestBase
{
    readonly RemedyHarvest _h;
    public RecordsRemedyGrammarTests(RecordsFixture f) : base(f) => _h = f.Shared(w => new RemedyHarvest(w));

    static readonly string[] DeclaredProbes =
    {
        "fields/json", "fields/text", "container/text", "container/json", "tree/text", "tree/json",
        "scan/text", "scan/json", "container/dense", "delta/text", "deltaIdentical/text",
        "pole/text", "pole/json", "overlay/text", "overlay/json", "poleOff/text", "poleOff/json",
        "scoped/text", "scoped/json", "scoped/dense", "spill/artifact",
    };

    public static TheoryData<string> ProbeLabels => new(DeclaredProbes);

    public static TheoryData<string, string, string> LaneLeverGrid
    {
        get
        {
            var d = new TheoryData<string, string, string>();
            foreach (var lane in RemedyHarvest.Lanes)
                foreach (var (pattern, claim) in RemedyHarvest.WrongLevers)
                    d.Add(lane, pattern, claim);
            return d;
        }
    }

    // The probe list is DERIVED from the harvest, not from the literal above: a probe added to the harvest and
    // forgotten here would otherwise never be asserted, and the grid would stay green over the gap.
    [Fact]
    public void EveryHarvestedProbeIsCoveredByTheBiteTheory() =>
        Assert.Equal(_h.ProbeLabels.OrderBy(x => x, StringComparer.Ordinal),
                     DeclaredProbes.OrderBy(x => x, StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(ProbeLabels))]
    public void EachProbeFamilyEmitsARemedySentenceAtAll(string label) =>
        Assert.Contains(_h.Sentences, s => s.Label == label && RemedyHarvest.RemedyLine.IsMatch(s.Text));

    [Theory]
    [InlineData("json")]
    [InlineData("text")]
    [InlineData("dense")]
    [InlineData("artifact")]
    public void EachLaneEmitsRemedySentencesAtAll(string lane) =>
        Assert.NotEmpty(_h.Sentences.Where(s => s.Lane == lane));

    [Theory]
    [MemberData(nameof(LaneLeverGrid))]
    public void NoSentenceOnALaneTellsARecordsCallerToUseALeverItLacks(string lane, string pattern, string claim)
    {
        var bad = _h.Sentences.Where(s => s.Lane == lane && Regex.IsMatch(s.Text, pattern))
                              .Select(s => $"[{s.Label}] {s.Text}").ToList();
        Assert.True(bad.Count == 0, $"{lane} sentences tell the caller to {claim}:\n  " + string.Join("\n  ", bad));
    }

    // ---- the single-response checks ---------------------------------------------------------------------

    [Fact]
    public void AMaxCharsStarvedFieldsReadEmitsATruncationNoticeAtAll() =>
        Assert.NotEmpty(RemedyHarvest.HarvestAllStrings(
            RecordsTools.Records(Svc, formids: AllWeaponIds, format: "json", max_chars: 220,
                                 project: Fields("BasicStats.Damage", "EditorID", "Name"))));

    [Fact]
    public void DroppingProjectAsTheScanNoticeSaysYieldsSummaryRowsNotARefusal() =>
        Served(RecordsTools.Records(Svc, types: new[] { "WEAP" }, max_chars: 300), "form=summary");

    // ---- the artifact ROW writers' own truncation note ----------------------------------------------------

    static void RowNoteSpeaksTheCallersVocabulary(string? note)
    {
        Assert.NotNull(note);
        Assert.Contains("project.fields=", note);
        Assert.Contains("project.depth=", note);
        Assert.DoesNotMatch(new Regex(@"(?<!project\.)\b(fields|depth)="), note);
    }

    [Fact]
    public void ABatchArtifactRowsTruncationNoteSpeaksTheCallersVocabularyNotOneXs()
    {
        var outcomes = Svc.ResolveBatch(new[] { Fid(W.SpellA) }, new[] { "Effects" }, false, 2,
                                        containerHint: LeverNames.Records.ContainerHint);
        var cap = W.Scratch("cap-batch.jsonl");
        Artifacts.WriteBatch(outcomes, cap, "to_file", Array.Empty<KeyValuePair<string, string>>(),
                             LeverNames.Records, rowCap: 40);
        RowNoteSpeaksTheCallersVocabulary(
            RemedyHarvest.HarvestArtifact(cap).FirstOrDefault(s => s.Contains("[truncated at max_chars:")));
    }

    [Fact]
    public void ACrossQueryArtifactRowsTruncationNoteDoesToo_ItsOwnWriterItsOwnSeam()
    {
        var q = Svc.CrossQuery(new[] { "SPEL" }, null, null, false, null, null, 500);
        var cap = W.Scratch("cap-cross.jsonl");
        Artifacts.WriteCrossQuery(Svc, q, new[] { "Effects" }, false, false, 2, cap, "to_file",
                                  Array.Empty<KeyValuePair<string, string>>(), LeverNames.Records, rowCap: 40);
        RowNoteSpeaksTheCallersVocabulary(
            RemedyHarvest.HarvestArtifact(cap).FirstOrDefault(s => s.Contains("[truncated at max_chars:")));
    }

    // ---- the scan cut's SLIM-DOWN clause is true only where something was passed to slim ------------------

    static string? ScanCut(string resp) =>
        resp.Split('\n').FirstOrDefault(l => l.Contains("... [truncated: rendered") && l.Contains(" returned matches before hitting"));

    [Fact]
    public void ASummaryFormScansCutNamesNoProjectToDrop_ItPassedNoneAndIsTheSummaryRender()
    {
        var cut = ScanCut(RecordsTools.Records(Svc, types: new[] { "WEAP" }, max_chars: 300));
        Assert.NotNull(cut);
        Assert.Contains("lower limit= or raise max_chars", cut);
        Assert.DoesNotContain("drop ", cut);
    }

    [Fact]
    public void AFieldsFormScansCutStillSaysToDropProjectWhichIsActionableThere()
    {
        var cut = ScanCut(RecordsTools.Records(Svc, types: new[] { "WEAP" }, max_chars: 300,
                                               project: Fields("BasicStats.Damage")));
        Assert.NotNull(cut);
        Assert.Contains("drop project= (summary rows)", cut);
    }

    [Fact]
    public void TheOffOrderScanNamesNoneEither_ItPassesNoFieldPathsAtAll()
    {
        var cut = ScanCut(RecordsTools.Records(Svc, types: new[] { "WEAP" }, source: Plugin(W.OldName), max_chars: 12));
        Assert.NotNull(cut);
        Assert.Contains("lower limit= or raise max_chars", cut);
        Assert.DoesNotContain("drop ", cut);
    }

    // ---- the batch cut's SELECTION clause is a function of the selection LANE -----------------------------

    static string? BatchCut(string resp) =>
        resp.Split('\n').FirstOrDefault(l => l.Contains("... [truncated: rendered") && l.Contains(" records before hitting"));

    [Fact]
    public void AScanSelectedBatchsTruncationNoticeNamesLimitNotAFormidsListTheCallerNeverWrote()
    {
        var cut = BatchCut(RecordsTools.Records(Svc, types: new[] { "WEAP" }, max_chars: 400, project: Form("everything")));
        Assert.NotNull(cut);
        Assert.Contains("lower limit=", cut);
        Assert.DoesNotContain("formids", cut);
    }

    [Fact]
    public void TheFormidsLaneStillSaysRequestFewerFormidsWhichIsTrueOnlyThere()
    {
        var cut = BatchCut(RecordsTools.Records(Svc, formids: AllWeaponIds, max_chars: 400, project: Form("everything")));
        Assert.NotNull(cut);
        Assert.Contains("request fewer formids", cut);
        Assert.DoesNotContain("lower limit=", cut);
    }

    [Fact]
    public void TheOffOrderScansBatchNoticeNamesLimitToo_ItsOwnLaneItsOwnArm()
    {
        var cut = BatchCut(RecordsTools.Records(Svc, types: new[] { "WEAP" }, source: Plugin(W.OldName), max_chars: 12,
                                                project: Form("everything")));
        Assert.NotNull(cut);
        Assert.Contains("lower limit=", cut);
        Assert.DoesNotContain("formids", cut);
    }
}

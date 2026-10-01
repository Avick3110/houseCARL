using System.Text;
using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The SEQ coverage and staleness lint a quest validation attaches (<see cref="DialogueValidate"/>), and the
/// in-place uncovered-quest detector (<see cref="SeqFile.UncoveredSgeQuests"/>), from the <c>seq-staleness-guard</c>
/// probe.</summary>
[Trait("tier", "integration")]
public sealed class SeqStalenessLintTests : IClassFixture<SeqStalenessLintFixture>
{
    readonly SeqStalenessLintFixture _f;
    public SeqStalenessLintTests(SeqStalenessLintFixture f) => _f = f;

    // SEQ-MISSING SGE quest, no .seq → SeqExists=false
    [Fact]
    public void AnSgeQuestWithNoSeqIsMissing()
    {
        var s = _f.Lint(_f.Miss);
        Assert.True(s is { QuestIsSge: true, SeqExists: false }, Show(s));
    }

    // SEQ-NOT-LISTED SGE quest absent from a present .seq → SeqContainsQuest=false
    [Fact]
    public void AnSgeQuestAbsentFromItsSeqIsNotListed()
    {
        var s = _f.Lint(_f.NotListed);
        Assert.True(s is { SeqExists: true, SeqContainsQuest: false }, Show(s));
    }

    // SEQ-STALE .seq older than plugin → SeqNewerThanPlugin=false
    [Fact]
    public void ASeqOlderThanItsPluginIsNotNewer()
    {
        var s = _f.Lint(_f.Stale);
        Assert.True(s is { SeqExists: true, SeqContainsQuest: true, SeqNewerThanPlugin: false }, Show(s));
    }

    // SEQ-COVERED-OK listed + fresh .seq → no warning
    [Fact]
    public void AListedQuestInAFreshSeqIsCovered()
    {
        var s = _f.Lint(_f.Ok);
        Assert.True(s is { SeqExists: true, SeqContainsQuest: true, SeqNewerThanPlugin: true }, Show(s));
    }

    // SEQ-CLEAN-NO-FLAG a non-SGE quest yields NO lint
    [Fact]
    public void ANonSgeQuestHasNoLint() => Assert.Null(_f.Lint(_f.Plain));

    // SEQ-OVERRIDE-AMBIGUOUS override adds SGE → winner!=defining (render softens to [?], not a false dormant against the
    // master). The probe checked winner!=defining only; this also checks the render softens.
    [Fact]
    public void AnOverrideThatAddsSgeIsRenderedAsUnconfirmedNotDormant()
    {
        var s = _f.Lint(_f.Ovr);
        Assert.True(s is { QuestIsSge: true, SeqExists: false }, Show(s));
        Assert.NotEqual(s.DefiningPlugin.ToString(), s.WinnerPlugin, StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        DialogueWire.AppendSeq(sb, s);
        Assert.Contains("WINNING override", sb.ToString());
        Assert.DoesNotContain("DORMANT", sb.ToString());
    }

    // SEQ-INPLACE-UNCOVERED an SGE quest absent from the .seq is returned (qNotListed), a covered one (qOk) is not, non-SGE ignored
    [Fact]
    public void TheDetectorReturnsOnlyTheUncoveredSgeQuest()
    {
        var uncovered = SeqFile.UncoveredSgeQuests(_f.OkPath, File.ReadAllBytes(_f.OkSeq));
        Assert.Equal(_f.NotListed, Assert.Single(uncovered).FormKey);
    }

    // SEQ-INPLACE-FRESH a .seq listing every SGE quest at its current on-disk FormID → none uncovered
    [Fact]
    public void ASeqListingEverySgeQuestLeavesNoneUncovered()
    {
        var full = SeqFile.Serialize(new[] { SeqFile.OnDiskFormIdFromPlugin(_f.OkPath, _f.Ok), SeqFile.OnDiskFormIdFromPlugin(_f.OkPath, _f.NotListed) });
        Assert.Empty(SeqFile.UncoveredSgeQuests(_f.OkPath, full));
    }

    // SEQ-INPLACE-ALL-STALE a .seq matching no current FormID → both SGE quests uncovered
    [Fact]
    public void ASeqMatchingNoCurrentFormIdLeavesEverySgeQuestUncovered() =>
        Assert.Equal(2, SeqFile.UncoveredSgeQuests(_f.OkPath, SeqFile.Serialize(new[] { 0xDEADBEEFu })).Count);

    static string Show(SeqLintFinding? s) => s is null ? "SeqLint=null"
        : $"sge={s.QuestIsSge} def={s.DefiningPlugin} win={s.WinnerPlugin} exists={s.SeqExists} contains={s.SeqContainsQuest} newer={s.SeqNewerThanPlugin} note=[{s.Note}]";
}

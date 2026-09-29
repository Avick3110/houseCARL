using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The dialogue validator's condition lints, each malformed shape against a well-formed control, and the
/// PlayerRef whitelist. From the retired <c>dialogue-validate-guard</c> probe.</summary>
[Trait("tier", "integration")]
public class DialogueValidateConditionTests
{
    static bool Warns(string seed, params string[] words)
        => DialogueValidateWorld.Topic(seed).Issues.Any(i => i.Severity == DialogueIssueSeverity.Warning
            && words.All(w => i.Message.Contains(w, StringComparison.Ordinal)));

    static void Clean(string seed)
    {
        var t = DialogueValidateWorld.Topic(seed);
        Assert.Equal(1, t.ConditionedInfoCount);
        Assert.Empty(t.Issues);
    }

    // CTDA-COUNT: a line carrying a Condition is counted.
    [Fact]
    public void AConditionedLineIsCounted()
        => Assert.True(DialogueValidateWorld.Topic("ctda-count").ConditionedInfoCount >= 1);

    // COND-CLEAN: GetStage on the owning quest plus GetActorValue report zero issues.
    [Fact]
    public void TwoWellFormedConditionsReportNoIssues() => Clean("cond-clean");

    // COND-REF-UNSET: Run On a specific Reference with none set warns 'Run On a specific reference'.
    [Fact]
    public void RunOnAReferenceWithNoneSetWarns() => Assert.True(Warns("cond-ref-unset", "Run On a specific reference"));

    // COND-DEAD-ALIAS: GetIsAliasRef at an alias the quest does not define warns 'no alias with that ID'.
    [Fact]
    public void AnAliasIndexTheQuestLacksWarns() => Assert.True(Warns("cond-dead-alias", "no alias with that ID"));

    // COND-DEAD-LOCALIAS: GetInCurrentLocAlias at an undefined alias warns 'location-alias', 'no alias with that ID'.
    [Fact]
    public void ALocationAliasIndexTheQuestLacksWarns() => Assert.True(Warns("cond-dead-localias", "location-alias", "no alias with that ID"));

    // COND-DEAD-QUESTALIAS: Run On QuestAlias at an undefined alias warns 'reference-alias', 'no alias with that ID'.
    [Fact]
    public void RunOnAQuestAliasTheQuestLacksWarns() => Assert.True(Warns("cond-dead-questalias", "reference-alias", "no alias with that ID"));

    // COND-ALIAS-OK: GetIsAliasRef at a real alias index is not flagged.
    [Fact]
    public void ARealAliasIndexIsNotFlagged() => Clean("cond-alias-ok");

    // COND-ALIAS-FLOI: an alias-mode form parameter (HasPerk.Perk = alias 7) is not flagged.
    [Fact]
    public void AnAliasModeFormParameterIsNotFlagged() => Clean("cond-alias-floi");

    // COND-DANGLING-PARAM: a condition form parameter not in the order warns 'not in the active load order'.
    [Fact]
    public void AConditionFormParameterNotInTheOrderWarns() => Assert.True(Warns("cond-dangling-param", "condition", "not in the active load order"));

    // COND-DANGLING-GLOBAL: a ConditionGlobal against a missing GLOB warns 'compares against global'.
    [Fact]
    public void AConditionGlobalNotInTheOrderWarns() => Assert.True(Warns("cond-dangling-global", "compares against global"));

    // COND-GETISID-OK: GetIsID at a base object is not flagged.
    [Fact]
    public void GetIsIdAtABaseObjectIsNotFlagged() => Clean("cond-getisid-ok");

    // COND-GETISID-PLACED: GetIsID at a placed reference warns 'placed reference'.
    [Fact]
    public void GetIsIdAtAPlacedReferenceWarns() => Assert.True(Warns("cond-getisid-placed", "placed reference"));

    // PLAYERREF-WHITELIST: Run On PlayerRef (000014:Skyrim.esm), absent from the order, is not flagged.
    [Fact]
    public void RunOnPlayerRefIsNotFlagged() => Assert.Empty(DialogueValidateWorld.Topic("playerref").Issues);

    // PLAYERREF-CONTROL: Run On a missing reference that is not whitelisted still warns.
    [Fact]
    public void RunOnAMissingReferenceWarns() => Assert.True(Warns("playerref-control", "Run On reference", "not in the active load order"));
}

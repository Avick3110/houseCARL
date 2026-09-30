using HousecarlCore;
using HousecarlMcp;
using Xunit;

using static HousecarlMcpTests.CheckMergeFixture;

namespace HousecarlMcpTests;

/// <summary>The dialogue family inside the merged check response: its seed grammar, its family-local refusal, its
/// accounting and its boundary, driven through the real <c>DialogueSweep.Run</c> with a stub validator. Each fact
/// carries the <c>check-guard</c> probe arm it replaces.</summary>
[Trait("tier", "integration")]
public class CheckMergeDialogueTests
{
    static CheckSweep Only(DialogueCheckResult d) => new(Sel("dialogue"), null, null, d);

    static DialogueCheckResult Budgeted()
        => DialogueRun(new[] { "000001:A.esp", "000002:A.esp", "000003:A.esp", "000004:A.esp", "000005:A.esp" }, 2);

    static CheckSweep Mixed() => new(Sel("errors", "dialogue"), Errors, null, DialogueRun(null, 1000));

    // DIALOGUE-REFUSED-WITHOUT-SEEDS: an unseeded dialogue family refuses in its own section and spells seeds=
    [Fact]
    public void AnUnseededDialogueFamilyRefusesInItsOwnSection()
    {
        var unseeded = DialogueRun(null, 1000);
        Assert.NotNull(unseeded.Error);
        var text = Text(Mixed(), 0);
        Assert.Contains("[dialogue] ", text);
        Assert.Contains("will NOT sweep the whole load order", text);
        Assert.Contains("82,343", text);
        Assert.Contains("seeds=[\"XXXXXX:Plugin.esp\"]", text);
        Assert.DoesNotContain("topic(s) these seeds own", text);
        Assert.Equal(Npcs, StatedPair(text, " dangling ref(s) found by this sweep appear above"));
        Assert.False(text.StartsWith("error:", StringComparison.Ordinal));

        var json = Json(Mixed(), 0);
        var dialogue = Family(json, "dialogue");
        var acct = Obj(dialogue, "accounting");
        Assert.NotNull(acct);
        Assert.False(Has(acct, "dialogue_topics_found"));
        Assert.False(Has(acct, "seeds_validated"));
        Assert.Equal(false, Bool(acct, "listing"));
        Assert.Contains("seeds=", Str(dialogue, "refused"));
        Assert.NotNull(Family(json, "errors"));
    }

    // SCOPE-SENTENCE-NAMES-A-REFUSED-FAMILY: the sentence and families_ran say what ANSWERED
    [Fact]
    public void TheScopeSentenceNamesARefusedFamilyAsRefused()
    {
        var text = Text(Mixed(), 0);
        string scope = FirstLineWith(text, "findings=");
        string dlg = SweepFamilySelection.Describe(SweepFamily.Dialogue);
        Assert.Contains("did NOT answer for", scope);
        Assert.Contains(dlg, scope);
        Assert.DoesNotContain("answers for: " + SweepFamilySelection.Describe(SweepFamily.Errors) + ", " + dlg, scope);
        var root = Root(Json(Mixed(), 0));
        var ran = Strings(Arr(root, "families_ran"));
        Assert.DoesNotContain("dialogue", ran);
        Assert.Contains("errors", ran);
        var refused = Arr(root, "families_refused");
        Assert.Equal(new[] { "dialogue" },
                     Enumerable.Range(0, refused?.GetArrayLength() ?? 0).Select(i => Str(At(refused, i), "family")));
        Assert.Equal(scope, Str(root, "findings_scope"));
    }

    // REFUSED-FAMILY-DECLARES-NO-SUBJECT-COUNTS: a refused family states no zeros; the family beside it still does
    [Fact]
    public void ARefusedFamilyDeclaresNoSubjectCountsWhileItsSiblingDoes()
    {
        var json = Json(Mixed(), 0);
        var dlg = Obj(Family(json, "dialogue"), "accounting");
        var err = Obj(Family(json, "errors"), "accounting");
        Assert.NotNull(dlg);
        Assert.NotNull(err);
        foreach (var key in new[] { "excluded_plugins_total", "excluded_plugins_named", "unread_plugins_total",
                                    "unread_plugins_named", "dangling_missing_by_source", "dangling_missing_by_source_total" })
            Assert.False(Has(dlg, key), key);
        Assert.Equal(Wire.DefaultMaxChars, Num(dlg, "max_chars"));
        Assert.Equal(false, Bool(dlg, "listing"));
        Assert.True(Has(err, "dangling_missing_by_source"));
        Assert.True(Has(err, "dangling_missing_by_source_total"));
        Assert.Equal(Npcs, Num(err, "dangling_found"));
        Assert.Equal(1, Count(Text(Mixed(), 0), CheckSentences.SweepAccountingLead));
    }

    // DIALOGUE-NOT-PLUGIN-SCOPED: the section states plugins=/exclude= do not narrow it and names source=
    [Fact]
    public void TheDialogueSectionSaysPluginScopeDoesNotNarrowIt()
    {
        var text = Text(Only(Dialogue()), 0);
        var fam = Family(Json(Only(Dialogue()), 0), "dialogue");
        Assert.Contains("seeded, not swept", text);
        Assert.Contains("do NOT scope it", text);
        Assert.Contains("off-order lane is source=", text);
        Assert.Equal(FirstLineWith(text, "scope:"), Str(fam, "scope"));
        Assert.Equal(true, Bool(fam, "seeded_not_swept"));
    }

    // DIALOGUE-COUNTS: the section states what the validation FOUND, validated against reached, in both transports
    [Fact]
    public void TheDialogueCountsLineStatesValidatedAgainstReached()
    {
        var text = Text(Only(Dialogue()), 0);
        var fam = Family(Json(Only(Dialogue()), 0), "dialogue");
        Assert.Contains($"1 of the {1 + UnreachableSeeds} seed(s) reached were validated, {Topics} topic(s), {DialogueFindings} finding(s)", text);
        Assert.Equal(Topics, Num(fam, "topics_found"));
        Assert.Equal(DialogueFindings, Num(fam, "findings_found"));
        Assert.Equal(1 + UnreachableSeeds, Num(fam, "seeds_named"));
        Assert.Equal(1 + UnreachableSeeds, Num(fam, "seeds_reached"));
        Assert.Equal(1, Num(fam, "seeds_validated"));
        Assert.Equal(UnreachableSeeds, Num(fam, "seeds_unreachable_total"));
    }

    // DIALOGUE-CLASS-8-ABSENT: no effective-INFO-order render here, and the boundary names records project=info_order
    [Fact]
    public void TheDialogueSectionRendersNoInfoOrderAndPointsAtRecords()
    {
        var text = Text(Only(Dialogue()), 0);
        var json = Json(Only(Dialogue()), 0);
        Assert.DoesNotContain("effective INFO order", text);
        Assert.DoesNotContain("INFO order:", text);
        Assert.DoesNotContain("info_order\":", json);
        Assert.Contains("records project=info_order", text);
        Assert.Contains("records project=info_order", json);
    }

    // DIALOGUE-UNREACHABLE-SEEDS-NAMED: unreachable seeds are named in the listing lane AND under counts_only
    [Fact]
    public void UnreachableSeedsAreNamedInBothLanes()
    {
        var listing = Text(Only(Dialogue()), 0);
        var counts = Text(Only(Dialogue() with { CountsOnly = true }), 0);
        Assert.Equal(UnreachableSeeds, Count(listing, "NOT validated:"));
        Assert.Equal(UnreachableSeeds, Count(counts, "NOT validated:"));
        Assert.Contains("no per-topic blocks", counts);
        Assert.DoesNotContain("HcCmTopic00", counts);
        Assert.Contains("HcCmTopic00", listing);
    }

    // DIALOGUE-SEED-BUDGET: limit= caps how many SEEDS a call expands, and the accounting names the rest
    [Fact]
    public void LimitCapsSeedsAndTheAccountingNamesTheUnreached()
    {
        var b = Budgeted();
        var text = Text(Only(b), 0);
        Assert.Equal(5, b.SeedsNamed);
        Assert.Equal(2, b.Seeds.Count);
        Assert.Contains("2 of the 5 seed(s) named were reached; 3 were NOT reached", text);
        Assert.Contains("limit=", text);
    }

    // DIALOGUE-SCOPE-COUNTS-WHAT-IT-REACHED: the scope sentence states what it reached, names limit=, both transports
    [Fact]
    public void TheDialogueScopeSentenceStatesWhatItReached()
    {
        var scope = FirstLineWith(Text(Only(Budgeted()), 0), "scope:");
        Assert.Contains("It reached 2 of the 5 seed(s)", scope);
        Assert.DoesNotContain("all 5 seed(s)", scope);
        Assert.DoesNotContain("It validated", scope);
        Assert.Contains("limit=", scope);
        Assert.Equal(scope, Str(Family(Json(Only(Budgeted()), 0), "dialogue"), "scope"));
    }

    // DIALOGUE-CUT-IS-STATED-IN-SEEDS: where a cap cuts this family's rows the accounting counts SEED sections
    [Fact]
    public void ADialogueCutIsCountedInSeedSections()
    {
        var sweep = Only(Budgeted());
        bool sawCut = false;
        for (int cap = 400; cap <= 6000; cap += 20)
        {
            var t = Text(sweep, cap);
            if (t.Contains("seed section(s) were rendered", StringComparison.Ordinal)) sawCut = true;
            Assert.False(t.Contains("plugin section(s) were rendered", StringComparison.Ordinal), $"@{cap}");
        }
        Assert.True(sawCut, "no cap in 400..6000 cut the seed sections");
    }

    // DIALOGUE-SEED-FACTS-IN-BOTH-LANES: named/reached/validated/unreachable in the head of both lanes
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SeedFactsAreInTheHeadOfBothLanes(bool countsOnly)
    {
        var b = Budgeted();
        var sweep = Only(countsOnly ? b with { CountsOnly = true } : b);
        if (countsOnly)
            Assert.Contains("2 of the 5 seed(s) named were reached; 3 were NOT reached", Text(sweep, 0));
        var fam = Family(Json(sweep, 0), "dialogue");
        Assert.Equal(5, Num(fam, "seeds_named"));
        Assert.Equal(2, Num(fam, "seeds_reached"));
        Assert.Equal(b.Resolved.Count(), Num(fam, "seeds_validated"));
        Assert.Equal(b.Unresolved.Count, Num(fam, "seeds_unreachable_total"));
        Assert.Equal(3, Num(Obj(fam, "accounting"), "seeds_not_reached_by_budget"));
        if (countsOnly) Assert.False(Has(Obj(fam, "accounting"), "dialogue_topics_rendered"));
    }

    // DIALOGUE-FOUR-POPULATIONS-ARE-FOUR-NUMBERS: named 5, reached 2, validated 1, unreachable 1, each as itself
    [Fact]
    public void TheFourSeedPopulationsAreFourNumbers()
    {
        var four = DialogueRun(new[] { "000A01:A.esp", "000B02:B.esp", "000C03:A.esp", "000D04:A.esp", "000E05:A.esp" }, 2);
        Assert.Equal(5, four.SeedsNamed);
        Assert.Equal(2, four.Seeds.Count);
        Assert.Single(four.Resolved);
        Assert.Single(four.Unresolved);
        var text = Text(Only(four), 0);
        Assert.Contains("It reached 2 of the 5 seed(s)", text);
        Assert.Contains("1 of the 2 seed(s) reached were validated", text);
        var fam = Family(Json(Only(four), 0), "dialogue");
        Assert.Equal(5, Num(fam, "seeds_named"));
        Assert.Equal(2, Num(fam, "seeds_reached"));
        Assert.Equal(1, Num(fam, "seeds_validated"));
        Assert.Equal(1, Num(fam, "seeds_unreachable_total"));
    }

    // COUNTS-ONLY-CARRIES-NO-SEEDS-ARRAY: seed rows only where the mode renders them; the unreachable roster in both
    [Fact]
    public void CountsOnlyCarriesNoSeedsArrayButBothCarryTheUnreachable()
    {
        var counts = Family(Json(Only(Budgeted() with { CountsOnly = true }), 0), "dialogue");
        var listing = Family(Json(Only(Budgeted()), 0), "dialogue");
        Assert.Null(Arr(counts, "seeds"));
        Assert.True(Arr(listing, "seeds")?.GetArrayLength() > 0);
        Assert.NotNull(Arr(listing, "seeds_unreachable"));
        Assert.NotNull(Arr(counts, "seeds_unreachable"));
    }

    // DIALOGUE-FINISHED-SEED-HANDS-BACK-ITS-SHARE: at a cap sized for two thirds of the topics, blocks spend > half
    [Fact]
    public void AOneSeedCallHandsTheSeedSharesRoomToItsTopicBlocks()
    {
        var one = DialogueRun(new[] { "000A01:HcCm.esp" }, 1000);
        var sweep = Only(one);
        int floor = QuietFloor(sweep, "  topic ");
        int block = TopicBlockWidth(one);
        int cap = floor + block * (Topics * 2 / 3);
        int rendered = Count(Text(sweep, cap), "  topic ");
        Assert.True(floor > 0);
        Assert.True(rendered * block > (cap - floor) / 2, $"cap={cap} floor={floor} block={block} rendered={rendered}");
        Assert.True(rendered <= Topics);
    }

    // DIALOGUE-FINISHED-SEED-HANDS-BACK-ITS-SHARE (json): the same, in the transport's own units
    [Fact]
    public void AOneSeedCallHandsTheSeedSharesRoomToItsTopicsInJson()
    {
        var one = DialogueRun(new[] { "000A01:HcCm.esp" }, 1000);
        var sweep = Only(one);
        int floor = Json(sweep, 1).Length;
        int row = (Json(sweep, 0).Length - Json(Only(WithTopics(one, 1)), 0).Length) / (Topics - 1);
        int cap = floor + row * (Topics * 2 / 3);
        int rendered = Count(Json(sweep, cap), "\"topic\":");
        Assert.True(rendered > (cap - floor) / (2 * row), $"cap={cap} floor={floor} row={row} rendered={rendered}");
        Assert.True(rendered <= Topics);
    }

    // DIALOGUE-BOTH-SEED-HEADS-KEEP-THEIR-SHARE: at a cap that cuts the topic blocks, both seed heads are written
    [Fact]
    public void ATwoSeedCallWritesBothHeadsAtACapThatCutsTopics()
    {
        var two = DialogueRun(new[] { "000A01:A.esp", "000B02:A.esp" }, 1000);
        var sweep = Only(two);
        int cap = RenderFloorAssert.Named(Text(sweep, 1)) + TopicBlockWidth(two) * (Topics * 2 / 3);   // the floor its refusal names (#986)
        var text = Text(sweep, cap);
        int topics = Count(text, "  topic ");
        Assert.Equal(2, Count(text, "\nseed "));
        Assert.InRange(topics, 1, Topics * 2 - 1);
    }

    // DIALOGUE-BOUNDARY-UNREFUSABLE: the standing-limits boundary is written at every cap that serves; below the
    // floor the call is refused (#986), and the cap it names serves the boundary
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(5)] [InlineData(10)] [InlineData(50)] [InlineData(200)]
    [InlineData(800)] [InlineData(2000)] [InlineData(6000)] [InlineData(12000)] [InlineData(40000)]
    public void TheDialogueBoundaryIsWrittenAtEveryCap(int cap)
    {
        var sweep = Only(Dialogue());
        var t = Text(sweep, cap);
        if (RenderFloorAssert.IsFloorRefusal(t)) t = RenderFloorAssert.RefusesAndTheNamedCapFits(t, cap, c => Text(sweep, c));
        Assert.Contains("does NOT mean the dialogue will play as intended", t);
    }

    // DIALOGUE-A-RECORD-LEVEL-SEED-STATES-ITS-VERDICT: passing DLVW/DLBR carry their own OK line; failing, the issue
    [Fact]
    public void ARecordLevelSeedStatesItsOwnVerdict()
    {
        var pass = Text(Only(RecordLevel()), 0);
        var fail = Text(Only(RecordLevelFailing()), 0);
        Assert.Contains("CK-parity: OK — the DNAM and ENAM byte subrecords", pass);
        Assert.Contains("CK-parity: OK — the TNAM (Category) and DNAM (Flags) subrecords", pass);
        Assert.DoesNotContain("CK-parity: OK", fail);
        Assert.Contains("the DNAM byte subrecord the Creation Kit always writes is absent", fail);
        Assert.Contains("quest CK-parity: OK", Text(Only(Dialogue()), 0));
    }

    // DIALOGUE-THE-BOUNDARY-CLAIMS-ONLY-THE-CHECKS-THAT-RAN: record-level seeds get the narrow boundary
    [Fact]
    public void TheBoundaryClaimsOnlyTheChecksThatRan()
    {
        var rl = Text(Only(RecordLevel()), 0);
        var quest = Text(Only(Dialogue()), 0);
        var mixed = Text(Only(MixedKind()), 0);
        Assert.Contains("record-level CK-parity check only", rl);
        Assert.DoesNotContain("LinkTo and previous-link targets", rl);
        Assert.DoesNotContain("each result script bound and compiled", rl);
        Assert.DoesNotContain("records project=info_order", rl);
        Assert.Contains("LinkTo and previous-link targets", quest);
        Assert.DoesNotContain("record-level CK-parity check only", quest);
        Assert.Contains("LinkTo and previous-link targets", mixed);
        Assert.Equal(1, Count(mixed, "record-level CK-parity check only"));
    }

    // DIALOGUE-THE-STAMP-DECLARES-ONLY-THE-BOUND-THIS-RESPONSE-HAS: the epoch names uncovered classes only where they ran
    [Fact]
    public void TheStampNamesUncoveredClassesOnlyWhereThoseChecksRan()
    {
        var rl = Text(Only(RecordLevel()), 0);
        var quest = Text(Only(Dialogue()), 0);
        Assert.Contains("it covers every verdict here", rl);
        Assert.DoesNotContain("does not cover", rl);
        Assert.Contains("does not cover: voiced lines (.fuz on disk)", quest);
        Assert.DoesNotContain("it covers every verdict here", quest);
        Assert.Contains("does not cover: voiced lines (.fuz on disk)", Text(Only(MixedKind()), 0));
    }

    // DIALOGUE-THE-KIND-AWARE-VERDICT-AND-BOUNDARY-ARE-THE-SAME-IN-JSON
    [Fact]
    public void TheKindAwareVerdictAndBoundaryAreTheSameInJson()
    {
        var fam = Family(Json(Only(RecordLevel()), 0), "dialogue");
        var questFam = Family(Json(Only(Dialogue()), 0), "dialogue");
        var seed0 = At(Arr(fam, "seeds"), 0);
        Assert.Contains("record-level CK-parity check only", Str(fam, "boundary"));
        Assert.DoesNotContain("LinkTo and previous-link targets", Str(fam, "boundary"));
        Assert.Equal(new[] { "record_parity" }, Strings(Arr(seed0, "checks_run")));
        Assert.Equal(0, Arr(seed0, "input_issues")?.GetArrayLength());
        Assert.Equal(new[] { "record_parity", "topic_graph" }, Strings(Arr(At(Arr(questFam, "seeds"), 0), "checks_run")));
        Assert.Contains("LinkTo and previous-link targets", Str(questFam, "boundary"));
        Assert.Equal(true, Bool(fam, "epoch_covers_all_inputs"));
        Assert.Null(Arr(fam, "epoch_uncovered"));
        Assert.Equal(false, Bool(questFam, "epoch_covers_all_inputs"));
        Assert.Equal(3, Strings(Arr(questFam, "epoch_uncovered")).Length);
    }
}

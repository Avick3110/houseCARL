using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The unbounded reverse question — <c>references=</c> with no <c>types=</c>/<c>plugins=</c> scope —
/// answered off the reverse-reference index, driven through the tool.</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsReverseIndexTests : RecordsTestBase
{
    public RecordsReverseIndexTests(RecordsFixture f) : base(f) { }

    [Fact]
    public void AnUnboundedReferencesFindsEveryReferencerWithNoScope()
    {
        var r = RecordsTools.Records(Svc, references: new[] { Fid(W.MgefA) });
        Served(r, "HcRecSpellA", "HcRecSpellC");
        Assert.DoesNotContain("HcRecSpellB", r);
    }

    [Fact]
    public void AnUnboundedReferencesOverTwoTargetsUnionsThem()
    {
        var r = RecordsTools.Records(Svc, references: new[] { Fid(W.MgefA), Fid(W.MgefB) });
        Served(r, "HcRecSpellA", "HcRecSpellB", "HcRecSpellC");
    }

    [Fact]
    public void AnUnboundedReferencesOnATargetNothingLinksMatchesNothing()
    {
        var r = RecordsTools.Records(Svc, references: new[] { Fid(W.SpellB) });
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.DoesNotContain("HcRecSpell", r);
    }

    [Fact]
    public void AnUnboundedNegatedReferencesKeepsTheRecordsThatDoNotLinkTheTarget()
    {
        var r = RecordsTools.Records(Svc, references: new[] { "!" + Fid(W.MgefA) });
        Served(r, "HcRecSpellB");
        Assert.DoesNotContain("HcRecSpellA", r);
        Assert.DoesNotContain("HcRecSpellC", r);
    }

    /// <summary>SPEC §2.2's and §3.2's 2026-09-05 amendments read the unbounded negated spelling as the orphan
    /// sweep, so a record something DOES reference is outside the universe however the target reads.</summary>
    [Fact]
    public void AnUnboundedNegatedReferencesIsTheOrphanSweep()
    {
        var r = RecordsTools.Records(Svc, references: new[] { "!" + Fid(W.SpellB) }, limit: 500);
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains("ORPHAN sweep", r);
        Assert.Contains("HcRecSpellA", r);            // nothing links a spell — an orphan
        Assert.DoesNotContain("HcRecMgefFire", r);    // two spells link it — not an orphan
        Assert.DoesNotContain("HcRecNpcParent", r);   // the child's Template links it — not an orphan
    }

    /// <summary>The orphan sweep's universe is millions of records on a real order, and the comparison forms
    /// consume every match uncapped — so the pair is refused with the bound named.</summary>
    [Fact]
    public void TheOrphanSweepIsRefusedUnderAComparisonForm() =>
        Refused(RecordsTools.Records(Svc, references: new[] { "!" + Fid(W.MgefA) }, project: Form("tree")),
                "orphan sweep");

    /// <summary>A positive unbounded references= is NOT the sweep — its universe is what links the target — so the
    /// same forms compose with it.</summary>
    [Fact]
    public void APositiveUnboundedReferencesStillComposesWithAComparisonForm()
    {
        var r = RecordsTools.Records(Svc, references: new[] { Fid(W.MgefA) }, project: Form("tree"));
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
    }

    /// <summary>Every form that consumes the scan as a SELECTION renders the index's accounting — otherwise a
    /// 24-second build and a coverage gap go unmentioned because the response came from another pipeline.</summary>
    [Theory]
    [InlineData("everything")]
    [InlineData("tree")]
    public void TheIndexAccountingRidesTheDerivedForms(string form)
    {
        var r = RecordsTools.Records(Svc, references: new[] { Fid(W.MgefA) }, project: Form(form));
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains("reverse-reference index", r);
        Assert.Contains("key=", r);
    }

    [Fact]
    public void TheIndexAccountingRidesTheJsonLane()
    {
        var r = RecordsTools.Records(Svc, references: new[] { Fid(W.MgefA) }, format: "json");
        Assert.Contains("\"notes\"", r);
        Assert.Contains("reverse-reference index", r);
        Assert.Contains("key=", r);
    }

    /// <summary>An in-order source= plugin IS a bound — the scan runs with that one plugin as its scope — so the
    /// sweep refusal must read the scope the scan will actually use, not the plugins= parameter alone.</summary>
    [Fact]
    public void ANegatedReferencesUnderAnInOrderSourceIsNotTheSweep()
    {
        var r = RecordsTools.Records(Svc, references: new[] { "!" + Fid(W.MgefA) },
                                     source: Plugin(W.MidName), project: Form("tree"));
        Assert.DoesNotContain("orphan sweep", r);
    }

    /// <summary>group_by='type' needs a body-bearing scope. An unbounded references= is one: the index hands the
    /// scan its universe and every match's body is read, which is what names the type.</summary>
    [Fact]
    public void AnUnboundedReferencesGroupsByType()
    {
        var r = RecordsTools.Records(Svc, references: new[] { Fid(W.MgefA) },
                                     project: new RecordsTools.RecordsProject { form = "aggregate", group_by = "type" });
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains("reverse-reference index", r);
        Assert.Contains("Spell", r);
    }

    [Fact]
    public void AnUnboundedWhereIsStillRefusedNamingTheBound() =>
        Refused(RecordsTools.Records(Svc, where: new[] { "EditorID = HcRecSpellA" }), "where=");

    [Fact]
    public void AnUnboundedReferencesWithAWhereIsStillRefused() =>
        Refused(RecordsTools.Records(Svc, references: new[] { Fid(W.MgefA) },
                                     where: new[] { "EditorID = HcRecSpellA" }), "types=");

    // ---- the transitive reverse walk (walk.direction='reverse' under a reading form) -------------------

    /// <summary>Depth repeats the follow rule at every hop: hop 1 is what links the seed, hop 2 what links those.
    /// MgefHop &lt;- SpellHop &lt;- ListHop, so the second hop is the one that proves transitivity.</summary>
    [Fact]
    public void AReverseWalkAtDepthTwoReachesTheSecondHopReferrer()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.MgefHop) },
                                     walk: new RecordsTools.RecordsWalk { direction = "reverse", depth = 2 });
        Served(r, "HcRecSpellHop", "HcRecListHop", "hop 1: 1", "hop 2: 1");
    }

    /// <summary>Depth 1 is the same walk with one hop: the second-hop referrer is NOT in it, so the depth is doing
    /// the work rather than the lane reaching everything and calling it a depth.</summary>
    [Fact]
    public void AReverseWalkAtDepthOneStopsAtTheFirstHop()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.MgefHop) },
                                     walk: new RecordsTools.RecordsWalk { direction = "reverse", depth = 1 });
        Served(r, "HcRecSpellHop", "hop 1: 1");
        Assert.DoesNotContain("HcRecListHop", r);
    }

    /// <summary>An exhausted walk says which hops it did not need to walk, so a depth-6 call that ran out at hop 3
    /// reads as exhausted rather than as a silent stop.</summary>
    [Fact]
    public void AReverseWalkThatRunsOutOfReferrersSaysSo()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.MgefHop) },
                                     walk: new RecordsTools.RecordsWalk { direction = "reverse", depth = 6 });
        Served(r, "hop 3: 0", "nothing left to expand");
    }

    /// <summary>The index's accounting rides this lane too — the build cost, the freshness key and the coverage
    /// disclosures are true of the walk's answer exactly as they are of a references= answer.</summary>
    [Fact]
    public void TheIndexAccountingRidesTheReverseWalk() =>
        Served(RecordsTools.Records(Svc, formids: new[] { Fid(W.MgefHop) },
                                    walk: new RecordsTools.RecordsWalk { direction = "reverse", depth = 2 }),
               "reverse-reference index", "key=");

    /// <summary>types= narrows the typed carrier walk's carrier types; this walk reaches every type, so the pair
    /// refuses with the re-entry spelling named rather than filtering something else.</summary>
    [Fact]
    public void TypesOnTheTransitiveReverseWalkRefusesNamingTheReEntrySpelling() =>
        Refused(RecordsTools.Records(Svc, formids: new[] { Fid(W.MgefHop) }, types: new[] { "Spell" },
                                     walk: new RecordsTools.RecordsWalk { direction = "reverse", depth = 2 }),
                "to_file");

    // ---- follow= is what tells the two reverse walks apart -------------------------------------------

    /// <summary>follow is legal on reverse: it names the edges the walk crosses, which is as meaningful backwards
    /// as forwards. "*" is the transitive walk, and it reaches the same set the unset spelling does.</summary>
    [Fact]
    public void AnExplicitFollowStarOnReverseIsTheTransitiveWalk()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.MgefHop) },
                                     walk: new RecordsTools.RecordsWalk { direction = "reverse", depth = 2, follow = "*" });
        Served(r, "HcRecSpellHop", "HcRecListHop", "hop 1: 1", "hop 2: 1");
    }

    /// <summary>The carrier follow picks the typed MGEF walk under a READING form too, so the form only chooses
    /// the view: the reached set is the carriers either way.</summary>
    [Fact]
    public void TheCarrierFollowUnderAReadingFormConsumesTheCarrierReachedSet()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.MgefHop) },
                                     walk: new RecordsTools.RecordsWalk { direction = "reverse", follow = "Effects[].BaseEffect" });
        Served(r, "HcRecSpellHop");
        Assert.DoesNotContain("HcRecListHop", r);
    }

    /// <summary>A follow the reverse index cannot serve refuses naming the two it can, rather than being ignored
    /// and answering a different question.</summary>
    [Fact]
    public void AnUnservableFollowOnReverseRefusesNamingTheTwoItServes() =>
        Refused(RecordsTools.Records(Svc, formids: new[] { Fid(W.MgefHop) },
                                     walk: new RecordsTools.RecordsWalk { direction = "reverse", follow = "Template" }),
                "Effects[].BaseEffect");

    /// <summary>The transitive walk expands one shared frontier, so it has no per-seed path for chain to draw —
    /// refused with both the reading forms and the carrier follow named.</summary>
    [Fact]
    public void ChainOverTheTransitiveReverseWalkRefusesNamingTheCarrierFollow() =>
        Refused(RecordsTools.Records(Svc, formids: new[] { Fid(W.MgefHop) },
                                     walk: new RecordsTools.RecordsWalk { direction = "reverse", follow = "*" },
                                     project: new RecordsTools.RecordsProject { form = "chain" }),
                "Effects[].BaseEffect");

    /// <summary>No default follow implies a walk under any form: chain on a reverse walk with follow unset refuses
    /// naming both follows the index serves, rather than quietly meaning the carrier one.</summary>
    [Fact]
    public void ChainOnAReverseWalkWithFollowUnsetRefusesNamingBothFollows()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.MgefHop) },
                                     walk: new RecordsTools.RecordsWalk { direction = "reverse" },
                                     project: new RecordsTools.RecordsProject { form = "chain" });
        Refused(r, "Effects[].BaseEffect");
        Assert.Contains("\"*\"", r);
        Assert.DoesNotContain("HcRecSpellHop", r);
    }

    /// <summary>The same call with the carrier follow said outright is served, so the refusal above is about the
    /// missing follow and not about the chain form on reverse.</summary>
    [Fact]
    public void TheSameChainCallWithTheCarrierFollowSaidOutrightIsServed() =>
        Served(RecordsTools.Records(Svc, formids: new[] { Fid(W.MgefHop) },
                                    walk: new RecordsTools.RecordsWalk { direction = "reverse", follow = "Effects[].BaseEffect" },
                                    project: new RecordsTools.RecordsProject { form = "chain" }),
               "HcRecSpellHop");

    /// <summary>A bad direction is taught the follow that picks the reverse walk, not the deleted depth-1 rule.
    /// </summary>
    [Fact]
    public void ABadDirectionNoLongerTeachesTheDeletedDepthOneRule()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.MgefHop) },
                                     walk: new RecordsTools.RecordsWalk { direction = "backward" });
        Assert.StartsWith("error:", r);
        Assert.DoesNotContain("depth 1", r);
    }

    // ---- the candidate set is verified, the budget is honest, the seeds are deduplicated ---------------

    /// <summary>The index names a candidate whose WINNER dropped the link. references= excludes it, and so must
    /// the walk — the two spellings of one question cannot disagree.</summary>
    [Fact]
    public void TheReverseWalkExcludesACandidateWhoseWinnerDroppedTheLink()
    {
        var refs = RecordsTools.Records(Svc, references: new[] { Fid(W.SpellHop) });
        Assert.DoesNotContain("HcRecListDropped", refs);
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellHop) },
                                     walk: new RecordsTools.RecordsWalk { direction = "reverse", depth = 1 });
        Served(r, "HcRecListHop", "hop 1: 1");
        Assert.DoesNotContain("HcRecListDropped", r);
    }

    /// <summary>And the drop is said out loud rather than left as a count the caller cannot account for.</summary>
    [Fact]
    public void TheReverseWalkSaysHowManyIndexCandidatesTheBodyCheckDropped() =>
        Served(RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellHop) },
                                    walk: new RecordsTools.RecordsWalk { direction = "reverse", depth = 1 }),
               "index candidate(s) were dropped");

    /// <summary>The drop line names the cause it actually saw rather than asserting one for all four: this
    /// candidate's winner dropped the link, and an unreadable winner would have read as a coverage gap.</summary>
    [Fact]
    public void TheDropLineNamesTheCauseRatherThanAssertingOne() =>
        Served(RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellHop) },
                                    walk: new RecordsTools.RecordsWalk { direction = "reverse", depth = 1 }),
               "whose winner does not carry the link");

    /// <summary>A walk that stopped at walk.depth with the last hop still finding records says so, so a prefix is
    /// never read as a complete answer — and it is not called exhausted.</summary>
    [Fact]
    public void AWalkStoppedByWalkDepthSaysTheCapCutIt()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.MgefHop) },
                                     walk: new RecordsTools.RecordsWalk { direction = "reverse", depth = 1 });
        Served(r, "walk.depth=1 was reached");
        Assert.DoesNotContain("nothing left to expand", r);
    }

    /// <summary>A walk that genuinely ran out before the cap still says THAT, not the depth clause.</summary>
    [Fact]
    public void AWalkThatRanOutBeforeTheCapIsStillCalledExhausted()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.MgefHop) },
                                     walk: new RecordsTools.RecordsWalk { direction = "reverse", depth = 5 });
        Served(r, "nothing left to expand");
        Assert.DoesNotContain("was reached with hop", r);
    }

    /// <summary>A spent budget stops the work, not just the reach: HcRecListHop fills a budget of 1, and the
    /// candidate behind it (HcRecListDropped) is never read, so it is not verified and not counted as a drop.
    /// </summary>
    [Fact]
    public void ASpentBudgetStopsVerifyingTheCandidatesBehindIt()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellHop) },
                                     walk: new RecordsTools.RecordsWalk { direction = "reverse", depth = 1, max_nodes = 1 });
        Served(r, "HcRecListHop");
        Assert.DoesNotContain("index candidate(s) were dropped", r);
    }

    /// <summary>One candidate on two frontiers is one drop: HcRecListDropped is named at hop 1 and again at hop 2,
    /// and the drop line counts records, not verification failures.</summary>
    [Fact]
    public void ACandidateNamedAtTwoHopsIsDroppedOnce()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.MgefHop) },
                                     walk: new RecordsTools.RecordsWalk { direction = "reverse", depth = 2 });
        Served(r, "1 index candidate(s) were dropped");
        Assert.DoesNotContain("HcRecListDropped", r);
    }

    /// <summary>A hop the node budget ended reads as cut, not as a hop that reached nothing, and a capped walk is
    /// never also called exhausted.</summary>
    [Fact]
    public void ACappedReverseWalkNamesTheCutHopAndIsNotCalledExhausted()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.MgefHop) },
                                     walk: new RecordsTools.RecordsWalk { direction = "reverse", depth = 6, max_nodes = 1 });
        Assert.Contains("cut by walk.max_nodes", r);
        Assert.DoesNotContain("nothing left to expand", r);
    }

    /// <summary>Two spellings of one seed key are one seed: the selection lists it once.</summary>
    [Fact]
    public void RepeatedSeedsAreDeduplicated()
    {
        var id = Fid(W.MgefHop);
        var r = RecordsTools.Records(Svc, formids: new[] { id, id },
                                     walk: new RecordsTools.RecordsWalk { direction = "reverse", depth = 1 });
        Served(r, "selection = 2 record(s) (1 referrer(s)");
    }
}

/// <summary>The index's own lifecycle: what a build costs, that a second call pays nothing, that a plugin whose
/// bytes changed rebuilds only its own slice, and that neither a snapshot swap nor a RESOLVER swap drops it. Each
/// test owns its world, because the accounting these assert on is "what THIS call did" and a shared world would
/// have been indexed by whichever test ran first.</summary>
[Trait("tier", "integration")]
public sealed class ReverseIndexLifecycleTests
{
    [Fact]
    public void TheFirstUnboundedCallReportsTheBuildAndItsPerPluginFreshnessKey()
    {
        using var w = new RecordsWorld();
        var r = RecordsTools.Records(w.Svc, references: new[] { RecordsWorld.Fid(w.MgefA) });
        Assert.Contains("reverse-reference index", r);
        Assert.Contains("key=", r);
        Assert.Contains("partition", r);
    }

    /// <summary>The build clause is only true of the call that paid it; the freshness key is true of every answer
    /// the index serves. A cached call that dropped the key would read as unindexed.</summary>
    [Fact]
    public void ACachedCallStillCarriesTheFreshnessKey()
    {
        using var w = new RecordsWorld();
        RecordsTools.Records(w.Svc, references: new[] { RecordsWorld.Fid(w.MgefA) });
        var second = RecordsTools.Records(w.Svc, references: new[] { RecordsWorld.Fid(w.MgefA) });
        Assert.Contains("reverse-reference index: unchanged", second);
        Assert.Contains("key=", second);
        Assert.DoesNotContain("built ", second);
    }

    [Fact]
    public void ANegatedUnboundedReferencesDeclaresThatItIsTheOrphanSweep()
    {
        using var w = new RecordsWorld();
        var r = RecordsTools.Records(w.Svc, references: new[] { "!" + RecordsWorld.Fid(w.MgefA) });
        Assert.Contains("ORPHAN sweep", r);
        Assert.Contains("nothing in the order references", r);
    }

    [Fact]
    public void TheIndexIsBuiltOnceAndASecondCallRebuildsNothing()
    {
        using var w = new RecordsWorld();
        var first = w.Svc.CaptureView().EnsureReverseIndex();
        Assert.True(first.Rebuilt > 0);
        Assert.True(first.Partitions > 1, "the world must hold more than one plugin for the partition claim to mean anything");
        var second = w.Svc.CaptureView().EnsureReverseIndex();
        Assert.Equal(0, second.Rebuilt);
        Assert.Equal(first.Partitions, second.Partitions);
        Assert.Equal(first.Key, second.Key);
    }

    [Fact]
    public void AChangedPluginRebuildsOnlyItsOwnPartition()
    {
        using var w = new RecordsWorld();
        var first = w.Svc.CaptureView().EnsureReverseIndex();
        File.SetLastWriteTimeUtc(w.OverrideFile, DateTime.UtcNow.AddHours(1));
        var after = w.Svc.CaptureView().EnsureReverseIndex();   // the capture rebuilds the SNAPSHOT; the index must not follow it wholesale
        Assert.Equal(1, after.Rebuilt);
        Assert.Equal(first.Partitions, after.Partitions);
        Assert.NotEqual(first.Key, after.Key);
    }

    /// <summary>A target FormKey is master-qualified and independent of the partition that carries the link, so a
    /// touched MASTER invalidates its own partition and nothing else — and the answers other partitions hold about
    /// its records still stand.</summary>
    [Fact]
    public void AMasterMtimeChangeRebuildsOnlyTheMasterAndKeepsEveryAnswerAboutIt()
    {
        using var w = new RecordsWorld();
        var before = RecordsTools.Records(w.Svc, references: new[] { RecordsWorld.Fid(w.MgefA) });
        Assert.Contains("HcRecSpellA", before);
        File.SetLastWriteTimeUtc(Path.Combine(w.ModsDir, "MasterMod", w.MasterName), DateTime.UtcNow.AddHours(1));
        var after = w.Svc.CaptureView().EnsureReverseIndex();
        Assert.Equal(1, after.Rebuilt);
        Assert.Contains("HcRecSpellA", RecordsTools.Records(w.Svc, references: new[] { RecordsWorld.Fid(w.MgefA) }));
    }

    /// <summary>The (path, mtime) key cannot see an edit that preserves the timestamp. That is the disclosed
    /// limit of the key, pinned here so the day the key changes shape something notices.</summary>
    [Fact]
    public void AnEditThatPreservesTheMtimeIsNotSeen()
    {
        using var w = new RecordsWorld();
        var first = w.Svc.CaptureView().EnsureReverseIndex();
        var stamp = File.GetLastWriteTimeUtc(w.OverrideFile);
        var bytes = File.ReadAllBytes(w.OverrideFile);
        File.WriteAllBytes(w.OverrideFile, bytes);
        File.SetLastWriteTimeUtc(w.OverrideFile, stamp);
        var after = w.Svc.CaptureView().EnsureReverseIndex();
        Assert.Equal(0, after.Rebuilt);
        Assert.Equal(first.Key, after.Key);
    }

    /// <summary>Ticking a plugin in MO2 replaces the RESOLVER, not just the snapshot. The index carries across, so
    /// the plugins whose files did not move are not re-walked — the whole point of the partition key.</summary>
    [Fact]
    public void ARemovedPluginDropsItsPartitionAndRebuildsNothingElse()
    {
        using var w = new RecordsWorld();
        var first = w.Svc.CaptureView().EnsureReverseIndex();
        Untick(w, w.MidName);
        var after = w.Svc.CaptureView().EnsureReverseIndex();
        Assert.Equal(first.Partitions - 1, after.Partitions);
        Assert.Equal(0, after.Rebuilt);
        Assert.Contains("HcRecSpellA", RecordsTools.Records(w.Svc, references: new[] { RecordsWorld.Fid(w.MgefA) }));
    }

    [Fact]
    public void AReorderedOrderRebuildsNothingAndStillAnswers()
    {
        using var w = new RecordsWorld();
        var first = w.Svc.CaptureView().EnsureReverseIndex();
        Order(w, w.MasterName, w.OverrideName, w.MidName);
        var after = w.Svc.CaptureView().EnsureReverseIndex();
        Assert.Equal(first.Partitions, after.Partitions);
        Assert.Equal(0, after.Rebuilt);
        Assert.Contains("HcRecSpellC", RecordsTools.Records(w.Svc, references: new[] { RecordsWorld.Fid(w.MgefA) }));
    }

    /// <summary>A plugin the walk cannot read contributes nothing, and the answer is short by whatever it
    /// references — said on EVERY answer, including the cached ones.</summary>
    [Fact]
    public void AnUnreadablePluginIsNamedOnEveryAnswer()
    {
        using var w = new RecordsWorld();
        RecordsTools.Records(w.Svc, references: new[] { RecordsWorld.Fid(w.MgefA) });
        File.WriteAllBytes(Path.Combine(w.ModsDir, "MidMod", w.MidName), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        var first = RecordsTools.Records(w.Svc, references: new[] { RecordsWorld.Fid(w.MgefA) });
        Assert.Contains("the answer is short", first);
        Assert.Contains(w.MidName, first);
        var second = RecordsTools.Records(w.Svc, references: new[] { RecordsWorld.Fid(w.MgefA) });
        Assert.Contains("the answer is short", second);
    }

    /// <summary>Two entries in the order can share a plugin FILENAME, so partitions are keyed on the PATH: a name
    /// key would overwrite one copy's edges and re-walk both plugins on every single call.</summary>
    [Fact]
    public void TwoCopiesOfOneFilenameEachKeepTheirOwnPartition()
    {
        using var w = new RecordsWorld();
        var copyDir = Path.Combine(w.ModsDir, "OldModCopy");
        Directory.CreateDirectory(copyDir);
        var copy = Path.Combine(copyDir, w.OldName);
        File.Copy(w.OldFile, copy);

        using var resolver = LoadOrderResolver.Build(new[]
        {
            Path.Combine(w.ModsDir, "MasterMod", w.MasterName), w.OldFile, copy,
        });
        var first = resolver.Capture().EnsureReverseIndex();
        Assert.Equal(3, first.Partitions);
        Assert.Equal(3, first.Rebuilt);
        var second = resolver.Capture().EnsureReverseIndex();
        Assert.Equal(0, second.Rebuilt);            // a filename key would rebuild the duplicate pair forever
        Assert.Equal(first.Key, second.Key);
    }

    /// <summary>Reads take no build lock, so a refresh must publish a whole generation rather than mutate the one
    /// being read: a torn read is either a thrown collection-modified or a silently short referencer set.</summary>
    [Fact]
    public void ConcurrentReadsAndRefreshesNeverTearTheIndex()
    {
        using var w = new RecordsWorld();
        var view = w.Svc.CaptureView();
        view.EnsureReverseIndex();
        var index = view.ReverseIndex!;
        var targets = new[] { w.MgefA };
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        var stop = false;

        var refresher = Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < 40; i++)
                {
                    File.SetLastWriteTimeUtc(w.OverrideFile, DateTime.UtcNow.AddMinutes(i + 1));
                    w.Svc.CaptureView().EnsureReverseIndex();
                }
            }
            catch (Exception ex) { failures.Add("refresh: " + ex); }
            finally { Volatile.Write(ref stop, true); }
        });
        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            try
            {
                while (!Volatile.Read(ref stop))
                {
                    if (index.ReferencersOf(targets).Count == 0) failures.Add("a read saw an empty index");
                    index.HasAnyReferencer(w.MgefA);
                }
            }
            catch (Exception ex) { failures.Add("read: " + ex); }
        })).ToArray();

        Task.WaitAll(readers.Append(refresher).ToArray());
        Assert.Empty(failures);
    }

    /// <summary>An unreadable plugin cuts both ways, and the two readings are opposites: the positive question
    /// loses referencers and is SHORT; the sweep loses the edges that would disqualify an orphan and is
    /// OVER-inclusive. Telling a sweep its answer is short says the orphans it listed are confirmed.</summary>
    [Fact]
    public void TheUnreadableDisclosureIsToldForTheSweepsDirection()
    {
        using var w = new RecordsWorld();
        RecordsTools.Records(w.Svc, references: new[] { RecordsWorld.Fid(w.MgefA) });
        File.WriteAllBytes(Path.Combine(w.ModsDir, "MidMod", w.MidName), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        var sweep = RecordsTools.Records(w.Svc, references: new[] { "!" + RecordsWorld.Fid(w.MgefA) });
        Assert.Contains(w.MidName, sweep);
        Assert.Contains("OVER-inclusive", sweep);
        Assert.DoesNotContain("the answer is short", sweep);
        var positive = RecordsTools.Records(w.Svc, references: new[] { RecordsWorld.Fid(w.MgefA) });
        Assert.Contains("the answer is short", positive);
        Assert.DoesNotContain("OVER-inclusive", positive);
    }

    /// <summary>The sweep takes ONE generation for its whole pass. Asked key by key, a refresh landing mid-pass
    /// would judge the early keys against the old edges and the late ones against the new, and the freshness key
    /// the response cites would name neither answer.</summary>
    [Fact]
    public void TheSweepJudgesEveryKeyAgainstOneGeneration()
    {
        using var w = new RecordsWorld();
        var masterPath = Path.Combine(w.ModsDir, "MasterMod", w.MasterName);
        using var resolver = LoadOrderResolver.Build(new[] { masterPath, Path.Combine(w.ModsDir, "MidMod", w.MidName) });
        var view = resolver.Capture();
        view.EnsureReverseIndex();
        var index = view.ReverseIndex!;
        Assert.True(index.HasAnyReferencer(w.MgefA), "two spells link it — the pre-swap generation says so");

        IEnumerable<FormKey> CandidatesThatRefreshMidway()
        {
            yield return w.Armor;
            // Every edge in the order lived in this plugin: the next generation knows of no referencer at all.
            File.WriteAllBytes(masterPath, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            resolver.Capture().EnsureReverseIndex();
            yield return w.MgefA;
        }

        var orphans = index.Orphans(CandidatesThatRefreshMidway());
        Assert.DoesNotContain(w.MgefA, orphans);
    }

    /// <summary>Two sweeps at once share one generation's memoised referenced-set rather than each building their
    /// own, and neither sees a torn one.</summary>
    [Fact]
    public void ConcurrentSweepsAgree()
    {
        using var w = new RecordsWorld();
        var view = w.Svc.CaptureView();
        view.EnsureReverseIndex();
        var index = view.ReverseIndex!;
        var keys = view.RecordKeys().ToList();
        var results = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => index.Orphans(keys).ToList())).ToArray();
        Task.WaitAll(results);
        foreach (var t in results) Assert.Equal(results[0].Result, t.Result);
    }

    /// <summary>Rewrite the profile so the named plugin is no longer active.</summary>
    static void Untick(RecordsWorld w, string plugin)
    {
        var names = new[] { w.MasterName, w.MidName, w.OverrideName }.Where(n => n != plugin).ToArray();
        Order(w, names);
    }

    /// <summary>Rewrite the profile's plugin list, in this order.</summary>
    static void Order(RecordsWorld w, params string[] names)
    {
        var prof = Path.Combine(w.Instance, "profiles", "Default");
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", names) + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), string.Join("\r\n", names.Select(n => "*" + n)) + "\r\n");
    }
}

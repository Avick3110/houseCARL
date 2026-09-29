using HousecarlCore;
using HousecarlMcp;
using Xunit;

using static HousecarlMcpTests.CheckMergeFixture;

namespace HousecarlMcpTests;

/// <summary>The merged check response's sections, accountings, roster, scope sentence and family grammar, rendered
/// over <see cref="CheckMergeFixture"/>. Each fact carries the <c>check-guard</c> probe arm it replaces.</summary>
[Trait("tier", "integration")]
public class CheckMergeRenderTests
{
    // FIXTURE: the one plugin carries BOTH families' findings (40 dangling refs, 40 record sections)
    [Fact]
    public void TheFixturePluginCarriesBothSweptFamiliesFindings()
    {
        Assert.Equal(Npcs, Errors.TotalDangling);
        Assert.Equal(Weapons, Scripts.Reports.Count);
    }

    // FIXTURE (dialogue): one seed owning 12 topics with 36 findings, plus 2 seeds that resolve to nothing
    [Fact]
    public void TheDialogueFixtureOwnsTwelveTopicsAndTwoUnreachableSeeds()
    {
        var d = Dialogue();
        Assert.Equal(Topics, d.TopicsFound);
        Assert.Equal(DialogueFindings, d.ProblemsFound);
        Assert.Equal(UnreachableSeeds, d.Unresolved.Count);
        Assert.Single(d.Resolved);
    }

    // SECTION-PER-FAMILY: one header, a section head per selected family in Registered order, a boundary for EACH
    [Fact]
    public void EachSelectedFamilyGetsOneSectionAndOneBoundaryInRegisteredOrder()
    {
        var text = Text(Both(), 0);
        Assert.Equal(1, Count(text, "\n[errors] load-order integrity sweep\n"));
        Assert.Equal(1, Count(text, "\n[scripts] VMAD script-property binding sweep\n"));
        Assert.True(text.IndexOf("[errors]", StringComparison.Ordinal) < text.IndexOf("[scripts]", StringComparison.Ordinal));
        Assert.Equal(1, Count(text, "boundary (errors): "));
        Assert.Equal(1, Count(text, "boundary (scripts): "));
        Assert.Equal(1, Count(text, CheckSentences.SweepMergedTitle));
    }

    // ACCOUNTING-PER-FAMILY: two accounting lines, each stating its own family's totals
    [Fact]
    public void EachFamilysAccountingStatesItsOwnTotals()
    {
        var text = Text(Both(), 0);
        Assert.Equal(2, Count(text, CheckSentences.SweepAccountingLead));
        Assert.Contains($"all {Npcs} dangling ref(s) found by this sweep appear above.", text);
        Assert.Contains($"all {Weapons} record section(s) found by this sweep appear above.", text);
    }

    // ACCOUNTING-PER-FAMILY (json): each family object carries its OWN accounting and its OWN boundary
    [Fact]
    public void EachJsonFamilyCarriesItsOwnAccountingAndBoundary()
    {
        var json = Json(Both(), 0);
        var errors = Family(json, "errors");
        var scripts = Family(json, "scripts");
        Assert.Equal(Npcs, Num(Obj(errors, "accounting"), "dangling_found"));
        Assert.Equal(Weapons, Num(Obj(scripts, "accounting"), "record_sections_with_findings"));
        var eb = Str(errors, "boundary");
        var sb = Str(scripts, "boundary");
        Assert.NotNull(eb);
        Assert.NotNull(sb);
        Assert.NotEqual(eb, sb);
    }

    // EXCLUDED-ROSTER-ONCE: each roster row appears once, under ONE head, however many families ran
    [Fact]
    public void TheExcludedRosterIsWrittenOnceUnderOneHead()
    {
        var text = Text(WithRoster(), 0);
        Assert.Equal(1, Count(text, "  HcCmBroken.esp: header could not be parsed\n"));
        Assert.Equal(1, Count(text, "  HcCmAlsoBroken.esp: header could not be parsed\n"));
        Assert.Equal(1, Count(text, "excluded plugins (could not be parsed"));
    }

    // EXCLUDED-ROSTER-ONCE-DECLARED: at every cap that cuts the roster, exactly ONE family's accounting states it
    [Fact]
    public void AtEveryCapThatCutsTheRosterOneAccountingStatesTheCut()
    {
        var sweep = WithRoster();
        bool sawCut = false;
        for (int cap = 300; cap <= 8000; cap += 20)
        {
            int stated = Count(Text(sweep, cap), " plugin(s) that could not be parsed are named above.");
            if (stated > 0) sawCut = true;
            Assert.True(stated <= 1, $"@{cap}: {stated} accountings state the roster cut");
        }
        Assert.True(sawCut, "no cap in 300..8000 cut the roster");
    }

    // EXCLUDED-ROSTER-CUT-IS-REPORTED-ONLY-WHEN-IT-HAPPENED: with room for everything no accounting claims a cut
    [Fact]
    public void AnUncappedResponseClaimsNoRosterCutInEitherTransport()
    {
        var text = Text(WithRoster(), 0);
        var json = Json(WithRoster(), 0);
        Assert.Equal(1, Count(text, "  HcCmBroken.esp: header could not be parsed\n"));
        Assert.DoesNotContain(" plugin(s) that could not be parsed are named above.", text);
        foreach (var family in new[] { "errors", "scripts" })
        {
            var acct = Obj(Family(json, family), "accounting");
            Assert.NotNull(acct);
            Assert.Equal(Num(acct, "excluded_plugins_total"), Num(acct, "excluded_plugins_named"));
            Assert.NotEqual(true, Bool(acct, "truncated"));
        }
    }

    // SCOPE-SENTENCE-DEFAULT: findings= omitted states it ran the default only and spells the findings= that adds
    [Fact]
    public void TheDefaultScopeSentenceNamesWhatDidNotRunAndHowToAddIt()
    {
        var text = Text(new CheckSweep(Sel(), Errors), 0);
        Assert.Contains("findings= was not given", text);
        Assert.Contains("did NOT run", text);
        Assert.Contains(SweepFamilySelection.Describe(SweepFamily.Scripts), text);
        Assert.Contains(SweepFamilySelection.Spelling(SweepFamily.Scripts), text);
    }

    // SCOPE-SENTENCE-DEFAULT (json): the SAME complete sentence, plus the same fact as data
    [Fact]
    public void TheJsonScopeSentenceIsTheTextLanesLineAndListsTheThreeFamiliesNotRun()
    {
        var sweep = new CheckSweep(Sel(), Errors);
        var text = Text(sweep, 0);
        var root = Root(Json(sweep, 0));
        Assert.Equal(FirstLineWith(text, "findings="), Str(root, "findings_scope"));
        Assert.Contains("did NOT run", Str(root, "findings_scope"));
        Assert.Equal(true, Bool(root, "findings_defaulted"));
        var notRun = Arr(root, "families_not_selected");
        Assert.Equal(new[] { "scripts", "dialogue", "facegen" },
                     Enumerable.Range(0, notRun?.GetArrayLength() ?? 0).Select(i => Str(At(notRun, i), "family")));
        Assert.Equal(SweepFamilySelection.Spelling(SweepFamily.Scripts), Str(At(notRun, 0), "findings"));
        var fams = Obj(root, "families");
        Assert.NotNull(fams);
        Assert.False(Has(fams, "scripts"));
        Assert.False(Has(fams, "dialogue"));
        Assert.False(Has(fams, "facegen"));
    }

    // SCOPE-SENTENCE-CHOSEN: a caller who NAMED the family is not told they omitted findings=
    [Fact]
    public void ANamedFamilyGetsTheChosenScopeSentence()
    {
        var text = Text(new CheckSweep(Sel("errors"), Errors), 0);
        Assert.Contains("findings= selected, and this response answers for:", text);
        Assert.DoesNotContain("findings= was not given", text);
        Assert.Contains(SweepFamilySelection.Spelling(SweepFamily.Scripts), text);
    }

    // SCOPE-SENTENCE-ALL: with every registered family run the sentence says so; two of four names the rest
    [Fact]
    public void EveryFamilyRunSaysSoAndTwoFamiliesNameTheRest()
    {
        var all = Text(All(), 0);
        var two = Text(Both(), 0);
        Assert.Contains("ran every findings family", all);
        Assert.DoesNotContain("did NOT run", all);
        Assert.DoesNotContain("ran every findings family", two);
        Assert.Contains("did NOT run", two);
    }

    // OFF-ORDER-NAMED-PER-FAMILY: a family that swept a file off-order names it in its OWN section, both transports
    [Fact]
    public void AnOffOrderFileIsNamedInsideTheFamilyThatSweptIt()
    {
        var sweep = new CheckSweep(Sel("errors", "scripts"), Errors,
                                   Scripts with { OffOrderScanned = new[] { "FreshPatch.esp" } });
        var text = Text(sweep, 0);
        int scriptsAt = text.IndexOf("[scripts] ", StringComparison.Ordinal);
        Assert.True(scriptsAt >= 0);
        Assert.True(text.IndexOf("swept OFF-ORDER (on disk, not in the active load order): FreshPatch.esp", scriptsAt,
                                 StringComparison.Ordinal) > scriptsAt);
        Assert.Contains("(indexed plugins only — off-order file content is outside the fingerprint)", text);
        var scripts = Family(Json(sweep, 0), "scripts");
        Assert.Contains("FreshPatch.esp", Strings(Arr(scripts, "off_order_scanned")));
        Assert.Equal(false, Bool(scripts, "epoch_covers_all_inputs"));
    }

    // OFF-ORDER-IS-EACH-FAMILY'S-OWN: one family's off-order file is not named in a sibling that never opened it
    [Fact]
    public void AnOffOrderFileIsNotBorrowedByTheSiblingFamily()
    {
        var sweep = new CheckSweep(Sel("errors", "scripts"),
                                   Errors with { OffOrderScanned = new[] { "FreshPatch.esp" } }, Scripts);
        var text = Text(sweep, 0);
        int scriptsAt = text.IndexOf("[scripts] ", StringComparison.Ordinal);
        Assert.True(scriptsAt >= 0);
        Assert.True(text.IndexOf("FreshPatch.esp", scriptsAt, StringComparison.Ordinal) < 0);
        Assert.Contains("swept OFF-ORDER (on disk, not in the active load order): FreshPatch.esp", text);
        var json = Json(sweep, 0);
        Assert.Empty(Strings(Arr(Family(json, "scripts"), "off_order_scanned")));
        Assert.True(Has(Family(json, "scripts"), "off_order_scanned"));
        Assert.Equal(true, Bool(Family(json, "scripts"), "epoch_covers_all_inputs"));
        Assert.Single(Strings(Arr(Family(json, "errors"), "off_order_scanned")));
    }

    // PLAN-LEAVES-EMPTY-SUBJECTS-OUT: a family whose subjects have no rows is not in the plan at all
    [Fact]
    public void AFamilyWithNoRowsIsLeftOutOfThePlan()
    {
        var plan = CheckOutcome.For(new CheckSweep(Sel("errors", "scripts"), Errors,
                                                   Scripts with { Reports = Array.Empty<RecordScriptFindings>() })).Plan();
        var only = Assert.Single(plan);
        Assert.Equal(SweepFamily.Errors, only.Family);
        Assert.Contains(SweepSubject.PluginSections, only.Subjects);
        Assert.Contains(SweepSubject.DanglingEntries, only.Subjects);
    }

    // CLASS-TOKEN-ROUND-TRIP: every class set the merged parser produces reads back through the family parsers
    [Theory]
    [InlineData(ErrorFindingClass.Dangling)]
    [InlineData(ErrorFindingClass.MissingMasters)]
    [InlineData(ErrorFindingClass.All)]
    public void ErrorClassTokensRoundTrip(ErrorFindingClass c)
    {
        Assert.True(SweepFindings.TryParseErrorClasses(SweepFindings.Tokens(c).ToList(), out var back, out var err), err);
        Assert.Equal(c, back);
    }

    // CLASS-TOKEN-ROUND-TRIP (scripts)
    [Theory]
    [InlineData(ScriptFindingClass.UnboundObject)]
    [InlineData(ScriptFindingClass.UnboundScalar)]
    [InlineData(ScriptFindingClass.BoundNull)]
    [InlineData(ScriptFindingClass.UnboundObject | ScriptFindingClass.UnboundScalar)]
    [InlineData(ScriptFindingClass.UnboundObject | ScriptFindingClass.BoundNull)]
    [InlineData(ScriptFindingClass.All)]
    public void ScriptClassTokensRoundTrip(ScriptFindingClass c)
    {
        Assert.True(SweepFindings.TryParseScriptClasses(SweepFindings.Tokens(c).ToList(), out var back, out var err), err);
        Assert.Equal(c, back);
    }

    // REGISTERED-IS-THE-MEMBERSHIP: every family the refusal OFFERS is one the parser ACCEPTS
    [Fact]
    public void EveryRegisteredFamilyIsOfferedAndAccepted()
    {
        foreach (var f in SweepFamilySelection.Registered)
        {
            string tok = SweepFamilySelection.Token(f);
            Assert.Contains("'" + tok + "'", SweepFamilySelection.Vocabulary);
            Assert.True(SweepFamilySelection.TryParse(new[] { tok }, out var sel, out var err), $"{tok}: {err}");
            Assert.Contains(f, sel!.Ran);
        }
    }

    // GROUNDS-ARE-ONE: distinct grounds render as sections and say no family answered; one shared ground collapses
    [Fact]
    public void DistinctRefusalGroundsStaySectionedAndOneSharedGroundCollapses()
    {
        var unseeded = DialogueRun(null, 1000);
        var distinct = new CheckSweep(Sel("errors", "dialogue"),
                                      ErrorCheckResult.Fail("errors-ground: exclude= removed every plugin this sweep would have covered."),
                                      null, unseeded);
        var text = Text(distinct, 0);
        Assert.Contains("errors-ground:", text);
        Assert.Contains("will NOT sweep the whole load order", text);
        Assert.False(text.StartsWith("error:", StringComparison.Ordinal));
        Assert.Contains("answered for NO family", text);

        var root = Root(Json(distinct, 0));
        var fams = Obj(root, "families");
        Assert.Contains("errors-ground:", Str(Obj(fams, "errors"), "refused"));
        Assert.True(Has(Obj(fams, "dialogue"), "refused"));
        Assert.Equal(0, Arr(root, "families_ran")?.GetArrayLength());
        Assert.Equal(2, Arr(root, "families_refused")?.GetArrayLength());

        var same = new CheckSweep(Sel("errors", "dialogue"), ErrorCheckResult.Fail(unseeded.Error!), null, unseeded);
        Assert.StartsWith("error:", Text(same, 0));
    }

    // GROUNDS-ARE-ONE-DOES-NOT-FIRE-WHILE-A-FAMILY-ANSWERED: two refusals on one ground beside an answer
    [Fact]
    public void OneSharedGroundDoesNotCollapseWhileAFamilyAnswered()
    {
        const string ground = "exclude= removed every plugin this sweep would have covered (1 in scope, all excluded)";
        var sweep = new CheckSweep(Sel("errors", "scripts", "dialogue"),
                                   ErrorCheckResult.Fail(ground), ScriptCheckResult.Fail(ground), Dialogue());
        var text = Text(sweep, 0);
        Assert.False(text.StartsWith("error:", StringComparison.Ordinal));
        Assert.Equal(2, Count(text, ground));
        Assert.DoesNotContain("answered for NO family", text);
        var root = Root(Json(sweep, 0));
        var fams = Obj(root, "families");
        Assert.True(Has(Obj(fams, "errors"), "refused"));
        Assert.True(Has(Obj(fams, "scripts"), "refused"));
        Assert.NotNull(Obj(fams, "dialogue"));
        Assert.False(Has(Obj(fams, "dialogue"), "refused"));
        Assert.Equal(1, Arr(root, "families_ran")?.GetArrayLength());
        Assert.Equal(2, Arr(root, "families_refused")?.GetArrayLength());
    }

    // NO-FAMILY-OBJECT-CARRIES-A-DUPLICATE-KEY: no object in any merged response names one key twice
    [Fact]
    public void NoMergedResponseObjectNamesAKeyTwice()
    {
        var budgeted = DialogueRun(new[] { "000001:A.esp", "000002:A.esp", "000003:A.esp", "000004:A.esp", "000005:A.esp" }, 2);
        var shapes = new (string, CheckSweep)[]
        {
            ("errors+scripts", Both()), ("all", All()),
            ("dialogue only", new CheckSweep(Sel("dialogue"), null, null, Dialogue())),
            ("dialogue counts_only", new CheckSweep(Sel("dialogue"), null, null, budgeted with { CountsOnly = true })),
            ("dialogue listing", new CheckSweep(Sel("dialogue"), null, null, budgeted)),
            ("mixed refusal", new CheckSweep(Sel("errors", "dialogue"), Errors, null, DialogueRun(null, 1000))),
            ("off-order", new CheckSweep(Sel("errors", "scripts"), Errors, Scripts with { OffOrderScanned = new[] { "FreshPatch.esp" } })),
        };
        var dupes = new List<string>();
        foreach (var (label, sweep) in shapes) CollectDuplicateKeys(Root(Json(sweep, 0)), label, dupes);
        Assert.Empty(dupes);
    }

    // ROSTER-STILL-ONE-WITH-THREE-FAMILIES: three families over a roster emit it once, owned by the errors family
    [Fact]
    public void WithThreeFamiliesTheRosterIsOwnedOnceAndDialogueOwnsNone()
    {
        var sweep = new CheckSweep(Sel("errors", "scripts", "dialogue"),
                                   Errors with { ExcludedPlugins = Roster }, Scripts with { ExcludedPlugins = Roster }, Dialogue());
        var text = Text(sweep, 0);
        Assert.Equal(SweepFamily.Errors, CheckOutcome.For(sweep).RosterOwner);
        Assert.Equal(1, Count(text, "excluded plugins (could not be parsed"));
        Assert.Equal(Roster.Count, Count(text, ": header could not be parsed\n"));
        Assert.True(Count(text, " plugin(s) that could not be parsed are named above.") <= 1);
        var dlgOnly = CheckOutcome.For(new CheckSweep(Sel("dialogue"), null, null, Dialogue()));
        Assert.Null(dlgOnly.RosterOwner);
        Assert.Empty(dlgOnly.ExcludedPlugins);
    }
}

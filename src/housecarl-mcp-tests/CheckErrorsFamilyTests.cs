using System.Linq;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using HousecarlCore;
using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.CheckErrorsFixtures;

namespace HousecarlMcpTests;

/// <summary>
/// The errors family's own 28 facts <c>CheckErrorsProbe.cs</c> asserted through the deleted 1.x single-family
/// renderer (<c>Wire.RenderCheckErrors</c> / <c>JsonWire.RenderCheckErrors</c>), re-asserted here against the
/// merged <c>Wire.RenderCheck</c> / <c>JsonWire.RenderCheck</c> a surviving tool (<c>housecarl_check</c>) actually
/// calls. Numbered per <c>dev/session-handoffs/render-halves-scratch/PHASE-1-record.md</c> §5's fact list, as
/// narrowed by <c>checkmerge-coverage-audit.md</c> (facts 21 and 29 are dropped — already covered on the merged
/// surface by <c>CheckMergeProbe</c>'s <c>CAP-LADDER</c> and <c>RESERVE-DECLARED-IS-RESERVE-DEMANDED</c> arms — and
/// fact 25 is narrowed to its uncovered MINIMALITY half). Rationale for the DTO-vs-live driving-lane split is in
/// <c>docs/architecture/check-family-tests.md</c>.
///
/// <para>Every PARTIAL the audit found stays on this list because the covered half belongs to another family
/// (scripts or dialogue) — each such test says so in its own comment.</para>
///
/// <para><b>Converted-from: CheckErrorsProbe</b> — the marker <c>HarnessResidueTests</c>' one-way-conversion guard
/// requires. <c>CheckErrorsProbe.cs</c> is deleted in the same commit that lands the last fact below.</para>
/// </summary>
[Trait("tier", "integration")]
public sealed class CheckErrorsFamilyTests : IClassFixture<CheckErrorsWorldFixture>
{
    readonly CheckErrorsWorld W;
    public CheckErrorsFamilyTests(CheckErrorsWorldFixture f) => W = f.W;

    LoadOrderService Svc => W.Svc;

    // ---- fact 1 -------------------------------------------------------------------------------------
    // An errors class the caller excluded renders as NOT CHECKED in text and null (never 0) in json.

    [Fact]
    public void Fact1_AnExcludedDanglingClassRendersNotCheckedInTextAndNullInJson()
    {
        var r = Svc.CheckErrors(null, 1000, findings: new[] { "missing_masters" });

        var text = Text(r, 20000);
        // Anchored on the surrounding bullet separators the head composes it between, so an insertion right
        // after the "· " that precedes this clause cannot hide behind a same-suffix match.
        Assert.Contains("· dangling refs NOT CHECKED (findings= excluded 'dangling') · ", text);
        Assert.DoesNotContain("0 dangling ref(s)", text);

        var fam = ErrorsFamily(Json(r, 20000));
        Assert.Equal(JsonValueKind.Null, fam.GetProperty("dangling").ValueKind);
    }

    [Fact]
    public void Fact1_AnExcludedMissingMastersClassRendersNotCheckedInTextAndNullInJson()
    {
        var r = Svc.CheckErrors(null, 1000, findings: new[] { "dangling" });

        var text = Text(r, 20000);
        Assert.Contains("missing masters NOT CHECKED (findings= excluded 'missing_masters')", text);

        var fam = ErrorsFamily(Json(r, 20000));
        Assert.Equal(JsonValueKind.Null, fam.GetProperty("missing_masters").ValueKind);
    }

    // ---- fact 2 -------------------------------------------------------------------------------------
    // The json head restates the text head's totals, and under counts_only carries the histogram OBJECT (not
    // merely the flat totals CheckMergeProbe's ACCOUNTING-PER-FAMILY already covers).

    [Fact]
    public void Fact2_JsonHeadRestatesTextTotals_AndCountsOnlyCarriesTheHistogramObjects()
    {
        var r = Svc.CheckErrors(null, 1000, findings: null, countsOnly: true);

        var text = Text(r, 20000);
        Assert.Contains($"{CheckErrorsWorld.TotalDangling} dangling ref(s)", text);

        var fam = ErrorsFamily(Json(r, 20000));
        Assert.Equal(CheckErrorsWorld.TotalDangling, fam.GetProperty("dangling").GetInt32());
        var byTarget = fam.GetProperty("dangling_by_target_plugin");
        Assert.True(byTarget.GetProperty("rows").GetArrayLength() > 0);
        var bySource = fam.GetProperty("dangling_by_source_plugin");
        Assert.True(bySource.GetProperty("rows").GetArrayLength() > 0);
    }

    // ---- fact 3 -------------------------------------------------------------------------------------
    // With the link walk skipped, both histogram axes are ABSENT in json (never present-with-null), and text says
    // the walk was not run — never "nothing to tally", which is what an axis that ran and found nothing says.

    [Fact]
    public void Fact3_LinkWalkSkipped_BothHistogramAxesAreAbsentInJson_TextSaysTheWalkWasNotRun()
    {
        var r = Svc.CheckErrors(null, 1000, findings: new[] { "missing_masters" }, countsOnly: true);

        var text = Text(r, 20000);
        Assert.Contains("no dangling histogram, by target or by source — the link walk was not run " +
                         "(findings= excluded 'dangling').", text);
        Assert.DoesNotContain("nothing to tally", text);

        var fam = ErrorsFamily(Json(r, 20000));
        Assert.False(fam.TryGetProperty("dangling_by_target_plugin", out _));
        Assert.False(fam.TryGetProperty("dangling_by_source_plugin", out _));
    }

    // ---- fact 4 -------------------------------------------------------------------------------------
    // counts_only builds no per-plugin listing, so the response makes no listing-completeness claim (no
    // "[accounting:" line where nothing else is short) and no by-source omission roster.

    [Fact]
    public void Fact4_CountsOnlyBuildsNoListing_NoCompletenessClaim_NoBySourceRoster()
    {
        var r = Svc.CheckErrors(null, 1000, findings: null, countsOnly: true);

        var text = Text(r, 20000);
        Assert.DoesNotContain("[ERROR]", text);
        Assert.DoesNotContain("[accounting:", text);   // nothing is short, and counts_only has no completeness claim
        Assert.DoesNotContain("Missing here, by source plugin", text);
    }

    // ---- fact 5 -------------------------------------------------------------------------------------
    // counts_only still names every unparseable plugin and its reason.

    [Fact]
    public void Fact5_CountsOnlyStillNamesUnparseablePluginsAndTheirReason()
    {
        var r = Svc.CheckErrors(null, 1000, findings: null, countsOnly: true);

        var text = Text(r, 20000);
        Assert.Contains("excluded plugins (could not be parsed — NOT checked):", text);
        Assert.Contains(W.BadName + ": could not be opened", text);
    }

    // ---- fact 6 -------------------------------------------------------------------------------------
    // The excluded-plugin roster's accounting is silent when every row is named, and states "N of M named above"
    // per transport when the response cuts rows.

    [Fact]
    public void Fact6_ExcludedRosterAccountingIsSilentWhenWhole_AndStatesNOfMWhenCut()
    {
        var r = Svc.CheckErrors(null, 1000, findings: null);

        var whole = Text(r, 20000);
        Assert.DoesNotContain("plugin(s) that could not be parsed are named above", whole);

        var cut = Text(r, 400);
        // Anchored on the PRECEDING clause's own full stop so a sabotage inserted right after the sentence's
        // leading space (before the "0") cannot hide behind a same-suffix match.
        Assert.Contains("plugin section(s) were rendered. 0 of 1 plugin(s) that could not be parsed are named " +
                         "above.", cut);
    }

    // ---- fact 7 -------------------------------------------------------------------------------------
    // The counts_only unread honesty tail states rows-named-of-total per transport, and json wraps it as
    // total/rows/rendered/truncated. Driven DTO-level: the live world has no per-record scan error to plant.

    [Fact]
    public void Fact7_CountsOnlyUnreadTail_TextStatesRowsNamedOfTotal_JsonWrapsTotalRowsRenderedTruncated()
    {
        var reports = new[]
        {
            new PluginErrors("HcUnread1.esp", Array.Empty<DanglingRef>(), Array.Empty<string>(),
                              2, new[] { "SomeRecord" }, "Mutagen threw parsing a record"),
        };
        var r = Result(reports: reports, scanned: 1, totalUnscannable: 2, countsOnly: true,
                       byTarget: Array.Empty<SweepCount>(), bySource: Array.Empty<SweepCount>());

        var text = Text(r, 20000);
        Assert.Contains("[UNREAD] HcUnread1.esp: Mutagen threw parsing a record 2 record(s) could not be scanned: " +
                         "SomeRecord", text);

        var fam = ErrorsFamily(Json(r, 20000));
        var unread = fam.GetProperty("unread");
        Assert.Equal(1, unread.GetProperty("total").GetInt32());
        Assert.Equal(1, unread.GetProperty("rendered").GetInt32());
        Assert.False(unread.GetProperty("truncated").GetBoolean());
        var row = Assert.Single(unread.GetProperty("rows").EnumerateArray());
        Assert.Equal("HcUnread1.esp", row.GetProperty("plugin").GetString());
        Assert.Equal(2, row.GetProperty("unscannable_records").GetInt32());
    }

    // ---- fact 8 -------------------------------------------------------------------------------------
    // A response cut only by limit= names the budget as the cause, states visible-of-found, and offers limit= and
    // NOT max_chars= — the errors family's own arm; CheckMergeProbe's DIALOGUE-SEED-BUDGET cluster covers this
    // shape for the dialogue family only (checkmerge-coverage-audit.md, fact 8).

    [Fact]
    public void Fact8_LimitOnlyCut_NamesTheBudget_StatesVisibleOfFound_OffersLimitNotMaxChars()
    {
        var r = Svc.CheckErrors(null, 1, findings: null);   // 1 of 6 admitted into Reports

        var text = Text(r, 20000);
        Assert.Contains("[accounting: 1 of the 6 dangling ref(s) found by this sweep appear above. 5 were never " +
                         "listed: the listing budget (limit=1) ran out.", text);
        Assert.Contains(" Raise limit= to list more.", text);
        Assert.DoesNotContain("Raise max_chars=", text);
    }

    // ---- fact 9 -------------------------------------------------------------------------------------
    // A response cut only by max_chars names the response cut, counts the entries actually emitted and the
    // sections rendered, and offers max_chars= and NOT limit=.

    [Fact]
    public void Fact9_MaxCharsOnlyCut_NamesTheResponseCut_CountsEmittedEntriesAndSections()
    {
        var r = Svc.CheckErrors(null, 1000, findings: null);

        var text = Text(r, 400);
        Assert.Contains("[accounting: 0 of the 6 dangling ref(s) found by this sweep appear above. 6 did not fit " +
                         "this response (max_chars=400). 0 of 3 plugin section(s) were rendered.", text);
        Assert.Empty(EntryLines(text));
        Assert.DoesNotContain("Raise limit=", text);
        Assert.Contains("Raise max_chars= to fit more of what was found.", text);
    }

    // ---- fact 10 ------------------------------------------------------------------------------------
    // With both cuts firing, each cause is stated and the two sum exactly to found minus visible.

    [Fact]
    public void Fact10_BothCutsFiring_EachCauseStated_AndTheTwoSumToFoundMinusVisible()
    {
        var r = Svc.CheckErrors(null, 3, findings: null);   // limit=3 of 6 admitted

        var text = Text(r, 900);
        Assert.Contains("3 were never listed: the listing budget (limit=3) ran out, 3 did not fit this response " +
                         "(max_chars=900).", text);
        // found (6) - visible (0) == byBudget (3) + byCut (3)
        Assert.Contains("0 of the 6 dangling ref(s) found by this sweep appear above.", text);
    }

    // ---- fact 11 --------------------------------------------------------------------------------------
    // An uncut, unbudgeted response states its completeness positively; a lane that lists nothing (counts_only)
    // makes no listing claim at all rather than an empty one.

    [Fact]
    public void Fact11_UncutResponseStatesCompletenessPositively_CountsOnlyMakesNoListingClaim()
    {
        var listing = Text(Svc.CheckErrors(null, 1000, findings: null), 20000);
        Assert.Contains($"[accounting: all {CheckErrorsWorld.TotalDangling} dangling ref(s) found by this sweep " +
                         "appear above.]", listing);

        var countsOnly = Text(Svc.CheckErrors(null, 1000, findings: null, countsOnly: true), 20000);
        Assert.DoesNotContain("[accounting:", countsOnly);
    }

    // ---- fact 12 --------------------------------------------------------------------------------------
    // The by-source omission roster is render-aware, is not capped by limit=, names a plugin whose whole set was
    // dropped, and is empty on a sweep that dropped nothing.

    [Fact]
    public void Fact12_BySourceRoster_NamesWholeDroppedPlugins_EmptyWhenNothingDropped()
    {
        var capped = Text(Svc.CheckErrors(null, 1, findings: null), 20000);
        // Anchored on the PRECEDING clause's own full stop, so a sabotage inserted right after the lead's leading
        // space cannot hide behind a same-suffix match.
        Assert.Contains("ran out. Missing here, by source plugin: " + W.BaseName + " (3), " + W.ModName +
                         " (1), " + W.PatchName + " (1).", capped);

        var uncapped = Text(Svc.CheckErrors(null, 1000, findings: null), 20000);
        Assert.DoesNotContain("Missing here, by source plugin", uncapped);
    }

    // ---- fact 13 --------------------------------------------------------------------------------------
    // The json accounting's numbers agree with its own document.

    [Fact]
    public void Fact13_JsonAccountingNumbersAgreeWithTheirOwnDocument()
    {
        var r = Svc.CheckErrors(null, 1000, findings: null);
        var fam = ErrorsFamily(Json(r, 400));

        int danglingVisible = fam.GetProperty("accounting").GetProperty("dangling_visible").GetInt32();
        int danglingEntriesInDoc = fam.TryGetProperty("plugins", out var plugins)
            ? plugins.EnumerateArray().Sum(p => p.GetProperty("dangling").GetArrayLength())
            : 0;
        Assert.Equal(danglingEntriesInDoc, danglingVisible);

        int sectionsRendered = fam.GetProperty("accounting").GetProperty("sections_rendered").GetInt32();
        int sectionsInDoc = fam.TryGetProperty("plugins", out var plugins2) ? plugins2.GetArrayLength() : 0;
        Assert.Equal(sectionsInDoc, sectionsRendered);
    }

    // ---- fact 14 / 15 ---------------------------------------------------------------------------------
    // The baseline split prints only where a base master was swept, names that subset, and gates the phase-order
    // sentence on stated facts; it rides the json as data, null for an unchecked class.

    [Fact]
    public void Fact14_BaselineLinePrintsOnlyWhereABaseMasterWasSwept_AndNamesThatSubset()
    {
        var swept = Text(Svc.CheckErrors(null, 1000, findings: null), 20000);
        // Extended past the plugin-name parenthesis into the very next clause, so a sabotage inserted right
        // after it cannot hide behind a same-prefix match.
        Assert.Contains("baseline: " + CheckErrorsWorld.BaselineDangling + " of " + CheckErrorsWorld.TotalDangling +
                         " dangling ref(s) come from the base-game master(s) this sweep covered (" + W.BaseName +
                         ") — vanilla leftovers rather than anything this load order introduced; " +
                         (CheckErrorsWorld.TotalDangling - CheckErrorsWorld.BaselineDangling) +
                         " come from the rest of the swept scope.", swept);

        var neverSwept = Text(Svc.CheckErrors(null, 1000, findings: null, exclude: new[] { W.BaseName }), 20000);
        Assert.DoesNotContain("baseline:", neverSwept);
    }

    [Fact]
    public void Fact14_PhaseOrderSentence_OnlyOnTheCappedSweepWithBaselineFindings()
    {
        // The phase-order sentence is about the LISTING BUDGET specifically (acct.OmittedByBudget), not about a
        // response cut by max_chars — a limit= cut is what crowds baseline findings behind the rest.
        var limitCapped = Text(Svc.CheckErrors(null, 1, findings: null), 20000);
        Assert.Contains("the listing budget (limit=) is spent on every other plugin BEFORE those", limitCapped);

        var uncapped = Text(Svc.CheckErrors(null, 1000, findings: null), 20000);
        Assert.DoesNotContain("spent on every other plugin BEFORE those", uncapped);
    }

    [Fact]
    public void Fact15_BaselineFieldsRideTheJson_NullForAnUncheckedClass()
    {
        var checkedFam = ErrorsFamily(Json(Svc.CheckErrors(null, 1000, findings: null), 20000));
        Assert.Equal(CheckErrorsWorld.BaselineDangling, checkedFam.GetProperty("baseline_dangling").GetInt32());
        Assert.Equal(new[] { W.BaseName }, checkedFam.GetProperty("base_masters_swept").EnumerateArray()
                                                      .Select(e => e.GetString()));

        var excludedFam = ErrorsFamily(Json(Svc.CheckErrors(null, 1000, findings: new[] { "missing_masters" }), 20000));
        Assert.Equal(JsonValueKind.Null, excludedFam.GetProperty("baseline_dangling").ValueKind);
        Assert.Equal(JsonValueKind.Null, excludedFam.GetProperty("non_baseline_dangling").ValueKind);
    }

    // ---- facts 16-19 ----------------------------------------------------------------------------------
    // Each counts_only histogram axis is titled, cut independently, and states its own missing-row count against
    // the knob that stopped it; an axis never drops silently; an axis's own framing is reserved out of max_chars.

    [Fact]
    public void Fact16_18_EachHistogramAxisIsTitled_AndStatesItsOwnMissingRowCount_InBothTransports()
    {
        var r = Svc.CheckErrors(null, 1000, findings: null, countsOnly: true);

        var text = Text(r, 900, histogramLimit: 1);
        Assert.Contains("dangling ref(s) by TARGET plugin (the plugin the broken refs point INTO) (2 distinct):", text);
        Assert.Contains("... [2 more row(s) — raise max_chars= to see them]", text);
        Assert.Contains("dangling ref(s) by SOURCE plugin (the plugin the broken refs come FROM) (3 distinct):", text);
        Assert.Contains("... [3 more row(s) — raise max_chars= to see them]", text);

        // Both axes are cut before their first row fits — the tight max_chars=900 cap is what stops them here,
        // not histogramLimit=1 (SweepEmission.HistogramCut: a row-limit break never sets cutByBudget, so a row
        // limit that actually bound would name "limit" — this cap binds first, and both axes report "max_chars").
        var fam = ErrorsFamily(Json(r, 900, histogramLimit: 1));
        Assert.Equal(0, fam.GetProperty("dangling_by_target_plugin").GetProperty("rendered").GetInt32());
        Assert.Equal("max_chars", fam.GetProperty("dangling_by_target_plugin").GetProperty("cut_by").GetString());
        Assert.Equal(0, fam.GetProperty("dangling_by_source_plugin").GetProperty("rendered").GetInt32());
        Assert.Equal("max_chars", fam.GetProperty("dangling_by_source_plugin").GetProperty("cut_by").GetString());

        // The OTHER knob: a cap wide enough that the row limit itself is what stops the axis names "limit"
        // instead — the two knobs move different things, and the disclosure has to name the one that fired.
        var wideCap = Text(r, 4000, histogramLimit: 1);
        Assert.Contains("... [1 more row(s) — raise limit= to see them]", wideCap);
        Assert.Contains("... [2 more row(s) — raise limit= to see them]", wideCap);
        Assert.DoesNotContain("raise max_chars= to see them", wideCap);
    }

    [Fact]
    public void Fact17_AnEmptyAxisStatesItself_AndAnEmptyFirstAxisDoesNotSilenceTheSecond()
    {
        var r = Result(countsOnly: true, byTarget: Array.Empty<SweepCount>(),
                       bySource: Array.Empty<SweepCount>());

        var text = Text(r, 20000);
        Assert.Contains("dangling ref(s) by TARGET plugin (the plugin the broken refs point INTO): nothing to " +
                         "tally — no findings in the swept scope.", text);
        Assert.Contains("dangling ref(s) by SOURCE plugin (the plugin the broken refs come FROM): nothing to " +
                         "tally — no findings in the swept scope.", text);
    }

    [Fact]
    public void Fact19_AxisFramingIsReservedNotBudgeted_AtACapAdmittingNoRowsEachAxisStillStatesItsCut()
    {
        var r = Svc.CheckErrors(null, 1000, findings: null, countsOnly: true);

        // A cap wide enough for the head but too tight for any histogram row: both axes still speak.
        var text = Text(r, 400, histogramLimit: 1000);
        Assert.Contains("dangling ref(s) by TARGET plugin (the plugin the broken refs point INTO) (2 distinct):", text);
        Assert.Contains("dangling ref(s) by SOURCE plugin (the plugin the broken refs come FROM) (3 distinct):", text);
    }

    [Fact]
    public void Fact20_TheCountsOnlyModeNoteIsPrintedOnce_AboveTheFirstAxisOnly()
    {
        var r = Svc.CheckErrors(null, 1000, findings: null, countsOnly: true);
        var text = Text(r, 20000);

        const string note = "counts_only=true — totals above are exact; no per-plugin listing was built.";
        int count = text.Split(new[] { note }, StringSplitOptions.None).Length - 1;
        Assert.Equal(1, count);
        var noteIndex = text.IndexOf(note, StringComparison.Ordinal);
        var firstAxisIndex = text.IndexOf("dangling ref(s) by TARGET plugin", StringComparison.Ordinal);
        Assert.True(noteIndex < firstAxisIndex);
    }

    // ---- fact 22 ----------------------------------------------------------------------------------------
    // The response's irreducible floor is content-independent: two results differing only in row width render to
    // the same length at a cap no body fits under, both transports.

    [Fact]
    public void Fact22_TheFloorIsContentIndependent_RowWidthDoesNotChangeItAtMaxChars1()
    {
        var narrow = new PluginErrors("HcA.esp",
            new[] { new DanglingRef(FormKey.Factory("000800:HcA.esp"), "Npc", "X", FormKey.Factory("0E0E0E:Skyrim.esm")) },
            Array.Empty<string>(), 0, Array.Empty<string>(), null);
        var wide = new PluginErrors("HcA.esp",
            new[] { new DanglingRef(FormKey.Factory("000800:HcA.esp"), "Npc",
                                    "AVeryMuchLongerEditorIdNameThanTheOtherOneByFar",
                                    FormKey.Factory("0E0E0E:Skyrim.esm")) },
            Array.Empty<string>(), 0, Array.Empty<string>(), null);

        var floorNarrow = Text(Result(reports: new[] { narrow }, scanned: 1, totalDangling: 1), 1);
        var floorWide = Text(Result(reports: new[] { wide }, scanned: 1, totalDangling: 1), 1);

        Assert.Equal(floorNarrow.Length, floorWide.Length);
    }

    // ---- fact 23 ----------------------------------------------------------------------------------------
    // Every text response longer than its cap names the FIXED PART as the cause, end to end across the searched
    // band, and a json document one character over says so too (both transports).

    [Fact]
    public void Fact23_EveryOverCapResponseNamesTheFixedPartAsTheCause_AcrossABand()
    {
        var r = Svc.CheckErrors(null, 1000, findings: null);
        foreach (var cap in new[] { 1, 100, 400, 900 })
        {
            var text = Text(r, cap);
            Assert.True(text.Length > cap, $"expected an overrun at cap={cap}");
            Assert.Contains("longer than the max_chars=" + cap + " it was given", text);
            Assert.Contains("raise it to at least ", text);
        }
    }

    // ---- fact 25 (narrowed to its uncovered MINIMALITY half; sufficiency is CAP-LADDER's) -----------------
    // The overrun remedy names the smallest cap that fits within the digit-width slack, and re-rendering at that
    // number clears the notice.

    [Fact]
    public void Fact25_TheRemedyNamesTheSmallestCapWithinTheDigitWidthSlack_AndClearsTheNoticeThere()
    {
        var r = Svc.CheckErrors(null, 1000, findings: null);
        var atOne = Text(r, 1);
        const string marker = "raise it to at least ";
        int idx = atOne.IndexOf(marker, StringComparison.Ordinal);
        var digits = new string(atOne.Substring(idx + marker.Length).TakeWhile(char.IsDigit).ToArray());
        int raiseTo = int.Parse(digits);

        // Sufficient: the notice clears exactly at the named cap.
        Assert.DoesNotContain("raise it to at least", Text(r, raiseTo));
        // Minimal within slack: two below the named cap, the notice is still present — the remedy is not
        // wildly larger than the true floor (measured true floor on this world: raiseTo - 1).
        Assert.Contains("raise it to at least", Text(r, raiseTo - 2));
    }

    // ---- fact 26 ------------------------------------------------------------------------------------------
    // A section is whole or absent: a rendered section carries its scan error, its missing-master line and its
    // unscannable count, and the only droppable units are a whole section or one entry. The missing-master half
    // is covered by CheckMasterRemedyTests; this proves the section stays WHOLE even when its entries got no
    // listing budget at all (checkmerge-coverage-audit.md, fact 26).

    [Fact]
    public void Fact26_ASectionIsWholeOrAbsent_ItsFixedPartRendersEvenWithZeroEntryBudget()
    {
        var text = Text(Svc.CheckErrors(null, 1, findings: null), 20000);   // limit=1 spends the whole budget on ModName

        Assert.Contains("[ERROR] " + W.PatchName, text);
        Assert.Contains("missing master(s) NOT installed anywhere in the MO2 install: " + W.GoneName, text);
        // The section rendered whole even though this plugin's OWN dangling entry got none of the budget.
        Assert.DoesNotContain(W.PatchName + "\n  dangling reference(s)", text);
    }

    // ---- fact 27 ------------------------------------------------------------------------------------------
    // The json in-band listing fields are written in both directions where there is a listing, and absent —
    // never 0 — where there is none.

    [Fact]
    public void Fact27_JsonInBandListingFields_PresentInListingMode_AbsentUnderCountsOnly()
    {
        var listingFam = ErrorsFamily(Json(Svc.CheckErrors(null, 1000, findings: new[] { "missing_masters" }), 20000));
        Assert.True(listingFam.TryGetProperty("plugins_with_findings", out _));
        Assert.True(listingFam.TryGetProperty("rendered", out _));
        Assert.True(listingFam.TryGetProperty("truncated", out _));

        var countsOnlyFam = ErrorsFamily(Json(Svc.CheckErrors(null, 1000, findings: new[] { "missing_masters" },
                                                              countsOnly: true), 20000));
        Assert.False(countsOnlyFam.TryGetProperty("plugins_with_findings", out _));
        Assert.False(countsOnlyFam.TryGetProperty("truncated", out _));
    }

    // ---- fact 28 ------------------------------------------------------------------------------------------
    // exclude= narrowing is stated in the response, and a fully-excluded base master leaves no baseline line.
    // The narrowing note itself is covered on the SCRIPTS family by ORCH-EXCLUDE-FILTER-NOTE-IS-STATED
    // (checkmerge-coverage-audit.md, fact 28); this is the errors-family half plus the baseline suppression,
    // neither of which that arm reaches.

    [Fact]
    public void Fact28_ExcludeNarrowingIsStated_AndAFullyExcludedBaseMasterLeavesNoBaselineLine()
    {
        var text = Text(Svc.CheckErrors(null, 1000, findings: null, exclude: new[] { W.BaseName }), 20000);

        Assert.Contains("NARROWED to exclude= left out 1 plugin(s).", text);
        Assert.DoesNotContain("baseline:", text);
    }

    // ---- fact 30 ------------------------------------------------------------------------------------------
    // The overrun sentence enumerates the closing lines the response cannot drop, and reads true in the lane
    // that owes none (only ONE of the two causes fired, not both).

    [Fact]
    public void Fact30_TheOverrunSentenceEnumeratesTheUndroppableLines_TrueEvenWhenOnlyOneCauseFired()
    {
        // Only a max_chars cut fires here (limit is ample) — the lane that "owes" nothing to the listing budget.
        var text = Text(Svc.CheckErrors(null, 1000, findings: null), 400);

        Assert.Contains("what it must carry whatever the budget — its header, the accounting above, the closing " +
                         "line for anything it cut short, the boundary — does not fit in that many chars", text);
        Assert.DoesNotContain("Raise limit=", text);
    }
}

using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.ScriptsFixtures;

namespace HousecarlMcpTests;

/// <summary>
/// The scripts family's own 13 facts <c>ScriptPropertyCheckProbe.cs</c> asserted through the deleted 1.x
/// single-family renderer (<c>Wire.RenderScriptCheck</c> / <c>JsonWire.RenderScriptCheck</c>), re-asserted here
/// against the merged <c>Wire.RenderCheck</c> / <c>JsonWire.RenderCheck</c> a surviving tool
/// (<c>housecarl_check</c>) actually calls. Numbered S1-S13 per
/// <c>dev/session-handoffs/render-halves-scratch/PHASE-1-record.md</c> §5's phase-3 fact list — the probe's 27
/// old <c>Check(...)</c> arms consolidate into these 13 facts, one test per fact rather than per old assertion.
///
/// <para>Facts drivable on the shared, frozen <see cref="ScriptsWorld"/> go through
/// <c>LoadOrderService.ValidateScripts</c> (the engine member <c>housecarl_check</c> calls); facts needing a scan
/// error, an excluded-plugin roster, or a cap-band the shared world cannot produce are driven DTO-level through
/// <see cref="ScriptsFixtures.Result"/>, the same split <c>CheckErrorsFamilyTests</c> uses. Every hand-shaped
/// value below was captured from the LIVE render (a scratch harness against the built assemblies, never
/// committed) rather than composed from memory of the source — the wording a merged accounting produces is not
/// always what the old single-family probe assumed.</para>
///
/// <para><b>Converted-from: ScriptPropertyCheckProbe</b> — the marker <c>HarnessResidueTests</c>' one-way-conversion
/// guard requires. <c>ScriptPropertyCheckProbe.cs</c> is deleted in the same commit that lands the last fact
/// below — the commit that ENDS the ADR-0003 rule-2 overlap <c>docs/architecture/test-project-fixtures.md</c>
/// declared (the scripts family briefly living in both the probe harness and this one).</para>
/// </summary>
[Collection("scripts")]
[Trait("tier", "integration")]
public sealed class ScriptsFamilyTests
{
    readonly ScriptsWorld W;
    public ScriptsFamilyTests(ScriptsFixture f) => W = f.W;

    LoadOrderService Svc => W.Svc;

    // ---- fact S1 --------------------------------------------------------------------------------------
    // An excluded finding class renders "NOT CHECKED" in text and null (never 0) in json — the partial case
    // (one class excluded) and the total case (both unbound classes excluded).

    [Fact]
    public void FactS1_APartiallyExcludedClassRendersNotCheckedInTextAndNullInJson()
    {
        var r = Svc.ValidateScripts(null, 1000, findings: new[] { "unbound_object" });

        var text = Wire.RenderCheck(new CheckSweep(Sel("scripts"), Scripts: r), 20000);
        Assert.Contains("(object only — unbound_scalar NOT CHECKED)", text);
        Assert.Contains("bound-but-null NOT CHECKED (findings= excluded 'bound_null')", text);
        Assert.DoesNotContain("0 bound-but-null", text);

        var fam = ScriptsFamily(JsonWire.RenderCheck(new CheckSweep(Sel("scripts"), Scripts: r), 20000));
        Assert.Equal(JsonValueKind.Null, fam.GetProperty("unbound_scalar").ValueKind);
        Assert.Equal(JsonValueKind.Null, fam.GetProperty("bound_but_null").ValueKind);
        Assert.Equal(r.TotalUnboundObject, fam.GetProperty("unbound_object").GetInt32());
    }

    [Fact]
    public void FactS1_BothUnboundClassesExcludedRendersNotCheckedInTextAndNullInJson()
    {
        var r = Svc.ValidateScripts(null, 1000, findings: new[] { "bound_null" });

        var text = Wire.RenderCheck(new CheckSweep(Sel("scripts"), Scripts: r), 20000);
        Assert.Contains("unbound NOT CHECKED (findings= excluded both unbound classes)", text);
        Assert.DoesNotContain("0 unbound", text);

        var fam = ScriptsFamily(JsonWire.RenderCheck(new CheckSweep(Sel("scripts"), Scripts: r), 20000));
        Assert.Equal(JsonValueKind.Null, fam.GetProperty("unbound").ValueKind);
        Assert.Equal(JsonValueKind.Null, fam.GetProperty("unbound_object").ValueKind);
        Assert.Equal(1, fam.GetProperty("bound_but_null").GetInt32());
    }

    // ---- fact S2 --------------------------------------------------------------------------------------
    // format=json parses and reports the same unbound total in listing and counts_only modes, and counts_only
    // carries the unbound_by_property histogram.

    [Fact]
    public void FactS2_JsonParityAcrossListingAndCountsOnly_AndCountsOnlyCarriesTheHistogram()
    {
        var listing = Svc.ValidateScripts(null, 1000);
        var counts = Svc.ValidateScripts(null, 1000, countsOnly: true);

        var listingFam = ScriptsFamily(JsonWire.RenderCheck(new CheckSweep(Sel("scripts"), Scripts: listing), 20000));
        var countsFam = ScriptsFamily(JsonWire.RenderCheck(new CheckSweep(Sel("scripts"), Scripts: counts), 20000));
        Assert.Equal(listing.TotalUnbound, listingFam.GetProperty("unbound").GetInt32());
        Assert.Equal(listing.TotalUnbound, countsFam.GetProperty("unbound").GetInt32());

        var histo = countsFam.GetProperty("unbound_by_property");
        Assert.True(histo.GetProperty("rows").GetArrayLength() > 0);
        Assert.False(listingFam.TryGetProperty("unbound_by_property", out _));
    }

    // ---- fact S3 --------------------------------------------------------------------------------------
    // counts_only + findings=[bound_null] leaves the histogram null, says "excluded both unbound classes, so
    // nothing was tallied", never "nothing to tally", and emits no unbound_by_property key.

    [Fact]
    public void FactS3_CountsOnlyWithBothUnboundClassesExcluded_NoTally()
    {
        var r = Svc.ValidateScripts(null, 1000, findings: new[] { "bound_null" }, countsOnly: true);

        var text = Wire.RenderCheck(new CheckSweep(Sel("scripts"), Scripts: r), 20000);
        Assert.Contains("no unbound histogram — findings= excluded both unbound classes, so nothing was tallied.", text);
        Assert.DoesNotContain("nothing to tally", text);

        var json = JsonWire.RenderCheck(new CheckSweep(Sel("scripts"), Scripts: r), 20000);
        Assert.False(ScriptsFamily(json).TryGetProperty("unbound_by_property", out _));
    }

    // ---- fact S4 --------------------------------------------------------------------------------------
    // The accounting's listing-budget clause restates class-aware totals — a small limit= never reintroduces the
    // "0 unbound" / "0 bound-but-null" the header dropped, in both directions.

    [Fact]
    public void FactS4_CappedTailRestatesClassAwareTotals_NeverReintroducesTheDroppedZero()
    {
        var cappedNull = Svc.ValidateScripts(null, 0, findings: new[] { "bound_null" });   // 1 bound-but-null, limit=0
        var nullText = Wire.RenderCheck(new CheckSweep(Sel("scripts"), Scripts: cappedNull), 20000);
        Assert.Contains(
            "True totals: unbound NOT CHECKED (findings= excluded both unbound classes) + 1 bound-but-null.",
            nullText);
        Assert.DoesNotContain("0 unbound", nullText);

        var cappedObj = Svc.ValidateScripts(null, 1, findings: new[] { "unbound_object" });   // 7 unbound, limit=1
        var objText = Wire.RenderCheck(new CheckSweep(Sel("scripts"), Scripts: cappedObj), 20000);
        Assert.Contains(
            "True totals: 7 unbound (object only — unbound_scalar NOT CHECKED) + bound-but-null NOT CHECKED (findings= excluded 'bound_null').",
            objText);
        Assert.DoesNotContain("0 bound-but-null", objText);
    }

    // ---- fact S5 --------------------------------------------------------------------------------------
    // With property_contains= in force, only the two counts it narrows carry the "matching '<x>'" label;
    // records-with-scripts and unverifiable stay unlabelled and plugin-wide.

    [Fact]
    public void FactS5_PropertyFilterLabelsOnlyTheTwoCountsItNarrows()
    {
        var full = Svc.ValidateScripts(null, 1000);
        var byProp = Svc.ValidateScripts(null, 1000, propertyContains: "myspell");

        var text = Wire.RenderCheck(new CheckSweep(Sel("scripts"), Scripts: byProp), 20000);
        Assert.Contains("2 unbound matching 'myspell' · 0 bound-but-null matching 'myspell'", text);
        Assert.DoesNotContain("record(s) with scripts matching", text);
        Assert.DoesNotContain("unverifiable matching", text);
        Assert.Equal(full.RecordsWithScripts, byProp.RecordsWithScripts);
        Assert.Equal(full.TotalUnverifiable, byProp.TotalUnverifiable);
    }

    // ---- fact S6 --------------------------------------------------------------------------------------
    // The property filter rides the json as data (property_contains), null when absent.

    [Fact]
    public void FactS6_PropertyFilterRidesTheJsonAsData_NullWhenAbsent()
    {
        var byProp = Svc.ValidateScripts(null, 1000, propertyContains: "myspell");
        var full = Svc.ValidateScripts(null, 1000);

        Assert.Equal("myspell",
            ScriptsFamily(JsonWire.RenderCheck(new CheckSweep(Sel("scripts"), Scripts: byProp), 20000))
                .GetProperty("property_contains").GetString());
        Assert.Equal(JsonValueKind.Null,
            ScriptsFamily(JsonWire.RenderCheck(new CheckSweep(Sel("scripts"), Scripts: full), 20000))
                .GetProperty("property_contains").ValueKind);
    }

    // ---- fact S7 --------------------------------------------------------------------------------------
    // counts_only text still NAMES every unparseable plugin and its reason, not just the header count. Driven
    // DTO-level: the live world has no per-plugin scan error to plant.

    [Fact]
    public void FactS7_CountsOnlyStillNamesUnparseablePluginsAndTheirReason()
    {
        var r = Result(countsOnly: true,
            excludedPlugins: new Dictionary<string, string> { ["HcSpBroken.esp"] = "header could not be parsed" });

        var text = Text(r, 0);
        Assert.Contains("excluded plugins (could not be parsed — NOT checked):", text);
        Assert.Contains("HcSpBroken.esp: header could not be parsed", text);
    }

    // ---- fact S8 --------------------------------------------------------------------------------------
    // The counts_only json honesty layer keeps its wrapper — {total, rows, rendered, truncated} — and the flags
    // equal the rows the response carries at every cap in a band.

    [Fact]
    public void FactS8_CountsOnlyJsonScanErrorsWrapped_FlagsEqualTheRowsAtEveryCap()
    {
        var scanned = Result(countsOnly: true, excludedPlugins: new Dictionary<string, string>
        {
            ["HcSpUnreadableA.esp"] = "header could not be parsed",
            ["HcSpUnreadableB.esp"] = "the record stream ended mid-record",
            ["HcSpUnreadableC.esp"] = "a subrecord length ran past the group",
        }) with
        {
            Reports = new[]
            {
                RecordScriptFindings.PluginScanError("HcSpUnreadableA.esp", "header could not be parsed"),
                RecordScriptFindings.PluginScanError("HcSpUnreadableB.esp", "the record stream ended mid-record"),
                RecordScriptFindings.PluginScanError("HcSpUnreadableC.esp", "a subrecord length ran past the group"),
            },
        };

        bool sawPartial = false, sawFull = false;
        foreach (var cap in new[] { 300, 3600, 3900, 4200 })
        {
            var doc = ScriptsFamily(Json(scanned, cap)).GetProperty("scan_errors");
            int total = doc.GetProperty("total").GetInt32();
            int rendered = doc.GetProperty("rendered").GetInt32();
            int rows = doc.GetProperty("rows").GetArrayLength();
            bool truncated = doc.GetProperty("truncated").GetBoolean();
            Assert.Equal(3, total);
            Assert.Equal(rendered, rows);
            Assert.Equal(rendered < total, truncated);
            if (rendered is > 0 and < 3) sawPartial = true;
            if (rendered == 3 && !truncated) sawFull = true;
        }
        Assert.True(sawPartial, "the band never showed a partial scan-error cut");
        Assert.True(sawFull, "the band never showed the uncapped case");
    }

    // ---- fact S9 --------------------------------------------------------------------------------------
    // At every cap the accounting's record-section count equals the sections the response carries, and a cut
    // response names a max_chars= remedy.

    [Fact]
    public void FactS9_RecordSectionsAccountedAtEveryCap_ACutResponseNamesTheRemedy()
    {
        var full = Svc.ValidateScripts(null, 1000);
        bool sawCut = false;
        foreach (var cap in new[] { 200, 1850, 2040, 2910 })
        {
            var t = Wire.RenderCheck(new CheckSweep(Sel("scripts"), Scripts: full), cap);
            int sections = t.Split('\n').Count(l => l.StartsWith("[UNBOUND] ", StringComparison.Ordinal)
                                                  || l.StartsWith("[CHECK] ", StringComparison.Ordinal));
            if (sections < full.Reports.Count)
            {
                sawCut = true;
                Assert.Contains($"{sections} of the {full.Reports.Count} record section(s) found by this sweep appear above.", t);
                Assert.Contains("Raise max_chars= to fit more of what was found.", t);
            }
            else
            {
                Assert.Contains($"all {full.Reports.Count} record section(s) found by this sweep appear above.", t);
            }
        }
        Assert.True(sawCut, "the cap band never cut the record listing");
    }

    // ---- fact S10 -------------------------------------------------------------------------------------
    // The counts_only histogram axis is in the response at every cap and states how many of its rows are
    // missing — the axis is never silently dropped (#392).

    [Fact]
    public void FactS10_HistogramAxisNeverDropsSilently_AcrossABand()
    {
        var counts = Svc.ValidateScripts(null, 1000, countsOnly: true);
        int distinct = counts.Histogram!.Count;
        bool sawCut = false;
        for (int cap = 300; cap <= 1400; cap += 20)
        {
            var t = Wire.RenderCheck(new CheckSweep(Sel("scripts"), Scripts: counts), cap);
            Assert.Contains("unbound properties by NAME", t);
            var m = System.Text.RegularExpressions.Regex.Match(t, @"\[(\d+) more row\(s\) — raise max_chars= to see them\]");
            if (m.Success) { sawCut = true; Assert.True(int.Parse(m.Groups[1].Value) <= distinct); }
        }
        Assert.True(sawCut, "the cap band never cut the histogram axis");
    }

    // ---- fact S11 -------------------------------------------------------------------------------------
    // When max_chars leaves no room for the excluded-plugin roster the accounting says what did not fit and how
    // many are unnamed; at an ample cap it names them and states no cut.

    [Fact]
    public void FactS11_RosterCutNamesItsSubject_AndAnAmpleCapNamesThemWithNoCutStated()
    {
        var excludedFat = Result(countsOnly: true, excludedPlugins:
            Enumerable.Range(0, 3).ToDictionary(i => $"HcSpBroken{i}.esp", i => new string('r', 300)));

        // Searched, not a fixed number: which cap admits the accounting but not a single roster row is a fact
        // about the fixture's own size, not a literal that survives an unrelated wording change elsewhere in
        // the same response. (The comment said this before; the code held a hardcoded 200 — pre-green review 1a,
        // finding 3. S12 below walks the band the same way for the PARTIAL split.)
        string? cut = null;
        for (int cap = 100; cap <= 3000 && cut is null; cap += 10)
        {
            var t = Text(excludedFat, cap);
            if (Enumerable.Range(0, 3).Any(i => t.Contains($"HcSpBroken{i}.esp: "))) continue;
            if (t.Contains("plugin(s) that could not be parsed are named above.")) cut = t;
        }
        Assert.True(cut is not null, "no cap in 100..3000 admitted the roster accounting while naming no row");
        Assert.Contains("0 of 3 plugin(s) that could not be parsed are named above.", cut!);
        Assert.Contains("Raise max_chars= to fit more of what was found.", cut!);

        var whole = Text(excludedFat, 0);
        Assert.Contains("HcSpBroken0.esp: ", whole);
        Assert.DoesNotContain("plugin(s) that could not be parsed are named above.", whole);
    }

    // ---- fact S12 -------------------------------------------------------------------------------------
    // At a partial roster cut the accounting's "N of M named" equals the rows actually emitted, in the
    // counts_only lane.

    [Fact]
    public void FactS12_PartialRosterCutCountsTheRowsItEmitted()
    {
        var excludedFat = Result(countsOnly: true, excludedPlugins:
            Enumerable.Range(0, 3).ToDictionary(i => $"HcSpBroken{i}.esp", i => new string('r', 300)));

        // Walk the band rather than pin one measured cap: the first partial split (neither 0 nor all 3 named)
        // is a fact about the fixture's own size, and pinning the literal cap that produces it would make this
        // arm fragile to any unrelated wording change elsewhere in the same response (measured: a sabotage of
        // an unrelated sentence shifted this fixture's split point by ~10 chars).
        bool sawPartial = false;
        for (int cap = 200; cap <= 3000 && !sawPartial; cap += 10)
        {
            var t = Text(excludedFat, cap);
            int named = Enumerable.Range(0, 3).Count(i => t.Contains($"HcSpBroken{i}.esp: "));
            if (named == 0 || named == 3) continue;
            sawPartial = true;
            Assert.Contains($"{named} of 3 plugin(s) that could not be parsed are named above.", t);
        }
        Assert.True(sawPartial, "no cap in 200..3000 named SOME of the roster and cut the rest");
    }

    // ---- fact S13 -------------------------------------------------------------------------------------
    // The response never exceeds max_chars at any integer cap 1..12000, in three lanes and both transports,
    // bounded by the fixture's own irreducible floor, with the json always parseable.

    [Fact]
    public void FactS13_ResponseNeverExceedsMaxChars_AcrossTheFullCapBand()
    {
        var listing = Svc.ValidateScripts(null, 1000);
        var counts = Svc.ValidateScripts(null, 1000, countsOnly: true);
        var withExcluded = listing with
        {
            ExcludedPlugins = Enumerable.Range(0, 3).ToDictionary(i => $"HcSpBroken{i}.esp", i => new string('r', 300)),
        };

        var bad = new List<string>();
        foreach (var fixture in new[] { listing, counts, withExcluded })
        {
            var sweep = new CheckSweep(Sel("scripts"), Scripts: fixture);
            int textFloor = Wire.RenderCheck(sweep, 1).Length;
            int jsonFloor = JsonWire.RenderCheck(sweep, 1).Length;
            foreach (int cap in Enumerable.Range(1, 12000).Append(40000))
            {
                var t = Wire.RenderCheck(sweep, cap);
                var j = JsonWire.RenderCheck(sweep, cap);
                int slack = 8 * cap.ToString().Length;
                if (t.Length > Math.Max(cap, textFloor + slack))
                    bad.Add($"text@{cap}={t.Length} over {Math.Max(cap, textFloor + slack)} (floor {textFloor})");
                if (j.Length > Math.Max(cap, jsonFloor + slack))
                    bad.Add($"json@{cap}={j.Length} over {Math.Max(cap, jsonFloor + slack)} (floor {jsonFloor})");
                try { JsonDocument.Parse(j); }
                catch (Exception ex) { bad.Add($"json@{cap} not valid: {ex.GetType().Name}"); }
                if (bad.Count > 8) break;
            }
            if (bad.Count > 8) break;
        }
        Assert.True(bad.Count == 0, string.Join("; ", bad.Take(8)));
    }
}

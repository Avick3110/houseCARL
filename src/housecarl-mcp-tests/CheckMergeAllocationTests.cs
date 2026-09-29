using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

using static HousecarlMcpTests.CheckMergeFixture;

namespace HousecarlMcpTests;

/// <summary>How the merged check response spends its <c>max_chars</c> budget across families: the water-fill's
/// properties (monotone, no stranding, allocation equals spend), the reserves, the cap ladder and the overrun
/// remedy, over <see cref="CheckMergeFixture"/>. Each fact carries the <c>check-guard</c> probe arm it replaces.</summary>
[Trait("tier", "integration")]
public class CheckMergeAllocationTests
{
    delegate string Render(CheckSweep s, int cap, out BoundedBody? body);

    static readonly (string Name, Render Render)[] Lanes =
    {
        ("text", (CheckSweep s, int cap, out BoundedBody? b) => CheckTextRender.RenderCheck(s, cap, 1000, out b)),
        ("json", (CheckSweep s, int cap, out BoundedBody? b) => JsonWire.RenderCheck(s, cap, 1000, out b)),
    };

    static SweepSubject[] Planned(CheckSweep s) => CheckOutcome.For(s).Plan().SelectMany(p => p.Subjects).Distinct().ToArray();

    // ALLOCATION-SECOND-FAMILY-DOES-NOT-WAIT-ITS-TURN (#394): scripts renders far below what a serial walk needs
    [Fact]
    public void TheSecondFamilyRendersBeforeTheFirstIsWhole()
    {
        var both = Both();
        int floor = Text(both, 1).Length;
        var errorsOnly = new CheckSweep(Sel("errors"), Errors);
        int errorsWholeBody = Text(errorsOnly, 0).Length - Text(errorsOnly, 1).Length;
        int first = -1;
        for (int cap = floor; cap <= 20000; cap += 20)
        {
            var t = Text(both, cap);
            int dangling = StatedPair(t, " dangling ref(s) found by this sweep appear above.");
            int recs = StatedPair(t, " record section(s) found by this sweep appear above.");
            Assert.True(dangling >= 0 && recs >= 0, $"@{cap}: an accounting states no count");
            if (recs > 0 && first < 0) first = cap;
            Assert.False(first >= 0 && recs == 0, $"@{cap}: scripts rendered nothing at a cap wider than {first}");
        }
        Assert.True(first >= 0, "the scripts family never rendered up to 20000");
        Assert.True(first < floor + errorsWholeBody, $"first scripts section at {first}; a serial walk needed {floor + errorsWholeBody}");
    }

    // ALLOCATION-MONOTONE-IN-MAX-CHARS (#394 pin 3(i)): no subject spends fewer characters at a wider cap
    [Fact]
    public void NoSubjectSpendsLessAtAWiderCap()
    {
        var all = All();
        var subjects = Planned(all);
        foreach (var (lane, render) in Lanes)
        {
            var previous = new Dictionary<SweepSubject, (int Cap, int Spent)>();
            for (int cap = 1; cap <= 9000; cap++)
            {
                render(all, cap, out var body);
                Assert.True(body is not null, $"{lane}@{cap}: the render built no allocation");
                foreach (var subject in subjects)
                {
                    int spent = body!.SpentOn(subject);
                    if (previous.TryGetValue(subject, out var was))
                        Assert.True(spent >= was.Spent, $"{lane} {subject}: {spent} at cap {cap}, {was.Spent} at {was.Cap}");
                    previous[subject] = (cap, spent);
                }
            }
        }
    }

    // ALLOCATION-NO-STRANDING (#394 pin 3(ii)): a call whose whole demand fits renders every unit and claims no cut
    [Fact]
    public void AMergedCallThatFitsRendersEverythingAndClaimsNoCut()
    {
        const int Default = 80000, TextSlack = 1500, JsonSlack = 1500;
        var s = Both();
        string uncapped = Text(s, 0);
        string capped = CheckTextRender.RenderCheck(s, Default, 1000, out var body);
        Assert.True(uncapped.Length < Default, "the fixture no longer fits the default");
        foreach (var unit in new[] { "[ERROR] ", "[UNBOUND] ", "   [target not defined by any active plugin]" })
            Assert.Equal(Count(uncapped, unit), Count(capped, unit));
        foreach (var claim in new[] { "did not fit this response", "were rendered.", "Raise max_chars=" })
            Assert.DoesNotContain(claim, capped);
        Assert.NotNull(body);
        foreach (var subject in Planned(s))
            Assert.Equal(body!.AllocationOf(subject), body.SpentOn(subject));

        var jsonWhole = Root(Json(s, 0));
        var jsonCapped = Root(Json(s, Default));
        foreach (var (family, array) in new[] { ("errors", "plugins"), ("scripts", "records"), ("dialogue", "seeds") })
            Assert.Equal(ArrayLength(jsonWhole, family, array), ArrayLength(jsonCapped, family, array));
        foreach (var family in new[] { "errors", "scripts", "dialogue" })
            Assert.NotEqual(true, Bool(Obj(Obj(Obj(jsonCapped, "families"), family), "accounting"), "truncated"));

        int textTight = SmallestWholeCap(c => TextUnits(Text(s, c)), TextUnits(uncapped), uncapped.Length);
        string jsonUncapped = Json(s, 0);
        int jsonTight = SmallestWholeCap(c => JsonUnits(Json(s, c)), JsonUnits(jsonUncapped), jsonUncapped.Length);
        Assert.InRange(textTight - uncapped.Length, int.MinValue, TextSlack);
        Assert.True(textTight >= 0);
        Assert.InRange(jsonTight - jsonUncapped.Length, int.MinValue, JsonSlack);
        Assert.True(jsonTight >= 0);
    }

    static int SmallestWholeCap(Func<int, string> unitsAt, string whole, int from)
    {
        int lo = Math.Max(1, from), hi = Math.Max(lo + 1, from * 3);
        if (unitsAt(hi) != whole) return -1;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (unitsAt(mid) == whole) hi = mid; else lo = mid + 1;
        }
        return lo;
    }

    static string TextUnits(string t)
        => $"{Count(t, "[ERROR] ")}/{Count(t, "[UNBOUND] ")}/{Count(t, "   [target not defined by any active plugin]")}/{Count(t, "\nseed ")}/{Count(t, "  topic ")}";

    static string JsonUnits(string j)
    {
        var root = Root(j);
        return $"{ArrayLength(root, "errors", "plugins")}/{ArrayLength(root, "scripts", "records")}/{ArrayLength(root, "dialogue", "seeds")}";
    }

    // ALLOCATION-EQUALS-SPEND (#394 pin 3(iv)): with nothing cut every subject spends exactly what it was allocated
    [Fact]
    public void WithNothingCutEverySubjectSpendsExactlyItsAllocation()
    {
        var all = All();
        foreach (var (lane, render) in Lanes)
        {
            render(all, 4000000, out var body);
            Assert.NotNull(body);
            foreach (var subject in Planned(all))
            {
                int spent = body!.SpentOn(subject);
                Assert.True(spent > 0, $"{lane} {subject}: spent nothing");
                Assert.Equal(body.AllocationOf(subject), spent);
            }
        }
    }

    // RESERVE-COVERS-WHAT-IT-RESERVES-FOR: what each response writes through its reserve fits that reserve
    [Fact]
    public void WhatARenderWritesThroughItsReserveFitsTheReserve()
    {
        var budgeted = DialogueRun(new[] { "000001:A.esp", "000002:A.esp", "000003:A.esp", "000004:A.esp", "000005:A.esp" }, 2);
        var shapes = new (string, CheckSweep)[]
        {
            ("three families", All()),
            ("three families + roster", new CheckSweep(Sel("errors", "scripts", "dialogue"),
                Errors with { ExcludedPlugins = Roster }, Scripts with { ExcludedPlugins = Roster }, Dialogue())),
            ("dialogue, seed budget cut", new CheckSweep(Sel("dialogue"), null, null, budgeted)),
            ("dialogue, counts_only", new CheckSweep(Sel("dialogue"), null, null, budgeted with { CountsOnly = true })),
            ("errors + refused dialogue", new CheckSweep(Sel("errors", "dialogue"), Errors, null, DialogueRun(null, 1000))),
        };
        foreach (var (label, sweep) in shapes)
        {
            var oc = CheckOutcome.For(sweep);
            var accts = oc.Accountings(Wire.DefaultMaxChars);
            int jsonReserve = accts.Sum(a => a.JsonAccountingReserve);
            JsonWire.RenderCheck(sweep, 0, 1000, out var jb);
            Assert.True(jb is null || jb.ReservedWritten <= jsonReserve, $"json/{label}: wrote {jb?.ReservedWritten} through {jsonReserve}");
            int textReserve = accts.Sum(a => a.TextAccountingReserve + a.Boundary.Length + CheckTextRender.BoundaryWrap)
                            + oc.Sections.Sum(f => string.Format(CheckSentences.SweepBoundaryLabelFor, SweepFamilySelection.Token(f)).Length);
            CheckTextRender.RenderCheck(sweep, 0, 1000, out var tb);
            Assert.True(tb is null || tb.ReservedWritten <= textReserve, $"text/{label}: wrote {tb?.ReservedWritten} through {textReserve}");
        }
    }

    // RESERVE-DECLARED-IS-RESERVE-DEMANDED: the demand pass's reserve is exactly the render's, including absent axes
    [Fact]
    public void TheDemandedReserveEqualsTheDeclaredReserve()
    {
        var noAxes = Errors with { CountsOnly = true, Histogram = null, DanglingBySource = null };
        var targetOnly = Errors with { CountsOnly = true, Histogram = new[] { new SweepCount("HcCmGhost.esm", 40) }, DanglingBySource = null };
        var bothAxes = targetOnly with { DanglingBySource = new[] { new SweepCount("HcCm.esp", 33) } };
        var scNoAxis = Scripts with { CountsOnly = true, Histogram = null };
        var scAxis = Scripts with { CountsOnly = true, Histogram = new[] { new SweepCount("HcCmSpell", 40) } };
        var shapes = new (string, CheckSweep)[]
        {
            ("errors counts_only, both axes absent", new CheckSweep(Sel("errors"), noAxes, null)),
            ("errors counts_only, by-source absent", new CheckSweep(Sel("errors"), targetOnly, null)),
            ("errors counts_only, both axes present", new CheckSweep(Sel("errors"), bothAxes, null)),
            ("scripts counts_only, axis absent", new CheckSweep(Sel("scripts"), null, scNoAxis)),
            ("scripts counts_only, axis present", new CheckSweep(Sel("scripts"), null, scAxis)),
            ("both counts_only, three axes absent", new CheckSweep(Sel("errors", "scripts"), noAxes, scNoAxis)),
            ("both counts_only, every axis present", new CheckSweep(Sel("errors", "scripts"), bothAxes, scAxis)),
            ("three families, listing", All()),
        };
        foreach (var (label, sweep) in shapes)
            foreach (var (lane, render) in Lanes)
            {
                render(sweep, 0, out var b);
                Assert.True(b is null || b.ReserveDeclared == b.ReserveDemanded,
                            $"{lane}/{label}: demanded {b?.ReserveDemanded}, declared {b?.ReserveDeclared}");
            }
    }

    // CAP-LADDER: at one cap in every three (the offset rotates, so each case asks all three residues) neither
    // transport returns more than it was given, bar the floor, and the json parses
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoCapReturnsMoreThanItWasGivenBarTheFloor(bool allFamilies)
    {
        var s = allFamilies ? All() : Both();
        int textFloor = Text(s, 1).Length;
        int jsonFloor = Json(s, 1).Length;
        // One cap in each block of three, the offset rotating per block and per case; the probe swept every integer.
        int shift = allFamilies ? 1 : 0;
        foreach (int cap in Enumerable.Range(0, 4000).Select(i => 1 + 3 * i + (i + shift) % 3).Append(40000))
        {
            var text = Text(s, cap);
            var json = Json(s, cap);
            int slack = 8 * cap.ToString().Length;
            Assert.True(text.Length <= Math.Max(cap, textFloor + slack), $"text@{cap}={text.Length} (floor {textFloor})");
            Assert.True(json.Length <= Math.Max(cap, jsonFloor + slack), $"json@{cap}={json.Length} (floor {jsonFloor})");
            Assert.Contains(CheckSentences.SweepMergedTitle, text);
            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.TryGetProperty("families", out _), $"json@{cap} is not a merged document");
        }
    }

    // REMEDY-SURVIVES-A-POWER-OF-TEN: the cap the overrun notice names covers what raising to it adds back
    [Theory]
    [InlineData(5, 9995, 3)]
    [InlineData(5, 9995, 1)]
    [InlineData(2000, 5361, 3)]
    [InlineData(7, 5361, 3)]
    public void TheRemedyCoversTheDigitsRaisingToItAdds(int cap, int floorLen, int sites)
    {
        var notice = new CheckAccounting(Errors, cap).CapTooSmall(floorLen, floorLen, 0, sites);
        Assert.NotNull(notice);
        const string marker = "raise max_chars to at least ";
        int at = notice!.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, notice);
        int end = notice.IndexOf('.', at);
        Assert.True(int.TryParse(notice[(at + marker.Length)..end], out var raiseTo), notice);
        Assert.True(raiseTo >= floorLen + sites * (raiseTo.ToString().Length - cap.ToString().Length), $"names {raiseTo}");
    }
}

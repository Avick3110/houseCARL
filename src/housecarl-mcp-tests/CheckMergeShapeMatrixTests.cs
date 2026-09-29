using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

using static HousecarlMcpTests.CheckMergeFixture;

namespace HousecarlMcpTests;

/// <summary>The allocation, cap and remedy properties of the merged check response, asked of every shape it can
/// produce: each family subset in the listing and counts_only lanes, with a roster, a refused family, two seeds and
/// an off-order file, in both transports, at every integer cap from 1 to past the whole answer. Moved from the
/// generator's <c>CheckShapeMatrix</c> (run inside the <c>check-guard</c> probe); the sweep runs once and each fact
/// asserts its own property off it.</summary>
[Trait("tier", "integration")]
public class CheckMergeShapeMatrixTests
{
    // MATRIX-INSIDE-ITS-CAP: no response is longer than its cap, bar the floor
    [Fact]
    public void EveryShapeStaysInsideItsCapBarTheFloor() => Assert.Empty(CheckMergeShapes.Swept.OverCap);

    // MATRIX-MONOTONE-IN-MAX-CHARS: no subject of any shape spends fewer characters at a wider cap
    [Fact]
    public void NoShapesSubjectSpendsLessAtAWiderCap() => Assert.Empty(CheckMergeShapes.Swept.NonMonotone);

    // MATRIX-ONE-BUDGET: units never spend more than the row budget; the response never owes more than was measured
    [Fact]
    public void TheAllocationAndTheEmissionTestAreOneBudget() => Assert.Empty(CheckMergeShapes.Swept.OneBudget);

    // MATRIX-JSON-PARSES-AT-EVERY-CAP
    [Fact]
    public void EveryJsonRenderParses() => Assert.Empty(CheckMergeShapes.Swept.Unparseable);

    // MATRIX-HONESTY-LAYERS-AGREE: `unread` and `scan_errors` are one {total, rows, rendered, truncated} shape
    [Fact]
    public void TheTwoHonestyLayersAreOneShape()
    {
        Assert.Empty(CheckMergeShapes.Swept.HonestyBad);
        Assert.True(CheckMergeShapes.Swept.HonestyCells > 0, "no shape carried an honesty layer");
    }

    // MATRIX-REMEDY-CLEARS-IN-ONE-STEP: the notice states the response's own length and its cap clears it in one step
    [Fact]
    public void TheOverrunRemedyClearsInOneStep()
    {
        Assert.Empty(CheckMergeShapes.Swept.RemedyBad);
        Assert.True(CheckMergeShapes.Swept.NoticesFollowed > 0, "the notice never fired anywhere in the matrix");
    }

    // MATRIX-EVERY-PLANNED-SUBJECT-IS-GOVERNED: every planned subject gets room somewhere, the five counts_only ones too
    [Fact]
    public void EveryPlannedSubjectIsGivenRoomSomewhereInItsBand()
    {
        Assert.Empty(CheckMergeShapes.Swept.NeverRendered);
        foreach (var subject in new[] { SweepSubject.HistogramByTarget, SweepSubject.HistogramBySource,
                                        SweepSubject.HistogramByProperty, SweepSubject.UnreadRows, SweepSubject.ScriptScanRows })
            Assert.Contains(subject, CheckMergeShapes.Swept.EverRendered);
    }

    // MATRIX-EVERY-FINGERPRINT-TERM-IS-REACHED: every fingerprint term is non-zero on at least one shape
    [Fact]
    public void EveryFingerprintTermIsReachedBySomeShape()
    {
        foreach (var (lane, count) in CheckMergeShapes.Swept.TermCount)
            for (int i = 0; i < count; i++)
                Assert.True(CheckMergeShapes.Swept.TermsReached.Contains((lane, i)), $"{lane} fingerprint term {i} is 0 on every shape");
    }

    // MATRIX-NO-STRANDING: a shape that fits renders every unit, claims no cut, and its tight fit is bounded
    [Fact]
    public void NoShapeStrandsItsBudget()
    {
        foreach (var shape in CheckMergeShapes.Shapes)
            foreach (var lane in CheckMergeShapes.Lanes)
            {
                string whole = lane.Render(shape.Sweep, 0, out _);
                string wide = lane.Render(shape.Sweep, whole.Length * 3 + 4000, out var body);
                Assert.True(CheckMergeShapes.Fingerprint(lane, wide) == CheckMergeShapes.Fingerprint(lane, whole), $"{shape.Name} [{lane.Name}] wide");
                Assert.True(lane.Notice(wide) is null, $"{shape.Name} [{lane.Name}] claims an overrun");
                Assert.NotNull(body);
                foreach (var subject in CheckMergeShapes.Subjects(shape.Sweep))
                    Assert.True(body!.SpentOn(subject) == body.AllocationOf(subject), $"{shape.Name} [{lane.Name}] {subject}");
                int tight = CheckMergeShapes.SmallestWholeCap(c => CheckMergeShapes.Fingerprint(lane, lane.Render(shape.Sweep, c, out _)),
                                                    CheckMergeShapes.Fingerprint(lane, whole), whole.Length);
                Assert.True(tight >= 0 && tight - whole.Length <= CheckMergeShapes.TightSlack,
                            $"{shape.Name} [{lane.Name}]: needs {tight} to render whole, returns {whole.Length}");
            }
    }

    // MATRIX-ALLOCATION-EQUALS-SPEND: with nothing cut every governed subject spends exactly its allocation
    [Fact]
    public void EveryShapesSubjectsSpendExactlyTheirAllocation()
    {
        foreach (var shape in CheckMergeShapes.Shapes)
            foreach (var lane in CheckMergeShapes.Lanes)
            {
                lane.Render(shape.Sweep, 4000000, out var body);
                Assert.NotNull(body);
                foreach (var subject in CheckMergeShapes.Subjects(shape.Sweep))
                {
                    int spent = body!.SpentOn(subject);
                    Assert.True(spent > 0, $"{shape.Name} [{lane.Name}] {subject}: spent nothing");
                    Assert.True(spent == body.AllocationOf(subject), $"{shape.Name} [{lane.Name}] {subject}: allocated {body.AllocationOf(subject)}, spent {spent}");
                }
            }
    }
}

/// <summary>The shape inventory and the one cap sweep the matrix facts read.</summary>
internal static class CheckMergeShapes
{
    internal readonly record struct Shape(string Name, CheckSweep Sweep);

    internal delegate string RenderLane(CheckSweep s, int cap, out BoundedBody? body);

    internal sealed record Lane(string Name, RenderLane Render, Func<string, string?> Notice);

    internal const int TightSlack = 2200;
    const int BandMargin = TightSlack + 200;
    // Every CapStride-th cap, offset by the shape index so the shapes together cover every residue; the probe swept every integer (151 s).
    const int CapStride = 11;
    const int RosterRows = 40;
    const string OffOrderFile = "HcMxFresh.esp";

    internal static readonly Lane[] Lanes =
    {
        new("text", (CheckSweep s, int cap, out BoundedBody? b) => CheckTextRender.RenderCheck(s, cap, 1000, out b), TextNotice),
        new("json", (CheckSweep s, int cap, out BoundedBody? b) => JsonWire.RenderCheck(s, cap, 1000, out b), JsonNotice),
    };

    static readonly Lazy<IReadOnlyList<Shape>> Built = new(Build);
    static readonly Lazy<Sweep> Result = new(() => Run(Built.Value));

    internal static IReadOnlyList<Shape> Shapes => Built.Value;
    internal static Sweep Swept => Result.Value;

    static IReadOnlyList<Shape> Build()
    {
        var e = Errors with
        {
            Reports = Errors.Reports.Take(1).Select(p => p with { Dangling = p.Dangling.Take(4).ToArray() }).ToArray(),
        };
        var kept = Scripts.Reports.Take(3).ToArray();
        kept[^1] = kept[^1] with { Unbound = Array.Empty<UnboundProperty>() };
        var sc = Scripts with { Reports = kept };
        var d = TrimDialogue(Dialogue(), 3);
        var multi = TrimDialogue(DialogueRun(new[] { "000A01:A.esp", "000B02:A.esp" }, 1000), 3);
        var refusedDialogue = DialogueRun(null, 1000);

        var eCounts = e with
        {
            CountsOnly = true,
            Histogram = new[] { new SweepCount("HcCmGhost.esm", 40), new SweepCount("HcCmOther.esm", 7) },
            DanglingBySource = new[] { new SweepCount("HcCm.esp", 33), new SweepCount("HcCmTwo.esp", 14) },
            Reports = new[]
            {
                new PluginErrors("HcMxUnread01.esp", Array.Empty<DanglingRef>(), Array.Empty<string>(), 0,
                                 Array.Empty<string>(), "record enumeration faulted"),
                new PluginErrors("HcMxUnread02.esp", Array.Empty<DanglingRef>(), Array.Empty<string>(), 0,
                                 Array.Empty<string>(), "record enumeration faulted"),
            },
        };
        var scCounts = sc with
        {
            CountsOnly = true,
            Histogram = new[] { new SweepCount("HcCmSpell", 40), new SweepCount("HcCmOther", 40), new SweepCount("HcCmChance", 40) },
            Reports = sc.Reports.Take(2).Select(rec => rec with { ScanError = "the record's VMAD could not be parsed" }).ToArray(),
        };
        var dCounts = d with { CountsOnly = true };

        var roster = new Dictionary<string, string>();
        for (int i = 0; i < RosterRows; i++) roster["HcMxBroken" + i.ToString("D2") + ".esp"] = "header could not be parsed";

        var shapes = new List<Shape>();
        void Add(string name, CheckSweep s) => shapes.Add(new Shape(name, s));

        foreach (var (lane, err, scr, dlg) in new[] { ("listing", e, sc, d), ("counts_only", eCounts, scCounts, dCounts) })
        {
            Add("errors, " + lane, new CheckSweep(Sel("errors"), err));
            Add("scripts, " + lane, new CheckSweep(Sel("scripts"), null, scr));
            Add("dialogue, " + lane, new CheckSweep(Sel("dialogue"), null, null, dlg));
            Add("errors+scripts, " + lane, new CheckSweep(Sel("errors", "scripts"), err, scr));
            Add("errors+dialogue, " + lane, new CheckSweep(Sel("errors", "dialogue"), err, null, dlg));
            Add("scripts+dialogue, " + lane, new CheckSweep(Sel("scripts", "dialogue"), null, scr, dlg));
            Add("all three, " + lane, new CheckSweep(Sel("errors", "scripts", "dialogue"), err, scr, dlg));
        }

        Add("errors, listing, roster", new CheckSweep(Sel("errors"), e with { ExcludedPlugins = roster }));
        Add("all three, listing, roster", new CheckSweep(Sel("errors", "scripts", "dialogue"),
            e with { ExcludedPlugins = roster }, sc with { ExcludedPlugins = roster }, d));
        Add("all three, counts_only, roster", new CheckSweep(Sel("errors", "scripts", "dialogue"),
            eCounts with { ExcludedPlugins = roster }, scCounts with { ExcludedPlugins = roster }, dCounts));
        Add("scripts+dialogue, counts_only, roster", new CheckSweep(Sel("scripts", "dialogue"),
            null, scCounts with { ExcludedPlugins = roster }, dCounts));

        var refusedScripts = ScriptCheckResult.Fail("exclude= removed every plugin this sweep would have covered");
        Add("all three, listing, dialogue refused", new CheckSweep(Sel("errors", "scripts", "dialogue"), e, sc, refusedDialogue));
        Add("all three, counts_only, dialogue refused", new CheckSweep(Sel("errors", "scripts", "dialogue"), eCounts, scCounts, refusedDialogue));
        Add("errors+scripts, listing, scripts refused", new CheckSweep(Sel("errors", "scripts"), e, refusedScripts));
        Add("all three, listing, scripts refused", new CheckSweep(Sel("errors", "scripts", "dialogue"), e, refusedScripts, d));
        Add("all three, listing, two refused, roster", new CheckSweep(Sel("errors", "scripts", "dialogue"),
            e with { ExcludedPlugins = roster }, refusedScripts, refusedDialogue));

        Add("dialogue, listing, two seeds", new CheckSweep(Sel("dialogue"), null, null, multi));
        Add("all three, listing, two seeds", new CheckSweep(Sel("errors", "scripts", "dialogue"), e, sc, multi));
        Add("all three, listing, two seeds, roster", new CheckSweep(Sel("errors", "scripts", "dialogue"),
            e with { ExcludedPlugins = roster }, sc with { ExcludedPlugins = roster }, multi));

        var eOff = e with { OffOrderScanned = new[] { OffOrderFile } };
        var scOff = sc with { OffOrderScanned = new[] { OffOrderFile }, TotalUnverifiable = 7, UnverifiableCollapsed = 5 };
        Add("errors+scripts, listing, off-order", new CheckSweep(Sel("errors", "scripts"), eOff, scOff));
        Add("errors+scripts, listing, off-order, scripts refused", new CheckSweep(Sel("errors", "scripts"), eOff, refusedScripts));
        Add("errors+scripts, both refused on distinct grounds", new CheckSweep(Sel("errors", "scripts"),
            ErrorCheckResult.Fail("the errors family's own ground, which is not the scripts family's"), refusedScripts));
        Add("all three, counts_only, off-order", new CheckSweep(Sel("errors", "scripts", "dialogue"),
            eCounts with { OffOrderScanned = new[] { OffOrderFile } }, scCounts with { OffOrderScanned = new[] { OffOrderFile } }, dCounts));
        Add("all three, listing, off-order, roster", new CheckSweep(Sel("errors", "scripts", "dialogue"),
            eOff with { ExcludedPlugins = roster }, scOff with { ExcludedPlugins = roster }, d));
        return shapes;
    }

    static DialogueCheckResult TrimDialogue(DialogueCheckResult r, int topics)
    {
        var trimmed = r.Seeds.Select(s => s.Report is { Topics.Count: > 0 }
                                        ? s with { Report = s.Report with { Topics = s.Report.Topics.Take(topics).ToArray() } }
                                        : s).ToArray();
        return r with { Seeds = trimmed, TopicsFound = topics * r.Resolved.Count() };
    }

    internal sealed class Sweep
    {
        public List<string> OverCap { get; } = new();
        public List<string> NonMonotone { get; } = new();
        public List<string> RemedyBad { get; } = new();
        public List<string> Unparseable { get; } = new();
        public List<string> NeverRendered { get; } = new();
        public HashSet<SweepSubject> EverRendered { get; } = new();
        public List<string> OneBudget { get; } = new();
        public List<string> HonestyBad { get; } = new();
        public HashSet<(string Lane, int Term)> TermsReached { get; } = new();
        public Dictionary<string, int> TermCount { get; } = new();
        public int NoticesFollowed { get; set; }
        public int HonestyCells { get; set; }
    }

    static Sweep Run(IReadOnlyList<Shape> shapes)
    {
        var r = new Sweep();
        foreach (var (shape, index) in shapes.Select((s, i) => (s, i)))
            foreach (var lane in Lanes)
            {
                int floor = lane.Render(shape.Sweep, 1, out _).Length;
                int whole = lane.Render(shape.Sweep, 0, out _).Length;
                var previous = new Dictionary<SweepSubject, (int Cap, int Spent, int Allocated)>();
                var rendered = new HashSet<SweepSubject>();
                var subjects = Subjects(shape.Sweep);

                for (int cap = 1 + index % CapStride; cap <= whole + BandMargin; cap += CapStride)
                {
                    string response = lane.Render(shape.Sweep, cap, out var body);
                    int allowed = Math.Max(cap, floor + 8 * cap.ToString().Length);
                    if (response.Length > allowed && r.OverCap.Count < 6)
                        r.OverCap.Add($"{shape.Name} [{lane.Name}] @{cap}: {response.Length} chars, allowed {allowed}");

                    if (lane.Name == "json")
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(response);
                            r.HonestyCells += HonestyLayers(doc.RootElement, shape.Name, cap, r.HonestyBad);
                        }
                        catch (Exception ex)
                        {
                            if (r.Unparseable.Count < 4) r.Unparseable.Add($"{shape.Name} @{cap}: {ex.GetType().Name}");
                        }
                    }

                    if (body is not null)
                    {
                        if (body.BodyTotal > body.RowBudget && r.OneBudget.Count < 6)
                            r.OneBudget.Add($"{shape.Name} [{lane.Name}] @{cap}: units spent {body.BodyTotal} of a {body.RowBudget} row budget");
                        if (body.OutstandingHigh > body.ReservedForRows && r.OneBudget.Count < 6)
                            r.OneBudget.Add($"{shape.Name} [{lane.Name}] @{cap}: owed {body.OutstandingHigh} outside its units, measured {body.ReservedForRows}");
                        foreach (var subject in subjects)
                        {
                            int spent = body.SpentOn(subject);
                            if (previous.TryGetValue(subject, out var was) && spent < was.Spent && r.NonMonotone.Count < 6)
                                r.NonMonotone.Add($"{shape.Name} [{lane.Name}] {subject}: {spent} at cap {cap}, {was.Spent} at {was.Cap}");
                            previous[subject] = (cap, spent, body.AllocationOf(subject));
                            if (spent > 0) rendered.Add(subject);
                        }
                    }

                    if (lane.Notice(response) is { } notice)
                    {
                        r.NoticesFollowed++;
                        if (Number(notice, NoticeOpener, " chars") is not { } stated)
                            r.RemedyBad.Add($"{shape.Name} [{lane.Name}] @{cap}: the notice states no length");
                        else if (stated != response.Length && r.RemedyBad.Count < 6)
                            r.RemedyBad.Add($"{shape.Name} [{lane.Name}] @{cap}: says {stated} chars, is {response.Length}");
                        if (Number(notice, "raise max_chars to at least ", ".") is not { } raiseTo)
                            r.RemedyBad.Add($"{shape.Name} [{lane.Name}] @{cap}: names no cap to raise to");
                        else
                        {
                            if (raiseTo <= cap && r.RemedyBad.Count < 6)
                                r.RemedyBad.Add($"{shape.Name} [{lane.Name}] @{cap}: raise to {raiseTo} is not above the cap");
                            if (lane.Notice(lane.Render(shape.Sweep, raiseTo, out _)) is not null && r.RemedyBad.Count < 6)
                                r.RemedyBad.Add($"{shape.Name} [{lane.Name}] @{cap}: following the remedy to {raiseTo} left the notice standing");
                        }
                    }
                }

                foreach (var subject in subjects)
                    if (!rendered.Contains(subject) && r.NeverRendered.Count < 6)
                        r.NeverRendered.Add($"{shape.Name} [{lane.Name}] {subject}: never given room in its band");
                r.EverRendered.UnionWith(rendered);

                var terms = Fingerprint(lane, lane.Render(shape.Sweep, 0, out _)).Split('/');
                r.TermCount[lane.Name] = terms.Length;
                for (int i = 0; i < terms.Length; i++)
                    if (int.TryParse(terms[i], out var n) && n > 0) r.TermsReached.Add((lane.Name, i));
            }
        return r;
    }

    static int HonestyLayers(JsonElement root, string shape, int cap, List<string> bad)
    {
        int seen = 0;
        foreach (var (family, member) in new[] { ("errors", "unread"), ("scripts", "scan_errors") })
        {
            if (!root.TryGetProperty("families", out var fams) || fams.ValueKind != JsonValueKind.Object) return seen;
            if (!fams.TryGetProperty(family, out var f) || f.ValueKind != JsonValueKind.Object) continue;
            if (!f.TryGetProperty(member, out var layer)) continue;
            seen++;
            if (layer.ValueKind != JsonValueKind.Object)
            {
                if (bad.Count < 6) bad.Add($"{shape} @{cap}: families.{family}.{member} is {layer.ValueKind}");
                continue;
            }
            int? total = Num(layer, "total"), stated = Num(layer, "rendered");
            bool? cut = Bool(layer, "truncated");
            int? carried = Arr(layer, "rows")?.GetArrayLength();
            if (total is null || stated is null || cut is null || carried is null)
            {
                if (bad.Count < 6) bad.Add($"{shape} @{cap}: families.{family}.{member} misses one of total/rows/rendered/truncated");
                continue;
            }
            if (stated != carried && bad.Count < 6) bad.Add($"{shape} @{cap}: {family}.{member} states {stated}, carries {carried}");
            if (cut != (carried < total) && bad.Count < 6) bad.Add($"{shape} @{cap}: {family}.{member} truncated={cut} with {carried} of {total}");
        }
        return seen;
    }

    static readonly string NoticeOpener =
        CheckSentences.SweepFixedPartLead[..CheckSentences.SweepFixedPartLead.IndexOf('{')];

    static string? TextNotice(string response)
    {
        int at = response.IndexOf(NoticeOpener, StringComparison.Ordinal);
        return at < 0 ? null : response[at..];
    }

    static string? JsonNotice(string response)
    {
        try
        {
            using var doc = JsonDocument.Parse(response);
            return doc.RootElement.TryGetProperty("max_chars_overrun", out var n) ? n.GetString() : null;
        }
        catch { return null; }
    }

    static int? Number(string s, string after, string before)
    {
        int at = s.IndexOf(after, StringComparison.Ordinal);
        if (at < 0) return null;
        int from = at + after.Length;
        int to = s.IndexOf(before, from, StringComparison.Ordinal);
        if (to < 0) return null;
        return int.TryParse(s[from..to].Replace(",", ""), out var n) ? n : null;
    }

    internal static int SmallestWholeCap(Func<int, string> unitsAt, string whole, int from)
    {
        int lo = Math.Max(1, from), hi = Math.Max(lo + 1, from * 3 + 4000);
        if (unitsAt(hi) != whole) return -1;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (unitsAt(mid) == whole) hi = mid; else lo = mid + 1;
        }
        return lo;
    }

    /// <summary>Every subject a shape can render: its families' planned subjects and the response's own.</summary>
    internal static SweepSubject[] Subjects(CheckSweep s)
    {
        var o = CheckOutcome.For(s);
        return o.Plan().SelectMany(p => p.Subjects).Concat(o.ResponseSubjects).Distinct().ToArray();
    }

    internal static string Fingerprint(Lane lane, string response)
        => lane.Name == "text" ? TextFingerprint(response) : JsonFingerprint(response);

    static string TextFingerprint(string t)
        => string.Join("/", new[]
           {
               Count(t, "\n[ERROR] "), Count(t, "\n[UNBOUND] "), Count(t, "\n[CHECK] "),
               Count(t, "\n[SCAN ERROR] "), Count(t, "\n[UNREAD] "), Count(t, "   [target not defined by any active plugin]"),
               Count(t, "\nseed "), Count(t, "  topic "), Count(t, "NOT validated:"),
               Count(t, "  HcMxBroken"), HistogramRows(t),
           });

    static int HistogramRows(string t)
    {
        int n = 0;
        foreach (var line in t.Split('\n'))
            if (line.Length > 10 && line.StartsWith("  ", StringComparison.Ordinal)
                && line[8..10] == "  " && int.TryParse(line[2..8].Trim(), out _)) n++;
        return n;
    }

    static string JsonFingerprint(string response)
    {
        try
        {
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            return string.Join("/", new[] { ("errors", "plugins"), ("scripts", "records"), ("dialogue", "seeds") }
                                    .Select(fa => ArrayLength(root, fa.Item1, fa.Item2))
                                    .Append(Arr(root, "excluded_plugins")?.GetArrayLength() ?? -1)
                                    .Append(Count(response, "\"count\":"))
                                    .Append(Count(response, "\"target\":"))
                                    .Append(Count(response, "\"topic\":"))
                                    .Append(Count(response, "\"scan_error\":")));
        }
        catch { return "unparseable"; }
    }
}

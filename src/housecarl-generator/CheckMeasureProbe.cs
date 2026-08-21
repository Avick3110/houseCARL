using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// MANUAL / REAL-DATA measurement harness for PR 4's kickoff (`check-measure`): the two #361 lanes and the response
/// accounting as they behave on the LIVE order, taken BEFORE any design work — the kickoff's binding empirical check.
/// A synthetic fixture cannot stand in: every number here is a function of how big a real order's findings are against
/// an 80k cap, and the #342s1 lesson is that a toy fixture was 15-20x off the order it stood for.
///
/// Drives the TOOL layer (<see cref="ReadTools.CheckErrorsTool"/>) — the same entry housecarl_check_errors calls, so
/// what it measures is what a caller receives, render included. Read-only; needs --mo2 &lt;instance&gt;, SKIPs without.
/// </summary>
public static class CheckMeasureProbe
{
    public static int RunMeasure(string[] args)
    {
        string? mo2 = ArgVal(args, "--mo2");
        if (mo2 is null) { Console.WriteLine("check-measure needs --mo2 <MO2 instance folder>"); return 2; }
        string? only = ArgVal(args, "--plugin");

        var store = new UserConfigStore(Path.Combine(Path.GetTempPath(), "hc-check-measure-" + Guid.NewGuid().ToString("N") + ".json"));
        using var svc = LoadOrderService.WithInstance(mo2, 0, store);

        int cap = Wire.DefaultMaxChars;
        Console.WriteLine($"# check-measure — live order at {mo2}");
        Console.WriteLine($"# default max_chars = {cap}, default limit = 1000\n");

        if (Array.IndexOf(args, "--listing") >= 0) return RunListing(svc);
        if (Array.IndexOf(args, "--budget") >= 0) return RunBudget(svc);

        if (only is null)
        {
            // Cell A — the whole-order text sweep at plain defaults: what a caller who types
            // `housecarl_check_errors` with no arguments actually receives.
            Cell("A  whole-order text, defaults", () => ReadTools.CheckErrorsTool(svc));
            // Cell B — the same sweep as json: the twin lane, same defaults.
            Cell("B  whole-order json, defaults", () => ReadTools.CheckErrorsTool(svc, format: "json"));
            // Cell C — counts_only, which names the by-SOURCE tally the single-plugin cells below need.
            Cell("C  counts_only text, defaults", () => ReadTools.CheckErrorsTool(svc, counts_only: true));
            // Cell F -- the flagged residual, measured rather than reasoned about. A counts_only axis renders its
            // rows through the bounded emitter and closes with a "... N more row(s)" line; if max_chars leaves room
            // for neither, the axis is absent ENTIRELY, and in a counts_only lane with no unread and no excluded
            // rows the accounting declares no subject, so TextLine() returns null and the response closes as
            // complete. The question this cell answers is whether that band is reachable at PLAIN DEFAULTS on a
            // real order, or only at caps far below what a caller ever passes.
            // Plain defaults is the cell that decides it; the ladder below walks DOWN from there to find where the
            // band actually starts, so the answer is "not at defaults, and here is how far below them it lives"
            // rather than "not at defaults" alone.
            AxisCell("F  counts_only axis-drop band, defaults", 0, () => ReadTools.CheckErrorsTool(svc, counts_only: true));
            foreach (int c in new[] { 2000, 1200 })
                AxisCell($"F  counts_only axis-drop band, max_chars={c}", c,
                         () => ReadTools.CheckErrorsTool(svc, counts_only: true, max_chars: c));
            // FOLLOW THE REMEDY, on live data. The guard follows it on fixtures; a remedy is a claim about a call
            // that has not happened, and the caller making it is on a real order. Whatever number the 1200 cell's
            // notice named, this is that call.
            FollowRemedy(ReadTools.CheckErrorsTool(svc, counts_only: true, max_chars: 1200),
                         n => ReadTools.CheckErrorsTool(svc, counts_only: true, max_chars: n));
            return 0;
        }

        // Cells D/E — ONE plugin in scope. This is the shape that reaches #361's silent path: with a single report
        // section, a cut inside it ends the inner loop and the outer loop then EXHAUSTS rather than breaking, so the
        // boundary-fired truncation notice never runs.
        Cell($"D  plugins=[{only}] text, defaults", () => ReadTools.CheckErrorsTool(svc, new[] { only }));
        Cell($"E  plugins=[{only}] json, defaults", () => ReadTools.CheckErrorsTool(svc, new[] { only }, format: "json"));
        return 0;
    }

    /// <summary>#394's measurement, on the live order: what SERIAL budget spending gives each counts_only histogram
    /// axis across a cap ladder, and what the same lanes cost when a second findings FAMILY shares the response.
    ///
    /// <para>The issue's own reproduction is one cap on one shape. What the 4b budget-policy decision needs instead
    /// is the BAND — where the serial split starts biting and where it stops mattering — plus the number no single
    /// cap can show: whether the merged surface's combined counts_only response fits a plain default at all. A rule
    /// that is invisible at every cap a caller actually passes is a different decision from one that starves a
    /// family on the no-arguments call.</para>
    ///
    /// <para>Per-axis ROW COST is printed beside the row counts, because the candidate splits differ only in how
    /// they divide room among rows of known width: an equal split of R chars over two axes whose rows cost c1 and
    /// c2 renders R/2c1 and R/2c2 rows, and that arithmetic is only worth doing over measured widths.</para>
    /// </summary>
    static int RunBudget(LoadOrderService svc)
    {
        Console.WriteLine("## 394-A  errors family, counts_only TEXT — the serial spend across a cap ladder");
        Console.WriteLine("   (rows/distinct per axis; cap 80000 is the plain default)\n");
        Console.WriteLine($"   {"cap",8} {"chars",8} {"TARGET",14} {"SOURCE",14}");
        foreach (int c in new[] { 0, 40000, 20000, 10000, 6000, 4000, 3000, 2500, 2000, 1600, 1400, 1200, 1000 })
        {
            string s = ReadTools.CheckErrorsTool(svc, counts_only: true, max_chars: c);
            var (tr, td) = AxisRows(s, "TARGET plugin");
            var (sr, sd) = AxisRows(s, "SOURCE plugin");
            int eff = c > 0 ? c : Wire.DefaultMaxChars;
            Console.WriteLine($"   {eff,8} {s.Length,8} {tr + "/" + td,14} {sr + "/" + sd,14}");
        }

        Console.WriteLine("\n## 394-B  errors family, counts_only JSON — the same question, the other transport\n");
        Console.WriteLine($"   {"cap",8} {"chars",8} {"TARGET",14} {"SOURCE",14}");
        foreach (int c in new[] { 0, 10000, 6000, 4000, 3000, 2000, 1600, 1200 })
        {
            string s = ReadTools.CheckErrorsTool(svc, counts_only: true, format: "json", max_chars: c);
            int eff = c > 0 ? c : Wire.DefaultMaxChars;
            Console.WriteLine($"   {eff,8} {s.Length,8} {JsonAxisRows(s, "dangling_by_target_plugin"),14} {JsonAxisRows(s, "dangling_by_source_plugin"),14}");
        }

        Console.WriteLine("\n## 394-C  per-axis ROW COST, measured off the rows a whole render writes");
        {
            string s = ReadTools.CheckErrorsTool(svc, counts_only: true);
            Console.WriteLine($"   TARGET axis: {RowCost(s, "TARGET plugin")}");
            Console.WriteLine($"   SOURCE axis: {RowCost(s, "SOURCE plugin")}");
        }

        Console.WriteLine("\n## 394-D  the SCRIPTS family, counts_only — the second family the merge puts in one response");
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string s = ReadTools.ValidateScriptsTool(svc, counts_only: true);
            sw.Stop();
            Console.WriteLine($"   chars      : {s.Length}  (cap {Wire.DefaultMaxChars})  elapsed {sw.ElapsedMilliseconds} ms");
            var (rows, distinct) = AxisRows(s, "unbound properties by NAME", "\nunbound properties by NAME");
            Console.WriteLine($"   by-PROPERTY axis: rows={rows} distinct={distinct}");
            foreach (var l in s.Split('\n'))
                if (l.StartsWith("scanned ", StringComparison.Ordinal)) Console.WriteLine("   > " + Trim(l));
        }

        Console.WriteLine("\n## 394-E  what the MERGE puts in ONE response: the families' counts_only sizes, summed");
        {
            string e = ReadTools.CheckErrorsTool(svc, counts_only: true);
            string p = ReadTools.ValidateScriptsTool(svc, counts_only: true);
            int sum = e.Length + p.Length;
            Console.WriteLine($"   errors  counts_only : {e.Length}");
            Console.WriteLine($"   scripts counts_only : {p.Length}");
            Console.WriteLine($"   sum                 : {sum}   (default cap {Wire.DefaultMaxChars}; "
                              + (sum > Wire.DefaultMaxChars ? "OVER by " + (sum - Wire.DefaultMaxChars)
                                                            : "fits with " + (Wire.DefaultMaxChars - sum) + " to spare") + ")");
            Console.WriteLine("   NOTE: an upper bound for the merged render — the merge writes ONE header and ONE boundary,");
            Console.WriteLine("   so the true combined size is this less one lane's framing. What it answers is the");
            Console.WriteLine("   FAMILY-COUNT question: whether a plain-default combined call can starve a family at all.");
        }
        return 0;
    }

    /// <summary>The LISTING lane's family-budget measurement, which is the one the counts_only question does not
    /// reach. #394 is stated over the <c>counts_only</c> histogram axes, where the two axes together cost ten
    /// thousand characters against an eighty-thousand default and the serial split is invisible unless a caller
    /// tightens the cap by hand. The listing lane is the opposite shape: on a real order the errors family alone
    /// renders to within a few hundred characters of the plain default, so a SECOND family spending after it in
    /// series receives whatever that leaves — at the no-arguments call, not at a tightened one.
    ///
    /// <para>Both families are rendered at plain defaults and their sizes reported against the default cap. The
    /// number that matters is the REMAINDER: what the merged response would have left for the family that renders
    /// second under today's serial rule.</para></summary>
    static int RunListing(LoadOrderService svc)
    {
        Console.WriteLine("## 394-F  the LISTING lane at plain defaults — what a second family would inherit\n");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        string e = ReadTools.CheckErrorsTool(svc);
        long eMs = sw.ElapsedMilliseconds;
        Console.WriteLine($"   errors  listing : {e.Length} chars, {eMs} ms");
        foreach (var l in e.Split('\n'))
            if (l.StartsWith("scanned ", StringComparison.Ordinal)) Console.WriteLine("   > " + Trim(l));
        foreach (var l in e.Split('\n'))
            if (l.Contains("[accounting:", StringComparison.Ordinal)) Console.WriteLine("   > " + Trim(l));

        sw.Restart();
        string p = ReadTools.ValidateScriptsTool(svc);
        long pMs = sw.ElapsedMilliseconds;
        Console.WriteLine($"\n   scripts listing : {p.Length} chars, {pMs} ms");
        foreach (var l in p.Split('\n'))
            if (l.StartsWith("scanned ", StringComparison.Ordinal)) Console.WriteLine("   > " + Trim(l));

        int cap = Wire.DefaultMaxChars;
        Console.WriteLine($"\n   default cap                              : {cap}");
        Console.WriteLine($"   errors listing leaves, spending in series : {cap - e.Length}");
        Console.WriteLine($"   scripts listing wants                     : {p.Length}");
        Console.WriteLine($"   sum of the two lanes                      : {e.Length + p.Length}"
                          + (e.Length + p.Length > cap ? $"   OVER the default by {e.Length + p.Length - cap}" : ""));
        Console.WriteLine("\n   The remainder is what a serial second family receives at the NO-ARGUMENTS call.");
        return 0;
    }

    /// <summary>Rendered rows and the distinct count for one text-lane axis, read off the RENDER rather than the
    /// model — what the caller can see, which is the whole subject of #394.</summary>
    static (int Rows, int Distinct) AxisRows(string s, string axis, string? lead = null)
    {
        string needle = lead ?? ("\ndangling ref(s) by " + axis);
        int at = s.IndexOf(needle, StringComparison.Ordinal);
        if (at < 0) return (-1, -1);
        int end = s.IndexOf("\n\n", at + 2, StringComparison.Ordinal);
        var seg = end < 0 ? s[at..] : s[at..end];
        int rows = seg.Split('\n').Count(l => l.StartsWith("  ", StringComparison.Ordinal)
                                           && !l.StartsWith("  ...", StringComparison.Ordinal) && l.Trim().Length > 0);
        var m = System.Text.RegularExpressions.Regex.Match(seg, @"\((\d+) distinct\)");
        return (rows, m.Success ? int.Parse(m.Groups[1].Value) : -1);
    }

    /// <summary>Rendered rows and distinct count for one json-lane axis. Read off the axis object's OWN
    /// <c>rendered</c>/<c>distinct</c>/<c>cut_by</c> members rather than by counting row objects: the axis states
    /// those itself, and a counter that re-derives them can disagree with the document it is measuring.</summary>
    static string JsonAxisRows(string s, string field)
    {
        int at = s.IndexOf("\"" + field + "\"", StringComparison.Ordinal);
        if (at < 0) return "ABSENT";
        int close = s.IndexOf("]", at, StringComparison.Ordinal);
        var seg = s[at..Math.Min(s.Length, close > 0 ? close + 400 : s.Length)];
        // Read each member by name off the axis object's own text. Deliberately not a regex: the pattern would
        // have to carry escaped quotes, and a measurement instrument that is hard to read is one nobody checks.
        string N(string k)
        {
            int i = seg.IndexOf('"' + k + '"', StringComparison.Ordinal);
            if (i < 0) return "?";
            i = seg.IndexOf(':', i) + 1;
            var digits = new string(seg[i..].SkipWhile(c => c == ' ').TakeWhile(char.IsDigit).ToArray());
            return digits.Length > 0 ? digits : "?";
        }
        int c = seg.IndexOf("\"cut_by\"", StringComparison.Ordinal);
        string cut = "";
        if (c >= 0)
        {
            var tail = seg[(seg.IndexOf(':', c) + 1)..].TrimStart();
            if (!tail.StartsWith("null", StringComparison.Ordinal))
                cut = " " + new string(tail.Skip(1).TakeWhile(ch => ch != '"').ToArray());
        }
        return N("rendered") + "/" + N("distinct") + cut;
    }

    /// <summary>The width of one axis's rendered rows — min/mean/max/total, over the rows actually written. The
    /// input every candidate split rule is priced against.</summary>
    static string RowCost(string s, string axis)
    {
        int at = s.IndexOf("\ndangling ref(s) by " + axis, StringComparison.Ordinal);
        if (at < 0) return "ABSENT";
        int end = s.IndexOf("\n\n", at + 2, StringComparison.Ordinal);
        var rows = (end < 0 ? s[at..] : s[at..end]).Split('\n')
            .Where(l => l.StartsWith("  ", StringComparison.Ordinal) && !l.StartsWith("  ...", StringComparison.Ordinal) && l.Trim().Length > 0)
            .Select(l => l.Length + 1).ToList();
        return rows.Count == 0 ? "no rows"
             : $"n={rows.Count} min={rows.Min()} mean={rows.Average():F1} max={rows.Max()} total={rows.Sum()}";
    }

    /// <summary>Read the number an overrun notice tells the caller to raise <c>max_chars</c> to, make that call, and
    /// report what comes back — on the LIVE order rather than on a fixture. Two claims land here: the notice must be
    /// gone (the remedy works), and the raised cap must not be materially larger than it needed to be, which the
    /// re-rendered length is the evidence for.</summary>
    static void FollowRemedy(string overrun, Func<int, string> render)
    {
        int at = overrun.IndexOf("raise it to at least ", StringComparison.Ordinal);
        if (at < 0) { Console.WriteLine("## F  follow the remedy: the 1200 response named no number"); return; }
        var digits = new string(overrun[(at + 21)..].TakeWhile(char.IsDigit).ToArray());
        if (!int.TryParse(digits, out int raised)) { Console.WriteLine("## F  follow the remedy: unparseable number"); return; }
        var again = render(raised);
        Console.WriteLine($"## F  follow the remedy, max_chars={raised}");
        Console.WriteLine($"   chars      : {again.Length}  (cap {raised}, unused {raised - again.Length})");
        Console.WriteLine($"   notice gone: {!again.Contains("raise it to at least", StringComparison.Ordinal)}");
        Console.WriteLine($"   inside cap : {again.Length <= raised}");
    }

    /// <summary>Report, for a counts_only response, whether EITHER axis is missing outright and whether the response
    /// says anything about it. An axis that renders no rows AND states no cut AND leaves no accounting line is the
    /// silent drop; an axis that states its cut, or a response that carries an accounting line, is not.</summary>
    static void AxisCell(string label, int cap, Func<string> call)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string s = call();
        sw.Stop();
        Console.WriteLine($"## {label}");
        int effective = cap > 0 ? cap : Wire.DefaultMaxChars;
        Console.WriteLine($"   chars      : {s.Length}  (cap {effective})");
        foreach (var axis in new[] { "TARGET plugin", "SOURCE plugin" })
        {
            const string lead = "\ndangling ref(s) by ";
            int at = s.IndexOf(lead + axis, StringComparison.Ordinal);
            if (at < 0)
            {
                bool empty = s.Contains("by " + axis + " (the plugin the broken refs ", StringComparison.Ordinal);
                Console.WriteLine($"   {axis,-14}: ABSENT from the response (empty-case line present: {empty})");
                continue;
            }
            int end = s.IndexOf(lead, at + lead.Length, StringComparison.Ordinal);
            var seg = end < 0 ? s[at..] : s[at..end];
            int rows = seg.Split('\n').Count(l => l.StartsWith("  ", StringComparison.Ordinal)
                                               && !l.StartsWith("  ...", StringComparison.Ordinal)
                                               && l.Trim().Length > 0);
            int cut = seg.IndexOf("more row(s) — raise ", StringComparison.Ordinal);
            Console.WriteLine($"   {axis,-14}: rows={rows}  states a cut={cut >= 0}"
                              + (cut >= 0 ? $" [{seg.Split('\n').First(l => l.Contains("more row(s)", StringComparison.Ordinal)).Trim()}]" : ""));
        }
        bool acct = s.Contains("[accounting:", StringComparison.Ordinal);
        bool notice = s.Contains("raise it to at least", StringComparison.Ordinal);
        Console.WriteLine($"   accounting line present : {acct}");
        Console.WriteLine($"   overrun notice present  : {notice}");
        // WHICH overrun it named and what it told the caller to raise to. Both are decided by the fixed part the
        // notice is handed, and on a counts_only response that emits no body unit the wrong branch is the one that
        // says a body unit overshot. Printed rather than inferred, because "a notice fired" is not the claim.
        if (notice)
        {
            int at = s.IndexOf(" This response is ", StringComparison.Ordinal);
            Console.WriteLine($"   overrun notice says     : {(at < 0 ? "(not found)" : s[at..].Trim())}");
        }
        // WHAT the accounting says matters as much as whether it exists: a line that discloses a different subject
        // entirely is not a disclosure of the axes, and the distinction is the whole question.
        if (acct)
        {
            int a = s.IndexOf("[accounting:", StringComparison.Ordinal);
            int z = s.IndexOf("]", a, StringComparison.Ordinal);
            Console.WriteLine($"   accounting says         : {s[a..(z < 0 ? s.Length : z + 1)]}");
            Console.WriteLine($"   ...mentions a histogram : {s[a..(z < 0 ? s.Length : z + 1)].Contains("histogram", StringComparison.OrdinalIgnoreCase) || s[a..(z < 0 ? s.Length : z + 1)].Contains("row(s)", StringComparison.Ordinal)}");
        }
        // An ABSENT axis is silent about ITSELF by construction: its "... N more row(s)" line lives inside its own
        // segment, so an axis that did not render has no way to have stated its cut. The accounting cannot cover it
        // either — the histogram subjects are deliberately not declared accounting subjects. So the presence of an
        // accounting line is NOT a disclosure of a dropped axis: at max_chars=2000 on the live order it reads
        // "0 of 1 plugin(s) whose records could not be read are named above", about an entirely different subject,
        // while the whole by-SOURCE axis is gone with nothing naming it.
        bool absent = !s.Contains("by TARGET plugin", StringComparison.Ordinal)
                   || !s.Contains("by SOURCE plugin", StringComparison.Ordinal);
        Console.WriteLine($"   SILENT AXIS DROP        : {absent}   (an absent axis cannot state its own cut, and the accounting has no histogram subject)");
        Console.WriteLine($"   elapsed    : {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>Run one cell and report the facts that decide both #361 lanes: the response's true size against the
    /// cap it was rendered under, whether a truncation notice is present, and the accounting lines it did emit.</summary>
    static void Cell(string label, Func<string> call)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string s = call();
        sw.Stop();
        int cap = Wire.DefaultMaxChars;
        Console.WriteLine($"## {label}");
        Console.WriteLine($"   chars      : {s.Length}  (cap {cap}{(s.Length > cap ? $" — OVER by {s.Length - cap}" : "")})");
        Console.WriteLine($"   states what it is missing : {s.Contains("found by this sweep appear above") || s.Contains("\"dangling_missing\"")}");
        Console.WriteLine($"   over its cap              : {s.Length > cap}{(s.Contains("raise it to at least") || s.Contains("max_chars_overrun") ? " (declared)" : "")}");
        Console.WriteLine($"   json truncated flag       : {(s.Contains("\"truncated\"") ? s.Substring(s.IndexOf("\"truncated\"", StringComparison.Ordinal), Math.Min(24, s.Length - s.IndexOf("\"truncated\"", StringComparison.Ordinal))) : "n/a")}");
        foreach (var l in s.Split('\n'))
            if (l.Contains("[accounting:", StringComparison.Ordinal)) Console.WriteLine("   accounting : " + Trim(l));
        // The number no sentence in either format states: how many findings this RESPONSE actually carries. Counted
        // off the rendered entries themselves, so it is what the caller can see rather than what a layer intended.
        int shown = s.Contains("\"target\"") ? Count(s, "\"target\":") : Count(s, "[target not defined by any active plugin]");
        Console.WriteLine($"   dangling entries VISIBLE in the response : {shown}");
        Console.WriteLine($"   boundary footer present   : {s.Contains("boundary: checks FormLink resolution") || s.Contains("\"boundary\"")}");
        Console.WriteLine($"   elapsed    : {sw.ElapsedMilliseconds} ms");
        foreach (var line in s.Split('\n'))
            if (line.StartsWith("scanned ", StringComparison.Ordinal) || line.StartsWith("baseline:", StringComparison.Ordinal))
                Console.WriteLine("   > " + Trim(line));
        Console.WriteLine($"   last 220 chars: …{Trim(s[Math.Max(0, s.Length - 220)..])}");
        Console.WriteLine();
    }

    static int Count(string hay, string needle)
    {
        int n = 0, i = 0;
        while ((i = hay.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    static string Trim(string s) => s.Replace("\r", "").Replace("\n", " ⏎ ");

    static string? ArgVal(string[] a, string key)
    {
        int i = Array.IndexOf(a, key);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }
}

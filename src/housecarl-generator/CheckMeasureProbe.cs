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
            return 0;
        }

        // Cells D/E — ONE plugin in scope. This is the shape that reaches #361's silent path: with a single report
        // section, a cut inside it ends the inner loop and the outer loop then EXHAUSTS rather than breaking, so the
        // boundary-fired truncation notice never runs.
        Cell($"D  plugins=[{only}] text, defaults", () => ReadTools.CheckErrorsTool(svc, new[] { only }));
        Cell($"E  plugins=[{only}] json, defaults", () => ReadTools.CheckErrorsTool(svc, new[] { only }, format: "json"));
        return 0;
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

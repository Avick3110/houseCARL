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
            return 0;
        }

        // Cells D/E — ONE plugin in scope. This is the shape that reaches #361's silent path: with a single report
        // section, a cut inside it ends the inner loop and the outer loop then EXHAUSTS rather than breaking, so the
        // boundary-fired truncation notice never runs.
        Cell($"D  plugins=[{only}] text, defaults", () => ReadTools.CheckErrorsTool(svc, new[] { only }));
        Cell($"E  plugins=[{only}] json, defaults", () => ReadTools.CheckErrorsTool(svc, new[] { only }, format: "json"));
        return 0;
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
        Console.WriteLine($"   truncation notice present : {s.Contains("[truncated at max_chars=")}");
        Console.WriteLine($"   json truncated flag       : {(s.Contains("\"truncated\"") ? s.Substring(s.IndexOf("\"truncated\"", StringComparison.Ordinal), Math.Min(24, s.Length - s.IndexOf("\"truncated\"", StringComparison.Ordinal))) : "n/a")}");
        Console.WriteLine($"   budget line present       : {s.Contains("[the listing budget (limit=) omitted")}");
        // The number no sentence in either format states: how many findings this RESPONSE actually carries. Counted
        // off the rendered entries themselves, so it is what the caller can see rather than what a layer intended.
        int shown = s.Contains("\"target\"") ? Count(s, "\"target\":") : Count(s, "[target not defined by any active plugin]");
        Console.WriteLine($"   dangling entries VISIBLE in the response : {shown}");
        Console.WriteLine($"   boundary footer present   : {s.Contains("boundary: checks FormLink resolution") || s.Contains("\"boundary\"")}");
        Console.WriteLine($"   elapsed    : {sw.ElapsedMilliseconds} ms");
        foreach (var line in s.Split('\n'))
            if (line.StartsWith("scanned ", StringComparison.Ordinal) || line.StartsWith("baseline:", StringComparison.Ordinal)
                || line.Contains("[the listing budget", StringComparison.Ordinal) || line.Contains("[truncated at max_chars=", StringComparison.Ordinal))
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

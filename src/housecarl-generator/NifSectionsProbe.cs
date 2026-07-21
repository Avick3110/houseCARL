using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for the nif_inspect <c>sections=</c> parsing (#247). Passing <c>sections</c> as a
/// JSON array — the natural MCP way to send a list — arrived as the literal string <c>["shapes","paths"]</c> and, split
/// only on comma/space, tokenized to <c>["shapes</c> / <c>"paths"]</c> (brackets+quotes glued on), read as
/// UNRECOGNIZED, so the tool QUIETLY fell back to rendering the default SUMMARY (a Q3 silent-degrade: the batch ran with
/// the wrong sections and the warning scrolled off the top of a 95 KB file). The tokenizer now treats bracket+quote as
/// delimiters too (the array form parses), and an all-unrecognized <c>sections=</c> is a LOUD error, never a silent
/// summary.
///
/// Exercises the real parsing seams (<see cref="NifTools.ParseSections"/> / <see cref="NifTools.SectionsError"/> /
/// <see cref="NifTools.KnownSectionsHint"/>):
///   * ARRAY-FORM  — <c>["shapes","paths"]</c> parses to {shapes, paths} with NO unknowns (the core fix; RED before).
///   * PLAIN       — <c>shapes, paths</c> still parses the same (the comma/space form is unchanged).
///   * ALL         — <c>all</c> expands to every known section.
///   * PARTIAL     — <c>["shapes","textures"]</c> → {shapes} + unknown {textures}: NOT an error (renders shapes + warns).
///   * ALL-UNKNOWN — <c>["textures"]</c> → no recognized section → a LOUD error naming the token + the hint (not a summary).
///   * EMPTY       — <c>""</c> → nothing requested → NOT an error (the summary is the intended default).
///   * HINT        — the shared known-sections hint points at where texture-set slot paths live ('shapes' / 'paths').
///
/// Run: dotnet run --project src/housecarl-generator -- nif-sections-guard
/// </summary>
public static class NifSectionsProbe
{
    static int _pass, _fail;

    public static int RunGuard(string[] args)
    {
        _pass = _fail = 0;
        Console.WriteLine("################  REGRESSION GUARD — nif_inspect sections= parsing (#247)  ################");
        Console.WriteLine();

        // ARRAY-FORM (the #247 fix): the JSON-array-as-string parses to the sections, no unknowns.
        var (arr, arrUnknown) = NifTools.ParseSections("[\"shapes\",\"paths\"]");
        Check("ARRAY-FORM: [\"shapes\",\"paths\"] → {shapes, paths}, no unknown tokens",
              arr.SetEquals(new[] { "shapes", "paths" }) && arrUnknown.Count == 0);

        // ARRAY-FORM with a space after the comma (how a client's JSON serializer usually renders it).
        var (arrSp, arrSpUnknown) = NifTools.ParseSections("[\"shapes\", \"paths\"]");
        Check("ARRAY-FORM(spaced): [\"shapes\", \"paths\"] → {shapes, paths}, no unknown tokens",
              arrSp.SetEquals(new[] { "shapes", "paths" }) && arrSpUnknown.Count == 0);

        // PLAIN: the comma/space form is unchanged.
        var (plain, plainUnknown) = NifTools.ParseSections("shapes, paths");
        Check("PLAIN: 'shapes, paths' → {shapes, paths}, no unknown",
              plain.SetEquals(new[] { "shapes", "paths" }) && plainUnknown.Count == 0);

        // ALL: expands to every known section.
        var (all, _) = NifTools.ParseSections("all");
        Check("ALL: 'all' expands to the 7 known sections",
              all.SetEquals(new[] { "shapes", "partitions", "alpha", "paths", "strings", "nodes", "bones" }));

        // PARTIAL: some valid, some not → NOT an error (renders the valid + a warning).
        var (partial, partialUnknown) = NifTools.ParseSections("[\"shapes\",\"textures\"]");
        Check("PARTIAL: [\"shapes\",\"textures\"] → {shapes} + unknown {textures}, NOT an error",
              partial.SetEquals(new[] { "shapes" }) && partialUnknown.Count == 1 && partialUnknown[0] == "textures"
              && NifTools.SectionsError(partial, partialUnknown) is null);

        // ALL-UNKNOWN: nothing valid → a LOUD error naming the token + the hint (Q3, not a silent summary).
        var (none, noneUnknown) = NifTools.ParseSections("[\"textures\"]");
        var allUnknownErr = NifTools.SectionsError(none, noneUnknown);
        Check("ALL-UNKNOWN: [\"textures\"] → loud error naming the token, never a silent summary fallback",
              none.Count == 0 && allUnknownErr is not null
              && allUnknownErr.Contains("textures", StringComparison.Ordinal)
              && allUnknownErr.Contains("shapes", StringComparison.Ordinal));

        // EMPTY: nothing requested → NOT an error (summary is the intended default).
        var (empty, emptyUnknown) = NifTools.ParseSections("");
        Check("EMPTY: '' → nothing requested → NOT an error (summary default)",
              empty.Count == 0 && emptyUnknown.Count == 0 && NifTools.SectionsError(empty, emptyUnknown) is null);

        // HINT: the shared hint points texture-set slot paths at where they actually live.
        Check("HINT: the known-sections hint points texture-set slot paths at 'shapes'/'paths'",
              NifTools.KnownSectionsHint.Contains("texture-set", StringComparison.OrdinalIgnoreCase)
              && NifTools.KnownSectionsHint.Contains("shapes", StringComparison.Ordinal)
              && NifTools.KnownSectionsHint.Contains("paths", StringComparison.Ordinal));

        Console.WriteLine();
        Console.WriteLine($"=== nif-sections-guard: {_pass} passed, {_fail} failed -> {(_fail == 0 ? "PASS" : "FAIL")} ===");
        return _fail == 0 ? 0 : 1;
    }

    static void Check(string label, bool ok)
    {
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        if (ok) _pass++; else _fail++;
    }
}

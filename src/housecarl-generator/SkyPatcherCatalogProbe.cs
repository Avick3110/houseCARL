using HousecarlCore;

namespace HousecarlGenerator;

// ======================================================================
//  SkyPatcherCatalogProbe — SELF-CONTAINED CI regression guard for the
//  SkyPatcher grammar catalog (SkyPatcherCatalog, Wave 0b — plan
//  dev/plans/SKYPATCHER_DISTRIBUTOR_TOOL_PLAN_2026-07-08.md).
//
//  Pins the CLOSED, warn-on-unknown catalog (Aaron's Wave-0b call): the
//  embedded skypatcher-catalog.json loads, covers every documented record
//  type, holds the shape⇒tractability invariants, classifies a known
//  filter (with connective) and a known operation correctly, flags an
//  unknown key as Unknown (bundled-or-warn), preserves the OMOD gap, and
//  cross-checks the record dimension (name/sig/subfolder/primaryFilter)
//  against the skypatcher-authoring skill's own router table in SKILL.md —
//  the drift guard for when the skill reference updates. In-process; the
//  catalog is an embedded resource, the table is read from the repo
//  (CWD-relative).
// ======================================================================
public static class SkyPatcherCatalogProbe
{
    [CiProbe("skypatcher-catalog-guard")]
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("[skypatcher-catalog-guard] SkyPatcher grammar catalog (Wave 0b)");
        int failures = 0;

        SkyPatcherCatalog cat;
        try { cat = SkyPatcherCatalog.Load(); }
        catch (Exception ex) { Console.WriteLine($"FAIL  catalog failed to load: {ex.GetType().Name}: {ex.Message}"); return 1; }

        // 1. coverage — the reference documents 27 record types (+ OMOD as a gap entry).
        failures += Check($"catalog covers >= 27 record types (got {cat.Records.Count})", cat.Records.Count >= 27);

        // 2. dimension fields present + subfolders unique (route key must be unambiguous).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in cat.Records)
        {
            // OMOD is the documented gap — it legitimately has no primaryFilter; still needs recordType/sig/subfolder.
            bool gap = r.Sig.Equals("OMOD", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(r.RecordType) || string.IsNullOrWhiteSpace(r.Subfolder)
                || string.IsNullOrWhiteSpace(r.Sig) || (!gap && string.IsNullOrWhiteSpace(r.PrimaryFilter)))
                failures += Check($"'{r.RecordType}' has required dimension fields", false, r.RecordType);
            if (!seen.Add(r.Subfolder))
                failures += Check($"subfolder '{r.Subfolder}' is unique", false);
        }

        // 3. per-record: OMOD is the empty documented gap; every other type has ops + a Primary filter.
        foreach (var r in cat.Records)
        {
            bool isOmod = r.Sig.Equals("OMOD", StringComparison.OrdinalIgnoreCase);
            if (isOmod)
            {
                failures += Check("OMOD: no ops + a documented-gap note", r.Operations.Count == 0 && r.Note is not null, r.Note ?? "<no note>");
                continue;
            }
            failures += Check($"{r.RecordType}: has >= 1 operation", r.Operations.Count > 0, $"{r.Operations.Count}");
            failures += Check($"{r.RecordType}: has a Primary filter", r.Filters.Any(f => f.Kind == SkyPatcherFilterKind.Primary));
        }

        // 4. shape ⇒ tractability / stateful invariants (catches transcription slips at the source).
        foreach (var r in cat.Records)
            foreach (var op in r.Operations)
            {
                if (op.Shape == SkyPatcherOpShape.Collection && op.Tractability != SkyPatcherTractability.Collection)
                    failures += Check($"{r.RecordType}.{op.Name}: collection ⇒ COLLECTION", false, op.Tractability.ToString());
                if (op.Shape == SkyPatcherOpShape.Mirror && op.Tractability != SkyPatcherTractability.Hard)
                    failures += Check($"{r.RecordType}.{op.Name}: mirror ⇒ HARD", false, op.Tractability.ToString());
                if ((op.Shape == SkyPatcherOpShape.Mult || op.Shape == SkyPatcherOpShape.AddNumeric) && !op.Stateful)
                    failures += Check($"{r.RecordType}.{op.Name}: mult/add_numeric ⇒ stateful", false);
            }

        // 5. classification on a well-known type (weapon) — the four cases the reader depends on.
        var weap = cat.ForSubfolder("weapon");
        failures += Check("weapon subfolder resolves", weap is not null);
        if (weap is not null)
        {
            var dmg = cat.Classify(weap, "attackDamage");
            failures += Check("weapon.attackDamage ⇒ Operation / set / CLEAN",
                dmg is { Role: SkyPatcherKeyRole.Operation, Operation: { Shape: SkyPatcherOpShape.Set, Tractability: SkyPatcherTractability.Clean } },
                $"{dmg.Role}/{dmg.Operation?.Shape}/{dmg.Operation?.Tractability}");

            var excl = cat.Classify(weap, "filterByWeaponsExcluded");
            failures += Check("weapon.filterByWeaponsExcluded ⇒ Filter base=filterByWeapons connective=Excluded",
                excl is { Role: SkyPatcherKeyRole.Filter, BaseKey: "filterByWeapons", Connective: "Excluded" },
                $"{excl.Role} base={excl.BaseKey} conn={excl.Connective}");

            var kw = cat.Classify(weap, "keywordsToAdd");
            failures += Check("weapon.keywordsToAdd ⇒ Operation / collection",
                kw is { Role: SkyPatcherKeyRole.Operation, Operation.Shape: SkyPatcherOpShape.Collection },
                $"{kw.Role}/{kw.Operation?.Shape}");

            var unk = cat.Classify(weap, "totallyBogusKeyXYZ");
            failures += Check("an unknown key ⇒ Unknown (bundled-or-warn, never silently assumed)",
                unk.Role == SkyPatcherKeyRole.Unknown, unk.Role.ToString());

            // The bare form of a filter documented ONLY with a connective (filterByFirstPersonModelOr —
            // no bare spelling exists in the reference) must warn Unknown, not silently pass as a Filter:
            // the bare branch is gated on "" membership in Connectives, same contract as the suffix branch.
            var bareOnly = cat.Classify(weap, "filterByFirstPersonModel");
            failures += Check("bare form of a suffix-only filter ⇒ Unknown (undocumented token warns)",
                bareOnly.Role == SkyPatcherKeyRole.Unknown, bareOnly.Role.ToString());
            var suffixed = cat.Classify(weap, "filterByFirstPersonModelOr");
            failures += Check("…while its documented suffixed form ⇒ Filter base=filterByFirstPersonModel",
                suffixed is { Role: SkyPatcherKeyRole.Filter, BaseKey: "filterByFirstPersonModel", Connective: "Or" },
                $"{suffixed.Role} base={suffixed.BaseKey} conn={suffixed.Connective}");
        }

        // A present-but-wrong-kind 'connectives' (or filters/operations) node must throw at LOAD, not
        // silently parse empty — the record would keep its other checks green while every suffixed key
        // on that filter degraded to Unknown at runtime (the Q3 silent-empty lane the guard can't see).
        try
        {
            SkyPatcherCatalog.LoadFrom("""[{"recordType":"T","sig":"TTTT","subfolder":"t","primaryFilter":"f","filters":[{"name":"f","kind":"primary","connectives":"oops"}]}]""");
            failures += Check("wrong-kind 'connectives' throws loudly at load", false, "no exception");
        }
        catch (InvalidOperationException)
        {
            failures += Check("wrong-kind 'connectives' throws loudly at load", true);
        }

        // 6. flagship HARD ops exist and are HARD — the tiered-honesty reader keys on these.
        failures += CheckHard(cat, "mirrorArmor");
        failures += CheckHard(cat, "changeStats");
        failures += CheckHard(cat, "setRandomVisualStyle");
        failures += CheckHard(cat, "mgefsToAdd");

        // 7. cross-check the record dimension against the skill body's own router table — the catalog's
        //    provenance. This is the drift guard: when the skypatcher-authoring reference updates, a
        //    record added/renamed/re-filtered there must fail HERE until the catalog is re-transcribed.
        failures += CrossCheckRouterTable(cat);

        Console.WriteLine(failures == 0
            ? "[skypatcher-catalog-guard] PASS — the closed SkyPatcher catalog holds."
            : $"[skypatcher-catalog-guard] FAIL — {failures} case(s) regressed.");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Compare recordType/sig/primaryFilter per subfolder against the skypatcher-authoring
    /// skill's own router table in SKILL.md (read from CWD like the other repo-file guards — run from
    /// the repo root), plus exact count parity both ways. A missing skill body is a loud FAIL, not a
    /// skip. The table replaced references/index.jsonl, which the skill rewrite deleted.</summary>
    static int CrossCheckRouterTable(SkyPatcherCatalog cat)
    {
        int failures = 0;
        var path = Path.Combine(".claude", "skills", "skypatcher-authoring", "SKILL.md");
        if (!File.Exists(path))
            return Check($"skill body exists at {path}", false, "wrong CWD? run from the repo root");

        // Each row is guarded to a NAMED fail — the guard's whole purpose is the reference-update
        // event, which is exactly when a row can change shape; a raw exception would skip every
        // remaining cross-check and name nothing (the PluginValidateProbe treatment).
        var entries = new List<(string Name, string[] Sigs, string Subfolder, string[] PrimaryFilters)>();
        bool inTable = false;
        int lineNo = 0;
        foreach (var line in File.ReadAllLines(path))
        {
            lineNo++;
            if (!inTable) { inTable = line.StartsWith("| Record type", StringComparison.Ordinal); continue; }
            if (!line.StartsWith("|", StringComparison.Ordinal)) break;      // past the table
            if (line.StartsWith("|---", StringComparison.Ordinal)) continue; // the separator row

            var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length != 4)
            {
                failures += Check($"router row {lineNo} has four columns", false, line.Trim());
                continue;
            }
            int open = cells[0].IndexOf(" (", StringComparison.Ordinal);
            if (open < 0 || !cells[0].EndsWith(")", StringComparison.Ordinal))
            {
                failures += Check($"router row {lineNo} names the xEdit signature", false, cells[0]);
                continue;
            }
            var sigs = cells[0].Substring(open + 2, cells[0].Length - open - 3)
                               .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var subfolder = Backticked(cells[1]).FirstOrDefault() ?? "";
            entries.Add((cells[0].Substring(0, open).Trim(), sigs, subfolder, Backticked(cells[2])));
        }

        failures += Check($"catalog count == router row count ({cat.Records.Count} vs {entries.Count})",
            cat.Records.Count == entries.Count);

        var rowSubfolders = new HashSet<string>(entries.Select(e => e.Subfolder), StringComparer.OrdinalIgnoreCase);
        foreach (var r in cat.Records)
            if (!rowSubfolders.Contains(r.Subfolder))
                failures += Check($"catalog subfolder '{r.Subfolder}' has a router row", false);

        foreach (var e in entries)
        {
            var r = cat.ForSubfolder(e.Subfolder);
            if (r is null) { failures += Check($"router subfolder '{e.Subfolder}' exists in the catalog", false); continue; }
            failures += Check($"{e.Subfolder}: recordType matches the router row", r.RecordType == e.Name, $"cat='{r.RecordType}' row='{e.Name}'");
            failures += Check($"{e.Subfolder}: sig is among the router row's", e.Sigs.Contains(r.Sig, StringComparer.Ordinal),
                $"cat='{r.Sig}' row='{string.Join(" / ", e.Sigs)}'");
            // Component-wise: every router primaryFilter must appear among the catalog's ' / '-separated
            // parts (and an empty router pf — the OMOD gap — requires an empty catalog pf).
            var parts = r.PrimaryFilter.Split(" / ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            bool pfOk = e.PrimaryFilters.Length == 0
                ? r.PrimaryFilter.Length == 0
                : e.PrimaryFilters.All(p => parts.Contains(p, StringComparer.Ordinal));
            failures += Check($"{e.Subfolder}: primaryFilter covers the router row's", pfOk,
                $"cat='{r.PrimaryFilter}' row='{string.Join(" / ", e.PrimaryFilters)}'");
        }
        return failures;
    }

    /// <summary>The `backticked` tokens of one table cell, in order — a cell's own prose and markers are dropped.</summary>
    static string[] Backticked(string cell)
        => System.Text.RegularExpressions.Regex.Matches(cell, "`([^`]+)`")
               .Select(m => m.Groups[1].Value.Trim())
               .ToArray();

    /// <summary>EVERY record carrying the op must be HARD, not just the first hit — per-record
    /// tractability variance is real in this catalog (alternateTexturesToAdd is HARD on some records,
    /// COLLECTION on others), so a single-hit check would let a later record's regression pass.</summary>
    static int CheckHard(SkyPatcherCatalog cat, string opName)
    {
        var hits = cat.Records
            .SelectMany(r => r.Operations.Where(o => o.Name == opName).Select(o => (r.RecordType, Op: o)))
            .ToList();
        var soft = hits.Where(h => h.Op.Tractability != SkyPatcherTractability.Hard).ToList();
        return Check($"HARD op '{opName}' present ({hits.Count} record(s)) and HARD on ALL of them",
            hits.Count > 0 && soft.Count == 0,
            hits.Count == 0 ? "<not found>" : string.Join(", ", soft.Select(s => $"{s.RecordType}={s.Op.Tractability}")));
    }

    static int Check(string what, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
        if (!ok && detail.Length > 0) Console.WriteLine($"      got: {detail}");
        return ok ? 0 : 1;
    }
}

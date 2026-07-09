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
//  against the skypatcher-authoring reference's index.jsonl — the drift
//  guard for when the skill reference updates. In-process; the catalog is
//  an embedded resource, the index is read from the repo (CWD-relative).
// ======================================================================
public static class SkyPatcherCatalogProbe
{
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
        }

        // 6. flagship HARD ops exist and are HARD — the tiered-honesty reader keys on these.
        failures += CheckHard(cat, "mirrorArmor");
        failures += CheckHard(cat, "changeStats");
        failures += CheckHard(cat, "setRandomVisualStyle");
        failures += CheckHard(cat, "mgefsToAdd");

        // 7. cross-check the record dimension against the reference's own index.jsonl — the catalog's
        //    provenance. This is the drift guard: when the skypatcher-authoring reference updates, a
        //    record added/renamed/re-filtered there must fail HERE until the catalog is re-transcribed.
        //    (LVLI: the catalog deliberately carries the dual primary filter 'filterByLLs / filterByLLNPCs'
        //    where the index carries one — compared component-wise.)
        failures += CrossCheckIndex(cat);

        Console.WriteLine(failures == 0
            ? "[skypatcher-catalog-guard] PASS — the closed SkyPatcher catalog holds."
            : $"[skypatcher-catalog-guard] FAIL — {failures} case(s) regressed.");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Compare recordType/sig/primaryFilter per subfolder against the skypatcher-authoring
    /// reference's index.jsonl (read from CWD like the other repo-file guards — run from the repo root),
    /// plus exact count parity both ways. A missing index file is a loud FAIL, not a skip.</summary>
    static int CrossCheckIndex(SkyPatcherCatalog cat)
    {
        int failures = 0;
        var path = Path.Combine(".claude", "skills", "skypatcher-authoring", "references", "index.jsonl");
        if (!File.Exists(path))
            return Check($"reference index.jsonl exists at {path}", false, "wrong CWD? run from the repo root");

        var entries = new List<(string Name, string Sig, string Subfolder, string PrimaryFilter)>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            var e = doc.RootElement;
            entries.Add((e.GetProperty("name").GetString() ?? "", e.GetProperty("sig").GetString() ?? "",
                         e.GetProperty("subfolder").GetString() ?? "", e.GetProperty("primaryFilter").GetString() ?? ""));
        }

        failures += Check($"catalog count == index count ({cat.Records.Count} vs {entries.Count})",
            cat.Records.Count == entries.Count);

        var indexSubfolders = new HashSet<string>(entries.Select(e => e.Subfolder), StringComparer.OrdinalIgnoreCase);
        foreach (var r in cat.Records)
            if (!indexSubfolders.Contains(r.Subfolder))
                failures += Check($"catalog subfolder '{r.Subfolder}' exists in the index", false);

        foreach (var e in entries)
        {
            var r = cat.ForSubfolder(e.Subfolder);
            if (r is null) { failures += Check($"index subfolder '{e.Subfolder}' exists in the catalog", false); continue; }
            failures += Check($"{e.Subfolder}: recordType matches index name", r.RecordType == e.Name, $"cat='{r.RecordType}' idx='{e.Name}'");
            failures += Check($"{e.Subfolder}: sig matches index", r.Sig == e.Sig, $"cat='{r.Sig}' idx='{e.Sig}'");
            // Component-wise: every index primaryFilter must appear among the catalog's ' / '-separated parts
            // (and an empty index pf — the OMOD gap — requires an empty catalog pf).
            var parts = r.PrimaryFilter.Split(" / ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            bool pfOk = e.PrimaryFilter.Length == 0 ? r.PrimaryFilter.Length == 0 : parts.Contains(e.PrimaryFilter, StringComparer.Ordinal);
            failures += Check($"{e.Subfolder}: primaryFilter covers the index's", pfOk, $"cat='{r.PrimaryFilter}' idx='{e.PrimaryFilter}'");
        }
        return failures;
    }

    static int CheckHard(SkyPatcherCatalog cat, string opName)
    {
        var hit = cat.Records.SelectMany(r => r.Operations).FirstOrDefault(o => o.Name == opName);
        return Check($"HARD op '{opName}' present and tractability=HARD",
            hit is { Tractability: SkyPatcherTractability.Hard }, hit is null ? "<not found>" : hit.Tractability.ToString());
    }

    static int Check(string what, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
        if (!ok && detail.Length > 0) Console.WriteLine($"      got: {detail}");
        return ok ? 0 : 1;
    }
}

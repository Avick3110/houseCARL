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
//  unknown key as Unknown (bundled-or-warn), and preserves the OMOD gap.
//  Pure in-process — the catalog is an embedded resource; no game data.
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

        Console.WriteLine(failures == 0
            ? "[skypatcher-catalog-guard] PASS — the closed SkyPatcher catalog holds."
            : $"[skypatcher-catalog-guard] FAIL — {failures} case(s) regressed.");
        return failures == 0 ? 0 : 1;
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

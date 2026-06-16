using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD (standing CI instrument) for HCBR-2026-06-15-01 item 1.2 (PR-B) — the SameShape over-reject.
///
/// THE GAP: when a field exists on several ARMS of a polymorphic base, <see cref="CorpusRulebook"/>'s FindField
/// admits the path only if those arms AGREE in shape (<c>SameShape</c>) — one shape validates for whichever arm the
/// live element turns out to be. SameShape compared the raw <c>Nullable</c> flag and three raw assembly-qualified
/// CLR-type strings, so two arms that are WRITE-LEGAL-IDENTICAL but differ only by the <c>Nullable&lt;T&gt;</c>
/// wrapper over-rejected with "CONFLICTING shapes" — even though <see cref="WriteEngine"/>.Coerce unwraps
/// <c>Nullable&lt;T&gt;</c> and admits the identical value set on either arm. Re-walking the corpus, EXACTLY ONE
/// field is affected: <c>APerkEffect.Value</c> — <c>float</c> on <c>PerkEntryPointModifyActorValue</c>, <c>float?</c>
/// on <c>PerkEntryPointModifyValue</c>/<c>…ModifyValues</c>.
///
/// THE FIX (by construction): SameShape now compares the AQ types after unwrapping <c>Nullable&lt;T&gt;</c>
/// (mirroring the engine's own Coerce/CanCoerce) and drops the raw Nullable-bool equality; an AQ name that won't
/// resolve to a runtime Type falls back to raw-string equality, so a genuinely unknown type is never silently
/// widened (Q3). Every GENUINE difference — cardinality, display type, or a different underlying CLR type — still
/// rejects.
///
/// RED→GREEN: check A (the one over-reject — <c>APerkEffect.Value</c> reached through <c>Perk.Effects[0].Value</c>)
/// is RED before the fix ("CONFLICTING shapes") and GREEN after. The genuine-conflict guards are GREEN before AND
/// after, proving the loosen is NARROW on BOTH axes the AQ check defends: C = <c>Condition.ComparisonValue</c>
/// (formlink vs scalar — the CARDINALITY axis), D = <c>APackageData.Data</c> (bool vs uint vs float — the
/// underlying-TYPE axis); each must still reject AT the SameShape gate (asserted on the "CONFLICTING shapes"
/// message, not just any refusal — no green-for-the-wrong-reason). Apply-1 drives the SAME request A through
/// <see cref="WriteEngine"/>.ApplyVerb on an in-memory Perk carrying a live arm, locking the invariant "pre-flight
/// admits exactly what the runtime coerces" (GREEN before and after — apply was never the gate; pre-flight was).
///
/// Self-contained: the corpus checks use the GENERATED corpus.json (built into a unique temp dir on a fresh
/// checkout, exactly as <c>poly-field-descend-guard</c> does); the apply check is pure in-memory Mutagen — no
/// plugin file, no Skyrim.esm.
///
/// Run: <c>dotnet run --project src/housecarl-generator sameshape-agree-guard</c>
/// </summary>
public static class SameShapeAgreeProbe
{
    public static int RunGuard(string[] args)
    {
        // CI-safe: corpus.json is GENERATED, not tracked — on a fresh checkout (the CI runner) build it into a
        // UNIQUE temp dir (no cross-run sharing/races) and point the rulebook there, leaving the working tree
        // untouched; cleaned up on exit. A repo with generated/ already present (local dev) is used as-is.
        string? tmp = null;
        if (!File.Exists(CorpusRulebook.CorpusPath))
        {
            tmp = Path.Combine(Path.GetTempPath(), "housecarl-sameshape-agree-guard-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            Console.WriteLine($"corpus.json absent — generating into {tmp} (CI / fresh checkout)…");
            var rc = CorpusGenerator.GenerateAll(Path.Combine(tmp, "generated"), Path.Combine(tmp, "refs"));
            if (rc != 0) { Console.Error.WriteLine("error: corpus generation failed"); return rc; }
            CorpusRulebook.CorpusPath = Path.Combine(tmp, "generated", "corpus.json");
        }
        try { return RunChecks(); }
        finally { if (tmp is not null) { try { Directory.Delete(tmp, recursive: true); } catch { /* best-effort */ } } }
    }

    static int RunChecks()
    {
        var rb = CorpusRulebook.Load();
        int failures = 0;

        void Check(string label, bool ok, string? detail = null)
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}{(ok || detail is null ? "" : $"\n        -> {detail}")}");
            if (!ok) failures++;
        }

        Console.WriteLine("sameshape-agree-guard — SameShape write-legality equivalence (HCBR 1.2 / PR-B)");
        Console.WriteLine();

        // ============================================================================================
        // PART 1 — pre-flight: the ONE over-reject now agrees; both axes of genuine conflict stay rejected.
        // ============================================================================================

        // ---- A: THE over-reject — APerkEffect.Value lives on three arms, all scalar float, differing ONLY on
        //         Nullable (float vs float?). Reached through Perk.Effects[0] (the list element type is the base
        //         APerkEffect, so FindField's over-arms loop is entered). RED today = "CONFLICTING shapes". ----
        var a = new WriteRequest
        {
            RecordType = "Perk",
            Path = new[] { "Effects[0]", "Value" },
            Verb = "Set", Value = "5",
        };
        var aErr = rb.Validate(a);
        Check("A: Set Perk.Effects[0].Value (float vs float? across arms — write-legal-identical) passes pre-flight",
            aErr is null, aErr);

        // ---- C: GENUINE conflict on the CARDINALITY axis — Condition.ComparisonValue is a formlink on
        //         ConditionGlobal and a scalar float on ConditionFloat. Reached through MagicEffect.Conditions[0].
        //         Stays rejected (the loosen never touches Cardinality), and rejects AT the SameShape gate. ----
        var c = new WriteRequest
        {
            RecordType = "MagicEffect",
            Path = new[] { "Conditions[0]", "ComparisonValue" },
            Verb = "Set", Value = "000800:Skyrim.esm",
        };
        var cErr = rb.Validate(c);
        Check("C: Condition.ComparisonValue (formlink vs scalar — cardinality conflict) stays rejected at the SameShape gate",
            cErr is not null && cErr.Contains("CONFLICTING shapes", StringComparison.Ordinal), cErr);

        // ---- D: GENUINE conflict on the underlying-TYPE axis — APackageData.Data is scalar bool / uint / float
        //         across arms (SAME cardinality + display facets differ only by the real CLR type). Reached through
        //         Package.Data[0] (dict of the poly base). Proves the AQ comparison still distinguishes
        //         Boolean/UInt32/Single AFTER the Nullable-unwrap — the loosen did NOT collapse different scalars. ----
        var d = new WriteRequest
        {
            RecordType = "Package",
            Path = new[] { "Data[0]", "Data" },
            Verb = "Set", Value = "1",
        };
        var dErr = rb.Validate(d);
        Check("D: APackageData.Data (bool vs uint vs float — underlying-type conflict) stays rejected at the SameShape gate",
            dErr is not null && dErr.Contains("CONFLICTING shapes", StringComparison.Ordinal), dErr);

        // ============================================================================================
        // PART 2 — apply: the same request A the loosen now admits actually applies through the engine, asserted
        // IN CI on an in-memory Perk with a live nullable-float arm — "pre-flight admits exactly what apply does".
        // (GREEN before and after the fix: apply already coerced "5" onto the live arm; pre-flight was the gate.)
        // ============================================================================================

        var perk = new SkyrimMod(new ModKey("hc_sameshape", ModType.Plugin), SkyrimRelease.SkyrimSE).Perks.AddNew();
        perk.Effects.Add(new PerkEntryPointModifyValue());   // a live arm carrying the nullable-float Value
        WriteEngine.ApplyVerb(perk, a);
        Check("Apply-1: ApplyVerb resolves Effects[0] to the live PerkEntryPointModifyValue arm and sets Value=5 (in-memory, in CI)",
            perk.Effects[0] is PerkEntryPointModifyValue { Value: 5f },
            perk.Effects[0] is PerkEntryPointModifyValue pe
                ? $"Value={pe.Value?.ToString() ?? "null"}"
                : $"arm is {perk.Effects[0].GetType().Name}");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "sameshape-agree-guard: ALL PASS" : $"sameshape-agree-guard: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }
}

using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for the EFFECT-CHAIN RESOLVER (housecarl_effect_chain — gap 2026-06-08: "given
/// an MGEF, what SPEL/ENCH/ALCH apply it, at what magnitude?"). Drives the REAL product path
/// (<see cref="EffectChain.Resolve"/> — what housecarl_effect_chain calls through the thin service wrapper) against a
/// SYNTHESIZED 1-plugin order in TEMP — NO Skyrim.esm, so it runs in CI.
///
/// THE GAP (reproduced by construction): there was no one call from a MagicEffect to "applied by these records, at
/// these magnitudes" — you re-ran cross_plugin_query references=&lt;MGEF&gt; for SPEL, then ENCH, ALCH, SCRL, INGR, and
/// hand-read each hit's matching Effects[].Data. Resolve collapses that into one verb.
///
/// THE FIX (generic by construction): the five effect-bearing records share ONE element type (IEffectGetter), so
/// EffectsOf() reaches the list with a five-arm switch and Match() extracts the matching entries uniformly — every
/// effect-bearing record, no per-type magnitude wiring.
///
/// FIXTURE (one master; distinct magnitude per carrier is the by-construction discriminator — the read-back mag proves
/// WHICH entry landed): MGEFs Target(T)/Other(U)/Unused(V); one carrier of each type applying T — Spell(11), ENCH(22),
/// ALCH(33), Scroll(44), INGR(55); a multi-entry Spell [U@100, T@66, T@77] (T at index 1 AND 2); a non-match Spell
/// [U@88]; a null-base Spell [unset@99]; and a Weapon (the non-MGEF Q3 fixture).
///
/// Arms (ALL required — a GREEN must mean "the contract holds"):
///   GATE-OK        — Resolve(T) succeeds and the header carries T's editorid (the typed-match proof).
///   MATCH-ALL-FIVE — SPEL/ENCH/ALCH/SCRL/INGR each return once with the authored magnitude + the right catalog type.
///   MULTI-ENTRY    — the multi Spell yields one row PER matching entry (mag 66 @1/3, mag 77 @2/3); its U-entry @0 is excluded.
///   TOTAL          — exactly 7 rows (5 single + 2 multi), not capped.
///   NON-MATCH      — a carrier of a DIFFERENT MGEF (and the multi's U-entry) is never returned.
///   NULL-BASE      — an effect with an unset BaseEffect is skipped — not matched, no throw, no scan note.
///   TYPES-NARROW   — scope=[Spell] returns only spell carriers (S1 + multi×2 = 3); ENCH/ALCH/SCRL/INGR excluded.
///   PURE           — Match/EffectsOf on the in-MEMORY bodies (the pure seam, independent of the scan): list for a
///                    Spell, null for a Weapon; the single hit on the spell, empty on the weapon.
///   Q3-NONMGEF     — a WEAP FormID fails LOUD ("not a MagicEffect"), no rows (the headline typed-mismatch case).
///   Q3-ABSENT      — a FormID no plugin defines fails LOUD ("not in the load order"), no rows.
///   Q3-UNUSED      — a valid-but-unreferenced MGEF returns a CLEAN zero (Success, 0 rows, no error, eid in header) —
///                    distinguishable from the bad-id errors above, so "0 carriers" is never a silent wrong answer.
///   CAP            — limit=3 returns 3 rows but reports the TRUE total (7) and Capped (the explicit-overrun teeth).
///
/// COVERAGE NOTE (Q3 — name what this guard LEANS ON rather than re-proves; PR #107 review): the fixture is ONE
/// plugin, so the winner-only scan (WinnerRecordsOfType) and the per-row winner= are exercised only at depth 0. Both
/// are the SAME shared primitive cross_plugin_query uses, multi-plugin-proven in source-display-guard +
/// snapshot-view-guard — so override/winner behavior is covered transitively, not re-proven here. The MATCH-ALL-FIVE
/// arm is the canary for a sixth effect-bearing record (it would simply not be scanned).
///
/// Run: dotnet run --project src/housecarl-generator -- effect-chain-guard
/// </summary>
public static class EffectChainProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("effect-chain-guard — resolve a MagicEffect's carriers + magnitudes (housecarl_effect_chain, gap 2026-06-08)");
        Console.WriteLine();

        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-effect-chain-guard");
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        Directory.CreateDirectory(tmpDir);
        try { return RunChecks(tmpDir); }
        finally { try { Directory.Delete(tmpDir, recursive: true); } catch { /* best-effort */ } }
    }

    static int RunChecks(string tmpDir)
    {
        int failures = 0;
        void Check(string label, bool ok, string? detail = null)
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}{(ok || detail is null ? "" : $"\n        -> {detail}")}");
            if (!ok) failures++;
        }

        // ---- synthesize the 1-plugin order (see class doc). ----
        string mPath = Path.Combine(tmpDir, "HcEcMaster.esm");
        FormKey tFk, uFk, vFk, s1Fk, e1Fk, a1Fk, c1Fk, i1Fk, s2Fk, s3Fk, s4Fk, wFk;
        ISpellGetter s1Mem, s4Mem; IWeaponGetter wMem;   // in-memory bodies for the PURE arm
        try
        {
            var m = new SkyrimMod(new ModKey("HcEcMaster", ModType.Master), SkyrimRelease.SkyrimSE);

            var t = m.MagicEffects.AddNew(); t.EditorID = "HcEcTarget";
            var u = m.MagicEffects.AddNew(); u.EditorID = "HcEcOther";
            var v = m.MagicEffects.AddNew(); v.EditorID = "HcEcUnused";
            tFk = t.FormKey; uFk = u.FormKey; vFk = v.FormKey;

            // distinct (mag, area, dur) per carrier — all three differ WITHIN a row, so an Area/Duration/Magnitude
            // field transposition in the extraction is caught, not just a magnitude bug (PR #107 review).
            var s1 = m.Spells.AddNew();      s1.EditorID = "HcEcSpell1";  s1.Effects.Add(Eff(t.FormKey, 11f, 1, 101));
            var e1 = m.ObjectEffects.AddNew(); e1.EditorID = "HcEcEnch1"; e1.Effects.Add(Eff(t.FormKey, 22f, 2, 102));
            var a1 = m.Ingestibles.AddNew(); a1.EditorID = "HcEcAlch1";   a1.Effects.Add(Eff(t.FormKey, 33f, 3, 103));
            var c1 = m.Scrolls.AddNew();     c1.EditorID = "HcEcScroll1"; c1.Effects.Add(Eff(t.FormKey, 44f, 4, 104));
            var i1 = m.Ingredients.AddNew(); i1.EditorID = "HcEcIngr1";   i1.Effects.Add(Eff(t.FormKey, 55f, 5, 105));
            s1Fk = s1.FormKey; e1Fk = e1.FormKey; a1Fk = a1.FormKey; c1Fk = c1.FormKey; i1Fk = i1.FormKey;

            // multi-entry: T at index 1 AND 2; the U-entry at index 0 must NOT match.
            var s2 = m.Spells.AddNew(); s2.EditorID = "HcEcMulti";
            s2.Effects.Add(Eff(u.FormKey, 100f));
            s2.Effects.Add(Eff(t.FormKey, 66f));
            s2.Effects.Add(Eff(t.FormKey, 77f));
            s2Fk = s2.FormKey;

            var s3 = m.Spells.AddNew(); s3.EditorID = "HcEcNonMatch"; s3.Effects.Add(Eff(u.FormKey, 88f));   // a DIFFERENT MGEF
            var s4 = m.Spells.AddNew(); s4.EditorID = "HcEcNullBase"; s4.Effects.Add(Eff(null, 99f));        // unset BaseEffect
            s3Fk = s3.FormKey; s4Fk = s4.FormKey;

            var w = m.Weapons.AddNew(); w.EditorID = "HcEcWeap"; w.BasicStats = new WeaponBasicStats { Damage = 10 };
            wFk = w.FormKey;

            s1Mem = s1; s4Mem = s4; wMem = w;   // capture before write (the write does not invalidate them)

            m.BeginWrite.ToPath(mPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: could not synthesize fixtures: {ex.GetType().Name}: {(ex.InnerException ?? ex).Message}");
            return 1;
        }

        using var r = LoadOrderResolver.Build(new[] { mPath });

        // ---- the main resolve over all five types. ----
        var all = EffectChain.Resolve(r, tFk, EffectChain.CarrierTypes, 500);

        Check("GATE-OK: Resolve(target) succeeds and the header carries the MGEF's editorid (typed-match proof)",
            all.Success && all.MgefEditorId == "HcEcTarget",
            $"success={all.Success} eid={all.MgefEditorId} err=[{Trim(all.Error)}]");

        bool five =
            RowMag(all, s1Fk, 0) == 11f && RowArea(all, s1Fk, 0) == 1 && RowDur(all, s1Fk, 0) == 101 && RowCount(all, s1Fk, 0) == 1 && RowType(all, s1Fk) == "Spell" &&
            RowMag(all, e1Fk, 0) == 22f && RowArea(all, e1Fk, 0) == 2 && RowDur(all, e1Fk, 0) == 102 && RowType(all, e1Fk) == "ObjectEffect" &&
            RowMag(all, a1Fk, 0) == 33f && RowArea(all, a1Fk, 0) == 3 && RowDur(all, a1Fk, 0) == 103 && RowType(all, a1Fk) == "Ingestible" &&
            RowMag(all, c1Fk, 0) == 44f && RowArea(all, c1Fk, 0) == 4 && RowDur(all, c1Fk, 0) == 104 && RowType(all, c1Fk) == "Scroll" &&
            RowMag(all, i1Fk, 0) == 55f && RowArea(all, i1Fk, 0) == 5 && RowDur(all, i1Fk, 0) == 105 && RowType(all, i1Fk) == "Ingredient";
        Check("MATCH-ALL-FIVE: SPEL/ENCH/ALCH/SCRL/INGR each return the authored magnitude/area/duration (distinct, so a field transposition is caught) + catalog type", five,
            $"S1={RowMag(all, s1Fk, 0)}/{RowType(all, s1Fk)} E1={RowMag(all, e1Fk, 0)}/{RowType(all, e1Fk)} A1={RowMag(all, a1Fk, 0)}/{RowType(all, a1Fk)} C1={RowMag(all, c1Fk, 0)}/{RowType(all, c1Fk)} I1={RowMag(all, i1Fk, 0)}/{RowType(all, i1Fk)}");

        bool multi =
            RowMag(all, s2Fk, 1) == 66f && RowCount(all, s2Fk, 1) == 3 &&
            RowMag(all, s2Fk, 2) == 77f && RowCount(all, s2Fk, 2) == 3 &&
            all.Rows.Count(x => x.Carrier == s2Fk) == 2;
        Check("MULTI-ENTRY: a carrier applying the MGEF twice yields one row per entry (66 @1/3, 77 @2/3); its U-entry @0 excluded", multi,
            $"rows@s2={all.Rows.Count(x => x.Carrier == s2Fk)} mag@1={RowMag(all, s2Fk, 1)} mag@2={RowMag(all, s2Fk, 2)} cnt@1={RowCount(all, s2Fk, 1)}");

        Check("TOTAL: exactly 7 carrier rows (5 single + 2 multi), not capped",
            all.Total == 7 && all.Rows.Count == 7 && !all.Capped,
            $"total={all.Total} rows={all.Rows.Count} capped={all.Capped}");

        bool nonMatch = !all.Rows.Any(x => x.Carrier == s3Fk) && !all.Rows.Any(x => x.Magnitude == 88f || x.Magnitude == 100f);
        Check("NON-MATCH: a carrier of a DIFFERENT MGEF (and the multi's U-entry) is never returned", nonMatch,
            $"s3 present={all.Rows.Any(x => x.Carrier == s3Fk)} stray-mags={all.Rows.Any(x => x.Magnitude == 88f || x.Magnitude == 100f)}");

        bool nullBase = !all.Rows.Any(x => x.Carrier == s4Fk) && all.ScanNote is null && all.Success;
        Check("NULL-BASE: an effect with an unset BaseEffect is skipped (not matched, no throw / no scan note)", nullBase,
            $"s4 present={all.Rows.Any(x => x.Carrier == s4Fk)} scanNote=[{Trim(all.ScanNote)}]");

        // ---- TYPES-NARROW: scope=[Spell] only. ----
        var spellsOnly = EffectChain.Resolve(r, tFk, new[] { typeof(ISpellGetter) }, 500);
        bool narrow = spellsOnly.Success && spellsOnly.Total == 3 &&
            spellsOnly.Rows.All(x => x.Type == "Spell") &&
            spellsOnly.Rows.Any(x => x.Carrier == s1Fk) && spellsOnly.Rows.Count(x => x.Carrier == s2Fk) == 2 &&
            !spellsOnly.Rows.Any(x => x.Carrier == e1Fk);
        Check("TYPES-NARROW: scope=[Spell] returns only spell carriers (S1 + multi×2 = 3); ENCH/ALCH/SCRL/INGR excluded", narrow,
            $"total={spellsOnly.Total} allSpell={spellsOnly.Rows.All(x => x.Type == "Spell")} e1present={spellsOnly.Rows.Any(x => x.Carrier == e1Fk)}");

        // ---- PURE: Match / EffectsOf on the in-MEMORY bodies (independent of the resolver scan). ----
        bool pureEffectsOf = EffectChain.EffectsOf(s1Mem)?.Count == 1 && EffectChain.EffectsOf(wMem) is null;
        Check("PURE: EffectsOf returns the list for a Spell, null for a Weapon", pureEffectsOf,
            $"spell={EffectChain.EffectsOf(s1Mem)?.Count} weapon={(EffectChain.EffectsOf(wMem) is null ? "null" : "non-null")}");
        var s1Hits = EffectChain.Match(s1Mem, tFk);
        bool pureMatch = s1Hits.Count == 1 && s1Hits[0].Magnitude == 11f && s1Hits[0].Index == 0
                         && EffectChain.Match(wMem, tFk).Count == 0 && EffectChain.Match(s4Mem, tFk).Count == 0;
        Check("PURE: Match returns the single hit (mag 11 @0) on the spell, empty on the weapon + the null-base spell", pureMatch,
            $"s1Hits={s1Hits.Count} mag={(s1Hits.Count > 0 ? s1Hits[0].Magnitude : -1)} weapHits={EffectChain.Match(wMem, tFk).Count} nullHits={EffectChain.Match(s4Mem, tFk).Count}");

        // ---- Q3 teeth (the gap's explicit ask — never a silent wrong answer). ----
        var wRes = EffectChain.Resolve(r, wFk, EffectChain.CarrierTypes, 500);
        Check("Q3-NONMGEF: a WEAP FormID fails LOUD ('not a MagicEffect'), no rows",
            !wRes.Success && wRes.Rows.Count == 0 && wRes.Error is not null && wRes.Error.Contains("not a MagicEffect", StringComparison.Ordinal),
            $"success={wRes.Success} rows={wRes.Rows.Count} err=[{Trim(wRes.Error)}]");

        var absentFk = FormKey.Factory("ABCDEF:HcEcNotReal.esp");
        var absRes = EffectChain.Resolve(r, absentFk, EffectChain.CarrierTypes, 500);
        Check("Q3-ABSENT: a FormID no plugin defines fails LOUD ('no record … in the load order'), no rows",
            !absRes.Success && absRes.Rows.Count == 0 && absRes.Error is not null && absRes.Error.Contains("in the load order", StringComparison.Ordinal),
            $"success={absRes.Success} rows={absRes.Rows.Count} err=[{Trim(absRes.Error)}]");

        var unusedRes = EffectChain.Resolve(r, vFk, EffectChain.CarrierTypes, 500);
        Check("Q3-UNUSED: a valid-but-unreferenced MGEF returns a CLEAN zero (Success, 0 rows, no error, eid in header)",
            unusedRes.Success && unusedRes.Total == 0 && unusedRes.Rows.Count == 0 && unusedRes.Error is null && unusedRes.MgefEditorId == "HcEcUnused",
            $"success={unusedRes.Success} total={unusedRes.Total} err=[{Trim(unusedRes.Error)}] eid={unusedRes.MgefEditorId}");

        // ---- CAP: limit=3 over the 7-row set. ----
        var capped = EffectChain.Resolve(r, tFk, EffectChain.CarrierTypes, 3);
        Check("CAP: limit=3 returns 3 rows but reports the TRUE total (7) and Capped",
            capped.Rows.Count == 3 && capped.Total == 7 && capped.Capped,
            $"rows={capped.Rows.Count} total={capped.Total} capped={capped.Capped}");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "effect-chain-guard: ALL PASS" : $"effect-chain-guard: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    // ---- helpers ----

    static Effect Eff(FormKey? baseEffect, float magnitude, int area = 0, int duration = 0)
    {
        var e = new Effect();
        if (baseEffect is { } bf) e.BaseEffect.SetTo(bf);     // unset (null) leaves BaseEffect at FormKey.Null — the null-base fixture
        e.Data = new EffectData { Magnitude = magnitude, Area = area, Duration = duration };
        return e;
    }

    static float? RowMag(EffectChainResult res, FormKey carrier, int effIndex) =>
        res.Rows.FirstOrDefault(x => x.Carrier == carrier && x.EffectIndex == effIndex)?.Magnitude;

    static int? RowArea(EffectChainResult res, FormKey carrier, int effIndex) =>
        res.Rows.FirstOrDefault(x => x.Carrier == carrier && x.EffectIndex == effIndex)?.Area;

    static int? RowDur(EffectChainResult res, FormKey carrier, int effIndex) =>
        res.Rows.FirstOrDefault(x => x.Carrier == carrier && x.EffectIndex == effIndex)?.Duration;

    static int? RowCount(EffectChainResult res, FormKey carrier, int effIndex) =>
        res.Rows.FirstOrDefault(x => x.Carrier == carrier && x.EffectIndex == effIndex)?.EffectCount;

    static string? RowType(EffectChainResult res, FormKey carrier) =>
        res.Rows.FirstOrDefault(x => x.Carrier == carrier)?.Type;

    static string Trim(string? s) => s is null ? "" : (s.Length <= 160 ? s.Replace("\n", " ") : s[..160].Replace("\n", " ") + "…");
}

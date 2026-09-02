using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD (standing CI instrument) for HCBR-2026-06-15-01 items 1.1 + 1.3 (PR-A) — the
/// standalone-polymorphic-field descend gap.
///
/// THE GAP: the pre-flight path-walker (<see cref="CorpusRulebook"/>'s <c>segKey is null</c> plain-hop block)
/// descended ONLY a <c>substruct</c>. A standalone <b>polymorphic</b> field — <c>NpcConfiguration.Level</c>
/// (base <c>ANpcLevel</c>), <c>Npc.Sound</c> (base <c>ANpcSoundDefinition</c>), <c>DialogResponsesAdapter.ScriptFragments</c>
/// — hit the hard reject "it is a polymorphic, not a substruct" and could never be reached. PR #2's <c>[N]</c>
/// traversal covers a polymorphic arm only as a LIST ELEMENT (<c>Properties[0].Object</c>), never as a standalone
/// field. THE FIX (by construction): the plain-hop block gained an <c>else-if</c> that, for a polymorphic field,
/// descends to its polymorphic-BASE catalog entry (<c>field.TypeRef</c>); <see cref="CorpusRulebook"/>'s existing
/// over-arms search then resolves the next hop against the base's arms — the standalone twin of the list-element
/// branch (#35), keyed on cardinality, no per-type wiring (cornerstone strengthened).
///
/// RED→GREEN: checks A/B/C/M and D2 are RED before the fix (the descend is hard-rejected, so pre-flight returns
/// the "not a substruct" refusal; D's rejection lacks the over-arms naming) and GREEN after. The NEGATIVE CONTROL
/// (N) is GREEN before AND after — it proves the fix is NARROW (a scalar plain-hop still rejects; the accept-path
/// was added only for the polymorphic cardinality, never broadened).
///
/// THE APPLY MUST-FIX (critic-mandated, runs IN CI — not behind <c>--source</c>): the pre-flight admitting the
/// path is only half the contract. The generic record <c>ApplyVerb</c> plain-hop (<c>p.GetValue</c> through a live
/// poly-arm value) was the *real*, previously-unreachable apply path; a probe that proved only pre-flight would go
/// green in CI even if apply were broken. So Apply-1/Apply-2 synthesize an NPC IN MEMORY with a live <c>NpcLevel</c>
/// / <c>PcLevelMult</c> arm and drive the SAME <see cref="WriteEngine.ApplyVerb"/> requests pre-flight just
/// accepted, asserting the runtime arm type + the set leaf — locking the invariant "pre-flight admits exactly what
/// the runtime can do." (These two are GREEN before and after the fix by design: the apply CODE already handled
/// the descend; what was broken was only that pre-flight rejected the path before apply ever ran.)
///
/// Self-contained: every check runs on CI. The corpus checks use the GENERATED corpus.json (built into a unique
/// temp dir on a fresh checkout, exactly as <c>vmad-poly-guard</c> does); the apply checks are pure in-memory
/// Mutagen — no plugin file, no Skyrim.esm.
///
/// Run: <c>dotnet run --project src/housecarl-generator poly-field-descend-guard</c>
/// </summary>
public static class PolyFieldDescendProbe
{
    [CiProbe("poly-field-descend-guard")]
    public static int RunGuard(string[] args)
    {
        // CI-safe: corpus.json is GENERATED, not tracked — on a fresh checkout (the CI runner) build it into a
        // UNIQUE temp dir (no cross-run sharing/races) and point the rulebook there, leaving the working tree
        // untouched; cleaned up on exit. A repo with generated/ already present (local dev) is used as-is.
        string? tmp = null;
        if (!File.Exists(CorpusRulebook.CorpusPath))
        {
            tmp = Path.Combine(Path.GetTempPath(), "housecarl-poly-field-descend-guard-" + Guid.NewGuid().ToString("N"));
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

        Console.WriteLine("poly-field-descend-guard — standalone-polymorphic-field descend (HCBR 1.1 + 1.3 / PR-A)");
        Console.WriteLine();

        // ============================================================================================
        // PART 1 — pre-flight: the standalone-poly descend now resolves (corpus-only, CI-safe).
        // Each request is reused as the apply input below, so "pre-flight admits exactly what apply does"
        // is proven with the literal same WriteRequest, not a paraphrase.
        // ============================================================================================

        // ---- A: THE headline — Npc.Configuration.Level.Level descends a substruct, THEN a standalone poly
        //         field (ANpcLevel), and resolves the leaf over the arms (Level lives only on NpcLevel). RED
        //         today = "Cannot descend through 'Level' … it is a polymorphic, not a substruct". ----
        var a = new WriteRequest
        {
            RecordType = "Npc",
            Path = new[] { "Configuration", "Level", "Level" },
            Verb = "Set", Value = "5",
        };
        var aErr = rb.Validate(a);
        Check("A: Set Npc.Configuration.Level.Level (substruct → standalone-poly → NpcLevel arm) passes pre-flight",
            aErr is null, aErr);

        // ---- B: the DISJOINT arm — the same descend reaches PcLevelMult.LevelMult, proving both arms of the
        //         base are reachable through the one cardinality-keyed branch (no per-arm wiring). ----
        var b = new WriteRequest
        {
            RecordType = "Npc",
            Path = new[] { "Configuration", "Level", "LevelMult" },
            Verb = "Set", Value = "1.5",
        };
        var bErr = rb.Validate(b);
        Check("B: …Level.LevelMult reaches the disjoint PcLevelMult arm", bErr is null, bErr);

        // ---- C: GENERALITY beyond ANpcLevel — a DIFFERENT poly base (ANpcSoundDefinition), descended at the
        //         RECORD ROOT (no substruct in front of it), resolving an arm-only formlink leaf. ----
        var c = new WriteRequest
        {
            RecordType = "Npc",
            Path = new[] { "Sound", "InheritsSoundsFrom" },
            Verb = "Set", Value = "000800:Skyrim.esm",
        };
        var cErr = rb.Validate(c);
        Check("C: Npc.Sound.InheritsSoundsFrom (a different poly base, descended at the record root) passes pre-flight",
            cErr is null, cErr);

        // ---- M: MULTI-HOP — substruct → poly (the new branch) → substruct → leaf. The poly-descend is NOT the
        //         last intermediate hop, so this proves the walker keeps iterating PAST the new branch (the new
        //         branch sets `current` and falls through, exactly like the substruct branch). ScriptFragments'
        //         OnBegin is a base-direct substruct on the poly base. ----
        var m = new WriteRequest
        {
            RecordType = "DialogResponses",
            Path = new[] { "VirtualMachineAdapter", "ScriptFragments", "OnBegin", "FragmentName" },
            Verb = "Set", Value = "Frag",
        };
        var mErr = rb.Validate(m);
        Check("M: …VirtualMachineAdapter.ScriptFragments.OnBegin.FragmentName (substruct→poly→substruct→leaf) passes pre-flight",
            mErr is null, mErr);

        // ---- D: Q3 teeth — a field on NO arm of the descended base still rejects LOUD, and the message names
        //         the searched arms (proof we DESCENDED into the base, vs the old flat "not a substruct"). ----
        var d = new WriteRequest
        {
            RecordType = "Npc",
            Path = new[] { "Configuration", "Level", "Bogus" },
            Verb = "Set", Value = "1",
        };
        var dErr = rb.Validate(d);
        Check("D: a field on no arm of the descended base still rejects", dErr is not null);
        Check("D2: …and the rejection names the searched arms (descended into the base, not 'not a substruct')",
            dErr is not null
                && dErr.Contains("arm", StringComparison.OrdinalIgnoreCase)
                && !dErr.Contains("not a substruct", StringComparison.OrdinalIgnoreCase),
            dErr);

        // ---- N: NEGATIVE CONTROL — descending through a SCALAR still rejects (the accept-path was added for
        //         the polymorphic cardinality ONLY; it did not broaden to "anything with a TypeRef"). GREEN
        //         before AND after the fix. ----
        var n = new WriteRequest
        {
            RecordType = "Npc",
            Path = new[] { "Configuration", "BleedoutOverride", "x" },
            Verb = "Set", Value = "1",
        };
        var nErr = rb.Validate(n);
        Check("N: descending through a scalar still rejects (the fix did not over-broaden)",
            nErr is not null && nErr.Contains("scalar", StringComparison.OrdinalIgnoreCase), nErr);

        // ============================================================================================
        // PART 2 — apply: the descend-through-a-live-arm apply path actually works, asserted IN CI on an
        // in-memory NPC (the critic must-fix). Without this, the probe could go green even if apply broke.
        // ============================================================================================

        // ---- Apply-1: a live NpcLevel arm — drive the SAME request A pre-flight just accepted; assert the
        //               runtime arm is NpcLevel and the leaf landed. ----
        var npc1 = new SkyrimMod(new ModKey("hc_polydescend", ModType.Plugin), SkyrimRelease.SkyrimSE).Npcs.AddNew();
        npc1.Configuration.Level = new NpcLevel { Level = 1 };
        WriteEngine.ApplyVerb(npc1, a);
        Check("Apply-1: ApplyVerb descends through the live NpcLevel arm and sets Level=5 (in-memory, in CI)",
            npc1.Configuration.Level is NpcLevel { Level: 5 },
            (npc1.Configuration.Level as NpcLevel)?.Level.ToString()
                ?? $"arm is {npc1.Configuration.Level?.GetType().Name ?? "null"}");

        // ---- Apply-2: the disjoint arm applies too — a live PcLevelMult arm, request B; assert PcLevelMult +
        //               the float leaf. (1.5 is exactly representable, so the constant pattern is exact.) ----
        var npc2 = new SkyrimMod(new ModKey("hc_polydescend2", ModType.Plugin), SkyrimRelease.SkyrimSE).Npcs.AddNew();
        npc2.Configuration.Level = new PcLevelMult { LevelMult = 0f };
        WriteEngine.ApplyVerb(npc2, b);
        Check("Apply-2: ApplyVerb descends through the live PcLevelMult arm and sets LevelMult=1.5 (in-memory, in CI)",
            npc2.Configuration.Level is PcLevelMult { LevelMult: 1.5f },
            (npc2.Configuration.Level as PcLevelMult)?.LevelMult.ToString()
                ?? $"arm is {npc2.Configuration.Level?.GetType().Name ?? "null"}");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "poly-field-descend-guard: ALL PASS" : $"poly-field-descend-guard: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }
}

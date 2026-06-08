using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD (standing CI instrument) for the field-value query predicate (cross_plugin_query where=).
///
/// Self-contained, in the pattern of <c>depth-leak-guard</c> / <c>pkcu-regression</c>: synthesizes records IN
/// MEMORY with KNOWN field values, runs the product evaluator (<see cref="FieldPredicateSet"/>), and asserts the
/// matched set EQUALS a brute-force reference computed independently from those known literals (plain C#
/// comparisons over the stored values — NOT the read walk), so a drift in the path-walk, the token comparison,
/// or the operator parse fails loud.
///
/// It locks in the load-bearing Q3 teeth: a wrong path reads no value EVERYWHERE and is surfaced LOUD (never a
/// silent "0 matches"); a numeric operator on a non-numeric field is a fast typed error; <c>=</c> on a float
/// matches across <c>0.5</c>/<c>0.50</c>; a FormLink compares as a FormKey; <c>!=</c> negates; an AND-list
/// intersects; and malformed predicates are refused at parse. RED if any of those is absent.
///
/// Run: <c>dotnet run --project src/housecarl-generator value-predicate-guard</c>
/// </summary>
public static class ValuePredicateProbe
{
    /// <summary>One synthesized MagicEffect plus the literals it was BUILT with — the independent ground truth the
    /// evaluator must reproduce (the brute-force oracle reads these fields, never the record).</summary>
    sealed record Mgef(IMagicEffectGetter Rec, ActorValue MagicSkill, float BaseCost, ActorValue ArchActorValue, FormKey Projectile)
    {
        public FormKey Fk => Rec.FormKey;
    }

    /// <summary>One synthesized Weapon plus its known BasicStats.Damage.</summary>
    sealed record Weap(IWeaponGetter Rec, ushort Damage)
    {
        public FormKey Fk => Rec.FormKey;
    }

    static int _pass, _fail;

    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — field-value query predicate (cross_plugin_query where=)  ################");
        Console.WriteLine();

        var mod = new SkyrimMod(new ModKey("hcvalguard", ModType.Plugin), SkyrimRelease.SkyrimSE);

        // ---- MGEF cohort: known (MagicSkill, BaseCost, Archetype.ActorValue, Projectile) ---------------------
        var projX = FormKey.Factory("0ABCDE:hcvalguard.esp");
        var projY = FormKey.Factory("0FEDCB:hcvalguard.esp");
        var mgefs = new List<Mgef>
        {
            MakeMgef(mod, ActorValue.Destruction, 0.5f,  ActorValue.Infamy,      projX),
            MakeMgef(mod, ActorValue.Conjuration, 1.0f,  ActorValue.Conjuration, projY),
            MakeMgef(mod, ActorValue.Destruction, 2.0f,  ActorValue.Infamy,      projX),
            MakeMgef(mod, ActorValue.Restoration, 0.5f,  ActorValue.Destruction, projY),
        };
        var mgefBodies = mgefs.Select(m => (IMajorRecordGetter)m.Rec).ToList();

        // ---- WEAP cohort: known BasicStats.Damage ------------------------------------------------------------
        var weaps = new List<Weap>
        {
            MakeWeap(mod, 10),
            MakeWeap(mod, 50),
            MakeWeap(mod, 100),
        };
        var weapBodies = weaps.Select(w => (IMajorRecordGetter)w.Rec).ToList();

        Console.WriteLine($"-- synthesized {mgefs.Count} MagicEffect + {weaps.Count} Weapon records --");
        Console.WriteLine();

        // ============================ FILTER-CORRECTNESS (matched set == brute force) ============================

        // 1. enum equality (top-level scalar enum) — case-insensitive on the enum NAME.
        CheckSet("MagicSkill = Destruction",
            Run(new[] { "MagicSkill = Destruction" }, mgefBodies),
            Expect(mgefs, m => m.MagicSkill == ActorValue.Destruction));
        CheckSet("MagicSkill = destruction  (case-insensitive)",
            Run(new[] { "MagicSkill = destruction" }, mgefBodies),
            Expect(mgefs, m => m.MagicSkill == ActorValue.Destruction));

        // 2. inequality.
        CheckSet("MagicSkill != Destruction",
            Run(new[] { "MagicSkill != Destruction" }, mgefBodies),
            Expect(mgefs, m => m.MagicSkill != ActorValue.Destruction));

        // 3. NESTED enum path (the plan's headline — substruct → field, by construction the same walk as reads).
        CheckSet("Archetype.ActorValue = Infamy",
            Run(new[] { "Archetype.ActorValue = Infamy" }, mgefBodies),
            Expect(mgefs, m => m.ArchActorValue == ActorValue.Infamy));

        // 4. numeric operators on a NESTED numeric leaf (ushort) — parse both sides to a number.
        CheckSet("BasicStats.Damage >= 50",
            Run(new[] { "BasicStats.Damage >= 50" }, weapBodies),
            Expect(weaps, w => w.Damage >= 50));
        CheckSet("BasicStats.Damage < 50",
            Run(new[] { "BasicStats.Damage < 50" }, weapBodies),
            Expect(weaps, w => w.Damage < 50));
        CheckSet("BasicStats.Damage = 50",
            Run(new[] { "BasicStats.Damage = 50" }, weapBodies),
            Expect(weaps, w => w.Damage == 50));

        // 5. float equality across 0.5 / 0.50 — numeric compare, not string (a raw string compare would miss it).
        CheckSet("BaseCost = 0.50  (matches stored 0.5)",
            Run(new[] { "BaseCost = 0.50" }, mgefBodies),
            Expect(mgefs, m => Math.Abs(m.BaseCost - 0.5f) < 1e-6));

        // 6. FormLink equality — compares as a FormKey (broader 0x/leading-zero leniency is deferred per plan).
        CheckSet($"Projectile = {projX}",
            Run(new[] { $"Projectile = {projX}" }, mgefBodies),
            Expect(mgefs, m => m.Projectile == projX));

        // 7. AND-list — every predicate must hold (intersection).
        CheckSet("[MagicSkill = Destruction] AND [BaseCost >= 1.0]",
            Run(new[] { "MagicSkill = Destruction", "BaseCost >= 1.0" }, mgefBodies),
            Expect(mgefs, m => m.MagicSkill == ActorValue.Destruction && m.BaseCost >= 1.0f));

        // ============================ Q3 TEETH (no silent wrong answer) ============================
        Console.WriteLine();
        Console.WriteLine("-- Q3 teeth --");

        // 8. WRONG PATH → no readable value on ANY candidate → LOUD accounting (NOT a silent zero).
        {
            var (matched, set) = RunWithSet(new[] { "Archetyp.ActorValue = Infamy" }, mgefBodies);  // typo'd 'Archetyp'
            var note = set.AccountingNote();
            bool loud = note is not null && note.Contains("no readable value", StringComparison.Ordinal);
            Check("wrong path: 0 matches", matched.Count == 0);
            Check("wrong path: LOUD note (not silent zero)", loud);
            Console.WriteLine($"     note: {Trunc(note)}");
        }

        // 9. LIST path (Keywords) reads as a container → no value → surfaced, never silently matched.
        {
            var (matched, set) = RunWithSet(new[] { "Keywords = 0ABCDE:hcvalguard.esp" }, mgefBodies);
            var note = set.AccountingNote();
            Check("list path: 0 matches", matched.Count == 0);
            Check("list path: surfaced (note mentions list/container)",
                  note is not null && note.Contains("container/list", StringComparison.Ordinal));
        }

        // 10. NUMERIC operator on a NON-numeric field → fast typed FatalError (not a whole-scan silent skip).
        {
            var (matched, set) = RunWithSet(new[] { "MagicSkill > 5" }, mgefBodies);
            Check("numeric op on enum: FatalError set", set.FatalError is not null);
            Check("numeric op on enum: 0 matches", matched.Count == 0);
            Console.WriteLine($"     fatal: {Trunc(set.FatalError)}");
        }

        // 11. PARSE errors refuse the whole call (before any scan).
        Check("parse: numeric op needs numeric operand ('>= abc')", FieldPredicateSet.Parse(new[] { "BasicStats.Damage >= abc" }).Error is not null);
        Check("parse: missing operator ('MagicSkill')", FieldPredicateSet.Parse(new[] { "MagicSkill" }).Error is not null);
        Check("parse: missing path ('= Infamy')", FieldPredicateSet.Parse(new[] { "= Infamy" }).Error is not null);
        Check("parse: missing value ('MagicSkill =')", FieldPredicateSet.Parse(new[] { "MagicSkill =" }).Error is not null);
        Check("parse: unknown operator ('MagicSkill ~ x')", FieldPredicateSet.Parse(new[] { "MagicSkill ~ x" }).Error is not null);
        Check("parse: empty where= refused", FieldPredicateSet.Parse(Array.Empty<string>()).Error is not null);
        // a VALID predicate parses clean.
        Check("parse: valid predicate accepted", FieldPredicateSet.Parse(new[] { "MagicSkill = Destruction" }).Error is null);

        // 12. healthy scan emits NO accounting note (no false alarms).
        {
            var (_, set) = RunWithSet(new[] { "MagicSkill = Destruction" }, mgefBodies);
            Check("healthy scan: no spurious accounting note", set.AccountingNote() is null);
        }

        Console.WriteLine();
        Console.WriteLine($"=== value-predicate-guard: {_pass} passed, {_fail} failed -> {(_fail == 0 ? "PASS" : "FAIL")} ===");
        return _fail == 0 ? 0 : 1;
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    static Mgef MakeMgef(SkyrimMod mod, ActorValue skill, float baseCost, ActorValue archAv, FormKey projectile)
    {
        var m = mod.MagicEffects.AddNew();
        m.MagicSkill = skill;
        m.BaseCost = baseCost;
        m.Archetype = new MagicEffectLightArchetype { ActorValue = archAv };
        m.Projectile.SetTo(projectile);
        return new Mgef(m, skill, baseCost, archAv, projectile);
    }

    static Weap MakeWeap(SkyrimMod mod, ushort damage)
    {
        var w = mod.Weapons.AddNew();
        w.BasicStats = new WeaponBasicStats { Damage = damage };
        return new Weap(w, damage);
    }

    /// <summary>Parse + evaluate the predicate set over a cohort; return the matched FormKey set. Throws if the
    /// predicates don't parse (these call sites pass well-formed predicates — a parse failure is a real bug).</summary>
    static HashSet<FormKey> Run(string[] where, IEnumerable<IMajorRecordGetter> cohort) => RunWithSet(where, cohort).matched;

    static (HashSet<FormKey> matched, FieldPredicateSet set) RunWithSet(string[] where, IEnumerable<IMajorRecordGetter> cohort)
    {
        var (set, err) = FieldPredicateSet.Parse(where);
        if (err is not null) throw new InvalidOperationException($"unexpected parse error for [{string.Join(", ", where)}]: {err}");
        var matched = new HashSet<FormKey>();
        foreach (var body in cohort) if (set!.Matches(body)) matched.Add(body.FormKey);
        return (matched, set!);
    }

    static HashSet<FormKey> Expect(IEnumerable<Mgef> cohort, Func<Mgef, bool> pred)
        => cohort.Where(pred).Select(m => m.Fk).ToHashSet();

    static HashSet<FormKey> Expect(IEnumerable<Weap> cohort, Func<Weap, bool> pred)
        => cohort.Where(pred).Select(w => w.Fk).ToHashSet();

    static void CheckSet(string label, HashSet<FormKey> actual, HashSet<FormKey> expected)
    {
        bool ok = actual.SetEquals(expected);
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}   matched={actual.Count} expected={expected.Count}");
        if (!ok)
        {
            Console.WriteLine($"           actual  : {string.Join(", ", actual)}");
            Console.WriteLine($"           expected: {string.Join(", ", expected)}");
        }
        if (ok) _pass++; else _fail++;
    }

    static void Check(string label, bool ok)
    {
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        if (ok) _pass++; else _fail++;
    }

    static string Trunc(string? s) => s is null ? "(null)" : s.Length > 160 ? s.Substring(0, 160) + "…" : s;
}

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
/// intersects; identity membership (<c>formid in</c>/<c>not in</c> — #226) keeps/drops exactly the listed set,
/// survives spaces in plugin filenames, and reads an <c>@file</c> list; and malformed predicates are refused at
/// parse. RED if any of those is absent.
///
/// Run: <c>dotnet run --project src/housecarl-generator value-predicate-guard</c>
/// </summary>
public static class ValuePredicateProbe
{
    /// <summary>One synthesized MagicEffect plus the literals it was BUILT with — the independent ground truth the
    /// evaluator must reproduce (the brute-force oracle reads these fields, never the record).</summary>
    sealed record Mgef(IMagicEffectGetter Rec, string Eid, ActorValue MagicSkill, float BaseCost, ActorValue ArchActorValue, FormKey Projectile)
    {
        public FormKey Fk => Rec.FormKey;
    }

    /// <summary>One synthesized Weapon plus its known BasicStats.Damage.</summary>
    sealed record Weap(IWeaponGetter Rec, ushort Damage)
    {
        public FormKey Fk => Rec.FormKey;
    }

    /// <summary>One synthesized Armor plus the known BodyTemplate.FirstPersonFlags bits it was built with — the
    /// independent ground truth for the bitwise <c>has</c> / flags-aware compare (the brute force ANDs these bits,
    /// never the record).</summary>
    sealed record Armo(IArmorGetter Rec, ulong Bits)
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
            MakeMgef(mod, "hcFireDamage",    ActorValue.Destruction, 0.5f,  ActorValue.Infamy,      projX),
            MakeMgef(mod, "hcConjureFlame",  ActorValue.Conjuration, 1.0f,  ActorValue.Conjuration, projY),
            MakeMgef(mod, "hcFrostDamage",   ActorValue.Destruction, 2.0f,  ActorValue.Infamy,      projX),
            MakeMgef(mod, "hcRestoreHealth", ActorValue.Restoration, 0.5f,  ActorValue.Destruction, projY),
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

        // ---- ARMO cohort: known BodyTemplate.FirstPersonFlags (a [Flags] BipedObjectFlag) --------------------
        // A mix of NAMED vanilla slots (render "Body"/"Forearms"), UNNAMED modder slots (render numeric), and a
        // body+modder-slot COMBO (renders as one number) — the exact name/number split HCBR 2026-06-24 reported.
        const uint slot52 = 0x400000;  // bit 22 — unnamed modder slot ("slot 52") -> "4194304"
        const uint slot53 = 0x800000;  // bit 23 — unnamed modder slot ("slot 53") -> "8388608"
        var armos = new List<Armo>
        {
            MakeArmo(mod, BipedObjectFlag.Body),                             // 4       -> "Body"
            MakeArmo(mod, BipedObjectFlag.Forearms),                        // 16      -> "Forearms"
            MakeArmo(mod, (BipedObjectFlag)slot53),                         // 8388608 -> "8388608"
            MakeArmo(mod, (BipedObjectFlag)slot52),                         // 4194304 -> "4194304"
            MakeArmo(mod, BipedObjectFlag.Body | (BipedObjectFlag)slot53),  // 8388612 -> "8388612" (combo)
        };
        var armoBodies = armos.Select(a => (IMajorRecordGetter)a.Rec).ToList();

        Console.WriteLine($"-- synthesized {mgefs.Count} MagicEffect + {weaps.Count} Weapon + {armos.Count} Armor records --");
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

        // 8. contains — case-insensitive substring on a string leaf (EditorID).
        CheckSet("EditorID contains Frost",
            Run(new[] { "EditorID contains Frost" }, mgefBodies),
            Expect(mgefs, m => m.Eid.IndexOf("Frost", StringComparison.OrdinalIgnoreCase) >= 0));
        CheckSet("EditorID contains frost  (case-insensitive)",
            Run(new[] { "EditorID contains frost" }, mgefBodies),
            Expect(mgefs, m => m.Eid.IndexOf("Frost", StringComparison.OrdinalIgnoreCase) >= 0));
        CheckSet("EditorID contains hc  (all)",
            Run(new[] { "EditorID contains hc" }, mgefBodies),
            Expect(mgefs, m => m.Eid.IndexOf("hc", StringComparison.OrdinalIgnoreCase) >= 0));

        // 9. the remaining numeric operators (> and <=), completing the set.
        CheckSet("BasicStats.Damage > 50",
            Run(new[] { "BasicStats.Damage > 50" }, weapBodies),
            Expect(weaps, w => w.Damage > 50));
        CheckSet("BasicStats.Damage <= 50",
            Run(new[] { "BasicStats.Damage <= 50" }, weapBodies),
            Expect(weaps, w => w.Damage <= 50));

        // ============== BITWISE `has` + flags-aware compare on a [Flags] enum (HCBR 2026-06-24) ==============
        Console.WriteLine();
        Console.WriteLine("-- bitwise `has` + flags-aware compare on BodyTemplate.FirstPersonFlags --");

        // `has <bit value>` — matches every record with that slot bit set, INCLUDING combos (what `=` misses).
        CheckSet("FirstPersonFlags has 8388608  (slot 53, incl. combos)",
            Run(new[] { "BodyTemplate.FirstPersonFlags has 8388608" }, armoBodies),
            Expect(armos, a => (a.Bits & slot53) == slot53));

        // the same bit in HEX — bitmasks read naturally in hex.
        CheckSet("FirstPersonFlags has 0x800000  (hex form of slot 53)",
            Run(new[] { "BodyTemplate.FirstPersonFlags has 0x800000" }, armoBodies),
            Expect(armos, a => (a.Bits & slot53) == slot53));

        // `has <flag name>` — a named vanilla slot by name, combo-tolerant (Body = bit 2 = 4).
        CheckSet("FirstPersonFlags has Body  (named slot, incl. combos)",
            Run(new[] { "BodyTemplate.FirstPersonFlags has Body" }, armoBodies),
            Expect(armos, a => (a.Bits & 4UL) == 4UL));

        // the CONTRAST that motivated the op: `=` is exact (excludes the combo); `has` finds it.
        CheckSet("FirstPersonFlags = 8388608  (exact — slot-53-ONLY, excludes the combo)",
            Run(new[] { "BodyTemplate.FirstPersonFlags = 8388608" }, armoBodies),
            Expect(armos, a => a.Bits == slot53));

        // flags `=` against a NUMERIC operand now matches a NAME-rendered field (the report's `= 16` case).
        CheckSet("FirstPersonFlags = 16  (matches the name-rendered Forearms)",
            Run(new[] { "BodyTemplate.FirstPersonFlags = 16" }, armoBodies),
            Expect(armos, a => a.Bits == 16UL));
        CheckSet("FirstPersonFlags = Forearms  (name still works)",
            Run(new[] { "BodyTemplate.FirstPersonFlags = Forearms" }, armoBodies),
            Expect(armos, a => a.Bits == 16UL));

        // numeric RANGE op no longer ERRORS on a flags field that renders as a name (the report's `>= 65536`).
        {
            var (matched, set) = RunWithSet(new[] { "BodyTemplate.FirstPersonFlags >= 65536" }, armoBodies);
            CheckSet("FirstPersonFlags >= 65536  (modder-range slots; no longer a type error)",
                matched, Expect(armos, a => a.Bits >= 65536UL));
            Check("FirstPersonFlags >= 65536: no FatalError (flags compare numerically now)", set.FatalError is null);
        }

        // `has` on a plain INTEGER leaf (ushort Damage) — generalizes beyond enums (50 = ...110010 has bit 4).
        CheckSet("BasicStats.Damage has 16  (integer bit-test)",
            Run(new[] { "BasicStats.Damage has 16" }, weapBodies),
            Expect(weaps, w => (w.Damage & 16) == 16));

        // Q3: `has` on a NON-bitmask field (a non-[Flags] enum) is a fast typed error, not a silent non-match.
        {
            var (matched, set) = RunWithSet(new[] { "MagicSkill has 4" }, mgefBodies);
            Check("has on non-flags enum: FatalError set", set.FatalError is not null);
            Check("has on non-flags enum: 0 matches", matched.Count == 0);
            Console.WriteLine($"     fatal: {Trunc(set.FatalError)}");
        }

        // parse: `has` is a recognized operator.
        Check("parse: `has` accepted", FieldPredicateSet.Parse(new[] { "BodyTemplate.FirstPersonFlags has 16" }).Error is null);

        // ============== PRESENCE tests: exists / missing (no operand) — "which records CARRY this field" (#197) ==============
        Console.WriteLine();
        Console.WriteLine("-- presence: exists / missing --");

        // A SUBSTRUCT present on every MGEF (Archetype, set by MakeMgef; test 9 confirms it reads as a container
        // summary) — exists matches all, missing none.
        CheckSet("Archetype exists  (substruct present on all)",
            Run(new[] { "Archetype exists" }, mgefBodies), Expect(mgefs, _ => true));
        CheckSet("Archetype missing  (complement — none)",
            Run(new[] { "Archetype missing" }, mgefBodies), Expect(mgefs, _ => false));

        // A SCALAR present on every MGEF (MagicSkill, set by MakeMgef) — exists matches all.
        CheckSet("MagicSkill exists  (scalar present on all)",
            Run(new[] { "MagicSkill exists" }, mgefBodies), Expect(mgefs, _ => true));

        // A whole LIST — the count-sensitive case proving present-and-NON-EMPTY (an EMPTY list is NOT carried).
        // Three Armors: Keywords non-empty / empty / absent(null). exists matches ONLY the non-empty one; missing
        // the other two. The empty-vs-carried split is exactly what the structural ContainerCount buys — a naive
        // "container summary == present" would wrongly match the empty list.
        {
            var m2 = new SkyrimMod(new ModKey("hcpresence", ModType.Plugin), SkyrimRelease.SkyrimSE);
            var kwFull = m2.Armors.AddNew();  kwFull.Keywords = new() { new FormLink<IKeywordGetter>(projX) };  // carried
            var kwEmpty = m2.Armors.AddNew(); kwEmpty.Keywords = new();                                          // present-but-EMPTY
            var kwNull = m2.Armors.AddNew();                                                                     // absent (null)
            var listCohort = new List<IMajorRecordGetter> { kwFull, kwEmpty, kwNull };
            var (matchedE, setE) = RunWithSet(new[] { "Keywords exists" }, listCohort);
            CheckSet("Keywords exists  (non-empty ONLY — empty & null excluded)",
                matchedE, new HashSet<FormKey> { kwFull.FormKey });
            Check("Keywords exists: healthy presence scan — no false-alarm note (2 of 3 absent is a TRUE result)",
                setE.AccountingNote() is null);
            CheckSet("Keywords missing  (empty & null — complement)",
                Run(new[] { "Keywords missing" }, listCohort),
                new HashSet<FormKey> { kwEmpty.FormKey, kwNull.FormKey });
        }

        // Q3: a MISTYPED presence path (no such field on ANY record) fails LOUD — not a silent 0.
        {
            var (matched, set) = RunWithSet(new[] { "Archetyp exists" }, mgefBodies);  // typo'd 'Archetyp'
            var note = set.AccountingNote();
            Check("mistyped exists: 0 matches", matched.Count == 0);
            Check("mistyped exists: LOUD 'not a field' note", note is not null && note.Contains("NOT A FIELD", StringComparison.Ordinal));
            Console.WriteLine($"     note: {Trunc(note)}");
        }

        // parse: exists / missing accepted; a trailing value is refused (they take NO operand).
        Check("parse: `exists` accepted", FieldPredicateSet.Parse(new[] { "VirtualMachineAdapter exists" }).Error is null);
        Check("parse: `missing` accepted", FieldPredicateSet.Parse(new[] { "VirtualMachineAdapter missing" }).Error is null);
        Check("parse: `exists <value>` refused (presence takes no operand)", FieldPredicateSet.Parse(new[] { "Archetype exists foo" }).Error is not null);
        Check("parse: `missing <value>` refused", FieldPredicateSet.Parse(new[] { "Archetype missing foo" }).Error is not null);

        // ============== IDENTITY membership: formid in / not in a supplied list (#226) ==============
        Console.WriteLine();
        Console.WriteLine("-- identity membership: formid in / not in --");

        // inline list — in keeps exactly the listed set; not in is its complement (the reconciliation subtraction
        // "every record minus these already-claimed"). Brute force: plain membership over the KNOWN FormKeys.
        {
            var claimed = new HashSet<FormKey> { mgefs[0].Fk, mgefs[2].Fk };
            CheckSet("formid in [inline]  (keeps exactly the listed)",
                Run(new[] { $"formid in [{mgefs[0].Fk}, {mgefs[2].Fk}]" }, mgefBodies),
                Expect(mgefs, m => claimed.Contains(m.Fk)));
            CheckSet("formid not in [inline]  (the complement — the #226 subtraction)",
                Run(new[] { $"formid not in [{mgefs[0].Fk}, {mgefs[2].Fk}]" }, mgefBodies),
                Expect(mgefs, m => !claimed.Contains(m.Fk)));

            // a pasted JSON array (quotes + brackets) parses as-is.
            CheckSet("formid in [\"…\", \"…\"]  (JSON-array paste form)",
                Run(new[] { $"formid in [\"{mgefs[0].Fk}\", \"{mgefs[2].Fk}\"]" }, mgefBodies),
                Expect(mgefs, m => claimed.Contains(m.Fk)));

            // ANDs with a value predicate — membership composes with the leaf ops.
            CheckSet("[formid not in …] AND [MagicSkill = Destruction]",
                Run(new[] { $"formid not in [{mgefs[0].Fk}]", "MagicSkill = Destruction" }, mgefBodies),
                Expect(mgefs, m => m.Fk != mgefs[0].Fk && m.MagicSkill == ActorValue.Destruction));

            // identity is always readable — a membership scan can never false-alarm the Q3 accounting.
            var (_, setM) = RunWithSet(new[] { $"formid not in [{mgefs[0].Fk}]" }, mgefBodies);
            Check("membership scan: no spurious accounting note", setM.AccountingNote() is null);
        }

        // a plugin filename WITH SPACES — commas separate the list, so '123456:My Mod.esp' stays one token (the
        // reason bare spaces are NOT a separator).
        {
            var spaced = new SkyrimMod(new ModKey("hc spaced guard", ModType.Plugin), SkyrimRelease.SkyrimSE);
            var s1 = spaced.MagicEffects.AddNew();
            var s2 = spaced.MagicEffects.AddNew();
            var cohort = new List<IMajorRecordGetter> { s1, s2 };
            CheckSet("formid in — plugin filename WITH SPACES stays one token",
                Run(new[] { $"formid in [{s1.FormKey}]" }, cohort),
                new HashSet<FormKey> { s1.FormKey });
        }

        // @file form — newline-separated FormIDs in a temp file; matches identically to the inline form.
        {
            var listFile = Path.Combine(Path.GetTempPath(), $"hcvalguard-formids-{Environment.ProcessId}.txt");
            try
            {
                File.WriteAllText(listFile, $"{mgefs[0].Fk}\r\n{mgefs[2].Fk}\n");
                var claimed = new HashSet<FormKey> { mgefs[0].Fk, mgefs[2].Fk };
                CheckSet("formid not in @file  (newline-separated list file)",
                    Run(new[] { $"formid not in @{listFile}" }, mgefBodies),
                    Expect(mgefs, m => !claimed.Contains(m.Fk)));
            }
            finally { try { File.Delete(listFile); } catch { } }
        }

        // parse refusals — each names itself and refuses the whole call BEFORE any scan (Q3).
        Check("parse: `in` on a non-formid path refused (identity-only)",
            FieldPredicateSet.Parse(new[] { $"MagicSkill in [{mgefs[0].Fk}]" }).Error is not null);
        Check("parse: `not` without `in` refused",
            FieldPredicateSet.Parse(new[] { "formid not [123456:X.esp]" }).Error is not null);
        Check("parse: empty list refused (`formid in []`)",
            FieldPredicateSet.Parse(new[] { "formid in []" }).Error is not null);
        Check("parse: non-FormID list entry refused",
            FieldPredicateSet.Parse(new[] { "formid in [not-a-formid]" }).Error is not null);
        Check("parse: missing @file refused (named, not thrown)",
            FieldPredicateSet.Parse(new[] { @"formid in @C:\hcvalguard-no-such-file-ever.txt" }).Error is not null);
        Check("parse: RELATIVE @file path refused (server CWD is not the caller's)",
            FieldPredicateSet.Parse(new[] { "formid in @relative-list.txt" }).Error is not null);

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

        // 8b. VALID field, UNSET on ALL scanned → still LOUD, but DISTINGUISHED from a mistyped path: the note says
        //     the field is valid/unset and does NOT call it "mistyped" (HCBR-2026-07-12 — 'Prompt' is a real INFO
        //     field, just unset on every one of the 531 scanned; the old "likely a mistyped path" note sent the
        //     reporter hunting a non-bug). 'Name' (FULL) is a real MagicEffect field left unset by MakeMgef.
        {
            var (matched, set) = RunWithSet(new[] { "Name = Whatever" }, mgefBodies);
            var note = set.AccountingNote();
            Check("valid-but-unset: 0 matches", matched.Count == 0);
            Check("valid-but-unset: LOUD note (not silent zero)",
                  note is not null && note.Contains("no readable value", StringComparison.Ordinal));
            Check("valid-but-unset: says VALID/unset, NOT 'mistyped'",
                  note is not null && note.Contains("VALID", StringComparison.Ordinal) && !note.Contains("mistyped", StringComparison.Ordinal));
            Console.WriteLine($"     note: {Trunc(note)}");
        }

        // 9. CONTAINER path (a non-scalar leaf — here the Archetype substruct, populated on every MGEF fixture)
        //    reads as a container summary → no scalar value → surfaced with the container-specific guidance, never
        //    silently matched. (A whole-LIST path lands in the same branch by the same '['-summary signal.)
        {
            var (matched, set) = RunWithSet(new[] { "Archetype = whatever" }, mgefBodies);
            var note = set.AccountingNote();
            Check("container path: 0 matches", matched.Count == 0);
            Check("container path: surfaced (note mentions container/list)",
                  note is not null && note.Contains("container/list", StringComparison.Ordinal));
            Console.WriteLine($"     note: {Trunc(note)}");
        }

        // 9b. A whole-LIST path (a POPULATED Keywords list) lands in the same container branch — exercising the
        //     [list: N item(s)] rendering directly, not just the substruct summary above (review item 3).
        {
            var kwArmo = mod.Armors.AddNew();
            kwArmo.Keywords = new() { new FormLink<IKeywordGetter>(projX) };   // a present, non-empty list → container summary, never "unset"
            var (matched, set) = RunWithSet(new[] { "Keywords = 0FFFFF:hcvalguard.esp" }, new List<IMajorRecordGetter> { kwArmo });
            var note = set.AccountingNote();
            Check("list path: 0 matches", matched.Count == 0);
            Check("list path: surfaced (note mentions container/list)",
                  note is not null && note.Contains("container/list", StringComparison.Ordinal));
        }

        // 10. NUMERIC operator on a NON-numeric field → fast typed FatalError (not a whole-scan silent skip).
        {
            var (matched, set) = RunWithSet(new[] { "MagicSkill > 5" }, mgefBodies);
            Check("numeric op on enum: FatalError set", set.FatalError is not null);
            Check("numeric op on enum: 0 matches", matched.Count == 0);
            Console.WriteLine($"     fatal: {Trunc(set.FatalError)}");
        }

        // 10b. SOFT no-value note — a path wrong for >half the candidates in a MIXED scan is surfaced softly, even
        //      though matches are still returned (distinct from the LOUD "no value on ANY" note in test 8/9 above).
        {
            var mixed = mgefBodies.Concat(weapBodies).ToList();   // 4 MGEF (no BasicStats) + 3 WEAP
            var (matched, set) = RunWithSet(new[] { "BasicStats.Damage >= 0" }, mixed);
            var note = set.AccountingNote();
            Check("soft note: matched only the weapons (3)", matched.Count == 3);
            Check("soft note: SOFT (>half no-value), not the loud 'no value on any' note",
                  note is not null && note.Contains("had no readable value on", StringComparison.Ordinal)
                  && !note.Contains("yielded no readable value", StringComparison.Ordinal));
            Console.WriteLine($"     note: {Trunc(note)}");
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

    static Mgef MakeMgef(SkyrimMod mod, string eid, ActorValue skill, float baseCost, ActorValue archAv, FormKey projectile)
    {
        var m = mod.MagicEffects.AddNew();
        m.EditorID = eid;
        m.MagicSkill = skill;
        m.BaseCost = baseCost;
        m.Archetype = new MagicEffectLightArchetype { ActorValue = archAv };
        m.Projectile.SetTo(projectile);
        return new Mgef(m, eid, skill, baseCost, archAv, projectile);
    }

    static Weap MakeWeap(SkyrimMod mod, ushort damage)
    {
        var w = mod.Weapons.AddNew();
        w.BasicStats = new WeaponBasicStats { Damage = damage };
        return new Weap(w, damage);
    }

    static Armo MakeArmo(SkyrimMod mod, BipedObjectFlag flags)
    {
        var a = mod.Armors.AddNew();
        a.BodyTemplate = new BodyTemplate { FirstPersonFlags = flags };
        return new Armo(a, (ulong)flags);
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

    static HashSet<FormKey> Expect(IEnumerable<Armo> cohort, Func<Armo, bool> pred)
        => cohort.Where(pred).Select(a => a.Fk).ToHashSet();

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

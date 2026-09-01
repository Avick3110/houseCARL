using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for <c>InsertAtIndex</c> — the modeled-list primitive #302 named, and the
/// third member of the family <c>Add</c> and <c>SetAtIndex</c> already had. Add appends (moves nothing, but only
/// ever lands at the END); SetAtIndex overwrites (holds the position, but only over an element already there);
/// Insert puts a NEW element AT a position and shifts the rest right.
///
/// <para><b>What this guard is FOR, and why it is its own file.</b> The verb's whole reason to exist is a claim
/// about POSITION: a CTDA OR-group is position-contiguous (the Or flag chains a row to the row after it), so a new
/// arm must land ADJACENT to the group and the rows after it must not move relative to one another. An insert that
/// lands the right element in the wrong place, or that rebuilds the tail, is not a cosmetic defect — it silently
/// re-groups conditions, which changes what a dialogue line gates on. So the apply arms below do not assert "the
/// element is somewhere in the list": they assert the tail is the SAME OBJECTS in the SAME ORDER (reference
/// identity, which is stronger than equal-looking values), and one arm carries that claim through a real serialize
/// + re-read, because the file is where the ordering has to survive.</para>
///
/// <para>ARMS. Gate (schema-only pre-flight, <c>CorpusRulebook.Validate</c>) — index PRESENCE / SHAPE, cardinality,
/// the element-value and compose-spec requirements, the record-element redirect, and the legal-verb list:
/// <list type="bullet">
///   <item>GATE-REJ-NOIDX / -BADIDX / -NEGIDX — a missing, non-integer, or negative index refuses BEFORE apply.</item>
///   <item>GATE-OK-IDX — a valid index is accepted (no over-reject).</item>
///   <item>GATE-OK-FARIDX (control) — an index far past any live list is STILL accepted: the gate has no live list,
///         so the in-RANGE bound is apply's. Nothing this branch wrote can turn it red; it guards a FUTURE in-range
///         check at the gate, which would refuse the append slot a caller computes from the real length.</item>
///   <item>GATE-REJ-NONLIST — InsertAtIndex on a dict refuses by cardinality.</item>
///   <item>GATE-REJ-NOVALUE — a coercible-element list insert with no value refuses ("requires an element value"),
///         the step-4-pre gate; without it a null element reaches serialize and throws about a missing arm.</item>
///   <item>GATE-REJ-NOSTRUCT / GATE-OK-COMPOSE — on a MODELED-element list (Faction.Conditions) a plain value
///         refuses and a compose spec is accepted, EXACTLY as Add and SetAtIndex behave.</item>
///   <item>GATE-REJ-BADFORMLINK — a malformed formlink element value refuses (the step-4a slot check).</item>
///   <item>GATE-REJ-RECORDELEM — an insert into a list of owned child RECORDS redirects to the record axis rather
///         than being accepted and thrown at apply. THE arm that matters most on this branch: the redirect is
///         verb-scoped, so a verb left out of it is a Q3 accept-then-throw, not a missing convenience.</item>
///   <item>GATE-REJ-COMPOSES — composes= (the batch element surface) stays Add/ReplaceAll only, and the refusal
///         names the singular path for this verb rather than leaving the caller to guess.</item>
///   <item>GATE-LEGALVERBS — the unknown-verb message lists InsertAtIndex, so the vocabulary a refusal advertises is
///         the vocabulary the engine has.</item>
/// </list></para>
///
/// <para>Apply (direct engine, in-memory records — deterministic, no load order):
/// <list type="bullet">
///   <item>APPLY-AT-0 / APPLY-MID — the POSITION claim: after inserting at 0 and at 1 into a three-row list, the
///         count is 4, the new element is at the index asked for, and every original row after it is the SAME
///         OBJECT, in order, shifted by exactly one.</item>
///   <item>APPLY-AT-COUNT-IS-ADD — inserting AT the list's length produces the identical list an Add produces,
///         element for element. This is what makes the append-inclusive bound a definition rather than an
///         off-by-one: `idx == count` is legal precisely because it already has a meaning.</item>
///   <item>APPLY-REJ-OOB — one past the append slot refuses, and the message states the APPEND-INCLUSIVE bound
///         (0..count) rather than the address-an-existing-element bound (0..count-1) the sibling verbs state.</item>
///   <item>APPLY-REJ-NEGIDX — a negative index refuses at apply too, for a direct/CLI call that never met the gate.</item>
///   <item>APPLY-COERCIBLE-SHIFT — the same shift on a plain-value (String) list, so the claim is the verb's and not
///         the modeled-element path's.</item>
///   <item>APPLY-SIBLING-SETATINDEX-MSG / APPLY-SIBLING-REMOVE-MSG — the two PRE-EXISTING messages this branch
///         re-mapped when <c>bool append</c> became <c>IndexOpKind</c>. Nothing was watching them: setting the
///         mapping wrong left this guard and ci-all fully green while SetAtIndex lost "or Add to append" and an
///         empty-list SetAtIndex started saying "nothing to remove". These arms are that reproduction, kept.</item>
///   <item>APPLY-ABSENT-AT-0 (materialize control) / APPLY-ABSENT-REJ-1 — an ABSENT (null) optional list
///         materializes for an insert at 0, and index 1 into that empty list refuses. The materialize is
///         verb-agnostic, so the first arm's own contribution is a control; its bound half is APPLY-AT-COUNT-IS-ADD's.</item>
///   <item>APPLY-SERIALIZE-ROUNDTRIP — the claim through a real write + binary-overlay re-read: a row inserted into
///         the middle of an OR-chain comes back off DISK in the position it was inserted at, with the rows after it
///         in their original order and their Or flags still on the rows that carried them.</item>
/// </list></para>
///
/// <para>CLI — CLI-OP-NEEDSVALUE / CLI-VERB-NEEDSVALUE: the verb consumes the singular value slot, so both of
/// <c>RunPatch</c>'s value-requiring verb lists must carry it or a valueless call reaches apply and throws at
/// serialize about a missing arm. Driven through <c>RunPatch</c> itself, not by re-reading the condition.</para>
///
/// <para>Every apply sub-arm runs under its OWN try. The arm-level try catches a throw but abandons the sub-arms
/// after it, so a bound regression reported 20 assertions where 25 exist — never falsely green, but the wrong SIZE,
/// which is what a sweep is read for.</para>
///
/// The batch-verification classification (InsertAtIndex is NOT count-neutral, so it must not join SetAtIndex's keyed
/// exemption) is pinned in <c>apply-guard</c>, beside the exemption itself; the owned-child disposition rows are
/// pinned in <c>write-surface-guard</c>, in the table that enumerates every verb against that shape.
///
/// Run: <c>dotnet run --project src/housecarl-generator insert-at-index-guard</c>
/// </summary>
public static class InsertAtIndexGuardProbe
{
    static int _pass, _fail;
    static void Check(string label, bool ok, string? got = null)
    {
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        if (!ok && got is not null) Console.WriteLine($"          got: {Trim(got)}");
        if (ok) _pass++; else _fail++;
    }
    static string Trim(string s) => s.Length <= 400 ? s.Replace("\n", " | ") : s[..400].Replace("\n", " | ") + " …";

    static CorpusRulebook Rules => _rules ??= CorpusRulebook.Load();
    static CorpusRulebook? _rules;

    /// <summary>The message <paramref name="act"/> threw, or null if it completed.</summary>
    static string? Throws(Action act)
    {
        try { act(); return null; }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>The refusal <paramref name="act"/> produced, as (was it the EXPECTED kind, what did it say). The kind
    /// half is the point: an out-of-range index that surfaces as a reflection-wrapped
    /// <c>TargetInvocationException</c> also "throws", and also leaves the list untouched — so an arm asserting only
    /// that something threw passes while the caller gets no field name, no index, no bound, and a kind the response
    /// layer does not route as an expected refusal. That is the Q3 unnamed accept-then-throw these pre-checks exist
    /// to prevent, and it is invisible to <see cref="Throws"/> alone.</summary>
    static (bool Expected, string? Message) Refuses(Action act)
    {
        try { act(); return (false, null); }
        catch (ExpectedApplyRejectionException ex) { return (true, ex.Message); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    static readonly ModKey MKey = new("HcInsertGuard", ModType.Master);
    static uint _next = 0x800;
    static FormKey NextFk() => new(MKey, _next++);

    public static int RunGuard(string[] args)
    {
        _pass = _fail = 0;
        Console.WriteLine("################  REGRESSION GUARD — InsertAtIndex on modeled lists (#302)  ################");
        Console.WriteLine();

        var root = Path.Combine(Path.GetTempPath(), "hc_insert_guard_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        // ONE try per arm, not one around all three. A regression that throws inside ApplyArm used to abort
        // SerializeArm as well, so a single sabotage reported far less broken surface than it had actually broken —
        // still RED, never falsely green, but a sweep reading the count learns the wrong size of the damage.
        try
        {
            foreach (var (name, arm) in new (string Name, Action Run)[]
                     { ("gate", GateArm), ("apply", ApplyArm), ("serialize", () => SerializeArm(root)),
                       ("cli", () => CliArm(root)) })
            {
                try { arm(); }
                catch (Exception ex)
                {
                    Console.WriteLine($"   [FAIL] the {name} arm threw: {ex.GetType().Name}: {(ex.InnerException ?? ex).Message}");
                    _fail++;
                }
            }
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }

        Console.WriteLine();
        Console.WriteLine($"=== insert-at-index-guard: {_pass} passed, {_fail} failed -> {(_fail == 0 ? "PASS" : "FAIL")} ===");
        return _fail == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ the gate

    static void GateArm()
    {
        Console.WriteLine("── GATE: what pre-flight says about an InsertAtIndex, with no live list in front of it ──");

        // Race.MovementTypeNames is List<String> — a coercible-element list, so the value/index gates are the
        // subject and no compose is in play. Faction.Conditions is List<Condition>, an ARM element — the exact
        // shape of a dialogue INFO's Conditions, which is what #302 was filed about.
        static WriteRequest Names(string? key, string? value = null) => new()
        { RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "InsertAtIndex", Key = key, Value = value };

        Check("GATE-REJ-NOIDX — an InsertAtIndex with NO index refuses at pre-flight, naming the index",
            Rules.Validate(Names(null, "MT_Walk")) is { } m1 && m1.Contains("requires an index", StringComparison.Ordinal),
            Rules.Validate(Names(null, "MT_Walk")) ?? "(accepted)");

        Check("GATE-REJ-BADIDX — a non-integer index refuses",
            Rules.Validate(Names("abc", "MT_Walk")) is { } m2 && m2.Contains("Illegal list index", StringComparison.Ordinal),
            Rules.Validate(Names("abc", "MT_Walk")) ?? "(accepted)");

        Check("GATE-REJ-NEGIDX — a NEGATIVE index refuses (int.Parse would accept '-1'; the indexer would not)",
            Rules.Validate(Names("-1", "MT_Walk")) is { } m3 && m3.Contains("Illegal list index", StringComparison.Ordinal),
            Rules.Validate(Names("-1", "MT_Walk")) ?? "(accepted)");

        Check("GATE-OK-IDX — a valid index + value is ACCEPTED (the gate does not over-reject the new verb)",
            Rules.Validate(Names("0", "MT_Walk")) is null,
            Rules.Validate(Names("0", "MT_Walk")));

        // The gate/apply split, restated for this verb. Insert's legal range is the one thing that differs from
        // SetAtIndex's — it admits index == count — and the gate is exactly the layer that cannot know either bound.
        // CONTROL, and labelled as one: no mutation of a line this branch wrote turns it red (GATE-OK-IDX already
        // proves a valid index is accepted, and this differs from it only by the index being far). What it guards
        // is a FUTURE change — a well-meaning in-range check added at the gate, which has no live list to check
        // against and would refuse the append slot a caller computes from the real length.
        Check("GATE-OK-FARIDX (control) — an index far past any live list is STILL accepted: in-RANGE is apply's, not the gate's",
            Rules.Validate(Names("9999", "MT_Walk")) is null,
            Rules.Validate(Names("9999", "MT_Walk")));

        Check("GATE-REJ-NONLIST — InsertAtIndex on a dict refuses by cardinality",
            Rules.Validate(new WriteRequest { RecordType = "Npc", Path = new[] { "PlayerSkills", "SkillValues" },
                Verb = "InsertAtIndex", Key = "0", Value = "50" }) is { } m4
                && m4.Contains("InsertAtIndex is only valid on list", StringComparison.Ordinal),
            Rules.Validate(new WriteRequest { RecordType = "Npc", Path = new[] { "PlayerSkills", "SkillValues" },
                Verb = "InsertAtIndex", Key = "0", Value = "50" }) ?? "(accepted)");

        Check("GATE-REJ-NOVALUE — a coercible-element insert with NO value refuses (else a null element throws at serialize)",
            Rules.Validate(Names("0")) is { } m5 && m5.Contains("requires an element value", StringComparison.Ordinal),
            Rules.Validate(Names("0")) ?? "(accepted)");

        // CONTROL, and labelled as one. The formlink-element check (step-4a) carries NO verb condition at all, so
        // insert meets it by REACHABILITY rather than by being listed — no mutation to a line this branch wrote can
        // turn this red. It earns its place as the arm that would catch a future verb-scoping of step-4a that forgot
        // insert, which is why it asserts the recognizer's own words instead of "something was refused".
        var badLink = Rules.Validate(new WriteRequest { RecordType = "Armor", Path = new[] { "Keywords" },
            Verb = "InsertAtIndex", Key = "0", Value = "notaformkey" });
        Check("GATE-REJ-BADFORMLINK (control) — a malformed formlink element value refuses; step-4a is verb-agnostic, so insert meets it by reachability",
            badLink is not null && badLink.Contains("notaformkey", StringComparison.Ordinal)
                && badLink.Contains("Keywords", StringComparison.Ordinal),
            badLink ?? "(accepted)");

        static WriteRequest Cond(string? value, StructSpec? spec) => new()
        {
            RecordType = "Faction", Path = new[] { "Conditions" }, Verb = "InsertAtIndex", Key = "0",
            Value = value, Struct = spec,
        };

        Check("GATE-REJ-NOSTRUCT — a PLAIN VALUE insert into a modeled-element list refuses, naming the compose spec (as Add does)",
            Rules.Validate(Cond("1", null)) is { } m6 && m6.Contains("compose spec", StringComparison.OrdinalIgnoreCase),
            Rules.Validate(Cond("1", null)) ?? "(accepted)");

        Check("GATE-OK-COMPOSE — an insert CARRYING an arm compose is accepted, through the same gate Add's compose passes",
            Rules.Validate(Cond(null, new StructSpec { Type = "ConditionFloat" })) is null,
            Rules.Validate(Cond(null, new StructSpec { Type = "ConditionFloat" })));

        // The redirect that must be verb-scoped correctly or the verb is a Q3 accept-then-throw: a child record is
        // allocated on the record axis, never built into a parent's collection by a write verb — and inserting one
        // AT a position is no more possible than appending one.
        // GATE-OK-COMPOSE proves a good compose is ACCEPTED — but "accepted after validation" and "accepted without
        // validation" look identical from the outside, so dropping insert from the StructElementLegality admit leaves
        // it green. These two make the validation itself falsifiable: the spec's contents are genuinely checked.
        var badType = Rules.Validate(Cond(null, new StructSpec { Type = "NotAConditionArm" }));
        Check("GATE-REJ-BADARMTYPE — a compose naming a type that is not an arm of the element refuses, so the spec IS validated",
            badType is not null && badType.Contains("NotAConditionArm", StringComparison.Ordinal), badType ?? "(accepted)");
        var badField = Rules.Validate(Cond(null, new StructSpec
        { Type = "ConditionFloat", Fields = new Dictionary<string, string> { ["ComparisonValue"] = "notafloat" } }));
        Check("GATE-REJ-BADARMFIELD — a malformed field inside the compose refuses too (contents validated recursively)",
            badField is not null && badField.Contains("notafloat", StringComparison.Ordinal), badField ?? "(accepted)");

        var recElem = Rules.Validate(new WriteRequest
        {
            RecordType = "Cell", Path = new[] { "Persistent" }, Verb = "InsertAtIndex", Key = "0",
            Struct = new StructSpec { Type = "PlacedObject" },
        });
        Check("GATE-REJ-RECORDELEM — an insert into a list of owned child RECORDS redirects to the record axis, not accepted-then-thrown",
            recElem is not null && recElem.Contains("owned child records", StringComparison.Ordinal)
                && recElem.Contains("housecarl_create", StringComparison.Ordinal),
            recElem ?? "(accepted)");

        var composes = Rules.Validate(new WriteRequest
        {
            RecordType = "Faction", Path = new[] { "Conditions" }, Verb = "InsertAtIndex", Key = "0",
            Structs = new[] { new StructSpec { Type = "ConditionFloat" } },
        });
        // The singular-path parenthetical is now DERIVED from the leaf's shape (WriteVerbs), so the arm pins the
        // derived form — verb plus the slot it takes — rather than a hand-written phrase.
        Check("GATE-REJ-COMPOSES — composes= stays Add/ReplaceAll only, and the refusal names InsertAtIndex's singular path",
            composes is not null && composes.Contains("InsertAtIndex (compose= + key=)", StringComparison.Ordinal),
            composes ?? "(accepted)");

        var unknown = Rules.Validate(new WriteRequest
        { RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "Nope", Key = "0", Value = "MT_Walk" });
        Check("GATE-LEGALVERBS — the unknown-verb message advertises InsertAtIndex, so the published vocabulary is the real one",
            unknown is not null && unknown.Contains("InsertAtIndex", StringComparison.Ordinal),
            unknown ?? "(accepted)");
    }

    // ------------------------------------------------------------------ apply

    /// <summary>A Faction carrying <paramref name="values"/> as ConditionFloat rows, in order.</summary>
    static Faction Conditions(params float[] values)
    {
        var fac = new Faction(NextFk(), SkyrimRelease.SkyrimSE);
        foreach (var v in values)
            WriteEngine.ApplyVerb(fac, new WriteRequest
            {
                RecordType = "Faction", Path = new[] { "Conditions" }, Verb = "Add",
                Struct = new StructSpec
                {
                    Type = "ConditionFloat",
                    Fields = new() { ["ComparisonValue"] = v.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    Sets = new() { new WriteRequest { RecordType = "ConditionFloat", Path = new[] { "Data" }, Verb = "Set",
                        Struct = new StructSpec { Type = "GetRandomPercentConditionData" } } },
                },
            });
        return fac;
    }

    static void Insert(Faction fac, int idx, float value) => WriteEngine.ApplyVerb(fac, new WriteRequest
    {
        RecordType = "Faction", Path = new[] { "Conditions" }, Verb = "InsertAtIndex",
        Key = idx.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Struct = new StructSpec
        {
            Type = "ConditionFloat",
            Fields = new() { ["ComparisonValue"] = value.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            Sets = new() { new WriteRequest { RecordType = "ConditionFloat", Path = new[] { "Data" }, Verb = "Set",
                Struct = new StructSpec { Type = "GetRandomPercentConditionData" } } },
        },
    });

    static float[] Values(IReadOnlyList<Condition> conds) =>
        conds.Select(c => c is IConditionFloatGetter f ? f.ComparisonValue : float.NaN).ToArray();

    /// <summary>Run one apply sub-arm under its OWN try. The arm-level try above catches a throw but abandons every
    /// sub-arm after it: a bound regression used to report 20 assertions where 25 exist, so a sweep reading the
    /// count learned the wrong SIZE of the damage — still RED, never falsely green, but a sweep is read for how
    /// much broke, not only whether something did.</summary>
    static void Sub(string name, Action body)
    {
        try { body(); }
        catch (Exception ex)
        { Check($"{name} — the sub-arm threw", false, $"{ex.GetType().Name}: {(ex.InnerException ?? ex).Message}"); }
    }

    static void ApplyArm()
    {
        Console.WriteLine();
        Console.WriteLine("── APPLY: where the element lands, and what moves — the position claim the verb exists for ──");

        // The tail is checked by REFERENCE, not by value. Equal-looking values would also pass if the engine
        // rebuilt the tail out of copies; only object identity says the rows after the insertion point were
        // shifted rather than reconstructed, which is what "the rest of the OR-group is untouched" has to mean.
        Sub("APPLY-AT-0", () =>
        {
            var fac = Conditions(1f, 2f, 3f);
            var before = fac.Conditions.ToArray();
            Insert(fac, 0, 9f);
            var after = fac.Conditions;
            bool ok = after.Count == 4
                && after[0] is IConditionFloatGetter n && n.ComparisonValue == 9f
                && ReferenceEquals(after[1], before[0]) && ReferenceEquals(after[2], before[1]) && ReferenceEquals(after[3], before[2]);
            Check("APPLY-AT-0 — inserting at 0 puts the new row first and shifts all three originals right by one, same objects, same order",
                ok, $"count={after.Count} values=[{string.Join(",", Values(after))}]");
        });

        Sub("APPLY-MID", () =>
        {
            var fac = Conditions(1f, 2f, 3f);
            var before = fac.Conditions.ToArray();
            Insert(fac, 1, 9f);
            var after = fac.Conditions;
            bool ok = after.Count == 4
                && ReferenceEquals(after[0], before[0])
                && after[1] is IConditionFloatGetter n && n.ComparisonValue == 9f
                && ReferenceEquals(after[2], before[1]) && ReferenceEquals(after[3], before[2]);
            Check("APPLY-MID — inserting mid-list leaves the rows BEFORE it untouched and shifts only the rows after it",
                ok, $"count={after.Count} values=[{string.Join(",", Values(after))}]");
        });

        // The append-inclusive bound, proven as an equality rather than asserted as an off-by-one preference.
        Sub("APPLY-AT-COUNT-IS-ADD", () =>
        {
            var viaAdd = Conditions(1f, 2f, 3f);
            WriteEngine.ApplyVerb(viaAdd, new WriteRequest
            {
                RecordType = "Faction", Path = new[] { "Conditions" }, Verb = "Add",
                Struct = new StructSpec
                {
                    Type = "ConditionFloat", Fields = new() { ["ComparisonValue"] = "9" },
                    Sets = new() { new WriteRequest { RecordType = "ConditionFloat", Path = new[] { "Data" }, Verb = "Set",
                        Struct = new StructSpec { Type = "GetRandomPercentConditionData" } } },
                },
            });
            var viaInsert = Conditions(1f, 2f, 3f);
            Insert(viaInsert, viaInsert.Conditions.Count, 9f);
            bool ok = Values(viaAdd.Conditions).SequenceEqual(Values(viaInsert.Conditions));
            Check("APPLY-AT-COUNT-IS-ADD — inserting AT the list's length yields the identical list an Add yields (why the bound includes count)",
                ok, $"add=[{string.Join(",", Values(viaAdd.Conditions))}] insert=[{string.Join(",", Values(viaInsert.Conditions))}]");
        });

        Sub("APPLY-REJ-OOB", () =>
        {
            var fac = Conditions(1f, 2f, 3f);
            var msg = Throws(() => Insert(fac, 4, 9f));
            bool ok = msg is not null
                && msg.Contains("0..3", StringComparison.Ordinal)          // the APPEND-INCLUSIVE bound (count == 3)
                && !msg.Contains("0..2", StringComparison.Ordinal)         // NOT the address-an-existing-element bound
                && fac.Conditions.Count == 3;                              // and it refused before touching the list
            Check("APPLY-REJ-OOB — one past the append slot refuses, stating the APPEND-INCLUSIVE bound (0..count), and changes nothing",
                ok, $"{msg ?? "(no throw)"} | count={fac.Conditions.Count}");
        });

        Sub("APPLY-REJ-NEGIDX", () =>
        {
            var fac = Conditions(1f, 2f);
            var (expected, msg) = Refuses(() => Insert(fac, -1, 9f));
            // The KIND and the WORDS, not merely that it threw. Deleting `idx < 0 ||` from the bound leaves the
            // reflection Invoke throwing and the list untouched, so an arm asserting only "something threw, count
            // unchanged" stays green on exactly the regression it names.
            Check("APPLY-REJ-NEGIDX — a negative index refuses at APPLY as the EXPECTED kind, naming the index, for a direct/CLI call that never met the gate",
                expected && msg is not null && msg.Contains("Index -1 out of range", StringComparison.Ordinal)
                    && fac.Conditions.Count == 2,
                $"expected={expected} {msg ?? "(no throw)"} | count={fac.Conditions.Count}");
        });

        // The SIBLING messages, which this branch re-mapped and nothing was watching. IndexRangeMessage was a
        // `bool append` meaning "may the message offer Add?"; insert needed a third mode whose BOUND differs, so it
        // became an IndexOpKind and the two pre-existing callsites were re-mapped by hand. Setting that mapping
        // wrong — `bool append = false` in the shared branch — left insert-at-index-guard 25/25 and ci-all 128/128
        // green while SetAtIndex silently lost "or Add to append" and an empty-list SetAtIndex started telling the
        // caller there was "nothing to remove", advice for a verb they did not use. These two arms are the
        // reproduction, kept: they fail on exactly that mis-mapping and on nothing else this branch does.
        Sub("APPLY-SIBLING-SETATINDEX-MSG", () =>
        {
            var fac = Conditions(1f, 2f, 3f);
            var msg = Throws(() => WriteEngine.ApplyVerb(fac, new WriteRequest
            { RecordType = "Faction", Path = new[] { "Conditions" }, Verb = "SetAtIndex", Key = "9",
              Struct = new StructSpec { Type = "ConditionFloat" } }));
            Check("APPLY-SIBLING-SETATINDEX-MSG — an out-of-range SetAtIndex still offers Add and still states its own bound (0..count-1), not insert's",
                msg is not null
                    && msg.Contains("or Add to append", StringComparison.Ordinal)
                    && msg.Contains("0..2", StringComparison.Ordinal)
                    && !msg.Contains("0..3", StringComparison.Ordinal),
                msg ?? "(no throw)");
        });

        Sub("APPLY-SIBLING-REMOVE-MSG", () =>
        {
            // The list must be PRESENT and empty, not absent. An absent one short-circuits to ApplyListVerb's own
            // "the collection is absent" throw and never reaches IndexRangeMessage at all — which is how the first
            // draft of this arm stayed green under the mis-mapping it exists to catch. Add a row and drop it.
            var fac = Conditions(1f);
            WriteEngine.ApplyVerb(fac, new WriteRequest
            { RecordType = "Faction", Path = new[] { "Conditions" }, Verb = "Remove", Key = "0" });
            var msg = Throws(() => WriteEngine.ApplyVerb(fac, new WriteRequest
            { RecordType = "Faction", Path = new[] { "Conditions" }, Verb = "Remove", Key = "0" }));
            Check("APPLY-SIBLING-REMOVE-MSG — an out-of-range Remove-by-index on a present, empty list says 'nothing to remove' and never offers Add",
                (fac.Conditions?.Count ?? -1) == 0
                    && msg is not null
                    && msg.Contains("nothing to remove", StringComparison.Ordinal)
                    && !msg.Contains("Add an element first", StringComparison.Ordinal),
                $"{msg ?? "(no throw)"} | count={fac.Conditions?.Count ?? -1}");
        });

        // The same claim on a plain-value list, so it is the VERB's behaviour and not something the compose path does.
        Sub("APPLY-COERCIBLE-SHIFT", () =>
        {
            var race = new Race(NextFk(), SkyrimRelease.SkyrimSE);
            void AddName(string v) => WriteEngine.ApplyVerb(race, new WriteRequest
            { RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "Add", Value = v });
            AddName("A"); AddName("B");
            WriteEngine.ApplyVerb(race, new WriteRequest
            { RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "InsertAtIndex", Key = "1", Value = "X" });
            Check("APPLY-COERCIBLE-SHIFT — a plain-value list shifts the same way (A,B + X@1 -> A,X,B)",
                race.MovementTypeNames is { Count: 3 } n && n[0] == "A" && n[1] == "X" && n[2] == "B",
                $"[{string.Join(",", race.MovementTypeNames ?? new Noggog.ExtendedList<string>())}]");
        });

        // An ABSENT optional list must be insertable-into at 0 — "insert the first element into a record that has
        // none" is the same requirement Add's materialize arm exists for, and index 0 is the only legal index there.
        // PART CONTROL, and worth saying which part: the MATERIALIZE half is verb-agnostic (ApplyListVerb
        // materializes an absent list for every verb but Remove), so insert meets it by REACHABILITY — no line this
        // branch wrote can turn that half red, and it earns its place by catching a future verb-scoping of the
        // materialize that forgot insert. The BOUND half (0 is legal when count is 0) is branch-owned but already
        // proven by APPLY-AT-COUNT-IS-ADD, so this arm's own contribution is the control.
        Sub("APPLY-ABSENT-AT-0", () =>
        {
            var fac = new Faction(NextFk(), SkyrimRelease.SkyrimSE);
            bool wasAbsent = (fac.Conditions?.Count ?? 0) == 0;
            var msg = Throws(() => Insert(fac, 0, 7f));
            Check("APPLY-ABSENT-AT-0 (materialize control) — an empty/absent list takes an insert at 0; the materialize it relies on is verb-agnostic, so insert meets it by reachability",
                msg is null && (fac.Conditions?.Count ?? 0) == 1,
                $"wasEmpty={wasAbsent} {msg ?? ""} count={fac.Conditions?.Count ?? -1}");
        });

        Sub("APPLY-ABSENT-REJ-1", () =>
        {
            var fac = new Faction(NextFk(), SkyrimRelease.SkyrimSE);
            var msg = Throws(() => Insert(fac, 1, 7f));
            Check("APPLY-ABSENT-REJ-1 — index 1 into an empty list refuses, and says the list is empty rather than quoting a bound",
                // "the list is empty" alone is NOT enough: the shared (SetAtIndex/Remove) message contains it too,
                // so disabling Insert's own sentence left this arm green on a refusal reading "nothing to remove"
                // — advice for a verb the caller did not use. Pin the remedy half, which only Insert's sentence has.
                msg is not null && msg.Contains("insert at index 0", StringComparison.Ordinal)
                    && (fac.Conditions?.Count ?? 0) == 0,
                $"{msg ?? "(no throw)"} | count={fac.Conditions?.Count ?? -1}");
        });
    }

    // ------------------------------------------------------------------ serialize round-trip

    /// <summary>The position claim, carried through a real write and a binary-overlay re-read. In-memory reference
    /// identity proves the ENGINE shifted rather than rebuilt; only the file proves the ORDER is what the game will
    /// read. The rows carry Or flags on the ones that chain, because the ordering fact this verb exists for is
    /// exactly that an OR-run stays contiguous — a row inserted into the middle of the run must come back inside it.</summary>
    static void SerializeArm(string root)
    {
        Console.WriteLine();
        Console.WriteLine("── SERIALIZE: the inserted row comes back off DISK in the position it was inserted at ──");

        var mod = new SkyrimMod(MKey, SkyrimRelease.SkyrimSE);
        var fac = mod.Factions.AddNew();
        fac.EditorID = "HcInsertGuardFaction";
        void AddCond(float v, bool or) => WriteEngine.ApplyVerb(fac, new WriteRequest
        {
            RecordType = "Faction", Path = new[] { "Conditions" }, Verb = "Add",
            Struct = new StructSpec
            {
                Type = "ConditionFloat",
                Fields = new() { ["ComparisonValue"] = v.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                 ["Flags"] = or ? "OR" : "0" },
                Sets = new() { new WriteRequest { RecordType = "ConditionFloat", Path = new[] { "Data" }, Verb = "Set",
                    Struct = new StructSpec { Type = "GetRandomPercentConditionData" } } },
            },
        });
        // An OR-run of two, then a plain AND row after it — the shape #302 described.
        AddCond(1f, or: true);
        AddCond(2f, or: false);
        AddCond(3f, or: false);

        // Insert a THIRD arm into the OR-run: it goes at index 1, carrying the Or flag, so the run reads 1|9|2.
        WriteEngine.ApplyVerb(fac, new WriteRequest
        {
            RecordType = "Faction", Path = new[] { "Conditions" }, Verb = "InsertAtIndex", Key = "1",
            Struct = new StructSpec
            {
                Type = "ConditionFloat",
                Fields = new() { ["ComparisonValue"] = "9", ["Flags"] = "OR" },
                Sets = new() { new WriteRequest { RecordType = "ConditionFloat", Path = new[] { "Data" }, Verb = "Set",
                    Struct = new StructSpec { Type = "GetRandomPercentConditionData" } } },
            },
        });

        string path = Path.Combine(root, MKey.FileName.String);
        mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        using var back = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        var read = back.Factions.FirstOrDefault(f => f.EditorID == "HcInsertGuardFaction");
        var conds = read?.Conditions?.ToArray() ?? Array.Empty<IConditionGetter>();
        var vals = conds.Select(c => c is IConditionFloatGetter f ? f.ComparisonValue : float.NaN).ToArray();
        var ors = conds.Select(c => c.Flags.HasFlag(Condition.Flag.OR)).ToArray();

        Check("SERIALIZE-ORDER — the file holds 1,9,2,3: the inserted row sits where it was inserted and the tail kept its order",
            vals.SequenceEqual(new[] { 1f, 9f, 2f, 3f }), $"[{string.Join(",", vals)}]");

        Check("SERIALIZE-ORRUN — the Or flags came back on the rows that carried them, so the inserted arm is INSIDE the OR-run",
            ors.SequenceEqual(new[] { true, true, false, false }), $"[{string.Join(",", ors)}]");
    }

    // ------------------------------------------------------------------ the CLI's value-requiring verb set

    /// <summary>The branch added <c>InsertAtIndex</c> to two CLI checks — the repeatable <c>--op</c> form and the
    /// single <c>--path/--verb</c> form — and nothing asserted either. They are not decoration: the verb consumes
    /// the singular value slot, so a <c>--op</c> without one reaches apply, coerces null into the element, and
    /// throws at SERIALIZE about a missing arm, which is the misleading failure the gate's own value check exists
    /// to prevent. Driven through <see cref="WriteEngine.RunPatch"/> itself rather than re-reading the condition,
    /// so dropping the verb from either list goes red here.</summary>
    static void CliArm(string root)
    {
        Console.WriteLine();
        Console.WriteLine("── CLI: an InsertAtIndex with no value is refused before anything is written ──");

        // RunPatch requires a real --source file before it parses the edits; a header-only mod is enough, and
        // nothing about it is read on the refusal path.
        var mod = new SkyrimMod(MKey, SkyrimRelease.SkyrimSE);
        // Its own directory: the filename has to match the ModKey, which the serialize arm already wrote under
        // root, and neither arm should be able to read the other's file.
        var dir = Directory.CreateDirectory(Path.Combine(root, "cli")).FullName;
        string src = Path.Combine(dir, MKey.FileName.String);
        mod.BeginWrite.ToPath(src).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        static (int Code, string Err) Run(string[] args)
        {
            var prior = Console.Error;
            var sw = new StringWriter();
            try { Console.SetError(sw); return (WriteEngine.RunPatch(args), sw.ToString()); }
            finally { Console.SetError(prior); }
        }

        var op = Run(new[] { "--source", src, "--type", "Armor", "--editorid", "Whatever",
                             "--op", "InsertAtIndex|Keywords|0" });
        Check("CLI-OP-NEEDSVALUE — a repeatable --op InsertAtIndex with no value is refused by name, before any patch is built",
            op.Code == 1 && op.Err.Contains("InsertAtIndex", StringComparison.Ordinal)
                && op.Err.Contains("needs a value", StringComparison.Ordinal),
            $"exit={op.Code} {op.Err.Trim()}");

        var single = Run(new[] { "--source", src, "--type", "Armor", "--editorid", "Whatever",
                                 "--path", "Keywords", "--verb", "InsertAtIndex", "--key", "0" });
        Check("CLI-VERB-NEEDSVALUE — the single-edit --verb form refuses it too, so the two lists cannot drift apart",
            single.Code == 1 && single.Err.Contains("InsertAtIndex", StringComparison.Ordinal)
                && single.Err.Contains("--value is required", StringComparison.Ordinal),
            $"exit={single.Code} {single.Err.Trim()}");
    }
}

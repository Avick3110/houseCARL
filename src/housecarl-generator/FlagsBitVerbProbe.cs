using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD (standing CI instrument) for HCBR-2026-07-15 — a [Flags]-enum write was whole-value-replace only,
/// so a literal <c>Set</c> silently CLEARED every bit the caller didn't re-list, and <c>Add</c>/<c>Remove</c> were
/// refused as collection-only. There was no way to flip ONE flag while preserving the rest.
///
/// THE DAMAGE (measured in the S4 Gray Fox Cowl validation run): lane-derived Flags tokens applied by literal Set
/// clobbered unlisted bits on 10+ records — ManualCostCalc dropped on 6 authored-cost spells (their authored BaseCost
/// thereby ignored), and Essential / Female / IsGhost / Invulnerable / Unique dropped on named NPCs (a ghost made
/// vulnerable, two quest NPCs made killable). The worst kind of write error: pre-flight ACCEPTED it, the read-back
/// CONFIRMED it, and it was wrong — caught only by a post-hoc oracle diff.
///
/// THE FIX (by construction, no per-type wiring): <c>Add</c>/<c>Remove</c> are admitted on a <c>[Flags]</c> enum leaf
/// as BIT operations — Add ORs the operand's bit(s) into the current value, Remove ANDs them out — so one flag flips
/// while every OTHER bit is preserved. The gate (<see cref="CorpusRulebook"/>: <c>IsFlagsEnumLeaf</c> in VerbLegality
/// + a step-4-flags value check) and the apply (<see cref="WriteEngine"/>.<c>ApplyScalarVerb</c>'s [Flags] branch →
/// <c>ApplyFlagsBitVerb</c>) key off the SAME FlagsAttribute-on-the-field's-real-AQ test, so they can't drift. The
/// operand resolves through the SAME enum coercion a Set uses (Enum.Parse: name / comma-combo / decimal), so a bogus
/// flag fails LOUD at the gate, never Enum.Parse-throws at apply.
///
/// RED-&gt;GREEN teeth: A1 (apply Add ORs a bit and PRESERVES the pre-existing one — the anti-clobber core; RED before
/// the fix, when Add was refused outright), A2 (apply Remove clears ONLY its bit), E2E (Set+Add survives serialize +
/// re-read as the UNION). CONTROLS: S0 (Quest.Flags really is a [Flags] enum — a Mutagen reshape can't pass green
/// silently), PRE-ACCEPT (pre-flight now ADMITS flags Add/Remove — was the report's refusal), PRE-BADFLAG (a bogus
/// flag is REFUSED at the gate, not accepted-then-thrown — Q3), PRE-KEY (a stray key is refused — a bit verb takes
/// none), PRE-SCALAR / APPLY-SCALAR (a plain scalar still REFUSES Add at BOTH gate and apply — the acceptance is
/// scoped to [Flags], it did not go universal), and NONNULL-VALUELESS / NULLABLE-VALUELESS (PR #202 review: a
/// VALUELESS Remove keeps its pre-bit-verb whole-clear meaning — accepted on a nullable flags field, refused with a
/// Set-'0' redirect on a non-nullable one — so the bit verbs ADD capability without removing the only path to null a
/// nullable flags field; the scan also reports the live nullable-flags blast radius).
///
/// Self-contained: the apply/E2E teeth are pure in-memory Mutagen (no plugin file, no Skyrim.esm); the PRE-* checks
/// use the GENERATED corpus.json (built into a unique temp dir on a fresh checkout, exactly as formlink-remove-guard).
///
/// Run: <c>dotnet run --project src/housecarl-generator flags-bit-verb-guard</c>
/// </summary>
public static class FlagsBitVerbProbe
{
    public static int RunGuard(string[] args)
    {
        // CI-safe corpus: corpus.json is GENERATED, not tracked — on a fresh checkout build it into a UNIQUE temp
        // dir and point the rulebook there (the pre-flight checks need it), leaving the working tree untouched.
        string? tmp = null;
        if (!File.Exists(CorpusRulebook.CorpusPath))
        {
            tmp = Path.Combine(Path.GetTempPath(), "housecarl-flags-bit-verb-guard-" + Guid.NewGuid().ToString("N"));
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
        int failures = 0;
        void Check(string label, bool ok, string? detail = null)
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}{(ok || detail is null ? "" : $"\n        -> {detail}")}");
            if (!ok) failures++;
        }

        Console.WriteLine("flags-bit-verb-guard — Add/Remove flip ONE bit on a [Flags] enum, other bits preserved (HCBR-2026-07-15)");
        Console.WriteLine();

        // ---- S0 (control): Quest.Flags is a [Flags] enum at runtime; pick TWO distinct single-bit members to test with.
        //      Members are read from the live enum (not hardcoded), so a Mutagen rename can't silently pass green.
        var flagType = typeof(Quest.Flag);
        var isFlags = flagType.IsEnum && flagType.IsDefined(typeof(FlagsAttribute), false);
        var singleBits = new List<(string Name, ulong Bits)>();
        foreach (var v in Enum.GetValues(flagType))
        {
            ulong b; try { b = Convert.ToUInt64(v!); } catch { continue; }   // skip any high-bit/negative member
            if (b != 0 && (b & (b - 1)) == 0) singleBits.Add((v!.ToString()!, b));
        }
        var members = singleBits.GroupBy(m => m.Bits).Select(g => g.First()).OrderBy(m => m.Bits).ToList();
        Check("S0: Quest.Flags is a [Flags] enum with >=2 distinct single-bit members", isFlags && members.Count >= 2,
            $"isFlags={isFlags} singleBitMembers={members.Count}");
        if (!isFlags || members.Count < 2)
        {
            Console.WriteLine("\nflags-bit-verb-guard: ABORT (test enum shape changed) — treated as FAILURE");
            return 1;
        }
        var (aName, aBits) = members[0];
        var (bName, bBits) = members[1];
        Console.WriteLine($"  using flags A={aName} (0x{aBits:X}) B={bName} (0x{bBits:X})");
        Console.WriteLine();

        static Quest FreshQuest() =>
            new SkyrimMod(new ModKey("hc_flags_bit", ModType.Plugin), SkyrimRelease.SkyrimSE).Quests.AddNew();
        static ulong FlagBits(Quest q) => Convert.ToUInt64(q.Flags);
        static WriteRequest FReq(string verb, string? value, string? key = null) =>
            new() { RecordType = "Quest", Path = new[] { "Flags" }, Verb = verb, Value = value, Key = key };

        var rb = CorpusRulebook.Load();

        // ---- PRE-ACCEPT (the report's refusal, now admitted): pre-flight ACCEPTS flags Add and Remove.
        var preAdd = rb.Validate(FReq("Add", aName));
        Check("PRE-ACCEPT: pre-flight ACCEPTS Add of a flag on Quest.Flags", preAdd is null, preAdd);
        var preRem = rb.Validate(FReq("Remove", aName));
        Check("PRE-ACCEPT: pre-flight ACCEPTS Remove of a flag on Quest.Flags", preRem is null, preRem);

        // ---- PRE-BADFLAG (Q3): a bogus flag NAME is REFUSED at the gate, never accepted-then-thrown at apply.
        var preBad = rb.Validate(FReq("Add", "NotARealQuestFlag"));
        Check("PRE-BADFLAG: pre-flight REFUSES Add of a bogus flag value (no accept-then-throw)", preBad is not null,
            preBad ?? "ACCEPTED — a bogus flag would Enum.Parse-throw at apply");

        // ---- PRE-KEY: a bit verb takes NO key (it is not a collection) — a stray key is refused, naming 'key'.
        var preKey = rb.Validate(FReq("Add", aName, key: "0"));
        Check("PRE-KEY: pre-flight REFUSES a flags Add with a stray key", preKey is not null && preKey.Contains("key", StringComparison.OrdinalIgnoreCase),
            preKey ?? "ACCEPTED — a flags bit verb takes no key");

        // ---- PRE-SCALAR (control): a plain scalar STILL refuses Add — the acceptance is scoped to [Flags], not universal.
        var preScalar = rb.Validate(new WriteRequest { RecordType = "Weapon", Path = new[] { "BasicStats", "Damage" }, Verb = "Add", Value = "5" });
        Check("PRE-SCALAR: pre-flight still REFUSES Add on a plain scalar (Weapon.BasicStats.Damage), names '[Flags]'",
            preScalar is not null && preScalar.Contains("[Flags]", StringComparison.Ordinal),
            preScalar ?? "ACCEPTED — Add must NOT be universal");

        // ---- NULLABLE-FLAGS whole-clear PRESERVATION (PR #202 review): the bit verbs must ADD capability without
        //      removing any. A VALUELESS Remove keeps its pre-bit-verb meaning — the whole-field clear of a NULLABLE
        //      scalar, the only path to make a nullable flags field absent (null) — so it is still accepted on a
        //      nullable flags field and refused (with a turn-all-off redirect) only on a NON-nullable one.
        Console.WriteLine();
        Console.WriteLine("── NULLABLE flags: valueless Remove still whole-clears (nullable) / refused w/ redirect (non-nullable) ──");
        var preNoVal = rb.Validate(FReq("Remove", null));
        Check("NONNULL-VALUELESS: valueless Remove on non-nullable Quest.Flags is REFUSED, names the Set '0' redirect (not a dead end)",
            preNoVal is not null && preNoVal.Contains("'0'", StringComparison.Ordinal),
            preNoVal ?? "ACCEPTED — a non-nullable flags field can't be whole-cleared");

        // Discover the ACTUAL nullable [Flags] surface from the live corpus (measures the change's blast radius, and if
        // any ROOT-record one exists, proves the gate still ACCEPTS its valueless whole-clear).
        var corpus = CorpusRulebook.LoadCorpus();
        var nullableFlags = new List<(string Type, string Field, bool Root)>();
        foreach (var ts in corpus.Types.Values)
            foreach (var f in ts.Fields)
                if (f.Cardinality == "enum" && f.Nullable && ResolvesToFlags(f.MutableTypeAssemblyQualified ?? f.GetterTypeAssemblyQualified))
                    nullableFlags.Add((ts.Name, f.Name, ts.Kind == "record"));
        Console.WriteLine($"  corpus nullable [Flags] fields: {nullableFlags.Count}" +
            (nullableFlags.Count > 0 ? " — e.g. " + string.Join(", ", nullableFlags.Take(6).Select(x => $"{x.Type}.{x.Field}")) : " (none — whole-clear preservation is vacuously safe, kept by construction)"));
        var rootNullable = nullableFlags.FirstOrDefault(x => x.Root);
        if (rootNullable.Type is not null)
        {
            var preNullClear = rb.Validate(new WriteRequest { RecordType = rootNullable.Type, Path = new[] { rootNullable.Field }, Verb = "Remove", Value = null });
            Check($"NULLABLE-VALUELESS: valueless Remove on a nullable flags field ({rootNullable.Type}.{rootNullable.Field}) is ACCEPTED (whole-clear preserved)",
                preNullClear is null, preNullClear);
        }
        else
            Console.WriteLine("  (no ROOT-record nullable flags field to gate-test; the leaf.Nullable branch is still exercised by the non-nullable refusal above)");

        // ---- A1 (apply TOOTH — the anti-clobber core): Set flag A, then Add flag B -> BOTH bits set. RED before the
        //      fix: Add on a flags enum was refused outright, so the only path was a Set that dropped A.
        bool a1Ok; string? a1Detail;
        try
        {
            var q = FreshQuest();
            WriteEngine.ApplyVerb(q, FReq("Set", aName));               // establish A only
            bool setTook = FlagBits(q) == aBits;
            WriteEngine.ApplyVerb(q, FReq("Add", bName));               // <- the tooth: OR B in, A must survive
            ulong after = FlagBits(q);
            a1Ok = setTook && (after & aBits) != 0 && (after & bBits) != 0 && after == (aBits | bBits);
            a1Detail = $"setTook={setTook} afterBits=0x{after:X} expected=0x{aBits | bBits:X}";
        }
        catch (Exception ex) { a1Ok = false; a1Detail = $"{ex.GetType().Name}: {ex.Message}"; }
        Check("A1: Add ORs a bit in and PRESERVES the pre-existing bit (no silent clobber)", a1Ok, a1Detail);

        // ---- A2 (apply TOOTH): from A|B, Remove B -> ONLY B cleared, A survives.
        bool a2Ok; string? a2Detail;
        try
        {
            var q = FreshQuest();
            WriteEngine.ApplyVerb(q, FReq("Set", $"{aName}, {bName}"));  // A|B via a comma-combo Set
            bool bothSet = FlagBits(q) == (aBits | bBits);
            WriteEngine.ApplyVerb(q, FReq("Remove", bName));            // clear only B
            ulong after = FlagBits(q);
            a2Ok = bothSet && (after & bBits) == 0 && (after & aBits) != 0 && after == aBits;
            a2Detail = $"bothSet={bothSet} afterBits=0x{after:X} expected=0x{aBits:X}";
        }
        catch (Exception ex) { a2Ok = false; a2Detail = $"{ex.GetType().Name}: {ex.Message}"; }
        Check("A2: Remove clears ONLY its bit, other bits preserved", a2Ok, a2Detail);

        // ---- A3 (idempotence): Add an already-set bit / Remove an unset bit are no-ops (pure bit math, not a toggle).
        bool a3Ok; string? a3Detail;
        try
        {
            var q = FreshQuest();
            WriteEngine.ApplyVerb(q, FReq("Set", aName));
            WriteEngine.ApplyVerb(q, FReq("Add", aName));               // already set -> unchanged
            bool addNoop = FlagBits(q) == aBits;
            WriteEngine.ApplyVerb(q, FReq("Remove", bName));           // not set -> unchanged
            bool remNoop = FlagBits(q) == aBits;
            a3Ok = addNoop && remNoop;
            a3Detail = $"addNoop={addNoop} remNoop={remNoop} bits=0x{FlagBits(q):X}";
        }
        catch (Exception ex) { a3Ok = false; a3Detail = $"{ex.GetType().Name}: {ex.Message}"; }
        Check("A3: Add of a set bit / Remove of an unset bit are no-ops (OR/AND-NOT, not toggle)", a3Ok, a3Detail);

        // ---- APPLY-SCALAR (control): Add on a plain scalar, driven straight through apply (bypassing pre-flight),
        //      STILL throws — the [Flags] branch did not swallow non-flags scalars.
        bool ctrlOk; string? ctrlDetail;
        try
        {
            var w = new SkyrimMod(new ModKey("hc_flags_ctrl", ModType.Plugin), SkyrimRelease.SkyrimSE).Weapons.AddNew();
            try
            {
                WriteEngine.ApplyVerb(w, new WriteRequest { RecordType = "Weapon", Path = new[] { "BasicStats", "Damage" }, Verb = "Add", Value = "5" });
                ctrlOk = false; ctrlDetail = "NO throw — Add was wrongly applied to a plain scalar";
            }
            catch (InvalidOperationException ex) { ctrlOk = true; ctrlDetail = ex.Message; }
        }
        catch (Exception ex) { ctrlOk = false; ctrlDetail = $"setup threw {ex.GetType().Name}: {ex.Message}"; }
        Check("APPLY-SCALAR: Add on a plain scalar still throws at apply (the [Flags] branch is scoped)", ctrlOk, ctrlDetail);

        // ---- E2E: Set A, Add B, SERIALIZE, re-read -> Flags came back as the UNION A|B (the clear/set PERSISTS).
        bool e2eOk; string? e2eDetail;
        var e2ePath = OutPath("hc_flags_bit_e2e");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(e2ePath)!);
            var mod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(e2ePath), ModType.Plugin), SkyrimRelease.SkyrimSE);
            var q = mod.Quests.AddNew();
            WriteEngine.ApplyVerb(q, FReq("Set", aName));
            WriteEngine.ApplyVerb(q, FReq("Add", bName));
            WriteEngine.WritePatch(mod, new ISkyrimModGetter[] { mod }, e2ePath);
            using var back = SkyrimMod.CreateFromBinaryOverlay(e2ePath, SkyrimRelease.SkyrimSE);
            var re = back.Quests.First();
            ulong reBits = Convert.ToUInt64(re.Flags);
            e2eOk = reBits == (aBits | bBits);
            e2eDetail = $"re-read Flags bits = 0x{reBits:X} expected 0x{aBits | bBits:X} ({re.Flags})";
        }
        catch (Exception ex) { e2eOk = false; e2eDetail = $"{ex.GetType().Name}: {ex.Message}"; }
        finally { CleanOut(e2ePath); }
        Check("E2E: Set+Add serializes AND re-reads as the UNION of both flags (persists through serialization)", e2eOk, e2eDetail);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "flags-bit-verb-guard: ALL PASS" : $"flags-bit-verb-guard: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Does the field's assembly-qualified type resolve to a <c>[Flags]</c> enum? The probe-side mirror of
    /// <c>CorpusRulebook.IsFlagsEnumLeaf</c>'s flags test — resolved via <c>Type.GetType(aq)</c> (Mutagen is loaded),
    /// so the discovery scan classifies exactly what the gate does.</summary>
    static bool ResolvesToFlags(string? aq)
    {
        if (string.IsNullOrEmpty(aq)) return false;
        var rt = Type.GetType(aq);
        if (rt is null) return false;
        var u = Nullable.GetUnderlyingType(rt) ?? rt;
        return u.IsEnum && u.IsDefined(typeof(FlagsAttribute), false);
    }

    static string OutPath(string stem) =>
        Path.Combine(Path.GetTempPath(), stem + "-" + Guid.NewGuid().ToString("N"), stem + ".esp");

    static void CleanOut(string outPath)
    {
        try { var dir = Path.GetDirectoryName(outPath); if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort */ }
    }
}

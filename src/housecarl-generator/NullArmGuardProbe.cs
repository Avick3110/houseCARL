using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD (standing CI instrument) for HCBR-2026-06-15-01 PR-C — two coupled needs.
///
/// PART A — the WIRE nested-compose gap. A compose's nested set (<see cref="StructInput"/>.<c>Sets[]</c>)
/// carried NO <c>compose</c> field, so a <c>sets[]</c> entry could only set a coercible scalar — never SELECT
/// a polymorphic sub-ARM (e.g. a Condition's <c>Data</c>). The core already applied AND validated a nested
/// <see cref="WriteRequest.Struct"/> end-to-end (<c>BuildStruct</c> recurses on a Set/Add carrying a Struct;
/// the rulebook's <c>ArmLegality</c> validates <c>compose.type</c> against the leaf's legal arms); the only
/// break was the wire→core mapping. THE FIX: <see cref="NestedSet"/> gained an optional <c>Compose</c>
/// (<see cref="StructInput"/>), and <see cref="LoadOrderService"/>.<c>MapStruct</c> propagates it recursively
/// into <see cref="WriteRequest.Struct"/>.
///
/// PART B — the serialize-boundary NULL-ARM refusal. A COMPOSED record that leaves a REQUIRED polymorphic
/// sub-field unset (the canonical case: a Condition composed without its <c>Data</c> arm) is null when
/// Mutagen's binary writer dereferences it → a bare <see cref="NullReferenceException"/> with NO field name.
/// Pre-flight can't reject it: the corpus now carries faithful polymorphic nullability (S4 Track D), but that flag
/// is NOT a "required arm at serialize" signal — this guard's own B2 proves NpcConfiguration.Level reads
/// <c>Nullable=false</c> yet serializes fine when null, while Condition.Data (also <c>Nullable=false</c>) throws. A
/// gate on the flag would over-reject a legitimately-absent field or need a hand-curated list (cornerstone §3), so
/// there is still no by-construction required/optional signal to gate on. THE FIX: <c>WriteEngine.WritePatch</c>
/// re-stamps the serialize-boundary NRE as a loud, NAMED <see cref="NullArmSerializeException"/> — all-or-nothing
/// (the staged temp is already discarded; the target is untouched), preserving the NRE as <c>InnerException</c>. Q3.
///
/// PART B (extended HCBR-2026-07-04) — single-gender GenderedItem formlink halves + the parallel-writer wrap. TWO coupled
/// fixes. (i) THE ROOT FIX: a GenderedItem whose element is a FormLink (an ArmorAddon's <c>SkinTexture</c>) authored with
/// only ONE gender half set used to leave the OTHER half a NULL formlink Mutagen's writer dereferenced (an NRE, wrapped in
/// nested <see cref="AggregateException"/>s on the parallel path). <c>WriteEngine.EmptyFormLinkOf</c> now materializes an
/// un-set formlink half as a NON-NULL empty link (mirroring the binary reader), so a fresh single-gender skin AA WRITES
/// (B3). (ii) THE SAFETY NET: <c>WriteEngine.RootNullArm</c> flattens an <see cref="AggregateException"/> and walks each
/// leaf to its root, re-stamping ONLY an all-NRE-rooted failure as the NAMED <see cref="NullArmSerializeException"/> — so a
/// COMPOSITION null-arm (a Condition without its Data arm, B1) renders NAMED whether it arrives bare or parallel-wrapped,
/// while a null MODEL half (WorldModel — Mutagen tolerates it, B4) and any other serialize error are untouched.
///
/// RED→GREEN: Part A checks A1/A2/A3/A5 are RED if <c>MapStruct</c> drops the nested Struct. Part B: B3 (single-gender skin
/// AA WRITES) is RED before the materializer fix (threw an AggregateException-wrapped NRE); B7 feeds the REAL Mutagen
/// parallel-wrapped null-arm through the actual serialize catch (a forced null half) and confirms it re-stamps NAMED, not
/// raw; R1–R5 unit-cover RootNullArm's unwrap (bare / doubly-nested / wrapper-of-NRE / mixed-non-NRE / non-NRE)
/// deterministically; B1 (composition null-arm -> NAMED refusal) stays GREEN. The FALSE-POSITIVE controls (B2 optional null
/// poly; B4 tolerated null MODEL half) and the illegal-nested-arm reject (A4) are GREEN before AND after — proving both
/// fixes are NARROW. B5 (explicit '0'-clear) and B6 (edit-existing overlay round-trip) confirm the single-gender record is
/// valid and existing records are unaffected.
///
/// Self-contained: A1/A2/A5/B1/B2 are pure in-memory Mutagen (no plugin file, no Skyrim.esm); A3/A4 use the
/// GENERATED corpus.json (built into a unique temp dir on a fresh checkout, exactly as poly-field-descend-guard does).
///
/// Run: <c>dotnet run --project src/housecarl-generator nullarm-guard</c>
/// </summary>
public static class NullArmGuardProbe
{
    [CiProbe("nullarm-guard")]
    public static int RunGuard(string[] args)
    {
        // CI-safe corpus: corpus.json is GENERATED, not tracked — on a fresh checkout build it into a UNIQUE
        // temp dir and point the rulebook there, leaving the working tree untouched; cleaned up on exit.
        string? tmp = null;
        if (!File.Exists(CorpusRulebook.CorpusPath))
        {
            tmp = Path.Combine(Path.GetTempPath(), "housecarl-nullarm-guard-" + Guid.NewGuid().ToString("N"));
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

        Console.WriteLine("nullarm-guard — nested compose (PR-C Part A) + serialize-boundary null-arm refusal (PR-C Part B)");
        Console.WriteLine();

        // The wire shape a caller would send: Add a Condition element whose polymorphic Data sub-field is SELECTED by
        // a nested compose. Before PR-C the inner `compose` could not be expressed (NestedSet had no such field) and,
        // even if it could, MapStruct dropped it.
        static StructInput ConditionInputWithData(string dataArm) => new()
        {
            Type = "ConditionFloat",
            Sets = new[] { new NestedSet { Path = "Data", Compose = new StructInput { Type = dataArm } } },
        };

        static WriteRequest AddCondition(StructSpec? spec) =>
            new() { RecordType = "ConstructibleObject", Path = new[] { "Conditions" }, Verb = "Add", Struct = spec };

        // ============================ PART A — wire→core nested compose ============================

        // ---- A1: MapStruct carries the nested compose into the nested WriteRequest.Struct (RED if dropped). ----
        var specWithData = LoadOrderService.MapStruct(ConditionInputWithData("GetActorValueConditionData"), "guard", out var mapErr);
        Check("A1: MapStruct propagates a nested compose into Sets[0].Struct",
            mapErr is null && specWithData?.Sets is { Count: 1 } && specWithData.Sets[0].Struct?.Type == "GetActorValueConditionData",
            mapErr ?? $"Sets[0].Struct = {specWithData?.Sets?[0].Struct?.Type ?? "null"}");

        // ---- A2: ApplyVerb of an Add(Conditions) built from that mapping yields a ConditionFloat whose Data IS the
        //          composed arm (in-memory; RED if Struct not carried → Data stays null / the nested Set throws). ----
        bool a2ok; string? a2Detail;
        try
        {
            var cobjA = new SkyrimMod(new ModKey("hc_nullarm_a2", ModType.Plugin), SkyrimRelease.SkyrimSE).ConstructibleObjects.AddNew();
            WriteEngine.ApplyVerb(cobjA, AddCondition(specWithData));
            a2ok = cobjA.Conditions is { Count: 1 } && cobjA.Conditions[0] is ConditionFloat { Data: GetActorValueConditionData };
            a2Detail = cobjA.Conditions.Count == 1
                ? $"Data arm = {(cobjA.Conditions[0] as ConditionFloat)?.Data?.GetType().Name ?? "null"}"
                : "no condition added";
        }
        catch (Exception ex) { a2ok = false; a2Detail = $"{ex.GetType().Name}: {ex.Message}"; }
        Check("A2: ApplyVerb builds the composed Data arm through the nested compose (in-memory)", a2ok, a2Detail);

        // ---- A3: pre-flight ACCEPTS the same wire-mapped Add (the nested compose validates through ArmLegality). ----
        var rb = CorpusRulebook.Load();
        var a3Err = rb.Validate(AddCondition(specWithData));
        Check("A3: pre-flight accepts the nested-composed Add (validates the sub-arm)", a3Err is null, a3Err);

        // ---- A4: Q3 teeth — an ILLEGAL nested arm rejects LOUD naming the legal arms. GREEN before+after. ----
        var badSpec = LoadOrderService.MapStruct(ConditionInputWithData("BogusConditionData"), "guard", out _);
        var a4Err = rb.Validate(AddCondition(badSpec));
        Check("A4: an illegal nested arm rejects naming the legal arms",
            a4Err is not null && a4Err.Contains("arm", StringComparison.OrdinalIgnoreCase) && a4Err.Contains("BogusConditionData", StringComparison.Ordinal),
            a4Err);

        // ---- A5: end-to-end — compose-Add a Condition WITH Data, then SERIALIZE: the record the nested-compose
        //          grammar builds is a writable plugin (the fix a user applies actually works through the wire path). ----
        var (a5ok, a5Detail) = TrySerialize("hc_nullarm_a5", mod =>
        {
            var cobj = mod.ConstructibleObjects.AddNew();
            WriteEngine.ApplyVerb(cobj, AddCondition(LoadOrderService.MapStruct(ConditionInputWithData("GetActorValueConditionData"), "guard", out _)));
        });
        Check("A5: a nested-composed Condition (Data set) serializes to a valid patch", a5ok, a5Detail);

        // ======================= PART B — serialize-boundary null-arm refusal =======================

        // ---- B1: compose-Add a ConditionFloat WITHOUT Data → serialize throws a NAMED NullArmSerializeException
        //          (not a bare NRE), names the cause, preserves the NRE as inner, and writes NOTHING. RED before. ----
        var b1Path = OutPath("hc_nullarm_b1");
        NullArmSerializeException? caught = null; Exception? wrong = null;
        try
        {
            SerializeTo(b1Path, mod =>
            {
                var cobj = mod.ConstructibleObjects.AddNew();
                WriteEngine.ApplyVerb(cobj, AddCondition(LoadOrderService.MapStruct(new StructInput { Type = "ConditionFloat" }, "guard", out _)));
            });
        }
        catch (NullArmSerializeException ex) { caught = ex; }
        catch (Exception ex) { wrong = ex; }
        Check("B1: a Condition composed without its Data arm fails as a NAMED NullArmSerializeException (not a bare NRE)",
            caught is not null, wrong is null ? "no exception thrown" : $"threw {wrong.GetType().Name} instead");
        Check("B1b: the refusal names the cause (compose) and preserves the NRE as InnerException",
            caught is not null && caught.Message.Contains("compose", StringComparison.OrdinalIgnoreCase) && caught.InnerException is NullReferenceException,
            caught?.Message);
        Check("B1c: nothing was written — all-or-nothing, the target is untouched", !File.Exists(b1Path));
        // B1d: the render boundary (WriteEngine.Describe, used by the three WritePatchBuilder serialize catches) keeps
        //      BOTH the loud NAMED outer AND the discriminating inner NRE — so a masked non-compose NRE never loses
        //      its only distinguishing signal (review should-fix; RED if Describe drops the inner). The pre-fix bare
        //      NRE has no inner, so the negative control below proves Describe only appends when an inner is present.
        var rendered = caught is null ? "" : WriteEngine.Describe(caught);
        Check("B1d: the render surfaces the named outer AND the inner NRE (no signal-stripping)",
            rendered.Contains("NullArmSerializeException", StringComparison.Ordinal)
                && rendered.Contains("[inner: NullReferenceException", StringComparison.Ordinal),
            rendered);
        Check("B1e: Describe appends NOTHING for an inner-less exception (the append is conditional, not noise)",
            WriteEngine.Describe(new InvalidOperationException("bare")) == "InvalidOperationException: bare");
        CleanOut(b1Path);

        // ---- B2: FALSE-POSITIVE control — records with genuinely-OPTIONAL null poly fields (a bare NPC whose
        //          Configuration.Level is null; an NPC with Level set but Sound null) serialize FINE. The catch must
        //          NEVER fire on a valid null poly. GREEN before+after. ----
        var (b2ok, b2Detail) = TrySerialize("hc_nullarm_b2", mod =>
        {
            mod.Npcs.AddNew();                                                            // bare NPC: Level + Sound null
            var n2 = mod.Npcs.AddNew(); n2.Configuration.Level = new NpcLevel { Level = 1 }; // Sound null, Level set
        });
        Check("B2: optional null polymorphic fields (NPC Sound/Level) still serialize fine (no false refusal)", b2ok, b2Detail);

        // ---- B3: a single-gender GenderedItem whose element is a FormLink (ArmorAddon SkinTexture, Female set /
        //          Male un-set) now WRITES a valid record — the materializer fills the un-set formlink half with a
        //          NON-NULL empty link (EmptyFormLinkOf), mirroring Mutagen's binary reader, so the writer no longer
        //          dereferences a null half. RED before the materializer fix (threw an AggregateException-wrapped NRE).
        //          This is the "fresh single-gender create just works" fix (HCBR-2026-07-04). ----
        var (b3ok, b3Detail) = TrySerialize("hc_nullarm_b3", mod =>
        {
            var arma = mod.ArmorAddons.AddNew();
            WriteEngine.ApplyVerb(arma, new WriteRequest { RecordType = "ArmorAddon", Path = new[] { "SkinTexture", "Female" }, Verb = "Set", Value = "000801:hc_nullarm_b3.esp" });
        });
        Check("B3: a single-gender skin AA (SkinTexture.Female only, Male un-set) now WRITES — fresh single-gender create just works", b3ok, b3Detail);

        // ---- B4: FALSE-POSITIVE control — a single-gender MODEL half (ArmorAddon WorldModel.Female, Male null) STILL
        //          serializes fine: Mutagen TOLERATES a null model half (writes only the female subrecord), so the
        //          widened catch must NOT refuse it. Proves RootNullArm fires only on an actual writer NRE, never on a
        //          legitimately-absent gender half. GREEN before+after (the fix is narrow — a model half is not a formlink half). ----
        var (b4ok, b4Detail) = TrySerialize("hc_nullarm_b4", mod =>
        {
            var arma = mod.ArmorAddons.AddNew();
            WriteEngine.ApplyVerb(arma, new WriteRequest { RecordType = "ArmorAddon", Path = new[] { "WorldModel", "Female", "File" }, Verb = "Set", Value = "meshes\\test.nif" });
        });
        Check("B4: a single-gender MODEL half (ArmorAddon WorldModel, Male null) still serializes fine (no false refusal)", b4ok, b4Detail);

        // ---- B5: EXPLICITLY clearing the other half ('0') also writes — belt-and-suspenders with B3's auto-empty
        //          materialization: whether the un-set half is auto-filled (B3) or the author clears it by hand, a
        //          single-gender skin AA serializes to a valid record. ----
        var (b5ok, b5Detail) = TrySerialize("hc_nullarm_b5", mod =>
        {
            var arma = mod.ArmorAddons.AddNew();
            WriteEngine.ApplyVerb(arma, new WriteRequest { RecordType = "ArmorAddon", Path = new[] { "SkinTexture", "Female" }, Verb = "Set", Value = "000801:hc_nullarm_b5.esp" });
            WriteEngine.ApplyVerb(arma, new WriteRequest { RecordType = "ArmorAddon", Path = new[] { "SkinTexture", "Male" }, Verb = "Set", Value = "0" });
        });
        Check("B5: SkinTexture.Female + Male explicitly cleared ('0') also serializes fine (belt-and-suspenders with B3's auto-empty half)", b5ok, b5Detail);

        // ---- B6: the EDIT-EXISTING path is SAFE — only fresh CREATE bites. Author a single-gender skin AA on disk
        //          (Female texture set, Male cleared), re-open it as a BinaryOverlay, deep-copy it into a fresh patch
        //          (exactly what forward_record / any in-place edit does), and serialize. Mutagen's binary READER fills
        //          the absent gender half with a NON-NULL empty FormLinkNullable (NOT the null interface the create
        //          materializer leaves), so the read->copy->serialize round-trip does NOT hit the null-arm refusal.
        //          GREEN before+after — the refusal is CREATE-only; existing mods' single-gender skin AAs (TSOSRefined
        //          NPC torsos, NPC hand-skins, …) forward/edit/compact fine. Confirmed live on 0UlfricNakedSkinTorso. ----
        string b6src = OutPath("hc_nullarm_b6src");
        string? b6out = null;   // declared out of the try so the finally cleans it on the FAILURE path too (WritePatch stages its GUID dir before it can throw)
        bool b6ok = false; string? b6Detail = null;
        try
        {
            SerializeTo(b6src, mod =>
            {
                var arma = mod.ArmorAddons.AddNew();
                WriteEngine.ApplyVerb(arma, new WriteRequest { RecordType = "ArmorAddon", Path = new[] { "SkinTexture", "Female" }, Verb = "Set", Value = "000801:hc_nullarm_b6src.esp" });
                WriteEngine.ApplyVerb(arma, new WriteRequest { RecordType = "ArmorAddon", Path = new[] { "SkinTexture", "Male" }, Verb = "Set", Value = "0" });
            });
            using var overlay = SkyrimMod.CreateFromBinaryOverlay(b6src, SkyrimRelease.SkyrimSE);
            b6out = OutPath("hc_nullarm_b6out");
            var patch = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(b6out), ModType.Plugin), SkyrimRelease.SkyrimSE);
            patch.ArmorAddons.Add(overlay.ArmorAddons.First().DeepCopy());   // the forward/edit deep-copy of an overlay getter
            WriteEngine.WritePatch(patch, new ISkyrimModGetter[] { overlay }, b6out);
            b6ok = File.Exists(b6out);
        }
        catch (Exception ex) { b6Detail = $"{ex.GetType().Name}: {ex.Message}"; }
        finally { CleanOut(b6src); if (b6out is not null) CleanOut(b6out); }
        Check("B6: an on-disk single-gender skin AA round-trips (overlay -> deep-copy -> serialize) without a null-arm refusal — edit-existing is safe, only CREATE bites", b6ok, b6Detail);

        // ---- B7: FAITHFUL real-Mutagen coverage of the wrapped-path safety net. R1–R5 cover RootNullArm's unwrap
        //          LOGIC against synthetic shapes; this proves the actual serialize CATCH handles the REAL exception
        //          Mutagen's parallel writer throws. The materializer now fills an un-set formlink half (so B3 writes),
        //          so the only way to reach a NULL half is to force it: null SkinTexture.Male AFTER materialization,
        //          then serialize. The genuine AggregateException(SubrecordException(NRE)) must be re-stamped as the
        //          NAMED NullArmSerializeException, not rendered raw. (Defensive: this state no longer arises via any
        //          normal op — it documents that IF it ever did, the safety net still fails loud + named.) ----
        var b7Path = OutPath("hc_nullarm_b7");
        NullArmSerializeException? b7caught = null; Exception? b7wrong = null;
        try
        {
            SerializeTo(b7Path, mod =>
            {
                var arma = mod.ArmorAddons.AddNew();
                WriteEngine.ApplyVerb(arma, new WriteRequest { RecordType = "ArmorAddon", Path = new[] { "SkinTexture", "Female" }, Verb = "Set", Value = "000801:hc_nullarm_b7.esp" });
                arma.SkinTexture!.GetType().GetProperty("Male")!.SetValue(arma.SkinTexture, null);   // force the pre-fix null formlink half
            });
        }
        catch (NullArmSerializeException ex) { b7caught = ex; }
        catch (Exception ex) { b7wrong = ex; }
        Check("B7: the REAL parallel-wrapped null-arm (a forced null gendered formlink half) is re-stamped as the NAMED NullArmSerializeException, not rendered raw",
            b7caught is not null, b7wrong is null ? "no exception thrown (serialize unexpectedly succeeded)" : $"threw {b7wrong.GetType().Name}: {b7wrong.Message}");
        Check("B7b: nothing was written — all-or-nothing", !File.Exists(b7Path));
        CleanOut(b7Path);

        // ---- R1–R5: RootNullArm unwrap LOGIC — DETERMINISTIC unit coverage against SYNTHETIC exception shapes. The
        //          real-Mutagen wrapped path is covered end-to-end by B7 above; these pin the branch logic cheaply and
        //          without depending on Mutagen's parallel scheduling. R2 matches the report's captured message shape
        //          (nested AggregateExceptions around the NRE); R3's inner wrapper is a structural STAND-IN for Mutagen's
        //          SubrecordException — the inner-walk is wrapper-type-agnostic, so any wrapper-of-NRE exercises it (the
        //          REAL SubrecordException is what B7 feeds through). RootNullArm re-stamps IFF the ROOT cause is an NRE,
        //          else returns null so a genuine other error is never masked (Q3). ----
        var nre = new NullReferenceException("x");
        Check("R1: a BARE NRE unwraps to itself",
            ReferenceEquals(WriteEngine.RootNullArm(nre), nre));
        Check("R2: a doubly-nested AggregateException(NRE leaf) flattens + unwraps to the NRE",
            ReferenceEquals(WriteEngine.RootNullArm(new AggregateException(new AggregateException(nre))), nre));
        Check("R3: a wrapper-of-NRE leaf (SubrecordException-style stand-in) — the inner chain is walked to the NRE",
            ReferenceEquals(WriteEngine.RootNullArm(new AggregateException(new AggregateException(new InvalidOperationException("subrecord-style wrapper", nre)))), nre));
        Check("R4: an aggregate with a NON-NRE-rooted leaf returns null (the genuine error keeps its own type/message — not masked)",
            WriteEngine.RootNullArm(new AggregateException(nre, new InvalidOperationException("a real, different serialize error"))) is null);
        Check("R5: a non-NRE-rooted throw returns null (propagates unchanged)",
            WriteEngine.RootNullArm(new InvalidOperationException("not a null-arm")) is null);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "nullarm-guard: ALL PASS" : $"nullarm-guard: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    // --- serialize helpers: a self-contained patch through the REAL WriteEngine.WritePatch (pure in-memory) ---

    static string OutPath(string stem) =>
        Path.Combine(Path.GetTempPath(), stem + "-" + Guid.NewGuid().ToString("N"), stem + ".esp");

    static void SerializeTo(string outPath, Action<SkyrimMod> build)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var mod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outPath), ModType.Plugin), SkyrimRelease.SkyrimSE);
        build(mod);
        WriteEngine.WritePatch(mod, new ISkyrimModGetter[] { mod }, outPath);
    }

    static (bool ok, string? detail) TrySerialize(string stem, Action<SkyrimMod> build)
    {
        var outPath = OutPath(stem);
        try { SerializeTo(outPath, build); var ok = File.Exists(outPath); CleanOut(outPath); return (ok, ok ? null : "no file written"); }
        catch (Exception ex) { CleanOut(outPath); return (false, $"{ex.GetType().Name}: {ex.Message}"); }
    }

    static void CleanOut(string outPath)
    {
        try { var dir = Path.GetDirectoryName(outPath); if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort */ }
    }
}

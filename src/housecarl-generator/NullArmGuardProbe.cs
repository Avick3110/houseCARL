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
/// RED→GREEN: Part A checks A1/A2/A3/A5 are RED if <c>MapStruct</c> drops the nested Struct; Part B check B1 is
/// RED (a bare <see cref="NullReferenceException"/>, not a <see cref="NullArmSerializeException"/>) without the
/// <c>WritePatch</c> catch. The FALSE-POSITIVE control (B2) and the illegal-nested-arm reject (A4) are GREEN
/// before AND after — they prove the fix is NARROW (a valid optional-null poly still serializes; an illegal
/// nested arm still rejects loud).
///
/// Self-contained: A1/A2/A5/B1/B2 are pure in-memory Mutagen (no plugin file, no Skyrim.esm); A3/A4 use the
/// GENERATED corpus.json (built into a unique temp dir on a fresh checkout, exactly as poly-field-descend-guard does).
///
/// Run: <c>dotnet run --project src/housecarl-generator nullarm-guard</c>
/// </summary>
public static class NullArmGuardProbe
{
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

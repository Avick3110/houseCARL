using System.Reflection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD (standing CI instrument) for HCBR-2026-06-22 — a condition FormLinkOrIndex target set via the
/// flat compose <c>fields:</c> shorthand.
///
/// THE GAP (reproduced verbatim against the live load order): composing a condition Data arm and setting its
/// FormLinkOrIndex ("FLOI") target through the flat <c>fields:</c> map — e.g.
/// <c>compose GetEquippedConditionData {ItemOrList:"0001F4:Skyrim.esm"}</c> — was REFUSED at PRE-FLIGHT with
/// <c>'…' does not coerce to FormLink&lt;IItemOrListGetter&gt;</c>, and would also have thrown at APPLY. A FLOI does
/// not coerce via the parentless Coerce family: its parent arm carries the form/alias mode bit, set by
/// <see cref="WriteEngine"/>.<c>SetFloi</c>. The nested <c>sets:</c> path ALREADY handled FLOI (it routes through
/// <c>ApplyScalarVerb</c>'s <c>IsFormLinkOrIndex</c> gate at apply, and <c>ValidateFromType</c>'s formlink branch at
/// pre-flight), but the flat <c>fields:</c> shorthand bypassed BOTH — so the natural shorthand failed on every FLOI
/// condition param (GetEquipped.ItemOrList, GetGlobalValue.Global, GetIsID.Object, …) while the verbose form worked.
///
/// THE FIX (general FLOI surface, by construction — recognised via the engine's shared <c>IsFormLinkOrIndex</c>, no
/// per-arm wiring): (1) pre-flight — <c>CorpusRulebook.CheckValue</c> validates a FLOI-typed field's value with
/// <c>WriteEngine.TryClassifyFloiValue</c> (the SAME recognizer the <c>sets:</c> formlink branch uses) instead of
/// <c>TryCoerce</c>; (2) apply — <c>WriteEngine.BuildStruct</c>'s flat-field loop routes a FLOI field to
/// <c>SetFloi(instance, prop, val)</c> (the just-built struct IS the flag-bearing arm) instead of the parentless
/// <c>Coerce</c>. Both mirror the gate the verbose <c>sets:</c> path already had, so the two compose entry points
/// produce the IDENTICAL write.
///
/// RED→GREEN — the TEETH: T1 (apply via <c>fields:</c> lands the FLOI in form mode — RED without the BuildStruct
/// branch: Coerce throws), T2 (pre-flight ACCEPTS the same <c>fields:</c> Add — RED without the CheckValue branch:
/// "does not coerce"), T3 (the <c>fields:</c> write is BYTE-IDENTICAL to the <c>sets:</c> write — the parity proof),
/// T-IDX (a fields: index-mode value lands in alias mode — RED without the fix; proves the fix is not form-mode-only).
/// CONTROLS, GREEN before AND after: C-SETS (the verbose <c>sets:</c> path still lands the FLOI — proves it was never
/// the broken one), C-BAD (a MALFORMED fields: FLOI value still REJECTS at pre-flight, now naming "condition target" —
/// the accuracy guard against an over-broad accept).
///
/// Self-contained: the apply / serialize arms are pure in-memory Mutagen (no plugin file, no Skyrim.esm — the
/// "0001F4:Skyrim.esm" target is a FormKey value, never resolved); the pre-flight arms use the GENERATED corpus.json
/// (built into a unique temp dir on a fresh checkout, exactly as nullarm-guard / formlink-null-guard do).
///
/// Run: <c>dotnet run --project src/housecarl-generator floi-fields-guard</c>
/// </summary>
public static class FloiFieldsProbe
{
    const string RecType = "ConstructibleObject";   // COBJ has a Conditions list (as nullarm-guard / floi-read-guard use)
    const string Arm = "GetEquippedConditionData";
    const string FloiField = "ItemOrList";           // a FormLinkOrIndex<IItemOrListGetter> on the arm
    const string FormVal = "0001F4:Skyrim.esm";      // Iron Sword — a real item; form mode (contains ':')

    public static int RunGuard(string[] args)
    {
        // CI-safe corpus: corpus.json is GENERATED, not tracked — on a fresh checkout build it into a UNIQUE temp dir
        // and point the rulebook there (the pre-flight arms need it), leaving the working tree untouched; cleaned on exit.
        string? tmp = null;
        if (!File.Exists(CorpusRulebook.CorpusPath))
        {
            tmp = Path.Combine(Path.GetTempPath(), "housecarl-floi-fields-guard-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            Console.WriteLine($"corpus.json absent — generating into {tmp} (CI / fresh checkout)…");
            var rc = CorpusGenerator.GenerateAll(Path.Combine(tmp, "generated"), Path.Combine(tmp, "refs"));
            if (rc != 0) { Console.Error.WriteLine("error: corpus generation failed"); return rc; }
            CorpusRulebook.CorpusPath = Path.Combine(tmp, "generated", "corpus.json");
        }
        try { return RunChecks(); }
        finally { if (tmp is not null) { try { Directory.Delete(tmp, recursive: true); } catch { /* best-effort */ } } }
    }

    // The compose request a caller sends: Add a ConditionFloat to COBJ.Conditions whose polymorphic Data arm sets a
    // FLOI target — the FLOI set EITHER via the flat fields: shorthand (the path under test) or via nested sets: (the
    // already-working path). Everything else about the two requests is identical, so a byte diff isolates the FLOI write.
    static WriteRequest AddCondition(bool viaFields, string value)
    {
        var armSpec = viaFields
            ? new StructSpec { Type = Arm, Fields = new() { [FloiField] = value } }
            : new StructSpec
            {
                Type = Arm,
                Sets = new() { new() { RecordType = RecType, Path = new[] { FloiField }, Verb = "Set", Value = value } },
            };
        var condSpec = new StructSpec
        {
            Type = "ConditionFloat",
            Sets = new() { new() { RecordType = RecType, Path = new[] { "Data" }, Verb = "Set", Struct = armSpec } },
        };
        return new() { RecordType = RecType, Path = new[] { "Conditions" }, Verb = "Add", Struct = condSpec };
    }

    static int RunChecks()
    {
        int failures = 0;
        void Check(string label, bool ok, string? detail = null)
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}{(ok || detail is null ? "" : $"\n        -> {detail}")}");
            if (!ok) failures++;
        }

        Console.WriteLine("floi-fields-guard — condition FLOI target via the flat compose fields: shorthand (HCBR-2026-06-22)");
        Console.WriteLine();

        var expectFk = FormKey.Factory(FormVal);

        // ---- T1 (apply TOOTH): ApplyVerb of the fields: request lands the FLOI in FORM mode with the right FormKey.
        //      RED before the BuildStruct branch — the parentless Coerce throws "does not coerce to FormLink<…>". ----
        var (t1ok, t1detail) = ApplyAndReadFloi(viaFields: true, FormVal);
        Check("T1: apply via fields: lands ItemOrList in form mode (FormKey + UseAliases/UsePackageData false)",
            t1ok.HasValue && t1ok.Value.fk == expectFk && !t1ok.Value.useAliases && !t1ok.Value.usePackData, t1detail);

        // ---- T2 (pre-flight TOOTH): the SAME fields: Add validates clean. RED before the CheckValue branch
        //      ("does not coerce to FormLink<IItemOrListGetter>"). ----
        var rb = CorpusRulebook.Load();
        var t2err = rb.Validate(AddCondition(viaFields: true, FormVal));
        Check("T2: pre-flight accepts a FLOI target set via fields: (was 'does not coerce')", t2err is null, t2err);

        // ---- T3 (parity PROOF): the fields: write is BYTE-IDENTICAL to the sets: write. The strongest statement of
        //      "full parity" — the shorthand produces exactly the verbose path's bytes, not merely a passing apply. ----
        var (t3ok, t3detail) = BytesEqual(
            serializeA: BuildCobj(viaFields: true, FormVal),
            serializeB: BuildCobj(viaFields: false, FormVal));
        Check("T3: the fields: write is byte-identical to the sets: write (full parity)", t3ok, t3detail);

        // ---- T-IDX (apply TOOTH, the other mode): a fields: INDEX-mode value ('alias 5') lands in alias mode. RED
        //      before the fix; proves the fix routes BOTH FLOI modes through SetFloi, not just form mode. ----
        var (idxOk, idxDetail) = ApplyAndReadFloi(viaFields: true, "alias 5");
        Check("T-IDX: apply via fields: 'alias 5' lands in alias index mode (UseAliases true, Index 5)",
            idxOk.HasValue && idxOk.Value.useAliases && !idxOk.Value.usePackData && idxOk.Value.index == 5, idxDetail);

        // ---- C-SETS (CONTROL, green before+after): the verbose sets: path still lands the FLOI — proves it was never
        //      the broken one, and is the baseline T3 compares against. ----
        var (cSetsOk, cSetsDetail) = ApplyAndReadFloi(viaFields: false, FormVal);
        Check("C-SETS: the verbose sets: path still lands the FLOI in form mode (unbroken control)",
            cSetsOk.HasValue && cSetsOk.Value.fk == expectFk && !cSetsOk.Value.useAliases, cSetsDetail);

        // ---- C-BAD (Q3 accuracy CONTROL, green before+after): a MALFORMED fields: FLOI value still REJECTS at
        //      pre-flight (before the fix by "does not coerce"; after, by the FLOI recognizer naming "condition
        //      target"). The control is the REJECTION itself — the fix must not open an over-broad accept. The
        //      separate sub-check asserts the post-fix message is the actionable FLOI one, not the misleading coerce one. ----
        var cBadErr = rb.Validate(AddCondition(viaFields: true, "notaformkey notanindex"));
        Check("C-BAD: a malformed FLOI value via fields: still rejects at pre-flight (no over-broad accept)",
            cBadErr is not null, "ACCEPTED — over-broad");
        Check("C-BAD2: the malformed-FLOI rejection names 'condition target' (actionable, not the coerce message)",
            cBadErr is not null && cBadErr.Contains("condition target", StringComparison.OrdinalIgnoreCase), cBadErr);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "floi-fields-guard: ALL PASS" : $"floi-fields-guard: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    // ---- apply a compose request to a fresh COBJ and read back its condition's FLOI target (reflection, robust to a
    //      Mutagen reshape of the FLOI accessor; mirrors ConditionProbe's live read). ----
    static ((FormKey fk, uint index, bool useAliases, bool usePackData)? value, string? detail)
        ApplyAndReadFloi(bool viaFields, string value)
    {
        try
        {
            var cobj = new SkyrimMod(new ModKey("hc_floi_fields", ModType.Plugin), SkyrimRelease.SkyrimSE).ConstructibleObjects.AddNew();
            WriteEngine.ApplyVerb(cobj, AddCondition(viaFields, value));
            if (cobj.Conditions is not { Count: 1 }) return (null, "no condition added");
            var data = cobj.Conditions[0].GetType().GetProperty("Data")?.GetValue(cobj.Conditions[0]);
            if (data is null) return (null, "Data arm null");
            if (data.GetType().Name != Arm) return (null, $"Data arm = {data.GetType().Name}");
            var floi = data.GetType().GetProperty(FloiField)?.GetValue(data);
            if (floi is null) return (null, $"{FloiField} null");
            var link = floi.GetType().GetProperty("Link")?.GetValue(floi);
            var fk = link?.GetType().GetProperty("FormKey")?.GetValue(link) is FormKey k ? k : default;
            var index = (uint)(floi.GetType().GetProperty("Index")?.GetValue(floi) ?? 0u);
            var ua = (bool)(data.GetType().GetProperty("UseAliases")?.GetValue(data) ?? false);
            var up = (bool)(data.GetType().GetProperty("UsePackageData")?.GetValue(data) ?? false);
            return ((fk, index, ua, up), $"FormKey={fk} Index={index} UseAliases={ua} UsePackageData={up}");
        }
        catch (Exception ex) { return (null, $"{ex.GetType().Name}: {ex.Message}"); }
    }

    // ---- build a COBJ with one composed condition (used by the byte-parity proof). ----
    static void BuildCobj(SkyrimMod mod, bool viaFields, string value)
    {
        var cobj = mod.ConstructibleObjects.AddNew();
        WriteEngine.ApplyVerb(cobj, AddCondition(viaFields, value));
    }

    static Action<SkyrimMod> BuildCobj(bool viaFields, string value) => mod => BuildCobj(mod, viaFields, value);

    // ---- serialize two builds through the REAL WriteEngine.WritePatch and byte-compare the .esp output. ----
    static (bool ok, string? detail) BytesEqual(Action<SkyrimMod> serializeA, Action<SkyrimMod> serializeB)
    {
        var (okA, pa, ba) = Serialize("hc_floi_fields_a", serializeA);
        var (okB, pb, bb) = Serialize("hc_floi_fields_b", serializeB);
        try
        {
            if (!okA) return (false, $"fields: write failed: {pa}");
            if (!okB) return (false, $"sets: write failed: {pb}");
            if (ba!.Length != bb!.Length) return (false, $"SIZE {ba.Length} != {bb.Length}");
            for (int i = 0; i < ba.Length; i++)
                if (ba[i] != bb[i]) return (false, $"@0x{i:X} fields=0x{ba[i]:X2} sets=0x{bb[i]:X2}");
            return (true, null);
        }
        finally { CleanOut(pa); CleanOut(pb); }
    }

    static (bool ok, string detail, byte[]? bytes) Serialize(string stem, Action<SkyrimMod> build)
    {
        var outPath = Path.Combine(Path.GetTempPath(), stem + "-" + Guid.NewGuid().ToString("N"), stem + ".esp");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            var mod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outPath), ModType.Plugin), SkyrimRelease.SkyrimSE);
            build(mod);
            WriteEngine.WritePatch(mod, new ISkyrimModGetter[] { mod }, outPath);
            return File.Exists(outPath) ? (true, outPath, File.ReadAllBytes(outPath)) : (false, "no file written", null);
        }
        catch (Exception ex) { return (false, $"{ex.GetType().Name}: {ex.Message}", null); }
    }

    static void CleanOut(string path)
    {
        try { var dir = Path.GetDirectoryName(path); if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort */ }
    }
}

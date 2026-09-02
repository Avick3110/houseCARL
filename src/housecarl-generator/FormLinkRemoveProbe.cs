using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD (standing CI instrument) for HCBR-2026-07-06 — Remove on a scalar FormLink threw at apply.
///
/// THE GAP: <c>Remove</c> on a NULLABLE scalar FormLink (the report's case: <c>Npc.HeadTexture</c> / WNAM) was
/// ACCEPTED by pre-flight (<see cref="CorpusRulebook"/> permits Remove on any nullable scalar) but THREW
/// <c>TargetInvocationException</c> at apply: <see cref="WriteEngine"/>.<c>ApplyScalarVerb</c>'s Remove case did
/// <c>prop.SetValue(parent, null)</c>, and a FormLink field is a struct-backed slot whose setter REJECTS a null
/// reference. A Q3 accept-then-throw hole on every nullable FormLink leaf; the only workaround was Set = "000000:…"
/// (which coerces an EMPTY link and writes clean).
///
/// THE FIX (by construction, no per-type wiring): the Remove case now clears through
/// <see cref="WriteEngine"/>.<c>EmptyFormLinkOf</c> — a FormLink-family property gets its EMPTY link (FormKey.Null),
/// every other nullable scalar / substruct / polymorphic still gets <c>null</c> (EmptyFormLinkOf returns null for
/// non-FormLink types). So Remove on a nullable FormLink is now identical to Set = "0" (the null-synonym clear that
/// already worked — see formlink-null-guard). The non-FormLink half is unchanged and is guarded by
/// substruct-nullable-clear-guard (nullable substruct / poly Remove), which rides the SAME ApplyScalarVerb line.
///
/// RED-&gt;GREEN: the TEETH are R1/R1b (apply Remove on a nullable FormLink CLEARS it instead of throwing — RED
/// without the EmptyFormLinkOf route), GUARD-REQUIRED (apply Remove on a REQUIRED FormLink, bypassing pre-flight,
/// FAILS LOUD instead of silently blanking — RED without the IsRequiredFormLink guard; PR #150 review), and R2 (the
/// same nullable clear, serialized then RE-READ from disk as FormKey.Null). CONTROLS: S0 (the test fields have the
/// expected nullability — a Mutagen reshape can't pass green silently), PRE-NULLABLE (pre-flight still ACCEPTS Remove
/// on a nullable FormLink — the report's "pre-flight accepted it" half), PRE-REQUIRED (pre-flight still REFUSES Remove
/// on a REQUIRED FormLink, naming 'non-nullable' — the first line of defense; GUARD-REQUIRED is the apply-layer twin
/// for a caller that bypasses it).
///
/// Self-contained: R1/R1b/R2 are pure in-memory Mutagen (no plugin file, no Skyrim.esm); the PRE-* checks use the
/// GENERATED corpus.json (built into a unique temp dir on a fresh checkout, exactly as formlink-null-guard does).
///
/// Run: <c>dotnet run --project src/housecarl-generator formlink-remove-guard</c>
/// </summary>
public static class FormLinkRemoveProbe
{
    [CiProbe("formlink-remove-guard")]
    public static int RunGuard(string[] args)
    {
        // CI-safe corpus: corpus.json is GENERATED, not tracked — on a fresh checkout build it into a UNIQUE temp
        // dir and point the rulebook there (the pre-flight checks need it), leaving the working tree untouched.
        string? tmp = null;
        if (!File.Exists(CorpusRulebook.CorpusPath))
        {
            tmp = Path.Combine(Path.GetTempPath(), "housecarl-formlink-remove-guard-" + Guid.NewGuid().ToString("N"));
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

        Console.WriteLine("formlink-remove-guard — Remove clears a nullable scalar FormLink (apply) instead of throwing (HCBR-2026-07-06)");
        Console.WriteLine();

        // The three NPC FormLinks under test, verified against the runtime schema (NOT assumed):
        //   HeadTexture — IFormLinkNullable<ITextureSetGetter>  (nullable) — the report's EXACT field (WNAM)
        //   DeathItem   — IFormLinkNullable<ILeveledItemGetter> (nullable) — the formlink-null-guard sibling
        //   Race        — IFormLink<IRaceGetter>                (REQUIRED) — the pre-flight refusal control
        // Assert that nullability at RUNTIME so a Mutagen reshape that flips any of them can't pass green silently.
        Check("S0: test fields have the expected nullability (HeadTexture + DeathItem nullable, Race required)",
            IsNullableFormLink(typeof(INpc), "HeadTexture") && IsNullableFormLink(typeof(INpc), "DeathItem")
            && !IsNullableFormLink(typeof(INpc), "Race"));

        static Npc FreshNpc() =>
            new SkyrimMod(new ModKey("hc_formlink_remove", ModType.Plugin), SkyrimRelease.SkyrimSE).Npcs.AddNew();
        static FormKey Fk(object link) => ((IFormLinkGetter)link).FormKey;   // non-generic getter — works for nullable + required
        static WriteRequest SetReq(string field, string value) =>
            new() { RecordType = "Npc", Path = new[] { field }, Verb = "Set", Value = value };
        static WriteRequest RemReq(string field) =>
            new() { RecordType = "Npc", Path = new[] { field }, Verb = "Remove" };

        // ---- PRE-NULLABLE (control, the report's "pre-flight ACCEPTED it" half): Remove on a nullable FormLink still
        //      passes pre-flight. GREEN before AND after — the fix was in apply, not the gate.
        var rb = CorpusRulebook.Load();
        var preHead = rb.Validate(RemReq("HeadTexture"));
        Check("PRE-NULLABLE: pre-flight ACCEPTS Remove on a nullable FormLink (HeadTexture)", preHead is null, preHead);
        var preDeath = rb.Validate(RemReq("DeathItem"));
        Check("PRE-NULLABLE: pre-flight ACCEPTS Remove on a nullable FormLink (DeathItem)", preDeath is null, preDeath);

        // ---- PRE-REQUIRED (control): Remove on a REQUIRED FormLink is still REFUSED, naming 'non-nullable'. Proves the
        //      apply Remove case is only ever reached for nullable fields — the fix is scoped by the existing gate.
        var preRace = rb.Validate(RemReq("Race"));
        Check("PRE-REQUIRED: pre-flight REFUSES Remove on a required FormLink (Race), names 'non-nullable'",
            preRace is not null && preRace.Contains("non-nullable", StringComparison.OrdinalIgnoreCase),
            preRace ?? "ACCEPTED — a required link must not be silently clearable");

        // ---- R1 (apply TOOTH): Set a nullable FormLink to a real link, then Remove -> clears to FormKey.Null, no throw.
        //      RED before: prop.SetValue(parent, null) on a FormLink slot threw TargetInvocationException.
        foreach (var field in new[] { "HeadTexture", "DeathItem" })
        {
            bool ok; string? detail;
            try
            {
                var npc = FreshNpc();
                WriteEngine.ApplyVerb(npc, SetReq(field, "012345:Skyrim.esm"));
                var prop = typeof(Npc).GetProperty(field)!;
                bool setTook = !Fk(prop.GetValue(npc)!).IsNull;                 // the Set populated a real link first
                WriteEngine.ApplyVerb(npc, RemReq(field));                       // <- the tooth: threw before the fix
                bool cleared = Fk(prop.GetValue(npc)!).IsNull;                   // now an EMPTY link (FormKey.Null), not a throw
                ok = setTook && cleared;
                detail = $"setTook={setTook} cleared={cleared} finalKey={Fk(prop.GetValue(npc)!)}";
            }
            catch (Exception ex) { ok = false; detail = $"{ex.GetType().Name}: {ex.Message}"; }
            Check($"R1: Remove clears a populated nullable FormLink ({field}) with no throw", ok, detail);
        }

        // ---- GUARD-REQUIRED (Q3 tooth, PR #150 review): Remove on a REQUIRED FormLink reaching apply — a direct/CLI
        //      caller that bypasses pre-flight — must FAIL LOUD, not silently blank the link. RED without the
        //      IsRequiredFormLink guard: EmptyFormLinkOf returns a non-null empty FormLink<T>, so Race would be
        //      silently emptied with no throw. Drives ApplyVerb directly (pre-flight, tested above, would refuse it).
        bool gOk; string? gDetail;
        try
        {
            var npc = FreshNpc();
            WriteEngine.ApplyVerb(npc, SetReq("Race", "012345:Skyrim.esm"));   // a real, populated required link
            try
            {
                WriteEngine.ApplyVerb(npc, RemReq("Race"));                    // <- must throw, not blank
                gOk = false; gDetail = $"NO throw — Race silently became {Fk(npc.Race)} (the latent Q3 silent-degrade)";
            }
            catch (InvalidOperationException ex)
            {
                // threw loud AND left the link untouched (not blanked) — both halves of "no silent degrade"
                gOk = ex.Message.Contains("required", StringComparison.OrdinalIgnoreCase) && !Fk(npc.Race).IsNull;
                gDetail = ex.Message;
            }
        }
        catch (Exception ex) { gOk = false; gDetail = $"setup threw {ex.GetType().Name}: {ex.Message}"; }
        Check("GUARD-REQUIRED: Remove on a required FormLink (Race) throws loud, does NOT silently blank", gOk, gDetail);

        // ---- R2 (end-to-end, TIGHTENED per PR #150 review): set then Remove a nullable FormLink, SERIALIZE, then
        //      RE-READ the .esp and assert the link came back FormKey.Null — proving the clear PERSISTS through
        //      serialization, not just that a file was written. A serialize that wrote a wrong/garbage link is caught here.
        bool r2Ok; string? r2Detail;
        var r2Path = OutPath("hc_formlink_remove_e2e");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(r2Path)!);
            var mod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(r2Path), ModType.Plugin), SkyrimRelease.SkyrimSE);
            var npc = mod.Npcs.AddNew();
            WriteEngine.ApplyVerb(npc, SetReq("HeadTexture", "012345:Skyrim.esm"));
            WriteEngine.ApplyVerb(npc, RemReq("HeadTexture"));
            WriteEngine.WritePatch(mod, new ISkyrimModGetter[] { mod }, r2Path);
            using var back = SkyrimMod.CreateFromBinaryOverlay(r2Path, SkyrimRelease.SkyrimSE);
            var re = back.Npcs.First();
            r2Ok = re.HeadTexture.FormKey.IsNull;
            r2Detail = $"re-read HeadTexture.FormKey = {re.HeadTexture.FormKey}";
        }
        catch (Exception ex) { r2Ok = false; r2Detail = $"{ex.GetType().Name}: {ex.Message}"; }
        finally { CleanOut(r2Path); }
        Check("R2: a Remove-cleared nullable FormLink serializes AND re-reads as FormKey.Null (end-to-end round-trip)", r2Ok, r2Detail);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "formlink-remove-guard: ALL PASS" : $"formlink-remove-guard: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Runtime nullability of a FormLink property: IFormLinkNullable&lt;&gt; vs IFormLink&lt;&gt; — the same
    /// distinction the engine keys off in <see cref="WriteEngine"/>.<c>TryFormLink</c>. Keeps the probe honest if a
    /// Mutagen version reshapes a field from required to nullable (or back).</summary>
    static bool IsNullableFormLink(Type iface, string prop)
    {
        var pt = iface.GetProperty(prop)?.PropertyType;
        if (pt is null || !pt.IsGenericType) return false;
        return pt.GetGenericTypeDefinition().Name.StartsWith("IFormLinkNullable", StringComparison.Ordinal);
    }

    // --- serialize helper: a self-contained patch through the REAL WriteEngine.WritePatch (pure in-memory) ---

    static string OutPath(string stem) =>
        Path.Combine(Path.GetTempPath(), stem + "-" + Guid.NewGuid().ToString("N"), stem + ".esp");

    static void CleanOut(string outPath)
    {
        try { var dir = Path.GetDirectoryName(outPath); if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort */ }
    }
}

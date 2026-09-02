using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD (standing CI instrument) for HCBR-2026-06-15-01 PR-F — FormLink null-clear.
///
/// THE GAP (both halves confirmed via a live FormKey probe): a <c>Set</c> that CLEARS a FormLink with a
/// null-synonym ("00000000" / "0") threw <c>ArgumentException: Malformed FormKey string</c> at APPLY
/// (<c>FormKey.Factory</c>, inside <see cref="WriteEngine"/>.<c>TryFormLink</c>) — and PRE-FLIGHT ACCEPTED it,
/// because the formlink arm called the TYPE-ONLY <c>CoercibilityReject</c>, which never validated the value
/// string. A Q3 accept-then-throw hole on every FormLink leaf, plus no way to clear a REQUIRED link at all.
///
/// THE FIX (general FormLink family, by construction): one shared recognizer
/// <see cref="WriteEngine.IsFormKeyNullSynonym"/> ("0" / "00000000" / "Null" / "000000:Null" — trimmed,
/// case-insensitive, FULL-STRING) used by BOTH apply and pre-flight. On apply, a synonym routes to
/// <c>FormKey.Null</c> instead of <c>FormKey.Factory</c>; on pre-flight, the FORMLINK arm validates the value
/// shape (<see cref="WriteEngine.IsValidFormLinkValue"/>: synonym OR <c>FormKey.TryFactory</c> succeeds, else a
/// loud reject naming the legal forms). The substruct arm is UNTOUCHED (it keeps its own type-shape reject).
///
/// RED-&gt;GREEN: the TEETH are C1 (pre-flight now REJECTS a malformed value — RED without the value-shape check),
/// D for the ALL-ZEROS forms (apply CLEARS a nullable link instead of throwing — RED without ToFormKey), E (apply
/// CLEARS a REQUIRED link via all-zeros, the genuinely-new "Set Race = null" case — RED without ToFormKey), and F
/// (the same all-zeros clear, end-to-end through serialize). EMPIRICAL FINDING (proven by sabotage, baked into the
/// arms): only the ALL-ZEROS spellings ("0"/"00000000") are newly-enabled — <c>FormKey.Factory</c> ALREADY parsed
/// "Null"/"000000:Null" to FormKey.Null, so those D rows are CONTROLS (GREEN with the fix reverted), confirming the
/// shared recognizer routes the already-working spellings identically. Other CONTROLS, GREEN before AND after:
/// A (a real 6-hex FormID is NOT swallowed as a clear — full-string match; the accuracy guard the scope's critic
/// called for, 6-hex not 8), C2/C3 (pre-flight still accepts a synonym and a real FormID), S0 (field nullability).
///
/// Self-contained: A/D/E/F are pure in-memory Mutagen (no plugin file, no Skyrim.esm); C1/C2/C3 use the GENERATED
/// corpus.json (built into a unique temp dir on a fresh checkout, exactly as nullarm-guard / poly-field-descend do).
///
/// Run: <c>dotnet run --project src/housecarl-generator formlink-null-guard</c>
/// </summary>
public static class FormLinkNullProbe
{
    [CiProbe("formlink-null-guard")]
    public static int RunGuard(string[] args)
    {
        // CI-safe corpus: corpus.json is GENERATED, not tracked — on a fresh checkout build it into a UNIQUE temp
        // dir and point the rulebook there (the pre-flight arms need it), leaving the working tree untouched.
        string? tmp = null;
        if (!File.Exists(CorpusRulebook.CorpusPath))
        {
            tmp = Path.Combine(Path.GetTempPath(), "housecarl-formlink-null-guard-" + Guid.NewGuid().ToString("N"));
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

        Console.WriteLine("formlink-null-guard — FormLink null-clear (apply) + value-shape pre-flight (HCBR PR-F)");
        Console.WriteLine();

        // The two NPC formlinks under test, verified against the mutagen-reference schema (NOT assumed):
        //   Race      — FormLink<IRaceGetter>          (NON-nullable / REQUIRED) -> arm E "required link clear"
        //   DeathItem — FormLink<ILeveledItemGetter>?  (nullable)                -> arm D "clear via Set" + arm A
        // Assert that nullability at RUNTIME so a Mutagen reshape that flips either can't pass green silently.
        Check("S0: test fields have the expected nullability (Race required, DeathItem nullable)",
            !IsNullableFormLink(typeof(INpc), "Race") && IsNullableFormLink(typeof(INpc), "DeathItem"));

        static Npc FreshNpc() =>
            new SkyrimMod(new ModKey("hc_formlink_null", ModType.Plugin), SkyrimRelease.SkyrimSE).Npcs.AddNew();
        static FormKey Fk(object link) => ((IFormLinkGetter)link).FormKey;   // non-generic getter — works for nullable + required
        static WriteRequest SetReq(string field, string value) =>
            new() { RecordType = "Npc", Path = new[] { field }, Verb = "Set", Value = value };

        // ---- A (accuracy CONTROL): a REAL 6-hex FormID is NOT swallowed as a null-clear — it round-trips to that
        //      exact link. Guards against an over-broad recognizer (e.g. a prefix match on "0") that would clear a
        //      real target. The scope's critic required a 6-hex form here, not the original 8-hex. GREEN before+after.
        var real = FormKey.Factory("012345:Skyrim.esm");
        bool aOk; string? aDetail;
        try
        {
            var npc = FreshNpc();
            WriteEngine.ApplyVerb(npc, SetReq("DeathItem", "012345:Skyrim.esm"));
            aOk = Fk(npc.DeathItem) == real && !Fk(npc.DeathItem).IsNull;
            aDetail = $"DeathItem.FormKey = {Fk(npc.DeathItem)} (expected {real})";
        }
        catch (Exception ex) { aOk = false; aDetail = $"{ex.GetType().Name}: {ex.Message}"; }
        Check("A: a real 6-hex FormID round-trips, not swallowed as a null-clear", aOk, aDetail);

        // ---- C (pre-flight): TOOTH C1 — a malformed FormLink value is now REJECTED at the gate (RED before: the
        //      type-only CoercibilityReject accepted any value, then apply threw). C2/C3 are CONTROLS (a synonym and
        //      a real FormID are still accepted) — they prove the new check rejects ONLY garbage, not legal values.
        var rb = CorpusRulebook.Load();
        var cBadErr = rb.Validate(SetReq("Race", "notaformkey"));
        Check("C1: pre-flight REJECTS a malformed FormLink value (was accept-then-throw)",
            cBadErr is not null && cBadErr.Contains("FormLink", StringComparison.OrdinalIgnoreCase),
            cBadErr ?? "ACCEPTED — hole open");
        Check("C2: pre-flight accepts a null-clear synonym ('00000000')", rb.Validate(SetReq("Race", "00000000")) is null,
            rb.Validate(SetReq("Race", "00000000")));
        Check("C3: pre-flight accepts a real FormID", rb.Validate(SetReq("DeathItem", "012345:Skyrim.esm")) is null,
            rb.Validate(SetReq("DeathItem", "012345:Skyrim.esm")));

        // ---- D (apply): Set a NULLABLE formlink to each null-synonym -> CLEARS it (FormKey.Null), no throw. The
        //      ALL-ZEROS forms ("00000000"/"0") are the TEETH — RED before, because FormKey.Factory throws "Malformed
        //      FormKey string" on them. The "Null"/"000000:Null" forms are CONTROLS: FormKey.Factory ALREADY parsed
        //      those to FormKey.Null (verified by sabotage — they stay GREEN with the fix reverted), so they confirm
        //      the recognizer routes the already-working spellings identically. All four are first-class clears now.
        foreach (var syn in new[] { "00000000", "0", "Null", "000000:Null" })
        {
            bool dOk; string? dDetail;
            try
            {
                var npc = FreshNpc();
                WriteEngine.ApplyVerb(npc, SetReq("DeathItem", syn));
                dOk = Fk(npc.DeathItem).IsNull;
                dDetail = $"DeathItem.FormKey = {Fk(npc.DeathItem)}";
            }
            catch (Exception ex) { dOk = false; dDetail = $"{ex.GetType().Name}: {ex.Message}"; }
            Check($"D: Set nullable FormLink = '{syn}' clears it (no throw)", dOk, dDetail);
        }

        // ---- E (apply TOOTH): the genuinely-NEW case. Set a REQUIRED (non-nullable) formlink to an ALL-ZEROS clear
        //      ("Set Race = 00000000") -> clears to FormKey.Null. RED before (FormKey.Factory throws on all-zeros).
        //      Uses an all-zeros form ON PURPOSE: "Null"/"000000:Null" already parsed via Factory, so they would NOT
        //      prove the fix is load-bearing here. Distinct from Remove, which is invalid on a required link.
        bool eOk; string? eDetail;
        try
        {
            var npc = FreshNpc();
            WriteEngine.ApplyVerb(npc, SetReq("Race", "00000000"));
            eOk = Fk(npc.Race).IsNull;
            eDetail = $"Race.FormKey = {Fk(npc.Race)}";
        }
        catch (Exception ex) { eOk = false; eDetail = $"{ex.GetType().Name}: {ex.Message}"; }
        Check("E: Set Race = 00000000 clears a REQUIRED FormLink (the new all-zeros required-clear)", eOk, eDetail);

        // ---- F (end-to-end): the real user path — clear a link with an all-zeros synonym, then SERIALIZE to a valid
        //      patch (the clear actually persists to disk, not just in memory). RED before at the APPLY step (so it
        //      shares D/E's apply tooth); the serialize half is what makes it end-to-end. Once a link is FormKey.Null
        //      in memory, serialization is identical regardless of which synonym produced it.
        var (fOk, fDetail) = TrySerialize("hc_formlink_null_f", mod =>
        {
            var npc = mod.Npcs.AddNew();
            WriteEngine.ApplyVerb(npc, SetReq("Race", "00000000"));
        });
        Check("F: an all-zeros-cleared link serializes end-to-end to a valid patch", fOk, fDetail);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "formlink-null-guard: ALL PASS" : $"formlink-null-guard: {failures} FAILURE(S)");
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

    static (bool ok, string? detail) TrySerialize(string stem, Action<SkyrimMod> build)
    {
        var outPath = OutPath(stem);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            var mod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outPath), ModType.Plugin), SkyrimRelease.SkyrimSE);
            build(mod);
            WriteEngine.WritePatch(mod, new ISkyrimModGetter[] { mod }, outPath);
            var ok = File.Exists(outPath);
            CleanOut(outPath);
            return (ok, ok ? null : "no file written");
        }
        catch (Exception ex) { CleanOut(outPath); return (false, $"{ex.GetType().Name}: {ex.Message}"); }
    }

    static void CleanOut(string outPath)
    {
        try { var dir = Path.GetDirectoryName(outPath); if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort */ }
    }
}

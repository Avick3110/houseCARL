using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for CLEARING A NULLABLE SUBSTRUCT OR POLYMORPHIC field via Remove (S4 Track D —
/// D2 the VMAD "un-fragment" capability, plus the polymorphic follow-on). The whole capability is BY CONSTRUCTION:
/// CorpusGenerator reads a reference-typed substruct's OR polymorphic union field's NRT "?" annotation
/// (NullabilityInfoContext) and emits Nullable=true, so VerbLegality already permits Remove on it and ApplyScalarVerb's
/// Remove sets the property null — no new write path was added, the corrected schema completes it. This guard pins that
/// chain end to end (RED before the generator fix: every substruct/poly field was marked non-nullable, so VerbLegality
/// refused Remove and there was no way to un-fragment an INFO except remove_record + recreate):
///   PREFLIGHT-VMAD    — Remove DialogResponses.VirtualMachineAdapter (a nullable substruct) PASSES pre-flight.
///   PREFLIGHT-PROMPT  — Remove DialogResponses.Prompt (another nullable substruct) PASSES too — the fix is GENERAL,
///                       not VMAD-special-cased (both are nullable reference substructs Mutagen models with "?").
///   CONTROL-OBJBOUNDS — Remove Armor.ObjectBounds (a genuinely NON-nullable substruct) still REFUSES, naming
///                       'non-nullable' — proves the gate is nullability-DRIVEN, not "every substruct is now removable".
///   CONTROL-SET       — Set on the VMAD substruct is STILL refused (a substruct is navigated INTO, not coerced) —
///                       only Remove opened; the fix touched nullability data, not the Set path.
///   E2E-UNFRAGMENT    — an in-memory INFO carrying a VMAD, driven through the REAL WriteEngine.ApplyVerb Remove,
///                       comes back with VirtualMachineAdapter == null (was non-null) — the apply half, un-fragmented.
///   PREFLIGHT-POLY(2) — Remove a nullable standalone polymorphic field (Book.Teaches; Npc.Sound) PASSES — the same
///                       NRT read extends to union fields, general across them (the poly analog of PREFLIGHT-VMAD/PROMPT).
///   CONTROL-POLY-REQ  — Remove a genuinely NON-nullable poly field (MagicEffect.Archetype) still REFUSES — a required
///                       arm is never silently clearable. (Poly nullability is NOT a required-at-serialize signal and
///                       no gate keys on it — nullarm-guard B2; a truly-missing required arm fails at the serialize
///                       boundary, not here.)
///   E2E-POLY          — a Book carrying a Teaches arm, ApplyVerb Remove -> Teaches == null — the poly apply half.
/// </summary>
public static class SubstructNullableClearProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — clear a nullable substruct OR polymorphic field via Remove (S4 Track D)  ################");
        Console.WriteLine();

        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-substruct-nullable-clear-guard");
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        Directory.CreateDirectory(tmpDir);

        var genDir = Path.Combine(tmpDir, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(tmpDir, "corpus-ref"));
        var rulebook = CorpusRulebook.Load(Path.Combine(genDir, "corpus.json"));

        static WriteRequest Rem(string type, string field) =>
            new() { RecordType = type, Path = new[] { field }, Verb = "Remove" };

        // PREFLIGHT-VMAD: the D2 case — a nullable substruct is now Remove-able at pre-flight.
        var vmadReject = rulebook.Validate(Rem("DialogResponses", "VirtualMachineAdapter"));
        bool vmadOk = vmadReject is null;
        Console.WriteLine($"   PREFLIGHT-VMAD    Remove nullable substruct : {(vmadOk ? "PASS — Remove VirtualMachineAdapter accepted (nullable substruct)" : $"FAIL — reject=[{vmadReject}]")}");

        // PREFLIGHT-PROMPT: another nullable substruct — proves generality, not a VMAD special-case.
        var promptReject = rulebook.Validate(Rem("DialogResponses", "Prompt"));
        bool promptOk = promptReject is null;
        Console.WriteLine($"   PREFLIGHT-PROMPT  Remove another nullable   : {(promptOk ? "PASS — Remove Prompt accepted (fix is general, not VMAD-only)" : $"FAIL — reject=[{promptReject}]")}");

        // CONTROL-OBJBOUNDS: a genuinely non-nullable substruct still refuses — the gate is nullability-driven.
        var obReject = rulebook.Validate(Rem("Armor", "ObjectBounds"));
        bool obOk = obReject is not null && obReject.Contains("non-nullable", StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"   CONTROL-OBJBOUNDS non-nullable still refuses: {(obOk ? "PASS — Remove ObjectBounds refused, names 'non-nullable' (nullability-driven, not blanket)" : $"FAIL — reject=[{obReject}]")}");

        // CONTROL-SET: a substruct Set is still refused (navigate INTO it) — only Remove opened.
        var setReject = rulebook.Validate(new WriteRequest { RecordType = "DialogResponses", Path = new[] { "VirtualMachineAdapter" }, Verb = "Set", Value = "0" });
        bool setOk = setReject is not null;
        Console.WriteLine($"   CONTROL-SET       Set on substruct unchanged: {(setOk ? "PASS — Set on VirtualMachineAdapter still refused (navigate-in unchanged)" : "FAIL — Set on a substruct was accepted")}");

        // E2E-UNFRAGMENT: the apply half — an INFO with a VMAD, Removed through the real engine, comes back null.
        bool e2eOk = false;
        try
        {
            var info = new DialogResponses(new FormKey(new ModKey("HcSncClear", ModType.Plugin), 0x000800), SkyrimRelease.SkyrimSE);
            info.VirtualMachineAdapter = new DialogResponsesAdapter();
            bool had = info.VirtualMachineAdapter is not null;
            WriteEngine.ApplyVerb(info, Rem("DialogResponses", "VirtualMachineAdapter"));
            bool cleared = info.VirtualMachineAdapter is null;
            e2eOk = had && cleared;
            Console.WriteLine($"   E2E-UNFRAGMENT    Remove clears the VMAD    : {(e2eOk ? "PASS — INFO with a VMAD, ApplyVerb Remove -> VirtualMachineAdapter == null" : $"FAIL — had={had} cleared={cleared}")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   E2E-UNFRAGMENT    Remove clears the VMAD    : FAIL — threw {ex.GetType().Name}: {ex.Message}");
        }

        // ============ POLYMORPHIC arms (S4 Track D follow-on) — SAME mechanism, extended to union fields ============
        // CorpusGenerator now reads a standalone polymorphic (union) field's NRT "?" too, so a nullable poly field is
        // Remove-able by the IDENTICAL VerbLegality + ApplyScalarVerb path — no new write path, same as substruct.
        // NOTE: this poly nullability is NOT a "required arm at serialize" signal and NO gate keys on it — nullarm-guard
        // B2 proves NpcConfiguration.Level reads Nullable=false yet serializes fine when null, while Condition.Data
        // (also Nullable=false) throws. This guard pins ONLY the clear path; a genuinely-missing required arm stays
        // caught at the serialize boundary (WriteEngine.WritePatch's NullArmSerializeException).

        // PREFLIGHT-POLY: a nullable standalone poly field (Book.Teaches) is now Remove-able at pre-flight.
        var teachReject = rulebook.Validate(Rem("Book", "Teaches"));
        bool teachOk = teachReject is null;
        Console.WriteLine($"   PREFLIGHT-POLY    Remove nullable poly field : {(teachOk ? "PASS — Remove Book.Teaches accepted (nullable polymorphic)" : $"FAIL — reject=[{teachReject}]")}");

        // PREFLIGHT-POLY2: another nullable poly field (Npc.Sound) — general across poly fields, not a Book special-case.
        var soundReject = rulebook.Validate(Rem("Npc", "Sound"));
        bool soundOk = soundReject is null;
        Console.WriteLine($"   PREFLIGHT-POLY2   Remove another nullable poly: {(soundOk ? "PASS — Remove Npc.Sound accepted (general across poly fields)" : $"FAIL — reject=[{soundReject}]")}");

        // CONTROL-POLY-REQ: a genuinely NON-nullable poly field (MagicEffect.Archetype) still REFUSES, naming
        // 'non-nullable' — a REQUIRED arm is never silently clearable; the gate is nullability-driven, not blanket.
        var archReject = rulebook.Validate(Rem("MagicEffect", "Archetype"));
        bool archOk = archReject is not null && archReject.Contains("non-nullable", StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"   CONTROL-POLY-REQ  non-nullable poly refuses  : {(archOk ? "PASS — Remove MagicEffect.Archetype refused, names 'non-nullable' (required arm stays required)" : $"FAIL — reject=[{archReject}]")}");

        // E2E-POLY: the apply half — a Book carrying a Teaches arm, driven through the REAL WriteEngine.ApplyVerb Remove,
        // comes back with Teaches == null (was non-null) — a standalone polymorphic arm cleared.
        bool e2ePolyOk = false;
        try
        {
            var book = new Book(new FormKey(new ModKey("HcSncClear", ModType.Plugin), 0x000801), SkyrimRelease.SkyrimSE);
            book.Teaches = new BookSkill();
            bool had = book.Teaches is not null;
            WriteEngine.ApplyVerb(book, Rem("Book", "Teaches"));
            bool cleared = book.Teaches is null;
            e2ePolyOk = had && cleared;
            Console.WriteLine($"   E2E-POLY          Remove clears the arm     : {(e2ePolyOk ? "PASS — Book with a Teaches arm, ApplyVerb Remove -> Teaches == null" : $"FAIL — had={had} cleared={cleared}")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   E2E-POLY          Remove clears the arm     : FAIL — threw {ex.GetType().Name}: {ex.Message}");
        }

        Console.WriteLine();
        bool pass = vmadOk && promptOk && obOk && setOk && e2eOk
                    && teachOk && soundOk && archOk && e2ePolyOk;
        Console.WriteLine($"=== substruct-nullable-clear-guard: {(pass ? "PASS" : "FAIL")} ===");
        try { Directory.Delete(tmpDir, recursive: true); } catch { }
        return pass ? 0 : 1;
    }
}

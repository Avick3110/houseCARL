using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for CLEARING A NULLABLE SUBSTRUCT via Remove (S4 Track D / D2 — the VMAD
/// "un-fragment" capability). The whole capability is BY CONSTRUCTION: CorpusGenerator now reads a substruct's NRT
/// "?" annotation (NullabilityInfoContext) and emits Nullable=true, so VerbLegality already permits Remove on it and
/// ApplyScalarVerb's Remove sets the property null — no new write path was added, the corrected schema completes it.
/// This guard pins that chain end to end (RED before the generator fix: every substruct was marked non-nullable, so
/// VerbLegality refused Remove and there was no way to un-fragment an INFO except remove_record + recreate):
///   PREFLIGHT-VMAD    — Remove DialogResponses.VirtualMachineAdapter (a nullable substruct) PASSES pre-flight.
///   PREFLIGHT-PROMPT  — Remove DialogResponses.Prompt (another nullable substruct) PASSES too — the fix is GENERAL,
///                       not VMAD-special-cased (both are nullable reference substructs Mutagen models with "?").
///   CONTROL-OBJBOUNDS — Remove Armor.ObjectBounds (a genuinely NON-nullable substruct) still REFUSES, naming
///                       'non-nullable' — proves the gate is nullability-DRIVEN, not "every substruct is now removable".
///   CONTROL-SET       — Set on the VMAD substruct is STILL refused (a substruct is navigated INTO, not coerced) —
///                       only Remove opened; the fix touched nullability data, not the Set path.
///   E2E-UNFRAGMENT    — an in-memory INFO carrying a VMAD, driven through the REAL WriteEngine.ApplyVerb Remove,
///                       comes back with VirtualMachineAdapter == null (was non-null) — the apply half, un-fragmented.
/// </summary>
public static class SubstructNullableClearProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — clear a nullable substruct via Remove (S4 Track D / D2)  ################");
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

        Console.WriteLine();
        bool pass = vmadOk && promptOk && obOk && setOk && e2eOk;
        Console.WriteLine($"=== substruct-nullable-clear-guard: {(pass ? "PASS" : "FAIL")} ===");
        try { Directory.Delete(tmpDir, recursive: true); } catch { }
        return pass ? 0 : 1;
    }
}

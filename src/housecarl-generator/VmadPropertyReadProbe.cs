using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for the VMAD script-property VALUE read (1.3.1 item 2), in the pattern of
/// <c>depth-leak-guard</c>. Synthesizes IN MEMORY an INFO whose VMAD carries three script properties — a
/// <c>ScriptObjectProperty</c> with its Object FormLink set, a second with Object LEFT NULL (the declared-but-None
/// case the quest-fragment linter keys on), and a <c>ScriptIntProperty</c> with a scalar Data — then drives the
/// PRODUCT read path (<see cref="ReadEngine.ReadFields"/>) at <c>depth=2</c> and asserts each property's VALUE now
/// surfaces ONE bounded level beneath its identity summary.
///
/// Before the fix the depth walker returned at the <c>depth &lt;= 1</c> floor right after the
/// <c>[ScriptObjectProperty] Name=…</c> summary, so the Object/Data values never appeared — RED. After, the floor
/// opens a script property's direct value members (only), GREEN. The guard ALSO proves the change is bounded and
/// type-targeted: the identity summary is KEPT (the values are additive), a declared-but-null Object renders the
/// named "(null link)" note (Q3 — never a guessed value, never silently dropped), and a NON-property substruct
/// (a CTDA condition arm) read at the same depth still STOPS at its summary — so the one-extra-level applies to
/// script properties alone and the depth FLOOR is unchanged for every other substruct.
///
/// Run: dotnet run --project src/housecarl-generator -- vmad-property-read-guard
/// </summary>
public static class VmadPropertyReadProbe
{
    [CiProbe("vmad-property-read-guard")]
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — VMAD script-property VALUE read (1.3.1 item 2)  ################");
        Console.WriteLine();

        var mod = new SkyrimMod(new ModKey("hc_vmadread", ModType.Plugin), SkyrimRelease.SkyrimSE);

        // --- The positive fixture: an INFO with a VMAD carrying three properties (DialogResponsesAdapter is the
        //     proven in-memory adapter idiom; the read path navigates it through the `VirtualMachineAdapter` field). ---
        var knownFk = FormKey.Factory("018C91:Skyrim.esm");          // the Object FormLink value to surface
        var objProp = new ScriptObjectProperty { Name = "HcObjProp", Alias = -1 };
        objProp.Object.SetTo(knownFk);
        var nullProp = new ScriptObjectProperty { Name = "HcNullProp", Alias = -1 };   // Object left null (declared-but-None)
        var intProp = new ScriptIntProperty { Name = "HcIntProp", Data = 5 };

        var entry = new ScriptEntry { Name = "HcVmadReadScript" };
        entry.Properties.Add(objProp);
        entry.Properties.Add(nullProp);
        entry.Properties.Add(intProp);
        var vmad = new DialogResponsesAdapter();
        vmad.Scripts.Add(entry);
        var info = new DialogResponses(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { VirtualMachineAdapter = vmad };

        const int depth = 2;
        var rf = ReadEngine.ReadFields(info, new[] { "VirtualMachineAdapter.Scripts[0].Properties" }, depth);

        var objVal = Find(rf.Fields, "Properties[0].Object");
        var nullLink = Find(rf.Fields, "Properties[1].Object");
        var scalarVal = Find(rf.Fields, "Properties[2].Data");
        var summary = rf.Fields.FirstOrDefault(f => f.Path.EndsWith("Properties[0]", StringComparison.Ordinal) && !f.HasValue);

        // --- The negative control: a non-property substruct (a CTDA arm) read at the SAME depth must still stop at
        //     its summary — the depth floor is unchanged off the script-property path (mirrors depth-leak-guard's shape). ---
        var mgef = new MagicEffect(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE);
        mgef.Conditions.Add(new ConditionFloat
        {
            CompareOperator = CompareOperator.EqualTo,
            ComparisonValue = 1f,
            Data = new GetActorValueConditionData { ActorValue = ActorValue.Conjuration },
        });
        var rfCtrl = ReadEngine.ReadFields(mgef, new[] { "Conditions" }, depth);
        var ctrlSummary = rfCtrl.Fields.FirstOrDefault(f => f.Path.EndsWith("Conditions[0]", StringComparison.Ordinal) && !f.HasValue);
        bool ctrlNoDescend = !rfCtrl.Fields.Any(f => f.Path.Contains("Conditions[0].Data", StringComparison.Ordinal));

        Console.WriteLine($"-- INFO VMAD read at depth={depth} ({rf.Fields.Count} field lines); MagicEffect Conditions control ({rfCtrl.Fields.Count} lines) --");
        Console.WriteLine($"   Properties[0].Object  : {Show(objVal)}");
        Console.WriteLine($"   Properties[1].Object  : {Show(nullLink)}");
        Console.WriteLine($"   Properties[2].Data    : {Show(scalarVal)}");
        Console.WriteLine($"   Properties[0] summary : {Show(summary)}");
        Console.WriteLine($"   control Conditions[0] : {Show(ctrlSummary)}  (no .Data descend: {ctrlNoDescend})");
        Console.WriteLine();

        bool objOk = objVal is { HasValue: true } && objVal.Token == "018C91:Skyrim.esm";
        bool scalarOk = scalarVal is { HasValue: true } && scalarVal.Token == "5";
        bool nullOk = nullLink is { HasValue: false } && (nullLink.Note ?? "").Contains("null link", StringComparison.Ordinal);
        bool summaryOk = summary is not null && (summary.Note ?? "").Contains("ScriptObjectProperty", StringComparison.Ordinal);
        bool controlOk = ctrlSummary is not null && ctrlNoDescend;
        bool pass = objOk && scalarOk && nullOk && summaryOk && controlOk;

        Console.WriteLine($"   OBJECT-VALUE  (Object FormLink surfaced)          : {(objOk ? "PASS" : "FAIL")}");
        Console.WriteLine($"   SCALAR-VALUE  (Data scalar surfaced)              : {(scalarOk ? "PASS" : "FAIL")}");
        Console.WriteLine($"   NULL-LINK     (declared-but-None named, not lost) : {(nullOk ? "PASS" : "FAIL")}");
        Console.WriteLine($"   SUMMARY-KEPT  (identity line still present)       : {(summaryOk ? "PASS" : "FAIL")}");
        Console.WriteLine($"   NON-PROPERTY-UNCHANGED (floor unchanged off path) : {(controlOk ? "PASS" : "FAIL")}");
        Console.WriteLine();
        Console.WriteLine($"=== vmad-property-read-guard: {(pass ? "PASS" : "FAIL")} ===");
        return pass ? 0 : 1;
    }

    static FieldValue? Find(IEnumerable<FieldValue> fields, string suffix) =>
        fields.FirstOrDefault(f => f.Path.EndsWith(suffix, StringComparison.Ordinal));

    static string Show(FieldValue? f) =>
        f is null ? "(missing)" : $"{f.Path} = {(f.HasValue ? f.Token : f.Note)}";
}

using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD (standing CI instrument) for HCBR-2026-06-09-02 — ConditionData form-link parameters
/// unreadable: a form-mode <c>FormLinkOrIndex</c> read off the BINARY OVERLAY rendered the placeholder
/// <c>(floi: form mode but no readable FormKey)</c> instead of the FormKey, because the renderer tested
/// the FLOI itself as an IFormLinkGetter while the overlay carries the link in its <c>.Link</c> property
/// (the accessor the write side already reads — <see cref="WriteEngine.ReadFloiFormKey"/>, oracle-proven).
///
/// Self-contained, in the pattern of <c>pkcu-regression</c>: synthesizes a ConstructibleObject carrying the
/// universal temper-gate shape from the report — a <c>HasPerk</c> condition whose Perk parameter is a
/// form-mode FLOI — plus an alias-mode sibling, writes it to a temp folder, re-opens it as a binary overlay
/// (the product read path's view), and asserts BOTH the mutable and overlay reads render:
///   Conditions[0].Data.Perk = &lt;the perk's FormKey&gt;   (form mode — RED before the fix on the overlay)
///   Conditions[1].Data.Perk = alias 7                 (index mode — the report's fix #3)
///
/// Run: <c>dotnet run --project src/housecarl-generator floi-read-guard</c>
/// </summary>
public static class FloiReadProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — floi form-mode FormKey read (HCBR-2026-06-09-02)  ################");
        Console.WriteLine();

        // Synthesize the report's shape: a COBJ whose HasPerk condition gates on a perk (form mode), plus an
        // alias-mode sibling. The perk lives in the SAME mod, so the plugin is single-master and self-contained.
        var mod = new SkyrimMod(new ModKey("hc_floiguard", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var perk = mod.Perks.AddNew();
        perk.EditorID = "HC_FloiGuard_GatePerk";
        var cobj = mod.ConstructibleObjects.AddNew();
        cobj.EditorID = "HC_FloiGuard_TemperRecipe";

        var formArm = new HasPerkConditionData();
        formArm.Perk = new FormLinkOrIndex<IPerkGetter>(formArm, perk.FormKey);
        cobj.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1f, Data = formArm });

        var aliasArm = new HasPerkConditionData { UseAliases = true };
        aliasArm.Perk = new FormLinkOrIndex<IPerkGetter>(aliasArm, 7u);
        cobj.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1f, Data = aliasArm });

        var expectForm = perk.FormKey.ToString();

        // Arm 1 — the MUTABLE read (the in-memory shape the write path hands back).
        var (mutForm, mutAlias) = ReadPerkParams(cobj);

        // Arm 2 — the BINARY OVERLAY read (the product read path's view; where the placeholder reproduced).
        var tmp = Path.Combine(Path.GetTempPath(), "hc_floiguard_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        string? ovForm, ovAlias;
        try
        {
            var espPath = Path.Combine(tmp, mod.ModKey.FileName);
            mod.BeginWrite.ToPath(espPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            using var back = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE);
            (ovForm, ovAlias) = ReadPerkParams(back.ConstructibleObjects.First());
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best-effort temp cleanup */ }
        }

        Console.WriteLine($"-- synthesized COBJ: HasPerk(form -> {expectForm}) + HasPerk(alias 7) --");
        Console.WriteLine($"   mutable  Conditions[0].Data.Perk : {mutForm ?? "(missing)"}");
        Console.WriteLine($"   mutable  Conditions[1].Data.Perk : {mutAlias ?? "(missing)"}");
        Console.WriteLine($"   overlay  Conditions[0].Data.Perk : {ovForm ?? "(missing)"}");
        Console.WriteLine($"   overlay  Conditions[1].Data.Perk : {ovAlias ?? "(missing)"}");
        Console.WriteLine();

        bool mutFormOk = mutForm == expectForm;
        bool mutAliasOk = mutAlias == "alias 7";
        bool ovFormOk = ovForm == expectForm;
        bool ovAliasOk = ovAlias == "alias 7";
        bool pass = mutFormOk && mutAliasOk && ovFormOk && ovAliasOk;

        Console.WriteLine($"   mutable form-mode FormKey read  : {(mutFormOk ? "PASS" : "FAIL")}");
        Console.WriteLine($"   mutable alias-mode index read   : {(mutAliasOk ? "PASS" : "FAIL")}");
        Console.WriteLine($"   overlay form-mode FormKey read  : {(ovFormOk ? "PASS" : "FAIL")}  (the HCBR-2026-06-09-02 arm)");
        Console.WriteLine($"   overlay alias-mode index read   : {(ovAliasOk ? "PASS" : "FAIL")}");
        Console.WriteLine();
        Console.WriteLine($"=== floi-read-guard: {(pass ? "PASS" : "FAIL")} ===");
        return pass ? 0 : 1;
    }

    /// <summary>Read Conditions at depth through the PRODUCT path (ReadEngine.ReadFields) and pull the two
    /// Perk-parameter leaves; a leaf that rendered as a note (the bug) comes back as its note text, so a
    /// failure prints WHAT the renderer said instead of the token.</summary>
    static (string? form, string? alias) ReadPerkParams(IMajorRecordGetter record)
    {
        var rf = ReadEngine.ReadFields(record, new[] { "Conditions" }, 6);
        string? Leaf(string path)
        {
            var f = rf.Fields.FirstOrDefault(fld => fld.Path == path);
            return f is null ? null : (f.HasValue ? f.Token : f.Note);
        }
        return (Leaf("Conditions[0].Data.Perk"), Leaf("Conditions[1].Data.Perk"));
    }
}

using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for the depth-2 element-identity render (#198), in the pattern of
/// <c>vmad-property-read-guard</c>. A struct element with no Name/EditorID/Title identity but EXACTLY ONE FormLink
/// now surfaces that link as its identity line (<c>[PerkPlacement] Perk=03AF81:Skyrim.esm</c>) instead of a bare
/// opaque <c>[PerkPlacement]</c> — so a caller reading a Perks list at the documented depth=2 sees WHICH perk each
/// element points at, matching what <c>ScriptObjectProperty</c> already does with <c>Name=</c>, instead of
/// concluding "the FormIDs aren't surfaced" and reconstructing them the hard way (the observed cost in #198).
///
/// Two arms, both driving the PRODUCT read path (<see cref="ReadEngine.ReadFields"/>) at depth=2:
///   * POSITIVE — a PerkPlacement (lone <c>Perk</c> FormLink, no name field) renders <c>[PerkPlacement] Perk=…</c>.
///     RED before the fix (bare <c>[PerkPlacement]</c>), GREEN after.
///   * NAME-WINS — a ScriptObjectProperty carries BOTH a Name AND an Object FormLink; it must still render its
///     <c>Name=</c> identity, NOT the link — proving the lone-FormLink fallback fires ONLY when a name-like identity
///     is absent (never displacing the existing Name/EditorID/Title path).
///
/// Run: dotnet run --project src/housecarl-generator -- element-identity-guard
/// </summary>
public static class ElementIdentityProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — depth-2 element identity: lone-FormLink struct (#198)  ################");
        Console.WriteLine();

        var mod = new SkyrimMod(new ModKey("hc_elemid", ModType.Plugin), SkyrimRelease.SkyrimSE);

        // --- POSITIVE: an NPC with one PerkPlacement. PerkPlacement has NO Name/EditorID/Title and EXACTLY ONE
        //     FormLink (Perk), so the lone-FormLink fallback is the ONLY identity it can carry. ---
        var perkFk = FormKey.Factory("03AF81:Skyrim.esm");
        var pp = new PerkPlacement { Rank = 1 };
        pp.Perk.SetTo(perkFk);
        var npc = mod.Npcs.AddNew();
        npc.Perks = new() { pp };

        var rfNpc = ReadEngine.ReadFields(npc, new[] { "Perks" }, 2);
        var perkElem = rfNpc.Fields.FirstOrDefault(f => f.Path.EndsWith("Perks[0]", StringComparison.Ordinal) && !f.HasValue);

        // --- NAME-WINS: a VMAD script property that has BOTH a Name and an Object FormLink. Name-identity must win
        //     over the lone-link fallback (the fallback is a LAST resort, never a displacement). ---
        var objProp = new ScriptObjectProperty { Name = "HcNamedProp", Alias = -1 };
        objProp.Object.SetTo(FormKey.Factory("018C91:Skyrim.esm"));
        var entry = new ScriptEntry { Name = "HcElemIdScript" };
        entry.Properties.Add(objProp);
        var vmad = new DialogResponsesAdapter();
        vmad.Scripts.Add(entry);
        var info = new DialogResponses(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { VirtualMachineAdapter = vmad };

        var rfInfo = ReadEngine.ReadFields(info, new[] { "VirtualMachineAdapter.Scripts[0].Properties" }, 2);
        var nameElem = rfInfo.Fields.FirstOrDefault(f => f.Path.EndsWith("Properties[0]", StringComparison.Ordinal) && !f.HasValue);

        Console.WriteLine($"   Perks[0]      : {Show(perkElem)}");
        Console.WriteLine($"   Properties[0] : {Show(nameElem)}");
        Console.WriteLine();

        var perkNote = perkElem?.Note ?? "";
        var nameNote = nameElem?.Note ?? "";
        bool perkTyped = perkNote.Contains("PerkPlacement", StringComparison.Ordinal);
        bool perkIdentity = perkNote.Contains($"Perk={perkFk}", StringComparison.Ordinal);   // the #198 fix
        bool nameWins = nameNote.Contains("Name=HcNamedProp", StringComparison.Ordinal)
                     && !nameNote.Contains("Object=", StringComparison.Ordinal);              // link did NOT displace Name

        bool pass = perkTyped && perkIdentity && nameWins;

        Console.WriteLine($"   LONE-LINK-IDENTITY  (PerkPlacement shows Perk={perkFk})   : {(perkIdentity ? "PASS" : "FAIL")}");
        Console.WriteLine($"   TYPE-KEPT           (identity is additive, [Type] stays)  : {(perkTyped ? "PASS" : "FAIL")}");
        Console.WriteLine($"   NAME-WINS           (Name identity beats the lone link)   : {(nameWins ? "PASS" : "FAIL")}");
        Console.WriteLine();
        Console.WriteLine($"=== element-identity-guard: {(pass ? "PASS" : "FAIL")} ===");
        return pass ? 0 : 1;
    }

    static string Show(FieldValue? f) =>
        f is null ? "(missing)" : $"{f.Path} = {(f.HasValue ? f.Token : f.Note)}";
}

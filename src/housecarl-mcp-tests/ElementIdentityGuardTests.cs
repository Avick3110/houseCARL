using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>At depth=2 a struct element with no name but one FormLink shows that link as its identity, and a
/// name-like identity still wins over the link (#198). Migrated from <c>element-identity-guard</c>.</summary>
[Trait("tier", "unit")]
public sealed class ElementIdentityGuardTests
{
    static readonly SkyrimMod Mod = new(new ModKey("hc_elemid", ModType.Plugin), SkyrimRelease.SkyrimSE);

    // LONE-LINK-IDENTITY + TYPE-KEPT: a PerkPlacement renders [PerkPlacement] Perk=<fk>.
    [Fact]
    public void APerkPlacementShowsItsPerkAndKeepsItsType()
    {
        var perk = FormKey.Factory("03AF81:Skyrim.esm");
        var pp = new PerkPlacement { Rank = 1 };
        pp.Perk.SetTo(perk);
        var npc = new Npc(Mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { Perks = new() { pp } };
        var note = ReadEngine.ReadFields(npc, new[] { "Perks" }, 2).Fields
            .Single(f => f.Path == "Perks[0]" && !f.HasValue).Note;
        Assert.Contains("PerkPlacement", note);
        Assert.Contains($"Perk={perk}", note);
    }

    // NAME-WINS: a ScriptObjectProperty with a Name and an Object renders Name=, not Object=.
    [Fact]
    public void ANamedPropertyShowsItsNameNotItsLink()
    {
        var prop = new ScriptObjectProperty { Name = "HcNamedProp", Alias = -1 };
        prop.Object.SetTo(FormKey.Factory("018C91:Skyrim.esm"));
        var entry = new ScriptEntry { Name = "HcElemIdScript" };
        entry.Properties.Add(prop);
        var vmad = new DialogResponsesAdapter();
        vmad.Scripts.Add(entry);
        var info = new DialogResponses(Mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { VirtualMachineAdapter = vmad };
        var note = ReadEngine.ReadFields(info, new[] { "VirtualMachineAdapter.Scripts[0].Properties" }, 2).Fields
            .Single(f => f.Path.EndsWith("Properties[0]", StringComparison.Ordinal) && !f.HasValue).Note;
        Assert.Contains("Name=HcNamedProp", note);
        Assert.DoesNotContain("Object=", note);
    }
}

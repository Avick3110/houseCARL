using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A condition's form-or-alias parameter reads as the form's FormKey in form mode and as "alias N" in alias
/// mode, both on the in-memory record and on the binary overlay the product reads plugins through. The overlay's form
/// mode used to render a "no readable FormKey" placeholder.</summary>
[Trait("tier", "integration")]
public sealed class ConditionFloiReadTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-condition-floi-" + Guid.NewGuid().ToString("N"));
    readonly SkyrimMod _mod = new(new ModKey("hc_floiguard", ModType.Plugin), SkyrimRelease.SkyrimSE);
    readonly ConstructibleObject _recipe;
    readonly string _perk;

    public ConditionFloiReadTests()
    {
        Directory.CreateDirectory(_dir);
        var perk = _mod.Perks.AddNew();
        perk.EditorID = "HC_FloiGuard_GatePerk";
        _perk = perk.FormKey.ToString();
        _recipe = _mod.ConstructibleObjects.AddNew();
        _recipe.EditorID = "HC_FloiGuard_TemperRecipe";

        var formArm = new HasPerkConditionData();
        formArm.Perk = new FormLinkOrIndex<IPerkGetter>(formArm, perk.FormKey);
        _recipe.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1f, Data = formArm });
        var aliasArm = new HasPerkConditionData { UseAliases = true };
        aliasArm.Perk = new FormLinkOrIndex<IPerkGetter>(aliasArm, 7u);
        _recipe.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1f, Data = aliasArm });
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp cleanup best-effort */ } }

    /// <summary>The leaf's token, or its note when it rendered as one, so a failure shows what the reader said.</summary>
    static string? Leaf(IMajorRecordGetter record, string path)
    {
        var f = ReadEngine.ReadFields(record, new[] { "Conditions" }, 6).Fields.FirstOrDefault(x => x.Path == path);
        return f is null ? null : f.HasValue ? f.Token : f.Note;
    }

    // Probe: "mutable form-mode FormKey read", "mutable alias-mode index read".
    [Fact]
    public void TheInMemoryRecordReadsTheFormKeyAndTheAlias()
    {
        Assert.Equal(_perk, Leaf(_recipe, "Conditions[0].Data.Perk"));
        Assert.Equal("alias 7", Leaf(_recipe, "Conditions[1].Data.Perk"));
    }

    // Probe: "overlay form-mode FormKey read (the HCBR-2026-06-09-02 arm)", "overlay alias-mode index read".
    [Fact]
    public void TheBinaryOverlayReadsTheFormKeyAndTheAlias()
    {
        var path = Path.Combine(_dir, _mod.ModKey.FileName);
        _mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        using var back = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        var recipe = back.ConstructibleObjects.First();

        Assert.Equal(_perk, Leaf(recipe, "Conditions[0].Data.Perk"));
        Assert.Equal("alias 7", Leaf(recipe, "Conditions[1].Data.Perk"));
    }
}

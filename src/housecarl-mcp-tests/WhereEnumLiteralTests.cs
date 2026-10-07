using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>#1072: an enum literal in where= that names no value of the field's enum refuses the call with a
/// "did you mean", instead of scanning to a silent zero; a legal name and a numeric literal behave as before.</summary>
[Trait("tier", "integration")]
public sealed class WhereEnumLiteralTests : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-where-enum-");

    public WhereEnumLiteralTests()
    {
        var key = new ModKey("HcEnumWorld", ModType.Master);
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        Add(m, "HcHeavyA", ActorValue.HeavyArmorModifier);
        Add(m, "HcHeavyB", ActorValue.HeavyArmorModifier);
        Add(m, "HcLight", ActorValue.LightArmorModifier);
        Add(m, "HcHealth", ActorValue.Health);
        m.BeginWrite.ToPath(Path.Combine(_mo2.DataDir, key.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        _mo2.Profile("HcEnumWorld.esm\r\n", "*HcEnumWorld.esm\r\n", "");
    }

    static void Add(SkyrimMod m, string eid, ActorValue av)
    {
        var e = m.MagicEffects.AddNew();
        e.EditorID = eid;
        e.Archetype = new MagicEffectArchetype(MagicEffectArchetype.TypeEnum.ValueModifier) { ActorValue = av };
    }

    public void Dispose() => _mo2.Delete();

    string Where(string clause)
    {
        using var svc = _mo2.Open();
        return RecordsTools.Records(svc, types: new[] { "MGEF" }, where: new[] { clause }, counts_only: true);
    }

    [Fact]
    public void TheIssuesCallRefusesAndNamesTheRealValue()
    {
        var r = Where("Archetype.ActorValue in [HeavyArmorMod, LightArmorMod]");
        Assert.Contains("'HeavyArmorMod' is not a value of the ActorValue enum", r);
        Assert.Contains("`HeavyArmorModifier`", r);
        Assert.DoesNotContain("0 matches", r);
    }

    [Theory]
    [InlineData("Archetype.ActorValue = HeavyArmorMod")]
    [InlineData("Archetype.ActorValue != HeavyArmorMod")]
    [InlineData("Archetype.ActorValue not in [HeavyArmorModifier, LightArmorMod]")]
    public void EveryValueOperatorRefusesABadName(string clause)
    {
        var r = Where(clause);
        Assert.Contains("is not a value of the ActorValue enum", r);
        Assert.Contains("Did you mean", r);
    }

    [Fact]
    public void TheRealNamesStillMatch() =>
        Assert.Contains("3 match", Where("Archetype.ActorValue in [HeavyArmorModifier, lightarmormodifier]"));

    [Fact]
    public void NotInWithRealNamesStillMatchesTheRest() =>
        Assert.Contains("1 match", Where("Archetype.ActorValue not in [HeavyArmorModifier, LightArmorModifier]"));

    [Theory]
    [InlineData("Archetype.ActorValue != 9999")]
    [InlineData("Archetype.ActorValue != 0x270F")]
    public void ANumericLiteralIsNotRefused(string clause)
    {
        var r = Where(clause);
        Assert.DoesNotContain("is not a value of", r);
        Assert.Contains("4 match", r);
    }
}

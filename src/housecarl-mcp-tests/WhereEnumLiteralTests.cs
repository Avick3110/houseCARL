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
        var offKey = new ModKey("HcEnumOff", ModType.Plugin);
        var off = new SkyrimMod(offKey, SkyrimRelease.SkyrimSE);
        Add(off, "HcOffHeavy", ActorValue.HeavyArmorModifier);
        off.BeginWrite.ToPath(Path.Combine(_mo2.DataDir, offKey.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        _mo2.Profile("HcEnumWorld.esm\r\nHcEnumOff.esp\r\n", "*HcEnumWorld.esm\r\nHcEnumOff.esp\r\n", "");
    }

    static void Add(SkyrimMod m, string eid, ActorValue av)
    {
        var e = m.MagicEffects.AddNew();
        e.EditorID = eid;
        e.Archetype = new MagicEffectArchetype(MagicEffectArchetype.TypeEnum.ValueModifier) { ActorValue = av };
    }

    public void Dispose() => _mo2.Delete();

    string Where(string clause, params string[] types)
    {
        using var svc = _mo2.Open();
        return RecordsTools.Records(svc, types: types.Length > 0 ? types : new[] { "MGEF" }, where: new[] { clause }, counts_only: true);
    }

    [Fact]
    public void TheIssuesCallRefusesAndNamesTheRealValue()
    {
        var r = Where("Archetype.ActorValue in [HeavyArmorMod, LightArmorMod]");
        Assert.Contains("'HeavyArmorMod' names no value of the ActorValue enum", r);
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
        Assert.Contains("names no value of the ActorValue enum", r);
        Assert.Contains("Did you mean", r);
    }

    [Fact]
    public void TheRealNamesStillMatch() =>
        Assert.Contains("3 match", Where("Archetype.ActorValue in [HeavyArmorModifier, lightarmormodifier]"));

    [Fact]
    public void NotInWithRealNamesStillMatchesTheRest() =>
        Assert.Contains("1 match", Where("Archetype.ActorValue not in [HeavyArmorModifier, LightArmorModifier]"));

    [Fact]
    public void ACommaListOnAPlainEnumRefuses() =>
        Assert.Contains("ActorValue is not a [Flags] enum", Where("Archetype.ActorValue = HeavyArmorModifier, Health"));

    [Fact]
    public void ACommaListOnAFlagsEnumIsNotRefused() =>
        Assert.DoesNotContain("names no value of", Where("Flags = Hostile, Recover"));

    [Fact]
    public void ANestedEnumIsNamedByItsDeclaringType() =>
        Assert.Contains("the MagicEffectArchetype.TypeEnum enum", Where("Archetype.Type = ValueModifer"));

    [Fact]
    public void AQuotedNameRefusesAndSaysToWriteItBare() =>
        Assert.Contains("write it bare: 'Archetype.ActorValue = HeavyArmorModifier'", Where("Archetype.ActorValue = \"HeavyArmorModifier\""));

    [Fact]
    public void ANumberEqualsTheEnumsUnderlyingValue() =>
        Assert.Contains("2 match", Where($"Archetype.ActorValue = {(int)ActorValue.HeavyArmorModifier}"));

    [Fact]
    public void ANumberInAListMatchesBesideAName() =>
        Assert.Contains("3 match", Where($"Archetype.ActorValue in [{(int)ActorValue.HeavyArmorModifier}, LightArmorModifier]"));

    [Theory]
    [InlineData("Archetype.ActorValue != 9999")]
    [InlineData("Archetype.ActorValue != 0x270F")]
    public void ANumericLiteralIsNotRefused(string clause)
    {
        var r = Where(clause);
        Assert.DoesNotContain("names no value of", r);
        Assert.Contains("4 match", r);
    }

    [Theory]
    [InlineData("Archetype.ActorValue = 1.5")]
    [InlineData("Archetype.ActorValue = NaN")]
    [InlineData("Archetype.ActorValue = 1e3")]
    public void ANonIntegerNumberRefuses(string clause) =>
        Assert.Contains("names no value of the ActorValue enum", Where(clause));

    [Fact]
    public void ARefusalUnderNotEqualsDoesNotClaimTheTermCanNeverMatch()
    {
        var r = Where("Archetype.ActorValue != HeavyArmorMod");
        Assert.Contains("so the test means nothing", r);
        Assert.DoesNotContain("never match", r);
    }

    [Fact]
    public void AMisspelledMemberOfAFlagsComboGetsADidYouMeanForThatMember()
    {
        var r = Where("Flags = Hostile, Recovr");
        Assert.Contains("'Recovr' is no flag of it", r);
        Assert.Contains("`Recover`", r);
        Assert.DoesNotContain("not a [Flags] enum", r);
    }

    [Fact]
    public void ATypeLackingTheFieldHasNoSayInTheRefusal() =>
        Assert.Contains("names no value of the ActorValue enum", Where("Archetype.ActorValue = HeavyArmorMod", "MGEF", "WEAP"));

    [Fact]
    public void ANameOnlyOneTypesEnumHasIsNotRefused() =>
        Assert.DoesNotContain("names no value", Where("Flags = Hostile", "MGEF", "SPEL"));

    [Fact]
    public void ANameNeitherTypesEnumHasRefusesNamingBoth() =>
        Assert.Contains("the MagicEffect.Flag or SpellDataFlag enum", Where("Flags = Hostil", "MGEF", "SPEL"));

    [Fact]
    public void TheOffOrderScanRefusesABadNameToo()
    {
        using var svc = _mo2.Open();
        var source = System.Text.Json.JsonDocument.Parse("\"HcEnumOff.esp\"").RootElement;
        var bad = RecordsTools.Records(svc, types: new[] { "MGEF" }, where: new[] { "Archetype.ActorValue = HeavyArmorMod" },
                                       counts_only: true, source: source);
        Assert.Contains("names no value of the ActorValue enum", bad);
        var good = RecordsTools.Records(svc, types: new[] { "MGEF" }, where: new[] { "Archetype.ActorValue = HeavyArmorModifier" },
                                        counts_only: true, source: source);
        Assert.Contains("1 match", good);
    }
}

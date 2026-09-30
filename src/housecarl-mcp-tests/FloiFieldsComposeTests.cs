using System.Reflection;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A condition's FormLinkOrIndex target set through the flat compose <c>fields:</c> shorthand validates, lands
/// in the right mode and writes the same bytes as the verbose <c>sets:</c> form, on every FLOI condition parameter.
/// Migrated from the floi-fields-guard probe.</summary>
[Trait("tier", "unit")]
public sealed class FloiFieldsComposeTests : IDisposable
{
    const string RecType = "ConstructibleObject";
    const string Arm = "GetEquippedConditionData";
    const string FloiField = "ItemOrList";
    const string FormVal = "0001F4:Skyrim.esm";

    readonly WritePathRig _rig = new();

    static WriteRequest AddCondition(StructSpec armSpec) => new()
    {
        RecordType = RecType, Path = new[] { "Conditions" }, Verb = "Add",
        Struct = new StructSpec
        {
            Type = "ConditionFloat",
            Sets = new() { new() { RecordType = RecType, Path = new[] { "Data" }, Verb = "Set", Struct = armSpec } },
        },
    };

    static WriteRequest ViaFields(string arm, string prop, string value) =>
        AddCondition(new StructSpec { Type = arm, Fields = new() { [prop] = value } });

    static WriteRequest ViaSets(string arm, string prop, string value) =>
        AddCondition(new StructSpec
        {
            Type = arm,
            Sets = new() { new() { RecordType = RecType, Path = new[] { prop }, Verb = "Set", Value = value } },
        });

    sealed record Floi(FormKey FormKey, uint Index, bool UseAliases, bool UsePackageData);

    /// <summary>Apply <paramref name="request"/> to a fresh COBJ and read back its one condition's FLOI target and the
    /// arm's mode bits.</summary>
    static Floi Landed(WriteRequest request, string prop)
    {
        var cobj = new SkyrimMod(new ModKey("hc_floi_fields", ModType.Plugin), SkyrimRelease.SkyrimSE).ConstructibleObjects.AddNew();
        WriteEngine.ApplyVerb(cobj, request);
        var data = Assert.Single(cobj.Conditions).GetType().GetProperty("Data")!.GetValue(cobj.Conditions[0])!;
        var floi = data.GetType().GetProperty(prop)!.GetValue(data)!;
        var link = floi.GetType().GetProperty("Link")!.GetValue(floi);
        var fk = link?.GetType().GetProperty("FormKey")?.GetValue(link) is FormKey k ? k : default;
        return new Floi(fk, floi.GetType().GetProperty("Index")?.GetValue(floi) is uint i ? i : 0u,
            (bool)data.GetType().GetProperty("UseAliases")!.GetValue(data)!,
            (bool)data.GetType().GetProperty("UsePackageData")!.GetValue(data)!);
    }

    byte[] Bytes(string stem, WriteRequest request)
    {
        var path = _rig.Out(stem + ".esp");
        var mod = new SkyrimMod(new ModKey(stem, ModType.Plugin), SkyrimRelease.SkyrimSE);
        WriteEngine.ApplyVerb(mod.ConstructibleObjects.AddNew(), request);
        WriteEngine.WritePatch(mod, new ISkyrimModGetter[] { mod }, path);
        return File.ReadAllBytes(path);
    }

    // T1: apply via fields: lands ItemOrList in form mode (FormKey + UseAliases/UsePackageData false)
    [Fact]
    public void FieldsLandsAFormTargetInFormMode()
    {
        var f = Landed(ViaFields(Arm, FloiField, FormVal), FloiField);
        Assert.Equal(FormKey.Factory(FormVal), f.FormKey);
        Assert.False(f.UseAliases);
        Assert.False(f.UsePackageData);
    }

    // T2: pre-flight accepts a FLOI target set via fields: (was 'does not coerce')
    [Fact]
    public void PreflightAcceptsAFormTargetViaFields()
        => Assert.Null(TestCorpus.Rulebook.Validate(ViaFields(Arm, FloiField, FormVal)));

    // T3: the fields: write is byte-identical to the sets: write (full parity)
    [Fact]
    public void TheFieldsWriteIsByteIdenticalToTheSetsWrite()
        => Assert.Equal(Bytes("hc_floi_same", ViaSets(Arm, FloiField, FormVal)), Bytes("hc_floi_same", ViaFields(Arm, FloiField, FormVal)));

    // T-IDX: apply via fields: 'alias 5' lands in alias index mode (UseAliases true, Index 5)
    [Fact]
    public void FieldsLandsAnAliasIndexInAliasMode()
    {
        var f = Landed(ViaFields(Arm, FloiField, "alias 5"), FloiField);
        Assert.True(f.UseAliases);
        Assert.False(f.UsePackageData);
        Assert.Equal(5u, f.Index);
    }

    // C-SETS: the verbose sets: path still lands the FLOI in form mode (unbroken control)
    [Fact]
    public void TheSetsPathStillLandsAFormTarget()
    {
        var f = Landed(ViaSets(Arm, FloiField, FormVal), FloiField);
        Assert.Equal(FormKey.Factory(FormVal), f.FormKey);
        Assert.False(f.UseAliases);
    }

    // C-BAD: a malformed FLOI value via fields: still rejects at pre-flight (no over-broad accept)
    // C-BAD2: the malformed-FLOI rejection names 'condition target' (actionable, not the coerce message)
    [Fact]
    public void AMalformedFieldsTargetRejectsNamingTheConditionTarget()
        => Assert.Contains("condition target", TestCorpus.Rulebook.Validate(ViaFields(Arm, FloiField, "notaformkey notanindex")),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Every writable FormLinkOrIndex property on every concrete condition-data arm, found by reflection.</summary>
    public static IEnumerable<object[]> FloiSites()
    {
        var asm = typeof(SkyrimMod).Assembly;
        var dataBase = asm.GetType("Mutagen.Bethesda.Skyrim.IConditionDataGetter")!;
        foreach (var arm in asm.GetTypes()
                     .Where(t => t is { IsClass: true, IsAbstract: false }
                                 && !t.Name.EndsWith("BinaryOverlay", StringComparison.Ordinal)
                                 && dataBase.IsAssignableFrom(t))
                     .OrderBy(t => t.Name, StringComparer.Ordinal))
            foreach (var p in arm.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite && IsFloi(p.PropertyType)))
                yield return new object[] { arm.Name, p.Name };
    }

    static bool IsFloi(Type t)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        if (!u.IsGenericType) return false;
        var n = u.GetGenericTypeDefinition().Name;
        return n.StartsWith("IFormLinkOrIndex", StringComparison.Ordinal) || n.StartsWith("FormLinkOrIndex", StringComparison.Ordinal);
    }

    // COVERAGE: all FLOI condition-param sites PRE-FLIGHT-ACCEPT + APPLY-LAND via fields:, both form + index mode (by construction)
    [Theory]
    [MemberData(nameof(FloiSites))]
    public void EveryFloiSiteTakesAFormTargetViaFields(string arm, string prop)
    {
        Assert.Null(TestCorpus.Rulebook.Validate(ViaFields(arm, prop, FormVal)));
        var f = Landed(ViaFields(arm, prop, FormVal), prop);
        Assert.Equal(FormKey.Factory(FormVal), f.FormKey);
        Assert.False(f.UseAliases);
        Assert.False(f.UsePackageData);
    }

    // COVERAGE: ...the index-mode half of the same sweep
    [Theory]
    [MemberData(nameof(FloiSites))]
    public void EveryFloiSiteTakesAnAliasIndexViaFields(string arm, string prop)
    {
        Assert.Null(TestCorpus.Rulebook.Validate(ViaFields(arm, prop, "alias 5")));
        var f = Landed(ViaFields(arm, prop, "alias 5"), prop);
        Assert.True(f.UseAliases);
        Assert.False(f.UsePackageData);
        Assert.Equal(5u, f.Index);
    }

    public void Dispose() => _rig.Dispose();
}

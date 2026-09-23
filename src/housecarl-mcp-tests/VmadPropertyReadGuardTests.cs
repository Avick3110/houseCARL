using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>At depth=2 a VMAD script property opens one level to its value under its kept summary, and a CTDA read at
/// the same depth still stops (1.3.1 item 2). Migrated from <c>vmad-property-read-guard</c>.</summary>
[Trait("tier", "unit")]
public sealed class VmadPropertyReadGuardTests
{
    readonly IReadOnlyList<FieldValue> _fields;

    public VmadPropertyReadGuardTests()
    {
        var mod = new SkyrimMod(new ModKey("hc_vmadread", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var objProp = new ScriptObjectProperty { Name = "HcObjProp", Alias = -1 };
        objProp.Object.SetTo(FormKey.Factory("018C91:Skyrim.esm"));
        var entry = new ScriptEntry { Name = "HcVmadReadScript" };
        entry.Properties.Add(objProp);
        entry.Properties.Add(new ScriptObjectProperty { Name = "HcNullProp", Alias = -1 });
        entry.Properties.Add(new ScriptIntProperty { Name = "HcIntProp", Data = 5 });
        var vmad = new DialogResponsesAdapter();
        vmad.Scripts.Add(entry);
        var info = new DialogResponses(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { VirtualMachineAdapter = vmad };
        _fields = ReadEngine.ReadFields(info, new[] { "VirtualMachineAdapter.Scripts[0].Properties" }, 2).Fields;
    }

    FieldValue At(string suffix) => _fields.Single(f => f.Path.EndsWith(suffix, StringComparison.Ordinal));

    // OBJECT-VALUE: the Object FormLink is surfaced.
    [Fact]
    public void AnObjectPropertyShowsItsLink()
        => Assert.Equal("018C91:Skyrim.esm", At("Properties[0].Object").Token);

    // SCALAR-VALUE: an Int property shows Data=5.
    [Fact]
    public void AnIntPropertyShowsItsData()
        => Assert.Equal("5", At("Properties[2].Data").Token);

    // NULL-LINK: a declared-but-None Object is named (null link), not dropped.
    [Fact]
    public void ANullObjectIsNamedANullLink()
    {
        var f = At("Properties[1].Object");
        Assert.False(f.HasValue);
        Assert.Contains("null link", f.Note);
    }

    // SUMMARY-KEPT: the [ScriptObjectProperty] identity line is still present.
    [Fact]
    public void ThePropertyKeepsItsSummary()
        => Assert.Contains("ScriptObjectProperty",
            _fields.Single(f => f.Path.EndsWith("Properties[0]", StringComparison.Ordinal) && !f.HasValue).Note);

    // BOUNDED: the floor opens one level, so a list property shows its Objects summary and none of its elements.
    // Condition-arm params are all leaves, so this is the arm family where a second level would show.
    [Fact]
    public void AListPropertyOpensOneLevelOnly()
    {
        var mod = new SkyrimMod(new ModKey("hc_vmadread", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var element = new ScriptObjectProperty { Name = "HcListElem", Alias = -1 };
        element.Object.SetTo(FormKey.Factory("018C91:Skyrim.esm"));
        var list = new ScriptObjectListProperty { Name = "HcListProp" };
        list.Objects.Add(element);
        var entry = new ScriptEntry { Name = "HcVmadReadScript" };
        entry.Properties.Add(list);
        var vmad = new DialogResponsesAdapter();
        vmad.Scripts.Add(entry);
        var info = new DialogResponses(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { VirtualMachineAdapter = vmad };
        var fields = ReadEngine.ReadFields(info, new[] { "VirtualMachineAdapter.Scripts[0].Properties" }, 2).Fields;
        Assert.Contains(fields, f => f.Path.EndsWith("Properties[0].Objects", StringComparison.Ordinal));
        Assert.DoesNotContain(fields, f => f.Path.Contains("Properties[0].Objects[", StringComparison.Ordinal));
    }

    // NON-PROPERTY-UNCHANGED: a CTDA at the same depth still stops at its summary.
    [Fact]
    public void AConditionAtTheSameDepthStillStops()
    {
        var mod = new SkyrimMod(new ModKey("hc_vmadread", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var mgef = new MagicEffect(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE);
        mgef.Conditions.Add(new ConditionFloat
        {
            CompareOperator = CompareOperator.EqualTo,
            ComparisonValue = 1f,
            Data = new GetActorValueConditionData { ActorValue = ActorValue.Conjuration },
        });
        var fields = ReadEngine.ReadFields(mgef, new[] { "Conditions" }, 2).Fields;
        Assert.Contains(fields, f => f.Path == "Conditions[0]" && !f.HasValue);
        Assert.DoesNotContain(fields, f => f.Path.Contains("Conditions[0].Data", StringComparison.Ordinal));
    }
}

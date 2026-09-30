using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A composed record missing a required polymorphic arm fails at the serialize as the named null-arm refusal,
/// bare or aggregate-wrapped, with nothing on disk; a legitimately absent half or optional arm still writes. Migrated
/// from the nullarm-guard probe, part B.</summary>
[Trait("tier", "unit")]
public sealed class NullArmSerializeTests : IDisposable
{
    readonly WritePathRig _rig = new();

    static WriteRequest AddCondition(StructSpec? spec) =>
        new() { RecordType = "ConstructibleObject", Path = new[] { "Conditions" }, Verb = "Add", Struct = spec };

    static WriteRequest SetArma(string value, params string[] path) =>
        new() { RecordType = "ArmorAddon", Path = path, Verb = "Set", Value = value };

    /// <summary>Build a mod named for <paramref name="stem"/>, let <paramref name="build"/> fill it, and serialize it to
    /// a fresh path; the path is returned whether or not the write threw.</summary>
    string Serialize(string stem, Action<SkyrimMod> build, out Exception? thrown)
    {
        var path = _rig.Out(stem + ".esp");
        var mod = new SkyrimMod(new ModKey(stem, ModType.Plugin), SkyrimRelease.SkyrimSE);
        build(mod);
        thrown = Record.Exception(() => WriteEngine.WritePatch(mod, new ISkyrimModGetter[] { mod }, path));
        return path;
    }

    string Writes(string stem, Action<SkyrimMod> build)
    {
        var path = Serialize(stem, build, out var thrown);
        Assert.Null(thrown);
        Assert.True(File.Exists(path));
        return path;
    }

    static void ConditionWithoutData(SkyrimMod mod) =>
        WriteEngine.ApplyVerb(mod.ConstructibleObjects.AddNew(),
            AddCondition(LoadOrderService.MapStruct(new StructInput { Type = "ConditionFloat" }, "test", out _)));

    // B1: a Condition composed without its Data arm fails as a NAMED NullArmSerializeException (not a bare NRE)
    // B1b: the refusal names the cause (compose) and preserves the NRE as InnerException
    [Fact]
    public void AConditionWithoutItsDataArmFailsAsTheNamedRefusal()
    {
        Serialize("hc_nullarm_b1", ConditionWithoutData, out var thrown);
        var ex = Assert.IsType<NullArmSerializeException>(thrown);
        Assert.Contains("compose", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<NullReferenceException>(ex.InnerException);
    }

    // B1c: nothing was written — all-or-nothing, the target is untouched
    [Fact]
    public void ANullArmRefusalWritesNothing()
        => Assert.False(File.Exists(Serialize("hc_nullarm_b1c", ConditionWithoutData, out _)));

    // B1d: the render surfaces the named outer AND the inner NRE (no signal-stripping)
    [Fact]
    public void TheRenderKeepsTheNamedOuterAndTheInnerNre()
    {
        Serialize("hc_nullarm_b1d", ConditionWithoutData, out var thrown);
        var rendered = WriteEngine.Describe(Assert.IsType<NullArmSerializeException>(thrown));
        Assert.Contains("NullArmSerializeException", rendered);
        Assert.Contains("[inner: NullReferenceException", rendered);
    }

    // B1e: Describe appends NOTHING for an inner-less exception (the append is conditional, not noise)
    [Fact]
    public void DescribeAppendsNothingWithoutAnInner()
        => Assert.Equal("InvalidOperationException: bare", WriteEngine.Describe(new InvalidOperationException("bare")));

    // B2: optional null polymorphic fields (NPC Sound/Level) still serialize fine (no false refusal)
    [Fact]
    public void OptionalNullPolyFieldsStillWrite()
        => Writes("hc_nullarm_b2", mod =>
        {
            mod.Npcs.AddNew();
            mod.Npcs.AddNew().Configuration.Level = new NpcLevel { Level = 1 };
        });

    // B3: a single-gender skin AA (SkinTexture.Female only, Male un-set) now WRITES — fresh single-gender create just works
    [Fact]
    public void ASingleGenderSkinTextureWrites()
    {
        var path = Writes("hc_nullarm_b3", mod =>
            WriteEngine.ApplyVerb(mod.ArmorAddons.AddNew(), SetArma("000801:hc_nullarm_b3.esp", "SkinTexture", "Female")));
        var back = _rig.Open(path).ArmorAddons.Single();
        Assert.Equal(0x801u, back.SkinTexture!.Female.FormKey.ID);   // the set half landed, not just "no throw"
    }

    // B4: a single-gender MODEL half (ArmorAddon WorldModel, Male null) still serializes fine (no false refusal)
    [Fact]
    public void ASingleGenderModelHalfStillWrites()
        => Writes("hc_nullarm_b4", mod =>
            WriteEngine.ApplyVerb(mod.ArmorAddons.AddNew(), SetArma("meshes\\test.nif", "WorldModel", "Female", "File")));

    // B5: SkinTexture.Female + Male explicitly cleared ('0') also serializes fine (belt-and-suspenders with B3's auto-empty half)
    [Fact]
    public void AnExplicitlyClearedOtherHalfWrites()
        => Writes("hc_nullarm_b5", mod =>
        {
            var arma = mod.ArmorAddons.AddNew();
            WriteEngine.ApplyVerb(arma, SetArma("000801:hc_nullarm_b5.esp", "SkinTexture", "Female"));
            WriteEngine.ApplyVerb(arma, SetArma("0", "SkinTexture", "Male"));
        });

    // B6: an on-disk single-gender skin AA round-trips (overlay -> deep-copy -> serialize) without a null-arm refusal
    [Fact]
    public void AnOnDiskSingleGenderSkinRoundTripsThroughADeepCopy()
    {
        var src = Writes("hc_nullarm_b6src", mod =>
        {
            var arma = mod.ArmorAddons.AddNew();
            WriteEngine.ApplyVerb(arma, SetArma("000801:hc_nullarm_b6src.esp", "SkinTexture", "Female"));
            WriteEngine.ApplyVerb(arma, SetArma("0", "SkinTexture", "Male"));
        });
        var overlay = _rig.Open(src);
        var outPath = _rig.Out("hc_nullarm_b6out.esp");
        var patch = new SkyrimMod(new ModKey("hc_nullarm_b6out", ModType.Plugin), SkyrimRelease.SkyrimSE);
        patch.ArmorAddons.Add(overlay.ArmorAddons.First().DeepCopy());
        WriteEngine.WritePatch(patch, new ISkyrimModGetter[] { overlay }, outPath);
        Assert.True(File.Exists(outPath));
    }

    // B7: the REAL parallel-wrapped null-arm (a forced null gendered formlink half) is re-stamped as the NAMED NullArmSerializeException, not rendered raw
    // B7b: nothing was written — all-or-nothing
    [Fact]
    public void ARealWrappedNullHalfIsReStampedAsTheNamedRefusal()
    {
        var path = Serialize("hc_nullarm_b7", mod =>
        {
            var arma = mod.ArmorAddons.AddNew();
            WriteEngine.ApplyVerb(arma, SetArma("000801:hc_nullarm_b7.esp", "SkinTexture", "Female"));
            arma.SkinTexture!.GetType().GetProperty("Male")!.SetValue(arma.SkinTexture, null);
        }, out var thrown);
        Assert.IsType<NullArmSerializeException>(thrown);
        Assert.False(File.Exists(path));
    }

    // R1: a BARE NRE unwraps to itself
    [Fact]
    public void ABareNreUnwrapsToItself()
    {
        var nre = new NullReferenceException("x");
        Assert.Same(nre, WriteEngine.RootNullArm(nre));
    }

    // R2: a doubly-nested AggregateException(NRE leaf) flattens + unwraps to the NRE
    [Fact]
    public void ADoublyNestedAggregateUnwrapsToTheNre()
    {
        var nre = new NullReferenceException("x");
        Assert.Same(nre, WriteEngine.RootNullArm(new AggregateException(new AggregateException(nre))));
    }

    // R3: a wrapper-of-NRE leaf (SubrecordException-style stand-in) — the inner chain is walked to the NRE
    [Fact]
    public void AWrapperOfAnNreLeafIsWalkedToTheNre()
    {
        var nre = new NullReferenceException("x");
        Assert.Same(nre, WriteEngine.RootNullArm(
            new AggregateException(new AggregateException(new InvalidOperationException("wrapper", nre)))));
    }

    // R4: an aggregate with a NON-NRE-rooted leaf returns null (the genuine error keeps its own type/message — not masked)
    [Fact]
    public void AnAggregateWithANonNreLeafIsNotReStamped()
        => Assert.Null(WriteEngine.RootNullArm(
            new AggregateException(new NullReferenceException("x"), new InvalidOperationException("a different error"))));

    // R5: a non-NRE-rooted throw returns null (propagates unchanged)
    [Fact]
    public void ANonNreThrowIsNotReStamped()
        => Assert.Null(WriteEngine.RootNullArm(new InvalidOperationException("not a null-arm")));

    public void Dispose() => _rig.Dispose();
}

using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The has-family on a signed integer leaf holding a negative value tests its bits at its own width (#1459).</summary>
[Trait("tier", "unit")]
public sealed class SignedIntegerHasTests
{
    readonly SkyrimMod _mod = new(new ModKey("SignedHas", ModType.Master), SkyrimRelease.SkyrimSE);
    readonly IMajorRecordGetter _persistBit31, _deletedBit31, _plain;
    readonly IMajorRecordGetter _npcMinusOne, _npcOne, _intList;

    public SignedIntegerHasTests()
    {
        _persistBit31 = Static("SPersist31", unchecked((int)0x80000400));
        _deletedBit31 = Static("SBit11And31", unchecked((int)0x80000800));
        _plain = Static("SPlain", 0x400);
        _npcMinusOne = Npc("NMinusOne", -1);
        _npcOne = Npc("NOne", 1);
        var act = _mod.Activators.AddNew();
        act.EditorID = "AIntList";
        var script = new ScriptEntry { Name = "SignedHasScript" };
        script.Properties.Add(new ScriptIntListProperty { Name = "Ints", Data = new Noggog.ExtendedList<int> { 3, unchecked((int)0x80000001) } });
        act.VirtualMachineAdapter = new VirtualMachineAdapter { Scripts = { script } };
        _intList = act;
    }

    IMajorRecordGetter Static(string eid, int raw)
    {
        var s = _mod.Statics.AddNew();
        s.EditorID = eid;
        s.MajorRecordFlagsRaw = raw;
        return s;
    }

    IMajorRecordGetter Npc(string eid, short healthOffset)
    {
        var n = _mod.Npcs.AddNew();
        n.EditorID = eid;
        n.Configuration.HealthOffset = healthOffset;
        return n;
    }

    static (HashSet<string?> Hits, string? Fatal) Run(IEnumerable<IMajorRecordGetter> records, string clause)
    {
        var (set, err) = FieldPredicateSet.Parse(new[] { clause });
        Assert.Null(err);
        var hits = records.Where(b => set!.Matches(b)).Select(b => b.EditorID).ToHashSet();
        return (hits, set!.FatalError);
    }

    (HashSet<string?> Hits, string? Fatal) Statics(string clause) => Run(new[] { _persistBit31, _deletedBit31, _plain }, clause);

    [Theory]
    [InlineData("MajorRecordFlagsRaw has 1024", new[] { "SPersist31", "SPlain" })]
    [InlineData("MajorRecordFlagsRaw has 0x80000000", new[] { "SPersist31", "SBit11And31" })]
    [InlineData("MajorRecordFlagsRaw has_any 0x80000000", new[] { "SPersist31", "SBit11And31" })]
    [InlineData("MajorRecordFlagsRaw has_none 2048", new[] { "SPersist31", "SPlain" })]
    public void ANegativeHeaderIntIsTestedOnItsBits(string clause, string[] expected)
    {
        var (hits, fatal) = Statics(clause);
        Assert.Null(fatal);
        Assert.Equal(expected.ToHashSet(), hits);
    }

    [Fact]
    public void TheFlagsViewOfTheHeaderAgrees() =>
        Assert.Equal(Statics("MajorRecordFlagsRaw has 0x80000000").Hits, Statics("SkyrimMajorRecordFlags has 0x80000000").Hits);

    [Fact]
    public void AnOperandBitAboveTheIntsWidthIsNeverSet()
    {
        var (hits, fatal) = Statics("MajorRecordFlagsRaw has_any 0x100000000");
        Assert.Null(fatal);
        Assert.Empty(hits);
    }

    [Fact]
    public void ANegativeShortHasItsOwnWidthsBits()
    {
        var npcs = new[] { _npcMinusOne, _npcOne };
        Assert.Equal(new HashSet<string?> { "NMinusOne" }, Run(npcs, "Configuration.HealthOffset has 0x8000").Hits);
        var (above, fatal) = Run(npcs, "Configuration.HealthOffset has_any 0x10000");
        Assert.Null(fatal);
        Assert.Empty(above);
    }

    [Fact]
    public void ANegativeIntListElementIsBitTested()
    {
        var (hits, fatal) = Run(new[] { _intList }, "VirtualMachineAdapter.Scripts[*any].Properties[*any].Data[*any] has 0x80000000");
        Assert.Null(fatal);
        Assert.Equal(new HashSet<string?> { "AIntList" }, hits);
    }

    [Fact]
    public void ANegativeOperandStillRefuses() =>
        Assert.Contains("must be a bit value", Statics("MajorRecordFlagsRaw has -2147483648").Fatal ?? "");

    // Pins unchanged behaviour: a string leaf is still refused.
    [Fact]
    public void AStringLeafStillRefuses()
    {
        var n = _mod.Npcs.AddNew();
        n.Name = "Bob";
        Assert.Contains("needs a flags/bitmask or integer field", Run(new[] { n }, "Name has 1").Fatal ?? "");
    }

    [Fact]
    public void AStringOfDigitsIsNoIntegerField()
    {
        var n = _mod.Npcs.AddNew();
        n.Name = "5";
        Assert.Contains("'Name' is not one: it read '5'", Run(new[] { n }, "Name has 1").Fatal ?? "");
    }

    // Pins unchanged behaviour: the token is still the signed decimal.
    [Fact]
    public void TheTokenStaysSignedDecimal() =>
        Assert.Equal("-2147482624", ReadEngine.ReadLeaf(_persistBit31, new[] { "MajorRecordFlagsRaw" }).Token);

    // Pins unchanged behaviour: Set takes the signed token back, and the file holds 0x80000400.
    [Fact]
    public void SetTakesTheSignedTokenBackToTheSameBitsOnDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hc-signed-has-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var mod = new SkyrimMod(ModKey.FromFileName("SignedSet.esp"), SkyrimRelease.SkyrimSE);
            var s = mod.Statics.AddNew();
            WriteEngine.ApplyVerb(s, new WriteRequest { RecordType = "Static", Path = new[] { "MajorRecordFlagsRaw" }, Verb = "Set", Value = "-2147482624" });
            var file = Path.Combine(dir, "SignedSet.esp");
            mod.WriteToBinary(file);
            using var back = SkyrimMod.CreateFromBinaryOverlay(file, SkyrimRelease.SkyrimSE);
            var raw = back.Statics.First().MajorRecordFlagsRaw;
            Assert.Equal(0x80000400u, unchecked((uint)raw));
            Assert.Equal("-2147482624", ReadEngine.EmitToken(raw, typeof(int), s).Token);
        }
        finally { Directory.Delete(dir, true); }
    }

    // Pins unchanged behaviour: '=' and the range ops stay on the signed value.
    [Theory]
    [InlineData("MajorRecordFlagsRaw = -2147482624", new[] { "SPersist31" })]
    [InlineData("MajorRecordFlagsRaw < 0", new[] { "SPersist31", "SBit11And31" })]
    public void EqualsAndRangeStayOnTheSignedValue(string clause, string[] expected) =>
        Assert.Equal(expected.ToHashSet(), Statics(clause).Hits);
}

using System.Globalization;
using System.Runtime.InteropServices;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A flags value carrying bits its enum does not name renders as a bare decimal from ToString; the read hangs
/// a display-only decode off the leaf that keeps the named bits and names each other bit as bitN, and leaves the
/// round-trip token alone. The enum bits are found by reflection, so the fixtures follow Mutagen's flag sets.</summary>
[Trait("tier", "unit")]
public sealed class UnknownFlagBitsDisplayTests
{
    readonly SkyrimMod _mod = new(new ModKey("hc_flagbits", ModType.Plugin), SkyrimRelease.SkyrimSE);

    static ulong Bits(object member, Type enumType)
    {
        var u = Convert.ChangeType(member, Enum.GetUnderlyingType(enumType), CultureInfo.InvariantCulture);
        return u switch
        {
            ulong x => x, long x => unchecked((ulong)x), uint x => x, int x => unchecked((ulong)(long)x),
            ushort x => x, short x => unchecked((ulong)(long)x), byte x => x, sbyte x => unchecked((ulong)(long)x),
            _ => Convert.ToUInt64(u, CultureInfo.InvariantCulture),
        };
    }

    static ulong LowestFreeBit(Type enumType, ulong named)
    {
        int width = Marshal.SizeOf(Enum.GetUnderlyingType(enumType)) * 8;
        for (int i = 0; i < width; i++) if ((named & (1UL << i)) == 0) return 1UL << i;
        return 0;
    }

    static int Index(ulong bit) => System.Numerics.BitOperations.TrailingZeroCount(bit);

    static FieldValue Leaf(RecordFields rf, string path) => Assert.Single(rf.Fields, f => f.Path.EndsWith(path, StringComparison.Ordinal));

    /// <summary>The NPC Configuration.Flags enum, one named single-bit member of it, and an unnamed bit.</summary>
    (Npc npc, Type type, object named, ulong namedBit, ulong unknownBit) NpcFlags()
    {
        var npc = _mod.Npcs.AddNew();
        var type = npc.Configuration!.GetType().GetProperty("Flags")!.PropertyType;
        var named = Enum.GetValues(type).Cast<object>().First(m => Bits(m, type) is var b && b != 0 && (b & (b - 1)) == 0);
        ulong all = Enum.GetValues(type).Cast<object>().Aggregate(0UL, (acc, m) => acc | Bits(m, type));
        var unknown = LowestFreeBit(type, all);
        Assert.NotEqual(0UL, unknown);
        return (npc, type, named, Bits(named, type), unknown);
    }

    static FieldValue ReadNpcFlags(Npc npc, Type type, ulong value)
    {
        npc.Configuration!.GetType().GetProperty("Flags")!.SetValue(npc.Configuration, Enum.ToObject(type, value));
        return Leaf(ReadEngine.ReadFields(npc, new[] { "Configuration.Flags" }), "Configuration.Flags");
    }

    // Probe DECODE: "known name + unnamed hex remainder". Probe TOKEN-INTACT: "round-trip token = bare decimal".
    [Fact]
    public void ANamedBitPlusAnUnnamedBit_DisplaysTheNameAndABitTokenAndKeepsTheDecimalToken()
    {
        var (npc, type, named, namedBit, unknownBit) = NpcFlags();
        var leaf = ReadNpcFlags(npc, type, namedBit | unknownBit);
        Assert.True(leaf.HasValue);
        Assert.Equal($"{named}, bit{Index(unknownBit)}", leaf.Display);
        Assert.Equal(Enum.ToObject(type, namedBit | unknownBit).ToString(), leaf.Token);
        Assert.NotEqual(leaf.Display, leaf.Token);
    }

    // Probe ALL-NAMED-NULL: "no decode when every bit is named".
    [Fact]
    public void OnlyNamedBits_GetNoDecodeDisplay()
    {
        var (npc, type, named, namedBit, _) = NpcFlags();
        var leaf = ReadNpcFlags(npc, type, namedBit);
        Assert.Null(leaf.Display);
        Assert.Equal(named.ToString(), leaf.Token);
    }

    // Probe BIPED-ROUTED: "biped leaf keeps its slot decode".
    [Fact]
    public void ABipedFlagsLeaf_KeepsItsSlotDecode()
    {
        var armo = _mod.Armors.AddNew();
        armo.BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Body };
        var leaf = Leaf(ReadEngine.ReadFields(armo, new[] { "BodyTemplate.FirstPersonFlags" }), "BodyTemplate.FirstPersonFlags");
        Assert.Contains("slot", leaf.Display);
        Assert.Equal("slot 32", leaf.Display);
    }

    // Probe COMBO-ALONE: "combo-only bit → a bitN token". Probe COMBO-MIXED: "combo-only+unnamed → no decimal name".
    [Fact]
    public void ABitOnlyInsideAComboMember_IsPartOfTheUnknownRemainder()
    {
        var pack = _mod.Packages.AddNew();
        var prop = pack.GetType().GetProperty("Flags")!;
        var type = prop.PropertyType;
        ulong single = 0, all = 0;
        foreach (var m in Enum.GetValues(type))
        {
            var b = Bits(m, type);
            all |= b;
            if (b != 0 && (b & (b - 1)) == 0) single |= b;
        }
        ulong comboOnly = all & ~single;
        ulong comboBit = comboOnly & (0UL - comboOnly);
        ulong freeBit = LowestFreeBit(type, all);
        Assert.NotEqual(0UL, comboBit);
        Assert.NotEqual(0UL, freeBit);

        prop.SetValue(pack, Enum.ToObject(type, comboBit));
        Assert.Equal($"bit{Index(comboBit)}", Leaf(ReadEngine.ReadFields(pack, new[] { "Flags" }), "Flags").Display);
        prop.SetValue(pack, Enum.ToObject(type, comboBit | freeBit));
        Assert.Equal($"bit{Math.Min(Index(comboBit), Index(freeBit))}, bit{Math.Max(Index(comboBit), Index(freeBit))}", Leaf(ReadEngine.ReadFields(pack, new[] { "Flags" }), "Flags").Display);
    }
}

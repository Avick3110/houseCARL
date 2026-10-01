using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Skyrim.Assets;
using Noggog;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Each value-type coercion builds an instance of the target type from a sample string, with the value the
/// string says. Migrated from the coerce-selftest probe.</summary>
[Trait("tier", "unit")]
public sealed class CoerceSampleTests
{
    static object Coerce(Type type, string text)
    {
        Assert.True(WriteEngine.TryCoerce(text, type, out var v, out var ex), ex?.ToString());
        Assert.NotNull(v);
        Assert.IsAssignableFrom(type, v);
        return v!;
    }

    public static TheoryData<string, Type, string> Samples => new()
    {
        { "Color rgb", typeof(System.Drawing.Color), "255,128,0" },
        { "Color rgba", typeof(System.Drawing.Color), "255,128,0,64" },
        { "DateTime", typeof(DateTime), "2026-05-30T12:00:00" },
        { "Char", typeof(char), "G" },
        { "String[]", typeof(string[]), "Foo,Bar" },
        { "FormKey", typeof(FormKey), "012E4B:Skyrim.esm" },
        { "ModKey", typeof(ModKey), "Skyrim.esm" },
        { "Percent", typeof(Percent), "0.5" },
        { "P3Float", typeof(P3Float), "1.5,2.5,3.5" },
        { "P2Int16", typeof(P2Int16), "4,5" },
        { "MemorySlice<byte>", typeof(MemorySlice<byte>), "DEADBEEF" },
        { "ReadOnlyMemSlice", typeof(ReadOnlyMemorySlice<byte>), "CAFE" },
        { "RecordType", typeof(RecordType), "EDID" },
        { "TimeOnly", typeof(TimeOnly), "06:30:00" },
        { "AssetLink<Texture>", typeof(AssetLink<SkyrimTextureAssetType>), @"textures\hc\test.dds" },
        { "IAssetLink<Sound> (setter iface)", typeof(IAssetLink<SkyrimSoundAssetType>), @"Sound\fx\hc\test.wav" },
        { "IAssetLinkGetter<Sound> (getter iface)", typeof(IAssetLinkGetter<SkyrimSoundAssetType>), @"Sound\fx\hc\test.wav" },
        { "IAssetLink<Texture> (setter iface)", typeof(IAssetLink<SkyrimTextureAssetType>), @"textures\hc\test2.dds" },
        { "IAssetLinkGetter<Texture> (getter iface)", typeof(IAssetLinkGetter<SkyrimTextureAssetType>), @"textures\hc\test2.dds" },
    };

    // coerce-selftest: '<label>' constructed + assignable
    [Theory]
    [MemberData(nameof(Samples))]
    public void TheSampleBuildsAnAssignableInstance(string label, Type type, string text)
    {
        _ = label;
        Coerce(type, text);
    }

    [Fact]
    public void AColorCarriesItsChannelsAndAnOptionalAlpha()
    {
        var rgb = (System.Drawing.Color)Coerce(typeof(System.Drawing.Color), "255,128,0");
        Assert.Equal((255, 128, 0), (rgb.R, rgb.G, rgb.B));
        var rgba = (System.Drawing.Color)Coerce(typeof(System.Drawing.Color), "255,128,0,64");
        Assert.Equal(64, rgba.A);
    }

    [Fact]
    public void AByteSliceCarriesTheHexBytes()
        => Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, ((MemorySlice<byte>)Coerce(typeof(MemorySlice<byte>), "DEADBEEF")).ToArray());

    [Fact]
    public void APointCarriesEachComponent()
    {
        Assert.Equal(new P3Float(1.5f, 2.5f, 3.5f), Coerce(typeof(P3Float), "1.5,2.5,3.5"));
        Assert.Equal(new P2Int16(4, 5), Coerce(typeof(P2Int16), "4,5"));
    }

    [Fact]
    public void AnAssetLinkInterfaceCarriesTheGivenPath()
        => Assert.Equal(@"Sound\fx\hc\test.wav",
            ((IAssetLinkGetter<SkyrimSoundAssetType>)Coerce(typeof(IAssetLinkGetter<SkyrimSoundAssetType>), @"Sound\fx\hc\test.wav")).GivenPath);
}

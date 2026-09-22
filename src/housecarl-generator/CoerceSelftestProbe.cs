using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using HousecarlCore;
using static HousecarlCore.WriteEngine;

namespace HousecarlGenerator;

/// <summary>The <c>coerce-selftest</c> probe: each value-type coercion builds an assignable instance from a sample string.</summary>
public static class CoerceSelftestProbe
{
    // ---- COERCE-SELFTEST: do the value-type CONSTRUCTIONS actually build a valid, assignable instance from a
    //  sample string? coerce-audit covers only RECOGNITION. Diagnoses on failure by dumping the ctor surface.
    [CiProbe("coerce-selftest")]
    public static int RunCoerceSelftest(string[] args)
    {
        var texAsset = typeof(SkyrimMod).Assembly.GetType("Mutagen.Bethesda.Skyrim.Assets.SkyrimTextureAssetType");

        var samples = new List<(string label, Type type, string text)>
        {
            ("Color rgb",         typeof(System.Drawing.Color), "255,128,0"),
            ("Color rgba",        typeof(System.Drawing.Color), "255,128,0,64"),
            ("DateTime",          typeof(DateTime), "2026-05-30T12:00:00"),
            ("Char",              typeof(char), "G"),
            ("String[]",          typeof(string[]), "Foo,Bar"),
            ("FormKey",           typeof(FormKey), "012E4B:Skyrim.esm"),
            ("ModKey",            typeof(ModKey), "Skyrim.esm"),
            ("Percent",           typeof(Noggog.Percent), "0.5"),
            ("P3Float",           typeof(Noggog.P3Float), "1.5,2.5,3.5"),
            ("P2Int16",           typeof(Noggog.P2Int16), "4,5"),
            ("MemorySlice<byte>", typeof(MemorySlice<byte>), "DEADBEEF"),
            ("ReadOnlyMemSlice",  typeof(ReadOnlyMemorySlice<byte>), "CAFE"),
            ("RecordType",        typeof(RecordType), "EDID"),
            ("TimeOnly",          typeof(TimeOnly), "06:30:00"),
        };
        if (texAsset is not null)
            samples.Add(("AssetLink<Texture>", typeof(Mutagen.Bethesda.Plugins.Assets.AssetLink<>).MakeGenericType(texAsset), @"textures\hc\test.dds"));
        // AssetLink INTERFACE forms — the runtime type a collection element exposes, which a concrete-only rule misses.
        var sndAsset = typeof(SkyrimMod).Assembly.GetType("Mutagen.Bethesda.Skyrim.Assets.SkyrimSoundAssetType");
        if (sndAsset is not null)
        {
            samples.Add(("IAssetLink<Sound> (setter iface)", typeof(Mutagen.Bethesda.Plugins.Assets.IAssetLink<>).MakeGenericType(sndAsset), @"Sound\fx\hc\test.wav"));
            samples.Add(("IAssetLinkGetter<Sound> (getter iface)", typeof(Mutagen.Bethesda.Plugins.Assets.IAssetLinkGetter<>).MakeGenericType(sndAsset), @"Sound\fx\hc\test.wav"));
        }
        if (texAsset is not null)
        {
            samples.Add(("IAssetLink<Texture> (setter iface)", typeof(Mutagen.Bethesda.Plugins.Assets.IAssetLink<>).MakeGenericType(texAsset), @"textures\hc\test2.dds"));
            samples.Add(("IAssetLinkGetter<Texture> (getter iface)", typeof(Mutagen.Bethesda.Plugins.Assets.IAssetLinkGetter<>).MakeGenericType(texAsset), @"textures\hc\test2.dds"));
        }

        int ok = 0;
        foreach (var (label, type, text) in samples)
        {
            try
            {
                var v = Coerce(text, type);
                var assignable = v is not null && type.IsInstanceOfType(v);
                Console.WriteLine($"  [{(assignable ? "OK" : "??")}] {label,-20} '{text}' -> {v?.GetType().Name ?? "null"}  (= {v})");
                if (assignable) ok++;
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException is { } ie ? $" / {ie.GetType().Name}: {ie.Message}" : "";
                Console.WriteLine($"  [FAIL] {label,-20} '{text}' -> {ex.GetType().Name}: {ex.Message}{inner}");
            }
        }
        Console.WriteLine();
        Console.WriteLine($"=== coerce-selftest: {ok}/{samples.Count} constructed + assignable ===");
        return ok == samples.Count ? 0 : 1;
    }
}

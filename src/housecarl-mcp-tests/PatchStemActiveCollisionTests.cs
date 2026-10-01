using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The default patch stem "Patch" against an ACTIVE plugin of the same basename in another mod folder: the stem
/// steps to "Patch_001" rather than writing a second active "Patch.esp"; a stem nothing in the order uses stays bare;
/// the active plugin is never written. <see cref="PatchStemShadowTests"/> covers the inactive-plugin shadow.
/// </summary>
[Trait("tier", "integration")]
public sealed class PatchStemActiveCollisionTests : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-patch-stem-active-");
    readonly string _base;
    readonly string _fid;

    public PatchStemActiveCollisionTests()
    {
        var key = new ModKey("Patch", ModType.Plugin);
        _base = _mo2.InMod("BaseMod", key);
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        var w = m.Weapons.AddNew(); w.EditorID = "HcStemWeap"; w.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1 };
        m.BeginWrite.ToPath(_base).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        _fid = ScratchMo2.Fid(w.FormKey);
        _mo2.Profile("Patch.esp\r\n", "*Patch.esp\r\n", "+BaseMod\r\n");
    }

    public void Dispose() => _mo2.Delete();

    BulkOp Damage(int v) => new() { Formid = _fid, FieldPath = "BasicStats.Damage", Verb = "Set", Value = v.ToString() };

    // Probe arm COLLISION: the default stem dodges the active Patch.esp to "houseCARL - Patch_001\Patch_001.esp".
    [Fact]
    public void TheDefaultStemStepsPastAnActivePluginOfTheSameName()
    {
        using var svc = _mo2.Open();

        var o = svc.ApplyEdits(new[] { Damage(20) }, null, null);

        Assert.True(o.Success, o.Error);
        Assert.Equal("Patch_001.esp", Path.GetFileName(o.OutputPath));
        Assert.Equal("houseCARL - Patch_001", Path.GetFileName(Path.GetDirectoryName(o.OutputPath)));
    }

    // Probe arm CONTROL: a stem with no load-order clash is used as-is.
    [Fact]
    public void AStemNothingInTheOrderUsesStaysBare()
    {
        using var svc = _mo2.Open();

        var o = svc.ApplyEdits(new[] { Damage(21) }, "HcNoSuchStemZZ", null);

        Assert.True(o.Success, o.Error);
        Assert.Equal("HcNoSuchStemZZ.esp", Path.GetFileName(o.OutputPath));
        Assert.Equal("houseCARL - HcNoSuchStemZZ", Path.GetFileName(Path.GetDirectoryName(o.OutputPath)));
    }

    // Probe arm ORIGINAL: the active Patch.esp is byte-untouched by the default-stem write.
    [Fact]
    public void TheActivePluginIsNotWritten()
    {
        var before = File.ReadAllBytes(_base);
        using var svc = _mo2.Open();

        var o = svc.ApplyEdits(new[] { Damage(20) }, null, null);

        Assert.True(o.Success, o.Error);
        Assert.Equal(before, File.ReadAllBytes(_base));
    }
}

using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The runtime base and the runtime-FormID lookup off a REAL order rather than a hand-fed stub: a full
/// plugin is numbered from its load index, a light one from the light block, and a plugin sitting at or after one
/// whose kind could not be read has no base at all — the guess that would otherwise name another plugin's record.</summary>
[Trait("tier", "integration")]
public sealed class RuntimeOrderBaseTests : IDisposable
{
    const string MasterName = "hcRtMaster.esm", LightName = "hcRtLight.esl", BrokenName = "hcRtBroken.esp", LateName = "hcRtLate.esp";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-runtime-base-" + Guid.NewGuid().ToString("N"));
    readonly LoadOrderResolver _resolver;
    readonly FormKey _lightWeapon;

    public RuntimeOrderBaseTests()
    {
        Directory.CreateDirectory(_dir);
        var master = new SkyrimMod(ModKey.FromNameAndExtension(MasterName), SkyrimRelease.SkyrimSE);
        master.Weapons.AddNew().EditorID = "hcRtMasterW";
        var light = new SkyrimMod(ModKey.FromNameAndExtension(LightName), SkyrimRelease.SkyrimSE) { IsSmallMaster = true };
        var lw = light.Weapons.AddNew();
        lw.EditorID = "hcRtLightW";
        _lightWeapon = lw.FormKey;
        var broken = new SkyrimMod(ModKey.FromNameAndExtension(BrokenName), SkyrimRelease.SkyrimSE);
        broken.Weapons.AddNew().EditorID = "hcRtBrokenW";
        var late = new SkyrimMod(ModKey.FromNameAndExtension(LateName), SkyrimRelease.SkyrimSE);
        late.Weapons.AddNew().EditorID = "hcRtLateW";

        var paths = new List<string>();
        foreach (var m in new[] { master, light, broken, late })
        {
            var p = Path.Combine(_dir, m.ModKey.FileName.String);
            m.BeginWrite.ToPath(p).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            paths.Add(p);
        }
        // A valid header with a truncated body: the could-not-be-opened class, which leaves this plugin's kind unread.
        var brokenPath = Path.Combine(_dir, BrokenName);
        var whole = File.ReadAllBytes(brokenPath);
        File.WriteAllBytes(brokenPath, whole[..(whole.Length - 12)]);

        _resolver = LoadOrderResolver.Build(paths.ToArray());
    }

    public void Dispose()
    {
        _resolver.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // Probe: the base is composed the plugin's own way — the load index for a full plugin, the light block for a light one.
    [Fact]
    public void AFullPluginIsNumberedFromItsLoadIndexAndALightOneFromTheLightBlock()
    {
        var view = _resolver.Capture();

        Assert.Equal(0x00000000u, view.RuntimeBaseOf(MasterName));
        Assert.Equal(FormIdRange.LightMasterIndexPrefix, view.RuntimeBaseOf(LightName));
    }

    // Probe: the lookup off that base names the record the game prints that number for.
    [Fact]
    public void ARuntimeFormIdInTheLightBlockNamesTheLightPluginsRecord()
    {
        var view = _resolver.Capture();
        uint runtime = view.RuntimeBaseOf(LightName)!.Value + (_lightWeapon.ID & FormIdRange.LightObjectIdMask);

        Assert.Equal(_lightWeapon, view.FromRuntimeFormId(runtime).Record);
    }

    // Probe: a plugin at or after one whose kind could not be read has no base, so a target on it names nothing
    // instead of a record composed from a guessed light flag; one before it answers as it always has.
    [Fact]
    public void APluginAfterOneWhoseKindCouldNotBeReadHasNoRuntimeBase()
    {
        var view = _resolver.Capture();

        Assert.True(view.IsUnopenable(BrokenName), "the fixture's broken plugin opened after all");
        Assert.Null(view.RuntimeBaseOf(LateName));
        Assert.NotNull(view.RuntimeBaseOf(MasterName));
    }
}

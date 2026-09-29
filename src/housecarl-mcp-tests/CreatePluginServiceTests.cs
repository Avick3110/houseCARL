using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The service half of <c>housecarl_create_plugin</c>: <see cref="LoadOrderService.CreatePlugin"/> over a
/// synthetic MO2 instance. The name is used exactly, and every collision refuses rather than renames. Migrated from
/// the <c>create-plugin-guard</c> probe's service arms. Each test builds its own world: every one adds a mod folder.</summary>
[Trait("tier", "integration")]
[Collection("records")]
public sealed class CreatePluginServiceTests
{
    // WIRE-CREATE: service writes the EXACT-named header-only plugin in a houseCARL folder (no auto-suffix)
    [Fact]
    public void TheServiceWritesTheExactNamedHeaderOnlyPluginInItsOwnHousecarlFolder()
    {
        using var w = new RecordsWorld();

        var o = w.Svc.CreatePlugin("HcCpTrigger");

        Assert.True(o.Success, o.Error);
        Assert.Equal("HcCpTrigger.esp", Path.GetFileName(o.OutputPath));
        Assert.Equal(Path.Combine(w.ModsDir, "houseCARL - HcCpTrigger"), Path.GetDirectoryName(o.OutputPath));
        Assert.True(HeaderOnlyPlugin.IsTes4(o.OutputPath));
        Assert.Equal(0, HeaderOnlyPlugin.Reopen(o.OutputPath).Records);
    }

    // REJ-FOLDER: re-creating the same name refuses loud (no auto-suffix); exactly one folder remains
    [Fact]
    public void ReCreatingTheSameNameIsRefusedAndNoSuffixedFolderAppears()
    {
        using var w = new RecordsWorld();
        Assert.True(w.Svc.CreatePlugin("HcCpTrigger").Success);

        var o = w.Svc.CreatePlugin("HcCpTrigger");

        Assert.False(o.Success);
        Assert.Contains("already exists", o.Error);
        Assert.Single(Directory.EnumerateDirectories(w.ModsDir, "houseCARL - HcCpTrigger*"));
    }

    // REJ-NAME-ACTIVE: naming an already-active plugin refuses loud (no shadow), no folder
    [Fact]
    public void NamingAPluginAlreadyActiveInTheOrderIsRefusedAndNoFolderIsMade()
    {
        using var w = new RecordsWorld();
        var activeStem = Path.GetFileNameWithoutExtension(w.MasterName);   // active as an .esm; the create would write .esp

        var o = w.Svc.CreatePlugin(activeStem);

        Assert.False(o.Success);
        Assert.Contains("already active", o.Error);
        Assert.Empty(Directory.EnumerateDirectories(w.ModsDir, "houseCARL - " + activeStem + "*"));
    }

    // ESL-WIRE: esl=true through the service lands a light-flagged plugin on disk
    [Fact]
    public void EslThroughTheServiceLandsALightFlaggedPluginOnDisk()
    {
        using var w = new RecordsWorld();

        var o = w.Svc.CreatePlugin("HcCpEslWire", esl: true);

        Assert.True(o.Success, o.Error);
        Assert.True(HeaderOnlyPlugin.Reopen(o.OutputPath).Esl);
    }

    // COLD-START: create_plugin as the first op on a fresh service succeeds (derives ModsDir; no false config error)
    // Resolver.Capture() is the first derive today and ConfiguredRoots() the second; only losing both fails this.
    [Fact]
    public void CreatePluginAsTheFirstCallOnAFreshServiceDerivesTheModsFolderItself()
    {
        using var w = new RecordsWorld();
        using var cold = LoadOrderService.WithInstance(w.Instance, 0, new UserConfigStore(w.Scratch("cold-user.json")));

        var o = cold.CreatePlugin("HcCpCold");

        Assert.True(o.Success, o.Error);
        Assert.Equal(Path.Combine(w.ModsDir, "houseCARL - HcCpCold", "HcCpCold.esp"), o.OutputPath);
    }
}

using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Reads back what a header-only plugin promises: the TES4 signature, and the record count, masters,
/// light-master flag, author and description off a fresh overlay of the written file.</summary>
static class HeaderOnlyPlugin
{
    public static bool IsTes4(string path)
    {
        using var fs = File.OpenRead(path);
        var b = new byte[4];
        return fs.Read(b, 0, 4) == 4 && b[0] == 'T' && b[1] == 'E' && b[2] == 'S' && b[3] == '4';
    }

    public static (int Records, List<string> Masters, bool Esl, string? Author, string? Description) Reopen(string path)
    {
        using var back = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        return (back.EnumerateMajorRecords().Count(),
                back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList(),
                back.IsSmallMaster, back.ModHeader.Author, back.ModHeader.Description);
    }
}

/// <summary>The builder half of <c>housecarl_create_plugin</c>: <see cref="WritePatchBuilder.CreatePlugin"/>
/// straight to a temp path, no MO2 instance. Migrated from the <c>create-plugin-guard</c> probe's core arms.</summary>
[Trait("tier", "unit")]
public sealed class CreatePluginBuilderTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-create-plugin-builder-" + Guid.NewGuid().ToString("N"));

    public CreatePluginBuilderTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ } }

    // HEADER-ONLY-ESP: 0 records, 0 masters, NOT ESL, sig TES4, author/description round-trip
    [Fact]
    public void AnEspHeaderOnlyPluginHasNoRecordsNoMastersNoEslFlagAndKeepsItsHeaderText()
    {
        var p = Path.Combine(_root, "HcCpEsp.esp");

        var o = WritePatchBuilder.CreatePlugin(p, esl: false, author: "  houseCARL ", description: "trigger plugin");

        Assert.True(o.Success, o.Error);
        Assert.Equal(0, o.RecordCount);
        Assert.Empty(o.Masters);
        Assert.True(HeaderOnlyPlugin.IsTes4(p));
        var back = HeaderOnlyPlugin.Reopen(p);
        Assert.Equal(0, back.Records);
        Assert.Empty(back.Masters);
        Assert.False(back.Esl);
        Assert.Equal("houseCARL", back.Author);          // trimmed on the way in
        Assert.Equal("trigger plugin", back.Description);
    }

    // HEADER-ONLY-ESL: esl=true sets the light-master flag (survives reopen); still 0 records / 0 masters
    [Fact]
    public void AnEslHeaderOnlyPluginCarriesTheLightMasterFlagOnDisk()
    {
        var p = Path.Combine(_root, "HcCpEslCore.esp");

        var o = WritePatchBuilder.CreatePlugin(p, esl: true, author: null, description: null);

        Assert.True(o.Success, o.Error);
        var back = HeaderOnlyPlugin.Reopen(p);
        Assert.True(back.Esl);
        Assert.Equal(0, back.Records);
        Assert.Empty(back.Masters);
    }
}

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

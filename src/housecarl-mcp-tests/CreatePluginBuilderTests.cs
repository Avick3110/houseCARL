using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

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

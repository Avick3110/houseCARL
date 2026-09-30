using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The records source= pole says which arm a plugin resolved to and, when it is not active, why: which copy
/// is served, whether the mod folder is off, the plugin unticked, or MO2 has not registered it. The cause is one fact
/// however the plugin is addressed. Migrated from the bulk-primitives-wave3 probe's P8c source-pole arms.</summary>
[Trait("tier", "integration")]
public sealed class SourcePoleCauseTests : IClassFixture<AbsenceCauseWorld>
{
    readonly AbsenceCauseWorld _w;
    public SourcePoleCauseTests(AbsenceCauseWorld w) => _w = w;

    const string Active = "active in the load order";

    string Where(string plugin, string? mod = null)
    {
        var pole = _w.Svc.ProbeSourceArm(plugin, mod, out var err);
        return err ?? pole!.Where;
    }

    /// <summary>The cause alone, out of the composed not-active label; null when the label states none.</summary>
    string? Cause(string plugin, string? mod = null)
    {
        const string marker = "; NOT active — ";
        var label = Where(plugin, mod);
        var at = label.IndexOf(marker, StringComparison.Ordinal);
        return at < 0 ? null : label[(at + marker.Length)..].TrimEnd(')');
    }

    string CauseOf(string plugin, string? mod = null)
    {
        var c = Cause(plugin, mod);
        Assert.NotNull(c);
        return c!;
    }

    // probe: source pole: an ENABLED plugin addressed by path resolves to the ACTIVE arm, not a not-active one
    [Fact]
    public void AnEnabledPluginByPathResolvesActive()
        => Assert.Equal(Active, Where(_w.ReplacerPath));

    // probe: source pole: the same-named backup OUTSIDE the install says no layer provides it — not 'disabled'
    [Fact]
    public void ASameNamedBackupOutsideTheInstallSaysNoLayerProvidesIt()
        => Assert.Contains("no MO2 layer was found providing this exact path", CauseOf(_w.ArchivePath));

    // probe: source pole: a SHADOWED copy in a lower-priority enabled mod says SHADOWED and names the serving mod
    [Fact]
    public void AShadowedCopySaysShadowedAndNamesTheServingMod()
    {
        var c = CauseOf(_w.ShadowPath);
        Assert.Contains("SHADOWED", c);
        Assert.Contains("'DiffRepl'", c);
        Assert.DoesNotContain("UNTICKED", c);
    }

    // probe: source pole: the shadowed copy still reads its own value (66) off the file
    [Fact]
    public void TheShadowedCopyStillReadsItsOwnValue()
    {
        var source = JsonDocument.Parse(JsonSerializer.Serialize(_w.ShadowPath)).RootElement.Clone();
        var r = RecordsTools.Records(_w.Svc, formids: new[] { _w.WFid }, source: source,
            project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "BasicStats.Damage" } });
        Assert.Contains("BasicStats.Damage = 66", r);
    }

    // probe: source pole: a game-Data-served plugin listed BEHIND a disabled copy still resolves ACTIVE
    [Fact]
    public void ADataServedPluginBehindADisabledCopyResolvesActive()
        => Assert.Equal(Active, Where(_w.DataServedPath));

    // probe: source pole: game Data serves the copy a disabled decoy is walked ahead of — the decoy never decides
    [Fact]
    public void TheDataCopyIsServedNotTheDisabledDecoyAheadOfIt()
    {
        var c = CauseOf(_w.DataOffPath);
        Assert.Contains("not registered in MO2's load order", c);
        Assert.DoesNotContain("SHADOWED", c);
        Assert.DoesNotContain("switched OFF", c);
    }

    // probe: source pole: that disabled copy blames the MOD FOLDER by name, and never calls the plugin unticked
    [Fact]
    public void ACopyInADisabledModBlamesTheModFolderByName()
    {
        var c = CauseOf(_w.DecoyPath);
        Assert.Contains("DataServedDecoy", c);
        Assert.Contains("switched OFF", c);
        Assert.DoesNotContain("UNTICKED", c);
    }

    // probe: source pole: a plugin in an ENABLED mod but UNTICKED in plugins.txt says so and names plugins.txt
    [Fact]
    public void AnUntickedPluginSaysSoAndNamesPluginsTxt()
    {
        var c = CauseOf(_w.UntickedPath);
        Assert.Contains("UNTICKED", c);
        Assert.Contains("plugins.txt", c);
        Assert.Contains(_w.UntickedName, c);
    }

    // probe: source pole: the same plugin BY FILENAME gives the SAME cause — one fact, however addressed
    [Fact]
    public void TheFilenameGivesThePathsCause()
        => Assert.Equal(CauseOf(_w.UntickedPath), CauseOf(_w.UntickedName));

    // probe: source pole: and via mod= too — the address lanes agree on the cause, not just on the state
    [Fact]
    public void TheModFormGivesThePathsCause()
        => Assert.Equal(CauseOf(_w.UntickedPath), CauseOf(_w.UntickedName, "DiffUnticked"));

    // probe: source pole: a DISABLED mod says switch it on; an UNLISTED folder says refresh — never swapped
    [Fact]
    public void ADisabledModSaysSwitchItOnAndAnUnlistedFolderDoesNot()
    {
        var disabled = CauseOf(_w.DonorName);
        Assert.Contains("switched OFF", disabled);
        Assert.Contains("switch it on", disabled);
        var unlisted = CauseOf(_w.UnlistedName);
        Assert.Contains("not registered", unlisted);
        Assert.DoesNotContain("switch it on", unlisted);
    }

    // probe: #271 why: the SERVED copy of an unregistered plugin says so, blaming neither the tick nor the mod
    [Fact]
    public void AnUnregisteredServedPluginBlamesNeitherTheTickNorTheMod()
    {
        var c = CauseOf(_w.UnregisteredName);
        Assert.Contains("not registered in MO2's load order", c);
        Assert.DoesNotContain("UNTICKED", c);
        Assert.DoesNotContain("which the game does not load", c);
    }
}

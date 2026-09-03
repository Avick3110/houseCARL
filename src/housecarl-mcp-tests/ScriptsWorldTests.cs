using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Pex;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The Papyrus fixture's own proofs (#486 PR 1). These do not test the script-property surface — they test
/// that <see cref="ScriptsWorld"/> is what it claims to be, so the arms PR 2 writes on top of it can assert
/// findings without also having to prove their fixture.
///
/// <para>The load-bearing one is the loose-layer arm. <c>ScriptCheckResult.RecordsWithScripts</c> is
/// incremented before any .pex is opened, so a world whose planted <c>.pex</c> files were never found would
/// still report four script-bearing records — with every declaration silently unverifiable. Counting alone
/// therefore cannot tell a resolved fixture from a missing one; the declarations have to be observed.</para>
/// </summary>
[Collection("scripts")]
[Trait("tier", "integration")]
public sealed class ScriptsWorldTests
{
    readonly ScriptsWorld _w;
    public ScriptsWorldTests(ScriptsFixture f) => _w = f.W;

    /// <summary>
    /// PEX-ROUNDTRIP, re-homed from <c>ScriptPropertyCheckProbe</c>: the planted child .pex is a valid
    /// Skyrim .pex whose Auto property table and parent link survive the write. Everything else in the
    /// fixture rests on this — a .pex the product cannot read makes every declaration unverifiable.
    /// </summary>
    [Fact]
    public void ThePlantedChildPexReadsBackWithItsAutoPropertyAndItsParentClass()
    {
        var back = PexFile.CreateFromFile(
            Path.Combine(_w.ScriptsDir, ScriptsWorld.ChildScript + ".pex"), GameCategory.Skyrim);

        var obj = Assert.Single(back.Objects);
        Assert.Equal(ScriptsWorld.BaseScript, obj.ParentClassName, ignoreCase: true);

        var prop = Assert.Single(obj.Properties,
            p => string.Equals(p.Name, ScriptsWorld.ObjectProperty, StringComparison.OrdinalIgnoreCase));
        Assert.True(prop.Flags.HasFlag(PropertyFlags.AutoVar));
    }

    /// <summary>
    /// The world loads through the engine the way <see cref="RecordsWorld"/>'s does — a real MO2 instance
    /// behind <c>LoadOrderService</c> — and the script-bearing population is exactly the four VMAD-carrying
    /// records. The script-free weapon is the teeth: it is in the same plugin and must not be counted.
    /// </summary>
    [Fact]
    public void TheServiceSweepsTheInstanceAndExactlyTheVmadCarryingRecordsAreScriptBearing()
    {
        var res = _w.Svc.ValidateScripts(null, 1000);

        Assert.True(res.Success, res.Error);
        Assert.Equal(ScriptsWorld.RecordsWithScripts, res.RecordsWithScripts);

        // The fully-bound control reports nothing, and the script-free record is not swept at all — so the
        // reported set is the other three, named by their fixture-known EditorIDs.
        var reported = res.Reports.Select(r => r.EditorId).OrderBy(e => e, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[] { ScriptsWorld.AliasQuestEditorId, ScriptsWorld.FootgunEditorId, ScriptsWorld.NoPexEditorId }
                .OrderBy(e => e, StringComparer.Ordinal).ToArray(),
            reported);
    }

    /// <summary>
    /// BOTH planted .pex files resolve through the MO2 loose-file layer — the fixture's vacuity canary.
    /// The child's own declaration and the ancestor's are asserted separately: the child proves the mod's
    /// <c>Scripts\</c> folder is on the asset path at all, and the inherited one proves the extends chain
    /// was walked to a SECOND file, which a world with only one resolvable .pex could not produce.
    /// </summary>
    [Fact]
    public void BothPlantedPexFilesResolveThroughTheLooseLayer_SoTheDeclarationsAreRealNotUnverifiable()
    {
        var res = _w.Svc.ValidateScripts(null, 1000);
        var foot = Assert.Single(res.Reports, r => r.Record == _w.Footgun);

        Assert.Empty(foot.Unverifiable);   // nothing about this record's script was left unread

        var own = Assert.Single(foot.Unbound,
            u => string.Equals(u.PropertyName, ScriptsWorld.ObjectProperty, StringComparison.Ordinal));
        Assert.True(own.IsObjectType);
        Assert.Equal(ScriptsWorld.ChildScript, own.DeclaringScript, ignoreCase: true);

        var inherited = Assert.Single(foot.Unbound,
            u => string.Equals(u.PropertyName, ScriptsWorld.InheritedProperty, StringComparison.Ordinal));
        Assert.Equal(ScriptsWorld.BaseScript, inherited.DeclaringScript, ignoreCase: true);
    }

    /// <summary>
    /// ONE wire-path smoke test: the world is reachable through the LIVE surface, driven off the built
    /// server over stdio — <c>housecarl_set_mo2_instance</c> at this instance, then
    /// <c>housecarl_check findings=["scripts"]</c>. This is what makes the fixture usable by the arms PR 2
    /// writes; it is not one of them.
    ///
    /// <para>Its own server, never the shared <see cref="ServerFixture"/>: that one is deliberately
    /// UNCONFIGURED — every stdio test in the run reads "the body ran" off its config prompt — and pointing
    /// it at an instance would silently retune all of them.</para>
    ///
    /// <para><b>Every string asserted here is anchored to something only this world can produce.</b> Three
    /// looser spellings were measured and refused: the bare words <c>not on disk</c> are in the scripts
    /// family's boundary sentence, which the renderer writes through the reserve on EVERY scripts response
    /// (<c>ReadSentences.SweepScriptBoundary</c>); the bare script name <c>HcSpNoPex</c> is also the
    /// record's own EditorID, printed by the record header regardless; and the record count survives a world
    /// whose .pex files never resolved, because <c>RecordsWithScripts</c> is incremented before any .pex is
    /// opened. All three stay green over a broken fixture. The reason line and the unbound finding below
    /// cannot.</para>
    /// </summary>
    [Fact]
    [Trait("tier", "stdio")]
    public void CheckOverTheWireReportsTheFixtureKnownScriptCountAndNamesTheUnverifiableScript()
    {
        using var server = new ServerFixture();

        // A bad instance comes back as an in-band "error: …" text result, not an MCP error, so IsError
        // cannot tell a configured server from a refused one. The confirmation render is what can.
        var set = server.Call(ToolNames.SetMo2Instance,
            $$"""{"path": {{JsonSerializer.Serialize(_w.Instance)}}}""");
        Assert.Contains($"configured houseCARL -> MO2 instance '{_w.Instance}'", set.Text, StringComparison.Ordinal);
        Assert.Contains("active profile: Default", set.Text, StringComparison.Ordinal);

        var r = server.Call(ToolNames.Check, """{"findings":["scripts"]}""");

        Assert.False(r.IsError, r.Describe());
        Assert.DoesNotContain(ServerFixture.ConfigPrompt, r.Text, StringComparison.Ordinal);
        Assert.Contains($"{ScriptsWorld.RecordsWithScripts} record(s) with scripts", r.Text, StringComparison.Ordinal);

        // The unverifiable attribution, spelled the way the sweep composes it — names the .pex it looked for.
        Assert.Contains($@"'Scripts\{ScriptsWorld.MissingScript}.pex' is not on disk", r.Text, StringComparison.Ordinal);

        // A finding only a RESOLVED child .pex can produce: the declaration has to have been read for the
        // property to be known unbound at all.
        Assert.Contains($"{ScriptsWorld.ObjectProperty} (Spell) on script {ScriptsWorld.ChildScript}",
                        r.Text, StringComparison.Ordinal);
        Assert.Contains("declared but NOT bound", r.Text, StringComparison.Ordinal);
    }
}

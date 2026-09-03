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
///
/// <para><b>ADR 0003 rule 2 — the scripts family is briefly in both harnesses.</b> These arms drive
/// <c>ValidateScripts</c> and <c>housecarl_check findings=["scripts"]</c>, so a product regression in
/// loose-layer resolution or ancestor walking turns them red — while <c>ScriptPropertyCheckProbe.cs</c>
/// still guards the same family. No <c>Converted-from:</c> marker is carried and none is owed: nothing
/// here converts a probe. The mechanical guard is decidable only on that marker, and the literal rule is
/// already documented RED at birth during the ruled sequence (<c>HarnessResidueTests.cs</c>, the one-way
/// conversion section). The overlap closes when #486's PR 2 deletes the probe, adds the marker and drops
/// its baseline key; it is stated on this PR for Aaron's gate rather than worked around here.</para>
/// </summary>
[Collection("scripts")]
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
    [Trait("tier", "integration")]
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
    [Trait("tier", "integration")]
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
    ///
    /// <para>The two finding SETS are pinned whole, not sampled. <c>Assert.Single(collection, predicate)</c>
    /// states that one element matches — it says nothing about the rest, so an EXTRA finding passes unseen,
    /// and the two fixture properties whose whole point is that they produce NO finding would be guarded by
    /// nothing: <c>MyDefaulted</c>, which rests on <see cref="PexWriter"/>'s baked-initializer branch (the
    /// product suppresses an unbound scalar exactly when the backing variable carries an initializer), and
    /// <c>MyAliasBound</c>, which rests on <see cref="ScriptsWorld"/> binding through a quest alias (Alias
    /// >= 0 with a null Object is BOUND, not bound-but-null). Both are absences, and only a set comparison
    /// can assert an absence.</para>
    /// </summary>
    [Fact]
    [Trait("tier", "integration")]
    public void BothPlantedPexFilesResolveThroughTheLooseLayer_SoTheDeclarationsAreRealNotUnverifiable()
    {
        var res = _w.Svc.ValidateScripts(null, 1000);
        var foot = Assert.Single(res.Reports, r => r.Record == _w.Footgun);

        Assert.Empty(foot.Unverifiable);   // nothing about this record's script was left unread

        // The unbound set EXACTLY: the two the footgun leaves unbound plus the ancestor's. MyDefaulted is
        // absent because its backing variable carries a baked initializer; MyBoundSpell, MyNullSpell and
        // MyAliasBound are absent because the VMAD binds them.
        Assert.Equal(
            new[] { ScriptsWorld.InheritedProperty, ScriptsWorld.ScalarProperty, ScriptsWorld.ObjectProperty }
                .OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            foot.Unbound.Select(u => u.PropertyName).OrderBy(x => x, StringComparer.Ordinal).ToArray());

        // The bound-but-null set EXACTLY: the null-form binding, and NOT the alias binding.
        Assert.Equal(
            new[] { ScriptsWorld.NullProperty },
            foot.NullObjects.Select(n => n.PropertyName).ToArray());

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

        // The WHOLE object-branch line, spelled from fixture-known values. The scalar branch emits the same
        // "declared but NOT bound" phrase, so a fragment of it is satisfied by MyChance; only this line is.
        Assert.Contains(
            $"{ScriptsWorld.ObjectProperty} (Spell) on script {ScriptsWorld.ChildScript}"
            + " — declared but NOT bound → None at runtime (HIGH: object/form type — the silent no-op)",
            r.Text, StringComparison.Ordinal);
    }
}

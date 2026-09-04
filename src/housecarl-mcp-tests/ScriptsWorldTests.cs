using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Pex;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The Papyrus fixture's own proofs: that <see cref="ScriptsWorld"/> is what it claims to be, so the tests
/// built on it can assert findings without also proving their fixture. What each rests on:
/// <c>docs/architecture/test-project-fixtures.md</c>.
/// </summary>
[Collection("scripts")]
public sealed class ScriptsWorldTests
{
    readonly ScriptsWorld _w;
    public ScriptsWorldTests(ScriptsFixture f) => _w = f.W;

    /// <summary>Everything else in the fixture rests on this: a .pex the product cannot read makes every
    /// declaration unverifiable.</summary>
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

    /// <summary>The writer bakes an Integer initializer whatever the declared type says, so a non-Int scalar
    /// with one is refused rather than written as a pairing no Papyrus compiler emits.</summary>
    [Fact]
    [Trait("tier", "integration")]
    public void TheWriterRefusesABakedInitializerOnANonIntScalar()
    {
        var ex = Assert.Throws<ArgumentException>(() => PexWriter.AutoScalar("MyFlag", "Bool", 1));

        Assert.Contains(
            "a baked initializer was given for 'MyFlag', declared 'Bool', with value 1: this writer only bakes "
            + "Integer initializers, because an Int scalar with a baked default is the only declared-type/"
            + "initializer pairing this fixture models. Writing it would pair VariableType.Integer with TypeName "
            + "'Bool' — a shape no Papyrus compiler emits.",
            ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The refusal's other branch: the pairing the fixture DOES model still writes, and still reads
    /// back as a baked Integer — which is what makes <c>MyDefaulted</c> mean anything.</summary>
    [Fact]
    [Trait("tier", "integration")]
    public void TheWriterStillBakesAnIntScalarInitializerAndItRoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hc-pexwriter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "HcPwProbe.pex");
            PexWriter.WritePex(path, "HcPwProbe", parent: null,
                PexWriter.AutoScalar(ScriptsWorld.ScalarProperty, "Int", ScriptsWorld.DefaultedValue));

            var obj = Assert.Single(PexFile.CreateFromFile(path, GameCategory.Skyrim).Objects);
            var backing = Assert.Single(obj.Variables,
                v => string.Equals(v.Name, $"::{ScriptsWorld.ScalarProperty}_var", StringComparison.OrdinalIgnoreCase));

            Assert.Equal("Int", backing.TypeName, ignoreCase: true);
            Assert.Equal(VariableType.Integer, backing.VariableData!.VariableType);
            Assert.Equal(ScriptsWorld.DefaultedValue, backing.VariableData.IntValue);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* temp cleanup best-effort */ } }
    }

    /// <summary>The script-bearing population is exactly the four VMAD-carrying records. The script-free
    /// weapon is the teeth: same plugin, must not be counted.</summary>
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

    /// <summary>The counts alone survive a world whose .pex files never resolved, so the declarations are
    /// observed instead. Both finding sets are pinned WHOLE — <c>MyDefaulted</c> and <c>MyAliasBound</c> exist
    /// to produce no finding, and only a set comparison asserts an absence.</summary>
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

    /// <summary>One wire-path smoke test: the fixture is reachable through the live surface. It gets its OWN
    /// server, because the shared <see cref="ServerFixture"/> is deliberately unconfigured and every stdio test
    /// reads "the body ran" off its config prompt.</summary>
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
        // A fixture-known count: cheap proof a scripts response came back at all. It cannot tell a resolved
        // fixture from a broken one; the two assertions below can.
        Assert.Contains($"{ScriptsWorld.RecordsWithScripts} record(s) with scripts", r.Text, StringComparison.Ordinal);

        // The unverifiable attribution, spelled the way the sweep composes it — names the .pex it looked for.
        Assert.Contains($@"'Scripts\{ScriptsWorld.MissingScript}.pex' is not on disk", r.Text, StringComparison.Ordinal);

        // The WHOLE object-branch line, spelled from fixture-known values, UNDER THE FOOTGUN'S OWN RECORD
        // HEADER. The line alone has two carriers in this world — the alias quest declares MySpell through the
        // same script and binds nothing, so it renders the identical line — and the header is the only thing
        // that says which record produced it. One composed span, so the two must be adjacent.
        Assert.Contains(
            $"[UNBOUND] {_w.Footgun} (Weapon '{ScriptsWorld.FootgunEditorId}') in {_w.PluginName}\n"
            + $"  ! {ScriptsWorld.ObjectProperty} (Spell) on script {ScriptsWorld.ChildScript}"
            + " — declared but NOT bound → None at runtime (HIGH: object/form type — the silent no-op)\n",
            r.Text, StringComparison.Ordinal);
    }
}

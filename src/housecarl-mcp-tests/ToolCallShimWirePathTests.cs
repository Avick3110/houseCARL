using System.Text.Json;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The binding shim's required-parameter refusals, and the refusal of 1.x parameter names on the write and
/// reshape tools.
///
/// <para>The population is derived from the wire — every published tool whose own schema carries a
/// <c>required</c> list — so the sweep is a function of the surface and no later tool can fall out of it.
/// (<c>housecarl_records</c> declares no required parameter, deliberately.)</para>
/// </summary>
[Collection("server")]
[Trait("tier", "stdio")]
public sealed class ToolCallShimWirePathTests
{
    readonly ServerFixture _s;
    public ToolCallShimWirePathTests(ServerFixture s) => _s = s;

    /// <summary>Every tool that declares required parameters, one theory row each. MemberData has to be static,
    /// so it cannot read the injected fixture; the population comes off the same generator the server publishes
    /// from, and the assertions read the required list off the LIVE fixture, so a disagreement between the two
    /// fails by name rather than quietly leaving the sweep short.</summary>
    public static IEnumerable<object[]> ToolsWithRequiredParameters()
    {
        foreach (var tool in PreFlattenSchemas.Read())
            if (tool.Schema["required"] is System.Text.Json.Nodes.JsonArray required && required.Count > 0)
                yield return new object[] { tool.Name };
    }

    /// <summary>The required list this run's server publishes for one tool.</summary>
    string[] RequiredOf(string tool) =>
        _s.PublishedTools[tool].GetProperty("inputSchema").GetProperty("required")
          .EnumerateArray().Select(e => e.GetString()!).ToArray();

    // ---- the required-parameter refusals ----------------------------------------------------------------

    /// <summary><c>{}</c> with a required parameter missing must be a named refusal naming EVERY missing
    /// parameter, never the SDK's generic "An error occurred invoking".</summary>
    [Theory]
    [MemberData(nameof(ToolsWithRequiredParameters))]
    public void EveryToolWithARequiredParameterRefusesAnEmptyCallNamingEveryMissingParameter(string tool)
    {
        var required = RequiredOf(tool);
        var r = _s.Call(tool, "{}");

        Assert.True(r.IsError, r.Describe());
        Assert.DoesNotContain(ServerFixture.GenericError, r.Text, StringComparison.Ordinal);
        Assert.False(r.BodyRan, r.Describe());

        // The whole refusal as a value: the tool, every missing name in schema order, and the fact that the
        // caller supplied nothing. A message that named only the first would pass a bare Contains sweep.
        var plural = required.Length > 1 ? "s" : "";
        Assert.Contains($"error: {tool}: required parameter{plural} missing: {string.Join(", ", required)}. " +
                        "Supplied: (none).", r.Text, StringComparison.Ordinal);
    }

    /// <summary>An EXPLICIT JSON null for a required parameter must be the same named missing-parameter
    /// refusal, saying the value was an explicit null rather than absent. Read as supplied, it binds null and
    /// the tool body NullReferences into Guard's "internal houseCARL failure" misdirection.</summary>
    [Theory]
    [MemberData(nameof(ToolsWithRequiredParameters))]
    public void AnExplicitNullForARequiredParameterIsRefusedAsMissingAndSaysItWasNull(string tool)
    {
        var required = RequiredOf(tool);
        var first = required[0];
        var r = _s.Call(tool, $$"""{"{{first}}":null}""");

        Assert.True(r.IsError, r.Describe());
        Assert.DoesNotContain(ServerFixture.GenericError, r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("internal houseCARL failure", r.Text, StringComparison.Ordinal);

        var plural = required.Length > 1 ? "s" : "";
        var rest = required.Length > 1 ? ", " + string.Join(", ", required.Skip(1)) : "";
        Assert.Contains($"error: {tool}: required parameter{plural} missing: {first} (was explicit null){rest}. " +
                        $"Supplied: {first}.", r.Text, StringComparison.Ordinal);
    }

    // ---- an optional complex list must not break the empty call -----------------------------------------

    /// <summary>The ops list is OFF the schema's required list — what to do when it is absent is judged in the
    /// tool BODY. Through the full binding/shim stack an empty <c>{}</c> must still bind cleanly: no required
    /// check fires, no binder throw on the absent complex array, and the body runs.</summary>
    [Fact]
    public void ApplyWithNoArgumentsBindsWithNoRequiredRefusalAndReachesTheBody()
    {
        var r = _s.Call(ToolNames.Apply, "{}");

        Assert.False(r.IsError, r.Describe());
        Assert.DoesNotContain("required parameter", r.Text, StringComparison.Ordinal);
        Assert.True(r.BodyRan, r.Describe());
    }

    // ---- 1.x parameter names are refused, never mapped --------------------------------------------------

    /// <summary><c>create_plugin</c> takes the 2.0 word <c>patch=</c> for the plugin it creates, so the old
    /// <c>plugin=</c> spelling is a named unknown rather than a silent rebind: no candidate of the plugin
    /// clique is declared there any more.</summary>
    [Fact]
    public void CreatePluginTakesPatchAndRefusesTheOldPluginSpellingByName()
    {
        var ok = _s.Call(ToolNames.CreatePlugin, """{"patch":"MyTrigger"}""");
        Assert.False(ok.IsError, ok.Describe());
        Assert.True(ok.BodyRan, ok.Describe());

        var old = _s.Call(ToolNames.CreatePlugin, """{"plugin":"MyTrigger"}""");
        Assert.True(old.IsError, old.Describe());
        Assert.Contains("patch", old.Text, StringComparison.Ordinal);
        Assert.False(old.BodyRan, old.Describe());
    }

    /// <summary><c>compact_plugin</c>'s subject is the SOURCE pole, so <c>source=</c> reaches the body and the
    /// old <c>plugin=</c> spelling is refused by name, naming <c>source</c>: no shim maps it any more.</summary>
    [Fact]
    public void CompactPluginTakesSourceAndRefusesTheOldPluginSpellingByName()
    {
        var direct = _s.Call(ToolNames.CompactPlugin, """{"source":"Skyrim.esm"}""");
        Assert.False(direct.IsError, direct.Describe());
        Assert.True(direct.BodyRan, direct.Describe());

        var old = _s.Call(ToolNames.CompactPlugin, """{"plugin":"Skyrim.esm"}""");
        Assert.True(old.IsError, old.Describe());
        Assert.False(old.BodyRan, old.Describe());
        Assert.Contains("required parameter missing: source. Supplied: plugin.", old.Text, StringComparison.Ordinal);
    }

    /// <summary>The tools whose <c>patch=</c> is the output mod FOLDER never take <c>plugin_name=</c> onto it,
    /// and it does not reach <c>compact_plugin</c>'s SOURCE pole either: on both tools the spelling is refused
    /// by name instead of silently naming the folder after a plugin.</summary>
    [Fact]
    public void PluginNameNeverBindsToTheOutputFolderPatchOnTheRiderTools()
    {
        var compact = _s.Call(ToolNames.CompactPlugin, """{"plugin_name":"Skyrim.esm"}""");
        Assert.True(compact.IsError, compact.Describe());
        Assert.False(compact.BodyRan, compact.Describe());
        Assert.Contains("required parameter missing: source. Supplied: plugin_name.", compact.Text, StringComparison.Ordinal);

        var compile = _s.Call(ToolNames.CompileScript, """{"script":"X.psc","plugin_name":"MyMod.esp"}""");
        Assert.True(compile.IsError, compile.Describe());
        Assert.Contains("unknown parameter: plugin_name", compile.Text, StringComparison.Ordinal);
        Assert.False(compile.BodyRan, compile.Describe());
    }

    /// <summary>A stray <c>target=</c> on a tool that has none is the named unknown WITH the supported list —
    /// never a rewrite onto <c>in_place</c>, which would engage the opt-in overwrite lane from a call that
    /// never spelled it.</summary>
    [Fact]
    public void AStrayTargetOnCompactPluginIsANamedUnknown_NotAnInPlaceTypeError()
    {
        var r = _s.Call(ToolNames.CompactPlugin, """{"source":"X.esp","target":"X.esp"}""");

        Assert.True(r.IsError, r.Describe());
        Assert.Contains("unknown parameter: target", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be bound", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("in_place (expects", r.Text, StringComparison.Ordinal);
    }

    /// <summary>1.x's <c>plugin_name=</c> is not mapped onto <c>compact_plugin</c>'s own <c>plugin=</c>: the one
    /// refusal names BOTH the required parameter it did not supply and the spelling it did — an old name standing
    /// in for a required parameter is exactly the caller who needs the accepted list, and the missing-parameter
    /// pass returning first must not swallow it.</summary>
    [Fact]
    public void TheOnePointXPluginNameSpellingIsRefusedByName_NotMappedOntoCompactPluginsOwnPlugin()
    {
        var r = _s.Call(ToolNames.CompactPlugin, """{"plugin_name":"Skyrim.esm"}""");

        Assert.True(r.IsError, r.Describe());
        Assert.False(r.BodyRan, r.Describe());
        Assert.Contains($"error: {ToolNames.CompactPlugin}: required parameter missing: plugin. " +
                        $"Supplied: plugin_name. plugin_name is not a parameter of {ToolNames.CompactPlugin} " +
                        "(it accepts only: ", r.Text, StringComparison.Ordinal);
        // The accepted list is the tool's own, not a fixed string: it must carry the parameter the caller meant.
        Assert.Contains("plugin,", r.Text, StringComparison.Ordinal);
    }

    // ---- a boolean, quoted or bare, never selects the in-place lane ---------------------------------------

    /// <summary>1.x's <c>in_place=false</c> meant the default new-patch lane. Quoted, it satisfies the string
    /// schema and the body's non-empty check, so without a gate the call enters the opt-in overwrite lane with a
    /// target named "false". Bare, it is the likelier arrival and must not fall through to the type-mismatch
    /// sentence, which says only "expects string" and steers the caller into the quoted spelling. Both spellings
    /// take the same refusal, saying what in_place takes. The parameter is the value as spelled, so the assertion
    /// pins that the refusal quotes a string and leaves a JSON boolean bare.</summary>
    [Theory]
    [InlineData("\"false\"")]
    [InlineData("\"true\"")]
    [InlineData("false")]
    [InlineData("true")]
    public void ABooleanInPlaceIsRefusedByNameAndNeverEntersTheOverwriteLane(string spelling)
    {
        var r = _s.Call(ToolNames.Apply, $$"""{"ops":[],"in_place":{{spelling}}}""");

        Assert.True(r.IsError, r.Describe());
        Assert.False(r.BodyRan, r.Describe());
        Assert.Contains($"error: {ToolNames.Apply}: in_place={spelling} names no file", r.Text, StringComparison.Ordinal);
        Assert.Contains("in_place=\"X.esp\"", r.Text, StringComparison.Ordinal);
        // The lane's own failures name the file it was given — proof the call never reached it.
        Assert.DoesNotContain("acknowledge", r.Text, StringComparison.Ordinal);
        // The type-mismatch pass must not be the one answering the bare spelling.
        Assert.DoesNotContain("expects string", r.Text, StringComparison.Ordinal);
    }

    /// <summary>A real filename is untouched by that gate: it reaches the tool body, which judges the lane.</summary>
    [Fact]
    public void AFilenameInPlaceIsNotRefusedByTheBooleanGate()
    {
        var r = _s.Call(ToolNames.Apply, """{"ops":[],"in_place":"NoSuchPlugin.esp"}""");

        Assert.DoesNotContain("names no file", r.Text, StringComparison.Ordinal);
        Assert.True(r.BodyRan, r.Describe());
    }
}

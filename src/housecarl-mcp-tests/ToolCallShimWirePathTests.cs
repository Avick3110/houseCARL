using System.Text.Json;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The binding shim's required-parameter refusals, and the alias clique on the write and reshape tools.
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

    // ---- the plugin/plugins/plugin_name clique ----------------------------------------------------------

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
        Assert.Contains("unknown parameter: plugin", old.Text, StringComparison.Ordinal);
    }

    /// <summary><c>compact_plugin</c>'s subject is the SOURCE pole, so <c>source=</c> reaches the body and the
    /// old bare <c>plugin=</c> still renames onto it through the clique's remaining edge.</summary>
    [Fact]
    public void CompactPluginTakesSourceAndTheOldPluginSpellingStillRenamesOntoIt()
    {
        var direct = _s.Call(ToolNames.CompactPlugin, """{"source":"Skyrim.esm"}""");
        Assert.False(direct.IsError, direct.Describe());
        Assert.True(direct.BodyRan, direct.Describe());

        var aliased = _s.Call(ToolNames.CompactPlugin, """{"plugin":"Skyrim.esm"}""");
        Assert.False(aliased.IsError, aliased.Describe());
        Assert.DoesNotContain("required parameter", aliased.Text, StringComparison.Ordinal);
        Assert.True(aliased.BodyRan, aliased.Describe());
    }

    /// <summary>A stray <c>target=</c> on a tool that has none stays the named unknown WITH the supported list
    /// — never a rename onto <c>in_place</c> that answers with a type error about a key the caller never
    /// sent.</summary>
    [Fact]
    public void AStrayTargetOnCompactPluginIsANamedUnknown_NotAnInPlaceTypeError()
    {
        var r = _s.Call(ToolNames.CompactPlugin, """{"plugin":"X.esp","target":"X.esp"}""");

        Assert.True(r.IsError, r.Describe());
        Assert.Contains("unknown parameter: target", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be bound", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("in_place (expects", r.Text, StringComparison.Ordinal);
    }

    /// <summary>The kind gate: a plural spelling carrying an ARRAY must not be renamed onto a SCALAR
    /// parameter, and the refusal keeps the caller's OWN key so the correction is about the argument they
    /// actually sent.
    ///
    /// <para>The scalar control is the other half of the gate: the SAME spelling carrying a STRING is
    /// compatible with the scalar pole, so it does rename and the call runs. Without it this test would pass
    /// against a gate that refused the spelling outright.</para>
    /// </summary>
    [Fact]
    public void AnArrayUnderAPluralSpellingIsNotRenamedOntoAScalarParameter_TheRefusalKeepsTheCallersKey()
    {
        var array = _s.Call(ToolNames.CompactPlugin, """{"plugins":["A.esp","B.esp"]}""");
        Assert.True(array.IsError, array.Describe());
        Assert.Contains("Supplied: plugins", array.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be bound", array.Text, StringComparison.Ordinal);

        var scalar = _s.Call(ToolNames.CompactPlugin, """{"plugins":"A.esp"}""");
        Assert.False(scalar.IsError, scalar.Describe());
        Assert.True(scalar.BodyRan, scalar.Describe());
    }
}

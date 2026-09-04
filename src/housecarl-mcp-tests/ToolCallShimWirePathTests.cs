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

    /// <summary>The plugin clique stays a full set of edges: <c>plugin=</c> binds on a tool whose declared
    /// parameter is <c>plugin_name</c>.</summary>
    [Fact]
    public void CreatePluginResolvesTheAliasPluginOntoItsOwnPluginNameAndReachesTheBody()
    {
        var r = _s.Call(ToolNames.CreatePlugin, """{"plugin":"MyTrigger"}""");

        Assert.False(r.IsError, r.Describe());
        Assert.DoesNotContain("unknown parameter", r.Text, StringComparison.Ordinal);
        Assert.True(r.BodyRan, r.Describe());
    }

    /// <summary>The reverse edge of the same clique: <c>plugin_name=</c> binds on a tool whose declared
    /// parameter is bare <c>plugin</c>. On <c>compact_plugin</c> the rename also satisfies the required check,
    /// so reaching the body is itself evidence the rename happened.</summary>
    [Fact]
    public void CompactPluginResolvesTheAliasPluginNameOntoItsOwnPluginAndReachesTheBody()
    {
        var r = _s.Call(ToolNames.CompactPlugin, """{"plugin_name":"Skyrim.esm"}""");

        Assert.False(r.IsError, r.Describe());
        Assert.DoesNotContain("unknown parameter", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("required parameter", r.Text, StringComparison.Ordinal);
        Assert.True(r.BodyRan, r.Describe());
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

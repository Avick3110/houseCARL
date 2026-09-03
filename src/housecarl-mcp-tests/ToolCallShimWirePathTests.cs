// Converted-from: BindingShimProbe
using System.Text.Json;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The binding shim's arms whose 2.0 subject is NOT <c>housecarl_records</c> — the required-parameter
/// refusals, and the alias clique on the write and reshape tools.
///
/// <para>The required arms were driven on <c>housecarl_batch_record_detail</c>, which this cut deletes.
/// <c>housecarl_records</c> cannot host them: it declares no required parameter, deliberately. So rather
/// than picking a replacement tool by hand, the population is DERIVED from the wire — every published tool
/// whose own schema carries a <c>required</c> list — which makes the sweep a function of the surface and
/// leaves nothing for a later tool to fall out of.</para>
/// </summary>
[Collection("server")]
[Trait("tier", "stdio")]
public sealed class ToolCallShimWirePathTests
{
    readonly ServerFixture _s;
    public ToolCallShimWirePathTests(ServerFixture s) => _s = s;

    /// <summary>
    /// Every tool that declares required parameters, one theory row each. MemberData has to be static, so it
    /// cannot read the injected fixture; the population comes off the same generator the server publishes
    /// from, and the assertions then read the required list off the LIVE fixture — so a tool whose two
    /// spellings ever disagreed would fail on the name it prints rather than quietly leaving the sweep short.
    /// </summary>
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

    // ---- D and H: the required-parameter refusals -------------------------------------------------------

    /// <summary>
    /// The audit's other failing call: <c>{}</c> with a required parameter missing. It must be a NAMED
    /// refusal naming EVERY missing parameter, never the SDK's generic "An error occurred invoking".
    /// </summary>
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

    /// <summary>
    /// The 2026-06-12 hunt, proven over stdio on <c>nexus_mod</c>: an EXPLICIT JSON null for a required
    /// parameter read as supplied, the SDK bound null, and the tool body NullReferenced into Guard's
    /// "internal houseCARL failure… capture a bug report" misdirection. It must be the same named
    /// missing-parameter refusal, and it must say the value was an explicit null rather than absent.
    /// </summary>
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

    // ---- D2 (#224): an optional complex list must not break the empty call ------------------------------

    /// <summary>
    /// #224: the ops list is OFF the schema's required list — what to do when it is absent is judged in the
    /// tool BODY. Through the full binding/shim stack an empty <c>{}</c> must still bind cleanly: no required
    /// check fires, no binder throw on the absent complex array, and the body runs. RED if optionalizing the
    /// parameter ever breaks <c>{}</c> binding (PR #241 review NOTE 4).
    /// </summary>
    [Fact]
    public void ApplyWithNoArgumentsBindsWithNoRequiredRefusalAndReachesTheBody()
    {
        var r = _s.Call(ToolNames.Apply, "{}");

        Assert.False(r.IsError, r.Describe());
        Assert.DoesNotContain("required parameter", r.Text, StringComparison.Ordinal);
        Assert.True(r.BodyRan, r.Describe());
    }

    // ---- J6-J9: the plugin/plugins/plugin_name clique on the surviving tools ----------------------------

    /// <summary>
    /// PR #304 review F1: the 1.x plugin clique stays a full set of edges. <c>plugin=</c> binds on a tool
    /// whose declared parameter is <c>plugin_name</c>.
    /// </summary>
    [Fact]
    public void CreatePluginResolvesTheAliasPluginOntoItsOwnPluginNameAndReachesTheBody()
    {
        var r = _s.Call(ToolNames.CreatePlugin, """{"plugin":"MyTrigger"}""");

        Assert.False(r.IsError, r.Describe());
        Assert.DoesNotContain("unknown parameter", r.Text, StringComparison.Ordinal);
        Assert.True(r.BodyRan, r.Describe());
    }

    /// <summary>
    /// The reverse edge of the same clique: <c>plugin_name=</c> binds on a tool whose declared parameter is
    /// bare <c>plugin</c>. Re-pointed from <c>housecarl_read_plugin_file</c> — the surviving tool declaring a
    /// bare <c>plugin=</c> is <c>housecarl_compact_plugin</c>, where the rename also satisfies the required
    /// check, so reaching the body is itself evidence the rename happened.
    /// </summary>
    [Fact]
    public void CompactPluginResolvesTheAliasPluginNameOntoItsOwnPluginAndReachesTheBody()
    {
        var r = _s.Call(ToolNames.CompactPlugin, """{"plugin_name":"Skyrim.esm"}""");

        Assert.False(r.IsError, r.Describe());
        Assert.DoesNotContain("unknown parameter", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("required parameter", r.Text, StringComparison.Ordinal);
        Assert.True(r.BodyRan, r.Describe());
    }

    /// <summary>
    /// PR #304 review F3: a stray <c>target=</c> on a tool that has none stays the named unknown WITH the
    /// supported list — never a rename onto <c>in_place</c> that answers with a type error about a key the
    /// caller never sent.
    /// </summary>
    [Fact]
    public void AStrayTargetOnCompactPluginIsANamedUnknown_NotAnInPlaceTypeError()
    {
        var r = _s.Call(ToolNames.CompactPlugin, """{"plugin":"X.esp","target":"X.esp"}""");

        Assert.True(r.IsError, r.Describe());
        Assert.Contains("unknown parameter: target", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be bound", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("in_place (expects", r.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// PR #304 review F5, the kind gate: a plural spelling carrying an ARRAY must not be renamed onto a
    /// SCALAR parameter. The refusal keeps the caller's OWN key, so the correction they read is about the
    /// argument they actually sent. Re-pointed from <c>read_record(formids=[…])</c> onto the surviving
    /// plural-array/scalar pair, <c>compact_plugin(plugins=[…])</c> over its declared <c>plugin=</c>.
    ///
    /// <para>The scalar control is the other half of the gate: the SAME spelling carrying a STRING is
    /// compatible with the scalar pole, so it does rename and the call runs. Without it this arm would pass
    /// just as well against a gate that refused the spelling outright.</para>
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

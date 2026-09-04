using System.Text.Json;
using HousecarlMcp;
using ModelContextProtocol.Protocol;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The tool-argument binding shim, driven over stdio against <c>housecarl_records</c>. The shim is
/// schema-driven off each tool's published <c>InputSchema</c> with no per-tool wiring, so the claims are
/// about the engine and this tool is only the carrier.
///
/// <para>An unconfigured server answers every call that BINDS with the same config prompt, whatever the
/// argument said, so a coercion that bound fine and threw the caller's value away is invisible over the wire
/// — a bare string wrapped into an empty array would pass. The coercion tests below therefore assert the
/// produced value too, by running <see cref="ToolCallShim.Coerce"/> against the schema the server published
/// for that parameter. <see cref="ToolCallShimCoercionTests"/> asserts the same values against hand-written
/// schemas; the real schema here is what says the coercion fires on <c>housecarl_records</c> at all.</para>
/// </summary>
[Collection("server")]
[Trait("tier", "stdio")]
public sealed class RecordsBindingShimTests
{
    readonly ServerFixture _s;
    public RecordsBindingShimTests(ServerFixture s) => _s = s;

    /// <summary>The schema the server published for one <c>housecarl_records</c> parameter.</summary>
    JsonElement Published(string parameter) =>
        _s.PublishedTools[ToolNames.Records].GetProperty("inputSchema").GetProperty("properties")
          .GetProperty(parameter);

    /// <summary>Every parameter name the published schema declares, in publication order.</summary>
    string[] PublishedParameters() =>
        _s.PublishedTools[ToolNames.Records].GetProperty("inputSchema").GetProperty("properties")
          .EnumerateObject().Select(p => p.Name).ToArray();

    static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    /// <summary>The argument dictionary the shim's alias pass leaves behind, for the tool's real schema.</summary>
    IDictionary<string, JsonElement> AfterAliasResolution(string argumentsJson)
    {
        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in Json(argumentsJson).EnumerateObject()) args[p.Name] = p.Value.Clone();

        var request = new CallToolRequestParams { Name = ToolNames.Records, Arguments = args };
        ToolCallShim.ResolveAliases(request, _s.PublishedTools[ToolNames.Records].GetProperty("inputSchema"));
        return request.Arguments!;
    }

    // ---- the published schema is not degraded -----------------------------------------------------------

    /// <summary>The shape problem must never be "fixed" with serializer-options converters: that collapses
    /// the generated schema and removes the model's shape hint.</summary>
    [Fact]
    public void ThePublishedFormidsParameterStillDeclaresAnArray_NotCollapsedByASerializerConverter()
    {
        var formids = Published("formids");

        Assert.Contains("array", formids.GetProperty("type").GetRawText(), StringComparison.Ordinal);
        Assert.Contains("string", formids.GetProperty("items").GetProperty("type").GetRawText(),
                        StringComparison.Ordinal);
    }

    // ---- the coercions ----------------------------------------------------------------------------------

    /// <summary>A list parameter sent as a bare string.</summary>
    [Fact]
    public void FormidsAsABareStringBecomesAOneElementArrayAndTheCallReachesTheBody()
    {
        var coerced = ToolCallShim.Coerce(Json("\"0F1AC1:Skyrim.esm\""), Published("formids"));

        Assert.NotNull(coerced);
        Assert.Equal(JsonValueKind.Array, coerced!.Value.ValueKind);
        Assert.Equal(1, coerced.Value.GetArrayLength());
        Assert.Equal("0F1AC1:Skyrim.esm", coerced.Value[0].GetString());

        var r = _s.Call(ToolNames.Records, """{"formids":"0F1AC1:Skyrim.esm"}""");
        Assert.False(r.IsError, r.Describe());
        Assert.True(r.BodyRan, r.Describe());
    }

    /// <summary>The whole array serialized as a JSON string. It must parse as the array it spells, never a
    /// one-element array holding the unparsed text, which binds and then fails later.</summary>
    [Fact]
    public void FormidsAsAStringEncodedJsonArrayBecomesThatArrayAndTheCallReachesTheBody()
    {
        var coerced = ToolCallShim.Coerce(Json("\"[\\\"0F1AC1:Skyrim.esm\\\",\\\"0F1AC2:Skyrim.esm\\\"]\""),
                                          Published("formids"));

        Assert.NotNull(coerced);
        Assert.Equal(new[] { "0F1AC1:Skyrim.esm", "0F1AC2:Skyrim.esm" },
                     coerced!.Value.EnumerateArray().Select(e => e.GetString()).ToArray());

        var r = _s.Call(ToolNames.Records, """{"formids":"[\"0F1AC1:Skyrim.esm\",\"0F1AC2:Skyrim.esm\"]"}""");
        Assert.False(r.IsError, r.Describe());
        Assert.True(r.BodyRan, r.Describe());
    }

    /// <summary>
    /// A bare string that merely STARTS with '[' but is not JSON keeps the one-element wrap — the
    /// fall-through — rather than being rejected by a failed parse.
    /// </summary>
    [Fact]
    public void FormidsAsANonJsonBracketLeadingStringIsWrappedWholeAndTheCallReachesTheBody()
    {
        var coerced = ToolCallShim.Coerce(Json("\"[Bracketed Name.esp\""), Published("formids"));

        Assert.NotNull(coerced);
        Assert.Equal(1, coerced!.Value.GetArrayLength());
        Assert.Equal("[Bracketed Name.esp", coerced.Value[0].GetString());

        var r = _s.Call(ToolNames.Records, """{"formids":"[Bracketed Name.esp"}""");
        Assert.False(r.IsError, r.Describe());
        Assert.True(r.BodyRan, r.Describe());
    }

    /// <summary>Control: the documented array shape is not rewritten, and behaves as it always did.</summary>
    [Fact]
    public void FormidsAsARealArrayIsLeftAloneAndTheCallReachesTheBody()
    {
        Assert.Null(ToolCallShim.Coerce(Json("""["0F1AC1:Skyrim.esm"]"""), Published("formids")));

        var r = _s.Call(ToolNames.Records, """{"formids":["0F1AC1:Skyrim.esm"]}""");
        Assert.False(r.IsError, r.Describe());
        Assert.True(r.BodyRan, r.Describe());
    }

    [Fact]
    public void AQuotedNumberForLimitBecomesThatNumberAndTheCallReachesTheBody()
    {
        var coerced = ToolCallShim.Coerce(Json("\"100\""), Published("limit"));

        Assert.NotNull(coerced);
        Assert.Equal(JsonValueKind.Number, coerced!.Value.ValueKind);
        Assert.Equal(100, coerced.Value.GetInt32());

        var r = _s.Call(ToolNames.Records, """{"limit":"100"}""");
        Assert.False(r.IsError, r.Describe());
        Assert.True(r.BodyRan, r.Describe());
    }

    [Fact]
    public void AQuotedBooleanForConflictsOnlyBecomesThatBooleanAndTheCallReachesTheBody()
    {
        var coerced = ToolCallShim.Coerce(Json("\"true\""), Published("conflicts_only"));

        Assert.NotNull(coerced);
        Assert.Equal(JsonValueKind.True, coerced!.Value.ValueKind);

        var r = _s.Call(ToolNames.Records, """{"conflicts_only":"true"}""");
        Assert.False(r.IsError, r.Describe());
        Assert.True(r.BodyRan, r.Describe());
    }

    // ---- the refusals that name the offending parameter -------------------------------------------------

    /// <summary>An uncoercible wrong-type shape still fails, naming the offending parameter and the kind
    /// received, caught before binding rather than as a byte-offset error over the whole argument list.</summary>
    [Fact]
    public void AnObjectWhereAnIntegerIsDeclaredIsRefusedNamingTheParameterAndTheKindReceived()
    {
        var r = _s.Call(ToolNames.Records, """{"limit":{"oops":1}}""");

        Assert.True(r.IsError, r.Describe());
        Assert.DoesNotContain(ServerFixture.GenericError, r.Text, StringComparison.Ordinal);
        Assert.Contains("limit (expects integer, received object)", r.Text, StringComparison.Ordinal);
    }

    /// <summary>A bad value for a declared boolean must name the parameter and the type it expects, before
    /// binding, and the body must never run (no config prompt).</summary>
    [Fact]
    public void AWrongTypeStringForABooleanIsRefusedNamingTheParameterAndTheTypeItExpects()
    {
        var r = _s.Call(ToolNames.Records, """{"conflicts_only":"CELL"}""");

        Assert.True(r.IsError, r.Describe());
        Assert.False(r.BodyRan, r.Describe());
        Assert.Contains("conflicts_only (expects boolean, received string)", r.Text, StringComparison.Ordinal);
    }

    // ---- the unknown-parameter refusal ------------------------------------------------------------------

    /// <summary>The SDK binder silently ignores an undeclared argument, dropping the caller's intent with no
    /// correction. The refusal names the offender and lists what the tool accepts, asserted as set equality
    /// against the published property list so a list that goes short fails here.</summary>
    [Fact]
    public void AnUndeclaredParameterIsRefusedByNameAndTheRefusalListsWhatTheToolAccepts()
    {
        var r = _s.Call(ToolNames.Records, """{"formids":["0F1AC1:Skyrim.esm"],"expand":true}""");

        Assert.True(r.IsError, r.Describe());
        Assert.False(r.BodyRan, r.Describe());
        Assert.Contains("unknown parameter: expand", r.Text, StringComparison.Ordinal);

        var accepts = r.Text.Split("This tool accepts only: ", StringSplitOptions.None)[^1].Split('.')[0];
        Assert.Equal(PublishedParameters(),
                     accepts.Split(',').Select(s => s.Trim()).ToArray());
    }

    /// <summary>Control: the same call WITHOUT the stray parameter binds and reaches the body, so the
    /// unknown-parameter check never rejects a well-formed call.</summary>
    [Fact]
    public void TheSameCallWithoutTheStrayParameterBindsAndReachesTheBody()
    {
        var r = _s.Call(ToolNames.Records, """{"formids":["0F1AC1:Skyrim.esm"]}""");

        Assert.False(r.IsError, r.Describe());
        Assert.True(r.BodyRan, r.Describe());
    }

    // ---- the alias layer --------------------------------------------------------------------------------

    /// <summary>A spelling the tool does not declare is renamed to the canonical parameter its own row names.
    /// The first declared candidate for <c>plugin</c> here is <c>source</c>, the whose-version pole, so the
    /// pole is asserted as a value rather than merely that something bound.</summary>
    [Fact]
    public void TheAliasPluginIsResolvedToRecordsOwnSourceAndTheCallReachesTheBody()
    {
        var args = AfterAliasResolution("""{"types":["CELL"],"plugin":"Synthetic.esp"}""");

        Assert.False(args.ContainsKey("plugin"));
        Assert.Equal("Synthetic.esp", args["source"].GetString());

        var r = _s.Call(ToolNames.Records, """{"types":["CELL"],"plugin":"Synthetic.esp"}""");
        Assert.False(r.IsError, r.Describe());
        Assert.True(r.BodyRan, r.Describe());
    }

    /// <summary>The underscore/case bridge, resolved by normalization alone. It is the bridge and not a table
    /// row that is proved, by asserting the table has no row for this spelling.</summary>
    [Fact]
    public void AnUnderscoreVariantIsResolvedToItsDeclaredParameterByNormalizationAlone()
    {
        Assert.Null(AliasTable.RenameFor(ToolCallShim.Normalize("wheresource")));

        var args = AfterAliasResolution("""{"types":["CELL"],"wheresource":"winner"}""");

        Assert.False(args.ContainsKey("wheresource"));
        Assert.Equal("winner", args["where_source"].GetString());

        var r = _s.Call(ToolNames.Records, """{"types":["CELL"],"wheresource":"winner"}""");
        Assert.False(r.IsError, r.Describe());
        Assert.True(r.BodyRan, r.Describe());
    }

    /// <summary>A spelling resolved via the synonym group rather than by normalization equality:
    /// <c>plugin_name</c> normalizes to "pluginname", which no declared parameter matches, so only the
    /// table's row can place it. The pole it lands on is the one the value's kind can bind — a filename
    /// string is the whose-version pole, while the scope pole takes an object.</summary>
    [Fact]
    public void TheAliasPluginNameIsResolvedThroughTheSynonymGroupNotByNormalizationEquality()
    {
        Assert.DoesNotContain(ToolCallShim.Normalize("plugin_name"),
                              PublishedParameters().Select(ToolCallShim.Normalize));

        var args = AfterAliasResolution("""{"types":["CELL"],"plugin_name":"Synthetic.esp"}""");

        Assert.False(args.ContainsKey("plugin_name"));
        Assert.Equal("Synthetic.esp", args["source"].GetString());

        var r = _s.Call(ToolNames.Records, """{"types":["CELL"],"plugin_name":"Synthetic.esp"}""");
        Assert.False(r.IsError, r.Describe());
        Assert.True(r.BodyRan, r.Describe());
    }

    /// <summary>A declared parameter is never treated as an alias. <c>plugins</c> is a rename row's old
    /// spelling on other tools and <c>housecarl_records</c> declares it, so the row has something here to be
    /// kept away from. Asserted as a value because the wire cannot see it: <c>records.source</c> is declared
    /// <c>JsonElement</c> and takes any JSON, so a rename onto it would still reach the body and answer with
    /// the same config prompt as the correct call.</summary>
    [Fact]
    public void RecordsOwnDeclaredPluginsIsNeverTreatedAsAnAlias()
    {
        Assert.NotNull(AliasTable.RenameFor(ToolCallShim.Normalize("plugins")));   // the row exists to fire

        var args = AfterAliasResolution("""{"types":["CELL"],"plugins":{"names":["Skyrim.esm"]}}""");

        Assert.Equal(new[] { "types", "plugins" }, args.Keys.ToArray());
        Assert.Equal(new[] { "Skyrim.esm" },
                     args["plugins"].GetProperty("names").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    /// <summary>An explicit canonical value is never clobbered: with <c>source</c> supplied the stray
    /// <c>plugin</c> has no free target, so it is left for the unknown-parameter path and named rather than
    /// silently merged over the caller's own value.</summary>
    [Fact]
    public void AnAliasWhoseCanonicalIsAlreadySuppliedIsNamedUnknown_NotMergedOverIt()
    {
        var args = AfterAliasResolution("""{"types":["CELL"],"source":"winner","plugin":"Other.esp"}""");
        Assert.Equal("winner", args["source"].GetString());     // the caller's own value survives
        Assert.True(args.ContainsKey("plugin"));                // the stray is left where it was

        var r = _s.Call(ToolNames.Records, """{"types":["CELL"],"source":"winner","plugin":"Other.esp"}""");
        Assert.True(r.IsError, r.Describe());
        Assert.Contains("unknown parameter: plugin", r.Text, StringComparison.Ordinal);
    }

    /// <summary>The kind gate must not reach the normalization bridge: a case variant names the right
    /// parameter, so the rename proceeds even for an unbindable value and the type refusal names the real
    /// parameter and fault, never an unknown-parameter refusal denying it exists.</summary>
    [Fact]
    public void ACaseVariantKeyCarryingAnUnbindableValueGetsTheTypeRefusal_NotUnknownParameter()
    {
        var r = _s.Call(ToolNames.Records, """{"types":["CELL"],"conflicts_Only":"CELL"}""");

        Assert.True(r.IsError, r.Describe());
        Assert.DoesNotContain("unknown parameter", r.Text, StringComparison.Ordinal);
        Assert.Contains("conflicts_only (expects boolean, received string)", r.Text, StringComparison.Ordinal);
    }

    // ---- the structured parameters over the SDK's JSON->POCO path ---------------------------------------

    /// <summary>The one seam a direct C# call on <c>RecordsTools.Records</c> cannot cover: the SDK's
    /// JSON→POCO deserialization of the published nested schema. The form-scoped <c>project=</c> object and
    /// the polymorphic <c>source=</c> (here a bare string) must bind and reach the tool body.</summary>
    [Fact]
    public void TheNestedProjectObjectAndAStringSourceBindOverTheWire()
    {
        var r = _s.Call(ToolNames.Records,
            """{"formids":["0F1AC1:Skyrim.esm"],"source":"winner","project":{"form":"identity"}}""");

        Assert.False(r.IsError, r.Describe());
        Assert.Equal(1, r.ContentBlocks);
        Assert.Equal("text", r.FirstBlockType);
        Assert.True(r.BodyRan, r.Describe());
    }

    [Fact]
    public void ThePluginsScopeObjectBindsOverTheWire()
    {
        var r = _s.Call(ToolNames.Records,
            """{"types":["WEAP"],"plugins":{"names":["Skyrim.esm"],"defined_in":true}}""");

        Assert.False(r.IsError, r.Describe());
        Assert.Equal("text", r.FirstBlockType);
        Assert.True(r.BodyRan, r.Describe());
    }
}

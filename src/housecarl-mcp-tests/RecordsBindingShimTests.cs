// Converted-from: BindingShimProbe
using System.Text.Json;
using HousecarlMcp;
using ModelContextProtocol.Protocol;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The tool-argument binding shim, driven over stdio against <c>housecarl_records</c> —
/// HCBR-2026-06-11-01's arms, re-pointed off the 1.x read tools onto the 2.0 tool that absorbed them.
///
/// <para>The original guard drove <c>housecarl_cross_plugin_query</c>, <c>housecarl_read_record</c>,
/// <c>housecarl_read_plugin_file</c> and <c>housecarl_batch_record_detail</c>: 20 of its 25 literal wire
/// calls were on tools this cut deletes. The shim is schema-driven off each tool's own published
/// <c>InputSchema</c> with no per-tool wiring, so the claims are about the ENGINE and the tool was only the
/// carrier — but the carrier the 2.0 read surface actually presents is this one, so this is where they are
/// re-driven.</para>
///
/// <para><b>What the wire can and cannot prove.</b> The unconfigured server answers every call that BINDS
/// with the same trained config prompt, whatever the argument said. So a coercion that bound fine and threw
/// the caller's value away is invisible to a wire arm — a bare string wrapped into an EMPTY array would pass
/// one. That is the silent-wrong-answer class, so the coercion arms below assert the PRODUCED VALUE too, by
/// running <see cref="ToolCallShim.Coerce"/> against the schema the server actually published for that
/// parameter. <see cref="ToolCallShimCoercionTests"/> asserts the same values against representative
/// hand-written schemas; the schema here is the real one, which is the half that says the coercion fires on
/// <c>housecarl_records</c> at all.</para>
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

    // ---- A: the fix did not degrade the published schema ------------------------------------------------

    /// <summary>
    /// The guard against ever "fixing" the shape problem with serializer-options converters, which would
    /// collapse the generated schema and remove the model's shape hint. (Probe arm A, read off
    /// <c>cross_plugin_query</c>'s <c>plugins=</c>; the 2.0 list parameter carrying the same claim is
    /// <c>formids=</c>.)
    /// </summary>
    [Fact]
    public void ThePublishedFormidsParameterStillDeclaresAnArray_NotCollapsedByASerializerConverter()
    {
        var formids = Published("formids");

        Assert.Contains("array", formids.GetProperty("type").GetRawText(), StringComparison.Ordinal);
        Assert.Contains("string", formids.GetProperty("items").GetProperty("type").GetRawText(),
                        StringComparison.Ordinal);
    }

    // ---- B/B2/B3/C: the coercion arms -------------------------------------------------------------------

    /// <summary>THE live failing shape (HCBR-2026-06-11-01): a list parameter sent as a bare string.</summary>
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

    /// <summary>
    /// The verified live Claude Code shape (#36): the whole array serialized as a JSON STRING. It must parse
    /// as the array it spells — never a one-element array holding the unparsed text, which binds and then
    /// fails later, misleadingly.
    /// </summary>
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

    // ---- G/G2: the refusals that name the offending parameter -------------------------------------------

    /// <summary>
    /// An UNCOERCIBLE wrong-type shape still fails — but named, naming the OFFENDING PARAMETER and the kind
    /// received (#222), caught before binding rather than as a byte-offset error over the whole argument list.
    /// </summary>
    [Fact]
    public void AnObjectWhereAnIntegerIsDeclaredIsRefusedNamingTheParameterAndTheKindReceived()
    {
        var r = _s.Call(ToolNames.Records, """{"limit":{"oops":1}}""");

        Assert.True(r.IsError, r.Describe());
        Assert.DoesNotContain(ServerFixture.GenericError, r.Text, StringComparison.Ordinal);
        Assert.Contains("limit (expects integer, received object)", r.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// #222's exact class: a bad value for a declared BOOLEAN used to throw "could not be converted to
    /// System.Boolean" with a byte offset and NO parameter name. It must name the parameter AND the type it
    /// expects, before binding — and the body must never run (no config prompt).
    /// </summary>
    [Fact]
    public void AWrongTypeStringForABooleanIsRefusedNamingTheParameterAndTheTypeItExpects()
    {
        var r = _s.Call(ToolNames.Records, """{"conflicts_only":"CELL"}""");

        Assert.True(r.IsError, r.Describe());
        Assert.False(r.BodyRan, r.Describe());
        Assert.Contains("conflicts_only (expects boolean, received string)", r.Text, StringComparison.Ordinal);
    }

    // ---- I/I2: the unknown-parameter refusal ------------------------------------------------------------

    /// <summary>
    /// HCBR-2026-07-12: the SDK binder SILENTLY IGNORES an undeclared argument, so the call ran with the
    /// caller's intent dropped and no correction reached them. The refusal names the offender and lists what
    /// the tool accepts — asserted as SET EQUALITY against the published property list, so a list that goes
    /// short (the probe sampled one name) fails here.
    /// </summary>
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

    // ---- J1-J5, J10: the alias layer (#221, #304) -------------------------------------------------------

    /// <summary>
    /// #221: a 1.x spelling the tool does not declare is renamed to the canonical parameter its own row
    /// names. On <c>housecarl_records</c> the first declared candidate for <c>plugin</c> is <c>source</c>
    /// — the whose-version pole — so this asserts the pole as a value, not merely that something bound.
    /// </summary>
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

    /// <summary>
    /// #221: the permanent underscore/case bridge, resolved by normalization alone. The arm proves the
    /// bridge rather than a table row by asserting the table has NO row for this spelling — the probe's
    /// <c>form_id</c> → <c>formid</c> pair cannot say that on this tool, because <c>formid</c> IS a row here.
    /// </summary>
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

    /// <summary>
    /// #221: a spelling resolved via the SYNONYM GROUP rather than by normalization equality —
    /// <c>plugin_name</c> normalizes to "pluginname", which no declared parameter of this tool matches, so
    /// the bridge cannot place it and only the table's row can. The pole it lands on is the one the value's
    /// kind can bind: a filename string is the whose-version pole, and the scope pole takes an object.
    /// </summary>
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

    /// <summary>
    /// #221 GUARD — a DECLARED parameter is never treated as an alias. <c>plugins</c> is a rename row's old
    /// spelling on other tools (it maps onto a scalar <c>plugin=</c> there), and <c>housecarl_records</c>
    /// declares it, so the row has something here to be kept away from.
    ///
    /// <para>Asserted as a value, because the wire cannot see this one: <c>records.source</c> is declared
    /// <c>JsonElement</c> and takes any JSON, so a rename onto it would still reach the tool body and answer
    /// with the same config prompt as the correct call.</para>
    /// </summary>
    [Fact]
    public void RecordsOwnDeclaredPluginsIsNeverTreatedAsAnAlias()
    {
        Assert.NotNull(AliasTable.RenameFor(ToolCallShim.Normalize("plugins")));   // the row exists to fire

        var args = AfterAliasResolution("""{"types":["CELL"],"plugins":{"names":["Skyrim.esm"]}}""");

        Assert.Equal(new[] { "types", "plugins" }, args.Keys.ToArray());
        Assert.Equal(new[] { "Skyrim.esm" },
                     args["plugins"].GetProperty("names").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    /// <summary>
    /// #221 GUARD — an explicit canonical value is never clobbered. With <c>source</c> supplied, the stray
    /// <c>plugin</c> has no free target, so it is left for the unknown-parameter path and NAMED, rather than
    /// silently merged over the caller's own value.
    /// </summary>
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

    /// <summary>
    /// The kind gate must not reach the NORMALIZATION bridge: a case variant names the RIGHT parameter, so
    /// the rename proceeds even for an unbindable value and the TYPE refusal names the real parameter and
    /// fault — never an unknown-parameter refusal denying that the parameter exists.
    /// </summary>
    [Fact]
    public void ACaseVariantKeyCarryingAnUnbindableValueGetsTheTypeRefusal_NotUnknownParameter()
    {
        var r = _s.Call(ToolNames.Records, """{"types":["CELL"],"conflicts_Only":"CELL"}""");

        Assert.True(r.IsError, r.Describe());
        Assert.DoesNotContain("unknown parameter", r.Text, StringComparison.Ordinal);
        Assert.Contains("conflicts_only (expects boolean, received string)", r.Text, StringComparison.Ordinal);
    }

    // ---- J11: the structured parameters over the SDK's JSON->POCO path ----------------------------------

    /// <summary>
    /// The one seam a direct C# call on <c>RecordsTools.Records</c> cannot cover: the SDK's JSON→POCO
    /// deserialization of the published nested schema. The form-scoped <c>project=</c> object and the
    /// polymorphic <c>source=</c> (here a bare string) must bind and reach the tool body.
    /// </summary>
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

using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// <c>housecarl_check</c> driven over stdio — the path #470 left unproven.
///
/// Every existing check guard calls <c>CheckTools.CheckTool</c> as a C# method, which is why none of them
/// noticed the tool was unpublished, and why none of them has ever exercised its published schema, its
/// argument binding, or the ToolCallShim. These are fresh tests against the JSON-RPC response.
///
/// <para><b>What an unconfigured server can and cannot prove.</b> The config check runs BEFORE the
/// <c>findings=</c> vocabulary is parsed, so every value that binds — a legal family, a class, or a
/// nonsense token — comes back with the same config prompt. So these arms prove exactly two things: the
/// argument BINDS, and the tool BODY was entered. They do not and cannot prove the vocabulary; that is
/// guarded where it lives, over <c>SweepFamilySelection.TryParse</c>. The arms that CAN fail here are the
/// negative ones below, and they are measured RED.</para>
///
/// <para>The subject set is derived from <see cref="SweepFamilySelection.Registered"/> — the same list the
/// product builds its own refusal vocabulary from — so a family added to the surface arrives here as a new
/// named cell rather than needing a line in a list somebody has to remember.</para>
/// </summary>
[Collection("server")]
[Trait("tier", "stdio")]
public sealed class CheckWirePathTests
{
    readonly ServerFixture _s;
    public CheckWirePathTests(ServerFixture s) => _s = s;

    public static IEnumerable<object[]> Families() =>
        SweepFamilySelection.Registered.Select(f => new object[] { SweepFamilySelection.Token(f) });

    [Theory]
    [MemberData(nameof(Families))]
    public void AFindingsFamilyBindsOverTheWireAndReachesTheToolBody(string family)
    {
        var r = _s.Call("housecarl_check", $$"""{"findings":["{{family}}"]}""");

        Assert.False(r.IsError, r.Describe());
        Assert.Equal(1, r.ContentBlocks);
        Assert.Equal("text", r.FirstBlockType);
        Assert.True(r.BodyRan, r.Describe());
    }

    [Fact]
    public void TheDefaultCallWithNoFindingsBindsAndReachesTheToolBody()
    {
        var r = _s.Call("housecarl_check", "{}");

        Assert.False(r.IsError, r.Describe());
        Assert.Equal("text", r.FirstBlockType);
        Assert.True(r.BodyRan, r.Describe());
    }

    /// <summary>
    /// The ToolCallShim's coercion path, on a check argument. HCBR-2026-06-11-01's live shape was a
    /// list-valued parameter sent as a bare string; the shim wraps it. Never exercised on this tool before.
    /// </summary>
    [Fact]
    public void FindingsSentAsABareStringIsCoercedToAOneElementArrayAndReachesTheBody()
    {
        var r = _s.Call("housecarl_check", """{"findings":"errors"}""");

        Assert.False(r.IsError, r.Describe());
        Assert.True(r.BodyRan, r.Describe());
    }

    // ---- the arms that can actually fail: binding refusals, named --------------------------------------

    [Fact]
    public void FindingsSentAsAnObjectIsRefusedByTypeAndTheRefusalNamesTheParameterAndBothTypes()
    {
        var r = _s.Call("housecarl_check", """{"findings":{"a":1}}""");

        Assert.True(r.IsError, r.Describe());
        Assert.Contains("findings", r.Text, StringComparison.Ordinal);
        Assert.Contains("expects array", r.Text, StringComparison.Ordinal);
        Assert.Contains("received object", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownParameterIsRefusedByNameAndTheRefusalListsWhatTheToolAccepts()
    {
        var r = _s.Call("housecarl_check", """{"nonexistent_param":"x"}""");

        Assert.True(r.IsError, r.Describe());
        Assert.Contains("unknown parameter: nonexistent_param", r.Text, StringComparison.Ordinal);
        Assert.Contains("findings", r.Text, StringComparison.Ordinal);   // the accepted list names the real one
    }

    [Fact]
    public void AScalarParameterSentAsTheWrongTypeIsRefusedNamingTheTypeItExpects()
    {
        var r = _s.Call("housecarl_check", """{"max_chars":"not-a-number"}""");

        Assert.True(r.IsError, r.Describe());
        Assert.Contains("max_chars", r.Text, StringComparison.Ordinal);
        Assert.Contains("expects integer", r.Text, StringComparison.Ordinal);
    }

    // ---- the published schema, against the product's own vocabulary -------------------------------------

    [Theory]
    [MemberData(nameof(Families))]
    public void ThePublishedFindingsSchemaNamesEveryRegisteredFamily(string family)
    {
        var findings = _s.PublishedTools["housecarl_check"]
                         .GetProperty("inputSchema").GetProperty("properties").GetProperty("findings");
        var description = findings.GetProperty("description").GetString() ?? "";

        // Quoted, the way the description spells its tokens — a bare substring would match the word
        // 'errors' anywhere in the prose and prove nothing.
        Assert.Contains($"'{family}'", description, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFindingsParameterIsPublishedAsAnArrayOfStrings_NotCollapsedByASerializerConverter()
    {
        var findings = _s.PublishedTools["housecarl_check"]
                         .GetProperty("inputSchema").GetProperty("properties").GetProperty("findings");

        Assert.Contains("array", findings.GetProperty("type").GetRawText(), StringComparison.Ordinal);
        Assert.Contains("string", findings.GetProperty("items").GetProperty("type").GetRawText(), StringComparison.Ordinal);
    }
}

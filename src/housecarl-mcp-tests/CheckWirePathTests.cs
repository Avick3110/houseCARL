using System.ComponentModel;
using System.Reflection;
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

    // ---- the parameter population ----------------------------------------------------------------------

    /// <summary>
    /// The tool's caller-facing parameters, off the method the SDK builds the schema FROM. MemberData has to
    /// be static, so this cannot read the running server; the arm below pins it to the wire instead, which is
    /// the stronger statement anyway — it fails if the two ever disagree.
    /// The method also takes an injected service argument, which carries no <c>[Description]</c> and is not a
    /// caller parameter; the pin is what proves that filter right rather than my say-so.
    /// </summary>
    public static IEnumerable<object[]> DeclaredParameters() =>
        CheckToolParameters().Select(p => new object[] { p.Name! });

    static System.Reflection.ParameterInfo[] CheckToolParameters() =>
        typeof(HousecarlMcp.CheckTools)
            .GetMethod("CheckTool", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!
            .GetParameters()
            .Where(p => p.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>() is not null)
            .ToArray();

    /// <summary>
    /// The population pin AND the vacuity canary in one: the parameters this sweep drives are exactly the
    /// properties the server publishes. A parameter added without a description, or published under another
    /// spelling, shows up here rather than quietly leaving the sweep short.
    /// </summary>
    [Fact]
    public void TheParametersDrivenAreExactlyTheOnesPublished_SoTheSweepIsNotShort()
    {
        var declared = CheckToolParameters().Select(p => p.Name!).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        var schema = _s.PublishedTools["housecarl_check"].GetProperty("inputSchema");
        var published = schema.GetProperty("properties").EnumerateObject()
                              .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.NotEmpty(declared);
        Assert.Equal(published, declared);
    }

    /// <summary>
    /// Every caller-facing parameter, sent over the wire on its own. Three of the twelve were driven when
    /// this file was written and nine had never been sent at all, on the one tool whose wire path had never
    /// been driven before this PR — a hand-picked population, short by exactly what nobody thought of.
    ///
    /// <para>The value is generated from the declared TYPE and is deliberately nonsense content ("zzz", a
    /// formid that cannot parse). That is sound because of the property this class's summary states: on an
    /// unconfigured server the config check runs before any value is interpreted, so anything that BINDS
    /// answers with the config prompt whatever it says. Measured before this test was written — all twelve
    /// parameters driven with garbage-but-well-typed values reached the body. So a RED here means the
    /// argument did not bind, which is the only thing this sweep claims.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(DeclaredParameters))]
    public void EveryPublishedParameterBindsOverTheWireAndReachesTheToolBody(string parameter)
    {
        var p = CheckToolParameters().Single(x => x.Name == parameter);
        var r = _s.Call("housecarl_check", $$"""{"{{parameter}}": {{SampleValueFor(p.ParameterType)}}}""");

        Assert.False(r.IsError, r.Describe());
        Assert.True(r.BodyRan, r.Describe());
    }

    /// <summary>A JSON literal of the declared type — the content is irrelevant, the shape is the subject.</summary>
    static string SampleValueFor(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t == typeof(string)) return "\"zzz\"";
        if (t == typeof(bool)) return "true";
        if (t == typeof(int) || t == typeof(long) || t == typeof(double)) return "1";
        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(t)) return """["zzz"]""";
        throw new NotSupportedException(
            $"No sample value for parameter type {t}. A new parameter shape reached this sweep; teach " +
            "SampleValueFor how to spell it rather than dropping the parameter from the population.");
    }

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
    ///
    /// <para>This arm proves the bare string BINDS and reaches the body — it cannot see what the shim
    /// produced, because an unconfigured server answers the same for any value that binds. A coercion
    /// yielding an empty array would pass here. The value itself is asserted in
    /// <see cref="ToolCallShimCoercionTests"/>; the name says only what this one measures.</para>
    /// </summary>
    [Fact]
    public void FindingsSentAsABareStringBindsAndReachesTheBody()
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

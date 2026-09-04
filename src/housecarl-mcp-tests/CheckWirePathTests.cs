using System.ComponentModel;
using System.Reflection;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// <c>housecarl_check</c> driven over stdio. Every other check test calls <c>CheckTools.CheckTool</c> as a
/// C# method, so none of them exercises its published schema, its argument binding, or the ToolCallShim.
///
/// <para>The config check runs BEFORE the <c>findings=</c> vocabulary is parsed, so every value that binds —
/// a legal family, a class, or a nonsense token — comes back with the same config prompt. These tests
/// therefore prove exactly two things: the argument binds, and the tool body was entered. They cannot prove
/// the vocabulary, which is covered over <c>SweepFamilySelection.TryParse</c>; the ones that can fail here
/// are the negative ones below.</para>
///
/// <para>The subject set is derived from <see cref="SweepFamilySelection.Registered"/>, the same list the
/// product builds its refusal vocabulary from, so a family added to the surface arrives here as a new named
/// case rather than needing a line in a hand-kept list.</para>
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

    /// <summary>The tool's caller-facing parameters, off the method the SDK builds the schema from. MemberData
    /// must be static, so this cannot read the running server; the test below pins it to the wire instead. The
    /// method also takes an injected service argument, which carries no <c>[Description]</c> and is not a
    /// caller parameter — that pin is what proves the filter right.</summary>
    public static IEnumerable<object[]> DeclaredParameters() =>
        CheckToolParameters().Select(p => new object[] { p.Name! });

    static System.Reflection.ParameterInfo[] CheckToolParameters() =>
        typeof(HousecarlMcp.CheckTools)
            .GetMethod("CheckTool", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!
            .GetParameters()
            .Where(p => p.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>() is not null)
            .ToArray();

    /// <summary>The parameters this sweep drives are exactly the properties the server publishes — an empty
    /// population, a parameter added without a description, or one published under another spelling shows up
    /// here rather than quietly leaving the sweep short.</summary>
    [Fact]
    public void TheParametersDrivenAreExactlyTheOnesPublished_SoTheSweepIsNotShort()
    {
        var declared = CheckToolParameters().Select(p => p.Name!).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        var schema = _s.PublishedTools[ToolNames.Check].GetProperty("inputSchema");
        var published = schema.GetProperty("properties").EnumerateObject()
                              .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.NotEmpty(declared);
        Assert.Equal(published, declared);
    }

    /// <summary>Every caller-facing parameter, sent over the wire on its own. The value is generated from the
    /// declared type and its content is deliberately nonsense: on an unconfigured server the config check runs
    /// before any value is interpreted, so anything that binds answers with the config prompt whatever it
    /// says. A failure here therefore means the argument did not bind, which is all this sweep claims.</summary>
    [Theory]
    [MemberData(nameof(DeclaredParameters))]
    public void EveryPublishedParameterBindsOverTheWireAndReachesTheToolBody(string parameter)
    {
        var p = CheckToolParameters().Single(x => x.Name == parameter);
        var r = _s.Call(ToolNames.Check, $$"""{"{{parameter}}": {{SampleValueFor(p.ParameterType)}}}""");

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
        var r = _s.Call(ToolNames.Check, $$"""{"findings":["{{family}}"]}""");

        Assert.False(r.IsError, r.Describe());
        Assert.Equal(1, r.ContentBlocks);
        Assert.Equal("text", r.FirstBlockType);
        Assert.True(r.BodyRan, r.Describe());
    }

    [Fact]
    public void TheDefaultCallWithNoFindingsBindsAndReachesTheToolBody()
    {
        var r = _s.Call(ToolNames.Check, "{}");

        Assert.False(r.IsError, r.Describe());
        Assert.Equal("text", r.FirstBlockType);
        Assert.True(r.BodyRan, r.Describe());
    }

    /// <summary>The ToolCallShim's coercion path on a check argument: a list-valued parameter sent as a bare
    /// string, which the shim wraps. This proves only that the bare string binds and reaches the body — an
    /// unconfigured server answers the same for any value that binds, so a coercion yielding an empty array
    /// would pass here. The produced value is asserted in <see cref="ToolCallShimCoercionTests"/>.</summary>
    [Fact]
    public void FindingsSentAsABareStringBindsAndReachesTheBody()
    {
        var r = _s.Call(ToolNames.Check, """{"findings":"errors"}""");

        Assert.False(r.IsError, r.Describe());
        Assert.True(r.BodyRan, r.Describe());
    }

    // ---- the tests that can actually fail: binding refusals, named -------------------------------------

    [Fact]
    public void FindingsSentAsAnObjectIsRefusedByTypeAndTheRefusalNamesTheParameterAndBothTypes()
    {
        var r = _s.Call(ToolNames.Check, """{"findings":{"a":1}}""");

        Assert.True(r.IsError, r.Describe());
        Assert.Contains("findings", r.Text, StringComparison.Ordinal);
        Assert.Contains("expects array", r.Text, StringComparison.Ordinal);
        Assert.Contains("received object", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownParameterIsRefusedByNameAndTheRefusalListsWhatTheToolAccepts()
    {
        var r = _s.Call(ToolNames.Check, """{"nonexistent_param":"x"}""");

        Assert.True(r.IsError, r.Describe());
        Assert.Contains("unknown parameter: nonexistent_param", r.Text, StringComparison.Ordinal);
        Assert.Contains("findings", r.Text, StringComparison.Ordinal);   // the accepted list names the real one
    }

    [Fact]
    public void AScalarParameterSentAsTheWrongTypeIsRefusedNamingTheTypeItExpects()
    {
        var r = _s.Call(ToolNames.Check, """{"max_chars":"not-a-number"}""");

        Assert.True(r.IsError, r.Describe());
        Assert.Contains("max_chars", r.Text, StringComparison.Ordinal);
        Assert.Contains("expects integer", r.Text, StringComparison.Ordinal);
    }

    // ---- the published schema, against the product's own vocabulary -------------------------------------

    [Theory]
    [MemberData(nameof(Families))]
    public void ThePublishedFindingsSchemaNamesEveryRegisteredFamily(string family)
    {
        var findings = _s.PublishedTools[ToolNames.Check]
                         .GetProperty("inputSchema").GetProperty("properties").GetProperty("findings");
        var description = findings.GetProperty("description").GetString() ?? "";

        // Quoted, the way the description spells its tokens — a bare substring would match the word
        // 'errors' anywhere in the prose and prove nothing.
        Assert.Contains($"'{family}'", description, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFindingsParameterIsPublishedAsAnArrayOfStrings_NotCollapsedByASerializerConverter()
    {
        var findings = _s.PublishedTools[ToolNames.Check]
                         .GetProperty("inputSchema").GetProperty("properties").GetProperty("findings");

        Assert.Contains("array", findings.GetProperty("type").GetRawText(), StringComparison.Ordinal);
        Assert.Contains("string", findings.GetProperty("items").GetProperty("type").GetRawText(), StringComparison.Ordinal);
    }
}

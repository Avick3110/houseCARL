using System.Text.Json;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The SHAPE <c>housecarl_skse</c>'s <c>findings=</c> accepts over the wire. A real JSON array is the
/// <c>housecarl_check</c> habit and the shape the tool's own description promises a refusal for, so it has to reach
/// the tool: published as a string alone it was caught by the shim's type check and answered with the generic
/// type-mismatch sentence, which teaches nothing about families.
///
/// <para>Driven through the built server: the string form goes nowhere near the schema or the binder, so it cannot
/// see this at all.</para>
/// </summary>
[Collection("server")]
[Trait("tier", "stdio")]
public sealed class SkseFindingsWireShapeTests
{
    readonly ServerFixture _s;
    public SkseFindingsWireShapeTests(ServerFixture s) => _s = s;

    /// <summary>The published declaration the shim judges against: a string OR an array, plus null for optional.</summary>
    [Fact]
    public void FindingsIsPublishedAsAStringOrAnArray()
    {
        var types = _s.PublishedTools[ToolNames.Skse].GetProperty("inputSchema").GetProperty("properties")
                      .GetProperty("findings").GetProperty("type")
                      .EnumerateArray().Select(e => e.GetString()!).ToArray();

        Assert.Equal(new[] { "string", "array", "null" }, types);
    }

    /// <summary>A real JSON array is answered by the TOOL, in its own words: the three family spellings and the
    /// one-family rule — never the shim's generic "could not be bound".</summary>
    [Theory]
    [InlineData("""{"findings":["inventory"]}""")]
    [InlineData("""{"findings":["inventory","pairing"]}""")]
    public void AJsonArrayForFindingsGetsTheToolsOwnOneFamilyRefusal(string arguments)
    {
        var r = _s.Call(ToolNames.Skse, arguments);

        Assert.DoesNotContain("could not be bound", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(ServerFixture.GenericError, r.Text, StringComparison.Ordinal);
        Assert.StartsWith("error: ", r.Text, StringComparison.Ordinal);
        Assert.Contains("is not a family on this tool", r.Text, StringComparison.Ordinal);
        foreach (var family in new[] { "inventory", "pairing", "config" })
            Assert.Contains($"'{family}'", r.Text, StringComparison.Ordinal);
        Assert.Contains("takes ONE value, not a list", r.Text, StringComparison.Ordinal);
        Assert.Contains("One family per call.", r.Text, StringComparison.Ordinal);

        // The argument is wrong whether or not an instance is configured, so the refusal outranks the config prompt:
        // being sent to configure one, only to meet this same refusal, is a round trip that teaches nothing.
        Assert.DoesNotContain(ServerFixture.ConfigPrompt, r.Text, StringComparison.Ordinal);
    }

    /// <summary>The control the array arm needs: a scalar findings= still binds as a SCALAR and runs. Declaring the
    /// array shape puts the parameter in reach of the shim's bare-string-to-array coercion, which would hand the tool
    /// <c>["pairing"]</c> — the very shape the arm above refuses — for a call that was spelled correctly.</summary>
    [Theory]
    [InlineData("inventory")]
    [InlineData("pairing")]
    [InlineData("config")]
    public void AScalarFindingsStillBindsAsAScalarAndReachesTheBody(string family)
    {
        var r = _s.Call(ToolNames.Skse, $$"""{"findings":"{{family}}"}""");

        Assert.False(r.IsError, r.Describe());
        Assert.DoesNotContain("is not a family on this tool", r.Text, StringComparison.Ordinal);
        Assert.True(r.BodyRan, r.Describe());
    }

    /// <summary>An OBJECT is neither declared shape, so it stays the SHIM's refusal — which now names both shapes the
    /// parameter takes. The tool's own refusal is for a value it can read; a shape the schema does not declare is the
    /// shim's to name.</summary>
    [Fact]
    public void AnObjectForFindingsIsRefusedNamingBothShapesTheParameterTakes()
    {
        var r = _s.Call(ToolNames.Skse, """{"findings":{"family":"inventory"}}""");

        Assert.DoesNotContain(ServerFixture.GenericError, r.Text, StringComparison.Ordinal);
        Assert.StartsWith("error: ", r.Text, StringComparison.Ordinal);
        Assert.Contains("findings (expects string or array, received object)", r.Text, StringComparison.Ordinal);
    }

    /// <summary>Whatever else changes, the arguments are judged before the instance is: an unusable findings= must
    /// never come back as the config prompt.</summary>
    [Fact]
    public void AnUnknownScalarFindingsIsRefusedRatherThanAnsweredWithTheConfigPrompt()
    {
        var r = _s.Call(ToolNames.Skse, """{"findings":"inventories"}""");

        Assert.Contains("is not a family on this tool", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(ServerFixture.ConfigPrompt, r.Text, StringComparison.Ordinal);
    }
}

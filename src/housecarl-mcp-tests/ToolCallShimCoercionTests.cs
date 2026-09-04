using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>What the ToolCallShim's coercion PRODUCES, asserted as a value. The wire tests in
/// <see cref="CheckWirePathTests"/> can only see that an argument bound, so a coercion that binds and drops
/// the caller's value is invisible there — the failure is a silently wrong answer, not a binding error. The
/// schemas are the shapes the published schema uses: <c>["array","null"]</c> is how the server spells an
/// optional list.</summary>
[Trait("tier", "unit")]
public sealed class ToolCallShimCoercionTests
{
    static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();
    static JsonElement Value(string json) => JsonDocument.Parse(json).RootElement.Clone();

    const string ArraySchema = """{"type":["array","null"],"items":{"type":["string","null"]}}""";

    [Fact]
    public void ABareStringForAnArrayParameterBecomesAOneElementArrayCarryingThatString()
    {
        var coerced = ToolCallShim.Coerce(Value("\"MyMod.esp\""), Schema(ArraySchema));

        Assert.NotNull(coerced);
        Assert.Equal(JsonValueKind.Array, coerced!.Value.ValueKind);
        Assert.Equal(1, coerced.Value.GetArrayLength());
        Assert.Equal("MyMod.esp", coerced.Value[0].GetString());
    }

    [Fact]
    public void AStringSpellingARealJsonArrayIsTakenAsThatArray_NotWrappedAgain()
    {
        var coerced = ToolCallShim.Coerce(Value("\"[\\\"A.esp\\\",\\\"B.esp\\\"]\""), Schema(ArraySchema));

        Assert.NotNull(coerced);
        Assert.Equal(JsonValueKind.Array, coerced!.Value.ValueKind);
        Assert.Equal(new[] { "A.esp", "B.esp" },
                     coerced.Value.EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public void AStringThatOnlyLooksLikeAnArrayIsWrappedWhole_SoNoWorkingShapeChangesMeaning()
    {
        var coerced = ToolCallShim.Coerce(Value("\"[not json\""), Schema(ArraySchema));

        Assert.NotNull(coerced);
        Assert.Equal(1, coerced!.Value.GetArrayLength());
        Assert.Equal("[not json", coerced.Value[0].GetString());
    }

    [Fact]
    public void ABareStringForABooleanParameterBecomesThatBoolean()
    {
        var coerced = ToolCallShim.Coerce(Value("\"true\""), Schema("""{"type":"boolean"}"""));

        Assert.NotNull(coerced);
        Assert.Equal(JsonValueKind.True, coerced!.Value.ValueKind);
    }

    [Fact]
    public void ABareStringForAnIntegerParameterBecomesThatNumber()
    {
        var coerced = ToolCallShim.Coerce(Value("\"100\""), Schema("""{"type":"integer"}"""));

        Assert.NotNull(coerced);
        Assert.Equal(JsonValueKind.Number, coerced!.Value.ValueKind);
        Assert.Equal(100, coerced.Value.GetInt32());
    }

    [Fact]
    public void AValueAlreadyOfTheDeclaredTypeIsLeftAlone()
    {
        Assert.Null(ToolCallShim.Coerce(Value("""["A.esp"]"""), Schema(ArraySchema)));
    }
}

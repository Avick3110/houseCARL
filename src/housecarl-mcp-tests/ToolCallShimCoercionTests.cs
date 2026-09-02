using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// What the ToolCallShim's coercion actually PRODUCES, asserted as a value.
///
/// The wire arms in <see cref="CheckWirePathTests"/> can only see that an argument bound and the tool body
/// was entered: an unconfigured server answers with the same config prompt whatever the value says. So a
/// coercion that bound fine and threw the caller's value away was invisible to them — a shim wrapping a bare
/// string into an EMPTY array instead of a one-element one passed the whole suite, and the caller who wrote
/// <c>{"plugins":"MyMod.esp"}</c> would have got the whole load order swept with a clean answer. That is the
/// silent-wrong-answer class, not a binding failure, and it is why these assert the element itself.
///
/// The schemas here are the shapes the real published schema uses (nullable unions — <c>["array","null"]</c>
/// is how the server spells an optional list), not invented ones.
/// </summary>
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

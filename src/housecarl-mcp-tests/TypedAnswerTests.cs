using System.Text.Json;
using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.ApplyGuardWorld;

namespace HousecarlMcpTests;

/// <summary>The typed answer: a refusal carries <c>isError</c>, a served answer and the in-place consent prompt do
/// not, the config prompt does, and the answer's text is what it was.</summary>
[Trait("tier", "integration")]
public sealed class TypedAnswerTests : IDisposable
{
    readonly ApplyGuardWorld W = new();
    public void Dispose() => W.Dispose();

    /// <summary>The call's text and whether Guard marked it refused, as the call-tool filter reads it; run on its own
    /// context so the verdict opened here ends with the call.</summary>
    internal static (string Text, bool Refused) Answer(Func<string> call) => Task.Run(() =>
    {
        var verdict = CallVerdict.Begin();
        var text = call();
        return (text, verdict.Refused);
    }).Result;

    [Fact]
    public void ARefusedWriteIsMarkedRefusedAndKeepsItsText()
    {
        var (text, refused) = Answer(() => ApplyTools.Apply(W.Svc,
            ops: Je($$"""[{"formid":"{{W.SubjectFid}}","field_path":"NoSuchField","value":"1"}]"""), patch: "TaBad"));
        Assert.True(refused, text);
        Assert.StartsWith("error:", text);
    }

    [Fact]
    public void AServedWriteIsNotMarkedRefused()
    {
        var (text, refused) = Answer(() => ApplyTools.Apply(W.Svc, ops: Je(W.DamageOp("71")), patch: "TaOk"));
        Assert.False(refused, text);
    }

    /// <summary>A failed write outcome is refused on json too, and keeps its outcome document's members.</summary>
    [Fact]
    public void ARefusedJsonWriteKeepsItsOutcomeDocument()
    {
        var (text, refused) = Answer(() => ApplyTools.Apply(W.Svc, ops: Je(W.DamageOp("72")),
            in_place: "NotInTheOrder.esp", format: "json"));
        Assert.True(refused, text);
        using var doc = JsonDocument.Parse(text);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("in_place", doc.RootElement.GetProperty("lane").GetString());
    }

    /// <summary>Aaron's pick (DECISIONS 2026-11-04 (12), Q2): the consent prompt is a question the next call answers, not a refusal.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("json")]
    public void TheInPlaceConsentPromptIsNotARefusal(string? format)
    {
        var (text, refused) = Answer(() => ApplyTools.Apply(W.Svc, ops: Je(W.DamageOp("73")), in_place: W.MasterName, format: format));
        Assert.False(refused, text);
        Assert.Contains("first-time confirmation", text);
    }

    /// <summary>The Guard's own refusal, an unhandled exception, is marked refused with its text unchanged.</summary>
    [Fact]
    public void TheGuardsOwnRefusalIsMarkedRefused()
    {
        static ToolAnswer Boom(ToolCall _) => throw new InvalidOperationException("boom");
        var (text, refused) = Answer(() => Guard.Tool("housecarl_probe", Boom));
        Assert.True(refused, text);
        Assert.StartsWith("error: housecarl_probe failed unexpectedly — InvalidOperationException: boom.", text);
    }

    /// <summary>The Guard's named-transient arm (a locked profile, a refused game language) is refused, text unchanged.</summary>
    [Fact]
    public void TheGuardsRefusedGameLanguageIsMarkedRefused()
    {
        static ToolAnswer Refuse(ToolCall _) => throw new HousecarlCore.GameLanguageRefusedException("the language is refused.");
        var (text, refused) = Answer(() => Guard.Tool("housecarl_probe", Refuse));
        Assert.True(refused, text);
        Assert.Equal("error: the language is refused.", text);
    }
}

/// <summary>The same verdict as the client sees it: <c>isError</c> on the call's result.</summary>
[Collection("server")]
[Trait("tier", "stdio")]
public sealed class TypedAnswerWireTests(ServerFixture s)
{
    /// <summary>Aaron's pick (Q1): a tool body's refusal reaches the client as <c>isError</c>, text unchanged.</summary>
    [Fact]
    public void ABodyRefusalReachesTheClientAsIsError()
    {
        var r = s.Call(ToolNames.Remove, """{"format":"xml"}""");
        Assert.True(r.IsError, r.Describe());
        Assert.StartsWith("error: format='xml' is not recognized", r.Text);
    }

    /// <summary>Aaron's pick (Q2): the config prompt sets <c>isError</c>, and is still the prompt.</summary>
    [Fact]
    public void TheConfigPromptReachesTheClientAsIsError()
    {
        var r = s.Call(ToolNames.Records, "{}");
        Assert.True(r.IsError, r.Describe());
        Assert.True(r.BodyRan, r.Describe());
    }
}

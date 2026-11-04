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

    /// <summary>One in-place write site, called on <paramref name="target"/> with no acknowledge.</summary>
    string InPlace(string site, string target) => site switch
    {
        "forward" => ForwardTools.Forward(W.Svc, formids: new[] { W.SubjectFid }, source: W.MasterName, in_place: target),
        "remove" => RemoveTools.Remove(W.Svc, formids: new[] { W.SubjectFid }, in_place: target),
        "create" => CreateTools.Create(W.Svc, in_place: target, records: Je(
            """[{"record_type":"Faction","editorid":"TaNewFaction","ops":[{"field_path":"Name","value":"N"}]}]""")),
        "compact" => WriteTools.CompactPlugin(W.Svc, source: target, in_place: true),
        _ => throw new ArgumentOutOfRangeException(nameof(site)),
    };

    /// <summary>Q2 at each in-place write site: the consent prompt is served.</summary>
    [Theory]
    [InlineData("forward", "first-time confirmation")]
    [InlineData("remove", "first-time confirmation")]
    [InlineData("create", "first-time confirmation")]
    [InlineData("compact", "CONFIRM in-place rewrite")]
    public void EachInPlaceSitesConsentPromptIsServed(string site, string prompt)
    {
        var (text, refused) = Answer(() => InPlace(site, W.ReplacerName));
        Assert.False(refused, text);
        Assert.Contains(prompt, text);
    }

    /// <summary>Each in-place write site refuses a write that failed.</summary>
    [Theory]
    [InlineData("forward")]
    [InlineData("remove")]
    [InlineData("create")]
    [InlineData("compact")]
    public void EachInPlaceSitesFailureIsRefused(string site)
    {
        var (text, refused) = Answer(() => InPlace(site, "NotInTheOrder.esp"));
        Assert.True(refused, text);
        Assert.Contains("NotInTheOrder.esp", text);
    }

    /// <summary>check is refused on its whole-call error and served on a sweep that ran.</summary>
    [Fact]
    public void CheckIsRefusedExactlyOnItsWholeCallError()
    {
        var (bad, badRefused) = Answer(() => CheckTools.CheckTool(W.Svc, plugins: new[] { " " }));
        Assert.True(badRefused, bad);
        Assert.Contains("a blank plugin name", bad);
        var (ok, okRefused) = Answer(() => CheckTools.CheckTool(W.Svc, plugins: new[] { W.ReplacerName }, findings: new[] { "errors" }));
        Assert.False(okRefused, ok);
    }

    /// <summary>bsa_extract's "extract FAILED" (an entry escaping the destination) is refused.</summary>
    [Fact]
    public void AFailedExtractIsRefused()
    {
        var archive = BsaExtractArchives.Write(W.Root, "evil.bsa", BsaExtractArchives.Escaping());
        var (text, refused) = Answer(() => BsaTools.BsaExtract(W.Svc, archive, out_path: Path.Combine(W.Root, "evil-out")));
        Assert.True(refused, text);
        Assert.StartsWith("extract FAILED:", text);
    }

    /// <summary>The tool-path prompt, like the config prompt, is refused: bsa_repack with no BSArch set.</summary>
    [Fact]
    public void TheRepackToolPathPromptIsRefused()
    {
        var source = Path.Combine(W.Root, "loose", "Pack");
        Directory.CreateDirectory(source);
        var bridge = new ToolPathResolver(new UserConfigStore(Path.Combine(W.Root, "tools-none.json")));
        Assert.Null(bridge.Resolve(ToolDependency.Bsarch));
        var (text, refused) = Answer(() => BsaTools.BsaRepack(W.Svc, bridge, source));
        Assert.True(refused, text);
        Assert.DoesNotContain("error:", text);
    }
}

/// <summary>The nif_set verdict: its consent prompt is served, a refused op that wrote nothing is refused.</summary>
[Trait("tier", "integration")]
public sealed class TypedAnswerNifSetTests
{
    [Fact]
    public void NifSetsConsentPromptIsServed()
    {
        using var w = new NifSetInstance(NifSetMeshes.Guard());
        var (text, refused) = TypedAnswerTests.Answer(() => NifTools.NifSet(w.Svc, mesh_path: NifSetInstance.MeshRel,
            op: "set_flags", target: "GuardShape", flags: "0x800000E", in_place: true));
        Assert.False(refused, text);
        Assert.Contains("first-time confirmation", text);
    }

    [Fact]
    public void NifSetsNoWriteRefusalIsRefused()
    {
        using var w = new NifSetInstance(NifSetMeshes.Guard());
        var (text, refused) = TypedAnswerTests.Answer(() => NifTools.NifSet(w.Svc, mesh_path: NifSetInstance.MeshRel,
            op: "set_flags", target: "NoSuchShape", flags: "0x800000E", patch: "TaNif"));
        Assert.True(refused, text);
        Assert.Contains("NoSuchShape", text);
    }
}

/// <summary>compile_script's verdicts: the tool-path prompt and a compile that fails are refused.</summary>
[Collection("scripts")]
[Trait("tier", "integration")]
public sealed class TypedAnswerCompileTests(ScriptsFixture f)
{
    readonly ScriptsWorld W = f.W;

    string Own()
    {
        var own = Path.Combine(W.Root, "hc-ta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(own);
        File.WriteAllText(Path.Combine(own, "TaScript.psc"), "ScriptName TaScript\n");
        return own;
    }

    [Fact]
    public void TheCompileToolPathPromptIsRefused()
    {
        var own = Own();
        var store = new UserConfigStore(Path.Combine(own, "user.json"));
        var bridge = new ToolPathResolver(store);
        // The body probes these hints, the located Skyrim SE install among them; a compiler there would run instead.
        var hints = W.Svc.CompilerGameDirHints();
        if (ToolBridge.Probe(ToolDependency.PapyrusCompiler, hints) is { } located)
            Assert.Fail($"cannot reach the tool-path prompt on this machine: the compiler at '{located}' is auto-detected.");
        var (text, refused) = TypedAnswerTests.Answer(() => CompileTools.CompileScript(W.Svc, bridge, store,
            Path.Combine(own, "TaScript.psc"), auto_imports: false, out_path: Path.Combine(own, "out")));
        Assert.True(refused, text);
        Assert.Equal(ToolBridge.RenderMissingPrompt(ToolDependency.PapyrusCompiler, hints), text);
    }

    /// <summary>A compiler that cannot run fails the compile, and the failed outcome is refused.</summary>
    [Fact]
    public void AFailedCompileIsRefused()
    {
        var own = Own();
        var stub = Path.Combine(own, "PapyrusCompiler.exe");
        File.WriteAllText(stub, "stub");
        var store = new UserConfigStore(Path.Combine(own, "user.json"));
        var bridge = new ToolPathResolver(store);
        Assert.True(bridge.Save(ToolDependency.PapyrusCompiler, stub).ok);
        var (text, refused) = TypedAnswerTests.Answer(() => CompileTools.CompileScript(W.Svc, bridge, store,
            Path.Combine(own, "TaScript.psc"), auto_imports: false, out_path: Path.Combine(own, "out")));
        Assert.True(refused, text);
        Assert.DoesNotContain("TaScript.pex written", text);
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

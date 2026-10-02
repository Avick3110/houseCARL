using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary><see cref="PapyrusCompile.ParseDiagnostics"/> on the exact stderr the CK compiler emits (captured
/// 2026-06-05). Migrated from the <c>compile-probe</c> generator probe, layer 1.</summary>
[Trait("tier", "unit")]
public sealed class PapyrusCompileParseTests
{
    static readonly string RealStderr = string.Join("\n", new[]
    {
        @"C:\mod\Source\Scripts\HCBad.psc(4,4): no viable alternative at character '@'",
        @"C:\mod\Source\Scripts\HCBad.psc(4,18): no viable alternative at input 'papyrus'",
        @"C:\mod\Source\Scripts\HCBad.psc(4,12): Unknown user flag papyrus",
        @"C:\mod\Source\Scripts\HCBad.psc(5,0): missing EOF at 'EndFunction'",
    });

    // probe: "parses 4 diagnostics"
    [Fact]
    public void ParsesFourDiagnostics() => Assert.Equal(4, PapyrusCompile.ParseDiagnostics(RealStderr).Count);

    // probe: "first diagnostic: line=4 col=4 message intact"
    [Fact]
    public void FirstDiagnostic_LineFourColFour_MessageIntact()
    {
        var first = PapyrusCompile.ParseDiagnostics(RealStderr)[0];
        Assert.Equal((4, 4, "no viable alternative at character '@'"), (first.Line, first.Col, first.Message));
    }

    // probe: "later diagnostic line/col parsed"
    [Fact]
    public void LaterDiagnostic_LineAndColParsed()
    {
        var fourth = PapyrusCompile.ParseDiagnostics(RealStderr)[3];
        Assert.Equal((5, 0), (fourth.Line, fourth.Col));
    }

    // probe: "empty stderr → no diagnostics"
    [Fact]
    public void EmptyStderr_NoDiagnostics() => Assert.Empty(PapyrusCompile.ParseDiagnostics(""));

    // probe: "a non-diagnostic line is ignored (not mis-parsed)"
    [Fact]
    public void ANonDiagnosticLine_IsIgnored() =>
        Assert.Empty(PapyrusCompile.ParseDiagnostics("Batch compile of 1 files finished. 1 succeeded, 0 failed."));
}

/// <summary>A fact that reports as skipped, not passed, when the Papyrus compiler is not on this machine.</summary>
public sealed class PapyrusCompilerFactAttribute : FactAttribute
{
    /// <summary>The compiler the end-to-end tests drive: <c>HOUSECARL_PAPYRUS_COMPILER</c> if set, else the probe's default.</summary>
    public static string Compiler =>
        Environment.GetEnvironmentVariable("HOUSECARL_PAPYRUS_COMPILER") is { Length: > 0 } set
            ? set
            : @"E:\SteamLibrary\steamapps\common\Skyrim Special Edition\Papyrus Compiler\PapyrusCompiler.exe";

    public PapyrusCompilerFactAttribute()
    {
        if (!File.Exists(Compiler))
            Skip = $"No Papyrus compiler at '{Compiler}'; set HOUSECARL_PAPYRUS_COMPILER to run this.";
    }
}

/// <summary><see cref="PapyrusCompile.CompileObject"/> against the real PapyrusCompiler.exe on a good and a broken
/// script, and a recompile over a prior .pex. Migrated from the <c>compile-probe</c> generator probe, layer 2. Skips
/// where the compiler is absent, which includes CI.</summary>
[Trait("tier", "integration")]
public sealed class PapyrusCompileEndToEndTests : IDisposable
{
    const string Good = "Scriptname HCGood extends Quest\n\nFunction Foo()\n    Debug.Trace(\"ok from houseCARL\")\nEndFunction\n";
    const string Bad = "Scriptname HCBad extends Quest\n\nFunction Foo()\n    @@@ not valid papyrus\nEndFunction\n";

    readonly string _work = Path.Combine(Path.GetTempPath(), "hc-compile-" + Guid.NewGuid().ToString("N"));
    readonly string _out;

    public PapyrusCompileEndToEndTests()
    {
        _out = Path.Combine(_work, "out");
        Directory.CreateDirectory(_out);
        File.WriteAllText(Path.Combine(_work, "HCGood.psc"), Good);
        File.WriteAllText(Path.Combine(_work, "HCBad.psc"), Bad);
    }

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch { /* temp scratch */ }
    }

    static string Vanilla =>
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(PapyrusCompilerFactAttribute.Compiler))!, "Data", "Source", "Scripts");

    CompileResult Compile(string name) =>
        PapyrusCompile.CompileObject(PapyrusCompilerFactAttribute.Compiler, name, new[] { _work, Vanilla }, _out);

    // probe: "vanilla sources derived from compiler dir exist"
    [PapyrusCompilerFact]
    public void VanillaSources_DerivedFromCompilerDir_Exist() => Assert.True(Directory.Exists(Vanilla), Vanilla);

    // probe: "good: compiler ran", "good: Success + .pex written", "good: no diagnostics"
    [PapyrusCompilerFact]
    public void GoodScript_Runs_WritesPex_NoDiagnostics()
    {
        var good = Compile("HCGood");
        Assert.True(good.Ran, good.RunError);
        Assert.True(good.Success && good.PexPath is not null && File.Exists(good.PexPath), good.Stderr);
        Assert.Empty(good.Diagnostics);
    }

    // probe: "bad: compiler ran", "bad: Success=false + no .pex", "bad: structured diagnostics produced"
    [PapyrusCompilerFact]
    public void BadScript_Runs_Fails_NoPex_WithDiagnostics()
    {
        var bad = Compile("HCBad");
        Assert.True(bad.Ran, bad.RunError);
        Assert.False(bad.Success);
        Assert.Null(bad.PexPath);
        Assert.NotEmpty(bad.Diagnostics);
    }

    // probe: "recompile (still valid): Success + .pex present", "recompile (still valid): .pex rewritten this run"
    [PapyrusCompilerFact]
    public void RecompileStillValid_Succeeds_AndRewritesThePex()
    {
        var pex = Compile("HCGood").PexPath;
        Assert.NotNull(pex);
        var firstWriteUtc = File.GetLastWriteTimeUtc(pex);

        var again = Compile("HCGood");
        Assert.True(again.Success && File.Exists(pex));
        Assert.True(File.GetLastWriteTimeUtc(pex) > firstWriteUtc);
    }

    // probe: "recompile (now broken): reports failure, no new .pex", "the PRIOR .pex is LEFT INTACT (non-destructive)"
    [PapyrusCompilerFact]
    public void RecompileNowBroken_ReportsFailure_AndLeavesThePriorPex()
    {
        var pex = Compile("HCGood").PexPath;
        Assert.NotNull(pex);

        File.WriteAllText(Path.Combine(_work, "HCGood.psc"), Good.Replace("Debug.Trace(\"ok from houseCARL\")", "@@@ broken edit"));
        var broken = Compile("HCGood");
        Assert.False(broken.Success);
        Assert.Null(broken.PexPath);
        Assert.True(File.Exists(pex));
    }
}

using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Pex;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// housecarl_decompile_script's contract on committed fixtures: two .pex files compiled once by the CK's own
/// PapyrusCompiler from our own probe sources, and the goldens their decompile must reproduce. The fixtures are a
/// copy of the ones the generator's <c>decompile-guard</c> probe read, re-homed under <c>fixtures/decompile/</c> in
/// this project when that probe was deleted. CI has no CK compiler, so the recompile round trip is not proved here.
/// Converted-from: DecompileGuardProbe.
/// </summary>
[Trait("tier", "unit")]
public sealed class DecompileGuardTests
{
    static readonly string Fixtures = Path.Combine(HarnessPaths.RepoRoot, "src", "housecarl-mcp-tests", "fixtures", "decompile");
    static string ConstructPex => Path.Combine(Fixtures, "HC_SpikeProbe01.pex");
    static string UpcastPex => Path.Combine(Fixtures, "HC_UpcastProbe.pex");

    // The vanilla class map the server ships beside its exe, read from the same place the server reads it.
    static readonly (Dictionary<string, string> Edges, string? Note) Baseline =
        PapyrusClassParents.LoadBaseline(Path.Combine(AppContext.BaseDirectory, "vanilla-class-parents.json"));

    static PexFile Load(string path) => PexFile.CreateFromFile(path, GameCategory.Skyrim);
    static string Norm(string s) => s.Replace("\r\n", "\n").TrimEnd('\n');
    static string FreshDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "hc-decompile-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // probe: "committed vanilla baseline loads"
    [Fact]
    public void TheShippedVanillaClassMapLoadsWithoutANote()
    {
        Assert.Null(Baseline.Note);
        Assert.NotEmpty(Baseline.Edges);
    }

    // probe: "writes without refusal on a fresh folder", "one .psc, named by the OBJECT", "zero failed functions",
    // "no optimizer hints on canonical CK-compiler output"
    [Fact]
    public void TheCanonicalFixtureWritesOnePscNamedByItsObjectWithNoFailuresAndNoOptimizerHints()
    {
        var dir = FreshDir();
        try
        {
            var o = DecompileTools.WriteObjects(Load(ConstructPex), Baseline.Edges, dir);

            Assert.Null(o.ExistingTarget);
            Assert.False(o.UnnamedObject);
            Assert.Equal("HC_SpikeProbe01.psc", Path.GetFileName(Assert.Single(o.Written)));
            Assert.Equal(0, o.FunctionsFailed);
            Assert.Equal(0, o.OptimizerHints);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* temp cleanup */ } }
    }

    // probe: "decompiled source EXACTLY matches the golden (modulo line endings)"
    [Fact]
    public void TheCanonicalFixtureDecompilesExactlyToItsGolden()
    {
        var dir = FreshDir();
        try
        {
            var o = DecompileTools.WriteObjects(Load(ConstructPex), Baseline.Edges, dir);

            var want = Norm(File.ReadAllText(Path.Combine(Fixtures, "HC_SpikeProbe01.golden.psc")));
            Assert.Equal(want, Norm(File.ReadAllText(Assert.Single(o.Written))));
        }
        finally { try { Directory.Delete(dir, true); } catch { /* temp cleanup */ } }
    }

    // probe: "refusal names the existing target", "nothing written on refusal", "the existing file is byte-untouched"
    [Fact]
    public void AnExistingTargetPscIsNamedAndLeftByteUntouched()
    {
        var dir = FreshDir();
        try
        {
            var target = Path.Combine(dir, "HC_SpikeProbe01.psc");
            File.WriteAllText(target, "SENTINEL - must survive");
            var before = File.ReadAllBytes(target);

            var o = DecompileTools.WriteObjects(Load(ConstructPex), Baseline.Edges, dir);

            Assert.Equal(target, o.ExistingTarget, StringComparer.OrdinalIgnoreCase);
            Assert.Empty(o.Written);
            Assert.Equal(before, File.ReadAllBytes(target));
        }
        finally { try { Directory.Delete(dir, true); } catch { /* temp cleanup */ } }
    }

    // probe: "inverted branch still structures clean (negated condition)", "the optimizer hint FIRES on
    // statement-level JMPT". The CK compiler emits a statement conditional as JMPF; flipping Tally's one to JMPT is
    // what an optimizer (Caprica) does.
    [Fact]
    public void AStatementLevelJmptFiresTheOptimizerHintAndStillStructuresClean()
    {
        var pex = Load(ConstructPex);
        var tally = ((PexObject)pex.Objects[0]).States
            .SelectMany(s => s.Functions)
            .Single(f => string.Equals(f.FunctionName, "Tally", StringComparison.OrdinalIgnoreCase)).Function!;
        tally.Instructions.First(x => x.OpCode == InstructionOpcode.JMPF).OpCode = InstructionOpcode.JMPT;

        var r = PapyrusDecompiler.DecompileFile(pex, Baseline.Edges);

        Assert.Equal(0, r.FunctionsFailed);
        Assert.True(r.OptimizerHints > 0, $"hints={r.OptimizerHints}");
    }

    // probe: "truncated pex throws out of the parser (mapped to the named error, no output written)"
    [Fact]
    public void ATruncatedPexThrowsOutOfTheParser()
    {
        var corrupt = Path.Combine(Path.GetTempPath(), "hc-decompile-guard-corrupt-" + Guid.NewGuid().ToString("N") + ".pex");
        try
        {
            var bytes = File.ReadAllBytes(ConstructPex);
            File.WriteAllBytes(corrupt, bytes[..(bytes.Length / 3)]);

            Assert.ThrowsAny<Exception>(() => Load(corrupt));
        }
        finally { try { File.Delete(corrupt); } catch { /* temp cleanup */ } }
    }

    // probe: "both objects written, none failed", "object 1 keeps its Hidden flag on the multi-object path",
    // "object 2 keeps its Hidden flag on the multi-object path"
    [Fact]
    public void BothObjectsOfATwoObjectPexKeepTheirHiddenFlag()
    {
        var dir = FreshDir();
        try
        {
            var multi = Load(ConstructPex);
            var second = (PexObject)Load(ConstructPex).Objects[0];
            second.Name = "HC_CloneProbe";
            multi.Objects.Add(second);

            var o = DecompileTools.WriteObjects(multi, Baseline.Edges, dir);

            Assert.Equal(2, o.Written.Count);
            Assert.Equal(0, o.FunctionsFailed);
            Assert.Contains("extends Quest Hidden", File.ReadAllText(o.Written[0]));
            Assert.Contains("extends Quest Hidden", File.ReadAllText(o.Written[1]));
        }
        finally { try { Directory.Delete(dir, true); } catch { /* temp cleanup */ } }
    }

    // probe: "both modes structure clean", "WITH the vanilla map: the Actor->ObjectReference upcast stays implicit
    // (Foo(a))", "with-map output matches its golden"
    [Fact]
    public void WithTheVanillaMapTheUpcastStaysImplicitAndMatchesItsGolden()
    {
        var r = PapyrusDecompiler.DecompileFile(Load(UpcastPex), Baseline.Edges);

        Assert.Equal(0, r.FunctionsFailed);
        Assert.Contains("Foo(a)", r.Source);
        Assert.DoesNotContain("as ObjectReference", r.Source, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Norm(File.ReadAllText(Path.Combine(Fixtures, "HC_UpcastProbe.golden.psc"))), Norm(r.Source));
    }

    // probe: "both modes structure clean", "WITHOUT a map: the upcast renders explicitly — correct source, the
    // documented degraded mode"
    [Fact]
    public void WithoutAClassMapTheUpcastRendersAsAnExplicitCast()
    {
        var r = PapyrusDecompiler.DecompileFile(Load(UpcastPex), null);

        Assert.Equal(0, r.FunctionsFailed);
        Assert.Contains("as ObjectReference", r.Source, StringComparison.OrdinalIgnoreCase);
    }
}

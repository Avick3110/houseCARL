using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Every <c>out_path=</c> takes an absolute path only (#782). A relative one used to be resolved against the
/// SERVER's working directory, so the extraction or the compiled file landed somewhere the caller could not predict
/// and the response named it as if it were the folder they asked for. Driven on the shared
/// <see cref="ScriptsWorld"/>, which these calls never write to: each one refuses before anything is written.</summary>
[Collection("scripts")]
[Trait("tier", "integration")]
public sealed class OutPathAbsoluteTests
{
    readonly ScriptsWorld W;
    public OutPathAbsoluteTests(ScriptsFixture f) => W = f.W;

    /// <summary>A relative out_path nothing can have created before this run — the bug these tests cover leaves a
    /// folder of that name beside the server, and a fixed name would carry one run's residue into the next.</summary>
    static string Relative() => "hc-out-path-" + Guid.NewGuid().ToString("N");

    /// <summary>The folder a relative out_path would have been resolved to, so a test can state it was not made.</summary>
    static string WhereARelativePathWouldLand(string relative) => Path.Combine(Directory.GetCurrentDirectory(), relative);

    [Fact]
    public void BsaExtractRefusesARelativeOutPathAndUnpacksNothing()
    {
        // A file that exists, so the call gets past the archive check and reaches out_path. It is never opened:
        // the refusal comes first.
        var archive = Path.Combine(Path.GetTempPath(), "hc-out-path-" + Guid.NewGuid().ToString("N") + ".bsa");
        File.WriteAllText(archive, "not a real archive");
        var relative = Relative();
        try
        {
            var r = BsaTools.BsaExtract(W.Svc, archive, out_path: relative);

            Assert.StartsWith("error:", r);
            Assert.Contains("absolute", r);
            Assert.False(Directory.Exists(WhereARelativePathWouldLand(relative)));
        }
        finally { try { File.Delete(archive); } catch { /* temp cleanup */ } }
    }

    [Fact]
    public void WriteSeqRefusesARelativeOutPathAndWritesNoSeq()
    {
        using var w = new DialogueWorld();
        var relative = Relative();

        var r = SeqTools.WriteSeq(w.Svc, w.PatchPath, out_path: relative);

        Assert.StartsWith("error:", r);
        Assert.Contains("absolute", r);
        Assert.False(Directory.Exists(WhereARelativePathWouldLand(relative)));
    }

    /// <summary>The compile lane's own resolver rather than the tool: <c>housecarl_compile_script</c> asks for the
    /// Papyrus compiler path before it reaches out_path, so a test driving the tool would only ever see that prompt.
    /// This is the same shared body the .seq lane refuses through.</summary>
    [Fact]
    public void TheCompiledScriptFolderRefusesARelativeOutPathAndCreatesNothing()
    {
        var relative = Relative();

        var ex = Assert.Throws<InvalidOperationException>(() => W.Svc.ResolveExplicitScriptFolder(relative, out _));

        Assert.Contains("absolute", ex.Message);
        Assert.False(Directory.Exists(WhereARelativePathWouldLand(relative)));
    }

    /// <summary>The vacuity check: an absolute out_path still resolves, so the refusals above are about the path
    /// being relative and nothing else.</summary>
    [Fact]
    public void AnAbsoluteOutPathStillResolvesToItsScriptsFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "hc-out-path-" + Guid.NewGuid().ToString("N"));
        try
        {
            var rf = W.Svc.ResolveExplicitScriptFolder(root, out _);

            Assert.Equal(Path.Combine(root, "Scripts"), rf.OutputDir);
            Assert.True(Directory.Exists(rf.OutputDir));
        }
        finally { try { Directory.Delete(root, true); } catch { /* temp cleanup */ } }
    }
}

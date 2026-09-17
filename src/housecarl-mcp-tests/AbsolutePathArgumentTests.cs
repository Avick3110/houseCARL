using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Every path a caller names — <c>out_path=</c>, <c>to_file=</c>, an <c>@file</c> list on a parameter or
/// inside a <c>where=</c> predicate, a SkyPatcher draft INI — is absolute or is refused (#782). Anything else
/// resolves against the SERVER's working directory, so the file is read or written somewhere the caller cannot
/// predict while the response names the path they typed. Each refusal test here refuses before anything is written;
/// the vacuity tests write only into a temp root of their own. Some need no world at all; the ones that do take the
/// shared <see cref="ScriptsWorld"/> or build their own.</summary>
[Collection("scripts")]
[Trait("tier", "integration")]
public sealed class AbsolutePathArgumentTests
{
    readonly ScriptsWorld W;
    public AbsolutePathArgumentTests(ScriptsFixture f) => W = f.W;

    /// <summary>A relative path nothing can have created before this run — the bug these tests cover leaves a file or
    /// folder of that name beside the server, and a fixed name would carry one run's residue into the next.</summary>
    static string Relative() => "hc-out-path-" + Guid.NewGuid().ToString("N");

    /// <summary>Where a relative path would have been resolved to, so a test can state nothing landed there.</summary>
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

    /// <summary>A drive-rooted path with no root directory ('C:name') is the same bug wearing a drive letter: it
    /// resolves against the server's current directory ON that drive, which is why the rule is fully-qualified
    /// rather than rooted. The artifact lane writes a file, so leaving it rooted-only left #782 standing there.</summary>
    [Fact]
    public void ToFileRefusesADriveRootedPathThatIsNotFullyQualified()
    {
        var relative = Relative() + ".jsonl";

        var r = AssetTools.AssetStatus(W.Svc, new[] { "textures\\hc\\nothing.dds" }, to_file: "C:" + relative);

        Assert.StartsWith("error:", r);
        Assert.Contains("absolute", r);
        Assert.False(File.Exists(Path.GetFullPath("C:" + relative)));
    }

    /// <summary>The same rule on the way IN: an '@file' list that is rooted but not fully qualified is refused by
    /// name, rather than read from wherever the server happens to be standing.</summary>
    [Fact]
    public void AnAtFileListRefusesADriveRootedPathThatIsNotFullyQualified()
    {
        var r = AssetTools.AssetStatus(W.Svc, new[] { "@C:" + Relative() + ".txt" });

        Assert.StartsWith("error:", r);
        Assert.Contains("absolute", r);
    }

    /// <summary>The list-parameter reader behind the JSON '@file' inputs, which has its own copy of the check.</summary>
    [Fact]
    public void TheListParameterFileReaderRefusesADriveRootedPath()
    {
        var (text, error) = ListParams.ReadAtFile("@C:" + Relative() + ".json", "ops");

        Assert.Null(text);
        Assert.Contains("absolute", error);
    }

    /// <summary>`where=`'s own list readers hold the same rule: both live in core, which is why the helper does too
    /// rather than the surface carrying two definitions of absolute.</summary>
    [Fact]
    public void AWhereListFileRefusesADriveRootedPathThatIsNotFullyQualified()
    {
        var relative = Relative() + ".txt";

        var values = FieldPredicateSet.Parse(new[] { $"editorid in @C:{relative}" });
        var formids = FieldPredicateSet.Parse(new[] { $"formid in @C:{relative}" });

        Assert.Contains("is not an absolute path", values.Error);
        Assert.Contains("is not an absolute path", formids.Error);
        // Vacuity: the same paths made fully qualified get past the shape gate and fail on the read instead.
        Assert.Contains("could not read",
                        FieldPredicateSet.Parse(new[] { $"editorid in @{Path.Combine(Path.GetTempPath(), relative)}" }).Error);
    }

    /// <summary>The SkyPatcher draft path reads a file off disk, so it holds the rule too.</summary>
    [Fact]
    public void TheSkyPatcherDraftPathRefusesADriveRootedPathThatIsNotFullyQualified()
    {
        var error = SkyPatcherDraft.Prepare("C:" + Relative() + ".ini", null, SkyPatcherCatalog.Load(), out var plan);

        Assert.Contains("is not an absolute path", error);
        Assert.Null(plan);
    }

    /// <summary>The vacuity check: an absolute out_path still resolves, so the refusals above are about the path
    /// being relative and nothing else. This one writes — the Scripts\ folder under its own temp root.</summary>
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

    /// <summary>The vacuity check for the artifact lane: the same call with a fully-qualified to_file writes its
    /// artifact, so the refusal above is about the path's shape and nothing else. Writes only into its temp root.</summary>
    [Fact]
    public void AnAbsoluteToFileStillWritesTheArtifact()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hc-out-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "paths.jsonl");
        try
        {
            AssetTools.AssetStatus(W.Svc, new[] { "textures\\hc\\nothing.dds" }, to_file: target);

            Assert.True(File.Exists(target));
        }
        finally { try { Directory.Delete(dir, true); } catch { /* temp cleanup */ } }
    }
}

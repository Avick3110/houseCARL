using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary><c>housecarl_decompile_script</c>'s <c>out_path=</c> lane: the .psc lands in the folder the caller
/// names, no mod folder is cut, and a call that also names a patch folder lands in out_path and is told so —
/// the same rule the compile and .seq lanes carry. Driven on the shared
/// <see cref="ScriptsWorld"/>, whose Scripts folder already holds real .pex files; the out_path lane writes
/// outside the instance, so the world stays frozen.</summary>
[Collection("scripts")]
[Trait("tier", "integration")]
public sealed class DecompileOutPathTests
{
    readonly ScriptsWorld W;
    public DecompileOutPathTests(ScriptsFixture f) => W = f.W;

    string Pex => Path.Combine(W.ScriptsDir, ScriptsWorld.BaseScript + ".pex");

    static string FreshDir() => Path.Combine(Path.GetTempPath(), "hc-decompile-out-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void OutPathWritesThePscThereAndCreatesTheFolderItNames()
    {
        var dest = Path.Combine(FreshDir(), "sources");   // does not exist yet: out_path= creates it
        try
        {
            var r = DecompileTools.DecompileScript(W.Svc, Pex, out_path: dest);

            var psc = Path.Combine(dest, ScriptsWorld.BaseScript + ".psc");
            Assert.True(File.Exists(psc), r);
            Assert.Contains(psc, r);
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(dest)!, true); } catch { /* temp cleanup */ } }
    }

    [Fact]
    public void OutPathCutsNoModFolder()
    {
        var dest = FreshDir();
        var before = Directory.GetDirectories(W.ModsDir).OrderBy(d => d, StringComparer.Ordinal).ToArray();
        try
        {
            DecompileTools.DecompileScript(W.Svc, Pex, out_path: dest);

            Assert.Equal(before, Directory.GetDirectories(W.ModsDir).OrderBy(d => d, StringComparer.Ordinal).ToArray());
        }
        finally { try { Directory.Delete(dest, true); } catch { /* temp cleanup */ } }
    }

    [Fact]
    public void ARelativeOutPathIsRefusedAndSaysToPassAnAbsoluteOne()
    {
        // A name nothing can have created before this run: the bug leaves a folder of it beside the server, and a
        // fixed name would carry one run's residue into the next.
        var relative = "hc-decompile-out-" + Guid.NewGuid().ToString("N");

        var r = DecompileTools.DecompileScript(W.Svc, Pex, out_path: relative);

        Assert.StartsWith("error:", r);
        Assert.Contains("absolute", r);
        Assert.False(Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), relative)));
    }

    [Fact]
    public void ARefusalCarryingAnIgnoredPatchStillOpensWithErrorAndStatesTheIgnoredLane()
    {
        // The note is appended, never prepended: a caller (and every refusal test on this surface) reads the first
        // word, and "note:" in front of an error hides that the call failed.
        var dest = FreshDir();
        try
        {
            var r = DecompileTools.DecompileScript(
                W.Svc, Path.Combine(W.ScriptsDir, "HcSpNoSuchScript.pex"), patch: "HcIgnoredPatch", out_path: dest);

            Assert.StartsWith("error:", r);
            Assert.Contains("patch=/into= were ignored", r);
            Assert.Contains("nothing was written", r);
        }
        finally { try { Directory.Delete(dest, true); } catch { /* temp cleanup */ } }
    }

    [Fact]
    public void OutPathRunsWithNoInstanceConfiguredAndSaysTheHierarchyIsTheBaseline()
    {
        var dir = FreshDir();
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, "psc");
        try
        {
            using var unconfigured = LoadOrderService.WithInstance(null, 0, new UserConfigStore(Path.Combine(dir, "user.json")));

            var r = DecompileTools.DecompileScript(unconfigured, Pex, out_path: dest);

            Assert.True(File.Exists(Path.Combine(dest, ScriptsWorld.BaseScript + ".psc")), r);
            Assert.Contains("no MO2 instance is configured", r);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* temp cleanup */ } }
    }

    [Fact]
    public void AConfiguredInstanceThatDoesNotResolveStillSaysTheHierarchyIsTheBaselineOnly()
    {
        // Configured is not the same as usable: the instance folder is gone, so the mods-tree top-up cannot run.
        // The degraded hierarchy is stated off what the build reports, not off whether an instance was named.
        var dir = FreshDir();
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, "psc");
        try
        {
            using var broken = LoadOrderService.WithInstance(
                Path.Combine(dir, "no-such-instance"), 0, new UserConfigStore(Path.Combine(dir, "user.json")));

            var r = DecompileTools.DecompileScript(broken, Pex, out_path: dest);

            Assert.True(File.Exists(Path.Combine(dest, ScriptsWorld.BaseScript + ".psc")), r);
            Assert.Contains("the mods-tree sources were not read", r);
            Assert.Contains("does not resolve", r);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* temp cleanup */ } }
    }

    [Fact]
    public void WithoutOutPathNoInstanceConfiguredStillAsksForTheInstance()
    {
        var dir = FreshDir();
        Directory.CreateDirectory(dir);
        try
        {
            using var unconfigured = LoadOrderService.WithInstance(null, 0, new UserConfigStore(Path.Combine(dir, "user.json")));

            var r = DecompileTools.DecompileScript(unconfigured, Pex);

            Assert.Contains("Mod Organizer 2 instance", r);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* temp cleanup */ } }
    }

    [Theory]
    [InlineData("..\\HcEscapedByDots")]
    [InlineData("C:\\HcEscapedByRoot")]
    public void AnObjectNameThatWouldLeaveTheOutputFolderIsRefusedAndNothingIsWritten(string objectName)
    {
        // The object name becomes the .psc's filename, and Path.Combine drops the output folder for a rooted name
        // and honours a "..", so such a name is refused before any file is created.
        var dir = FreshDir();
        var src = Path.Combine(dir, "src");
        var dest = Path.Combine(dir, "psc");
        Directory.CreateDirectory(src);
        var pex = Path.Combine(src, "HcEscaping.pex");
        PexWriter.WritePex(pex, objectName, null);
        // Where the write would land without the check: the drive root for the rooted name, the temp root
        // for the dotted one. Asserting on it is what makes the rooted case pin its own escape — a folder
        // scan under dir cannot see a file at C:\.
        var escaped = Path.Combine(dest, objectName + ".psc");
        try
        {
            var r = DecompileTools.DecompileScript(W.Svc, pex, out_path: dest);

            Assert.StartsWith("error:", r);
            Assert.Contains(objectName, r);
            Assert.Contains(dest, r);
            Assert.False(File.Exists(escaped), escaped);
            Assert.Empty(Directory.Exists(dest) ? Directory.GetFiles(dest, "*.psc") : []);
        }
        finally
        {
            // The escape target first: it can sit outside dir, and deleting dir would not reach it.
            try { File.Delete(escaped); } catch { /* temp cleanup */ }
            try { Directory.Delete(dir, true); } catch { /* temp cleanup */ }
        }
    }

    [Fact]
    public void AnEscapingNameLaterInTheFileStopsTheCallBeforeTheFirstPscIsWritten()
    {
        // The names are all read before the first write, so a bad one at the end of a multi-object .pex
        // refuses with nothing on disk rather than after its neighbours have been written.
        var dir = FreshDir();
        var src = Path.Combine(dir, "src");
        var dest = Path.Combine(dir, "psc");
        Directory.CreateDirectory(src);
        var pex = Path.Combine(src, "HcTwoObjects.pex");
        PexWriter.WriteMultiObjectPex(pex, "HcFirstObject", "..\\HcSecondEscapes");
        try
        {
            var r = DecompileTools.DecompileScript(W.Svc, pex, out_path: dest);

            Assert.StartsWith("error:", r);
            Assert.Contains("Nothing was written", r);
            Assert.Empty(Directory.GetFiles(dir, "*.psc", SearchOption.AllDirectories));
        }
        finally { try { Directory.Delete(dir, true); } catch { /* temp cleanup */ } }
    }

    [Fact]
    public void OutPathSupersedesIntoAndTheResponseSaysSo()
    {
        var dest = FreshDir();
        var before = Directory.GetDirectories(W.ModsDir).OrderBy(d => d, StringComparer.Ordinal).ToArray();
        try
        {
            var r = DecompileTools.DecompileScript(W.Svc, Pex, into: W.PluginName, out_path: dest);

            Assert.Contains("patch=/into= are ignored", r);
            Assert.True(File.Exists(Path.Combine(dest, ScriptsWorld.BaseScript + ".psc")), r);
            Assert.Equal(before, Directory.GetDirectories(W.ModsDir).OrderBy(d => d, StringComparer.Ordinal).ToArray());
        }
        finally { try { Directory.Delete(dest, true); } catch { /* temp cleanup */ } }
    }

    [Fact]
    public void AnUnreadableSiblingPexIsNamedAndTheHierarchySaysWhatItIsInstead()
    {
        // The third hierarchy source used to degrade silently: a sibling .pex Mutagen cannot read cost edges and
        // nothing in the result said so, leaving explicit casts with no reason. It is now named like the other two.
        var dir = FreshDir();
        var src = Path.Combine(dir, "src");
        var dest = Path.Combine(dir, "psc");
        Directory.CreateDirectory(src);
        var pex = Path.Combine(src, "HcSiblingHost.pex");
        PexWriter.WritePex(pex, "HcSiblingHost", parent: null);
        // Not a .pex at all: Mutagen throws on it, which is the unreadable class this note covers.
        File.WriteAllBytes(Path.Combine(src, "HcSiblingBroken.pex"), new byte[] { 0x01, 0x02, 0x03, 0x04 });
        try
        {
            var r = DecompileTools.DecompileScript(W.Svc, pex, out_path: dest);

            Assert.True(File.Exists(Path.Combine(dest, "HcSiblingHost.psc")), r);
            Assert.Contains("the .pex files beside this one were not all read", r);
            Assert.Contains("1 of 2 .pex file(s)", r);
            Assert.Contains(src, r);
            // The "is" half drops the siblings and keeps this .pex, so the note never claims edges it does not have.
            Assert.Contains("the class hierarchy is", r);
            Assert.Contains("what this .pex declares", r);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* temp cleanup */ } }
    }
}

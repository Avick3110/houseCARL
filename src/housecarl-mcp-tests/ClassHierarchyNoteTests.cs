using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The decompiler's class-parent map where the edges and the rendered note can both be seen: a file the walk
/// could not read costs that file's edges and nothing else, and the note credits the sources that WERE read.
/// Contracts in <c>docs/architecture/papyrus.md</c>.</summary>
[Trait("tier", "unit")]
public sealed class ClassHierarchyNoteTests
{
    static string FreshDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "hc-class-parents-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    static Dictionary<string, string> Empty() => new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void OneUnreadablePexDoesNotStopTheSiblingsAroundIt()
    {
        // One readable sibling sorts before the broken file and one after, so a walk that gives up at the first
        // failure loses an edge whichever order the folder lists in.
        var dir = FreshDir();
        PexWriter.WritePex(Path.Combine(dir, "HcAlphaChild.pex"), "HcAlphaChild", parent: "HcWalkHost");
        PexWriter.WritePex(Path.Combine(dir, "HcZetaChild.pex"), "HcZetaChild", parent: "HcWalkHost");
        File.WriteAllBytes(Path.Combine(dir, "HcMiddleBroken.pex"), new byte[] { 0x01, 0x02, 0x03, 0x04 });
        try
        {
            var edges = Empty();
            var scan = PapyrusClassParents.AddFromPexFolder(edges, dir);

            Assert.Equal(1, scan.FilesFailed);
            Assert.Equal(3, scan.FilesSeen);
            Assert.Equal("HcWalkHost", edges["HcAlphaChild"]);
            Assert.Equal("HcWalkHost", edges["HcZetaChild"]);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* temp cleanup */ } }
    }

    [Fact]
    public void OneUnreadablePscDoesNotStopTheFilesAroundIt()
    {
        // The same rule on the mods-tree source: a locked .psc costs its own edge, not the walk.
        var dir = FreshDir();
        File.WriteAllText(Path.Combine(dir, "HcAlphaScript.psc"), "ScriptName HcAlphaScript extends HcTreeHost\n");
        File.WriteAllText(Path.Combine(dir, "HcZetaScript.psc"), "ScriptName HcZetaScript extends HcTreeHost\n");
        var locked = Path.Combine(dir, "HcMiddleScript.psc");
        File.WriteAllText(locked, "ScriptName HcMiddleScript extends HcTreeHost\n");
        try
        {
            using var held = HeldOpen.Hold(locked);
            var edges = Empty();

            var scan = PapyrusClassParents.AddFromPscHeaders(edges, new[] { dir });

            Assert.Equal(1, scan.FilesFailed);
            Assert.Equal(3, scan.FilesSeen);
            Assert.Equal("HcTreeHost", edges["HcAlphaScript"]);
            Assert.Equal("HcTreeHost", edges["HcZetaScript"]);
            Assert.False(edges.ContainsKey("HcMiddleScript"));
        }
        finally { try { Directory.Delete(dir, true); } catch { /* temp cleanup */ } }
    }

    [Fact]
    public void APartialModsTreeReadStillCreditsTheSourcesItRead()
    {
        // TopUpMissing carries a partial .psc failure as well as a tree that was not read at all, so the note says
        // "not all read" and keeps the mods-tree sources in what the hierarchy IS.
        var h = new ClassParents(Empty(), null, "1 of 200 .psc file(s) under 'C:\\mods' could not be read");

        var s = DecompileTools.HierarchySentence(h);

        Assert.Contains("the mods-tree sources were not all read", s);
        Assert.Contains("the MO2 mods-tree sources that could be read", s);
    }

    [Fact]
    public void APartialSiblingReadStillCreditsTheSiblingsItRead()
    {
        var h = new ClassParents(Empty(), null, null, "1 of 2 sibling .pex file(s) in 'C:\\mods' could not be read");

        var s = DecompileTools.HierarchySentence(h);

        Assert.Contains("the .pex files beside this one were not all read", s);
        Assert.Contains("the sibling .pex files that could be read", s);
        // The sources that lost nothing are still claimed whole.
        Assert.Contains("the shipped vanilla baseline plus the MO2 mods-tree sources", s);
    }

    [Fact]
    public void AHierarchyWithNothingMissingSaysNothing()
    {
        Assert.Equal("", DecompileTools.HierarchySentence(new ClassParents(Empty(), null, null)));
    }
}

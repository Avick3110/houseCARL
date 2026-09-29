using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// <c>LoadOrderService.WriteSeq</c> over a synthetic MO2 instance (migrated from the <c>seq-write-guard</c> probe):
/// where the .seq lands on each lane, the no-op for a plugin with no start-game-enabled quest, and the refusals.
/// Every call here has a configured instance, so none depends on whether the config prompt is checked before or
/// after the source is located.
/// </summary>
[Trait("tier", "integration")]
public sealed class SeqWriteServiceTests : IDisposable
{
    readonly SeqWriteWorld W = new();
    public void Dispose() => W.Dispose();

    // Probe SERVICE-WRITE: "WriteSeq lands SEQ\<plugin>.seq with the SGE quest".
    [Fact]
    public void TheSeqLandsInSeqWithTheQuestsBytes()
    {
        var o = W.Svc.WriteSeq(W.SvcPlugin, null, null);
        var expected = Path.Combine(W.OwnedFolder, "SEQ", "HcSeqSvc.seq");
        Assert.True(o.Success, o.Error);
        Assert.Equal(expected, o.SeqPath, ignoreCase: true);
        Assert.Single(o.Quests);
        Assert.Equal(4, new FileInfo(expected).Length);
    }

    // Probe SAME-FOLDER: ".seq defaults into the plugin's OWN houseCARL folder".
    [Fact]
    public void APluginInItsOwnHouseCarlFolderGetsTheSeqThere()
    {
        var o = W.Svc.WriteSeq(W.SvcPlugin, null, null);
        Assert.True(o.WroteIntoPluginFolder);
        Assert.True(SeqWriteWorld.PathUnder(o.SeqPath, W.OwnedFolder), o.SeqPath);
    }

    // Probe EMPTY-NOOP: "no SGE quests → nothing written, no folder cut".
    [Fact]
    public void APluginWithNoSgeQuestWritesNothingAndCutsNoFolder()
    {
        int before = Directory.GetDirectories(W.Mods).Length;
        var o = W.Svc.WriteSeq(W.EmptyPlugin, null, null);
        Assert.True(o.Success, o.Error);
        Assert.Null(o.SeqPath);
        Assert.Empty(o.Quests);
        Assert.Equal(before, Directory.GetDirectories(W.Mods).Length);
    }

    // Probe REFUSE-NOFILE: "missing plugin path refused + named, both spellings offered".
    [Fact]
    public void AMissingPathIsRefusedOfferingBothSpellings()
    {
        var o = W.Svc.WriteSeq(Path.Combine(W.Root, "does-not-exist.esp"), null, null);
        Assert.False(o.Success);
        Assert.Contains("no file at path", o.Error);
        Assert.Contains("FILENAME", o.Error);
        Assert.Contains("ABSOLUTE", o.Error);
    }

    // Probe REFUSE-UNFINDABLE: "unlocatable filename refused + named".
    [Fact]
    public void AFilenameNothingProvidesIsRefusedByName()
    {
        var o = W.Svc.WriteSeq("HcSeqNoSuchPlugin.esp", null, null);
        Assert.False(o.Success);
        Assert.Contains("HcSeqNoSuchPlugin.esp", o.Error);
    }

    // Probe FILENAME-LANE: "source= by filename resolves to the same file and states its arm".
    [Fact]
    public void AFilenameResolvesToTheSameFileAndStatesWhereItWasRead()
    {
        var o = W.Svc.WriteSeq(Path.GetFileName(W.SvcPlugin), null, null);
        Assert.True(o.Success, o.Error);
        Assert.Equal(Path.GetFullPath(W.SvcPlugin), o.PluginPath, ignoreCase: true);
        Assert.False(string.IsNullOrEmpty(o.ResolvedFrom));
    }

    // Probe OUTPUT-DIR: ".seq lands in <out_path>\SEQ".
    [Fact]
    public void OutPathPutsTheSeqInItsSeqFolder()
    {
        var o = W.WriteToUserMod();
        Assert.True(o.Success, o.Error);
        Assert.Equal(W.UserSeq, o.SeqPath, ignoreCase: true);
        Assert.True(File.Exists(W.UserSeq));
        Assert.True(o.UserChoseOutput);
        Assert.False(o.WroteIntoPluginFolder);
    }

    // Probe OUTPUT-DIR: "no houseCARL folder cut, no ownership marker stamped". The plugin is a copy outside any
    // houseCARL folder, so without out_path= this call would cut a fresh one.
    [Fact]
    public void OutPathCutsNoFolderAndStampsNoMarker()
    {
        var loose = Path.Combine(W.Root, "loose", "HcSeqSvc.esp");
        Directory.CreateDirectory(Path.GetDirectoryName(loose)!);
        File.Copy(W.SvcPlugin, loose);
        int before = Directory.GetDirectories(W.Mods).Length;
        Assert.True(W.Svc.WriteSeq(loose, null, null, W.UserMod).Success);
        Assert.Equal(before, Directory.GetDirectories(W.Mods).Length);
        Assert.False(File.Exists(Path.Combine(W.UserMod, "meta.ini")));
    }

    // Probe OUTPUT-DIR-DEPLOYS: "the .seq is IN the named folder AND <mods>\<mod>\SEQ carries no deploy warning".
    [Fact]
    public void OutPathToAModFolderDeploysWithNoWarning()
    {
        var o = W.WriteToUserMod();
        Assert.True(SeqWriteWorld.PathUnder(o.SeqPath, W.UserMod), o.SeqPath);
        Assert.Null(o.DeployWarning);
    }

    // Probe OUTPUT-DIR-OUTSIDE: "written but WARNED (never a clean done for a .seq the game won't read)".
    [Fact]
    public void OutPathOffTheModTreeIsWrittenAndWarned()
    {
        var offTree = Path.Combine(W.Root, "elsewhere", "NotAMod");
        var o = W.Svc.WriteSeq(W.SvcPlugin, null, null, offTree);
        Assert.True(o.Success, o.Error);
        Assert.True(File.Exists(Path.Combine(offTree, "SEQ", "HcSeqSvc.seq")));
        Assert.NotNull(o.DeployWarning);
    }

    // Probe USER-OWNED-SURVIVES: "residue cleanup never deletes an out_path folder (CreatedFresh=false; the folder survives)".
    [Fact]
    public void ResidueCleanupLeavesAnOutPathFolderStanding()
    {
        var rf = W.Svc.ResolveExplicitSeqFolder(W.UserMod, out _);
        Assert.False(rf.CreatedFresh);
        Assert.Null(W.Svc.RemoveOrNameRiderResidue(rf));
        Assert.True(Directory.Exists(rf.OutputDir));
    }

    // Probe WRITE-FAIL-FOLDER: "a failed out_path write names the folder it leaves behind, and leaves it".
    [Fact]
    public void AFailedOutPathWriteNamesTheFolderItLeavesAndLeavesIt()
    {
        var blocked = Path.Combine(W.Root, "blocked");
        Directory.CreateDirectory(Path.Combine(blocked, "SEQ", "HcSeqSvc.seq"));   // a folder where the file goes
        var o = W.Svc.WriteSeq(W.SvcPlugin, null, null, blocked);
        Assert.False(o.Success);
        Assert.Contains("could not write", o.Error);
        Assert.Contains("never removes a folder you named", o.Error);
        Assert.True(Directory.Exists(Path.Combine(blocked, "SEQ")));
    }
}

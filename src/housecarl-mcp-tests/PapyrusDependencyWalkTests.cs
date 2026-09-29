using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// <c>PapyrusDependencyFilter.Relevant</c>, the reference walk that narrows the modlist's source folders (migrated from the
/// <c>compile-ergonomics-guard</c> probe, part H): direct and transitive references, first-provider precedence, the
/// unreferenced drop, the unreadable target, and cycles.
/// </summary>
[Trait("tier", "unit")]
public sealed class PapyrusDependencyWalkTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-pydeps-tests-" + Guid.NewGuid().ToString("N"));
    readonly string _own, _framework, _deep, _unrelated, _target;

    // own references Framework, which references Deep; Unrelated is named by nobody.
    public PapyrusDependencyWalkTests()
    {
        _own = Folder("own", ("Target", "Scriptname Target extends Quest\n\nFunction Go()\n    Framework.Ping()\nEndFunction\n"));
        _framework = Folder("framework", ("Framework", "Scriptname Framework\n\nDeep Function Get() global\n    return None\nEndFunction\n"));
        _deep = Folder("deep", ("Deep", "Scriptname Deep\n"));
        _unrelated = Folder("unrelated", ("Unrelated", "Scriptname Unrelated\n"));
        _target = Path.Combine(_own, "Target.psc");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* non-fatal */ }
    }

    string Folder(string name, params (string File, string Body)[] scripts)
    {
        var d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        foreach (var (f, b) in scripts) File.WriteAllText(Path.Combine(d, f + ".psc"), b);
        return d;
    }

    PapyrusDependencyScan Walk() => PapyrusDependencyFilter.Relevant(_target, new[] { _own }, new[] { _framework, _deep, _unrelated });

    // Probe H: "a folder the target names DIRECTLY is kept".
    [Fact]
    public void AFolderTheTargetNamesIsKept() => Assert.Contains(_framework, Walk().Folders);

    // Probe H: "a folder reached only THROUGH that dependency is kept — the walk is transitive".
    [Fact]
    public void AFolderReachedThroughADependencyIsKept() => Assert.Contains(_deep, Walk().Folders);

    // Probe H: "a folder nothing references is DROPPED".
    [Fact]
    public void AFolderNothingReferencesIsDropped() => Assert.DoesNotContain(_unrelated, Walk().Folders);

    // Probe H: "kept folders come back in the CANDIDATE order (MO2 precedence), not discovery order".
    [Fact]
    public void KeptFoldersComeBackInCandidateOrder()
    {
        var scan = PapyrusDependencyFilter.Relevant(_target, new[] { _own }, new[] { _deep, _framework, _unrelated });
        Assert.Equal(new[] { _deep, _framework }, scan.Folders);
    }

    // Probe H: "a SEED folder is indexed but never returned (the caller adds it unconditionally)".
    [Fact]
    public void ASeedFolderIsNeverReturned() => Assert.DoesNotContain(_own, Walk().Folders);

    // Probe H: "the disclosed numbers are real: 4 indexed, 4 read (seed, Target, Framework, Deep)".
    [Fact]
    public void TheDisclosedNumbersAreReal()
    {
        var scan = Walk();
        Assert.Equal((4, 4, false), (scan.Indexed, scan.FilesRead, scan.BudgetExhausted));
    }

    // Probe H: "a name provided twice resolves to the HIGHER-precedence folder only — the loser's copy never joins the path".
    [Fact]
    public void ANameProvidedTwiceResolvesToTheFirstProvider()
    {
        var winner = Folder("winner", ("Shared", "Scriptname Shared\n"));
        var loser = Folder("loser", ("Shared", "Scriptname Shared\n"));
        var shOwn = Folder("shOwn", ("ShTarget", "Scriptname ShTarget\n\nShared s\n"));
        var scan = PapyrusDependencyFilter.Relevant(Path.Combine(shOwn, "ShTarget.psc"), new[] { shOwn }, new[] { winner, loser });
        Assert.Equal(new[] { winner }, scan.Folders);
    }

    // Probe H: "a name appearing only in a comment still earns its folder (deliberate over-inclusion)".
    [Fact]
    public void ANameInACommentStillEarnsItsFolder()
    {
        var cmtOwn = Folder("cmtOwn", ("CmtTarget", "Scriptname CmtTarget\n; TODO: wire up Framework later\n"));
        var scan = PapyrusDependencyFilter.Relevant(Path.Combine(cmtOwn, "CmtTarget.psc"), new[] { cmtOwn }, new[] { _framework, _deep, _unrelated });
        Assert.Contains(_framework, scan.Folders);
    }

    // Probe H: "an unreadable target returns an empty set, never a throw" and "…and it is FLAGGED unreadable".
    [Fact]
    public void AnUnreadableTargetIsEmptyAndFlagged()
    {
        var scan = PapyrusDependencyFilter.Relevant(Path.Combine(_root, "no-such.psc"), new[] { _own }, new[] { _framework, _deep, _unrelated });
        Assert.Empty(scan.Folders);
        Assert.True(scan.TargetUnreadable);
    }

    // Probe H: "…while a real walk is not flagged (no false alarm)".
    [Fact]
    public void ARealWalkIsNotFlaggedUnreadable() => Assert.False(Walk().TargetUnreadable);

    // Probe H: "a reference CYCLE terminates and keeps both folders".
    [Fact]
    public void AReferenceCycleTerminatesAndKeepsBothFolders()
    {
        var cycA = Folder("cycA", ("CycA", "Scriptname CycA\n\nCycB b\n"));
        var cycB = Folder("cycB", ("CycB", "Scriptname CycB\n\nCycA a\n"));
        var cycOwn = Folder("cycOwn", ("CycTarget", "Scriptname CycTarget\n\nCycA a\n"));
        var scan = PapyrusDependencyFilter.Relevant(Path.Combine(cycOwn, "CycTarget.psc"), new[] { cycOwn }, new[] { cycA, cycB });
        Assert.Equal(new[] { cycA, cycB }, scan.Folders);
    }
}

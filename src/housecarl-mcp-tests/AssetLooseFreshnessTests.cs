using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Freshness of the loose layer after the per-build watch set replaced the per-subtree stamp array (#828).
/// The three things that can happen to a warmed subtree — a file appearing in a root that had nothing there, a file
/// vanishing, a file's bytes changing — must each still be seen on the next call. Driven straight at
/// <see cref="AssetResolver"/>, because what is under test is the build's own freshness check.</summary>
[Trait("tier", "integration")]
public sealed class AssetLooseFreshnessTests : IDisposable
{
    const string Subtree = @"meshes\hcfresh";
    const string Provided = Subtree + @"\a.nif";
    const string Newcomer = "NewcomerMod";
    const string Provider = "ProviderMod";

    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-fresh-" + Guid.NewGuid().ToString("N"));
    readonly string _mods;

    public AssetLooseFreshnessTests()
    {
        _mods = Path.Combine(_root, "mods");
        // The newcomer has a mod folder and nothing else: its copy of the subtree is the absent case, the one whose
        // freshness now rides an ancestor's stamp instead of the subtree dir's own.
        Directory.CreateDirectory(Path.Combine(_mods, Newcomer));
        Directory.CreateDirectory(Path.Combine(_mods, Provider, Subtree));
        File.WriteAllText(Path.Combine(_mods, Provider, Provided), "first");
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ } }

    /// <summary>Newcomer first, so it WINS when it provides a file: a resolution that still names the provider would
    /// pass a weaker assert.</summary>
    AssetResolver Build() =>
        AssetResolver.Build(overwriteDir: "", _mods, dataDir: "", new[] { Newcomer, Provider }, Array.Empty<ActiveArchive>());

    static string? Winner(AssetResolver r, string rel)
    {
        var hit = r.Resolve(rel);
        return hit.Exists ? hit.Winner!.Source : null;
    }

    [Fact]
    public void AFileAppearingInARootThatHadNothingIsSeenOnTheNextCall()
    {
        using var r = Build();
        Assert.Equal(Provider, Winner(r, Provided));          // warms the subtree across both roots

        Directory.CreateDirectory(Path.Combine(_mods, Newcomer, Subtree));
        File.WriteAllText(Path.Combine(_mods, Newcomer, Provided), "newcomer");

        Assert.True(r.RefreshIfStale(), "the appearing file left the build stale and it was not noticed");
        Assert.Equal(Newcomer, Winner(r, Provided));          // the newcomer now wins the same path
    }

    [Fact]
    public void AFileVanishingFromAWarmedSubtreeIsSeenOnTheNextCall()
    {
        using var r = Build();
        Assert.Equal(Provider, Winner(r, Provided));

        File.Delete(Path.Combine(_mods, Provider, Provided));

        Assert.True(r.RefreshIfStale(), "the vanished file left the build stale and it was not noticed");
        Assert.Null(Winner(r, Provided));
    }

    /// <summary>The same two changes with NO time between them and the baseline, run enough times that some rounds
    /// certainly fall inside one timestamp tick: a Windows directory's last-write is coarser than a write next to a
    /// delete, so a check made of stamps passes this about half the time. Names do not have a granularity.</summary>
    [Fact]
    public void AppearAndVanishAreSeenEvenInsideOneTimestampTick()
    {
        var newcomerDir = Path.Combine(_mods, Newcomer, Subtree);
        var newcomerFile = Path.Combine(_mods, Newcomer, Provided);
        for (int round = 0; round < 50; round++)
        {
            using var r = Build();
            Assert.Equal(Provider, Winner(r, Provided));      // warms both roots, taking the baseline right here

            Directory.CreateDirectory(newcomerDir);
            File.WriteAllText(newcomerFile, "newcomer");      // the appearance, in the same tick as the baseline
            Assert.True(r.RefreshIfStale(), $"round {round}: the appearing file was not noticed");
            Assert.Equal(Newcomer, Winner(r, Provided));      // re-warms, taking a new baseline right here

            File.Delete(newcomerFile);                        // the vanish, in the same tick as THAT baseline
            Assert.True(r.RefreshIfStale(), $"round {round}: the vanished file was not noticed");
            Assert.Equal(Provider, Winner(r, Provided));

            Directory.Delete(newcomerDir);
        }
    }

    /// <summary>What the check must NOT wake up for: the ancestor a root with nothing there is answered by is its own
    /// mod folder, and a session writes to mod folders all the time. Only the name the subtree needs counts, so an
    /// unrelated file appearing beside it leaves the build alone.</summary>
    [Fact]
    public void AnUnrelatedFileAppearingInAModFolderDoesNotDiscardTheBuild()
    {
        using var r = Build();
        Winner(r, Provided);
        Winner(r, @"meshes\hcgone\x.nif");                     // the newcomer root is answered by its own mod folder

        File.WriteAllText(Path.Combine(_mods, Newcomer, "meta.ini"), "[General]\r\n");
        File.WriteAllText(Path.Combine(_mods, Provider, "notes.txt"), "x");

        Assert.False(r.RefreshIfStale(), "an unrelated file in a mod folder threw the whole build away");
    }

    /// <summary>A file that goes and comes BACK between calls. The build memoizes a directory's listing the first time
    /// anything asks about it, and that memo can be many calls older than the warm that takes a baseline from it — so a
    /// baseline must be a fresh listing. With the memo as the baseline the fresh listing equals it and the file stays
    /// invisible for the life of the build.</summary>
    [Fact]
    public void AFileDeletedAndPutBackBetweenCallsIsSeen()
    {
        var file = Path.Combine(_mods, Provider, Provided);

        using var r = Build();
        Assert.Null(Winner(r, Subtree + @"\deep\y.nif"));      // memoizes the provider's listing of the subtree dir
        Assert.False(r.RefreshIfStale());

        File.Delete(file);                                     // not a forbidden name, so nothing is stale yet
        Assert.False(r.RefreshIfStale());
        Assert.Null(Winner(r, Provided));                      // correct at this instant: nothing provides it

        File.WriteAllText(file, "back");

        Assert.True(r.RefreshIfStale(), "the file that came back was measured against a baseline older than the warm");
        Assert.Equal(Provider, Winner(r, Provided));
    }

    /// <summary>The same root cause one level up: a sweep memoizes an ancestor's listing, the subtree dir is deleted,
    /// and a warm that trusts the memo sees a listed name that no longer stats — a real absence with nothing watched,
    /// so the subtree coming back is never seen.</summary>
    [Fact]
    public void ASubtreeDeletedAfterASweepMemoizedItsParentIsSeenComingBack()
    {
        var subtreeDir = Path.Combine(_mods, Provider, Subtree);

        using var r = Build();
        r.EnumerateUnder(@"meshes\hcnothing");                 // memoizes the provider's meshes\ listing, watching nothing

        Directory.Delete(subtreeDir, true);
        Assert.Null(Winner(r, Provided));                      // the warm here must still put something under watch

        Directory.CreateDirectory(subtreeDir);
        File.WriteAllText(Path.Combine(_mods, Provider, Provided), "back");

        Assert.True(r.RefreshIfStale(), "the subtree that came back was answered from a memoized parent listing");
        Assert.Equal(Provider, Winner(r, Provided));
    }

    /// <summary>A DIRECTORY inside a warmed subtree is not an asset. The warm reads names and attributes off one pass
    /// and keeps only the files; a subfolder reported as an existing loose path with providers would be the
    /// silently-wrong answer, so the split is asserted rather than left to an expression.</summary>
    [Fact]
    public void ADirectoryInsideAWarmedSubtreeIsNotReportedAsAnAsset()
    {
        Directory.CreateDirectory(Path.Combine(_mods, Provider, Subtree, "subdir"));
        File.WriteAllText(Path.Combine(_mods, Provider, Subtree, "subdir", "inner.nif"), "x");

        using var r = Build();
        Assert.Equal(Provider, Winner(r, Provided));           // the file beside it resolves
        Assert.Null(Winner(r, Subtree + @"\subdir"));          // the folder does not
        Assert.Equal(Provider, Winner(r, Subtree + @"\subdir\inner.nif"));   // and its own contents still do
    }

    /// <summary>The watch baseline and the check that compares against it are two enumerations, so they have to list
    /// the same set: a hidden, system-flagged file — `desktop.ini` is the one a real order grows — is listed by both,
    /// and a build that skipped it on one side would call itself stale on every call.</summary>
    [Fact]
    public void AHiddenSystemFileInAWarmedSubtreeDoesNotMakeEveryCallStale()
    {
        var hidden = Path.Combine(_mods, Provider, Subtree, "desktop.ini");
        File.WriteAllText(hidden, "[.ShellClassInfo]\r\n");
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);

        using var r = Build();
        Assert.Equal(Provider, Winner(r, Provided));

        Assert.False(r.RefreshIfStale(), "the baseline and the check disagreed about a hidden system entry");
        Assert.False(r.RefreshIfStale(), "…and again: a disagreement rebuilds the build on every call");
    }

    /// <summary>A loose file's BYTES are never cached — the read goes to the resolved path — so a rewrite is seen with
    /// nothing to invalidate. The assert stands on the read the tools actually make.</summary>
    [Fact]
    public void AFilesChangedBytesAreSeenOnTheNextRead()
    {
        using var r = Build();
        Assert.Equal(Provider, Winner(r, Provided));

        File.WriteAllText(Path.Combine(_mods, Provider, Provided), "second and longer");

        r.RefreshIfStale();
        var source = Assert.Single(r.ResolveForPlacement(Provided).Sources);
        var (bytes, error) = AssetResolver.ReadPlacementSource(source);
        Assert.Null(error);
        Assert.Equal("second and longer", System.Text.Encoding.UTF8.GetString(bytes!));
    }

    /// <summary>What the check costs grows with the DIRECTORIES that answer, not with roots times subtrees, and this
    /// is the best case of that: subtrees whose roots all answer from ONE shared ancestor — here each root's own mod
    /// folder, because neither has a `meshes\` at all — add no watched directory however many are warmed. The two
    /// sibling tests below stage the cases that do add one: roots whose answering ancestor differs per subtree, and
    /// roots that provide the subtree. None of the three has to be read off another.</summary>
    [Fact]
    public void WarmingMoreSubtreesAnsweredByOneSharedAncestorAddsNoWatchedDirectory()
    {
        using var r = Build();
        Winner(r, Provided);
        Winner(r, @"meshes\hcgonefirst\x.nif");                // pays the one ancestor each root answers from
        var afterFirst = r.WatchedDirectoryCount;

        for (int i = 0; i < 20; i++) Winner(r, $@"meshes\hcgone{i}\x.nif");

        Assert.Equal(afterFirst, r.WatchedDirectoryCount);
        Assert.False(r.RefreshIfStale(), "nothing changed on disk, so the build must not have been called stale");
    }

    /// <summary>The case the shared-ancestor one does not cover: the subtree is absent in every root, but each root
    /// answers from its OWN `meshes\g{i}` rather than from its mod folder, so twenty subtrees add one watched
    /// directory per root per subtree. Growth with the directories that answer, in the shape that does not collapse.</summary>
    [Fact]
    public void WarmingSubtreesWithADifferentAnsweringAncestorEachAddsOnePerRoot()
    {
        for (int i = 0; i < 20; i++)
            foreach (var mod in new[] { Newcomer, Provider })
            {
                // Each root HAS meshes\g{i} (with a file, so the dir is real) but not the deeper subtree asked for,
                // which makes meshes\g{i} the deepest ancestor that answers — a different one per subtree.
                Directory.CreateDirectory(Path.Combine(_mods, mod, "meshes", "g" + i));
                File.WriteAllText(Path.Combine(_mods, mod, "meshes", "g" + i, "sibling.nif"), "x");
            }

        using var r = Build();
        Winner(r, @"meshes\g0\deep\a.nif");
        var afterFirst = r.WatchedDirectoryCount;

        for (int i = 1; i < 20; i++) Winner(r, $@"meshes\g{i}\deep\a.nif");

        Assert.Equal(afterFirst + 38, r.WatchedDirectoryCount);      // 19 further subtrees x 2 roots
        Assert.False(r.RefreshIfStale(), "nothing changed on disk, so the build must not have been called stale");
    }

    /// <summary>The third shape: a subtree a root PROVIDES is watched by that directory's own listing, so the watch
    /// set grows by one per providing root per subtree.</summary>
    [Fact]
    public void WarmingASubtreeARootProvidesAddsOneWatchedDirectoryPerProvidingRoot()
    {
        for (int i = 0; i < 20; i++)
        {
            foreach (var mod in new[] { Newcomer, Provider })
            {
                Directory.CreateDirectory(Path.Combine(_mods, mod, "meshes", "have" + i));
                File.WriteAllText(Path.Combine(_mods, mod, "meshes", "have" + i, "a.nif"), "x");
            }
        }

        using var r = Build();
        Winner(r, Provided);
        var afterFirst = r.WatchedDirectoryCount;

        for (int i = 0; i < 20; i++) Winner(r, $@"meshes\have{i}\a.nif");

        Assert.Equal(afterFirst + 40, r.WatchedDirectoryCount);      // 20 subtrees x 2 roots that have them
        Assert.False(r.RefreshIfStale(), "nothing changed on disk, so the build must not have been called stale");
    }

    /// <summary>A root where a plain FILE sits where the subtree wanted a directory: a real absence, so it must be
    /// watched as one. The change to catch is the name turning into the directory, which is how a mod that shipped a
    /// stray file called `meshes` starts providing meshes.</summary>
    [Fact]
    public void ASubtreeAppearingWhereAFileHeldTheNameIsSeenOnTheNextCall()
    {
        var held = Path.Combine(_mods, Newcomer, "meshes");
        File.WriteAllText(held, "a file, not a folder");

        using var r = Build();
        Assert.Equal(Provider, Winner(r, Provided));           // the newcomer cannot provide anything under a file

        File.Delete(held);
        Directory.CreateDirectory(Path.Combine(_mods, Newcomer, Subtree));
        File.WriteAllText(Path.Combine(_mods, Newcomer, Provided), "newcomer");

        Assert.True(r.RefreshIfStale(), "the directory that replaced the file was not noticed");
        Assert.Equal(Newcomer, Winner(r, Provided));
    }

    /// <summary>The Data-ROOT subtree is the one case where a mod folder's whole listing IS what resolution reads, so
    /// there it is watched whole and a top-level file does discard the build. Pinned so the narrower claim above is
    /// read as what it is: true until a path with no directory component is asked about.</summary>
    [Fact]
    public void AskingForARootLevelPathPutsTheModFoldersUnderAWholeListingWatch()
    {
        File.WriteAllText(Path.Combine(_mods, Provider, "top.txt"), "x");

        using var r = Build();
        Assert.Equal(Provider, Winner(r, "top.txt"));          // subtree "" — the mod folder itself

        File.WriteAllText(Path.Combine(_mods, Newcomer, "meta.ini"), "[General]\r\n");

        Assert.True(r.RefreshIfStale(), "a root-level path is resolved from the mod folder's own listing");
    }

    /// <summary>Warming a subtree a root does not have settles that absence with two stats on the missing name — not
    /// a directory, not a file — rather than a fresh listing of the ancestor per root per warm (#861). The absence
    /// memo still lists an ancestor the first time it is asked; what must not happen is an uncached ancestor listing
    /// per warm. The absence is still watched: the name appearing afterwards is seen on the next call.</summary>
    [Fact]
    public void WarmingAnAbsentSubtreeTakesNoFreshAncestorListingAndStillSeesItAppear()
    {
        using var r = Build();
        Assert.Equal(Provider, Winner(r, Provided));
        var before = r.FreshListingCount;

        for (int i = 0; i < 20; i++) Assert.Null(Winner(r, $@"meshes\hcgone{i}\x.nif"));

        Assert.Equal(before, r.FreshListingCount);             // an ancestor listing per root per subtree would be 40

        Directory.CreateDirectory(Path.Combine(_mods, Provider, "meshes", "hcgone7"));
        File.WriteAllText(Path.Combine(_mods, Provider, "meshes", "hcgone7", "x.nif"), "x");

        Assert.True(r.RefreshIfStale(), "the subtree established absent by stats appeared and was not noticed");
        Assert.Equal(Provider, Winner(r, @"meshes\hcgone7\x.nif"));
    }

    /// <summary>The one case two stats cannot settle: a mod folder that stats but will not list, so its subtree does
    /// not stat either and the absence cannot be proved. That root is named as a failure and nothing is watched for
    /// it; watching the missing name there anyway would find the folder unlistable on every check and rebuild the
    /// build on every call.</summary>
    [Fact]
    public void AModFolderThatWillNotListDoesNotMakeEveryCallStale()
    {
        var blocked = Path.Combine(_mods, "BlockedMod");
        Directory.CreateDirectory(Path.Combine(blocked, Subtree));
        File.WriteAllText(Path.Combine(blocked, Provided), "x");
        Assert.True(DenyAce.TryDeny(blocked), UnreadableRootWorld.NotStaged);
        try
        {
            using var r = AssetResolver.Build(overwriteDir: "", _mods, dataDir: "",
                new[] { "BlockedMod", Newcomer, Provider }, Array.Empty<ActiveArchive>());
            Assert.Equal(Provider, Winner(r, Provided));
            Assert.Contains(r.RootFailures, f => f.StartsWith("BlockedMod"));   // named, not proved absent

            Assert.False(r.RefreshIfStale(), "a root that will not list was watched as if it were absent");
            Assert.Equal(Provider, Winner(r, Provided));      // a call re-warms, so the next check reads a new watch
            Assert.False(r.RefreshIfStale(), "…and again: that watch would rebuild the build on every call");
        }
        finally { DenyAce.Undeny(blocked); }
    }

    /// <summary>The fallback for an absence the memo could not prove: a folder the build found unlistable and that has
    /// since been given back. The memo still says "would not list", so a new subtree under it is not proved absent;
    /// the warm lists the ancestor fresh instead, finds the name missing and watches it — so the subtree appearing
    /// there later is seen, rather than hidden behind a failure the build named when the folder was still denied.</summary>
    [Fact]
    public void AnAbsenceUnderAFolderGivenBackAfterItWouldNotListIsStillWatched()
    {
        var blocked = Path.Combine(_mods, "BlockedMod");
        Directory.CreateDirectory(Path.Combine(blocked, "meshes"));
        Assert.True(DenyAce.TryDeny(blocked), UnreadableRootWorld.NotStaged);
        AssetResolver r;
        try
        {
            r = AssetResolver.Build(overwriteDir: "", _mods, dataDir: "",
                new[] { "BlockedMod", Newcomer, Provider }, Array.Empty<ActiveArchive>());
            Assert.Equal(Provider, Winner(r, Provided));
            Assert.Contains(r.RootFailures, f => f.StartsWith("BlockedMod"));   // the memo now holds it as unlistable
        }
        finally { DenyAce.Undeny(blocked); }

        using (r)
        {
            Assert.Null(Winner(r, @"meshes\hclater\y.nif"));  // not provable off the memo; the fresh walk stops at meshes
            Directory.CreateDirectory(Path.Combine(blocked, "meshes", "hclater"));
            File.WriteAllText(Path.Combine(blocked, "meshes", "hclater", "y.nif"), "y");

            Assert.True(r.RefreshIfStale(), "a subtree appearing under the folder given back was not noticed");
            Assert.Equal("BlockedMod", Winner(r, @"meshes\hclater\y.nif"));
        }
    }
}

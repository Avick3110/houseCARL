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

    /// <summary>The point of the change: what the check costs must not grow with the subtrees a session has touched.
    /// Twenty more warmed subtrees add no watched directory at all, because every root's copy of them is absent and
    /// answers from the root dir that the first subtree already put under watch.</summary>
    [Fact]
    public void WarmingMoreSubtreesAddsNoWatchedDirectory()
    {
        using var r = Build();
        Winner(r, Provided);
        Winner(r, @"meshes\hcgonefirst\x.nif");                // pays the one ancestor each root answers from
        var afterFirst = r.WatchedDirectoryCount;

        for (int i = 0; i < 20; i++) Winner(r, $@"meshes\hcgone{i}\x.nif");

        Assert.Equal(afterFirst, r.WatchedDirectoryCount);
        Assert.False(r.RefreshIfStale(), "nothing changed on disk, so the build must not have been called stale");
    }
}

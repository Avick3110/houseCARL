using HousecarlCore;
using Xunit;
using static HousecarlMcpTests.ResolverArchives;

namespace HousecarlMcpTests;

/// <summary>The VFS asset resolver under MO2 precedence: overwrite, then mods by priority, then Data, loose over
/// archive, archives by plugin rank; path normalisation; the loose cache and archive mtime refresh; one build per
/// capture; a duplicate archive binding read once; zero archive handles at rest; an unreadable archive named.</summary>
[Trait("tier", "unit")]
public sealed class AssetResolverTests : IDisposable
{
    const string Rel = @"meshes\actors\character\facegendata\facegeom\Skyrim.esm\000918E2.nif";
    static readonly string[] Enabled = { "HighMod", "LowMod" };   // highest priority first

    readonly string _root, _overwrite, _mods, _data, _high, _low;

    public AssetResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-asset-resolver-" + Guid.NewGuid().ToString("N"));
        _overwrite = Path.Combine(_root, "overwrite");
        _mods = Path.Combine(_root, "mods");
        _data = Path.Combine(_root, "Data");
        _high = Path.Combine(_mods, "HighMod");
        _low = Path.Combine(_mods, "LowMod");
        foreach (var d in new[] { _overwrite, _high, _low, _data }) Directory.CreateDirectory(d);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* temp scratch */ } }

    AssetResolver Build(params ActiveArchive[] archives) => AssetResolver.Build(_overwrite, _mods, _data, Enabled, archives);

    /// <summary>Rel in Data, LowMod and HighMod, and optionally in overwrite.</summary>
    void LooseStack(bool withOverwrite)
    {
        Loose(_data, Rel); Loose(_low, Rel); Loose(_high, Rel);
        if (withOverwrite) Loose(_overwrite, Rel);
    }

    string Archive(string name, byte[] bytes) => Write(_root, name, bytes);

    // Probe: "overwrite wins the full loose stack", "all 4 loose providers listed IN PRECEDENCE ORDER" and
    // "contention flagged ambiguous".
    [Fact]
    public void OverwriteWinsAndEveryLooseProviderIsListedInPrecedenceOrder()
    {
        LooseStack(withOverwrite: true);
        using var r = Build();

        var hit = r.Resolve(Rel);

        Assert.True(hit.Exists);
        Assert.Equal(AssetKind.Loose, hit.Winner!.Kind);
        Assert.Equal(new[] { "overwrite", "HighMod", "LowMod", "Data" }, hit.Providers.Select(p => p.Source));
        Assert.True(hit.Ambiguous);
    }

    // Probe: "the higher-priority mod wins among loose".
    [Fact]
    public void WithoutOverwriteTheHigherPriorityModWins()
    {
        LooseStack(withOverwrite: false);
        using var r = Build();

        Assert.Equal("HighMod", r.Resolve(Rel).Winner?.Source);
    }

    // Probe: "a single-source loose asset is healthy — Exists, one provider, not ambiguous".
    [Fact]
    public void ASingleSourceAssetIsOneProviderAndNotAmbiguous()
    {
        Loose(_data, @"meshes\solo\unique.nif");
        using var r = Build();

        var hit = r.Resolve(@"meshes\solo\unique.nif");

        Assert.True(hit.Exists);
        Assert.Equal("Data", Assert.Single(hit.Providers).Source);
        Assert.False(hit.Ambiguous);
    }

    // Probe: "an absent asset → Exists=false, no winner, not ambiguous".
    [Fact]
    public void AnUnprovidedPathIsAbsentWithNoWinner()
    {
        LooseStack(withOverwrite: true);
        using var r = Build();

        var miss = r.Resolve(@"meshes\does\not\exist.nif");

        Assert.False(miss.Exists);
        Assert.Null(miss.Winner);
        Assert.False(miss.Ambiguous);
    }

    // Probe: "fresh path absent before it's written", "still absent from the warmed cache before a refresh",
    // "RefreshIfStale sees the new loose dir" and "after the refresh the new loose file resolves".
    [Fact]
    public void ALooseFileAddedToAWarmedFolderIsSeenOnlyAfterARefresh()
    {
        const string fresh = @"meshes\foo\fresh.nif";
        using var r = Build();
        Assert.False(r.Resolve(fresh).Exists);

        Loose(_high, fresh);

        Assert.False(r.Resolve(fresh).Exists);
        Assert.True(r.RefreshIfStale());
        Assert.Equal("HighMod", r.Resolve(fresh).Winner?.Source);
    }

    // Probe: "ResolveMany matches per-path Resolve (one pinned build)", "RefreshIfStale is false when nothing changed",
    // "the result is unchanged across the no-op refresh" and "a captured view resolves + reports its (empty) failure list".
    [Fact]
    public void ResolveManyAndACapturedViewAnswerFromOneBuild()
    {
        LooseStack(withOverwrite: false);
        using var r = Build();
        var paths = new[] { Rel, @"meshes\does\not\exist.nif", Rel.ToUpperInvariant() };

        var many = r.ResolveMany(paths);

        Assert.Equal(paths.Select(p => (r.Resolve(p).Exists, r.Resolve(p).Winner?.Source)),
            many.Select(h => (h.Exists, h.Winner?.Source)));
        Assert.Equal("HighMod", many[2].Winner?.Source);
        Assert.False(r.RefreshIfStale());
        Assert.False(r.RefreshIfStale());
        Assert.Equal("HighMod", r.Resolve(Rel).Winner?.Source);
        var view = r.Capture();
        Assert.Equal("HighMod", view.Resolve(Rel).Winner?.Source);
        Assert.Empty(view.BsaFailures);
        Assert.False(view.ReadIncomplete);
    }

    // Strengthened from the probe's capture arm, which changed nothing on disk between capture and read: a view keeps
    // answering from its own build after the resolver rebuilds under it.
    [Fact]
    public void ACapturedViewKeepsItsBuildAcrossARebuild()
    {
        const string fresh = @"meshes\later\added.nif";
        LooseStack(withOverwrite: false);
        using var r = Build();
        Assert.False(r.Resolve(fresh).Exists);
        var view = r.Capture();

        Loose(_high, fresh);
        Assert.True(r.RefreshIfStale());

        Assert.True(r.Resolve(fresh).Exists);
        Assert.False(view.Resolve(fresh).Exists);
    }

    // Probe: "slash/leading-sep/case forms resolve to the same winner".
    [Theory]
    [InlineData(@"meshes/actors/character/facegendata/facegeom/Skyrim.esm/000918E2.nif")]
    [InlineData(@"\meshes\actors\character\facegendata\facegeom\Skyrim.esm\000918E2.nif")]
    [InlineData(@"MESHES\ACTORS\CHARACTER\FACEGENDATA\FACEGEOM\SKYRIM.ESM\000918E2.NIF")]
    public void PathFormsResolveToTheSameWinner(string asked)
    {
        LooseStack(withOverwrite: false);
        using var r = Build();

        Assert.Equal("HighMod", r.Resolve(asked).Winner?.Source);
    }

    // Probe: "a drive-rooted query path is rejected loud" and "a parent-escaping ('..') query path is rejected loud".
    [Theory]
    [InlineData(@"C:\Windows\evil.nif")]
    [InlineData(@"..\..\escape.nif")]
    public void ADriveRootedOrEscapingPathIsRejected(string asked)
    {
        using var r = Build();

        Assert.Throws<ArgumentException>(() => r.Resolve(asked));
    }

    // Probe: "a path-duplicate archive is read once — 1 failure(s) (expected 1)".
    [Fact]
    public void AnUnreadableArchiveBoundTwiceIsReadOnce()
    {
        var dup = Path.Combine(_root, "Shared.bsa");   // never created
        using var r = Build(new ActiveArchive(dup, "PluginX.esp", PluginRank: 1), new ActiveArchive(dup, "PluginY.esp", PluginRank: 2));

        Assert.Single(r.BsaFailures);
    }

    // Probe: "native Mutagen read: no archive-read failures" and "a BSA-packed asset resolves via the native reader".
    [Fact]
    public void AnArchivedAssetResolvesThroughTheNativeReader()
    {
        using var r = Build(new ActiveArchive(Archive("FixtureA.bsa", A()), "PluginA.esp", 1),
                            new ActiveArchive(Archive("FixtureB.bsa", B()), "PluginB.esp", 2));

        Assert.Empty(r.BsaFailures);
        var hit = r.Resolve(FacegenRel);
        Assert.True(hit.Exists);
        Assert.Equal(AssetKind.Bsa, hit.Winner!.Kind);
        Assert.Equal("FixtureA.bsa", hit.Winner.Source, ignoreCase: true);
    }

    // Probe: "a loose copy beats the BSA copy" and "…and the BSA copy is still listed as a provider, flagged ambiguous".
    [Fact]
    public void ALooseCopyBeatsTheArchiveCopyWhichStaysListed()
    {
        using var r = Build(new ActiveArchive(Archive("FixtureA.bsa", A()), "PluginA.esp", 1));
        Assert.Equal(AssetKind.Bsa, r.Resolve(FacegenRel).Winner?.Kind);

        Loose(_high, FacegenRel, "loose");
        r.RefreshIfStale();
        var beat = r.Resolve(FacegenRel);

        Assert.Equal("HighMod", beat.Winner?.Source);
        Assert.Equal(AssetKind.Loose, beat.Winner?.Kind);
        Assert.Contains(beat.Providers, p => p.Kind == AssetKind.Bsa);
        Assert.True(beat.Ambiguous);
    }

    // Probe: "among BSAs the higher plugin-rank wins". Strengthened: the higher rank is bound first, so a
    // first-bound-wins resolver fails it.
    [Fact]
    public void AmongArchivesTheHigherPluginRankWins()
    {
        using var r = Build(new ActiveArchive(Archive("FixtureB.bsa", B()), "PluginB.esp", 2),
                            new ActiveArchive(Archive("FixtureA.bsa", A()), "PluginA.esp", 1));

        Assert.Equal("FixtureB.bsa", r.Resolve(RankRel).Winner?.Source, ignoreCase: true);
    }

    // Probe: "a path-duplicate READABLE archive lists ONE provider, not ambiguous".
    [Fact]
    public void AReadableArchiveBoundTwiceIsOneProvider()
    {
        var a = Archive("FixtureA.bsa", A());
        using var r = Build(new ActiveArchive(a, "PluginA.esp", 1), new ActiveArchive(a, "PluginA2.esp", 2));

        var hit = r.Resolve(RankRel);

        Assert.True(hit.Exists);
        Assert.Equal(AssetKind.Bsa, Assert.Single(hit.Providers).Kind);
        Assert.False(hit.Ambiguous);
    }

    // Probe: "at-rest fixture: the BSA table was read", "the .bsa is RENAMABLE while the resolver is alive" and
    // "…and DELETABLE".
    [Fact]
    public void TheResolverHoldsNoArchiveHandleAtRest()
    {
        var atRest = Archive("AtRest.bsa", A());
        using var r = Build(new ActiveArchive(atRest, "P.esp", 1));
        Assert.Equal(AssetKind.Bsa, r.Resolve(FacegenRel).Winner?.Kind);

        File.Move(atRest, atRest + ".ren");
        File.Move(atRest + ".ren", atRest);
        File.Delete(atRest);

        Assert.False(File.Exists(atRest));
    }

    // Probe: "an unreadable BSA is ONE named failure (file + owning plugin)", "ReadIncomplete flags the build" and
    // "a good archive still resolves alongside the unreadable one".
    [Fact]
    public void AnUnreadableArchiveIsOneNamedFailureAndAGoodOneStillResolves()
    {
        using var r = Build(new ActiveArchive(Archive("FixtureA.bsa", A()), "GoodPlugin.esp", 1),
                            new ActiveArchive(Archive("Garbage.bsa", Truncated()), "BadPlugin.esp", 2));

        var failure = Assert.Single(r.BsaFailures);
        Assert.Contains("Garbage.bsa", failure);
        Assert.Contains("BadPlugin.esp", failure);
        Assert.True(r.ReadIncomplete);
        Assert.Equal(AssetKind.Bsa, r.Resolve(FacegenRel).Winner?.Kind);
    }

    // Probe (the BSArch-only arm 8, now self-contained): "the to-be-added path is absent before the repack",
    // "RefreshIfStale() reports the repacked archive as stale" and "the newly-packed path resolves after the refresh".
    [Fact]
    public void ARepackedArchiveIsPickedUpByARefresh()
    {
        const string added = @"meshes\added\after.nif";
        var a = Archive("ArchiveA.bsa", A());
        using var r = Build(new ActiveArchive(a, "P.esp", 1));
        Assert.False(r.Resolve(added).Exists);

        File.WriteAllBytes(a, AWith(added));
        File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddMinutes(5));

        Assert.True(r.RefreshIfStale());
        Assert.Equal(AssetKind.Bsa, r.Resolve(added).Winner?.Kind);
    }
}

using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The directory / glob SELECT form on asset_status (#246): one call over a folder answers for every file the
/// VFS provides beneath it, loose and archive both, with the winner and provider chain per file — and says in a
/// structured line what it left out.</summary>
[Trait("tier", "unit")]
public sealed class AssetSelectTests : IClassFixture<AssetSelectWorld>
{
    readonly AssetSelectWorld _w;
    public AssetSelectTests(AssetSelectWorld w) => _w = w;

    static string[] Paths(AssetStatusData d) => d.Results.Select(r => r.RelPath).ToArray();

    static string Leaf(string p) => Path.GetFileName(p);

    /// <summary>The #246 measure: one call per defining master over its facegeom folder answers for the whole set —
    /// the same files, the same winners, as spelling every path out. Forty path-list calls collapse to one.</summary>
    [Fact]
    public void OneCallOverAMastersFacegenFolderAnswersForEveryPathAPathListWouldHaveSpelled()
    {
        var spelled = new[] { "0001.nif", "0002.nif", "0003.nif", "0004.nif", "0005.nif" }.Select(_w.Rel).ToArray();

        var swept = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { AssetSelectWorld.FaceGeomDir });
        var listed = _w.Svc.AssetStatus(spelled);

        Assert.Equal(AssetSelectWorld.FaceGeomFiles, swept.Selected);
        Assert.Equal(spelled.OrderBy(p => p, StringComparer.OrdinalIgnoreCase),
                     Paths(swept).OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        Assert.All(swept.Results, r => Assert.True(r.Hit!.Exists));
        // Winners agree file for file with the explicit-path answer.
        Assert.Equal(listed.Results.Select(r => r.Hit!.Winner!.Source).OrderBy(s => s),
                     swept.Results.Select(r => r.Hit!.Winner!.Source).OrderBy(s => s));
    }

    /// <summary>The sweep is VFS-scoped: a file only a BSA carries is in the set, and a contested file still reports
    /// the priority winner with its whole provider chain.</summary>
    [Fact]
    public void ASweepUnionsLooseAndArchiveProvidersAndStillCallsTheWinnerPerFile()
    {
        var d = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { AssetSelectWorld.FaceGeomDir });

        var bsaOnly = d.Results.Single(r => Leaf(r.RelPath) == "0005.nif");
        Assert.Equal(AssetKind.Bsa, bsaOnly.Hit!.Winner!.Kind);
        Assert.Equal("HcArch.bsa", bsaOnly.Hit.Winner.Source);

        var contested = d.Results.Single(r => Leaf(r.RelPath) == "0002.nif");
        Assert.Equal("FaceHigher", contested.Hit!.Winner!.Source);
        Assert.Equal(new[] { "FaceHigher", "FaceBase" }, contested.Hit.Providers.Select(p => p.Source).ToArray());
    }

    /// <summary>A trailing separator is how a modder spells a folder, and the two spellings must answer alike.</summary>
    [Fact]
    public void ADirectorySelectorAnswersTheSameWithOrWithoutATrailingSeparator()
    {
        var bare = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { AssetSelectWorld.FaceGeomDir });
        var slashed = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { AssetSelectWorld.FaceGeomDir + "/" });

        Assert.Equal(Paths(bare), Paths(slashed));
    }

    /// <summary>A glob narrows within the subtree: '*' stays inside one segment, '**' crosses separators.</summary>
    [Fact]
    public void AGlobNarrowsTheSweepToTheFilesItMatches()
    {
        var nifs = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { AssetSelectWorld.FaceGeomDir + @"\000?.nif" });
        Assert.Equal(AssetSelectWorld.FaceGeomFiles, nifs.Selected);

        var one = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { AssetSelectWorld.FaceGeomDir + @"\0004.*" });
        Assert.Equal("0004.nif", Leaf(Assert.Single(one.Results).RelPath));

        // '**' crosses separators, so one selector reaches every facegen mesh from the actors root.
        var deep = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { @"meshes\actors\**\*.nif" });
        Assert.Equal(AssetSelectWorld.FaceGeomFiles, deep.Selected);

        // '*' does NOT cross a separator, so the same shape one level up matches nothing.
        var shallow = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { @"meshes\actors\*.nif" });
        Assert.Empty(shallow.Results);
    }

    /// <summary>'**' spans zero segments as well as many, so a sweep written with it does not quietly skip the files
    /// sitting directly in the anchor folder.</summary>
    [Fact]
    public void ADoubleStarMatchesTheAnchorFolderItselfAndNotOnlyItsSubfolders()
    {
        var deep = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { AssetSelectWorld.FaceGeomDir + @"\**\*.nif" });

        Assert.Equal(AssetSelectWorld.FaceGeomFiles, deep.Selected);
    }

    /// <summary>A glob with no directory in front of it would enumerate every loose file and every archive entry in
    /// the order before rendering anything, so it is refused instead of paid.</summary>
    [Fact]
    public void AnUnanchoredGlobIsRefusedRatherThanSweepingTheWholeLoadOrder()
    {
        var d = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { @"**\*.nif" });

        Assert.Empty(d.Results);
        Assert.Contains(d.SelectorNotes!, n => n.Contains("anchored under a directory", StringComparison.Ordinal));
    }

    /// <summary>Explicit paths and a directory compose in one call: the paths keep their place and their order, and the
    /// sweep does not repeat one it already named.</summary>
    [Fact]
    public void ExplicitPathsAndADirectoryComposeWithoutDuplicatingAPath()
    {
        var d = _w.Svc.AssetStatus(new[] { _w.Rel("0003.nif") }, new[] { AssetSelectWorld.FaceGeomDir });

        Assert.Equal(AssetSelectWorld.FaceGeomFiles, d.Selected);
        Assert.Equal(_w.Rel("0003.nif"), d.Results[0].RelPath);
        Assert.Single(d.Results.Where(r => Leaf(r.RelPath) == "0003.nif"));
    }

    /// <summary>A selector that matches nothing says so. Read as a silent no-op it looks exactly like a clean sweep,
    /// which is how a typo'd folder under-counts.</summary>
    [Fact]
    public void ASelectorThatMatchesNothingSaysSoRatherThanPassingAsAnEmptySweep()
    {
        var d = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { @"meshes\actors\character\facegendata\facegeom\Typo.esm" });

        Assert.Empty(d.Results);
        Assert.Contains(d.SelectorNotes!, n => n.Contains("matched no file", StringComparison.Ordinal));
        Assert.Contains("notes=1", AssetWire.Render(d, 80_000));
    }

    /// <summary>The escape guard covers the selector too — a drive-rooted or '..'-escaping directory is refused by
    /// name, never enumerated outside the load order.</summary>
    [Fact]
    public void ADriveRootedOrEscapingSelectorIsRefusedByName()
    {
        var d = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { @"C:\Windows", @"meshes\..\..\secrets" });

        Assert.Empty(d.Results);
        Assert.Equal(2, d.SelectorNotes!.Count);
        Assert.Contains(d.SelectorNotes, n => n.Contains("drive-rooted", StringComparison.Ordinal));
        Assert.Contains(d.SelectorNotes, n => n.Contains("parent-escaping", StringComparison.Ordinal));
    }

    /// <summary>A selector that normalizes to nothing — "/", "\", "//" — names the Data root, and enumerating it
    /// sweeps every loose file and every archive table in the order. It is refused for the same reason an unanchored
    /// glob is, in one plain sentence.</summary>
    [Fact]
    public void ASelectorThatNamesNoDirectoryIsRefusedTheSameWayAnUnanchoredGlobIs()
    {
        foreach (var root in new[] { "/", @"\", "//" })
        {
            var d = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { root });

            Assert.Empty(d.Results);
            var note = Assert.Single(d.SelectorNotes!);
            Assert.Contains("anchored under a directory", note, StringComparison.Ordinal);
        }
    }

    /// <summary>"./meshes" and "meshes" are the same folder. Left in, a "." segment survives into the loose walk but
    /// never matches a BSA table entry, so the archive-only files drop out of the sweep without a word.</summary>
    [Fact]
    public void ADotSegmentSelectsTheSameSetAsThePlainSpellingIncludingArchiveOnlyFiles()
    {
        var plain = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { "meshes" });
        var dotted = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { "./meshes" });

        Assert.Equal(AssetSelectWorld.FaceGeomFiles, plain.Selected);
        Assert.Equal(Paths(plain), Paths(dotted));
        // The BSA-only file is in both, not just the loose lane's answer.
        Assert.Contains(Paths(dotted), p => Leaf(p) == "0005.nif");
        Assert.Empty(dotted.SelectorNotes ?? Array.Empty<string>());
    }

    /// <summary>A refusal reads as one plain sentence — no .NET exception furniture trailing off the end of it.</summary>
    [Fact]
    public void ARefusedSelectorReadsAsOnePlainSentenceWithNoParameterSuffix()
    {
        var d = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { @"**\*.nif", @"C:\Windows", @"meshes\..\secrets" });

        var text = AssetWire.Render(d, 80_000);
        Assert.DoesNotContain("(Parameter", text, StringComparison.Ordinal);
        Assert.All(d.SelectorNotes!, n => Assert.DoesNotContain("(Parameter", n, StringComparison.Ordinal));
    }

    /// <summary>The window pages, and the accounting says how much of the selection it left behind and where the next
    /// page starts.</summary>
    [Fact]
    public void TheSweepPagesAndTheAccountingNamesWhatThePagingLeftOut()
    {
        var page = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { AssetSelectWorld.FaceGeomDir }, limit: 2, offset: 1);

        Assert.Equal(AssetSelectWorld.FaceGeomFiles, page.Selected);
        Assert.Equal(2, page.Results.Count);

        var text = AssetWire.Render(page, 80_000);
        Assert.Contains("[accounting] total=5 rendered=2 capped=3 truncated=0 offset=1 remaining=2", text);
        Assert.Contains("offset=3 for the next page", text);

        // The pages tile the selection: page 2 continues where page 1 stopped.
        var whole = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { AssetSelectWorld.FaceGeomDir });
        Assert.Equal(Paths(whole).Skip(1).Take(2), Paths(page));
    }

    /// <summary>The secondary half of #246: a render cut by max_chars is counted in the structured accounting line,
    /// which is written after the cut and so can never itself be truncated away.</summary>
    [Fact]
    public void AMaxCharsCutIsCountedInTheAccountingLineAndNotOnlyInProse()
    {
        var d = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { AssetSelectWorld.FaceGeomDir });

        var text = AssetWire.Render(d, 200);

        Assert.Contains("[accounting] total=5 rendered=", text);
        Assert.Matches(@"truncated=[1-9]", text);
        Assert.Contains("max_chars cut", text);
    }

    /// <summary>The last page says it is the last, and an offset past the end says THAT rather than pointing back at
    /// the offset it was just called with — a caller following the line's own advice must not loop.</summary>
    [Fact]
    public void TheLastPageOffersNoNextPageAndAnOffsetPastTheEndSaysSo()
    {
        var last = AssetWire.Render(
            _w.Svc.AssetStatus(Array.Empty<string>(), new[] { AssetSelectWorld.FaceGeomDir }, limit: 2, offset: 3), 80_000);
        Assert.Contains("remaining=0", last);
        Assert.DoesNotContain("for the next page", last);

        var past = AssetWire.Render(
            _w.Svc.AssetStatus(Array.Empty<string>(), new[] { AssetSelectWorld.FaceGeomDir }, limit: 2, offset: 9), 80_000);
        Assert.Contains("offset=9 is past the end of the selection (5 path(s))", past);
        Assert.DoesNotContain("for the next page", past);
    }

    /// <summary>A negative window is refused by name rather than reinterpreted as "no limit".</summary>
    [Fact]
    public void ANegativeLimitOrOffsetIsRefusedByName()
    {
        var text = AssetTools.AssetStatus(_w.Svc, new[] { _w.Rel("0001.nif") }, limit: -1);

        Assert.Contains("neither can be negative", text);
    }

    /// <summary>An unpaged, uncut explicit-path call reports itself whole — the accounting is on every response, not
    /// only the ones that lost something.</summary>
    [Fact]
    public void AnOrdinaryPathListStillCarriesTheAccountingLine()
    {
        var text = AssetTools.AssetStatus(_w.Svc, new[] { _w.Rel("0001.nif") });

        Assert.Contains("[accounting] total=1 rendered=1 capped=0 truncated=0 offset=0 remaining=0 notes=0", text);
    }

    /// <summary>Both select forms empty is a refusal that names both, since either one alone is a legal call.</summary>
    [Fact]
    public void ACallThatSelectsNothingRefusesNamingBothSelectForms()
    {
        var text = AssetTools.AssetStatus(_w.Svc, Array.Empty<string>());

        Assert.Contains("empty", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("under", text, StringComparison.Ordinal);
    }
}

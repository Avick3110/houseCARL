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

    /// <summary>The window pages, and the accounting says how much of the selection it left behind and where the next
    /// page starts.</summary>
    [Fact]
    public void TheSweepPagesAndTheAccountingNamesWhatThePagingLeftOut()
    {
        var page = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { AssetSelectWorld.FaceGeomDir }, limit: 2, offset: 1);

        Assert.Equal(AssetSelectWorld.FaceGeomFiles, page.Selected);
        Assert.Equal(2, page.Results.Count);

        var text = AssetWire.Render(page, 80_000);
        Assert.Contains("[accounting] total=5 rendered=2 capped=3 truncated=0 offset=1", text);
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

    /// <summary>An unpaged, uncut explicit-path call reports itself whole — the accounting is on every response, not
    /// only the ones that lost something.</summary>
    [Fact]
    public void AnOrdinaryPathListStillCarriesTheAccountingLine()
    {
        var text = AssetTools.AssetStatus(_w.Svc, new[] { _w.Rel("0001.nif") });

        Assert.Contains("[accounting] total=1 rendered=1 capped=0 truncated=0 offset=0 notes=0", text);
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

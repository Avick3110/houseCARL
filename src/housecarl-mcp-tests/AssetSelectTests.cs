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

        // '**' crosses separators, so one selector reaches every facegen mesh from the actors root — both masters'.
        var deep = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { @"meshes\actors\**\*.nif" });
        Assert.Equal(AssetSelectWorld.AllFaceGeomNifs, deep.Selected);

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

    /// <summary>A wildcard-free selector that names an existing FILE is answered as that one path. Enumerating a file
    /// finds nothing beneath it, and saying "nothing provides that folder" would contradict what asset_paths= answers
    /// for the very same string — which is what a modder pasting a path into under= would read.</summary>
    [Fact]
    public void AWildcardFreeSelectorThatNamesAFileIsAnsweredAsThatFile()
    {
        var rel = _w.Rel("0001.nif");

        var d = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { rel });

        var r = Assert.Single(d.Results);
        Assert.Equal(rel, r.RelPath);
        Assert.True(r.Hit!.Exists);
        // The same string through asset_paths= agrees, winner and all.
        Assert.Equal(_w.Svc.AssetStatus(new[] { rel }).Results[0].Hit!.Winner!.Source, r.Hit.Winner!.Source);
        // And the note says what under= did with it, rather than claiming nothing provides the folder.
        Assert.Contains(d.SelectorNotes!, n => n.Contains("names a file", StringComparison.Ordinal));
        Assert.DoesNotContain(d.SelectorNotes!, n => n.Contains("matched no file", StringComparison.Ordinal));
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
        // skipped= is what offset stepped over, capped= what limit left behind: two different causes, counted apart.
        Assert.Contains("[accounting] total=5 rendered=2 skipped=1 capped=2 truncated=0 offset=1 remaining=2", text);
        Assert.Contains("limit=2 offset=3 for the next page", text);

        // The pages tile the selection: page 2 continues where page 1 stopped.
        var whole = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { AssetSelectWorld.FaceGeomDir });
        Assert.Equal(Paths(whole).Skip(1).Take(2), Paths(page));
    }

    /// <summary>The next-page advice names limit= as well as offset=. Without it a caller following the line calls
    /// back with limit=0 and resolves the whole remainder on every page — the paging is only cheap if the advice
    /// keeps it paged.</summary>
    [Fact]
    public void TheNextPageAdviceCarriesTheLimitSoFollowingItStaysPaged()
    {
        var paged = AssetWire.Render(
            _w.Svc.AssetStatus(Array.Empty<string>(), new[] { AssetSelectWorld.FaceGeomDir }, limit: 2), 80_000);
        Assert.Contains("re-call with limit=2 offset=2 for the next page", paged);

        // No limit passed: the advice still names one, so the follow-up call is a page and not the whole remainder.
        var unlimited = AssetWire.Render(
            _w.Svc.AssetStatus(Array.Empty<string>(), new[] { AssetSelectWorld.FaceGeomDir }), 1_500);
        Assert.Matches(@"re-call with limit=[1-9]\d* offset=\d+ for the next page", unlimited);
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

    /// <summary>And it is the sentence the WINDOW owns, not a second copy of it: asset_status and housecarl_skse
    /// refuse the same input class, so a reword of one cannot leave the other answering the old wording.</summary>
    [Fact]
    public void TheNegativeWindowRefusalIsTheOneTheWindowItselfSpells()
    {
        Assert.Equal(new RowWindow(0, -1).Error,
                     AssetTools.AssetStatus(_w.Svc, new[] { _w.Rel("0001.nif") }, limit: -1));
        Assert.Equal(new RowWindow(-2, 0).Error,
                     AssetTools.AssetStatus(_w.Svc, new[] { _w.Rel("0001.nif") }, offset: -2));
    }

    /// <summary>An unpaged, uncut explicit-path call reports itself whole — the accounting is on every response, not
    /// only the ones that lost something.</summary>
    [Fact]
    public void AnOrdinaryPathListStillCarriesTheAccountingLine()
    {
        var text = AssetTools.AssetStatus(_w.Svc, new[] { _w.Rel("0001.nif") });

        Assert.Contains("[accounting] total=1 rendered=1 skipped=0 capped=0 truncated=0 offset=0 remaining=0 notes=0", text);
    }

    /// <summary>A caller that walks the sweep by the accounting line's OWN advice sees every path exactly once. The
    /// next offset has to count what was RENDERED, not what was resolved: under a max_chars cut the two differ, and
    /// counting the resolved window steps the caller straight over the paths the cap never showed it.</summary>
    [Fact]
    public void WalkingBySuccessiveNextPageOffsetsRendersEveryPathExactlyOnce()
    {
        var all = Paths(_w.Svc.AssetStatus(Array.Empty<string>(), new[] { AssetSelectWorld.FaceGeomDir }));
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in all) seen[p] = 0;

        // limit=3 resolves three paths a page; max_chars=4000 renders fewer than three of them — wide enough to
        // get one path onto the page, which is what a next-page offset is measured off.
        int offset = 0;
        for (int page = 0; page < 20; page++)
        {
            var text = AssetWire.Render(
                _w.Svc.AssetStatus(Array.Empty<string>(), new[] { AssetSelectWorld.FaceGeomDir }, limit: 3, offset: offset), 4_000);
            foreach (var p in all) if (text.Contains(p, StringComparison.Ordinal)) seen[p]++;

            var next = System.Text.RegularExpressions.Regex.Match(text, @"re-call with limit=\d+ offset=(\d+)");
            if (!next.Success) break;
            var advanced = int.Parse(next.Groups[1].Value);
            Assert.True(advanced > offset, $"the advice must move forward, got offset={advanced} from offset={offset}");
            offset = advanced;
        }

        Assert.All(all, p => Assert.Equal(1, seen[p]));
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

        Assert.Equal(AssetSelectWorld.AllFaceGeomNifs, plain.Selected);
        Assert.Equal(Paths(plain), Paths(dotted));
        // The BSA-only file is in both, not just the loose lane's answer.
        Assert.Contains(Paths(dotted), p => Leaf(p) == "0005.nif");
        Assert.Empty(dotted.SelectorNotes ?? Array.Empty<string>());
    }

    /// <summary>One answer spells one folder one way. The loose walk echoes back the caller's own casing and the
    /// archive tables their author's, so an upper-cased selector used to return loose rows and BSA rows in two
    /// different spellings of the same directory.</summary>
    [Fact]
    public void AnUpperCasedSelectorAnswersInOneSpellingAcrossLooseAndArchiveRows()
    {
        var shouted = AssetSelectWorld.FaceGeomDir.ToUpperInvariant();

        var d = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { shouted });

        Assert.Equal(AssetSelectWorld.FaceGeomFiles, d.Selected);
        Assert.Contains(Paths(d), p => Leaf(p) == "0005.nif");        // the archive-only file is in the sweep
        Assert.All(Paths(d), p => Assert.StartsWith(shouted + "\\", p, StringComparison.Ordinal));
        // Typing does not change WHICH files come back, only how the prefix is spelt.
        Assert.Equal(Paths(_w.Svc.AssetStatus(Array.Empty<string>(), new[] { AssetSelectWorld.FaceGeomDir }))
                        .Select(Leaf).OrderBy(p => p),
                     Paths(d).Select(Leaf).OrderBy(p => p));
    }

    /// <summary>A refusal reads as one plain sentence — no ".NET" exception furniture trailing off the end of it.</summary>
    [Fact]
    public void ARefusedSelectorReadsAsOnePlainSentenceWithNoParameterSuffix()
    {
        var d = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { @"**\*.nif", @"C:\Windows", @"meshes\..\secrets" });

        var text = AssetWire.Render(d, 80_000);
        Assert.DoesNotContain("(Parameter", text, StringComparison.Ordinal);
        Assert.All(d.SelectorNotes!, n => Assert.DoesNotContain("(Parameter", n, StringComparison.Ordinal));
    }

    /// <summary>The accounting block is priced INSIDE max_chars: room for its widest spelling is held back before the
    /// paths render, rather than appended past the cap once they have used all of it.</summary>
    [Fact]
    public void TheAccountingBlockIsPricedInsideMaxCharsRatherThanAppendedPastIt()
    {
        var d = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { AssetSelectWorld.FaceGeomDir });
        int reserve = AssetWire.AccountingReserve(d);

        foreach (var cap in new[] { 400, 900, 1200, 1600, 80_000 })
        {
            var text = AssetWire.Render(d, cap);
            int at = text.IndexOf("\n\n[accounting]", StringComparison.Ordinal);
            Assert.True(at >= 0, $"max_chars={cap} dropped the accounting block");
            // The overrun sentence a too-small cap earns is written after the block and is not part of it, so the
            // measurement stops where it starts.
            var block = text[at..];
            int overrun = block.IndexOf("\n[!] this response is ", StringComparison.Ordinal);
            if (overrun >= 0) block = block[..overrun];
            // What the block actually writes fits the room reserved for it, so subtracting that room from max_chars
            // before the body renders is enough to hold the whole response inside the cap.
            Assert.True(block.Length <= reserve,
                        $"max_chars={cap} wrote {block.Length} chars through a reserve of {reserve}");
        }

        // A cap that can hold the whole answer holds the accounting too, block and all.
        Assert.True(AssetWire.Render(d, 2_000).Length <= 2_000);
    }

    /// <summary>The selector-notes block is capped like its two sibling alarm blocks. One note per selector is bounded
    /// by the call's own input, but that input can be thousands of selectors, which would write megabytes before the
    /// per-path loop ever checks the budget.</summary>
    [Fact]
    public void ThousandsOfBadSelectorsAreCutAtMaxCharsRatherThanWrittenWhole()
    {
        var many = Enumerable.Range(0, 2_000).Select(i => @"meshes\nosuchfolder" + i).ToArray();

        var text = AssetWire.Render(_w.Svc.AssetStatus(Array.Empty<string>(), many), 2_000);

        Assert.True(text.Length <= 2_000, $"max_chars=2000 wrote {text.Length} chars of selector notes");
        Assert.Contains("more selector(s) omitted at max_chars=2000", text);
        Assert.Contains("notes=2000", text);                  // the count is still stated in full
    }

    /// <summary>Both select forms empty is a refusal that names both, since either one alone is a legal call.</summary>
    [Fact]
    public void ACallThatSelectsNothingRefusesNamingBothSelectForms()
    {
        var text = AssetTools.AssetStatus(_w.Svc, Array.Empty<string>());

        Assert.Contains("empty", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("under", text, StringComparison.Ordinal);
    }

    /// <summary>counts_only= answers the aggregate question a sweep's rows only imply: which layers win how many
    /// paths, how the winners split between loose and BSA, and how many are absent — with no path rows at all. The
    /// table is titled by LAYER and carries the note that says why: two of its values are not mods.</summary>
    [Fact]
    public void CountsOnlyAnswersTheCensusAndNoPathRows()
    {
        var text = AssetTools.AssetStatus(_w.Svc, under: new[] { AssetSelectWorld.FaceGeomDir }, counts_only: true);

        Assert.Contains("census: counted=5 present=5 absent=0 errors=0", text);
        Assert.Contains("winners: loose=4 BSA=1", text);
        // Count descending, then name ascending — the layer that wins most sits at the top of the table.
        Assert.Contains("winning layers (3 distinct):", text);
        Assert.Matches(@"2  FaceBase\n\s+2  FaceHigher\n\s+1  ArchiveMod", text);
        Assert.Contains("the game's own Data folder, or overwrite", text);
        Assert.DoesNotContain("WINS:", text);
    }

    /// <summary>An absent path is counted apart from a present one, so the census answers "how many of this
    /// selection does nothing provide" without the caller reading a row.</summary>
    [Fact]
    public void TheCensusCountsAbsentPathsApartFromPresentOnes()
    {
        var text = AssetTools.AssetStatus(_w.Svc, formids: new[] { AssetSelectWorld.TintAbsentFormId },
                                          counts_only: true);

        Assert.Contains("census: counted=2 present=1 absent=1 errors=0", text);
    }

    /// <summary>A census is never a window: limit= does not lower what it counts, it pages the table instead. A
    /// census of a window read as the selection's would be a wrong answer about the order.</summary>
    [Fact]
    public void ACensusCountsTheWholeSelectionWhateverLimitSays()
    {
        var windowed = AssetTools.AssetStatus(_w.Svc, under: new[] { AssetSelectWorld.FaceGeomDir },
                                              limit: 2, counts_only: true);
        var whole = AssetTools.AssetStatus(_w.Svc, under: new[] { AssetSelectWorld.FaceGeomDir }, counts_only: true);

        Assert.Contains("census: counted=5 present=5 absent=0 errors=0", windowed);
        Assert.Contains("winning layers (3 distinct):", windowed);   // the distinct count is the selection's too
        Assert.Contains("census: counted=5 present=5 absent=0 errors=0", whole);
    }

    /// <summary>The json twin carries the same census as data, the layer table as the shared histogram axis — the
    /// shape a consumer already reads on check's counts_only=, ordered, with its own distinct/rendered/cut_by.</summary>
    [Fact]
    public void TheJsonCensusCarriesTheLayerTableAsTheSharedHistogramAxis()
    {
        var root = System.Text.Json.JsonDocument.Parse(
            AssetTools.AssetStatus(_w.Svc, under: new[] { AssetSelectWorld.FaceGeomDir },
                                   counts_only: true, format: "json")).RootElement;

        Assert.Equal(5, root.GetProperty("counted").GetInt32());
        Assert.Equal(4, root.GetProperty("loose").GetInt32());
        Assert.Equal(1, root.GetProperty("bsa").GetInt32());
        Assert.Equal(0, root.GetProperty("absent").GetInt32());
        var axis = root.GetProperty("winners_by_layer");
        Assert.Equal(3, axis.GetProperty("distinct").GetInt32());
        Assert.Equal(3, axis.GetProperty("rendered").GetInt32());
        var rows = axis.GetProperty("rows");
        Assert.Equal("FaceBase", rows[0].GetProperty("key").GetString());
        Assert.Equal(2, rows[0].GetProperty("count").GetInt32());
        Assert.Equal("ArchiveMod", rows[2].GetProperty("key").GetString());
        Assert.False(root.TryGetProperty("results", out _));
    }

    /// <summary>Under ALARM pressure the census still fits its cap on both transports. The alarm blocks are the
    /// only cuttable thing above the counters, so if the counters, the axis note and the axis head are not charged
    /// before the alarms render, the alarms take that room and the response lands over the cap on a cut that would
    /// have fitted. A selection whose selectors mostly match nothing is what puts that pressure on.</summary>
    [Fact]
    public void ACensusUnderAlarmPressureStillFitsItsCapOnBothTransports()
    {
        var many = new[] { AssetSelectWorld.FaceGeomDir }
            .Concat(Enumerable.Range(0, 40).Select(i => @"meshes\hcnothing" + i)).ToArray();

        var text = AssetTools.AssetStatus(_w.Svc, under: many, counts_only: true, max_chars: 1_200);
        var json = AssetTools.AssetStatus(_w.Svc, under: many, counts_only: true, format: "json", max_chars: 1_000);

        Assert.True(text.Length <= 1_200, $"the text census is {text.Length} chars on max_chars=1200");
        Assert.True(json.Length <= 1_000, $"the json census is {json.Length} chars on max_chars=1000");
        // The counters and the alarm counts survive the pressure: what gives is the cuttable selector list.
        Assert.Contains("census: counted=5 present=5 absent=0 errors=0", text);
        Assert.Contains("[!] under (40):", text);
        Assert.Contains("more selector(s) omitted", text);
        Assert.Equal(5, System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("counted").GetInt32());
    }

    /// <summary>The json document's tail is reserved out of max_chars before anything is written, as the path
    /// render's is — a census that overran by its own closing members would make max_chars mean two things on one
    /// tool.</summary>
    [Fact]
    public void TheJsonCensusFitsTheMaxCharsItWasGiven()
    {
        const int Cap = 1_300;
        var text = AssetTools.AssetStatus(_w.Svc, under: new[] { AssetSelectWorld.FaceGeomDir },
                                          counts_only: true, format: "json", max_chars: Cap);

        var root = System.Text.Json.JsonDocument.Parse(text).RootElement;
        // A real cut: some rows in, the rest disclosed by the axis's own frame.
        Assert.Equal(3, root.GetProperty("winners_by_layer").GetProperty("distinct").GetInt32());
        Assert.Equal(2, root.GetProperty("winners_by_layer").GetProperty("rendered").GetInt32());
        Assert.Equal("max_chars", root.GetProperty("winners_by_layer").GetProperty("cut_by").GetString());
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.True(text.Length <= Cap, $"the census document is {text.Length} chars on max_chars={Cap}");
    }

    /// <summary>counts_only= and to_file= ask for opposite dispositions of the same result, so the pair is refused
    /// by name — the same refusal the records lanes make.</summary>
    [Fact]
    public void CountsOnlyBesideToFileIsRefusedByName()
    {
        var text = AssetTools.AssetStatus(_w.Svc, under: new[] { AssetSelectWorld.FaceGeomDir },
                                          counts_only: true, to_file: @"C:\work\never.jsonl");

        Assert.Contains("counts_only", text);
        Assert.Contains("the two contradict", text);
        Assert.False(File.Exists(@"C:\work\never.jsonl"));
    }

    /// <summary>The mod table is bounded by max_chars with the same named cut the path list takes, and the counters
    /// above it stay exact — a cut table never makes a total wrong.</summary>
    [Fact]
    public void TheCensusTableIsCutAtMaxCharsWithTheCountersStillExact()
    {
        // A cap that admits SOME rows and refuses the rest — the partial cut this is about. A cap small enough to
        // admit none proves only that the counters survive a collapse.
        var text = AssetTools.AssetStatus(_w.Svc, under: new[] { AssetSelectWorld.FaceGeomDir },
                                          counts_only: true, max_chars: 550);

        Assert.Contains("census: counted=5 present=5", text);       // the counters are exact whatever the cut
        Assert.Contains("winning layers (3 distinct):", text);      // and so is the distinct count
        Assert.Equal(2, Rows(text));
        // The knob named is the one that stopped the axis: these rows were refused room, not capped by limit=.
        Assert.Contains("1 more row(s) — raise max_chars= to see them", text);
        Assert.True(text.Length <= 550, $"the census is {text.Length} chars on max_chars=550");
    }

    /// <summary>limit= pages the census TABLE, which is the thing here that needs paging — the census itself covers
    /// the whole selection whatever limit= says. The cut names limit=, not max_chars=: raising the cap over rows
    /// limit= held back moves nothing.</summary>
    [Fact]
    public void LimitPagesTheCensusTableAndTheCutNamesThatKnob()
    {
        var text = AssetTools.AssetStatus(_w.Svc, under: new[] { AssetSelectWorld.FaceGeomDir },
                                          counts_only: true, limit: 1);

        Assert.Contains("census: counted=5 present=5 absent=0 errors=0", text);   // the whole selection, not a window
        Assert.Contains("winning layers (3 distinct):", text);
        Assert.Equal(1, Rows(text));
        Assert.Contains("2 more row(s) — raise limit= to see them", text);
    }

    /// <summary>offset= has no selection window to move under a census, so it is refused rather than silently
    /// ignored — and the refusal names the knob that does page the table.</summary>
    [Fact]
    public void OffsetBesideCountsOnlyIsRefusedAndNamesLimitInstead()
    {
        var text = AssetTools.AssetStatus(_w.Svc, under: new[] { AssetSelectWorld.FaceGeomDir },
                                          counts_only: true, offset: 2);

        Assert.Contains("counts_only= answers as a count table over the COMPLETE selection", text);
        Assert.Contains("caps with limit= and does not page", text);
    }

    /// <summary>How many table rows a census rendered — a row is a count padded to six columns, which nothing else
    /// in this response writes.</summary>
    static int Rows(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, @"\n {2,}\d+  \S").Count;
}

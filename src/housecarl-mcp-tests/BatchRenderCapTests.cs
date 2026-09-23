using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The shared batch-render skeleton's cap contract, on all three of its callers: <c>max_chars=</c> is a
/// CEILING on what comes back, not a test the render takes before writing the item that crosses it (#546). Everything
/// written after the items — the caller's trailer, this skeleton's own cut notice — is charged before the first item
/// is laid; an item that would cross what is left is taken back out whole and counted; an item wider than the whole
/// budget is named with the max_chars that clears it. The one arm left over is a cap too small for what the response
/// carries whatever the budget, and that says so too.</summary>
[Trait("tier", "unit")]
public class BatchRenderCapTests
{
    // ---- fixtures -----------------------------------------------------------------------------------

    static AssetStatusData Paths(int n, int providers = 3) => new(
        Enumerable.Range(0, n).Select(i => Contested($"meshes/batch/render/cap/path{i:D4}.nif", providers)).ToList(),
        new[] { "Broken - Textures.bsa (header refused)" },
        Array.Empty<string>(),   // no unwalkable loose root in this fixture
        true,
        Array.Empty<string>(),
        "TestProfile");

    static AssetStatusData ThreePaths() => new(
        new[]
        {
            Absent("meshes/a/first.nif"),
            Absent("meshes/b/second.nif"),
            Absent("meshes/c/third.nif"),
        },
        new[] { "Broken - Textures.bsa (header refused)" },
        Array.Empty<string>(),   // no unwalkable loose root in this fixture
        true,
        Array.Empty<string>(),
        "TestProfile");

    static AssetStatusData ThreeReadFailures() => new(
        new[] { Absent("meshes/a/first.nif") },
        new[]
        {
            "Broken - Textures.bsa (header refused)",
            "Broken - Meshes.bsa (header refused)",
            "Broken - Sounds.bsa (header refused)",
        },
        Array.Empty<string>(),   // no unwalkable loose root in this fixture
        true,
        Array.Empty<string>(),
        "TestProfile");

    /// <summary>An alarm block wide enough to fill a modest budget on its own, over ordinary short path blocks.</summary>
    static AssetStatusData ManyReadFailures(int failures, int paths) => new(
        Enumerable.Range(0, paths).Select(i => Absent($"meshes/alarm/path{i:D4}.nif")).ToList(),
        Enumerable.Range(0, failures).Select(i => $"A Mod With A Long Folder Name {i:D3} - Textures.bsa (header refused)").ToList(),
        Array.Empty<string>(),   // no unwalkable loose root in this fixture
        true,
        Array.Empty<string>(),
        "TestProfile");

    /// <summary>Narrow items first, then one item wider than any budget this render can give it.</summary>
    static AssetStatusData NarrowThenWide(int narrow, int providers) => new(
        Enumerable.Range(0, narrow).Select(i => Absent($"meshes/narrow/path{i:D4}.nif"))
            .Append(Contested("meshes/wide/path.nif", providers)).ToList(),
        Array.Empty<string>(),
        Array.Empty<string>(),   // no unwalkable loose root in this fixture
        true,
        Array.Empty<string>(),
        "TestProfile");

    /// <summary>An alarm block that must itself be cut, above one item wider than the whole budget: both the alarms
    /// and the item render wider at a wider cap.</summary>
    static AssetStatusData WideItemUnderManyAlarms(int failures, int providers) => new(
        new[] { Contested("meshes/wide/path.nif", providers) },
        Enumerable.Range(0, failures).Select(i => $"A Mod With A Long Folder Name {i:D3} - Textures.bsa (header refused)").ToList(),
        Array.Empty<string>(),   // no unwalkable loose root in this fixture
        true,
        Array.Empty<string>(),
        "TestProfile");

    static AssetPathResult Absent(string path) =>
        new(path, new AssetHit(path, false, null, Array.Empty<AssetProvider>(), false), null);

    /// <summary>A path several mods provide, so its block is many lines rather than one — the shape whose last block
    /// used to land past the cap.</summary>
    static AssetPathResult Contested(string path, int providers)
    {
        var chain = Enumerable.Range(0, providers)
            .Select(i => new AssetProvider($"A Mod Whose Folder Name Is Long Enough To Matter {i}", AssetKind.Loose))
            .ToList();
        return new AssetPathResult(path, new AssetHit(path, true, chain[0], chain, providers > 1), null);
    }

    static NifInspectBatchData Meshes(int n) => new(
        Enumerable.Range(0, n)
            .Select(i => NifInspectData.Fail($"meshes/batch/render/cap/mesh{i:D4}.nif",
                                             "ABSENT — no active mod or BSA provides this mesh"))
            .ToList(),
        new[] { "Broken - Textures.bsa (header refused)" },
        new[] { "BlockedMod: could not read 'meshes\\batch' — Access to the path is denied." },
        Array.Empty<string>(),
        "TestProfile");

    static string RenderNif(NifInspectBatchData d, int cap) =>
        NifWire.Render(d, new HashSet<string>(StringComparer.OrdinalIgnoreCase), Array.Empty<string>(), cap);

    static string RenderNif(NifInspectBatchData d, int cap, params string[] sections) =>
        NifWire.Render(d, new HashSet<string>(sections, StringComparer.OrdinalIgnoreCase), Array.Empty<string>(), cap);

    /// <summary>One readable mesh with enough in every section to outgrow a modest budget: the section the budget
    /// starts cuts, and the sections after it cannot start at all.</summary>
    static NifInspectBatchData OneFullMesh(int shapes = 40, int nodes = 40, int strings = 40)
    {
        var prov = new NifProvider("A Mod Whose Folder Name Is Long Enough To Matter", "loose");
        var mesh = new HousecarlCore.NifInspect(
            "20.2.0.7", 12, 100, true, shapes + nodes,
            new[] { new HousecarlCore.NifBlockTypeCount("BSTriShape", shapes) }, false, Array.Empty<string>(),
            Enumerable.Range(0, shapes).Select(i => new HousecarlCore.NifShape(
                $"ShapeNumber{i:D3}", 14u, 1f, "BSTriShape", 14u, "NiAVObject",
                Array.Empty<HousecarlCore.NifPartition>(), null, Array.Empty<HousecarlCore.NifTexture>(),
                Array.Empty<string>())).ToList(),
            Enumerable.Range(0, nodes).Select(i => new HousecarlCore.NifNode(1, $"NodeNumber{i:D3}", 14u, "NiNode", 14u, "NiAVObject")).ToList(),
            Enumerable.Range(0, strings).Select(i => $"string table entry number {i:D3}").ToList());
        return new NifInspectBatchData(
            new[] { new NifInspectData("meshes/full/mesh.nif", prov, new[] { prov }, false, false, mesh, null) },
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), "TestProfile");
    }

    static PlaceOutcome Placed(int n) => new(
        Enumerable.Range(0, n)
            .Select(i => new PlaceResult($"meshes/batch/render/cap/place{i:D4}.nif", true, 42,
                                         "A Source Mod (loose)", "The Current Winner (loose)", null))
            .ToList(),
        @"C:\mods\houseCARL - MyFixes", Array.Empty<string>(), null, null);

    // ---- the ceiling, on each render ------------------------------------------------------------------

    /// <summary>asset_status filled well past its cap comes back inside it, and says how many paths that cost.</summary>
    [Theory]
    [InlineData(1_600)]
    [InlineData(2_500)]
    [InlineData(6_000)]
    public void AnAssetStatusRenderFilledPastItsCapAnswersInsideIt(int cap)
    {
        var text = AssetWire.Render(Paths(400), cap);

        Assert.True(text.Length <= cap, $"asset_status returned {text.Length} chars at max_chars={cap}");
        Assert.Contains($"omitted at max_chars={cap}", text);
        Assert.Matches(@"\[\d+ more path\(s\) omitted", text);
    }

    /// <summary>nif_inspect, the same.</summary>
    [Theory]
    [InlineData(1_600)]
    [InlineData(2_500)]
    [InlineData(6_000)]
    public void ANifInspectRenderFilledPastItsCapAnswersInsideIt(int cap)
    {
        var text = RenderNif(Meshes(400), cap);

        Assert.True(text.Length <= cap, $"nif_inspect returned {text.Length} chars at max_chars={cap}");
        Assert.Contains($"omitted at max_chars={cap}", text);
        Assert.Matches(@"\[\d+ more mesh\(es\) omitted", text);
    }

    /// <summary>place, whose whole trailer — the counts line and the enable-and-sort instruction — used to sit
    /// outside the cap on purpose. It is charged now, and still written.</summary>
    [Theory]
    [InlineData(1_500)]
    [InlineData(4_000)]
    public void APlaceRenderFilledPastItsCapAnswersInsideItAndStillCarriesItsTrailer(int cap)
    {
        var text = PlaceWire.Render(Placed(400), cap);

        Assert.True(text.Length <= cap, $"place returned {text.Length} chars at max_chars={cap}");
        Assert.Contains($"omitted at max_chars={cap}", text);
        Assert.Contains("\"wrote it\" is not \"it wins\"", text);
    }

    /// <summary>The count the notice states is the count the render actually held back: rendered plus omitted is the
    /// whole selection, so a caller can act on the number instead of counting blocks.</summary>
    [Fact]
    public void TheNoticeCountsExactlyWhatTheRenderHeldBack()
    {
        var text = AssetWire.Render(Paths(400), 3_000);

        var omitted = int.Parse(System.Text.RegularExpressions.Regex.Match(text, @"\[(\d+) more path\(s\) omitted").Groups[1].Value);
        var rendered = int.Parse(System.Text.RegularExpressions.Regex.Match(text, @"rendered=(\d+)").Groups[1].Value);
        Assert.Equal(400, rendered + omitted);
    }

    // ---- the two ways a batch ends with nothing on the page --------------------------------------------

    /// <summary>One item wider than the whole budget cannot be cut into place, so it is NAMED — with the max_chars
    /// that clears it in one step — rather than dropped as if the list had simply run long.</summary>
    [Fact]
    public void AnItemWiderThanTheWholeBudgetIsNamedNotSilentlyDropped()
    {
        var text = AssetWire.Render(Paths(3, providers: 60), 1_200);

        Assert.True(text.Length <= 1_200, $"returned {text.Length} chars at max_chars=1200");
        Assert.Contains("the next one alone is wider than this response's whole budget", text);
        Assert.Contains("raise max_chars to at least ", text);
        var needed = int.Parse(System.Text.RegularExpressions.Regex.Match(text, @"raise max_chars to at least (\d+)").Groups[1].Value);
        Assert.True(needed > 1_200, $"the remedy must name a wider cap than the one that failed, got {needed}");
    }

    /// <summary>The remedy is executable: rendering again at the max_chars the notice named gets the item onto the
    /// page. A remedy that has to be followed twice is one the caller cannot act on — including from a cap with
    /// fewer digits than its own answer, where the notice the raise pays for grows as the number does.</summary>
    [Theory]
    [InlineData(900)]
    [InlineData(950)]
    [InlineData(990)]
    [InlineData(1_200)]
    public void FollowingThatRemedyOnceRendersTheItem(int cap)
    {
        var data = Paths(3, providers: 60);
        var first = AssetWire.Render(data, cap);
        var needed = int.Parse(System.Text.RegularExpressions.Regex.Match(first, @"raise max_chars to at least (\d+)").Groups[1].Value);

        var second = AssetWire.Render(data, needed);

        Assert.Contains("meshes/batch/render/cap/path0000.nif", second);
        Assert.DoesNotContain("wider than this response's whole budget", second);
    }

    /// <summary>The place render's named-roots block grows with max_chars, so the oversize remedy has to price it at
    /// the cap it names: following that remedy once still gets the row onto the page past a long root list.</summary>
    [Fact]
    public void FollowingThePlaceRemedyOnceRendersTheRowPastManyRoots()
    {
        var roots = Enumerable.Range(0, 200)
                              .Select(i => $"BlockedMod{i:D3}: could not read 'meshes' — Access to the path is denied.")
                              .ToList();
        var data = new PlaceOutcome(new[] { PlaceResult.Fail("meshes/wide/row.nif", new string('e', 6000)) }, null,
                                    Array.Empty<string>(), null, null) { RootFailures = roots };
        var first = PlaceWire.Render(data, 2_000);
        var needed = int.Parse(System.Text.RegularExpressions.Regex.Match(first, @"raise max_chars to at least (\d+)").Groups[1].Value);

        var second = PlaceWire.Render(data, needed);

        Assert.Contains("meshes/wide/row.nif", second);
        Assert.DoesNotContain("wider than this response's whole budget", second);
    }

    /// <summary>The empty page has two causes and the notice names the right one: when the ALARMS above the items
    /// filled the budget, the first item is ordinary and the cut marker is what the caller reads — not a sentence
    /// blaming an item that would have fitted an empty page.</summary>
    [Fact]
    public void AlarmsThatFillTheBudgetAreNotBlamedOnTheFirstItem()
    {
        var text = AssetWire.Render(ManyReadFailures(40, 20), 2_000);

        Assert.DoesNotContain("the next one alone is wider than this response's whole budget", text);
        Assert.Matches(@"\[\d+ more path\(s\) omitted", text);
    }

    /// <summary>The one arm a bounded render may still exceed on: a cap too small for the header, the alarms and the
    /// accounting it carries whatever the budget. It ships the answer and NAMES the overrun, the same shape the check
    /// sweep has carried since #537 — never a silent overrun and never a mid-token trim.</summary>
    [Fact]
    public void ACapTooSmallForTheFixedPartSaysSoAndNamesTheCapThatClearsIt()
    {
        var text = AssetWire.Render(ThreePaths(), 60);

        Assert.Contains("over the max_chars=60 it was given", text);
        Assert.Contains("raise max_chars to at least ", text);
        var needed = int.Parse(System.Text.RegularExpressions.Regex.Match(text, @"raise max_chars to at least (\d+)").Groups[1].Value);
        Assert.Equal(text.Length, needed);
    }

    // ---- the alarm lists ------------------------------------------------------------------------------

    /// <summary>The alarm block above the items is bounded the same way, whole lines only, and counts what it
    /// dropped.</summary>
    [Fact]
    public void AReadFailureListIsBoundedToAndCutsWithTheNamedMarker()
    {
        var text = AssetWire.Render(ThreeReadFailures(), 1_500);

        Assert.True(text.Length <= 1_500, $"returned {text.Length} chars at max_chars=1500");
        Assert.Contains("Broken - Textures.bsa (header refused)", text);
        Assert.Contains("archive(s) could NOT be read this build", text);
    }

    /// <summary>A cap that actually cuts the alarm list: the heading — the count, which IS the alarm — is written
    /// whatever the budget, the lines under it are cut with the named marker in its one spelling, and what the cut
    /// cost is said rather than left as a short list that reads complete.</summary>
    [Fact]
    public void ACutReadFailureListNamesTheArchivesItHeldBack()
    {
        var text = AssetWire.Render(ThreeReadFailures(), 620);

        Assert.Contains("3 archive(s) could NOT be read this build", text);
        Assert.Contains("archive(s) omitted at max_chars=620", text);
        Assert.Contains("  … [", text);
        var held = int.Parse(System.Text.RegularExpressions.Regex.Match(text, @"\[(\d+) more archive\(s\) omitted").Groups[1].Value);
        var listed = System.Text.RegularExpressions.Regex.Matches(text, @"  - Broken - \w+\.bsa").Count;
        Assert.Equal(3, held + listed);
    }

    /// <summary>An alarm the budget cannot hold is NEVER dropped in silence: the heading and its count ship, and the
    /// overrun that costs is named. A response that quietly loses the archive-read alarm reads as a clean sweep.</summary>
    [Fact]
    public void AnAlarmTooWideForTheBudgetIsStillSaidAndTheOverrunIsNamed()
    {
        var text = AssetWire.Render(ThreeReadFailures(), 300);

        Assert.Contains("3 archive(s) could NOT be read this build", text);
        Assert.Contains("over the max_chars=300 it was given", text);
    }

    /// <summary>The two batch renders agree under the same cap: each names its cut with the same marker, in the same
    /// spelling, which is what the shared skeleton buys.</summary>
    [Fact]
    public void BothBatchRendersCutWithTheSameMarker()
    {
        var asset = AssetWire.Render(Paths(200), 4_000);
        var nif = RenderNif(Meshes(200), 4_000);

        Assert.Contains("  … [", asset);
        Assert.Contains("  … [", nif);
        Assert.DoesNotContain("... [", asset);
        Assert.DoesNotContain("... [", nif);
    }

    /// <summary>One mesh whose shaders read as another game's layout, so every slot line it prints carries the
    /// not-derived caveat.</summary>
    static NifInspectBatchData MeshWithForeignShader(int shapes = 12)
    {
        var prov = new NifProvider("A Mod Whose Folder Name Is Long Enough To Matter", "loose");
        var shader = new HousecarlCore.NifShader("BSLightingShaderProperty", "FO4", "Default", null, null, null,
                                                 null, null, null, null, null);
        var mesh = new HousecarlCore.NifInspect(
            "20.2.0.7", 12, 100, true, shapes,
            new[] { new HousecarlCore.NifBlockTypeCount("BSTriShape", shapes) }, false, Array.Empty<string>(),
            Enumerable.Range(0, shapes).Select(i => new HousecarlCore.NifShape(
                $"ShapeNumber{i:D3}", 14u, 1f, "BSTriShape", 14u, "NiAVObject",
                Array.Empty<HousecarlCore.NifPartition>(), null,
                new[] { new HousecarlCore.NifTexture(2, $"textures/mod/shape{i:D3}_g.dds") },
                Array.Empty<string>(), shader)).ToList(),
            Array.Empty<HousecarlCore.NifNode>(), Array.Empty<string>());
        return new NifInspectBatchData(
            new[] { new NifInspectData("meshes/foreign/mesh.nif", prov, new[] { prov }, false, false, mesh, null) },
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), "TestProfile");
    }

    /// <summary>A mesh whose sections cut still reaches the page: the marker a cut section writes is charged with the
    /// missed-sections line, so the mesh does not end one marker past the budget and get taken back out entire —
    /// which answered with the path line and nothing else, no version, no providers, no resolution.</summary>
    [Theory]
    [InlineData(2_000)]
    [InlineData(3_000)]
    [InlineData(4_500)]
    public void AMeshWhoseSectionsCutIsStillRendered(int cap)
    {
        var text = RenderNif(OneFullMesh(), cap, "shapes", "nodes", "strings");

        Assert.True(text.Length <= cap, $"nif_inspect returned {text.Length} chars at max_chars={cap}");
        Assert.Contains("meshes/full/mesh.nif", text);
        Assert.Contains("version: 20.2.0.7", text);
    }

    /// <summary>The caveat that says how the slot lines below it must be read is never dropped to meet the ceiling: a
    /// section renders its rows with it or does not start. Without it a bare tex[2] reads as "this Skyrim shader does
    /// not determine slot 2" rather than "this layout is not modelled at all".</summary>
    [Theory]
    [InlineData(930)]
    [InlineData(1_000)]
    [InlineData(1_100)]
    public void ASlotSectionRendersItsCaveatOrDoesNotStart(int cap)
    {
        var text = RenderNif(MeshWithForeignShader(), cap, "shapes", "paths");

        if (text.Contains("--- shapes (") || text.Contains("--- paths ("))
            Assert.Contains("slot names are NOT DERIVED", text);
    }
    /// <summary>An item nothing can make room for is named wherever it falls in the list, not only when it is first:
    /// the cut marker's "raise max_chars to see all" sends the caller round a raise that cannot work, because the
    /// items before it grow into whatever they are given and the same item crosses again. Swept over caps rather than
    /// pinned to one, so a change in what the response reserves cannot move the case out from under the test.</summary>
    [Fact]
    public void AnOversizeItemAfterTheFirstIsNamedToo()
    {
        var data = NarrowThenWide(3, providers: 60);
        int named = 0;
        for (int cap = 1_000; cap <= 2_500; cap += 25)
        {
            var first = AssetWire.Render(data, cap);
            if (!first.Contains("meshes/narrow/path0000.nif")) continue;
            var m = System.Text.RegularExpressions.Regex.Match(first, @"whole budget; raise max_chars to at least (\d+)");
            if (!m.Success) continue;
            named++;
            var second = AssetWire.Render(data, int.Parse(m.Groups[1].Value));
            Assert.Contains("meshes/wide/path.nif", second);
        }
        Assert.True(named > 0, "no cap in the sweep put a narrow item on the page and named the wide one");
    }

    /// <summary>The remedy holds where the rendered width itself GROWS with the budget: a mesh whose detail sections
    /// fill whatever room they are given. The number is measured against the widest form rather than the cut one, so
    /// following it once puts the item on the page instead of returning the same sentence with a larger number.</summary>
    [Fact]
    public void TheRemedyHoldsWhereTheRenderedWidthGrowsWithTheBudget()
    {
        var data = OneFullMesh();
        int named = 0;
        for (int cap = 300; cap <= 2_000; cap += 10)
        {
            var first = RenderNif(data, cap, "shapes", "nodes", "strings");
            var m = System.Text.RegularExpressions.Regex.Match(first, @"whole budget; raise max_chars to at least (\d+)");
            if (!m.Success) continue;
            named++;
            var second = RenderNif(data, int.Parse(m.Groups[1].Value), "shapes", "nodes", "strings");
            Assert.Contains("meshes/full/mesh.nif", second);
            Assert.DoesNotContain("wider than this response's whole budget", second);
        }
        Assert.True(named > 0, "no cap in the sweep named the oversize remedy");
    }

    /// <summary>The same, where the ALARM block above the item is what grows: it renders more lines at the wider cap,
    /// and the remedy already counted them.</summary>
    [Fact]
    public void TheRemedyCountsTheAlarmLinesAWiderCapWouldRender()
    {
        var data = WideItemUnderManyAlarms(40, 60);
        int named = 0;
        for (int cap = 300; cap <= 4_000; cap += 25)
        {
            var first = AssetWire.Render(data, cap);
            var m = System.Text.RegularExpressions.Regex.Match(first, @"whole budget; raise max_chars to at least (\d+)");
            if (!m.Success) continue;
            named++;
            var second = AssetWire.Render(data, int.Parse(m.Groups[1].Value));
            Assert.Contains("meshes/wide/path.nif", second);
            Assert.DoesNotContain("wider than this response's whole budget", second);
        }
        Assert.True(named > 0, "no cap in the sweep named the oversize remedy");
    }

    /// <summary>A caveat block's lists share ONE quarter of max_chars, max-min fair: a short list is shown whole with no
    /// marker, and the two long lists split what is left instead of the first taking it all. A whole quarter per list
    /// overruns the block's length; a greedy split leaves the second long list its one forced entry.</summary>
    [Fact]
    public void ACaveatBlockSharesOneQuarterFairlyAcrossItsLists()
    {
        const int cap = 20_000;
        var warnings = BatchRender.WarningList(Enumerable.Range(1, 200).Select(i => $"warning {i:D3}: " + new string('w', 180)).ToList());
        // Three lines of 216 chars: 648 together, well inside any fair share.
        var archives = BatchRender.ArchiveFailureList(Enumerable.Range(1, 3).Select(i => $"Archive{i}.bsa: " + new string('a', 175)).ToList());
        var roots = BatchRender.RootFailureList(Enumerable.Range(1, 200).Select(i => $"BlockedMod{i:D3}: " + new string('r', 160)).ToList());

        var cuts = BatchRender.CaveatBlockCut(cap, warnings, archives, roots);
        string block = BatchRender.CaveatBlockLines(cap, warnings, archives, roots);

        Assert.Equal(3, cuts[1].Shown.Count);                       // the short list, whole
        Assert.Equal(0, cuts[1].Omitted);
        Assert.DoesNotContain("archive read failure(s); raise max_chars", block);
        Assert.InRange(cuts[0].Shown.Count, 8, 12);                 // ~2,170 chars each of the 4,346 left: ~10 warnings,
        Assert.InRange(cuts[2].Shown.Count, 8, 12);                 // ~11 roots — not 20 and 1
        // The block itself stays inside the quarter, give or take one line.
        Assert.True(block.Length <= cap / 4 + 300, $"the caveat block is {block.Length} chars against a quarter of {cap}");

        // At 8,000 the short list's fair share is 666 chars against its 648 of lines: they fit, so all three are shown —
        // the marker's room is set aside only for a list that will not fit, never out of one that does.
        var tight = BatchRender.CaveatBlockCut(8_000, warnings, archives, roots);
        Assert.Equal(3, tight[1].Shown.Count);
        Assert.Equal(0, tight[1].Omitted);

        // At 2,000 the quarter (500) is under three forced lines: each list names its one entry and no more, so the
        // one-entry minimum is the only thing past the quarter.
        var floor = BatchRender.CaveatBlockCut(2_000, warnings, archives, roots);
        Assert.All(floor, c => Assert.Single(c.Shown));

        // A forced entry wider than its part is charged to the next list, not to the rows: one 3,000-char warning at
        // 8,000 spends the whole quarter, so the roots get their one forced entry — not the 1,000 chars the split
        // gave them before, which pushed the block ~1,000 chars past its quarter.
        var wide = BatchRender.WarningList(new[] { "one wide warning: " + new string('w', 2_982) });
        var over = BatchRender.CaveatBlockCut(8_000, wide, roots);
        string overBlock = BatchRender.CaveatBlockLines(8_000, wide, roots);
        Assert.Single(over[1].Shown);
        Assert.True(overBlock.Length <= 3_005 + 300,
                    $"the block is {overBlock.Length} chars: more than the wide warning plus one forced root");
    }

    /// <summary>A roots-only lane (the records sweep, create, their json) cuts through the same block cut: a list whose
    /// lines fit the quarter is shown WHOLE with no marker, even where the lines plus a marker would not fit, and one
    /// line more is cut and counted — in the text lines and the json array alike.</summary>
    [Fact]
    public void ARootsOnlyListThatFitsItsQuarterIsWholeAndOneMoreIsCut()
    {
        const int cap = 4_000;   // a quarter of 1,000; the widest marker is ~63, so 960 of lines sits between the two
        var four = Enumerable.Range(1, 4).Select(i => $"BlockedMod{i}: " + new string('r', 197)).ToList();   // 240 a line
        var five = four.Append("BlockedMod5: " + new string('r', 197)).ToList();

        Assert.Equal(960, four.Sum(f => BatchRender.RootFailureLead.Length + f.Length + 1));
        Assert.Equal((4, 0), (BatchRender.RootFailureCut(four, cap).Shown.Count, BatchRender.RootFailureCut(four, cap).Omitted));
        Assert.DoesNotContain("raise max_chars", BatchRender.RootFailureLines(four, cap));
        Assert.Equal((0, 4), JsonRoots(four, cap));

        Assert.Contains("of 5 loose root read failure(s); raise max_chars", BatchRender.RootFailureLines(five, cap));
        Assert.Equal((2, 3), JsonRoots(five, cap));   // 1,000 less the marker holds three 240-char lines
    }

    /// <summary>What a roots-only json lane writes for <paramref name="roots"/>: (omitted, shown).</summary>
    static (int Omitted, int Shown) JsonRoots(IReadOnlyList<string> roots, int cap)
    {
        using var ms = new MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            JsonWire.WriteRootFailuresCut(w, roots, cap);
            w.WriteEndObject();
        }
        using var doc = System.Text.Json.JsonDocument.Parse(ms.ToArray());
        return (doc.RootElement.GetProperty("root_read_failures_omitted").GetInt32(),
                doc.RootElement.GetProperty("root_read_failures").GetArrayLength());
    }
}

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
        true,
        Array.Empty<string>(),
        "TestProfile");

    /// <summary>An alarm block wide enough to fill a modest budget on its own, over ordinary short path blocks.</summary>
    static AssetStatusData ManyReadFailures(int failures, int paths) => new(
        Enumerable.Range(0, paths).Select(i => Absent($"meshes/alarm/path{i:D4}.nif")).ToList(),
        Enumerable.Range(0, failures).Select(i => $"A Mod With A Long Folder Name {i:D3} - Textures.bsa (header refused)").ToList(),
        true,
        Array.Empty<string>(),
        "TestProfile");

    /// <summary>Narrow items first, then one item wider than any budget this render can give it.</summary>
    static AssetStatusData NarrowThenWide(int narrow, int providers) => new(
        Enumerable.Range(0, narrow).Select(i => Absent($"meshes/narrow/path{i:D4}.nif"))
            .Append(Contested("meshes/wide/path.nif", providers)).ToList(),
        Array.Empty<string>(),
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
            Array.Empty<string>(), Array.Empty<string>(), "TestProfile");
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
            Array.Empty<string>(), Array.Empty<string>(), "TestProfile");
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
    /// items before it grow into whatever they are given and the same item crosses again.</summary>
    [Theory]
    [InlineData(1_400)]
    [InlineData(1_800)]
    public void AnOversizeItemAfterTheFirstIsNamedToo(int cap)
    {
        var data = NarrowThenWide(3, providers: 60);
        var first = AssetWire.Render(data, cap);

        Assert.Contains("meshes/narrow/path0000.nif", first);
        Assert.Contains("the next one alone is wider than this response's whole budget", first);

        var needed = int.Parse(System.Text.RegularExpressions.Regex.Match(first, @"raise max_chars to at least (\d+)").Groups[1].Value);
        var second = AssetWire.Render(data, needed);
        Assert.Contains("meshes/wide/path.nif", second);
    }
}

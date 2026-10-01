using System.Text.RegularExpressions;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The nif_inspect batch render (#229) over constructed per-path results: input order, one bad path not
/// aborting the batch, batch alarms once and first, an explicit cut inside the cap, the ABSENT hedge at the point of
/// use, and no cut notice when every mesh rendered.</summary>
[Trait("tier", "unit")]
public sealed class NifBatchWireTests
{
    const int BigCap = 80_000;
    const string PathA = @"meshes\a\first.nif";
    const string PathB = @"meshes\b\second.nif";
    const string PathC = @"meshes\c\third.nif";
    const string ArchiveAlarm = "Broken - Textures.bsa (header refused)";

    static readonly HashSet<string> NoSections = new(StringComparer.OrdinalIgnoreCase);

    static string Render(NifInspectBatchData batch, int cap = BigCap) => NifWire.Render(batch, NoSections, Array.Empty<string>(), cap);

    static NifInspectBatchData Batch(IReadOnlyList<NifInspectData> results, params string[] archiveFailures)
        => new(results, archiveFailures, Array.Empty<string>(), Array.Empty<string>(), "TestProfile");

    static NifInspectData Ok(string rel, string shapePrefix)
        => new(rel, new NifProvider("ModA", "loose"), new[] { new NifProvider("ModA", "loose") }, false, false,
            OneShapeMesh(shapePrefix + "0"), null);

    static NifInspect OneShapeMesh(string shapeName)
        => new("20.2.0.7", 12, 100, true, 2,
            new List<NifBlockTypeCount> { new("BSTriShape", 1), new("NiNode", 1) },
            false, Array.Empty<string>(),
            new List<NifShape> { new(shapeName, 0x400000E, 1f, "BSTriShape", 0x8000E, "BSTriShape",
                new List<NifPartition>(), null, new List<NifTexture>(), new List<string>()) },
            new List<NifNode> { new(0, "Root", 0xE, "NiNode", 0xE, "NiNode") }, new List<string> { "Root", shapeName });

    static NifInspectData Absent(string rel)
        => new(rel, null, Array.Empty<NifProvider>(), false, Absent: true, null,
            "ABSENT — no active mod or BSA provides this mesh path.");

    static int Count(string text, string what) => Regex.Matches(text, Regex.Escape(what)).Count;

    // Probe: "1. input order: three meshes render in the order passed". Strengthened: passed in reverse path order,
    // so a sort by path would fail it.
    [Fact]
    public void MeshesRenderInTheOrderPassed()
    {
        var o = Render(Batch(new[] { Ok(PathC, "ShapeC"), Ok(PathA, "ShapeA"), Ok(PathB, "ShapeB") }));

        int ic = o.IndexOf(PathC, StringComparison.Ordinal), ia = o.IndexOf(PathA, StringComparison.Ordinal),
            ib = o.IndexOf(PathB, StringComparison.Ordinal);
        Assert.True(ic >= 0 && ia > ic && ib > ia, o);
    }

    // Probe: "2. per-path error: the middle path's ABSENT is loud and both neighbors still render" and "2b. exactly
    // the two clean reads carry a 'read from:' resolution".
    [Fact]
    public void ABadPathInTheMiddleDoesNotStopItsNeighbours()
    {
        var o = Render(Batch(new[] { Ok(PathA, "ShapeA"), Absent(PathB), Ok(PathC, "ShapeC") }));

        Assert.Contains("ABSENT", o);
        Assert.Contains("'ShapeA0'", o);
        Assert.Contains("'ShapeC0'", o);
        Assert.Equal(2, Count(o, "read from:"));
    }

    // Probe: "3. the BSA read-failure alarm renders exactly once for a 3-mesh batch" and "3b. the alarm renders
    // BEFORE the first per-mesh block".
    [Fact]
    public void TheArchiveAlarmRendersOnceBeforeTheFirstMesh()
    {
        var o = Render(Batch(new[] { Ok(PathA, "ShapeA"), Ok(PathB, "ShapeB"), Ok(PathC, "ShapeC") }, ArchiveAlarm));

        Assert.Equal(1, Count(o, "could NOT be read"));
        Assert.True(o.IndexOf("could NOT be read", StringComparison.Ordinal) < o.IndexOf(PathA, StringComparison.Ordinal), o);
    }

    // Probe: "4. a small max_chars names the omitted-mesh count", "4b. the batch-level alarm still renders under the
    // cut" and "4c. max_chars is a ceiling — the mesh that would cross it is not written at all (#546)".
    [Fact]
    public void ASmallCapCutsWithANamedCountKeepsTheAlarmAndStaysInsideTheCap()
    {
        var o = Render(Batch(new[] { Ok(PathA, "ShapeA"), Ok(PathB, "ShapeB"), Ok(PathC, "ShapeC") }, ArchiveAlarm), 700);

        Assert.Contains("more mesh(es) omitted at max_chars=700", o);
        Assert.Contains("could NOT be read", o);
        Assert.True(o.Length <= 700, $"{o.Length} chars");
    }

    // Probe: "5. the archive and discovery hedge lines render under the ABSENT", "5a. the loose-root hedge line renders
    // under the ABSENT too", "5b. the hedge sits at POINT OF USE" and "5c. the non-ABSENT error is NOT hedged".
    [Fact]
    public void AnAbsentInAnIncompleteScanCarriesEveryHedgeAndAParseErrorCarriesNone()
    {
        var caveated = new NifInspectBatchData(
            new[] { Absent(PathA), NifInspectData.Fail(PathB, "NiflySharp refused this mesh — not a NIF.") },
            new[] { ArchiveAlarm },
            new[] { @"BlockedMod: could not read 'meshes\hcwalk' — Access to the path is denied." },
            new[] { "Skyrim.ini not found — base archives unscanned" }, "TestProfile");

        var o = Render(caveated);

        Assert.Contains("the mesh could live in the unreadable archive", o);
        Assert.Contains("BSAs that weren't enumerated", o);
        Assert.Contains("the mesh could live in the folder that would not read", o);
        Assert.True(o.IndexOf("ABSENT", StringComparison.Ordinal)
                    < o.IndexOf("could live in the unreadable archive", StringComparison.Ordinal), o);
        Assert.Equal(3, Count(o, "may be incomplete"));
    }

    // Probe: "6. no bogus omitted notice when every mesh rendered".
    [Fact]
    public void ABatchThatRenderedEveryMeshClaimsNoCut()
    {
        var o = Render(Batch(new[] { Ok(PathA, "ShapeA") }, ArchiveAlarm));

        Assert.Contains("read from:", o);
        Assert.Contains(PathA, o);
        Assert.DoesNotContain("mesh(es) omitted", o);
    }
}

using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The asset walk's front door (#1314): the row lane's transport checks run for the walk too, limit=/offset=
/// window the nodes listed while the walk runs whole, to_file= and counts_only= are honoured, links= is refused, an
/// unused walk input is a note, and every node, an error node too, is one row per path under the file bound.</summary>
[Trait("tier", "unit")]
public sealed class AssetWalkFrontDoorTests : IClassFixture<AssetWalkWorld>
{
    readonly AssetWalkWorld _w;
    public AssetWalkFrontDoorTests(AssetWalkWorld w) => _w = w;

    const int Big = 400_000;

    /// <summary>The sixty-texture walk from the donor: every node from source, so the place set is the whole walk.</summary>
    string Many(int limit = 0, int offset = 0, string? format = null, int max = Big, string? toFile = null, bool counts = false)
        => AssetTools.AssetStatus(_w.Svc, asset_paths: AssetWalkWorld.ManyTex, walk: true, source_provider: AssetWalkWorld.DonorMod,
                                  limit: limit, offset: offset, format: format, max_chars: max, to_file: toFile, counts_only: counts);

    static string[] Paths(JsonElement doc) => doc.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("path").GetString()!).ToArray();

    static string Place(JsonElement doc) => doc.GetProperty("place_set").GetRawText();

    static string Notes(JsonElement doc) => doc.GetProperty("notes").GetRawText();

    /// <summary>The standing line and every line above it: the part of a text walk that covers the whole walk.</summary>
    static string Lead(string text) => text[..text.IndexOf('\n', text.IndexOf("\nnodes: ", StringComparison.Ordinal) + 1)];

    string File(string tag) => Path.Combine(_w.Root, tag + "-" + Guid.NewGuid().ToString("N") + ".jsonl");

    /// <summary>A negative limit= or offset= on a walk is refused before it runs, in the row lane's own words.</summary>
    [Fact]
    public void ANegativeWindowOnAWalkIsRefusedInTheRowLanesWords()
    {
        foreach (var (limit, offset) in new[] { (-1, 0), (0, -1) })
        {
            var row = (string)AssetTools.AssetStatus(_w.Svc, asset_paths: AssetWalkWorld.ManyTex, limit: limit, offset: offset);
            Assert.StartsWith("error: limit=", row);
            Assert.Equal(row, Many(limit, offset));
        }
    }

    /// <summary>Pages of limit=7 concatenate to the default walk's nodes, in order; each page's place set, notes and
    /// standing counts are the default's, over the whole walk.</summary>
    [Fact]
    public void PagesConcatenateToTheWholeWalkAndKeepItsWholeCounts()
    {
        using var whole = JsonDocument.Parse(Many(format: "json"));
        var all = Paths(whole.RootElement);
        Assert.Equal(AssetWalkWorld.ManyTex.Length, all.Length);
        var lead = Lead(Many());

        var paged = new List<string>();
        for (int offset = 0; offset < all.Length; offset += 7)
        {
            using var page = JsonDocument.Parse(Many(7, offset, "json"));
            Assert.Equal(Place(whole.RootElement), Place(page.RootElement));
            Assert.Equal(Notes(whole.RootElement), Notes(page.RootElement));
            Assert.Equal(all.Length, page.RootElement.GetProperty("accounting").GetProperty("total").GetInt32());
            paged.AddRange(Paths(page.RootElement));
            Assert.Equal(lead, Lead(Many(7, offset)));
        }
        Assert.Equal(all, paged);
    }

    /// <summary>A window that fits max_chars spills nothing, though the whole walk would; a window that does not fit
    /// says how many of the WINDOW's nodes it cut.</summary>
    [Fact]
    public void AWindowThatFitsSpillsNothingAndTheCutCountsTheWindow()
    {
        const int cap = 6000;
        Assert.Contains("spilled: complete result", Many(max: cap));

        var fits = Many(3, 10, max: cap);
        Assert.DoesNotContain("spilled:", fits);
        Assert.DoesNotContain("not shown", fits);
        Assert.Contains("[accounting] total=60 rendered=3 skipped=10", fits);

        var cut = Many(50, 0, max: cap);
        Assert.Contains("of 50 node(s) not shown: max_chars was reached.", cut);
        Assert.Contains("spilled: complete result (60 rows)", cut);
    }

    /// <summary>An offset past the end lists no nodes, says the shared past-the-end sentence, and keeps the whole counts.</summary>
    [Fact]
    public void AnOffsetPastTheEndListsNoNodesAndKeepsTheWholeCounts()
    {
        var text = Many(0, 500);
        Assert.Contains("offset=500 is past the end of the selection (60 node(s)) — the last page starts before it.", text);
        Assert.Contains("(60 nodes", text);
        Assert.Contains("nodes: from_source=60", text);
        Assert.DoesNotContain("\n" + AssetWalkWorld.ManyTex[0] + "\n", text, StringComparison.OrdinalIgnoreCase);
        using var json = JsonDocument.Parse(Many(0, 500, "json"));
        Assert.Empty(Paths(json.RootElement));
        Assert.Contains("past the end", json.RootElement.GetProperty("offset_note").GetString());
        Assert.Equal(60, json.RootElement.GetProperty("place_set").GetArrayLength());
    }

    /// <summary>F1-a: a past-the-end page whose place set does not fit max_chars spills and points at the file, as a page
    /// inside the walk does, text and json; a json page that fits a small cap spills nothing.</summary>
    [Fact]
    public void APastTheEndPageWhosePlaceSetDoesNotFitSpills()
    {
        const int cap = 2000;
        var text = Many(0, 500, max: cap);
        Assert.True(text.Length <= cap, $"{text.Length} chars");
        Assert.Contains("spilled: complete result (60 rows)", text);
        Assert.Contains("place set (60): the spilled file's rows with standing from_source", text);
        Assert.DoesNotContain("assets=[", text);

        var raw = Many(0, 500, "json", cap);
        Assert.True(raw.Length <= cap, $"{raw.Length} chars");
        using var json = JsonDocument.Parse(raw);
        Assert.True(json.RootElement.GetProperty("place_set_in_file").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("place_set").ValueKind);
        Assert.EndsWith(".jsonl", json.RootElement.GetProperty("spilled").GetProperty("path").GetString());

        using var fits = JsonDocument.Parse(Many(1, 0, "json", 6000));
        Assert.False(fits.RootElement.TryGetProperty("spilled", out _));
        Assert.Single(Paths(fits.RootElement));
    }

    /// <summary>At defaults a walk carries no accounting line (Aaron's pick, DECISIONS 2026-11-06 (25)): only a paged one does.</summary>
    [Fact]
    public void OnlyAPagedWalkCarriesTheAccounting()
    {
        Assert.DoesNotContain("[accounting]", Many());
        using var json = JsonDocument.Parse(Many(format: "json"));
        Assert.False(json.RootElement.TryGetProperty("accounting", out _));
        Assert.Contains("[accounting] total=60 rendered=5", Many(5));
    }

    /// <summary>links= with walk=true is refused by name before the walk runs: a walk with no seed would refuse otherwise.</summary>
    [Fact]
    public void LinksWithAWalkIsRefusedBeforeItRuns()
    {
        Assert.Equal(ReadSentences.WalkWithLinks, (string)AssetTools.AssetStatus(_w.Svc, walk: true, links: true));
        Assert.Equal(ReadSentences.WalkWithLinks, (string)AssetTools.AssetStatus(_w.Svc, asset_paths: new[] { AssetWalkWorld.CapeNif }, walk: true, links: true));
        // With counts_only= too, the shared counts_only+links sentence answers first, as on the row lane.
        Assert.Equal((string)AssetTools.AssetStatus(_w.Svc, asset_paths: new[] { AssetWalkWorld.CapeNif }, links: true, counts_only: true),
                     (string)AssetTools.AssetStatus(_w.Svc, asset_paths: new[] { AssetWalkWorld.CapeNif }, walk: true, links: true, counts_only: true));
    }

    /// <summary>to_file= on a walk writes every node (row_count == total) for reason to_file and renders the manifest only;
    /// limit= does not narrow it; with offset= or counts_only= it is refused in the row lane's words.</summary>
    [Fact]
    public void ToFileWritesEveryNodeAndRendersTheManifestOnly()
    {
        var file = File("walk");
        var text = Many(limit: 5, toFile: file);
        var lines = System.IO.File.ReadAllLines(file);
        using (var manifest = JsonDocument.Parse(lines[0]))
        {
            Assert.Equal(60, manifest.RootElement.GetProperty("row_count").GetInt32());
            Assert.Equal(60, manifest.RootElement.GetProperty("total").GetInt32());
        }
        Assert.Equal(60, lines.Length - 1);
        Assert.Contains("written at your request (to_file=)", text);
        Assert.DoesNotContain("\n" + AssetWalkWorld.ManyTex[0] + "\n", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[accounting]", text);
        using var json = JsonDocument.Parse(Many(toFile: File("walkj"), format: "json"));
        Assert.Empty(Paths(json.RootElement));
        Assert.True(json.RootElement.GetProperty("spilled").GetProperty("path").GetString()!.EndsWith(".jsonl"));

        foreach (var (offset, counts) in new[] { (3, false), (0, true) })
        {
            var row = (string)AssetTools.AssetStatus(_w.Svc, asset_paths: AssetWalkWorld.ManyTex, offset: offset, counts_only: counts, to_file: File("r"));
            Assert.StartsWith("error:", row);
            Assert.Equal(row, Many(offset: offset, counts: counts, toFile: File("w")));
        }
        Assert.Equal((string)AssetTools.AssetStatus(_w.Svc, asset_paths: AssetWalkWorld.ManyTex, to_file: "relative.jsonl"),
                     Many(toFile: "relative.jsonl"));
    }

    /// <summary>counts_only= on a walk: counts per edge and per standing equal a group-by of the walk's own nodes, no
    /// nodes listed; limit= caps the table rows; offset= is refused in the row lane's words.</summary>
    [Fact]
    public void CountsOnlyCountsByEdgeAndStandingWithNoNodes()
    {
        using var whole = JsonDocument.Parse(AssetTools.AssetStatus(_w.Svc, records: new[] { _w.Id("HcWalkHero") }, walk: true,
                                                                    source_provider: AssetWalkWorld.DonorMod, format: "json", max_chars: Big));
        var nodes = whole.RootElement.GetProperty("nodes").EnumerateArray().ToList();
        static Dictionary<string, int> By(IEnumerable<string> keys) => keys.GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
        static Dictionary<string, int> Rows(JsonElement axis)
            => axis.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("key").GetString()!, r => r.GetProperty("count").GetInt32());

        using var counts = JsonDocument.Parse(AssetTools.AssetStatus(_w.Svc, records: new[] { _w.Id("HcWalkHero") }, walk: true,
                                                                     source_provider: AssetWalkWorld.DonorMod, counts_only: true, format: "json"));
        var c = counts.RootElement;
        Assert.False(c.TryGetProperty("place_set", out _));
        Assert.Equal(By(nodes.Select(n => n.GetProperty("edge").GetString()!)), Rows(c.GetProperty("nodes_by_edge")));
        Assert.Equal(By(nodes.Select(n => n.GetProperty("standing").GetString()!)), Rows(c.GetProperty("nodes_by_standing")));
        Assert.Equal(nodes.Count, c.GetProperty("nodes").GetInt32());
        Assert.Equal(whole.RootElement.GetProperty("place_set").GetArrayLength(), c.GetProperty("place_set_count").GetInt32());

        var text = (string)AssetTools.AssetStatus(_w.Svc, records: new[] { _w.Id("HcWalkHero") }, walk: true, counts_only: true);
        Assert.Contains("nodes by edge (", text);
        Assert.Contains("nodes by standing (", text);
        Assert.DoesNotContain("\n  seed of ", text);
        Assert.DoesNotContain("spilled:", text);

        using var capped = JsonDocument.Parse(AssetTools.AssetStatus(_w.Svc, records: new[] { _w.Id("HcWalkHero") }, walk: true,
                                                                     counts_only: true, limit: 1, format: "json"));
        Assert.Single(capped.RootElement.GetProperty("nodes_by_edge").GetProperty("rows").EnumerateArray());

        var row = (string)AssetTools.AssetStatus(_w.Svc, asset_paths: AssetWalkWorld.ManyTex, counts_only: true, offset: 2);
        Assert.StartsWith("error:", row);
        Assert.Equal(row, Many(offset: 2, counts: true));
    }

    /// <summary>F1-b: a json counts_only walk whose thirty not-found notes pass max_chars stays inside it and says how many
    /// notes it cut; at a cap it fits, the document is the uncapped one.</summary>
    [Fact]
    public void AJsonCountsOnlyWalkKeepsToMaxChars()
    {
        var seeds = Enumerable.Range(0, 30).Select(i => $"0EEE{i:X2}:{AssetWalkWorld.Plugin}").Prepend(_w.Id("HcWalkHero")).ToArray();
        string Counts(int max) => AssetTools.AssetStatus(_w.Svc, records: seeds, walk: true, source_provider: AssetWalkWorld.DonorMod,
                                                         counts_only: true, format: "json", max_chars: max);
        var whole = Counts(Big);
        Assert.Equal(whole, Counts(whole.Length));
        Assert.DoesNotContain("_omitted", whole);

        int cap = whole.Length / 2;
        var cut = Counts(cap);
        using var doc = JsonDocument.Parse(cut);
        var r = doc.RootElement;
        Assert.True(cut.Length <= cap, $"{cut.Length} chars against {cap}");
        Assert.False(r.TryGetProperty("max_chars_overrun", out _));
        int notes = r.GetProperty("notes").GetArrayLength(), omitted = r.GetProperty("notes_omitted").GetInt32();
        Assert.True(omitted > 0, cut);
        Assert.Equal(30, notes + omitted);
    }

    /// <summary>A walk input given without the walk, and from_source= on a walk with no record seed, are each one note;
    /// a formids= walk with from_source= reads its face through it and has none.</summary>
    [Fact]
    public void AnUnusedWalkInputIsANote()
    {
        var fromSource = (string)AssetTools.AssetStatus(_w.Svc, asset_paths: new[] { AssetWalkWorld.CapeNif }, from_source: new[] { AssetWalkWorld.Plugin });
        Assert.Contains("[!] notes (1):", fromSource);
        Assert.Contains(ReadSentences.WalkOnlyInput("from_source="), fromSource);
        Assert.Contains("notes=1", fromSource);
        Assert.DoesNotContain("[!] under", fromSource);
        var provider = (string)AssetTools.AssetStatus(_w.Svc, asset_paths: new[] { AssetWalkWorld.CapeNif }, source_provider: "Cape", format: "json");
        using (var doc = JsonDocument.Parse(provider))
            Assert.Equal(ReadSentences.WalkOnlyInput("source_provider="), doc.RootElement.GetProperty("notes")[0].GetString());

        var pathWalk = (string)AssetTools.AssetStatus(_w.Svc, asset_paths: new[] { AssetWalkWorld.CapeNif }, walk: true, from_source: new[] { AssetWalkWorld.Plugin });
        Assert.Contains(ReadSentences.FromSourceWithoutRecordSeed, pathWalk);

        var faceWalk = (string)AssetTools.AssetStatus(_w.Svc, formids: new[] { _w.Id("HcWalkHero") }, walk: true, from_source: new[] { AssetWalkWorld.Plugin });
        Assert.DoesNotContain(ReadSentences.FromSourceWithoutRecordSeed, faceWalk);
        Assert.DoesNotContain(ReadSentences.WalkOnlyInput("from_source="), faceWalk);
    }

    /// <summary>Bug 7: a mesh naming one non-Data string at two sites, and a .pex whose parent cannot be read, give one
    /// row per path; an error node is counted under the file bound.</summary>
    [Fact]
    public void EveryPathIsOneRowUnderTheFileBound()
    {
        var dup = _w.Svc.AssetWalk(new[] { AssetWalkWorld.DupNif }, Array.Empty<Mutagen.Bethesda.Plugins.FormKey>(), null, null);
        Assert.Single(dup.Nodes, n => n.Path == AssetWalkWorld.DupBad);
        Assert.Equal(2, dup.Nodes.Count);

        var pex = _w.Svc.AssetWalk(new[] { AssetWalkWorld.BadPex }, Array.Empty<Mutagen.Bethesda.Plugins.FormKey>(), null, null);
        Assert.Single(pex.Nodes);
        Assert.Contains(pex.Notes, n => n.StartsWith(AssetWalkWorld.BadPex + ": its parent class was not read", StringComparison.OrdinalIgnoreCase));

        var prior = _w.Svc.AssetArea.MaxAssetPaths;
        _w.Svc.AssetArea.MaxAssetPaths = 1;
        try
        {
            var capped = _w.Svc.AssetWalk(new[] { AssetWalkWorld.DupNif }, Array.Empty<Mutagen.Bethesda.Plugins.FormKey>(), null, null);
            Assert.Single(capped.Nodes);
            Assert.Contains(capped.Notes, n => n.StartsWith("the walk stopped at 1 files"));
        }
        finally { _w.Svc.AssetArea.MaxAssetPaths = prior; }
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Base has four keywords; Extra four more and 2,500 weapons. The first 2,000 weapons (one scan chunk) link
/// Base's A1, A2 and Extra's X1, X2; the last 500 link B1, B2 and Y1, Y2, so each chunk names new targets.</summary>
public sealed class RenderDoorWorld : IDisposable
{
    public const int FirstChunk = 2000, SecondChunk = 500;
    readonly ScratchMo2 _mo2 = new("hc-render-door-");
    public readonly ModKey Base = new("HcRdBase", ModType.Master), Extra = new("HcRdExtra", ModType.Plugin);
    public LoadOrderService Svc { get; }
    public FormKey KwB1 { get; }
    public IReadOnlyList<string> WeaponIds { get; }
    public string Scratch => Path.Combine(_mo2.Root, "out");

    /// <summary>Add a third plugin to the profile without touching the other two, so the next read sees a new epoch.</summary>
    public void AddLate()
    {
        var late = new ModKey("HcRdLate", ModType.Plugin);
        new SkyrimMod(late, SkyrimRelease.SkyrimSE).BeginWrite.ToPath(_mo2.InMod("RdLate", late)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        var names = new[] { Base, Extra, late }.Select(k => k.FileName.String).ToList();
        _mo2.Profile(string.Join("\r\n", names) + "\r\n", string.Join("", names.Select(n => "*" + n + "\r\n")), "+RdLate\r\n+RdExtra\r\n+RdBase\r\n");
    }

    public RenderDoorWorld()
    {
        var b = new SkyrimMod(Base, SkyrimRelease.SkyrimSE);
        var bk = new[] { "A1", "A2", "B1", "B2" }.Select(n => { var k = b.Keywords.AddNew(); k.EditorID = "HcRdKw" + n; return k.FormKey; }).ToList();
        b.BeginWrite.ToPath(_mo2.InMod("RdBase", Base)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var x = new SkyrimMod(Extra, SkyrimRelease.SkyrimSE);
        var xk = new[] { "X1", "X2", "Y1", "Y2" }.Select(n => { var k = x.Keywords.AddNew(); k.EditorID = "HcRdKw" + n; return k.FormKey; }).ToList();
        var ids = new List<string>();
        for (int i = 0; i < FirstChunk + SecondChunk; i++)
        {
            var w = x.Weapons.AddNew();
            w.EditorID = "HcRdSword" + i;
            int o = i < FirstChunk ? 0 : 2;
            w.Keywords = new() { new FormLink<IKeywordGetter>(bk[o]), new FormLink<IKeywordGetter>(bk[o + 1]),
                                 new FormLink<IKeywordGetter>(xk[o]), new FormLink<IKeywordGetter>(xk[o + 1]) };
            ids.Add(ScratchMo2.Fid(w.FormKey));
        }
        x.BeginWrite.ToPath(_mo2.InMod("RdExtra", Extra)).WithLoadOrder(new ISkyrimModGetter[] { b }).Write();
        (KwB1, WeaponIds) = (bk[2], ids);

        var names = new[] { Base, Extra }.Select(k => k.FileName.String).ToList();
        _mo2.Profile(string.Join("\r\n", names) + "\r\n", string.Join("", names.Select(n => "*" + n + "\r\n")), "+RdExtra\r\n+RdBase\r\n");
        Directory.CreateDirectory(Scratch);
        Svc = _mo2.Open();
    }

    public void Dispose() { Svc.Dispose(); _mo2.Delete(); }
}

/// <summary>resolve_names on a scan names each chunk's links a group at a time, and a walk reads only the window it shows.</summary>
[Trait("tier", "integration")]
public sealed class RenderDoorTests : IClassFixture<RenderDoorWorld>
{
    readonly RenderDoorWorld _w;
    public RenderDoorTests(RenderDoorWorld w) => _w = w;

    const int Reached = RenderDoorWorld.SecondChunk + 1;   // the B1 reverse walk: every second-chunk weapon, seed included

    static RecordsTools.RecordsProject Kw(bool names = false) => new() { form = "fields", fields = new[] { "Keywords" }, depth = 2, resolve_names = names };
    static RecordsTools.RecordsWalk Reverse() => new() { direction = "reverse", max_nodes = 5000 };
    string Seed => ScratchMo2.Fid(_w.KwB1);
    static string Receipt(string text) => Regex.Match(text, @"read \d+ record bodies").Value;
    static string WindowNote(string text) => text.Split('\n').Single(l => l.StartsWith("window: ", StringComparison.Ordinal));
    static string SpillFile(string text) => File.ReadAllText(Regex.Match(text, @"[A-Za-z]:[\\/][^\s""']*?\.jsonl").Value);

    // One read of each (plugin, Keywords) group per chunk, not one per target: four targets per chunk, two per plugin.
    [Fact]
    public void ResolveNamesOnAScanReadsEachTargetGroupOncePerChunk()
    {
        var file = Path.Combine(_w.Scratch, "names.jsonl");
        var reads = GroupReadDelta.Of(_w.Svc, () => RecordsTools.Records(_w.Svc, types: new[] { "WEAP" }, to_file: file, project: Kw(names: true)));
        Assert.Equal(2, reads[("HcRdBase.esm", "Keywords")]);
        Assert.Equal(2, reads[("HcRdExtra.esp", "Keywords")]);
        Assert.Contains("HcRdKwY2", File.ReadAllText(file));
    }

    [Fact]
    public void AWalkWindowThatFitsReadsOnlyTheRowsItShows()
    {
        var text = RecordsTools.Records(_w.Svc, formids: new[] { Seed }, walk: Reverse(), limit: 10, project: Kw());
        Assert.Equal("read 10 record bodies", Receipt(text));
        Assert.Equal($"window: rows 1–10 of {Reached} (limit=10, offset=0)", WindowNote(text));
        var doc = JsonDocument.Parse(RecordsTools.Records(_w.Svc, formids: new[] { Seed }, walk: Reverse(), limit: 10, format: "json", project: Kw())).RootElement;
        Assert.Equal(10, doc.GetProperty("rows_read").GetInt32());
    }

    // The forward walk hands its reached set to the same list lane: 50 seed weapons and the four keywords they link.
    [Fact]
    public void AForwardWalkWindowReadsOnlyTheRowsItShows()
    {
        var text = RecordsTools.Records(_w.Svc, formids: _w.WeaponIds.Take(50).ToArray(), walk: new() { direction = "forward" }, limit: 5, project: Kw());
        Assert.Equal("read 5 record bodies", Receipt(text));
        Assert.Equal("window: rows 1–5 of 54 (limit=5, offset=0)", WindowNote(text));
    }

    // A cut window spills every row, so the receipt counts the window's bodies and the whole read's, and the window
    // note and the file match the same list read whole on the formids= lane, where no window read applies.
    [Fact]
    public void ACutWalkWindowSpillsTheWholeListAsTheWholeReadDoes()
    {
        var walked = RecordsTools.Records(_w.Svc, formids: new[] { Seed }, walk: Reverse(), limit: 40, max_chars: 3000, project: Kw());
        Assert.Equal($"read {40 + Reached} record bodies", Receipt(walked));
        var listed = RecordsTools.Records(_w.Svc, formids: _w.WeaponIds.Skip(RenderDoorWorld.FirstChunk).Prepend(Seed).ToArray(), limit: 40, max_chars: 3000, project: Kw());
        Assert.Equal(WindowNote(listed), WindowNote(walked));
        Assert.Equal(SpillFile(listed).Split('\n').Skip(1), SpillFile(walked).Split('\n').Skip(1));
    }

    [Fact]
    public void AnOffsetPastTheEndReadsTheWholeList()
    {
        var text = RecordsTools.Records(_w.Svc, formids: new[] { Seed }, walk: Reverse(), limit: 10, offset: 600, project: Kw());
        Assert.Equal($"read {Reached} record bodies", Receipt(text));
        Assert.Equal($"window: no rows — offset=600 is past the end of the {Reached}-row list", WindowNote(text));
    }

    // A cut named scan: only the spill file reads (and names) the whole chunk; each inline pass reads the rows it asks
    // for, as on main: 2,000 for the file plus the few the two inline passes render, never a second chunk.
    [Fact]
    public void ACutNamedScanReadsTheWholeChunkOnlyForItsSpillFile()
    {
        long before = _w.Svc.Counters.ScanRowReads;
        var text = RecordsTools.Records(_w.Svc, types: new[] { "WEAP" }, limit: RenderDoorWorld.FirstChunk, max_chars: 3000,
                                        project: new() { form = "fields", fields = new[] { "Keywords", "EditorID", "Name", "Data" }, depth = 2, resolve_names = true });
        Assert.Contains(".jsonl", text);
        Assert.InRange(_w.Svc.Counters.ScanRowReads - before, RenderDoorWorld.FirstChunk, 2 * RenderDoorWorld.FirstChunk - 1);
    }

    // The aggregate counts the whole reached set, never the window.
    [Fact]
    public void AnAggregateOverAWalkCountsEveryReachedRow()
    {
        var text = RecordsTools.Records(_w.Svc, formids: new[] { Seed }, walk: Reverse(), limit: 10,
                                        project: new() { form = "aggregate", group_by = "type" });
        Assert.Contains($"\n{Reached} record(s)", text);
    }

    [Fact]
    public void CountsOnlyOnAWalkReadsEveryRow()
    {
        var text = RecordsTools.Records(_w.Svc, formids: new[] { Seed }, walk: Reverse(), counts_only: true, limit: 10);
        Assert.Contains($"count={Reached} ok={Reached} errors=0", text);
    }
}

/// <summary>A load-order change between a cut walk window's read and its whole re-read.</summary>
[Collection(SerialCollection.Name)]   // sets the process-wide cut-window seam
[Trait("tier", "integration")]
public sealed class RenderDoorSeamTests
{
    // Refused like the window read's own tear: "error: " and the build the tear was found on, in text and json.
    [Theory]
    [InlineData("text")]
    [InlineData("json")]
    public void ACutWindowsWholeReadRefusesOnTheBuildItFoundTheTearOn(string format)
    {
        using var w = new RenderDoorWorld();
        RecordsTools.BeforeCutWindowWholeRead = w.AddLate;
        string answer;
        try
        {
            answer = RecordsTools.Records(w.Svc, formids: new[] { ScratchMo2.Fid(w.KwB1) }, walk: new() { direction = "reverse", max_nodes = 5000 },
                                          limit: 40, max_chars: 3000, format: format,
                                          project: new() { form = "fields", fields = new[] { "Keywords" }, depth = 2 });
        }
        finally { RecordsTools.BeforeCutWindowWholeRead = null; }
        var tear = Regex.Match(answer, @"deriving the selection \(epoch=([^)]+)\) and reading it \(epoch=([^)]+)\)");
        Assert.True(tear.Success, answer);
        Assert.NotEqual(tear.Groups[1].Value, tear.Groups[2].Value);
        if (format == "json")
            Assert.Equal(tear.Groups[2].Value, JsonDocument.Parse(answer).RootElement.GetProperty("epoch").GetString());
        else
        {
            Assert.StartsWith("error: the load order changed", answer);
            Assert.Contains("\nepoch=" + tear.Groups[2].Value, answer);
        }
    }
}

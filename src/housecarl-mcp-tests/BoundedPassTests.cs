using System.Text.RegularExpressions;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Every pass a capped text call makes is bounded by the cap it asks about, not by the size of the answer
/// (#986): the whole-first pass stops once past the cap, a cut scan reads only the bodies it lays, and a cut is decided
/// on a spill block sized from the selection, so a refused call builds no artifact.</summary>
public sealed class BoundedPassTests : IClassFixture<WideCutWorld>
{
    readonly WideCutWorld _w;
    public BoundedPassTests(WideCutWorld w) => _w = w;

    string[] TopicIds => _w.Topics.Select(t => $"{t.ID:X6}:{t.ModKey.FileName}").ToArray();

    static readonly KeyValuePair<string, string>[] NoEcho = Array.Empty<KeyValuePair<string, string>>();

    /// <summary>Six thousand identity rows at max_chars=200 (refused) and 4,000 (served cut), through the spilling lane
    /// and the plain one: no render the call makes is wider than the cap it was given or named plus one row.</summary>
    [Fact]
    public void EveryPassOfACappedCallIsBoundedByTheCapItAsksAbout()
    {
        var one = _w.Svc.ResolveRefs(TopicIds, null, out var epoch, out _);
        var rows = Enumerable.Repeat(one, 200).SelectMany(r => r).ToList();
        var laid = new List<int>();
        string At(int n, SpillState? sp, out bool t)
        {
            var r = Wire.RenderResolve(rows, n, epoch, sp, out t, "records  form=identity", (rows.Count, 5));
            laid.Add(r.Length);
            return r;
        }
        int whole = At(RenderCap.Whole, null, out _).Length;
        var dir = Path.Combine(Path.GetTempPath(), "hc-bounded-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (int cap in new[] { 200, 4_000 })
            {
                laid.Clear();
                var text = Artifacts.CeilingText(cap, At, () => ArtifactTarget.Named(Path.Combine(dir, $"r{cap}.jsonl")),
                    t => Artifacts.WriteResolve(rows, epoch.Epoch, t, "ceiling", NoEcho), Wire.EpochLine(epoch));
                int bound = (RenderFloorAssert.IsFloorRefusal(text) ? RenderFloorAssert.Named(text) : cap) + 400;

                Assert.True(laid.Max() <= bound, $"at max_chars={cap} a pass laid {laid.Max()} chars of a {whole}-char answer");
                Assert.True(whole > 50 * bound, $"the answer ({whole} chars) is not wide enough to tell");
            }
            laid.Clear();
            RenderCap.Capped(4_000, n => At(n, null, out _));
            Assert.True(laid.Max() <= 4_400, $"a plain capped pass laid {laid.Max()} chars of a {whole}-char answer");
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>A fields scan served cut reads the bodies of the rows it lays (and the one past the cap its whole-first
    /// pass stops on), not every body in its window.</summary>
    [Fact]
    public void ACutFieldsScanReadsOnlyTheBodiesItLays()
    {
        var fields = new[] { "EditorID" };
        var q = _w.Svc.CrossQuery(new[] { "DIAL" }, null, null, false, null, null, 100_000);
        string Call(ScanRows rows, int c) => RenderCap.Capped(c, n => Wire.RenderCrossQuery(rows, q, fields, n, false, null, out _));
        int cap;
        using (var probe = new ScanRows(_w.Svc, q, fields, 1, false, false, null, default))
            (cap, _) = RenderFloorAssert.ServedCut(c => Call(probe, c), t => t.Contains("truncated: rendered", StringComparison.Ordinal));

        using var rows = new ScanRows(_w.Svc, q, fields, 1, false, false, null, default);
        var cut = Call(rows, cap);

        int shown = int.Parse(Regex.Match(cut, @"rendered (\d+) of").Groups[1].Value);
        Assert.True(rows.BodiesRead < q.Keys.Count, $"read all {q.Keys.Count} bodies to lay {shown}");
        Assert.True(rows.BodiesRead <= shown + 5, $"read {rows.BodiesRead} bodies to lay {shown}");
    }

    /// <summary>A cut refused below its floor sizes its spill block and writes nothing: the artifact writer is handed a
    /// sizing target only. The call at the cap it names is served cut and writes the artifact once.</summary>
    [Fact]
    public void ARefusedCutBuildsNoArtifactAndAServedOneWritesItOnce()
    {
        var rows = _w.Svc.ResolveRefs(TopicIds, null, out var epoch, out _);
        var dir = Path.Combine(Path.GetTempPath(), "hc-sized-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "records_x.jsonl");
        var handed = new List<bool>();
        string Call(int cap) => Artifacts.CeilingText(cap,
            (int n, SpillState? sp, out bool t) => Wire.RenderResolve(rows, n, epoch, sp, out t, "records  form=identity", (rows.Count, 5)),
            () => ArtifactTarget.Named(path),
            t => { handed.Add(t.SizeOnly); return Artifacts.WriteResolve(rows, epoch.Epoch, t, "ceiling", NoEcho); },
            Wire.EpochLine(epoch));
        try
        {
            var refused = Call(200);

            Assert.True(RenderFloorAssert.IsFloorRefusal(refused), refused);
            Assert.Equal(new[] { true }, handed);
            Assert.False(File.Exists(path));

            handed.Clear();
            var served = RenderFloorAssert.RefusesAndTheNamedCapFits(refused, 200, Call);

            Assert.Contains("spilled: complete result (30 rows) -> " + path, served);
            Assert.Equal(new[] { true, false }, handed);
            Assert.True(File.Exists(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>A fields scan's spill block is sized from its selection with no body read, and is the block the written
    /// artifact prints: the width depends on the file name and the counts per type, not on the bodies.</summary>
    [Fact]
    public void AScanSpillBlockIsSizedFromTheSelectionWithNoBodyRead()
    {
        var fields = new[] { "EditorID" };
        var q = _w.Svc.CrossQuery(new[] { "DIAL" }, null, null, false, null, null, 100_000);
        var counters = q.Pin!.Resolver.Counters;
        var dir = Path.Combine(Path.GetTempPath(), "hc-sizescan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "records_x.jsonl");
        try
        {
            long before = counters.KeysWanted;
            var sized = Artifacts.WriteCrossQuery(_w.Svc, q, fields, false, false, 1, ArtifactTarget.Sizing(path), "ceiling", NoEcho).Spill!;

            Assert.Equal(before, counters.KeysWanted);
            Assert.False(File.Exists(path));

            var written = Artifacts.WriteCrossQuery(_w.Svc, q, fields, false, false, 1, ArtifactTarget.Named(path), "ceiling", NoEcho).Spill!;

            Assert.True(counters.KeysWanted > before, "the written artifact read no body, so the sizing proves nothing");
            Assert.Equal(Wire.SpillText(SpillState.Spilled(written, manifestOnly: false)),
                         Wire.SpillText(SpillState.Spilled(sized, manifestOnly: false)));
        }
        finally { Directory.Delete(dir, true); }
    }
}

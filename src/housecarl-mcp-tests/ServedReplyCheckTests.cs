using System.Reflection;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A capped text reply is checked against its cap on the render actually served, and sizing a spill block is
/// only a hint (#986): the checks after the write, the flag that travels with the served render, and the sizing passes
/// that read no more than they must.</summary>
public sealed class ServedReplyCheckTests : IClassFixture<WideCutWorld>
{
    readonly WideCutWorld _w;
    public ServedReplyCheckTests(WideCutWorld w) => _w = w;

    string[] TopicIds => _w.Topics.Select(t => $"{t.ID:X6}:{t.ModKey.FileName}").ToArray();

    static readonly KeyValuePair<string, string>[] NoEcho = Array.Empty<KeyValuePair<string, string>>();

    static string Block(SpillInfo s) => Wire.SpillText(SpillState.Spilled(s, manifestOnly: false));

    /// <summary>Sizing is handed one row where the write stamps thirty, so its block is narrower than the one printed: at
    /// every cap the reply fits or is refused, a refusal leaves no spill file, and some cap is refused only after the write.</summary>
    [Fact]
    public void AReplyOverItsCapAfterTheWriteIsRefusedAndItsSpillRemoved()
    {
        var rows = _w.Svc.ResolveRefs(TopicIds, null, out var epoch, out _);
        var dir = Path.Combine(Path.GetTempPath(), "hc-exitcheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        int refusedAfterWrite = 0;
        try
        {
            for (int cap = 100; cap <= 1_500; cap++)
            {
                var path = Path.Combine(dir, $"r{cap}.jsonl");
                bool wrote = false;
                var text = Artifacts.CeilingText(cap,
                    (int n, SpillState? sp, WholePass? w, out bool t) =>
                        Wire.RenderResolve(rows, n, epoch, sp, out t, "records  form=identity", (rows.Count, 5), w),
                    Artifacts.SpillTo.At(path),
                    t =>
                    {
                        wrote |= !t.SizeOnly;
                        return Artifacts.WriteResolve(t.SizeOnly ? rows.Take(1).ToList() : rows, epoch.Epoch, t, "ceiling", NoEcho);
                    },
                    Wire.EpochLine(epoch));
                bool refused = RenderFloorAssert.IsFloorRefusal(text);

                Assert.True(refused || text.Length <= cap, $"{text.Length} chars served at max_chars={cap}");
                if (refused) Assert.False(File.Exists(path), $"the refusal at max_chars={cap} left its spill file");
                if (refused && wrote) refusedAfterWrite++;
            }
        }
        finally { Directory.Delete(dir, true); }
        Assert.True(refusedAfterWrite > 0, "no cap was refused after its write, so the check after it was never reached");
    }

    /// <summary>The scan render's truncated flag is the served render's: false for a whole answer, and for a refusal the
    /// cut of the render at the cap the refusal was decided on, never of a grow round or a whole pass.</summary>
    [Fact]
    public void TruncatedTravelsWithTheRenderTheReplyComesFrom()
    {
        var q = _w.Svc.CrossQuery(new[] { "DIAL" }, null, null, false, null, null, 1);

        var whole = Wire.RenderCrossQuery(_w.Svc, q, null, 80_000, false, false, 1, null, out bool wholeCut);
        var refused = Wire.RenderCrossQuery(_w.Svc, q, null, 40, false, false, 1, null, out bool refusedCut);

        Assert.False(RenderFloorAssert.IsFloorRefusal(whole), whole);
        Assert.False(wholeCut, "a whole answer came back truncated");
        Assert.True(RenderFloorAssert.IsFloorRefusal(refused), refused);
        Assert.True(refusedCut, "a refusal decided on a render that cut its row came back untruncated");
    }

    /// <summary>An asset_status call refused below its floor builds no record index: the stamp its spill would carry is
    /// taken only when a cut is served. The call at the named cap is served, spilling, and stamps it.</summary>
    [Fact]
    public void ARefusedAssetStatusCallBuildsNoRecordIndex()
    {
        var home = Path.Combine(_w.Root, "fresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var svc = LoadOrderService.WithInstance(Path.Combine(_w.Root, "inst"), 0, new UserConfigStore(Path.Combine(home, "user.json")));
        bool IndexBuilt() => typeof(LoadOrderService).GetField("_resolver", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(svc) is not null;
        string Call(int c) => AssetTools.AssetStatus(svc, under: new[] { WideCutWorld.MeshDir }, max_chars: c);

        var refused = Call(200);

        Assert.True(RenderFloorAssert.IsFloorRefusal(refused), refused);
        Assert.False(IndexBuilt(), "a refused asset_status call built the record index");
        var served = RenderFloorAssert.RefusesAndTheNamedCapFits(refused, 200, Call);
        Assert.Contains("spilled: complete result", served);
        Assert.True(IndexBuilt(), "the served spill carries no stamp, so the test proves nothing");
    }

    /// <summary>A render that cannot stop early lays its whole answer once per call, however many grow rounds the refusal
    /// takes: a floor that prints the cap back takes two here.</summary>
    [Fact]
    public void ARenderThatCannotStopLaysItsWholeAnswerOncePerCall()
    {
        int wholes = 0, renders = 0;
        string At(int n)
        {
            renders++;
            if (n == RenderCap.Whole) wholes++;
            return new string('x', 995) + " max_chars=" + n;
        }

        var r = RenderCap.CappedOnce(100, At);

        Assert.True(RenderFloorAssert.IsFloorRefusal(r), r);
        Assert.True(renders > 2, $"{renders} renders: the refusal took no grow round, so the test proves nothing");
        Assert.Equal(1, wholes);
    }
}

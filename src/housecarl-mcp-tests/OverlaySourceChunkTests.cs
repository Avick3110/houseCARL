using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>An overlay <c>source=</c> past one <see cref="BodyPrefetch.ChunkRows"/> chunk reads every chunk's winners
/// through the door, not the rows past the first chunk one at a time (#1282).</summary>
[Trait("tier", "integration")]
public sealed class OverlaySourceChunkTests : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-overlay-chunk-");
    static readonly ModKey A = new("HcOverlayChunk", ModType.Plugin);
    const int Extra = 10;
    readonly List<FormKey> _keys = new();
    readonly LoadOrderService _svc;

    public OverlaySourceChunkTests()
    {
        var a = new SkyrimMod(A, SkyrimRelease.SkyrimSE);
        for (int i = 0; i < BodyPrefetch.ChunkRows + Extra; i++) _keys.Add(a.Keywords.AddNew("HcChunkKw" + i).FormKey);
        a.BeginWrite.ToPath(_mo2.InMod("Chunk", A)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        var name = A.FileName.String;
        _mo2.Profile(name + "\r\n", "*" + name + "\r\n", "+Chunk\r\n");
        _svc = _mo2.Open();
    }

    public void Dispose() { _svc.Dispose(); _mo2.Delete(); }

    /// <summary>One plugin, two chunks: two walks, where a per-row read of the second chunk would walk once per row.</summary>
    [Fact]
    public void EachChunkReadsItsWinnersThroughTheDoor()
    {
        long passes = _svc.Counters.CollectPasses;
        var r = RecordsTools.Records(_svc, formids: _keys.Select(RecordsWorld.Fid).ToArray(),
                                     source: ApplyGuardWorld.Je("{\"overlay\": \"skypatcher\", \"state\": \"post\"}"),
                                     project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "EditorID" } });
        Assert.False(r.StartsWith("error:"), r);
        Assert.Equal(2, _svc.Counters.CollectPasses - passes);
    }
}

using System;
using System.Threading.Tasks;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>#926: a mesh whose corrupted count makes NiflySharp read past a block's stored size comes back from
/// NifService.Inspect as a named error in well under a second, instead of running the process out of memory.</summary>
[Trait("tier", "unit")]
public sealed class NifInspectMalformedTests
{
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    // The first input the random 4-byte corruption of the authored mesh ran out of memory on (seed 10).
    [Fact]
    public void TheSeedTenCorruptionIsANamedErrorNotARunaway()
    {
        var o = InspectWithin(Limit, (948, 0xC0), (756, 0xB1), (719, 0x4C), (385, 0x75));

        Assert.Null(o.Inspect);
        Assert.Contains("malformed", o.Error);
        Assert.Contains("Block 0 (NiNode) read 556 bytes, past its stored size of 88", o.Error);
    }

    // Byte 385 alone is the low byte of the root node's effect count; 0x75 makes it 117 refs, past the block's end.
    [Fact]
    public void TheRootEffectCountByteIsANamedErrorNotARunaway()
    {
        var o = InspectWithin(Limit, (385, 0x75));

        Assert.Null(o.Inspect);
        Assert.Contains("malformed", o.Error);
        Assert.Contains("Block 0 (NiNode) read 556 bytes, past its stored size of 88", o.Error);
    }

    static NifInspectOutcome InspectWithin(TimeSpan limit, params (int Pos, byte Val)[] edits)
    {
        var bytes = NifInspectFixtures.BuildSyntheticSe();
        Assert.Equal(998, bytes.Length);
        foreach (var (pos, val) in edits)
            bytes[pos] = val;

        var inspect = Task.Run(() => NifService.Inspect(bytes));
        Assert.True(inspect.Wait(limit), $"Inspect did not return within {limit.TotalSeconds:0} s");
        return inspect.Result;
    }
}

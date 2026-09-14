using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>from_source= is honoured only by op='CopyFrom', so naming it on any other verb is refused the way from=
/// already is — the op index and the verb it got (#630). Accepted, it wrote off the load-order winner and reported
/// success. The refusal writes nothing, so the shared world is untouched.</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class ApplyFromSourceVerbGateTests : RecordsTestBase
{
    public ApplyFromSourceVerbGateTests(RecordsFixture f) : base(f) { }

    [Fact]
    public void ANonCopyFromOpNamingFromSourceIsRefusedByOpIndexAndVerb()
    {
        var text = ApplyTools.Apply(Svc,
            ops: Je("[{\"formid\":\"" + Fid(W.Weapons[0]) + "\",\"field_path\":\"BasicStats.Damage\","
                  + "\"op\":\"Set\",\"value\":\"12\",\"from_source\":\"" + W.OldName + "\"}]"));

        Refused(text, "ops[0]: from_source=", "op='CopyFrom'", "got op='Set'");
        Assert.Contains("NOTHING written", text);
    }

    [Fact]
    public void AnOpThatOmitsOpEntirelyIsStillJudgedOnItsDefaultVerb()
    {
        var text = ApplyTools.Apply(Svc,
            ops: Je("[{\"formid\":\"" + Fid(W.Weapons[0]) + "\",\"field_path\":\"BasicStats.Damage\","
                  + "\"value\":\"12\",\"from_source\":\"" + W.OldName + "\"}]"));

        Refused(text, "ops[0]: from_source=", "got op='Set'");
    }
}

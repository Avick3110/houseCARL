using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The verb bound on a compose's nested <c>sets</c>. The nested writes replay through the verb engine itself, so
/// every verb that acts on a path from a root works there — except <c>CopyFrom</c>, which reads a SOURCE RECORD and
/// the nested shape has no slot to name one. It used to pass the leaf gate on the strength of the same rulebook
/// switch an op's verb goes through, and then throw at apply as a verb the leaf does not take.
///
/// <para>Dry runs: the shared world must stay unwritten.</para>
/// </summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class ComposeNestedVerbTests : RecordsTestBase
{
    public ComposeNestedVerbTests(RecordsFixture f) : base(f) { }

    string ComposeWithNestedSet(string set) => ApplyTools.Apply(Svc,
        ops: Je($@"[{{""formid"":""{Fid(W.MgefB)}"",""field_path"":""Conditions[0].Data"",""op"":""Set"",""compose"":{{""type"":""GetActorValueConditionData"",""sets"":[{set}]}}}}]"),
        dry_run: true);

    /// <summary>CopyFrom in a nested set is refused at pre-flight, and the refusal says where the copy belongs.</summary>
    [Fact]
    public void CopyFromInANestedSetIsRefusedAndSentToItsOwnOp()
    {
        var r = ComposeWithNestedSet(@"{""path"":""ActorValue"",""verb"":""CopyFrom""}");

        Refused(r, "CopyFrom", "nested sets", "from_source=");
        Assert.DoesNotContain("the apply threw", r);
    }

    /// <summary>The same nested set with a verb the shape does take still composes — the refusal is about the one
    /// verb, not about nested sets.</summary>
    [Fact]
    public void ANestedSetWithAVerbTheLeafTakesStillComposes()
        => Served(ComposeWithNestedSet(@"{""path"":""ActorValue"",""value"":""Destruction""}"),
                  "Set Conditions[0].Data");
}

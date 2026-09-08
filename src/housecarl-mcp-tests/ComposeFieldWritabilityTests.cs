using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// A compose FIELD the apply cannot set. <c>BuildStruct</c>'s field pass throws on a property with no setter, and a
/// read-only field is the natural thing to copy out of a read and back into a compose — a condition arm's
/// <c>Function</c> is the discriminator the read shows first. Pre-flight used to check only that the field existed
/// and that its value coerced, so the call was accepted and then threw mid-apply: the same accept-then-throw as the
/// constructor-argument arm this PR closes, one field pass along.
///
/// <para>Dry runs: the shared world must stay unwritten.</para>
/// </summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class ComposeFieldWritabilityTests : RecordsTestBase
{
    string ComposeConditionData(string fields) => ApplyTools.Apply(Svc,
        ops: Je($@"[{{""formid"":""{Fid(W.MgefB)}"",""field_path"":""Conditions[0].Data"",""op"":""Set"",""compose"":{{""type"":""GetActorValueConditionData"",""fields"":{fields}}}}}]"),
        dry_run: true);

    public ComposeFieldWritabilityTests(RecordsFixture f) : base(f) { }

    /// <summary>The read-only discriminator named alongside a settable field is refused at pre-flight, in the
    /// rulebook's own words for a discriminator — not accepted and thrown at apply.</summary>
    [Fact]
    public void AReadOnlyComposeFieldIsRefusedBeforeAnythingIsWritten()
    {
        var r = ComposeConditionData(@"{""Function"":""GetActorValue"",""ActorValue"":""Destruction""}");

        Refused(r, "'Function'", "discriminator");
        Assert.DoesNotContain("the apply threw", r);
    }

    /// <summary>The same compose without it still composes — the refusal is about the one field, not the arm.</summary>
    [Fact]
    public void TheSameComposeWithoutThatFieldStillComposes()
        => Served(ComposeConditionData(@"{""ActorValue"":""Destruction""}"), "Set Conditions[0].Data");
}

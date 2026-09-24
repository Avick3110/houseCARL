using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The verb bound on a compose's nested <c>sets</c>. The nested writes replay through the verb engine itself, so a
/// verb the nested shape can FEED works there — but a nested set is <c>{path, verb, value, key, compose}</c>, with
/// no member carrying <c>ReplaceAll</c>'s values, <c>Merge</c>'s entries or <c>CopyFrom</c>'s source record. All
/// three used to pass the leaf gate on the strength of the same rulebook switch an op's verb goes through, and then
/// consume nothing: ReplaceAll replaced with an empty list, Merge merged nothing, and CopyFrom reached apply as a
/// verb the leaf does not take. The first two reported the write as landed.
///
/// <para>Dry runs, and the lane arms go to the rulebook directly: the shared world must stay unwritten.</para>
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

    /// <summary>ReplaceAll names the slot it reads and the fact the nested shape has no member for it — rather than
    /// replacing with the empty list a nested set can only ever supply.</summary>
    [Fact]
    public void ReplaceAllInANestedSetIsRefusedNamingTheSlotItCannotBeGiven()
        => Refused(ComposeWithNestedSet(@"{""path"":""ActorValue"",""verb"":""ReplaceAll"",""value"":""Destruction""}"),
                   "ReplaceAll", "values=", "nested set");

    /// <summary>Merge, the same, for the slot a dict verb reads.</summary>
    [Fact]
    public void MergeInANestedSetIsRefusedNamingTheSlotItCannotBeGiven()
        => Refused(ComposeWithNestedSet(@"{""path"":""ActorValue"",""verb"":""Merge""}"),
                   "Merge", "entries=", "nested set");

    /// <summary>A verb the nested shape CAN feed still composes — the refusals are about the three, not about
    /// nested sets.</summary>
    [Fact]
    public void ANestedSetWithAVerbTheShapeCanFeedStillComposes()
        => Served(ComposeWithNestedSet(@"{""path"":""ActorValue"",""value"":""Destruction""}"),
                  "Set Conditions[0].Data");

    // ---- the two lanes' remedies, read off the rulebook ------------------------------------------------
    //  Straight to CorpusRulebook rather than through housecarl_create: create has no dry_run, so a call that
    //  stopped being refused — the one regression these arms exist to catch — would allocate a patch in the shared
    //  fixture's ModsDir. Validate() writes nothing whatever it answers.

    static WriteRequest NestedCopyFrom() => new()
    {
        RecordType = "LeveledItem",
        Path = new[] { "Entries" },
        Verb = "Add",
        Struct = new StructSpec
        {
            Type = "LeveledItemEntry",
            Sets = new List<WriteRequest>
            {
                new() { RecordType = "LeveledItemEntry", Path = new[] { "Data", "Reference" }, Verb = "CopyFrom" },
            },
        },
    };

    /// <summary>On the EDIT lane the remedy is the op that surface has.</summary>
    [Fact]
    public void TheEditLaneIsSentToItsOwnCopyFromOp()
    {
        var r = TestCorpus.Rulebook.Validate(NestedCopyFrom());

        Assert.NotNull(r);
        Assert.Contains("from_source=", r);
    }

    /// <summary>On the CREATE lane — the same gate, reached with a sibling set — it cannot be, because that surface
    /// publishes no CopyFrom op to put from= on; naming one would land the caller on a second refusal.</summary>
    [Fact]
    public void TheCreateLaneGetsARemedyThatLaneCanFollow()
    {
        var r = TestCorpus.Rulebook.Validate(NestedCopyFrom(), new[] { "HcSomeSibling" });

        Assert.NotNull(r);
        Assert.Contains(ToolNames.Apply, r);
        Assert.DoesNotContain("from_source=", r);
    }
}

using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The create engine has ONE vocabulary — <c>records[r]</c> and <c>ops[i]</c>, the members
/// <c>housecarl_create</c> publishes — so a caller that passes no vocabulary of its own still gets that spelling and
/// never the deleted 1.x <c>op[i]</c> or the singular <c>record[r]</c> (#471). Refused before any write, so the
/// shared world is untouched.</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class CreateOpSpellingTests : RecordsTestBase
{
    public CreateOpSpellingTests(RecordsFixture f) : base(f) { }

    [Fact]
    public void AMalformedCreateOpIsLabelledOpsIndex()
    {
        var o = Svc.CreateRecordsBatch(
            new[]
            {
                new CreateOp
                {
                    RecordType = "Keyword", Editorid = "HcOpsLbl",
                    Operations = new[] { new BulkOp { Value = "x" } },
                },
            },
            "HcOpsLblPatch", null);

        Assert.False(o.Success);
        Assert.Contains("records[0]: ops[0]: field_path is required", o.Error);
        Assert.DoesNotContain("op[0]:", o.Error);
        Assert.DoesNotContain("record[0]:", o.Error);
    }
}

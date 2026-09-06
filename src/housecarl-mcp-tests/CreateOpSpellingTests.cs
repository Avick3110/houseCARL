using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The create engine has ONE op vocabulary — <c>ops[i]</c>, the member <c>housecarl_create</c> publishes
/// — so a caller that passes no vocabulary of its own still gets that spelling and never the deleted 1.x
/// <c>op[i]</c> (#471). Refused before any write, so the shared world is untouched.</summary>
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
        Assert.Contains("ops[0]: field_path is required", o.Error);
        Assert.DoesNotContain("op[0]:", o.Error);
    }
}

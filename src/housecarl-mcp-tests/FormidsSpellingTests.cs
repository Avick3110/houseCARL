using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A malformed FormID is labelled <c>formids[i]</c> — the member <c>housecarl_forward</c> and
/// <c>housecarl_remove</c> both publish — and never the singular <c>formid[i]</c>, which points at a parameter no
/// tool declares (#471). Both refusals land before the write gate, so the shared world is untouched.</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class FormidsSpellingTests : RecordsTestBase
{
    public FormidsSpellingTests(RecordsFixture f) : base(f) { }

    [Fact]
    public void ForwardLabelsAMalformedFormidFormidsIndex()
    {
        var text = ForwardTools.Forward(Svc, formids: new[] { "NOTAFORMID" }, source: W.MasterName, patch: "HcFwdLbl");

        Refused(text, "formids[0]");
        Assert.DoesNotContain("formid[0]", text);
    }

    [Fact]
    public void RemoveLabelsAMalformedFormidFormidsIndex()
    {
        var text = RemoveTools.Remove(Svc, formids: new[] { "NOTAFORMID" }, into: "HcRmLbl.esp");

        Refused(text, "formids[0]");
        Assert.DoesNotContain("formid[0]", text);
    }
}

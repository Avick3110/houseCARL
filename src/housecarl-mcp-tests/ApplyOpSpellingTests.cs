using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>An inline <c>housecarl_apply</c> op is labelled <c>ops[i]</c> — the member the tool publishes — at both
/// altitudes that can refuse one: the tool's own mapping and the engine's (#471). Neither refusal writes, so the
/// shared world is untouched.</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class ApplyOpSpellingTests : RecordsTestBase
{
    public ApplyOpSpellingTests(RecordsFixture f) : base(f) { }

    [Fact]
    public void TheToolLabelsAnInlineOpOpsIndex()
    {
        var text = ApplyTools.Apply(Svc,
            ops: Je(@"[{""formid"":""000ABC:Skyrim.esm"",""field_path"":""Name"",""op"":""Set"",""value"":""x"",""from"":""000DEF:Skyrim.esm""}]"));

        Refused(text, "ops[0]: from= names the SOURCE RECORD");
        Assert.DoesNotContain("op[0]:", text);
    }

    [Fact]
    public void TheEngineLabelsAnInlineOpOpsIndex()
    {
        var text = ApplyTools.Apply(Svc,
            ops: Je(@"[{""field_path"":""Name"",""op"":""Set"",""value"":""x""}]"));

        Refused(text, "ops[0]: formid is required");
        Assert.DoesNotContain("op[0]:", text);
    }
}

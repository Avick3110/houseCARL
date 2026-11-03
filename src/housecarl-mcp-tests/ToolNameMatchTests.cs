using HousecarlGenerator;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The whole-identifier matcher the retired-name refusal tests rely on. Pins unchanged behaviour: a name
/// inside a longer name, on either side, is not a reference.</summary>
[Trait("tier", "unit")]
public sealed class ToolNameMatchTests
{
    [Fact]
    public void ANameSeenOnlyAsALongerNamesPrefixIsNotReferenced() =>
        Assert.False(ToolNameMatch.ReferencedAtBoundary("- `housecarl_create_record` — the 1.x tool", "housecarl_create"));

    [Fact]
    public void ANameSeenOnlyAsALongerNamesSuffixIsNotReferenced() =>
        Assert.False(ToolNameMatch.ReferencedAtBoundary("- `bulk-record-jobs` (catalogues, audits)", "record-jobs"));

    [Fact]
    public void AWholeNameIsReferenced() =>
        Assert.True(ToolNameMatch.ReferencedAtBoundary("- `housecarl_create` — the 2.0 tool", "housecarl_create"));
}

using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The pre-flight refusal for an unknown field is the only on-surface schema source for a record type with no
/// instance in the load order, so it must not cut the list short with nowhere to look for the rest (#688). It now
/// lists every field up to a cap, and past the cap names the mutagen-reference skill.
///
/// <para>Corpus-only, so it needs no records — the world is here for the generated corpus
/// <c>CorpusRulebook.CorpusPath</c> points at.</para>
/// </summary>
[Trait("tier", "integration")]
[Collection("bulk-records")]
public sealed class FieldNotFoundListTests : BulkRecordsTestBase
{
    public FieldNotFoundListTests(BulkRecordsFixture f) : base(f) { }

    static string Refusal(string recordType, string field)
    {
        var r = CorpusRulebook.Load().Validate(new WriteRequest
        {
            RecordType = recordType, Path = new[] { field }, Verb = "Set", Value = "1",
        });
        Assert.NotNull(r);
        return r!;
    }

    /// <summary>The reported call: a PlacedArrow create with a bad field. Its 29 fields all fit under the cap, so
    /// the refusal names every one of them — including 'Reflections', the field fourteen further probe calls never
    /// reached behind the old cut at twelve.</summary>
    [Fact]
    public void AShortTypeListsEveryFieldAndCutsNothing()
    {
        var r = Refusal("PlacedArrow", "NoSuchField");

        Assert.Contains("Reflections", r);
        Assert.Contains("VirtualMachineAdapter", r);
        Assert.DoesNotContain("more)", r);
    }

    /// <summary>Past the cap the list still cuts — 83 names is not a sentence — but the same sentence says where
    /// the rest are, so the caller is never left with an unreachable remainder.</summary>
    [Fact]
    public void ALongTypeCutsButNamesWhereTheRestAre()
    {
        var r = Refusal("Race", "NoSuchField");

        Assert.Contains("more —", r);
        Assert.Contains("mutagen-reference", r);
    }
}

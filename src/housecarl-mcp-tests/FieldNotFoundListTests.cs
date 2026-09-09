using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// A write call is the only place on the surface that shows a record type's schema without an instance of it in
/// the load order, so its pre-flight refusal for an unknown field must not cut the list short with nowhere to look
/// for the rest (#688). It now lists every field up to a cap plus a slack, and past that names the
/// mutagen-reference skill.
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

    /// <summary>Every field name the corpus carries for <paramref name="recordType"/> must appear in the refusal —
    /// "cuts nothing" said directly, rather than by the absence of one literal from the message.</summary>
    static void AssertNamesEveryField(string recordType, string refusal)
    {
        var fields = CorpusRulebook.LoadCorpus().Types[recordType].Fields.Select(f => f.Name).ToList();
        Assert.NotEmpty(fields);
        foreach (var f in fields) Assert.Contains(f, refusal);
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
        AssertNamesEveryField("PlacedArrow", r);
    }

    /// <summary>A type just past the cap prints whole rather than hiding a name behind a pointer longer than the
    /// name: Weapon runs 41 fields, one over the cap and inside the slack, so nothing is cut and the caller reads
    /// the field a bare cap would have hidden ('VirtualMachineAdapter', last of the alphabetical list) in the
    /// refusal itself.</summary>
    [Fact]
    public void ATypeInsideTheSlackPrintsWholeRatherThanHidingAName()
    {
        var r = Refusal("Weapon", "NoSuchField");

        Assert.DoesNotContain("mutagen-reference", r);
        AssertNamesEveryField("Weapon", r);
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

using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The two pre-flight refusals a caller hits when editing one element of a POLYMORPHIC collection — an AI
/// package's <c>Data</c> dict of <c>APackageData</c> arms is the reported shape (#319). Both were correct and both
/// dead-ended: neither named the call that works, so landing one edit took four tries.
///
/// <para>Corpus-only, so it needs no records — the world is here for the generated corpus
/// <c>CorpusRulebook.CorpusPath</c> points at.</para>
/// </summary>
[Trait("tier", "integration")]
[Collection("bulk-records")]
public sealed class ElementRefusalRemedyTests : BulkRecordsTestBase
{
    public ElementRefusalRemedyTests(BulkRecordsFixture f) : base(f) { }

    static CorpusRulebook Book() => CorpusRulebook.Load();

    static string Refusal(string[] path, string verb, string? key = null, string? value = null)
    {
        var r = Book().Validate(new WriteRequest
        {
            RecordType = "Package", Path = path, Verb = verb, Key = key, Value = value,
        });
        Assert.NotNull(r);
        return r!;
    }

    /// <summary>Reaching THROUGH a polymorphic element to a field whose arms disagree on shape: the validator still
    /// refuses to pick an arm, but it now hands back the container call — the field, the caller's own key, and the
    /// verbs that shape takes — instead of "target a field whose shape is unambiguous".</summary>
    [Fact]
    public void AConflictingArmFieldUnderADictElementNamesTheContainerCall()
    {
        var r = Refusal(new[] { "Data[7]", "Data" }, "Set", value: "True");

        Assert.Contains("CONFLICTING shapes", r);
        Assert.Contains("field_path='Data'", r);
        Assert.Contains("key='7'", r);
        Assert.Contains("compose=", r);
    }

    /// <summary>…and the verbs it names are the DICT ones. Naming <c>SetAtIndex</c> here is the wrong turn the
    /// reporter took on their second call.</summary>
    [Fact]
    public void TheContainerCallOnADictNamesNoIndexVerb()
        => Assert.DoesNotContain("SetAtIndex", Refusal(new[] { "Data[7]", "Data" }, "Set", value: "True"));

    /// <summary>A bracket at the LEAF was already told the rule and the verb menu; it now also carries the path and
    /// the key the caller typed, which is the difference between a shape to fill in and a call to make.</summary>
    [Fact]
    public void ABracketedLeafNamesThePathAndKeyTheCallerTyped()
    {
        var r = Refusal(new[] { "Data[7]" }, "Set");

        Assert.Contains("brackets a collection element at the LEAF", r);
        Assert.Contains("field_path='Data'", r);
        Assert.Contains("key='7'", r);
    }

    /// <summary>The container path keeps the hops ABOVE it — they are still navigation — and drops only its own
    /// bracket, so a nested container is named in full rather than by its last segment.</summary>
    [Fact]
    public void ANestedContainerIsNamedByItsWholePath()
    {
        var r = Book().Validate(new WriteRequest
        {
            RecordType = "Npc", Path = new[] { "VirtualMachineAdapter", "Scripts[0]", "Properties[0]" },
            Verb = "Set",
        });

        Assert.NotNull(r);
        Assert.Contains("field_path='VirtualMachineAdapter.Scripts[0].Properties'", r!);
        Assert.Contains("key='0'", r);
    }
}

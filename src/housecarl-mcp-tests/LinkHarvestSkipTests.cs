using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The pre-flight walks a write once when it carries no link. The harvest pass IS a validate, so a second walk in
/// Phase 1b would double the cost of every edit in a bulk call; the write lanes keep the harvest's verdict for any
/// edit that put nothing in the sink and re-walk only the ones that did. These pin the premise: what goes in the
/// sink, and that the harvest's verdict is the checking pass's verdict for a link-free edit.
///
/// <para>Corpus-only, so it needs no records — the world is here for the generated corpus
/// <c>CorpusRulebook.CorpusPath</c> points at.</para>
/// </summary>
[Trait("tier", "integration")]
[Collection("bulk-records")]
public sealed class LinkHarvestSkipTests : BulkRecordsTestBase
{
    public LinkHarvestSkipTests(BulkRecordsFixture f) : base(f) { }

    static WriteRequest Req(string type, string[] path, string verb, string? value = null)
        => new() { RecordType = type, Path = path, Verb = verb, Value = value };

    /// <summary>Harvest one write; hand back what it put in the sink and the verdict it reached.</summary>
    static (List<string> sunk, string? verdict) Harvest(WriteRequest req, IReadOnlyCollection<string>? siblings = null)
    {
        var sink = new List<string>();
        var verdict = CorpusRulebook.Load().WithLinkHarvest(sink).CollectLinkValues(req, siblings);
        return (sink, verdict);
    }

    /// <summary>A write with no FormLink in it contributes nothing — which is the signal the lanes skip on.</summary>
    [Fact]
    public void ALinkFreeWriteContributesNothingToTheSink()
        => Assert.Empty(Harvest(Req("Armor", new[] { "Value" }, "Set", "100")).sunk);

    /// <summary>…and the harvest's verdict on it is the verdict the checking walk reaches, so the skipped second
    /// walk costs the caller no accuracy. The legal write.</summary>
    [Fact]
    public void ALinkFreeWriteGetsTheSameVerdictFromBothWalks()
    {
        var req = Req("Armor", new[] { "Value" }, "Set", "100");

        Assert.Equal(CorpusRulebook.Load().Validate(req), Harvest(req).verdict);
    }

    /// <summary>The same for a REFUSED one: a bad field path is reported off the harvest walk, word for word.</summary>
    [Fact]
    public void ALinkFreeRefusalIsTheSameSentenceFromBothWalks()
    {
        var req = Req("Armor", new[] { "NoSuchField" }, "Set", "100");
        var (sunk, verdict) = Harvest(req);

        Assert.Empty(sunk);
        Assert.NotNull(verdict);
        Assert.Equal(CorpusRulebook.Load().Validate(req), verdict);
    }

    /// <summary>A write that DOES set a link contributes its value, so its lane pays the second walk — the one that
    /// can type-check the target once the lookup exists.</summary>
    [Fact]
    public void AFormLinkWriteContributesItsValue()
        => Assert.Equal(new[] { "000019:Skyrim.esm" },
                        Harvest(Req("Armor", new[] { "Race" }, "Set", "000019:Skyrim.esm")).sunk);

    /// <summary>A same-call '@editorid' reference contributes too. It is not a FormID and the link lookup skips it;
    /// it is in the sink so the create lane never settles a forward reference on the harvest's answer, which sees
    /// EVERY editorid in the call where the check sees only the ones declared earlier.</summary>
    [Fact]
    public void ASameCallSiblingReferenceContributesToo()
        => Assert.Equal(new[] { "@LaterRace" },
                        Harvest(Req("Armor", new[] { "Race" }, "Set", "@LaterRace"),
                                new[] { "LaterRace" }).sunk);
}

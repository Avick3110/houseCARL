using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary><c>housecarl_copy</c>'s argument layer driven through <c>CopyTools.Copy</c>, so the wire spelling of each
/// parameter is under test rather than the typed values the service takes: exclude_types severities, exactly one
/// destination, FormID parsing and its order, seed_paths required and trimmed, the from_source default.</summary>
[Trait("tier", "integration")]
public sealed class CopyWireParseTests : IClassFixture<CopyParserWorld>
{
    static readonly string[] Seed = { "HeadParts" };
    readonly CopyParserWorld _w;
    public CopyWireParseTests(CopyParserWorld w) => _w = w;

    string Copy(string[]? source, string[]? seeds, string[]? exclude, string? target, string? newEid, string patch, string? from = null)
        => CopyTools.Copy(_w.Svc, from ?? _w.From, source, seeds, exclude, target, newEid, patch);

    static void Has(string text, params string[] needles)
    {
        foreach (var n in needles) Assert.Contains(n, text, StringComparison.OrdinalIgnoreCase);
    }

    // baseline: the fixture copies at all, BOTH source records are internalized, and the artifact does not master the source
    [Fact]
    public void WithNoExclusionsBothRecordsAreInternalizedAndTheCloneIsStandalone()
        => Has(Copy(null, Seed, null, null, "PBaseline", "PBaseline"),
            "CLONED", "SrcHair", "SrcTex", "standalone: the source is NOT a master");

    // 'TextureSet:refuse' on the WIRE fails the whole copy, naming the type and the severity, and says nothing was written
    [Fact]
    public void RefuseOnTheWireFailsTheCopyNamingTypeAndSeverity()
        => Has(Copy(null, Seed, new[] { "TextureSet:refuse" }, null, "PRefuse", "PRefuse"),
            "error:", "marks 'refuse'", "TextureSet", "Nothing was written");

    // 'TextureSet:stop' on the WIRE lets the copy through, PRUNING the texture set while internalizing the head part
    [Fact]
    public void StopOnTheWirePrunesTheRecordAndCopies()
    {
        var stop = Copy(null, Seed, new[] { "TextureSet:stop" }, null, "PStop", "PStop");
        Has(stop, "CLONED", "internalized under new FormIDs", "SrcHair");
        Assert.DoesNotContain("SrcTex", stop, StringComparison.OrdinalIgnoreCase);
    }

    // the kept link's cost is ALARMED: the 'stop' artifact masters the source
    [Fact]
    public void StopsKeptLinkIsAlarmedAsNotStandalone()
        => Has(Copy(null, Seed, new[] { "TextureSet:stop" }, null, "PStopAlarm", "PStopAlarm"),
            "the source IS among the masters", "NOT standalone");

    // the severity token is case-insensitive
    [Fact]
    public void TheSeverityTokenIsCaseInsensitive()
        => Has(Copy(null, Seed, new[] { "TextureSet:REFUSE" }, null, "PUpper", "PUpper"), "error:", "marks 'refuse'");

    // an entry with NO severity defaults to the loud one ('refuse')
    [Fact]
    public void AnEntryWithNoSeverityDefaultsToRefuse()
        => Has(Copy(null, Seed, new[] { "TextureSet" }, null, "PBare", "PBare"), "error:", "marks 'refuse'");

    // an unknown severity refuses by name, quoting what was typed and both legal spellings
    [Fact]
    public void AnUnknownSeverityRefusesQuotingItAndBothLegalSpellings()
        => Has(Copy(null, Seed, new[] { "TextureSet:prune" }, null, "PBadSev", "PBadSev"), "error:", "'prune'", "stop", "refuse");

    // an entry naming no record type refuses by name
    [Fact]
    public void AnEntryNamingNoTypeRefuses()
        => Has(Copy(null, Seed, new[] { ":refuse" }, null, "PNoType", "PNoType"), "error:", "names no record type");

    // a list of only blank exclusion entries REFUSES rather than excluding nothing, and writes nothing
    [Fact]
    public void BlankExclusionEntriesRefuse()
    {
        var r = Copy(null, Seed, new[] { "", "   " }, null, "PBlankX", "PBlankX");
        Has(r, "error:", "blank entry");
        Assert.DoesNotContain("CLONED", r, StringComparison.OrdinalIgnoreCase);
    }

    // a blank BESIDE a real exclusion refuses too, rather than quietly applying one fewer
    [Fact]
    public void ABlankBesideARealExclusionRefuses()
        => Has(Copy(null, Seed, new[] { "TextureSet:refuse", "  " }, null, "PBlankY", "PBlankY"), "error:", "blank entry");

    // a DUPLICATE exclude_types type refuses by name, not as an unexpected failure
    [Fact]
    public void ADuplicateExclusionTypeRefusesByName()
    {
        var r = Copy(null, Seed, new[] { "TextureSet:stop", "textureset:refuse" }, null, "PDup", "PDup");
        Has(r, "error:", "more than once");
        Assert.DoesNotContain("failed unexpectedly", r, StringComparison.OrdinalIgnoreCase);
    }

    // an unsupported list seed refuses BY NAME, naming the field, its entries' shape, the route (apply's zip, WHOLE), and writes nothing
    [Fact]
    public void AStructListSeedRefusesNamingTheFieldItsEntriesAndTheRoute()
        => Has(Copy(null, new[] { "HeadParts", "Factions" }, null, null, "PShape", "PShape"),
            "error:", "Factions", "RankPlacement", "housecarl_apply", "CopyFrom", "WHOLE", "Nothing was written");

    // NEITHER destination refuses, naming both routes
    [Fact]
    public void NeitherDestinationRefusesNamingBothRoutes()
        => Has(Copy(null, Seed, null, null, null, "PNeither"), "error:", "EXACTLY ONE destination", "target=", "new_editorid=");

    // BOTH destinations refuse the same way
    [Fact]
    public void BothDestinationsRefuse()
        => Has(Copy(null, Seed, null, _w.From, "PClone", "PBoth"), "error:", "EXACTLY ONE destination");

    // a whitespace-only target= is ABSENT, not a destination that then fails to parse
    [Fact]
    public void AWhitespaceTargetIsAbsent()
        => Has(Copy(null, Seed, null, "   ", null, "PWsTarget"), "error:", "EXACTLY ONE destination");

    // a bad from= refuses naming the parameter and the expected shape, ahead of the destination gate
    [Fact]
    public void ABadFromRefusesFirstNamingTheShape()
    {
        var r = Copy(null, Seed, null, null, null, "PBadFrom", from: "notaformid");
        Has(r, "error:", "bad from", "XXXXXX:Plugin.esp");
        Assert.DoesNotContain("EXACTLY ONE destination", r, StringComparison.OrdinalIgnoreCase);
    }

    // a bad target= refuses naming the parameter and the expected shape
    [Fact]
    public void ABadTargetRefusesNamingTheShape()
        => Has(Copy(null, Seed, null, "nope", null, "PBadTarget"), "error:", "bad target", "XXXXXX:Plugin.esp");

    // seed_paths ABSENT refuses rather than walking from nothing
    [Fact]
    public void AbsentSeedPathsRefuse()
        => Has(Copy(null, null, null, null, "PNoSeeds", "PNoSeeds"), "error:", "seed_paths is required");

    // an EMPTY seed_paths list refuses the same way
    [Fact]
    public void EmptySeedPathsRefuse()
        => Has(Copy(null, Array.Empty<string>(), null, null, "PEmpty", "PEmpty"), "error:", "seed_paths is required");

    // a BLANK seed element is refused BY INDEX, not silently dropped
    [Fact]
    public void ABlankSeedIsRefusedByIndex()
        => Has(Copy(null, new[] { "", "  " }, null, null, "PBlankS", "PBlankS"), "error:", "seed_paths[0] is blank");

    // the index names the element the CALLER passed, not a post-filter position
    [Fact]
    public void TheBlankSeedIndexIsTheCallers()
        => Has(Copy(null, new[] { "HeadParts", " " }, null, null, "PBlankS2", "PBlankS2"), "error:", "seed_paths[1] is blank");

    // a padded seed path is TRIMMED, not refused as an unknown field
    [Fact]
    public void APaddedSeedPathIsTrimmed()
        => Has(Copy(null, new[] { "  HeadParts  " }, null, null, "PPadded", "PPadded"), "CLONED", "SrcHair");

    // no from_source= still copies, through the DOCUMENTED default, and the readback names it 'winner'
    [Fact]
    public void NoSourceTakesTheWinnerDefaultAndSaysSo()
        => Has(Copy(null, Seed, null, null, "PDefSrc", "PDefSrc"), "CLONED", "source: winner");

    // an EMPTY from_source= takes the same default rather than reaching the empty-universe refusal
    [Fact]
    public void AnEmptySourceListTakesTheWinnerDefault()
        => Has(Copy(Array.Empty<string>(), Seed, null, null, "PEmptySrc", "PEmptySrc"), "source: winner");

    // a blank from_source= element is REFUSED BY INDEX
    [Fact]
    public void ABlankSourceIsRefusedByIndex()
        => Has(Copy(new[] { "  ", "Src.esp" }, Seed, null, null, "PBlankSrc", "PBlankSrc"), "error:", "from_source[0] is blank");

    // …at the caller's own index
    [Fact]
    public void TheBlankSourceIndexIsTheCallers()
        => Has(Copy(new[] { "Src.esp", "" }, Seed, null, null, "PBlankSrc2", "PBlankSrc2"), "error:", "from_source[1] is blank");
}

using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary><c>housecarl_copy</c>'s argument layer driven through <c>CopyTools.Copy</c>, so the wire spelling of each
/// parameter is under test rather than the typed values the service takes: the exclusions' severities and types, exactly one
/// destination, FormID parsing and its order, seed_paths required and trimmed, the from_source default.</summary>
[Trait("tier", "integration")]
public sealed class CopyWireParseTests : IClassFixture<CopyParserWorld>
{
    static readonly string[] Seed = { "HeadParts" };
    readonly CopyParserWorld _w;
    public CopyWireParseTests(CopyParserWorld w) => _w = w;

    string Copy(string[]? source, string[]? seeds, string[]? exclude, string? target, string? newEid, string patch, string? from = null)
        => CopyTools.Copy(_w.Svc, from ?? _w.From, source, seeds, Walks.Excl(exclude), target, newEid, patch);

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

    // an entry with NO severity is refused, as on the walks: there is no bare default any more
    [Fact]
    public void AnEntryWithNoSeverityIsRefused()
    {
        var r = Copy(null, Seed, new[] { "TextureSet" }, null, "PBare", "PBare");
        Assert.Equal("error: exclusions 'TextureSet': severity='' — use 'stop' (prune, record the boundary) or 'refuse' (the whole walk fails loud).", r);
    }

    // an unknown severity refuses by name, quoting what was typed and both legal spellings
    [Fact]
    public void AnUnknownSeverityRefusesQuotingItAndBothLegalSpellings()
        => Has(Copy(null, Seed, new[] { "TextureSet:prune" }, null, "PBadSev", "PBadSev"), "error:", "'prune'", "stop", "refuse");

    // an entry naming no record type refuses by name
    [Fact]
    public void AnEntryNamingNoTypeRefuses()
        => Assert.Equal("error: an exclusions entry needs match= — the record type name a read reports (e.g. 'Race').",
                        Copy(null, Seed, new[] { ":refuse" }, null, "PNoType", "PNoType"));

    // a blank BESIDE a real exclusion refuses too, rather than quietly applying one fewer, and writes nothing
    [Fact]
    public void ABlankBesideARealExclusionRefuses()
    {
        var r = Copy(null, Seed, new[] { "TextureSet:refuse", "  :stop" }, null, "PBlankY", "PBlankY");
        Has(r, "error:", "needs match=");
        Assert.DoesNotContain("CLONED", r, StringComparison.OrdinalIgnoreCase);
    }

    // a type twice takes its first entry, so a stop then a refuse stops, with no unexpected failure
    [Fact]
    public void ADuplicateExclusionTypeTakesItsFirstEntry()
    {
        var r = Copy(null, Seed, new[] { "TextureSet:stop", "textureset:refuse" }, null, "PDup", "PDup");
        Has(r, "CLONED", "SrcHair");
        Assert.DoesNotContain("SrcTex", r, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("failed unexpectedly", r, StringComparison.OrdinalIgnoreCase);
    }

    // a signature names its type as the walks read it: 'TXST' stops at the texture set
    [Fact]
    public void ASignatureStopsAtItsType()
    {
        var r = Copy(null, Seed, new[] { "TXST:stop" }, null, "PSig", "PSig");
        Has(r, "CLONED", "SrcHair");
        Assert.DoesNotContain("SrcTex", r, StringComparison.OrdinalIgnoreCase);
    }

    // a base type covers every arm: 'Global' stops at the float global the list holds
    [Fact]
    public void ABaseTypeStopsAtEveryArm()
    {
        var r = Copy(null, new[] { "Items" }, new[] { "Global:stop" }, null, "PGlob", "PGlob", from: _w.ListFrom);
        Has(r, "CLONED", "SrcTex");
        Assert.DoesNotContain("SrcGlob", r, StringComparison.OrdinalIgnoreCase);
    }

    // a base beside its arm takes the first entry once per type, rather than failing inside the walk
    [Fact]
    public void ABaseBesideItsArmTakesTheFirstEntry()
    {
        var r = Copy(null, new[] { "Items" }, new[] { "Global:stop", "GlobalFloat:refuse" }, null, "PGlobDup", "PGlobDup", from: _w.ListFrom);
        Has(r, "CLONED", "SrcTex");
        Assert.DoesNotContain("SrcGlob", r, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("failed unexpectedly", r, StringComparison.OrdinalIgnoreCase);
    }

    // a name that is no record type refuses before anything is read or written
    [Fact]
    public void ANameThatIsNoTypeRefusesBeforeAnythingIsWritten()
    {
        var r = Copy(null, Seed, new[] { "TextureSets:stop" }, null, "PNoSuch", "PNoSuch");
        Assert.Equal("error: exclusions 'TextureSets' is not a record type — pass a catalog name (e.g. 'Npc', 'LeveledItem') or a signature (e.g. 'NPC_', 'LVLI').", r);
        Assert.False(Directory.EnumerateDirectories(Path.Combine(_w.Root, "inst", "mods"), "*PNoSuch*").Any());
    }

    // the type names copy's real calls passed hand the walk the same exclusion they did as 'Type:severity', once each
    [Theory]
    [InlineData("Race"), InlineData("FootstepSet"), InlineData("TextureSet"), InlineData("ArtObject"), InlineData("FormList")]
    public void ARealCallsTypeNameIsHandedToTheWalkAsSpelled(string type)
    {
        Assert.Null(WalkGrammar.ParseExclusions(_w.Svc.Types, Walks.Excl(type + ":stop", type + ":refuse"), "exclusions", out var parsed));
        Assert.Equal(new[] { (type, false) }, parsed);
    }

    // the re-spelled exclusion writes the patch the old spelling wrote, byte for byte: the walk is handed the same value
    [Fact]
    public void TheReSpelledExclusionWritesTheSamePatch()
    {
        var mods = Path.Combine(_w.Root, "inst", "mods");
        Has(CopyTools.Copy(_w.Svc, _w.From, seed_paths: Seed, exclusions: Walks.Excl("TextureSet:stop"), new_editorid: "PSame", patch: "PSameNew"), "CLONED");
        var old = _w.Svc.CopyClosure(Mutagen.Bethesda.Plugins.FormKey.Factory(_w.From), new[] { HousecarlCore.SourcePoles.Winner }, Seed,
                                     new[] { new HousecarlCore.WalkExclusion("TextureSet", HousecarlCore.ExclusionSeverity.Stop, "excluded by the caller as 'stop'") },
                                     null, "PSame", "PSameOld", null);
        Assert.True(old.Success);
        byte[] Bytes(string stem) => File.ReadAllBytes(Directory.EnumerateFiles(mods, stem + ".esp", SearchOption.AllDirectories).Single());
        Assert.Equal(Bytes("PSameOld"), Bytes("PSameNew"));
    }

    // the retired spelling is refused, naming the new one
    [Fact]
    public void ExcludeTypesIsRefusedNamingExclusions()
    {
        var r = CopyTools.Copy(_w.Svc, _w.From, seed_paths: Seed, exclude_types: Walks.Of("[\"TextureSet:stop\"]"), new_editorid: "PRetired", patch: "PRetired");
        Has(r, "error:", "exclude_types= is retired", "exclusions=[{\"match\"");
        Assert.False(Directory.EnumerateDirectories(Path.Combine(_w.Root, "inst", "mods"), "*PRetired*").Any());
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

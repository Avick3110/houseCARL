using Xunit;

namespace HousecarlMcpTests;

/// <summary>Migrated from the skse-config-audit-guard probe's extractor arms: every reference shape the SKSE
/// config audit's evidence sample established, read by <see cref="SkseConfigReferenceExtractor"/>.</summary>
[Trait("tier", "unit")]
public sealed class SkseConfigReferenceShapeTests
{
    static IReadOnlyList<SkseConfigRef> Ex(string relPath, string text) => SkseConfigReferenceExtractor.Extract(relPath, text);

    static List<SkseConfigRef> Tokens(IReadOnlyList<SkseConfigRef> refs) => refs.Where(r => r.Shape == SkseRefShape.FormToken).ToList();

    static SkseConfigRef OnlyToken(string relPath, string text) => Assert.Single(Tokens(Ex(relPath, text)));

    [Fact] // CDF JSONC 0x4FDAF|Skyrim.esm → Skyrim.esm / 0x4FDAF
    public void AJsoncFormIdFirstTokenWithCommentsReadsPluginAndId()
    {
        var a = OnlyToken(@"SKSE\Plugins\ContainerDistributionFramework\C.O.I.N.json", "{\n  // COIN distribution\n  \"form\": \"0x4FDAF|Skyrim.esm\",\n}");
        Assert.Equal(("Skyrim.esm", (uint?)0x4FDAF, (string?)null), (a.Plugin, a.LocalId, a.Unparseable));
    }

    [Fact] // DSD FE007800|<spaced name>.esp → 12-bit local 0x800 (ESL mask)
    public void AnEslPrefixedIdMasksToTheTwelveBitLocalIdAndKeepsASpacedName()
    {
        var a = OnlyToken(@"SKSE\Plugins\DynamicStringDistributor\x.json",
            "  \"form_id\": \"FE007800|Dynamic Activation Key - Addons Collection.esp\",");
        Assert.Equal(("Dynamic Activation Key - Addons Collection.esp", (uint?)0x800), (a.Plugin, a.LocalId));
    }

    [Fact] // apostrophe name → full 'kryptopyr's Trade & Barter.esp' kept
    public void APluginNameWithAnApostropheIsNotCutAtTheApostrophe()
    {
        var a = OnlyToken(@"SKSE\Plugins\Foo\x.json", "\"form\": \"0x800|kryptopyr's Trade & Barter.esp\"");
        Assert.Equal(("kryptopyr's Trade & Barter.esp", (uint?)0x800), (a.Plugin, a.LocalId));
    }

    [Fact] // TOML 'Foo.esp|0x1' → leading-quote stripped to 'Foo.esp'
    public void ATomlSingleQuotedPluginFirstTokenLosesTheLeadingQuote()
    {
        var a = OnlyToken(@"SKSE\Plugins\Foo\x.toml", "form = 'Foo.esp|0x1'");
        Assert.Equal(("Foo.esp", (uint?)0x1), (a.Plugin, a.LocalId));
    }

    [Fact] // prose '(Skyrim.esm|0x5)' → plugin bounded to Skyrim.esm (not the prose prefix)
    public void AnOpeningParenthesisBoundsThePluginNameInProse()
    {
        var a = OnlyToken(@"SKSE\Plugins\Foo\x.ini", "; this will cast fireball (Skyrim.esm|0x5) on the target");
        Assert.Equal(("Skyrim.esm", (uint?)0x5), (a.Plugin, a.LocalId));
    }

    [Fact] // NON-ESL 1204FDAF|Skyrim.esm → low-24 local 0x04FDAF
    public void ANonEslEightDigitIdMasksToTheLowTwentyFourBits()
    {
        var a = OnlyToken(@"SKSE\Plugins\Foo\bar.ini", "target = 1204FDAF|Skyrim.esm");
        Assert.Equal(("Skyrim.esm", (uint?)0x04FDAF), (a.Plugin, a.LocalId));
    }

    [Fact] // SkyPatcher Skyrim.esm|0x01397E (plugin-first) → Skyrim.esm / 0x1397E
    public void ASkyPatcherPluginFirstTokenReadsPluginAndId()
    {
        var a = OnlyToken(@"SKSE\Plugins\SkyPatcher\weapons\x.ini", "filterByWeapons=Skyrim.esm|0x01397E:attackDamage=20");
        Assert.Equal(("Skyrim.esm", (uint?)0x1397E), (a.Plugin, a.LocalId));
    }

    [Fact] // tilde 0x000FE2~Dawnguard.esm → Dawnguard.esm / 0xFE2
    public void ATildeTokenWithLeadingZerosReadsPluginAndId()
    {
        var a = OnlyToken(@"SKSE\Plugins\Foo\x.ini", "Spell = 0x000FE2~Dawnguard.esm");
        Assert.Equal(("Dawnguard.esm", (uint?)0xFE2), (a.Plugin, a.LocalId));
    }

    [Fact] // comma list → 2 tokens (Skyrim.esm/0x1, Update.esm/0x2)
    public void EachTokenInACommaListIsItsOwnReference()
    {
        var toks = Tokens(Ex(@"SKSE\Plugins\Foo\x.json", "\"forms\": [\"0x1|Skyrim.esm\", \"0x2|Update.esm\"]"));
        Assert.Equal(new[] { ("Skyrim.esm", (uint?)0x1), ("Update.esm", (uint?)0x2) }, toks.Select(t => (t.Plugin, t.LocalId)));
    }

    [Fact] // path gate \Dawnguard.esm\ → 1 gate, 0 tokens
    public void APluginNamedFolderIsOneGateWithNoId()
    {
        var r = Ex(@"SKSE\Plugins\DynamicStringDistributor\Dawnguard.esm\names.json", "{ \"strings\": [ \"Hi\" ] }");
        var gate = Assert.Single(r);
        Assert.Equal((SkseRefShape.PathSegmentGate, "Dawnguard.esm", (uint?)null), (gate.Shape, gate.Plugin, gate.LocalId));
    }

    [Fact] // no-reference file → 0 refs (clean empty)
    public void AFileWithNoReferencesYieldsNone()
        => Assert.Empty(Ex(@"SKSE\Plugins\OStim\scenes\x.json", "{ \"duration\": 4.0, \"actors\": 2, \"anim\": \"idle_a\" }"));

    [Fact] // overflow 0x1FFFFFFFF|Skyrim.esm → unparseable, LocalId null
    public void ANineDigitIdIsKeptAsUnparseableWithNoId()
    {
        var a = OnlyToken(@"SKSE\Plugins\Foo\x.ini", "form = 0x1FFFFFFFF|Skyrim.esm");
        Assert.NotNull(a.Unparseable);
        Assert.Equal(("Skyrim.esm", (uint?)null), (a.Plugin, a.LocalId));
    }

    [Fact] // comment-embedded 0x5|Skyrim.esm → still extracted (declared refs)
    public void ATokenInsideACommentIsStillExtracted()
    {
        var a = OnlyToken(@"SKSE\Plugins\Foo\x.ini", "; see 0x5|Skyrim.esm for the base record");
        Assert.Equal(("Skyrim.esm", (uint?)0x5), (a.Plugin, a.LocalId));
    }

    [Fact] // bare 0x800|SomeMod.esl → 0x800 (no FE prefix, low-24 identity)
    public void ABareIdUnderAnEslPluginIsKeptAsIs()
    {
        var a = OnlyToken(@"SKSE\Plugins\Foo\x.ini", "x = 0x800|SomeMod.esl");
        Assert.Equal(("SomeMod.esl", (uint?)0x800), (a.Plugin, a.LocalId));
    }
}

using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The SkyPatcher line tokenizer (<see cref="SkyPatcherParse"/>, from the <c>skypatcher-parse-guard</c>
/// probe): line kinds, segment and list splitting, the <c>Plugin.esp|FormID</c> address, the name literal, compound
/// values, the loud notes on malformed segments, and BOM handling. No game data.</summary>
[Trait("tier", "unit")]
public sealed class SkyPatcherParseTests
{
    // blank line ⇒ Blank; ';'-led line ⇒ Comment; key=value line ⇒ Patch
    [Theory]
    [InlineData("   ", SkyPatcherLineKind.Blank)]
    [InlineData("  ; a note", SkyPatcherLineKind.Comment)]
    [InlineData("attackDamage=99", SkyPatcherLineKind.Patch)]
    [InlineData("  [Deathbringer Vorla]  ", SkyPatcherLineKind.Label)]
    [InlineData("filterByNpcs=Vigilant.esm|1D0ECCB3:shoutsToRemove=Vigilant.esm|020ECCB9", SkyPatcherLineKind.Patch)]
    public void ALineIsClassifiedByItsLead(string line, SkyPatcherLineKind kind) =>
        Assert.Equal(kind, SkyPatcherParse.ParseLine(line).Kind);

    // '[Name]' ⇒ Label (inert): no segments, no note
    [Fact]
    public void ASectionLineIsAnInertLabel()
    {
        var lbl = SkyPatcherParse.ParseLine("[Vernaccus]");
        Assert.Equal(SkyPatcherLineKind.Label, lbl.Kind);
        Assert.Empty(lbl.Segments);
        Assert.Null(lbl.Note);
    }

    // weapon patch ⇒ 3 segments; segment[0] key = filterByWeapons; segment[1] key/value = attackDamage/99;
    // weapon FormID address parsed = Skyrim.esm|0x12EB7; no parse note on the clean line
    [Fact]
    public void TheCanonicalWeaponPatchSplitsIntoAddressedSegments()
    {
        var w = SkyPatcherParse.ParseLine("filterByWeapons=Skyrim.esm|00012EB7:attackDamage=99:weight=0");

        Assert.Equal(3, w.Segments.Count);
        Assert.Equal("filterByWeapons", w.Segments[0].Key);
        Assert.Equal("attackDamage", w.Segments[1].Key);
        Assert.Equal("99", w.Segments[1].RawValue);
        var a = w.Segments[0].Values[0].Address;
        Assert.NotNull(a);
        Assert.True(a.IsFormId);
        Assert.Equal("Skyrim.esm", a.Plugin);
        Assert.Equal(0x12EB7u, a.FormId);
        Assert.Null(w.Note);
    }

    // comma list ⇒ 2 values; both list items address-parsed (leading space trimmed)
    [Fact]
    public void ACommaListAddressesEveryItem()
    {
        var mv = SkyPatcherParse.ParseLine("filterByWeapons=Skyrim.esm|00012EB7, Skyrim.esm|00013790:attackDamage=50");

        Assert.Equal(2, mv.Segments[0].Values.Count);
        Assert.Equal(0x12EB7u, mv.Segments[0].Values[0].Address?.FormId);
        Assert.Equal(0x13790u, mv.Segments[0].Values[1].Address?.FormId);
    }

    // FormID leading-zero trim (myMod.esp|223 ⇒ 0x223); player form Skyrim.esm|7 ⇒ 0x7
    [Theory]
    [InlineData("keywordsToAdd=myMod.esp|223", "myMod.esp", 0x223u)]
    [InlineData("filterByNpcs=Skyrim.esm|7", "Skyrim.esm", 0x7u)]
    public void AShortFormIdParsesAsHex(string line, string plugin, uint formId)
    {
        var a = SkyPatcherParse.ParseLine(line).Segments[0].Values[0].Address;
        Assert.Equal(plugin, a?.Plugin);
        Assert.Equal(formId, a?.FormId);
    }

    // ~…~ ⇒ name-literal with inner text preserved; a name-literal is NOT address-parsed;
    // bare EditorID 'IronSword' is left un-addressed at 0a
    [Fact]
    public void ATildeNameIsALiteralAndABareEditorIdStaysUnaddressed()
    {
        var rn = SkyPatcherParse.ParseLine("filterByWeapons=IronSword:fullName=~Reforged Blade~");
        var name = rn.Segments[1].Values[0];
        var eid = rn.Segments[0].Values[0];

        Assert.True(name.IsNameLiteral);
        Assert.Equal("Reforged Blade", name.NameText);
        Assert.Null(name.Address);
        Assert.Null(eid.Address);
        Assert.Equal("IronSword", eid.Raw);
    }

    // compound ⇒ 4 sub-args; compound first sub-arg address-parsed
    [Fact]
    public void ACompoundValueSplitsOnTildeAndAddressesItsFirstPart()
    {
        var v = SkyPatcherParse.ParseLine("filterBySpells=Skyrim.esm|12FCD:mgefsToAdd=Skyrim.esm|0001C08A~50~10~0").Segments[1].Values[0];

        Assert.Equal(new[] { "Skyrim.esm|0001C08A", "50", "10", "0" }, v.SubArgs);
        Assert.Equal(0x1C08Au, v.Address?.FormId);
    }

    // first-'=' split keeps 'form=rank' intact in the value; 'form=rank' item is deliberately un-addressed at 0a
    [Fact]
    public void OnlyTheFirstEqualsSplitsAndFormRankStaysUnaddressed()
    {
        var s = SkyPatcherParse.ParseLine("filterByNpcs=Skyrim.esm|13BBF:factionsToAdd=Skyrim.esm|1BE1B=5").Segments[1];

        Assert.Equal("factionsToAdd", s.Key);
        Assert.Equal("Skyrim.esm|1BE1B=5", s.RawValue);
        Assert.Null(s.Values[0].Address);
    }

    // doubled ',' ⇒ 2 items + a loud note naming the empty ','-item
    [Fact]
    public void AnEmptyCommaItemIsSkippedWithANote()
    {
        var ec = SkyPatcherParse.ParseLine("keywordsToAdd=Skyrim.esm|123,,Skyrim.esm|456");

        Assert.Equal(2, ec.Segments[0].Values.Count);
        Assert.Contains("','-item", ec.Note);
    }

    // objectEffect=null ⇒ scalar 'null', no address, not a name-literal
    [Fact]
    public void NullIsAPlainScalar()
    {
        var v = SkyPatcherParse.ParseLine("filterByWeapons=SomeSword:objectEffect=null").Segments[1].Values[0];

        Assert.Equal("null", v.Raw);
        Assert.False(v.IsNameLiteral);
        Assert.Null(v.Address);
    }

    // whitespace around key/value is trimmed
    [Fact]
    public void WhitespaceAroundKeysAndValuesIsTrimmed()
    {
        var ws = SkyPatcherParse.ParseLine("  filterByWeapons = Skyrim.esm|12EB7  :  attackDamage = 99 ");

        Assert.Equal("filterByWeapons", ws.Segments[0].Key);
        Assert.Equal("attackDamage", ws.Segments[1].Key);
        Assert.Equal("99", ws.Segments[1].RawValue);
    }

    // no-'=' segment ⇒ loud note + segment retained
    [Fact]
    public void ASegmentWithNoEqualsIsNotedAndKept()
    {
        var m = SkyPatcherParse.ParseLine("filterByWeapons=X:justtext");

        Assert.Contains("no '='", m.Note);
        Assert.Equal(2, m.Segments.Count);
    }

    // empty-key segment ⇒ loud note
    [Fact]
    public void AnEmptyKeyIsNoted() =>
        Assert.Contains("empty key", SkyPatcherParse.ParseLine("=99").Note);

    // TryParseAddress rejects non-hex right side; TryParseAddress rejects a bare identifier (no '|')
    [Theory]
    [InlineData("Skyrim.esm|NotHex")]
    [InlineData("IronSword")]
    public void TryParseAddressRejectsANonAddress(string raw) =>
        Assert.Null(SkyPatcherParse.TryParseAddress(raw));

    // file parse ⇒ 2 patch, 1 comment, 1 label, ≥1 blank (the label is not a patch)
    [Fact]
    public void AFileParsesEachLineByKind()
    {
        var file = SkyPatcherParse.ParseFile(
            "; header comment\n[Some NPC]\nfilterByWeapons=Skyrim.esm|12EB7:attackDamage=99\n\nfilterByArmors=Skyrim.esm|12E49:armorRating=40\n");

        Assert.Equal(2, file.Count(l => l.Kind == SkyPatcherLineKind.Patch));
        Assert.Equal(1, file.Count(l => l.Kind == SkyPatcherLineKind.Comment));
        Assert.Equal(1, file.Count(l => l.Kind == SkyPatcherLineKind.Label));
        Assert.Contains(file, l => l.Kind == SkyPatcherLineKind.Blank);
    }

    // leading BOM stripped — first key parses clean
    [Fact]
    public void ALeadingBomIsStripped()
    {
        var f = SkyPatcherParse.ParseFile("\uFEFF" + "filterByWeapons=Skyrim.esm|12EB7:weight=0");

        var line = Assert.Single(f);
        Assert.Equal(SkyPatcherLineKind.Patch, line.Kind);
        Assert.Equal("filterByWeapons", line.Segments[0].Key);
    }

    // mid-file BOM at a line start trimmed — key parses clean
    [Fact]
    public void ABomAtALineStartMidFileIsTrimmed()
    {
        var f = SkyPatcherParse.ParseFile("weight=1\n" + "\uFEFF" + "filterByKeywordsOr=A,B:weight=0");

        Assert.Equal(2, f.Count);
        Assert.Equal("filterByKeywordsOr", f[1].Segments[0].Key);
    }

    // mid-LINE BOM treated as whitespace — both segments parse clean
    [Fact]
    public void ABomMidLineIsWhitespace()
    {
        var l = SkyPatcherParse.ParseLine("attackDamage=5" + "\uFEFF" + ":weight=1");

        Assert.Equal(2, l.Segments.Count);
        Assert.Equal("5", l.Segments[0].RawValue);
        Assert.Equal("weight", l.Segments[1].Key);
    }
}

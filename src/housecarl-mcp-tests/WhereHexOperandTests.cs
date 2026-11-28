using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>#1464: '=', '!=', 'in' and 'not in' read a literal by the kind of leaf it meets. On an integer leaf 0x hex is
/// the field's bits at its own width; a float leaf takes a decimal number and a link a FormID, and a literal no leaf in
/// scope can hold refuses the call at plan instead of scanning to a silent zero.</summary>
[Trait("tier", "unit")]
public sealed class WhereHexOperandTests
{
    const int High = unchecked((int)0x80000400);   // bit 31 and 0x400: reads -2147482624

    readonly SkyrimMod _mod = new(new ModKey("HcHexUnit", ModType.Master), SkyrimRelease.SkyrimSE);
    readonly List<IMajorRecordGetter> _statics = new();
    readonly FormKey _four, _fourToo, _high;
    readonly IMajorRecordGetter _miscTen, _miscHexName, _miscFourName, _weapTwo, _weapOne, _npcOne, _npcMinus;

    public WhereHexOperandTests()
    {
        _four = Static("HcFour", 4);
        _fourToo = Static("HcFourToo", 4);
        Static("HcZero", 0);
        _high = Static("HcHigh", High);
        _miscTen = Misc("HcTen", 10, "Ten");
        _miscHexName = Misc("HcHexName", 4, "0x4");
        _miscFourName = Misc("HcFourName", 4, "4");
        var kw1 = _mod.Keywords.AddNew().FormKey;
        var kw2 = _mod.Keywords.AddNew().FormKey;
        _weapTwo = Weapon("HcWeapTwo", kw1, kw2);
        _weapOne = Weapon("HcWeapOne", kw1);
        var fac = _mod.Factions.AddNew().FormKey;
        _npcOne = Npc("HcNpcOne", fac, 1);
        _npcMinus = Npc("HcNpcMinus", fac, -1);
    }

    FormKey Static(string eid, int raw)
    {
        var s = _mod.Statics.AddNew();
        s.EditorID = eid;
        s.MajorRecordFlagsRaw = raw;
        _statics.Add(s);
        return s.FormKey;
    }

    IMajorRecordGetter Misc(string eid, uint value, string name)
    {
        var m = _mod.MiscItems.AddNew();
        m.EditorID = eid;
        m.Value = value;
        m.Name = name;
        return m;
    }

    IMajorRecordGetter Weapon(string eid, params FormKey[] keywords)
    {
        var w = _mod.Weapons.AddNew();
        w.EditorID = eid;
        var kws = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>>();
        foreach (var k in keywords) kws.Add(new FormLink<IKeywordGetter>(k));
        w.Keywords = kws;
        return w;
    }

    IMajorRecordGetter Npc(string eid, FormKey faction, sbyte rank)
    {
        var n = _mod.Npcs.AddNew();
        n.EditorID = eid;
        n.Factions.Add(new RankPlacement { Faction = new FormLink<IFactionGetter>(faction), Rank = rank });
        return n;
    }

    static HashSet<FormKey> Run(IEnumerable<IMajorRecordGetter> records, string clause)
    {
        var (set, err) = FieldPredicateSet.Parse(new[] { clause });
        Assert.Null(err);
        var hits = records.Where(b => set!.Matches(b)).Select(b => b.FormKey).ToHashSet();
        Assert.Null(set!.FatalError);
        return hits;
    }

    HashSet<FormKey> Keys(params FormKey[] keys) => keys.ToHashSet();
    HashSet<FormKey> AllStaticsBut(params FormKey[] keys) => _statics.Select(s => s.FormKey).Except(keys).ToHashSet();

    [Theory]
    [InlineData("0x4")]
    [InlineData("0X4")]
    [InlineData("0x04")]
    [InlineData("0x0000004")]
    [InlineData("0x00000004")]
    public void HexOnAnIntegerFieldIsItsNumber(string hex)
    {
        Assert.Equal(Keys(_four, _fourToo), Run(_statics, $"MajorRecordFlagsRaw = {hex}"));
        Assert.Null(SchemaPlan.Of($"MajorRecordFlagsRaw = {hex}", "STAT"));
    }

    [Fact]
    public void NotEqualAndTheListsReadHexToo()
    {
        Assert.Equal(AllStaticsBut(_four, _fourToo), Run(_statics, "MajorRecordFlagsRaw != 0x4"));
        Assert.Equal(AllStaticsBut(_four, _fourToo), Run(_statics, "MajorRecordFlagsRaw not in [0x4]"));
        Assert.Equal(Keys(_four, _fourToo), Run(_statics, "MajorRecordFlagsRaw in [0x4]"));
        Assert.Equal(Keys(_four, _fourToo), Run(_statics, "MajorRecordFlagsRaw in [4, 0x4]"));
    }

    /// <summary>Aaron's pick (Q1): hex is the field's bits at its width, so the signed header int with bit 31 set matches
    /// the hex `has` tests the same bits with; read as a number by value, 0x80000400 is no int and would match nothing.</summary>
    [Fact]
    public void HexIsTheFieldsBitsSoBit31MatchesTheNegativeRead()
    {
        Assert.Equal(Keys(_high), Run(_statics, "MajorRecordFlagsRaw = 0x80000400"));
        Assert.Equal(Run(_statics, "MajorRecordFlagsRaw has 0x80000400"), Run(_statics, "MajorRecordFlagsRaw = 0x80000400"));
    }

    [Fact]
    public void BitsAtTheFieldsOwnWidthOnASignedByte()
    {
        Assert.Equal(Keys(_npcOne.FormKey), Run(new[] { _npcOne, _npcMinus }, "Factions[*any].Rank = 0x1"));
        Assert.Equal(Keys(_npcMinus.FormKey), Run(new[] { _npcOne, _npcMinus }, "Factions[*any].Rank = 0xFF"));
    }

    [Theory]
    [InlineData("0xA")]
    [InlineData("0xa")]
    public void HexOnAnUnsignedFieldIsItsNumber(string hex) =>
        Assert.Equal(Keys(_miscTen.FormKey), Run(new[] { _miscTen, _miscHexName }, $"Value = {hex}"));

    [Fact]
    public void HexOnACountIsTheCount()
    {
        Assert.Equal(Keys(_weapTwo.FormKey), Run(new[] { _weapTwo, _weapOne }, "Keywords[*count] = 0x2"));
        Assert.Null(SchemaPlan.Of("Keywords[*count] = 0x2", "WEAP"));
    }

    [Fact]
    public void DecimalStillComparesAsBefore()
    {
        Assert.Equal(Keys(_four, _fourToo), Run(_statics, "MajorRecordFlagsRaw = 4"));
        Assert.Equal(Keys(_high), Run(_statics, "MajorRecordFlagsRaw = -2147482624"));
        Assert.Equal(Keys(_four, _fourToo), Run(_statics, "MajorRecordFlagsRaw = 00000004"));
    }

    [Fact]
    public void AStringFieldKeepsATextCompare()
    {
        Assert.Equal(Keys(_miscHexName.FormKey), Run(new[] { _miscHexName, _miscFourName }, "Name = 0x4"));
        Assert.Null(SchemaPlan.Of("Name = 0x4", "MISC"));
    }

    [Fact]
    public void HexOnAFloatFieldIsRefusedSayingItTakesADecimalNumber()
    {
        var r = SchemaPlan.Of("Data.Speed = 0x1", "WEAP");
        Assert.Contains("'0x1' is no value 'Data.Speed' can hold", r);
        Assert.Contains("written in decimal", r);
        Assert.Null(SchemaPlan.Of("Data.Speed = 1", "WEAP"));
    }

    [Theory]
    [InlineData("EquipmentType = 013F44")]
    [InlineData("EquipmentType = 0x013F44")]
    [InlineData("EquipmentType in [013F44, 013F45:Skyrim.esm]")]
    public void ABareIdOnALinkIsRefusedNamingThePluginForm(string clause)
    {
        var r = SchemaPlan.Of(clause, "WEAP");
        Assert.Contains("'XXXXXX:Plugin.esp'", r);
        Assert.Contains("write '013F44:<the plugin that defines it>'", r);
    }

    [Fact]
    public void APluginFormIdOnALinkPasses() => Assert.Null(SchemaPlan.Of("EquipmentType = 013F44:Skyrim.esm", "WEAP"));

    [Theory]
    [InlineData("Factions[*any].Rank = 0x100", "NPC_", "at most 8 bits")]
    [InlineData("MajorRecordFlagsRaw = 0x100000000", "STAT", "at most 32 bits")]
    [InlineData("MajorRecordFlagsRaw = -0x4", "STAT", "as an integer (int)")]
    public void HexWiderThanTheFieldOrSignedIsRefused(string clause, string type, string says) =>
        Assert.Contains(says, SchemaPlan.Of(clause, type));

    /// <summary>A runtime FormID that does not resolve is refused only where a leaf could be the link it names; on an
    /// integer field an eight-digit literal is a number (this plan has no load order, so every runtime literal misses).</summary>
    [Fact]
    public void AnEightDigitLiteralOnAnIntegerFieldIsANumber()
    {
        Assert.Null(SchemaPlan.Of("MajorRecordFlagsRaw = 0x80000400", "STAT"));
        Assert.Null(SchemaPlan.Of("MajorRecordFlagsRaw in [0x80000400, 4]", "STAT"));
        Assert.Null(SchemaPlan.Of("Value = 90000000", "MISC"));
    }

    [Fact]
    public void AnUnresolvedRuntimeFormIdOnALinkIsRefusedAsOne()
    {
        var r = SchemaPlan.Of("ObjectEffect = 000A2C94", "WEAP");
        Assert.Contains("RUNTIME FormID", r);
        Assert.Contains("XXXXXX:Plugin.esp", r);
        Assert.Contains("list entry '000A2C94' is a RUNTIME FormID", SchemaPlan.Of("Keywords[*any] in [000A2C94]", "WEAP"));
    }

    [Fact]
    public void AnUnresolvedRuntimeFormIdWhereTheSchemaCannotTellIsRefusedAsOne() =>
        Assert.Contains("RUNTIME FormID", SchemaPlan.Of("Responses[*any].Unknown2[0] = 7F000001", "INFO"));

    /// <summary>A condition target holds a FormID or a numeric index, so a number there is not refused as a bad link.</summary>
    [Fact]
    public void AConditionTargetTakesANumber() => Assert.Null(SchemaPlan.Of("Conditions[*any].Data.Global = 3", "MGEF"));

    [Fact]
    public void MixedKindsRefuseOnlyWhenNoLeafReadsTheLiteral()
    {
        Assert.Null(SchemaPlan.Of("Priority = 0x1", "DIAL", "QUST"));
        var r = SchemaPlan.Of("Rank = Foo", "RELA", "ECZN");
        Assert.Contains("as an integer (byte)", r);
        Assert.Contains("enum it takes one of its names", r);
    }

    [Fact]
    public void EnumFieldsKeepTheirOwnReading()
    {
        Assert.Null(SchemaPlan.Of("BodyTemplate.ArmorType = 0x1", "ARMO"));
        Assert.Null(SchemaPlan.Of("SkyrimMajorRecordFlags = 0x4", "STAT"));
        Assert.Contains("names no value of the", SchemaPlan.Of("BodyTemplate.ArmorType = Heavvy", "ARMO"));
    }
}

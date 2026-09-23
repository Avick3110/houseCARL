using HousecarlGenerator;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A pre-flight rulebook over a corpus this fixture generates into its own temp folder, so nothing here
/// touches the process-wide <c>CorpusRulebook.CorpusPath</c> a world sets.</summary>
public sealed class OwnCorpusFixture : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-own-corpus-" + Guid.NewGuid().ToString("N"));

    public CorpusRulebook Rulebook { get; }

    public OwnCorpusFixture()
    {
        var gen = Path.Combine(_root, "gen");
        CorpusGenerator.GenerateAll(gen, Path.Combine(_root, "ref"));
        Rulebook = CorpusRulebook.Load(Path.Combine(gen, "corpus.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}

/// <summary>
/// A condition's FormLinkOrIndex target set outside a compose (Stryker row T15): the value lands, and its form or
/// index reading sets the arm's discriminator.
/// </summary>
[Trait("tier", "unit")]
public sealed class ConditionTargetSetTests
{
    static HasPerkConditionData SetPerk(string value)
    {
        var mod = new SkyrimMod(new ModKey("HcFloiMod", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var mgef = mod.MagicEffects.AddNew("HcFloiEffect");
        mgef.Conditions.Add(new ConditionFloat { Data = new HasPerkConditionData() });
        WriteEngine.ApplyVerb(mgef, new WriteRequest
        {
            RecordType = "MagicEffect", Path = new[] { "Conditions[0]", "Data", "Perk" }, Verb = "Set", Value = value,
        });
        return (HasPerkConditionData)mgef.Conditions[0].Data;
    }

    [Fact]
    public void AFormIdLandsOnTheConditionTarget()
    {
        var data = SetPerk("000123:Skyrim.esm");
        Assert.Equal(FormKey.Factory("000123:Skyrim.esm"), data.Perk.Link.FormKey);
        Assert.False(data.UseAliases);
    }

    [Fact]
    public void AnAliasIndexLandsOnTheConditionTarget()
    {
        var data = SetPerk("alias 3");
        Assert.Equal(3u, data.Perk.Index);
        Assert.True(data.UseAliases);
    }
}

/// <summary>
/// Add and valued Remove on a [Flags] field (Stryker row T16) flip one bit and keep the rest.
/// </summary>
[Trait("tier", "unit")]
public sealed class FlagsBitVerbTests
{
    static Faction FactionWith(Faction.FactionFlag flags)
    {
        var mod = new SkyrimMod(new ModKey("HcFlagsMod", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var f = mod.Factions.AddNew("HcFlagsFaction");
        f.Flags = flags;
        return f;
    }

    static void Apply(Faction f, string verb, string value) => WriteEngine.ApplyVerb(f, new WriteRequest
    {
        RecordType = "Faction", Path = new[] { "Flags" }, Verb = verb, Value = value,
    });

    [Fact]
    public void AddingOneFlagKeepsTheOthersSet()
    {
        var f = FactionWith(Faction.FactionFlag.HiddenFromPC | Faction.FactionFlag.TrackCrime);
        Apply(f, "Add", "SpecialCombat");
        Assert.Equal(Faction.FactionFlag.HiddenFromPC | Faction.FactionFlag.TrackCrime | Faction.FactionFlag.SpecialCombat, f.Flags);
    }

    [Fact]
    public void RemovingOneFlagClearsOnlyThatFlag()
    {
        var f = FactionWith(Faction.FactionFlag.HiddenFromPC | Faction.FactionFlag.TrackCrime);
        Apply(f, "Remove", "HiddenFromPC");
        Assert.Equal(Faction.FactionFlag.TrackCrime, f.Flags);
    }
}

/// <summary>
/// A scalar Set lands for each primitive and value-type family the coercion covers (Stryker row T24), read back off
/// the record it was applied to.
/// </summary>
[Trait("tier", "unit")]
public sealed class ScalarSetTests
{
    static SkyrimMod NewMod() => new(new ModKey("HcScalarMod", ModType.Plugin), SkyrimRelease.SkyrimSE);

    static void Set(object record, string recordType, string value, params string[] path) =>
        WriteEngine.ApplyVerb(record, new WriteRequest { RecordType = recordType, Path = path, Verb = "Set", Value = value });

    [Fact]
    public void AStringLands()
    {
        var f = NewMod().Factions.AddNew("HcScalarOld");
        Set(f, "Faction", "HcScalarNew", "EditorID");
        Assert.Equal("HcScalarNew", f.EditorID);
    }

    [Fact]
    public void AnIntLands()
    {
        var g = new GlobalInt(new FormKey(new ModKey("HcScalarMod", ModType.Plugin), 0x800), SkyrimRelease.SkyrimSE);
        Set(g, "GlobalInt", "-42", "Data");
        Assert.Equal(-42, g.Data);
    }

    [Theory]
    [InlineData("250", 250u)]
    [InlineData("0x1F", 31u)]
    public void AUintLandsInDecimalAndInHex(string text, uint expected)
    {
        var w = NewMod().Weapons.AddNew("HcScalarWeap");
        Set(w, "Weapon", text, "BasicStats", "Value");
        Assert.Equal(expected, w.BasicStats!.Value);
    }

    /// <summary>No Skyrim record field is a double, so the coercion is asked directly.</summary>
    [Fact]
    public void ADoubleCoerces()
    {
        Assert.True(WriteEngine.TryCoerce("1.5", typeof(double), out var v));
        Assert.Equal(1.5, v);
    }

    [Fact]
    public void AnEnumSpelledInAnotherCaseLands()
    {
        var m = NewMod().MagicEffects.AddNew("HcScalarEffect");
        Set(m, "MagicEffect", "fireandforget", "CastType");
        Assert.Equal(CastType.FireAndForget, m.CastType);
    }

    [Fact]
    public void AP3FloatLandsFromCommaSeparatedComponents()
    {
        var r = new PlacedObject(new FormKey(new ModKey("HcScalarMod", ModType.Plugin), 0x800), SkyrimRelease.SkyrimSE);
        Set(r, "PlacedObject", "1,2,3", "Placement", "Position");
        Assert.Equal(new P3Float(1, 2, 3), r.Placement!.Position);
    }

    [Fact]
    public void AByteBlobLandsFromHex()
    {
        var w = NewMod().Weapons.AddNew("HcScalarBlobWeap");
        Set(w, "Weapon", "0A0B", "Unused");
        Assert.Equal(new byte[] { 0x0A, 0x0B }, w.Unused!.Value.ToArray());
    }
}

/// <summary>
/// Clearing a FormLink by a null synonym (Stryker row T25): each synonym, in any case and padded, clears the link;
/// a value that is neither a synonym nor a FormID is refused at pre-flight.
/// </summary>
[Trait("tier", "integration")]
public sealed class FormLinkNullSynonymTests : IClassFixture<OwnCorpusFixture>
{
    readonly CorpusRulebook _rulebook;

    public FormLinkNullSynonymTests(OwnCorpusFixture f) => _rulebook = f.Rulebook;

    [Theory]
    [InlineData("0")]
    [InlineData("00000000")]
    [InlineData("Null")]
    [InlineData(" null ")]
    [InlineData("000000:Null")]
    [InlineData("000000:NULL")]
    public void ANullSynonymClearsTheLink(string synonym)
    {
        var mod = new SkyrimMod(new ModKey("HcNullMod", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var w = mod.Weapons.AddNew("HcNullWeap");
        w.EquipmentType.SetTo(FormKey.Factory("013F42:Skyrim.esm"));

        WriteEngine.ApplyVerb(w, new WriteRequest
        {
            RecordType = "Weapon", Path = new[] { "EquipmentType" }, Verb = "Set", Value = synonym,
        });
        Assert.True(w.EquipmentType.IsNull);
    }

    [Fact]
    public void AValueThatIsNeitherASynonymNorAFormIdIsRefusedAtPreFlight()
        => Assert.NotNull(_rulebook.Validate(new WriteRequest
        {
            RecordType = "Weapon", Path = new[] { "EquipmentType" }, Verb = "Set", Value = "notaformkey",
        }));
}

/// <summary>
/// A same-call sibling reference (Stryker row T26): <c>@X</c>, a one-character editorid, is a reference to the record
/// created earlier in the call, not a malformed FormID.
/// </summary>
[Trait("tier", "integration")]
public sealed class SiblingReferenceTests : IClassFixture<OwnCorpusFixture>
{
    readonly CorpusRulebook _rulebook;

    public SiblingReferenceTests(OwnCorpusFixture f) => _rulebook = f.Rulebook;

    [Fact]
    public void AOneCharacterEditorIdSiblingReferenceIsAccepted()
        => Assert.Null(_rulebook.Validate(new WriteRequest
        {
            RecordType = "Weapon", Path = new[] { "EquipmentType" }, Verb = "Set", Value = "@X",
        }, new[] { "X" }));
}

using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>#1464 through the records tool over a one-plugin order: an eight-digit hex literal on an integer field is
/// a number even where its index byte names no plugin, a runtime FormID on a link still resolves, and text and json agree.</summary>
[Trait("tier", "integration")]
public sealed class WhereHexOperandOrderTests : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-where-hex-");
    readonly FormKey _race;

    public WhereHexOperandOrderTests()
    {
        var key = new ModKey("HcHexWorld", ModType.Master);
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        foreach (var (eid, raw) in new[] { ("HcFourA", 4), ("HcFourB", 4), ("HcZero", 0), ("HcHigh", unchecked((int)0x80000400)) })
        {
            var s = m.Statics.AddNew();
            s.EditorID = eid;
            s.MajorRecordFlagsRaw = raw;
        }
        foreach (var (eid, value) in new[] { ("HcBig", 90000000u), ("HcTen", 10u) })
        {
            var x = m.MiscItems.AddNew();
            x.EditorID = eid;
            x.Value = value;
        }
        var race = m.Races.AddNew();
        race.EditorID = "HcRace";
        _race = race.FormKey;
        var npc = m.Npcs.AddNew();
        npc.EditorID = "HcNpc";
        npc.Race.SetTo(_race);
        m.Npcs.AddNew().EditorID = "HcNpcNoRace";
        m.BeginWrite.ToPath(Path.Combine(_mo2.DataDir, key.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        _mo2.Profile("HcHexWorld.esm\r\n", "*HcHexWorld.esm\r\n", "");
    }

    public void Dispose() => _mo2.Delete();

    string Where(string type, string clause, string format = "text")
    {
        using var svc = _mo2.Open();
        return RecordsTools.Records(svc, types: new[] { type }, where: new[] { clause }, counts_only: true, format: format);
    }

    /// <summary>Index 0x80 is empty, so the parse used to refuse this as an unresolvable FormID; it is the field's bits.</summary>
    [Fact]
    public void AnEightDigitHexWhoseIndexIsEmptyIsTheFieldsBits()
    {
        Assert.Contains("1 match", Where("STAT", "MajorRecordFlagsRaw = 0x80000400"));
        Assert.Contains("1 match", Where("STAT", "MajorRecordFlagsRaw in [0x80000400]"));
    }

    [Fact]
    public void AnEightDigitHexWhoseIndexResolvesIsStillTheNumber() =>
        Assert.Contains("2 match", Where("STAT", "MajorRecordFlagsRaw = 0x00000004"));

    [Fact]
    public void AnEightDigitDecimalOnAnIntegerFieldIsANumber()
    {
        var r = Where("MISC", "Value = 90000000");
        Assert.Contains("1 match", r);
        Assert.DoesNotContain("RUNTIME FormID", r);
    }

    [Fact]
    public void TextAndJsonAgree()
    {
        Assert.Contains("2 match", Where("STAT", "MajorRecordFlagsRaw = 0x4"));
        using var doc = JsonDocument.Parse(Where("STAT", "MajorRecordFlagsRaw = 0x4", "json"));
        Assert.Equal(2, doc.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public void ALinkStepReadsHexOnTheTargetsIntegerField() =>
        Assert.Contains("1 match", Where("NPC_", "Race->MajorRecordFlagsRaw = 0x0"));

    [Fact]
    public void ARuntimeFormIdOnALinkStillResolves()
    {
        Assert.Contains("1 match", Where("NPC_", $"Race = 00{_race.ID:X6}"));
        Assert.Contains("cannot resolve", Where("NPC_", $"Race = 7F{_race.ID:X6}"));
    }
}

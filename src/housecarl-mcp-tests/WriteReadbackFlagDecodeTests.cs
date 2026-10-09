using System.Text.Json;
using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.ApplyGuardWorld;

namespace HousecarlMcpTests;

/// <summary>The write read-back decodes a flags value the way a read does (#1109): an apply setting an armor's biped
/// slots and its record flags echoes what a read of the written patch prints for the same fields.</summary>
[Trait("tier", "integration")]
public sealed class WriteReadbackFlagDecodeTests : IDisposable
{
    readonly ApplyGuardWorld W = new();
    public void Dispose() => W.Dispose();

    static readonly string[] Paths = { "BodyTemplate.FirstPersonFlags", "MajorFlags" };

    string FlagOps() =>
        "[{\"formid\":\"" + W.ArmorFid + "\",\"field_path\":\"BodyTemplate.FirstPersonFlags\",\"value\":\"1073741828\"}," +
        "{\"formid\":\"" + W.ArmorFid + "\",\"field_path\":\"MajorFlags\",\"value\":\"260\"}]";

    string ReadPatch(string plugin, string path, string? format = null, string? fid = null) =>
        RecordsTools.Records(W.Svc, formids: new[] { fid ?? W.ArmorFid }, source: Je("\"" + plugin + "\""),
            project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { path } }, format: format);

    /// <summary>The read's own line for one field, without its indent.</summary>
    string ReadLine(string plugin, string path, string? fid = null)
    {
        var line = ReadPatch(plugin, path, fid: fid).ReplaceLineEndings("\n").Split('\n').Single(l => l.StartsWith("  " + path + " = "));
        Assert.Contains("   (", line);   // the read decodes it, so a match below is a decode and not two bare values
        return line.TrimStart();
    }

    static JsonElement ReadField(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("records")[0].GetProperty("fields")[0];

    [Fact]
    public void TextReadbackDecodesBipedAndRecordFlagsAsAReadDoes()
    {
        var r = ApplyTools.Apply(W.Svc, ops: Je(FlagOps()), patch: "FlagEcho", readback: true).ReplaceLineEndings("\n");
        foreach (var p in Paths) Assert.Contains("\n    " + ReadLine("FlagEcho.esp", p) + "\n", r);
    }

    [Fact]
    public void JsonReadbackDecodesBipedAndRecordFlagsAsAReadDoes()
    {
        var r = ApplyTools.Apply(W.Svc, ops: Je(FlagOps()), patch: "FlagEchoJson", readback: true, format: "json");
        var fields = JsonDocument.Parse(r).RootElement.GetProperty("readback")[0].GetProperty("fields").EnumerateArray().ToList();
        foreach (var p in Paths)
        {
            var echo = fields.Single(f => f.GetProperty("path").GetString() == p);
            var read = ReadField(ReadPatch("FlagEchoJson.esp", p, "json"));
            Assert.Equal(read.GetProperty("value").GetString(), echo.GetProperty("value").GetString());
            Assert.Equal(read.GetProperty("display").GetString(), echo.GetProperty("display").GetString());
            Assert.Equal(read.TryGetProperty("slots", out var rs) ? rs.GetRawText() : null,
                         echo.TryGetProperty("slots", out var es) ? es.GetRawText() : null);
        }
    }

    [Fact]
    public void DefaultEchoDecodesFlagsOnTheEditLineAndTheInPlaceVerifyClause()
    {
        var r = ApplyTools.Apply(W.Svc, ops: Je(FlagOps()), in_place: W.MasterName, acknowledge: true).ReplaceLineEndings("\n");
        foreach (var p in Paths)
        {
            var value = ReadLine(W.MasterName, p)[(p.Length + 3)..];   // "<token>   (<display>)"
            var token = value.Split("   (")[0];
            Assert.Contains(p + " = " + token + "  -> " + value + "\n", r);
            Assert.Contains(p + " = " + token + ": " + value, r);
        }
    }

    static JsonElement Op(JsonElement root, string path) =>
        root.GetProperty("ops").EnumerateArray().Single(o => o.GetProperty("label").GetString()!.Contains(" " + path + " = "));

    [Fact]
    public void JsonDefaultEchoCarriesTheReadsDecodeOnTheOpRow()
    {
        var root = JsonDocument.Parse(ApplyTools.Apply(W.Svc, ops: Je(FlagOps()), patch: "FlagOpJson", format: "json")).RootElement;
        foreach (var p in Paths)
            Assert.Equal(ReadField(ReadPatch("FlagOpJson.esp", p, "json")).GetProperty("display").GetString(),
                         Op(root, p).GetProperty("display").GetString());
    }

    [Fact]
    public void DryRunWouldBecomeDecodesAsItsOwnPreviewDoes()
    {
        var r = ApplyTools.Apply(W.Svc, ops: Je(FlagOps()), patch: "FlagDry", dry_run: true, readback: true).ReplaceLineEndings("\n");
        var root = JsonDocument.Parse(ApplyTools.Apply(W.Svc, ops: Je(FlagOps()), patch: "FlagDryJson", dry_run: true,
            readback: true, format: "json")).RootElement;
        var preview = root.GetProperty("readback")[0].GetProperty("fields").EnumerateArray().ToList();
        foreach (var p in Paths)
        {
            var value = r.Split('\n').Single(l => l.StartsWith("    " + p + " = "))[(p.Length + 7)..];
            Assert.Contains("   (", value);
            Assert.Contains("  -> would become " + value + "\n", r);
            Assert.Equal(preview.Single(f => f.GetProperty("path").GetString() == p).GetProperty("display").GetString(),
                         Op(root, p).GetProperty("display").GetString());
        }
    }

    [Fact]
    public void ASupersededOpShowsTheDecodeOfTheLeafsFinalState()
    {
        var ops = "[{\"formid\":\"" + W.ArmorFid + "\",\"field_path\":\"MajorFlags\",\"value\":\"4\"}," +
                  "{\"formid\":\"" + W.ArmorFid + "\",\"field_path\":\"MajorFlags\",\"value\":\"260\"}]";
        var r = ApplyTools.Apply(W.Svc, ops: Je(ops), patch: "FlagSuper").ReplaceLineEndings("\n");
        var value = ReadLine("FlagSuper.esp", "MajorFlags")["MajorFlags = ".Length..];
        Assert.Contains("Set MajorFlags = 4  -> " + value + "  [the leaf as the file now holds it", r);
    }

    [Fact]
    public void CreateEditLineDecodesAsAReadOfTheCreatedRecord()
    {
        var r = CreateTools.Create(W.Svc, records: Je("[{\"record_type\":\"Armor\",\"editorid\":\"HcFlagArmor\"," +
            "\"ops\":[{\"field_path\":\"MajorFlags\",\"value\":\"260\"}]}]"), patch: "FlagCreate").ReplaceLineEndings("\n");
        var value = ReadLine("FlagCreate.esp", "MajorFlags", fid: "000800:FlagCreate.esp")["MajorFlags = ".Length..];
        Assert.Contains("Set MajorFlags = 260  -> " + value + "\n", r);
    }

    [Fact]
    public void ForwardFullReadbackDecodesAsAReadDoes()
    {
        ApplyTools.Apply(W.Svc, ops: Je(FlagOps()), patch: "FlagSrc");
        var r = ForwardTools.Forward(W.Svc, formids: new[] { W.ArmorFid }, source: "FlagSrc.esp", patch: "FlagFwd", readback: true)
            .ReplaceLineEndings("\n");
        foreach (var p in Paths) Assert.Contains("\n    " + ReadLine("FlagFwd.esp", p) + "\n", r);
    }

    [Fact]
    public void JsonReadbackCarriesTheReadsNoteRefOnASummaryLine()
    {
        var ops = "[{\"formid\":\"" + W.PotionAFid + "\",\"field_path\":\"Effects[0].Data.Magnitude\",\"value\":\"9\"}]";
        var r = ApplyTools.Apply(W.Svc, ops: Je(ops), patch: "NoteRefJson", readback: true, format: "json");
        var echo = JsonDocument.Parse(r).RootElement.GetProperty("readback")[0].GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("path").GetString() == "Effects[0]");
        var read = JsonDocument.Parse(RecordsTools.Records(W.Svc, formids: new[] { W.PotionAFid }, source: Je("\"NoteRefJson.esp\""),
                project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Effects" }, depth = 2 }, format: "json"))
            .RootElement.GetProperty("records")[0].GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("path").GetString() == "Effects[0]");
        Assert.Equal(read.GetProperty("note_ref").GetString(), echo.GetProperty("note_ref").GetString());
    }
}

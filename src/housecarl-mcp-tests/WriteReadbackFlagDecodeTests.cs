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

    string ReadPatch(string plugin, string path, string? format = null) =>
        RecordsTools.Records(W.Svc, formids: new[] { W.ArmorFid }, source: Je("\"" + plugin + "\""),
            project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { path } }, format: format);

    /// <summary>The read's own line for one field, without its indent.</summary>
    string ReadLine(string plugin, string path)
    {
        var line = ReadPatch(plugin, path).ReplaceLineEndings("\n").Split('\n').Single(l => l.StartsWith("  " + path + " = "));
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
}

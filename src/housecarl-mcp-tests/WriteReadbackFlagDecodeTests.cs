using System.Text.Json;
using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.ApplyGuardWorld;

namespace HousecarlMcpTests;

/// <summary>The write read-back decodes a flags value the way a read does (#1109): an apply setting an armor's biped
/// slots and its record flags echoes the same display, and json the same slot array, as FlagDecodeFormatsTests' reads.</summary>
[Trait("tier", "integration")]
public sealed class WriteReadbackFlagDecodeTests : IDisposable
{
    readonly ApplyGuardWorld W = new();
    public void Dispose() => W.Dispose();

    string FlagOps() =>
        "[{\"formid\":\"" + W.ArmorFid + "\",\"field_path\":\"BodyTemplate.FirstPersonFlags\",\"value\":\"1073741828\"}," +
        "{\"formid\":\"" + W.ArmorFid + "\",\"field_path\":\"MajorFlags\",\"value\":\"260\"}]";

    [Fact]
    public void TextReadbackDecodesBipedAndRecordFlagsAsAReadDoes()
    {
        var r = ApplyTools.Apply(W.Svc, ops: Je(FlagOps()), patch: "FlagEcho", readback: true).ReplaceLineEndings("\n");
        Assert.Contains("    BodyTemplate.FirstPersonFlags = 1073741828   (Body | slot60 | slots 32 60)\n", r);
        Assert.Contains("    MajorFlags = 260   (NonPlayable | bit8)\n", r);
    }

    [Fact]
    public void JsonReadbackDecodesBipedAndRecordFlagsAsAReadDoes()
    {
        var r = ApplyTools.Apply(W.Svc, ops: Je(FlagOps()), patch: "FlagEchoJson", readback: true, format: "json");
        var fields = JsonDocument.Parse(r).RootElement.GetProperty("readback")[0].GetProperty("fields").EnumerateArray().ToList();
        var biped = fields.Single(f => f.GetProperty("path").GetString() == "BodyTemplate.FirstPersonFlags");
        Assert.Equal("1073741828", biped.GetProperty("value").GetString());
        Assert.Equal("Body | slot60 | slots 32 60", biped.GetProperty("display").GetString());
        Assert.Equal(new[] { 32, 60 }, biped.GetProperty("slots").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        var major = fields.Single(f => f.GetProperty("path").GetString() == "MajorFlags");
        Assert.Equal("260", major.GetProperty("value").GetString());
        Assert.Equal("NonPlayable | bit8", major.GetProperty("display").GetString());
    }
}

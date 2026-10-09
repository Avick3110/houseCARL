using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A form object over the wire: a one-line file's row shows its JSON path alone, a multi-line file's its line beside it, and the json twin keeps the real line.</summary>
[Trait("tier", "stdio")]
public sealed class SkseConfigFormObjectWireTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-skse-formobj-" + Guid.NewGuid().ToString("N"));
    readonly string _instance;

    public SkseConfigFormObjectWireTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "game", "Data"));
        _instance = Path.Combine(_root, "inst");
        var group = Path.Combine(_instance, "mods", "FormObjMod", "SKSE", "Plugins", "HcFormObj");
        Directory.CreateDirectory(group);
        File.WriteAllText(Path.Combine(group, "one.json"), "{\"a\":{\"id\":2049,\"plugin\":\"Ghost.esp\"}}\r\n");
        File.WriteAllText(Path.Combine(group, "multi.json"), "{\r\n  \"b\": {\"id\": 2050, \"plugin\": \"Ghost.esp\"}\r\n}\r\n");

        var key = new ModKey("HcFormObj", ModType.Plugin);
        new SkyrimMod(key, SkyrimRelease.SkyrimSE).BeginWrite
            .ToPath(Path.Combine(_instance, "mods", "FormObjMod", key.FileName.String))
            .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        File.WriteAllText(Path.Combine(_instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(_instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + key.FileName.String + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + key.FileName.String + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+FormObjMod\r\n");
    }

    [Fact]
    public void AOneLineFileShowsThePathAloneAMultiLineFileItsLineBesideItAndTheJsonKeepsTheRealLineAndShape()
    {
        using var server = new ServerFixture();
        var set = server.Call(ToolNames.SetMo2Instance, $$"""{"path": {{JsonSerializer.Serialize(_instance)}}}""");
        Assert.Contains("configured houseCARL", set.Text, StringComparison.Ordinal);

        var text = server.Call(ToolNames.Skse, """{"findings":"config","filter":"HcFormObj"}""");
        Assert.False(text.IsError, text.Describe());
        Assert.Contains("'{\"id\":2049,\"plugin\":\"Ghost.esp\"}' (at $.a)", text.Text, StringComparison.Ordinal);
        Assert.Contains("'{\"id\":2050,\"plugin\":\"Ghost.esp\"}' (line 2, at $.b)", text.Text, StringComparison.Ordinal);

        var json = server.Call(ToolNames.Skse, """{"findings":"config","filter":"HcFormObj","format":"json"}""");
        Assert.False(json.IsError, json.Describe());
        using var doc = JsonDocument.Parse(json.Text);
        var rows = doc.RootElement.GetProperty("files").EnumerateArray()
            .SelectMany(f => f.GetProperty("references").EnumerateArray())
            .Select(r => (r.GetProperty("shape").GetString(), r.GetProperty("line").GetInt32(), r.GetProperty("path").GetString()))
            .OrderBy(r => r.Item3).ToArray();
        Assert.Equal(new[] { ("form_object", 1, "$.a"), ("form_object", 2, "$.b") }, rows);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }
}

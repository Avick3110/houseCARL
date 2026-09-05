using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// One successful <c>housecarl_skse</c> answer driven all the way over the wire: an MO2 instance of SKSE configs, the
/// tool called with <c>format='json'</c> and a <c>limit=</c>/<c>offset=</c> window, and the document that comes back.
/// The rest of the transport lane is driven straight into the renders, which proves the renders and nothing about the
/// path from the published arguments to them — a limit that never reaches the window would pass every one of those
/// tests. This gets its OWN server, because the shared <see cref="ServerFixture"/> is deliberately unconfigured.
/// </summary>
[Trait("tier", "stdio")]
public sealed class SkseTransportWireTests : IDisposable
{
    /// <summary>How many SKSE config files the world holds — the fixture-known total every accounting is read
    /// against.</summary>
    const int Configs = 6;

    readonly string _root;
    readonly string _instance;

    public SkseTransportWireTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-skse-wire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "game", "Data"));

        _instance = Path.Combine(_root, "inst");
        var group = Path.Combine(_instance, "mods", "SkseWireMod", "SKSE", "Plugins", "HcWire");
        Directory.CreateDirectory(group);
        // Each config declares one form token against a plugin the order does not have — a stable, fixture-known
        // verdict that needs no plugin on disk.
        for (int i = 1; i <= Configs; i++)
            File.WriteAllText(Path.Combine(group, $"f{i}.ini"), $"[General]\r\ntarget=0x00080{i}|Ghost.esp\r\n");

        // One active plugin, because a profile that resolves none is refused before any family runs. Nothing reads its
        // records — it is here so the order exists.
        var key = new ModKey("HcWire", ModType.Plugin);
        new SkyrimMod(key, SkyrimRelease.SkyrimSE).BeginWrite
            .ToPath(Path.Combine(_instance, "mods", "SkseWireMod", key.FileName.String))
            .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        File.WriteAllText(Path.Combine(_instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(_instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + key.FileName.String + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + key.FileName.String + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+SkseWireMod\r\n");
    }

    ServerFixture Configured()
    {
        var server = new ServerFixture();
        var set = server.Call(ToolNames.SetMo2Instance,
            $$"""{"path": {{JsonSerializer.Serialize(_instance)}}}""");
        Assert.Contains($"configured houseCARL -> MO2 instance '{_instance}'", set.Text, StringComparison.Ordinal);
        return server;
    }

    /// <summary>format='json' with a window: the document comes back parseable, carries the windowed rows and the
    /// in-band accounting, and the numbers are the ones the published limit= and offset= asked for. This is the seam
    /// the render-level tests cannot see — a limit that never reached the window would pass all of them.</summary>
    [Fact]
    public void APagedJsonAnswerComesBackOverTheWireWithItsAccounting()
    {
        using var server = Configured();

        var r = server.Call(ToolNames.Skse, """{"findings":"config","format":"json","limit":2,"offset":1}""");

        Assert.False(r.IsError, r.Describe());
        Assert.DoesNotContain(ServerFixture.ConfigPrompt, r.Text, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(r.Text);
        Assert.Equal("config", doc.RootElement.GetProperty("family").GetString());

        var files = doc.RootElement.GetProperty("files").EnumerateArray()
                       .Select(f => f.GetProperty("file_name").GetString()).ToArray();
        Assert.Equal(new[] { "f2.ini", "f3.ini" }, files);

        var a = doc.RootElement.GetProperty("accounting");
        Assert.Equal(Configs, a.GetProperty("total").GetInt32());
        Assert.Equal(2, a.GetProperty("rendered").GetInt32());
        Assert.Equal(1, a.GetProperty("skipped").GetInt32());
        Assert.Equal(Configs - 3, a.GetProperty("capped").GetInt32());
        Assert.Equal(1, a.GetProperty("offset").GetInt32());
    }

    /// <summary>The same window over the TEXT lane, so the two formats are known to be reading the same published
    /// arguments — and the text answer still ends on its accounting line and its family footer.</summary>
    [Fact]
    public void ThePagedTextAnswerCarriesTheSameWindowAndEndsOnTheFooter()
    {
        using var server = Configured();

        var r = server.Call(ToolNames.Skse, """{"findings":"config","limit":2,"offset":1}""");

        Assert.False(r.IsError, r.Describe());
        Assert.Contains($"[accounting] total={Configs} rendered=2 skipped=1 capped={Configs - 3} truncated=0 offset=1",
                        r.Text, StringComparison.Ordinal);
        Assert.Contains("re-call with limit=2 offset=3 for the next page.", r.Text, StringComparison.Ordinal);
        Assert.EndsWith(SkseTools.FamilyFooter(SkseTools.SkseFamily.Config), r.Text, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }
}

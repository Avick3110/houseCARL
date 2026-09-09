using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A one-plugin world holding a topic with the two PNAM shapes side by side: one INFO whose PNAM
/// subrecord is PRESENT with FormID zero (the "I am first" head marker), one with no PNAM subrecord at all.
///
/// <para>The zero PNAM is byte-patched onto the written plugin, not authored: Mutagen's writer emits NO subrecord
/// for a null link, so a writer-authored version of this fixture would carry the ABSENT shape twice and the test
/// would pass without measuring anything. The patch counter is asserted for exactly that reason.</para></summary>
public sealed class PresentNullLinkWorld : IDisposable
{
    public const string PluginName = "HcPnlMaster.esp";

    public string Root { get; }
    public LoadOrderService Svc { get; }

    /// <summary>The INFO whose PNAM subrecord is present and zero — the head marker.</summary>
    public FormKey MarkedLine { get; }

    /// <summary>The INFO carrying no PNAM subrecord.</summary>
    public FormKey PlainLine { get; }

    public PresentNullLinkWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-present-null-link-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "inst");
        var modDir = Path.Combine(instance, "mods", "PnlMod");
        Directory.CreateDirectory(modDir);
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));

        var mod = new SkyrimMod(ModKey.FromNameAndExtension(PluginName), SkyrimRelease.SkyrimSE);
        var topic = mod.DialogTopics.AddNew(); topic.EditorID = "HcPnlTopic";
        var plain = new DialogResponses(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcPnlPlain" };
        var marked = new DialogResponses(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcPnlMarked" };
        // Only the marked line gets a PNAM at all, so the byte patch below can zero every PNAM in the file and
        // still leave the plain line's ABSENT shape untouched.
        marked.PreviousDialog.SetTo(plain.FormKey);
        topic.Responses.Add(plain);
        topic.Responses.Add(marked);
        PlainLine = plain.FormKey;
        MarkedLine = marked.FormKey;

        var path = Path.Combine(modDir, PluginName);
        mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        // The plugin masters nothing, so its own records' FormIDs are stored with master index 0 — the raw
        // subrecord payload the marked line's PNAM carries, and what makes the patch hit that PNAM and not the
        // topic's own (unrelated, also 4-byte) PNAM priority field.
        Assert.Equal(1, ZeroPnamPointingAt(path, plain.FormKey.ID));

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + PluginName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + PluginName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+PnlMod\r\n");

        var store = new UserConfigStore(Path.Combine(Root, "user.json"));
        Svc = LoadOrderService.WithInstance(instance, 0, store);
    }

    /// <summary>Zero the 4 data bytes of every 4-byte PNAM subrecord in a plugin whose payload is
    /// <paramref name="targetId"/>, in place; returns how many were patched. The shape real CK / xEdit-filled
    /// plugins carry and Mutagen's writer will not emit.</summary>
    static int ZeroPnamPointingAt(string path, uint targetId)
    {
        var bytes = File.ReadAllBytes(path);
        int patched = 0;
        for (int i = 0; i + 10 <= bytes.Length; i++)
        {
            if (bytes[i] != (byte)'P' || bytes[i + 1] != (byte)'N' || bytes[i + 2] != (byte)'A' || bytes[i + 3] != (byte)'M')
                continue;
            if (BitConverter.ToUInt16(bytes, i + 4) != 4) continue;          // size field must be 4
            if (BitConverter.ToUInt32(bytes, i + 6) != targetId) continue;   // …and the link this INFO's PNAM holds
            bytes[i + 6] = bytes[i + 7] = bytes[i + 8] = bytes[i + 9] = 0;
            patched++;
            i += 9;
        }
        File.WriteAllBytes(path, bytes);
        return patched;
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>#697: a nullable link whose subrecord is PRESENT with FormID zero means the opposite of an absent one
/// (an INFO's PNAM: pinned to the head vs appended to the tail), so a record read must not render them alike.</summary>
[Trait("tier", "integration")]
public sealed class PresentNullLinkRenderTests : IDisposable
{
    readonly PresentNullLinkWorld _w = new();

    public void Dispose() => _w.Dispose();

    static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

    string ReadPnam(FormKey fk) =>
        RecordsTools.Records(_w.Svc, formids: new[] { Fid(fk) },
                             project: new RecordsTools.RecordsProject
                             { form = "fields", fields = new[] { "PreviousDialog" } });

    [Fact]
    public void APresentZeroPnamRendersApartFromAnAbsentOne()
    {
        var marked = ReadPnam(_w.MarkedLine);
        var plain = ReadPnam(_w.PlainLine);

        Assert.Contains("(null link, subrecord present)", marked);
        Assert.DoesNotContain("(null link, subrecord present)", plain);
        Assert.Contains("(null link)", plain);
    }
}

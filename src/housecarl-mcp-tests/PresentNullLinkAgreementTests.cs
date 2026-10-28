using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A master and a patch over it, each carrying the same INFO with its PNAM subrecord present and zero (the
/// head marker), byte-patched in both files because Mutagen's writer emits no subrecord for a null link.</summary>
public sealed class PresentNullLinkPairWorld : IDisposable
{
    public const string MasterName = "HcPnpMaster.esm";
    public const string PatchName = "HcPnpPatch.esp";
    public string Root { get; }
    public LoadOrderService Svc { get; }
    public FormKey MarkedLine { get; }

    public PresentNullLinkPairWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-pnl-pair-" + Guid.NewGuid().ToString("N"));
        var instance = SyntheticInstance.Create(Root);
        var sky = new SkyrimMod(new ModKey("Skyrim", ModType.Master), SkyrimRelease.SkyrimSE);
        SyntheticInstance.WriteMod(instance, "VanillaStub", sky);

        var master = new SkyrimMod(ModKey.FromNameAndExtension(MasterName), SkyrimRelease.SkyrimSE);
        var topic = master.DialogTopics.AddNew(); topic.EditorID = "HcPnpTopic";
        var plain = new DialogResponses(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcPnpPlain" };
        var marked = new DialogResponses(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcPnpMarked" };
        marked.PreviousDialog.SetTo(plain.FormKey);
        topic.Responses.Add(plain);
        topic.Responses.Add(marked);
        MarkedLine = marked.FormKey;
        var masterPath = SyntheticInstance.WriteMod(instance, "PnpMaster", master, sky);

        var patch = new SkyrimMod(ModKey.FromNameAndExtension(PatchName), SkyrimRelease.SkyrimSE);
        var patchTopic = patch.DialogTopics.GetOrAddAsOverride(topic);
        patchTopic.Responses.Clear();
        patchTopic.Responses.Add(marked.DeepCopy());
        var patchPath = SyntheticInstance.WriteMod(instance, "PnpPatch", patch, master);

        // Both files hold the plain line's FormID at master index 0, so one patch value hits the marked line's PNAM.
        Assert.Equal(1, ZeroPnamPointingAt(masterPath, plain.FormKey.ID));
        Assert.Equal(1, ZeroPnamPointingAt(patchPath, plain.FormKey.ID));

        SyntheticInstance.WriteProfile(instance,
            new[] { "# header", "+PnpPatch", "+PnpMaster", "+VanillaStub" },
            new[] { "# header", "Skyrim.esm", MasterName, PatchName },
            new[] { "*" + MasterName, "*" + PatchName });
        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "user.json")));
    }

    /// <summary>Zero the payload of every 4-byte PNAM subrecord holding <paramref name="targetId"/>; returns how many.</summary>
    static int ZeroPnamPointingAt(string path, uint targetId)
    {
        var bytes = File.ReadAllBytes(path);
        int patched = 0;
        for (int i = 0; i + 10 <= bytes.Length; i++)
        {
            if (bytes[i] != (byte)'P' || bytes[i + 1] != (byte)'N' || bytes[i + 2] != (byte)'A' || bytes[i + 3] != (byte)'M') continue;
            if (BitConverter.ToUInt16(bytes, i + 4) != 4 || BitConverter.ToUInt32(bytes, i + 6) != targetId) continue;
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

/// <summary>#1079 r2 #8: a link null on both sides with its subrecord present is named as what agreed, never "no
/// differing fields" with every count 0.</summary>
[Trait("tier", "integration")]
public sealed class PresentNullLinkAgreementTests : IClassFixture<PresentNullLinkPairWorld>
{
    readonly PresentNullLinkPairWorld W;
    public PresentNullLinkAgreementTests(PresentNullLinkPairWorld w) => W = w;

    static JsonElement Pole(string name) => JsonDocument.Parse("\"" + name + "\"").RootElement.Clone();
    static RecordsTools.RecordsProject Project(string form) => new() { form = form, fields = new[] { "PreviousDialog" } };

    string Records(string form, string? format = null) =>
        RecordsTools.Records(W.Svc, formids: new[] { RecordsWorld.Fid(W.MarkedLine) }, project: Project(form),
                             versus: form == "delta" ? Pole(PresentNullLinkPairWorld.MasterName) : null, format: format);

    [Fact]
    public void TreeAndDelta_TextNameTheLinkNullOnBoth()
    {
        var tree = Records("tree");
        Assert.Contains("across the fields read (1 link null on both, subrecord present)", tree);
        Assert.DoesNotContain("no differing fields", tree);
        Assert.Contains("identical across the fields read (1 link null on both, subrecord present).", Records("delta"));
    }

    [Fact]
    public void TreeAndDelta_JsonCountTheLinkNullOnBoth()
    {
        var node = JsonDocument.Parse(Records("tree", "json")).RootElement.GetProperty("rows")[0].GetProperty("nodes")
                   .EnumerateArray().Single(n => !n.GetProperty("is_reference").GetBoolean());
        Assert.Equal(1, node.GetProperty("present_null_link_count").GetInt32());
        Assert.Equal(0, node.GetProperty("both_absent_count").GetInt32());
        Assert.Equal(0, node.GetProperty("agreed_count").GetInt32());
        var row = JsonDocument.Parse(Records("delta", "json")).RootElement.GetProperty("rows")[0];
        Assert.Equal(1, row.GetProperty("present_null_link_count").GetInt32());
        Assert.True(row.GetProperty("complete").GetBoolean());
    }

    // Pins unchanged behaviour: the delta census counts the row identical.
    [Fact]
    public void DeltaCensus_StillCountsItIdentical()
    {
        var r = RecordsTools.Records(W.Svc, formids: new[] { RecordsWorld.Fid(W.MarkedLine) }, project: Project("delta"),
                                     versus: Pole(PresentNullLinkPairWorld.MasterName), counts_only: true);
        Assert.Contains("identical=1", r);
    }
}

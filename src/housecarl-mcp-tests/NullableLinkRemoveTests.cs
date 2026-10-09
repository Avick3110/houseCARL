using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The PNAM subrecord of one INFO in a written plugin, read off the raw bytes: null when absent, else its 4-byte payload.</summary>
static class InfoPnamBytes
{
    public static uint? Of(string path, uint infoId)
    {
        var b = File.ReadAllBytes(path);
        for (int i = 0; i + 24 <= b.Length; i++)
        {
            if (b[i] != 'I' || b[i + 1] != 'N' || b[i + 2] != 'F' || b[i + 3] != 'O') continue;
            if ((BitConverter.ToUInt32(b, i + 12) & 0x00FFFFFF) != (infoId & 0x00FFFFFF)) continue;
            Assert.Equal(0u, BitConverter.ToUInt32(b, i + 8) & 0x00040000);   // not compressed, so the subrecords are readable
            int end = i + 24 + (int)BitConverter.ToUInt32(b, i + 4);
            for (int p = i + 24; p + 6 <= end; p += 6 + BitConverter.ToUInt16(b, p + 4))
                if (b[p] == 'P' && b[p + 1] == 'N' && b[p + 2] == 'A' && b[p + 3] == 'M')
                    return BitConverter.ToUInt32(b, p + 6);
            return null;
        }
        throw new InvalidOperationException($"INFO {infoId:X6} not found in {path}");
    }
}

/// <summary>#1144: what Mutagen writes for a nullable link, measured on a written file — the ground truth the fix rests on.</summary>
[Trait("tier", "unit")]
public sealed class NullableLinkWriterShapeTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-nullable-link-shape-" + Guid.NewGuid().ToString("N"));
    public NullableLinkWriterShapeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp cleanup best-effort */ } }

    uint? WrittenPnam(Action<DialogResponses> shape)
    {
        var mod = new SkyrimMod(ModKey.FromNameAndExtension("HcShape.esp"), SkyrimRelease.SkyrimSE);
        var topic = mod.DialogTopics.AddNew();
        var info = new DialogResponses(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE);
        info.PreviousDialog.SetTo(topic.FormKey);
        shape(info);
        topic.Responses.Add(info);
        var path = Path.Combine(_dir, "HcShape.esp");
        mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        return InfoPnamBytes.Of(path, info.FormKey.ID);
    }

    [Fact]
    public void ANullFormKeyNullableWritesNoSubrecord() =>
        Assert.Null(WrittenPnam(i => i.PreviousDialog.SetTo(new FormLinkNullable<IDialogResponsesGetter>((FormKey?)null))));

    [Fact]
    public void AFormKeyNullLinkWritesAPresentZero() =>
        Assert.Equal(0u, WrittenPnam(i => i.PreviousDialog.SetTo(new FormLinkNullable<IDialogResponsesGetter>(FormKey.Null))));
}

/// <summary>#1144: Remove on a nullable link leaves the subrecord out; <c>Set "0"</c> writes the present zero. Driven at the verb engine.</summary>
[Trait("tier", "unit")]
public sealed class NullableLinkRemoveVerbTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-nullable-link-remove-" + Guid.NewGuid().ToString("N"));
    public NullableLinkRemoveVerbTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp cleanup best-effort */ } }

    static WriteRequest Req(string verb, string? value = null) =>
        new() { RecordType = "DialogResponses", Path = new[] { "PreviousDialog" }, Verb = verb, Value = value };

    uint? PnamAfter(WriteRequest req)
    {
        var mod = new SkyrimMod(ModKey.FromNameAndExtension("HcRemove.esp"), SkyrimRelease.SkyrimSE);
        var topic = mod.DialogTopics.AddNew();
        var info = new DialogResponses(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE);
        info.PreviousDialog.SetTo(topic.FormKey);
        topic.Responses.Add(info);
        WriteEngine.ApplyVerb(info, req);
        var path = Path.Combine(_dir, "HcRemove.esp");
        WriteEngine.WritePatch(mod, new ISkyrimModGetter[] { mod }, path);
        return InfoPnamBytes.Of(path, info.FormKey.ID);
    }

    [Fact]
    public void RemoveLeavesTheSubrecordOut() => Assert.Null(PnamAfter(Req("Remove")));

    [Fact]
    public void SetZeroWritesAPresentZero() => Assert.Equal(0u, PnamAfter(Req("Set", "0")));
}

/// <summary>#1144, the copy lane: a copied nullable link keeps the source's shape, absent as absent and a present zero as a present zero.</summary>
[Trait("tier", "unit")]
public sealed class NullableLinkCopyTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-nullable-link-copy-" + Guid.NewGuid().ToString("N"));
    public NullableLinkCopyTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp cleanup best-effort */ } }

    uint? CopiedPnam(bool sourceZero)
    {
        var src = new SkyrimMod(ModKey.FromNameAndExtension("HcCopySrc.esp"), SkyrimRelease.SkyrimSE);
        var srcInfo = new DialogResponses(src.GetNextFormKey(), SkyrimRelease.SkyrimSE);
        if (sourceZero) srcInfo.PreviousDialog.SetTo(new FormLinkNullable<IDialogResponsesGetter>(FormKey.Null));
        src.DialogTopics.AddNew().Responses.Add(srcInfo);
        var srcPath = Path.Combine(_dir, "HcCopySrc.esp");
        src.BeginWrite.ToPath(srcPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        Assert.Equal(sourceZero ? 0u : null, InfoPnamBytes.Of(srcPath, srcInfo.FormKey.ID));

        using var overlay = SkyrimMod.CreateFromBinaryOverlay(srcPath, SkyrimRelease.SkyrimSE);
        var mod = new SkyrimMod(ModKey.FromNameAndExtension("HcCopyTgt.esp"), SkyrimRelease.SkyrimSE);
        var topic = mod.DialogTopics.AddNew();
        var info = new DialogResponses(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE);
        info.PreviousDialog.SetTo(topic.FormKey);
        topic.Responses.Add(info);
        WriteEngine.CopyField(overlay.DialogTopics.First().Responses.First(), info, new[] { "PreviousDialog" });
        var path = Path.Combine(_dir, "HcCopyTgt.esp");
        WriteEngine.WritePatch(mod, new ISkyrimModGetter[] { mod }, path);
        return InfoPnamBytes.Of(path, info.FormKey.ID);
    }

    [Fact]
    public void AnAbsentSourceLinkCopiesAsAbsent() => Assert.Null(CopiedPnam(sourceZero: false));

    [Fact]
    public void APresentZeroSourceLinkCopiesAsAPresentZero() => Assert.Equal(0u, CopiedPnam(sourceZero: true));
}

/// <summary>#1144 end to end: one patch Removes one line's PNAM and Sets another's to "0"; the echo reads each shape back
/// from the file, and the merged order puts the Removed line last and the zeroed line first.</summary>
[Trait("tier", "integration")]
public sealed class NullableLinkRemoveApplyTests : IDisposable
{
    const string Master = "HcNlrMaster.esp";
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-nullable-link-apply-" + Guid.NewGuid().ToString("N"));
    readonly LoadOrderService _svc;
    readonly FormKey _topic;
    readonly FormKey[] _line = new FormKey[3];

    public NullableLinkRemoveApplyTests()
    {
        var instance = Path.Combine(_root, "inst");
        var modDir = Path.Combine(instance, "mods", "NlrMod");
        Directory.CreateDirectory(modDir);
        Directory.CreateDirectory(Path.Combine(_root, "game", "Data"));

        var mod = new SkyrimMod(ModKey.FromNameAndExtension(Master), SkyrimRelease.SkyrimSE);
        var topic = mod.DialogTopics.AddNew(); topic.EditorID = "HcNlrTopic";
        _topic = topic.FormKey;
        for (int i = 0; i < 3; i++)
        {
            var r = new DialogResponses(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = $"HcNlrLine{i}" };
            if (i > 0) r.PreviousDialog.SetTo(_line[i - 1]);
            topic.Responses.Add(r);
            _line[i] = r.FormKey;
        }
        mod.BeginWrite.ToPath(Path.Combine(modDir, Master)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + Master + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + Master + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+NlrMod\r\n");
        _svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "user.json")));
    }

    public void Dispose()
    {
        _svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }

    static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

    string ApplyBoth() => ApplyTools.Apply(_svc, patch: "NlrPatch", ops: JsonDocument.Parse(
        "[{\"formid\":\"" + Fid(_line[1]) + "\",\"field_path\":\"PreviousDialog\",\"op\":\"Remove\"}," +
        " {\"formid\":\"" + Fid(_line[2]) + "\",\"field_path\":\"PreviousDialog\",\"op\":\"Set\",\"value\":\"0\"}]").RootElement.Clone());

    string PatchPath => Path.Combine(_root, "inst", "mods", "houseCARL - NlrPatch", "NlrPatch.esp");

    [Fact]
    public void TheFileCarriesNoPnamForTheRemoveAndAZeroForTheSet()
    {
        var r = ApplyBoth();
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);

        Assert.Null(InfoPnamBytes.Of(PatchPath, _line[1].ID));
        Assert.Equal(0u, InfoPnamBytes.Of(PatchPath, _line[2].ID));
    }

    [Fact]
    public void TheEchoReadsEachShapeBackFromTheFile()
    {
        var r = ApplyBoth();

        var removed = r.Split('\n').Where(l => l.Contains("PreviousDialog") && l.Contains(Fid(_line[1]))).ToList();
        var zeroed = r.Split('\n').Where(l => l.Contains("PreviousDialog") && l.Contains(Fid(_line[2]))).ToList();
        Assert.True(removed.Count > 0 && zeroed.Count > 0, r);
        Assert.Contains(removed, l => l.TrimEnd().EndsWith("-> " + ReadEngine.NullLinkNote, StringComparison.Ordinal));
        Assert.Contains(zeroed, l => l.TrimEnd().EndsWith("-> " + ReadEngine.PresentNullLinkNote, StringComparison.Ordinal));
    }

    [Fact]
    public void InfoOrderPutsTheRemovedLineLastAndTheZeroedLineFirst()
    {
        Assert.False(ApplyBoth().StartsWith("error:", StringComparison.Ordinal));

        // The patch is not enabled, so the merge folds it in at the end of the order, as MO2 would place it.
        var order = RecordsTools.Records(_svc, formids: new[] { Fid(_topic) },
                                         project: new RecordsTools.RecordsProject { form = "info_order" },
                                         source: JsonDocument.Parse("\"NlrPatch.esp\"").RootElement.Clone());

        Assert.Contains($"#1  {Fid(_line[2])}", order);
        Assert.Contains($"#3  {Fid(_line[1])}", order);
    }
}

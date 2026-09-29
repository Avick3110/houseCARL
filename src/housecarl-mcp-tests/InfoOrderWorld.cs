using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlMcpTests;

/// <summary>The merged INFO order's on-disk fixture, moved from the retired <c>dialogue-info-order-guard</c> probe:
/// master &lt; mid &lt; last plus a byte-patched zero-PNAM plugin, one topic per merge shape. Every topic's order is
/// read once through the real <c>DialogueValidate.InfoOrders</c>; the tests read the views.</summary>
internal static class InfoOrderWorld
{
    internal const string MasterName = "hcInfoMaster.esp", MidName = "hcInfoMid.esp", LastName = "hcInfoLast.esp";

    static readonly string Dir = Path.Combine(Path.GetTempPath(), "hc-info-order-" + Guid.NewGuid().ToString("N"));
    static readonly Lazy<Fixture> Built = new(Build);

    internal static Fixture World => Built.Value;

    /// <summary>The views the tests assert on, and the FormKeys they look lines up by.</summary>
    internal sealed class Fixture
    {
        public InfoOrderView? Solo, Order, Head, Cycle, Deleted, Zero, ZeroWriter, FanA, FanB, Foreign, Self;
        public FormKey[] SoloLines = Array.Empty<FormKey>(), OrderLines = Array.Empty<FormKey>();
        public FormKey HeadSecond, DeletedLine, ZeroMarkedLine, WriterNulledLine, FanA0, FanB0, ForeignLine, SelfLine;
        public int ZeroPatched;
    }

    static Fixture Build()
    {
        Directory.CreateDirectory(Dir);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(Dir, true); } catch { /* best-effort */ } };

        var f = new Fixture();
        var mPath = Path.Combine(Dir, MasterName);
        var midPath = Path.Combine(Dir, MidName);
        var lastPath = Path.Combine(Dir, LastName);

        var master = new SkyrimMod(ModKey.FromNameAndExtension(MasterName), SkyrimRelease.SkyrimSE);
        DialogResponses NewInfo(string edid) => new(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = edid };

        // tOrder: 8 plain INFOs, no PNAM, so the file order IS the order.
        var tOrder = master.DialogTopics.AddNew(); tOrder.EditorID = "HcIoOrder";
        var info = new FormKey[8];
        for (int i = 0; i < 8; i++) { var r = NewInfo($"HcIoLine{i}"); info[i] = r.FormKey; tOrder.Responses.Add(r); }
        f.OrderLines = info;

        // tSolo: touched by one plugin only.
        var tSolo = master.DialogTopics.AddNew(); tSolo.EditorID = "HcIoSolo";
        var solo = new FormKey[3];
        for (int i = 0; i < 3; i++) { var r = NewInfo($"HcIoSolo{i}"); solo[i] = r.FormKey; tSolo.Responses.Add(r); }
        f.SoloLines = solo;

        // tHead: the second INFO's PNAM names a record nothing defines.
        var tHead = master.DialogTopics.AddNew(); tHead.EditorID = "HcIoHead";
        var h0 = NewInfo("HcIoHead0");
        var h1 = NewInfo("HcIoHead1");
        h1.PreviousDialog.SetTo(FormKey.Factory("ABCDEF:hcInfoMaster.esp"));
        tHead.Responses.Add(h0); tHead.Responses.Add(h1);
        f.HeadSecond = h1.FormKey;

        // tCycle: two INFOs whose PNAMs name each other.
        var tCycle = master.DialogTopics.AddNew(); tCycle.EditorID = "HcIoCycle";
        var c0 = NewInfo("HcIoCycle0");
        var c1 = NewInfo("HcIoCycle1");
        c0.PreviousDialog.SetTo(c1.FormKey);
        c1.PreviousDialog.SetTo(c0.FormKey);
        tCycle.Responses.Add(c0); tCycle.Responses.Add(c1);

        // tDeleted: the middle INFO is deleted.
        var tDeleted = master.DialogTopics.AddNew(); tDeleted.EditorID = "HcIoDeleted";
        var d0 = NewInfo("HcIoDel0");
        var d1 = NewInfo("HcIoDel1"); d1.IsDeleted = true;
        var d2 = NewInfo("HcIoDel2");
        tDeleted.Responses.Add(d0); tDeleted.Responses.Add(d1); tDeleted.Responses.Add(d2);
        f.DeletedLine = d1.FormKey;

        // One quest owning two topics that different plugin subsets touch, read in one batch.
        var qFan = master.Quests.AddNew(); qFan.EditorID = "HcIoFanQuest";
        var tFanA = master.DialogTopics.AddNew(); tFanA.EditorID = "HcIoFanA"; tFanA.Quest.SetTo(qFan.FormKey);
        var fa0 = NewInfo("HcIoFanA0"); var fa1 = NewInfo("HcIoFanA1");
        tFanA.Responses.Add(fa0); tFanA.Responses.Add(fa1);
        var tFanB = master.DialogTopics.AddNew(); tFanB.EditorID = "HcIoFanB"; tFanB.Quest.SetTo(qFan.FormKey);
        var fb0 = NewInfo("HcIoFanB0"); var fb1 = NewInfo("HcIoFanB1");
        tFanB.Responses.Add(fb0); tFanB.Responses.Add(fb1);
        f.FanA0 = fa0.FormKey; f.FanB0 = fb0.FormKey;

        // tForeign: an anchor line; the last plugin adds a line whose PNAM names fan-out topic A's first line.
        var tForeign = master.DialogTopics.AddNew(); tForeign.EditorID = "HcIoForeign";
        tForeign.Responses.Add(NewInfo("HcIoForeign0"));

        // tSelf: the first INFO's PNAM names its own record.
        var tSelf = master.DialogTopics.AddNew(); tSelf.EditorID = "HcIoSelf";
        var s0 = NewInfo("HcIoSelf0");
        s0.PreviousDialog.SetTo(s0.FormKey);
        var s1 = NewInfo("HcIoSelf1");
        tSelf.Responses.Add(s0); tSelf.Responses.Add(s1);
        f.SelfLine = s0.FormKey;

        // A present-but-zero PNAM, in its own plugin: written with a real link, then its data bytes zeroed on disk.
        const string zeroName = "hcInfoZero.esp";
        var zeroPath = Path.Combine(Dir, zeroName);
        var zeroMod = new SkyrimMod(ModKey.FromNameAndExtension(zeroName), SkyrimRelease.SkyrimSE);
        var tZeroReal = zeroMod.DialogTopics.AddNew(); tZeroReal.EditorID = "HcIoZeroReal";
        var zr0 = new DialogResponses(zeroMod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcIoZeroR0" };
        var zr1 = new DialogResponses(zeroMod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcIoZeroR1" };
        zr1.PreviousDialog.SetTo(zr0.FormKey);
        tZeroReal.Responses.Add(zr0); tZeroReal.Responses.Add(zr1);
        zeroMod.BeginWrite.ToPath(zeroPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        f.ZeroPatched = ZeroEveryPnam(zeroPath);
        f.ZeroMarkedLine = zr1.FormKey;

        // The writer asked for an explicitly-null PNAM emits no subrecord, so this line reads back as PNAM-absent.
        var tZeroWriter = master.DialogTopics.AddNew(); tZeroWriter.EditorID = "HcIoZeroWriter";
        var zw0 = NewInfo("HcIoZeroW0");
        var zw1 = NewInfo("HcIoZeroW1"); zw1.PreviousDialog.SetToNull();
        tZeroWriter.Responses.Add(zw0); tZeroWriter.Responses.Add(zw1);
        f.WriterNulledLine = zw1.FormKey;

        master.BeginWrite.ToPath(mPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        // Mid re-lists INFOs 2..7 of tOrder in REVERSE, each with its PNAM: plain tail-appending would rotate them.
        var mid = new SkyrimMod(ModKey.FromNameAndExtension(MidName), SkyrimRelease.SkyrimSE);
        var midT = (IDialogTopic)WriteEngine.GenericGetOrAddAsOverride(mid, tOrder);
        midT.Responses.Clear();
        for (int i = 7; i >= 2; i--)
        {
            var r = new DialogResponses(info[i], SkyrimRelease.SkyrimSE) { EditorID = $"HcIoLine{i}" };
            r.PreviousDialog.SetTo(info[i - 1]);
            midT.Responses.Add(r);
        }
        // Mid adds one line to fan-out topic A and leaves topic B alone.
        var midFanA = (IDialogTopic)WriteEngine.GenericGetOrAddAsOverride(mid, tFanA);
        midFanA.Responses.Clear();
        midFanA.Responses.Add(new DialogResponses(mid.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcIoFanAMid" });
        mid.BeginWrite.ToPath(midPath).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

        // Last re-lists only INFO 0 of tOrder, with no PNAM.
        var last = new SkyrimMod(ModKey.FromNameAndExtension(LastName), SkyrimRelease.SkyrimSE);
        var lastT = (IDialogTopic)WriteEngine.GenericGetOrAddAsOverride(last, tOrder);
        lastT.Responses.Clear();
        lastT.Responses.Add(new DialogResponses(info[0], SkyrimRelease.SkyrimSE) { EditorID = "HcIoLine0" });
        // Last adds a line to tForeign whose PNAM names a master line of ANOTHER topic.
        var lastForeign = (IDialogTopic)WriteEngine.GenericGetOrAddAsOverride(last, tForeign);
        lastForeign.Responses.Clear();
        var lf = new DialogResponses(last.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcIoForeignLast" };
        lf.PreviousDialog.SetTo(fa0.FormKey);
        lastForeign.Responses.Add(lf);
        f.ForeignLine = lf.FormKey;
        last.BeginWrite.ToPath(lastPath).WithLoadOrder(new ISkyrimModGetter[] { master, mid }).Write();

        using var resolver = LoadOrderResolver.Build(new[] { mPath, midPath, lastPath, zeroPath });
        Dictionary<FormKey, InfoOrderView> Orders(params FormKey[] topics)
        {
            using var session = resolver.OpenSession();
            return DialogueValidate.InfoOrders(resolver.Capture(), session, topics);
        }
        InfoOrderView? One(FormKey topic) => Orders(topic).GetValueOrDefault(topic);

        f.Solo = One(tSolo.FormKey);
        f.Order = One(tOrder.FormKey);
        f.Head = One(tHead.FormKey);
        f.Cycle = One(tCycle.FormKey);
        f.Deleted = One(tDeleted.FormKey);
        f.Zero = One(tZeroReal.FormKey);
        f.ZeroWriter = One(tZeroWriter.FormKey);
        f.Foreign = One(tForeign.FormKey);
        f.Self = One(tSelf.FormKey);
        var fan = Orders(tFanA.FormKey, tFanB.FormKey);
        f.FanA = fan.GetValueOrDefault(tFanA.FormKey);
        f.FanB = fan.GetValueOrDefault(tFanB.FormKey);
        return f;
    }

    /// <summary>Zero the 4 data bytes of every 4-byte PNAM subrecord in a plugin, in place; returns how many.</summary>
    static int ZeroEveryPnam(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int patched = 0;
        for (int i = 0; i + 10 <= bytes.Length; i++)
        {
            if (bytes[i] != (byte)'P' || bytes[i + 1] != (byte)'N' || bytes[i + 2] != (byte)'A' || bytes[i + 3] != (byte)'M')
                continue;
            if (BitConverter.ToUInt16(bytes, i + 4) != 4) continue;
            bytes[i + 6] = bytes[i + 7] = bytes[i + 8] = bytes[i + 9] = 0;
            patched++;
            i += 9;
        }
        File.WriteAllBytes(path, bytes);
        return patched;
    }
}

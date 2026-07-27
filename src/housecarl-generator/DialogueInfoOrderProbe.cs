using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD (standing CI instrument) for #275 — the EFFECTIVE, MERGED INFO order of a dialogue topic
/// (xEdit INOM/INOA parity). A topic's lines are ORDERED and the game plays the FIRST whose conditions pass, so a
/// pure reorder changes behaviour with NO field delta anywhere. Before this, houseCARL had no merged-order view at
/// all, and shipped the opposite model — "a line the winning topic does not re-list is dropped in game" — which the
/// live evidence falsifies (a topic whose winner lists ONE INFO plays EIGHT).
///
/// Self-contained: synthesizes ON DISK a 3-plugin order (master &lt; mid &lt; last) reproducing the reported
/// HirelingQuestTopic1 shape, then drives the REAL product path — <see cref="DialogueValidate.Run"/> →
/// <see cref="DialogueInfoOrder.Compute"/> → <see cref="DialogueWire.Render"/> — and asserts:
///
///   MERGE-FILE-ORDER — a topic only ONE plugin touches: the effective order IS that plugin's list, Contested=false.
///   NO-FALSE-MOVE    — …and it reports ZERO moved lines (the no-false-positive lock for REORDER-TO-TAIL: a guard
///                      that only ever asserts "moved" would pass on a merge that marked everything moved).
///   REORDER-TO-TAIL  — the reported bug shape: the LAST plugin re-lists ONE INFO with no PNAM → that line is evicted
///                      from #1 and appended LAST, and is reported MOVED #1 -> #8 crediting the plugin that moved it.
///                      (RED before this work: no order view existed, and the field diff called the winner identical.)
///   PNAM-CHAIN-NOOP  — the mid plugin re-lists SIX INFOs carrying a PNAM chain in their original relative order →
///                      net order UNCHANGED. Teeth: proves the after-target arm actually places by PNAM. Were PNAM
///                      ignored (everything tail-appended), those six would rotate to the end and this fails.
///   PNAM-HEAD        — an INFO whose PNAM names a record no plugin defines is placed at the HEAD, not silently
///                      tail-appended — the arm that keeps an unresolvable link from reading as "no link".
///   PNAM-CYCLE       — two INFOs whose PNAMs point at each other TERMINATE and both appear (a cycle degrades to a
///                      placement, never a hang or a dropped line).
///   DELETED-KEPT     — a deleted INFO still occupies its slot in the order (it is shown flagged, never silently
///                      dropped — the index of every line after it depends on it being counted).
///   PNAM-ZERO-AXIS   — THE FIDELITY PIN. "PNAM absent" and "PNAM present-but-zero" place at OPPOSITE ENDS (tail vs
///                      head), so the merge is only as faithful as Mutagen's ability to tell them apart. This arm
///                      MEASURES that round-trip against a real write→read and asserts it agrees with
///                      <see cref="DialogueInfoOrder.PnamZeroIsDistinguishable"/> — so a Mutagen bump that changes
///                      the behaviour fails CI instead of silently degrading the order.
///   RENDER-MODEL-PIN — the rendered report states the CORRECTED model and does NOT contain the falsified claim
///                      ("dropped in game"). Pins the prose against a regression to the pre-#275 wording.
///
/// Run: <c>dotnet run --project src/housecarl-generator -- dialogue-info-order-guard</c>
/// </summary>
public static class DialogueInfoOrderProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — effective merged INFO order (#275)  ################");
        Console.WriteLine();

        var dir = Path.Combine(Path.GetTempPath(), "hc-infoorder-guard");
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        Directory.CreateDirectory(dir);

        const string masterName = "hcInfoMaster.esp", midName = "hcInfoMid.esp", lastName = "hcInfoLast.esp";
        var mPath = Path.Combine(dir, masterName);
        var midPath = Path.Combine(dir, midName);
        var lastPath = Path.Combine(dir, lastName);

        var master = new SkyrimMod(ModKey.FromNameAndExtension(masterName), SkyrimRelease.SkyrimSE);

        DialogResponses NewInfo(string edid)
            => new(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = edid };

        // ---- tOrder: the reported shape — 8 plain INFOs, no PNAM, so the file order IS the order. ----
        var tOrder = master.DialogTopics.AddNew(); tOrder.EditorID = "HcIoOrder";
        var info = new FormKey[8];
        for (int i = 0; i < 8; i++) { var r = NewInfo($"HcIoLine{i}"); info[i] = r.FormKey; tOrder.Responses.Add(r); }

        // ---- tSolo: touched by ONE plugin — the uncontested baseline + the no-false-move lock. ----
        var tSolo = master.DialogTopics.AddNew(); tSolo.EditorID = "HcIoSolo";
        for (int i = 0; i < 3; i++) tSolo.Responses.Add(NewInfo($"HcIoSolo{i}"));

        // ---- tHead: an INFO whose PNAM names a record nothing defines → the HEAD arm. ----
        var tHead = master.DialogTopics.AddNew(); tHead.EditorID = "HcIoHead";
        var h0 = NewInfo("HcIoHead0");
        var h1 = NewInfo("HcIoHead1");
        h1.PreviousDialog.SetTo(FormKey.Factory("ABCDEF:hcInfoMaster.esp"));      // nothing defines this
        tHead.Responses.Add(h0); tHead.Responses.Add(h1);
        var headSecond = h1.FormKey;

        // ---- tCycle: mutually-referencing PNAMs → must terminate with both lines placed. ----
        var tCycle = master.DialogTopics.AddNew(); tCycle.EditorID = "HcIoCycle";
        var c0 = NewInfo("HcIoCycle0");
        var c1 = NewInfo("HcIoCycle1");
        c0.PreviousDialog.SetTo(c1.FormKey);
        c1.PreviousDialog.SetTo(c0.FormKey);
        tCycle.Responses.Add(c0); tCycle.Responses.Add(c1);

        // ---- tDeleted: a deleted line still holds its slot. ----
        var tDeleted = master.DialogTopics.AddNew(); tDeleted.EditorID = "HcIoDeleted";
        var d0 = NewInfo("HcIoDel0");
        var d1 = NewInfo("HcIoDel1"); d1.IsDeleted = true;
        var d2 = NewInfo("HcIoDel2");
        tDeleted.Responses.Add(d0); tDeleted.Responses.Add(d1); tDeleted.Responses.Add(d2);
        var deletedFk = d1.FormKey;

        // ---- tZero: line 2 carries an explicitly-ZEROED PNAM (the "I am first" marker) while line 1 carries
        //      none at all. If Mutagen preserves the distinction, the zeroed one is placed HEAD and leads. ----
        var tZero = master.DialogTopics.AddNew(); tZero.EditorID = "HcIoZero";
        var z0 = NewInfo("HcIoZero0");
        var z1 = NewInfo("HcIoZero1"); z1.PreviousDialog.SetToNull();
        tZero.Responses.Add(z0); tZero.Responses.Add(z1);
        var zeroMarked = z1.FormKey;

        master.BeginWrite.ToPath(mPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        // ---- MID: re-lists INFOs 2..7 of tOrder, each carrying its PNAM — but lists them in REVERSE. A
        //      well-behaved patch: it touches six lines and moves none, BECAUSE each PNAM puts its line back
        //      after its predecessor. Reversed deliberately: listing them in their original relative order would
        //      let plain tail-appending reproduce the same result by accident, and the arm would pass even with
        //      PNAM ignored entirely (measured — it did, before this fixture was reversed). ----
        var mid = new SkyrimMod(ModKey.FromNameAndExtension(midName), SkyrimRelease.SkyrimSE);
        var midT = (IDialogTopic)WriteEngine.GenericGetOrAddAsOverride(mid, tOrder);
        midT.Responses.Clear();
        for (int i = 7; i >= 2; i--)
        {
            var r = new DialogResponses(info[i], SkyrimRelease.SkyrimSE) { EditorID = $"HcIoLine{i}" };
            r.PreviousDialog.SetTo(info[i - 1]);
            midT.Responses.Add(r);
        }
        mid.BeginWrite.ToPath(midPath).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

        // ---- LAST: re-lists ONLY INFO 0, with NO PNAM. The whole bug in one edit — it is evicted from the top
        //      and appended to the BOTTOM, so a broader line now answers first. ----
        var last = new SkyrimMod(ModKey.FromNameAndExtension(lastName), SkyrimRelease.SkyrimSE);
        var lastT = (IDialogTopic)WriteEngine.GenericGetOrAddAsOverride(last, tOrder);
        lastT.Responses.Clear();
        lastT.Responses.Add(new DialogResponses(info[0], SkyrimRelease.SkyrimSE) { EditorID = "HcIoLine0" });
        last.BeginWrite.ToPath(lastPath).WithLoadOrder(new ISkyrimModGetter[] { master, mid }).Write();

        var dataDir = Path.Combine(dir, "data"); Directory.CreateDirectory(dataDir);
        using var resolver = LoadOrderResolver.Build(new[] { mPath, midPath, lastPath });
        using var assets = AssetResolver.Build("", "", dataDir, Array.Empty<string>(), Array.Empty<ActiveArchive>());
        Console.WriteLine($"-- synthesized {masterName} < {midName} < {lastName}; tOrder=8 lines, mid re-lists 6 (PNAM-chained), last re-lists 1 (no PNAM) --");
        Console.WriteLine();

        bool all = true;

        InfoOrderView? Order(FormKey topic)
        {
            var rep = DialogueValidate.Run(resolver, assets, topic);
            return rep.Topics.Count == 1 ? rep.Topics[0].InfoOrder : null;
        }

        // ---------- MERGE-FILE-ORDER / NO-FALSE-MOVE ----------
        {
            var io = Order(tSolo.FormKey);
            bool ok = io is { Contested: false } && io.Order.Count == 3
                      && io.Order.Select(e => e.Index).SequenceEqual(new[] { 0, 1, 2 });
            all &= Pass("MERGE-FILE-ORDER", ok, io is null ? "no order view" : $"contested={io.Contested} n={io.Order.Count}");

            bool noMove = io is not null && io.Moved.Count == 0;
            all &= Pass("NO-FALSE-MOVE", noMove, io is null ? "no order view" : $"moved={io.Moved.Count}");
        }

        // ---------- REORDER-TO-TAIL + PNAM-CHAIN-NOOP ----------
        {
            var io = Order(tOrder.FormKey);
            var expected = new[] { info[1], info[2], info[3], info[4], info[5], info[6], info[7], info[0] };
            bool seq = io is not null && io.Order.Select(e => e.Info).SequenceEqual(expected);
            all &= Pass("PNAM-CHAIN-NOOP", seq, io is null ? "no order view"
                : "order=" + string.Join(",", io.Order.Select(e => e.Info.ID.ToString("X6"))));

            var moved = io?.Moved ?? Array.Empty<InfoOrderEntry>();
            var m0 = moved.FirstOrDefault(e => e.Info == info[0]);
            bool ok = io is { Contested: true } && moved.Count == 1
                      && m0 is { OriginIndex: 0, Index: 7 }
                      && m0.PlacedBy.Equals(lastName, StringComparison.OrdinalIgnoreCase);
            all &= Pass("REORDER-TO-TAIL", ok, m0 is null
                ? $"line0 not reported moved (moved={moved.Count})"
                : $"#{m0.OriginIndex + 1} -> #{m0.Index + 1} by {m0.PlacedBy}; movedCount={moved.Count}");
        }

        // ---------- PNAM-HEAD ----------
        {
            var io = Order(tHead.FormKey);
            var e = io?.Order.FirstOrDefault(x => x.Info == headSecond);
            bool ok = e is { Index: 0, Placement: InfoPlacement.Head };
            all &= Pass("PNAM-HEAD", ok, e is null ? "line not in order" : $"index={e.Index} placement={e.Placement}");
        }

        // ---------- PNAM-CYCLE ----------
        {
            var io = Order(tCycle.FormKey);
            bool ok = io is not null && io.Order.Count == 2 && io.Order.Select(x => x.Info).Distinct().Count() == 2;
            all &= Pass("PNAM-CYCLE", ok, io is null ? "no order view" : $"n={io.Order.Count} (terminated)");
        }

        // ---------- DELETED-KEPT ----------
        {
            var io = Order(tDeleted.FormKey);
            var e = io?.Order.FirstOrDefault(x => x.Info == deletedFk);
            bool ok = io is not null && io.Order.Count == 3 && e is { Deleted: true, Index: 1 };
            all &= Pass("DELETED-KEPT", ok, e is null ? "deleted line absent from order"
                : $"n={io!.Order.Count} index={e.Index} deleted={e.Deleted}");
        }

        // ---------- PNAM-ZERO-AXIS — measure the round-trip, assert the product flag matches ----------
        {
            var io = Order(tZero.FormKey);
            var e = io?.Order.FirstOrDefault(x => x.Info == zeroMarked);
            bool measured = e is { Placement: InfoPlacement.Head };      // head ⇒ the zero survived the round-trip
            bool ok = e is not null && measured == DialogueInfoOrder.PnamZeroIsDistinguishable;
            all &= Pass("PNAM-ZERO-AXIS", ok, e is null ? "marked line absent from order"
                : $"measured distinguishable={measured} (placement={e.Placement}), product flag={DialogueInfoOrder.PnamZeroIsDistinguishable}");
        }

        // ---------- RENDER-MODEL-PIN ----------
        {
            string r = DialogueWire.Render(DialogueValidate.Run(resolver, assets, tOrder.FormKey), 0);
            bool statesModel = r.Contains("effective INFO order", StringComparison.Ordinal)
                               && r.Contains("MOVED from #", StringComparison.Ordinal);
            bool dropsFalseClaim = !r.Contains("dropped in game", StringComparison.Ordinal);
            all &= Pass("RENDER-MODEL-PIN", statesModel && dropsFalseClaim,
                $"statesOrder={statesModel} falseClaimGone={dropsFalseClaim}");
        }

        Console.WriteLine();
        Console.WriteLine(all ? "RESULT: PASS — effective INFO order holds." : "RESULT: FAIL");
        return all ? 0 : 1;
    }

    static bool Pass(string label, bool ok, string detail)
    {
        Console.WriteLine($"   {label,-20}: {(ok ? "PASS" : "FAIL")} — {detail}");
        return ok;
    }
}

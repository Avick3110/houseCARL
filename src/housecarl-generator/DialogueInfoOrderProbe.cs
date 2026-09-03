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
///   PNAM-HEAD        — an INFO whose PNAM names a record no plugin defines is placed at the HEAD as
///                      HeadUnresolvable, not silently tail-appended, and not confused with the zero marker.
///   PNAM-CYCLE       — two INFOs whose PNAMs point at each other TERMINATE and both appear (a cycle degrades to a
///                      placement, never a hang or a dropped line).
///   PNAM-SELF        — an INFO whose PNAM names its OWN record degrades to a placement AND says so on the note.
///                      RED before the PR #293 review fix: that shape drove a negative-length list insert, throwing
///                      ArgumentOutOfRangeException out of a path whose contract is that it never throws.
///   RENDER-NO-FALSE-CAVEAT — the footer does NOT carry the retracted PNAM-zero blind-spot caveat. It shipped for
///                      four review rounds describing a limitation that does not exist; this pins the retraction so
///                      the false caveat cannot drift back in.
///   DELETED-KEPT     — a deleted INFO still occupies its slot in the order (it is shown flagged, never silently
///                      dropped — the index of every line after it depends on it being counted).
///   PNAM-ZERO-AXIS   — THE FIDELITY PIN. "PNAM absent" and "PNAM present-but-zero" place at OPPOSITE ENDS (tail vs
///                      head), so the merge is only as faithful as the READER's ability to tell them apart. The
///                      fixture is BYTE-PATCHED to carry a real on-disk zero PNAM, because authoring it through
///                      Mutagen's writer (which emits no subrecord for a null link) measured the WRITER and made
///                      this arm assert a non-existent limitation for four rounds — caught only by running the
///                      shipped build against the real load order. A round-trip test measures the round trip.
///   RENDER-MODEL-PIN — the rendered report states the CORRECTED model and does NOT contain the falsified claim
///                      ("dropped in game"). Pins the prose against a regression to the pre-#275 wording.
///   UNREAD-PARTIAL / UNREAD-TOTAL / UNREAD-RENDER / UNREAD-BASELINE — the silent-contributor-drop fix, which
///                      shipped with ZERO arms in its own commit (PR #293 third pass). A plugin the index says
///                      touches the topic but whose child list could not be read must never be absorbed: the view
///                      reports it (Complete/UnreadContributors), the render says INCOMPLETE instead of the false
///                      "single plugin, nothing merges here", the TOTAL case (nothing read — the COMMON shape,
///                      since most topics have one contributor) renders loudly instead of silently, and move
///                      analysis is SKIPPED when the DEFINING plugin is the unread one, since the baseline would
///                      otherwise silently become a later plugin's list. UNREAD-BASELINE carries a control arm —
///                      asserting "no moves" on input where nothing moves anyway passed with the guard deleted.
///   UNREAD-WIRED     — the same fix END TO END through DialogueValidate.Run, with a plugin made genuinely
///                      unreadable by an exclusive lock (what MO2/xEdit holding a file does). The four arms above
///                      call Compute directly, which is precisely why they all passed while the production wiring
///                      was absent (PR #293 fourth pass): a guard that never drives the shipped path cannot catch
///                      an unwired one. Drop the argument at the call site and THIS arm goes red.
///   DEFINER-LOCK-LOUD / WINNER-LOCK-LOUD — the REACHABILITY facts the defensive branches rest on, measured
///                      rather than assumed. A plugin can only override a record by declaring the definer as a
///                      master, so an unreadable definer breaks opening its overrides too; and the winner body is
///                      fetched through GetRecord, which throws. Both therefore surface as a named CheckError
///                      BEFORE any order code runs — which is why the total-drop and shifted-baseline states are
///                      defence in depth, not live silent paths, and why their logic is covered synthetically.
///                      If a future change makes either fetch swallow its failure, the silent path opens and
///                      these go red instead of the report going quiet.
///   CYCLE-PREPLACED  — a PNAM cycle whose members were BOTH already placed by an earlier plugin, so no recursion
///                      occurs. The shape post-hoc CountPnamCycles was written for, and the one the old
///                      placement-time signal could never see; without it CountPnamCycles could return 0 unnoticed.
///   RENDER-BIG-TOPIC — over the row cap with nothing moved must not print an EMPTY list, and a SINGLE-topic
///                      report must ignore the cap (it counts rows, so max_chars could not lift it).
///   RENDER-CLAIM-GATES — aggregate claims stated unconditionally: the header counted plugins successfully READ
///                      while calling them the plugins that TOUCH the topic (a different number from the banner
///                      directly above it), and "none of which changed position" was asserted whenever the moved
///                      set was empty. Gated on BOTH axes — the analysis having RUN and having run over COMPLETE
///                      input; an unread plugin AFTER the definer keeps MovesComputed true while lines are
///                      missing, and this arm's first cut covered only the other axis and stayed green with the
///                      Complete gate deleted.
///   RENDER-HEAD-SPLIT — the HeadFirstMarker/HeadUnresolvable split asserted where a USER meets it. Pinned only
///                      at the enum level, collapsing the two render branches back to one shared string would
///                      restore the whole user-visible defect with the suite green — the fourth-pass lesson
///                      applied to the fifth pass's own fix. Asserted per LINE, so a swap fails too.
///   DEGRADE-CEILINGS — both bounds, previously unpinned: the move-analysis line ceiling (order still exact, moved
///                      set not computed) and the PNAM-chain hop ceiling on a 600-deep FORWARD chain — the
///                      stack-overflow shape, which must degrade and say so rather than take the process down.
///
/// Run: <c>dotnet run --project src/housecarl-generator -- dialogue-info-order-guard</c>
/// </summary>
public static class DialogueInfoOrderProbe
{
    [CiProbe("dialogue-info-order-guard")]
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

        // ---- BATCH FAN-IN fixture: ONE quest owning TWO topics touched by DIFFERENT plugin subsets. Validating
        //      the quest drives OrdersFor with a multi-topic collection — the axis every other arm leaves at 1,
        //      where the cross-topic wanted-filter degenerates to an equality test and a mis-association (or the
        //      plugin-name comparer mismatch) is structurally unobservable (PR #293 re-review). ----
        var qFan = master.Quests.AddNew(); qFan.EditorID = "HcIoFanQuest";
        var tFanA = master.DialogTopics.AddNew(); tFanA.EditorID = "HcIoFanA"; tFanA.Quest.SetTo(qFan.FormKey);
        var fa0 = NewInfo("HcIoFanA0"); var fa1 = NewInfo("HcIoFanA1");
        tFanA.Responses.Add(fa0); tFanA.Responses.Add(fa1);
        var tFanB = master.DialogTopics.AddNew(); tFanB.EditorID = "HcIoFanB"; tFanB.Quest.SetTo(qFan.FormKey);
        var fb0 = NewInfo("HcIoFanB0"); var fb1 = NewInfo("HcIoFanB1");
        tFanB.Responses.Add(fb0); tFanB.Responses.Add(fb1);
        var fanA = tFanA.FormKey; var fanB = tFanB.FormKey;
        var fanA0 = fa0.FormKey; var fanB0 = fb0.FormKey;

        // ---- tForeign: an anchor line here; the LAST plugin then adds a line whose PNAM points at an INFO
        //      belonging to ANOTHER topic. That is the only shape reaching the fallback resolver's SUCCESS path
        //      (every other arm's targets are served from the topic's own lines). The puller lives in a DIFFERENT
        //      plugin from the target on purpose: with both in master, "credited to its own plugin" and "credited
        //      to the puller" give the same answer and the assertion cannot fail (PR #293 third pass). ----
        var tForeign = master.DialogTopics.AddNew(); tForeign.EditorID = "HcIoForeign";
        tForeign.Responses.Add(NewInfo("HcIoForeign0"));

        // ---- tSelf: an INFO whose PNAM names its OWN record. Malformed, and before the explicit guard it drove
        //      Place() into a negative-length insert — an uncatchable-shaped crash out of a "never throws" path
        //      (PR #293 review). It is the FIRST line so it is placed into an empty list, the exact trace. ----
        var tSelf = master.DialogTopics.AddNew(); tSelf.EditorID = "HcIoSelf";
        var s0 = NewInfo("HcIoSelf0");
        s0.PreviousDialog.SetTo(s0.FormKey);
        var s1 = NewInfo("HcIoSelf1");
        tSelf.Responses.Add(s0); tSelf.Responses.Add(s1);

        // ---- ZERO-PNAM fixture, in its OWN plugin so it can be byte-patched safely. Mutagen's WRITER drops a
        //      PreviousDialog set to null (no subrecord emitted), so authoring the fixture through the writer
        //      measures the writer, not the reader — the mistake that made the first PNAM-ZERO-AXIS arm assert a
        //      non-existent limitation for four review rounds. Real plugins DO carry a present-but-zero PNAM (the
        //      "I am first" marker xEdit's PNAM fill writes), so the fixture is authored with a REAL PNAM and the
        //      four data bytes are then zeroed ON DISK, giving exactly the shape a shipped plugin has.
        //      Isolated in its own file because DIAL's own PNAM subrecord is the topic Priority float — patching
        //      every PNAM in a shared master would also rewrite unrelated records' priorities.
        const string zeroName = "hcInfoZero.esp";
        var zeroPath = Path.Combine(dir, zeroName);
        var zeroMod = new SkyrimMod(ModKey.FromNameAndExtension(zeroName), SkyrimRelease.SkyrimSE);
        var tZeroReal = zeroMod.DialogTopics.AddNew(); tZeroReal.EditorID = "HcIoZeroReal";
        var zr0 = new DialogResponses(zeroMod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcIoZeroR0" };
        var zr1 = new DialogResponses(zeroMod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcIoZeroR1" };
        zr1.PreviousDialog.SetTo(zr0.FormKey);          // a REAL link, so the subrecord is actually written
        tZeroReal.Responses.Add(zr0); tZeroReal.Responses.Add(zr1);
        zeroMod.BeginWrite.ToPath(zeroPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        int zeroed = ZeroEveryPnam(zeroPath);
        var zeroTopic = tZeroReal.FormKey;
        var zeroMarkedReal = zr1.FormKey;

        // ---- WRITER-SIDE PROVENANCE, kept deliberately: line 2 asks the writer for an explicitly-null PNAM.
        //      Mutagen emits NO subrecord for it, so this topic reads back as two plain lines — which is exactly
        //      how the original PNAM-ZERO-AXIS arm concluded the READER could not see a zero PNAM. Retained
        //      unasserted as the record of that mistake; the real fidelity pin is the byte-patched fixture above.
        var tZeroWriter = master.DialogTopics.AddNew(); tZeroWriter.EditorID = "HcIoZeroWriter";
        var zw0 = NewInfo("HcIoZeroW0");
        var zw1 = NewInfo("HcIoZeroW1"); zw1.PreviousDialog.SetToNull();
        tZeroWriter.Responses.Add(zw0); tZeroWriter.Responses.Add(zw1);
        var writerNulled = zw1.FormKey;

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
        // MID also adds one line to fan-out topic A and does NOT touch topic B — the asymmetry the BATCH-FAN-IN
        // arm reads. If the batched loader cross-wired topics or served only the first, A and B would not differ
        // in contributor count and line count the way they must.
        var midFanA = (IDialogTopic)WriteEngine.GenericGetOrAddAsOverride(mid, tFanA);
        midFanA.Responses.Clear();
        midFanA.Responses.Add(new DialogResponses(mid.GetNextFormKey(), SkyrimRelease.SkyrimSE)
            { EditorID = "HcIoFanAMid" });
        mid.BeginWrite.ToPath(midPath).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

        // ---- LAST: re-lists ONLY INFO 0, with NO PNAM. The whole bug in one edit — it is evicted from the top
        //      and appended to the BOTTOM, so a broader line now answers first. ----
        var last = new SkyrimMod(ModKey.FromNameAndExtension(lastName), SkyrimRelease.SkyrimSE);
        var lastT = (IDialogTopic)WriteEngine.GenericGetOrAddAsOverride(last, tOrder);
        lastT.Responses.Clear();
        lastT.Responses.Add(new DialogResponses(info[0], SkyrimRelease.SkyrimSE) { EditorID = "HcIoLine0" });

        // LAST also adds a line to tForeign whose PNAM names a line of ANOTHER topic — defined in MASTER, while
        // the line pulling it in is defined HERE. That split is what makes the attribution assertion able to fail.
        var lastForeign = (IDialogTopic)WriteEngine.GenericGetOrAddAsOverride(last, tForeign);
        lastForeign.Responses.Clear();
        var lf = new DialogResponses(last.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcIoForeignLast" };
        lf.PreviousDialog.SetTo(fanA0);
        lastForeign.Responses.Add(lf);
        var foreignLine = lf.FormKey;

        last.BeginWrite.ToPath(lastPath).WithLoadOrder(new ISkyrimModGetter[] { master, mid }).Write();

        var dataDir = Path.Combine(dir, "data"); Directory.CreateDirectory(dataDir);
        using var resolver = LoadOrderResolver.Build(new[] { mPath, midPath, lastPath, zeroPath });
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
            bool ok = e is { Index: 0, Placement: InfoPlacement.HeadUnresolvable };
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

        // ---------- PNAM-ZERO-AXIS — measure the READER against a real on-disk zero PNAM ----------
        // The fixture is byte-patched, not writer-authored: Mutagen's writer emits NO subrecord for a null link,
        // so the writer-authored version of this arm measured the writer and concluded the reader could not tell
        // the two apart. It can. Live 1.9.4-dev against the real load order proved it (0BCC84's winning copy
        // carries a zero PNAM and is correctly placed FIRST), and this arm now pins the reader directly.
        {
            var io = Order(zeroTopic);
            var e = io?.Order.FirstOrDefault(x => x.Info == zeroMarkedReal);
            bool measured = e is { Placement: InfoPlacement.HeadFirstMarker };      // Head ⇒ the reader saw a PRESENT zero
            bool ok = zeroed > 0 && e is not null
                      && measured == DialogueInfoOrder.PnamZeroIsDistinguishable
                      && e.Index == 0;
            all &= Pass("PNAM-ZERO-AXIS", ok, e is null ? $"marked line absent (patched {zeroed} PNAM subrecord(s))"
                : $"patched={zeroed} reader-distinguishes={measured} (placement={e.Placement}, index={e.Index}), product flag={DialogueInfoOrder.PnamZeroIsDistinguishable}");
        }

        // ---------- BATCH-FAN-IN: one quest, two topics, DIFFERENT contributing plugins each ----------
        {
            var rep = DialogueValidate.Run(resolver, assets, qFan.FormKey);
            var a = rep.Topics.FirstOrDefault(t => t.Topic == fanA)?.InfoOrder;
            var b = rep.Topics.FirstOrDefault(t => t.Topic == fanB)?.InfoOrder;

            // A carries the mid plugin's extra line, B does not — so a cross-wired or first-topic-only batch
            // shows up as the wrong contributor set or the wrong line count, not merely a different order.
            bool aOk = a is not null && a.ContributingPlugins.Count == 2 && a.Order.Count == 3
                       && a.ContributingPlugins.Contains(midName, StringComparer.OrdinalIgnoreCase);
            bool bOk = b is not null && b.ContributingPlugins.Count == 1 && b.Order.Count == 2
                       && !b.ContributingPlugins.Contains(midName, StringComparer.OrdinalIgnoreCase);

            // IDENTITY, not just counts. The two topics are deliberately symmetric on the master side (2 lines
            // each), so a bug that SWAPS their master contributions keeps every count and membership test above
            // true and passes vacuously — the arm would claim to catch cross-wiring while catching only the
            // mid-plugin asymmetry (PR #293 third pass). These two assertions are what actually bite.
            bool identityOk = a is not null && b is not null
                              && a.Order.Any(e => e.Info == fanA0) && !a.Order.Any(e => e.Info == fanB0)
                              && b.Order.Any(e => e.Info == fanB0) && !b.Order.Any(e => e.Info == fanA0);

            all &= Pass("BATCH-FAN-IN", aOk && bOk && identityOk,
                a is null || b is null ? "a topic returned no order view"
                    : $"A: {a.ContributingPlugins.Count} plugin(s)/{a.Order.Count} lines, B: {b.ContributingPlugins.Count}/{b.Order.Count}, identity={identityOk}");
        }

        // ---------- PNAM-FOREIGN: a PNAM target in NO contributing list reaches the fallback resolver ----------
        {
            var io = Order(tForeign.FormKey);
            var e = io?.Order.FirstOrDefault(x => x.Info == foreignLine);
            var target = io?.Order.FirstOrDefault(x => x.Info == fanA0);
            // The foreign target is pulled IN, placed, and credited to ITS OWN plugin (master) — not to the
            // plugin that pulled it in (last), and not merely treated as unresolvable. A regression to Head
            // placement, or one crediting the puller, fails here.
            bool ok = io is not null && io.Order.Count == 3
                      && e is { Placement: InfoPlacement.AfterTarget } && target is not null
                      && target.PlacedBy.Equals(masterName, StringComparison.OrdinalIgnoreCase)
                      && e.Index == target.Index + 1;
            all &= Pass("PNAM-FOREIGN", ok, e is null ? "puller line absent"
                : $"n={io!.Order.Count} placement={e.Placement} target@{target?.Index} by {target?.PlacedBy}");
        }

        // ---------- PNAM-SELF: a self-referencing PNAM degrades, never throws ----------
        {
            InfoOrderView? io = null;
            string detail;
            try { io = Order(tSelf.FormKey); detail = io is null ? "no order view" : $"n={io.Order.Count} note={(io.Note is null ? "<none>" : "set")}"; }
            catch (Exception ex) { detail = $"THREW {ex.GetType().Name} — the never-throws contract is broken"; }
            bool ok = io is not null && io.Order.Count == 2 && io.Note is not null
                      && io.Note.Contains("OWN record", StringComparison.Ordinal);
            all &= Pass("PNAM-SELF", ok, detail);
        }

        // ---------- WRITER-DROPS-NULL: why the axis was mis-measured, pinned so the lesson is testable ----------
        // Asking Mutagen's WRITER for a null PreviousDialog emits NO subrecord, so the line reads back as having
        // no PNAM and takes the TAIL arm. That is the writer's behaviour, NOT the reader's — and conflating them
        // is what made PNAM-ZERO-AXIS assert a non-existent limitation for four review rounds. If a future Mutagen
        // starts emitting a zero subrecord here, this arm goes red and the byte-patched fixture becomes redundant.
        {
            var io = Order(tZeroWriter.FormKey);
            var e = io?.Order.FirstOrDefault(x => x.Info == writerNulled);
            bool ok = e is { Placement: InfoPlacement.Tail, Index: 1 };
            all &= Pass("WRITER-DROPS-NULL", ok, e is null ? "line absent"
                : $"placement={e.Placement} index={e.Index} (writer emitted no PNAM subrecord)");
        }

        // ---------- RENDER-MODEL-PIN + the retracted PNAM-zero caveat: MOVED to xunit ----------
        // DialogueWire.Render (the deleted 1.x whole-report renderer) is gone; both facts move onto the LIVE
        // surface that renders a topic's info-order today — housecarl_records project=info_order — as
        // DialogueFamilyTests.FactD1_TheShippedRenderStatesTheMergeModel and .FactD2_TheRetractedCaveatStaysAbsent.

        // ---------- UNREAD-* : the silent-contributor-drop fix, pinned directly ----------
        // Compute is a pure static, so the degraded states need no fixture plumbing — synthesise the inputs.
        // Without these the whole Q3 fix (Complete, UnreadContributors, the gated claim) is unpinned: delete the
        // `unread` argument or the `&& io.Complete` render gate and every other arm still passes.
        {
            var fkA = FormKey.Factory("000801:hcInfoMaster.esp");
            var fkB = FormKey.Factory("000802:hcInfoMaster.esp");
            var oneGroup = new List<(string, IReadOnlyList<InfoLine>)>
                { ("readable.esp", new[] { new InfoLine(fkA, null, false), new InfoLine(fkB, null, false) }) };

            // PARTIAL: one plugin read, one not — must NOT read as an uncontested single-plugin topic.
            var partial = DialogueInfoOrder.Compute(oneGroup, _ => null, new[] { "locked.esp" });
            bool partialOk = !partial.Complete && partial.Order.Count == 2
                             && partial.UnreadContributors.Count == 1
                             && partial.Note is not null && partial.Note.Contains("locked.esp", StringComparison.Ordinal);
            all &= Pass("UNREAD-PARTIAL", partialOk,
                $"complete={partial.Complete} unread={partial.UnreadContributors.Count} note={(partial.Note is null ? "<none>" : "names it")}");

            // TOTAL: nothing read at all. The shape that stayed silent after the first fix — and the COMMON one,
            // since a topic touched by a single plugin has no partial case.
            var total = DialogueInfoOrder.Compute(
                new List<(string, IReadOnlyList<InfoLine>)>(), _ => null, new[] { "locked.esp" });
            bool totalOk = !total.Complete && total.Order.Count == 0 && total.UnreadContributors.Count == 1;
            all &= Pass("UNREAD-TOTAL", totalOk,
                $"complete={total.Complete} lines={total.Order.Count} unread={total.UnreadContributors.Count}");

            // The RENDER must not print the "nothing merges here" claim for either, and must say INCOMPLETE.
            string rPartial = RenderOrderOnly(partial), rTotal = RenderOrderOnly(total);
            bool renderOk = rPartial.Contains("INCOMPLETE", StringComparison.Ordinal)
                            && !rPartial.Contains("nothing merges here", StringComparison.Ordinal)
                            && rTotal.Contains("INCOMPLETE", StringComparison.Ordinal)
                            && rTotal.Contains("NOT an empty topic", StringComparison.Ordinal);
            all &= Pass("UNREAD-RENDER", renderOk,
                $"partial={rPartial.Contains("INCOMPLETE", StringComparison.Ordinal)} total-not-silent={rTotal.Contains("INCOMPLETE", StringComparison.Ordinal)}");

            // The move BASELINE is the first CONTRIBUTING group, so with the DEFINING plugin unread it would be a
            // later plugin's list — move analysis must be SKIPPED, not measured against the wrong order.
            // Needs a fixture that DOES produce a move, plus a control: asserting "no moves" on input where
            // nothing moves anyway passes with the guard deleted (measured — this arm did exactly that at first).
            var fkC = FormKey.Factory("000803:hcInfoMaster.esp");
            var movingGroups = new List<(string, IReadOnlyList<InfoLine>)>
            {
                ("definer.esp", new[] { new InfoLine(fkA, null, false), new InfoLine(fkB, null, false),
                                        new InfoLine(fkC, null, false) }),
                ("patch.esp",   new[] { new InfoLine(fkA, null, false) }),      // re-lists A -> A goes to the tail
            };
            var control = DialogueInfoOrder.Compute(movingGroups, _ => null);
            var shifted = DialogueInfoOrder.Compute(movingGroups, _ => null, new[] { "definer.esp" },
                                                    originIsDefiningPlugin: false);
            bool baselineOk = control.Moved.Count == 1              // the move IS detectable on this input…
                              && shifted.Moved.Count == 0           // …and is withheld when the baseline is suspect
                              && shifted.Note is not null && shifted.Note.Contains("SKIPPED", StringComparison.Ordinal);
            all &= Pass("UNREAD-BASELINE", baselineOk,
                $"control moved={control.Moved.Count}, suspect-baseline moved={shifted.Moved.Count}, note={(shifted.Note is null ? "<none>" : "says skipped")}");
        }

        // ---------- UNREAD-WIRED / DEFINER-LOCK-LOUD / WINNER-LOCK-LOUD: MOVED to xunit ----------
        // All three drove DialogueWire.Render (the deleted 1.x whole-report renderer) on a HELD file — MO2/xEdit
        // holding a plugin exclusively, this project's own no-handles-at-rest scenario. They move onto a
        // dedicated DialogueWorld the test constructs itself (never the shared one — a held file is unreadable
        // to anything else in the process): DialogueFamilyTests.FactD3_UnreadWired,
        // .FactD4a_DefinerLockIsLoud, .FactD4b_WinnerLockIsLoud, using the ported HeldOpen harness and
        // asserting on CheckError / the merged "the check did not finish — {CheckError}" sentence
        // (DialogueSweep.cs:57-59) rather than the retired "could NOT complete" wording.

        // ---------- RENDER-BIG-TOPIC: over the row cap with nothing moved must not print an EMPTY list ----------
        // Found by running the shipped build over a real quest: a 37-line topic with 0 moved rendered the
        // "effective INFO order" header, then "listing only the 0 that moved", then nothing at all. Also pins
        // that a SINGLE-topic report ignores the row cap — the cap counts rows, so max_chars could not lift it
        // and the full order was simply unobtainable.
        {
            var many = new List<InfoLine>();
            for (int i = 1; i <= 40; i++) many.Add(new InfoLine(FormKey.Factory($"{i:X6}:big.esp"), null, false));
            var io = DialogueInfoOrder.Compute(
                new List<(string, IReadOnlyList<InfoLine>)> { ("big.esp", many), ("patch.esp", many) }, _ => null);

            string quest = RenderOrderOnly(io, asQuest: true);      // indented -> the row cap applies
            string solo = RenderOrderOnly(io, asQuest: false);      // single topic -> always in full

            bool questOk = io.Moved.Count == 0
                           && quest.Contains("none of which changed position", StringComparison.Ordinal)
                           && !quest.Contains("listing only the 0", StringComparison.Ordinal);
            bool soloOk = solo.Contains("#40", StringComparison.Ordinal);   // full list, cap ignored
            all &= Pass("RENDER-BIG-TOPIC", questOk && soloOk,
                $"moved={io.Moved.Count} questSaysNoMoves={questOk} soloListsAll={soloOk}");
        }

        // ---------- RENDER-CLAIM-GATES: two aggregate claims that were stated unconditionally ----------
        // (a) the header counted plugins successfully READ while calling them the plugins that TOUCH the topic,
        //     printing a different number from the INCOMPLETE banner directly above it; (b) "none of which
        //     changed position" was asserted whenever the moved set was empty — including when move analysis
        //     never ran, contradicting the note two lines below it.
        {
            var lines = new List<InfoLine>();
            for (int i = 1; i <= 40; i++) lines.Add(new InfoLine(FormKey.Factory($"{i:X6}:big.esp"), null, false));

            // One list read, one plugin unread -> the header must say 2 touching, not 1, and not "1 plugins".
            var partial = DialogueInfoOrder.Compute(
                new List<(string, IReadOnlyList<InfoLine>)> { ("read.esp", lines) }, _ => null, new[] { "locked.esp" });
            string rPartial = RenderOrderOnly(partial, asQuest: true);
            bool countOk = rPartial.Contains("merged across 2 plugins that touch", StringComparison.Ordinal)
                           && !rPartial.Contains("merged across 1 plugins", StringComparison.Ordinal);

            // Move analysis skipped (baseline untrusted) -> must NOT claim nothing moved.
            var skipped = DialogueInfoOrder.Compute(
                new List<(string, IReadOnlyList<InfoLine>)> { ("read.esp", lines) }, _ => null,
                new[] { "definer.esp" }, originIsDefiningPlugin: false);
            string rSkipped = RenderOrderOnly(skipped, asQuest: true);
            bool claimOk = !skipped.MovesComputed
                           && !rSkipped.Contains("none of which changed position", StringComparison.Ordinal)
                           && rSkipped.Contains("is NOT known here", StringComparison.Ordinal);

            // The OTHER axis, and the one actually reported: an unread plugin AFTER the definer leaves the
            // baseline trusted, so MovesComputed stays TRUE while lines are missing. Gating on MovesComputed
            // alone passes here — measured: this arm's first cut used only the untrusted-baseline fixture and
            // stayed green with the Complete gate deleted, the same vacuity the review found in it.
            var incomplete = DialogueInfoOrder.Compute(
                new List<(string, IReadOnlyList<InfoLine>)> { ("read.esp", lines) }, _ => null,
                new[] { "locked.esp" });                       // definer read, a LATER plugin unread
            string rIncomplete = RenderOrderOnly(incomplete, asQuest: true);
            bool completeGateOk = incomplete.MovesComputed && !incomplete.Complete
                                  && !rIncomplete.Contains("none of which changed position", StringComparison.Ordinal);

            // The SIBLING branch, six lines below the one above: "the rest keep their original relative order"
            // is a claim about rows that branch WITHHOLDS, so a reader cannot check it. Needs a moved line to
            // reach that branch at all, plus an unread contributor so the claim is unsafe.
            var withMove = new List<InfoLine>(lines);
            var movedGroups = new List<(string, IReadOnlyList<InfoLine>)>
            {
                ("definer.esp", withMove),
                ("patch.esp",   new[] { withMove[0] }),        // re-lists line 1, no PNAM -> it goes to the tail
            };
            var siblingIncomplete = DialogueInfoOrder.Compute(movedGroups, _ => null, new[] { "locked.esp" });
            string rSibling = RenderOrderOnly(siblingIncomplete, asQuest: true);
            bool siblingOk = siblingIncomplete.Moved.Count > 0 && !siblingIncomplete.Complete
                             && !rSibling.Contains("the rest keep their original relative order",
                                                   StringComparison.Ordinal)
                             // …and the POSITIVE moved claim is qualified rather than suppressed: an unread
                             // plugin could re-list the same line WITH its PNAM and put it back, so the lead
                             // must say how far the evidence reaches instead of asserting the shift outright.
                             && rSibling.Contains("as far as could be read", StringComparison.Ordinal);

            all &= Pass("RENDER-CLAIM-GATES", countOk && claimOk && completeGateOk && siblingOk,
                $"touchingCount={countOk} skippedGated={claimOk} incompleteGated={completeGateOk} " +
                $"siblingGated={siblingOk} (movesComputed={incomplete.MovesComputed}, complete={incomplete.Complete})");
        }

        // ---------- RENDER-HEAD-SPLIT: the split must be visible where users see it ----------
        // HeadFirstMarker/HeadUnresolvable were pinned only at the enum level. Collapsing the two render branches
        // back to one shared string would restore the whole user-visible defect — a correct vanilla line
        // described as a fault — with the entire suite green. That is the fourth-pass lesson (a guard that never
        // drives the shipped path cannot catch an unwired one) applied to the fifth pass's own fix.
        {
            // A CONTESTED view, because the uncontested branch prints a one-line summary and no rows at all —
            // so the annotations under test never render there. Entries are built directly to put one of each
            // placement in the same list; the placements themselves are pinned against Compute by PNAM-ZERO-AXIS
            // and PNAM-HEAD. Asserted per LINE, not merely per report, so a swap would fail too.
            var fkMarker = FormKey.Factory("00AA01:split.esp");
            var fkBroken = FormKey.Factory("00AA02:split.esp");
            var view = new InfoOrderView(
                new[]
                {
                    new InfoOrderEntry(fkMarker, 0, "a.esp", InfoPlacement.HeadFirstMarker, 0, false, false),
                    new InfoOrderEntry(fkBroken, 1, "b.esp", InfoPlacement.HeadUnresolvable, 1, false, false),
                },
                new[] { "a.esp", "b.esp" }, Array.Empty<InfoOrderEntry>(), null);

            string rendered = RenderOrderOnly(view);
            string markerLine = rendered.Split((char)10).FirstOrDefault(l => l.Contains(fkMarker.ToString(), StringComparison.Ordinal)) ?? "";
            string brokenLine = rendered.Split((char)10).FirstOrDefault(l => l.Contains(fkBroken.ToString(), StringComparison.Ordinal)) ?? "";

            bool markerOk = markerLine.Contains("deliberate, not a fault", StringComparison.Ordinal)
                            && !markerLine.Contains("names no reachable line", StringComparison.Ordinal);
            bool brokenOk = brokenLine.Contains("names no reachable line", StringComparison.Ordinal)
                            && !brokenLine.Contains("deliberate, not a fault", StringComparison.Ordinal);
            all &= Pass("RENDER-HEAD-SPLIT", markerOk && brokenOk,
                $"markerWordedDeliberate={markerOk} unresolvableWordedSuspect={brokenOk}");
        }

        // ---------- CYCLE-PREPLACED: the shape post-hoc detection was added FOR ----------
        // Both members already placed by an earlier plugin, so no recursion happens and the old placement-time
        // signal could never fire. Without this arm CountPnamCycles could `return 0` and everything stays green.
        {
            var x = FormKey.Factory("000901:hcInfoMaster.esp");
            var y = FormKey.Factory("000902:hcInfoMaster.esp");
            var groups = new List<(string, IReadOnlyList<InfoLine>)>
            {
                ("base.esp",  new[] { new InfoLine(x, null, false), new InfoLine(y, null, false) }),
                ("patch.esp", new[] { new InfoLine(x, y, false),    new InfoLine(y, x, false) }),   // crossed PNAMs
            };
            var io = DialogueInfoOrder.Compute(groups, _ => null);
            bool ok = io.Order.Count == 2 && io.Note is not null
                      && io.Note.Contains("PNAM cycle", StringComparison.Ordinal);
            all &= Pass("CYCLE-PREPLACED", ok,
                $"n={io.Order.Count} note={(io.Note is null ? "<none>" : io.Note[..Math.Min(28, io.Note.Length)])}");
        }

        // ---------- DEGRADE-CEILINGS: the two bounds, both previously unpinned ----------
        {
            // Move analysis past its line ceiling — the order stays exact, the moved set is not computed.
            var many = new List<InfoLine>();
            for (int i = 1; i <= 401; i++) many.Add(new InfoLine(FormKey.Factory($"{i:X6}:big.esp"), null, false));
            var big = DialogueInfoOrder.Compute(
                new List<(string, IReadOnlyList<InfoLine>)> { ("big.esp", many) }, _ => null);
            bool moveCeil = big.Order.Count == 401 && big.Note is not null
                            && big.Note.Contains("move analysis", StringComparison.Ordinal);

            // A FORWARD PNAM chain longer than the recursion ceiling: each line points at the NEXT, which is not
            // yet placed, so every placement recurses. Uncapped this is the stack-overflow shape.
            var chain = new List<InfoLine>();
            for (int i = 1; i <= 600; i++)
                chain.Add(new InfoLine(FormKey.Factory($"{i:X6}:chain.esp"),
                                       i < 600 ? FormKey.Factory($"{i + 1:X6}:chain.esp") : null, false));
            var deep = DialogueInfoOrder.Compute(
                new List<(string, IReadOnlyList<InfoLine>)> { ("chain.esp", chain) }, _ => null);
            bool depthCeil = deep.Order.Count == 600 && deep.Note is not null
                             && deep.Note.Contains("hop ceiling", StringComparison.Ordinal);

            all &= Pass("DEGRADE-CEILINGS", moveCeil && depthCeil,
                $"moveCeiling={moveCeil} depthCeiling={depthCeil} (deep n={deep.Order.Count})");
        }

        Console.WriteLine();
        Console.WriteLine(all ? "RESULT: PASS — effective INFO order holds." : "RESULT: FAIL");
        return all ? 0 : 1;
    }

    /// <summary>Render just the INFO-order block for a synthesised view, by wrapping it in the minimum report the
    /// real renderer consumes — so the arms above pin the SHIPPED render path (the gated "nothing merges here"
    /// claim, the INCOMPLETE banner) rather than a re-implementation of it.</summary>
    static string RenderOrderOnly(InfoOrderView io, bool asQuest = false)
    {
        // #486: DialogueWire.Render (the deleted 1.x whole-report renderer) is gone; the INFO-order BLOCK it
        // wrapped survives as DialogueWire.AppendInfoOrderView, called directly here rather than through a
        // synthetic TopicValidation/DialogueValidationReport shell.
        var sb = new System.Text.StringBuilder();
        DialogueWire.AppendInfoOrderView(sb, io, "", 8000, indent: asQuest);
        return sb.ToString();
    }

    /// <summary>Zero the 4 data bytes of every 4-byte PNAM subrecord in a plugin, in place. Produces the
    /// present-but-zero PNAM that real (CK / xEdit-filled) plugins carry and that Mutagen's writer will not emit
    /// from a null link — so the reader can be measured on the shape it actually meets in the wild. Returns how
    /// many it patched, so a fixture that silently patched nothing fails loudly rather than testing the absent
    /// case by accident.</summary>
    static int ZeroEveryPnam(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var sig = new byte[] { (byte)'P', (byte)'N', (byte)'A', (byte)'M' };
        int patched = 0;
        for (int i = 0; i + 10 <= bytes.Length; i++)
        {
            if (bytes[i] != sig[0] || bytes[i + 1] != sig[1] || bytes[i + 2] != sig[2] || bytes[i + 3] != sig[3])
                continue;
            if (BitConverter.ToUInt16(bytes, i + 4) != 4) continue;          // size field must be 4
            bytes[i + 6] = bytes[i + 7] = bytes[i + 8] = bytes[i + 9] = 0;
            patched++;
            i += 9;
        }
        File.WriteAllBytes(path, bytes);
        return patched;
    }

    static bool Pass(string label, bool ok, string detail)
    {
        Console.WriteLine($"   {label,-20}: {(ok ? "PASS" : "FAIL")} — {detail}");
        return ok;
    }
}

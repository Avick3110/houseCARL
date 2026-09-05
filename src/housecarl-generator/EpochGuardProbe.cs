using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD for the epoch fingerprint (tool-surface 2.0, W1 — SPEC §2.1.1): every index-backed response
/// stamps the identity of the build it was answered from, so cross-page drift is DETECTABLE instead of silently
/// incoherent, and a future artifact re-entry can be epoch-checked. Arms:
///
///   1  IDENTITY — the fingerprint is a deterministic function of the world-state (plugin names + mtimes, in
///      order): same order twice = same epoch, a SECOND resolver over the same files (the restart case) = same
///      epoch, and the resolver/view/format contracts hold (16 lowercase hex; view epoch == resolver epoch).
///   2  SENSITIVITY — a content edit (mtime change, NEWER or OLDER — the restored-backup case must not be
///      invisible), a reorder, and a set change each produce a DIFFERENT epoch; RefreshIfStale over an
///      unchanged order keeps it.
///   3  STAMPS — every index-backed lane carries the capture's epoch: cross_plugin_query (incl. the Fail path
///      staying null — a refusal that never consulted a build must not invent one), batch (ONE epoch for the
///      whole batch; a malformed-formid row carries none), single read (refusals INCLUDED — "not present" is an
///      answer about a build), resolve (out-epoch), effect_chain, status. The diff, check_errors and
///      validate_scripts arms this list used to name went with #486's deleted render halves: the two sweep
///      families' stamp facts are carried by <c>EpochCheckSweepTests</c> against the merged renderer today, and
///      diff's died with the 1.x pairwise-diff service.
///   4  RENDERS — the text and json renders both emit it (D2: the two may differ only in formatting), and a
///      freshness rebuild between two queries changes the STAMP, not just the index (the cross-page detection
///      this exists for).
///
/// Self-contained: synthetic MO2 instance + synthesized plugins in temp. No game data.
/// </summary>
internal static class EpochGuardProbe
{
    [CiProbe("epoch-guard")]
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" epoch guard — every index-backed response names the build it read (SPEC §2.1.1)");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }
        static bool IsEpochToken(string? e) => e is { Length: 16 } && e.All(ch => ch is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

        var root = Path.Combine(Path.GetTempPath(), "hc-epoch-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "game", "Data"));

            // ---- synthesized plugins: a master (weapons + an MGEF/spell pair) and an override ----
            var masterKey = new ModKey("HcEpochMaster", ModType.Master);
            var ovKey = new ModKey("HcEpochOverride", ModType.Plugin);
            string masterName = masterKey.FileName.String, ovName = ovKey.FileName.String;

            var master = new SkyrimMod(masterKey, SkyrimRelease.SkyrimSE);
            var weapons = new List<FormKey>();
            for (int i = 0; i < 3; i++)
            {
                var w = master.Weapons.AddNew(); w.EditorID = $"HcEpW{i}";
                w.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1 };
                weapons.Add(w.FormKey);
            }
            var mgef = master.MagicEffects.AddNew(); mgef.EditorID = "HcEpMgef";
            var spell = master.Spells.AddNew(); spell.EditorID = "HcEpSpell";
            var eff = new Effect(); eff.BaseEffect.SetTo(mgef.FormKey);
            eff.Data = new EffectData { Magnitude = 5 };
            spell.Effects.Add(eff);

            var ovMod = new SkyrimMod(ovKey, SkyrimRelease.SkyrimSE);
            ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(ovMod, master.Weapons.First()))
                .BasicStats = new WeaponBasicStats { Damage = 20, Weight = 1 };

            // An OFF-ORDER plugin: in a DISABLED mod folder (modlist '-OldMod'), overriding weapons[0] — the
            // documented diff-against-a-disabled-old-patch case. The coverage-qualifier arms it was built for
            // (PR #305 fold) moved to EpochCheckSweepTests with #486's deletion; the plugin stays because this
            // world's SHAPE is what arms 1 and 2 fingerprint, and dropping a mod folder from it changes what they
            // measure.
            var oldKey = new ModKey("HcEpochOld", ModType.Plugin);
            string oldName = oldKey.FileName.String;
            var oldMod = new SkyrimMod(oldKey, SkyrimRelease.SkyrimSE);
            ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(oldMod, master.Weapons.First()))
                .BasicStats = new WeaponBasicStats { Damage = 15, Weight = 1 };

            var inst = Path.Combine(root, "inst");
            var mods = Path.Combine(inst, "mods");
            Directory.CreateDirectory(Path.Combine(mods, "MasterMod"));
            Directory.CreateDirectory(Path.Combine(mods, "OverrideMod"));
            Directory.CreateDirectory(Path.Combine(mods, "OldMod"));
            Directory.CreateDirectory(Path.Combine(mods, "BadMod"));
            var masterFile = Path.Combine(mods, "MasterMod", masterName);
            var ovFile = Path.Combine(mods, "OverrideMod", ovName);
            var oldFile = Path.Combine(mods, "OldMod", oldName);
            master.BeginWrite.ToPath(masterFile).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            ovMod.BeginWrite.ToPath(ovFile).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();
            oldMod.BeginWrite.ToPath(oldFile).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();
            // An UNPARSEABLE plugin, ENABLED — the index build excludes it. The arm that named it in a sweep scope
            // and read back the CORE frame's excluded-plugin refusal (PR #305 re-review finding 1) moved to
            // EpochCheckSweepTests.FactE5_6 with #486's deletion; the plugin stays because it is listed in this
            // world's loadorder.txt/plugins.txt below and arms 1-2 fingerprint that order.
            const string badName = "HcEpochBad.esp";
            File.WriteAllText(Path.Combine(mods, "BadMod", badName), "this is not a bethesda plugin");

            string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

            // ---- 1: identity — deterministic over the world-state; stable across resolver instances ----
            Console.WriteLine("--- 1: identity — a deterministic function of (names, mtimes, order) ---");
            {
                var paths = new[] { masterFile, ovFile };
                using var r1 = LoadOrderResolver.Build(paths);
                using var r2 = LoadOrderResolver.Build(paths);          // the restart case: a fresh process over unchanged files
                Check(IsEpochToken(r1.Epoch), $"epoch is an opaque 16-hex token — '{r1.Epoch}'");
                Check(r1.Epoch == r2.Epoch, "a SECOND resolver over the same files fingerprints IDENTICALLY (restart invalidates nothing)");
                Check(r1.Capture().Epoch == r1.Epoch, "view epoch == resolver epoch (one build, one identity)");
                Check(!r1.RefreshIfStale() && r1.Capture().Epoch == r2.Epoch, "RefreshIfStale over an UNCHANGED order keeps the epoch");
            }

            // ---- 2: sensitivity — content, order, and set changes each re-fingerprint ----
            Console.WriteLine();
            Console.WriteLine("--- 2: sensitivity — content/order/set changes re-fingerprint; backdating is not invisible ---");
            {
                var paths = new[] { masterFile, ovFile };
                using var r = LoadOrderResolver.Build(paths);
                var e0 = r.Epoch;

                File.SetLastWriteTimeUtc(ovFile, DateTime.UtcNow.AddHours(-2));   // OLDER mtime — the restored-backup case
                Check(r.RefreshIfStale(), "a BACKDATED content change is seen (value-compare, not newer-than)");
                var e1 = r.Capture().Epoch;
                Check(e1 != e0, $"…and the epoch changed with it ({e0} → {e1})");

                using var rSwap = LoadOrderResolver.Build(new[] { ovFile, masterFile });   // same set, different order
                Check(rSwap.Epoch != e1, "a REORDER fingerprints differently (winner identity depends on it)");
                using var rOne = LoadOrderResolver.Build(new[] { masterFile });            // different set
                Check(rOne.Epoch != e1 && rOne.Epoch != rSwap.Epoch, "a SET change fingerprints differently");

                // PR #305 review: the MO2 same-name conflict — two mods ship the SAME-NAMED plugin; a left-pane
                // reorder swaps WHICH file wins the slot with no name change and (move, or same base archive) no
                // mtime change. Names+mtimes alone gave the new build the OLD epoch; the path term catches it.
                var dirA = Path.Combine(root, "same-name-a"); var dirB = Path.Combine(root, "same-name-b");
                Directory.CreateDirectory(dirA); Directory.CreateDirectory(dirB);
                var copyA = Path.Combine(dirA, ovName); var copyB = Path.Combine(dirB, ovName);
                File.Copy(ovFile, copyA); File.Copy(ovFile, copyB);
                var tick = DateTime.UtcNow.AddHours(-1);
                File.SetLastWriteTimeUtc(copyA, tick); File.SetLastWriteTimeUtc(copyB, tick);   // identical name + mtime
                using var rA = LoadOrderResolver.Build(new[] { masterFile, copyA });
                using var rB = LoadOrderResolver.Build(new[] { masterFile, copyB });
                Check(rA.Epoch != rB.Epoch, "a DIFFERENT FILE winning a same-named slot (same name, same mtime) fingerprints differently — the path term");

                // Third-round BLOCKER: an open failure is TRANSIENT (xEdit/MO2 holding an exclusive handle) and
                // excludes the plugin wholesale — a build that skipped it resolves materially different winners
                // than the healthy build over identical names/paths/mtimes, so the exclusion set is a fingerprint
                // term. Locked here exactly the way a real editor locks it (FileShare.None), mtimes untouched.
                string lockedEpoch;
                using (new FileStream(copyA, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    using var rLocked = LoadOrderResolver.Build(new[] { masterFile, copyA });
                    Check(rLocked.ExcludedPlugins.Count == 1, "a LOCKED plugin is excluded from the build (the transient degraded state)");
                    lockedEpoch = rLocked.Epoch;
                }
                using var rHealthy = LoadOrderResolver.Build(new[] { masterFile, copyA });
                Check(rHealthy.ExcludedPlugins.Count == 0 && lockedEpoch != rHealthy.Epoch,
                      $"…and the degraded and healthy builds fingerprint DIFFERENTLY over identical names/paths/mtimes ({lockedEpoch} vs {rHealthy.Epoch}) — an artifact saved degraded can never pass re-entry against healthy");
            }

            // ---- 3+4: stamps + renders, on the real service over a synthetic MO2 instance ----
            Console.WriteLine();
            Console.WriteLine("--- 3: every index-backed lane stamps the capture's epoch ---");
            {
                var genDir = Path.Combine(root, "corpus-gen");
                CorpusGenerator.GenerateAll(genDir, Path.Combine(root, "corpus-ref"));
                CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

                File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
                    "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                    + Path.Combine(root, "game").Replace(@"\", @"\\") + ")\r\n");
                var prof = Path.Combine(inst, "profiles", "Default");
                Directory.CreateDirectory(prof);
                File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + masterName + "\r\n" + ovName + "\r\n" + badName + "\r\n");
                File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + masterName + "\r\n*" + ovName + "\r\n*" + badName + "\r\n");
                File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n-OldMod\r\n+BadMod\r\n+OverrideMod\r\n+MasterMod\r\n");

                var store = new UserConfigStore(Path.Combine(root, "user.json"));
                using var svc = LoadOrderService.WithInstance(inst, 0, store);
                var current = svc.Stats().epoch;
                Check(IsEpochToken(current), $"Stats() names the current build epoch — '{current}'");
                Check(svc.StatusData().Epoch == current, "StatusData carries the same epoch (the status line's source)");

                // cross_plugin_query — outcome stamp + all three renders + the Fail path
                var q = svc.CrossQuery("WEAP", null, null, false, null, null, 500);
                Check(q.Epoch == current, "cross_plugin_query outcome stamps the scanned build");
                Check(Wire.RenderCrossQuery(svc, q, null, 0).Contains($"epoch={current}"), "…text render carries epoch=<hex> in the header");
                Check(JsonWire.RenderCrossQuery(svc, q, null, 0, false, false).Contains($"\"epoch\": \"{current}\""), "…json render carries the epoch field");
                Check(JsonWire.RenderCrossQueryDense(svc, q, null, 0, false, false).Contains($"\"epoch\": \"{current}\""), "…dense render carries it too");
                var g = svc.CrossQuery("WEAP", null, null, false, null, null, 500, groupBy: "winner");
                Check(g.Epoch == current && Wire.RenderCrossQuery(svc, g, null, 0).Contains($"epoch={current}"),
                      "…group_by count table carries it (aggregations page too)");
                Check(svc.CrossQuery((string?)null, null, null, false, null, null, 500).Epoch is null,
                      "…a REFUSED query (no filter) stays unstamped — a refusal that consulted no build invents none");

                // batch — ONE epoch for the whole batch; refusal rows answered off the build carry it; parse failures don't
                var batch = svc.ResolveBatch(new[] { Fid(weapons[0]), Fid(weapons[1]), "notaformid", "ABC123:Absent.esp" }, null, false);
                var stamped = batch.Where(o => o.Epoch is not null).Select(o => o.Epoch).Distinct().ToList();
                Check(stamped.Count == 1 && stamped[0] == current, "batch: every consulted row carries the batch's ONE epoch");
                Check(batch[2].Epoch is null, "…the malformed-formid row (no view consulted) carries none");
                Check(batch[3].Error is not null && batch[3].Epoch == current,
                      "…the absent-record REFUSAL is stamped ('not present' is an answer about a build)");
                Check(Wire.RenderBatch(batch, 0).Contains($"epoch={current}"), "…text header carries it once, response-level");
                Check(JsonWire.RenderBatch(batch, 0).Contains($"\"epoch\": \"{current}\""), "…json carries it once, response-level");

                // single read — text tail + json top-level. Wire.RenderRecord / JsonWire.RenderRecord went with
                // #486's render-halves cut; the fact moved to housecarl_records, its live surface today
                // (RecordsListLaneTests.IdentityForm_LabelsTheListStatesTheFormAndStampsTheEpoch for text,
                // EpochCheckSweepTests.FactE2_ASingleRecordReadsJsonRenderCarriesTheCapturesEpoch for json).
                var one = svc.ResolveRead(FormKey.Factory(Fid(weapons[0])), null, null, false);
                Check(one.Epoch == current, "read_record outcome stamps its capture");

                // resolve — the out-epoch overload feeds both renders
                var rows = svc.ResolveRefs(new[] { Fid(weapons[0]), Fid(mgef.FormKey) }, out var resolveEpoch);
                Check(resolveEpoch == current, "resolve hands back the batch's epoch");
                Check(Wire.RenderResolve(rows, 0, resolveEpoch).Contains($"epoch={current}"), "…text render carries it");
                Check(JsonWire.RenderResolve(rows, 0, resolveEpoch).Contains($"\"epoch\": \"{current}\""), "…json render carries it");

                // effect_chain / sweeps (the pairwise-diff arms went with LoadOrderService.DiffRecord, #486)
                var ec = svc.ResolveEffectChain(mgef.FormKey, null, 500);
                Check(ec.Error is null && ec.Epoch == current && Wire.RenderEffectChain(ec, 0, "limit=").Contains($"epoch={current}"),
                      "effect_chain stamps + renders it");
                // check_errors / validate_scripts sweep stamps, their refusal contracts (locate, the CORE
                // sweep frame's excluded-plugin refusal, scripts' not-in-order refusal), and the off-order
                // coverage qualifier all went through the deleted 1.x single-family renderers
                // (Wire/JsonWire.RenderCheckErrors, .RenderScriptCheck). Fresh facts, on the merged
                // Wire.RenderCheck / JsonWire.RenderCheck a surviving tool calls: EpochCheckSweepTests —
                // FactE4 (sweep stamps), FactE5_6 (every refusal shape, no coverage claim on a refusal),
                // FactE7/FactE8 (the off-order qualifier, text and json).
                //
                // The absent-record READ refusal (ex-"3b") is not re-tested here either: it is the same fact
                // E3 already covers on the live records surface (RecordsArtifactTests.StaleReEntry…,
                // RecordsRefusalGrammarTests.TheOffOrderRefusalCarriesTheBuildItConsultedNotEpochNull).
                //
                // effect_chain's own refusal contract survives unmoved — Wire.RenderEffectChain is untouched.
                var ecMiss = svc.ResolveEffectChain(FormKey.Factory("0ABC12:" + masterName), null, 500);
                Check(ecMiss.Error is not null && ecMiss.Epoch == current, "effect_chain's not-in-order refusal is stamped");
                Check(Wire.RenderEffectChain(ecMiss, 0, "limit=").Contains($"epoch={current}"), "…and rendered");
                Check(svc.ResolveEffectChain(mgef.FormKey, new[] { "WEAP" }, 500).Epoch is null,
                      "…its PRE-capture type-narrow refusal stays null");

                // ---- 4: the pin + the re-stamp (PR #305 fold, finding 2) ----
                Console.WriteLine();
                Console.WriteLine("--- 4: detail fills read the SCANNED build (the pin); the NEXT query re-stamps ---");
                // Scan at the current build, THEN rewrite the override plugin to also override weapons[1]. The
                // pinned render must fill its rows off the SCANNED build (override wins exactly ONE record); the
                // pre-fold code re-captured per row, so the rewrite's build leaked into rows under a header
                // stamped with the old epoch — the affirmative single-build claim the response didn't satisfy.
                var qPin = svc.CrossQuery("WEAP", null, null, false, null, null, 500);
                Check(qPin.Epoch == current, "pin arm: scan stamped at the current build");
                // Re-review finding 3 (structural): every ReadOutcome carries the ViewPin its epoch names, so the
                // conflict-tree fill on single reads / batch items reads the stamped build through the SAME
                // ResolveTreePinned path the behavioural arm below exercises for the scan.
                Check(qPin.Pin is not null
                      && svc.ResolveRead(FormKey.Factory(Fid(weapons[0])), null, null, true).Pin is not null
                      && svc.ResolveBatch(new[] { Fid(weapons[0]) }, null, true)[0].Pin is not null,
                      "scan, single read, and batch outcomes all carry the ViewPin their stamp names (tree fills read it)");
                var ovMod2 = new SkyrimMod(ovKey, SkyrimRelease.SkyrimSE);
                ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(ovMod2, master.Weapons.First()))
                    .BasicStats = new WeaponBasicStats { Damage = 20, Weight = 1 };
                ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(ovMod2, master.Weapons.Skip(1).First()))
                    .BasicStats = new WeaponBasicStats { Damage = 99, Weight = 1 };
                ovMod2.BeginWrite.ToPath(ovFile).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();
                var pinned = JsonWire.RenderCrossQuery(svc, qPin, new[] { "EditorID" }, 0, false, false);
                Check(pinned.Contains($"\"epoch\": \"{current}\""), "the pinned render still stamps the scanned build");
                int ovWins = System.Text.RegularExpressions.Regex.Matches(
                    pinned, "\"winner\": \"" + System.Text.RegularExpressions.Regex.Escape(ovName) + "\"").Count;
                Check(ovWins == 1,
                      $"…and its fills read the SCANNED build's winners — the override wins exactly its one record ({ovWins}/1), not the mid-render rewrite's two");
                var q2 = svc.CrossQuery("WEAP", null, null, false, null, null, 500);
                Check(q2.Epoch is not null && q2.Epoch != current,
                      $"the NEXT query re-stamps ({current} → {q2.Epoch}) — cross-page drift visible");
                Check(svc.Stats().epoch == q2.Epoch, "…and status agrees with the new build");
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort temp cleanup */ }
        }

        Console.WriteLine();
        Console.WriteLine($"[epoch-guard] {(fail == 0 ? "PASS — every index-backed answer names its build." : $"FAIL — {fail} check(s) failed.")}");
        return fail == 0 ? 0 : 1;
    }
}

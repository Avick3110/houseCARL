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
///      answer about a build), resolve (out-epoch), diff, effect_chain, check_errors, validate_scripts, status.
///   4  RENDERS — the text and json renders both emit it (D2: the two may differ only in formatting), and a
///      freshness rebuild between two queries changes the STAMP, not just the index (the cross-page detection
///      this exists for).
///
/// Self-contained: synthetic MO2 instance + synthesized plugins in temp. No game data.
/// </summary>
internal static class EpochGuardProbe
{
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

            var inst = Path.Combine(root, "inst");
            var mods = Path.Combine(inst, "mods");
            Directory.CreateDirectory(Path.Combine(mods, "MasterMod"));
            Directory.CreateDirectory(Path.Combine(mods, "OverrideMod"));
            var masterFile = Path.Combine(mods, "MasterMod", masterName);
            var ovFile = Path.Combine(mods, "OverrideMod", ovName);
            master.BeginWrite.ToPath(masterFile).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            ovMod.BeginWrite.ToPath(ovFile).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

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
                File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + masterName + "\r\n" + ovName + "\r\n");
                File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + masterName + "\r\n*" + ovName + "\r\n");
                File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+OverrideMod\r\n+MasterMod\r\n");

                var store = new UserConfigStore(Path.Combine(root, "user.json"));
                using var svc = LoadOrderService.WithInstance(inst, 0, store);
                var current = svc.Stats().epoch;
                Check(IsEpochToken(current), $"Stats() names the current build epoch — '{current}'");
                Check(svc.StatusData().Epoch == current, "StatusData carries the same epoch (the status line's source)");

                // cross_plugin_query — outcome stamp + all three renders + the Fail path
                var q = svc.CrossQuery("WEAP", null, null, false, null, null, 500);
                Check(q.Epoch == current, "cross_plugin_query outcome stamps the scanned build");
                Check(Wire.RenderCrossQuery(svc, q, null, false, 0).Contains($"epoch={current}"), "…text render carries epoch=<hex> in the header");
                Check(JsonWire.RenderCrossQuery(svc, q, null, 0, false, false).Contains($"\"epoch\": \"{current}\""), "…json render carries the epoch field");
                Check(JsonWire.RenderCrossQueryDense(svc, q, null, 0, false, false).Contains($"\"epoch\": \"{current}\""), "…dense render carries it too");
                var g = svc.CrossQuery("WEAP", null, null, false, null, null, 500, groupBy: "winner");
                Check(g.Epoch == current && Wire.RenderCrossQuery(svc, g, null, false, 0).Contains($"epoch={current}"),
                      "…group_by count table carries it (aggregations page too)");
                Check(svc.CrossQuery(null, null, null, false, null, null, 500).Epoch is null,
                      "…a REFUSED query (no filter) stays unstamped — a refusal that consulted no build invents none");

                // batch — ONE epoch for the whole batch; refusal rows answered off the build carry it; parse failures don't
                var batch = svc.ResolveBatch(new[] { Fid(weapons[0]), Fid(weapons[1]), "notaformid", "ABC123:Absent.esp" }, null, false);
                var stamped = batch.Where(o => o.Epoch is not null).Select(o => o.Epoch).Distinct().ToList();
                Check(stamped.Count == 1 && stamped[0] == current, "batch: every consulted row carries the batch's ONE epoch");
                Check(batch[2].Epoch is null, "…the malformed-formid row (no view consulted) carries none");
                Check(batch[3].Error is not null && batch[3].Epoch == current,
                      "…the absent-record REFUSAL is stamped ('not present' is an answer about a build)");
                Check(Wire.RenderBatch(svc, batch, null, false, 0).Contains($"epoch={current}"), "…text header carries it once, response-level");
                Check(JsonWire.RenderBatch(batch, 0).Contains($"\"epoch\": \"{current}\""), "…json carries it once, response-level");

                // single read — text tail + json top-level
                var one = svc.ResolveRead(FormKey.Factory(Fid(weapons[0])), null, null, false);
                Check(one.Epoch == current, "read_record outcome stamps its capture");
                Check(Wire.RenderRecord(svc, one, null, false, 0).Contains($"epoch={current}"), "…text render carries it");
                Check(JsonWire.RenderRecord(one, 0).Contains($"\"epoch\": \"{current}\""), "…json render carries it");

                // resolve — the out-epoch overload feeds both renders
                var rows = svc.ResolveRefs(new[] { Fid(weapons[0]), Fid(mgef.FormKey) }, out var resolveEpoch);
                Check(resolveEpoch == current, "resolve hands back the batch's epoch");
                Check(Wire.RenderResolve(rows, 0, resolveEpoch).Contains($"epoch={current}"), "…text render carries it");
                Check(JsonWire.RenderResolve(rows, 0, resolveEpoch).Contains($"\"epoch\": \"{current}\""), "…json render carries it");

                // diff / effect_chain / sweeps
                var d = svc.DiffRecord(Fid(weapons[0]), masterName, ovName, null);
                Check(d.Error is null && d.Epoch == current, "diff_record stamps the ONE build both poles resolved against");
                Check(Wire.RenderDiffRecord(d, 0).Contains($"epoch={current}"), "…text render carries it");
                Check(JsonWire.RenderDiffRecord(d, 0).Contains($"\"epoch\": \"{current}\""), "…json render carries it");
                var ec = svc.ResolveEffectChain(mgef.FormKey, null, 500);
                Check(ec.Error is null && ec.Epoch == current && Wire.RenderEffectChain(ec, 0).Contains($"epoch={current}"),
                      "effect_chain stamps + renders it");
                var ce = svc.CheckErrors(null, 1000);
                Check(ce.Error is null && ce.Epoch == current, "check_errors stamps the swept build");
                Check(Wire.RenderCheckErrors(ce, 0).Contains($"epoch={current}") && JsonWire.RenderCheckErrors(ce, 0).Contains($"\"epoch\": \"{current}\""),
                      "…both renders carry it");
                var vs = svc.ValidateScripts(null, 1000);
                Check(vs.Error is null && vs.Epoch == current, "validate_scripts stamps the swept build");
                Check(Wire.RenderScriptCheck(vs, 0).Contains($"epoch={current}") && JsonWire.RenderScriptCheck(vs, 0).Contains($"\"epoch\": \"{current}\""),
                      "…both renders carry it");

                // ---- 4: the point of it all — a rebuild between two queries changes the STAMP ----
                Console.WriteLine();
                Console.WriteLine("--- 4: a mid-session rebuild re-stamps — cross-page drift becomes visible ---");
                File.SetLastWriteTimeUtc(ovFile, DateTime.UtcNow.AddMinutes(-30));
                var q2 = svc.CrossQuery("WEAP", null, null, false, null, null, 500);
                Check(q2.Epoch is not null && q2.Epoch != current,
                      $"page 2 after a content change carries a DIFFERENT epoch ({current} → {q2.Epoch}) — the incoherent-assembly tell");
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

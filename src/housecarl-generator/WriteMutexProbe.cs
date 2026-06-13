using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// Write-path mutex + orphan-folder guard (2026-06-12 adversarial hunt F2 + F4): the MCP SDK dispatches tool
/// calls CONCURRENTLY, and the .esp write path had no mutual exclusion — ResolveOutputPath took no gate (its
/// sibling ResolvePatchModFolder locks), UniqueStem was check-then-create TOCTOU, and the staging path under a
/// patch folder is FIXED (.housecarl-tmp\&lt;name&gt;.esp), so two same-default-name writes could allocate the
/// SAME folder and cross-commit (R1's success message shipping R2's bytes). F4 rode the same path: the fresh
/// folder + meta.ini were created BEFORE pre-flight, so a refused write's "NO patch written" left an orphan
/// folder accreting _001/_002 on retry.
///
/// Drives the REAL service-level write path (LoadOrderService.ApplyEdits) against a synthetic MO2 instance in
/// temp (the hierarchy-cache-guard synth pattern + the upsert-guard synthesized master). Arms:
///   CONCURRENT — N pairs of simultaneous fresh default-name writes: every call succeeds, every output path is
///                distinct, and every written file carries ITS OWN edit (no cross-commit). RED pre-fix (same
///                folder allocated; cross-committed bytes).
///   EXTEND     — two simultaneous into= extends of one patch: both edits survive in the final file (the write
///                gate means the second opens what the first committed — no lost update). RED pre-fix.
///   ORPHAN     — a pre-flight-refused fresh write leaves NO folder behind, twice over (no _001 accretion), and
///                a later successful write under that name gets the BASE stem. RED pre-fix (deterministic).
///   DOTTED     — a dotted patch name ("My.Cool.Patch") keeps every segment in the folder + plugin, and an
///                into= naming the .esp-suffixed form strips ONLY the extension, resolving the SAME folder.
///                RED pre-fix (Path.GetFileNameWithoutExtension clipped at the last dot -> "My.Cool"). NOTE N1.
/// </summary>
internal static class WriteMutexProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" write-mutex guard — concurrent writes must not cross-commit");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var root = Path.Combine(Path.GetTempPath(), "hc-write-mutex-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            // ---- synthetic MO2 instance (the established synth-instance pattern) with ONE real master plugin ----
            string instance = Path.Combine(root, "instance");
            string profiles = Path.Combine(instance, "profiles", "Default");
            string mods = Path.Combine(instance, "mods");
            string data = Path.Combine(root, "game", "Data");
            Directory.CreateDirectory(profiles); Directory.CreateDirectory(mods); Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
                "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                + Path.Combine(root, "game").Replace(@"\", @"\\") + ")\r\n");

            var mKey = new ModKey("HcWmxMaster", ModType.Master);
            var modDir = Path.Combine(mods, "MasterMod");
            Directory.CreateDirectory(modDir);
            FormKey weapFk;
            {
                var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
                var w = m.Weapons.AddNew(); w.EditorID = "HcWmxWeap"; w.BasicStats = new WeaponBasicStats { Damage = 10 };
                weapFk = w.FormKey;
                m.BeginWrite.ToPath(Path.Combine(modDir, mKey.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            }
            File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + mKey.FileName + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + mKey.FileName + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+MasterMod\r\n");

            // The service's Rulebook loads from the static CorpusPath — point it at a freshly generated corpus.
            var genDir = Path.Combine(root, "corpus-gen");
            CorpusGenerator.GenerateAll(genDir, Path.Combine(root, "corpus-ref"));
            CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

            string fid = $"{weapFk.ID:X6}:{weapFk.ModKey.FileName}";
            var store = new UserConfigStore(Path.Combine(root, "houseCARL.user.json"));
            using var svc = LoadOrderService.WithInstance(instance, 0, store);
            svc.Stats();                                                       // warm the lazy index once, off the clock

            static BulkOp DamageOp(string fid, int dmg) =>
                new() { Formid = fid, FieldPath = "BasicStats.Damage", Verb = "Set", Value = dmg.ToString() };

            static ushort? ReadDamage(string path, FormKey fk)
            {
                ISkyrimModGetter? ov = null;
                try
                {
                    ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
                    return ov.Weapons.FirstOrDefault(w => w.FormKey == fk)?.BasicStats?.Damage;
                }
                catch { return null; }
                finally { (ov as IDisposable)?.Dispose(); }
            }

            // ---- CONCURRENT: pairs of simultaneous default-name writes — distinct outputs, own bytes ----
            Console.WriteLine("--- 1: concurrent fresh writes (default patch name on both) ---");
            {
                const int pairs = 6;
                var all = new List<(int dmg, WritePatchBuilder.PatchOutcome o)>();
                bool threw = false;
                for (int i = 0; i < pairs; i++)
                {
                    int dmgA = 100 + i * 2, dmgB = 101 + i * 2;
                    using var barrier = new Barrier(2);
                    WritePatchBuilder.PatchOutcome? oA = null, oB = null;
                    var tA = Task.Run(() => { barrier.SignalAndWait(); oA = svc.ApplyEdits(new[] { DamageOp(fid, dmgA) }, null, null); });
                    var tB = Task.Run(() => { barrier.SignalAndWait(); oB = svc.ApplyEdits(new[] { DamageOp(fid, dmgB) }, null, null); });
                    try { Task.WaitAll(tA, tB); } catch { threw = true; }
                    if (oA is null || oB is null) { threw = true; continue; }
                    all.Add((dmgA, oA)); all.Add((dmgB, oB));
                }
                Check(!threw, "no call threw or vanished");
                Check(all.All(x => x.o.Success), $"every concurrent write succeeded ({all.Count(x => x.o.Success)}/{all.Count})");
                var paths = all.Select(x => x.o.OutputPath).ToList();
                Check(paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == paths.Count,
                      "every call got its OWN output path (no shared folder from the UniqueStem race)");
                bool ownBytes = all.All(x => x.o.Success && ReadDamage(x.o.OutputPath, weapFk) == x.dmg);
                Check(ownBytes, "every written file carries ITS OWN edit (no cross-commit through the shared staging path)");
            }

            // ---- EXTEND: two simultaneous extends of ONE patch — no lost update ----
            Console.WriteLine();
            Console.WriteLine("--- 2: concurrent into= extends of one patch ---");
            {
                var seed = svc.ApplyEdits(new[] { DamageOp(fid, 50) }, "HcWmxExtend", null);
                Check(seed.Success, "seed patch created");
                using var barrier = new Barrier(2);
                WritePatchBuilder.PatchOutcome? oA = null, oB = null;
                var tA = Task.Run(() => { barrier.SignalAndWait(); oA = svc.ApplyEdits(new[] { DamageOp(fid, 60) }, null, "HcWmxExtend"); });
                var tB = Task.Run(() => { barrier.SignalAndWait(); oB = svc.ApplyEdits(
                    new[] { new BulkOp { Formid = fid, FieldPath = "BasicStats.Weight", Verb = "Set", Value = "7" } }, null, "HcWmxExtend"); });
                Task.WaitAll(tA, tB);
                bool bothOk = oA is { Success: true } && oB is { Success: true };
                Check(bothOk, "both extends succeeded");
                ushort? dmg = null; float? weight = null;
                if (bothOk)
                {
                    ISkyrimModGetter? ov = null;
                    try
                    {
                        ov = SkyrimMod.CreateFromBinaryOverlay(seed.OutputPath, SkyrimRelease.SkyrimSE);
                        var w = ov.Weapons.FirstOrDefault(x => x.FormKey == weapFk);
                        dmg = w?.BasicStats?.Damage; weight = w?.BasicStats?.Weight;
                    }
                    finally { (ov as IDisposable)?.Dispose(); }
                }
                Check(dmg == 60 && weight == 7, $"BOTH edits survive in the final file (no lost update) — Damage={dmg}, Weight={weight}");
            }

            // ---- ORPHAN: a pre-flight-refused fresh write leaves no folder; no _001 accretion on retry ----
            Console.WriteLine();
            Console.WriteLine("--- 3: refused write leaves no orphan folder (hunt F4) ---");
            {
                var bad = new BulkOp { Formid = fid, FieldPath = "NoSuchFieldAnywhere", Verb = "Set", Value = "1" };
                var r1 = svc.ApplyEdits(new[] { bad }, "HcWmxOrphan", null);
                var r2 = svc.ApplyEdits(new[] { bad }, "HcWmxOrphan", null);
                Check(!r1.Success && !r2.Success, "both bad writes refused");
                Check(r1.Error is not null && r1.Error.Contains("NO patch written", StringComparison.OrdinalIgnoreCase),
                      "refusal says NO patch written");
                bool noOrphans = !Directory.EnumerateDirectories(mods, "houseCARL - HcWmxOrphan*").Any();
                Check(noOrphans, "no orphan folder remains after either refusal (the message is true of the disk)");
                var good = svc.ApplyEdits(new[] { DamageOp(fid, 75) }, "HcWmxOrphan", null);
                Check(good.Success && good.OutputPath.EndsWith(Path.Combine("houseCARL - HcWmxOrphan", "HcWmxOrphan.esp"), StringComparison.OrdinalIgnoreCase),
                      $"a later good write gets the BASE stem, not an accreted _NNN — wrote {Path.GetFileName(good.OutputPath)}");
            }

            // ---- DOTTED: a dotted patch name survives intact; into= strips only the extension (render NOTE N1) ----
            Console.WriteLine();
            Console.WriteLine("--- 4: dotted patch name not clipped at a dot (render NOTE N1) ---");
            {
                var dot = svc.ApplyEdits(new[] { DamageOp(fid, 80) }, "My.Cool.Patch", null);
                Check(dot.Success && dot.OutputPath.EndsWith(
                          Path.Combine("houseCARL - My.Cool.Patch", "My.Cool.Patch.esp"), StringComparison.OrdinalIgnoreCase),
                      $"the dotted stem survives — folder + plugin keep every segment (wrote {Path.GetFileName(dot.OutputPath)})");
                // into= naming the .esp-suffixed form strips ONLY the extension and resolves the SAME dotted folder.
                var ext = svc.ApplyEdits(
                    new[] { new BulkOp { Formid = fid, FieldPath = "BasicStats.Weight", Verb = "Set", Value = "9" } },
                    null, "My.Cool.Patch.esp");
                Check(ext.Success && dot.Success && string.Equals(ext.OutputPath, dot.OutputPath, StringComparison.OrdinalIgnoreCase),
                      $"into='My.Cool.Patch.esp' resolves the SAME folder — extension stripped, inner dots kept (into {Path.GetFileName(ext.OutputPath)})");
            }
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* temp scratch */ } }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "================ ALL PASS ================" : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }
}

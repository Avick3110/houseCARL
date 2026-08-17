using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// into=-extend RESOLVER guard (HCBR-2026-06-23: "in-place into= extend can't find a houseCARL-built plugin once its
/// mod folder is renamed"). The folder-per-patch model created each patch as "houseCARL - &lt;stem&gt;\&lt;stem&gt;.esp",
/// and the old into= branch demanded a 3-way name match: into= == mod-folder suffix == .esp basename. A user who renamed
/// the MO2 mod folder for organization (the .esp basename is FIXED — SPID _DISTR / the CSF JSON / masters bind it) could
/// no longer extend their OWN patch in place. The fix routes BOTH write lanes — the .esp path (ResolveOutputPath) AND the
/// rider/asset path (ResolvePatchModFolder: compile / decompile / bsa_repack / place_asset) — through ONE shared 4-step
/// resolver (ResolveOwnedPatchFolder), decoupling the folder name from the .esp basename WITHOUT relaxing the ownership
/// marker (this is houseCARL's OWN output; the marker still gates every touch, so it opens NO foreign-plugin door — that's
/// the separate, unbuilt in-place lane).
///
/// Drives the REAL service write paths against a synthetic MO2 instance in temp (the WriteMutexProbe synth pattern). Arms:
///   CANONICAL    — into="SeedA" still resolves the unchanged "houseCARL - SeedA\SeedA.esp" (zero regression, no scan).
///   BY-ESP       — after the folder is RENAMED, into=&lt;esp basename&gt; finds the patch by the plugin it holds. RED pre-fix.
///   BY-ESP-EXT   — into="SeedA.esp" (with extension) strips the ext and resolves the same renamed patch.
///   BY-FOLDER    — into=&lt;the renamed folder's name&gt; edits the single .esp inside it, names NEED NOT match. RED pre-fix.
///   ACCUMULATED  — both .esp resolution paths extended the SAME patch file (the edits accumulate; not a fresh patch).
///   RIDER        — the SIBLING lane (ResolvePatchModFolder, used by compile/decompile/bsa/place_asset): a renamed patch
///                  folder resolves by the .esp it holds AND by its new name, returning the reused folder. RED pre-fix
///                  (the rider branch carried the same untouched 3-way "folder not found" coupling).
///   AMBIGUOUS    — two OWNED folders carry the same &lt;esp&gt; → refuse loud, name BOTH + the into="&lt;folder&gt;"
///                  disambiguator (Q3 — never guess). Then into=&lt;a folder name&gt; picks one unambiguously.
///   MULTI-PLUGIN — into=&lt;a folder holding 2+ plugins&gt; (no &lt;stem&gt;.esp to single out) refuses, naming each plugin.
///   NOT-FOUND    — into= a name nothing holds/answers to → refuse, naming both places searched + "create it fresh".
///   REMEDY       — that refusal's fresh-write escape NAMES patch=&lt;the guessed name&gt; for the callers whose patch=
///                  really does name a fresh patch (#343: the bare "omit into=" it used to offer is the call that
///                  yields a generically-named Patch.esp), and the named call is then MADE to prove the sentence
///                  true rather than read. The two arms that keep it honest are negative: the RIDER lane does NOT
///                  get it (on bsa_repack patch= binds to archive_name — the .bsa), and neither does the REMOVAL
///                  lane, which shares the record branch but whose patch= names an EXISTING patch.
///   FOREIGN      — a "houseCARL - X" folder with NO marker is still REFUSED (originals untouched, Q3) and left byte-intact.
///   ORIGINALS    — the master plugin is byte-untouched throughout (extends only ever wrote the patch folder).
/// </summary>
internal static class ExtendResolveProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" into=-extend resolver guard — find a renamed houseCARL patch");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var root = Path.Combine(Path.GetTempPath(), "hc-extend-resolve-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            // ---- synthetic MO2 instance with ONE real master plugin (the established synth-instance pattern) ----
            string instance = Path.Combine(root, "instance");
            string profiles = Path.Combine(instance, "profiles", "Default");
            string mods = Path.Combine(instance, "mods");
            Directory.CreateDirectory(profiles); Directory.CreateDirectory(mods);
            Directory.CreateDirectory(Path.Combine(root, "game", "Data"));
            File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
                "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                + Path.Combine(root, "game").Replace(@"\", @"\\") + ")\r\n");

            var mKey = new ModKey("HcExtMaster", ModType.Master);
            var masterPath = Path.Combine(mods, "MasterMod", mKey.FileName.String);
            Directory.CreateDirectory(Path.GetDirectoryName(masterPath)!);
            FormKey weapFk;
            {
                var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
                var w = m.Weapons.AddNew(); w.EditorID = "HcExtWeap"; w.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1 };
                weapFk = w.FormKey;
                m.BeginWrite.ToPath(masterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            }
            File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + mKey.FileName + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + mKey.FileName + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+MasterMod\r\n");
            byte[] masterBytesAtStart = File.ReadAllBytes(masterPath);

            var genDir = Path.Combine(root, "corpus-gen");
            CorpusGenerator.GenerateAll(genDir, Path.Combine(root, "corpus-ref"));
            CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

            string fid = $"{weapFk.ID:X6}:{weapFk.ModKey.FileName}";
            var store = new UserConfigStore(Path.Combine(root, "houseCARL.user.json"));
            using var svc = LoadOrderService.WithInstance(instance, 0, store);
            svc.Stats();                                                       // warm the lazy index once, off the clock

            static BulkOp Set(string fid, string path, string val) =>
                new() { Formid = fid, FieldPath = path, Verb = "Set", Value = val };
            BulkOp Dmg(int v) => Set(fid, "BasicStats.Damage", v.ToString());
            BulkOp Wgt(int v) => Set(fid, "BasicStats.Weight", v.ToString());

            (ushort? dmg, float? wgt) ReadWeap(string espPath)
            {
                ISkyrimModGetter? ov = null;
                try { ov = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE);
                      var w = ov.Weapons.FirstOrDefault(x => x.FormKey == weapFk); return (w?.BasicStats?.Damage, w?.BasicStats?.Weight); }
                catch { return (null, null); }
                finally { (ov as IDisposable)?.Dispose(); }
            }
            static bool Ends(string path, params string[] parts) =>
                path.EndsWith(Path.Combine(parts), StringComparison.OrdinalIgnoreCase);
            void MarkOwned(string folder, string plugin) =>
                File.WriteAllText(Path.Combine(folder, "meta.ini"), $"{HousecarlOwnerMeta.Section}\r\ngenerated=true\r\nplugin={plugin}\r\n");

            // ---- SEED: create the patch fresh — "houseCARL - SeedA\SeedA.esp" carrying a Damage=50 override ----
            var seed = svc.ApplyEdits(new[] { Dmg(50) }, "SeedA", null);
            Check(seed.Success && Ends(seed.OutputPath, "houseCARL - SeedA", "SeedA.esp"), "seed patch created at houseCARL - SeedA\\SeedA.esp");

            // ---- 1: CANONICAL — into= still resolves the unchanged folder (no scan, zero regression) ----
            Console.WriteLine("--- 1: canonical into= (unchanged folder) ---");
            {
                var r = svc.ApplyEdits(new[] { Wgt(5) }, null, "SeedA");
                Check(r.Success && Ends(r.OutputPath, "houseCARL - SeedA", "SeedA.esp"),
                      $"into=\"SeedA\" resolves the canonical folder ({Path.GetFileName(Path.GetDirectoryName(r.OutputPath) ?? "")})");
            }

            // ---- RENAME the MO2 mod folder; the .esp basename stays SeedA.esp (SPID/CSF/masters bind it) ----
            string seedFolder = Path.Combine(mods, "houseCARL - SeedA");
            string renamedFolder = Path.Combine(mods, "houseCARL - SeedA Renamed");
            Directory.Move(seedFolder, renamedFolder);

            // ---- 2: BY-ESP — find the renamed patch by the plugin it holds (RED pre-fix) ----
            Console.WriteLine();
            Console.WriteLine("--- 2: into=<esp basename> after the folder was renamed ---");
            {
                var r = svc.ApplyEdits(new[] { Wgt(7) }, null, "SeedA");
                Check(r.Success && Ends(r.OutputPath, "houseCARL - SeedA Renamed", "SeedA.esp"),
                      $"into=\"SeedA\" finds the RENAMED folder by the .esp it holds (wrote into {Path.GetFileName(Path.GetDirectoryName(r.OutputPath) ?? "")})");

                // into="SeedA.esp" — the .esp extension is stripped and resolves the SAME renamed patch
                var ext = svc.ApplyEdits(new[] { Wgt(7) }, null, "SeedA.esp");
                Check(ext.Success && Ends(ext.OutputPath, "houseCARL - SeedA Renamed", "SeedA.esp"),
                      "into=\"SeedA.esp\" (with extension) strips the ext and resolves the same renamed patch");
            }

            // ---- 3: BY-FOLDER — name the renamed folder; the .esp inside need not match (RED pre-fix) ----
            Console.WriteLine();
            Console.WriteLine("--- 3: into=<the renamed folder's name> (folder & esp names differ) ---");
            {
                var r = svc.ApplyEdits(new[] { Dmg(88) }, null, "SeedA Renamed");
                Check(r.Success && Ends(r.OutputPath, "houseCARL - SeedA Renamed", "SeedA.esp"),
                      $"into=\"SeedA Renamed\" (folder name) edits the single .esp inside it ({Path.GetFileName(r.OutputPath)})");
            }

            // ---- 4: ACCUMULATED — both paths grew the SAME patch (not a fresh one) ----
            Console.WriteLine();
            Console.WriteLine("--- 4: both resolutions extended the same patch ---");
            {
                var (dmg, wgt) = ReadWeap(Path.Combine(renamedFolder, "SeedA.esp"));
                Check(dmg == 88 && wgt == 7, $"the renamed patch carries BOTH extends — Damage={dmg} (by-folder), Weight={wgt} (by-esp)");
            }

            // ---- 5: RIDER lane — the SAME shared resolver serves compile/decompile/bsa/place_asset (RED pre-fix) ----
            Console.WriteLine();
            Console.WriteLine("--- 5: rider/asset lane (ResolvePatchModFolder) resolves the renamed patch too ---");
            {
                var byEsp = svc.ResolvePatchModFolder(null, "SeedA", "houseCARL_Archive");
                Check(!byEsp.CreatedFresh && Ends(byEsp.ModFolder, "houseCARL - SeedA Renamed"),
                      $"rider into=\"SeedA\" finds the renamed folder by the .esp it holds ({Path.GetFileName(byEsp.ModFolder)}, reused)");
                var byFolder = svc.ResolvePatchModFolder(null, "SeedA Renamed", "houseCARL_Archive");
                Check(!byFolder.CreatedFresh && Ends(byFolder.ModFolder, "houseCARL - SeedA Renamed"),
                      "rider into=\"SeedA Renamed\" (folder name) resolves the same reused folder");
            }

            // ---- 6: AMBIGUOUS — a second OWNED folder carrying the same esp → refuse loud + name both ----
            Console.WriteLine();
            Console.WriteLine("--- 6: two owned folders carry the same .esp → ambiguous refusal ---");
            string dupFolder = Path.Combine(mods, "houseCARL - DupHome");
            {
                Directory.CreateDirectory(dupFolder);
                MarkOwned(dupFolder, "SeedA.esp");
                File.Copy(Path.Combine(renamedFolder, "SeedA.esp"), Path.Combine(dupFolder, "SeedA.esp"));

                var r = svc.ApplyEdits(new[] { Wgt(1) }, null, "SeedA");
                bool named = r.Error is not null
                    && r.Error.Contains("ambiguous", StringComparison.OrdinalIgnoreCase)
                    && r.Error.Contains("houseCARL - SeedA Renamed", StringComparison.Ordinal)
                    && r.Error.Contains("houseCARL - DupHome", StringComparison.Ordinal)
                    && r.Error.Contains("into=", StringComparison.Ordinal);
                Check(!r.Success && named, "into=\"SeedA\" refuses (ambiguous), naming BOTH folders + the into=<folder> disambiguator");

                var pick = svc.ApplyEdits(new[] { Wgt(3) }, null, "DupHome");
                Check(pick.Success && Ends(pick.OutputPath, "houseCARL - DupHome", "SeedA.esp"),
                      "into=\"DupHome\" (folder name) picks ONE despite the shared .esp basename");
            }

            // ---- 7: MULTI-PLUGIN — into=<folder holding 2+ plugins, none == stem> refuses, naming each ----
            Console.WriteLine();
            Console.WriteLine("--- 7: into=<folder with several plugins> refuses, naming them ---");
            {
                string twoEsp = Path.Combine(mods, "houseCARL - TwoEsp");
                Directory.CreateDirectory(twoEsp);
                MarkOwned(twoEsp, "Alpha.esp");
                File.WriteAllText(Path.Combine(twoEsp, "Alpha.esp"), "x");
                File.WriteAllText(Path.Combine(twoEsp, "Beta.esp"), "y");

                var r = svc.ApplyEdits(new[] { Wgt(1) }, null, "TwoEsp");
                bool named = r.Error is not null
                    && r.Error.Contains("2 plugins", StringComparison.Ordinal)
                    && r.Error.Contains("Alpha.esp", StringComparison.Ordinal)
                    && r.Error.Contains("Beta.esp", StringComparison.Ordinal);
                Check(!r.Success && named, "into=\"TwoEsp\" (folder holds Alpha.esp + Beta.esp) refuses, naming both plugins");
            }

            // ---- 8: NOT-FOUND — refuse, naming both places searched + the fresh-write escape ----
            Console.WriteLine();
            Console.WriteLine("--- 8: into= a name nothing answers to → named refusal ---");
            {
                var r = svc.ApplyEdits(new[] { Wgt(1) }, null, "GhostPatch");
                bool named = r.Error is not null
                    && r.Error.Contains("GhostPatch.esp", StringComparison.Ordinal)
                    && r.Error.Contains("houseCARL - GhostPatch", StringComparison.Ordinal)
                    && r.Error.Contains("create it fresh", StringComparison.OrdinalIgnoreCase);
                Check(!r.Success && named, "into=\"GhostPatch\" refuses, naming the .esp + the folder searched + 'create it fresh'");

                // #343: the fresh-write escape must NAME the parameter that gives the new patch a name, with the
                // caller's own guessed name already in it — the bare "omit into=" it used to offer is precisely the
                // call that produces a generically-named Patch.esp. Both halves of the sentence are asserted because
                // both were measured before it was written: patch="GhostPatch" writes GhostPatch.esp (arm 8b below
                // re-proves it end to end), and omitting both lanes writes Patch.esp.
                bool remedy = r.Error is not null
                    && r.Error.Contains("pass patch=\"GhostPatch\"", StringComparison.Ordinal)
                    && r.Error.Contains("houseCARL names it \"Patch\"", StringComparison.Ordinal);
                Check(remedy, "…and the remedy names patch=\"GhostPatch\" + what omitting it costs (#343)");
            }

            // ---- 8b: the remedy is TRUE — following it produces the patch the caller asked for ----
            //      A remedy is a claim about what a call does, so it is checked by making the call, not by reading
            //      the sentence (#343 is itself the class where a plausible remedy sent callers the wrong way).
            Console.WriteLine();
            Console.WriteLine("--- 8b: following the remedy produces GhostPatch.esp, not Patch.esp ---");
            {
                var r = svc.ApplyEdits(new[] { Wgt(2) }, "GhostPatch", null);
                Check(r.Success && Ends(r.OutputPath, "houseCARL - GhostPatch", "GhostPatch.esp"),
                      $"patch=\"GhostPatch\" writes the patch under that name ({Path.GetFileName(r.OutputPath)})");
            }

            // ---- 8c: the RIDER lane keeps the older tail — it must NOT name patch= ----
            //      Measured, not assumed: patch= is the new patch's name on the record-lane write tools, but on
            //      housecarl_bsa_repack — a rider — it binds to archive_name (the .bsa), because that tool declares
            //      both spellings and §5.3 routes patch= to the artifact. Telling a rider caller to pass patch=
            //      would rename their archive and leave the mod folder defaulted, so the naming sentence does not
            //      reach this lane. This arm is what makes that a decision rather than an oversight.
            Console.WriteLine();
            Console.WriteLine("--- 8c: the rider lane's refusal does not name patch= ---");
            {
                string riderErr = "";
                try { svc.ResolvePatchModFolder(null, "GhostRider", "houseCARL_Archive"); }
                catch (InvalidOperationException ex) { riderErr = ex.Message; }
                Check(riderErr.Contains("GhostRider.esp", StringComparison.Ordinal)
                      && riderErr.Contains("create it fresh", StringComparison.OrdinalIgnoreCase),
                      "the rider lane still refuses, naming the .esp searched + 'create it fresh'");
                Check(!riderErr.Contains("patch=", StringComparison.Ordinal),
                      "…and does NOT name patch= (on bsa_repack it names the .bsa, not the patch)");
            }

            // ---- 8d: the REMOVAL lane must not get the naming sentence either ----
            //      Removal reaches the same refusal through ResolveOutputPath — the SAME branch arm 8 covers — so the
            //      lane bit does not separate them; only the caller's own patchNamesFresh does. For housecarl_remove
            //      the naming sentence would be actively wrong twice over: patch= there names an EXISTING patch (it
            //      IS the into= slot), so "pass patch=<the name you just passed>" re-issues the failing call, and
            //      removal cannot create a patch at all, so the cost clause would be false as well. Withholding it is
            //      the fix for a defect this branch briefly shipped; this arm is what stops it coming back.
            Console.WriteLine();
            Console.WriteLine("--- 8d: the removal lane's refusal does not name patch= ---");
            {
                var r = svc.RemoveRecords(new[] { fid }, "GhostRemove");
                Check(!r.Success && r.Error is not null && r.Error.Contains("GhostRemove.esp", StringComparison.Ordinal),
                      "housecarl_remove into a patch that does not exist still refuses, naming the .esp searched");
                Check(r.Error is not null && !r.Error.Contains("patch=", StringComparison.Ordinal),
                      "…and does NOT name patch= (there it names an EXISTING patch — the remedy would loop)");
            }

            // ---- 9: FOREIGN — an un-owned "houseCARL - X" folder stays REFUSED + byte-untouched (Q3) ----
            Console.WriteLine();
            Console.WriteLine("--- 9: an un-owned folder is still refused (originals untouched) ---");
            {
                string foreignFolder = Path.Combine(mods, "houseCARL - Foreign");
                Directory.CreateDirectory(foreignFolder);                                  // NO meta.ini marker → not owned
                string foreignEsp = Path.Combine(foreignFolder, "Foreign.esp");
                File.WriteAllText(foreignEsp, "not a real plugin — a user file houseCARL must never touch");
                byte[] foreignBefore = File.ReadAllBytes(foreignEsp);

                var r = svc.ApplyEdits(new[] { Wgt(1) }, null, "Foreign");
                bool refused = r.Error is not null
                    && r.Error.Contains("NOT created by houseCARL", StringComparison.Ordinal)
                    && r.Error.Contains("originals untouched", StringComparison.OrdinalIgnoreCase);
                Check(!r.Success && refused, "into=\"Foreign\" (un-owned folder) is REFUSED — houseCARL won't edit a folder it doesn't own");
                Check(File.ReadAllBytes(foreignEsp).SequenceEqual(foreignBefore), "the un-owned plugin is byte-untouched after the refusal");

                // the rider lane refuses the un-owned folder too (shared resolver — same gate)
                bool riderRefused = false;
                try { svc.ResolvePatchModFolder(null, "Foreign", "houseCARL_Archive"); }
                catch (InvalidOperationException ex) { riderRefused = ex.Message.Contains("NOT created by houseCARL", StringComparison.Ordinal); }
                Check(riderRefused, "the RIDER lane also refuses the un-owned folder (same ownership gate, no foreign-plugin door)");
            }

            // ---- 10: ORIGINALS — the master plugin never moved a byte (extends only wrote the patch folder) ----
            Console.WriteLine();
            Console.WriteLine("--- 10: the master plugin is byte-untouched throughout ---");
            Check(File.ReadAllBytes(masterPath).SequenceEqual(masterBytesAtStart),
                  "the master plugin is byte-identical to its pre-write state (every extend wrote only the patch)");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* temp scratch */ } }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "================ ALL PASS ================" : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }
}

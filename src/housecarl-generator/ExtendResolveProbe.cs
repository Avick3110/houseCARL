using System.Text.Json;
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
///   NOT-FOUND    — into= a name nothing holds/answers to → refuse in ONE sentence, naming both places searched, then
///                  the nearest owned patches as into= spellings and the lane's own fresh-patch parameter.
///   REMEDY       — that refusal's fresh-write escape NAMES patch=&lt;the guessed name&gt; for the callers whose patch=
///                  really does name a fresh patch (#343: the bare "omit into=" it used to offer is the call that
///                  yields a generically-named Patch.esp), and the named call is then MADE to prove the sentence
///                  true rather than read. The two arms that keep it honest are negative: the RIDER lane does NOT
///                  get it (on bsa_repack patch= binds to archive_name — the .bsa), and neither does the REMOVAL
///                  lane, which shares the record branch but whose patch= names an EXISTING patch.
///   REMOVE-TAIL  — that removal lane offers no create route at all (#356: it used to inherit "omit into= to create
///                  it fresh", which removal itself refuses), states why, and names the one lane it does have. Its
///                  spelling is the calling TOOL'"'"'s, not the service'"'"'s, and the arms call the tools to see it.
///   FOREIGN      — a "houseCARL - X" folder with NO marker is still REFUSED (originals untouched, Q3) and left byte-intact.
///   ORIGINALS    — the master plugin is byte-untouched throughout (extends only ever wrote the patch folder).
/// </summary>
internal static class ExtendResolveProbe
{
    [CiProbe("extend-resolve-guard")]
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" into=-extend resolver guard — find a renamed houseCARL patch");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }
        // Aaron's ruling (2026-09-06): each extend refusal is ONE sentence. Read as one terminating period and no
        // sentence break inside it — a plugin basename's dot is followed by a letter, never by a space.
        static bool OneSentence(string s) =>
            s.EndsWith('.') && !s.AsSpan(0, s.Length - 1).Contains(". ", StringComparison.Ordinal);
        // The candidates the sentence offers, in the order it names them, for the ordering and cap arms.
        static List<string> Candidates(string s) =>
            System.Text.RegularExpressions.Regex.Matches(s, "into=\"[^\"]*\"").Select(m => m.Value).ToList();

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
                var byEsp = svc.ResolvePatchModFolder(null, "SeedA", "houseCARL_Archive", BsaTools.RepackNaming);
                Check(!byEsp.CreatedFresh && Ends(byEsp.ModFolder, "houseCARL - SeedA Renamed"),
                      $"rider into=\"SeedA\" finds the renamed folder by the .esp it holds ({Path.GetFileName(byEsp.ModFolder)}, reused)");
                var byFolder = svc.ResolvePatchModFolder(null, "SeedA Renamed", "houseCARL_Archive", BsaTools.RepackNaming);
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
                // Real plugins, not placeholder bytes: arm 9c FOLLOWS the spelling this folder is rendered as, and a
                // remedy is checked by making the call.
                foreach (var n in new[] { "Alpha", "Beta" })
                    new SkyrimMod(new ModKey(n, ModType.Plugin), SkyrimRelease.SkyrimSE)
                        .BeginWrite.ToPath(Path.Combine(twoEsp, n + ".esp")).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

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
                    && r.Error.Contains("houseCARL - GhostPatch", StringComparison.Ordinal);
                Check(!r.Success && named, "into=\"GhostPatch\" refuses, naming the .esp + the folder searched");

                // #343: the fresh-write escape must NAME the parameter that gives the new patch a name, with the
                // caller's own guessed name already in it — the bare "omit into=" it used to offer is precisely the
                // call that produces a generically-named Patch.esp. Measured before it was written: patch="GhostPatch"
                // writes GhostPatch.esp (arm 8b below re-proves it end to end).
                Check(r.Error is not null && r.Error.Contains("patch=\"GhostPatch\" for a fresh patch", StringComparison.Ordinal),
                      "…and the remedy names patch=\"GhostPatch\" (#343)");

                // The qualifier is load-bearing, not padding: it is the only thing keeping the sentence true in the
                // collision case arm 8b2 exercises, where the fresh write auto-suffixes off an active plugin.
                Check(r.Error is not null && r.Error.Contains("auto-suffixed if that name is taken", StringComparison.Ordinal),
                      "…and qualifies the name it offers with the auto-suffix, rather than promising a filename");

                // Aaron's ruling (2026-09-06): ONE sentence — what went wrong, then what to try, with the candidates
                // named inside it. Every arm below pins the same three properties, so a clause creeping back in on
                // any one lane reddens: one terminating period, the "try" remedy, and no in-place lane.
                Check(OneSentence(r.Error ?? ""), $"…and the whole refusal is ONE sentence ({r.Error})");
                Check(r.Error is not null && r.Error.Contains("; try ", StringComparison.Ordinal)
                      && !r.Error.Contains("in_place", StringComparison.Ordinal),
                      "…that names what to try and never the in-place lane (that lane lives on the tool that declares it)");
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

            // ---- 8b2: the COLLISION case — the one the remedy used to mispromise ----
            //      An ACTIVE plugin whose mod folder is named something else is exactly what a caller guesses into=
            //      from, because the plugin name is what they saw in their load order. Nothing houseCARL owns answers
            //      to it, so the not-found remedy fires — and the fresh write then auto-suffixes off the active
            //      plugin, so a remedy promising the guessed name "under that name" was false precisely here. The
            //      sentence now offers the name without promising the filename; this arm holds it to that.
            Console.WriteLine();
            Console.WriteLine("--- 8b2: an active plugin of the same name → the remedy must not promise the filename ---");
            {
                var oddDir = Path.Combine(mods, "OddlyNamedMod");
                Directory.CreateDirectory(oddDir);
                new SkyrimMod(new ModKey("GhostActive", ModType.Plugin), SkyrimRelease.SkyrimSE)
                    .BeginWrite.ToPath(Path.Combine(oddDir, "GhostActive.esp"))
                    .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
                File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + mKey.FileName + "\r\nGhostActive.esp\r\n");
                File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + mKey.FileName + "\r\n*GhostActive.esp\r\n");
                File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+OddlyNamedMod\r\n+MasterMod\r\n");
                svc.Stats();                                                   // let the resolver see the changed order

                var r = svc.ApplyEdits(new[] { Wgt(4) }, null, "GhostActive");
                Check(!r.Success && r.Error is not null
                      && r.Error.Contains("patch=\"GhostActive\" for a fresh patch", StringComparison.Ordinal),
                      "into=\"GhostActive\" (an active plugin, foreign folder) still refuses with the naming remedy");

                var followed = svc.ApplyEdits(new[] { Wgt(4) }, "GhostActive", null);
                Check(followed.Success && Ends(followed.OutputPath, "houseCARL - GhostActive_001", "GhostActive_001.esp"),
                      $"…and following it auto-suffixes off the active plugin ({Path.GetFileName(followed.OutputPath)})");

                // The negative is pinned to what the CURRENT sentence must not contain, not to a phrase only an old
                // one did: the refusal must not name the file the write actually produces. Its predecessor pinned
                // the superseded wording, so it reddened for exactly one sabotage — restoring that wording — and
                // would have stayed green on any new way of predicting the filename.
                //
                // Its reach, stated rather than implied: it catches a wording that echoes the name that really gets
                // written (the suffixed stem). It does NOT catch one predicting the UNSUFFIXED name, because the
                // refusal legitimately quotes "<stem>.esp" already as the plugin it searched for — no string test
                // can tell that occurrence from a prediction. The property's real load is carried by the positive
                // pin above plus the `followed` call, which make the collision observable; this is a second net,
                // not the proof.
                var written = Path.GetFileNameWithoutExtension(followed.OutputPath);      // "GhostActive_001"
                Check(r.Error is not null && !r.Error.Contains(written, StringComparison.OrdinalIgnoreCase),
                      $"…and the refusal never names the file the write produces ('{written}') — it promises no filename");
            }

            // ---- 8c: the RIDER lane names ITS OWN folder parameter and default (#357) ----
            //      Measured, not assumed: patch= is the new patch's name on the record-lane write tools, but on
            //      housecarl_bsa_repack — a rider — it binds to archive_name (the .bsa), because that tool declares
            //      both spellings and §5.3 routes patch= to the artifact. So the rider sentence is the LANE's, not a
            //      shared one: it names the parameter that tool actually declares, its own default folder name, and
            //      on this tool the correction that a bare patch= names the archive. The naming comes from the
            //      shipped tool's own constant, so an arm here cannot pass against a sentence the tool never uses.
            Console.WriteLine();
            Console.WriteLine("--- 8c: the rider lane names patch_name= and its own default, not patch= ---");
            {
                string riderErr = "";
                try { svc.ResolvePatchModFolder(null, "GhostRider", "houseCARL_Archive", BsaTools.RepackNaming); }
                catch (InvalidOperationException ex) { riderErr = ex.Message; }
                Check(riderErr.Contains("GhostRider.esp", StringComparison.Ordinal) && OneSentence(riderErr),
                      $"the rider lane still refuses in ONE sentence, naming the .esp searched ({riderErr})");
                // The caveat is a standalone sentence spliced mid-clause, so it reads as a clause: no leading capital
                // after the semicolon, and the acronym inside it is left alone.
                Check(riderErr.Contains("; on this tool a bare patch= names the ARCHIVE", StringComparison.Ordinal),
                      $"…and its spliced caveat reads as a clause, not a capitalised sentence inside one ({riderErr})");
                Check(riderErr.Contains("patch_name=\"GhostRider\" for a fresh folder", StringComparison.Ordinal)
                      && riderErr.Contains("auto-suffixed if that name is taken", StringComparison.Ordinal),
                      "…and hands back patch_name= with the caller's own guessed name in it, qualified with the auto-suffix");
                Check(riderErr.Contains("a bare patch= names the ARCHIVE", StringComparison.Ordinal),
                      "…and corrects patch= on the one tool where it binds to the .bsa instead");
                Check(!riderErr.Contains("patch=\"", StringComparison.Ordinal),
                      "…and never offers patch= itself on this lane, where it would rename the caller's archive");

                // The remedy has to say to DROP into=, not merely to add the parameter: this lane takes the extend
                // branch on ANY non-blank into= and never reads patch_name=, and no rider tool declares an
                // into=/patch_name= exclusivity check to intercept a caller who adds one to the other.
                Check(riderErr.Contains("dropping into= and passing patch_name=\"GhostRider\"", StringComparison.Ordinal),
                      "…and says to DROP into=, so following it is not a loop");
                string bothErr = "";
                try { svc.ResolvePatchModFolder("GhostRider", "GhostRider", "houseCARL_Archive", BsaTools.RepackNaming); }
                catch (InvalidOperationException ex) { bothErr = ex.Message; }
                Check(bothErr == riderErr,
                      "…which is the point: keeping into= and adding patch_name= returns the IDENTICAL refusal");

                // FOLLOW it, the way 8b follows its own: dropping into= makes the folder the sentence promised.
                var madeFresh = svc.ResolvePatchModFolder("GhostRider", null, "houseCARL_Archive", BsaTools.RepackNaming);
                Check(Path.GetFileName(madeFresh.ModFolder) == "houseCARL - GhostRider" && madeFresh.CreatedFresh,
                      $"…and following it creates that folder fresh ({madeFresh.ModFolder})");
                Directory.Delete(madeFresh.ModFolder, recursive: true);        // later arms count the owned inventory
                svc.Stats();
            }

            // ---- 8c2: a rider that names NO folder parameter still offers nothing it cannot back ----
            //      bsa_extract, the compact folder cut and the merge folder cut pass into: null, so this arm is
            //      unreachable for them — but they pass null naming rather than borrowing a sibling's sentence, and
            //      null must degrade to the weakest true remedy rather than to a wrong one.
            Console.WriteLine();
            Console.WriteLine("--- 8c2: a rider with no naming falls back to the owned-patch list ---");
            {
                string bareErr = "";
                try { svc.ResolvePatchModFolder(null, "GhostRider", "houseCARL_Extract", naming: null); }
                catch (InvalidOperationException ex) { bareErr = ex.Message; }
                Check(bareErr.Contains("GhostRider.esp", StringComparison.Ordinal)
                      && !bareErr.Contains("patch_name=", StringComparison.Ordinal)
                      && !bareErr.Contains("patch=", StringComparison.Ordinal),
                      "a null naming refuses without offering a parameter it was never told about");
                // #380: the always-true remedy used to be "Check the name.", which named no candidate at all. It is
                // now the nearest owned patches — the one thing true for every caller that also tells them something.
                Check(bareErr.Contains("; try into=\"", StringComparison.Ordinal) && OneSentence(bareErr),
                      $"…and names the owned patches to try, as into= spellings, in one sentence ({bareErr})");
            }

            // ---- 8d: the REMOVAL lane must not get the naming sentence either ----
            //      Removal reaches the same refusal through ResolveOutputPath — the SAME branch arm 8 covers — so the
            //      lane bit does not separate them; only the caller's own FreshPatchRemedy does. A removal edits an
            //      artifact that already exists, so it cannot create a patch and the cost clause is false whichever
            //      spelling the tool gives the lane: 2.0's housecarl_remove calls it into=, while 1.x remove_record
            //      calls it patch= and means the EXISTING patch — there "pass patch=<the name you just passed>" would
            //      re-issue the very call that failed. Withholding it is the fix for a defect this branch briefly
            //      shipped; this arm is what stops it coming back.
            Console.WriteLine();
            Console.WriteLine("--- 8d: the removal lane's refusal does not name patch= ---");
            {
                var r = svc.RemoveRecords(new[] { fid }, "GhostRemove");
                Check(!r.Success && r.Error is not null && r.Error.Contains("GhostRemove.esp", StringComparison.Ordinal),
                      "housecarl_remove into a patch that does not exist still refuses, naming the .esp searched");
                Check(r.Error is not null && !r.Error.Contains("patch=", StringComparison.Ordinal),
                      "…and does NOT name patch= (a removal cannot create a patch — the remedy would be false)");

                // #356: the tail this lane USED to inherit. It offered "Omit into= to create it fresh" — and omitting
                // the lane is refused with "patch is required", so the sentence handed callers a second refusal. The
                // shared default no longer claims a create path at all, and this is the arm that keeps removal off it:
                // a lane that starts stating CreatedByOmittingInto, or a default that starts assuming one, reddens here.
                Check(r.Error is not null && !r.Error.Contains("create it fresh", StringComparison.OrdinalIgnoreCase),
                      "…and no longer offers 'create it fresh' — the remedy this lane could not perform (#356)");
                Check(r.Error is not null && r.Error.Contains("; try into=\"", StringComparison.Ordinal),
                      "…it falls back to the always-true default remedy, which is now the owned candidates (#380)");
                Check(r.Error is not null && r.Error.Contains(WriteSentences.RemoveNoFreshPatch, StringComparison.Ordinal),
                      "…and the LANE states why there is no create route here (removal needs a patch that already carries it)");
                // One sentence, and no in-place clause on it: this lane's in-place spelling is the TOOL's, and it
                // rides the missing-patch= refusal rather than this one (Aaron, 2026-09-06).
                Check(r.Error is not null && OneSentence(r.Error) && !r.Error.Contains("in_place", StringComparison.Ordinal),
                      $"…and the whole refusal is ONE sentence naming no in-place lane ({r.Error})");

                // remove's OTHER no-patch refusal renders the caller's own spelling on the same terms: a direct
                // service call hands none, so it names none.
                var bare = svc.RemoveRecords(new[] { fid }, null);
                Check(!bare.Success && bare.Error is not null
                      && bare.Error.Contains("patch is required", StringComparison.Ordinal)
                      && !bare.Error.Contains("in_place", StringComparison.Ordinal),
                      "…and the missing-patch= refusal names no spelling either when the caller hands none");
            }

            // ---- 8d3: the sentence is read at the TOOL altitude, and the tool spells its OWN lane ----------------
            //      The service hands the lane spelling down rather than choosing it, so the claim belongs where a
            //      caller reads it. The 1.x half of this pair (remove_record's target= + in_place=true) went with
            //      the tool at the demolition catch-up (#468) — SPEC §5.2(1) leaves that spelling nothing to name.
            Console.WriteLine();
            Console.WriteLine("--- 8d3: the remove TOOL's refusal names the lane IT declares ---");
            {
                // The extend refusal is one sentence about the extend that failed: no in-place clause rides it at
                // TOOL altitude either, even though this tool has a spelling to hand down (Aaron, 2026-09-06).
                var modern = RemoveTools.Remove(svc, new[] { fid }, into: "GhostRemove");
                Check(!modern.Contains("in_place", StringComparison.Ordinal) && !modern.Contains("target=", StringComparison.Ordinal),
                      "housecarl_remove's not-found refusal names no in-place lane, and never target=");
                Check(modern.Contains("; try into=\"", StringComparison.Ordinal),
                      "…and offers the owned candidates instead, which touch nobody else's file");

                // The 2.0 tool refuses first, in its own spelling. Kept as a statement about the TOOL — it is
                // no longer what makes anything in the service correct.
                var noLane = RemoveTools.Remove(svc, new[] { fid });
                Check(noLane.Contains("no lane named", StringComparison.Ordinal)
                      && !noLane.Contains("patch is required", StringComparison.Ordinal),
                      "housecarl_remove answers a lane-less call itself, before the service's own arm");
            }

            // ---- 8d2: the in-place lane the extend refusal no longer names still WORKS ---------------------------
            //      The refusal is one sentence about the extend that failed, so it stops at the candidates; the lane
            //      stays discoverable on housecarl_remove itself, which is only worth saying if the lane is real.
            //      Its own throwaway active plugin, never the master — arm 10 asserts that file is byte-untouched,
            //      and an in-place removal would be exactly the write that falsifies it.
            Console.WriteLine();
            Console.WriteLine("--- 8d2: in_place=\"<plugin filename>\" on housecarl_remove removes the record ---");
            {
                var ipDir = Path.Combine(mods, "RemoveHereMod");
                Directory.CreateDirectory(ipDir);
                var ipPath = Path.Combine(ipDir, "RemoveHere.esp");
                FormKey ipFk;
                {
                    var m = new SkyrimMod(new ModKey("RemoveHere", ModType.Plugin), SkyrimRelease.SkyrimSE);
                    var w = m.Weapons.AddNew(); w.EditorID = "HcRemoveMe";
                    w.BasicStats = new WeaponBasicStats { Damage = 3, Weight = 1 };
                    ipFk = w.FormKey;
                    m.BeginWrite.ToPath(ipPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
                }
                File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + mKey.FileName + "\r\nGhostActive.esp\r\nRemoveHere.esp\r\n");
                File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + mKey.FileName + "\r\n*GhostActive.esp\r\n*RemoveHere.esp\r\n");
                File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+RemoveHereMod\r\n+OddlyNamedMod\r\n+MasterMod\r\n");
                svc.Stats();                                                   // let the resolver see the changed order

                string ipFid = $"{ipFk.ID:X6}:RemoveHere.esp";
                // Driven through the 2.0 TOOL, not the service, since the in-place spelling is the tool's own.
                var refused = RemoveTools.Remove(svc, new[] { ipFid }, into: "GhostRemove");
                Check(refused.Contains("cannot extend: no houseCARL patch named 'GhostRemove'", StringComparison.Ordinal),
                      "the not-found refusal fires for this record too");

                // The lane lands on the first-touch CONSENT handshake, not a write and not an error. That is what
                // THIS fixture shows and all it shows: the plugin has never been acknowledged. Against one that has
                // — consent is persisted and shared across the in-place lanes — the same call writes with no prompt.
                var prompted = RemoveTools.Remove(svc, new[] { ipFid }, in_place: "RemoveHere.esp");
                Check(prompted.Contains("first-time confirmation", StringComparison.Ordinal),
                      "the lane reaches the in-place first-touch confirmation (a prompt, not a refusal)");

                var done = RemoveTools.Remove(svc, new[] { ipFid }, in_place: "RemoveHere.esp", acknowledge: true);
                Check(done.Contains("removed 1 record from RemoveHere.esp IN PLACE", StringComparison.Ordinal),
                      $"…and acknowledging it REMOVES the record ({done})");
                ISkyrimModGetter? back = null;
                try
                {
                    back = SkyrimMod.CreateFromBinaryOverlay(ipPath, SkyrimRelease.SkyrimSE);
                    Check(back.Weapons.All(w => w.FormKey != ipFk), "…and the record is GONE from the file on disk");
                }
                finally { (back as IDisposable)?.Dispose(); }
            }

            // ---- 8e: every lane that STATES a fresh-write path is held to saying it ------------------------------
            //      The shared default used to carry the create clause, so these call sites were free-riding on it and
            //      an arm would have proved nothing. Now the default claims nothing and each lane states its own, so
            //      a deleted statement is a silently weakened refusal — the #356 shape, one lane over. apply is
            //      covered by arm 8; forward and create are cheap on this fixture; the two copy lanes need an NPC
            //      universe, so they are pinned in their own probes (copy-service-guard, npc-copy-guard).
            Console.WriteLine();
            Console.WriteLine("--- 8e: forward and create still state that patch= names a fresh patch ---");
            {
                // A name nothing holds — arm 8b's remedy call created "GhostPatch", so reusing it would resolve.
                var fwd = svc.ForwardRecords(new[] { fid }, mKey.FileName, null, "GhostFwd");
                Check(!fwd.Success && fwd.Error is not null
                      && fwd.Error.Contains("patch=\"GhostFwd\" for a fresh patch", StringComparison.Ordinal),
                      "housecarl_forward's not-found refusal names patch=<the guessed name>");

                var cre = svc.CreateRecordsBatch(new[] { new CreateOp { RecordType = "Keyword", Editorid = "HcExtKw" } },
                                                 null, "GhostCre");
                Check(!cre.Success && cre.Error is not null
                      && cre.Error.Contains("patch=\"GhostCre\" for a fresh patch", StringComparison.Ordinal),
                      "housecarl_create's does too");
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

                // #359: the refusal must not dead-end. In ONE sentence it names the fresh lane's own parameter and
                // the owned patches to extend instead (Aaron, 2026-09-06).
                Check(r.Error is not null && r.Error.Contains("patch= a name no mod folder already uses for a fresh patch", StringComparison.Ordinal),
                      "…the un-owned refusal names the fresh lane's parameter (#359)");
                Check(r.Error is not null && !r.Error.Contains("patch=\"Foreign\"", StringComparison.Ordinal),
                      "…and does NOT hand back the colliding stem, which would shadow the foreign plugin (#359)");
                Check(r.Error is not null && r.Error.Contains("; try into=\"", StringComparison.Ordinal),
                      "…and names the patches houseCARL owns instead of 'Use a different patch name.'");
                Check(r.Error is not null && OneSentence(r.Error) && !r.Error.Contains("in_place", StringComparison.Ordinal),
                      $"…and is ONE sentence with no in-place clause ({r.Error})");

                // Same at TOOL altitude, where the in-place spelling IS known: the refusal still stops at the
                // candidates, and the removal lane says why it has no fresh-patch route.
                var tooled = RemoveTools.Remove(svc, new[] { fid }, into: "Foreign");
                Check(!tooled.Contains("in_place", StringComparison.Ordinal)
                      && tooled.Contains("; try into=\"", StringComparison.Ordinal),
                      "housecarl_remove's un-owned refusal offers no in-place lane either, and still names the owned patches");

                // the rider lane refuses the un-owned folder too (shared resolver — same gate)
                string riderErr2 = "";
                try { svc.ResolvePatchModFolder(null, "Foreign", "houseCARL_Archive", BsaTools.RepackNaming); }
                catch (InvalidOperationException ex) { riderErr2 = ex.Message; }
                Check(riderErr2.Contains("NOT created by houseCARL", StringComparison.Ordinal),
                      "the RIDER lane also refuses the un-owned folder (same ownership gate, no foreign-plugin door)");
                Check(riderErr2.Contains("patch_name= a name no mod folder already uses for a fresh folder", StringComparison.Ordinal)
                      && riderErr2.Contains("; try into=\"", StringComparison.Ordinal) && OneSentence(riderErr2),
                      "…and names THAT lane's own parameter plus the owned patches, in one sentence, never patch= or a dead end (#359)");
            }

            // ---- 9b: the un-owned refusal at TOOL altitude, on a folder holding an ACTIVE plugin -----------------
            //      "RemoveHereMod" is un-owned and holds RemoveHere.esp, which arm 8d2 put in the active order and
            //      then removed a record from in place — so this is the folder where an in-place clause would have
            //      been true, and Aaron's ruling still keeps it out. Each of the four write tools is called, because
            //      each renders the refusal for itself: a clause creeping back into any one of them reddens here.
            Console.WriteLine();
            Console.WriteLine("--- 9b: every write tool's un-owned refusal is one sentence, with no in-place clause ---");
            {
                static JsonElement Doc(string s) => JsonDocument.Parse(s).RootElement;

                var ap = ApplyTools.Apply(svc, ops: Doc($"[{{\"formid\":\"{fid}\",\"field_path\":\"BasicStats.Weight\",\"value\":\"2\"}}]"),
                                          into: "RemoveHereMod");
                Check(!ap.Contains("in_place", StringComparison.Ordinal) && ap.Contains("; try into=\"", StringComparison.Ordinal),
                      "housecarl_apply names the owned candidates and no in-place lane, on a folder whose plugin the lane could take");
                Check(OneSentence(ap.Trim()), $"…and the refusal it renders is ONE sentence ({ap.Trim()})");

                var cr = CreateTools.Create(svc, records: Doc("[{\"record_type\":\"Keyword\",\"editorid\":\"HcExtUnowned\"}]"),
                                            into: "RemoveHereMod");
                Check(!cr.Contains("in_place", StringComparison.Ordinal) && cr.Contains("; try into=\"", StringComparison.Ordinal),
                      "housecarl_create does the same");

                var fw = ForwardTools.Forward(svc, formids: new[] { fid }, source: mKey.FileName.String, into: "RemoveHereMod");
                Check(!fw.Contains("in_place", StringComparison.Ordinal) && fw.Contains("; try into=\"", StringComparison.Ordinal),
                      "housecarl_forward does too");

                var rm = RemoveTools.Remove(svc, new[] { fid }, into: "RemoveHereMod");
                Check(!rm.Contains("in_place", StringComparison.Ordinal) && rm.Contains("; try into=\"", StringComparison.Ordinal),
                      "housecarl_remove does too, in the lane that has an in-place spelling to hand down");
                // The un-owned arm used to say nothing about why there is no fresh-patch route on the one lane that
                // has none, so a caller who reached for patch= met a second refusal.
                Check(rm.Contains(WriteSentences.RemoveNoFreshPatch, StringComparison.Ordinal)
                      && !rm.Contains("patch=", StringComparison.Ordinal),
                      "…and states why removal offers no fresh patch here, rather than leaving it to be guessed");
            }

            // ---- 9c: every spelling the candidate list prints RESOLVES to the row it was printed for ----
            //      A list of candidates is a set of claims about what a call does, so the two shapes that could make
            //      one false are built and then FOLLOWED: a folder whose own name resolves somewhere else, and a
            //      folder holding several plugins, where the folder spelling reaches the folder but no single plugin.
            Console.WriteLine();
            Console.WriteLine("--- 9c: the rendered into= spellings resolve where they say they do ---");
            {
                // A renamed owned folder literally named "SeedA", holding Solo.esp. into="SeedA" does NOT reach it:
                // two other owned folders carry SeedA.esp, and the by-plugin arm runs before the folder catch-all.
                var soloDir = Path.Combine(mods, "SeedA");
                Directory.CreateDirectory(soloDir);
                MarkOwned(soloDir, "Solo.esp");
                new SkyrimMod(new ModKey("Solo", ModType.Plugin), SkyrimRelease.SkyrimSE)
                    .BeginWrite.ToPath(Path.Combine(soloDir, "Solo.esp")).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

                // Each shape is reached by a stem NEAR it, since the sentence names only the three nearest — the cap
                // is the arm below, and a claim about which spelling is offered has to be read where it is offered.
                var missSolo = svc.ApplyEdits(new[] { Wgt(1) }, null, "Soloo");
                string list = missSolo.Error ?? "";
                Check(list.Contains("; try into=\"", StringComparison.Ordinal) && OneSentence(list),
                      $"the refusal names candidates in one sentence, not the owns-nothing clause (#380) ({list})");
                Check(!list.Contains("into=\"SeedA\"", StringComparison.Ordinal)
                      && list.Contains("into=\"Solo.esp\"", StringComparison.Ordinal),
                      "a folder whose own name resolves elsewhere is named by its PLUGIN instead");
                string two = svc.ApplyEdits(new[] { Wgt(1) }, null, "Alphaa").Error ?? "";
                Check(two.Contains("into=\"Alpha.esp\"", StringComparison.Ordinal)
                      && !two.Contains("into=\"houseCARL - TwoEsp\"", StringComparison.Ordinal),
                      "a folder holding two plugins is named by a plugin, not by the folder spelling it would refuse");

                // Followed, both of them, since that is the only way to check a claim about what a call does.
                var followedSolo = svc.ApplyEdits(new[] { Wgt(6) }, null, "Solo.esp");
                Check(followedSolo.Success && Ends(followedSolo.OutputPath, "SeedA", "Solo.esp"),
                      $"following into=\"Solo.esp\" extends THAT patch ({Path.GetFileName(Path.GetDirectoryName(followedSolo.OutputPath) ?? "")})");
                var followedAlpha = svc.ApplyEdits(new[] { Wgt(6) }, null, "Alpha.esp");
                Check(followedAlpha.Success && Ends(followedAlpha.OutputPath, "houseCARL - TwoEsp", "Alpha.esp"),
                      "following into=\"Alpha.esp\" extends the named plugin inside the two-plugin folder");

                // Near-stem FIRST (#380): with only three candidates named, ranking is the whole of what the caller
                // gets, so a typo'd plugin has to come first, not merely appear.
                var typo = svc.ApplyEdits(new[] { Wgt(1) }, null, "Betta");
                var ranked = Candidates(typo.Error ?? "");
                Check(ranked.Count > 0 && ranked[0] == "into=\"Beta.esp\"",
                      $"into=\"Betta\" names the near-stem candidate FIRST, ahead of the alphabet ({string.Join(", ", ranked)})");
            }

            // ---- 9d: the pluginless folder, and the cap of three ----
            //      Both halves of the candidates' own contract: the record lane leaves out a folder it could not
            //      extend, the rider lane keeps it, and a modlist full of patches still yields one short sentence.
            Console.WriteLine();
            Console.WriteLine("--- 9d: pluginless folders are record-lane-only omissions, and three candidates is the cap ---");
            {
                var bare = Path.Combine(mods, "houseCARL - AssetsOnly");
                Directory.CreateDirectory(bare);
                MarkOwned(bare, "");                                          // an owned folder holding no plugin at all

                // A near-MISS of the pluginless folder's own name, so the suggester ranks it top on both lanes and the
                // only thing separating them is the needEsp filter this arm is about — not where the cap fell.
                const string nearBare = "houseCARL - AssetsOnli";
                var rec = svc.ApplyEdits(new[] { Wgt(1) }, null, nearBare);
                Check(!(rec.Error ?? "").Contains("AssetsOnly", StringComparison.Ordinal),
                      "the RECORD lane leaves out an owned folder holding no plugin (it could not extend it)");
                string riderList = "";
                try { svc.ResolvePatchModFolder(null, nearBare, "houseCARL_Archive", BsaTools.RepackNaming); }
                catch (InvalidOperationException ex) { riderList = ex.Message; }
                Check(riderList.Contains("into=\"houseCARL - AssetsOnly\"", StringComparison.Ordinal),
                      "…and the RIDER lane keeps it, since that lane extends the folder itself");

                // Past the cap the sentence names three nearest candidates and COUNTS the rest, staying one sentence.
                for (int i = 0; i < 9; i++)
                {
                    var extra = Path.Combine(mods, $"houseCARL - Bulk{i:D2}");
                    Directory.CreateDirectory(extra);
                    MarkOwned(extra, $"Bulk{i:D2}.esp");
                    File.WriteAllText(Path.Combine(extra, $"Bulk{i:D2}.esp"), "b");
                }
                var capped = svc.ApplyEdits(new[] { Wgt(1) }, null, "GhostCapped");
                string capText = capped.Error ?? "";
                var rows = Candidates(capText);
                Check(rows.Count == 3 && OneSentence(capText),
                      $"the sentence names three candidates and no more ({string.Join(", ", rows)})");
                // The suggester vouches for none of these against "GhostCapped", so before the always-answering
                // distance tiebreak this list was the alphabet's first three and the near-miss went unnamed.
                Check(rows[0] == "into=\"houseCARL - GhostPatch\"",
                      $"…led by the near-miss the caller meant, not by whichever name sorts first ({string.Join(", ", rows)})");
                // The shape the ruling asks for, read off the real sentence: "A, B or C (+N more), or <fresh>".
                Check(capText.Contains($"; try {rows[0]}, {rows[1]} or {rows[2]} (+", StringComparison.Ordinal)
                      && capText.Contains(" more), or dropping into= and passing patch=\"GhostCapped\" for a fresh patch", StringComparison.Ordinal),
                      $"…in the ruled shape — candidates, the count the cap dropped, then the fresh-patch parameter ({capText})");
                // The count is a real remainder, not a token: one more offerable patch, one higher. A silent cap reads
                // as an exhaustive inventory, which is the wrong next step.
                int Dropped(string s) => int.Parse(System.Text.RegularExpressions.Regex.Match(s, @"\(\+(\d+) more\)").Groups[1].Value);
                var oneMore = Path.Combine(mods, "houseCARL - Bulk09");
                Directory.CreateDirectory(oneMore);
                MarkOwned(oneMore, "Bulk09.esp");
                File.WriteAllText(Path.Combine(oneMore, "Bulk09.esp"), "b");
                string capText2 = svc.ApplyEdits(new[] { Wgt(1) }, null, "GhostCapped").Error ?? "";
                Check(Dropped(capText2) == Dropped(capText) + 1,
                      $"…and the count tracks the inventory, {Dropped(capText)} then {Dropped(capText2)} with one patch more");
            }

            // ---- 9f: owned patches that no single into= spelling reaches are COUNTED, not denied ----
            //      An empty candidate set has two causes and only one of them is "there is nothing to extend". Its
            //      own instance, because the claim is about EVERY owned patch being unreachable.
            Console.WriteLine();
            Console.WriteLine("--- 9f: patches nothing reaches are counted, instead of claiming none exists ---");
            {
                string inst2 = Path.Combine(root, "instance2");
                string prof2 = Path.Combine(inst2, "profiles", "Default");
                string mods2 = Path.Combine(inst2, "mods");
                Directory.CreateDirectory(prof2); Directory.CreateDirectory(mods2);
                File.WriteAllText(Path.Combine(inst2, "ModOrganizer.ini"),
                    "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                    + Path.Combine(root, "game").Replace(@"\", @"\\") + ")\r\n");
                Directory.CreateDirectory(Path.Combine(mods2, "MasterMod"));
                File.Copy(masterPath, Path.Combine(mods2, "MasterMod", mKey.FileName.String));
                File.WriteAllText(Path.Combine(prof2, "loadorder.txt"), "# header\r\n" + mKey.FileName + "\r\n");
                File.WriteAllText(Path.Combine(prof2, "plugins.txt"), "*" + mKey.FileName + "\r\n");
                File.WriteAllText(Path.Combine(prof2, "modlist.txt"), "# header\r\n+MasterMod\r\n");

                // Two owned twins, each holding the SAME two plugins: no folder name resolves (neither holds
                // "<folder>.esp"), and each plugin name is ambiguous across the pair. Every spelling is refused.
                // Real plugins, not placeholder bytes: the remedy this arm asserts is FOLLOWED below, and following it
                // means writing into one of these folders.
                foreach (var name in new[] { "houseCARL - Twin", "houseCARL - Twin backup" })
                {
                    var d = Path.Combine(mods2, name);
                    Directory.CreateDirectory(d);
                    File.WriteAllText(Path.Combine(d, "meta.ini"),
                        $"{HousecarlOwnerMeta.Section}\r\ngenerated=true\r\nplugin=Alpha.esp\r\n");
                    foreach (var n in new[] { "Alpha", "Beta" })
                        new SkyrimMod(new ModKey(n, ModType.Plugin), SkyrimRelease.SkyrimSE)
                            .BeginWrite.ToPath(Path.Combine(d, n + ".esp")).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
                }

                var store2 = new UserConfigStore(Path.Combine(root, "houseCARL.user2.json"));
                using var svc2 = LoadOrderService.WithInstance(inst2, 0, store2);
                svc2.Stats();
                var unreachable = svc2.ApplyEdits(new[] { Wgt(1) }, null, "GhostNowhere");
                string text = unreachable.Error ?? "";
                Check(text.Contains("try renaming one of the 2 patches houseCARL owns in MO2", StringComparison.Ordinal)
                      && text.Contains("no single into= spelling reaches any of them", StringComparison.Ordinal),
                      $"the refusal counts the patches no spelling reaches and says what to do about it ({text})");
                Check(!text.Contains("owns no patch", StringComparison.Ordinal) && OneSentence(text),
                      "…rather than telling the caller houseCARL owns none, which would send them to mint a duplicate");

                // FOLLOW the remedy, the way 8b and 9c follow theirs: a remedy is a claim about what a call does, so
                // renaming one of the twins in MO2 has to be the thing that makes an into= spelling resolve.
                Directory.Move(Path.Combine(mods2, "houseCARL - Twin backup"), Path.Combine(mods2, "houseCARL - Alpha"));
                svc2.Stats();
                var renamed = svc2.ApplyEdits(new[] { Wgt(2) }, null, "Alpha");
                Check(renamed.Success && Ends(renamed.OutputPath, "houseCARL - Alpha", "Alpha.esp"),
                      $"following it — the rename — makes into=\"Alpha\" resolve to that patch ({renamed.Error ?? renamed.OutputPath})");

                // A folder scan that throws PARTWAY is the dangerous one: reachability is decided by counting the
                // OTHER owned folders holding the same plugin, so dropping one of a colliding pair makes an ambiguous
                // token look unambiguous. MO2 holding a meta.ini open is the ordinary way this happens.
                using (File.Open(Path.Combine(mods2, "houseCARL - Twin", "meta.ini"), FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    svc2.Stats();
                    string locked = svc2.ApplyEdits(new[] { Wgt(3) }, null, "GhostLocked").Error ?? "";
                    Check(Candidates(locked).Count == 0,
                          $"an unreadable folder mid-scan yields NO candidates, not the half the scan got to ({locked})");
                    // A failed scan is a THIRD empty list. Saying "houseCARL owns no patch" here is a positive claim
                    // the scan never established — on a real install it sends a user with forty patches off to mint a
                    // duplicate — so the sentence says the scan failed and what to try instead.
                    Check(!locked.Contains("owns no patch", StringComparison.Ordinal)
                          && locked.Contains("could not scan it just now", StringComparison.Ordinal),
                          $"…and says the scan failed rather than claiming houseCARL owns none ({locked})");
                    // The half it got to would have offered into="Alpha.esp", which is ambiguous once both twins are
                    // readable again — the second refusal these candidates exist to prevent.
                    Check(!locked.Contains("into=\"Alpha.esp\"", StringComparison.Ordinal) && OneSentence(locked),
                          "…so it never names a spelling that only looks unambiguous because a folder went missing");

                    // The removal lane is where the false claim did real harm: with no fresh route it also told the
                    // caller to go make a patch, beside the ones the scan failed to see.
                    string lockedRm = RemoveTools.Remove(svc2, new[] { fid }, into: "GhostLocked");
                    Check(!lockedRm.Contains("owns no patch", StringComparison.Ordinal)
                          && !lockedRm.Contains("making the patch first", StringComparison.Ordinal)
                          && OneSentence(lockedRm.Trim()),
                          $"…and the removal lane no longer sends them to mint one on a scan that failed ({lockedRm.Trim()})");
                }
            }

            // ---- 9g: the removal lane on an install owning nothing yet says how the patch gets made --------------
            //      Its own instance, because the claim is about an inventory with NOTHING in it: no fresh route (the
            //      removal lane creates nothing) and no candidates is the likeliest first-time path, and it used to
            //      stop dead after two stacked ", and" clauses.
            Console.WriteLine();
            Console.WriteLine("--- 9g: the removal lane owning nothing still says what to try ---");
            {
                string inst3 = Path.Combine(root, "instance3");
                string prof3 = Path.Combine(inst3, "profiles", "Default");
                string mods3 = Path.Combine(inst3, "mods");
                Directory.CreateDirectory(prof3); Directory.CreateDirectory(mods3);
                File.WriteAllText(Path.Combine(inst3, "ModOrganizer.ini"),
                    "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                    + Path.Combine(root, "game").Replace(@"\", @"\\") + ")\r\n");
                var vDir = Path.Combine(mods3, "VictimMod");
                Directory.CreateDirectory(vDir);
                FormKey vFk;
                {
                    var m = new SkyrimMod(new ModKey("Victim", ModType.Plugin), SkyrimRelease.SkyrimSE);
                    var w = m.Weapons.AddNew(); w.EditorID = "HcVictim";
                    w.BasicStats = new WeaponBasicStats { Damage = 3, Weight = 1 };
                    vFk = w.FormKey;
                    m.BeginWrite.ToPath(Path.Combine(vDir, "Victim.esp")).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
                }
                File.WriteAllText(Path.Combine(prof3, "loadorder.txt"), "# header\r\nVictim.esp\r\n");
                File.WriteAllText(Path.Combine(prof3, "plugins.txt"), "*Victim.esp\r\n");
                File.WriteAllText(Path.Combine(prof3, "modlist.txt"), "# header\r\n+VictimMod\r\n");

                var store3 = new UserConfigStore(Path.Combine(root, "houseCARL.user3.json"));
                using var svc3 = LoadOrderService.WithInstance(inst3, 0, store3);
                svc3.Stats();
                var bare = RemoveTools.Remove(svc3, new[] { $"{vFk.ID:X6}:Victim.esp" }, into: "Nothing");
                Check(bare.Contains("houseCARL owns no patch holding a plugin yet", StringComparison.Ordinal) && OneSentence(bare.Trim()),
                      $"the refusal states the empty inventory in ONE sentence ({bare.Trim()})");
                Check(!bare.Contains(", and houseCARL will not create a patch here, since a removal only drops a record the patch ITSELF already carries, and", StringComparison.Ordinal),
                      "…with the facts as clauses rather than two stacked \", and\" openings");
                Check(bare.Contains("; try making the patch first with a write that creates one", StringComparison.Ordinal),
                      "…and it says how the patch gets made, instead of stopping dead on the likeliest first-time path");
                // A remedy naming a RETIRED tool hands the caller a second refusal, so the names come from the
                // constants: housecarl_create_record and housecarl_forward_record are absorbed and off the surface.
                Check(bare.Contains($"({ToolNames.Apply}, {ToolNames.Create} or {ToolNames.Forward})", StringComparison.Ordinal)
                      && !bare.Contains("housecarl_create_record", StringComparison.Ordinal)
                      && !bare.Contains("housecarl_forward_record", StringComparison.Ordinal),
                      $"…naming the tools that are ON the surface, not their retired spellings ({bare.Trim()})");
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

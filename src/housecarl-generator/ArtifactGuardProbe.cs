using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD for the §2.1.1 artifact disposition (tool-surface 2.0, W1 PR 2): bulk output decouples result
/// size from render size via ONE self-contained JSONL file — manifest line 1, one row per line — and re-enters
/// through the @file convention, epoch-checked. Arms:
///
///   1  ROUND-TRIP — the core writer/reader pair: manifest fields survive, identity tokens come back in row
///      order, the artifact sniff says yes to a manifest and no to a plain list.
///   2  TO_FILE — the forced disposition on all three wired lanes: the COMPLETE result lands in the caller's
///      file, only the manifest renders inline (no rows), the spilled marker names the file in BOTH formats
///      (D2), and the doomed-disposition refusals (relative path, wrong extension, conflict_tree, offset) fire
///      BEFORE any scan.
///   3  AUTO-SPILL — a render that hits max_chars ALWAYS leaves a complete artifact in the server results dir
///      and says so in-band (text, json, dense; cross-query, batch, resolve). Truncation without a named
///      complete artifact is the E4.2 failure this kills. A FAILED spill write is named loud in both formats —
///      never a truncated response silently missing its promised file.
///   4  RE-ENTRY — formids=@artifact (batch, resolve) and where=["formid in @artifact"] (cross-query) yield the
///      identity column against the SAME build; a plain @file list keeps working with no epoch claim; @ mixed
///      with inline entries refuses named; a no-identity (group_by) artifact refuses named.
///   5  EPOCH CHECK — after the load order changes, artifact re-entry refuses LOUD naming BOTH epochs on every
///      consuming lane, the refusal is stamped (it consulted the build), and the plain-list control still works
///      (no epoch claim to violate). Re-materializing via to_file against the new build re-enters cleanly.
///   6  RESULTS STORE — prune-by-age deletes old spills at write time; a to_file target overwrites wholesale
///      (a re-run is a NEW artifact, not an append).
///   7  PR #306 REVIEW FOLDS — a truncated conflict_tree render refuses to spill thinner rows and says why
///      (no row form); a limit-windowed spill says WINDOW, never "complete result" (json complete=false); a
///      post-scan to_file failure keeps the format contract ({error,epoch} under json); a whitespace list
///      element stays a per-item error; path reservation is atomic (same-second spills can't collide) and a
///      failed spill releases its reservation; orphaned Writer temps prune on age; to_file into the pruned
///      results dir refuses naming the hazard.
///   8  PR #306 RE-REVIEW FOLD — error rows are not identity-bearing: a legitimately-produced artifact whose
///      inputs included garbage (resolve keeps the raw token; batch keeps the null FormKey) still re-enters
///      cleanly on its RESOLVED rows, and an all-error artifact refuses naming the real cause — never the
///      was-it-edited misdiagnosis, which is reserved for genuine file/manifest mismatches.
///
/// Self-contained: synthetic MO2 instance + synthesized plugins in temp. No game data.
/// </summary>
internal static class ArtifactGuardProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" artifact guard — §2.1.1: results decouple from renders; artifacts re-enter epoch-checked");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var root = Path.Combine(Path.GetTempPath(), "hc-artifact-guard-" + Guid.NewGuid().ToString("N"));
        var savedOverride = ResultsStore.OverrideDirForTests;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "game", "Data"));

            // ---- synthesized plugins: a master with 8 weapons, and an override ----
            var masterKey = new ModKey("HcArtMaster", ModType.Master);
            var ovKey = new ModKey("HcArtOverride", ModType.Plugin);
            string masterName = masterKey.FileName.String, ovName = ovKey.FileName.String;

            var master = new SkyrimMod(masterKey, SkyrimRelease.SkyrimSE);
            var weapons = new List<FormKey>();
            for (int i = 0; i < 8; i++)
            {
                var w = master.Weapons.AddNew(); w.EditorID = $"HcArtW{i}";
                w.BasicStats = new WeaponBasicStats { Damage = (ushort)(10 + i), Weight = 1 };
                weapons.Add(w.FormKey);
            }
            var ovMod = new SkyrimMod(ovKey, SkyrimRelease.SkyrimSE);
            ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(ovMod, master.Weapons.First()))
                .BasicStats = new WeaponBasicStats { Damage = 99, Weight = 1 };

            var inst = Path.Combine(root, "inst");
            var mods = Path.Combine(inst, "mods");
            Directory.CreateDirectory(Path.Combine(mods, "MasterMod"));
            Directory.CreateDirectory(Path.Combine(mods, "OverrideMod"));
            var masterFile = Path.Combine(mods, "MasterMod", masterName);
            var ovFile = Path.Combine(mods, "OverrideMod", ovName);
            master.BeginWrite.ToPath(masterFile).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            ovMod.BeginWrite.ToPath(ovFile).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

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
            var epoch0 = svc.Stats().epoch;

            var resultsDir = Path.Combine(root, "results");
            ResultsStore.OverrideDirForTests = resultsDir;

            string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";
            var work = Path.Combine(root, "work");
            Directory.CreateDirectory(work);

            static JsonDocument Parse(string s) => JsonDocument.Parse(s);

            // ---- 1: the core writer/reader round-trip ----
            Console.WriteLine("--- 1: writer/reader round-trip — manifest line 1, identity column back in row order ---");
            {
                var p = Path.Combine(work, "roundtrip.jsonl");
                using (var w = new ResultArtifact.Writer())
                {
                    w.WriteRow((jw, _) => { jw.WriteStartObject(); jw.WriteString("formid", "000001:A.esp"); jw.WriteString("type", "Weapon"); jw.WriteEndObject(); }, "Weapon");
                    w.WriteRow((jw, _) => { jw.WriteStartObject(); jw.WriteString("formid", "000002:A.esp"); jw.WriteString("type", "Armor"); jw.WriteEndObject(); }, "Armor");
                    var (m, err) = w.Save(p, "housecarl_test", new List<KeyValuePair<string, string>> { new("type", "WEAP") },
                                          "formid", new[] { "formid", "type" }, "input order", 2, "abcdef0123456789");
                    Check(err is null && m is not null, $"writer saves manifest+rows ({err ?? "ok"})");
                }
                var content = File.ReadAllText(p);
                Check(ResultArtifact.LooksLikeArtifact(content), "the sniff recognizes a manifest on line 1");
                Check(!ResultArtifact.LooksLikeArtifact("000001:A.esp\n000002:A.esp\n"), "…and says NO to a plain formid list");
                var (rm, tokens, rerr) = ResultArtifact.ReadIdentity(p, content);
                Check(rerr is null && rm is not null && rm.Epoch == "abcdef0123456789" && rm.Tool == "housecarl_test"
                      && rm.RowCount == 2 && rm.Identity == "formid" && rm.TypeCounts is { Count: 2 },
                      "manifest fields survive the round-trip (tool, epoch, row_count, identity, type_counts)");
                Check(tokens is ["000001:A.esp", "000002:A.esp"], "identity tokens come back in row order");
            }

            // ---- 2: to_file — the forced disposition ----
            Console.WriteLine();
            Console.WriteLine("--- 2: to_file — complete result to the caller's file, manifest-only inline, refusals pre-scan ---");
            var artifactPath = Path.Combine(work, "weapons.jsonl");
            {
                var text = ReadTools.CrossPluginQuery(svc, type: "WEAP", to_file: artifactPath);
                Check(File.Exists(artifactPath), "to_file writes the artifact where asked");
                var (m, toks, err) = ResultArtifact.ReadIdentity(artifactPath, File.ReadAllText(artifactPath));
                Check(err is null && m!.RowCount == 8 && m.Total == 8 && m.Epoch == epoch0 && toks!.Count == 8,
                      $"…the artifact is COMPLETE (8/8 rows) and stamped with the scanned build ({m?.Epoch})");
                Check(m!.TypeCounts is { Count: 1 } tc && tc["Weapon"] == 8, "…manifest type_counts count the rows");
                Check(text.Contains("spilled:") && text.Contains(artifactPath), "…the text response's spilled marker NAMES the file (contract)");
                Check(text.Contains("8 matches") && !text.Contains(Fid(weapons[0])), "…and renders the manifest ONLY — no inline rows");

                var json = ReadTools.CrossPluginQuery(svc, type: "WEAP", format: "json", to_file: Path.Combine(work, "weapons-j.jsonl"));
                using var doc = Parse(json);
                Check(doc.RootElement.TryGetProperty("spilled", out var sp) && sp.GetProperty("path").GetString()!.EndsWith("weapons-j.jsonl")
                      && sp.GetProperty("reason").GetString() == "to_file",
                      "json mode: the spilled marker rides IN the document with the path (D2)");
                Check(doc.RootElement.GetProperty("matches").GetArrayLength() == 0 && doc.RootElement.GetProperty("total").GetInt32() == 8,
                      "…json rows omitted, true total intact");

                // group_by to_file: a count-table artifact carries NO identity column.
                var gPath = Path.Combine(work, "groups.jsonl");
                ReadTools.CrossPluginQuery(svc, type: "WEAP", group_by: "winner", to_file: gPath);
                var (gm, _, gerr) = ResultArtifact.ReadIdentity(gPath, File.ReadAllText(gPath));
                Check(gerr is not null && gerr.Contains("NO identity column"), "a group_by artifact refuses identity re-entry by name");

                // Doomed dispositions refuse BEFORE any scan.
                Check(ReadTools.CrossPluginQuery(svc, type: "WEAP", to_file: "relative.jsonl").Contains("ABSOLUTE"),
                      "to_file refuses a relative path, named");
                Check(ReadTools.CrossPluginQuery(svc, type: "WEAP", to_file: Path.Combine(work, "x.csv")).Contains(".jsonl"),
                      "…and a non-.jsonl name (the file must say what it is)");
                Check(ReadTools.CrossPluginQuery(svc, type: "WEAP", conflict_tree: true, to_file: Path.Combine(work, "x.jsonl")).Contains("conflict_tree"),
                      "…and conflict_tree (a text-only view with no row form)");
                Check(ReadTools.CrossPluginQuery(svc, type: "WEAP", offset: 2, to_file: Path.Combine(work, "y.jsonl")).Contains("offset"),
                      "…and offset= (the artifact is never a window)");
            }

            // ---- 3: auto-spill at the inline ceiling ----
            Console.WriteLine();
            Console.WriteLine("--- 3: auto-spill — a truncated render ALWAYS leaves a complete, named artifact ---");
            {
                var text = ReadTools.CrossPluginQuery(svc, type: "WEAP", max_chars: 250);
                Check(text.Contains("[truncated:"), "control: max_chars=250 truncates the inline text render");
                Check(text.Contains("spilled: complete result (8 rows)"), "…and the spilled marker says the artifact is COMPLETE (8 rows, not the rendered prefix)");
                var spillPath = Directory.GetFiles(resultsDir, "cross_plugin_query_*.jsonl").SingleOrDefault();
                Check(spillPath is not null && text.Contains(spillPath), "…naming the results-dir file that actually exists");
                var (sm, stoks, serr) = ResultArtifact.ReadIdentity(spillPath!, File.ReadAllText(spillPath!));
                Check(serr is null && sm!.RowCount == 8 && stoks!.Count == 8 && sm.Epoch == epoch0,
                      "…whose rows are the complete window, stamped with the scanned build");

                var json = ReadTools.CrossPluginQuery(svc, type: "WEAP", format: "json", max_chars: 250);
                using (var doc = Parse(json))
                    Check(doc.RootElement.GetProperty("truncated").GetBoolean()
                          && doc.RootElement.GetProperty("spilled").GetProperty("path").GetString() is { } jp && File.Exists(jp),
                          "json mode: truncated=true AND spilled.path names a real file (D2)");
                var dense = ReadTools.CrossPluginQuery(svc, type: "WEAP", format: "dense", max_chars: 250);
                using (var doc = Parse(dense))
                    Check(doc.RootElement.TryGetProperty("spilled", out var dsp) && File.Exists(dsp.GetProperty("path").GetString()!),
                          "dense mode: the spilled marker rides too");

                var allFids = weapons.Select(Fid).ToArray();
                var bt = ReadTools.BatchRecordDetail(svc, allFids, max_chars: 300);
                Check(bt.Contains("spilled:") && Directory.GetFiles(resultsDir, "batch_record_detail_*.jsonl").Length == 1,
                      "batch: a truncated batch auto-spills its complete rows");
                var rt = ReadTools.Resolve(svc, allFids, max_chars: 250);
                Check(rt.Contains("spilled:") && Directory.GetFiles(resultsDir, "resolve_*.jsonl").Length == 1,
                      "resolve: same contract");

                // A FAILED spill write is named loud — never a truncated response silently missing its artifact.
                var blocker = Path.Combine(root, "results-blocked");
                File.WriteAllText(blocker, "a file where the results DIR should be");
                ResultsStore.OverrideDirForTests = Path.Combine(blocker, "sub");   // un-creatable: parent is a file
                var failText = ReadTools.CrossPluginQuery(svc, type: "WEAP", max_chars: 250);
                Check(failText.Contains("[truncated:") && failText.Contains("could NOT be written") && failText.Contains("exists NOWHERE"),
                      "a failed auto-spill is named in the text response (the truncation keeps rendering)");
                var failJson = ReadTools.CrossPluginQuery(svc, type: "WEAP", format: "json", max_chars: 250);
                using (var doc = Parse(failJson))
                    Check(doc.RootElement.TryGetProperty("spill_error", out _), "…and in the json document (spill_error)");
                ResultsStore.OverrideDirForTests = resultsDir;
            }

            // ---- 4: @file re-entry against the same build ----
            Console.WriteLine();
            Console.WriteLine("--- 4: @file re-entry — artifact yields its identity column; plain lists unchanged; misuse named ---");
            {
                var bt = ReadTools.BatchRecordDetail(svc, new[] { "@" + artifactPath });
                Check(bt.StartsWith("batch: 8 records") && bt.Contains($"epoch={epoch0}"), "formids=@artifact reads all 8 identities (batch)");
                var rt = ReadTools.Resolve(svc, new[] { "@" + artifactPath });
                Check(rt.StartsWith("resolve: 8 formids"), "…and resolve");
                var qt = ReadTools.CrossPluginQuery(svc, type: "WEAP", where: new[] { $"formid in @{artifactPath}" });
                Check(qt.Contains("8 matches"), "…and the where-grammar membership test (cross-query)");

                var plainPath = Path.Combine(work, "plain.txt");
                File.WriteAllText(plainPath, string.Join("\r\n", weapons.Take(3).Select(Fid)));
                Check(ReadTools.BatchRecordDetail(svc, new[] { "@" + plainPath }).StartsWith("batch: 3 records"),
                      "a PLAIN @file list still works (no manifest, no epoch claim)");

                Check(ReadTools.BatchRecordDetail(svc, new[] { "@" + plainPath, Fid(weapons[0]) }).Contains("IN PLACE OF the whole list"),
                      "@ mixed with inline entries refuses named");
                Check(ReadTools.BatchRecordDetail(svc, new[] { "@relative.txt" }).Contains("ABSOLUTE"),
                      "a relative @path refuses named");
                Check(ReadTools.BatchRecordDetail(svc, new[] { "@" + gArtifactPathFor(work) }).Contains("NO identity column"),
                      "a no-identity (group_by) artifact refuses named at the formids= door too");
            }

            // ---- 5: the epoch check — the load order changes, re-entry refuses loud ----
            Console.WriteLine();
            Console.WriteLine("--- 5: epoch check — a stale artifact refuses LOUD naming both epochs; plain lists don't claim ---");
            {
                File.SetLastWriteTimeUtc(ovFile, DateTime.UtcNow.AddHours(1));   // content-change signal → next capture re-fingerprints
                var epoch1 = svc.Stats().epoch;
                Check(epoch1 != epoch0, $"control: the build re-fingerprinted ({epoch0} -> {epoch1})");

                var bt = ReadTools.BatchRecordDetail(svc, new[] { "@" + artifactPath });
                Check(bt.StartsWith("error:") && bt.Contains(epoch0) && bt.Contains(epoch1) && bt.Contains("no stale-override"),
                      "batch re-entry refuses naming BOTH epochs and the no-override posture");
                Check(bt.Contains($"epoch={epoch1}"), "…and the refusal is STAMPED with the build it consulted");
                var bj = ReadTools.BatchRecordDetail(svc, new[] { "@" + artifactPath }, format: "json");
                using (var doc = Parse(bj))
                    Check(doc.RootElement.TryGetProperty("error", out _) && doc.RootElement.GetProperty("epoch").GetString() == epoch1,
                          "…json refusal: {error, epoch} (D2)");

                var qt = ReadTools.CrossPluginQuery(svc, type: "WEAP", where: new[] { $"formid in @{artifactPath}" });
                Check(qt.StartsWith("error:") && qt.Contains(epoch0) && qt.Contains(epoch1) && qt.Contains($"epoch={epoch1}"),
                      "cross-query predicate re-entry refuses the same way, stamped");
                var rt = ReadTools.Resolve(svc, new[] { "@" + artifactPath });
                Check(rt.StartsWith("error:") && rt.Contains(epoch0) && rt.Contains(epoch1), "resolve re-entry refuses too");

                var plainPath = Path.Combine(work, "plain.txt");
                Check(ReadTools.BatchRecordDetail(svc, new[] { "@" + plainPath }).StartsWith("batch: 3 records"),
                      "control: the PLAIN list still enters — it never claimed a build");

                // Re-materialize against the new build → re-entry is clean again (and to_file overwrites wholesale).
                var text = ReadTools.CrossPluginQuery(svc, type: "WEAP", to_file: artifactPath);
                var (m2, _, err2) = ResultArtifact.ReadIdentity(artifactPath, File.ReadAllText(artifactPath));
                Check(err2 is null && m2!.Epoch == epoch1 && text.Contains(artifactPath),
                      "to_file OVERWRITES the stale artifact with the new build's result");
                Check(ReadTools.BatchRecordDetail(svc, new[] { "@" + artifactPath }).StartsWith("batch: 8 records"),
                      "…and re-entry is clean against the re-materialized artifact");
            }

            // ---- 6: the results store — prune-by-age at write ----
            Console.WriteLine();
            Console.WriteLine("--- 6: results store — old spills prune at write time ---");
            {
                var old = Path.Combine(resultsDir, "cross_plugin_query_old.jsonl");
                File.WriteAllText(old, "{}");
                File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-(ResultsStore.PruneAfterDays + 1)));
                var fresh = Path.Combine(resultsDir, "resolve_fresh.jsonl");
                File.WriteAllText(fresh, "{}");
                ResultsStore.NextPath("housecarl_cross_plugin_query", "0123456789abcdef");
                Check(!File.Exists(old), $"a spill older than {ResultsStore.PruneAfterDays} days is pruned at the next write");
                Check(File.Exists(fresh), "…while a fresh spill survives");
            }

            // ---- 7: PR #306 review folds — honesty at the edges ----
            Console.WriteLine();
            Console.WriteLine("--- 7 (PR #306 fold): no-row-form honesty, windowed wording, format-kept failures, races, hygiene ---");
            {
                var epochNow = svc.Stats().epoch;
                int SpillCount(string prefix) => Directory.GetFiles(resultsDir, prefix + "_*.jsonl").Length;

                // F1 — a truncated conflict_tree render is NOT auto-spilled into thinner rows; it says why.
                int before = SpillCount("cross_plugin_query");
                var ct = ReadTools.CrossPluginQuery(svc, type: "WEAP", conflict_tree: true, max_chars: 250);
                Check(ct.Contains("[truncated:") && ct.Contains("NOT auto-spilled") && ct.Contains("conflict_tree"),
                      "a truncated conflict_tree query names its no-row-form instead of spilling thinner rows");
                Check(SpillCount("cross_plugin_query") == before, "…and no artifact was written for it");
                int bBefore = SpillCount("batch_record_detail");
                var bct = ReadTools.BatchRecordDetail(svc, weapons.Select(Fid).ToArray(), conflict_tree: true, max_chars: 300);
                Check(bct.Contains("NOT auto-spilled") && SpillCount("batch_record_detail") == bBefore,
                      "…batch twin: same honesty, no artifact");

                // F3 — a limit-windowed auto-spill never claims the complete result.
                var win = ReadTools.CrossPluginQuery(svc, type: "WEAP", limit: 3, max_chars: 250);
                Check(win.Contains("the returned WINDOW (3 rows of 8 total matches)") && !win.Contains("complete result"),
                      "a windowed spill says WINDOW (3 of 8), never 'complete result'");
                Check(win.Contains("beyond limit= are in NO file"), "…and names where the missing matches are (nowhere)");
                var winJson = ReadTools.CrossPluginQuery(svc, type: "WEAP", limit: 3, format: "json", max_chars: 250);
                using (var doc = Parse(winJson))
                {
                    var sp = doc.RootElement.GetProperty("spilled");
                    Check(!sp.GetProperty("complete").GetBoolean() && sp.GetProperty("row_count").GetInt32() == 3 && sp.GetProperty("total").GetInt32() == 8,
                          "…json: spilled.complete=false with row_count/total as data");
                }
                var whole = ReadTools.CrossPluginQuery(svc, type: "WEAP", format: "json", max_chars: 250);
                using (var doc = Parse(whole))
                    Check(doc.RootElement.GetProperty("spilled").GetProperty("complete").GetBoolean(),
                          "…control: an un-windowed spill is complete=true");

                // F4 — a POST-scan to_file write failure keeps the format contract.
                var blocked = Path.Combine(root, "blocked-tofile");
                File.WriteAllText(blocked, "a file where a directory should be");
                var tfj = ReadTools.CrossPluginQuery(svc, type: "WEAP", format: "json", to_file: Path.Combine(blocked, "sub", "x.jsonl"));
                using (var doc = Parse(tfj))
                    Check(doc.RootElement.GetProperty("error").GetString()!.Contains("could not write")
                          && doc.RootElement.GetProperty("epoch").GetString() == epochNow,
                          "a failed to_file under format=json returns a PARSEABLE {error, epoch} document");
                var tfb = ReadTools.BatchRecordDetail(svc, new[] { Fid(weapons[0]) }, format: "json", to_file: Path.Combine(blocked, "sub", "y.jsonl"));
                using (var doc = Parse(tfb))
                    Check(doc.RootElement.TryGetProperty("error", out _), "…batch twin parses too");

                // F5 — a whitespace-only list element is a per-item error, never a fake internal failure.
                var ws = ReadTools.BatchRecordDetail(svc, new[] { Fid(weapons[0]), "  " });
                Check(ws.StartsWith("batch: 2 records") && ws.Contains("bad FormID") && !ws.Contains("failed unexpectedly"),
                      "a whitespace-only formids element stays a per-item error (the batch survives)");

                // F2 — path reservation is atomic: two same-second reservations get DIFFERENT names, both created.
                var p1 = ResultsStore.NextPath("housecarl_cross_plugin_query", epochNow);
                var p2 = ResultsStore.NextPath("housecarl_cross_plugin_query", epochNow);
                Check(p1 != p2 && File.Exists(p1) && File.Exists(p2),
                      "same-second reservations get distinct names because reserving CREATES the file");
                ResultsStore.Release(p1); ResultsStore.Release(p2);
                Check(!File.Exists(p1) && !File.Exists(p2), "…and Release cleans a failed spill's reservation");

                // F6 — orphaned Writer temps are swept by the age prune.
                var orphan = Path.Combine(resultsDir, "cross_plugin_query_x.jsonl.tmp-deadbeef");
                File.WriteAllText(orphan, "half-written");
                File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddDays(-(ResultsStore.PruneAfterDays + 1)));
                ResultsStore.Release(ResultsStore.NextPath("housecarl_resolve", epochNow));   // any write-time prune pass
                Check(!File.Exists(orphan), "an old orphaned .jsonl.tmp-* is pruned like any stale spill");

                // F7 — to_file may not point into the pruned results dir.
                var collide = ReadTools.CrossPluginQuery(svc, type: "WEAP", to_file: Path.Combine(resultsDir, "mine.jsonl"));
                Check(collide.StartsWith("error:") && collide.Contains("pruned by age"),
                      "to_file into the server results dir refuses naming the prune hazard");
            }

            // ---- 8: PR #306 re-review fold — error rows are not identity-bearing ----
            Console.WriteLine();
            Console.WriteLine("--- 8 (PR #306 re-review): a legitimately-produced artifact with error rows re-enters on its RESOLVED rows ---");
            {
                // A resolve artifact whose inputs included garbage: the error row keeps the caller's RAW token as
                // its formid — a legitimate, manifest-matching artifact whose identity column is not all FormIDs.
                var mixed = Path.Combine(work, "mixed.jsonl");
                ReadTools.Resolve(svc, new[] { "garbage", Fid(weapons[0]), Fid(weapons[1]) }, to_file: mixed);
                var (mm, mtoks, merr) = ResultArtifact.ReadIdentity(mixed, File.ReadAllText(mixed));
                Check(merr is null && mm!.RowCount == 3 && mtoks!.Count == 2,
                      "the artifact keeps its error row (rows=3) while identity extraction yields the 2 RESOLVED formids");
                Check(ReadTools.BatchRecordDetail(svc, new[] { "@" + mixed }).StartsWith("batch: 2 records"),
                      "…formids=@mixed-artifact re-enters on the resolved rows — no was-it-edited misdiagnosis");
                var qm = ReadTools.CrossPluginQuery(svc, type: "WEAP", where: new[] { $"formid in @{mixed}" });
                Check(qm.Contains("2 matches"), "…and the where-grammar membership test agrees (2 of 8 weapons)");

                // batch's parse-failure rows (the null FormKey) are error rows too — same skip, same clean re-entry.
                var bmixed = Path.Combine(work, "bmixed.jsonl");
                ReadTools.BatchRecordDetail(svc, new[] { "notaformid", Fid(weapons[2]) }, to_file: bmixed);
                var (bm, btoks, berr) = ResultArtifact.ReadIdentity(bmixed, File.ReadAllText(bmixed));
                Check(berr is null && bm!.RowCount == 2 && btoks is [var only] && only == Fid(weapons[2]),
                      "a batch artifact's parse-failure row (null FormKey) is skipped the same way");

                // All-error artifact: refuse by the REAL cause, not tampering.
                var allErr = Path.Combine(work, "allerr.jsonl");
                ReadTools.Resolve(svc, new[] { "garbage1", "garbage2" }, to_file: allErr);
                var reenter = ReadTools.BatchRecordDetail(svc, new[] { "@" + allErr });
                Check(reenter.StartsWith("error:") && reenter.Contains("ERROR rows") && !reenter.Contains("was it edited"),
                      "an all-error artifact refuses naming the real cause (failed inputs), never accusing the file");
            }
        }
        finally
        {
            ResultsStore.OverrideDirForTests = savedOverride;
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort temp cleanup */ }
        }

        Console.WriteLine();
        Console.WriteLine($"[artifact-guard] {(fail == 0 ? "PASS — results decouple from renders, and artifacts re-enter honestly." : $"FAIL — {fail} check(s) failed.")}");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>The group_by artifact arm 2 wrote — one place for the path so arms 2 and 4 can't drift.</summary>
    static string gArtifactPathFor(string work) => Path.Combine(work, "groups.jsonl");
}

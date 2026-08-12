using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for the write tools' DRY-RUN mode (#225: set_field / bulk_apply /
/// forward_record with dry_run=true). The design claim under guard: a dry run is the REAL write pipeline HALTED at
/// the point of no return (the Phase-4 serialize) — never a parallel validate-lite that could drift from the write
/// it predicts. Each property is RED-provable:
///
///   NOTHING WRITTEN     — a successful fresh-lane dry run leaves the mods dir WITHOUT a new folder (ResolveOutputPath
///                         create:false), an into= dry run leaves the extended patch byte-identical, an in-place dry
///                         run leaves the target byte-identical. RED if any file/folder appears or changes.
///   SAME REFUSAL        — a malformed op refuses through dry_run with the EXACT string the real call gives (same
///                         resolve + pre-flight code path, by construction). RED if the texts diverge.
///   PREDICTION HOLDS    — a dry run's would-be output path, per-op After value, and expected-master preview all
///                         match what the subsequent REAL write of the same op produces. RED on any mismatch.
///   SERIALIZE PRE-EMPT  — an op composing a FormLink into a plugin NOT in the load order: the real write fails only
///                         AT the serialize (MissingModException); the dry run must refuse the same call with the
///                         missing plugin NAMED (the membership test is the serialize's own condition). RED if the
///                         dry run says "would apply" about a write that then fails.
///   CONSENT READ-ONLY   — an in-place dry run needs NO acknowledge (nothing is touched), NEVER records consent
///                         (a real write afterwards still shows the first-touch prompt), and NOTES the pending
///                         consent. RED if it prompts, records, or stays silent.
///   CONTRACT UNCHANGED  — the in_place/target/into mutual-exclusion refusals fire identically under dry_run.
///   RENDER HONESTY      — the rendered confirmation for a dry outcome leads with DRY RUN/nothing-written and never
///                         reads like a write ("wrote ..."), incl. the forward renderer's would-be phrasing and the
///                         full_readback deep dump labeled as the IN-MEMORY preview (arm J).
///   NULL-ARM PARITY     — a composed record missing a required polymorphic arm (a Condition without its Data arm)
///                         refuses through dry_run with the NAMED null-arm framing the real serialize re-stamp gives,
///                         never the opaque bare NRE; the real call refuses too (arm I). RED if either lies.
///   FORWARD IN-PLACE    — the consent-bypass logic is DUPLICATED in ForwardRecordsInPlace; arm K guards that copy
///                         (arm F guards the ApplyEdits copy), so an edit to either alone goes RED.
///   FROM_FILE PARITY    — a bulk_apply ops manifest (#224 from_file=) parses to the SAME BulkOp[] and rides the
///                         SAME ApplyEdits call as inline ops: manifest + dry_run renders IDENTICALLY to the same
///                         ops inline (the issue's stated pairing needs zero extra code — arm L proves it), a real
///                         manifest write lands, and the file contract (operations XOR from_file, absolute path,
///                         readable, valid JSON with line+column on failure, array root, non-empty, no unknown op
///                         members) refuses NAMED with nothing written. RED on any divergence or silent drop.
///
/// Self-contained: synthesizes a master + a user override in a synthetic MO2 instance in TEMP (the WriteMutexProbe
/// pattern — the full SERVICE path runs: ResolveOutputPath, the write gate, the in-place consent store) and generates
/// the validator corpus BY CONSTRUCTION in-process.
/// Run: dotnet run --project src/housecarl-generator dry-run-guard
/// </summary>
internal static class DryRunProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" dry-run guard — the real pipeline halted, nothing written (#225)");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var root = Path.Combine(Path.GetTempPath(), "hc-dry-run-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            // ---- synthetic MO2 instance (the established synth-instance pattern): one master + one user override ----
            string instance = Path.Combine(root, "instance");
            string profiles = Path.Combine(instance, "profiles", "Default");
            string mods = Path.Combine(instance, "mods");
            Directory.CreateDirectory(profiles); Directory.CreateDirectory(mods);
            Directory.CreateDirectory(Path.Combine(root, "game", "Data"));
            File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
                "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                + Path.Combine(root, "game").Replace(@"\", @"\\") + ")\r\n");

            var mKey = new ModKey("HcDryMaster", ModType.Master);
            var masterDir = Path.Combine(mods, "MasterMod");
            var userDir = Path.Combine(mods, "UserMod");
            Directory.CreateDirectory(masterDir); Directory.CreateDirectory(userDir);
            FormKey weapFk, weap2Fk, mgefFk;
            {
                var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
                var w = m.Weapons.AddNew(); w.EditorID = "HcDryWeap"; w.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1 };
                var w2 = m.Weapons.AddNew(); w2.EditorID = "HcDryWeap2"; w2.BasicStats = new WeaponBasicStats { Damage = 5 };
                var mg = m.MagicEffects.AddNew(); mg.EditorID = "HcDryMgef";   // hosts a Conditions list (the null-arm compose arm I)
                weapFk = w.FormKey; weap2Fk = w2.FormKey; mgefFk = mg.FormKey;
                m.BeginWrite.ToPath(Path.Combine(masterDir, mKey.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            }
            string userPath = Path.Combine(userDir, "HcDryUser.esp");
            using (var mOv = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(masterDir, mKey.FileName.String), SkyrimRelease.SkyrimSE))
            {
                var u = new SkyrimMod(new ModKey("HcDryUser", ModType.Plugin), SkyrimRelease.SkyrimSE);
                var uw = u.Weapons.GetOrAddAsOverride(mOv.Weapons.First(x => x.FormKey == weapFk));
                uw.BasicStats!.Damage = 20;
                u.BeginWrite.ToPath(userPath).WithLoadOrder(new ISkyrimModGetter[] { mOv }).Write();
            }
            File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + mKey.FileName + "\r\nHcDryUser.esp\r\n");
            File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + mKey.FileName + "\r\n*HcDryUser.esp\r\n");
            File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+UserMod\r\n+MasterMod\r\n");

            var genDir = Path.Combine(root, "corpus-gen");
            CorpusGenerator.GenerateAll(genDir, Path.Combine(root, "corpus-ref"));
            CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

            string fid = $"{weapFk.ID:X6}:{mKey.FileName}";
            string fid2 = $"{weap2Fk.ID:X6}:{mKey.FileName}";
            string fidMg = $"{mgefFk.ID:X6}:{mKey.FileName}";
            var store = new UserConfigStore(Path.Combine(root, "houseCARL.user.json"));
            using var svc = LoadOrderService.WithInstance(instance, 0, store);
            svc.Stats();                                                       // warm the lazy index once, off the clock

            static BulkOp DamageOp(string fid, int dmg) =>
                new() { Formid = fid, FieldPath = "BasicStats.Damage", Verb = "Set", Value = dmg.ToString() };
            string[] ModFolders() => Directory.GetDirectories(mods).Select(p => Path.GetFileName(p)!).OrderBy(x => x).ToArray();

            // ---- A: fresh-lane dry run — success report, NOTHING on disk, honest preview ----
            Console.WriteLine("--- A: fresh-patch dry run writes nothing ---");
            {
                var before = ModFolders();
                var o = svc.ApplyEdits(new[] { DamageOp(fid, 77) }, "DryA", null, dryRun: true);
                var after = ModFolders();
                Check(o.Success && o.DryRun && !o.InPlace, $"dry run succeeds with DryRun flagged  [{o.Error ?? "ok"}]");
                Check(before.SequenceEqual(after), $"no mod folder appeared (mods dir: {before.Length} -> {after.Length} entries)");
                Check(o.Bytes == 0, $"bytes=0 (nothing serialized), got {o.Bytes}");
                Check(o.Ops.Count == 1 && (o.Ops[0].After?.Contains("77") ?? false),
                    $"per-op After carries the would-be value  [{o.Ops[0].After ?? "<null>"}]");
                Check(o.Masters.Count == 1 && o.Masters[0].Equals(mKey.FileName.String, StringComparison.OrdinalIgnoreCase),
                    $"expected-master preview = [{string.Join(",", o.Masters)}] (want only {mKey.FileName})");
                var text = WriteTools.Render(o);
                Check(text.Contains("DRY RUN") && text.Contains("NOTHING was written") && !text.Contains("wrote "),
                    "render leads with DRY RUN / nothing-written and never reads like a write");
            }

            // ---- B: a malformed op refuses with the EXACT string the real call gives ----
            Console.WriteLine("--- B: dry refusal == real refusal (same pipeline by construction) ---");
            {
                var bad = new[] { new BulkOp { Formid = fid, FieldPath = "BasicStats.Nope", Verb = "Set", Value = "1" } };
                var dry = svc.ApplyEdits(bad, "DryB", null, dryRun: true);
                var real = svc.ApplyEdits(bad, "DryB", null);
                Check(!dry.Success && !real.Success && dry.Error == real.Error,
                    $"identical refusal text  [dry: {Snip(dry.Error)}]");
                Check(!Directory.Exists(Path.Combine(mods, "DryB")), "neither attempt left a folder behind");
            }

            // ---- C: the dry run's predictions hold against the subsequent REAL write ----
            Console.WriteLine("--- C: prediction parity (path, After value, masters) ---");
            {
                var op = new[] { DamageOp(fid, 88) };
                var dry = svc.ApplyEdits(op, "DryC", null, dryRun: true);
                var real = svc.ApplyEdits(op, "DryC", null);
                Check(dry.Success && real.Success, $"both succeed  [dry: {dry.Error ?? "ok"} | real: {real.Error ?? "ok"}]");
                Check(dry.OutputPath == real.OutputPath,
                    $"would-be path == real path  [{Path.GetFileName(dry.OutputPath)} vs {Path.GetFileName(real.OutputPath)}]");
                Check(dry.Ops[0].After == real.Ops[0].After,
                    $"would-be After == real After  [{dry.Ops[0].After} vs {real.Ops[0].After}]");
                Check(dry.Masters.SequenceEqual(real.Masters, StringComparer.OrdinalIgnoreCase),
                    $"expected masters == real lean header  [{string.Join(",", dry.Masters)} vs {string.Join(",", real.Masters)}]");
            }

            // ---- D: into= extend dry run — the on-disk patch stays byte-identical ----
            Console.WriteLine("--- D: into= dry run leaves the extended patch untouched ---");
            {
                var seed = svc.ApplyEdits(new[] { DamageOp(fid, 50) }, "DryD", null);
                Check(seed.Success, $"seed patch written  [{seed.Error ?? "ok"}]");
                var bytesBefore = File.ReadAllBytes(seed.OutputPath);
                var o = svc.ApplyEdits(new[] { new BulkOp { Formid = fid, FieldPath = "BasicStats.Weight", Verb = "Set", Value = "9" } },
                    null, "DryD", dryRun: true);
                Check(o.Success && o.DryRun && o.Extended, $"extend dry run succeeds with Extended flagged  [{o.Error ?? "ok"}]");
                Check(File.ReadAllBytes(seed.OutputPath).AsSpan().SequenceEqual(bytesBefore),
                    "the extended patch's on-disk bytes are unchanged");
            }

            // ---- E: the serialize's missing-master failure is PRE-EMPTED, named, by the dry run ----
            Console.WriteLine("--- E: unresolvable FormLink — dry refuses named; real fails only at serialize ---");
            {
                var ghost = new[] { new BulkOp { Formid = fid, FieldPath = "Keywords", Verb = "Add", Value = "000ABC:Ghost.esp" } };
                var dry = svc.ApplyEdits(ghost, "DryE", null, dryRun: true);
                Check(!dry.Success && (dry.Error?.Contains("Ghost.esp") ?? false) && (dry.Error?.Contains("NOT active") ?? false),
                    $"dry run refuses, naming the missing plugin  [{Snip(dry.Error)}]");
                var real = svc.ApplyEdits(ghost, "DryE", null);
                Check(!real.Success && (real.Error?.Contains("MissingModException") ?? false),
                    $"the real write fails AT the serialize boundary (MissingModException) — the exact condition the dry refusal pre-empts  [{Snip(real.Error)}]");
                Check(!Directory.Exists(Path.Combine(mods, "DryE")), "neither attempt left a folder behind");
            }

            // ---- F: in-place dry run — read-only on the consent axis AND the file ----
            Console.WriteLine("--- F: in-place dry run (no prompt, no consent recorded, file untouched) ---");
            {
                var fileBefore = File.ReadAllBytes(userPath);
                var dryAck = svc.ApplyEdits(new[] { DamageOp(fid, 61) }, null, null,
                    target: "HcDryUser.esp", inPlace: true, acknowledge: true, dryRun: true);
                Check(dryAck.Success && dryAck.DryRun && dryAck.InPlace && !dryAck.NeedsAcknowledge,
                    $"dry run + acknowledge=true succeeds without the prompt  [{dryAck.Error ?? "ok"}]");
                var dry = svc.ApplyEdits(new[] { DamageOp(fid, 62) }, null, null,
                    target: "HcDryUser.esp", inPlace: true, dryRun: true);
                Check(dry.Success && !dry.NeedsAcknowledge && (dry.Note?.Contains("PENDING") ?? false),
                    $"dry run without acknowledge succeeds AND notes the pending consent  [{Snip(dry.Note)}]");
                Check(File.ReadAllBytes(userPath).AsSpan().SequenceEqual(fileBefore), "the target file is byte-identical");
                var real = svc.ApplyEdits(new[] { DamageOp(fid, 63) }, null, null, target: "HcDryUser.esp", inPlace: true);
                Check(real.NeedsAcknowledge,
                    "a REAL in-place write afterwards still shows the first-touch prompt (no dry run recorded consent)");
            }

            // ---- G: the lane contract refuses identically under dry_run ----
            Console.WriteLine("--- G: contract refusals unchanged under dry_run ---");
            {
                var op = new[] { DamageOp(fid, 1) };
                var noTarget = svc.ApplyEdits(op, null, null, inPlace: true, dryRun: true);
                var withInto = svc.ApplyEdits(op, null, "DryD", target: "HcDryUser.esp", inPlace: true, dryRun: true);
                Check(!noTarget.Success && (noTarget.Error?.Contains("requires target=") ?? false)
                   && !withInto.Success && (withInto.Error?.Contains("mutually exclusive") ?? false),
                    "in_place⇔target and in_place⊥into= refusals fire as on the real path");
            }

            // ---- H: forward_record dry run — would-copy report, nothing on disk, same refusal as real ----
            Console.WriteLine("--- H: forward dry run ---");
            {
                var before = ModFolders();
                var o = svc.ForwardRecords(new[] { fid }, mKey.FileName.String, "DryH", null, dryRun: true);
                Check(o.Success && o.DryRun && o.Forwarded.Count == 1
                   && o.Forwarded[0].FromPlugin.Equals(mKey.FileName.String, StringComparison.OrdinalIgnoreCase)
                   && !o.Forwarded[0].WasAlreadyWinner,
                    $"dry forward reports the would-be copy (source={o.Forwarded.FirstOrDefault()?.FromPlugin}, priorWinner={o.Forwarded.FirstOrDefault()?.PriorWinner})  [{o.Error ?? "ok"}]");
                Check(before.SequenceEqual(ModFolders()), "no mod folder appeared");
                var text = WriteTools.RenderForward(o);
                Check(text.Contains(WriteSentences.DryRunHeader, StringComparison.Ordinal)
                      && text.Contains("would be copied from")
                      // Any masters LINE, not the empty-set spelling. This outcome forwards from a REAL master, so
                      // pinning "masters: (none)" would be a string the render can never emit — an assertion that
                      // passes whatever happens, which is what it briefly became.
                      && !text.Contains("\n" + WriteSentences.Masters(Array.Empty<string>()).Split('(')[0]),
                    "forward render uses the would-be phrasing (+ the expected-masters preview, not a real header)");
                var dryMiss = svc.ForwardRecords(new[] { fid2 }, "HcDryUser.esp", "DryH2", null, dryRun: true);
                var realMiss = svc.ForwardRecords(new[] { fid2 }, "HcDryUser.esp", "DryH2", null);
                Check(!dryMiss.Success && dryMiss.Error == realMiss.Error,
                    $"a source that doesn't define the record refuses identically  [{Snip(dryMiss.Error)}]");
            }

            // ---- I: composed null-arm (a Condition without its Data arm) — the serialize-only refusal class the
            //      dry run's link walk catches; both must refuse, and the dry refusal must carry the NAMED null-arm
            //      framing, not the opaque bare NRE (PR #240 review MEDIUM 1) ----
            Console.WriteLine("--- I: compose missing a required arm — dry refuses NAMED; real refuses at serialize ---");
            {
                var op = new[] { new BulkOp { Formid = fidMg, FieldPath = "Conditions", Verb = "Add",
                    Compose = new StructInput { Type = "ConditionFloat", Fields = new() { ["ComparisonValue"] = "1" } } } };
                var dry = svc.ApplyEdits(op, "DryI", null, dryRun: true);
                Check(!dry.Success && (dry.Error?.Contains("Data arm") ?? false) && (dry.Error?.Contains("required") ?? false),
                    $"dry run refuses with the named null-arm framing  [{Snip(dry.Error)}]");
                var real = svc.ApplyEdits(op, "DryI", null);
                Check(!real.Success, $"the real write refuses too (the serialize null-arm re-stamp) — the dry refusal predicts a real failure  [{Snip(real.Error)}]");
                Check(!Directory.Exists(Path.Combine(mods, "DryI")), "neither attempt left a folder behind");
            }

            // ---- J: full_readback + dry_run — the in-memory preview path, rendered honestly ----
            Console.WriteLine("--- J: full_readback dry run reads the in-memory would-be record ---");
            {
                var o = svc.ApplyEdits(new[] { DamageOp(fid, 91) }, "DryJ", null, fullReadback: true, dryRun: true);
                Check(o.Success && o.DryRun && o.ReadBack is { Count: 1 } && o.ReadBack[0].Record is not null,
                    $"in-memory read-back present and clean  [{o.Error ?? o.ReadBack?[0].Error ?? "ok"}]");
                var text = WriteTools.Render(o, 0, fullDump: true);
                Check(text.Contains("full preview") && text.Contains("nothing is on disk") && text.Contains("91"),
                    "render labels the deep dump as the in-memory preview (never implying a file exists) and carries the would-be value");
                Check(!Directory.Exists(Path.Combine(mods, "DryJ")), "no folder appeared despite the deep read-back");
            }

            // ---- K: forward IN-PLACE dry run — the DUPLICATED consent-bypass copy in ForwardRecordsInPlace
            //      (PR #240 review MEDIUM 2: arm F guards only the ApplyEdits copy) ----
            Console.WriteLine("--- K: forward in-place dry run (no prompt, no consent recorded, file untouched) ---");
            {
                var fileBefore = File.ReadAllBytes(userPath);
                var dry = svc.ForwardRecords(new[] { fid }, mKey.FileName.String, null, null,
                    target: "HcDryUser.esp", inPlace: true, dryRun: true);
                Check(dry.Success && dry.DryRun && dry.InPlace && !dry.NeedsAcknowledge && (dry.Note?.Contains("PENDING") ?? false),
                    $"dry forward succeeds without the prompt AND notes the pending consent  [{dry.Error ?? Snip(dry.Note)}]");
                Check(File.ReadAllBytes(userPath).AsSpan().SequenceEqual(fileBefore), "the target file is byte-identical");
                var real = svc.ForwardRecords(new[] { fid }, mKey.FileName.String, null, null,
                    target: "HcDryUser.esp", inPlace: true);
                Check(real.NeedsAcknowledge,
                    "a REAL in-place forward afterwards still shows the first-touch prompt (no dry run recorded consent)");
            }

            // ---- L: #224 from_file ops manifest — the SAME pipeline as inline ops, file contract refuses named ----
            Console.WriteLine("--- L: bulk_apply from_file manifest (#224) ---");
            {
                string ManifestFile(string name, string content)
                {
                    var p = Path.Combine(root, name);
                    File.WriteAllText(p, content);
                    return p;
                }
                string manifest = ManifestFile("ops-manifest.json",
                    $"[{{\"formid\": \"{fid}\", \"field_path\": \"BasicStats.Damage\", \"verb\": \"Set\", \"value\": \"73\"}}]");

                // parity — the issue's stated pairing: manifest + dry_run reports IDENTICALLY to the same ops inline
                var before = ModFolders();
                var dryFile = WriteTools.BulkApply(svc, from_file: manifest, patch_name: "DryL", dry_run: true);
                var dryInline = WriteTools.BulkApply(svc, operations: new[] { DamageOp(fid, 73) }, patch_name: "DryL", dry_run: true);
                Check(dryFile == dryInline, "manifest + dry_run renders IDENTICALLY to the same ops inline (same ApplyEdits by construction)");
                Check(dryFile.Contains("DRY RUN") && before.SequenceEqual(ModFolders()), "the manifest dry run wrote nothing");

                // contract refusals — each named, nothing written
                var both = WriteTools.BulkApply(svc, operations: new[] { DamageOp(fid, 1) }, from_file: manifest);
                var neither = WriteTools.BulkApply(svc);
                Check(both.StartsWith("error:") && both.Contains("mutually exclusive")
                   && neither.StartsWith("error:") && neither.Contains("operations") && neither.Contains("from_file"),
                    $"operations XOR from_file (both and neither refuse named)  [{Snip(neither)}]");
                var emptyInline = WriteTools.BulkApply(svc, operations: Array.Empty<BulkOp>());
                Check(emptyInline.StartsWith("error:") && emptyInline.Contains("operations is empty"),
                    "an explicit empty INLINE array keeps its existing refusal");
                var blank = WriteTools.BulkApply(svc, from_file: "   ");
                Check(blank.StartsWith("error:") && blank.Contains("from_file is empty"),
                    $"an explicit blank from_file refuses NAMED, never silently reinterpreted as absent  [{Snip(blank)}]");
                var rel = WriteTools.BulkApply(svc, from_file: "ops.json");
                Check(rel.StartsWith("error:") && rel.Contains("ABSOLUTE"), $"a relative path refuses  [{Snip(rel)}]");
                var unreadable = WriteTools.BulkApply(svc, from_file: Path.Combine(root, "no-such-manifest.json"));
                Check(unreadable.StartsWith("error:") && unreadable.Contains("could not read") && unreadable.Contains("no-such-manifest.json"),
                    "an unreadable file refuses naming the path");
                var badJson = WriteTools.BulkApply(svc, from_file: ManifestFile("bad.json", "[{\"formid\": }]"));
                Check(badJson.StartsWith("error:") && badJson.Contains("bad.json") && badJson.Contains("line "),
                    $"invalid JSON refuses naming the file + position  [{Snip(badJson)}]");
                var objRoot = WriteTools.BulkApply(svc, from_file: ManifestFile("obj.json", "{\"operations\": []}"));
                Check(objRoot.StartsWith("error:") && objRoot.Contains("JSON ARRAY"),
                    $"a non-array root refuses naming the expected shape  [{Snip(objRoot)}]");
                var emptyArr = WriteTools.BulkApply(svc, from_file: ManifestFile("empty.json", "[]"));
                Check(emptyArr.StartsWith("error:") && emptyArr.Contains("empty array") && emptyArr.Contains("empty.json"),
                    "an empty manifest array refuses like empty operations, naming the file");
                var nullEl = WriteTools.BulkApply(svc, from_file: ManifestFile("nullel.json",
                    $"[null, {{\"formid\": \"{fid}\", \"field_path\": \"BasicStats.Damage\", \"value\": \"1\"}}]"));
                Check(nullEl.StartsWith("error:") && nullEl.Contains("[0]"),
                    $"a null element refuses naming its index  [{Snip(nullEl)}]");
                var typo = WriteTools.BulkApply(svc, from_file: ManifestFile("typo.json",
                    $"[{{\"formid\": \"{fid}\", \"feild_path\": \"BasicStats.Damage\", \"value\": \"5\"}}]"));
                Check(typo.StartsWith("error:") && typo.Contains("feild_path"),
                    $"a misspelled op member refuses BY NAME (never the inline binder's silent drop)  [{Snip(typo)}]");
                Check(before.SequenceEqual(ModFolders()), "none of the refusals left anything behind");

                // the real write from the manifest lands
                var real = WriteTools.BulkApply(svc, from_file: manifest, patch_name: "DryL");
                Check(!real.StartsWith("error:") && real.Contains("wrote DryL.esp")
                   && ModFolders().Except(before).Any(f => f.Contains("DryL")),
                    $"a real manifest write lands (a new DryL mod folder + the wrote confirmation)  [{Snip(real)}]");
            }

            Console.WriteLine();
            Console.WriteLine(fail == 0 ? "ALL PASS" : $"{fail} FAILURE(S)");
            return fail == 0 ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    static string Snip(string? s) => s is null ? "<null>" : s.Length <= 140 ? s.Replace('\n', ' ') : s[..140].Replace('\n', ' ') + "…";
}

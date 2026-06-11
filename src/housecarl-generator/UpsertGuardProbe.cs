using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for the create_record into= UPSERT + atomic staged write (PR #44, outside
/// contribution by AlmightyChan, hardened in review), in the pattern of writelock-guard / formid-floor-guard.
/// Drives the REAL product paths (WritePatchBuilder.CreateRecords / Apply) against a synthesized master in TEMP.
/// Run: dotnet run --project src/housecarl-generator -- upsert-guard
///
/// Arms (ALL required — a GREEN must mean "the contract holds", never "the scenario doesn't arise here"):
///   RERUN     — re-running the same create against the same into= target REPLACES instead of appending: 1 copy of
///               each record, FormKeys stable, output byte-identical, list edits applied once, the persisted counter
///               unmoved, and EVERY replace surfaced on CreatedRecord.ReplacedExisting (a replace is never silent).
///               RED pre-PR-44 (dup-append: 2 copies, shifted FormKeys, growing file).
///   OVERRIDE  — a create whose editorid collides with an OVERRIDE the patch carries (another plugin's record) is
///               REFUSED loud, the file untouched, and the override's edited fields survive. RED on unhardened
///               PR #44 (the upsert matched overrides too and silently emitted a field-wiping override of the
///               original plugin's record — the master-blanking path).
///   CROSS-TYPE— an editorid collision across record types refuses loud (carried over from the contribution).
///   DUP       — duplicate same-editorid residue (the pre-fix bug's own product) refuses LOUD naming every copy,
///               replacing none — which FormKey survives is the caller's call (external references may point at
///               either). RED on unhardened PR #44 (FirstOrDefault replaced one copy silently, leaving the rest).
///   LOCKED    — a commit blocked by a foreign handle on the target (no FILE_SHARE_DELETE) fails LOUD with the
///               target's old bytes fully intact and no .housecarl-tmp residue — the staged write's failure
///               contract (the old in-place serialize had no such guarantee shape to test).
/// </summary>
public static class UpsertGuardProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — create_record into= upsert + staged write (PR #44)  ################");
        Console.WriteLine();

        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-upsert-guard");
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        Directory.CreateDirectory(tmpDir);

        // --- Setup: a master carrying a weapon (the override target) + a keyword (the created weapon's list edit),
        //     + the validator corpus. ---
        var mKey = new ModKey("HcUpsGdMaster", ModType.Master);
        string mPath = Path.Combine(tmpDir, mKey.FileName.String);
        FormKey masterWeapFk, masterKwFk;
        {
            var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
            var w = m.Weapons.AddNew(); w.EditorID = "HcUpsGdMasterWeap"; w.BasicStats = new WeaponBasicStats { Damage = 10 };
            masterWeapFk = w.FormKey;
            var k = m.Keywords.AddNew(); k.EditorID = "HcUpsGdMasterKw";
            masterKwFk = k.FormKey;
            m.BeginWrite.ToPath(mPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }
        var genDir = Path.Combine(tmpDir, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(tmpDir, "corpus-ref"));
        var rulebook = CorpusRulebook.Load(Path.Combine(genDir, "corpus.json"));
        string masterKwRef = $"{masterKwFk.ID:X6}:{masterKwFk.ModKey.FileName}";
        Console.WriteLine($"-- setup: master {mKey.FileName} with weapon {masterWeapFk} + keyword {masterKwFk}; corpus generated --");

        string pPath = Path.Combine(tmpDir, "HcUpsGuard.esp");
        WritePatchBuilder.CreateSpec[] Specs() => new[]
        {
            new WritePatchBuilder.CreateSpec { RecordType = "Keyword", EditorId = "HcUpsGdKw", Edits = Array.Empty<WriteRequest>() },
            new WritePatchBuilder.CreateSpec { RecordType = "Weapon", EditorId = "HcUpsGdWeap", Edits = new[]
            {
                new WriteRequest { RecordType = "Weapon", Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = "25" },
                new WriteRequest { RecordType = "Weapon", Path = new[] { "Keywords" }, Verb = "Add", Value = masterKwRef },
            } },
        };

        // --- RERUN: fresh create, then the identical create with extend=true. ---
        bool rerunOk = false;
        {
            FormKey kwFk1 = default, weapFk1 = default;
            bool freshOk = false, freshNotReplaced = false;
            using (var r = LoadOrderResolver.Build(new[] { mPath }))
            {
                var o = WritePatchBuilder.CreateRecords(r, rulebook, Specs(), pPath, extend: false);
                freshOk = o.Success;
                if (o.Success)
                {
                    kwFk1 = o.Created[0].FormKey; weapFk1 = o.Created[1].FormKey;
                    freshNotReplaced = o.Created.All(c => !c.ReplacedExisting);
                }
            }
            byte[] bytes1 = freshOk ? File.ReadAllBytes(pPath) : Array.Empty<byte>();

            bool rerunSuccess = false, flagsOk = false;
            using (var r = LoadOrderResolver.Build(new[] { mPath }))
            {
                var o = WritePatchBuilder.CreateRecords(r, rulebook, Specs(), pPath, extend: true);
                rerunSuccess = o.Success;
                if (o.Success) flagsOk = o.Created.Count == 2 && o.Created.All(c => c.ReplacedExisting);
            }

            int kwCount = 0, weapCount = 0, weapKwList = -1; FormKey kwFk2 = default, weapFk2 = default;
            if (rerunSuccess)
            {
                ISkyrimModGetter? ov = null;
                try
                {
                    ov = SkyrimMod.CreateFromBinaryOverlay(pPath, SkyrimRelease.SkyrimSE);
                    foreach (var k in ov.Keywords) if (k.EditorID == "HcUpsGdKw") { kwCount++; kwFk2 = k.FormKey; }
                    foreach (var w in ov.Weapons) if (w.EditorID == "HcUpsGdWeap") { weapCount++; weapFk2 = w.FormKey; weapKwList = w.Keywords?.Count ?? 0; }
                }
                finally { (ov as IDisposable)?.Dispose(); }
            }
            byte[] bytes2 = rerunSuccess ? File.ReadAllBytes(pPath) : Array.Empty<byte>();
            uint counter = rerunSuccess ? ReadDiskNextFormId(pPath) : 0;

            rerunOk = freshOk && freshNotReplaced && rerunSuccess && flagsOk
                      && kwCount == 1 && weapCount == 1 && weapKwList == 1
                      && kwFk2 == kwFk1 && weapFk2 == weapFk1
                      && bytes1.AsSpan().SequenceEqual(bytes2)
                      && counter == 0x802;
            Console.WriteLine($"   RERUN replaces, loudly, in place    : {(rerunOk ? $"PASS — 1/1 copies, stable FormKeys ({kwFk2}, {weapFk2}), byte-identical, both flagged REPLACED, counter 0x{counter:X6}" : $"FAIL — fresh={freshOk}/{freshNotReplaced} rerun={rerunSuccess} flags={flagsOk} counts={kwCount}/{weapCount} list={weapKwList} stable={kwFk2 == kwFk1 && weapFk2 == weapFk1} bytes={bytes1.Length}->{bytes2.Length} counter=0x{counter:X6}")}");
        }

        // --- OVERRIDE: an Apply-born override rides in the patch; a create colliding with ITS editorid must refuse,
        //     file untouched, override fields intact (the master-blanking guard). ---
        bool overrideOk = false;
        {
            bool applyOk;
            using (var r = LoadOrderResolver.Build(new[] { mPath }))
            {
                var edit = new WritePatchBuilder.PatchEdit { Target = masterWeapFk, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = "20" };
                applyOk = WritePatchBuilder.Apply(r, rulebook, new[] { edit }, pPath, extend: true).Success;
            }
            byte[] before = applyOk ? File.ReadAllBytes(pPath) : Array.Empty<byte>();

            bool refused = false; string? error = null;
            if (applyOk)
                using (var r = LoadOrderResolver.Build(new[] { mPath }))
                {
                    var clash = new[] { new WritePatchBuilder.CreateSpec { RecordType = "Weapon", EditorId = "HcUpsGdMasterWeap", Edits = Array.Empty<WriteRequest>() } };
                    var o = WritePatchBuilder.CreateRecords(r, rulebook, clash, pPath, extend: true);
                    refused = !o.Success; error = o.Error;
                }

            bool fileUntouched = applyOk && before.AsSpan().SequenceEqual(File.ReadAllBytes(pPath));
            bool overrideIntact = false;
            if (applyOk)
            {
                ISkyrimModGetter? ov = null;
                try
                {
                    ov = SkyrimMod.CreateFromBinaryOverlay(pPath, SkyrimRelease.SkyrimSE);
                    var w = ov.Weapons.FirstOrDefault(x => x.FormKey == masterWeapFk);
                    overrideIntact = w is not null && w.BasicStats?.Damage == 20;
                }
                finally { (ov as IDisposable)?.Dispose(); }
            }
            bool named = error is not null && error.Contains("override", StringComparison.OrdinalIgnoreCase);
            overrideOk = applyOk && refused && named && fileUntouched && overrideIntact;
            Console.WriteLine($"   OVERRIDE collision refused loud     : {(overrideOk ? "PASS — refused by name, file untouched, override Damage=20 intact" : $"FAIL — apply={applyOk} refused={refused} named={named} untouched={fileUntouched} intact={overrideIntact} error=[{error}]")}");
        }

        // --- CROSS-TYPE: a Keyword create colliding with the patch-defined weapon's editorid refuses loud. ---
        bool crossTypeOk = false;
        {
            using var r = LoadOrderResolver.Build(new[] { mPath });
            var clash = new[] { new WritePatchBuilder.CreateSpec { RecordType = "Keyword", EditorId = "HcUpsGdWeap", Edits = Array.Empty<WriteRequest>() } };
            var o = WritePatchBuilder.CreateRecords(r, rulebook, clash, pPath, extend: true);
            crossTypeOk = !o.Success && o.Error is not null && o.Error.Contains("not a Keyword", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   CROSS-TYPE collision refused loud   : {(crossTypeOk ? "PASS" : $"FAIL — success={o.Success} error=[{o.Error}]")}");
        }

        // --- DUP: duplicate same-editorid residue (the old bug's product) refuses LOUD naming every copy. ---
        bool dupOk = false;
        {
            string dupPath = Path.Combine(tmpDir, "HcUpsGuardDup.esp");
            FormKey dup1, dup2;
            {
                var p = new SkyrimMod(new ModKey("HcUpsGuardDup", ModType.Plugin), SkyrimRelease.SkyrimSE);
                var k1 = p.Keywords.AddNew(); k1.EditorID = "HcUpsGdDupKw"; dup1 = k1.FormKey;
                var k2 = p.Keywords.AddNew(); k2.EditorID = "HcUpsGdDupKw"; dup2 = k2.FormKey;
                p.BeginWrite.ToPath(dupPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();
            }
            byte[] before = File.ReadAllBytes(dupPath);
            bool refused; string? error;
            using (var r = LoadOrderResolver.Build(new[] { mPath }))
            {
                var spec = new[] { new WritePatchBuilder.CreateSpec { RecordType = "Keyword", EditorId = "HcUpsGdDupKw", Edits = Array.Empty<WriteRequest>() } };
                var o = WritePatchBuilder.CreateRecords(r, rulebook, spec, dupPath, extend: true);
                refused = !o.Success; error = o.Error;
            }
            bool namedBoth = error is not null && error.Contains(dup1.ToString()) && error.Contains(dup2.ToString());
            bool untouched = before.AsSpan().SequenceEqual(File.ReadAllBytes(dupPath));
            dupOk = refused && namedBoth && untouched;
            Console.WriteLine($"   DUP residue refused naming copies   : {(dupOk ? $"PASS — both copies named ({dup1}, {dup2}), file untouched" : $"FAIL — refused={refused} namedBoth={namedBoth} untouched={untouched} error=[{error}]")}");
        }

        // --- LOCKED: a foreign no-delete-share handle on the target blocks the COMMIT; the failure must be loud,
        //     the old file intact, the temp cleaned. ---
        bool lockedOk = false;
        {
            byte[] before = File.ReadAllBytes(pPath);
            bool failed; string? error;
            using (new FileStream(pPath, FileMode.Open, FileAccess.Read, FileShare.Read))   // readers allowed, delete/replace blocked
            using (var r = LoadOrderResolver.Build(new[] { mPath }))
            {
                var spec = new[] { new WritePatchBuilder.CreateSpec { RecordType = "Keyword", EditorId = "HcUpsGdKwLocked", Edits = Array.Empty<WriteRequest>() } };
                var o = WritePatchBuilder.CreateRecords(r, rulebook, spec, pPath, extend: true);
                failed = !o.Success; error = o.Error;
            }
            bool intact = before.AsSpan().SequenceEqual(File.ReadAllBytes(pPath));
            bool noResidue = !Directory.EnumerateDirectories(tmpDir, ".housecarl-tmp", SearchOption.AllDirectories).Any();
            bool named = error is not null && error.Contains("writing the patch after create failed", StringComparison.OrdinalIgnoreCase);
            lockedOk = failed && named && intact && noResidue;
            Console.WriteLine($"   LOCKED commit fails loud + intact   : {(lockedOk ? "PASS — loud refusal, old bytes intact, no temp residue" : $"FAIL — failed={failed} named={named} intact={intact} noResidue={noResidue} error=[{error}]")}");
        }

        Console.WriteLine();
        bool pass = rerunOk && overrideOk && crossTypeOk && dupOk && lockedOk;
        Console.WriteLine($"=== upsert-guard: {(pass ? "PASS" : "FAIL")} ===");
        try { Directory.Delete(tmpDir, recursive: true); } catch { }
        return pass ? 0 : 1;
    }

    /// <summary>Read the persisted HEDR.NextObjectID by reopening the file as a binary overlay (same helper shape
    /// as formid-floor-guard).</summary>
    static uint ReadDiskNextFormId(string path)
    {
        ISkyrimModGetter? ov = null;
        try { ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); return ov.ModHeader.Stats.NextFormID; }
        finally { (ov as IDisposable)?.Dispose(); }
    }
}

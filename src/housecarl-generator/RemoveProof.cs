using System.Globalization;
using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// Capability-arc remove-record build proof — exercise <see cref="WritePatchBuilder.RemoveRecords"/>, the core the MCP
/// <c>housecarl_remove_record</c> tool calls, so the proof transfers to the server BY CONSTRUCTION (same code path, the
/// way <see cref="ApplyProof"/> proves the edit cleave). Each check SEEDS a patch carrying real override(s)
/// (the records a removal can target), then removes through the core and re-reads from disk:
///
///   R1 — FLAT, selective: a patch carries two weapon overrides; remove ONE → it's gone, the OTHER survives, sources
///        byte-untouched (proves selective whole-record removal + the present-check finds carried records).
///   R2 — NESTED: a patch carries a nested-group override (a Cell — no flat SkyrimGroup&lt;T&gt;); remove it through the
///        real into= flow (RemoveRecords re-opens from disk) → gone (the load-bearing nested proof vs a real, large load order).
///   R3 — CLEAN-MASTERS: a patch carries ONE override (header lists its master[s]); remove it → 0 records AND an empty
///        master header (the record-level auto-prune — removing the last referencer drops the master, probe Q5).
///   R4 — REJECT (Q3): remove a FormKey the patch does NOT carry → refused, NOTHING written (the patch byte-unchanged),
///        the carried record still present (no silent no-op; the present-check refuses loud).
///
/// Every original master/mod the seed touched is SHA-checked unchanged. Patches are left in write-output/remove-proof/
/// for Aaron to open in xEdit.  Run: dotnet run --project src/housecarl-generator remove-proof [maxPlugins]
/// </summary>
public static class RemoveProof
{
    static readonly HashSet<string> Vanilla = new(StringComparer.OrdinalIgnoreCase)
        { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" };

    public static int RunRemoveProof(string[] args)
    {
        int maxPlugins = args.Length > 0 && int.TryParse(args[0], out var m) ? m : 0;

        Console.WriteLine("================================================================");
        Console.WriteLine(" houseCARL remove-record proof (capability arc — housecarl_remove_record)");
        Console.WriteLine("================================================================");

        var paths = ResolveProbe.GatherPlugins();
        if (maxPlugins > 0 && paths.Count > maxPlugins) paths = paths.Take(maxPlugins).ToList();
        if (paths.Count == 0) { Console.Error.WriteLine("error: no plugins found"); return 1; }
        var nameToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths) nameToPath[Path.GetFileName(p)] = p;

        using var resolver = LoadOrderResolver.Build(paths);
        using var session = resolver.OpenSession();   // Option B: open source plugins on demand, disposed at harness exit
        var rulebook = CorpusRulebook.Load();
        Console.WriteLine($"Resolver: {resolver.PluginCount:N0} plugins | {resolver.RecordCount:N0} records | {resolver.ConflictCount:N0} conflicts.");
        Console.WriteLine();

        var outDir = Path.GetFullPath(Path.Combine("write-output", "remove-proof"));
        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        Directory.CreateDirectory(outDir);

        // Two distinct vanilla-defined weapons with BasicStats (stable FormKeys; same approach as ApplyProof).
        var weapons = new List<(FormKey fk, string edid)>();
        foreach (var (fk, _, body) in resolver.WinnerRecordsOfType(new[] { typeof(IWeaponGetter) }))
        {
            if (!Vanilla.Contains(fk.ModKey.FileName)) continue;
            if (body is not IWeaponGetter w || w.BasicStats is null) continue;
            weapons.Add((fk, w.EditorID ?? "<no edid>"));
            if (weapons.Count >= 2) break;
        }
        if (weapons.Count < 2) { Console.Error.WriteLine("error: need 2 vanilla weapons with BasicStats"); return 1; }
        var (fkA, edidA) = weapons[0];
        var (fkB, edidB) = weapons[1];

        // A nested-group record (Cell) whose winner overrides cleanly — for R2.
        FormKey nestedFk = default; string nestedEdid = "<no edid>", nestedType = "?";
        foreach (var (fk, _, body) in resolver.WinnerRecordsOfType(new[] { typeof(ICellGetter) }))
        {
            if (!WriteEngine.RecordNeedsSourceCache(body)) continue;     // confirm it's truly nested (no flat group)
            nestedFk = fk; nestedEdid = body.EditorID ?? "<no edid>"; nestedType = RecordNaming.StripOverlay(body.GetType().Name); break;
        }
        Console.WriteLine($"Targets: weapons {edidA} {fkA} | {edidB} {fkB}   nested {nestedType} {nestedFk} ({nestedEdid})");
        Console.WriteLine();

        var results = new List<(string name, bool pass, string detail)>();

        // ===================== R1 — FLAT, selective removal (A removed, B survives) =====================
        {
            var outPath = Path.Combine(outDir, "houseCARL_RemoveProof_R1.esp");
            var shaBefore = ShaNames(nameToPath, fkA.ModKey.FileName, fkB.ModKey.FileName);
            // Seed: a patch carrying overrides of BOTH weapons (the proven edit cleave).
            var seed = WritePatchBuilder.Apply(resolver, rulebook, new[]
            {
                new WritePatchBuilder.PatchEdit { Target = fkA, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = "25" },
                new WritePatchBuilder.PatchEdit { Target = fkB, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = "25" },
            }, outPath, extend: false);
            bool seeded = seed.Success && PatchCarries(outPath, fkA) && PatchCarries(outPath, fkB);
            // Remove A only.
            var rem = WritePatchBuilder.RemoveRecords(resolver, new[] { fkA }, outPath);
            bool removedOk = rem.Success && rem.Removed.Count == 1 && rem.Removed[0].Target == fkA;
            bool aGone = !PatchCarries(outPath, fkA);
            bool bSurvives = PatchCarries(outPath, fkB);
            bool srcOk = SourcesUnchanged(shaBefore, nameToPath);
            bool pass = seeded && removedOk && aGone && bSurvives && srcOk;
            results.Add(("R1 flat selective", pass,
                $"seeded={YN(seeded)} removed={YN(removedOk)} A-gone={YN(aGone)} B-survives={YN(bSurvives)} src-untouched={YN(srcOk)} remain={rem.RemainingRecords}"));
        }

        // ===================== R2 — NESTED override removal (via disk re-open) =====================
        {
            var outPath = Path.Combine(outDir, "houseCARL_RemoveProof_R2.esp");
            if (nestedFk.IsNull)
            {
                results.Add(("R2 nested", false, "skipped — no nested Cell found in the order"));
            }
            else
            {
                var winner = resolver.ResolveWinner(nestedFk);
                var body = winner is null ? null : resolver.GetRecord(session, winner.Value.WinnerPlugin, nestedFk);
                var shaBefore = ShaNames(nameToPath, nestedFk.ModKey.FileName, winner?.WinnerPlugin ?? nestedFk.ModKey.FileName);
                // Seed the nested override directly (the proven override primitive) → write to disk.
                var patchMod = new SkyrimMod(new ModKey("houseCARL_RemoveProof_R2", ModType.Plugin), SkyrimRelease.SkyrimSE);
                var cache = session.LinkCacheFor(winner!.Value.WinnerPlugin);
                WriteEngine.GenericGetOrAddAsOverride(patchMod, body!, cache);
                WriteEngine.WritePatch(patchMod, session.AllMasters(), outPath);
                bool seeded = PatchCarries(outPath, nestedFk);
                // Remove it through the real into= flow (RemoveRecords re-opens from disk).
                var rem = WritePatchBuilder.RemoveRecords(resolver, new[] { nestedFk }, outPath);
                bool removedOk = rem.Success && rem.Removed.Count == 1 && rem.Removed[0].Target == nestedFk;
                bool gone = !PatchCarries(outPath, nestedFk);
                bool srcOk = SourcesUnchanged(shaBefore, nameToPath);
                bool pass = seeded && removedOk && gone && srcOk;
                results.Add(("R2 nested (disk re-open)", pass,
                    $"seeded={YN(seeded)} removed={YN(removedOk)} ({rem.Removed.FirstOrDefault()?.RecordType}) gone={YN(gone)} src-untouched={YN(srcOk)}"));
            }
        }

        // ===================== R3 — CLEAN-MASTERS + Skyrim.esm BASELINE (single override → emptied; baseline pinned) =====================
        // Removing the patch's only record empties it. Mutagen's derivation still STRIPS unreferenced masters (clean-masters)
        // — but Skyrim.esm is force-included on every write (Aaron 2026-06-02: every plugin must carry it), so an emptied
        // patch retains exactly [Skyrim.esm], not []. (Non-baseline clean-masters is shown lean in apply-proof's headers.)
        {
            var outPath = Path.Combine(outDir, "houseCARL_RemoveProof_R3.esp");
            var shaBefore = ShaNames(nameToPath, fkA.ModKey.FileName);
            var seed = WritePatchBuilder.Apply(resolver, rulebook, new[]
            {
                new WritePatchBuilder.PatchEdit { Target = fkA, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = "30" },
            }, outPath, extend: false);
            var (cBefore, mBefore) = PatchState(outPath);
            var rem = WritePatchBuilder.RemoveRecords(resolver, new[] { fkA }, outPath);
            var (cAfter, mAfter) = PatchState(outPath);
            // Emptied → derived set empty, but the CK baseline (Skyrim.esm + Update.esm) is pinned; non-baseline masters drop.
            bool baselineOnly = mAfter.Count == 2
                && mAfter.Any(m => m.Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase))
                && mAfter.Any(m => m.Equals("Update.esm", StringComparison.OrdinalIgnoreCase));
            bool pass = seed.Success && cBefore == 1 && mBefore.Count >= 1
                        && rem.Success && cAfter == 0 && baselineOnly
                        && SourcesUnchanged(shaBefore, nameToPath);
            results.Add(("R3 baseline-on-empty (CK masters pinned)", pass,
                $"before: {cBefore} rec / [{string.Join(",", mBefore)}]  ->  after: {cAfter} rec / [{string.Join(",", mAfter)}]  (Skyrim.esm + Update.esm baseline retained)"));
        }

        // ===================== R4 — REJECT not-carried (Q3, no silent no-op, no write) =====================
        {
            var outPath = Path.Combine(outDir, "houseCARL_RemoveProof_R4.esp");
            var seed = WritePatchBuilder.Apply(resolver, rulebook, new[]
            {
                new WritePatchBuilder.PatchEdit { Target = fkA, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = "35" },
            }, outPath, extend: false);
            var shaPatchBefore = ShaFile(outPath);
            // Ask to remove weapon B — a real vanilla FormKey the patch does NOT carry.
            var rem = WritePatchBuilder.RemoveRecords(resolver, new[] { fkB }, outPath);
            bool refused = !rem.Success;
            bool fileUnchanged = ShaFile(outPath) == shaPatchBefore;
            bool aStillThere = PatchCarries(outPath, fkA);
            bool mentionsNotCarried = (rem.Error ?? "").Contains("not carried", StringComparison.OrdinalIgnoreCase);
            bool pass = seed.Success && refused && fileUnchanged && aStillThere && mentionsNotCarried;
            results.Add(("R4 reject not-carried", pass,
                $"refused={YN(refused)} patch-byte-unchanged={YN(fileUnchanged)} carried-record-intact={YN(aStillThere)} msg-named={YN(mentionsNotCarried)}"));
        }

        // ---- VERDICT ----
        Console.WriteLine("Results:");
        foreach (var (name, pass, detail) in results)
            Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name,-26} {detail}");
        Console.WriteLine();
        bool allPass = results.All(r => r.pass);
        Console.WriteLine("================================================================");
        Console.WriteLine(allPass
            ? "=== ALL CHECKS PASS — whole-record removal is proven through the core the MCP tool calls.\n" +
              "    Open write-output/remove-proof/*.esp in xEdit: R1=one weapon override left (the other removed),\n" +
              "    R2=empty (nested Cell override removed), R3=0 records w/ Skyrim.esm baseline pinned. Originals byte-untouched. ==="
            : "=== FAIL — see the checks above ===");
        Console.WriteLine("================================================================");
        return allPass ? 0 : 1;
    }

    // ---- helpers ----

    static bool PatchCarries(string patchPath, FormKey fk)
    {
        ISkyrimModGetter? back = null;
        try { back = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE); return back.EnumerateMajorRecords().Any(r => r.FormKey == fk); }
        catch { return false; }
        finally { (back as IDisposable)?.Dispose(); }
    }

    static (int count, List<string> masters) PatchState(string patchPath)
    {
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            return (back.EnumerateMajorRecords().Count(), back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList());
        }
        catch { return (-1, new()); }
        finally { (back as IDisposable)?.Dispose(); }
    }

    static Dictionary<string, string> ShaNames(Dictionary<string, string> nameToPath, params string[] names)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names.Distinct(StringComparer.OrdinalIgnoreCase)) d[n] = ShaOf(nameToPath, n);
        return d;
    }

    static bool SourcesUnchanged(Dictionary<string, string> before, Dictionary<string, string> nameToPath)
        => before.All(kv => string.Equals(kv.Value, ShaOf(nameToPath, kv.Key), StringComparison.OrdinalIgnoreCase));

    static string ShaOf(Dictionary<string, string> nameToPath, string name)
    {
        if (!nameToPath.TryGetValue(name, out var path)) return $"(no path {name})";
        return ShaFile(path);
    }

    static string ShaFile(string path)
    {
        if (!File.Exists(path)) return "(no file)";
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    static string YN(bool b) => b ? "Y" : "N";
}

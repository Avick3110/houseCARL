using System.Diagnostics;
using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// MCP step (Beat C de-risk) — prove the MULTI-MASTER write path BEFORE building set_field/bulk_apply on it.
///
/// The standalone <c>patch</c> harness opens ONE plugin, so it can only ever hand the serializer one known master
/// (the "single-master limit"). That was always an artifact of one-plugin-open, not a wall: the write-proof
/// serializes cross-master refs byte-identical-to-native by handing the writer the FULL master set. This mode
/// prototypes the real MCP write path and proves the central modding case Aaron named — a MERGE patch:
///
///   build the load-order resolver (every plugin held open as an overlay)
///   → take a leveled list DEFINED in a vanilla master (so the only cross-master refs are the ones we add)
///   → MERGE one weapon entry from each of N distinct mods into it (struct-element Add-from-parts, Phase-6 shape)
///   → serialize with the resolver's FULL overlay set as known masters (WriteEngine's multi-master WritePatch)
///   → emit ONE reviewable .esp whose header lists EVERY referenced master; sources stay byte-untouched (SHA-checked).
///
/// Aaron opens it in xEdit and confirms the masters + the merged entries. This is the empirical gate that turns the
/// "inherited fail-loud boundary" into a CLOSED capability the write tools then ride.
///
/// Run: dotnet run --project src/housecarl-generator multimaster-patch [maxPlugins]
/// </summary>
public static class MultiMasterProof
{
    static readonly HashSet<string> Vanilla = new(StringComparer.OrdinalIgnoreCase)
        { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" };

    public static int RunMultiMasterPatch(string[] args)
    {
        int maxPlugins = args.Length > 0 && int.TryParse(args[0], out var m) ? m : 0;   // 0 = all

        Console.WriteLine("================================================================");
        Console.WriteLine(" houseCARL multi-master write proof (Beat C de-risk — merge patch)");
        Console.WriteLine("================================================================");

        var paths = ResolveProbe.GatherPlugins();
        if (maxPlugins > 0 && paths.Count > maxPlugins) paths = paths.Take(maxPlugins).ToList();
        if (paths.Count == 0) { Console.Error.WriteLine("error: no plugins found"); return 1; }
        var nameToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths) nameToPath[Path.GetFileName(p)] = p;   // last copy wins (matches dedup/priority)
        Console.WriteLine($"Plugins (placeholder order): {paths.Count}");

        var sw = Stopwatch.StartNew();
        using var resolver = LoadOrderResolver.Build(paths);
        sw.Stop();
        using var session = resolver.OpenSession();   // Option B: open source plugins on demand, disposed at harness exit
        Console.WriteLine($"Resolver built in {sw.Elapsed.TotalSeconds:N1}s: {resolver.PluginCount:N0} plugins | " +
                          $"{resolver.RecordCount:N0} records | {resolver.ConflictCount:N0} conflicts");
        Console.WriteLine();

        // ---- DISCOVER a clean base: a leveled list DEFINED in a vanilla master with existing entries. We override
        //      the MASTER'S version (vanilla entries only), so the only cross-master refs are the ones we add — the
        //      patch's master list is then exactly {base master} ∪ {the mods we reference}. ----
        var swd = Stopwatch.StartNew();
        FormKey baseFk = default; string? baseMaster = null; int beforeCount = 0; string? baseEdid = null;
        foreach (var (fk, _, _) in resolver.WinnerRecordsOfType(new[] { typeof(ILeveledItemGetter) }))
        {
            if (!Vanilla.Contains(fk.ModKey.FileName)) continue;
            if (resolver.GetRecord(session, fk.ModKey.FileName, fk) is not ILeveledItemGetter li) continue;  // the master's version
            if (li.Entries is not { Count: > 0 }) continue;                 // existing entries → added ones append visibly
            baseFk = fk; baseMaster = fk.ModKey.FileName; beforeCount = li.Entries.Count; baseEdid = li.EditorID;
            break;
        }
        if (baseMaster is null) { Console.Error.WriteLine("error: no vanilla leveled list with entries found"); return 1; }

        // ---- DISCOVER cross-master items: weapons DEFINED in distinct NON-vanilla plugins (each adds one master).
        //      The item's DEFINING master (fk.ModKey) must itself be a loaded overlay — only then is it in the load
        //      order we write against. (A discovered winner can be an OVERRIDE whose defining master is outside a
        //      capped set; referencing it would need an absent master. The full order has every master, but this
        //      filter keeps the proof correct at any cap.) ----
        var loadedNames = new HashSet<string>(resolver.PluginNames, StringComparer.OrdinalIgnoreCase);
        var crossItems = new List<FormKey>();
        var seenMasters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fk, _, _) in resolver.WinnerRecordsOfType(new[] { typeof(IWeaponGetter) }))
        {
            var mk = fk.ModKey.FileName.ToString();
            if (Vanilla.Contains(mk) || string.Equals(mk, baseMaster, StringComparison.OrdinalIgnoreCase)) continue;
            if (!loadedNames.Contains(mk)) continue;                       // defining master must be in the load order
            if (!seenMasters.Add(mk)) continue;                            // one item per distinct master
            crossItems.Add(fk);
            if (crossItems.Count >= 3) break;                              // 3 distinct mods → a 4-master patch
        }
        swd.Stop();
        if (crossItems.Count == 0) { Console.Error.WriteLine("error: no cross-master weapon found to merge"); return 1; }

        Console.WriteLine($"Discovery ({swd.Elapsed.TotalMilliseconds:N0}ms):");
        Console.WriteLine($"  base leveled list : {baseFk}  ({baseEdid ?? "<no edid>"})  from {baseMaster}, {beforeCount} existing entries");
        Console.WriteLine($"  merging {crossItems.Count} cross-master weapon entr{(crossItems.Count == 1 ? "y" : "ies")}:");
        foreach (var it in crossItems) Console.WriteLine($"      + {it}   (adds master {it.ModKey.FileName})");
        Console.WriteLine();

        // ---- BUILD the Add-entry requests (struct-element Add-from-parts — the proven Phase-6 composition shape). ----
        var reqs = crossItems.Select(it => new WriteRequest
        {
            RecordType = "LeveledItem",
            Path = new[] { "Entries" },
            Verb = "Add",
            Struct = new StructSpec
            {
                Type = "LeveledItemEntry",
                Sets = new List<WriteRequest>
                {
                    new() { RecordType = "LeveledItemEntry", Path = new[] { "Data", "Level" },     Verb = "Set", Value = "1" },
                    new() { RecordType = "LeveledItemEntry", Path = new[] { "Data", "Count" },     Verb = "Set", Value = "1" },
                    new() { RecordType = "LeveledItemEntry", Path = new[] { "Data", "Reference" }, Verb = "Set", Value = it.ToString() },
                }
            }
        }).ToList();

        // ---- PRE-FLIGHT every request (Q3): refuse to write if ANY rejects. ----
        var rulebook = CorpusRulebook.Load();
        var rejects = reqs.Select(r => rulebook.Validate(r)).Where(x => x is not null).ToList();
        if (rejects.Count > 0)
        {
            Console.Error.WriteLine("REJECTED by pre-flight (no write performed):");
            foreach (var rj in rejects) Console.Error.WriteLine($"  - {rj}");
            return 1;
        }
        Console.WriteLine($"Pre-flight: ACCEPT (all {reqs.Count})");

        // SHA the source files we touch (base master + each referenced mod) — must stay byte-identical.
        var sourceNames = new[] { baseMaster }.Concat(crossItems.Select(c => c.ModKey.FileName.ToString()))
                                              .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var shaBefore = sourceNames.ToDictionary(n => n, n => ShaOf(nameToPath, n), StringComparer.OrdinalIgnoreCase);

        // ---- ENGINE: override the vanilla base, Add each cross-master entry. LeveledItem is a flat group (no link
        //      cache needed). The override carries the master's vanilla entries; we append the modded ones. ----
        var name = "houseCARL_MultiMasterProof";
        var outDir = Path.GetFullPath(Path.Combine("write-output", "multimaster-patch"));
        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, name + ".esp");

        var baseGetter = resolver.GetRecord(session, baseMaster, baseFk)!;          // re-fetch (the master's version)
        var patchMod = new SkyrimMod(new ModKey(name, ModType.Plugin), SkyrimRelease.SkyrimSE);
        IMajorRecord ov;
        try { ov = WriteEngine.GenericGetOrAddAsOverride(patchMod, baseGetter); }
        catch (Exception ex) { Console.Error.WriteLine($"error: could not override {baseFk} — {ex.Message}"); return 1; }
        foreach (var r in reqs) WriteEngine.ApplyVerb(ov, r);

        // ---- WRITE with the FULL known-master set (the multi-master capability). Time it (measure-first). ----
        var sww = Stopwatch.StartNew();
        try { WriteEngine.WritePatch(patchMod, session.AllMasters(), outPath); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: multi-master serialize threw — {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        sww.Stop();
        Console.WriteLine($"Wrote patch ({new FileInfo(outPath).Length} bytes) in {sww.Elapsed.TotalMilliseconds:N0}ms " +
                          $"(known-master set offered: {resolver.PluginCount:N0} overlays).");
        Console.WriteLine();

        // ---- READ BACK: the patch's master list + the merged entries; assert sources untouched. ----
        var back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE);
        var masters = back.ModHeader.MasterReferences.Select(mr => mr.Master.FileName.ToString()).ToList();
        Console.WriteLine($"Patch masters ({masters.Count}): {string.Join(", ", masters)}");

        var patchedLi = back.EnumerateMajorRecords<ILeveledItemGetter>().FirstOrDefault(r => r.FormKey == baseFk);
        if (patchedLi is null) { Console.Error.WriteLine("FAIL: edited leveled list not found in the written patch."); return 1; }
        int afterCount = patchedLi.Entries?.Count ?? 0;
        var refsBack = (patchedLi.Entries ?? new List<ILeveledItemEntryGetter>())
            .Select(e => e.Data?.Reference.FormKey).Where(fk => fk is not null).Select(fk => fk!.Value).ToHashSet();
        Console.WriteLine($"Entries: {beforeCount} → {afterCount}  (expected +{crossItems.Count})");
        var presentRefs = crossItems.Where(refsBack.Contains).ToList();
        foreach (var it in crossItems)
            Console.WriteLine($"  merged {it}: {(refsBack.Contains(it) ? "PRESENT in patch" : "MISSING")}");

        var changed = sourceNames.Where(n => !string.Equals(shaBefore[n], ShaOf(nameToPath, n), StringComparison.OrdinalIgnoreCase)).ToList();
        Console.WriteLine($"Sources unchanged: {(changed.Count == 0 ? $"YES (all {sourceNames.Count})" : "NO — mutated: " + string.Join(", ", changed))}");
        Console.WriteLine();

        // ---- VERDICT ----
        bool mastersMulti = masters.Count >= 2;
        bool allMastersPresent = sourceNames.All(n => masters.Contains(n, StringComparer.OrdinalIgnoreCase));
        bool entriesLanded = afterCount == beforeCount + crossItems.Count && presentRefs.Count == crossItems.Count;
        bool srcOk = changed.Count == 0;
        bool pass = mastersMulti && allMastersPresent && entriesLanded && srcOk;

        Console.WriteLine("================================================================");
        Console.WriteLine(pass
            ? $"=== MULTI-MASTER PATCH WRITTEN — {masters.Count} masters, {crossItems.Count} cross-master entries merged, sources untouched.\n" +
              $"    Open {name}.esp in xEdit: LeveledItem {baseFk} ({baseEdid}); confirm the {crossItems.Count} added entries\n" +
              $"    resolve to the modded weapons and the master list carries every referenced plugin. ==="
            : "=== FAIL — see the checks above ===");
        Console.WriteLine($"    multi-master:{YN(mastersMulti)}  all-referenced-masters-present:{YN(allMastersPresent)}  " +
                          $"entries-landed:{YN(entriesLanded)}  sources-untouched:{YN(srcOk)}");
        Console.WriteLine("================================================================");
        return pass ? 0 : 1;
    }

    static string YN(bool b) => b ? "PASS" : "FAIL";

    static string ShaOf(Dictionary<string, string> nameToPath, string name)
    {
        if (!nameToPath.TryGetValue(name, out var path)) return $"(no path for {name})";
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }
}

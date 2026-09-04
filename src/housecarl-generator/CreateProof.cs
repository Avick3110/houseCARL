using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// Build proof for <see cref="WritePatchBuilder.CreateRecords"/>, the core the <c>housecarl_create_record</c> tool
/// calls. It must drive the same code path the tool does, or it proves nothing about it (as for
/// <see cref="RemoveProof"/> and <see cref="ApplyProof"/>). Each check runs against a LIVE large load order and
/// re-reads the written patch from disk:
///
///   K1 — FLAT, self-contained: create a Keyword (editorid only) → present on re-read, a fresh LOCAL 0x800+ FormID, and
///        a MASTERLESS plugin (references nothing external).
///   K2 — FLAT, referencing existing: create an Armor + set ArmorRating + Name + Add a real vanilla keyword → all fields
///        land via the SAME ApplyVerb path, and the referenced master is DERIVED into the header.
///   K3 — EXTEND (into=): create one record fresh, a second into= the same patch → BOTH present, distinct ids (the
///        accumulate-across-calls path, re-opening from disk).
///   K4 — REJECT nested: create "Cell" (no flat group) → refused loud, NOTHING written.
///   K5 — REJECT abstract: create "Global" (abstract T) → refused loud naming concrete subtypes, NOTHING written.
///   K6 — REJECT no editorid: create a Keyword with an empty editorid → refused loud, NOTHING written.
///
/// Vanilla masters the proof references are SHA-checked unchanged (create only writes the patch). Patches are left in
/// write-output/create-proof/ to open in xEdit.  Run: dotnet run --project src/housecarl-generator create-proof [maxPlugins]
/// </summary>
public static class CreateProof
{
    static readonly HashSet<string> Vanilla = new(StringComparer.OrdinalIgnoreCase)
        { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" };

    public static int RunCreateProof(string[] args)
    {
        int maxPlugins = args.Length > 0 && int.TryParse(args[0], out var m) ? m : 0;

        Console.WriteLine("================================================================");
        Console.WriteLine(" houseCARL create-record proof (capability arc — housecarl_create_record)");
        Console.WriteLine("================================================================");

        var paths = ResolveProbe.GatherPlugins();
        if (maxPlugins > 0 && paths.Count > maxPlugins) paths = paths.Take(maxPlugins).ToList();
        if (paths.Count == 0) { Console.Error.WriteLine("error: no plugins found"); return 1; }
        var nameToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths) nameToPath[Path.GetFileName(p)] = p;

        using var resolver = LoadOrderResolver.Build(paths);
        var rulebook = CorpusRulebook.Load();
        Console.WriteLine($"Resolver: {resolver.PluginCount:N0} plugins | {resolver.RecordCount:N0} records | {resolver.ConflictCount:N0} conflicts.");

        // A real vanilla keyword for K2 to reference (so the master derivation is against a stable, known master).
        FormKey kwRef = default; string kwEdid = "<no edid>";
        foreach (var (fk, _, body) in resolver.WinnerRecordsOfType(new[] { typeof(IKeywordGetter) }))
        {
            if (!Vanilla.Contains(fk.ModKey.FileName)) continue;
            kwRef = fk; kwEdid = body.EditorID ?? "<no edid>"; break;
        }
        if (kwRef.IsNull) { Console.Error.WriteLine("error: no vanilla keyword found to reference"); return 1; }
        Console.WriteLine($"Reference keyword (K2): {kwEdid} {kwRef}");
        Console.WriteLine();

        var outDir = Path.GetFullPath(Path.Combine("write-output", "create-proof"));
        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        Directory.CreateDirectory(outDir);

        // SHA the masters this proof references, to confirm create never writes an original.
        var shaBefore = ShaNames(nameToPath, "Skyrim.esm", kwRef.ModKey.FileName);

        var results = new List<(string name, bool pass, string detail)>();

        // ===================== K1 — FLAT, self-contained create =====================
        {
            var outPath = Path.Combine(outDir, "houseCARL_CreateProof_K1.esp");
            var spec = new WritePatchBuilder.CreateSpec
            {
                RecordType = "Keyword", EditorId = "HC_CreateProof_K1_Keyword",
                Edits = Array.Empty<WriteRequest>(),
            };
            var o = WritePatchBuilder.CreateRecords(resolver, rulebook, new[] { spec }, outPath, extend: false);
            bool created = o.Success && o.Created.Count == 1;
            var fk = created ? o.Created[0].FormKey : default;
            bool present = created && PatchCarries(outPath, fk);
            bool localId = created && fk.ID >= 0x800 && string.Equals(fk.ModKey.FileName, Path.GetFileName(outPath), StringComparison.OrdinalIgnoreCase);
            var (cnt, masters) = PatchState(outPath);
            // A self-contained create references nothing, so the DERIVED master set is empty — but every Skyrim plugin
            // must carry the CK baseline masters (Skyrim.esm + Update.esm), which WritePatch force-includes.
            bool ckBaseline = cnt == 1 && masters.Count == 2
                && masters.Any(m => m.Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase))
                && masters.Any(m => m.Equals("Update.esm", StringComparison.OrdinalIgnoreCase));
            bool pass = created && present && localId && ckBaseline;
            results.Add(("K1 flat self-contained", pass,
                $"created={YN(created)} id={(created ? "0x" + fk.ID.ToString("X6") : "-")} present={YN(present)} local-id={YN(localId)} ck-baseline={YN(ckBaseline)} masters=[{string.Join(",", masters)}]"));
        }

        // ===================== K2 — FLAT, referencing existing (fields + derived master) =====================
        {
            var outPath = Path.Combine(outDir, "houseCARL_CreateProof_K2.esp");
            var spec = new WritePatchBuilder.CreateSpec
            {
                RecordType = "Armor", EditorId = "HC_CreateProof_K2_Armor",
                Edits = new[]
                {
                    new WriteRequest { RecordType = "Armor", Path = new[] { "ArmorRating" }, Verb = "Set", Value = "42.5" },
                    new WriteRequest { RecordType = "Armor", Path = new[] { "Name" }, Verb = "Set", Value = "houseCARL Created Cuirass" },
                    new WriteRequest { RecordType = "Armor", Path = new[] { "Keywords" }, Verb = "Add", Value = kwRef.ToString() },
                },
            };
            var o = WritePatchBuilder.CreateRecords(resolver, rulebook, new[] { spec }, outPath, extend: false);
            bool created = o.Success && o.Created.Count == 1;
            var fk = created ? o.Created[0].FormKey : default;
            var read = created ? ReadArmor(outPath, fk) : default;
            bool fieldsOk = read.found && Math.Abs(read.rating - 42.5f) < 0.01f && read.name == "houseCARL Created Cuirass" && read.hasKw;
            var (_, masters) = PatchState(outPath);
            bool masterDerived = masters.Any(x => x.Equals(kwRef.ModKey.FileName, StringComparison.OrdinalIgnoreCase));
            bool pass = created && fieldsOk && masterDerived;
            results.Add(("K2 flat + ref (fields/master)", pass,
                $"created={YN(created)} rating={read.rating} name=\"{read.name}\" hasKw={YN(read.hasKw)} masters=[{string.Join(",", masters)}] derived={YN(masterDerived)}"));
        }

        // ===================== K3 — EXTEND (into=): two created records accumulate in one patch =====================
        {
            var outPath = Path.Combine(outDir, "houseCARL_CreateProof_K3.esp");
            var s1 = WritePatchBuilder.CreateRecords(resolver, rulebook,
                new[] { new WritePatchBuilder.CreateSpec { RecordType = "Keyword", EditorId = "HC_CreateProof_K3a", Edits = Array.Empty<WriteRequest>() } },
                outPath, extend: false);
            var s2 = WritePatchBuilder.CreateRecords(resolver, rulebook,
                new[] { new WritePatchBuilder.CreateSpec { RecordType = "Spell", EditorId = "HC_CreateProof_K3b", Edits = Array.Empty<WriteRequest>() } },
                outPath, extend: true);
            bool both = s1.Success && s2.Success && s2.Extended;
            var (cnt, _) = PatchState(outPath);
            bool aPresent = s1.Success && PatchCarries(outPath, s1.Created[0].FormKey);
            bool bPresent = s2.Success && PatchCarries(outPath, s2.Created[0].FormKey);
            bool distinct = s1.Success && s2.Success && s1.Created[0].FormKey != s2.Created[0].FormKey;
            bool pass = both && aPresent && bPresent && distinct && cnt == 2;
            results.Add(("K3 extend (into=)", pass,
                $"s1={YN(s1.Success)} s2-extended={YN(s2.Extended)} both-present={YN(aPresent && bPresent)} distinct={YN(distinct)} count={cnt}"));
        }

        // ===================== K4 — REJECT nested (no write) =====================
        {
            var outPath = Path.Combine(outDir, "houseCARL_CreateProof_K4.esp");
            var o = WritePatchBuilder.CreateRecords(resolver, rulebook,
                new[] { new WritePatchBuilder.CreateSpec { RecordType = "Cell", EditorId = "HC_CreateProof_K4", Edits = Array.Empty<WriteRequest>() } },
                outPath, extend: false);
            bool refused = !o.Success;
            bool named = (o.Error ?? "").Contains("nested", StringComparison.OrdinalIgnoreCase) || (o.Error ?? "").Contains("no top-level group", StringComparison.OrdinalIgnoreCase);
            bool noFile = !File.Exists(outPath);
            bool pass = refused && named && noFile;
            results.Add(("K4 reject nested (Cell)", pass, $"refused={YN(refused)} msg-named={YN(named)} no-file={YN(noFile)}"));
        }

        // ===================== K5 — REJECT abstract (no write) =====================
        {
            var outPath = Path.Combine(outDir, "houseCARL_CreateProof_K5.esp");
            var o = WritePatchBuilder.CreateRecords(resolver, rulebook,
                new[] { new WritePatchBuilder.CreateSpec { RecordType = "Global", EditorId = "HC_CreateProof_K5", Edits = Array.Empty<WriteRequest>() } },
                outPath, extend: false);
            bool refused = !o.Success;
            bool named = (o.Error ?? "").Contains("abstract", StringComparison.OrdinalIgnoreCase);
            bool noFile = !File.Exists(outPath);
            bool pass = refused && named && noFile;
            results.Add(("K5 reject abstract (Global)", pass, $"refused={YN(refused)} msg-named={YN(named)} no-file={YN(noFile)}"));
        }

        // ===================== K6 — REJECT no editorid (no write) =====================
        {
            var outPath = Path.Combine(outDir, "houseCARL_CreateProof_K6.esp");
            var o = WritePatchBuilder.CreateRecords(resolver, rulebook,
                new[] { new WritePatchBuilder.CreateSpec { RecordType = "Keyword", EditorId = "", Edits = Array.Empty<WriteRequest>() } },
                outPath, extend: false);
            bool refused = !o.Success;
            bool named = (o.Error ?? "").Contains("editorid", StringComparison.OrdinalIgnoreCase);
            bool noFile = !File.Exists(outPath);
            bool pass = refused && named && noFile;
            results.Add(("K6 reject no-editorid", pass, $"refused={YN(refused)} msg-named={YN(named)} no-file={YN(noFile)}"));
        }

        bool srcOk = SourcesUnchanged(shaBefore, nameToPath);

        // ---- VERDICT ----
        Console.WriteLine("Results:");
        foreach (var (name, pass, detail) in results)
            Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name,-30} {detail}");
        Console.WriteLine($"  [{(srcOk ? "PASS" : "FAIL")}] {"originals byte-untouched",-30} (Skyrim.esm + {kwRef.ModKey.FileName})");
        Console.WriteLine();
        bool allPass = results.All(r => r.pass) && srcOk;
        Console.WriteLine("================================================================");
        Console.WriteLine(allPass
            ? "=== ALL CHECKS PASS — brand-new record creation is proven through the core the MCP tool calls.\n" +
              "    Open write-output/create-proof/*.esp in xEdit: K1=a new Keyword, K2=a new Armor (rating 42.5, named,\n" +
              "    with the keyword), K3=a Keyword + a Spell in one patch. New FormIDs, originals byte-untouched. ==="
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

    static (bool found, float rating, string name, bool hasKw) ReadArmor(string patchPath, FormKey fk)
    {
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            var a = back.Armors.FirstOrDefault(x => x.FormKey == fk);
            if (a is null) return (false, 0, "", false);
            return (true, a.ArmorRating, a.Name?.String ?? "", a.Keywords?.Count is > 0);
        }
        catch { return (false, 0, "", false); }
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
        if (!File.Exists(path)) return "(no file)";
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    static string YN(bool b) => b ? "Y" : "N";
}

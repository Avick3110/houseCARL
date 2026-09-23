using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// The two real-data in-place proofs: a NESTED own-override (a PlacedObject in a Cell) re-edited and removed in place on
/// a foreign target. They need a real Skyrim.esm for a genuine nested record, so they self-skip without one and are not
/// part of <c>ci-all</c>. The self-contained in-place checks are the InPlaceGuard* tests in housecarl-mcp-tests.
/// </summary>
public static class InPlaceNestedProof
{
    /// <summary>
    /// REAL-DATA proof (the one case the self-contained tests cannot cover): the in-place re-edit of a NESTED own-override
    /// (a PlacedObject — lives in a Cell, so Phase-3 builds the source LinkCacheFor OVER the target overlay). The
    /// ReleaseOverlay-before-swap discipline must dispose BOTH the flat (GetRecord) AND the nested (LinkCacheFor) session
    /// overlays on the FOREIGN target before File.Replace. Mirrors writelock-nested-proof but through ApplyInPlace.
    /// Needs a real master (Skyrim.esm) for a genuine nested record — self-SKIPs on the CI runner (no game data).
    /// Run: dotnet run --project src/housecarl-generator inplace-nested-proof ["&lt;Data dir with Skyrim.esm&gt;"]
    /// </summary>
    public static int RunNestedProof(string[] args)
    {
        Console.WriteLine("=== inplace-nested-proof — in-place re-edit of a NESTED own-override (real data) ===");
        string dataDir = args.Length > 0 ? args[0] : @"E:\Skyrim Modding\ARR 2.0\Stock Game\Data";
        string skyrim = Path.Combine(dataDir, "Skyrim.esm");
        if (!File.Exists(skyrim))
        {
            Console.WriteLine($"SKIP: need Skyrim.esm; not found at {skyrim} (pass the Data dir as arg 1). A real nested record + master");
            Console.WriteLine("      can't be synthesized for the LinkCacheFor-on-a-foreign-target arm — the same posture as writelock-nested-proof.");
            return 0;
        }

        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-inplace-nested");
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        Directory.CreateDirectory(tmpDir);
        var rulebook = CorpusRulebook.Load(GenerateCorpus(tmpDir));

        FormKey refrFk;
        using (var r0 = LoadOrderResolver.Build(new[] { skyrim }))
        {
            refrFk = r0.WinnerRecordsOfType(new[] { typeof(IPlacedObjectGetter) }).Select(x => x.fk).FirstOrDefault();
            if (refrFk.IsNull) { Console.Error.WriteLine("no PlacedObject in Skyrim.esm"); return 1; }
        }
        Console.WriteLine($"-- real nested record: PlacedObject {refrFk} --");
        string userPath = Path.Combine(tmpDir, "HcInPlaceNested.esp");

        // STEP 1 — author a foreign-style user mod that OVERRIDES the nested record (winner=Skyrim.esm; the patch lane).
        using (var r1 = LoadOrderResolver.Build(new[] { skyrim }))
        {
            var o = WritePatchBuilder.Apply(r1, rulebook,
                new[] { new WritePatchBuilder.PatchEdit { Target = refrFk, Path = new[] { "Scale" }, Verb = "Set", Value = "1.5" } },
                userPath, extend: false);
            Console.WriteLine($"   step 1  author the nested override (winner=Skyrim.esm) : {(o.Success ? "OK" : "FAIL — " + o.Error)}");
            if (!o.Success) return 1;
        }

        // STEP 2 — THE TEST: edit that nested record IN PLACE (winner == the user mod == target → flat + nested overlay on it).
        bool ok; string err;
        using (var r2 = LoadOrderResolver.Build(new[] { skyrim, userPath }))
        {
            var o = WritePatchBuilder.ApplyInPlace(r2, rulebook,
                new[] { new WritePatchBuilder.PatchEdit { Target = refrFk, Path = new[] { "Scale" }, Verb = "Set", Value = "2.5" } },
                userPath, Path.GetFileName(userPath));
            ok = o.Success; err = o.Error ?? "ok";
            Console.WriteLine($"   step 2  re-edit the NESTED record IN PLACE : {(ok ? "OK" : "FAIL — " + err)}");
        }
        float? scale = ReadPlacedScale(userPath, refrFk);
        bool landed = scale.HasValue && Math.Abs(scale.Value - 2.5f) < 0.001f;
        Console.WriteLine($"   nested in-place edit landed (Scale==2.5) : {(landed ? "PASS" : $"FAIL (scale={scale?.ToString() ?? "null"})")}");

        bool pass = ok && landed;
        Console.WriteLine($"=== inplace-nested-proof: {(pass ? "PASS — nested in-place survives ReleaseOverlay-before-serialize on a foreign target" : "FAIL")} ===");
        try { Directory.Delete(tmpDir, recursive: true); } catch { }
        return pass ? 0 : 1;
    }

    /// <summary>
    /// REAL-DATA proof for WAVE 2 (remove-in-place): the in-place REMOVE of a NESTED own-override (a PlacedObject — lives
    /// in a Cell). Confirms the typed nested <c>Remove(FormKey, Type)</c> drops a nested record AND the WriteInPlace
    /// rewrite re-emits the rest of the foreign target faithfully on REAL data — the remove counterpart of
    /// <see cref="RunNestedProof"/>. (Remove's present-check reads the mutable mod directly via CreateFromBinary, so it
    /// opens no LinkCacheFor overlay on the target — the nested-LOCK hazard the edit proof guards is lighter here; what
    /// this adds is the nested-Remove + real-data re-serialize on a third-party file.) Needs a real master (Skyrim.esm);
    /// self-SKIPs on the CI runner.
    /// Run: dotnet run --project src/housecarl-generator inplace-remove-nested-proof ["&lt;Data dir with Skyrim.esm&gt;"]
    /// </summary>
    public static int RunRemoveNestedProof(string[] args)
    {
        Console.WriteLine("=== inplace-remove-nested-proof — in-place REMOVE of a NESTED own-override (real data) ===");
        string dataDir = args.Length > 0 ? args[0] : @"E:\Skyrim Modding\ARR 2.0\Stock Game\Data";
        string skyrim = Path.Combine(dataDir, "Skyrim.esm");
        if (!File.Exists(skyrim))
        {
            Console.WriteLine($"SKIP: need Skyrim.esm; not found at {skyrim} (pass the Data dir as arg 1). A real nested record + master");
            Console.WriteLine("      can't be synthesized for the nested-remove arm — the same posture as inplace-nested-proof.");
            return 0;
        }

        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-inplace-remove-nested");
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        Directory.CreateDirectory(tmpDir);
        var rulebook = CorpusRulebook.Load(GenerateCorpus(tmpDir));

        FormKey refrFk;
        using (var r0 = LoadOrderResolver.Build(new[] { skyrim }))
        {
            refrFk = r0.WinnerRecordsOfType(new[] { typeof(IPlacedObjectGetter) }).Select(x => x.fk).FirstOrDefault();
            if (refrFk.IsNull) { Console.Error.WriteLine("no PlacedObject in Skyrim.esm"); return 1; }
        }
        Console.WriteLine($"-- real nested record: PlacedObject {refrFk} --");
        string userPath = Path.Combine(tmpDir, "HcInPlaceRmNested.esp");

        // STEP 1 — author a foreign-style user mod that OVERRIDES the nested record (winner=Skyrim.esm; the patch lane).
        using (var r1 = LoadOrderResolver.Build(new[] { skyrim }))
        {
            var o = WritePatchBuilder.Apply(r1, rulebook,
                new[] { new WritePatchBuilder.PatchEdit { Target = refrFk, Path = new[] { "Scale" }, Verb = "Set", Value = "1.5" } },
                userPath, extend: false);
            Console.WriteLine($"   step 1  author the nested override (winner=Skyrim.esm) : {(o.Success ? "OK" : "FAIL — " + o.Error)}");
            if (!o.Success) return 1;
        }
        bool present0 = RecordPresent(userPath, refrFk);

        // STEP 2 — THE TEST: REMOVE that nested record IN PLACE (drop the override from the foreign target's own file).
        bool ok; string err;
        using (var r2 = LoadOrderResolver.Build(new[] { skyrim, userPath }))
        {
            var o = WritePatchBuilder.RemoveRecordsInPlace(r2, new[] { refrFk }, userPath, Path.GetFileName(userPath));
            ok = o.Success; err = o.Error ?? "ok";
            Console.WriteLine($"   step 2  REMOVE the NESTED record IN PLACE : {(ok ? "OK" : "FAIL — " + err)}");
        }
        bool gone = !RecordPresent(userPath, refrFk);
        Console.WriteLine($"   nested record present after step 1 : {present0} ;  gone after in-place remove : {gone}");

        bool pass = ok && present0 && gone;
        Console.WriteLine($"=== inplace-remove-nested-proof: {(pass ? "PASS — nested in-place remove drops the record + re-serializes faithfully on a foreign target" : "FAIL")} ===");
        try { Directory.Delete(tmpDir, recursive: true); } catch { }
        return pass ? 0 : 1;
    }

    static string GenerateCorpus(string tmpDir)
    {
        var genDir = Path.Combine(tmpDir, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(tmpDir, "corpus-ref"));
        return Path.Combine(genDir, "corpus.json");
    }

    static bool RecordPresent(string path, FormKey fk)
    {
        ISkyrimModGetter? ov = null;
        try { ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); return ov.EnumerateMajorRecords().Any(r => r.FormKey == fk); }
        catch { return false; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    static float? ReadPlacedScale(string path, FormKey fk)
    {
        ISkyrimModGetter? ov = null;
        try { ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); return ov.EnumerateMajorRecords<IPlacedObjectGetter>().FirstOrDefault(r => r.FormKey == fk)?.Scale; }
        catch { return null; }
        finally { (ov as IDisposable)?.Dispose(); }
    }
}

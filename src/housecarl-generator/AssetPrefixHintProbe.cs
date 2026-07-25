using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD (<c>asset-prefix-hint-guard</c>) for #273 — the missing-root suggestion on an
/// ABSENT asset path. A model path read straight off a record (<c>Model.File</c>) is stored relative to
/// <c>meshes\</c>, so passing it verbatim — the NORMAL way one arrives at a mesh — used to return a flat, hint-free
/// ABSENT. Now the tools RE-RESOLVE the root-prefixed form and say so when it hits.
///
/// Drives the REAL service (<see cref="LoadOrderService.NifInspect"/> / <see cref="LoadOrderService.NifSet"/> /
/// <see cref="LoadOrderService.AssetStatus"/>) over a synthetic MO2 instance with loose assets — no game data, no
/// BSArch. The instance holds <c>meshes\actors\canine\wolf.nif</c> and <c>textures\actors\canine\wolf.dds</c>, so a
/// record-relative <c>actors\canine\wolf.nif</c> is exactly the reported repro.
///
/// The arms are deliberately paired — the SUGGESTION and its ABSENCE are equally load-bearing, because the failure
/// mode a bad "did you mean" introduces (sending a caller to a file that isn't there) is worse than the papercut it
/// fixes (<see cref="PluginNameSuggest"/>'s posture, which this follows):
///   FIRES        — nif_inspect on the record-relative path names the meshes\-prefixed path that really is provided.
///   NOT-A-GUESS  — a record-relative path whose prefixed form ALSO misses gets NO "did you mean" (only the weaker
///                  convention note, which names no file). This is the arm a string-heuristic implementation fails.
///   NO-NOISE     — a path already under meshes\ that is simply absent gets no hint at all: the prefix isn't wrong.
///   NIF-SET      — the same suggestion rides the nif_set refusal (the sibling site, same input mistake).
///   ASSET-MESH / ASSET-TEX — asset_status tries BOTH roots, since that lane can't know the path's kind.
///   ASSET-QUIET  — asset_status stays silent for a genuinely-absent path AND for a non-asset-root path
///                  (sound\, scripts\ … legitimately live there; a meshes\ lecture would be noise), and prints
///                  nothing at all when the path resolves.
///   PLACE / PLACE-TEX / PLACE-QUIET / PLACE-SOURCE (#283) — the fourth lane: place_asset's auto-resolve refusal
///                  carries the same both-roots verified suggestion, stays silent when there's nothing honest to
///                  say, and — the arm that pins the scope — leaves the explicit-source= lane alone, where an
///                  unprovided destination is the NORMAL case (placing a brand-new file), not a mistake.
///
/// Run: dotnet run --project src/housecarl-generator -- asset-prefix-hint-guard
/// </summary>
internal static class AssetPrefixHintProbe
{
    const string MeshRel = @"meshes\actors\canine\wolf.nif";      // what the file really is, Data-relative
    const string RecordRel = @"actors\canine\wolf.nif";           // what the RECORD's Model.File holds — the repro
    const string TexRel = @"textures\actors\canine\wolf.dds";
    const string TexRecordRel = @"actors\canine\wolf.dds";

    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" asset-prefix-hint guard — a record-relative path gets a VERIFIED root suggestion, never a guess (#273)");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label, string? detail = null)
        {
            Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label + (c || detail is null ? "" : $"\n        -> {detail}"));
            if (!c) fail++;
        }

        var root = Path.Combine(Path.GetTempPath(), "hc-asset-prefix-hint-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            var inst = Path.Combine(root, "inst");
            var mods = Path.Combine(inst, "mods");
            var data = Path.Combine(inst, "game", "Data");
            var prof = Path.Combine(inst, "profiles", "Default");
            var modA = Path.Combine(mods, "WolfMod");
            foreach (var d in new[] { data, prof, modA }) Directory.CreateDirectory(d);
            WriteLoose(modA, MeshRel);
            WriteLoose(modA, TexRel);
            WriteProfile(prof, Array.Empty<string>(), Array.Empty<string>(), new[] { "+WolfMod" });
            WriteSkyrimIni(prof);
            WriteIni(inst, "Default", Path.Combine(inst, "game"));

            using var svc = LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(root, "user.json")));

            // ---------- nif_inspect ----------
            Console.WriteLine("--- nif_inspect ---");
            var batch = svc.NifInspect(new[] { RecordRel, @"nowhere\ghost.nif", @"meshes\nowhere\ghost.nif", MeshRel }, null);
            var rRecord = batch.Results[0];
            var rNoHit = batch.Results[1];
            var rPrefixed = batch.Results[2];
            var rReal = batch.Results[3];

            Check(rRecord.Absent && (rRecord.Error?.Contains("Did you mean `" + MeshRel + "`", StringComparison.OrdinalIgnoreCase) ?? false),
                  "FIRES — the record-relative path is ABSENT, and the answer names the meshes\\-prefixed path that IS provided",
                  rRecord.Error);
            // The teeth against a string heuristic: this path LOOKS exactly as record-relative as the one above.
            // Only a real re-resolve can tell them apart, so a "did you mean" here would be a wrong suggestion.
            Check(rNoHit.Absent
                  && !(rNoHit.Error?.Contains("Did you mean", StringComparison.OrdinalIgnoreCase) ?? true)
                  && (rNoHit.Error?.Contains(@"`meshes\nowhere\ghost.nif`", StringComparison.OrdinalIgnoreCase) ?? false)
                  && (rNoHit.Error?.Contains("not provided", StringComparison.OrdinalIgnoreCase) ?? false),
                  "NOT-A-GUESS — a record-relative path whose prefixed form ALSO misses gets no 'did you mean', only the convention note (which claims no file)",
                  rNoHit.Error);
            bool prefixedQuiet = rPrefixed.Error is { } prefErr
                                 && !prefErr.Contains("Did you mean", StringComparison.OrdinalIgnoreCase)
                                 && !prefErr.Contains("stored relative to", StringComparison.OrdinalIgnoreCase);
            Check(rPrefixed.Absent && prefixedQuiet,
                  "NO-NOISE — a path already under meshes\\ that is simply absent gets no prefix hint at all",
                  rPrefixed.Error);
            Check(!rReal.Absent && rReal.Providers.Count == 1,
                  "CONTROL — the real Data-relative path still resolves (the hint path never touches a hit)",
                  rReal.Error);

            // ---------- nif_set (the sibling site) ----------
            Console.WriteLine();
            Console.WriteLine("--- nif_set ---");
            var set = svc.NifSet(RecordRel, new[] { new NifSetOp(NifSetOpKind.RenameShape, "Old", NewName: "New") },
                                 null, null, null, false, false);
            Check(set.Error is not null && (set.Error?.Contains("Did you mean `" + MeshRel + "`", StringComparison.OrdinalIgnoreCase) ?? false),
                  "NIF-SET — the same verified suggestion rides the nif_set ABSENT refusal", set.Error);

            // ---------- asset_status (both roots; verified-only, no speculative note) ----------
            Console.WriteLine();
            Console.WriteLine("--- asset_status ---");
            var st = svc.AssetStatus(new[] { RecordRel, TexRecordRel, @"nowhere\ghost.nif", @"sound\fx\ghost.wav", MeshRel });
            var sMesh = st.Results[0];
            var sTex = st.Results[1];
            var sMiss = st.Results[2];
            var sOther = st.Results[3];
            var sHit = st.Results[4];

            Check(sMesh.Hit is { Exists: false } && (sMesh.PrefixSuggestions?.Contains(MeshRel) ?? false),
                  "ASSET-MESH — a record-relative mesh path suggests its meshes\\ form",
                  string.Join(",", sMesh.PrefixSuggestions ?? Array.Empty<string>()));
            Check(sTex.Hit is { Exists: false } && (sTex.PrefixSuggestions?.Contains(TexRel) ?? false),
                  "ASSET-TEX — a record-relative texture path suggests its textures\\ form (this lane can't know the kind, so it tries both)",
                  string.Join(",", sTex.PrefixSuggestions ?? Array.Empty<string>()));
            Check(sMiss.Hit is { Exists: false } && (sMiss.PrefixSuggestions?.Count ?? 0) == 0,
                  "ASSET-QUIET — a genuinely absent path suggests nothing",
                  string.Join(",", sMiss.PrefixSuggestions ?? Array.Empty<string>()));
            Check(sOther.Hit is { Exists: false } && (sOther.PrefixSuggestions?.Count ?? 0) == 0,
                  "ASSET-QUIET — a non-asset-root path (sound\\) gets no meshes\\ lecture",
                  string.Join(",", sOther.PrefixSuggestions ?? Array.Empty<string>()));
            Check(sHit.Hit is { Exists: true } && (sHit.PrefixSuggestions?.Count ?? 0) == 0,
                  "ASSET-QUIET — a path that RESOLVES carries no suggestion");

            // ---------- place_asset (the fourth lane, #283) ----------
            Console.WriteLine();
            Console.WriteLine("--- place_asset ---");
            var pRecord = svc.PlaceAssets(new[] { new PlaceRequest(RecordRel, null) }, null, null).Results[0];
            Check(!pRecord.Placed && (pRecord.Error?.Contains("Did you mean `" + MeshRel + "`", StringComparison.OrdinalIgnoreCase) ?? false),
                  "PLACE — the auto-resolve refusal names the meshes\\-prefixed copy that IS provided", pRecord.Error);
            var pTex = svc.PlaceAssets(new[] { new PlaceRequest(TexRecordRel, null) }, null, null).Results[0];
            Check(!pTex.Placed && (pTex.Error?.Contains("Did you mean `" + TexRel + "`", StringComparison.OrdinalIgnoreCase) ?? false),
                  "PLACE-TEX — both roots are tried here too (place_asset can't know the path's kind)", pTex.Error);
            // Same teeth as NOT-A-GUESS above, and the quiet arms: verified-only means silence is the default.
            var pMiss = svc.PlaceAssets(new[] { new PlaceRequest(@"nowhere\ghost.nif", null) }, null, null).Results[0];
            Check(!pMiss.Placed && !(pMiss.Error?.Contains("Did you mean", StringComparison.OrdinalIgnoreCase) ?? true),
                  "PLACE-QUIET — a path whose prefixed form ALSO misses gets no 'did you mean'", pMiss.Error);
            var pOther = svc.PlaceAssets(new[] { new PlaceRequest(@"sound\fx\ghost.wav", null) }, null, null).Results[0];
            Check(!pOther.Placed && !(pOther.Error?.Contains("Did you mean", StringComparison.OrdinalIgnoreCase) ?? true),
                  "PLACE-QUIET — a non-asset-root path (sound\\) gets no meshes\\ lecture", pOther.Error);
            // The hint rides the AUTO-RESOLVE arm only. With source= named, an unprovided destination is the NORMAL
            // case (placing a brand-new file), so it must still place cleanly and say nothing about roots.
            var newSrc = Path.Combine(root, "brand-new.nif");
            File.WriteAllText(newSrc, "y");
            var pNew = svc.PlaceAssets(new[] { new PlaceRequest(RecordRel, newSrc) }, null, null).Results[0];
            Check(pNew.Placed && pNew.Error is null,
                  "PLACE-SOURCE — an explicit source= still places at a path nothing provides (the hint never fires on that arm)", pNew.Error);

            // The rendered surface is what the user actually reads — a data-only fix would be invisible.
            var rendered = AssetWire.Render(st, 20000);
            Check(rendered.Contains("did you mean", StringComparison.OrdinalIgnoreCase) && rendered.Contains(MeshRel, StringComparison.OrdinalIgnoreCase),
                  "RENDER — the suggestion reaches the rendered asset_status output, not just the data record");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ } }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "================ ALL PASS ================" : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }

    // ---- synthetic MO2 layout helpers (the asset-status guard's shape) ----

    static void WriteProfile(string profDir, string[] loadorder, string[] plugins, string[] modlist)
    {
        Directory.CreateDirectory(profDir);
        File.WriteAllText(Path.Combine(profDir, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", loadorder) + "\r\n");
        File.WriteAllText(Path.Combine(profDir, "plugins.txt"), string.Join("\r\n", plugins) + "\r\n");
        File.WriteAllText(Path.Combine(profDir, "modlist.txt"), "# header\r\n" + string.Join("\r\n", modlist) + "\r\n");
    }

    static void WriteSkyrimIni(string profDir) =>
        File.WriteAllText(Path.Combine(profDir, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");

    static void WriteIni(string inst, string profile, string gameDir) =>
        File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(" + profile + ")\r\ngamePath=@ByteArray("
            + gameDir.Replace(@"\", @"\\") + ")\r\n");

    static void WriteLoose(string baseDir, string rel)
    {
        var p = Path.Combine(baseDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "x");
    }
}

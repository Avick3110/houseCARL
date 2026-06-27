using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

// ======================================================================
//  AssetRenameService — carry FormID-KEYED assets across a renumber (compact/merge Wave A1).
//
//  THE PROBLEM (research MERGE_REFERENCE_RESEARCH_2026-06-26 §3 / §8): renumbering a plugin's
//  records (compact's RenumberModInto) moves every record to a NEW FormID, but the asset FILES
//  the engine looks up BY that FormID — FaceGen head meshes/tints, voice .fuz/.lip — keep their
//  OLD-FormID names. The engine then can't find them at the new FormID, so a compacted NPC mod
//  silently dark-faces and a voiced mod goes mute. xEdit/zMerge/Merge Plugins all rename these
//  along the same old→new map; houseCARL must too, or compact is a Q3 "silently degraded mode".
//
//  This is the shared SPINE both compact and merge ride: given the renumber's old→new FormKey map
//  and the written P′, carry each renumbered record's FormID-keyed assets to their NEW-FormID
//  paths under the P′ mod folder. A1 covers FACEGEN (the dominant break — the dark-face bug);
//  voice (A2), SEQ + strings (A3) extend the same service.
//
//  COMPOSES existing, Aaron-locked primitives — NO new path logic:
//    • FaceGenPath.For (the pure FormKey→path transform; folder = the FormKey's defining master,
//      so For(oldKey) and For(newKey) give the correct OLD and NEW paths automatically).
//    • AssetResolver (resolve the OLD path to its load-order WINNING on-disk bytes, loose or BSA).
//    • AtomicFile.WriteAllBytes (the same crash-atomic place_asset uses).
//
//  Q3 — NEVER throws and NEVER fails the compact: the RECORDS are already written correctly by the
//  time this runs; a facegen it can't carry is a NAMED warning in the outcome, not a crash. An NPC
//  with NO own facegen is NORMAL (vanilla/inherited head), not a failure — only a facegen that was
//  FOUND but couldn't be written is a warning. A BSA that failed to read is surfaced (ReadIncomplete)
//  so "no facegen" is never silently trusted.
//
//  NON-DESTRUCTIVE: writes only the NEW-FormID copies under the P′ mod folder (the fresh folder in
//  the new-file lane; the target's own folder in the in-place lane). The OLD-FormID files are left
//  untouched — harmless orphans the engine no longer looks up (project_nondestructive_output_policy).
// ======================================================================

/// <summary>The accounting of one asset-rename pass (A1: facegen). <see cref="NpcCount"/> = renumbered NPCs considered
/// (the denominator). <see cref="FacegenNpcsCarried"/> = those for which ≥1 facegen file was carried; <see cref="FacegenFilesCarried"/>
/// = total files written (mesh + tint). <see cref="Failures"/> = facegen that was FOUND but could not be written (Q3 — real
/// problems, named; an NPC simply WITHOUT facegen is not a failure). <see cref="ReadIncomplete"/> = a BSA failed to read this
/// scan, so a "no facegen" answer may be incomplete (a facegen present only in an unreadable archive looks absent).</summary>
public sealed record AssetRenameOutcome(
    int NpcCount, int FacegenNpcsCarried, int FacegenFilesCarried,
    IReadOnlyList<string> Failures, bool ReadIncomplete)
{
    /// <summary>Nothing carried (no renumbered NPCs, or the carry was not run) — a clean zero, ReadIncomplete propagated.</summary>
    public static AssetRenameOutcome None(bool readIncomplete = false) =>
        new(0, 0, 0, Array.Empty<string>(), readIncomplete);
}

public static class AssetRenameService
{
    /// <summary>Carry the FaceGen assets (head mesh + face tint) of every RENUMBERED NPC in <paramref name="pPrimePath"/>
    /// from their OLD-FormID path to their NEW-FormID path, writing the new copies under <paramref name="outDir"/> (the
    /// P′ mod-folder root — pass <c>Path.GetDirectoryName(outPath)</c>, which is that root in BOTH the new-file and
    /// in-place lanes). <paramref name="map"/> is the renumber's old→new FormKey map; only NPCs whose new key is a map
    /// VALUE (i.e. were renumbered) are carried — override NPCs kept at a master key are left alone. <paramref name="assets"/>
    /// is a pinned resolver view (its winner is the copy that currently displays in-game). Best-effort + reported (Q3):
    /// records are already written, so this never throws and never fails the compact.</summary>
    public static AssetRenameOutcome CarryFaceGen(
        string pPrimePath, IReadOnlyDictionary<FormKey, FormKey> map, AssetResolver.AssetView assets, string outDir)
    {
        // 1. Which renumbered records are NPCs? Read P′ back and intersect its NPCs with the map's NEW keys (the values).
        //    Reverse the map ONCE (new→old) so each renumbered NPC yields its old key (for the old asset path). Reading the
        //    just-written P′ is a transient overlay (disposed immediately — zero handle at rest, the codebase idiom).
        List<(FormKey Old, FormKey New)> npcs;
        try
        {
            using var pp = SkyrimMod.CreateFromBinaryOverlay(pPrimePath, SkyrimRelease.SkyrimSE);
            var reverse = new Dictionary<FormKey, FormKey>(map.Count);
            foreach (var kv in map) reverse[kv.Value] = kv.Key;            // new → old
            npcs = pp.Npcs.Where(n => reverse.ContainsKey(n.FormKey))
                          .Select(n => (Old: reverse[n.FormKey], New: n.FormKey))
                          .ToList();
        }
        catch (Exception ex)
        {
            // Can't read P′ back ⇒ can't find which NPCs to carry. The compact SUCCEEDED; this is a degraded asset pass,
            // surfaced as a named warning (Q3) rather than a silent skip.
            return new AssetRenameOutcome(0, 0, 0,
                new[] { $"could not read '{Path.GetFileName(pPrimePath)}' back to find its NPCs for facegen carry ({ex.Message}) — verify NPC faces in-game." },
                assets.ReadIncomplete);
        }

        if (npcs.Count == 0) return AssetRenameOutcome.None(assets.ReadIncomplete);

        // 2. Carry each NPC's mesh + tint from the old path's WINNING copy to the new path.
        var failures = new List<string>();
        int npcsCarried = 0, files = 0;
        foreach (var (oldKey, newKey) in npcs)
        {
            int carriedForNpc = 0;
            foreach (var (slot, oldPath) in FaceGenPath.Both(oldKey))      // (Mesh, …), (Tint, …) — the dark-face pair
            {
                var res = assets.ResolveForPlacement(oldPath);
                if (res.Sources.Count == 0) continue;                      // no facegen for this slot — NORMAL (vanilla/inherited head)

                var (bytes, err) = ReadWinner(res.Sources[0]);             // the copy that currently displays in-game
                if (err is not null) { failures.Add($"{oldKey.ID:X6} {slot}: {err}"); continue; }

                var dest = Path.Combine(outDir, FaceGenPath.For(newKey, slot));
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    AtomicFile.WriteAllBytes(dest, bytes!);
                    // Belt-and-braces truncation guard (the PlaceOne idiom): a size mismatch means a short write.
                    long size; try { size = new FileInfo(dest).Length; } catch { size = -1; }
                    if (size != bytes!.Length)
                    { failures.Add($"{newKey.ID:X6} {slot}: wrote {size} byte(s), expected {bytes.Length} — verify."); continue; }
                    files++; carriedForNpc++;
                }
                catch (Exception ex) { failures.Add($"{newKey.ID:X6} {slot}: could not write '{FaceGenPath.For(newKey, slot)}' — {ex.Message}"); }
            }
            if (carriedForNpc > 0) npcsCarried++;
        }
        return new AssetRenameOutcome(npcs.Count, npcsCarried, files, failures, assets.ReadIncomplete);
    }

    /// <summary>Read the bytes of a resolved WINNING provider — a loose file off disk, or a single BSA entry via native
    /// Mutagen (zero handle at rest, the AssetResolver.TryReadArchiveEntry cornerstone). Mirrors LoadOrderService.ReadResolvedSource
    /// but lives in core (the service's home) so the spine has no mcp dependency. A named error (Q3) if the resolved copy
    /// vanished between resolve and read, or the archive can't be read.</summary>
    static (byte[]? bytes, string? error) ReadWinner(PlacementSource s)
    {
        if (s.Kind == AssetKind.Loose)
        {
            var p = s.LooseFilePath!;
            if (!File.Exists(p)) return (null, $"the resolved loose source '{p}' is no longer on disk");
            try { return (File.ReadAllBytes(p), null); }
            catch (Exception ex) { return (null, $"could not read resolved source '{p}': {ex.Message}"); }
        }
        try
        {
            var b = AssetResolver.TryReadArchiveEntry(s.ArchivePath!, s.EntryPath);
            return b is null ? (null, $"entry '{s.EntryPath}' not found inside '{Path.GetFileName(s.ArchivePath!)}'") : (b, null);
        }
        catch (Exception ex) { return (null, $"could not read archive '{Path.GetFileName(s.ArchivePath!)}': {ex.Message}"); }
    }
}

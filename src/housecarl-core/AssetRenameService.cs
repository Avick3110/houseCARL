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
//  A2 covers VOICE (.fuz/.lip — a compacted voiced mod otherwise goes mute) — A1+A2 ride ONE shared
//  two-phase carry (CarryItems), so the in-place aliasing fix (PR #123) lives in exactly one place,
//  never two diverging copies. A3 covers SEQ (RegenerateSeq) — NOT a map-rename carry: a .seq lists
//  each start-game-enabled quest's master-relative on-disk FormID, every one of which a renumber
//  shifts, so it is REBUILT from P′ (SeqFile.Build), not renamed. Strings stay OUT of the spine —
//  they're plugin-name-keyed, untouched by a renumber (a merge-only edge, not a compact break).
//
//  COMPOSES existing, Aaron-locked primitives — NO new path logic:
//    • FaceGenPath.For / VoicePath (the pure FormKey→path transforms; folder = the FormKey's
//      defining master, so For(oldKey) and For(newKey) give the correct OLD and NEW paths).
//    • AssetResolver (resolve the OLD path to its load-order WINNING on-disk bytes, loose or BSA;
//      EnumerateUnder lists what voice files actually exist under the plugin's voice prefix).
//    • AtomicFile.WriteAllBytes / Commit (the same crash-atomic place_asset uses).
//
//  Q3 — NEVER throws and NEVER fails the compact: the RECORDS are already written correctly by the
//  time this runs; an asset it can't carry is a NAMED warning in the outcome, not a crash. An NPC
//  with NO own facegen is NORMAL (vanilla/inherited head), not a failure — only an asset that was
//  FOUND but couldn't be written is a warning. A BSA that failed to read is surfaced (ReadIncomplete)
//  so "no facegen"/"no voice" is never silently trusted.
//
//  WHY VOICE SCANS DISK (A2 discovery, strategy b): a facegen path is a PURE FormKey transform, but a
//  voice filename embeds the parent quest/topic EditorIDs, the voice type, AND a response number —
//  none of which a renumber changes. On a compact the plugin keeps its basename, so ONLY the
//  '00<6hex>' id segment differs old→new. So instead of re-deriving the dialogue graph per INFO
//  (which silently misses radiant/quest-alias lines whose graph won't resolve), CarryVoice ENUMERATES
//  the voice files actually present under Sound\Voice\<plugin>\ and rewrites the id segment of each
//  whose embedded FormID was renumbered — catching every file the engine would lose, the way
//  zMerge/xEdit do it. Matching is purely by the on-disk filename, so it needs no record readback.
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

/// <summary>The accounting of the voice-carry pass (A2). <see cref="FilesScanned"/> = voice files found under the
/// plugin's <c>Sound\Voice\&lt;plugin&gt;\</c> prefix (the denominator). <see cref="FilesCarried"/> = those whose
/// embedded FormID was renumbered and that were written to the new id; <see cref="LinesCarried"/> = the distinct
/// dialogue lines (INFOs) those files belong to. <see cref="Failures"/> = a voice file FOUND but not writable (Q3 —
/// a file simply NOT keyed to a renumbered line is not a failure). <see cref="ReadIncomplete"/> = a BSA failed to
/// read this scan, so a "no voice" answer may be incomplete (audio present only in an unreadable archive looks absent).</summary>
public sealed record VoiceCarryOutcome(
    int FilesScanned, int FilesCarried, int LinesCarried,
    IReadOnlyList<string> Failures, bool ReadIncomplete)
{
    /// <summary>Nothing carried (no renumbered records, or no voice files) — a clean zero, ReadIncomplete propagated.</summary>
    public static VoiceCarryOutcome None(bool readIncomplete = false) =>
        new(0, 0, 0, Array.Empty<string>(), readIncomplete);
}

/// <summary>The accounting of the SEQ-regeneration pass (A3). A compact renumbers a plugin's start-game-enabled quests,
/// so the master-relative on-disk FormIDs any pre-existing <c>.seq</c> lists go STALE and those quests would then never
/// start (the silent-failure class <see cref="SeqFile"/> exists to prevent). Unlike facegen/voice this is NOT a map-rename
/// — the <c>.seq</c> is REBUILT from the renumbered plugin. <see cref="SgeQuestCount"/> = start-game-enabled quests written
/// into the <c>.seq</c> (0 ⇒ the plugin has none → a clean no-op, no file written). <see cref="Written"/> ⇒ a fresh,
/// correct <c>.seq</c> was committed at <see cref="SeqPath"/>. <see cref="Failures"/> = the regen was NEEDED (SGE quests
/// present) but the <c>.seq</c> could not be built/written (Q3 — named, never a silent stale <c>.seq</c>).</summary>
public sealed record SeqRegenOutcome(
    int SgeQuestCount, bool Written, string? SeqPath, IReadOnlyList<string> Failures)
{
    /// <summary>No start-game-enabled quests — nothing to write (no <c>.seq</c> cut), a clean no-op.</summary>
    public static SeqRegenOutcome None() => new(0, false, null, Array.Empty<string>());
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

        // 2. Build the carry list — each renumbered NPC's mesh + tint, OLD path → NEW path — and run it through the shared
        //    two-phase carry (CarryItems): all reads stage to temps before ANY commit, so the in-place lane can't read-after
        //    -write alias one NPC's facegen over another's not-yet-read file (PR #123). The dark-face pair is placed together.
        var items = new List<CarryItem>();
        foreach (var (oldKey, newKey) in npcs)
            foreach (var (slot, oldPath) in FaceGenPath.Both(oldKey))      // (Mesh, …), (Tint, …) — the dark-face pair
                items.Add(new CarryItem(oldPath, FaceGenPath.For(newKey, slot), newKey, $"{oldKey.ID:X6}→{newKey.ID:X6} {slot}"));

        var failures = new List<string>();
        var (files, carried) = CarryItems(items, assets, outDir, failures);
        return new AssetRenameOutcome(npcs.Count, carried.Count, files, failures, assets.ReadIncomplete);
    }

    /// <summary>Carry the VOICE files (.fuz/.lip/…) of every RENUMBERED dialogue line (INFO) in <paramref name="pPrimePath"/>
    /// from their OLD-FormID name to their NEW-FormID name, writing the new copies under <paramref name="outDir"/> (the P′
    /// mod-folder root, as <see cref="CarryFaceGen"/>). DISCOVERS by SCANNING the plugin's voice prefix rather than re-deriving
    /// the dialogue graph (see the file header): every file under <c>Sound\Voice\&lt;basename&gt;\</c> whose embedded local
    /// FormID is a renumbered key in <paramref name="map"/> gets its id segment rewritten to the new id. On a compact the
    /// plugin keeps its basename, so the folder + voice-type + quest/topic + response-number segments are unchanged — ONLY the
    /// id moves. Rides the same two-phase <see cref="CarryItems"/> as facegen (same in-place aliasing guard). Best-effort +
    /// reported (Q3): records are already written, so this never throws and never fails the compact.</summary>
    public static VoiceCarryOutcome CarryVoice(
        string pPrimePath, IReadOnlyDictionary<FormKey, FormKey> map, AssetResolver.AssetView assets, string outDir)
    {
        var basename = Path.GetFileName(pPrimePath);                        // P′ keeps the source basename → the voice folder name (with ext)

        // local-id → new-local-id for THIS plugin's renumbered records — the only ones whose voice lives under
        // Sound\Voice\<basename>\ (an override kept at a master key has its voice under the MASTER's folder, untouched).
        var idMap = new Dictionary<uint, uint>();
        foreach (var kv in map)
            if (string.Equals(kv.Key.ModKey.FileName.ToString(), basename, StringComparison.OrdinalIgnoreCase))
                idMap[kv.Key.ID] = kv.Value.ID;
        if (idMap.Count == 0) return VoiceCarryOutcome.None(assets.ReadIncomplete);

        // The new INFO FormKeys need a ModKey for the distinct-line accounting; basename came from a real plugin path, so
        // this is valid (a malformed name is surfaced rather than silently producing a zero pass — Q3).
        ModKey modKey;
        try { modKey = ModKey.FromFileName(basename); }
        catch (Exception ex)
        {
            return new VoiceCarryOutcome(0, 0, 0,
                new[] { $"'{basename}' is not a valid plugin filename for voice carry ({ex.Message}) — verify voiced lines in-game." },
                assets.ReadIncomplete);
        }

        IReadOnlyCollection<string> files;
        try { files = assets.EnumerateUnder($@"Sound\Voice\{basename}"); }
        catch (Exception ex)
        {
            return new VoiceCarryOutcome(0, 0, 0,
                new[] { $@"could not scan 'Sound\Voice\{basename}' for voice files ({ex.Message}) — verify voiced lines in-game." },
                assets.ReadIncomplete);
        }
        if (files.Count == 0) return VoiceCarryOutcome.None(assets.ReadIncomplete);

        // Build the carry list: each voice file whose embedded id was renumbered → its new-id name (ONLY the id segment
        // changes — see the header). A file with no '_<8hex>_<num>.<ext>' tail, or whose id wasn't renumbered, is left alone.
        var items = new List<CarryItem>();
        foreach (var oldRel in files)
        {
            var fname = Path.GetFileName(oldRel);
            var m = VoiceIdRx.Match(fname);
            if (!m.Success) continue;                                      // not an INFO-keyed voice file — nothing to remap
            uint full;
            try { full = Convert.ToUInt32(m.Groups[1].Value, 16); } catch { continue; }
            uint oldLocal = full & 0xFFFFFFu;                              // mask the index byte, exactly like VoicePath emits "00"+6hex
            if (!idMap.TryGetValue(oldLocal, out var newLocal)) continue;  // id not renumbered → filename unchanged → no carry

            var newId = "00" + newLocal.ToString("X6");
            var newFname = fname.Substring(0, m.Groups[1].Index) + newId + fname.Substring(m.Groups[1].Index + m.Groups[1].Length);
            var dir = Path.GetDirectoryName(oldRel) ?? "";                 // same voice-type folder — only the filename's id moves
            var newRel = dir.Length == 0 ? newFname : Path.Combine(dir, newFname);
            items.Add(new CarryItem(oldRel, newRel, new FormKey(modKey, newLocal), $"{oldLocal:X6}→{newLocal:X6} {fname}"));
        }

        var failures = new List<string>();
        var (carriedFiles, carriedLines) = CarryItems(items, assets, outDir, failures);
        return new VoiceCarryOutcome(files.Count, carriedFiles, carriedLines.Count, failures, assets.ReadIncomplete);
    }

    /// <summary>(Re)generate the start-game-enabled-quest <c>.seq</c> for the RENUMBERED plugin <paramref name="pPrimePath"/>,
    /// writing it to <c>&lt;outDir&gt;\SEQ\&lt;basename&gt;.seq</c> (<paramref name="outDir"/> = the P′ mod-folder root, as the
    /// carry methods take — <c>Path.GetDirectoryName(outPath)</c>, that root in BOTH lanes). Unlike <see cref="CarryFaceGen"/>/
    /// <see cref="CarryVoice"/> this is NOT a map-rename and needs no map/resolver/AssetView: a <c>.seq</c> lists each SGE
    /// quest's master-relative ON-DISK FormID, and a renumber shifts every one, so the file is REBUILT from scratch off P′ via
    /// <see cref="SeqFile.Build"/> — the same regeneration <c>housecarl_write_seq</c> runs — and the FormIDs come out correct
    /// because they're read from the already-renumbered plugin. A plugin with NO SGE quests writes nothing and cuts no folder
    /// (a clean no-op, mirroring write_seq). Engine-correct placement: the game reads <c>Data\SEQ\</c>, so the <c>.seq</c> lands
    /// in a <c>SEQ\</c> subfolder of the mod root (NOT the root, where facegen/voice files sit at their own Data-relative paths).
    /// Best-effort + reported (Q3): the records are already written, so a <c>.seq</c> it can't build/write is a NAMED warning,
    /// never a failure of the compact; never throws.</summary>
    public static SeqRegenOutcome RegenerateSeq(string pPrimePath, string outDir)
    {
        SeqFile.SeqBuild built;
        try { built = SeqFile.Build(pPrimePath); }
        catch (Exception ex)
        {
            // Can't read P′ back ⇒ can't (re)build its .seq. The compact SUCCEEDED; this is a degraded SEQ pass, surfaced
            // as a named warning (Q3) rather than a silent stale/missing .seq.
            return new SeqRegenOutcome(0, false, null,
                new[] { $"could not read '{Path.GetFileName(pPrimePath)}' back to (re)build its .seq ({ex.Message}) — if it has start-game-enabled quests, run housecarl_write_seq on the compacted plugin." });
        }

        if (built.Quests.Count == 0) return SeqRegenOutcome.None();        // no SGE quests → no .seq needed (the write_seq no-op)

        var dest = Path.Combine(outDir, "SEQ", Path.GetFileNameWithoutExtension(pPrimePath) + ".seq");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);       // AtomicFile.WriteAllBytes does NOT create the dir
            AtomicFile.WriteAllBytes(dest, built.Bytes);
            long size; try { size = new FileInfo(dest).Length; } catch { size = -1; }
            if (size != built.Bytes.Length)                               // truncation guard (the .seq is tiny; a short write is a real fault)
                return new SeqRegenOutcome(built.Quests.Count, false, null,
                    new[] { $"wrote {size} byte(s) to '{Path.GetFileName(dest)}', expected {built.Bytes.Length} — verify the .seq." });
        }
        catch (Exception ex)
        {
            return new SeqRegenOutcome(built.Quests.Count, false, null,
                new[] { $"could not write '{Path.GetFileName(dest)}' ({ex.Message}) — its start-game-enabled quests may not start; run housecarl_write_seq on the compacted plugin." });
        }
        return new SeqRegenOutcome(built.Quests.Count, true, dest, Array.Empty<string>());
    }

    /// <summary>One asset to carry: read <see cref="OldPath"/>'s winning on-disk copy and place its bytes at
    /// <see cref="NewPath"/> under the output dir. <see cref="Owner"/> is the renumbered record the asset belongs to (the
    /// distinct-owner count = NPCs-carried for facegen, lines-carried for voice); <see cref="Label"/> prefixes any failure.</summary>
    readonly record struct CarryItem(string OldPath, string NewPath, FormKey Owner, string Label);

    /// <summary>The shared TWO-PHASE carry both facegen (A1) and voice (A2) ride. Phase 1: resolve each item's OLD path to
    /// its WINNING on-disk bytes and stage them to a '.houseCARL-tmp' SIBLING of the final new path (a name that never
    /// collides with an old '00&lt;hex&gt;.ext' read path). Phase 2: once EVERY read is done, commit the temps via
    /// <see cref="AtomicFile.Commit"/>. Staging-before-committing is the in-place-aliasing fix (PR #123): the renumber packs
    /// the new ids into the same window the source used, so in the in-place lane (outDir == the donor's own folder) a direct
    /// new-id write could clobber a DIFFERENT record's not-yet-read old-id file — handing it the wrong face/voice (a Q3 silent
    /// wrong answer). O(1) memory per file (staged to disk, not buffered). A resolve MISS is skipped silently (the caller
    /// decides whether "absent" is normal); a FOUND-but-unwritable file is a NAMED failure. Returns (files committed, the set
    /// of distinct owners that had ≥1 file committed). Never throws — the records are already written.</summary>
    static (int Files, HashSet<FormKey> Owners) CarryItems(
        IReadOnlyList<CarryItem> items, AssetResolver.AssetView assets, string outDir, List<string> failures)
    {
        var pending = new List<(string Staged, string Final, FormKey Owner)>();

        // ---- phase 1: read every old asset + stage it to a temp (writes ONLY '.houseCARL-tmp', never an old read path) ----
        foreach (var it in items)
        {
            var res = assets.ResolveForPlacement(it.OldPath);
            if (res.Sources.Count == 0) continue;                          // not on disk — the caller decides if that's normal

            var (bytes, err) = ReadWinner(res.Sources[0]);                 // the copy that currently displays/plays in-game
            if (err is not null) { failures.Add($"{it.Label}: {err}"); continue; }

            var final = Path.Combine(outDir, it.NewPath);
            var staged = final + ".houseCARL-tmp";                         // sibling temp — distinct from every old read path
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(final)!);
                try { if (File.Exists(staged)) File.Delete(staged); } catch { /* a stuck temp surfaces on the write below */ }
                File.WriteAllBytes(staged, bytes!);
                // Truncation guard on the TEMP (the commit is an atomic rename, which can't truncate): a short stage is caught here.
                long size; try { size = new FileInfo(staged).Length; } catch { size = -1; }
                if (size != bytes!.Length)
                {
                    failures.Add($"{it.Label}: staged {size} byte(s), expected {bytes.Length} — verify.");
                    try { File.Delete(staged); } catch { }
                    continue;
                }
                pending.Add((staged, final, it.Owner));
            }
            catch (Exception ex)
            {
                failures.Add($"{it.Label}: could not stage '{it.NewPath}' — {ex.Message}");
                try { if (File.Exists(staged)) File.Delete(staged); } catch { }
            }
        }

        // ---- phase 2: commit every staged temp (all reads done → overwriting an old file at a now-reused name is safe) ----
        int files = 0;
        var owners = new HashSet<FormKey>();
        foreach (var (staged, final, owner) in pending)
        {
            try { AtomicFile.Commit(staged, final); files++; owners.Add(owner); }
            catch (Exception ex)
            {
                failures.Add($"could not commit '{Path.GetFileName(final)}' — {ex.Message}");
                try { if (File.Exists(staged)) File.Delete(staged); } catch { }
            }
        }
        return (files, owners);
    }

    /// <summary>Matches a voice file's '_&lt;8hex&gt;_&lt;response&gt;.&lt;ext&gt;' tail (anchored to the end, so quest/topic
    /// EditorID segments that themselves contain underscores or hex don't confuse it). Group 1 is the 8-hex FormID segment —
    /// the only part a renumber moves. Compiled: CarryVoice runs it once per discovered file.</summary>
    static readonly System.Text.RegularExpressions.Regex VoiceIdRx =
        new(@"_([0-9A-Fa-f]{8})_\d+\.[A-Za-z0-9]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

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

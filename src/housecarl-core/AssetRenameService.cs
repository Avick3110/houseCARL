using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

// AssetRenameService — carry FormID-KEYED assets (facegen, voice) across a renumber, plus the .seq rebuild that is
// not a rename. The two-phase carry, the non-destructive rule, the disk-scan voice discovery and the refresh-only
// .seq are contracts in docs/architecture/assets.md; pinned by facegen-carry-guard and voice-carry-guard.

/// <summary>The accounting of one facegen pass: renumbered NPCs considered, those with ≥1 file carried, files
/// written, FOUND-but-unwritable failures, and whether a BSA failed to read (so "no facegen" may be incomplete).</summary>
public sealed record AssetRenameOutcome(
    int NpcCount, int FacegenNpcsCarried, int FacegenFilesCarried,
    IReadOnlyList<string> Failures, bool ReadIncomplete)
{
    /// <summary>The loose roots the scan could not walk or list, each named with the reason, so the "may be
    /// incomplete" note says WHICH folder; empty when every root read.</summary>
    public IReadOnlyList<string> RootFailures { get; init; } = Array.Empty<string>();

    public static AssetRenameOutcome None(bool readIncomplete = false, IReadOnlyList<string>? rootFailures = null) =>
        new(0, 0, 0, Array.Empty<string>(), readIncomplete) { RootFailures = rootFailures ?? Array.Empty<string>() };
}

/// <summary>The accounting of the voice pass: files found under the plugin's voice prefix, those whose embedded
/// FormID was renumbered and written, the distinct lines they belong to, failures, and the read caveat.</summary>
public sealed record VoiceCarryOutcome(
    int FilesScanned, int FilesCarried, int LinesCarried,
    IReadOnlyList<string> Failures, bool ReadIncomplete)
{
    /// <summary>The loose roots the scan could not walk or list, each named with the reason — the voice twin of
    /// <see cref="AssetRenameOutcome.RootFailures"/>.</summary>
    public IReadOnlyList<string> RootFailures { get; init; } = Array.Empty<string>();

    public static VoiceCarryOutcome None(bool readIncomplete = false, IReadOnlyList<string>? rootFailures = null) =>
        new(0, 0, 0, Array.Empty<string>(), readIncomplete) { RootFailures = rootFailures ?? Array.Empty<string>() };
}

/// <summary>The accounting of the SEQ pass. Not a map-rename — the <c>.seq</c> is REBUILT from the renumbered
/// plugin — and refresh-only: a source that shipped none gets a named advisory, never an invented file.</summary>
public sealed record SeqRegenOutcome(
    int SgeQuestCount, bool Written, string? SeqPath, IReadOnlyList<string> Failures)
{
    public static SeqRegenOutcome None() => new(0, false, null, Array.Empty<string>());
}

public static class AssetRenameService
{
    /// <summary>Carry the FaceGen assets of every RENUMBERED NPC in <paramref name="pPrimePath"/> to their new-FormID
    /// path under <paramref name="outDir"/> (the output mod-folder root in both lanes). Only NPCs whose new key is a
    /// <paramref name="map"/> VALUE are carried; best-effort, so this never throws and never fails the compact.</summary>
    public static AssetRenameOutcome CarryFaceGen(
        string pPrimePath, IReadOnlyDictionary<FormKey, FormKey> map, AssetResolver.AssetView assets, string outDir)
    {
        // Which renumbered records are NPCs? Read P′ back and intersect its NPCs with the map's NEW keys, reversing
        // the map once so each renumbered NPC yields its old key. The overlay is transient — zero handle at rest.
        List<(FormKey Old, FormKey New)> npcs;
        try
        {
            using var pp = SkyrimMod.CreateFromBinaryOverlay(pPrimePath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(pPrimePath));
            var reverse = new Dictionary<FormKey, FormKey>(map.Count);
            foreach (var kv in map) reverse[kv.Value] = kv.Key;            // new → old
            npcs = pp.Npcs.Where(n => reverse.ContainsKey(n.FormKey))
                          .Select(n => (Old: reverse[n.FormKey], New: n.FormKey))
                          .ToList();
        }
        catch (Exception ex)
        {
            // Can't read P′ back, so this is a degraded asset pass on a compact that SUCCEEDED: a named warning.
            return new AssetRenameOutcome(0, 0, 0,
                new[] { $"could not read '{Path.GetFileName(pPrimePath)}' back to find its NPCs for facegen carry ({ex.Message}) — verify NPC faces in-game." },
                assets.ReadIncomplete) { RootFailures = assets.RootFailures };
        }

        if (npcs.Count == 0) return AssetRenameOutcome.None(assets.ReadIncomplete, assets.RootFailures);

        // Each renumbered NPC's mesh + tint, old path to new, through the shared two-phase carry.
        var items = new List<CarryItem>();
        foreach (var (oldKey, newKey) in npcs)
            foreach (var (slot, oldPath) in FaceGenPath.Both(oldKey))      // (Mesh, …), (Tint, …) — the dark-face pair
                items.Add(new CarryItem(oldPath, FaceGenPath.For(newKey, slot), newKey, $"{oldKey.ID:X6}→{newKey.ID:X6} {slot}"));

        var failures = new List<string>();
        var (files, carried) = CarryItems(items, assets, outDir, failures);
        return new AssetRenameOutcome(npcs.Count, carried.Count, files, failures, assets.ReadIncomplete)
               { RootFailures = assets.RootFailures };
    }

    /// <summary>Carry the VOICE files of every RENUMBERED dialogue line to their new-FormID name under
    /// <paramref name="outDir"/>, DISCOVERING by scanning the plugin's voice prefix. Two lanes: on a compact only the
    /// id segment moves; on a merge the caller passes each donor as <paramref name="sourcePlugin"/> and the folder
    /// segment is rewritten alongside it. Rides the same two-phase carry; never throws.</summary>
    public static VoiceCarryOutcome CarryVoice(
        string pPrimePath, IReadOnlyDictionary<FormKey, FormKey> map, AssetResolver.AssetView assets, string outDir,
        string? sourcePlugin = null)
    {
        var targetBasename = Path.GetFileName(pPrimePath);                  // the OUTPUT plugin's filename → the NEW voice folder name
        var sourceBasename = sourcePlugin ?? targetBasename;                // compact: same name; merge: the donor's filename (OLD folder)

        // local id to new local id for the SOURCE plugin's renumbered records — the only ones whose voice lives here.
        var idMap = new Dictionary<uint, uint>();
        foreach (var kv in map)
            if (string.Equals(kv.Key.ModKey.FileName.ToString(), sourceBasename, StringComparison.OrdinalIgnoreCase))
                idMap[kv.Key.ID] = kv.Value.ID;
        if (idMap.Count == 0) return VoiceCarryOutcome.None(assets.ReadIncomplete, assets.RootFailures);

        // The new FormKeys need a ModKey for the distinct-line accounting; a malformed name is surfaced, not zeroed.
        ModKey modKey;
        try { modKey = ModKey.FromFileName(targetBasename); }
        catch (Exception ex)
        {
            return new VoiceCarryOutcome(0, 0, 0,
                new[] { $"'{targetBasename}' is not a valid plugin filename for voice carry ({ex.Message}) — verify voiced lines in-game." },
                assets.ReadIncomplete) { RootFailures = assets.RootFailures };
        }

        var srcPrefix = $@"Sound\Voice\{sourceBasename}";
        var tgtPrefix = $@"Sound\Voice\{targetBasename}";
        IReadOnlyCollection<string> files;
        try { files = assets.EnumerateUnder(srcPrefix); }
        catch (Exception ex)
        {
            return new VoiceCarryOutcome(0, 0, 0,
                new[] { $"could not scan '{srcPrefix}' for voice files ({ex.Message}) — verify voiced lines in-game." },
                assets.ReadIncomplete) { RootFailures = assets.RootFailures };
        }
        if (files.Count == 0) return VoiceCarryOutcome.None(assets.ReadIncomplete, assets.RootFailures);

        // Each voice file whose embedded id was renumbered, to its new-id name. A file with no '_<8hex>_<num>.<ext>'
        // tail, or whose id was not renumbered, is left alone.
        var items = new List<CarryItem>();
        foreach (var oldRel in files)
        {
            var fname = Path.GetFileName(oldRel);
            var m = VoiceIdRx.Match(fname);
            if (!m.Success) continue;                                      // not an INFO-keyed voice file — nothing to remap
            uint full;
            try { full = Convert.ToUInt32(m.Groups[1].Value, 16); } catch { continue; }
            uint oldLocal = full & FormIdRange.ObjectIdMask;              // mask the index byte, exactly like VoicePath emits "00"+6hex
            if (!idMap.TryGetValue(oldLocal, out var newLocal)) continue;  // id not renumbered → filename unchanged → no carry

            var newId = "00" + newLocal.ToString("X6");
            var newFname = fname.Substring(0, m.Groups[1].Index) + newId + fname.Substring(m.Groups[1].Index + m.Groups[1].Length);
            var dir = Path.GetDirectoryName(oldRel) ?? "";                 // same voice-type folder; the PLUGIN segment swaps on a merge
            var newDir = dir.Length >= srcPrefix.Length ? tgtPrefix + dir.Substring(srcPrefix.Length) : dir;
            var newRel = newDir.Length == 0 ? newFname : Path.Combine(newDir, newFname);
            items.Add(new CarryItem(oldRel, newRel, new FormKey(modKey, newLocal), $"{oldLocal:X6}→{newLocal:X6} {fname}"));
        }

        var failures = new List<string>();
        var (carriedFiles, carriedLines) = CarryItems(items, assets, outDir, failures);
        return new VoiceCarryOutcome(files.Count, carriedFiles, carriedLines.Count, failures, assets.ReadIncomplete)
               { RootFailures = assets.RootFailures };
    }

    /// <summary>REFRESH the start-game-enabled-quest <c>.seq</c> of the renumbered plugin when the source SHIPPED
    /// one, writing it to <c>&lt;outDir&gt;\SEQ\&lt;basename&gt;.seq</c> where the game reads it. Rebuilt from P′ via
    /// <see cref="SeqFile.Build"/>, not renamed; no SGE quests is a clean no-op, and a source that shipped none gets
    /// a named advisory rather than an invented file. Never throws.</summary>
    public static SeqRegenOutcome RegenerateSeq(string pPrimePath, string outDir, bool sourceHadSeq)
    {
        SeqFile.SeqBuild built;
        try { built = SeqFile.Build(pPrimePath); }
        catch (Exception ex)
        {
            // Can't read P′ back, so this is a degraded SEQ pass on a compact that SUCCEEDED: a named warning.
            return new SeqRegenOutcome(0, false, null,
                new[] { $"could not read '{Path.GetFileName(pPrimePath)}' back to (re)build its .seq ({ex.Message}) — if it has start-game-enabled quests, run {ToolNames.WriteSeq} on the compacted plugin." });
        }

        // No SGE quests means no .seq. Returns BEFORE the sourceHadSeq gate, so a source that shipped one for a quest
        // whose SGE flag is since gone would leave it stale — unreachable via compact, latent if reused for merge.
        if (built.Quests.Count == 0) return SeqRegenOutcome.None();

        if (!sourceHadSeq)
            // Refresh-only: do NOT invent a .seq the source lacked; the quests likely weren't starting before either.
            return new SeqRegenOutcome(built.Quests.Count, false, null,
                new[] { $"'{Path.GetFileName(pPrimePath)}' has {built.Quests.Count} start-game-enabled quest(s) but no .seq — they likely weren't starting even before compaction; run {ToolNames.WriteSeq} on the compacted plugin to add one." });

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
                new[] { $"could not write '{Path.GetFileName(dest)}' ({ex.Message}) — its start-game-enabled quests may not start; run {ToolNames.WriteSeq} on the compacted plugin." });
        }
        return new SeqRegenOutcome(built.Quests.Count, true, dest, Array.Empty<string>());
    }

    /// <summary>One asset to carry: read <see cref="OldPath"/>'s winning copy and place its bytes at <see cref="NewPath"/> under the output dir.</summary>
    readonly record struct CarryItem(string OldPath, string NewPath, FormKey Owner, string Label);

    /// <summary>The shared TWO-PHASE carry both facegen and voice ride: every read stages to a '.houseCARL-tmp'
    /// sibling, and only once EVERY read is done are the temps committed — the in-place aliasing contract in
    /// docs/architecture/assets.md. A resolve miss is skipped; a FOUND-but-unwritable file is a named failure.</summary>
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

    /// <summary>Matches a voice file's '_&lt;8hex&gt;_&lt;response&gt;.&lt;ext&gt;' tail, anchored to the end so an EditorID segment cannot confuse it. Group 1 is the FormID.</summary>
    static readonly System.Text.RegularExpressions.Regex VoiceIdRx =
        new(@"_([0-9A-Fa-f]{8})_\d+\.[A-Za-z0-9]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Read the bytes of a resolved WINNING provider — the one shared loose-vs-BSA read.</summary>
    static (byte[]? bytes, string? error) ReadWinner(PlacementSource s) => AssetResolver.ReadPlacementSource(s);
}

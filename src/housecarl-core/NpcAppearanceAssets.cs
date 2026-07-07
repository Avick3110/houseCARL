using System.Collections;
using System.Reflection;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

// ======================================================================
//  NpcAppearanceAssets — the FILE half of the composed standalone-NPC-copy verb
//  (capability chain Stage 3 §1 items 3–4). Three jobs:
//
//    1. FACEGEN RENAME — the donor's baked facegeom .nif + facetint .dds move to the
//       NEW NPC's FormKey path (FaceGenPath — folder = the defining master, so the
//       apply lane files under the TARGET's plugin, the clone lane under the patch).
//       Empirically pinned (the 2026-07-01 build test, NPC2 cross-validated): the
//       engine resolves the facetint from the FormKey path and IGNORES the path
//       embedded inside the .nif — so a rename needs NO .nif editing, ever.
//
//    2. REFERENCED-ASSET HARVEST — the internalized records' own asset paths (headpart
//       models, texture-set .dds, morph .tri) via a generic IAssetLinkGetter walk (no
//       per-type hand list), PLUS a conservative string-scan of the carried facegeom
//       bytes for the skin/hair textures the geom embeds (NifSkope slots — the engine
//       DOES use those; with the donor disabled they would silently unresolve). No NIF
//       parser: paths in a .nif are plain ASCII, and a false-positive costs one skipped
//       candidate, never a wrong write.
//
//    3. THE CARRY RULE — a harvested path is carried iff its bytes would VANISH with the
//       donor: the active VFS winner is the donor's own mod folder / BSA, or the path
//       resolves nowhere active but exists in the (disabled) donor's folder. A path
//       another active provider (vanilla BSA, a shared-resource mod) supplies is
//       SKIPPED + noted — it keeps resolving without the donor. Best-effort + reported
//       (Q3): the records are already written, so a carry miss is a NAMED warning,
//       never a silent gap and never a failed copy.
// ======================================================================

/// <summary>One carried file: old → new Data-relative path (identical except the facegen pair), bytes, and where
/// the winning copy came from.</summary>
public sealed record CarriedAsset(string OldRelPath, string NewRelPath, long Bytes, string From);

/// <summary>The asset-carry half's outcome: what moved, what was deliberately left (another active provider still
/// supplies it), what was referenced but found nowhere (verify in-game), and named failures. Never throws.</summary>
public sealed record NpcAssetOutcome(
    IReadOnlyList<CarriedAsset> Carried,
    IReadOnlyList<string> SkippedStillProvided,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Failures,
    bool FaceGenMeshCarried,
    bool FaceGenTintCarried);

public static class NpcAppearanceAssets
{
    /// <summary>Harvest every asset path the given records list — the generic <see cref="IAssetLinkGetter"/> walk over
    /// each record's property graph (lists + substructs included), by construction rather than a per-type field list.
    /// Paths come back Data-relative (Mutagen's DataRelativePath — e.g. a Model.File 'x.nif' → 'meshes\x.nif').</summary>
    public static IReadOnlyList<string> HarvestAssetPaths(IEnumerable<IMajorRecordGetter> records)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rec in records)
            HarvestFrom(rec, paths, seen, new HashSet<object>(ReferenceEqualityComparer.Instance), depth: 0);
        return paths;
    }

    static void HarvestFrom(object node, List<string> paths, HashSet<string> seen, HashSet<object> visited, int depth)
    {
        if (depth > 6 || node is string || !visited.Add(node)) return;

        if (node is IAssetLinkGetter asset)
        {
            string? rel = null;
            try { rel = asset.DataRelativePath.Path; } catch { /* an unset link — nothing to harvest */ }
            if (!string.IsNullOrWhiteSpace(rel) && seen.Add(rel)) paths.Add(rel);
            return;
        }

        if (node is IEnumerable en and not IFormLinkGetter)
        {
            foreach (var el in en)
                if (el is not null) HarvestFrom(el, paths, seen, visited, depth + 1);
            return;
        }

        // Only descend Mutagen model types (their namespace), never arbitrary BCL values — keeps the walk cheap + safe.
        var t = node.GetType();
        if (t.Namespace is null || !t.Namespace.StartsWith("Mutagen.Bethesda", StringComparison.Ordinal)) return;
        if (node is IFormLinkGetter) return;                              // a link is an identity, not an asset container

        foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length != 0 || prop.GetGetMethod() is null) continue;
            object? val;
            try { val = prop.GetValue(node); } catch { continue; }
            if (val is null) continue;
            HarvestFrom(val, paths, seen, visited, depth + 1);
        }
    }

    /// <summary>Conservative texture-path scan of facegeom bytes: every ASCII run that starts 'textures\' (either
    /// slash) and ends '.dds', case-insensitive — the skin/hair texture paths a facegen .nif embeds and the engine
    /// resolves at render time. No NIF parsing: a path in a .nif is stored as plain text, and this is the same
    /// byte-scrape the 2026-07-01 build test used. A false positive costs one 'referenced but found nowhere' note.</summary>
    public static IReadOnlyList<string> ScrapeNifTexturePaths(byte[] nif)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lower = new byte[] { (byte)'t', (byte)'e', (byte)'x', (byte)'t', (byte)'u', (byte)'r', (byte)'e', (byte)'s' };
        for (int i = 0; i + 12 < nif.Length; i++)                        // 12 = 'textures\' + 'a.dds' lower bound
        {
            int j = 0;
            while (j < lower.Length && i + j < nif.Length && (nif[i + j] | 0x20) == lower[j]) j++;
            if (j != lower.Length) continue;
            int k = i + j;
            if (k >= nif.Length || (nif[k] != (byte)'\\' && nif[k] != (byte)'/')) continue;

            // Extend through printable path chars until it stops; accept only if it ends '.dds'.
            int end = k;
            while (end < nif.Length && end - i < 260)
            {
                byte b = nif[end];
                if (b < 0x20 || b > 0x7E) break;
                end++;
            }
            var s = Encoding.ASCII.GetString(nif, i, end - i);
            var dds = s.LastIndexOf(".dds", StringComparison.OrdinalIgnoreCase);
            if (dds <= 0) { i = end; continue; }
            var path = s[..(dds + 4)].Replace('/', '\\');
            if (seen.Add(path)) found.Add(path);
            i = end;
        }
        return found;
    }

    /// <summary>Where the (possibly disabled) donor's own files live on disk: the donor plugin's MO2 mod folder plus
    /// any BSAs sitting at its root — the direct-disk lane for a donor the active VFS cannot see. Null folder = the
    /// donor was read via a direct path outside ModsDir; only its own directory is scanned.</summary>
    public sealed record DonorDisk(string Folder, IReadOnlyList<string> Bsas)
    {
        public static DonorDisk For(string donorPluginPath)
        {
            var dir = Path.GetDirectoryName(donorPluginPath)!;
            List<string> bsas;
            try { bsas = Directory.EnumerateFiles(dir, "*.bsa", SearchOption.TopDirectoryOnly).ToList(); }
            catch { bsas = new List<string>(); }
            return new DonorDisk(dir, bsas);
        }

        /// <summary>Read a Data-relative path from the donor's own disk (loose first, then its root BSAs). Null = absent.</summary>
        public (byte[]? Bytes, string? From) Read(string relPath)
        {
            var loose = Path.Combine(Folder, relPath);
            if (File.Exists(loose))
            {
                try { return (File.ReadAllBytes(loose), $"donor folder (loose)"); }
                catch { /* fall through to BSAs */ }
            }
            foreach (var bsa in Bsas)
            {
                byte[]? b;
                try { b = AssetResolver.TryReadArchiveEntry(bsa, relPath); }
                catch { continue; }
                if (b is not null) return (b, $"donor archive '{Path.GetFileName(bsa)}'");
            }
            return (null, null);
        }
    }

    /// <summary>Resolve ONE Data-relative path under the carry rule (file header §3). Returns the bytes + provenance
    /// when the path must be carried, (null, note) when it is deliberately skipped or missing.</summary>
    static (byte[]? Bytes, string? From, string? SkipNote, bool Missing) ResolveForCarry(
        string relPath, AssetResolver.AssetView view, DonorDisk? donor, string? donorModFolderName)
    {
        var res = view.ResolveForPlacement(relPath);
        if (res.Sources.Count > 0)
        {
            var winner = res.Sources[0];
            bool donorProvides =
                (donorModFolderName is not null && string.Equals(winner.ProviderName, donorModFolderName, StringComparison.OrdinalIgnoreCase))
                || (donor is not null && winner.Kind == AssetKind.Bsa && winner.ArchivePath is not null
                    && string.Equals(Path.GetDirectoryName(winner.ArchivePath), donor.Folder, StringComparison.OrdinalIgnoreCase));
            if (!donorProvides)
                return (null, null, $"'{relPath}' — still provided by '{winner.ProviderName}' after the donor is removed; not carried.", false);

            if (winner.Kind == AssetKind.Loose && winner.LooseFilePath is not null && File.Exists(winner.LooseFilePath))
            {
                try { return (File.ReadAllBytes(winner.LooseFilePath), $"'{winner.ProviderName}' (loose)", null, false); }
                catch { /* fall through to the donor-disk lane */ }
            }
            if (winner.Kind == AssetKind.Bsa && winner.ArchivePath is not null)
            {
                try
                {
                    var b = AssetResolver.TryReadArchiveEntry(winner.ArchivePath, winner.EntryPath);
                    if (b is not null) return (b, $"'{Path.GetFileName(winner.ArchivePath)}'", null, false);
                }
                catch { /* fall through */ }
            }
        }

        if (donor is not null)
        {
            var (bytes, from) = donor.Read(relPath);
            if (bytes is not null) return (bytes, from, null, false);
        }
        return (null, null, null, true);
    }

    /// <summary>
    /// The whole asset carry for one copied NPC: rename the facegen pair donor-key → new-key (always carried when
    /// found — the destination path is NEW, so this is a move, not a keep), scrape the carried geom for its embedded
    /// textures, harvest the internalized records' asset links, and carry each harvested path under the carry rule.
    /// Writes go under <paramref name="outDir"/> (the patch mod folder) via staged temp + <see cref="AtomicFile"/>.
    /// Never throws; every miss/failure is named in the outcome (Q3).
    /// </summary>
    public static NpcAssetOutcome CarryAll(
        FormKey donorNpc, FormKey newNpc,
        IReadOnlyList<IMajorRecordGetter> internalized,
        AssetResolver.AssetView view, DonorDisk? donor, string? donorModFolderName, string outDir)
    {
        var carried = new List<CarriedAsset>();
        var skipped = new List<string>();
        var missing = new List<string>();
        var failures = new List<string>();
        bool meshCarried = false, tintCarried = false;
        byte[]? geomBytes = null;

        // ---- 1. the facegen pair: donor path → NEW path (a rename — carried from wherever the winning copy lives) ----
        foreach (var (slot, oldRel) in FaceGenPath.Both(donorNpc))
        {
            var newRel = FaceGenPath.For(newNpc, slot);
            byte[]? bytes = null; string? from = null;
            var res = view.ResolveForPlacement(oldRel);
            if (res.Sources.Count > 0)
            {
                var w = res.Sources[0];
                try
                {
                    bytes = w.Kind == AssetKind.Loose
                        ? (w.LooseFilePath is not null && File.Exists(w.LooseFilePath) ? File.ReadAllBytes(w.LooseFilePath) : null)
                        : (w.ArchivePath is not null ? AssetResolver.TryReadArchiveEntry(w.ArchivePath, w.EntryPath) : null);
                    from = w.Kind == AssetKind.Loose ? $"'{w.ProviderName}' (loose)" : $"'{Path.GetFileName(w.ArchivePath!)}'";
                }
                catch (Exception ex) { failures.Add($"facegen {slot}: could not read the resolved winner — {ex.Message}"); }
            }
            if (bytes is null && donor is not null)
            {
                var (b, f) = donor.Read(oldRel);
                bytes = b; from = f;
            }
            if (bytes is null)
            {
                missing.Add($"facegen {slot} '{oldRel}' — found neither in the active VFS nor the donor's folder. " +
                            "Without it the engine regenerates the head at runtime (grey/dark-face risk); verify in-game.");
                continue;
            }
            if (WriteCarried(outDir, newRel, bytes, failures))
            {
                carried.Add(new CarriedAsset(oldRel, newRel, bytes.Length, from ?? "?"));
                if (slot == FaceGenSlot.Mesh) { meshCarried = true; geomBytes = bytes; }
                else tintCarried = true;
            }
        }

        // ---- 2. harvest: record asset links + the geom's embedded textures ----
        var wanted = new List<string>(HarvestAssetPaths(internalized));
        if (geomBytes is not null)
            foreach (var p in ScrapeNifTexturePaths(geomBytes))
                if (!wanted.Contains(p, StringComparer.OrdinalIgnoreCase)) wanted.Add(p);

        // ---- 3. carry each harvested path under the rule (same relpath — a keep-resolving move, not a rename) ----
        foreach (var rel in wanted)
        {
            var (bytes, from, skipNote, isMissing) = ResolveForCarry(rel, view, donor, donorModFolderName);
            if (skipNote is not null) { skipped.Add(skipNote); continue; }
            if (isMissing) { missing.Add($"'{rel}' — referenced by the copied records/geom but found nowhere (active VFS or donor folder); verify in-game."); continue; }
            if (bytes is null) continue;
            if (WriteCarried(outDir, rel, bytes, failures))
                carried.Add(new CarriedAsset(rel, rel, bytes.Length, from ?? "?"));
        }

        return new NpcAssetOutcome(carried, skipped, missing, failures, meshCarried, tintCarried);
    }

    /// <summary>Stage + atomically commit one carried file under <paramref name="outDir"/>. A failure is a named
    /// entry in <paramref name="failures"/>, never a throw (the records are already written).</summary>
    static bool WriteCarried(string outDir, string relPath, byte[] bytes, List<string> failures)
    {
        try
        {
            var final = Path.Combine(outDir, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            AtomicFile.WriteAllBytes(final, bytes);
            return true;
        }
        catch (Exception ex)
        {
            failures.Add($"could not write '{relPath}' — {ex.Message}");
            return false;
        }
    }
}

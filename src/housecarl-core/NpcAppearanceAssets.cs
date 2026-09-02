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
//    2. REFERENCED-ASSET CARRY — the harvested asset paths (the caller collects them
//       from the IN-PATCH duplicates pre-serialize via HarvestAssetPaths — a generic
//       IAssetLinkGetter walk, no per-type hand list), PLUS a conservative string-scan
//       of the carried facegeom bytes for the skin/hair textures the geom embeds
//       (NifSkope slots — the engine DOES use those; with the donor disabled they
//       would silently unresolve). No NIF parser: paths in a .nif are plain ASCII,
//       and a false-positive costs one skipped candidate, never a wrong write.
//
//    3. THE CARRY RULE — a harvested path is carried iff its bytes would VANISH with the
//       donor: the active VFS winner is the donor's own mod folder / BSA, or the path
//       resolves nowhere active but exists in the (disabled) donor's folder. A path
//       another active provider (vanilla BSA, a shared-resource mod) supplies is
//       SKIPPED + noted — it keeps resolving without the donor. The facegen pair is
//       the exception (alwaysCarry): its DESTINATION path is new, so it is a rename
//       from wherever the winning copy lives. Best-effort + reported (Q3): the records
//       are already written, so a carry miss is a NAMED warning, never a silent gap.
// ======================================================================

/// <summary>One carried file: old → new Data-relative path (identical except the facegen pair), bytes, and where
/// the winning copy came from.</summary>
public sealed record CarriedAsset(string OldRelPath, string NewRelPath, long Bytes, string From);

/// <summary>The asset-carry half's outcome: what moved, what was deliberately left (another active provider still
/// supplies it), what was referenced but found nowhere (verify in-game), VFS-precedence warnings (a carried file
/// whose destination path an ACTIVE provider already supplies does not win until sorted above it — the
/// facegen-diagnostics keystone: file precedence is its own system), and named failures. Never throws.</summary>
public sealed record NpcAssetOutcome(
    IReadOnlyList<CarriedAsset> Carried,
    IReadOnlyList<string> SkippedStillProvided,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Warnings,
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
    /// resolves at render time. No NIF parsing: a path in a .nif is stored as plain text (the same byte-scrape the
    /// 2026-07-01 build test used); a false positive costs one 'referenced but found nowhere' note. Within a
    /// printable run the match ends at the FIRST '.dds' (review finding: last-match glued two adjacent paths into
    /// garbage), and the scan resumes right after the matched path so a second 'textures\…' in the same run is
    /// still found; the resume never skips a byte (review finding: an 'i = end' reset over the for-increment ate
    /// the first byte of a path that started exactly at a run boundary).</summary>
    public static IReadOnlyList<string> ScrapeNifTexturePaths(byte[] nif)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prefix = "textures"u8.ToArray();
        for (int i = 0; i + prefix.Length + 5 <= nif.Length; i++)         // +5 = '\' + shortest 'a.dds' won't fit past this
        {
            int j = 0;
            while (j < prefix.Length && (nif[i + j] | 0x20) == prefix[j]) j++;
            if (j != prefix.Length) continue;
            int k = i + j;
            if (k >= nif.Length || (nif[k] != (byte)'\\' && nif[k] != (byte)'/')) continue;

            // Extend through printable path chars, stopping at the FIRST '.dds' (case-insensitive).
            int end = k + 1, ddsEnd = -1;
            while (end < nif.Length && end - i < 260)
            {
                byte b = nif[end];
                if (b < 0x20 || b > 0x7E) break;
                end++;
                if (end - i >= 4
                    && nif[end - 4] == (byte)'.'
                    && (nif[end - 3] | 0x20) == (byte)'d'
                    && (nif[end - 2] | 0x20) == (byte)'d'
                    && (nif[end - 1] | 0x20) == (byte)'s')
                { ddsEnd = end; break; }
            }
            if (ddsEnd < 0) { i = end - 1; continue; }                    // no '.dds' in this run — resume AT the stop byte

            var path = Encoding.ASCII.GetString(nif, i, ddsEnd - i).Replace('/', '\\');
            if (seen.Add(path)) found.Add(path);
            i = ddsEnd - 1;                                               // resume right after the match (for-increment lands on the next byte)
        }
        return found;
    }

    /// <summary>Where the donor's own files live on disk: the donor plugin's containing folder plus any BSAs at its
    /// root — the direct-disk lane for a DISABLED donor the active VFS cannot see. The caller only constructs one
    /// for a donor under an MO2 mod folder (never for the game Data folder — there every vanilla BSA would
    /// misclassify as "the donor's", review finding).</summary>
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

    /// <summary>Resolve ONE Data-relative path under the carry rule (file header §3). <paramref name="alwaysCarry"/>
    /// = the facegen-rename case: the destination path is NEW, so the bytes move from wherever the winning copy
    /// lives (the donor-provides test doesn't apply). Returns the bytes + provenance when the path must be carried,
    /// (null, note) when deliberately skipped, or a miss — with <c>ReadError</c> carrying the REAL cause when a
    /// resolved winner could not be read (review finding: discarding it reported an in-use file as "found neither
    /// in the active VFS nor the donor's folder" — factually false, Q3).</summary>
    static (byte[]? Bytes, string? From, string? SkipNote, bool Missing, string? ReadError) ResolveForCarry(
        string relPath, AssetResolver.AssetView view, IReadOnlyList<DonorDisk> donors, IReadOnlyList<string> donorModFolderNames, bool alwaysCarry)
    {
        string? readError = null;
        var res = view.ResolveForPlacement(relPath);
        // The pick goes through the shared S2 source policy (AssetSourceSelection) — the same code path place_asset
        // uses — under the WINNER pole. That pole is this lane's ORIGINAL behavior, kept verbatim, and it is a known
        // DIVERGENCE from the successor flow: on the facegen rename (alwaysCarry) the caller has named a donor mod,
        // so the successor reads that NAMED provider while this reads whatever currently wins. Where a replacer
        // out-sorts the donor the two carry different bytes, and the winner's are the ones that can disagree with the
        // donor's head-part and tint records. Deliberately NOT changed here: this tool is being subsumed, and the
        // difference is the thing its replacement is measured against.
        var pick = AssetSourceSelection.Select(res, AssetSourceChoice.Winner);
        if (pick.Source is { } winner)
        {
            if (!alwaysCarry)
            {
                bool donorProvides =
                    donorModFolderNames.Any(n => string.Equals(winner.ProviderName, n, StringComparison.OrdinalIgnoreCase))
                    || (winner.Kind == AssetKind.Bsa && winner.ArchivePath is not null
                        && donors.Any(d => string.Equals(Path.GetDirectoryName(winner.ArchivePath), d.Folder, StringComparison.OrdinalIgnoreCase)));
                if (!donorProvides)
                    return (null, null, $"'{relPath}' — still provided by '{winner.ProviderName}' after the donor is removed; not carried.", false, null);
            }
            (var bytes, readError) = AssetResolver.ReadPlacementSource(winner);
            if (bytes is not null)
                return (bytes, winner.Kind == AssetKind.Loose ? $"'{winner.ProviderName}' (loose)" : $"'{Path.GetFileName(winner.ArchivePath!)}'", null, false, null);
            // the winner could not be read — fall through to the donor-disk lane, keeping the named cause
        }

        foreach (var donor in donors)
        {
            var (bytes, from) = donor.Read(relPath);
            if (bytes is not null) return (bytes, from, null, false, null);
        }
        return (null, null, null, true, readError);
    }

    /// <summary>
    /// The whole asset carry for one copied NPC: rename the facegen pair donor-key → new-key, scrape the carried
    /// geom for its embedded textures, and carry each harvested path under the carry rule.
    /// <paramref name="harvestedPaths"/> comes from the caller's pre-serialize harvest of the in-patch duplicates.
    /// Writes go under <paramref name="outDir"/> (the patch mod folder) via staged temp + <see cref="AtomicFile"/>.
    /// A carried file whose DESTINATION relpath an active provider already supplies gets a precedence WARNING
    /// (it does not win until the patch is sorted above that provider). Never throws; every miss/failure named (Q3).
    /// </summary>
    public static NpcAssetOutcome CarryAll(
        FormKey donorNpc, FormKey newNpc,
        IReadOnlyList<string> harvestedPaths,
        AssetResolver.AssetView view, IReadOnlyList<DonorDisk> donors, IReadOnlyList<string> donorModFolderNames, string outDir)
    {
        var carried = new List<CarriedAsset>();
        var skipped = new List<string>();
        var missing = new List<string>();
        var warnings = new List<string>();
        var failures = new List<string>();
        bool meshCarried = false, tintCarried = false;
        byte[]? geomBytes = null;

        void WarnIfDestinationContested(string newRel)
        {
            var already = view.ResolveForPlacement(newRel);
            if (already.Sources.Count > 0)
                warnings.Add($"'{newRel}' is ALREADY provided by '{already.Sources[0].ProviderName}' — the carried copy does " +
                             "not win until the patch mod is sorted ABOVE that provider in MO2 (file precedence is its own system).");
        }

        // ---- 1. the facegen pair: donor path → NEW path (a rename — alwaysCarry from the winning copy) ----
        foreach (var (slot, oldRel) in FaceGenPath.Both(donorNpc))
        {
            try
            {
                var newRel = FaceGenPath.For(newNpc, slot);
                var (bytes, from, _, isMissing, readErr) = ResolveForCarry(oldRel, view, donors, donorModFolderNames, alwaysCarry: true);
                if (isMissing || bytes is null)
                {
                    if (readErr is not null)
                        failures.Add($"facegen {slot} '{oldRel}': the winning copy exists but could not be read — {readErr}. Re-run once the file is free, or carry it with {ToolNames.PlaceAsset}.");
                    else
                        missing.Add($"facegen {slot} '{oldRel}' — found neither in the active VFS nor the donor's folder. " +
                                    "Without it the engine regenerates the head at runtime (grey/dark-face risk); verify in-game.");
                    continue;
                }
                WarnIfDestinationContested(newRel);
                if (WriteCarried(outDir, newRel, bytes, failures))
                {
                    carried.Add(new CarriedAsset(oldRel, newRel, bytes.Length, from ?? "?"));
                    if (slot == FaceGenSlot.Mesh) { meshCarried = true; geomBytes = bytes; }
                    else tintCarried = true;
                }
            }
            catch (Exception ex) { failures.Add($"facegen {slot} '{oldRel}': {ex.Message}"); }
        }

        // ---- 2. harvest: the caller's record-link harvest + the geom's embedded textures ----
        var wanted = new List<string>(harvestedPaths);
        var wantedSet = new HashSet<string>(harvestedPaths, StringComparer.OrdinalIgnoreCase);
        if (geomBytes is not null)
            foreach (var p in ScrapeNifTexturePaths(geomBytes))
                if (wantedSet.Add(p)) wanted.Add(p);

        // ---- 3. carry each harvested path under the rule (same relpath — a keep-resolving move, not a rename).
        //         Per-path fault isolation (review finding): ONE dirty path (a '..' segment surviving in a mod
        //         author's data, a scrape false-positive) is ONE named failure — never an abort that silently
        //         drops every path after it while reporting "asset carry skipped". ----
        foreach (var rel in wanted)
        {
            try
            {
                var (bytes, from, skipNote, isMissing, readErr) = ResolveForCarry(rel, view, donors, donorModFolderNames, alwaysCarry: false);
                if (skipNote is not null) { skipped.Add(skipNote); continue; }
                if (isMissing)
                {
                    if (readErr is not null)
                        failures.Add($"'{rel}': the winning copy exists but could not be read — {readErr}.");
                    else
                        missing.Add($"'{rel}' — referenced by the copied records/geom but found nowhere (active VFS or donor folder); verify in-game.");
                    continue;
                }
                if (bytes is null) continue;
                if (WriteCarried(outDir, rel, bytes, failures))
                    carried.Add(new CarriedAsset(rel, rel, bytes.Length, from ?? "?"));
            }
            catch (Exception ex) { failures.Add($"'{rel}': {ex.Message}"); }
        }

        return new NpcAssetOutcome(carried, skipped, missing, warnings, failures, meshCarried, tintCarried);
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

namespace HousecarlCore;

// ======================================================================
//  OffOrderAssetSource — reading an asset out of a mod folder the ACTIVE profile does not
//  include (F1, ruling O1 2026-08-14).
//
//  AssetResolver is built over comp.EnabledMods, so every provider it can name is a mod MO2
//  currently ticks. That made a NAMED source pole silently narrower than the plugin surface's:
//  naming a plugin resolves it "wherever it lives" (SPEC §4.2's one-pole rule), while naming a
//  MOD reached only the ticked ones. This type is the other half — the same rule applied to the
//  asset surface, so a caller who names a source gets that source regardless of the MO2 tick.
//
//  NAMING IS THE CONSENT, and it is the ONLY door. Nothing here is reachable from an omitted
//  provider, the winner pole, or a contention listing: those stay strictly inside the built
//  universe, because a mod nobody named must never contend silently or be reported as *winner.
//
//  UNIVERSE FIRST. A name the built universe already knows is answered by the universe and never
//  reaches disk (the caller passes the test in; this type does not know what is enabled). So no
//  enabled name changes behaviour, and the disk look is paid only on a name the universe has no
//  answer for.
//
//  The lane's SHAPE is the ancestor's, deliberately: loose file at the rel path first, then the
//  folder's ROOT archives (NpcAppearanceAssets.DonorDisk). Root archives are the capability that
//  disqualified deriving mods\<name>\ paths in a skill — a path guess cannot enumerate them.
// ======================================================================

/// <summary>Resolve a Data-relative asset path inside ONE named MO2 mod folder, off the active profile.</summary>
public static class OffOrderAssetSource
{
    /// <summary>The named mod folder's copy of <paramref name="relPath"/>, or null when that name resolves to no
    /// reachable copy. <paramref name="isUniverseProviderName"/> is the universe-first gate — a name it accepts
    /// returns null here, so the caller's own (enabled) answer stands and this lane cannot shadow it.
    ///
    /// <para><paramref name="relPath"/> must already be through <see cref="AssetResolver.ValidateRelPath"/>; the
    /// PROVIDER name is validated here instead, because it arrives raw off the wire and is about to be joined to
    /// <paramref name="modsDir"/>: anything carrying a separator, a drive, or a '..' segment is refused rather than
    /// combined, or a provider name would be a second way to address a file outside the mods folder.</para>
    ///
    /// <para>Reads DISK at call time. Nothing about an off-order folder is in the resolver's snapshot, so this lane
    /// is not snapshot-pinned and cannot be — the same contract the ancestor's donor-disk carry has always had.
    /// Never throws: an unreadable folder or archive is simply not a hit, never a false one (Q3).</para></summary>
    public static PlacementSource? Resolve(string modsDir, Func<string, bool> isUniverseProviderName,
                                           string? providerName, string relPath)
    {
        var name = providerName?.Trim() ?? "";
        if (name.Length == 0 || modsDir.Length == 0 || relPath.Length == 0) return null;

        // UNIVERSE FIRST — the one line that keeps every enabled name on its existing path.
        if (isUniverseProviderName(name)) return null;

        if (!IsPlainFolderName(name)) return null;

        string dir;
        try { dir = Path.Combine(modsDir, name); }
        catch { return null; }
        if (!SafeDirectoryExists(dir)) return null;

        // ---- loose, exactly as the VFS would layer it if this mod were ticked ----
        string loose;
        try { loose = Path.Combine(dir, relPath); }
        catch { return null; }
        if (SafeFileExists(loose))
            return new PlacementSource(name, AssetKind.Loose, LooseFilePath: loose, ArchivePath: null,
                                       EntryPath: relPath, OffOrder: true);

        // ---- the folder's ROOT archives, in a deterministic order ----
        foreach (var bsa in RootArchives(dir))
        {
            bool has;
            try { has = AssetResolver.ArchiveHasEntry(bsa, relPath); }
            catch { continue; }                                   // an archive that will not read is not a hit
            if (has)
                // ProviderName stays the name the CALLER typed — the mod folder is the provider they chose, and the
                // archive inside it is named separately by the read description. Spelling it as the .bsa filename
                // here would answer a different question than the one asked.
                return new PlacementSource(name, AssetKind.Bsa, LooseFilePath: null, ArchivePath: bsa,
                                           EntryPath: relPath, OffOrder: true);
        }
        return null;
    }

    /// <summary>A provider name is a FOLDER name and nothing else — no separator, no drive, no '..'. Mirrors
    /// <see cref="AssetResolver.ValidateRelPath"/>'s intent on the other half of the join, and returns false rather
    /// than throwing because a name that cannot be a folder is simply not a hit on this lane (the caller's existing
    /// named-provider refusal is the honest answer, and it already names what it searched).</summary>
    static bool IsPlainFolderName(string name)
    {
        if (name is "." or "..") return false;
        if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0) return false;
        if (name.IndexOf(':') >= 0) return false;
        try { return !Path.IsPathRooted(name) && Path.GetFileName(name) == name; }
        catch { return false; }
    }

    /// <summary>The archives at the folder's ROOT, sorted so the same folder answers the same way every run (the
    /// resolver's own deterministic tie-break among equal-rank archives). Top level only: a .bsa nested in a subtree
    /// is not one the engine would load for this mod.</summary>
    static IEnumerable<string> RootArchives(string dir)
    {
        List<string> bsas;
        try { bsas = Directory.EnumerateFiles(dir, "*.bsa", SearchOption.TopDirectoryOnly).ToList(); }
        catch { return Array.Empty<string>(); }
        bsas.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));
        return bsas;
    }

    static bool SafeDirectoryExists(string dir) { try { return Directory.Exists(dir); } catch { return false; } }
    static bool SafeFileExists(string path) { try { return File.Exists(path); } catch { return false; } }
}

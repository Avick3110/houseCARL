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

/// <summary>What one off-order lookup did, not just what it found. <see cref="FolderSearched"/> is false when the
/// universe-first gate answered first or the name could not be a folder — and a refusal that says "houseCARL also
/// looked in a mod folder of that name" is only true when it is TRUE, which a null <see cref="Source"/> alone cannot
/// tell a caller. <see cref="ReadFailure"/> names a folder or archive that could not be READ, so an absent answer is
/// never reported as an authoritative "this mod does not have it" (Q3 — the same caveat the active lane carries as
/// <c>ReadIncomplete</c>).</summary>
public readonly record struct OffOrderLookup(PlacementSource? Source, bool FolderSearched, string? ReadFailure)
{
    /// <summary>The gate answered, or the name is not a folder name: nothing was looked at on disk.</summary>
    public static readonly OffOrderLookup NotSearched = new(null, false, null);
}

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
    public static OffOrderLookup Resolve(string modsDir, Func<string, bool> isUniverseProviderName,
                                         string? providerName, string relPath)
    {
        var name = providerName?.Trim() ?? "";
        if (name.Length == 0 || modsDir.Length == 0 || relPath.Length == 0) return OffOrderLookup.NotSearched;

        // UNIVERSE FIRST — the one line that keeps every enabled name on its existing path.
        if (isUniverseProviderName(name)) return OffOrderLookup.NotSearched;

        if (!IsPlainFolderName(name)) return OffOrderLookup.NotSearched;

        string dir;
        try { dir = Path.Combine(modsDir, name); }
        catch { return OffOrderLookup.NotSearched; }

        // From here the caller may honestly say a mod folder of this name was looked for — including when there is
        // no such folder, which is a search with an answer, not an absent search.
        string? readFailure = null;
        if (!SafeDirectoryExists(dir, out var dirFailure))
            return new OffOrderLookup(null, FolderSearched: true, ReadFailure: dirFailure);

        // ---- loose, exactly as the VFS would layer it if this mod were ticked ----
        string loose;
        try { loose = Path.Combine(dir, relPath); }
        catch { return new OffOrderLookup(null, true, null); }
        if (SafeFileExists(loose))
            return new OffOrderLookup(new PlacementSource(name, AssetKind.Loose, LooseFilePath: loose, ArchivePath: null,
                                                          EntryPath: relPath, OffOrder: true), true, null);

        // ---- the folder's ROOT archives, in a deterministic order ----
        var (archives, listFailure) = RootArchives(dir);
        readFailure ??= listFailure;
        foreach (var bsa in archives)
        {
            bool has;
            // An archive that will not READ is not a miss — it is an unknown, and reporting it as "this mod does not
            // have it" is the silent-wrong-answer class. Named and carried out, the way the active lane carries its
            // own unreadable-archive caveat.
            try { has = AssetResolver.ArchiveHasEntry(bsa, relPath); }
            catch (Exception ex) { readFailure ??= $"'{Path.GetFileName(bsa)}' in that folder could not be read ({Concise(ex)})"; continue; }
            if (has)
                // ProviderName stays the name the CALLER typed — the mod folder is the provider they chose, and the
                // archive inside it is named separately by the read description. Spelling it as the .bsa filename
                // here would answer a different question than the one asked.
                return new OffOrderLookup(new PlacementSource(name, AssetKind.Bsa, LooseFilePath: null, ArchivePath: bsa,
                                                              EntryPath: relPath, OffOrder: true), true, null);
        }
        return new OffOrderLookup(null, true, readFailure);
    }

    /// <summary>A provider name is a FOLDER name and nothing else — no separator, no drive, no '..'. Mirrors
    /// <see cref="AssetResolver.ValidateRelPath"/>'s intent on the other half of the join, and returns false rather
    /// than throwing because a name that cannot be a folder is simply not a hit on this lane (the caller's existing
    /// named-provider refusal is the honest answer, and it already names what it searched).
    /// <para>A TRAILING DOT OR SPACE is refused, and that is not cosmetic. Windows strips both when it resolves a
    /// path, so <c>mods\Data.</c> opens <c>mods\Data</c> — which walks straight around the universe-first gate, since
    /// the gate matches the name the caller typed and "Data." is not "Data". Left in, a caller could reach a folder
    /// shadowed by a universe name, and worse, an ENABLED mod named with a trailing dot would be served off disk and
    /// reported as "NOT enabled in MO2" — the one sentence this lane exists to keep honest.</para></summary>
    static bool IsPlainFolderName(string name)
    {
        if (name is "." or "..") return false;
        if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0) return false;
        if (name.IndexOf(':') >= 0) return false;
        if (name.EndsWith('.') || name.EndsWith(' ')) return false;
        try { return !Path.IsPathRooted(name) && Path.GetFileName(name) == name; }
        catch { return false; }
    }

    static string Concise(Exception ex)
    {
        var s = ex.Message.Replace("\r", "").Replace("\n", " ").Trim();
        return s.Length > 120 ? s.Substring(0, 120) + "…" : s;
    }

    /// <summary>The archives at the folder's ROOT, sorted so the same folder answers the same way every run (the
    /// resolver's own deterministic tie-break among equal-rank archives). Top level only: a .bsa nested in a subtree
    /// is not one the engine would load for this mod, so reading one would serve bytes the game never would.
    /// <para>A folder that cannot be LISTED returns its reason rather than an empty list, so "no archives here" and
    /// "could not see the archives here" stay different answers (Q3).</para></summary>
    static (IReadOnlyList<string> Archives, string? Failure) RootArchives(string dir)
    {
        List<string> bsas;
        try { bsas = Directory.EnumerateFiles(dir, "*.bsa", SearchOption.TopDirectoryOnly).ToList(); }
        catch (Exception ex) { return (Array.Empty<string>(), $"that mod folder's archives could not be listed ({Concise(ex)})"); }
        bsas.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));
        return (bsas, null);
    }

    /// <summary>Does the folder exist? A folder that cannot be STATTED is not the same answer as one that is not
    /// there, and the caller renders the difference.</summary>
    static bool SafeDirectoryExists(string dir, out string? failure)
    {
        failure = null;
        try { return Directory.Exists(dir); }
        catch (Exception ex) { failure = $"that mod folder could not be read ({Concise(ex)})"; return false; }
    }

    static bool SafeFileExists(string path) { try { return File.Exists(path); } catch { return false; } }
}

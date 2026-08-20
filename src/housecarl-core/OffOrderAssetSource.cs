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

/// <summary>WHY one off-order lookup ended where it did — the CLOSED set of outcomes this lane can reach, one per
/// exit in <see cref="OffOrderAssetSource.Resolve"/>.
///
/// <para><b>Why an enum and not flags.</b> This started as one boolean, then two, and two consecutive review rounds
/// each found a state the booleans could not express — a refusal claiming a search the gate had prevented, then a
/// refusal calling a drive-rooted path "a name the active load order already provides files under". Every consumer
/// sentence now keys to exactly ONE value and no site re-derives the state, so the next outcome added here is a
/// compiler error at the render rather than a false sentence in front of a caller.</para>
///
/// <para>A value that needed free-text to explain what KIND of outcome it is would mean the set is not closed and
/// belongs back at Aaron's gate. The unreadable arm carries a NAME and a cause, which are that outcome's data — the
/// same way the provider name is the sentence's data — not a qualifier on what the outcome means.</para></summary>
public enum OffOrderReason
{
    /// <summary>A copy was found; <see cref="OffOrderLookup.Source"/> is non-null. Never renders a refusal.</summary>
    Found,
    /// <summary>The lane was not consulted at all — no lookup supplied, or degenerate inputs. No claim about disk
    /// may be made, because nothing on disk was looked at.</summary>
    NotConsulted,
    /// <summary>Universe-first answered: the name is one the built universe already provides files under, so the
    /// disk was deliberately not consulted.</summary>
    UniverseName,
    /// <summary>The name cannot BE a mod folder name — a separator, a drive, a '..', a trailing dot or space.</summary>
    NotAFolderName,
    /// <summary>A mod folder of that name was looked for and there is none.</summary>
    NoSuchFolder,
    /// <summary>The folder is there and was searched — loose, then its root archives — and holds no copy.</summary>
    NoCopyInFolder,
    /// <summary>The folder was searched but something in it could not be READ, so "absent" is an unknown rather than
    /// an answer (Q3 — the caveat the active lane carries as <c>ReadIncomplete</c>).</summary>
    FolderUnreadable,
}

/// <summary>What one off-order lookup did, not just what it found. <see cref="Reason"/> is the typed outcome; the
/// two unreadable fields are that outcome's data — the NAME of what would not read and a concise cause — kept as
/// data rather than a sentence because prose belongs to the tool layer (#337).</summary>
public readonly record struct OffOrderLookup(PlacementSource? Source, OffOrderReason Reason,
                                             string? UnreadableName = null, string? UnreadableCause = null)
{
    /// <summary>The lane was not consulted: no lookup supplied, or degenerate inputs.</summary>
    public static readonly OffOrderLookup NotConsulted = new(null, OffOrderReason.NotConsulted);
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
    /// Never throws, and never converts a failure into an absence: an unreadable folder, file or archive comes back
    /// as <see cref="OffOrderReason.FolderUnreadable"/> with a name and a cause, so the caller can say "unknown"
    /// rather than "this mod does not have it" (Q3).</para></summary>
    public static OffOrderLookup Resolve(string modsDir, Func<string, bool> isUniverseProviderName,
                                         string? providerName, string relPath)
    {
        var name = providerName?.Trim() ?? "";
        if (name.Length == 0 || modsDir.Length == 0 || relPath.Length == 0) return OffOrderLookup.NotConsulted;

        // UNIVERSE FIRST — the one line that keeps every enabled name on its existing path.
        if (isUniverseProviderName(name)) return new OffOrderLookup(null, OffOrderReason.UniverseName);

        // A name that cannot BE a folder is its own outcome, NOT the gate's. Collapsing the two is what let a
        // drive-rooted path be refused as "a name the active load order already provides files under".
        if (!IsPlainFolderName(name)) return new OffOrderLookup(null, OffOrderReason.NotAFolderName);

        string dir;
        try { dir = Path.Combine(modsDir, name); }
        catch { return new OffOrderLookup(null, OffOrderReason.NotAFolderName); }

        // ---- loose FIRST, exactly as the VFS would layer it if this mod were ticked ----
        string loose;
        try { loose = Path.Combine(dir, relPath); }
        catch (Exception ex) { return new OffOrderLookup(null, OffOrderReason.FolderUnreadable, name, Because(ex)); }

        switch (ProbeFile(loose, out var looseCause))
        {
            case Probe.Present:
                return new OffOrderLookup(new PlacementSource(name, AssetKind.Loose, LooseFilePath: loose, ArchivePath: null,
                                                              EntryPath: relPath, OffOrder: true), OffOrderReason.Found);
            case Probe.Unreadable:
                // The copy may be right there and unreadable. Calling that "this mod does not have it" is the
                // strongest false claim in the set, and it is the one an appearance flow reads as "the donor has no
                // mesh" (Aaron's review, F3). An unknown, named, like the archive half beneath it.
                return new OffOrderLookup(null, OffOrderReason.FolderUnreadable, name, looseCause);
        }

        // ---- the folder's ROOT archives, in a deterministic order ----
        // This enumeration is ALSO the folder's existence probe, and that is measured rather than assumed: the
        // review proved one API-behaviour assumption wrong, and a replacement guess would be another. On a
        // deny-ACL'd folder Directory.Exists returns TRUE, Directory.GetLastWriteTimeUtc returns a real date, and
        // File.GetAttributes returns Directory - not one of them can see it. Directory.EnumerateFiles throws
        // UnauthorizedAccessException there and DirectoryNotFoundException when the folder is genuinely absent, so
        // it is the only probe here that tells the two apart.
        var (archives, folderMissing, listCause) = RootArchives(dir);
        if (listCause is not null) return new OffOrderLookup(null, OffOrderReason.FolderUnreadable, name, listCause);
        if (folderMissing) return new OffOrderLookup(null, OffOrderReason.NoSuchFolder);

        string? unreadableName = null, unreadableCause = null;
        foreach (var bsa in archives)
        {
            bool has;
            // An archive that will not READ is not a miss - it is an unknown, and reporting it as "this mod does not
            // have it" is the silent-wrong-answer class. Named and carried out as DATA; the sentence is the tool's.
            try { has = AssetResolver.ArchiveHasEntry(bsa, relPath); }
            catch (Exception ex)
            {
                unreadableName ??= Path.GetFileName(bsa);
                unreadableCause ??= Because(ex);
                continue;
            }
            if (has)
                // ProviderName stays the name the CALLER typed - the mod folder is the provider they chose, and the
                // archive inside it is named separately by the read description. Spelling it as the .bsa filename
                // here would answer a different question than the one asked.
                return new OffOrderLookup(new PlacementSource(name, AssetKind.Bsa, LooseFilePath: null, ArchivePath: bsa,
                                                              EntryPath: relPath, OffOrder: true), OffOrderReason.Found);
        }
        return unreadableCause is null
            ? new OffOrderLookup(null, OffOrderReason.NoCopyInFolder)
            : new OffOrderLookup(null, OffOrderReason.FolderUnreadable, unreadableName, unreadableCause);
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

    /// <summary>WHY a probe failed, as a short phrase that carries NO on-disk path. The exception MESSAGE cannot be
    /// used here: .NET puts the full path in it ("Access to the path 'C:\...\mods\DonorMod' is denied"), and this
    /// string is rendered into a REFUSAL — the one place names-not-paths forbids a machine path, because a path in a
    /// refusal teaches the caller to round-trip one that goes stale between resolve and read. The caller already
    /// knows which mod it named; what it does not know is WHY, and the exception TYPE is that.</summary>
    static string Because(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "access denied",
        IOException                 => "an I/O error",
        _                           => ex.GetType().Name,
    };

    /// <summary>The archives at the folder's ROOT, sorted so the same folder answers the same way every run (the
    /// resolver's own deterministic tie-break among equal-rank archives). Top level only: a .bsa nested in a subtree
    /// is not one the engine would load for this mod, so reading one would serve bytes the game never would.
    /// <para>THREE answers, because this call is also the folder's existence probe: the archives, or "there is no
    /// such folder" (DirectoryNotFoundException), or a CAUSE for anything else - a denied folder throws
    /// UnauthorizedAccessException here. "no archives here", "no folder here" and "could not see in here" stay
    /// different answers (Q3). The cause is data; the sentence is the tool's.</para></summary>
    static (IReadOnlyList<string> Archives, bool FolderMissing, string? Cause) RootArchives(string dir)
    {
        List<string> bsas;
        try { bsas = Directory.EnumerateFiles(dir, "*.bsa", SearchOption.TopDirectoryOnly).ToList(); }
        catch (DirectoryNotFoundException) { return (Array.Empty<string>(), true, null); }
        catch (Exception ex) { return (Array.Empty<string>(), false, Because(ex)); }
        bsas.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));
        return (bsas, false, null);
    }

    /// <summary>What a filesystem probe actually learned. Three answers, because two of them used to be one: a
    /// bool-returning probe collapses "not there" and "there and I cannot read it" into the same false, and any
    /// sentence built on it then states an absence as a fact it never established.</summary>
    enum Probe { Present, Absent, Unreadable }

    /// <summary>Probe ONE path with an API whose failures SURFACE. <c>File.Exists</c> cannot serve here: it signals
    /// every failure, permission errors included, by returning false. <c>File.GetAttributes</c> throws, and throws
    /// DIFFERENTLY - measured on a real deny-ACL fixture before this was wired:
    /// <list type="bullet">
    /// <item>a denied file gives <c>UnauthorizedAccessException</c></item>
    /// <item>an absent file in a readable folder gives <c>FileNotFoundException</c></item>
    /// <item>any path under an absent folder gives <c>DirectoryNotFoundException</c></item>
    /// </list>
    /// Anything else - an I/O fault, an offline volume - reads as UNREADABLE rather than absent, because the safe
    /// default for an unknown is to say it is unknown. A path that turns out to be a DIRECTORY reads as absent: it
    /// is not the file that was asked for, which is what the bool probe said too.</summary>
    static Probe ProbeFile(string path, out string? cause)
    {
        cause = null;
        try
        {
            var attrs = File.GetAttributes(path);
            return attrs.HasFlag(FileAttributes.Directory) ? Probe.Absent : Probe.Present;
        }
        catch (FileNotFoundException) { return Probe.Absent; }
        catch (DirectoryNotFoundException) { return Probe.Absent; }
        catch (Exception ex) { cause = Because(ex); return Probe.Unreadable; }
    }
}

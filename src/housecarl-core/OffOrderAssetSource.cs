namespace HousecarlCore;

// OffOrderAssetSource — reading an asset out of a mod folder the ACTIVE profile does not include. Naming is the
// consent and the only door, reserved names are answered by the universe first, and the lane reads the folder's
// loose file then its root archives; the whole rule is in docs/architecture/assets.md.

/// <summary>WHY one off-order lookup ended where it did — the CLOSED set of outcomes this lane reaches, one per exit
/// in <see cref="OffOrderAssetSource.Resolve"/>, so a new outcome is a compiler error at the render.</summary>
public enum OffOrderReason
{
    Found,
    /// <summary>The lane was not consulted, so no claim about disk may be made.</summary>
    NotConsulted,
    /// <summary>The name means a LAYER rather than a mod folder, so the disk was deliberately not consulted.</summary>
    ReservedName,
    /// <summary>The name cannot BE a mod folder name — a separator, a drive, a '..', a trailing dot or space.</summary>
    NotAFolderName,
    NoSuchFolder,
    /// <summary>The folder is there and was searched — loose, then its root archives — and holds no copy.</summary>
    NoCopyInFolder,
    /// <summary>The folder was searched but something in it could not be READ, so "absent" is an unknown.</summary>
    FolderUnreadable,
    /// <summary>The folder holds the file loose, but MO2 hides it (<see cref="OffOrderLookup.Hidden"/> says by which
    /// rule), and none of its root archives has it: hiding applies to a named folder whatever its tick.</summary>
    Hidden,
}

/// <summary>What one off-order lookup did, not just what it found; the unreadable and hidden fields are those outcomes' data.</summary>
public readonly record struct OffOrderLookup(PlacementSource? Source, OffOrderReason Reason,
                                             string? UnreadableName = null, string? UnreadableCause = null,
                                             HiddenCopies? Hidden = null)
{
    public static readonly OffOrderLookup NotConsulted = new(null, OffOrderReason.NotConsulted);
}

public static class OffOrderAssetSource
{
    /// <summary>The named mod folder's copy of <paramref name="relPath"/>, or null when that name reaches no copy.
    /// <paramref name="relPath"/> arrives already validated; the PROVIDER name is validated here, because it comes
    /// raw off the wire and is about to be joined to <paramref name="modsDir"/>. <paramref name="opened"/> is the
    /// path as Windows opens it, which MO2's rules are asked about. <paramref name="listings"/> is the call's, given
    /// for an ENABLED folder, whose loose copy and root archives are then read off them; an unticked folder (null)
    /// is read directly, never memoised. Never throws.</summary>
    public static OffOrderLookup Resolve(string modsDir, Func<string, bool> isReservedProviderName,
                                         string? providerName, string opened, string relPath, Mo2SkipRules skip,
                                         LooseListings? listings = null)
    {
        var name = providerName?.Trim() ?? "";
        if (name.Length == 0 || modsDir.Length == 0 || relPath.Length == 0) return OffOrderLookup.NotConsulted;

        // RESERVED FIRST — the one line that keeps a layer name meaning the layer, never a folder called that.
        if (isReservedProviderName(name)) return new OffOrderLookup(null, OffOrderReason.ReservedName);

        // A name that cannot BE a folder is its own outcome, not the reserved gate's.
        if (!IsPlainFolderName(name)) return new OffOrderLookup(null, OffOrderReason.NotAFolderName);

        string dir;
        try { dir = LooseListings.OnDisk(modsDir, name); }
        catch { return new OffOrderLookup(null, OffOrderReason.NotAFolderName); }

        // ---- loose FIRST, exactly as the VFS would layer it if this mod were ticked ----
        string loose;
        try { loose = LooseListings.OnDisk(dir, opened); }
        catch (Exception ex) { return new OffOrderLookup(null, OffOrderReason.FolderUnreadable, name, Because(ex)); }

        // The same rule the build applies, ticked or not: a hidden loose copy is never read, only named.
        HiddenCopies? hidden = null;
        var rule = skip.Hides(opened, linked: true);
        // dir is the enabled root's folder as resolve keys it (the same join of the mods folder and the name).
        Probe Look(string rel, out string? cause) => listings is null ? ProbeFile(LooseListings.OnDisk(dir, rel), out cause) : ProbeListed(listings, dir, rel, out cause);
        switch (Look(opened, out var looseCause))
        {
            case Probe.Present when rule is null:
                return new OffOrderLookup(new PlacementSource(name, AssetKind.Loose, LooseFilePath: loose, ArchivePath: null,
                                                              EntryPath: relPath, OffOrder: true), OffOrderReason.Found);
            case Probe.Present:
                hidden = new HiddenCopies(rule, opened, new[] { name });
                break;
            case Probe.Unreadable:
                // The copy may be right there and unreadable; "this mod does not have it" is the strongest false claim in the set.
                return new OffOrderLookup(null, OffOrderReason.FolderUnreadable, name, looseCause);
            case Probe.Absent when rule is null:
                // A folder holding only x's hidden twin has x, hidden.
                if (skip.Twin(opened, p => Look(p, out _) == Probe.Present) is { } twin)
                    hidden = new HiddenCopies(twin.Rule, opened + twin.Rule.Token, new[] { name }, Twin: true);
                break;
        }

        // ---- the folder's ROOT archives, in a deterministic order ----
        // This enumeration is ALSO the folder's existence check: on a deny-ACL'd folder Directory.Exists,
        // GetLastWriteTimeUtc and GetAttributes all succeed, and only EnumerateFiles tells denied from absent.
        var (archives, folderMissing, listCause) = RootArchives(dir, listings);
        if (listCause is not null) return new OffOrderLookup(null, OffOrderReason.FolderUnreadable, name, listCause);
        if (folderMissing) return new OffOrderLookup(null, OffOrderReason.NoSuchFolder);

        string? unreadableName = null, unreadableCause = null;
        foreach (var bsa in archives)
        {
            bool has;
            // An archive that will not READ is an unknown, not a miss; named and carried out as data.
            try { has = AssetResolver.ArchiveHasEntry(bsa, relPath); }
            catch (Exception ex)
            {
                unreadableName ??= Path.GetFileName(bsa);
                unreadableCause ??= Because(ex);
                continue;
            }
            if (has)
                // ProviderName stays the name the CALLER typed — the mod folder is the provider they chose.
                return new OffOrderLookup(new PlacementSource(name, AssetKind.Bsa, LooseFilePath: null, ArchivePath: bsa,
                                                              EntryPath: relPath, OffOrder: true), OffOrderReason.Found);
        }
        // A hidden copy is a fact about the folder; an archive that would not read rides along as a note.
        if (hidden is not null) return new OffOrderLookup(null, OffOrderReason.Hidden, unreadableName, unreadableCause, hidden);
        return unreadableCause is null
            ? new OffOrderLookup(null, OffOrderReason.NoCopyInFolder)
            : new OffOrderLookup(null, OffOrderReason.FolderUnreadable, unreadableName, unreadableCause);
    }

    /// <summary>A provider name is a FOLDER name and nothing else — no separator, no drive, no '..', and no trailing
    /// dot or space, which Windows strips and which would walk around the reserved-name gate.</summary>
    static bool IsPlainFolderName(string name)
    {
        if (name is "." or "..") return false;
        if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0) return false;
        if (name.IndexOf(':') >= 0) return false;
        if (name.EndsWith('.') || name.EndsWith(' ')) return false;
        try { return !Path.IsPathRooted(name) && Path.GetFileName(name) == name; }
        catch { return false; }
    }

    /// <summary>WHY a probe failed, as a short phrase carrying NO on-disk path: .NET puts the full path in the message, and this is rendered into a refusal.</summary>
    static string Because(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "access denied",
        IOException                 => "an I/O error",
        _                           => ex.GetType().Name,
    };

    /// <summary>The archives at the folder's ROOT, sorted, top level only. THREE answers, because this call is also
    /// the folder's existence check: the archives, no such folder, or a cause for anything else.</summary>
    static (IReadOnlyList<string> Archives, bool FolderMissing, string? Cause) RootArchives(string dir, LooseListings? listings)
    {
        List<string> bsas;
        Exception? failed = null;
        if (listings is not null)
        {
            var read = listings.Descend(dir, []);
            failed = read.Failure;
            bsas = read.Listing?.Files.Where(f => f.EndsWith(".bsa", StringComparison.OrdinalIgnoreCase))
                                      .Select(f => LooseListings.OnDisk(dir, f)).ToList() ?? [];
        }
        else
        {
            try { bsas = Directory.EnumerateFiles(dir, "*.bsa", SearchOption.TopDirectoryOnly).ToList(); }
            catch (Exception ex) { failed = ex; bsas = []; }
        }
        if (failed is DirectoryNotFoundException) return (Array.Empty<string>(), true, null);
        if (failed is not null) return (Array.Empty<string>(), false, Because(failed));
        bsas.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));
        return (bsas, false, null);
    }

    /// <summary>What a filesystem check actually learned — a bool collapses "not there" and "cannot read it".</summary>
    enum Probe { Present, Absent, Unreadable }

    /// <summary>Check ONE path in an enabled folder off the call's listings, descended from the folder as resolve
    /// descends: there only if its parent's listing holds it as a file, absent where a listing on the way lacks a
    /// folder. Where the way in will not list, the parent is listed itself, and a parent that will not list is checked
    /// directly, since its listing has nothing to say.</summary>
    static Probe ProbeListed(LooseListings listings, string dir, string rel, out string? cause)
    {
        cause = null;
        var at = rel.LastIndexOf('\\');
        var parent = at < 0 ? "" : rel[..at];
        var into = listings.Descend(dir, LooseListings.Segments(parent));
        if (into.Missing is not null) return Probe.Absent;
        var read = into.Listing is { } held ? new LooseListings.Read(held, null) : listings.Get(LooseListings.OnDisk(dir, parent));
        if (read.Listing is { } listing) return listing.IsFile(rel[(at + 1)..]) ? Probe.Present : Probe.Absent;
        return read.Failure is DirectoryNotFoundException ? Probe.Absent : ProbeFile(LooseListings.OnDisk(dir, rel), out cause);
    }

    /// <summary>Check ONE path with an API whose failures SURFACE: <c>File.Exists</c> returns false for a permission
    /// error too, while <c>File.GetAttributes</c> throws differently for denied, absent file and absent folder.</summary>
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

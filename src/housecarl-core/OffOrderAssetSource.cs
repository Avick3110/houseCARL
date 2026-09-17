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
}

/// <summary>What one off-order lookup did, not just what it found; the two unreadable fields are that outcome's data.</summary>
public readonly record struct OffOrderLookup(PlacementSource? Source, OffOrderReason Reason,
                                             string? UnreadableName = null, string? UnreadableCause = null)
{
    public static readonly OffOrderLookup NotConsulted = new(null, OffOrderReason.NotConsulted);
}

public static class OffOrderAssetSource
{
    /// <summary>The named mod folder's copy of <paramref name="relPath"/>, or null when that name reaches no copy.
    /// <paramref name="relPath"/> arrives already validated; the PROVIDER name is validated here, because it comes
    /// raw off the wire and is about to be joined to <paramref name="modsDir"/>. Never throws.</summary>
    public static OffOrderLookup Resolve(string modsDir, Func<string, bool> isReservedProviderName,
                                         string? providerName, string relPath)
    {
        var name = providerName?.Trim() ?? "";
        if (name.Length == 0 || modsDir.Length == 0 || relPath.Length == 0) return OffOrderLookup.NotConsulted;

        // RESERVED FIRST — the one line that keeps a layer name meaning the layer, never a folder called that.
        if (isReservedProviderName(name)) return new OffOrderLookup(null, OffOrderReason.ReservedName);

        // A name that cannot BE a folder is its own outcome, not the reserved gate's.
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
                // The copy may be right there and unreadable; "this mod does not have it" is the strongest false claim in the set.
                return new OffOrderLookup(null, OffOrderReason.FolderUnreadable, name, looseCause);
        }

        // ---- the folder's ROOT archives, in a deterministic order ----
        // This enumeration is ALSO the folder's existence check: on a deny-ACL'd folder Directory.Exists,
        // GetLastWriteTimeUtc and GetAttributes all succeed, and only EnumerateFiles tells denied from absent.
        var (archives, folderMissing, listCause) = RootArchives(dir);
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
    static (IReadOnlyList<string> Archives, bool FolderMissing, string? Cause) RootArchives(string dir)
    {
        List<string> bsas;
        try { bsas = Directory.EnumerateFiles(dir, "*.bsa", SearchOption.TopDirectoryOnly).ToList(); }
        catch (DirectoryNotFoundException) { return (Array.Empty<string>(), true, null); }
        catch (Exception ex) { return (Array.Empty<string>(), false, Because(ex)); }
        bsas.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));
        return (bsas, false, null);
    }

    /// <summary>What a filesystem check actually learned — a bool collapses "not there" and "cannot read it".</summary>
    enum Probe { Present, Absent, Unreadable }

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

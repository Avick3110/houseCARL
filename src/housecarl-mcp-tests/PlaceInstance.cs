using System.Security.AccessControl;
using System.Security.Principal;
using HousecarlGenerator;
using HousecarlMcp;

namespace HousecarlMcpTests;

/// <summary>A throwaway MO2 instance for the place tests (mods\, game\Data\, profiles\Default\, ModOrganizer.ini) in
/// its own temp folder, plus a FaceGen archive authored in memory. Each test class that places builds one; a test that
/// changes the instance under a live service builds its own.</summary>
public sealed class PlaceInstance : IDisposable
{
    /// <summary>A FaceGen head path, the dark-face shape, held by <see cref="FaceArchive"/>.</summary>
    public const string FacegenRel = @"meshes\actors\character\facegendata\facegeom\Dawnguard.esm\0001A51A.nif";
    /// <summary>The same NPC's tint, also held by <see cref="FaceArchive"/>.</summary>
    public const string FacegenTintRel = @"textures\actors\character\facegendata\facetint\Dawnguard.esm\0001A51A.dds";
    /// <summary>The NPC both paths belong to.</summary>
    public const string FacegenFormId = "01A51A:Dawnguard.esm";

    public static readonly byte[] ArchiveFaceBytes = BsaBuilder.Bytes("PLACE-FACE", 64);
    public static readonly byte[] ArchiveTintBytes = BsaBuilder.Bytes("PLACE-TINT", 40);

    /// <summary>An uncompressed BSA holding <see cref="FacegenRel"/> and <see cref="FacegenTintRel"/>.</summary>
    public static byte[] FaceArchive() => BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames,
        new[]
        {
            (Path.GetDirectoryName(FacegenRel)!, new[] { (Path.GetFileName(FacegenRel), ArchiveFaceBytes) }),
            (Path.GetDirectoryName(FacegenTintRel)!, new[] { (Path.GetFileName(FacegenTintRel), ArchiveTintBytes) }),
        });

    public string Root { get; }
    public string Inst { get; }
    public string Mods { get; }
    public string Data { get; }
    public string Prof { get; }

    readonly List<LoadOrderService> _services = new();

    public PlaceInstance()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-place-" + Guid.NewGuid().ToString("N"));
        Inst = Path.Combine(Root, "inst");
        Mods = Path.Combine(Inst, "mods");
        Data = Path.Combine(Inst, "game", "Data");
        Prof = Path.Combine(Inst, "profiles", "Default");
        foreach (var d in new[] { Mods, Data, Prof }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(Inst, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Inst, "game").Replace(@"\", @"\\") + ")\r\n");
    }

    /// <summary>loadorder.txt, plugins.txt and modlist.txt (listed first = higher priority), plus an empty or given
    /// sResourceArchiveList.</summary>
    public void Profile(string[] loadorder, string[] plugins, string[] modlist, string archiveList = "")
    {
        File.WriteAllText(Path.Combine(Prof, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", loadorder) + "\r\n");
        File.WriteAllText(Path.Combine(Prof, "plugins.txt"), string.Join("\r\n", plugins) + "\r\n");
        File.WriteAllText(Path.Combine(Prof, "modlist.txt"), "# header\r\n" + string.Join("\r\n", modlist) + "\r\n");
        File.WriteAllText(Path.Combine(Prof, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=" + archiveList + "\r\n");
    }

    /// <summary>One active placeholder plugin, Dummy.esp, shipped by <paramref name="host"/>, and the given mod list.</summary>
    public void ProfileWithDummy(string host, params string[] modlist)
    {
        File.WriteAllText(Path.Combine(Mod(host), "Dummy.esp"), "x");
        Profile(new[] { "Dummy.esp" }, new[] { "*Dummy.esp" }, modlist);
    }

    /// <summary>A mod folder under mods\, created if absent.</summary>
    public string Mod(string name)
    {
        var d = Path.Combine(Mods, name);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Mark the mod list changed, so a live service rebuilds its asset view.</summary>
    public void TouchModlist() => File.SetLastWriteTimeUtc(Path.Combine(Prof, "modlist.txt"), DateTime.UtcNow.AddHours(1));

    public LoadOrderService Open()
    {
        var svc = LoadOrderService.WithInstance(Inst, 0,
            new UserConfigStore(Path.Combine(Root, "user-" + _services.Count + ".json")));
        _services.Add(svc);
        return svc;
    }

    public string Scratch(string name, byte[] bytes)
    {
        var p = Path.Combine(Root, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    public static void Loose(string baseDir, string rel, byte[] bytes)
    {
        var p = Path.Combine(baseDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
    }

    /// <summary>The placed file for a service-level outcome, or null when no folder was reported or the file is absent.</summary>
    public static string? PlacedAt(PlaceOutcome o, string rel) =>
        o.ModFolder is null ? null : (File.Exists(Path.Combine(o.ModFolder, rel)) ? Path.Combine(o.ModFolder, rel) : null);

    /// <summary>The on-disk path of a file placed by a tool-level call, read off the render's "mod folder:" line rather
    /// than rebuilt from the naming convention. Null = no folder reported, or the file is not there.</summary>
    public string? PlacedFileFrom(string render, string rel)
    {
        foreach (var line in render.Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith("mod folder:", StringComparison.Ordinal)) continue;
            var name = t["mod folder:".Length..].Trim();
            var cut = name.IndexOf("  —", StringComparison.Ordinal);
            if (cut >= 0) name = name[..cut].Trim();
            var p = Path.Combine(Mods, name, rel);
            return File.Exists(p) ? p : null;
        }
        return null;
    }

    /// <summary>Deny only the list-directory right on one folder, without inheritance, so a path under it still probes
    /// as absent while the folder's own enumeration fails. False means the deny did not bite and was taken back off.</summary>
    public static bool TryDenyList(string dir)
    {
        try
        {
            var di = new DirectoryInfo(dir);
            var sec = di.GetAccessControl();
            sec.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().Name, FileSystemRights.ListDirectory,
                InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
            di.SetAccessControl(sec);
            try { Directory.EnumerateFiles(dir, "*.bsa").ToList(); } catch (UnauthorizedAccessException) { return true; } catch { }
            UndenyList(dir);
            return false;
        }
        catch { return false; }
    }

    public static void UndenyList(string dir)
    {
        try
        {
            var di = new DirectoryInfo(dir);
            var sec = di.GetAccessControl();
            sec.RemoveAccessRuleAll(new FileSystemAccessRule(WindowsIdentity.GetCurrent().Name, FileSystemRights.ListDirectory,
                AccessControlType.Deny));
            di.SetAccessControl(sec);
        }
        catch { /* best effort; the temp tree goes either way */ }
    }

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        try { Directory.Delete(Root, recursive: true); } catch { /* temp cleanup best-effort */ }
    }
}

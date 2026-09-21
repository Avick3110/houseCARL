using System.Security.AccessControl;
using System.Security.Principal;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A loose root that will not read is NAMED by every lane that builds from an asset view, not only by
/// asset_status (#827). Each lane carries the failing root's name in its data and renders it, so the "a BSA or a
/// loose mod folder failed to read" hedge says WHICH folder instead of leaving the modder nothing to act on.</summary>
[Trait("tier", "integration")]
public sealed class UnreadableRootNamedTests : IDisposable
{
    readonly BlockedModWorld _w = new();

    public void Dispose() => _w.Dispose();

    static string Named(IReadOnlyList<string> failures) => string.Join(" | ", failures);

    [Fact]
    public void TheSkseInventoryNamesTheRootItCouldNotRead()
    {
        Assert.True(_w.Blocked, BlockedModWorld.NotStaged);

        var d = _w.Svc.SkseInventory();

        Assert.Contains(BlockedModWorld.BlockedMod, Named(d.RootFailures));
        Assert.Contains(BlockedModWorld.BlockedMod, SkseInventoryWire.Render(d, null, 200_000));
    }

    [Fact]
    public void TheSkseConfigAuditNamesTheRootItCouldNotRead()
    {
        Assert.True(_w.Blocked, BlockedModWorld.NotStaged);

        var d = _w.Svc.SkseConfigAudit();

        Assert.Contains(BlockedModWorld.BlockedMod, Named(d.RootFailures));
        Assert.Contains(BlockedModWorld.BlockedMod, SkseConfigAuditWire.Render(d, null, 200_000));
    }

    [Fact]
    public void TheNativePairingAuditNamesTheRootItCouldNotRead()
    {
        Assert.True(_w.Blocked, BlockedModWorld.NotStaged);

        var d = _w.Svc.NativePairingAudit();

        Assert.Contains(BlockedModWorld.BlockedMod, Named(d.RootFailures));
        Assert.Contains(BlockedModWorld.BlockedMod, NativePairingWire.Render(d, null, 200_000));
    }

    [Fact]
    public void TheSkyPatcherLayerNamesTheRootItCouldNotRead()
    {
        Assert.True(_w.Blocked, BlockedModWorld.NotStaged);

        var d = _w.Svc.SkyPatcherLayer();

        Assert.Contains(BlockedModWorld.BlockedMod, Named(d.RootFailures));
        Assert.Contains(BlockedModWorld.BlockedMod, SkyPatcherWire.RenderLayer(d, null, 200_000));
    }

    [Fact]
    public void TheNifBatchNamesTheRootItCouldNotRead()
    {
        Assert.True(_w.Blocked, BlockedModWorld.NotStaged);

        var d = _w.Svc.NifInspect(new[] { BlockedModWorld.BlockedMesh }, null);

        Assert.Contains(BlockedModWorld.BlockedMod, Named(d.RootFailures));
        Assert.Contains(BlockedModWorld.BlockedMod, NifWire.Render(d, new HashSet<string>(StringComparer.OrdinalIgnoreCase), Array.Empty<string>(), 200_000));
    }
}

/// <summary>Its own instance, never a shared fixture: it denies the current account one whole mod folder, which a
/// world other tests read must not carry. The blocked mod provides every layer these lanes scan — SKSE plugins, a
/// SkyPatcher INI, a compiled script and a mesh — all written BEFORE the deny goes on.</summary>
sealed class BlockedModWorld : IDisposable
{
    public const string BlockedMod = "RootBlockedMod";
    public const string BlockedMesh = @"meshes\hcblocked\blocked.nif";
    public const string NotStaged = "the deny ACE did not bite on this host, so the unreadable root was never staged";

    public string Root { get; }
    public LoadOrderService Svc { get; }
    public bool Blocked { get; }

    readonly string _blockedDir;

    public BlockedModWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-rootnamed-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profile = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        var pluginMod = Path.Combine(mods, "PluginMod");
        var assetMod = Path.Combine(mods, "AssetMod");
        _blockedDir = Path.Combine(mods, BlockedMod);
        foreach (var d in new[] { profile, Path.Combine(Root, "game", "Data"), pluginMod, assetMod })
            Directory.CreateDirectory(d);

        // Two masters, so the order resolves: an instance with no active plugin answers differently.
        foreach (var name in new[] { "HcRootOne", "HcRootTwo" })
        {
            var key = new ModKey(name, ModType.Master);
            var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
            mod.Weapons.AddNew().EditorID = name + "Weapon";
            mod.BeginWrite.ToPath(Path.Combine(pluginMod, key.FileName.String))
               .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }

        // A readable mod provides each layer too, so no lane answers "nothing here" instead of scanning.
        Write(assetMod, @"SKSE\Plugins\readable.ini", "[General]\r\n");
        Write(assetMod, @"SKSE\Plugins\SkyPatcher\npc\readable.ini", "filterByNpcs=HcRootOne.esm|000800:health=10\r\n");
        Write(assetMod, @"Scripts\readable.pex", "not a real pex");
        Write(assetMod, @"meshes\hcblocked\other.nif", "x");

        // The blocked mod's content is written FIRST: from the deny on, the folder cannot be touched.
        Write(_blockedDir, @"SKSE\Plugins\blocked.dll", "x");
        Write(_blockedDir, @"SKSE\Plugins\blocked.ini", "[General]\r\n");
        Write(_blockedDir, @"SKSE\Plugins\SkyPatcher\npc\blocked.ini", "filterByNpcs=HcRootOne.esm|000800:health=20\r\n");
        Write(_blockedDir, @"Scripts\blocked.pex", "x");
        Write(_blockedDir, BlockedMesh, "x");

        // From the deny on, anything that throws would leave the ACE behind and block the temp tree's own cleanup.
        try
        {
            Blocked = TryDenyAll(_blockedDir);
            Svc = Stage(instance, profile);
        }
        catch
        {
            UndenyAll(_blockedDir);
            throw;
        }
    }

    static void Write(string modDir, string rel, string text)
    {
        var path = Path.Combine(modDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    LoadOrderService Stage(string instance, string profile)
    {
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\nHcRootOne.esm\r\nHcRootTwo.esm\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*HcRootOne.esm\r\n*HcRootTwo.esm\r\n");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"),
            "# header\r\n+" + BlockedMod + "\r\n+AssetMod\r\n+PluginMod\r\n");
        File.WriteAllText(Path.Combine(profile, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");

        return LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
    }

    /// <summary>Deny the current user everything on one directory, and verify the deny bites rather than trusting the call.</summary>
    static bool TryDenyAll(string dir)
    {
        try
        {
            var me = WindowsIdentity.GetCurrent().Name;
            var di = new DirectoryInfo(dir);
            var sec = di.GetAccessControl();
            sec.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Deny));
            di.SetAccessControl(sec);
            try { Directory.EnumerateFiles(dir, "*").ToList(); } catch (UnauthorizedAccessException) { return true; } catch { }
            UndenyAll(dir);
            return false;
        }
        catch { return false; }
    }

    /// <summary>Take the deny ACE off again — left behind, it would block this world's own cleanup.</summary>
    static void UndenyAll(string dir)
    {
        try
        {
            var me = WindowsIdentity.GetCurrent().Name;
            var di = new DirectoryInfo(dir);
            var sec = di.GetAccessControl();
            sec.RemoveAccessRuleAll(new FileSystemAccessRule(me, FileSystemRights.FullControl, AccessControlType.Deny));
            di.SetAccessControl(sec);
        }
        catch { /* cleanup is best effort; the temp tree goes either way */ }
    }

    public void Dispose()
    {
        Svc.Dispose();
        UndenyAll(_blockedDir);
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

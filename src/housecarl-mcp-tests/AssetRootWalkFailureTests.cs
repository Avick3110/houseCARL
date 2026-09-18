using System.Security.AccessControl;
using System.Security.Principal;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A loose root the walk cannot enumerate is NAMED in the answer (#820): it sets read_incomplete and the
/// render says which root was not walked, instead of an answer that silently omits what it could not read.</summary>
[Trait("tier", "integration")]
public sealed class AssetRootWalkFailureTests : IDisposable
{
    readonly UnwalkableRootWorld _w = new();

    public void Dispose() => _w.Dispose();

    [Fact]
    public void ALooseRootThatThrowsMidWalkIsNamedInTheAnswer()
    {
        Assert.True(_w.Denied, "the deny ACE did not bite on this host, so the unwalkable root was never staged");

        var d = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { UnwalkableRootWorld.SweepDir });

        Assert.True(d.ReadIncomplete);
        var named = Assert.Single(d.RootFailures);
        Assert.Contains(UnwalkableRootWorld.BlockedMod, named);
        // The render is where the modder reads it: the root is named above the rows the walk did return.
        Assert.Contains("could NOT be walked this build", AssetWire.Render(d, 80_000));
        Assert.Contains(UnwalkableRootWorld.BlockedMod, AssetWire.Render(d, 80_000));
    }

    /// <summary>The single-path lane rides a different read — the per-subtree listing, not the recursive walk — and
    /// names the root the same way, so an ABSENT on a blocked root is hedged rather than stated flat.</summary>
    [Fact]
    public void AnExplicitPathUnderAnUnreadableRootIsNamedInTheAnswer()
    {
        Assert.True(_w.Denied, "the deny ACE did not bite on this host, so the unreadable root was never staged");

        var d = _w.Svc.AssetStatus(new[] { UnwalkableRootWorld.BlockedPath });

        Assert.True(d.ReadIncomplete);
        Assert.Contains(UnwalkableRootWorld.BlockedMod, Assert.Single(d.RootFailures));
        Assert.Contains("could NOT be walked this build", AssetWire.Render(d, 80_000));
    }
}

/// <summary>Its own instance, not a shared fixture: it denies itself access to one mod folder's subtree, which a
/// world other tests read must never carry. One mod provides a file under the sweep folder; a SECOND mod provides
/// the same folder with an unreadable subdirectory, so the recursive walk throws part way through.</summary>
sealed class UnwalkableRootWorld : IDisposable
{
    /// <summary>The folder the sweep is aimed at — provided by both mods below.</summary>
    public const string SweepDir = @"meshes\hcwalk";

    /// <summary>The mod whose copy of <see cref="SweepDir"/> cannot be walked.</summary>
    public const string BlockedMod = "BlockedMod";

    /// <summary>A file that IS on disk inside the unreadable subtree — what the single-path lane asks about.</summary>
    public const string BlockedPath = SweepDir + @"\locked\hidden.nif";

    public string Root { get; }
    public LoadOrderService Svc { get; }   // set once, inside the constructor's try

    /// <summary>Whether the deny ACE actually took on this host — a fixture that did not build is a failure, never a pass.</summary>
    public bool Denied { get; }

    readonly string _lockedDir;

    public UnwalkableRootWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-unwalkable-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profile = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        var pluginMod = Path.Combine(mods, "PluginMod");
        var assetMod = Path.Combine(mods, "AssetMod");
        var blockedMod = Path.Combine(mods, BlockedMod);
        foreach (var d in new[] { profile, Path.Combine(Root, "game", "Data"), pluginMod, assetMod, blockedMod })
            Directory.CreateDirectory(d);

        // Two masters, so the order resolves: an instance with no active plugin answers differently.
        foreach (var name in new[] { "HcWalkOne", "HcWalkTwo" })
        {
            var key = new ModKey(name, ModType.Master);
            var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
            mod.Weapons.AddNew().EditorID = name + "Weapon";
            mod.BeginWrite.ToPath(Path.Combine(pluginMod, key.FileName.String))
               .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }

        var readable = Path.Combine(assetMod, SweepDir);
        Directory.CreateDirectory(readable);
        File.WriteAllText(Path.Combine(readable, "ok.nif"), "x");

        // The blocked root has the same folder with one subdirectory the walk cannot enter.
        var blockedSweep = Path.Combine(blockedMod, SweepDir);
        _lockedDir = Path.Combine(blockedSweep, "locked");
        Directory.CreateDirectory(_lockedDir);
        File.WriteAllText(Path.Combine(_lockedDir, "hidden.nif"), "x");
        Denied = TryDenyAll(_lockedDir);

        // Past the deny, anything that throws leaves the object unbuilt and Dispose unrun, so the ACE would outlive
        // the fixture and block the temp tree's own cleanup. Take it off before the failure leaves here.
        try
        {
            Svc = Stage(instance, profile);
        }
        catch
        {
            UndenyAll(_lockedDir);
            throw;
        }
    }

    /// <summary>The profile files and the service, in one place so the constructor can undo the deny if this throws.</summary>
    LoadOrderService Stage(string instance, string profile)
    {
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\nHcWalkOne.esm\r\nHcWalkTwo.esm\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*HcWalkOne.esm\r\n*HcWalkTwo.esm\r\n");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "# header\r\n+" + BlockedMod + "\r\n+AssetMod\r\n+PluginMod\r\n");
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
        UndenyAll(_lockedDir);
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

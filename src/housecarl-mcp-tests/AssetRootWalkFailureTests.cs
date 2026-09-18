using System.Security.AccessControl;
using System.Security.Principal;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A loose root the resolver cannot read is NAMED in the answer (#820): it sets read_incomplete and the
/// render says which root was not read, instead of an answer that silently omits what it could not reach. Both the
/// shapes a denial takes are covered — a blocked subdirectory under a readable mod, and a blocked mod folder, whose
/// subtrees do not even stat.</summary>
[Trait("tier", "integration")]
public sealed class AssetRootWalkFailureTests : IDisposable
{
    readonly UnreadableRootWorld _w = new();

    public void Dispose() => _w.Dispose();

    static string Named(AssetStatusData d) => string.Join(" | ", d.RootFailures);

    [Fact]
    public void ALooseRootThatThrowsMidWalkIsNamedInTheAnswer()
    {
        Assert.True(_w.Denied, UnreadableRootWorld.NotStaged);

        var d = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { UnreadableRootWorld.SweepDir });

        Assert.True(d.ReadIncomplete);
        Assert.Contains(UnreadableRootWorld.SubBlockedMod, Named(d));
        // The render is where the modder reads it: the root is named above the rows the walk did return.
        Assert.Contains("could NOT be walked this build", AssetWire.Render(d, 80_000));
        Assert.Contains(UnreadableRootWorld.SubBlockedMod, AssetWire.Render(d, 80_000));
    }

    /// <summary>The single-path lane rides a different read — the per-subtree listing, not the recursive walk — and
    /// names the root the same way, so an ABSENT on a blocked root is hedged rather than stated flat.</summary>
    [Fact]
    public void AnExplicitPathUnderAnUnreadableRootIsNamedInTheAnswer()
    {
        Assert.True(_w.Denied, UnreadableRootWorld.NotStaged);

        var d = _w.Svc.AssetStatus(new[] { UnreadableRootWorld.SubBlockedPath });

        Assert.True(d.ReadIncomplete);
        Assert.Contains(UnreadableRootWorld.SubBlockedMod, Named(d));
        Assert.Contains("could NOT be walked this build", AssetWire.Render(d, 80_000));
    }

    /// <summary>A mod folder blocked at its TOP never throws: its subtrees do not stat, so `Directory.Exists` answers
    /// "not there" and the read never starts. Both lanes must still name it — the common permissions shape, where a
    /// whole folder belongs to another account.</summary>
    [Fact]
    public void AModFolderBlockedAtItsTopIsNamedByBothLanes()
    {
        Assert.True(_w.TopDenied, UnreadableRootWorld.NotStaged);

        var swept = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { UnreadableRootWorld.SweepDir });
        var one = _w.Svc.AssetStatus(new[] { UnreadableRootWorld.TopBlockedPath });

        Assert.True(swept.ReadIncomplete);
        Assert.Contains(UnreadableRootWorld.TopBlockedMod, Named(swept));
        Assert.True(one.ReadIncomplete);
        Assert.Contains(UnreadableRootWorld.TopBlockedMod, Named(one));
    }
}

/// <summary>Its own instance, not a shared fixture: it denies itself access to mod folders, which a world other
/// tests read must never carry. One mod provides a readable file under the sweep folder; a second blocks a
/// subdirectory of its copy; a third blocks its whole mod folder.</summary>
sealed class UnreadableRootWorld : IDisposable
{
    /// <summary>The folder the sweep is aimed at — provided by all three mods below.</summary>
    public const string SweepDir = @"meshes\hcwalk";

    /// <summary>The mod whose copy of <see cref="SweepDir"/> holds a subdirectory that will not enumerate.</summary>
    public const string SubBlockedMod = "SubBlockedMod";

    /// <summary>The mod whose whole folder is blocked, so nothing under it even stats.</summary>
    public const string TopBlockedMod = "TopBlockedMod";

    /// <summary>A file that IS on disk inside the unreadable subtree — what the single-path lane asks about.</summary>
    public const string SubBlockedPath = SweepDir + @"\locked\hidden.nif";

    /// <summary>A file that IS on disk inside the blocked mod folder.</summary>
    public const string TopBlockedPath = SweepDir + @"\top.nif";

    /// <summary>What a test says when the host would not let the fixture be built — never a quiet pass.</summary>
    public const string NotStaged = "the deny ACE did not bite on this host, so the unreadable root was never staged";

    public string Root { get; }
    public LoadOrderService Svc { get; }   // set once, inside the constructor's try

    /// <summary>Whether each deny ACE actually took on this host — a fixture that did not build is a failure, never a pass.</summary>
    public bool Denied { get; }
    public bool TopDenied { get; }

    readonly string _lockedDir;
    readonly string _topDir;

    public UnreadableRootWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-unreadable-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profile = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        var pluginMod = Path.Combine(mods, "PluginMod");
        var assetMod = Path.Combine(mods, "AssetMod");
        _topDir = Path.Combine(mods, TopBlockedMod);
        foreach (var d in new[] { profile, Path.Combine(Root, "game", "Data"), pluginMod, assetMod, _topDir })
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

        // One mod blocks a subdirectory of its own copy: the recursive walk starts and throws part way through.
        _lockedDir = Path.Combine(mods, SubBlockedMod, SweepDir, "locked");
        Directory.CreateDirectory(_lockedDir);
        File.WriteAllText(Path.Combine(_lockedDir, "hidden.nif"), "x");
        Denied = TryDenyAll(_lockedDir);

        // The other blocks its whole mod folder: nothing under it stats, so no read ever starts.
        var topSweep = Path.Combine(_topDir, SweepDir);
        Directory.CreateDirectory(topSweep);
        File.WriteAllText(Path.Combine(topSweep, "top.nif"), "x");
        TopDenied = TryDenyAll(_topDir);

        // Past the denies, anything that throws leaves the object unbuilt and Dispose unrun, so the ACEs would
        // outlive the fixture and block the temp tree's own cleanup. Take them off before the failure leaves here.
        try
        {
            Svc = Stage(instance, profile);
        }
        catch
        {
            UndenyAll(_lockedDir);
            UndenyAll(_topDir);
            throw;
        }
    }

    /// <summary>The profile files and the service, in one place so the constructor can undo the denies if this throws.</summary>
    LoadOrderService Stage(string instance, string profile)
    {
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\nHcWalkOne.esm\r\nHcWalkTwo.esm\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*HcWalkOne.esm\r\n*HcWalkTwo.esm\r\n");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"),
            "# header\r\n+" + TopBlockedMod + "\r\n+" + SubBlockedMod + "\r\n+AssetMod\r\n+PluginMod\r\n");
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
        UndenyAll(_topDir);
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

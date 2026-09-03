using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The synthetic MO2 world the epoch stamp's off-order and excluded-plugin facts are driven against (SPEC
/// §2.1.1). Three plugins: an ACTIVE master every sweep indexes, an OFF-ORDER plugin sitting in a DISABLED mod
/// folder (on disk, not in the active order — the "diff against a disabled old patch" shape), and an
/// UNPARSEABLE plugin that is ENABLED, so the index build excludes it wholesale and a scope naming it hits the
/// CORE sweep frame's excluded-plugin refusal.
///
/// <para>Ported from <c>EpochGuardProbe</c>'s world (#486 PR 2) as a real MO2 instance, in
/// <see cref="RecordsWorld"/>'s shape, so it is reachable both by <see cref="LoadOrderService"/> directly and by
/// <c>housecarl_check</c> off the built server.</para>
///
/// <para><b>Frozen</b>: tests take fixture-known names from it; a later need gets its own world.</para>
/// </summary>
public sealed class EpochWorld : IDisposable
{
    public const string OldName = "HcEpOld.esp";
    public const string BadName = "HcEpBad.esp";

    public string Root { get; }
    public string Instance { get; }
    public LoadOrderService Svc { get; }
    public FormKey Weapon { get; }

    public EpochWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-epoch-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));

        var masterKey = new ModKey("HcEpMaster", ModType.Master);
        var oldKey = new ModKey("HcEpOld", ModType.Plugin);
        string masterName = masterKey.FileName.String;

        var master = new SkyrimMod(masterKey, SkyrimRelease.SkyrimSE);
        var w = master.Weapons.AddNew(); w.EditorID = "HcEpWeapon";
        w.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1 };
        Weapon = w.FormKey;

        var old = new SkyrimMod(oldKey, SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(old, master.Weapons.First()))
            .BasicStats = new WeaponBasicStats { Damage = 15, Weight = 1 };

        Instance = Path.Combine(Root, "inst");
        var mods = Path.Combine(Instance, "mods");
        Directory.CreateDirectory(Path.Combine(mods, "MasterMod"));
        Directory.CreateDirectory(Path.Combine(mods, "OldMod"));
        Directory.CreateDirectory(Path.Combine(mods, "BadMod"));
        var masterFile = Path.Combine(mods, "MasterMod", masterName);
        var oldFile = Path.Combine(mods, "OldMod", OldName);
        master.BeginWrite.ToPath(masterFile).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        old.BeginWrite.ToPath(oldFile).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();
        // ENABLED but unparseable — the index build excludes it, so a scope naming it hits the CORE sweep
        // frame's own excluded-plugin refusal rather than the service's not-in-order refusal.
        File.WriteAllText(Path.Combine(mods, "BadMod", BadName), "this is not a bethesda plugin");

        File.WriteAllText(Path.Combine(Instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(Instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + masterName + "\r\n" + BadName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + masterName + "\r\n*" + BadName + "\r\n");
        // OldMod is UNTICKED ('-' prefix) — off-order: on disk, not in the active order.
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n-OldMod\r\n+BadMod\r\n+MasterMod\r\n");

        var store = new UserConfigStore(Path.Combine(Root, "user.json"));
        Svc = LoadOrderService.WithInstance(Instance, 0, store);
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>The shared, read-only epoch world. One build per test class collection.</summary>
public sealed class EpochFixture : IDisposable
{
    public EpochWorld W { get; } = new();
    public LoadOrderService Svc => W.Svc;
    public void Dispose() => W.Dispose();
}

[CollectionDefinition("epoch")]
public sealed class EpochCollection : ICollectionFixture<EpochFixture> { }

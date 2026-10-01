using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The resolver's plugin-scoped record stream pairs each record with the plugin it came from and that plugin's
/// own body, never the winner's; the type stream yields the winner's body. A master (damage 10) is overridden by A (50)
/// and then by B (99), which wins.</summary>
[Trait("tier", "integration")]
public sealed class ScopedRecordSourcePairingTests : IDisposable
{
    const string MasterName = "hcSrcMaster.esp", AName = "hcSrcA.esp", BName = "hcSrcB.esp";
    const ushort DmgA = 50, DmgB = 99;
    static readonly Type[] Weapons = { typeof(IWeaponGetter) };

    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-source-pairing-" + Guid.NewGuid().ToString("N"));
    readonly LoadOrderResolver _resolver;
    readonly FormKey _fk;

    public ScopedRecordSourcePairingTests()
    {
        Directory.CreateDirectory(_dir);
        var master = new SkyrimMod(ModKey.FromNameAndExtension(MasterName), SkyrimRelease.SkyrimSE);
        var mw = master.Weapons.AddNew();
        mw.EditorID = "hcSrcSword";
        mw.BasicStats = new WeaponBasicStats { Damage = 10 };
        _fk = mw.FormKey;
        master.BeginWrite.ToPath(Path.Combine(_dir, MasterName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        WriteOverride(AName, master, mw, DmgA);
        WriteOverride(BName, master, mw, DmgB);
        _resolver = LoadOrderResolver.Build(new[] { MasterName, AName, BName }.Select(n => Path.Combine(_dir, n)).ToList());
    }

    void WriteOverride(string name, SkyrimMod master, Weapon mw, ushort damage)
    {
        var mod = new SkyrimMod(ModKey.FromNameAndExtension(name), SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(mod, mw)).BasicStats = new WeaponBasicStats { Damage = damage };
        mod.BeginWrite.ToPath(Path.Combine(_dir, name)).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();
    }

    public void Dispose()
    {
        _resolver.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* temp cleanup best-effort */ }
    }

    static ushort? Damage(IMajorRecordGetter body) => (body as IWeaponGetter)?.BasicStats?.Damage;

    // Probe: "winner is hcSrcB.esp (highest override wins)".
    [Fact]
    public void TheHighestOverrideWins() =>
        Assert.Equal(BName, _resolver.ResolveWinner(_fk)?.WinnerPlugin, StringComparer.OrdinalIgnoreCase);

    // Probe: "plugins=[A]: weapon yielded once", "source = hcSrcA.esp", "body is hcSrcA.esp's OWN (dmg 50, not the winner)";
    // and the same three for plugins=[B].
    [Theory]
    [InlineData(AName, DmgA)]
    [InlineData(BName, DmgB)]
    public void ASinglePluginScopeYieldsThatPluginsOwnBodyOnce(string plugin, ushort damage)
    {
        var hit = Assert.Single(_resolver.RecordsIn(new[] { plugin }, Weapons).Where(x => x.fk == _fk));
        Assert.Equal(plugin, hit.source, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(damage, Damage(hit.body));
    }

    // Probe: "plugins=[A,B]: weapon yielded once per scoped plugin (2)", "A's yield → source=A & dmg=50", "B's yield →
    // source=B & dmg=99", "EVERY yield's body matches its source plugin (no mispairing — the heart of the fix)".
    [Fact]
    public void ATwoPluginScopeYieldsEachPluginsOwnBodyOnce()
    {
        var pairs = _resolver.RecordsIn(new[] { AName, BName }, Weapons).Where(x => x.fk == _fk)
            .Select(x => (x.source.ToLowerInvariant(), Damage(x.body))).OrderBy(p => p.Item1).ToList();

        Assert.Equal(new[] { (AName.ToLowerInvariant(), (ushort?)DmgA), (BName.ToLowerInvariant(), (ushort?)DmgB) }, pairs);
    }

    // Probe: "type=: weapon yielded once (the winner only)", "type=: winner body is B's (dmg 99)".
    [Fact]
    public void TheTypeStreamYieldsTheWinnersBodyOnce()
    {
        var hit = Assert.Single(_resolver.WinnerRecordsOfType(Weapons).Where(x => x.fk == _fk));
        Assert.Equal(DmgB, Damage(hit.body));
    }
}

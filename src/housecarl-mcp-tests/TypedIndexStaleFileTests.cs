using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A plugin rewritten after the index build (#1158) is no longer read under that build: the seek and the
/// gather refuse, naming it (ruling F), instead of falling back to a flat walk of a file the build does not describe.</summary>
[Trait("tier", "integration")]
public sealed class TypedIndexStaleFileTests
{
    [Fact]
    public void ASeekOrGatherOnARewrittenPluginRefuses()
    {
        using var w = new TypedIndexWorld();
        var pin = w.Svc.CapturePin();

        // Rewrite the master under the captured build: the armor key becomes a weapon and the list is dropped.
        var master = new SkyrimMod(ModKey.FromFileName(w.MasterName), SkyrimRelease.SkyrimSE);
        master.Weapons.Add(new Weapon(w.Armor, SkyrimRelease.SkyrimSE) { EditorID = "HcTypedStaleWeapon" });
        master.BeginWrite.ToPath(Path.Combine(w.Root, "inst", "mods", "TypedMaster", w.MasterName))
            .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        using (var session = pin.Resolver.OpenSession())
            Assert.Equal(w.MasterName, Assert.Throws<LoadOrderChangedMidCallException>(
                () => pin.View.GetRecord(session, w.MasterName, w.Armor)).Plugin);

        using (var session = pin.Resolver.OpenSession())
        {
            var sink = new Dictionary<FormKey, IMajorRecordGetter>();
            Assert.Equal(w.MasterName, Assert.Throws<LoadOrderChangedMidCallException>(
                () => pin.View.CollectRecords(session, w.MasterName, new[] { w.Armor }, sink)).Plugin);
            Assert.Empty(sink);
        }
    }
}

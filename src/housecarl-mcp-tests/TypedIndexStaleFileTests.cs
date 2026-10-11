using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A plugin rewritten after the index build (#1158): a typed seek that misses falls back to a flat walk and still finds the record.
/// Fails when the flat fallback is removed: the armor key, now a weapon, is then not found.</summary>
[Trait("tier", "integration")]
public sealed class TypedIndexStaleFileTests
{
    [Fact]
    public void ATypedMissOnARewrittenPluginFallsBackToAFlatWalk()
    {
        using var w = new TypedIndexWorld();
        var pin = w.Svc.CapturePin();

        // Rewrite the master under the captured build: the armor key becomes a weapon and the list is dropped.
        var master = new SkyrimMod(ModKey.FromFileName(w.MasterName), SkyrimRelease.SkyrimSE);
        master.Weapons.Add(new Weapon(w.Armor, SkyrimRelease.SkyrimSE) { EditorID = "HcTypedStaleWeapon" });
        master.BeginWrite.ToPath(Path.Combine(w.Root, "inst", "mods", "TypedMaster", w.MasterName))
            .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        long[] Costs() { var c = w.Svc.Counters; return new[] { c.BodySeeks, c.TypedSeeks, c.CollectPasses, c.TypedCollectPasses }; }

        using (var session = pin.Resolver.OpenSession())
        {
            var before = Costs();
            var seek = pin.View.GetRecord(session, w.MasterName, w.Armor);
            Assert.Equal("HcTypedStaleWeapon", Assert.IsAssignableFrom<IWeaponGetter>(seek).EditorID);
            Assert.Null(pin.View.GetRecord(session, w.MasterName, w.List));
            Assert.Equal(new long[] { 2, 0, 0, 0 }, Costs().Zip(before, (a, b) => a - b).ToArray());
        }

        using (var session = pin.Resolver.OpenSession())
        {
            var sink = new Dictionary<FormKey, IMajorRecordGetter>();
            var before = Costs();
            pin.View.CollectRecords(session, w.MasterName, new[] { w.Armor }, sink);
            Assert.Equal("HcTypedStaleWeapon", Assert.IsAssignableFrom<IWeaponGetter>(sink[w.Armor]).EditorID);
            Assert.Equal(new long[] { 0, 0, 1, 0 }, Costs().Zip(before, (a, b) => a - b).ToArray());
        }
    }
}

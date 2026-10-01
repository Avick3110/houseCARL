using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// Compacting a plugin another plugin overrides: the overrider shares the renumbered FormKey but carries no link into
/// the plugin, so the identify pass tests each external record's own FormKey too. An overrider is a warning and the
/// compaction proceeds; a referencer is still refused by name. Each test builds its own two-plugin instance.
/// </summary>
[Trait("tier", "integration")]
public sealed class CompactOverriderDetectTests
{
    // Probe arm OVERRIDER: compacting P succeeds, names Q as an external overrider, and lists no referencer.
    [Fact]
    public void APluginThatOverridesTheTargetIsNamedAsAnOverriderAndTheCompactProceeds()
    {
        var mo2 = new ScratchMo2("hc-compact-overrider-");
        try
        {
            var pKey = new ModKey("OvrTarget", ModType.Plugin);
            var pPath = mo2.InMod("OvrTarget", pKey);
            var p = new SkyrimMod(pKey, SkyrimRelease.SkyrimSE);
            p.Weapons.Add(new Weapon(new FormKey(pKey, 0xA01), SkyrimRelease.SkyrimSE) { EditorID = "OvrWeap", BasicStats = new WeaponBasicStats { Damage = 7 } });
            p.BeginWrite.ToPath(pPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            var qKey = new ModKey("OvrDependent", ModType.Plugin);
            using (var pOv = SkyrimMod.CreateFromBinaryOverlay(pPath, SkyrimRelease.SkyrimSE))
            {
                var q = new SkyrimMod(qKey, SkyrimRelease.SkyrimSE);
                WriteEngine.GenericGetOrAddAsOverride(q, pOv.Weapons.First(w => w.EditorID == "OvrWeap"), pOv.ToImmutableLinkCache());
                q.BeginWrite.ToPath(mo2.InMod("OvrDependent", qKey)).WithLoadOrder(new[] { pOv }).NoNextFormIDProcessing().Write();
            }
            mo2.Profile("OvrTarget.esp\r\nOvrDependent.esp\r\n", "*OvrTarget.esp\r\n*OvrDependent.esp\r\n", "+OvrDependent\r\n+OvrTarget\r\n");
            File.WriteAllText(Path.Combine(mo2.Profiles, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");

            using var svc = mo2.Open();
            var o = svc.CompactPlugin("OvrTarget.esp");

            Assert.True(o.Success, o.Error);
            Assert.NotNull(o.ExternalOverriders);
            Assert.Contains("OvrDependent.esp", o.ExternalOverriders!, StringComparer.OrdinalIgnoreCase);
            Assert.Empty(o.ExternalPlugins);
        }
        finally { mo2.Delete(); }
    }

    // Probe arm REFERENCER: a plugin that links into the target is still refused by name, not asked to acknowledge.
    [Fact]
    public void APluginThatReferencesTheTargetIsStillRefusedByName()
    {
        var mo2 = new ScratchMo2("hc-compact-referencer-");
        try
        {
            var pKey = new ModKey("RefTarget", ModType.Plugin);
            var pPath = mo2.InMod("RefTarget", pKey);
            var pWeap = new FormKey(pKey, 0xA01);
            var p = new SkyrimMod(pKey, SkyrimRelease.SkyrimSE);
            p.Weapons.Add(new Weapon(pWeap, SkyrimRelease.SkyrimSE) { EditorID = "RefWeap", BasicStats = new WeaponBasicStats { Damage = 7 } });
            p.BeginWrite.ToPath(pPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            var rKey = new ModKey("RefDependent", ModType.Plugin);
            using (var pOv = SkyrimMod.CreateFromBinaryOverlay(pPath, SkyrimRelease.SkyrimSE))
            {
                var r = new SkyrimMod(rKey, SkyrimRelease.SkyrimSE);
                var fl = new FormList(new FormKey(rKey, 0xA01), SkyrimRelease.SkyrimSE) { EditorID = "RefList" };
                fl.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(pWeap));
                r.FormLists.Add(fl);
                r.ModHeader.Stats.NextFormID = 0xA02;
                r.BeginWrite.ToPath(mo2.InMod("RefDependent", rKey)).WithLoadOrder(new[] { pOv }).NoNextFormIDProcessing().Write();
            }
            mo2.Profile("RefTarget.esp\r\nRefDependent.esp\r\n", "*RefTarget.esp\r\n*RefDependent.esp\r\n", "+RefDependent\r\n+RefTarget\r\n");
            File.WriteAllText(Path.Combine(mo2.Profiles, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");

            using var svc = mo2.Open();
            var o = svc.CompactPlugin("RefTarget.esp");

            Assert.False(o.Success);
            Assert.False(o.NeedsAcknowledge);
            Assert.Contains("RefDependent.esp", o.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally { mo2.Delete(); }
    }
}

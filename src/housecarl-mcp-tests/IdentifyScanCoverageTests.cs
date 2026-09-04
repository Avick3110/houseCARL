using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The compact/merge external-referencer scan against a referencer that becomes unreadable AFTER the
/// index was built — a plugin held open by xEdit, MO2 or the running game. The file lock mechanism:
/// <c>docs/architecture/test-project-fixtures.md</c>.</summary>
[Trait("tier", "integration")]
public sealed class IdentifyScanCoverageTests
{
    /// <summary>A throwaway two-plugin world this test owns outright: a target holding one weapon, and a
    /// second plugin whose form list references that weapon.</summary>
    sealed class ReferencerWorld : IDisposable
    {
        public string Root { get; }
        public string TargetPath { get; }
        public string ReferencerPath { get; }
        public ModKey TargetKey { get; } = new("HcScanTarget", ModType.Plugin);
        public ModKey ReferencerKey { get; } = new("HcScanRef", ModType.Plugin);
        public string TargetName => TargetKey.FileName.String;
        public string ReferencerName => ReferencerKey.FileName.String;
        public FormKey WeaponKey { get; }

        public ReferencerWorld()
        {
            Root = Path.Combine(Path.GetTempPath(), "hc-scan-coverage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);

            WeaponKey = new FormKey(TargetKey, 0x800);
            var target = new SkyrimMod(TargetKey, SkyrimRelease.SkyrimSE);
            target.Weapons.Add(new Weapon(WeaponKey, SkyrimRelease.SkyrimSE) { EditorID = "HcScanWeapon" });
            target.ModHeader.Stats.NextFormID = 0x801;
            TargetPath = Path.Combine(Root, TargetName);
            target.BeginWrite.ToPath(TargetPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();

            using var targetOverlay = SkyrimMod.CreateFromBinaryOverlay(TargetPath, SkyrimRelease.SkyrimSE);
            var referencer = new SkyrimMod(ReferencerKey, SkyrimRelease.SkyrimSE);
            var list = new FormList(new FormKey(ReferencerKey, 0x800), SkyrimRelease.SkyrimSE) { EditorID = "HcScanList" };
            list.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(WeaponKey));
            referencer.FormLists.Add(list);
            referencer.ModHeader.Stats.NextFormID = 0x801;
            ReferencerPath = Path.Combine(Root, ReferencerName);
            referencer.BeginWrite.ToPath(ReferencerPath).WithLoadOrder(new[] { targetOverlay }).NoNextFormIDProcessing().Write();
        }

        public void Dispose() { try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ } }
    }

    static RemapEngine.IdentifyResult Identify(LoadOrderResolver resolver, ReferencerWorld world)
        => RemapEngine.IdentifyExternalReferencers(
            resolver,
            new HashSet<FormKey> { world.WeaponKey },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { world.TargetName });

    /// <summary>Holding the referencer open across the identify call, with the index already built, is the
    /// live shape: the hold does not change the file's last-write time, so nothing rebuilds and the scan
    /// meets a plugin it cannot open. It must report that plugin as unscannable and must not count it as
    /// scanned — a clean scan here would let a compaction proceed and break the referencer.</summary>
    [Fact]
    public void AReferencerLockedAfterTheIndexWasBuiltIsReportedUnscannable()
    {
        using var world = new ReferencerWorld();
        using var resolver = LoadOrderResolver.Build(new[] { world.TargetPath, world.ReferencerPath });

        // Vacuity: unheld, the same call finds the referencer — so what follows is about the lock, not about
        // a world that never had a referencer in it.
        var open = Identify(resolver, world);
        Assert.Contains(world.ReferencerName, open.ExternalPlugins, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(1, open.PluginsScanned);
        Assert.Equal(0, open.UnscannableRecords);

        using var hold = HeldOpen.Hold(world.ReferencerPath);
        var locked = Identify(resolver, world);

        Assert.NotEqual(0, locked.UnscannableRecords);
        Assert.Contains(locked.UnscannableSamples,
                        s => s.Contains(world.ReferencerName, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, locked.PluginsScanned);
    }
}

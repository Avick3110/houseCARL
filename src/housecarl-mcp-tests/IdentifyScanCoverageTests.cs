using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;
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
        Assert.Equal(0, locked.PluginsScanned);

        // The plugin is named WITH its cause and a reason: a held file is the unopenable cause, and the reason is
        // recorded unconditionally rather than competing for the per-record sample budget.
        var unread = Assert.Single(locked.UnscannablePlugins!);
        Assert.Equal(world.ReferencerName, unread.Plugin, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(RemapEngine.UnscannableCause.Unopenable, unread.Cause);
        Assert.NotEmpty(unread.Reason);
    }
}

/// <summary>The compact verb's own answer when the external-referencer scan could not read a plugin through —
/// driven end to end against a synthetic MO2 instance, because what changes is which lane refuses and what the
/// caller reads, not the scan result.</summary>
[Trait("tier", "integration")]
public sealed class CompactUnscannableReferencerTests
{
    /// <summary>A throwaway MO2 instance: a target plugin holding one weapon, and a second, later plugin whose
    /// form list references it. Both active, each in its own mod folder, so compact resolves them normally.</summary>
    sealed class CompactWorld : IDisposable
    {
        public string Root { get; }
        public string TargetPath { get; }
        public string ReferencerPath { get; }
        public ModKey TargetKey { get; } = new("HcCompactTarget", ModType.Plugin);
        public ModKey ReferencerKey { get; } = new("HcCompactRef", ModType.Plugin);
        public string TargetName => TargetKey.FileName.String;
        public string ReferencerName => ReferencerKey.FileName.String;
        public LoadOrderService Svc { get; }

        public CompactWorld()
        {
            Root = Path.Combine(Path.GetTempPath(), "hc-compact-unscannable-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));
            var inst = Path.Combine(Root, "inst");
            var modsDir = Path.Combine(inst, "mods");
            Directory.CreateDirectory(Path.Combine(modsDir, "TargetMod"));
            Directory.CreateDirectory(Path.Combine(modsDir, "RefMod"));

            var weaponKey = new FormKey(TargetKey, 0x800);
            var target = new SkyrimMod(TargetKey, SkyrimRelease.SkyrimSE);
            target.Weapons.Add(new Weapon(weaponKey, SkyrimRelease.SkyrimSE) { EditorID = "HcCompactWeapon" });
            target.ModHeader.Stats.NextFormID = 0x801;
            TargetPath = Path.Combine(modsDir, "TargetMod", TargetName);
            target.BeginWrite.ToPath(TargetPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();

            using (var targetOverlay = SkyrimMod.CreateFromBinaryOverlay(TargetPath, SkyrimRelease.SkyrimSE))
            {
                var referencer = new SkyrimMod(ReferencerKey, SkyrimRelease.SkyrimSE);
                var list = new FormList(new FormKey(ReferencerKey, 0x800), SkyrimRelease.SkyrimSE) { EditorID = "HcCompactList" };
                list.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(weaponKey));
                referencer.FormLists.Add(list);
                referencer.ModHeader.Stats.NextFormID = 0x801;
                ReferencerPath = Path.Combine(modsDir, "RefMod", ReferencerName);
                referencer.BeginWrite.ToPath(ReferencerPath).WithLoadOrder(new[] { targetOverlay }).NoNextFormIDProcessing().Write();
            }

            File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
                "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
            var prof = Path.Combine(inst, "profiles", "Default");
            Directory.CreateDirectory(prof);
            File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + TargetName + "\r\n" + ReferencerName + "\r\n");
            File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + TargetName + "\r\n*" + ReferencerName + "\r\n");
            File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+RefMod\r\n+TargetMod\r\n");

            Svc = LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(Root, "user.json")));
            Svc.Stats();                                  // build the index BEFORE any hold, which is the live shape
        }

        public void Dispose()
        {
            Svc.Dispose();
            try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
        }
    }

    /// <summary>The in-place lane must REFUSE a run whose referencer scan could not read a plugin, even with
    /// acknowledge=true — which skips the confirm prompt, so a note there would never be seen. There is no backup
    /// and no review step in place, and the unread plugin may be the very thing the renumber breaks.</summary>
    [Fact]
    public void InPlaceCompactRefusesWhenAReferencerCouldNotBeRead()
    {
        using var world = new CompactWorld();
        using var hold = HeldOpen.Hold(world.ReferencerPath);
        var before = new FileInfo(world.TargetPath).Length;

        var o = world.Svc.CompactPlugin(world.TargetName, esl: true, inPlace: true, repointExternals: false, acknowledge: true);

        Assert.False(o.Success);
        Assert.False(o.NeedsAcknowledge);                                  // a refusal, not a prompt acknowledge could waive
        Assert.Contains(world.ReferencerName, o.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("in_place=false", o.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, new FileInfo(world.TargetPath).Length);       // nothing was written
    }

    /// <summary>The new-plugin lane keeps the note, and the note says which of the two causes it met: a plugin that
    /// could not be OPENED is the held-open case, and the remedy that fixes it is naming the programs to close.</summary>
    [Fact]
    public void TheReportSaysAnUnreadablePluginCouldNotBeOpened()
    {
        using var world = new CompactWorld();
        using var hold = HeldOpen.Hold(world.ReferencerPath);

        var text = WriteTools.RenderCompact(
            world.Svc.CompactPlugin(world.TargetName, esl: true, inPlace: false, repointExternals: false, acknowledge: false));

        Assert.Contains(world.ReferencerName, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not be OPENED", text, StringComparison.Ordinal);
        Assert.Contains("close xEdit, MO2 or Skyrim", text, StringComparison.Ordinal);
    }
}

using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>SkyPatcher INI discovery (<see cref="SkyPatcherDiscovery"/>, from the <c>skypatcher-discovery-guard</c>
/// probe) over synthetic mod folders through the real <see cref="AssetResolver"/>: the union of differently named
/// INIs, the winner-only same-path collision, the apply order, the <c>Plugin.esp.ini</c> gate, the
/// <c>SkyPatcher.ini</c> toggle, stray and undocumented INIs, and the loose-only rule for an INI inside a BSA.</summary>
[Trait("tier", "integration")]
public sealed class SkyPatcherDiscoveryTests : IDisposable
{
    const string W = "SKSE/Plugins/SkyPatcher/weapon";
    static readonly string FixtureBsa = Path.Combine(HarnessPaths.RepoRoot, "src", "housecarl-mcp-tests", "fixtures", "skypatcher", "IniFixture.bsa");

    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-skypatcher-discovery-tests-" + Guid.NewGuid().ToString("N"));
    readonly string _overwrite, _mods, _data;
    static readonly string[] Enabled = { "ModA", "ModB" };   // ModA has the higher MO2 priority

    public SkyPatcherDiscoveryTests()
    {
        _overwrite = Path.Combine(_root, "overwrite");
        _mods = Path.Combine(_root, "mods");
        _data = Path.Combine(_root, "Data");
        var modA = Path.Combine(_mods, "ModA");
        var modB = Path.Combine(_mods, "ModB");
        foreach (var d in new[] { _overwrite, modA, modB, _data }) Directory.CreateDirectory(d);

        Write(modA, $"{W}/a.ini", "attackDamage=1");
        Write(modB, $"{W}/b.ini", "attackDamage=2");
        Write(modA, $"{W}/same.ini", "attackDamage=3");
        Write(modB, $"{W}/same.ini", "attackDamage=999");
        Write(modA, $"{W}/zzz.ini", "attackDamage=4");
        Write(modA, $"{W}/org/bbb.ini", "attackDamage=5");
        Write(modA, $"{W}/org/deep/ccc.ini", "attackDamage=5");
        Write(modA, $"{W}/Aaa/zz.ini", "attackDamage=5");
        Write(modA, $"{W}/Gated.esp.ini.bak", "attackDamage=6");
        Write(modA, $"{W}/LOUD.INI", "attackDamage=6");
        Write(modA, $"{W}/Gated.esp-extra.ini", "attackDamage=6");
        Write(modA, "SKSE/Plugins/SkyPatcher/armor/noKey.ini", "weight=1");
        Write(modA, "SKSE/Plugins/SkyPatcher/light/l.ini", "radius=1");
        Write(modA, $"{W}/Gated.esp.ini", "attackDamage=6");
        Write(modA, $"{W}/Absent.esp.ini", "attackDamage=7");
        Write(modA, "SKSE/Plugins/SkyPatcher/stray.ini", "attackDamage=8");
        Write(modA, "SKSE/Plugins/SkyPatcher/bogusType/x.ini", "foo=1");
        Write(modA, "SKSE/Plugins/SkyPatcher/npc/n.ini", "setEssential=true");
        // An inline comment on the toggle and trailing text on the header must still read as 'npc disabled'.
        Write(modA, "SKSE/Plugins/SkyPatcher.ini", "[Patcher] ; per-type toggles\niEnableNpcPatching=0 ; off for testing\niEnableWeaponPatching=1\n[Log]\niEnablelog=0\n");
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    static void Write(string baseDir, string rel, string text)
    {
        var p = Path.Combine(baseDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    SkyPatcherDiscovery.LayerScan Scan(params ActiveArchive[] archives)
    {
        using var r = AssetResolver.Build(_overwrite, _mods, _data, Enabled, archives);
        return SkyPatcherDiscovery.Scan(r.Capture(), SkyPatcherCatalog.Load(),
            p => p.Equals("Gated.esp", StringComparison.OrdinalIgnoreCase));
    }

    static SkyPatcherDiscovery.FolderScan Folder(SkyPatcherDiscovery.LayerScan scan, string name) =>
        Assert.Single(scan.Folders, f => f.Subfolder.Equals(name, StringComparison.OrdinalIgnoreCase));

    // weapon folder discovered + catalog attached + enabled
    [Fact]
    public void TheWeaponFolderIsFoundWithItsCatalogAndEnabled()
    {
        var weapon = Folder(Scan(), "weapon");
        Assert.NotNull(weapon.Catalog);
        Assert.True(weapon.PatchingEnabled);
    }

    // differently-named INIs from DIFFERENT mods BOTH apply (union, not winner)
    [Fact]
    public void DifferentlyNamedInisFromDifferentModsBothApply()
    {
        var applied = Folder(Scan(), "weapon").Files.Where(f => f.NotApplied is null).Select(f => f.SortKey).ToList();
        Assert.Contains("a.ini", applied);
        Assert.Contains("b.ini", applied);
    }

    // same-path collision: winner is ModA, ModB named as shadowed; …the winner's CONTENT is what got parsed (3, not 999);
    // …and the collision is a layer note
    [Fact]
    public void ASamePathCollisionParsesTheWinnerAndNamesTheLoser()
    {
        var scan = Scan();
        var same = Assert.Single(Folder(scan, "weapon").Files, f => f.SortKey == "same.ini");

        Assert.Equal("ModA", same.WinningProvider);
        Assert.Contains("ModB", same.ShadowedProviders);
        Assert.Contains(same.Lines, l => l.Segments.Any(s => s.RawValue == "3"));
        Assert.DoesNotContain(same.Lines, l => l.Segments.Any(s => s.RawValue == "999"));
        Assert.Contains(scan.Notes, n => n.Contains("same.ini") && n.Contains("SHADOWED"));
    }

    // files are read breadth-first: the folder's own files A to Z, then each depth of subfolders, A to Z by path component
    [Fact]
    public void FilesAreReadBreadthFirst() =>
        Assert.Equal(new[] { "a.ini", "Absent.esp.ini", "b.ini", "Gated.esp-extra.ini", "Gated.esp.ini", "Gated.esp.ini.bak", "LOUD.INI",
                             "same.ini", "zzz.ini", "Aaa\\zz.ini", "org\\bbb.ini", "org\\deep\\ccc.ini" },
            Folder(Scan(), "weapon").Files.Select(f => f.SortKey));

    // a name with '.ini' inside is read and gated on the plugin before it; an upper-case ending and a plugin name with more text are not read
    [Fact]
    public void TheFileNameRulesReadIniAnywhereAndRefuseTheRest()
    {
        var files = Folder(Scan(), "weapon").Files;
        var bak = Assert.Single(files, f => f.SortKey == "Gated.esp.ini.bak");
        Assert.Null(bak.NotApplied);
        Assert.Equal("Gated.esp", bak.GatePlugin);
        Assert.Contains("lower-case", Assert.Single(files, f => f.SortKey == "LOUD.INI").NotApplied);
        Assert.Contains("exactly the plugin", Assert.Single(files, f => f.SortKey == "Gated.esp-extra.ini").NotApplied);
    }

    // a patcher whose switch is missing is off, except Light; folders are listed in patcher order (weapon before armor before light)
    [Fact]
    public void AMissingSwitchIsOffButLightRuns()
    {
        var scan = Scan();
        Assert.False(Folder(scan, "armor").PatchingEnabled);
        Assert.Contains(scan.Notes, n => n.Contains("no iEnable…Patching key for 'armor'"));
        Assert.True(Folder(scan, "light").PatchingEnabled);
        var order = scan.Folders.Select(f => f.Subfolder).Where(f => f is "weapon" or "armor" or "light").ToList();
        Assert.Equal(new[] { "weapon", "armor", "light" }, order);
    }

    // with no SkyPatcher.ini every patcher but Light is off, said once
    [Fact]
    public void NoSwitchFileLeavesEveryPatcherButLightOff()
    {
        File.Delete(Path.Combine(_mods, "ModA", "SKSE", "Plugins", "SkyPatcher.ini"));
        var scan = Scan();
        Assert.False(Folder(scan, "weapon").PatchingEnabled);
        Assert.True(Folder(scan, "light").PatchingEnabled);
        Assert.Single(scan.Notes, n => n.Contains("no loose SkyPatcher.ini"));
        Assert.Contains(scan.Notes, n => n.Contains("is not loose in this order, which leaves 'weapon' patching off"));
        Assert.DoesNotContain(scan.Notes, n => n.Contains("iEnable…Patching"));
    }

    // an unreadable SkyPatcher.ini is said and every folder marked, never assumed either way
    [Fact]
    public void AnUnreadableSwitchFileIsSaidAndMarksEveryFolder()
    {
        using var locked = new FileStream(Path.Combine(_mods, "ModA", "SKSE", "Plugins", "SkyPatcher.ini"), FileMode.Open, FileAccess.Read, FileShare.None);
        var scan = Scan();
        Assert.Contains(scan.Notes, n => n.Contains("SkyPatcher.ini could not be read") && n.Contains("UNKNOWN"));
        Assert.All(scan.Folders, f => Assert.True(f.SwitchUnknown, f.Subfolder));
        Assert.DoesNotContain(scan.Notes, n => n.Contains("assumed"));
    }

    // an unreadable SkyPatcher.ini replays every folder, as its note says: none reads off, the counts hold every line, and a draft into a new folder replays
    [Fact]
    public void AnUnreadableSwitchFileReplaysEveryFolder()
    {
        using var locked = new FileStream(Path.Combine(_mods, "ModA", "SKSE", "Plugins", "SkyPatcher.ini"), FileMode.Open, FileAccess.Read, FileShare.None);
        var scan = Scan();
        Assert.All(scan.Folders, f => Assert.True(f.PatchingEnabled, f.Subfolder));
        Assert.NotEmpty(SkyPatcherDiscovery.OrderedLines(Folder(scan, "npc")));
        Assert.NotEmpty(SkyPatcherDiscovery.OrderedLines(Folder(scan, "armor")));
        Assert.DoesNotContain(scan.Notes, n => n.Contains("iEnable…Patching"));

        var text = SkyPatcherWire.RenderLayer(new SkyPatcherLayerData(scan,
            Array.Empty<SkyPatcherConflicts.SkyPatcherConflict>(), Array.Empty<SkyPatcherConflicts.SkyPatcherItm>(),
            Array.Empty<SkyPatcherConflicts.SkyPatcherDuplicate>(), Array.Empty<SkyPatcherNoOpWrite>(),
            Array.Empty<string>(), Array.Empty<string>(), false, Array.Empty<string>(), "Default"), null, 40_000);
        Assert.DoesNotContain("the DLL skips this whole folder", text);
        Assert.Contains($"({scan.Folders.Sum(f => f.Files.Count(x => x.NotApplied is null))} applied)", text);

        var path = Path.Combine(_root, "drafts", "ammo", "d.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "damage=9");
        Assert.Null(SkyPatcherDraft.Prepare(path, "ammo", SkyPatcherCatalog.Load(), out var plan));
        var warnings = new SkyPatcherOverlay.WarningSink();
        var folded = plan!.Fold(scan, SkyPatcherCatalog.Load(), _ => true, out var refusal, warnings);
        Assert.Null(refusal);
        Assert.True(Folder(folded, "ammo").PatchingEnabled);
        Assert.NotEmpty(SkyPatcherDiscovery.OrderedLines(Folder(folded, "ammo")));
        Assert.DoesNotContain(warnings.Kept, w => w.Contains("iEnable…Patching"));
    }

    // a draft folds in with the same order, gate and switch answers a placed file of its name gets
    [Fact]
    public void ADraftFoldsInWithThePlacedFileRules()
    {
        var live = Scan();
        var dir = Path.Combine(_root, "drafts", "weapon");
        Directory.CreateDirectory(dir);
        string Fold(string name, string sub, out SkyPatcherDiscovery.LayerScan folded)
        {
            var path = Path.Combine(dir, name);
            File.WriteAllText(path, "attackDamage=9");
            Assert.Null(SkyPatcherDraft.Prepare(path, sub, SkyPatcherCatalog.Load(), out var plan));
            folded = plan!.Fold(live, SkyPatcherCatalog.Load(), p => p.Equals("Gated.esp", StringComparison.OrdinalIgnoreCase), out var refusal);
            Assert.Null(refusal);
            return path;
        }
        Fold("m.ini", "weapon", out var withM);
        Assert.Equal(new[] { "Gated.esp.ini.bak", "LOUD.INI", "m.ini", "same.ini" },
            Folder(withM, "weapon").Files.Select(f => f.SortKey).SkipWhile(k => k != "Gated.esp.ini.bak").Take(4));
        Fold("Absent.esp.ini.bak", "weapon", out var withGate);
        Assert.Contains("Absent.esp", Assert.Single(Folder(withGate, "weapon").Files, f => f.SortKey == "Absent.esp.ini.bak").NotApplied);
        Fold("d.ini", "ammo", out var withAmmo);
        Assert.False(Folder(withAmmo, "ammo").PatchingEnabled);
        Assert.True(withAmmo.Folders.Select(f => f.Subfolder).ToList().IndexOf("ammo")
                    < withAmmo.Folders.Select(f => f.Subfolder).ToList().IndexOf("weapon"));
    }

    // Gated.esp.ini applies (its plugin is active)
    [Fact]
    public void AGatedIniAppliesWhenItsPluginIsPresent()
    {
        var gated = Assert.Single(Folder(Scan(), "weapon").Files, f => f.SortKey == "Gated.esp.ini");
        Assert.Null(gated.NotApplied);
        Assert.Equal("Gated.esp", gated.GatePlugin);
    }

    // Absent.esp.ini is NotApplied, reason names the gate; …but its content is still inspectable (parsed lines kept)
    [Fact]
    public void AGatedIniWhosePluginIsAbsentIsNotAppliedButStillParsed()
    {
        var absent = Assert.Single(Folder(Scan(), "weapon").Files, f => f.SortKey == "Absent.esp.ini");
        Assert.Contains("Absent.esp", absent.NotApplied);
        Assert.Contains(absent.Lines, l => l.Segments.Any(s => s.Key == "attackDamage"));
    }

    // ordered lines exclude gated-off files and keep file order (1-based line numbers)
    [Fact]
    public void OrderedLinesSkipGatedOffFilesAndStartAtLineOne()
    {
        var lines = SkyPatcherDiscovery.OrderedLines(Folder(Scan(), "weapon"));

        Assert.DoesNotContain(lines, l => l.File.EndsWith("Absent.esp.ini", StringComparison.OrdinalIgnoreCase));
        Assert.EndsWith("a.ini", lines[0].File, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, lines[0].LineNumber);
    }

    // SkyPatcher.ini [Patcher] toggle disables the npc folder; …and a disabled folder contributes NO ordered lines
    [Fact]
    public void TheToggleDisablesTheNpcFolder()
    {
        var scan = Scan();
        var npc = Folder(scan, "npc");

        Assert.False(npc.PatchingEnabled);
        Assert.Contains(scan.Notes, n => n.Contains("disables 'npc'"));
        Assert.Empty(SkyPatcherDiscovery.OrderedLines(npc));
    }

    // a root-level stray INI is excluded with a note
    [Fact]
    public void ARootLevelStrayIniIsExcludedWithANote()
    {
        var scan = Scan();
        Assert.Contains(scan.Notes, n => n.Contains("stray.ini") && n.Contains("NOT applied"));
        Assert.All(scan.Folders, f => Assert.DoesNotContain(f.Files, x => x.RelPath.EndsWith("stray.ini", StringComparison.OrdinalIgnoreCase)));
    }

    // an undocumented subfolder is listed with Catalog=null + a note
    [Fact]
    public void AnUndocumentedSubfolderIsListedWithNoCatalogAndANote()
    {
        var scan = Scan();
        Assert.Null(Folder(scan, "bogusType").Catalog);
        Assert.Contains(scan.Notes, n => n.Contains("bogusType"));
    }

    // an INI present ONLY inside a BSA is NotApplied (loose-only); …and it contributes NO ordered lines.
    // The probe skipped this arm when the fixture was absent; here a missing fixture fails.
    [Fact]
    public void AnIniOnlyInsideABsaIsNotApplied()
    {
        Assert.True(File.Exists(FixtureBsa), FixtureBsa);
        var weapon = Folder(Scan(new ActiveArchive(FixtureBsa, "PluginA.esp", PluginRank: 1)), "weapon");
        var bsaOnly = Assert.Single(weapon.Files, f => f.SortKey.Equals("bsaonly.ini", StringComparison.OrdinalIgnoreCase));

        Assert.Contains("BSA", bsaOnly.NotApplied);
        Assert.DoesNotContain(SkyPatcherDiscovery.OrderedLines(weapon), l => l.File.EndsWith("bsaonly.ini", StringComparison.OrdinalIgnoreCase));
    }
}

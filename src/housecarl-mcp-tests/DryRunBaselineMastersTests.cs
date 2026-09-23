using System.Text.Json;
using HousecarlGenerator;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The patch-lane dry run's would-be masters (Stryker row T09, the builder half): with Skyrim.esm and Update.esm in the
/// order, the preview lists both even though the edit references neither, as the real write's header will. The world
/// is built per test and written into its own temp folder; nothing is written by a dry run.
/// </summary>
[Trait("tier", "integration")]
public sealed class DryRunBaselineMastersTests : IDisposable
{
    const string BaseName = "HcBaselineBase.esp";

    readonly string _root;
    readonly string _priorCorpusPath;
    readonly LoadOrderService _svc;
    readonly FormKey _weapon;

    public DryRunBaselineMastersTests()
    {
        _priorCorpusPath = CorpusRulebook.CorpusPath;
        _root = Path.Combine(Path.GetTempPath(), "hc-dryrun-baseline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "game", "Data"));
        var instance = Path.Combine(_root, "inst");
        var mods = Path.Combine(instance, "mods");
        var vanilla = Path.Combine(mods, "VanillaStub");
        var baseDir = Path.Combine(mods, "BaselineBase");
        Directory.CreateDirectory(vanilla);
        Directory.CreateDirectory(baseDir);

        foreach (var name in new[] { "Skyrim", "Update" })
            new SkyrimMod(new ModKey(name, ModType.Master), SkyrimRelease.SkyrimSE)
                .BeginWrite.ToPath(Path.Combine(vanilla, name + ".esm")).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var mod = new SkyrimMod(new ModKey("HcBaselineBase", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var w = mod.Weapons.AddNew("HcBaselineSword");
        w.BasicStats = new WeaponBasicStats { Damage = 5, Weight = 1 };
        _weapon = w.FormKey;
        mod.BeginWrite.ToPath(Path.Combine(baseDir, BaseName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var genDir = Path.Combine(_root, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(_root, "corpus-ref"));
        CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\nSkyrim.esm\r\nUpdate.esm\r\n" + BaseName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + BaseName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+BaselineBase\r\n+VanillaStub\r\n");

        _svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "user.json")));
    }

    public void Dispose()
    {
        CorpusRulebook.CorpusPath = _priorCorpusPath;
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void APatchLaneDryRunListsSkyrimAndUpdateAsWouldBeMasters()
    {
        var r = ApplyTools.Apply(_svc,
            ops: JsonDocument.Parse($@"[{{""formid"":""{_weapon.ID:X6}:{BaseName}"",""field_path"":""BasicStats.Damage"",""op"":""Set"",""value"":""9""}}]").RootElement.Clone(),
            dry_run: true, format: "json");

        var masters = JsonDocument.Parse(r).RootElement.GetProperty("masters").EnumerateArray().Select(m => m.GetString()).ToList();
        Assert.Contains("Skyrim.esm", masters);
        Assert.Contains("Update.esm", masters);
    }
}

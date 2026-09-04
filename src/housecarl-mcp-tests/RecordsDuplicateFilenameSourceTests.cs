using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Addressing an off-order plugin by FILENAME when two mod folders provide that name: the read is refused
/// with both folders named rather than one of them guessed, and <c>mod=</c> says which copy to read.
///
/// <para>Its own instance — the shared records world has one plugin per mod folder, so there is no filename in it
/// for two folders to disagree about. It joins the records collection all the same, for the corpus that fixture
/// generates and points <c>CorpusRulebook.CorpusPath</c> at.</para></summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsDuplicateFilenameSourceTests : IDisposable
{
    const string DupeName = "HcDupe.esp";
    const ushort DamageA = 33, DamageB = 77;

    readonly string _root;
    readonly LoadOrderService _svc;

    public RecordsDuplicateFilenameSourceTests(RecordsFixture corpus)
    {
        _ = corpus;   // taken for the corpus it generates, not for its world

        _root = Path.Combine(Path.GetTempPath(), "hc-dupe-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "game", "Data"));

        var masterKey = new ModKey("HcDupeMaster", ModType.Master);
        var master = new SkyrimMod(masterKey, SkyrimRelease.SkyrimSE);
        var w = master.Weapons.AddNew();
        w.EditorID = "HcDupeW";
        w.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1 };

        var instance = Path.Combine(_root, "inst");
        var modsDir = Path.Combine(instance, "mods");
        foreach (var d in new[] { "MasterMod", "DupeA", "DupeB" }) Directory.CreateDirectory(Path.Combine(modsDir, d));

        master.BeginWrite.ToPath(Path.Combine(modsDir, "MasterMod", masterKey.FileName.String))
              .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        // The same filename in two switched-off folders, each carrying its own value for the same record.
        foreach (var (folder, damage) in new[] { ("DupeA", DamageA), ("DupeB", DamageB) })
        {
            var copy = new SkyrimMod(ModKey.FromNameAndExtension(DupeName), SkyrimRelease.SkyrimSE);
            ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(copy, master.Weapons.First()))
                .BasicStats = new WeaponBasicStats { Damage = damage, Weight = 1 };
            copy.BeginWrite.ToPath(Path.Combine(modsDir, folder, DupeName))
                .WithLoadOrder(new ISkyrimModGetter[] { master }).Write();
        }

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + masterKey.FileName.String + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + masterKey.FileName.String + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n-DupeB\r\n-DupeA\r\n+MasterMod\r\n");

        _svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "user.json")));
        _svc.Stats();
    }

    static JsonElement Source(string json) => JsonDocument.Parse(json).RootElement.Clone();

    string DamageOf(string source) =>
        RecordsTools.Records(_svc, source: Source(source), types: new[] { "WEAP" },
                             project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "BasicStats.Damage" } });

    [Fact]
    public void AFilenameTwoModFoldersProvideIsRefusedWithBothNamed()
    {
        var r = DamageOf("{\"file\": \"" + DupeName + "\"}");

        Assert.StartsWith("error:", r);
        Assert.Contains("DupeA", r);
        Assert.Contains("DupeB", r);
        // The refusal must hand over the disambiguating spelling, not just report the collision.
        Assert.Contains("\"mod\"", r);
        // And it must not have read either copy anyway.
        Assert.DoesNotContain("BasicStats.Damage = ", r);
    }

    [Theory]
    [InlineData("DupeA", DamageA)]
    [InlineData("DupeB", DamageB)]
    public void ModNamesWhichCopyIsRead(string mod, ushort damage)
    {
        var r = DamageOf("{\"file\": \"" + DupeName + "\", \"mod\": \"" + mod + "\"}");

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), "refused: " + r.Split('\n')[0]);
        Assert.Contains("BasicStats.Damage = " + damage, r);
    }

    public void Dispose()
    {
        _svc.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}

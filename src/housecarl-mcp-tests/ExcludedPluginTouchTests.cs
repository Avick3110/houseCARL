using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>#1206: a plugin excluded for a record Mutagen cannot parse still touches records. Its overrides are read
/// marked, apply refuses them, and a reference into its own records is classified as into it, not as undefined.</summary>
[Trait("tier", "integration")]
public sealed class ExcludedPluginTouchTests : IDisposable
{
    const string BadName = "HcExBad.esp";
    const string CleanName = "HcExClean.esp";

    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-excluded-touch-" + Guid.NewGuid().ToString("N"));
    readonly LoadOrderService _svc;
    readonly FormKey _overridden;   // the master's weapon the bad plugin overrides
    readonly FormKey _untouched;    // the master's keyword nothing else touches
    readonly FormKey _badOwn;       // a keyword the bad plugin defines

    public ExcludedPluginTouchTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "game", "Data"));
        var master = new SkyrimMod(new ModKey("HcExMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var weapon = master.Weapons.AddNew(); weapon.EditorID = "HcExWeapon";
        weapon.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1 };
        var kw = master.Keywords.AddNew(); kw.EditorID = "HcExKeyword";
        _overridden = weapon.FormKey; _untouched = kw.FormKey;

        var bad = new SkyrimMod(ModKey.FromNameAndExtension(BadName), SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(bad, weapon)).BasicStats = new WeaponBasicStats { Damage = 99, Weight = 1 };
        var badKw = bad.Keywords.AddNew(); badKw.EditorID = "HcExBadKeyword";
        _badOwn = badKw.FormKey;
        bad.Packages.AddNew().EditorID = "HcExBadPackage";

        var clean = new SkyrimMod(ModKey.FromNameAndExtension(CleanName), SkyrimRelease.SkyrimSE);
        var cw = clean.Weapons.AddNew(); cw.EditorID = "HcExCleanWeapon";
        cw.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>> { badKw.ToLink() };

        var mods = Path.Combine(_root, "inst", "mods");
        string Place(string mod, SkyrimMod m, params ISkyrimModGetter[] order)
        {
            Directory.CreateDirectory(Path.Combine(mods, mod));
            var path = Path.Combine(mods, mod, m.ModKey.FileName);
            m.BeginWrite.ToPath(path).WithLoadOrder(order).Write();
            return path;
        }
        Place("MasterMod", master);
        OverstatePkcuLength(Place("BadMod", bad, master));
        Place("CleanMod", clean, master, bad);

        var inst = Path.Combine(_root, "inst");
        File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(inst, "profiles", "Default");
        Directory.CreateDirectory(prof);
        var names = new[] { master.ModKey.FileName.String, BadName, CleanName };
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", names) + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), string.Join("", names.Select(n => "*" + n + "\r\n")));
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+CleanMod\r\n+BadMod\r\n+MasterMod\r\n");
        _svc = LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(_root, "user.json")));
    }

    public void Dispose()
    {
        _svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }

    /// <summary>Overstate the first PKCU subrecord's length so Mutagen throws mid-enumeration; the record headers stay intact.</summary>
    static void OverstatePkcuLength(string path)
    {
        var b = File.ReadAllBytes(path);
        for (int i = 0; i < b.Length - 6; i++)
        {
            if (b[i] != (byte)'P' || b[i + 1] != (byte)'K' || b[i + 2] != (byte)'C' || b[i + 3] != (byte)'U') continue;
            BitConverter.GetBytes((ushort)99).CopyTo(b, i + 4);
            File.WriteAllBytes(path, b);
            return;
        }
        Assert.Fail("Mutagen wrote no PKCU subrecord for the package");
    }

    static string Fid(FormKey fk) => FormIdToken.Of(fk);

    string Note => OrderDegraded.TouchNote(new[] { BadName });

    [Fact]
    public void TheBadPluginIsExcludedForAParseFailure()
    {
        var status = StatusTools.LoadOrderStatus(_svc, new ToolPathResolver(new UserConfigStore(Path.Combine(_root, "user.json"))), filter: BadName);
        Assert.Contains("EXCLUDED this session", status);
        Assert.DoesNotContain("every other plugin is unaffected", status);
    }

    [Fact]
    public void AReadOfARecordTheExcludedPluginOverridesIsMarked()
    {
        var tree = RecordsTools.Records(_svc, formids: new[] { Fid(_overridden) }, project: new() { form = "tree" });
        Assert.Contains("note: " + Note, tree);
        var fields = RecordsTools.Records(_svc, formids: new[] { Fid(_overridden) },
                                          project: new() { form = "fields", fields = new[] { "BasicStats.Damage" } }, format: "json");
        var row = JsonDocument.Parse(fields).RootElement.GetProperty("records")[0];
        Assert.Equal(Note, row.GetProperty("excluded_note").GetString());

        var control = RecordsTools.Records(_svc, formids: new[] { Fid(_untouched) }, project: new() { form = "tree" });
        Assert.DoesNotContain("also touched by", control);
    }

    [Fact]
    public void ADefaultLaneWriteToARecordTheExcludedPluginOverridesIsRefused()
    {
        string Set(FormKey fk, string path) => ApplyTools.Apply(_svc, dry_run: true,
            ops: JsonDocument.Parse("[{\"formid\":\"" + Fid(fk) + "\",\"field_path\":\"" + path + "\",\"op\":\"Set\",\"value\":\"12\"}]").RootElement);

        var refused = Set(_overridden, "BasicStats.Damage");
        Assert.Contains(OrderDegraded.TouchRefusal(Fid(_overridden), new[] { BadName }), refused);

        Assert.DoesNotContain("also touched by", Set(_untouched, "EditorID"));
    }

    [Fact]
    public void AReferenceIntoTheExcludedPluginsOwnRecordIsClassifiedAsIntoIt()
    {
        var text = CheckTools.CheckTool(_svc, plugins: new[] { CleanName }, findings: new[] { "errors" });
        Assert.Contains($"-> {_badOwn}   [target in excluded plugin {BadName}, which houseCARL could not read]", text);
        Assert.DoesNotContain("[target not defined by any active plugin]", text);
    }
}

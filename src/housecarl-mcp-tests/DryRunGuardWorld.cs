using System.Security.Cryptography;
using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcpTests;

/// <summary>
/// The world the former <c>dry-run-guard</c> probe built, one per test: a master with two weapons and a magic effect,
/// and a user override of the first weapon, in a synthetic MO2 instance in its own temp folder, driven through the
/// full service path (the output resolver, the write gate, the in-place consent store).
/// </summary>
sealed class DryRunGuardWorld : IDisposable
{
    public const string MasterFile = "HcDryMaster.esm";
    public const string UserFile = "HcDryUser.esp";

    public readonly string Root, Instance, Mods, UserPath, StorePath, ManifestDir;
    public readonly string Fid, Fid2, FidMg;
    public readonly LoadOrderService Svc;

    public DryRunGuardWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-dryrun-guard-" + Guid.NewGuid().ToString("N"));
        Instance = Path.Combine(Root, "instance");
        Mods = Path.Combine(Instance, "mods");
        ManifestDir = Path.Combine(Root, "manifests");
        var profiles = Path.Combine(Instance, "profiles", "Default");
        Directory.CreateDirectory(profiles);
        Directory.CreateDirectory(ManifestDir);
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));
        File.WriteAllText(Path.Combine(Instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        var mKey = ModKey.FromFileName(MasterFile);
        var masterDir = Path.Combine(Mods, "MasterMod");
        var userDir = Path.Combine(Mods, "UserMod");
        Directory.CreateDirectory(masterDir);
        Directory.CreateDirectory(userDir);
        var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
        var w = m.Weapons.AddNew(); w.EditorID = "HcDryWeap"; w.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1 };
        var w2 = m.Weapons.AddNew(); w2.EditorID = "HcDryWeap2"; w2.BasicStats = new WeaponBasicStats { Damage = 5 };
        var mg = m.MagicEffects.AddNew(); mg.EditorID = "HcDryMgef";
        var masterPath = Path.Combine(masterDir, MasterFile);
        m.BeginWrite.ToPath(masterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        UserPath = Path.Combine(userDir, UserFile);
        using (var mOv = SkyrimMod.CreateFromBinaryOverlay(masterPath, SkyrimRelease.SkyrimSE))
        {
            var u = new SkyrimMod(ModKey.FromFileName(UserFile), SkyrimRelease.SkyrimSE);
            var uw = u.Weapons.GetOrAddAsOverride(mOv.Weapons.First(x => x.FormKey == w.FormKey));
            uw.BasicStats!.Damage = 20;
            u.BeginWrite.ToPath(UserPath).WithLoadOrder(new ISkyrimModGetter[] { mOv }).Write();
        }
        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + MasterFile + "\r\n" + UserFile + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + MasterFile + "\r\n*" + UserFile + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+UserMod\r\n+MasterMod\r\n");

        Fid = $"{w.FormKey.ID:X6}:{MasterFile}";
        Fid2 = $"{w2.FormKey.ID:X6}:{MasterFile}";
        FidMg = $"{mg.FormKey.ID:X6}:{MasterFile}";
        StorePath = Path.Combine(Root, "houseCARL.user.json");
        Svc = LoadOrderService.WithInstance(Instance, 0, new UserConfigStore(StorePath));
        Svc.Stats();   // warm the index once, so a snapshot taken after it sees only what the call under test writes
    }

    public static BulkOp DamageOp(string fid, int dmg) =>
        new() { Formid = fid, FieldPath = "BasicStats.Damage", Verb = "Set", Value = dmg.ToString() };

    /// <summary>The instance tree (every mod folder, rider, marker, .seq, profile file) and the consent store: each
    /// directory and each file with its content hash. Equal before and after means the call wrote nothing.</summary>
    public SortedDictionary<string, string> Snapshot()
    {
        var snap = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in Directory.EnumerateDirectories(Instance, "*", SearchOption.AllDirectories))
            snap[Path.GetRelativePath(Root, d) + "/"] = "dir";
        foreach (var f in Directory.EnumerateFiles(Instance, "*", SearchOption.AllDirectories).Append(StorePath).Where(File.Exists))
            snap[Path.GetRelativePath(Root, f)] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)));
        return snap;
    }

    public string[] ModFolders() => Directory.GetDirectories(Mods).Select(p => Path.GetFileName(p)!).OrderBy(x => x).ToArray();

    /// <summary>A manifest file outside the instance, so writing it never shows in a <see cref="Snapshot"/>.</summary>
    public string Manifest(string name, string content)
    {
        var p = Path.Combine(ManifestDir, name);
        File.WriteAllText(p, content);
        return p;
    }

    public static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    public static JsonElement AtPath(string path) => Json("\"@" + path.Replace("\\", "\\\\") + "\"");

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

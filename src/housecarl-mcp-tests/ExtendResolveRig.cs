using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>One MO2 instance holding one master with one weapon, for the into= extend resolver: the seed patch, a
/// renamed patch folder, owned and un-owned folders. Each test builds its own, since every one adds mod folders.
/// Built from the extend-resolve-guard probe's synthetic instance.</summary>
sealed class ExtendResolveRig : IDisposable
{
    public ScratchMo2 Mo2 { get; } = new("hc-extend-resolve-");
    public LoadOrderService Svc { get; }
    public ModKey MasterKey { get; } = new("HcExtMaster", ModType.Master);
    public string MasterPath { get; }
    public FormKey Weapon { get; }
    public string Fid => ScratchMo2.Fid(Weapon);
    public string ModsDir => Mo2.ModsDir;

    public ExtendResolveRig()
    {
        MasterPath = Mo2.InMod("MasterMod", MasterKey);
        var m = new SkyrimMod(MasterKey, SkyrimRelease.SkyrimSE);
        var w = m.Weapons.AddNew();
        w.EditorID = "HcExtWeap";
        w.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1 };
        Weapon = w.FormKey;
        m.BeginWrite.ToPath(MasterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        Mo2.Profile(MasterKey.FileName + "\r\n", "*" + MasterKey.FileName + "\r\n", "+MasterMod\r\n");
        Svc = Mo2.Open();
    }

    public BulkOp Dmg(int v) => new() { Formid = Fid, FieldPath = "BasicStats.Damage", Verb = "Set", Value = v.ToString() };
    public BulkOp Wgt(int v) => new() { Formid = Fid, FieldPath = "BasicStats.Weight", Verb = "Set", Value = v.ToString() };

    public WritePatchBuilder.PatchOutcome Into(string into, BulkOp op) => Svc.ApplyEdits(new[] { op }, null, into);

    /// <summary>The seed patch "houseCARL - SeedA\SeedA.esp" carrying Damage=50, created fresh.</summary>
    public string Seed()
    {
        var seed = Svc.ApplyEdits(new[] { Dmg(50) }, "SeedA", null);
        if (!seed.Success) throw new InvalidOperationException("seed patch not created: " + seed.Error);
        return seed.OutputPath;
    }

    /// <summary>The seed patch, then its mod folder renamed to "houseCARL - SeedA Renamed"; SeedA.esp keeps its name.</summary>
    public string SeedRenamed()
    {
        Seed();
        var renamed = Path.Combine(ModsDir, "houseCARL - SeedA Renamed");
        Directory.Move(Path.Combine(ModsDir, "houseCARL - SeedA"), renamed);
        return renamed;
    }

    public string MarkOwned(string folderName, string plugin)
    {
        var dir = Path.Combine(ModsDir, folderName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "meta.ini"), $"{HousecarlOwnerMeta.Section}\r\ngenerated=true\r\nplugin={plugin}\r\n");
        return dir;
    }

    /// <summary>An owned folder holding real, empty plugins of the given stems.</summary>
    public string OwnedWithPlugins(string folderName, params string[] stems)
    {
        var dir = MarkOwned(folderName, stems.Length > 0 ? stems[0] + ".esp" : "");
        foreach (var n in stems) WritePlugin(Path.Combine(dir, n + ".esp"));
        return dir;
    }

    public static void WritePlugin(string path) =>
        new SkyrimMod(ModKey.FromFileName(Path.GetFileName(path)), SkyrimRelease.SkyrimSE)
            .BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

    public (ushort? dmg, float? wgt) ReadWeapon(string espPath)
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE);
        var w = ov.Weapons.FirstOrDefault(x => x.FormKey == Weapon);
        return (w?.BasicStats?.Damage, w?.BasicStats?.Weight);
    }

    /// <summary>The folder and file the path ends in, for "wrote into houseCARL - X\X.esp".</summary>
    public static (string folder, string file) Tail(string path) =>
        (Path.GetFileName(Path.GetDirectoryName(path)) ?? "", Path.GetFileName(path));

    /// <summary>One terminating period and no sentence break inside: a plugin basename's dot is followed by a letter.</summary>
    public static bool OneSentence(string s) =>
        s.EndsWith('.') && !s.AsSpan(0, s.Length - 1).Contains(". ", StringComparison.Ordinal);

    /// <summary>The into= candidates a refusal offers, in the order it names them.</summary>
    public static List<string> Candidates(string s) =>
        Regex.Matches(s, "into=\"[^\"]*\"").Select(m => m.Value).ToList();

    public static string RiderRefusal(Action call) => Assert.Throws<InvalidOperationException>(call).Message;

    public void Dispose()
    {
        Svc.Dispose();
        Mo2.Delete();
    }
}

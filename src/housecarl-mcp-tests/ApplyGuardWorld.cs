using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;

namespace HousecarlMcpTests;

/// <summary>The apply-guard probe's synthetic order, one per test: a master, a replacer that wins the subject weapon
/// (Damage 99, no keywords) and overrides the donor weapon (Damage 7; the master's is 42), two potions and a faction
/// the replacer owns, and an armor for the cross-type refusal.</summary>
public sealed class ApplyGuardWorld : IDisposable
{
    readonly string _root;
    public LoadOrderService Svc { get; }
    public string SubjectFid { get; }
    public string DonorWeaponFid { get; }
    public string ArmorFid { get; }
    public string FactionFid { get; }
    public string KeywordFid { get; }
    public string PotionAFid { get; }
    public string PotionBFid { get; }
    public string ModsDir { get; }
    public string ReplacerPath { get; }
    public string MasterName { get; }
    public string ReplacerName { get; }
    public string Root => _root;
    public FormKey SubjectKey { get; }
    public FormKey DonorKey { get; }
    public FormKey PotionAKey { get; }
    public FormKey PotionBKey { get; }

    public ApplyGuardWorld()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-applyguard-" + Guid.NewGuid().ToString("N"));
        string instance = Path.Combine(_root, "instance");
        string profiles = Path.Combine(instance, "profiles", "Default");
        ModsDir = Path.Combine(instance, "mods");
        Directory.CreateDirectory(profiles); Directory.CreateDirectory(ModsDir);
        Directory.CreateDirectory(Path.Combine(_root, "game", "Data"));
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");

        var mKey = new ModKey("HcApplyMaster", ModType.Master);
        var rKey = new ModKey("HcApplyRepl", ModType.Plugin);
        var masterPath = Path.Combine(ModsDir, "ApplyMaster", mKey.FileName.String);
        ReplacerPath = Path.Combine(ModsDir, "ApplyRepl", rKey.FileName.String);
        Directory.CreateDirectory(Path.GetDirectoryName(masterPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(ReplacerPath)!);

        var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
        var k1 = m.Keywords.AddNew(); k1.EditorID = "ApKw1";
        var k2 = m.Keywords.AddNew(); k2.EditorID = "ApKw2";

        var subject = m.Weapons.AddNew();
        subject.EditorID = "ApSubject";
        subject.Name = "Master Sword";
        subject.BasicStats = new WeaponBasicStats { Damage = 10 };

        var donor = m.Weapons.AddNew();
        donor.EditorID = "ApDonor";
        donor.Name = "Donor Sword";
        donor.BasicStats = new WeaponBasicStats { Damage = 42 };
        donor.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>>
            { new FormLink<IKeywordGetter>(k1.FormKey), new FormLink<IKeywordGetter>(k2.FormKey) };

        var mg = m.MagicEffects.AddNew(); mg.EditorID = "ApMgef";
        var potA = m.Ingestibles.AddNew(); potA.EditorID = "ApPotionA";
        var eA = new Effect { Data = new EffectData { Magnitude = 5 } }; eA.BaseEffect.SetTo(mg.FormKey);
        potA.Effects.Add(eA);
        var potB = m.Ingestibles.AddNew(); potB.EditorID = "ApPotionB";
        var eB = new Effect { Data = new EffectData { Magnitude = 1 } }; eB.BaseEffect.SetTo(mg.FormKey);
        potB.Effects.Add(eB);

        var armor = m.Armors.AddNew();
        armor.EditorID = "ApArmor";
        armor.Name = "Some Cuirass";

        var faction = m.Factions.AddNew();
        faction.EditorID = "ApFaction";

        m.BeginWrite.ToPath(masterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var r = new SkyrimMod(rKey, SkyrimRelease.SkyrimSE);
        var rw = (IWeapon)WriteEngine.GenericGetOrAddAsOverride(r, subject);
        rw.Name = "Winner Sword";
        rw.BasicStats = new WeaponBasicStats { Damage = 99 };
        rw.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>>();
        var rd = (IWeapon)WriteEngine.GenericGetOrAddAsOverride(r, donor);
        rd.BasicStats = new WeaponBasicStats { Damage = 7 };
        WriteEngine.GenericGetOrAddAsOverride(r, potA);
        WriteEngine.GenericGetOrAddAsOverride(r, potB);
        WriteEngine.GenericGetOrAddAsOverride(r, faction);
        r.BeginWrite.ToPath(ReplacerPath).WithLoadOrder(new ISkyrimModGetter[] { m }).Write();

        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + mKey.FileName + "\r\n" + rKey.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + mKey.FileName + "\r\n*" + rKey.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+ApplyRepl\r\n+ApplyMaster\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "houseCARL.user.json")));
        Svc.Stats();

        string Fid(FormKey fk) => $"{fk.ID:X6}:{mKey.FileName}";
        SubjectFid = Fid(subject.FormKey);
        DonorWeaponFid = Fid(donor.FormKey);
        ArmorFid = Fid(armor.FormKey);
        FactionFid = Fid(faction.FormKey);
        KeywordFid = Fid(k1.FormKey);
        PotionAFid = Fid(potA.FormKey);
        PotionBFid = Fid(potB.FormKey);
        MasterName = mKey.FileName.String;
        ReplacerName = rKey.FileName.String;
        SubjectKey = subject.FormKey;
        DonorKey = donor.FormKey;
        PotionAKey = potA.FormKey;
        PotionBKey = potB.FormKey;
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }

    public static JsonElement Je(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>A JSON string literal for <c>"@path"</c>, escaping the path's backslashes.</summary>
    public static string AtPath(string path) => "\"@" + path.Replace("\\", "\\\\") + "\"";

    /// <summary>One <c>Set BasicStats.Damage</c> op on the subject.</summary>
    public string DamageOp(string v) =>
        "[{\"formid\":\"" + SubjectFid + "\",\"field_path\":\"BasicStats.Damage\",\"value\":\"" + v + "\"}]";

    /// <summary>One <c>Add Ranks</c> compose op on the faction, as a one-element array.</summary>
    public string ComposeRankOp(string? extra) =>
        "[{\"formid\":\"" + FactionFid + "\",\"field_path\":\"Ranks\",\"op\":\"Add\",\"compose\":{\"type\":\"Rank\""
        + (extra is null ? "" : "," + extra) + "}}]";

    /// <summary>Several <c>Add Ranks</c> ops with the given Numbers, in one array.</summary>
    public string AddRanks(params string[] numbers) =>
        "[" + string.Join(",", numbers.Select(n => ComposeRankOp("\"fields\":{\"Number\":\"" + n + "\"}")[1..^1])) + "]";

    /// <summary>The written patch's path, parsed out of the text render's first line and its "mod folder:" clause.</summary>
    public string? PatchPathFrom(string render)
    {
        if (!render.StartsWith("wrote ", StringComparison.Ordinal) && !render.StartsWith("extended ", StringComparison.Ordinal)) return null;
        var file = render[(render.IndexOf(' ') + 1)..];
        file = file[..file.IndexOf(' ')];
        var mod = render.Contains("mod folder: ", StringComparison.Ordinal)
            ? render[(render.IndexOf("mod folder: ", StringComparison.Ordinal) + 12)..].Split('\n')[0].Split("  ")[0].Trim()
            : null;
        return mod is null ? null : Path.Combine(ModsDir, mod, file);
    }

    static T Read<T>(string espPath, Func<ISkyrimModGetter, T> read, T failed)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE);
            return read(ov);
        }
        catch { return failed; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>A weapon read back off a plugin: (damage, name, keyword count).</summary>
    public static (ushort? Dmg, string? Name, int? Kw) ReadWeapon(string? espPath, FormKey fk) =>
        espPath is null ? (null, null, null) : Read<(ushort?, string?, int?)>(espPath, ov =>
        {
            var w = ov.Weapons.FirstOrDefault(x => x.FormKey == fk);
            return (w?.BasicStats?.Damage, w?.Name?.String, w?.Keywords?.Count);
        }, (null, null, null));

    public static int? CountEffects(string? espPath, FormKey fk) =>
        espPath is null ? null : Read<int?>(espPath, ov => ov.Ingestibles.FirstOrDefault(x => x.FormKey == fk)?.Effects?.Count, null);

    /// <summary>How many Ranks the faction carries in the replacer on disk.</summary>
    public int RanksOnDisk()
    {
        var fk = FormKey.Factory(FactionFid);
        return Read(ReplacerPath, ov => ov.Factions.FirstOrDefault(f => f.FormKey == fk)?.Ranks.Count ?? -1, -1);
    }

    /// <summary>The two potions' first-effect magnitudes in the replacer on disk.</summary>
    public (ushort? A, ushort? B) PotionMagnitudes() =>
        Read<(ushort?, ushort?)>(ReplacerPath, ov =>
        {
            ushort? Mag(FormKey fk) => (ushort?)ov.Ingestibles.FirstOrDefault(x => x.FormKey == fk)?
                .Effects.FirstOrDefault()?.Data?.Magnitude;
            return (Mag(PotionAKey), Mag(PotionBKey));
        }, (null, null));

    /// <summary>Each op's <c>landed_source</c> in a json apply render, "?" where absent.</summary>
    public static List<string> LandedSources(string json)
    {
        var states = new List<string>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("ops", out var arr))
            foreach (var op in arr.EnumerateArray())
                states.Add(op.TryGetProperty("landed_source", out var v) ? v.GetString() ?? "?" : "?");
        return states;
    }
}

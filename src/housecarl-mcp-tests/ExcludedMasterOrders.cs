using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlMcpTests;

/// <summary>The orders for #314: each holds one active plugin truncated so Mutagen cannot OPEN it. An unopenable
/// plugin poisons every write in the order it sits in, so each arm gets its own order and no other test shares one.</summary>
public abstract class ExcludedMasterOrder : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "hc-x314-" + Guid.NewGuid().ToString("N"));
    public string Instance => Path.Combine(Root, "instance");
    public string Mods => Path.Combine(Instance, "mods");
    public LoadOrderService Svc { get; private set; } = null!;

    protected ExcludedMasterOrder()
    {
        Directory.CreateDirectory(Path.Combine(Instance, "profiles", "Default"));
        Directory.CreateDirectory(Mods);
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));
        File.WriteAllText(Path.Combine(Instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
    }

    /// <summary>Write the profile for an order given in LOAD order, then open the service over it.</summary>
    protected void Open(params (string folder, ModKey key)[] order)
    {
        var p = Path.Combine(Instance, "profiles", "Default");
        File.WriteAllText(Path.Combine(p, "loadorder.txt"), "# header\r\n" + string.Concat(order.Select(o => o.key.FileName + "\r\n")));
        File.WriteAllText(Path.Combine(p, "plugins.txt"), string.Concat(order.Select(o => "*" + o.key.FileName + "\r\n")));
        File.WriteAllText(Path.Combine(p, "modlist.txt"),
            "# header\r\n" + string.Concat(Enumerable.Reverse(order).Select(o => "+" + o.folder + "\r\n")));
        Svc = LoadOrderService.WithInstance(Instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
        Svc.Stats();
    }

    protected string WriteMod(string folder, ModKey k, SkyrimMod m, bool keepNextFormId, params ISkyrimModGetter[] lo)
    {
        var p = Path.Combine(Mods, folder, k.FileName.String);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var w = m.BeginWrite.ToPath(p).WithLoadOrder(lo);
        if (keepNextFormId) w.NoNextFormIDProcessing().Write(); else w.Write();
        return p;
    }

    /// <summary>A valid header followed by a truncated body: the could-not-be-OPENED exclusion class.</summary>
    protected static void Truncate(string path)
    {
        var whole = File.ReadAllBytes(path);
        File.WriteAllBytes(path, whole[..(whole.Length - 12)]);
    }

    protected static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

    public void Dispose()
    {
        Svc?.Dispose();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>No baselines: a master, the broken plugin (overrides the master's weapon, originates its own), and a clean
/// plugin that wins both and declares the broken one as a master.</summary>
public sealed class ExcludedMasterMainOrder : ExcludedMasterOrder
{
    public const string MasterName = "HcXMaster.esm";
    public const string CleanName = "HcXClean.esp";
    public const string BrokenName = "HcXBroken.esp";
    public string SubjectFid { get; }
    public string BrokenOwnFid { get; }

    public ExcludedMasterMainOrder()
    {
        var mKey = new ModKey("HcXMaster", ModType.Master);
        var bKey = new ModKey("HcXBroken", ModType.Plugin);
        var cKey = new ModKey("HcXClean", ModType.Plugin);

        var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
        var subject = m.Weapons.AddNew();
        subject.EditorID = "XSubject";
        subject.BasicStats = new WeaponBasicStats { Damage = 10 };
        WriteMod("XMaster", mKey, m, false);

        var b = new SkyrimMod(bKey, SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(b, subject)).BasicStats = new WeaponBasicStats { Damage = 30 };
        var brokenOwn = b.Weapons.AddNew();
        brokenOwn.EditorID = "XBrokenOwn";
        brokenOwn.BasicStats = new WeaponBasicStats { Damage = 40 };
        var brokenPath = WriteMod("XBroken", bKey, b, false, m);

        var c = new SkyrimMod(cKey, SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(c, subject)).BasicStats = new WeaponBasicStats { Damage = 20 };
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(c, brokenOwn)).BasicStats = new WeaponBasicStats { Damage = 41 };
        WriteMod("XClean", cKey, c, false, m, b);

        Truncate(brokenPath);
        Open(("XMaster", mKey), ("XBroken", bKey), ("XClean", cKey));
        SubjectFid = Fid(subject.FormKey);
        BrokenOwnFid = Fid(brokenOwn.FormKey);
    }
}

/// <summary>The order a real user has: Skyrim.esm and Update.esm present and openable, plus one broken plugin that a
/// clean plugin overrides.</summary>
public sealed class ExcludedMasterRealOrder : ExcludedMasterOrder
{
    public const string BrokenName = "SxBroken.esp";
    public const string CleanName = "SxClean.esp";
    public string SubjectFid { get; }
    public string BrokenOwnFid { get; }

    public ExcludedMasterRealOrder()
    {
        var skyKey = new ModKey("Skyrim", ModType.Master);
        var updKey = new ModKey("Update", ModType.Master);
        var brkKey = new ModKey("SxBroken", ModType.Plugin);
        var clnKey = new ModKey("SxClean", ModType.Plugin);

        var sky = new SkyrimMod(skyKey, SkyrimRelease.SkyrimSE);
        var subject = sky.Weapons.AddNew();
        subject.EditorID = "SxSubject";
        subject.BasicStats = new WeaponBasicStats { Damage = 10 };
        WriteMod("SxSky", skyKey, sky, false);
        WriteMod("SxUpd", updKey, new SkyrimMod(updKey, SkyrimRelease.SkyrimSE), false, sky);

        var brk = new SkyrimMod(brkKey, SkyrimRelease.SkyrimSE);
        var brkOwn = brk.Weapons.AddNew();
        brkOwn.EditorID = "SxBrokenOwn";
        brkOwn.BasicStats = new WeaponBasicStats { Damage = 40 };
        var brkPath = WriteMod("SxBroken", brkKey, brk, false, sky);

        var cln = new SkyrimMod(clnKey, SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(cln, brkOwn)).BasicStats = new WeaponBasicStats { Damage = 41 };
        WriteMod("SxClean", clnKey, cln, false, sky, brk);

        Truncate(brkPath);
        Open(("SxSky", skyKey), ("SxUpd", updKey), ("SxBroken", brkKey), ("SxClean", clnKey));
        SubjectFid = Fid(subject.FormKey);
        BrokenOwnFid = Fid(brkOwn.FormKey);
    }
}

/// <summary>A broken Skyrim.esm (the real baseline name: the force-include matches on ModKey) and a clean plugin that
/// overrides its weapon and holds an NPC donor with one head part, for the closure copy.</summary>
public sealed class ExcludedMasterBaselineOrder : ExcludedMasterOrder
{
    public string SubjectFid { get; }
    public string DonorFid { get; }

    public ExcludedMasterBaselineOrder()
    {
        var skyKey = new ModKey("Skyrim", ModType.Master);
        var clnKey = new ModKey("BlClean", ModType.Plugin);

        var sky = new SkyrimMod(skyKey, SkyrimRelease.SkyrimSE);
        var w = sky.Weapons.AddNew();
        w.EditorID = "BlSubject";
        w.BasicStats = new WeaponBasicStats { Damage = 10 };
        var skyPath = WriteMod("BlSky", skyKey, sky, false);
        Truncate(skyPath);

        var cln = new SkyrimMod(clnKey, SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(cln, w)).BasicStats = new WeaponBasicStats { Damage = 20 };
        var donor = cln.Npcs.AddNew();
        donor.EditorID = "BlDonor";
        var donorHair = cln.HeadParts.AddNew();
        donorHair.EditorID = "BlDonorHair";
        donor.HeadParts.Add(donorHair.FormKey);
        WriteMod("BlClean", clnKey, cln, false, sky);

        Open(("BlSky", skyKey), ("BlClean", clnKey));
        SubjectFid = Fid(w.FormKey);
        DonorFid = Fid(donor.FormKey);
    }
}

/// <summary>#316 (1): a compact subject that declares the broken plugin as a master. The compact runs once here and
/// its outcome (or the exception it let escape) and the mod-folder count either side of it are kept for the tests.</summary>
public sealed class ExcludedMasterCompactOutcome : ExcludedMasterOrder
{
    public const string BrokenName = "HcXCmBroken.esp";
    public WritePatchBuilder.CompactOutcome? Outcome { get; }
    public Exception? Escaped { get; }
    public int FoldersBefore { get; }
    public int FoldersAfter { get; }

    public ExcludedMasterCompactOutcome()
    {
        var brkKey = new ModKey("HcXCmBroken", ModType.Plugin);
        var subKey = new ModKey("HcXCmSubject", ModType.Plugin);

        var brk = new SkyrimMod(brkKey, SkyrimRelease.SkyrimSE);
        var brkOwn = brk.Weapons.AddNew(); brkOwn.EditorID = "CmBrokenOwn";
        brkOwn.BasicStats = new WeaponBasicStats { Damage = 40 };
        var brkPath = WriteMod("CmBroken", brkKey, brk, true);

        var sub = new SkyrimMod(subKey, SkyrimRelease.SkyrimSE);
        if (sub.ModHeader.Stats.NextFormID < 0x800) sub.ModHeader.Stats.NextFormID = 0x800;
        var subOwn = sub.Weapons.AddNew(); subOwn.EditorID = "CmSubjectOwn";
        subOwn.BasicStats = new WeaponBasicStats { Damage = 15 };
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(sub, brkOwn)).BasicStats = new WeaponBasicStats { Damage = 41 };
        WriteMod("CmSubject", subKey, sub, true, brk);

        Truncate(brkPath);
        Open(("CmBroken", brkKey), ("CmSubject", subKey));

        FoldersBefore = Directory.GetDirectories(Mods).Length;
        try
        {
            Outcome = Svc.CompactPlugin(subKey.FileName.String, esl: true, inPlace: false, repointExternals: false,
                acknowledge: false, patchName: null);
        }
        catch (Exception ex) { Escaped = ex; }
        FoldersAfter = Directory.GetDirectories(Mods).Length;
    }
}

/// <summary>#316 (2): an in-place compact with repoint, whose one external referencer links the target's record and
/// declares the broken plugin as a master. The call runs once here, through the tool.</summary>
public sealed class ExcludedMasterRepointOutcome : ExcludedMasterOrder
{
    public const string BrokenName = "HcXRpBroken.esp";
    public const string ExternalName = "HcXRpExternal.esp";
    public string Render { get; }
    public bool TargetRewritten { get; }

    public ExcludedMasterRepointOutcome()
    {
        var brkKey = new ModKey("HcXRpBroken", ModType.Plugin);
        var tgtKey = new ModKey("HcXRpTarget", ModType.Plugin);
        var extKey = new ModKey("HcXRpExternal", ModType.Plugin);

        var brk = new SkyrimMod(brkKey, SkyrimRelease.SkyrimSE);
        var brkOwn = brk.Weapons.AddNew(); brkOwn.EditorID = "RpBrokenOwn";
        brkOwn.BasicStats = new WeaponBasicStats { Damage = 40 };
        var brkPath = WriteMod("RpBroken", brkKey, brk, true);

        var tgt = new SkyrimMod(tgtKey, SkyrimRelease.SkyrimSE);
        if (tgt.ModHeader.Stats.NextFormID < 0x800) tgt.ModHeader.Stats.NextFormID = 0x800;
        var tgtOwn = tgt.Weapons.AddNew(); tgtOwn.EditorID = "RpTargetOwn";
        tgtOwn.BasicStats = new WeaponBasicStats { Damage = 15 };
        var tgtPath = WriteMod("RpTarget", tgtKey, tgt, true);

        // A LINK into the target (an override never gates the repoint) plus an override of the broken plugin's record.
        var ext = new SkyrimMod(extKey, SkyrimRelease.SkyrimSE);
        if (ext.ModHeader.Stats.NextFormID < 0x800) ext.ModHeader.Stats.NextFormID = 0x800;
        var lvl = ext.LeveledItems.AddNew(); lvl.EditorID = "RpExtList";
        lvl.Entries = new Noggog.ExtendedList<LeveledItemEntry>
        {
            new LeveledItemEntry { Data = new LeveledItemEntryData { Level = 1, Count = 1, Reference = tgtOwn.ToLink<IItemGetter>() } },
        };
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(ext, brkOwn)).BasicStats = new WeaponBasicStats { Damage = 41 };
        WriteMod("RpExternal", extKey, ext, true, brk, tgt);

        Truncate(brkPath);
        Open(("RpBroken", brkKey), ("RpTarget", tgtKey), ("RpExternal", extKey));

        var before = File.ReadAllBytes(tgtPath);
        Render = WriteTools.CompactPlugin(Svc, source: tgtKey.FileName.String, esl: true, in_place: true,
            repoint_externals: true, acknowledge: true);
        TargetRewritten = !File.ReadAllBytes(tgtPath).AsSpan().SequenceEqual(before);
    }
}

/// <summary>#316 (3): the in-place lane's single-master threshold. One plugin overrides only the broken plugin's record
/// (header {broken}); the other also overrides an openable master's (a two-entry header that must be sorted).</summary>
public sealed class ExcludedMasterInPlaceOrder : ExcludedMasterOrder
{
    public const string BrokenName = "HcXIpBroken.esp";
    public const string OneName = "HcXIpOne.esp";
    public const string TwoName = "HcXIpTwo.esp";
    public string BrokenOwnFid { get; }
    public FormKey BrokenOwn { get; }
    public string OnePath => Path.Combine(Mods, "IpOne", OneName);

    public ExcludedMasterInPlaceOrder()
    {
        var mstKey = new ModKey("HcXIpMaster", ModType.Master);
        var brkKey = new ModKey("HcXIpBroken", ModType.Plugin);
        var oneKey = new ModKey("HcXIpOne", ModType.Plugin);
        var twoKey = new ModKey("HcXIpTwo", ModType.Plugin);

        var mst = new SkyrimMod(mstKey, SkyrimRelease.SkyrimSE);
        var mstOwn = mst.Weapons.AddNew(); mstOwn.EditorID = "IpMasterOwn";
        mstOwn.BasicStats = new WeaponBasicStats { Damage = 10 };
        WriteMod("IpMaster", mstKey, mst, true);

        var brk = new SkyrimMod(brkKey, SkyrimRelease.SkyrimSE);
        var brkOwn = brk.Weapons.AddNew(); brkOwn.EditorID = "IpBrokenOwn";
        brkOwn.BasicStats = new WeaponBasicStats { Damage = 40 };
        var brkPath = WriteMod("IpBroken", brkKey, brk, true);

        var one = new SkyrimMod(oneKey, SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(one, brkOwn)).BasicStats = new WeaponBasicStats { Damage = 41 };
        WriteMod("IpOne", oneKey, one, true, brk);

        var two = new SkyrimMod(twoKey, SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(two, brkOwn)).BasicStats = new WeaponBasicStats { Damage = 42 };
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(two, mstOwn)).BasicStats = new WeaponBasicStats { Damage = 11 };
        WriteMod("IpTwo", twoKey, two, true, mst, brk);

        Truncate(brkPath);
        Open(("IpMaster", mstKey), ("IpBroken", brkKey), ("IpOne", oneKey), ("IpTwo", twoKey));
        BrokenOwn = brkOwn.FormKey;
        BrokenOwnFid = Fid(brkOwn.FormKey);
    }

    /// <summary>One weapon's Damage read straight off a written plugin; null when it cannot be read.</summary>
    public static ushort? DamageOnDisk(string espPath, FormKey fk)
    {
        try
        {
            using var ov = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE);
            return ov.Weapons.FirstOrDefault(w => w.FormKey == fk)?.BasicStats?.Damage;
        }
        catch (Exception) { return null; }
    }
}

using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;

namespace HousecarlMcpTests;

/// <summary>A fresh synthetic MO2 instance for the compact service lane, one per test: several arms compact in place
/// or add plugins, so no two tests share one. Five plugins, each in its own mod folder, in load order:
/// HcCsBase.esm (an interior cell), HcCsSelf.esp (a self-contained weapon, cell and placed ref), HcCsLib.esp (a
/// weapon), HcCsOver.esp (overrides Base's cell and adds a placed ref), HcCsDep.esp (a FormList naming Lib's weapon).</summary>
public sealed class CompactServiceWorld : IDisposable
{
    public static readonly ModKey SelfKey = new("HcCsSelf", ModType.Plugin);
    public static readonly ModKey BaseKey = new("HcCsBase", ModType.Master);
    public static readonly ModKey OverKey = new("HcCsOver", ModType.Plugin);
    public static readonly ModKey LibKey = new("HcCsLib", ModType.Plugin);
    public static readonly ModKey DepKey = new("HcCsDep", ModType.Plugin);
    public static readonly FormKey BaseCell = new(BaseKey, 0xA01);
    public static readonly FormKey LibWeapon = new(LibKey, 0xA01);

    public string Root { get; }
    public string Mods { get; }
    public LoadOrderService Svc { get; }

    public CompactServiceWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-compact-service-" + Guid.NewGuid().ToString("N"));
        string instance = Path.Combine(Root, "instance");
        string profiles = Path.Combine(instance, "profiles", "Default");
        Mods = Path.Combine(instance, "mods");
        string data = Path.Combine(Root, "game", "Data");
        Directory.CreateDirectory(profiles); Directory.CreateDirectory(Mods); Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        WriteMod("SelfMod", SelfKey, m =>
        {
            m.Weapons.Add(new Weapon(new FormKey(SelfKey, 0xA01), SkyrimRelease.SkyrimSE) { EditorID = "HcCsWeap", BasicStats = new WeaponBasicStats { Damage = 5 } });
            var c = new Cell(new FormKey(SelfKey, 0xA02), SkyrimRelease.SkyrimSE) { EditorID = "HcCsCell", Flags = Cell.Flag.IsInteriorCell };
            c.Temporary.Add(new PlacedObject(new FormKey(SelfKey, 0xA03), SkyrimRelease.SkyrimSE) { EditorID = "HcCsRef" });
            FileInterior(m, c);
        });

        WriteMod("BaseMod", BaseKey, m =>
            FileInterior(m, new Cell(BaseCell, SkyrimRelease.SkyrimSE) { EditorID = "HcCsBaseCell", Flags = Cell.Flag.IsInteriorCell }));

        using (var baseOv = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(Mods, "BaseMod", BaseKey.FileName.String), SkyrimRelease.SkyrimSE))
        {
            var baseCell = baseOv.EnumerateMajorRecords<ICellGetter>().First(c => c.EditorID == "HcCsBaseCell");
            var dir = Path.Combine(Mods, "OverMod"); Directory.CreateDirectory(dir);
            var o = new SkyrimMod(OverKey, SkyrimRelease.SkyrimSE);
            var ovCell = (ICell)WriteEngine.GenericGetOrAddAsOverride(o, baseCell, baseOv.ToImmutableLinkCache());
            ovCell.Temporary.Add(new PlacedObject(new FormKey(OverKey, 0xA01), SkyrimRelease.SkyrimSE) { EditorID = "HcCsOverRef" });
            o.ModHeader.Stats.NextFormID = 0xA02;
            o.BeginWrite.ToPath(Path.Combine(dir, OverKey.FileName.String)).WithLoadOrder(new[] { baseOv }).NoNextFormIDProcessing().Write();
        }

        WriteMod("LibMod", LibKey, m =>
            m.Weapons.Add(new Weapon(LibWeapon, SkyrimRelease.SkyrimSE) { EditorID = "HcCsLibWeap", BasicStats = new WeaponBasicStats { Damage = 8 } }));

        using (var libOv = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(Mods, "LibMod", LibKey.FileName.String), SkyrimRelease.SkyrimSE))
        {
            var dir = Path.Combine(Mods, "DepMod"); Directory.CreateDirectory(dir);
            var d = new SkyrimMod(DepKey, SkyrimRelease.SkyrimSE);
            var fl = new FormList(new FormKey(DepKey, 0xA01), SkyrimRelease.SkyrimSE) { EditorID = "HcCsDepList" };
            fl.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(LibWeapon));
            d.FormLists.Add(fl);
            d.ModHeader.Stats.NextFormID = 0xA02;
            d.BeginWrite.ToPath(Path.Combine(dir, DepKey.FileName.String)).WithLoadOrder(new[] { libOv }).NoNextFormIDProcessing().Write();
        }

        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"),
            "# header\r\n" + string.Join("\r\n", BaseKey.FileName, SelfKey.FileName, LibKey.FileName, OverKey.FileName, DepKey.FileName) + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"),
            string.Join("\r\n", "*" + BaseKey.FileName, "*" + SelfKey.FileName, "*" + LibKey.FileName, "*" + OverKey.FileName, "*" + DepKey.FileName) + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"),
            "# header\r\n" + string.Join("\r\n", "+DepMod", "+OverMod", "+LibMod", "+SelfMod", "+BaseMod") + "\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
        Svc.Stats();
    }

    /// <summary>Write a plugin into its own mod folder that no profile file lists — the fresh patch before an MO2 refresh.</summary>
    public void WriteMod(string folder, ModKey key, Action<SkyrimMod> build)
    {
        var dir = Path.Combine(Mods, folder);
        Directory.CreateDirectory(dir);
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        build(m);
        m.BeginWrite.ToPath(Path.Combine(dir, key.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
    }

    /// <summary>Write an override-only plugin into an unlisted mod folder: Lib's weapon with its damage set to 42.</summary>
    public void WriteFlagOnlyMod(ModKey key)
    {
        using var libOv = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(Mods, "LibMod", LibKey.FileName.String), SkyrimRelease.SkyrimSE);
        var dir = Path.Combine(Mods, "FlagOnlyMod"); Directory.CreateDirectory(dir);
        var f = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        var w = (IWeapon)WriteEngine.GenericGetOrAddAsOverride(f, libOv.Weapons.First(), libOv.ToImmutableLinkCache());
        w.BasicStats!.Damage = 42;
        f.BeginWrite.ToPath(Path.Combine(dir, key.FileName.String)).WithLoadOrder(new[] { libOv }).Write();
    }

    /// <summary>File an interior cell into a mod's block tree by its FormID digits.</summary>
    static void FileInterior(SkyrimMod mod, Cell cell)
    {
        uint id = cell.FormKey.ID;
        int blockN = (int)(id % 10), subN = (int)((id / 10) % 10);
        var records = mod.Cells.Records;
        var block = records.FirstOrDefault(b => b.BlockNumber == blockN);
        if (block is null) { block = new CellBlock { BlockNumber = blockN, GroupType = GroupTypeEnum.InteriorCellBlock }; records.Add(block); }
        var sub = block.SubBlocks.FirstOrDefault(s => s.BlockNumber == subN);
        if (sub is null) { sub = new CellSubBlock { BlockNumber = subN, GroupType = GroupTypeEnum.InteriorCellSubBlock }; block.SubBlocks.Add(sub); }
        sub.Cells.Add(cell);
    }

    public static bool InEslWindow(FormKey k) => k.ID >= RemapEngine.EslFloor && k.ID <= RemapEngine.EslCeiling;

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* best effort */ }
    }
}

/// <summary>A service over a <see cref="LocalizedStringsFixture"/> instance in its own temp folder, one per test.</summary>
public sealed class LocalizedCompactWorld : IDisposable
{
    readonly string _root;
    internal LocalizedStringsFixture.Built Fx { get; }
    public LoadOrderService Svc { get; }

    internal LocalizedCompactWorld(params LocalizedStringsFixture.Spec[] specs)
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-compact-loc-" + Guid.NewGuid().ToString("N"));
        Fx = LocalizedStringsFixture.Build(_root, specs);
        Svc = LoadOrderService.WithInstance(Fx.Instance, 0, new UserConfigStore(Path.Combine(_root, "houseCARL.user.json")));
        Svc.Stats();
    }

    internal string PluginPath(LocalizedStringsFixture.Spec s) => Path.Combine(Fx.Mods, s.ModFolder, s.Key.FileName.String);

    public static bool Same(string path, byte[] before) => File.ReadAllBytes(path).AsSpan().SequenceEqual(before);

    public static bool NoStaging(string pluginPath) => !Directory.Exists(Path.Combine(Path.GetDirectoryName(pluginPath)!, ".housecarl-tmp"));

    /// <summary>Deny the current user ListDirectory on <paramref name="dir"/>, and report whether the deny took.</summary>
    public static bool TryDenyListing(string dir)
    {
        try
        {
            var me = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            var di = new DirectoryInfo(dir);
            var sec = di.GetAccessControl();
            sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                me, System.Security.AccessControl.FileSystemRights.ListDirectory,
                System.Security.AccessControl.AccessControlType.Deny));
            di.SetAccessControl(sec);
            if (!Directory.Exists(dir)) { UndenyListing(dir); return false; }
            try { Directory.EnumerateFiles(dir).ToList(); }
            catch (UnauthorizedAccessException) { return true; }
            catch (IOException) { return true; }
            UndenyListing(dir);
            return false;
        }
        catch { return false; }
    }

    /// <summary>Lift the deny <see cref="TryDenyListing"/> added.</summary>
    public static void UndenyListing(string dir)
    {
        try
        {
            var me = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            var di = new DirectoryInfo(dir);
            var sec = di.GetAccessControl();
            sec.RemoveAccessRuleAll(new System.Security.AccessControl.FileSystemAccessRule(
                me, System.Security.AccessControl.FileSystemRights.ListDirectory,
                System.Security.AccessControl.AccessControlType.Deny));
            di.SetAccessControl(sec);
        }
        catch { /* best effort */ }
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }
}

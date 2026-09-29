using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The real merge shape (the former merge-service-guard fixture): a base master, donor A (a weapon, a DIAL with two
/// INFOs, an NPC with facegen, an SGE quest with a shipped .seq, a voiced line, a placed ref in its own cell, an
/// override of the base weapon), donor B — a PATCH of A later in the load order (overrides A's DIAL re-listing only
/// INFO1, overrides the base weapon, collides with A on object id 0xA01, references A's weapon, moves A's placed ref
/// into B's own cell) — plus an external referencer (Dep) and an external overrider (Ovr) outside the merge set, and
/// four header-shape donors (light by flag, light by extension, master, empty). The full-shape merge of A+B and the
/// rename of A alone run once here; the tests read their outcomes.
/// </summary>
public sealed class MergeServiceWorld : IDisposable
{
    public string Root { get; }
    public string Instance { get; }
    public string Mods { get; }
    public LoadOrderService Svc { get; }

    public static readonly ModKey BaseKey = new("HcMgBase", ModType.Master);
    public static readonly ModKey AKey = new("HcMgA", ModType.Plugin);
    public static readonly ModKey BKey = new("HcMgB", ModType.Plugin);
    public static readonly ModKey MergedKey = new("HcMgMerged", ModType.Plugin);
    public static readonly ModKey RenamedKey = new("HcMgRenamed", ModType.Plugin);

    public static readonly FormKey BaseWeap = new(BaseKey, 0xA01);
    public static readonly FormKey ADial = new(AKey, 0xA10);
    public static readonly FormKey AInfo1 = new(AKey, 0xA11);
    public static readonly FormKey ANpc = new(AKey, 0xA20);
    public static readonly FormKey ARef = new(AKey, 0xA40);

    public string APath { get; }
    public string BPath { get; }
    public byte[] ABytesBefore { get; }
    public byte[] BBytesBefore { get; }

    /// <summary>The one full-shape call: A+B, donor args scrambled so LOAD order must govern.</summary>
    public WritePatchBuilder.MergeOutcome Merged { get; }

    /// <summary>Donor A alone into a new name — the single-donor rename arm (#345).</summary>
    public WritePatchBuilder.MergeOutcome Renamed { get; }

    public MergeServiceWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-merge-service-" + Guid.NewGuid().ToString("N"));
        Instance = Path.Combine(Root, "instance");
        var profiles = Path.Combine(Instance, "profiles", "Default");
        Mods = Path.Combine(Instance, "mods");
        foreach (var d in new[] { profiles, Mods, Path.Combine(Root, "game", "Data") }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(Instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        var baseDir = Dir("BaseMod");
        var basePath = Path.Combine(baseDir, BaseKey.FileName.String);
        {
            var m = new SkyrimMod(BaseKey, SkyrimRelease.SkyrimSE);
            m.Weapons.Add(new Weapon(BaseWeap, SkyrimRelease.SkyrimSE) { EditorID = "HcMgBaseWeap", BasicStats = new WeaponBasicStats { Damage = 5 } });
            m.BeginWrite.ToPath(basePath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }

        var aWeap = new FormKey(AKey, 0xA01);
        var aDir = Dir("AMod");
        APath = Path.Combine(aDir, AKey.FileName.String);
        {
            using var baseOv = SkyrimMod.CreateFromBinaryOverlay(basePath, SkyrimRelease.SkyrimSE);
            var m = new SkyrimMod(AKey, SkyrimRelease.SkyrimSE);
            m.Weapons.Add(new Weapon(aWeap, SkyrimRelease.SkyrimSE) { EditorID = "HcMgAWeap", BasicStats = new WeaponBasicStats { Damage = 7 } });
            var topic = new DialogTopic(ADial, SkyrimRelease.SkyrimSE) { EditorID = "HcMgTopic" };
            var i1 = new DialogResponses(AInfo1, SkyrimRelease.SkyrimSE);
            i1.Responses.Add(new DialogResponse { Text = "A11 base" });
            var i2 = new DialogResponses(new FormKey(AKey, 0xA12), SkyrimRelease.SkyrimSE);
            i2.Responses.Add(new DialogResponse { Text = "A12 base" });
            topic.Responses.Add(i1); topic.Responses.Add(i2);
            m.DialogTopics.Add(topic);
            m.Npcs.Add(new Npc(ANpc, SkyrimRelease.SkyrimSE) { EditorID = "HcMgNpc" });
            m.Quests.Add(new Quest(new FormKey(AKey, 0xA30), SkyrimRelease.SkyrimSE) { EditorID = "HcMgQuest", Flags = Quest.Flag.StartGameEnabled });
            var c1 = new Cell(new FormKey(AKey, 0xA41), SkyrimRelease.SkyrimSE) { EditorID = "HcMgACell", Flags = Cell.Flag.IsInteriorCell };
            c1.Temporary.Add(new PlacedObject(ARef, SkyrimRelease.SkyrimSE) { EditorID = "HcMgRefBase" });
            FileInterior(m, c1);
            m.Weapons.GetOrAddAsOverride(baseOv.Weapons.First(w => w.FormKey == BaseWeap)).BasicStats!.Damage = 10;
            m.BeginWrite.ToPath(APath).WithLoadOrder(new ISkyrimModGetter[] { baseOv }).Write();
        }
        foreach (var (_, rel) in FaceGenPath.Both(ANpc))
        {
            var p = Path.Combine(aDir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, new byte[] { 0xFA, 0xCE });
        }
        var aVoice = Path.Combine(aDir, "Sound", "Voice", AKey.FileName.String, "MaleEvenToned", VoiceFile);
        Directory.CreateDirectory(Path.GetDirectoryName(aVoice)!);
        File.WriteAllBytes(aVoice, new byte[] { 0xF0, 0x02 });
        Directory.CreateDirectory(Path.Combine(aDir, "SEQ"));
        File.WriteAllBytes(Path.Combine(aDir, "SEQ", "HcMgA.seq"), new byte[] { 0x30, 0x0A, 0x00, 0x00 });

        var bDir = Dir("BMod");
        BPath = Path.Combine(bDir, BKey.FileName.String);
        {
            using var baseOv = SkyrimMod.CreateFromBinaryOverlay(basePath, SkyrimRelease.SkyrimSE);
            using var aOv = SkyrimMod.CreateFromBinaryOverlay(APath, SkyrimRelease.SkyrimSE);
            var m = new SkyrimMod(BKey, SkyrimRelease.SkyrimSE);
            m.Weapons.Add(new Weapon(new FormKey(BKey, 0xA01), SkyrimRelease.SkyrimSE) { EditorID = "HcMgBColl", BasicStats = new WeaponBasicStats { Damage = 3 } });
            m.Weapons.Add(new Weapon(new FormKey(BKey, 0xB01), SkyrimRelease.SkyrimSE) { EditorID = "HcMgBWeap", BasicStats = new WeaponBasicStats { Damage = 8 } });
            var fl = new FormList(new FormKey(BKey, 0xB02), SkyrimRelease.SkyrimSE) { EditorID = "HcMgBList" };
            fl.Items.Add(aWeap.ToLink<ISkyrimMajorRecordGetter>());
            m.FormLists.Add(fl);
            var patchTopic = new DialogTopic(ADial, SkyrimRelease.SkyrimSE) { EditorID = "HcMgTopicPatched" };
            var pi1 = new DialogResponses(AInfo1, SkyrimRelease.SkyrimSE);
            pi1.Responses.Add(new DialogResponse { Text = "A11 patched" });
            patchTopic.Responses.Add(pi1);
            m.DialogTopics.Add(patchTopic);
            var c2 = new Cell(new FormKey(BKey, 0xB10), SkyrimRelease.SkyrimSE) { EditorID = "HcMgBCell", Flags = Cell.Flag.IsInteriorCell };
            c2.Temporary.Add(new PlacedObject(ARef, SkyrimRelease.SkyrimSE) { EditorID = "HcMgRefMoved" });
            FileInterior(m, c2);
            m.Weapons.GetOrAddAsOverride(baseOv.Weapons.First(w => w.FormKey == BaseWeap)).BasicStats!.Damage = 20;
            m.BeginWrite.ToPath(BPath).WithLoadOrder(new ISkyrimModGetter[] { baseOv, aOv }).Write();
        }

        var depKey = new ModKey("HcMgDep", ModType.Plugin);
        {
            using var aOv = SkyrimMod.CreateFromBinaryOverlay(APath, SkyrimRelease.SkyrimSE);
            var m = new SkyrimMod(depKey, SkyrimRelease.SkyrimSE);
            var fl = new FormList(new FormKey(depKey, 0xA01), SkyrimRelease.SkyrimSE) { EditorID = "HcMgDepList" };
            fl.Items.Add(aWeap.ToLink<ISkyrimMajorRecordGetter>());
            m.FormLists.Add(fl);
            m.BeginWrite.ToPath(Path.Combine(Dir("DepMod"), depKey.FileName.String)).WithLoadOrder(new ISkyrimModGetter[] { aOv }).Write();
        }
        var ovrKey = new ModKey("HcMgOvr", ModType.Plugin);
        {
            using var aOv = SkyrimMod.CreateFromBinaryOverlay(APath, SkyrimRelease.SkyrimSE);
            var m = new SkyrimMod(ovrKey, SkyrimRelease.SkyrimSE);
            m.Weapons.GetOrAddAsOverride(aOv.Weapons.First(w => w.FormKey == aWeap)).BasicStats!.Damage = 99;
            m.BeginWrite.ToPath(Path.Combine(Dir("OvrMod"), ovrKey.FileName.String)).WithLoadOrder(new ISkyrimModGetter[] { aOv }).Write();
        }

        // Header-shape donors: light by flag with Author/Description, light by .esl extension only, master, empty.
        var eslKey = new ModKey("HcMgEsl", ModType.Plugin);
        {
            var m = new SkyrimMod(eslKey, SkyrimRelease.SkyrimSE) { IsSmallMaster = true };
            m.ModHeader.Author = "HcMgAuthor";
            m.ModHeader.Description = "HcMgDescription";
            m.Weapons.Add(new Weapon(new FormKey(eslKey, 0x801), SkyrimRelease.SkyrimSE) { EditorID = "HcMgEslWeap" });
            m.BeginWrite.ToPath(Path.Combine(Dir("EslMod"), eslKey.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }
        var eslExtKey = new ModKey("HcMgEslExt", ModType.Light);
        {
            var m = new SkyrimMod(eslExtKey, SkyrimRelease.SkyrimSE);
            m.Weapons.Add(new Weapon(new FormKey(eslExtKey, 0x802), SkyrimRelease.SkyrimSE) { EditorID = "HcMgEslExtWeap" });
            m.BeginWrite.ToPath(Path.Combine(Dir("EslExtMod"), eslExtKey.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }
        var emptyKey = new ModKey("HcMgEmpty", ModType.Plugin);
        new SkyrimMod(emptyKey, SkyrimRelease.SkyrimSE).BeginWrite
            .ToPath(Path.Combine(Dir("EmptyMod"), emptyKey.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        var esmKey = new ModKey("HcMgEsm", ModType.Master);
        {
            var m = new SkyrimMod(esmKey, SkyrimRelease.SkyrimSE);
            m.Weapons.Add(new Weapon(new FormKey(esmKey, 0xD01), SkyrimRelease.SkyrimSE) { EditorID = "HcMgEsmWeap" });
            m.BeginWrite.ToPath(Path.Combine(Dir("EsmMod"), esmKey.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }

        var order = new[] { BaseKey, AKey, BKey, depKey, ovrKey, eslKey, eslExtKey, esmKey, emptyKey }.Select(k => k.FileName.String).ToArray();
        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", order) + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"), string.Join("\r\n", order.Select(p => "*" + p)) + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"),
            "# header\r\n+EmptyMod\r\n+EsmMod\r\n+EslExtMod\r\n+EslMod\r\n+OvrMod\r\n+DepMod\r\n+BMod\r\n+AMod\r\n+BaseMod\r\n");

        Svc = LoadOrderService.WithInstance(Instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
        Svc.Stats();

        ABytesBefore = File.ReadAllBytes(APath);
        BBytesBefore = File.ReadAllBytes(BPath);
        Merged = Svc.MergePlugins(new[] { "HcMgB.esp", "HcMgA.esp" }, "HcMgMerged.esp");
        Renamed = Svc.MergePlugins(new[] { "HcMgA.esp" }, "HcMgRenamed.esp");
    }

    public const string VoiceFile = "HcQ_HcT_00000A11_1.fuz";

    string Dir(string folder)
    {
        var d = Path.Combine(Mods, folder);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>File an interior cell into a mod's Cells block tree by its FormID digits.</summary>
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

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

[CollectionDefinition("merge-service")]
public sealed class MergeServiceCollection : ICollectionFixture<MergeServiceWorld> { }

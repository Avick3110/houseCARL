using System.Reflection;
using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The forward jobs of the walk-shape needs table on one synthetic order (DECISIONS 2026-11-08 (20), part
/// fields). <c>HcShapeBase.esp</c> defines an NPC with its race, base eyes, face texture, hair colour, skin, an outfit
/// whose clothes sit under a leveled list (four hops to their addon), a ring in his inventory and a quest named by a
/// script property; an iron-cuirass-like armour whose addon names the race; a sword with a 1st-person static and a
/// pick-up sound; a throne with an alternate texture; and an interior cell holding a chair, a chest and the NPC, its
/// music a track. <c>HcShapeLooks.esp</c> overrides the NPC with hair (an extra part, a texture set, a .tri) and a head
/// it defines, as an appearance merge does, and overrides the cell with a lamp of its own.</summary>
public sealed class AssetWalkPartsWorld : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-walk-parts-");
    public static readonly ModKey Base = ModKey.FromFileName("HcShapeBase.esp"), Looks = ModKey.FromFileName("HcShapeLooks.esp");
    public LoadOrderService Svc { get; }
    public Dictionary<string, FormKey> Keys { get; } = new(StringComparer.Ordinal);

    public const string HairNif = @"meshes\hcshape\hair.nif", HairTri = @"meshes\hcshape\hair.tri", HairlineNif = @"meshes\hcshape\hairline.nif",
                        HairTex = @"textures\hcshape\hair_d.dds", HairMeshTex = @"textures\hcshape\hair_n.dds", HeadNif = @"meshes\hcshape\head.nif",
                        EyesNif = @"meshes\hcshape\eyes.nif", FaceTex = @"textures\hcshape\face_rough.dds",
                        BodyNif = @"meshes\hcshape\body_1.nif", FirstBodyNif = @"meshes\hcshape\1stbody_1.nif", HandsNif = @"meshes\hcshape\hands_1.nif",
                        FeetNif = @"meshes\hcshape\feet_1.nif", OutfitNif = @"meshes\hcshape\outfit_1.nif", OutfitGround = @"meshes\hcshape\outfitgo.nif",
                        BootsNif = @"meshes\hcshape\boots_1.nif",
                        RaceSkinNif = @"meshes\hcshape\raceskin_1.nif", RaceSkeleton = @"meshes\hcshape\skeleton.nif", RingNif = @"meshes\hcshape\ring.nif",
                        QuestScript = "HcShapeQuestScript", NpcScript = "HcShapeNpcScript",
                        CuirassGround = @"meshes\hcshape\cuirassgnd.nif", CuirassGroundF = @"meshes\hcshape\f\cuirassgnd.nif",
                        CuirassNif = @"meshes\hcshape\cuirass_1.nif", CuirassNifF = @"meshes\hcshape\f\cuirass_1.nif",
                        FirstCuirassNif = @"meshes\hcshape\1stcuirass_1.nif", FirstCuirassNifF = @"meshes\hcshape\f\1stcuirass_1.nif",
                        CuirassTex = @"textures\hcshape\cuirassplate.dds", CuirassTexN = @"textures\hcshape\cuirassplate_n.dds",
                        SwordNif = @"meshes\hcshape\sword.nif", FirstSwordNif = @"meshes\hcshape\1stsword.nif", PickUpWav = @"sound\hcshape\pickup.wav",
                        ThroneNif = @"meshes\hcshape\throne.nif", ThroneTex = @"textures\hcshape\throne_d.dds",
                        ChairNif = @"meshes\hcshape\chair.nif", ChestNif = @"meshes\hcshape\chest.nif", LampNif = @"meshes\hcshape\lamp.nif",
                        TrackXwm = @"music\hcshape\track.xwm";

    public AssetWalkPartsWorld()
    {
        var b = new SkyrimMod(Base, SkyrimRelease.SkyrimSE);
        T Keyed<T>(T r) where T : IMajorRecord { Keys[r.EditorID!] = r.FormKey; return r; }
        ArmorAddon Addon(SkyrimMod m, string edid, string male, string? female = null, string? first = null, string? firstF = null)
        {
            var a = Keyed(m.ArmorAddons.AddNew(edid));
            a.WorldModel = new GenderedItem<Model?>(new Model { File = male }, female is null ? null : new Model { File = female });
            if (first is not null) a.FirstPersonModel = new GenderedItem<Model?>(new Model { File = first }, firstF is null ? null : new Model { File = firstF });
            return a;
        }
        Armor Armour(SkyrimMod m, string edid, string? ground, params ArmorAddon[] addons)
        {
            var a = Keyed(m.Armors.AddNew(edid));
            if (ground is not null) a.WorldModel = new GenderedItem<ArmorModel?>(new ArmorModel { Model = new Model { File = ground } }, null);
            foreach (var aa in addons) a.Armature.Add(aa);
            return a;
        }
        static string Rel(string meshPath) => meshPath["meshes\\".Length..];

        // The NPC's race, with a skin of its own: reached only through the NPC's Race link, which is no part field.
        var race = Keyed(b.Races.AddNew("HcShapeRace"));
        race.Flags = Race.Flag.Playable | Race.Flag.FaceGenHead;
        race.SkeletalModel = new GenderedItem<SimpleModel?>(new SimpleModel { File = Rel(RaceSkeleton) }, null);
        race.Skin.SetTo(Armour(b, "HcShapeRaceSkin", null, Addon(b, "HcShapeRaceSkinAA", Rel(RaceSkinNif))));
        var eyes = Keyed(b.HeadParts.AddNew("HcShapeEyes"));
        eyes.Model = new Model { File = Rel(EyesNif) };
        var faceTex = Keyed(b.TextureSets.AddNew("HcShapeFaceRough"));
        faceTex.Diffuse = FaceTex["textures\\".Length..];
        var hairColor = Keyed(b.Colors.AddNew("HcShapeHairColor"));
        var skin = Armour(b, "HcShapeSkin", null,
                          Addon(b, "HcShapeBodyAA", Rel(BodyNif), first: Rel(FirstBodyNif)), Addon(b, "HcShapeHandsAA", Rel(HandsNif)), Addon(b, "HcShapeFeetAA", Rel(FeetNif)));
        var clothes = Armour(b, "HcShapeClothes", Rel(OutfitGround), Addon(b, "HcShapeClothesAA", Rel(OutfitNif)));
        var boots = Armour(b, "HcShapeBoots", null, Addon(b, "HcShapeBootsAA", Rel(BootsNif)));
        var clothesList = Keyed(b.LeveledItems.AddNew("HcShapeClothesList"));
        clothesList.Entries = new() { new LeveledItemEntry { Data = new LeveledItemEntryData { Level = 1, Count = 1, Reference = clothes.ToLink<IItemGetter>() } } };
        var outfit = Keyed(b.Outfits.AddNew("HcShapeOutfit"));
        outfit.Items = new() { clothesList.ToLink<IOutfitTargetGetter>(), boots.ToLink<IOutfitTargetGetter>() };
        var ring = Armour(b, "HcShapeRing", Rel(RingNif));
        var quest = Keyed(b.Quests.AddNew("HcShapeQuest"));
        quest.VirtualMachineAdapter = new QuestAdapter { Scripts = { new ScriptEntry { Name = QuestScript } } };
        var npc = Keyed(b.Npcs.AddNew("HcShapeNpc"));
        npc.Race.SetTo(race);
        npc.HeadParts.Add(eyes);
        npc.HeadTexture.SetTo(faceTex);
        npc.HairColor.SetTo(hairColor);
        npc.WornArmor.SetTo(skin);
        npc.DefaultOutfit.SetTo(outfit);
        npc.Items = new() { new ContainerEntry { Item = new ContainerItem { Item = ring.ToLink<IItemGetter>(), Count = 1 } } };
        npc.VirtualMachineAdapter = new VirtualMachineAdapter
        {
            Scripts = { new ScriptEntry { Name = NpcScript, Properties = { new ScriptObjectProperty { Name = "Quest", Object = quest.ToLink<ISkyrimMajorRecordGetter>() } } } },
        };

        // The cuirass: its addon names the race, whose skin is not the cuirass's.
        var cuirassAA = Addon(b, "HcShapeCuirassAA", Rel(CuirassNif), Rel(CuirassNifF), Rel(FirstCuirassNif), Rel(FirstCuirassNifF));
        cuirassAA.Race.SetTo(race);
        var cuirass = Armour(b, "HcShapeCuirass", null, cuirassAA);
        cuirass.WorldModel = new GenderedItem<ArmorModel?>(new ArmorModel { Model = new Model { File = Rel(CuirassGround) } },
                                                           new ArmorModel { Model = new Model { File = Rel(CuirassGroundF) } });

        // The sword: its 1st-person static is a part, its pick-up sound is not.
        var firstSword = Keyed(b.Statics.AddNew("HcShape1stSword"));
        firstSword.Model = new Model { File = Rel(FirstSwordNif) };
        var pickUp = Keyed(b.SoundDescriptors.AddNew("HcShapePickUp"));
        pickUp.SoundFiles.Add(new Mutagen.Bethesda.Plugins.Assets.AssetLink<Mutagen.Bethesda.Skyrim.Assets.SkyrimSoundAssetType>(PickUpWav["sound\\".Length..]));
        var sword = Keyed(b.Weapons.AddNew("HcShapeSword"));
        sword.Model = new Model { File = Rel(SwordNif) };
        sword.FirstPersonModel.SetTo(firstSword);
        sword.PickUpSound.SetTo(pickUp);

        // The throne: its model's alternate texture set.
        var throneTex = Keyed(b.TextureSets.AddNew("HcShapeThroneTex"));
        throneTex.Diffuse = ThroneTex["textures\\".Length..];
        var throne = Keyed(b.Furniture.AddNew("HcShapeThrone"));
        throne.Model = new Model { File = Rel(ThroneNif), AlternateTextures = new() { new AlternateTexture { Name = "Throne", NewTexture = throneTex.ToLink(), Index = 0 } } };

        // The cell: two placed objects and the NPC, its music a track.
        var chair = Keyed(b.Statics.AddNew("HcShapeChair"));
        chair.Model = new Model { File = Rel(ChairNif) };
        var chest = Keyed(b.Containers.AddNew("HcShapeChest"));
        chest.Model = new Model { File = Rel(ChestNif) };
        var track = Keyed(b.MusicTracks.AddNew("HcShapeTrack"));
        track.TrackFilename = TrackXwm["music\\".Length..];
        var music = Keyed(b.MusicTypes.AddNew("HcShapeMusic"));
        music.Tracks = new() { track.ToLink() };
        var cell = new Cell(b.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcShapeCell", Flags = Cell.Flag.IsInteriorCell };
        Keys["HcShapeCell"] = cell.FormKey;
        cell.Music.SetTo(music);
        PlacedObject Place(SkyrimMod m, IMajorRecordGetter at)
        {
            var r = new PlacedObject(m.GetNextFormKey(), SkyrimRelease.SkyrimSE);
            r.Base.SetTo(at.FormKey);
            return r;
        }
        cell.Temporary.Add(Place(b, chair));
        cell.Persistent.Add(Place(b, chest));
        var placedNpc = new PlacedNpc(b.GetNextFormKey(), SkyrimRelease.SkyrimSE);
        placedNpc.Base.SetTo(npc);
        cell.Persistent.Add(placedNpc);
        static void AddCell(SkyrimMod m, Cell c)
        {
            var block = new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock };
            var sub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
            sub.Cells.Add(c);
            block.SubBlocks.Add(sub);
            m.Cells.Records.Add(block);
        }
        AddCell(b, cell);
        b.BeginWrite.ToPath(_mo2.InMod("ShapeBase", Base)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        // The appearance merge: hair and head it defines, on its override of the NPC.
        var l = new SkyrimMod(Looks, SkyrimRelease.SkyrimSE);
        var hairTexSet = Keyed(l.TextureSets.AddNew("HcShapeHairTex"));
        hairTexSet.Diffuse = HairTex["textures\\".Length..];
        var hairline = Keyed(l.HeadParts.AddNew("HcShapeHairline"));
        hairline.Model = new Model { File = Rel(HairlineNif) };
        var hair = Keyed(l.HeadParts.AddNew("HcShapeHair"));
        hair.Model = new Model { File = Rel(HairNif) };
        hair.Parts.Add(new Part { PartType = Part.PartTypeEnum.RaceMorph, FileName = Rel(HairTri) });
        hair.ExtraParts.Add(hairline);
        hair.TextureSet.SetTo(hairTexSet);
        var head = Keyed(l.HeadParts.AddNew("HcShapeHead"));
        head.Model = new Model { File = Rel(HeadNif) };
        var ov = npc.DeepCopy();
        ov.HeadParts.Add(hair);
        ov.HeadParts.Add(head);
        l.Npcs.Add(ov);
        var lamp = Keyed(l.Statics.AddNew("HcShapeLamp"));
        lamp.Model = new Model { File = Rel(LampNif) };
        var cellOv = new Cell(cell.FormKey, SkyrimRelease.SkyrimSE) { EditorID = "HcShapeCell", Flags = Cell.Flag.IsInteriorCell };
        cellOv.Music.SetTo(music);
        cellOv.Temporary.Add(Place(l, lamp));
        AddCell(l, cellOv);
        l.BeginWrite.ToPath(_mo2.InMod("ShapeLooks", Looks)).WithLoadOrder(new ISkyrimModGetter[] { b, l }).Write();

        // The meshes that name textures: the hair's normal map and the cuirass's plate.
        var files = Path.Combine(_mo2.ModsDir, "ShapeFiles");
        NifSourceLaneInstance.Loose(files, HairNif, AssetWalkWorld.Mesh(new[] { HairMeshTex }, null));
        NifSourceLaneInstance.Loose(files, CuirassNif, AssetWalkWorld.Mesh(new[] { CuirassTex, CuirassTexN }, null));

        var names = new[] { Base, Looks }.Select(k => k.FileName.String).ToList();
        _mo2.Profile(string.Join("\r\n", names) + "\r\n", string.Join("", names.Select(n => "*" + n + "\r\n")), "+ShapeFiles\r\n+ShapeLooks\r\n+ShapeBase\r\n");
        Svc = _mo2.Open();
    }

    public void Dispose() { Svc.Dispose(); _mo2.Delete(); }
}

/// <summary>The default walk, records=[seed] walk=true, against each forward job's must rows and noise (walk-shape
/// research.md, Needs table). A row that main also gave pins unchanged behaviour and says so.</summary>
[Trait("tier", "unit")]
public sealed class AssetWalkPartsTests : IClassFixture<AssetWalkPartsWorld>
{
    readonly AssetWalkPartsWorld _w;
    public AssetWalkPartsTests(AssetWalkPartsWorld w) => _w = w;

    AssetWalkData Walk(string seed, WalkGrammar.Values? values = null)
        => _w.Svc.AssetWalk(Array.Empty<string>(), new[] { _w.Keys[seed] }, null, null, values: values);

    static bool Has(AssetWalkData d, string path) => d.Nodes.Any(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase));

    static string Paths(AssetWalkData d) => string.Join("\n", d.Nodes.Select(n => n.Path));

    /// <summary>The NPC look job's must rows: his FaceGen; the merge's hair with its extra part, .tri, texture set and
    /// the texture its mesh names, and its head, which main kept as boundaries in another plugin; the base eyes, face
    /// texture and skin (body with 1st person, hands, feet), which main also gave; the outfit's boots, and its clothes
    /// under a leveled list, four hops out, past main's depth of 3; the clothes' ground model, which main also gave.</summary>
    [Theory]
    [InlineData(AssetWalkPartsWorld.HairNif)]
    [InlineData(AssetWalkPartsWorld.HairTri)]
    [InlineData(AssetWalkPartsWorld.HairlineNif)]
    [InlineData(AssetWalkPartsWorld.HairTex)]
    [InlineData(AssetWalkPartsWorld.HairMeshTex)]
    [InlineData(AssetWalkPartsWorld.HeadNif)]
    [InlineData(AssetWalkPartsWorld.EyesNif)]
    [InlineData(AssetWalkPartsWorld.FaceTex)]
    [InlineData(AssetWalkPartsWorld.BodyNif)]
    [InlineData(AssetWalkPartsWorld.FirstBodyNif)]
    [InlineData(AssetWalkPartsWorld.HandsNif)]
    [InlineData(AssetWalkPartsWorld.FeetNif)]
    [InlineData(AssetWalkPartsWorld.OutfitNif)]
    [InlineData(AssetWalkPartsWorld.OutfitGround)]
    [InlineData(AssetWalkPartsWorld.BootsNif)]
    public void TheNpcWalkListsEachPartOfHisLook(string path)
    {
        var d = Walk("HcShapeNpc");
        Assert.True(Has(d, path), path + " missing from:\n" + Paths(d));
    }

    /// <summary>His FaceGen pair, the seed's own (main gave it too).</summary>
    [Fact]
    public void TheNpcWalkListsHisFaceGen()
    {
        var d = Walk("HcShapeNpc");
        Assert.Equal(2, d.Nodes.Count(n => n.Edge == "facegen"));
    }

    /// <summary>The NPC look job's noise, which main listed: his race's skeleton and skin, the ring in his inventory,
    /// and the script of the quest his script property names.</summary>
    [Theory]
    [InlineData(AssetWalkPartsWorld.RaceSkeleton)]
    [InlineData(AssetWalkPartsWorld.RaceSkinNif)]
    [InlineData(AssetWalkPartsWorld.RingNif)]
    [InlineData(@"scripts\" + AssetWalkPartsWorld.QuestScript + ".pex")]
    public void TheNpcWalkListsNoneOfWhatHeMerelyNames(string path)
    {
        var d = Walk("HcShapeNpc");
        Assert.False(Has(d, path), path + " listed in:\n" + Paths(d));
    }

    /// <summary>The armour job's must rows (main gave them too): both ground models, the addon's male and female
    /// models and 1st-person models, and the plate textures its mesh names.</summary>
    [Theory]
    [InlineData(AssetWalkPartsWorld.CuirassGround)]
    [InlineData(AssetWalkPartsWorld.CuirassGroundF)]
    [InlineData(AssetWalkPartsWorld.CuirassNif)]
    [InlineData(AssetWalkPartsWorld.CuirassNifF)]
    [InlineData(AssetWalkPartsWorld.FirstCuirassNif)]
    [InlineData(AssetWalkPartsWorld.FirstCuirassNifF)]
    [InlineData(AssetWalkPartsWorld.CuirassTex)]
    [InlineData(AssetWalkPartsWorld.CuirassTexN)]
    public void TheArmourWalkListsItsModels(string path)
    {
        var d = Walk("HcShapeCuirass");
        Assert.True(Has(d, path), path + " missing from:\n" + Paths(d));
    }

    /// <summary>The armour job's noise: the files of the race the addon names, its skeleton, which main listed, and its
    /// skin, past main's depth.</summary>
    [Theory]
    [InlineData(AssetWalkPartsWorld.RaceSkeleton)]
    [InlineData(AssetWalkPartsWorld.RaceSkinNif)]
    public void TheArmourWalkListsNoneOfItsRacesFiles(string path) => Assert.False(Has(Walk("HcShapeCuirass"), path), path);

    /// <summary>The weapon job's must rows (main gave them too) and its noise, the pick-up sound main listed.</summary>
    [Theory]
    [InlineData(AssetWalkPartsWorld.SwordNif, true)]
    [InlineData(AssetWalkPartsWorld.FirstSwordNif, true)]
    [InlineData(AssetWalkPartsWorld.PickUpWav, false)]
    public void TheWeaponWalkListsItsModelsAndNoSound(string path, bool listed)
        => Assert.Equal(listed, Has(Walk("HcShapeSword"), path));

    /// <summary>The furniture job's must rows, its model and its alternate texture set's texture; pins main's answer.</summary>
    [Theory]
    [InlineData(AssetWalkPartsWorld.ThroneNif)]
    [InlineData(AssetWalkPartsWorld.ThroneTex)]
    public void TheFurnitureWalkListsItsModelAndTexture(string path) => Assert.True(Has(Walk("HcShapeThrone"), path), path);

    /// <summary>The cell job's must rows, its placed objects' base models, which main never reached: the chair and chest
    /// the base plugin places, and the lamp the winning override places, read across both plugins' children.</summary>
    [Theory]
    [InlineData(AssetWalkPartsWorld.ChairNif)]
    [InlineData(AssetWalkPartsWorld.ChestNif)]
    [InlineData(AssetWalkPartsWorld.LampNif)]
    public void TheCellWalkListsItsPlacedBases(string path)
    {
        var d = Walk("HcShapeCell");
        Assert.True(Has(d, path), path + " missing from:\n" + Paths(d));
    }

    /// <summary>The cell job's noise, its music track, which main listed; its placed NPC is a counted boundary.</summary>
    [Fact]
    public void TheCellWalkListsNoMusicAndCountsItsNpc()
    {
        var d = Walk("HcShapeCell");
        Assert.False(Has(d, AssetWalkPartsWorld.TrackXwm));
        Assert.Contains(AssetLayers.NpcBoundaryNote(1), d.Notes);
        Assert.DoesNotContain(d.Nodes, n => n.Edge == "facegen");
    }

    /// <summary>The cell's count on the links-not-followed note is its own links outside a part field, its music alone:
    /// the lamp ref's Base, which its child the walk enters holds, is not counted on the cell.</summary>
    [Fact]
    public void TheCellCountsOnlyItsOwnLinksNotFollowed()
    {
        var note = Assert.Single(Walk("HcShapeCell").Notes, n => n.Contains("were not walked"));
        Assert.Matches(@"[:,] 1 on Cell\b", note);
    }

    /// <summary>walk.follow as a list follows only the named fields: the NPC's HeadParts and WornArmor give his hair,
    /// eyes and skin but no outfit, and no child record is entered.</summary>
    [Fact]
    public void AFollowListFollowsOnlyItsFields()
    {
        var d = Walk("HcShapeNpc", Values("{\"follow\": [\"HeadParts\", \"WornArmor\", \"Armature\"]}"));
        Assert.True(Has(d, AssetWalkPartsWorld.HairNif));
        Assert.True(Has(d, AssetWalkPartsWorld.BodyNif));
        Assert.False(Has(d, AssetWalkPartsWorld.BootsNif));
        Assert.False(Has(d, AssetWalkPartsWorld.HairlineNif));
        Assert.Contains(d.Notes, n => n.Contains("sit outside the fields walk.follow names"));
        Assert.False(Has(Walk("HcShapeCell", Values("{\"follow\": [\"Base\"]}")), AssetWalkPartsWorld.ChairNif));
    }

    /// <summary>walk.follow "*" follows every form link: the race's skin and the quest's script are back, and no
    /// links-not-followed note is written.</summary>
    [Fact]
    public void FollowEveryLinkReachesWhatTheNpcNames()
    {
        var d = Walk("HcShapeNpc", Values("{\"follow\": \"*\"}"));
        Assert.True(Has(d, AssetWalkPartsWorld.RaceSkinNif));
        Assert.True(Has(d, @"scripts\" + AssetWalkPartsWorld.QuestScript + ".pex"));
        Assert.DoesNotContain(d.Notes, n => n.Contains("were not walked"));
    }

    /// <summary>walk.follow "*" is a superset of the default on a cell: it enters the cell's children across both
    /// plugins too, so the base plugin's chair and chest stay beside the override's lamp.</summary>
    [Theory]
    [InlineData(AssetWalkPartsWorld.ChairNif)]
    [InlineData(AssetWalkPartsWorld.ChestNif)]
    [InlineData(AssetWalkPartsWorld.LampNif)]
    public void FollowEveryLinkKeepsTheCellsPlacedBases(string path)
    {
        var d = Walk("HcShapeCell", Values("{\"follow\": \"*\"}"));
        Assert.True(Has(d, path), path + " missing from:\n" + Paths(d));
    }

    /// <summary>The links-not-followed note counts, per holding type, every form link an entered record holds outside its
    /// part fields: the sword's pick-up sound (one on Weapon), and nothing on the static or the sound it never entered.</summary>
    [Fact]
    public void TheLinksNotFollowedNoteCountsEachLinkByType()
        => Assert.Contains("1 form link(s) on the records entered sit outside the part fields and were not walked: 1 on Weapon; walk.follow names the fields to follow, and walk={\"follow\": \"*\"} follows every form link.",
                           Walk("HcShapeSword").Notes);

    /// <summary>The records walk takes walk.follow as a list too, following each path's links together at every later
    /// hop: from the NPC's head parts, ExtraParts and TextureSet reach the hair's extra part and its texture set, where
    /// ExtraParts alone, as one string, reaches the extra part only.</summary>
    [Fact]
    public void TheRecordsWalkFollowsAListOfPaths()
    {
        string Run(WalkFollow follow) => RecordsTools.Records(_w.Svc, formids: new[] { FormIdToken.Of(_w.Keys["HcShapeNpc"]) },
                                                              walk: new() { seed_paths = new[] { "HeadParts" }, follow = follow }, limit: 0);
        var both = Run(new[] { "ExtraParts", "TextureSet" });
        Assert.Contains("HcShapeHairline", both);
        Assert.Contains("HcShapeHairTex", both);
        var one = Run("ExtraParts");
        Assert.Contains("HcShapeHairline", one);
        Assert.DoesNotContain("HcShapeHairTex", one);
    }

    /// <summary>walk.follow reads from the wire as one string or a list of strings, on both walks; another shape, an
    /// empty list and, on the asset walk, a '*parent' climb are refused in one sentence.</summary>
    [Fact]
    public void WalkFollowReadsAStringOrAList()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Equal(new[] { "Template" }, JsonSerializer.Deserialize<RecordsTools.RecordsWalk>("{\"follow\": \"Template\"}", web)!.follow!.Paths);
        Assert.Equal(new[] { "HeadParts", "WornArmor" }, JsonSerializer.Deserialize<RecordsTools.RecordsWalk>("{\"follow\": [\"HeadParts\", \"WornArmor\"]}", web)!.follow!.Paths);
        Assert.Equal(new[] { "HeadParts", "WornArmor" }, Values("{\"follow\": [\"HeadParts\", \"WornArmor\"]}").Follow);
        Assert.Null(Values("{}").Follow);
        string Refused(string walk)
        {
            Assert.NotNull(WalkGrammar.ReadAssetWalk(_w.Svc.Types, Walks.Of(walk), out var v) is { } err ? err : null);
            return WalkGrammar.ReadAssetWalk(_w.Svc.Types, Walks.Of(walk), out _)!;
        }
        Assert.StartsWith("error: walk.follow takes one field path as a string", Refused("{\"follow\": 3}"));
        Assert.StartsWith("error: walk.follow is an empty list", Refused("{\"follow\": []}"));
        Assert.StartsWith("error: walk.follow '*parent' climbs", Refused("{\"follow\": [\"*parent\"]}"));
    }

    WalkGrammar.Values Values(string walk)
    {
        Assert.Null(WalkGrammar.ReadAssetWalk(_w.Svc.Types, Walks.Of(walk), out var v));
        return v!;
    }
}

/// <summary>The shipped part-field list (walk-parts.json) is data the walk reads by name, so each entry is checked here
/// against Mutagen's own types: every type key names a record getter, and every path's fields exist on it.</summary>
[Trait("tier", "unit")]
public sealed class WalkPartsTests
{
    static readonly Assembly Skyrim = typeof(INpcGetter).Assembly;

    public static IEnumerable<object[]> Entries() =>
        WalkParts.ByType.SelectMany(kv => kv.Value.Select(path => new object[] { kv.Key, path }));

    [Theory]
    [MemberData(nameof(Entries))]
    public void EachPartFieldExistsOnItsType(string type, string path)
    {
        // The "*" paths are read on every type; they are checked on a type that has a model.
        var getter = type == WalkParts.AnyType ? typeof(IStaticGetter) : Skyrim.GetType($"Mutagen.Bethesda.Skyrim.I{type}Getter");
        Assert.True(getter is not null && typeof(IMajorRecordGetter).IsAssignableFrom(getter), $"'{type}' names no Skyrim record type");
        var at = getter!;
        foreach (var seg in path.Split('.'))
        {
            var p = WriteEngine.ResolveProperty(at, seg);
            Assert.True(p is not null, $"{type}: '{seg}' of '{path}' is no field of {at.Name}");
            at = p!.PropertyType;
            if (Nullable.GetUnderlyingType(at) is { } u) at = u;
        }
    }

    /// <summary>The list is not empty, and the walk reads an NPC's head parts by default.</summary>
    [Fact]
    public void TheListHoldsAnNpcsHeadParts() => Assert.Contains("HeadParts", WalkParts.For("Npc"));
}

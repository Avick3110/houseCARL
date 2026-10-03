using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;

namespace HousecarlMcpTests;

/// <summary>A gear-distribution graph for walk.through and walk.exclusions: an armor carried by a leveled list,
/// which an outfit carries, which an NPC wears; a container holding the armor; a recipe that makes it; and a form
/// list naming the NPC, past where a stop at Npc should end the walk.</summary>
public sealed class ReverseWalkTypesWorld : IDisposable
{
    public string Root { get; }
    public LoadOrderService Svc { get; }

    public FormKey Cuirass { get; }
    public FormKey Recipe { get; }
    public FormKey List { get; }
    public FormKey Outfit { get; }
    public FormKey Guard { get; }
    public FormKey GuardList { get; }
    public FormKey Chest { get; }

    public static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

    public ReverseWalkTypesWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-rwt-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));

        var masterKey = new ModKey("HcRwtMaster", ModType.Master);
        var master = new SkyrimMod(masterKey, SkyrimRelease.SkyrimSE);

        var cuirass = master.Armors.AddNew(); cuirass.EditorID = "HcRwtCuirass"; Cuirass = cuirass.FormKey;

        var recipe = master.ConstructibleObjects.AddNew(); recipe.EditorID = "HcRwtRecipe"; Recipe = recipe.FormKey;
        recipe.CreatedObject.SetTo(Cuirass);

        var list = master.LeveledItems.AddNew(); list.EditorID = "HcRwtList"; List = list.FormKey;
        list.Entries = new Noggog.ExtendedList<LeveledItemEntry>
        {
            new LeveledItemEntry { Data = new LeveledItemEntryData { Level = 1, Count = 1, Reference = new FormLink<IItemGetter>(Cuirass) } },
        };

        var outfit = master.Outfits.AddNew(); outfit.EditorID = "HcRwtOutfit"; Outfit = outfit.FormKey;
        outfit.Items = new Noggog.ExtendedList<IFormLinkGetter<IOutfitTargetGetter>> { new FormLink<IOutfitTargetGetter>(List) };

        var guard = master.Npcs.AddNew(); guard.EditorID = "HcRwtGuard"; Guard = guard.FormKey;
        guard.DefaultOutfit.SetTo(Outfit);

        var guardList = master.FormLists.AddNew(); guardList.EditorID = "HcRwtGuardList"; GuardList = guardList.FormKey;
        guardList.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(Guard));

        var chest = master.Containers.AddNew(); chest.EditorID = "HcRwtChest"; Chest = chest.FormKey;
        chest.Items = new Noggog.ExtendedList<ContainerEntry>
        {
            new ContainerEntry { Item = new ContainerItem { Item = new FormLink<IItemGetter>(Cuirass), Count = 1 } },
        };

        var instance = Path.Combine(Root, "inst");
        var modDir = Path.Combine(instance, "mods", "MasterMod");
        Directory.CreateDirectory(modDir);
        var masterName = masterKey.FileName.String;
        master.BeginWrite.ToPath(Path.Combine(modDir, masterName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + masterName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + masterName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+MasterMod\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "user.json")));
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;

namespace HousecarlMcpTests;

/// <summary>NPC template inheritance for walk.inherit: a carrier NPC wearing an outfit that holds a cuirass, NPCs
/// templated on it directly and through a leveled NPC list, an NPC whose own outfit its Use Inventory flag masks,
/// NPCs whose flag is clear, an NPC whose death item (not its inventory) carries the cuirass, and a second plugin
/// whose override sets the flag on an NPC the master leaves clear.</summary>
public sealed class WalkInheritWorld : IDisposable
{
    public string Root { get; }
    public LoadOrderService Svc { get; }

    public FormKey Cuirass { get; }
    public FormKey Carrier { get; }
    public FormKey Heir { get; }
    public FormKey LvlHeir { get; }
    public FormKey Masked { get; }
    public FormKey ClearOwn { get; }
    public FormKey ClearHeir { get; }
    public FormKey DeathCarrier { get; }
    public FormKey DeathHeir { get; }
    public FormKey PatchedHeir { get; }

    public static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

    public WalkInheritWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-iw-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));

        var masterKey = new ModKey("HcIwMaster", ModType.Master);
        var master = new SkyrimMod(masterKey, SkyrimRelease.SkyrimSE);

        var cuirass = master.Armors.AddNew(); cuirass.EditorID = "HcIwCuirass"; Cuirass = cuirass.FormKey;
        var list = master.LeveledItems.AddNew(); list.EditorID = "HcIwList";
        list.Entries = new Noggog.ExtendedList<LeveledItemEntry>
        {
            new LeveledItemEntry { Data = new LeveledItemEntryData { Level = 1, Count = 1, Reference = new FormLink<IItemGetter>(Cuirass) } },
        };
        var outfit = master.Outfits.AddNew(); outfit.EditorID = "HcIwOutfit";
        outfit.Items = new Noggog.ExtendedList<IFormLinkGetter<IOutfitTargetGetter>> { new FormLink<IOutfitTargetGetter>(list.FormKey) };
        var otherOutfit = master.Outfits.AddNew(); otherOutfit.EditorID = "HcIwOtherOutfit";

        Npc NewNpc(string editorId, NpcConfiguration.TemplateFlag flags)
        {
            var n = master.Npcs.AddNew();
            n.EditorID = editorId;
            n.Configuration.TemplateFlags = flags;
            return n;
        }

        var bare = NewNpc("HcIwBare", 0);
        var carrier = NewNpc("HcIwCarrier", 0); carrier.DefaultOutfit.SetTo(outfit); Carrier = carrier.FormKey;
        // Its own outfit is masked: it takes the carrier's inventory through its template.
        var heir = NewNpc("HcIwHeir", NpcConfiguration.TemplateFlag.Inventory);
        heir.Template.SetTo(Carrier); heir.DefaultOutfit.SetTo(otherOutfit); Heir = heir.FormKey;

        var lvln = master.LeveledNpcs.AddNew(); lvln.EditorID = "HcIwLvln";
        lvln.Entries = new Noggog.ExtendedList<LeveledNpcEntry>
        {
            new LeveledNpcEntry { Data = new LeveledNpcEntryData { Level = 1, Count = 1, Reference = new FormLink<INpcSpawnGetter>(Carrier) } },
        };
        var lvlHeir = NewNpc("HcIwLvlHeir", NpcConfiguration.TemplateFlag.Inventory);
        lvlHeir.Template.SetTo(lvln.FormKey); LvlHeir = lvlHeir.FormKey;

        var masked = NewNpc("HcIwMasked", NpcConfiguration.TemplateFlag.Inventory);
        masked.Template.SetTo(bare); masked.DefaultOutfit.SetTo(outfit); Masked = masked.FormKey;

        var clearOwn = NewNpc("HcIwClearOwn", NpcConfiguration.TemplateFlag.Traits);
        clearOwn.Template.SetTo(bare); clearOwn.DefaultOutfit.SetTo(outfit); ClearOwn = clearOwn.FormKey;
        var clearHeir = NewNpc("HcIwClearHeir", NpcConfiguration.TemplateFlag.Traits);
        clearHeir.Template.SetTo(Carrier); ClearHeir = clearHeir.FormKey;

        // The death item is Traits, not Inventory: an NPC reached only through it carries no inventory to its heirs.
        var deathCarrier = NewNpc("HcIwDeathCarrier", 0); deathCarrier.DeathItem.SetTo(list.FormKey); DeathCarrier = deathCarrier.FormKey;
        var deathHeir = NewNpc("HcIwDeathHeir", NpcConfiguration.TemplateFlag.Inventory);
        deathHeir.Template.SetTo(DeathCarrier); DeathHeir = deathHeir.FormKey;

        // The master leaves the flag clear; the patch's winning override sets it.
        var patchedHeir = NewNpc("HcIwPatchedHeir", 0);
        patchedHeir.Template.SetTo(Carrier); PatchedHeir = patchedHeir.FormKey;

        var instance = Path.Combine(Root, "inst");
        var modDir = Path.Combine(instance, "mods", "MasterMod");
        Directory.CreateDirectory(modDir);
        var masterName = masterKey.FileName.String;
        master.BeginWrite.ToPath(Path.Combine(modDir, masterName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var patchKey = new ModKey("HcIwPatch", ModType.Plugin);
        var patch = new SkyrimMod(patchKey, SkyrimRelease.SkyrimSE);
        var over = patch.Npcs.GetOrAddAsOverride(patchedHeir);
        over.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Inventory;
        var patchDir = Path.Combine(instance, "mods", "PatchMod");
        Directory.CreateDirectory(patchDir);
        var patchName = patchKey.FileName.String;
        patch.BeginWrite.ToPath(Path.Combine(patchDir, patchName)).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + masterName + "\r\n" + patchName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + masterName + "\r\n*" + patchName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+PatchMod\r\n+MasterMod\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "user.json")));
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

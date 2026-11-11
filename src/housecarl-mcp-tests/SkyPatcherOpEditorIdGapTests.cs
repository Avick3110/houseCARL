using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;
using static HousecarlMcpTests.SkyPatcherOverlayHarness;

namespace HousecarlMcpTests;

/// <summary>Without po3 Tweaks an op value or entry key naming a non-kept record by EditorID is not applied and its
/// warning names Tweaks; with Tweaks it applies and a name that finds nothing keeps the op's own wording.</summary>
[Trait("tier", "unit")]
public sealed class SkyPatcherOpEditorIdGapTests
{
    const string Missing = "powerofthree's Tweaks is not in this order";
    static readonly ModKey Other = new("HcGap", ModType.Plugin);
    static readonly FormKey Spell = new(Other, 0x900), Item = new(Other, 0x901), Item2 = new(Other, 0x902),
        DonorWeapon = new(Other, 0x903), DonorNpc = new(Other, 0x904);

    static SkyPatcherOverlay.SkyPatcherOverlayResult Run(string subfolder, string type, string line, bool tweaks)
    {
        var mod = new SkyrimMod(new ModKey("HcSpOv", ModType.Plugin), SkyrimRelease.SkyrimSE);
        ContainerEntry Entry(int count) => new() { Item = new ContainerItem { Item = Item.ToLink<IItemGetter>(), Count = count } };
        SkyrimMajorRecord record = type switch
        {
            "Npc" => mod.Npcs.AddNew(),
            "ConstructibleObject" => mod.ConstructibleObjects.AddNew(),
            "Book" => mod.Books.AddNew(),
            "Weapon" => mod.Weapons.AddNew(),
            "Outfit" => mod.Outfits.AddNew(),
            _ => throw new ArgumentException(type),
        };
        switch (record)
        {
            case Npc n: n.Items = new() { Entry(2) }; n.ActorEffect = new(); break;
            case ConstructibleObject c: c.Items = new() { Entry(5) }; break;
            case Weapon w: w.Model = new Model { File = "old.nif" }; break;
            case Outfit o: o.Items = new() { Item.ToLink<IOutfitTargetGetter>() }; break;
        }
        record.EditorID = "HcGapTarget";
        var resolver = new StubResolver { TweaksMissing = tweaks ? null : Missing };
        foreach (var (eid, fk, t) in new[] { ("HcSpell", Spell, "Spell"), ("HcItem", Item, "Item"), ("HcItem2", Item2, "Item"),
                     ("HcDonorWeapon", DonorWeapon, "Weapon"), ("HcDonorNpc", DonorNpc, "Npc") })
        {
            resolver.EditorIds[eid] = fk;
            resolver.EditorIdTypes[eid] = t;
        }
        resolver.WinnerLeafs[DonorWeapon] = "donor.nif";
        return Apply(record, record.FormKey, record.EditorID, subfolder, type, resolver, Line("g.ini", 1, line));
    }

    // {0} is the EditorID: an existing one applies with Tweaks and names Tweaks without; a typo with Tweaks keeps the op's wording
    public static TheoryData<string, string, string, string> Ops => new()
    {
        { "npc", "Npc", "spellsToAdd={0}", "form not resolvable (EditorID 'HcTypo' not found among records of the Spell type or group)" },
        { "npc", "Npc", "objectsToAdd={0}=2", "entry form 'HcTypo' not resolvable" },
        { "npc", "Npc", "objectsToRemove={0}", "form not resolvable; skipped" },
        { "npc", "Npc", "removeInventoryObjectsByCount={0}~1", "expected form~count" },
        { "npc", "Npc", "objectsToReplace={0}~HcItem2", "expected formA~formB" },
        { "constructibleObject", "ConstructibleObject", "changeCobjsCount={0}~3", "form not resolvable; skipped" },
        { "constructibleObject", "ConstructibleObject", "changeCobjsCountByMult={0}~2", "expected form~mult or mult" },
        { "book", "Book", "teachSpell={0}", "spell not resolvable" },
        { "weapon", "Weapon", "model={0}", "donor form not resolvable" },
    };

    // the existing record each op names, and its Mutagen type
    static (string Eid, string Type) Named(string op) => op.StartsWith("spellsToAdd") || op.StartsWith("teachSpell") ? ("HcSpell", "Spell")
        : op.StartsWith("model") ? ("HcDonorWeapon", "Weapon") : ("HcItem", "Item");

    [Theory, MemberData(nameof(Ops))]
    public void WithoutTweaksAnExistingEditorIdOpValueNamesTweaks(string subfolder, string type, string op, string _)
    {
        var (eid, eidType) = Named(op);
        var line = string.Format(op, eid);
        var key = op[..op.IndexOf('=')];
        var without = Run(subfolder, type, line, tweaks: false);
        Assert.DoesNotContain(without.Applied, a => a.Op == key);
        Assert.Equal($"g.ini:1: '{line}' — EditorID '{eid}' finds nothing: {Missing}, so the game keeps no EditorIDs of {eidType} records; not applied.",
            Assert.Single(without.Warnings));
        var with = Run(subfolder, type, line, tweaks: true);
        Assert.Contains(with.Applied, a => a.Op == key);
        Assert.Empty(with.Warnings);
    }

    [Theory, MemberData(nameof(Ops))] // pins unchanged behaviour: with Tweaks a name that finds nothing keeps the op's own wording
    public void WithTweaksAMissingEditorIdKeepsTheOpWording(string subfolder, string type, string op, string wording)
    {
        var w = Assert.Single(Run(subfolder, type, string.Format(op, "HcTypo"), tweaks: true).Warnings);
        Assert.Contains(wording, w);
        Assert.DoesNotContain("powerofthree", w);
    }

    [Fact] // pins unchanged behaviour: a teachSpell name that finds nothing with Tweaks names where, the op and its value, and that it is skipped
    public void WithTweaksAMissingTeachSpellNamesTheWholeLine() =>
        Assert.Equal("g.ini:1: 'teachSpell=HcTypo' — spell not resolvable; skipped.",
            Assert.Single(Run("book", "Book", "teachSpell=HcTypo", tweaks: true).Warnings));

    [Fact] // an op value whose '|' side SkyPatcher cannot read says so, not that its plugin is unparseable
    public void AMalformedOpAddressSaysItIsNoFormId() =>
        Assert.Contains("form not resolvable (not a FormID SkyPatcher can read after '|')",
            Assert.Single(Run("npc", "Npc", "spellsToAdd=HcGap.esp|zz", tweaks: true).Warnings));

    [Fact] // skin=<donor NPC EditorID> without Tweaks names the donor type's gap; with Tweaks it is the donor-copy warning, unchanged
    public void WithoutTweaksASkinDonorNpcEditorIdNamesTheNpcType()
    {
        var w = Assert.Single(Run("npc", "Npc", "skin=HcDonorNpc", tweaks: false).Warnings);
        Assert.Equal($"g.ini:1: 'skin=HcDonorNpc' — EditorID 'HcDonorNpc' finds nothing (neither Armor nor donor Npc): {Missing}, so the game keeps no EditorIDs of Npc records; not applied.", w);
        Assert.Contains("'HcDonorNpc' is a Npc, not a Armor", Assert.Single(Run("npc", "Npc", "skin=HcDonorNpc", tweaks: true).Warnings));
    }

    [Fact] // pins unchanged behaviour: formsToReplace has no form type or lookup scope, so without Tweaks its EditorID miss keeps the op's wording (#1334)
    public void AnUnscopedOpEditorIdDoesNotNameTweaks()
    {
        var w = Assert.Single(Run("outfit", "Outfit", "formsToReplace=HcItem=HcItem2", tweaks: false).Warnings);
        Assert.Contains("form(s) not resolvable; skipped", w);
        Assert.DoesNotContain("powerofthree", w);
    }

    [Fact] // formsToAdd looks its EditorIDs up among outfit items (Armor, LeveledItem), which the game keeps no names of without Tweaks
    public void AnOutfitItemEditorIdNamesTweaks()
        => Assert.Equal($"g.ini:1: 'formsToAdd=HcItem' — EditorID 'HcItem' finds nothing: {Missing}, so the game keeps no EditorIDs of OutfitTarget records; not applied.",
            Assert.Single(Run("outfit", "Outfit", "formsToAdd=HcItem", tweaks: false).Warnings));
}

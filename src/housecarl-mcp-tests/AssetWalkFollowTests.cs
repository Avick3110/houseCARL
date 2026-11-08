using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>An NPC and his parts for walk.follow and the winning-plugin home (DECISIONS 2026-11-08 (14)). Base.esm
/// defines the NPC, whose head part has a brow (ExtraParts) and a texture set (TextureSet); his skin armour's addon has a
/// gendered skin texture set (SkinTexture); his outfit's shirt has an addon (Items, Armature); his race's skin and a
/// skin addon's other race are mentions. Look.esp overrides him, the winner, adding a hair it defines with its own
/// texture set. A second NPC reaches one armour through his race's Skin first and his outfit's Items second.</summary>
public sealed class AssetWalkFollowWorld : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-walk-follow-");
    public static readonly ModKey Base = ModKey.FromFileName("HcFwBase.esm"), Look = ModKey.FromFileName("HcFwLook.esp");
    public LoadOrderService Svc { get; }
    public Dictionary<string, FormKey> Keys { get; } = new(StringComparer.Ordinal);

    public AssetWalkFollowWorld()
    {
        var b = new SkyrimMod(Base, SkyrimRelease.SkyrimSE);
        T Keep<T>(T r) where T : IMajorRecordGetter { Keys[r.EditorID!] = r.FormKey; return r; }
        TextureSet Txst(string edid)
        {
            var t = Keep(b.TextureSets.AddNew(edid));
            t.Diffuse = $@"textures\fw\{edid}.dds";
            return t;
        }
        ArmorAddon Arma(SkyrimMod m, string edid)
        {
            var a = Keep(m.ArmorAddons.AddNew(edid));
            a.WorldModel = new GenderedItem<Model?>(new Model { File = $@"meshes\fw\{edid}.nif" }, null);
            return a;
        }
        Armor Armo(string edid, ArmorAddon arma)
        {
            var a = Keep(b.Armors.AddNew(edid));
            a.Armature.Add(arma);
            return a;
        }
        HeadPart Part(SkyrimMod m, string edid)
        {
            var p = Keep(m.HeadParts.AddNew(edid));
            p.Model = new Model { File = $@"meshes\fw\{edid}.nif" };
            return p;
        }

        var brow = Part(b, "FwBrow");
        var head = Part(b, "FwHead");
        head.ExtraParts.Add(brow);
        head.TextureSet.SetTo(Txst("FwHeadTxst"));

        var otherRace = Keep(b.Races.AddNew("FwOtherRace"));
        otherRace.Skin.SetTo(Armo("FwOtherSkin", Arma(b, "FwOtherSkinAA")));
        var skinAa = Arma(b, "FwSkinAA");
        skinAa.SkinTexture = new GenderedItem<IFormLinkNullableGetter<ITextureSetGetter>>(
            new FormLinkNullable<ITextureSetGetter>(Txst("FwSkinTxst").FormKey), new FormLinkNullable<ITextureSetGetter>());
        skinAa.AdditionalRaces.Add(otherRace);
        var skin = Armo("FwSkin", skinAa);
        var outfit = Keep(b.Outfits.AddNew("FwOutfit"));
        outfit.Items = new() { new FormLink<IOutfitTargetGetter>(Armo("FwShirt", Arma(b, "FwShirtAA")).FormKey) };
        var race = Keep(b.Races.AddNew("FwRace"));
        race.Skin.SetTo(Armo("FwRaceSkin", Arma(b, "FwRaceSkinAA")));

        var npc = Keep(b.Npcs.AddNew("FwNpc"));
        npc.HeadParts.Add(head);
        npc.WornArmor.SetTo(skin);
        npc.DefaultOutfit.SetTo(outfit);
        npc.Race.SetTo(race);

        // The second NPC: one armour reached past depth through his race's Skin, then through his outfit's Items.
        var shared = Armo("FwShared", Arma(b, "FwSharedAA"));
        var race2 = Keep(b.Races.AddNew("FwRace2"));
        race2.Skin.SetTo(shared);
        var outfit2 = Keep(b.Outfits.AddNew("FwOutfit2"));
        outfit2.Items = new() { new FormLink<IOutfitTargetGetter>(shared.FormKey) };
        var npc2 = Keep(b.Npcs.AddNew("FwNpc2"));
        npc2.Race.SetTo(race2);
        npc2.DefaultOutfit.SetTo(outfit2);
        b.BeginWrite.ToPath(_mo2.InMod("FwBase", Base)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var l = new SkyrimMod(Look, SkyrimRelease.SkyrimSE);
        var hair = Part(l, "FwHair");
        var hairTxst = Keep(l.TextureSets.AddNew("FwHairTxst"));
        hairTxst.Diffuse = @"textures\fw\FwHairTxst.dds";
        hair.TextureSet.SetTo(hairTxst);
        var over = l.Npcs.GetOrAddAsOverride(npc);
        over.HeadParts.Add(hair);
        l.BeginWrite.ToPath(_mo2.InMod("FwLook", Look)).WithLoadOrder(new ISkyrimModGetter[] { b, l }).Write();

        var names = new[] { Base, Look }.Select(k => k.FileName.String).ToList();
        _mo2.Profile(string.Join("\r\n", names) + "\r\n", string.Join("", names.Select(n => "*" + n + "\r\n")), "+FwLook\r\n+FwBase\r\n");
        Svc = _mo2.Open();
    }

    public void Dispose() { Svc.Dispose(); _mo2.Delete(); }
}

/// <summary>walk.follow on the asset walk: each field kind it follows past walk.depth (a list of links, one link, a
/// gendered pair, a list through two records), a mention left a counted stop, a follow field no record carries said, a
/// record past depth on one link entered through a follow field on another; the seed's winning plugin as home; and the
/// records walk's follow as a list, each path at every hop.</summary>
[Trait("tier", "unit")]
public sealed class AssetWalkFollowTests : IClassFixture<AssetWalkFollowWorld>
{
    readonly AssetWalkFollowWorld _w;
    public AssetWalkFollowTests(AssetWalkFollowWorld w) => _w = w;

    AssetWalkData Walk(string walk, string seed = "FwNpc")
    {
        Assert.Null(WalkGrammar.ReadAssetWalk(_w.Svc.Types, Walks.Of(walk), out var values));
        return _w.Svc.AssetWalk(Array.Empty<string>(), new[] { _w.Keys[seed] }, null, null, values: values);
    }

    static bool Lists(AssetWalkData d, string edid) => d.Nodes.Any(n => n.Path.Contains(edid + ".", StringComparison.OrdinalIgnoreCase));

    /// <summary>The default walk enters what the seed lists and none of its parts' parts.</summary>
    [Fact]
    public void TheDefaultWalkStopsAtWhatTheSeedLists()
    {
        var d = Walk("true");
        Assert.True(Lists(d, "FwHead"));
        Assert.False(Lists(d, "FwBrow"));
        Assert.False(Lists(d, "FwHeadTxst"));
        Assert.False(Lists(d, "FwSkinAA"));
        Assert.False(Lists(d, "FwShirtAA"));
    }

    /// <summary>A list of links (a head part's ExtraParts) is followed past depth 1, and only the field named.</summary>
    [Fact]
    public void AListOfLinksIsFollowedPastDepth()
    {
        var d = Walk("{\"follow\": [\"ExtraParts\"]}");
        Assert.True(Lists(d, "FwBrow"));
        Assert.False(Lists(d, "FwHeadTxst"));
    }

    /// <summary>One link (a head part's TextureSet) is followed past depth 1.</summary>
    [Fact]
    public void OneLinkIsFollowedPastDepth()
    {
        var d = Walk("{\"follow\": [\"TextureSet\"]}");
        Assert.True(Lists(d, "FwHeadTxst"));
        Assert.False(Lists(d, "FwBrow"));
    }

    /// <summary>A gendered pair (an armour addon's SkinTexture) is followed, two hops past depth 1 through Armature.</summary>
    [Fact]
    public void AGenderedPairIsFollowedToTheEnd()
    {
        var d = Walk("{\"follow\": [\"Armature\", \"SkinTexture\"]}");
        Assert.True(Lists(d, "FwSkinAA"));
        Assert.True(Lists(d, "FwSkinTxst"));
        Assert.False(Walk("{\"follow\": [\"Armature\"]}").Nodes.Any(n => n.Path.Contains("FwSkinTxst", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The outfit's worn addon, three hops out, comes through Items then Armature; the race's skin and the
    /// skin addon's other race are mentions, counted stops, not listed.</summary>
    [Fact]
    public void TheOutfitsAddonComesThroughAndAMentionIsACountedStop()
    {
        var d = Walk("{\"follow\": [\"Items\", \"Armature\", \"SkinTexture\"]}");
        Assert.True(Lists(d, "FwShirtAA"));
        Assert.False(Lists(d, "FwRaceSkinAA"));
        Assert.False(Lists(d, "FwOtherSkinAA"));
        Assert.Contains(d.Notes, n => n.Contains("sit more than walk.depth=1 form-link hops from a seed off the walk.follow fields"));
    }

    /// <summary>A follow field no entered record carries followed nothing, and the walk says so by name.</summary>
    [Fact]
    public void AFollowFieldNoRecordCarriesIsSaid()
        => Assert.Contains("walk.follow 'HeadPart' names no link field on any record the walk entered, so it followed nothing; check its spelling against the record type's fields.",
                           Walk("{\"follow\": [\"HeadPart\"]}").Notes);

    /// <summary>An armour reached past depth through the race's Skin first is still entered through the outfit's Items,
    /// and is not counted past depth.</summary>
    [Fact]
    public void ARecordPastDepthOnOneLinkIsEnteredThroughAFollowField()
    {
        var d = Walk("{\"follow\": [\"Items\", \"Armature\"]}", seed: "FwNpc2");
        Assert.True(Lists(d, "FwSharedAA"));
        Assert.DoesNotContain(d.Notes, n => n.Contains("sit more than walk.depth"));
    }

    /// <summary>A seed read through 'winner' walks into the plugin its winning copy is in: the hair the override adds
    /// is entered and lists its mesh, and Look.esp is no boundary.</summary>
    [Fact]
    public void TheSeedsWinningPluginIsHome()
    {
        var d = Walk("true");
        Assert.True(Lists(d, "FwHair"));
        Assert.DoesNotContain(d.Notes, n => n.Contains("kept as boundaries") && n.Contains(AssetWalkFollowWorld.Look.FileName));
        Assert.True(Lists(Walk("{\"follow\": [\"TextureSet\"]}"), "FwHairTxst"));
    }

    /// <summary>The records walk's follow as a list follows each path at every later hop; one path is as before.</summary>
    [Fact]
    public void TheRecordsWalksFollowListFollowsEachPath()
    {
        IReadOnlyList<string?> Reached(WalkFollow follow)
        {
            var rows = _w.Svc.WalkForwardBatch(new[] { ScratchMo2.Fid(_w.Keys["FwNpc"]) }, null, follow, 16, 2000,
                                               Array.Empty<(string, bool)>(), null, out var refusal, out _);
            Assert.Null(refusal);
            return rows.Single().Nodes.Select(n => n.EditorId).ToList();
        }
        var one = Reached("ExtraParts");
        Assert.Contains("FwBrow", one);
        Assert.DoesNotContain("FwHeadTxst", one);
        var both = Reached(new[] { "ExtraParts", "TextureSet" });
        Assert.Contains("FwBrow", both);
        Assert.Contains("FwHeadTxst", both);
        Assert.Contains("FwHairTxst", both);
    }
}

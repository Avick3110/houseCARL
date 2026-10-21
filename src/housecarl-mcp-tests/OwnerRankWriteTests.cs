using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// #1204, the write half of #207: an override whose container item is owned by a faction in ANOTHER plugin reads as
/// <c>UntypedOwner</c>, and its rank word was then written as a FormID — remapped through the new master list (-1
/// became <c>0x01FFFFFF</c> here) and adding the plugin it seemed to point into as a master. The shape is the real one:
/// a base master defines the chests, the faction, an NPC and a global; an override plugin owns the chests' items.
/// Each written file is re-read WITH a link cache, so the owner types and its rank is the raw int on disk.
/// </summary>
[Trait("tier", "integration")]
public sealed class OwnerRankWriteTests : IDisposable
{
    const string BaseName = "HcOrBase.esm";
    const string OverName = "HcOrOver.esp";

    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-owner-rank-write-" + Guid.NewGuid().ToString("N"));
    readonly string _basePath, _overPath;
    readonly FormKey _minusOne, _minusTwo, _rankThree, _ownFaction, _npcOwned, _faction, _npc, _global;

    public OwnerRankWriteTests()
    {
        Directory.CreateDirectory(_root);
        var b = new SkyrimMod(ModKey.FromNameAndExtension(BaseName), SkyrimRelease.SkyrimSE);
        _faction = b.Factions.AddNew("HcOrFaction").FormKey;
        _npc = b.Npcs.AddNew("HcOrNpc").FormKey;
        var global = new GlobalShort(b) { EditorID = "HcOrGlobal" };
        b.Globals.Add(global);
        _global = global.FormKey;
        FormKey Chest(string edid) => b.Containers.AddNew(edid).FormKey;
        _minusOne = Chest("HcOrMinusOne"); _minusTwo = Chest("HcOrMinusTwo"); _rankThree = Chest("HcOrRankThree");
        _npcOwned = Chest("HcOrNpcOwned"); _ownFaction = Chest("HcOrOwnFaction");
        _basePath = Path.Combine(_root, BaseName);
        b.BeginWrite.ToPath(_basePath).WithNoLoadOrder().Write();

        var o = new SkyrimMod(ModKey.FromNameAndExtension(OverName), SkyrimRelease.SkyrimSE);
        var own = o.Factions.AddNew("HcOrOwnFactionDef").FormKey;
        void Owned(FormKey chest, OwnerTarget owner)
        {
            var c = new Container(chest, SkyrimRelease.SkyrimSE) { EditorID = "Over" + chest.ID.ToString("X6") };
            c.Items = new() { new ContainerEntry { Item = new ContainerItem { Count = 1 }, Data = new ExtraData { ItemCondition = 1f, Owner = owner } } };
            o.Containers.Add(c);
        }
        static FactionOwner At(FormKey faction, int rank) => new() { Faction = new FormLink<IFactionGetter>(faction), RequiredRank = rank };
        Owned(_minusOne, At(_faction, -1));
        Owned(_minusTwo, At(_faction, -2));
        Owned(_rankThree, At(_faction, 3));
        Owned(_npcOwned, new NpcOwner { Npc = new FormLink<INpcGetter>(_npc), Global = new FormLink<IGlobalGetter>(_global) });
        Owned(_ownFaction, At(own, -1));
        _overPath = Path.Combine(_root, OverName);
        using var baseOv = SkyrimMod.CreateFromBinaryOverlay(_basePath, SkyrimRelease.SkyrimSE);
        o.BeginWrite.ToPath(_overPath).WithLoadOrder(new ISkyrimModGetter[] { baseOv }).Write();
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ } }

    WritePatchBuilder.PatchEdit NoOp(FormKey chest) =>
        new() { Target = chest, Path = new[] { "EditorID" }, Verb = "Set", Value = "Over" + chest.ID.ToString("X6") };

    /// <summary>Each chest's owner in <paramref name="path"/>, typed through a cache over the base and that file, plus its masters.</summary>
    (Dictionary<FormKey, IOwnerTargetGetter> owners, string[] masters) ReadBack(string path)
    {
        using var baseOv = SkyrimMod.CreateFromBinaryOverlay(_basePath, SkyrimRelease.SkyrimSE);
        using var probe = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        var cache = new ISkyrimModGetter[] { baseOv, probe }.ToImmutableLinkCache();
        using var typed = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE, new() { LinkCache = cache });
        var owners = typed.Containers.ToDictionary(c => c.FormKey, c => c.Items![0].Data!.Owner);
        return (owners, typed.ModHeader.MasterReferences.Select(m => m.Master.FileName.String).ToArray());
    }

    static int Rank(IOwnerTargetGetter owner) => Assert.IsAssignableFrom<IFactionOwnerGetter>(owner).RequiredRank;

    [Fact]
    public void ADefaultLanePatchKeepsCrossPluginRanksAndGainsNoMasterForThem()
    {
        var outPath = Path.Combine(_root, "HcOrPatch.esp");
        using (var r = LoadOrderResolver.Build(new[] { _basePath, _overPath }))
        {
            var o = WritePatchBuilder.Apply(r, TestCorpus.Rulebook, new[] { NoOp(_minusOne), NoOp(_minusTwo), NoOp(_rankThree), NoOp(_npcOwned) }, outPath, extend: false);
            Assert.True(o.Success, o.Error);
        }
        var (owners, masters) = ReadBack(outPath);
        Assert.Equal(-1, Rank(owners[_minusOne]));
        Assert.Equal(-2, Rank(owners[_minusTwo]));
        Assert.Equal(3, Rank(owners[_rankThree]));
        Assert.Equal(_global, Assert.IsAssignableFrom<INpcOwnerGetter>(owners[_npcOwned]).Global.FormKey);   // an NPC owner's second word stays a link
        Assert.Equal(new[] { BaseName }, masters);
    }

    [Fact]
    public void AnInPlaceEditKeepsRanksOnTheEditedAndTheUntouchedRecords()
    {
        using (var r = LoadOrderResolver.Build(new[] { _basePath, _overPath }))
        {
            var o = WritePatchBuilder.ApplyInPlace(r, TestCorpus.Rulebook, new[] { NoOp(_minusOne) }, _overPath, OverName);
            Assert.True(o.Success, o.Error);
        }
        var (owners, masters) = ReadBack(_overPath);
        Assert.Equal(-1, Rank(owners[_minusOne]));
        Assert.Equal(-2, Rank(owners[_minusTwo]));
        Assert.Equal(3, Rank(owners[_rankThree]));
        Assert.Equal(-1, Rank(owners[_ownFaction]));                                                      // same-file owner: typed on read all along
        Assert.Equal(_global, Assert.IsAssignableFrom<INpcOwnerGetter>(owners[_npcOwned]).Global.FormKey);
        Assert.Equal(new[] { BaseName }, masters);
    }
}

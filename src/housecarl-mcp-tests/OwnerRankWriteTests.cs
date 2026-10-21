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
/// a base master defines the chests, an NPC, a leveled list, the faction and a global; a second plugin injects a faction
/// into the base's FormID space; an override plugin, whose first master is not the base, owns the items and entries.
/// Each written file is re-read WITH a link cache, so the owner types and its rank is the raw int on disk.
/// </summary>
[Trait("tier", "integration")]
public sealed class OwnerRankWriteTests : IDisposable
{
    const string ExtraName = "HcOrExtra.esm";
    const string BaseName = "HcOrBase.esm";
    const string InjName = "HcOrInj.esp";
    const string OverName = "HcOrOver.esp";

    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-owner-rank-write-" + Guid.NewGuid().ToString("N"));
    readonly string _extraPath, _basePath, _injPath, _overPath;
    readonly FormKey _minusOne, _minusTwo, _rankThree, _ownFaction, _npcOwned, _injectedOwned, _npcInventory, _leveled;
    readonly FormKey _faction, _npc, _global, _ownFactionDef, _extraUser;

    public OwnerRankWriteTests()
    {
        Directory.CreateDirectory(_root);
        var e = new SkyrimMod(ModKey.FromNameAndExtension(ExtraName), SkyrimRelease.SkyrimSE);
        var misc = e.MiscItems.AddNew("HcOrExtraMisc").FormKey;
        _extraPath = Path.Combine(_root, ExtraName);
        e.BeginWrite.ToPath(_extraPath).WithNoLoadOrder().Write();

        var b = new SkyrimMod(ModKey.FromNameAndExtension(BaseName), SkyrimRelease.SkyrimSE);
        _faction = b.Factions.AddNew("HcOrFaction").FormKey;
        _npc = b.Npcs.AddNew("HcOrNpc").FormKey;
        _npcInventory = b.Npcs.AddNew("HcOrNpcInventory").FormKey;
        _leveled = b.LeveledNpcs.AddNew("HcOrLeveled").FormKey;
        var global = new GlobalShort(b) { EditorID = "HcOrGlobal" };
        b.Globals.Add(global);
        _global = global.FormKey;
        FormKey Chest(string edid) => b.Containers.AddNew(edid).FormKey;
        _minusOne = Chest("HcOrMinusOne"); _minusTwo = Chest("HcOrMinusTwo"); _rankThree = Chest("HcOrRankThree");
        _npcOwned = Chest("HcOrNpcOwned"); _ownFaction = Chest("HcOrOwnFaction"); _injectedOwned = Chest("HcOrInjectedOwned");
        _basePath = Path.Combine(_root, BaseName);
        b.BeginWrite.ToPath(_basePath).WithNoLoadOrder().Write();

        // A faction whose key says the base but whose FACT lives only here.
        var injected = new FormKey(b.ModKey, 0xA00);
        var inj = new SkyrimMod(ModKey.FromNameAndExtension(InjName), SkyrimRelease.SkyrimSE);
        inj.Factions.Add(new Faction(injected, SkyrimRelease.SkyrimSE) { EditorID = "HcOrInjected" });
        _injPath = Path.Combine(_root, InjName);
        using (var baseOv = SkyrimMod.CreateFromBinaryOverlay(_basePath, SkyrimRelease.SkyrimSE))
            inj.BeginWrite.ToPath(_injPath).WithLoadOrder(new ISkyrimModGetter[] { baseOv }).Write();

        var o = new SkyrimMod(ModKey.FromNameAndExtension(OverName), SkyrimRelease.SkyrimSE);
        _ownFactionDef = o.Factions.AddNew("HcOrOwnFactionDef").FormKey;
        static ExtraData Owning(OwnerTarget owner) => new() { ItemCondition = 1f, Owner = owner };
        static ContainerEntry Entry(OwnerTarget owner, FormKey? item = null) => new()
        {
            Item = new ContainerItem { Count = 1, Item = new FormLink<IItemGetter>(item ?? FormKey.Null) },
            Data = Owning(owner),
        };
        void Owned(FormKey chest, OwnerTarget owner)
        {
            var c = new Container(chest, SkyrimRelease.SkyrimSE) { EditorID = "Over" + chest.ID.ToString("X6") };
            c.Items = new() { Entry(owner) };
            o.Containers.Add(c);
        }
        static FactionOwner At(FormKey faction, int rank) => new() { Faction = new FormLink<IFactionGetter>(faction), RequiredRank = rank };
        Owned(_minusOne, At(_faction, -1));
        Owned(_minusTwo, At(_faction, -2));
        Owned(_rankThree, At(_faction, 3));
        Owned(_npcOwned, new NpcOwner { Npc = new FormLink<INpcGetter>(_npc), Global = new FormLink<IGlobalGetter>(_global) });
        Owned(_ownFaction, At(_ownFactionDef, -1));
        Owned(_injectedOwned, At(injected, -1));
        o.Npcs.Add(new Npc(_npcInventory, SkyrimRelease.SkyrimSE) { EditorID = "Over" + _npcInventory.ID.ToString("X6"), Items = new() { Entry(At(_faction, -1)) } });
        o.LeveledNpcs.Add(new LeveledNpc(_leveled, SkyrimRelease.SkyrimSE)
        {
            EditorID = "Over" + _leveled.ID.ToString("X6"),
            Entries = new() { new LeveledNpcEntry { Data = new LeveledNpcEntryData { Level = 1, Count = 1 }, ExtraData = Owning(At(_faction, -1)) } },
        });
        // A record of the override's own that links the extra master, so the override's master 0 is not the base.
        var user = new Container(o) { EditorID = "HcOrExtraUser" };
        user.Items = new() { Entry(At(_faction, 5), misc) };
        o.Containers.Add(user);
        _extraUser = user.FormKey;
        _overPath = Path.Combine(_root, OverName);
        using (var extraOv = SkyrimMod.CreateFromBinaryOverlay(_extraPath, SkyrimRelease.SkyrimSE))
        using (var baseOv = SkyrimMod.CreateFromBinaryOverlay(_basePath, SkyrimRelease.SkyrimSE))
            o.BeginWrite.ToPath(_overPath).WithLoadOrder(new ISkyrimModGetter[] { extraOv, baseOv }).Write();
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ } }

    string[] Order => new[] { _extraPath, _basePath, _injPath, _overPath };

    static WritePatchBuilder.PatchEdit NoOp(FormKey target) =>
        new() { Target = target, Path = new[] { "EditorID" }, Verb = "Set", Value = "Over" + target.ID.ToString("X6") };

    WritePatchBuilder.PatchEdit[] Overrides => new[]
        { NoOp(_minusOne), NoOp(_minusTwo), NoOp(_rankThree), NoOp(_npcOwned), NoOp(_injectedOwned), NoOp(_npcInventory), NoOp(_leveled) };

    /// <summary>Each owner in <paramref name="path"/> by its record, typed through a cache over the fixture (with <paramref name="basePath"/> as the base), plus its masters.</summary>
    (Dictionary<FormKey, IOwnerTargetGetter> owners, string[] masters) ReadBack(string path, string? basePath = null)
    {
        var opened = new[] { _extraPath, basePath ?? _basePath, _injPath, path }
            .Select(p => SkyrimMod.CreateFromBinaryOverlay(p, SkyrimRelease.SkyrimSE)).ToList();
        try
        {
            var cache = opened.ToImmutableLinkCache();
            using var typed = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE, new() { LinkCache = cache });
            var owners = typed.Containers.ToDictionary(c => c.FormKey, c => c.Items![0].Data!.Owner!);
            foreach (var n in typed.Npcs) owners[n.FormKey] = n.Items![0].Data!.Owner!;
            foreach (var l in typed.LeveledNpcs) owners[l.FormKey] = l.Entries![0].ExtraData!.Owner!;
            return (owners, typed.ModHeader.MasterReferences.Select(m => m.Master.FileName.String).ToArray());
        }
        finally { foreach (var m in opened) m.Dispose(); }
    }

    static int Rank(IOwnerTargetGetter owner) => Assert.IsAssignableFrom<IFactionOwnerGetter>(owner).RequiredRank;

    void AssertCrossPluginRanks(Dictionary<FormKey, IOwnerTargetGetter> owners)
    {
        Assert.Equal(-1, Rank(owners[_minusOne]));
        Assert.Equal(-2, Rank(owners[_minusTwo]));
        Assert.Equal(3, Rank(owners[_rankThree]));
        Assert.Equal(-1, Rank(owners[_injectedOwned]));
        Assert.Equal(-1, Rank(owners[_npcInventory]));
        Assert.Equal(-1, Rank(owners[_leveled]));
        Assert.Equal(_global, Assert.IsAssignableFrom<INpcOwnerGetter>(owners[_npcOwned]).Global.FormKey);   // an NPC owner's second word stays a link
    }

    [Fact]
    public void ADefaultLanePatchKeepsCrossPluginRanksAndGainsNoMasterForThem()
    {
        var outPath = Path.Combine(_root, "HcOrPatch.esp");
        using (var r = LoadOrderResolver.Build(Order))
        {
            var o = WritePatchBuilder.Apply(r, TestCorpus.Rulebook, Overrides, outPath, extend: false);
            Assert.True(o.Success, o.Error);
        }
        var (owners, masters) = ReadBack(outPath);
        AssertCrossPluginRanks(owners);
        Assert.Equal(new[] { BaseName }, masters);   // the override's master 0 is the extra master, the patch's is the base
    }

    [Fact]
    public void ADryRunPreviewsTheMastersTheWriteWillDeclare()
    {
        using var r = LoadOrderResolver.Build(Order);
        var o = WritePatchBuilder.Apply(r, TestCorpus.Rulebook, Overrides, Path.Combine(_root, "HcOrDry.esp"), extend: false, dryRun: true);
        Assert.True(o.Success, o.Error);
        Assert.Equal(new[] { BaseName }, o.Masters);
    }

    [Fact]
    public void AnInPlaceEditKeepsRanksOnTheEditedAndTheUntouchedRecords()
    {
        using (var r = LoadOrderResolver.Build(Order))
        {
            var o = WritePatchBuilder.ApplyInPlace(r, TestCorpus.Rulebook, new[] { NoOp(_minusOne) }, _overPath, OverName);
            Assert.True(o.Success, o.Error);
        }
        var (owners, masters) = ReadBack(_overPath);
        AssertCrossPluginRanks(owners);
        Assert.Equal(-1, Rank(owners[_ownFaction]));                                                      // same-file owner: typed on read all along
        Assert.Equal(new[] { ExtraName, BaseName }, masters);
    }

    [Fact]
    public void AnInPlaceRemovalKeepsRanksOnTheRecordsItReserializes()
    {
        using (var r = LoadOrderResolver.Build(Order))
        {
            var o = WritePatchBuilder.RemoveRecordsInPlace(r, new[] { _ownFaction }, _overPath, OverName);
            Assert.True(o.Success, o.Error);
        }
        var (owners, masters) = ReadBack(_overPath);
        AssertCrossPluginRanks(owners);
        Assert.DoesNotContain(_ownFaction, owners.Keys);
        Assert.Equal(new[] { ExtraName, BaseName }, masters);
    }

    /// <summary>A plugin holding a weapon (no owner) and an override of the -1 chest, each with a script block whose count claims 65535 scripts.</summary>
    ISkyrimModDisposableGetter BadScripts()
    {
        var bad = new SkyrimMod(ModKey.FromNameAndExtension("HcOrBad.esp"), SkyrimRelease.SkyrimSE);
        static VirtualMachineAdapter Vmad() => new() { Scripts = new() { new ScriptEntry { Name = "HcOrScript" } } };
        var w = bad.Weapons.AddNew("HcOrBadWeapon");
        w.VirtualMachineAdapter = Vmad();
        var c = new Container(_minusOne, SkyrimRelease.SkyrimSE) { EditorID = "HcOrBadChest", VirtualMachineAdapter = Vmad() };
        c.Items = new() { new ContainerEntry
        {
            Item = new ContainerItem { Count = 1, Item = new FormLink<IItemGetter>(FormKey.Null) },
            Data = new ExtraData { ItemCondition = 1f, Owner = new FactionOwner { Faction = new FormLink<IFactionGetter>(_faction), RequiredRank = -1 } },
        } };
        bad.Containers.Add(c);
        var path = Path.Combine(_root, "HcOrBad.esp");
        using (var baseOv = SkyrimMod.CreateFromBinaryOverlay(_basePath, SkyrimRelease.SkyrimSE))
            bad.BeginWrite.ToPath(path).WithLoadOrder(new ISkyrimModGetter[] { baseOv }).Write();
        var bytes = File.ReadAllBytes(path);
        for (int i = 0; i <= bytes.Length - 4; i++)
            if (bytes.AsSpan(i, 4).SequenceEqual("VMAD"u8)) { bytes[i + 10] = 0xFF; bytes[i + 11] = 0xFF; }
        File.WriteAllBytes(path, bytes);
        return SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
    }

    [Fact]
    public void ASourceFetchPassesARecordWithNoUntypedFactionOwnerThroughUncopied()
    {
        using var bad = BadScripts();
        var weapon = bad.Weapons.Single();
        Assert.Same(weapon, OwnerRanks.Settled(weapon, _ => true));   // a deep copy would read the broken script block
    }

    [Fact]
    public void ASourceFetchThatCannotCopyAnOwnedRecordReportsTheParseFaultItself()
    {
        using var bad = BadScripts();
        var chest = bad.Containers.Single();
        var ex = Record.Exception(() => OwnerRanks.Settled(chest, fk => fk == _faction));
        Assert.NotNull(ex);
        Assert.IsNotType<System.Reflection.TargetInvocationException>(ex);
    }

    [Fact]
    public void AMergeOfADonorWithCrossPluginFactionOwnersKeepsTheirRanks()
    {
        var outKey = ModKey.FromNameAndExtension("HcOrMerged.esp");
        var outPath = Path.Combine(_root, outKey.FileName.String);
        var dict = new Dictionary<FormKey, FormKey> { [_ownFactionDef] = new(outKey, 0x800), [_extraUser] = new(outKey, 0x801) };
        var paths = new Dictionary<string, string> { [ExtraName] = _extraPath, [BaseName] = _basePath };
        using (var r = LoadOrderResolver.Build(Order))
        {
            var m = WritePatchBuilder.MergeBuild(new[] { (OverName, _overPath, ModKey.FromNameAndExtension(OverName)) }, outKey, dict,
                new[] { ExtraName, BaseName }, n => paths.GetValueOrDefault(n), outPath, dataDir: null, OwnerRanks.FactionIn(r.Capture()));
            Assert.True(m.Success, m.Error);
        }
        var (owners, masters) = ReadBack(outPath);
        AssertCrossPluginRanks(owners);
        Assert.Equal(new[] { ExtraName, BaseName }, masters);
    }

    [Fact]
    public void ACompactIntoANewFolderRepointsAReferencerAndKeepsItsRanks()
    {
        var newBase = Path.Combine(_root, "compacted", BaseName);
        Directory.CreateDirectory(Path.GetDirectoryName(newBase)!);
        var dict = new Dictionary<FormKey, FormKey> { [_faction] = new(_faction.ModKey, 0x900) };
        using (var r = LoadOrderResolver.Build(Order))
        {
            var c = WritePatchBuilder.CompactBuild(_basePath, _faction.ModKey, dict, _ => null, newBase, esl: false, floor: 0x900,
                dataDir: null, OwnerRanks.FactionIn(r.Capture()));
            Assert.True(c.Success, c.Error);
            var rep = RemapEngine.RepointInPlace(r, OverName, dict);   // the view still resolves the base to its original file
            Assert.True(rep.Success, rep.Error);
        }
        var (owners, masters) = ReadBack(_overPath, newBase);
        AssertCrossPluginRanks(owners);
        Assert.Equal(new FormKey(_faction.ModKey, 0x900), Assert.IsAssignableFrom<IFactionOwnerGetter>(owners[_minusOne]).Faction.FormKey);
        Assert.Equal(new[] { ExtraName, BaseName }, masters);
    }
}

using System.Runtime.CompilerServices;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A small order for the typed index (#1158): armors with armatures, a form list of them, an NPC template
/// chain, a Global whose override changes its variant, and one key a later plugin redefines as a different record type.</summary>
public sealed class TypedIndexWorld : IDisposable
{
    public string Root { get; }
    public string MasterName => "HcTypedMaster.esm";
    public string PatchName => "HcTypedPatch.esp";
    public LoadOrderService Svc { get; }
    public FormKey Armor { get; }
    public FormKey List { get; }
    public FormKey Npc { get; }
    public FormKey Global { get; }
    public FormKey Clash { get; }

    public TypedIndexWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-typedindex-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));
        var master = new SkyrimMod(ModKey.FromFileName(MasterName), SkyrimRelease.SkyrimSE);
        var kw = master.Keywords.AddNew("HcTypedKeyword");
        var armas = Enumerable.Range(0, 6).Select(i => master.ArmorAddons.AddNew($"HcTypedArma{i}")).ToList();
        var armors = new List<Armor>();
        for (int i = 0; i < 3; i++)
        {
            var a = master.Armors.AddNew($"HcTypedArmor{i}");
            a.Armature.Add(armas[2 * i].ToLink());
            a.Armature.Add(armas[2 * i + 1].ToLink());
            a.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>> { kw.ToLink() };
            armors.Add(a);
        }
        var list = master.FormLists.AddNew("HcTypedList");
        foreach (var a in armors) list.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(a.FormKey));
        var npcBase = master.Npcs.AddNew("HcTypedNpcBase");
        var npc = master.Npcs.AddNew("HcTypedNpc");
        npc.Template.SetTo(npcBase);
        npc.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Stats;
        var glob = new GlobalFloat(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcTypedGlobal", Data = 1.5f };
        master.Globals.Add(glob);
        var clash = master.Armors.AddNew("HcTypedClashArmor");

        var patch = new SkyrimMod(ModKey.FromFileName(PatchName), SkyrimRelease.SkyrimSE);
        WriteEngine.GenericGetOrAddAsOverride(patch, armors[0]).EditorID = "HcTypedArmor0Patched";
        // The Global variant changes: the same seek type, a different class.
        patch.Globals.Add(new GlobalShort(glob.FormKey, SkyrimRelease.SkyrimSE) { EditorID = "HcTypedGlobal", Data = 2 });
        // A true seek-type mismatch: the patch redefines the master's armor as a weapon.
        patch.Weapons.Add(new Weapon(clash.FormKey, SkyrimRelease.SkyrimSE) { EditorID = "HcTypedClashWeapon" });

        var instance = Path.Combine(Root, "inst");
        var mods = Path.Combine(instance, "mods");
        Directory.CreateDirectory(Path.Combine(mods, "TypedMaster"));
        Directory.CreateDirectory(Path.Combine(mods, "TypedPatch"));
        master.BeginWrite.ToPath(Path.Combine(mods, "TypedMaster", MasterName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        patch.BeginWrite.ToPath(Path.Combine(mods, "TypedPatch", PatchName)).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + MasterName + "\r\n" + PatchName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + MasterName + "\r\n*" + PatchName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+TypedPatch\r\n+TypedMaster\r\n");
        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "user.json")));

        Armor = armors[0].FormKey; List = list.FormKey; Npc = npc.FormKey; Global = glob.FormKey; Clash = clash.FormKey;
    }

    public static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

public sealed class TypedIndexFixture : IDisposable
{
    public TypedIndexWorld W { get; } = new();
    public void Dispose() => W.Dispose();
}

/// <summary>Every in-order fetch is typed by the order index (#1158): no lane walks a plugin from the top to find one record.</summary>
[Trait("tier", "integration")]
public sealed class TypedIndexTests : IClassFixture<TypedIndexFixture>
{
    readonly TypedIndexWorld _w;
    public TypedIndexTests(TypedIndexFixture f) => _w = f.W;

    long[] Costs() { var c = _w.Svc.Counters; return new[] { c.BodySeeks, c.TypedSeeks, c.CollectPasses, c.TypedCollectPasses }; }
    long[] Since(long[] before) => Costs().Zip(before, (a, b) => a - b).ToArray();

    public static TheoryData<string> Lanes => new()
    {
        "cold read", "resolve names", "link scan", "link scan on a list", "link scan on a formid set",
        "winner scan", "walk", "template chain", "tree", "tree of a mixed key",
    };

    string Run(string lane) => lane switch
    {
        "cold read" => RecordsTools.Records(_w.Svc, formids: new[] { Fid(_w.Armor), Fid(_w.Global), Fid(_w.Clash), Fid(_w.List) }),
        "resolve names" => RecordsTools.Records(_w.Svc, formids: new[] { Fid(_w.Armor) },
            project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Armature", "Keywords" }, resolve_names = true }),
        "link scan" => RecordsTools.Records(_w.Svc, types: new[] { "ARMO" }, where: new[] { "Armature[*any]->editorid startswith HcTypedArma3" }, counts_only: true),
        "link scan on a list" => RecordsTools.Records(_w.Svc, types: new[] { "FLST" }, where: new[] { "Items[*any]->editorid startswith HcTypedArmor" }),
        "link scan on a formid set" => RecordsTools.Records(_w.Svc, formids: new[] { Fid(_w.List) }, where: new[] { "Items[*any]->editorid startswith HcTypedArmor2" }),
        "winner scan" => RecordsTools.Records(_w.Svc, types: new[] { "ARMO" }, plugins: new RecordsTools.RecordsScope { names = new[] { _w.MasterName } },
            where: new[] { "editorid startswith HcTyped" }, where_source: "winner"),
        "walk" => RecordsTools.Records(_w.Svc, formids: new[] { Fid(_w.List) }, walk: new RecordsTools.RecordsWalk { depth = 3 }),
        "template chain" => RecordsTools.Records(_w.Svc, formids: new[] { Fid(_w.Npc) }, walk: new RecordsTools.RecordsWalk { follow = "Template" },
            project: new RecordsTools.RecordsProject { form = "chain" }),
        "tree" => RecordsTools.Records(_w.Svc, formids: new[] { Fid(_w.Global), Fid(_w.Armor) }, project: new RecordsTools.RecordsProject { form = "tree" }),
        "tree of a mixed key" => RecordsTools.Records(_w.Svc, formids: new[] { Fid(_w.Clash) }, project: new RecordsTools.RecordsProject { form = "tree" }),
        _ => throw new ArgumentException(lane),
    };

    static string Fid(FormKey fk) => TypedIndexWorld.Fid(fk);

    /// <summary>The lane reads bodies, and every seek and every gather walk it makes is answered by the record's own groups.</summary>
    [Theory]
    [MemberData(nameof(Lanes))]
    public void EveryFetchOnALaneIsTyped(string lane)
    {
        var before = Costs();
        var r = Run(lane);
        Assert.False(r.StartsWith("error", StringComparison.Ordinal), r);
        var d = Since(before);
        Assert.True(d[0] + d[2] > 0, $"{lane} read no body: {string.Join(",", d)}");
        Assert.Equal(d[0], d[1]);
        Assert.Equal(d[2], d[3]);
    }

    /// <summary>A key a later plugin redefines as another record type is sought in each provider's own groups.
    /// Fails when the index keeps one type per key: the master's armor is then sought as a weapon and found only flat.</summary>
    [Fact]
    public void AKeyWhoseProvidersDifferInTypeIsSoughtTypedInEach()
    {
        var pin = _w.Svc.CapturePin();
        using var session = pin.Resolver.OpenSession();
        var before = Costs();
        Assert.IsAssignableFrom<IArmorGetter>(pin.View.GetRecord(session, _w.MasterName, _w.Clash));
        Assert.IsAssignableFrom<IWeaponGetter>(pin.View.GetRecord(session, _w.PatchName, _w.Clash));
        Assert.Equal(new long[] { 2, 2, 0, 0 }, Since(before));
        Assert.True(typeof(IWeaponGetter).IsAssignableFrom(pin.View.WinnerClassOf(_w.Clash)));
    }

    /// <summary>The same in a gather: the master's walk finds its armor copy of the mixed key beside a plain armor, typed.</summary>
    [Fact]
    public void AGatherOfAMixedKeyIsTyped()
    {
        var pin = _w.Svc.CapturePin();
        using var session = pin.Resolver.OpenSession();
        var sink = new Dictionary<FormKey, IMajorRecordGetter>();
        var before = Costs();
        pin.View.CollectRecords(session, _w.MasterName, new[] { _w.Clash, _w.Armor }, sink);
        Assert.IsAssignableFrom<IArmorGetter>(sink[_w.Clash]);
        Assert.IsAssignableFrom<IArmorGetter>(sink[_w.Armor]);
        Assert.Equal(new long[] { 0, 0, 1, 1 }, Since(before));
    }

    /// <summary>A Global whose override changes variant shares one seek type, and the winner's class is the override's.</summary>
    [Fact]
    public void AGlobalVariantKeepsOneSeekTypeAndTheWinnersClass()
    {
        var pin = _w.Svc.CapturePin();
        using var session = pin.Resolver.OpenSession();
        var before = Costs();
        Assert.IsAssignableFrom<IGlobalFloatGetter>(pin.View.GetRecord(session, _w.MasterName, _w.Global));
        Assert.IsAssignableFrom<IGlobalShortGetter>(pin.View.GetRecord(session, _w.PatchName, _w.Global));
        Assert.Equal(new long[] { 2, 2, 0, 0 }, Since(before));
        Assert.True(typeof(IGlobalShortGetter).IsAssignableFrom(pin.View.WinnerClassOf(_w.Global)));
    }

    /// <summary>A plugin the index says holds no copy of a key answers absent without reading it.</summary>
    [Fact]
    public void APluginWithoutACopyIsNotRead()
    {
        var pin = _w.Svc.CapturePin();
        using var session = pin.Resolver.OpenSession();
        var before = Costs();
        var opens = _w.Svc.Counters.SessionOverlayOpens;
        Assert.Null(pin.View.GetRecord(session, _w.PatchName, _w.List));
        Assert.Equal(new long[] { 0, 0, 0, 0 }, Since(before));
        Assert.Equal(opens, _w.Svc.Counters.SessionOverlayOpens);
    }
}

/// <summary>The typed door answers exactly what a flat walk of the plugin finds, for every copy of every key in the shared worlds.</summary>
[Trait("tier", "integration")]
public sealed class TypedIndexEquivalenceTests : IClassFixture<TypedIndexFixture>, IClassFixture<OwnedChildFixture>, IClassFixture<RecordsFixture>
{
    readonly LoadOrderService[] _svcs;
    public TypedIndexEquivalenceTests(TypedIndexFixture t, OwnedChildFixture o, RecordsFixture r) => _svcs = new[] { t.W.Svc, o.W.Svc, r.Svc };

    [Fact]
    public void EveryCopyFetchedTypedEqualsTheFlatWalksCopy()
    {
        int compared = 0;
        foreach (var svc in _svcs)
        {
            var pin = svc.CapturePin();
            using var session = pin.Resolver.OpenSession();
            foreach (var plugin in pin.View.ScannablePluginNames)
            {
                var ov = LoadOrderResolver.OpenOverlay(pin.View.PluginPath(plugin)!, pin.View.DataDir);
                try
                {
                    var flat = new Dictionary<FormKey, IMajorRecordGetter>();
                    foreach (var rec in ov.EnumerateMajorRecords()) flat.TryAdd(rec.FormKey, rec);
                    foreach (var (fk, expected) in flat)
                    {
                        var typed = pin.View.GetRecord(session, plugin, fk);
                        Assert.NotNull(typed);
                        Assert.Equal(expected.GetType(), typed!.GetType());
                        Assert.Equal(Render(expected), Render(typed));
                        compared++;
                    }
                }
                finally { (ov as IDisposable)?.Dispose(); }
            }
        }
        Assert.True(compared > 100, compared.ToString());
    }

    static string Render(IMajorRecordGetter body) => System.Text.Json.JsonSerializer.Serialize(ReadEngine.ReadFields(body, depth: 3));
}

/// <summary>The <c>-&gt;</c> step keeps each target's verdict, not its body (#1158): a scan judges each target once and
/// holds no target body after it is judged. Fails on a body cache, which keeps every target for the whole call.</summary>
[Trait("tier", "unit")]
public sealed class LinkStepVerdictTests
{
    static readonly ModKey Mod = ModKey.FromFileName("HcLinkVerdict.esm");

    [Fact]
    public void ALinkScanJudgesEachTargetOnceAndHoldsNoTargetBody()
    {
        var (set, err) = FieldPredicateSet.Parse(new[] { "Armature[*any]->editorid startswith Hit" });
        Assert.Null(err);
        var weak = new List<WeakReference>();
        int fetches = 0;
        set!.BindResolution(_ => Mod.FileName.String, fk => { fetches++; return Target(fk, weak); });
        var targets = Enumerable.Range(1, 4).Select(i => new FormKey(Mod, (uint)(0x800 + i))).ToArray();

        int hits = Scan(set, targets, 200);

        Assert.Equal(100, hits);                     // targets 0x802 and 0x804 are the hits, every other candidate links one
        Assert.Equal(4, fetches);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.All(weak, w => Assert.False(w.IsAlive));
        GC.KeepAlive(set);
    }

    /// <summary>A remembered verdict brings back its own note: the rollup names the link step's bad key, not the one
    /// another predicate read since. Fails when a memo hit leaves the stash the last fresh read of any predicate set.</summary>
    [Fact]
    public void ARememberedVerdictNamesItsOwnNote()
    {
        var (set, err) = FieldPredicateSet.Parse(new[]
        {
            "Armature[*any]->Conditions[any].CompareOperator = EqualTo",
            "Keywords[foo].EditorID = HcNone",
        });
        Assert.Null(err);
        var effect = new MagicEffect(new FormKey(Mod, 0x801), SkyrimRelease.SkyrimSE);
        effect.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.EqualTo, Data = new GetLevelConditionData() });
        var plain = new Armor(new FormKey(Mod, 0x802), SkyrimRelease.SkyrimSE);
        var targets = new Dictionary<FormKey, IMajorRecordGetter> { [effect.FormKey] = effect, [plain.FormKey] = plain };
        set!.BindResolution(_ => Mod.FileName.String, fk => targets[fk]);

        // The first links both targets, so its verdict is the plain armor's missing field; the second links only the effect.
        Armor Candidate(uint id, params FormKey[] links)
        {
            var a = new Armor(new FormKey(Mod, id), SkyrimRelease.SkyrimSE);
            foreach (var l in links) a.Armature.Add(new FormLink<IArmorAddonGetter>(l));
            return a;
        }
        set.Matches(Candidate(0x1001, effect.FormKey, plain.FormKey));
        set.Matches(Candidate(0x1002, effect.FormKey));
        set.Matches(new GlobalFloat(new FormKey(Mod, 0x1003), SkyrimRelease.SkyrimSE));   // neither field, so neither predicate refuses

        Assert.Null(set.FatalError);
        // Only the link step's own note says 'any'; the keyword predicate's says 'foo'.
        Assert.Contains("got 'any'", set.AccountingNote() ?? "");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Scan(FieldPredicateSet set, FormKey[] targets, int candidates)
    {
        int hits = 0;
        for (int i = 0; i < candidates; i++)
        {
            var armor = new Armor(new FormKey(Mod, (uint)(0x1000 + i)), SkyrimRelease.SkyrimSE);
            armor.Armature.Add(new FormLink<IArmorAddonGetter>(targets[i % targets.Length]));
            if (set.Matches(armor)) hits++;
        }
        return hits;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static IMajorRecordGetter Target(FormKey fk, List<WeakReference> weak)
    {
        var r = new ArmorAddon(fk, SkyrimRelease.SkyrimSE) { EditorID = fk.ID % 2 == 0 ? "Hit" : "Miss" };
        weak.Add(new WeakReference(r));
        return r;
    }
}

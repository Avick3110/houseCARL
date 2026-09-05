using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The synthetic MO2 world the records surface is driven against: a master (weapons, MGEF/spell pairs,
/// an NPC template pair, an over-budget form list), an ACTIVE override, a MID override, and a DISABLED
/// old patch that serves as the off-order pole.
///
/// <para>One instance is shared by every read-only test through <see cref="RecordsFixture"/>. A test that
/// MUTATES the world (rewriting a plugin's mtime, say) constructs its own instance.</para>
/// </summary>
public sealed class RecordsWorld : IDisposable
{
    public string Root { get; }
    public string Instance { get; }
    public string OverrideFile { get; }
    public string OldFile { get; }
    public string ModsDir { get; }

    public string MasterName { get; }
    public string MidName { get; }
    public string OverrideName { get; }
    public string OldName { get; }

    public LoadOrderService Svc { get; }
    public string? Epoch0 { get; }

    public SkyrimMod Master { get; }
    public IReadOnlyList<FormKey> Weapons { get; }
    public FormKey NoEidWeapon { get; }
    public FormKey Armor { get; }
    public FormKey MgefA { get; }
    public FormKey MgefB { get; }
    public FormKey SpellA { get; }
    public FormKey SpellB { get; }
    public FormKey SpellC { get; }
    public FormKey BigList { get; }
    public FormKey NpcParent { get; }
    public FormKey NpcChild { get; }
    // A three-link chain of its own — MgefHop <- SpellHop <- ListHop — so a transitive reverse walk has a second
    // hop to reach without any existing record gaining a referrer it did not have.
    public FormKey MgefHop { get; }
    public FormKey SpellHop { get; }
    public FormKey ListHop { get; }

    public IReadOnlyList<IMajorRecordGetter> WeaponBodies { get; }
    public IReadOnlyList<IMajorRecordGetter> SpellBodies { get; }
    public IReadOnlyDictionary<FormKey, IMajorRecordGetter> MgefByKey { get; }

    public static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

    /// <summary>What <c>CorpusRulebook.CorpusPath</c> named before this world repointed it.</summary>
    readonly string _priorCorpusPath;

    public RecordsWorld()
    {
        // CorpusRulebook.CorpusPath is a process-global this world repoints at its own generated corpus.
        // Capture the prior value here so Dispose can put it back: Dispose deletes Root, and a static left
        // naming a path under Root would name a directory that no longer exists.
        _priorCorpusPath = CorpusRulebook.CorpusPath;

        Root = Path.Combine(Path.GetTempPath(), "hc-records-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));

        var masterKey = new ModKey("HcRecMaster", ModType.Master);
        var ovKey = new ModKey("HcRecOverride", ModType.Plugin);
        var oldKey = new ModKey("HcRecOld", ModType.Plugin);
        var midKey = new ModKey("HcRecMid", ModType.Plugin);
        MasterName = masterKey.FileName.String;
        OverrideName = ovKey.FileName.String;
        OldName = oldKey.FileName.String;
        MidName = midKey.FileName.String;

        var master = new SkyrimMod(masterKey, SkyrimRelease.SkyrimSE);
        Master = master;
        var weapons = new List<FormKey>();
        for (int i = 0; i < 3; i++)
        {
            var w = master.Weapons.AddNew();
            w.EditorID = $"HcRecW{i}";
            w.BasicStats = new WeaponBasicStats { Damage = (ushort)(10 * (i + 1)), Weight = 1 };
            weapons.Add(w.FormKey);
        }
        Weapons = weapons;

        var noEid = master.Weapons.AddNew();
        noEid.BasicStats = new WeaponBasicStats { Damage = 5, Weight = 1 };
        NoEidWeapon = noEid.FormKey;

        var armo = master.Armors.AddNew(); armo.EditorID = "HcRecA0"; Armor = armo.FormKey;
        var mgefA = master.MagicEffects.AddNew(); mgefA.EditorID = "HcRecMgefFire"; MgefA = mgefA.FormKey;
        var mgefB = master.MagicEffects.AddNew(); mgefB.EditorID = "OtherMgef"; MgefB = mgefB.FormKey;
        // A condition stack: a struct list whose polymorphic arm carries the value, one row Or-flagged and one
        // whose arm has no parameters at all. The rows form's headline fixture.
        mgefB.Conditions.Add(new ConditionFloat
        {
            CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1f,
            Data = new GetActorValueConditionData { ActorValue = ActorValue.Conjuration },
        });
        mgefB.Conditions.Add(new ConditionFloat
        {
            Flags = Condition.Flag.OR,
            CompareOperator = CompareOperator.GreaterThanOrEqualTo, ComparisonValue = 30f,
            Data = new GetActorValueConditionData { ActorValue = ActorValue.Destruction },
        });
        mgefB.Conditions.Add(new ConditionFloat
        {
            CompareOperator = CompareOperator.NotEqualTo, ComparisonValue = 0f,
            Data = new GetRandomPercentConditionData(),
        });

        var spellA = master.Spells.AddNew(); spellA.EditorID = "HcRecSpellA"; SpellA = spellA.FormKey;
        { var e = new Effect(); e.BaseEffect.SetTo(MgefA); e.Data = new EffectData { Magnitude = 5 }; spellA.Effects.Add(e); }
        // A SECOND effect, so the general (non-condition) case exercises the multi-element fold: its Data is the
        // absent optional, its BaseEffect the declared-but-null link, and its own Conditions the nested list
        // inside an element. Deliberately unlinked — a link here would join the reverse-carrier lanes.
        {
            var e = new Effect();
            e.Data = new EffectData { Magnitude = 4 };
            e.Conditions.Add(new ConditionFloat
            {
                CompareOperator = CompareOperator.EqualTo, ComparisonValue = 2f,
                Data = new GetRandomPercentConditionData(),
            });
            spellA.Effects.Add(e);
        }
        var spellC = master.Spells.AddNew(); spellC.EditorID = "HcRecSpellC"; SpellC = spellC.FormKey;
        { var e = new Effect(); e.BaseEffect.SetTo(MgefA); e.Data = new EffectData { Magnitude = 9 }; spellC.Effects.Add(e); }
        var spellB = master.Spells.AddNew(); spellB.EditorID = "HcRecSpellB"; SpellB = spellB.FormKey;
        { var e = new Effect(); e.BaseEffect.SetTo(MgefB); e.Data = new EffectData { Magnitude = 7 }; spellB.Effects.Add(e); }

        // The reverse chain: nothing here is linked from any record above, so every existing referencer count and
        // orphan verdict is unchanged.
        var mgefHop = master.MagicEffects.AddNew(); mgefHop.EditorID = "HcRecMgefHop"; MgefHop = mgefHop.FormKey;
        var spellHop = master.Spells.AddNew(); spellHop.EditorID = "HcRecSpellHop"; SpellHop = spellHop.FormKey;
        { var e = new Effect(); e.BaseEffect.SetTo(MgefHop); e.Data = new EffectData { Magnitude = 3 }; spellHop.Effects.Add(e); }
        var listHop = master.FormLists.AddNew(); listHop.EditorID = "HcRecListHop"; ListHop = listHop.FormKey;
        listHop.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(SpellHop));

        // One element past ReadEngine's expansion budget: the only fixture that reaches the delta form's
        // "TRUNCATED at the cap" sentence (zero deltas AND an incomplete deep read).
        var bigList = master.FormLists.AddNew(); bigList.EditorID = "HcRecBigList"; BigList = bigList.FormKey;
        for (int i = 0; i < ReadEngine.MaxExpandNodes + 1; i++)
            bigList.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(weapons[i % weapons.Count]));

        var npcParent = master.Npcs.AddNew(); npcParent.EditorID = "HcRecNpcParent"; NpcParent = npcParent.FormKey;
        var npcChild = master.Npcs.AddNew(); npcChild.EditorID = "HcRecNpcChild"; NpcChild = npcChild.FormKey;
        npcChild.Template.SetTo(NpcParent);
        npcChild.Configuration.TemplateFlags |= NpcConfiguration.TemplateFlag.Traits;

        var ovMod = new SkyrimMod(ovKey, SkyrimRelease.SkyrimSE);
        WriteEngine.GenericGetOrAddAsOverride(ovMod, bigList);   // identical copy — no field changed
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(ovMod, master.Weapons.First(w => w.FormKey == weapons[0])))
            .BasicStats = new WeaponBasicStats { Damage = 99, Weight = 1 };
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(ovMod, master.Weapons.First(w => w.FormKey == weapons[2])))
            .IsDeleted = true;

        var oldMod = new SkyrimMod(oldKey, SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(oldMod, master.Weapons.First(w => w.FormKey == weapons[1])))
            .BasicStats = new WeaponBasicStats { Damage = 55, Weight = 1 };
        // The off-order file carries a list too, so a read of one can be driven through the off-order arm.
        WriteEngine.GenericGetOrAddAsOverride(oldMod, spellA);

        var midMod = new SkyrimMod(midKey, SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(midMod, master.Weapons.First(w => w.FormKey == weapons[0])))
            .BasicStats = new WeaponBasicStats { Damage = 50, Weight = 1 };

        Instance = Path.Combine(Root, "inst");
        ModsDir = Path.Combine(Instance, "mods");
        foreach (var d in new[] { "MasterMod", "MidMod", "OverrideMod", "OldMod" })
            Directory.CreateDirectory(Path.Combine(ModsDir, d));
        var masterFile = Path.Combine(ModsDir, "MasterMod", MasterName);
        OverrideFile = Path.Combine(ModsDir, "OverrideMod", OverrideName);
        OldFile = Path.Combine(ModsDir, "OldMod", OldName);
        var midFile = Path.Combine(ModsDir, "MidMod", MidName);
        master.BeginWrite.ToPath(masterFile).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        midMod.BeginWrite.ToPath(midFile).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();
        ovMod.BeginWrite.ToPath(OverrideFile).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();
        oldMod.BeginWrite.ToPath(OldFile).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

        var genDir = Path.Combine(Root, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(Root, "corpus-ref"));
        CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

        File.WriteAllText(Path.Combine(Instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(Instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + MasterName + "\r\n" + MidName + "\r\n" + OverrideName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + MasterName + "\r\n*" + MidName + "\r\n*" + OverrideName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n-OldMod\r\n+OverrideMod\r\n+MidMod\r\n+MasterMod\r\n");

        var store = new UserConfigStore(Path.Combine(Root, "user.json"));
        Svc = LoadOrderService.WithInstance(Instance, 0, store);
        Epoch0 = Svc.Stats().epoch;

        WeaponBodies = master.Weapons.Select(w => (IMajorRecordGetter)w).ToList();
        SpellBodies = master.Spells.Select(s => (IMajorRecordGetter)s).ToList();
        MgefByKey = master.MagicEffects.ToDictionary(m => m.FormKey, m => (IMajorRecordGetter)m);
    }

    /// <summary>Per-test scratch path under the world's root (results dirs, artifacts).</summary>
    public string Scratch(params string[] parts)
    {
        var p = Path.Combine(new[] { Root, "scratch" }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        return p;
    }

    public void Dispose()
    {
        // Before the delete, never after: the static must not be left naming a directory this line removes.
        CorpusRulebook.CorpusPath = _priorCorpusPath;
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>The shared, read-only world. One build per test class collection.</summary>
public sealed class RecordsFixture : IDisposable
{
    public RecordsWorld W { get; } = new();
    public LoadOrderService Svc => W.Svc;

    readonly Dictionary<Type, object> _memo = new();

    /// <summary>
    /// Build-once-per-collection derived state. xUnit constructs the test class per test method, so a
    /// derivation that drives dozens of tool calls belongs on the fixture, not the class.
    /// </summary>
    public T Shared<T>(Func<RecordsWorld, T> make) where T : class
    {
        if (_memo.TryGetValue(typeof(T), out var v)) return (T)v;
        var made = make(W);
        _memo[typeof(T)] = made;
        return made;
    }

    public void Dispose() => W.Dispose();
}

/// <summary>
/// Every records test runs in one collection: <c>CorpusRulebook.CorpusPath</c> is a process-wide mutable
/// static, so two worlds built in parallel would point the rulebook at each other's corpus.
/// </summary>
[CollectionDefinition("records")]
public sealed class RecordsCollection : ICollectionFixture<RecordsFixture> { }

using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The synthetic MO2 world the script-property surface is driven against: one plugin of five records plus
/// the loose <c>Scripts/HcSpBase.pex</c> + <c>Scripts/HcSpChild.pex</c> pair that DECLARES what those
/// records do or do not bind. Where the records came from, why it is an MO2 instance rather than the
/// probe's bare directory, and why it generates no corpus:
/// <c>docs/architecture/test-project-fixtures.md</c>.
///
/// <para>Shared through <see cref="ScriptsFixture"/> and FROZEN: tests take fixture-known totals from it,
/// so a later need gets its own world rather than an edit to this one, and a test that holds a file open
/// builds its own instance.</para>
/// </summary>
public sealed class ScriptsWorld : IDisposable
{
    // ---- fixture-known names: the vocabulary every assertion spells ------------------------------------

    /// <summary>The child script every scripted record in this world attaches.</summary>
    public const string ChildScript = "HcSpChild";

    /// <summary>The ancestor <see cref="ChildScript"/> extends — where <c>InheritedThing</c> is declared.</summary>
    public const string BaseScript = "HcSpBase";

    /// <summary>The script attached with NO compiled .pex on disk — the unverifiable case (Q3).</summary>
    public const string MissingScript = "HcSpNoPex";

    public const string FootgunEditorId = "HcSpFootgun";
    public const string CleanEditorId = "HcSpClean";
    public const string NoPexEditorId = "HcSpNoPex";
    public const string NoVmadEditorId = "HcSpNoVmad";
    public const string AliasQuestEditorId = "HcSpAliasQuest";

    /// <summary>The property declared on the ANCESTOR script, reachable only by walking the extends chain.</summary>
    public const string InheritedProperty = "InheritedThing";

    /// <summary>The unbound OBJECT property — the reported silent-None footgun.</summary>
    public const string ObjectProperty = "MySpell";

    /// <summary>An object property the footgun DOES bind, to a non-null form.</summary>
    public const string BoundProperty = "MyBoundSpell";

    /// <summary>An Int property with no baked default — unbound reads as an uninitialized SCALAR finding.</summary>
    public const string ScalarProperty = "MyChance";

    /// <summary>An Int property with a baked default of 5 — unbound is NOT a finding.</summary>
    public const string DefaultedProperty = "MyDefaulted";

    /// <summary>The baked initializer on <see cref="DefaultedProperty"/>.</summary>
    public const int DefaultedValue = 5;

    /// <summary>An object property bound through a quest ALIAS (Object null, Alias >= 0) — bound, not null.</summary>
    public const string AliasBoundProperty = "MyAliasBound";

    /// <summary>An object property bound to a NULL form — the bound-but-null advisory.</summary>
    public const string NullProperty = "MyNullSpell";

    /// <summary>How many records in this world carry scripts: footgun, clean, noPex, aliasQuest. The
    /// script-free record is excluded — that exclusion is the whole point of the number.</summary>
    public const int RecordsWithScripts = 4;

    // ---- the world -------------------------------------------------------------------------------------

    public string Root { get; }
    public string Instance { get; }
    public string ModsDir { get; }
    public string ScriptsDir { get; }

    /// <summary>The shared world's plugin. An arm that would HOLD it open builds its own world instead.</summary>
    public string PluginPath { get; }

    public string PluginName { get; }

    public LoadOrderService Svc { get; }

    public FormKey Footgun { get; }
    public FormKey Clean { get; }
    public FormKey NoPex { get; }
    public FormKey NoVmad { get; }
    public FormKey AliasQuest { get; }

    public ScriptsWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-scripts-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));

        Instance = Path.Combine(Root, "inst");
        ModsDir = Path.Combine(Instance, "mods");
        var modDir = Path.Combine(ModsDir, "ScriptsMod");
        ScriptsDir = Path.Combine(modDir, "Scripts");
        Directory.CreateDirectory(ScriptsDir);

        // ---- 1) the two .pex fixtures, under the mod's own Scripts folder (MO2's loose-file layer). ----
        PexWriter.WritePex(Path.Combine(ScriptsDir, BaseScript + ".pex"), BaseScript, parent: null,
            PexWriter.AutoObj(InheritedProperty, "ObjectReference"));

        PexWriter.WritePex(Path.Combine(ScriptsDir, ChildScript + ".pex"), ChildScript, parent: BaseScript,
            PexWriter.AutoObj(ObjectProperty, "Spell"),
            PexWriter.AutoObj(BoundProperty, "Spell"),
            PexWriter.AutoScalar(ScalarProperty, "Int", initInt: null),
            PexWriter.AutoScalar(DefaultedProperty, "Int", initInt: DefaultedValue),  // baked default ⇒ NOT flagged unbound
            PexWriter.AutoObj(AliasBoundProperty, "Spell"),                           // bound via a quest Alias
            PexWriter.AutoObj(NullProperty, "Spell"));

        // ---- 2) the plugin of scripted records. ----
        var key = new ModKey("HcSp", ModType.Plugin);
        PluginName = key.FileName.String;
        PluginPath = Path.Combine(modDir, PluginName);

        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        // A non-null in-plugin FormKey for "bound" object props — the target need not exist, only IsNull is read.
        var self = new FormKey(key, 0x000801);

        // wFootgun — the reported failure shape. MyAliasBound is bound via a quest alias (Object null,
        // Alias >= 0): it must count as BOUND, and must NOT be flagged bound-but-null.
        var footgun = mod.Weapons.AddNew(); footgun.EditorID = FootgunEditorId;
        footgun.VirtualMachineAdapter = Vmad(ChildScript,
            ObjProp(BoundProperty, self), ObjProp(NullProperty, FormKey.Null), AliasProp(AliasBoundProperty, 2));
        Footgun = footgun.FormKey;

        // wClean — fully bound: the no-false-positive control.
        var clean = mod.Weapons.AddNew(); clean.EditorID = CleanEditorId;
        clean.VirtualMachineAdapter = Vmad(ChildScript,
            ObjProp(ObjectProperty, self), ObjProp(BoundProperty, self), ObjProp(NullProperty, self),
            ObjProp(AliasBoundProperty, self), ObjProp(InheritedProperty, self),
            IntProp(ScalarProperty, 3), IntProp(DefaultedProperty, DefaultedValue));
        Clean = clean.FormKey;

        // wNoPex — a script with no compiled .pex on disk: unverifiable, never a silent clean.
        var noPex = mod.Weapons.AddNew(); noPex.EditorID = NoPexEditorId;
        noPex.VirtualMachineAdapter = Vmad(MissingScript);
        NoPex = noPex.FormKey;

        // wNoVmad — a script-free record: never counted, never nagged.
        var noVmad = mod.Weapons.AddNew(); noVmad.EditorID = NoVmadEditorId;
        NoVmad = noVmad.FormKey;

        // aliasQuest — a QUST whose script hangs off an ALIAS (QuestAdapter.Aliases[].Scripts), NOT the
        // quest's own Scripts. The alias script binds nothing, so every declared property is unbound.
        var quest = mod.Quests.AddNew(); quest.EditorID = AliasQuestEditorId;
        var adapter = new QuestAdapter();
        var alias = new QuestFragmentAlias { Property = new ScriptObjectProperty { Alias = 0 } };
        alias.Scripts.Add(new ScriptEntry { Name = ChildScript });
        adapter.Aliases.Add(alias);
        quest.VirtualMachineAdapter = adapter;
        AliasQuest = quest.FormKey;

        mod.BeginWrite.ToPath(PluginPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        // ---- 3) the MO2 instance around them. ----
        File.WriteAllText(Path.Combine(Instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(Instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + PluginName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + PluginName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+ScriptsMod\r\n");

        var store = new UserConfigStore(Path.Combine(Root, "user.json"));
        Svc = LoadOrderService.WithInstance(Instance, 0, store);
    }

    // ---- VMAD builders (ported from the probe's fixture builders) ---------------------------------------

    /// <summary>A VMAD binding ONE script with the given bound properties.</summary>
    static VirtualMachineAdapter Vmad(string scriptClass, params ScriptProperty[] props)
    {
        var entry = new ScriptEntry { Name = scriptClass };
        foreach (var p in props) entry.Properties.Add(p);
        var vmad = new VirtualMachineAdapter();
        vmad.Scripts.Add(entry);
        return vmad;
    }

    static ScriptObjectProperty ObjProp(string name, FormKey obj)
    {
        var p = new ScriptObjectProperty { Name = name, Alias = -1 };
        if (!obj.IsNull) p.Object.SetTo(obj);
        return p;
    }

    /// <summary>An object property bound to a quest ALIAS (Alias &gt;= 0), Object left null — the healthy
    /// shape that must NOT read as bound-but-null.</summary>
    static ScriptObjectProperty AliasProp(string name, short alias) => new() { Name = name, Alias = alias };

    static ScriptIntProperty IntProp(string name, int data) => new() { Name = name, Data = data };

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>The shared, read-only Papyrus world. One build per test class collection.</summary>
public sealed class ScriptsFixture : IDisposable
{
    public ScriptsWorld W { get; } = new();
    public LoadOrderService Svc => W.Svc;
    public void Dispose() => W.Dispose();
}

/// <summary>Every Papyrus-fixture test runs in one collection, over one built world.</summary>
[CollectionDefinition("scripts")]
public sealed class ScriptsCollection : ICollectionFixture<ScriptsFixture> { }

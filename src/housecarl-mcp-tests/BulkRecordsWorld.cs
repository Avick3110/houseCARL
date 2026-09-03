using System.Text.Json;
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
/// A second synthetic MO2 world, shaped for the BULK SELECT questions: a master defining two keywords,
/// two keyword-bearing weapons and an armor, and a replacer that OVERRIDES one weapon (changing both its
/// damage and its EditorID) and DEFINES a third weapon carrying BOTH keywords plus a second armor.
///
/// <para>That shape is what the scan-lane arms need and the shared <see cref="RecordsWorld"/> does not
/// carry: reverse lookups with a record hitting two targets at once, a defined-vs-overridden split for
/// aggregate counts, a scoped-body-vs-winner value pair on the same record, and a link that no plugin
/// defines. The shared world is frozen, so this one is its own.</para>
/// </summary>
public sealed class BulkRecordsWorld : IDisposable
{
    public string Root { get; }
    public string MasterName { get; }
    public string ReplName { get; }

    public LoadOrderService Svc { get; }
    public string? Epoch { get; }

    /// <summary>Keyword A — carried by W1 (master body) and W3.</summary>
    public FormKey KwA { get; }
    /// <summary>Keyword B — carried by W2 and W3.</summary>
    public FormKey KwB { get; }

    /// <summary>Defined in the master (damage 10, editorid HcBulkSword1), OVERRIDDEN by the replacer
    /// (damage 15, editorid HcBulkSword1Winner). The scoped-vs-winner pair.</summary>
    public FormKey W1 { get; }
    /// <summary>Master-only (damage 20).</summary>
    public FormKey W2 { get; }
    /// <summary>Defined in the replacer (damage 30), carries BOTH keywords.</summary>
    public FormKey W3 { get; }
    public FormKey Armor1 { get; }
    public FormKey Armor2 { get; }

    /// <summary>A keyword link on W1 that NOTHING defines — the unresolved-annotation pole.</summary>
    public FormKey Ghost { get; }

    public const string W1ScopedEditorId = "HcBulkSword1";
    public const string W1WinnerEditorId = "HcBulkSword1Winner";
    /// <summary>W1's full name — the identity form's <c>name=</c> column has to have something to carry.</summary>
    public const string W1Name = "Bulk Iron Sword";

    public static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

    readonly string _priorCorpusPath;

    public BulkRecordsWorld()
    {
        // Same process-global discipline RecordsWorld records: capture before repointing, restore before
        // the directory the new value names is deleted.
        _priorCorpusPath = CorpusRulebook.CorpusPath;

        Root = Path.Combine(Path.GetTempPath(), "hc-bulk-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));

        var masterKey = new ModKey("HcBulkMaster", ModType.Master);
        var replKey = new ModKey("HcBulkRepl", ModType.Plugin);
        MasterName = masterKey.FileName.String;
        ReplName = replKey.FileName.String;

        var master = new SkyrimMod(masterKey, SkyrimRelease.SkyrimSE);
        var ka = master.Keywords.AddNew(); ka.EditorID = "HcBulkKwA"; KwA = ka.FormKey;
        var kb = master.Keywords.AddNew(); kb.EditorID = "HcBulkKwB"; KwB = kb.FormKey;
        Ghost = new FormKey(masterKey, 0x000FFF);   // in the master's own space: dangling, not missing-master

        var w1 = master.Weapons.AddNew();
        w1.EditorID = W1ScopedEditorId;
        w1.Name = W1Name;
        w1.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1 };
        w1.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>>
            { new FormLink<IKeywordGetter>(KwA), new FormLink<IKeywordGetter>(Ghost) };
        W1 = w1.FormKey;

        var w2 = master.Weapons.AddNew();
        w2.EditorID = "HcBulkSword2";
        w2.BasicStats = new WeaponBasicStats { Damage = 20, Weight = 1 };
        w2.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>> { new FormLink<IKeywordGetter>(KwB) };
        W2 = w2.FormKey;

        var a1 = master.Armors.AddNew(); a1.EditorID = "HcBulkArmor1"; Armor1 = a1.FormKey;

        var repl = new SkyrimMod(replKey, SkyrimRelease.SkyrimSE);
        var w1ov = (IWeapon)WriteEngine.GenericGetOrAddAsOverride(repl, w1);
        w1ov.BasicStats = new WeaponBasicStats { Damage = 15, Weight = 1 };
        w1ov.EditorID = W1WinnerEditorId;

        var w3 = repl.Weapons.AddNew();
        w3.EditorID = "HcBulkSword3";
        w3.BasicStats = new WeaponBasicStats { Damage = 30, Weight = 1 };
        w3.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>>
            { new FormLink<IKeywordGetter>(KwA), new FormLink<IKeywordGetter>(KwB) };
        W3 = w3.FormKey;

        var a2 = repl.Armors.AddNew(); a2.EditorID = "HcBulkArmor2"; Armor2 = a2.FormKey;

        var instance = Path.Combine(Root, "inst");
        var mods = Path.Combine(instance, "mods");
        Directory.CreateDirectory(Path.Combine(mods, "BulkMasterMod"));
        Directory.CreateDirectory(Path.Combine(mods, "BulkReplMod"));
        var masterFile = Path.Combine(mods, "BulkMasterMod", MasterName);
        var replFile = Path.Combine(mods, "BulkReplMod", ReplName);
        master.BeginWrite.ToPath(masterFile).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        repl.BeginWrite.ToPath(replFile).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

        var genDir = Path.Combine(Root, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(Root, "corpus-ref"));
        CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + MasterName + "\r\n" + ReplName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + MasterName + "\r\n*" + ReplName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+BulkReplMod\r\n+BulkMasterMod\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "user.json")));
        Epoch = Svc.Stats().epoch;
    }

    public string Scratch(params string[] parts)
    {
        var p = Path.Combine(new[] { Root, "scratch" }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        return p;
    }

    public void Dispose()
    {
        CorpusRulebook.CorpusPath = _priorCorpusPath;
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>
/// A one-plugin world whose only record links to the two ENGINE-IMPLICIT forms — PlayerRef
/// (000014:Skyrim.esm) and the next sub-0x800 form (000015:Skyrim.esm), which no plugin defines either.
/// Skyrim.esm is written to disk so the plugin has a masters entry, and is deliberately left OUT of the
/// active order: both links then fail ordinary winner resolution and only the hardcoded exemption
/// separates them.
///
/// <para>Its own world, not a third plugin in <see cref="BulkRecordsWorld"/>: an extra active plugin
/// would move every count the scan arms assert.</para>
/// </summary>
public sealed class EngineImplicitLinkWorld : IDisposable
{
    public string Root { get; }
    public LoadOrderService Svc { get; }
    public FormKey Carrier { get; }

    public const string PlayerRefToken = "000014:Skyrim.esm";
    public const string ControlToken = "000015:Skyrim.esm";

    readonly string _priorCorpusPath;

    public EngineImplicitLinkWorld()
    {
        _priorCorpusPath = CorpusRulebook.CorpusPath;
        Root = Path.Combine(Path.GetTempPath(), "hc-engineimplicit-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));

        var instance = Path.Combine(Root, "inst");
        var mods = Path.Combine(instance, "mods");
        Directory.CreateDirectory(Path.Combine(mods, "EiStubMod"));
        Directory.CreateDirectory(Path.Combine(mods, "EiMod"));

        var stub = new SkyrimMod(new ModKey("Skyrim", ModType.Master), SkyrimRelease.SkyrimSE);
        stub.Races.AddNew();   // one throwaway record so the stub is a valid, non-empty master
        var stubPath = Path.Combine(mods, "EiStubMod", "Skyrim.esm");
        stub.BeginWrite.ToPath(stubPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var eiKey = ModKey.FromNameAndExtension("HcEiCarrier.esp");
        var ei = new SkyrimMod(eiKey, SkyrimRelease.SkyrimSE);
        var w = ei.Weapons.AddNew();
        w.EditorID = "HcEiCarrier";
        w.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>>
        {
            new FormLink<IKeywordGetter>(FormKey.Factory(PlayerRefToken)),
            new FormLink<IKeywordGetter>(FormKey.Factory(ControlToken)),
        };
        Carrier = w.FormKey;
        ei.BeginWrite.ToPath(Path.Combine(mods, "EiMod", eiKey.FileName)).WithLoadOrder(new ISkyrimModGetter[] { stub }).Write();

        var genDir = Path.Combine(Root, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(Root, "corpus-ref"));
        CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        // Skyrim.esm is on disk but NOT listed: the exemption, not the load order, has to answer for 000014.
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + eiKey.FileName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + eiKey.FileName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+EiMod\r\n+EiStubMod\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "user.json")));
    }

    public void Dispose()
    {
        CorpusRulebook.CorpusPath = _priorCorpusPath;
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>The shared, read-only bulk world. One build per collection.</summary>
public sealed class BulkRecordsFixture : IDisposable
{
    public BulkRecordsWorld W { get; } = new();
    public void Dispose() => W.Dispose();
}

/// <summary>
/// Its own collection for the same reason the records one exists: <c>CorpusRulebook.CorpusPath</c> is a
/// process-global, and only one world may own it at a time. Assembly-wide parallelisation is off, so the
/// collections run one after another.
/// </summary>
[CollectionDefinition("bulk-records")]
public sealed class BulkRecordsCollection : ICollectionFixture<BulkRecordsFixture> { }

/// <summary>Shared shorthand for the bulk-world tests.</summary>
public abstract class BulkRecordsTestBase
{
    protected readonly BulkRecordsWorld W;
    protected BulkRecordsTestBase(BulkRecordsFixture f) => W = f.W;

    protected LoadOrderService Svc => W.Svc;

    protected static JsonElement Je(string json) => JsonDocument.Parse(json).RootElement.Clone();
    protected static JsonElement Plugin(string name) => Je(JsonSerializer.Serialize(name));
    protected static string Fid(FormKey fk) => BulkRecordsWorld.Fid(fk);

    protected static RecordsTools.RecordsProject Form(string form) => new() { form = form };
    protected static RecordsTools.RecordsProject Fields(params string[] paths) =>
        new() { form = "fields", fields = paths };
    protected static RecordsTools.RecordsProject Aggregate(string key) =>
        new() { form = "aggregate", group_by = key };

    protected static void Refused(string response, params string[] mustName)
    {
        Assert.StartsWith("error:", response);
        foreach (var s in mustName) Assert.Contains(s, response);
    }

    protected static void Served(string response, params string[] mustName)
    {
        Assert.False(response.StartsWith("error:", StringComparison.Ordinal), "refused: " + First(response));
        foreach (var s in mustName) Assert.Contains(s, response);
    }

    protected static string First(string response) => response.Split('\n').FirstOrDefault()?.Trim() ?? "";

    /// <summary>Parse a response as a document — a parse failure here IS the assertion failing.</summary>
    protected static JsonElement Doc(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>A match object from a scan's json render, by FormID.</summary>
    protected static JsonElement Match(JsonElement root, string formid) =>
        root.GetProperty("matches").EnumerateArray().Single(m => m.GetProperty("formid").GetString() == formid);

    /// <summary>A field object from a record/match's "fields" array, by path.</summary>
    protected static JsonElement Field(JsonElement holder, string path) =>
        holder.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("path").GetString() == path);

    /// <summary>A dense render's row, by its leading formid cell.</summary>
    protected static JsonElement DenseRow(JsonElement root, string formid) =>
        root.GetProperty("rows").EnumerateArray().Single(r => r[0].GetString() == formid);

    protected static string[] DenseColumns(JsonElement root) =>
        root.GetProperty("columns").EnumerateArray().Select(c => c.GetString()!).ToArray();
}

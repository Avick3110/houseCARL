using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// Two writes that used to answer without saying what they had done: an Add that appended an element the list
/// already carried (#526), and an in-place create that grew the target's master header without the re-sort note its
/// sibling lanes emit (#382).
///
/// <para>The world is built per class and mutated by the in-place create, so it is this class's own: an in-place
/// write rewrites a plugin, which would poison a shared instance.</para>
/// </summary>
[Trait("tier", "integration")]
public sealed class WriteSilentAddAndMasterGrowTests : IDisposable
{
    const string MasterName = "HcDupMaster.esm";
    const string UserName = "HcDupUser.esp";

    readonly string _root;
    readonly string _priorCorpusPath;
    readonly LoadOrderService _svc;
    readonly FormKey _weapon, _kwA, _kwB;

    public WriteSilentAddAndMasterGrowTests()
    {
        _priorCorpusPath = CorpusRulebook.CorpusPath;
        _root = Path.Combine(Path.GetTempPath(), "hc-dupadd-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "game", "Data"));

        var master = new SkyrimMod(new ModKey("HcDupMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var ka = master.Keywords.AddNew(); ka.EditorID = "HcDupKwA"; _kwA = ka.FormKey;
        var kb = master.Keywords.AddNew(); kb.EditorID = "HcDupKwB"; _kwB = kb.FormKey;
        var w = master.Weapons.AddNew();
        w.EditorID = "HcDupSword";
        w.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1 };
        w.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>> { new FormLink<IKeywordGetter>(_kwA) };
        _weapon = w.FormKey;

        // A BARE user plugin: no masters and no records, so the in-place create's cross-plugin reference is the only
        // thing that can grow its header.
        var user = new SkyrimMod(new ModKey("HcDupUser", ModType.Plugin), SkyrimRelease.SkyrimSE);

        var instance = Path.Combine(_root, "inst");
        var mods = Path.Combine(instance, "mods");
        Directory.CreateDirectory(Path.Combine(mods, "DupMasterMod"));
        Directory.CreateDirectory(Path.Combine(mods, "DupUserMod"));
        master.BeginWrite.ToPath(Path.Combine(mods, "DupMasterMod", MasterName))
            .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        user.BeginWrite.ToPath(Path.Combine(mods, "DupUserMod", UserName))
            .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var genDir = Path.Combine(_root, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(_root, "corpus-ref"));
        CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + MasterName + "\r\n" + UserName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + MasterName + "\r\n*" + UserName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+DupUserMod\r\n+DupMasterMod\r\n");

        _svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "user.json")));
    }

    static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";
    static JsonElement Je(string json) => JsonDocument.Parse(json).RootElement.Clone();

    string AddKeyword(FormKey kw, string? format = null) => ApplyTools.Apply(_svc,
        ops: Je($@"[{{""formid"":""{Fid(_weapon)}"",""field_path"":""Keywords"",""op"":""Add"",""value"":""{Fid(kw)}""}}]"),
        format: format);

    // ---- #526: Add appends an element the list already carries ------------------------------------

    [Fact]
    public void AddingAKeywordTheRecordAlreadyCarriesSaysItIsADuplicate()
    {
        var r = AddKeyword(_kwA);
        Assert.DoesNotContain("error:", r);
        Assert.Contains("duplicate", r);
    }

    [Fact]
    public void AddingAKeywordTheRecordDoesNotCarrySaysNothingAboutDuplicates()
    {
        var r = AddKeyword(_kwB);
        Assert.DoesNotContain("error:", r);
        Assert.DoesNotContain("duplicate", r);
    }

    [Fact]
    public void TheDuplicateAddIsItsOwnKeyInTheJsonRender()
    {
        var doc = JsonDocument.Parse(AddKeyword(_kwA, format: "json"));
        var op = doc.RootElement.GetProperty("ops")[0];
        Assert.Contains("duplicate", op.GetProperty("apply_note").GetString());
        // The note is NOT folded into the file-vs-memory pair, which is compared: a duplicate Add still landed.
        Assert.NotEqual("no_answer", op.GetProperty("landed_source").GetString());
    }

    [Fact]
    public void ADryRunAlsoSaysTheAddWouldDuplicate()
    {
        var r = ApplyTools.Apply(_svc,
            ops: Je($@"[{{""formid"":""{Fid(_weapon)}"",""field_path"":""Keywords"",""op"":""Add"",""value"":""{Fid(_kwA)}""}}]"),
            dry_run: true);
        Assert.Contains("duplicate", r);
    }

    // ---- #382: an in-place create that grows the master header ------------------------------------

    [Fact]
    public void AnInPlaceCreateThatGrowsTheMasterHeaderSaysToReSort()
    {
        var r = CreateTools.Create(_svc,
            records: Je($@"[{{""record_type"":""FormList"",""editorid"":""HcDupRefList"",""ops"":[{{""field_path"":""Items"",""op"":""Add"",""value"":""{Fid(_weapon)}""}}]}}]"),
            in_place: UserName, acknowledge: true);
        Assert.DoesNotContain("error:", r);
        // The sibling lanes' own sentence, not the mod-folder line's "re-sort only if a winner changed": a grown
        // master makes the file unloadable until the order is re-sorted, whatever the winners did.
        Assert.Contains($"{MasterName} was added as a master", r);
    }

    public void Dispose()
    {
        CorpusRulebook.CorpusPath = _priorCorpusPath;
        _svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }
}

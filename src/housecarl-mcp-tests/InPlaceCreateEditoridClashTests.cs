using System.Security.Cryptography;
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
/// #611: a create on the in-place lane whose editorid the TARGET already defines used to re-create that record fresh at
/// its own FormID, discarding everything it held. That rebuild is the into= lane's idempotence and stays; in place the
/// file is someone else's and the collision refuses instead, all-or-nothing, until replace= says to overwrite it.
///
/// <para>The world is built PER TEST (xUnit constructs the class once per test method) because the in-place calls
/// rewrite a plugin, which would poison a shared instance.</para>
/// </summary>
[Trait("tier", "integration")]
public sealed class InPlaceCreateEditoridClashTests : IDisposable
{
    const string MasterName = "HcClashMaster.esm";
    const string UserName = "HcClashUser.esp";
    const string TakenEditorId = "HcClashFaction";
    const string KeywordEditorId = "HcClashUserKeyword";   // a Keyword, so a Faction create under it is CROSS-TYPE
    const string DupeEditorId = "HcClashDupe";             // two Factions share it — duplicate editorid residue
    const string TopicEditorId = "HcClashTopic";           // a DialogTopic with INFOs under it — children a replace drops

    readonly string _root, _userPath, _priorCorpusPath;
    readonly LoadOrderService _svc;
    readonly FormKey _faction;

    public InPlaceCreateEditoridClashTests()
    {
        _priorCorpusPath = CorpusRulebook.CorpusPath;
        _root = Path.Combine(Path.GetTempPath(), "hc-clash-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "game", "Data"));

        var master = new SkyrimMod(new ModKey("HcClashMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var kw = master.Keywords.AddNew(); kw.EditorID = "HcClashKeyword";

        // The user's OWN plugin, defining a faction under an editorid a later create will collide with. Name is the
        // field a rebuild from a bare spec would drop, so "prior contents discarded" is observable.
        var user = new SkyrimMod(new ModKey("HcClashUser", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var fac = user.Factions.AddNew();
        fac.EditorID = TakenEditorId;
        fac.Name = "Original faction";
        _faction = fac.FormKey;
        // Two collisions no overwrite can resolve: a name held by a record of ANOTHER type, and a name held twice.
        var userKw = user.Keywords.AddNew(); userKw.EditorID = KeywordEditorId;
        var dupeA = user.Factions.AddNew(); dupeA.EditorID = DupeEditorId;
        var dupeB = user.Factions.AddNew(); dupeB.EditorID = DupeEditorId;
        // A topic with two lines under it. A replace drops the record from its group and re-adds it fresh, and the
        // child group goes with the drop — these are the records that would go missing from the user's own file.
        var topic = user.DialogTopics.AddNew(); topic.EditorID = TopicEditorId;
        topic.Responses.Add(new DialogResponses(user.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcClashLine0" });
        topic.Responses.Add(new DialogResponses(user.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcClashLine1" });

        var instance = Path.Combine(_root, "inst");
        var mods = Path.Combine(instance, "mods");
        Directory.CreateDirectory(Path.Combine(mods, "ClashMasterMod"));
        Directory.CreateDirectory(Path.Combine(mods, "ClashUserMod"));
        _userPath = Path.Combine(mods, "ClashUserMod", UserName);
        master.BeginWrite.ToPath(Path.Combine(mods, "ClashMasterMod", MasterName))
            .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        user.BeginWrite.ToPath(_userPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

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
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+ClashUserMod\r\n+ClashMasterMod\r\n");

        _svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "user.json")));
    }

    static JsonElement Je(string json) => JsonDocument.Parse(json).RootElement.Clone();

    static string Spec(string editorId, string name) =>
        $@"{{""record_type"":""Faction"",""editorid"":""{editorId}"",""ops"":[{{""field_path"":""Name"",""value"":""{name}""}}]}}";

    static string TopicSpec(string editorId, string name) =>
        $@"[{{""record_type"":""DialogTopic"",""editorid"":""{editorId}"",""ops"":[{{""field_path"":""Name"",""value"":""{name}""}}]}}]";

    string Hash() => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_userPath)));

    /// <summary>The faction's Name as the WRITTEN file carries it, or null when the record is gone.</summary>
    string? WrittenName()
    {
        var mod = SkyrimMod.CreateFromBinary(_userPath, SkyrimRelease.SkyrimSE);
        return mod.Factions.FirstOrDefault(f => f.FormKey == _faction)?.Name?.String;
    }

    // ---- the refusal ------------------------------------------------------------------------------

    [Fact]
    public void AnInPlaceCreateWhoseEditoridTheTargetAlreadyDefinesIsRefused()
    {
        var r = CreateTools.Create(_svc, records: Je($"[{Spec(TakenEditorId, "Overwritten")}]"),
            in_place: UserName, acknowledge: true);
        Assert.Contains("error:", r);
        Assert.Contains(TakenEditorId, r);
        Assert.Contains("replace=true", r);
    }

    /// <summary>All-or-nothing: the sibling spec that had no collision is not written either, and the target keeps
    /// every byte it had — which is the whole point of refusing before the rebuild rather than reporting it after.</summary>
    [Fact]
    public void TheRefusedCreateLeavesTheTargetByteIdentical()
    {
        var before = Hash();
        var r = CreateTools.Create(_svc,
            records: Je($"[{Spec("HcClashFresh", "Brand new")},{Spec(TakenEditorId, "Overwritten")}]"),
            in_place: UserName, acknowledge: true);
        Assert.Contains("error:", r);
        Assert.Equal(before, Hash());
    }

    // ---- collisions replace= cannot resolve -------------------------------------------------------

    /// <summary>The clashing record is a Keyword, so the upsert would refuse the create outright rather than overwrite
    /// it. The refusal must say to pick another editorid and NOT offer replace=, which would land on a second refusal.
    /// </summary>
    [Fact]
    public void ACrossTypeClashIsRefusedWithoutOfferingReplace()
    {
        var r = CreateTools.Create(_svc, records: Je($"[{Spec(KeywordEditorId, "Overwritten")}]"),
            in_place: UserName, acknowledge: true);
        Assert.Contains("error:", r);
        Assert.Contains("Keyword", r);
        Assert.DoesNotContain("replace=", r);
    }

    /// <summary>Two records already share the editorid, so which one an overwrite would keep is the caller's call —
    /// the refusal names the removal, not replace=.</summary>
    [Fact]
    public void ADuplicateEditoridClashIsRefusedWithoutOfferingReplace()
    {
        var r = CreateTools.Create(_svc, records: Je($"[{Spec(DupeEditorId, "Overwritten")}]"),
            in_place: UserName, acknowledge: true);
        Assert.Contains("error:", r);
        Assert.Contains("housecarl_remove", r);
        Assert.DoesNotContain("replace=", r);
    }

    /// <summary>The record the editorid names owns child records. An overwrite removes it from its group and re-adds
    /// it fresh, and the children go with the removal — the INFOs under a DialogTopic, in the user's own file, with
    /// nothing to put them back. So replace= is not offered here, and does not go through when passed.</summary>
    [Fact]
    public void AClashOverARecordThatOwnsChildrenIsRefusedEvenWithReplace()
    {
        var before = Hash();
        var r = CreateTools.Create(_svc, records: Je(TopicSpec(TopicEditorId, "Rebuilt")),
            in_place: UserName, acknowledge: true, replace: true);
        Assert.Contains("error:", r);
        Assert.Contains("HcClashLine0", r);        // the child that would have gone missing, named before the write
        Assert.Equal(before, Hash());
    }

    // ---- replace= opts back in --------------------------------------------------------------------

    [Fact]
    public void ReplaceTrueOverwritesTheRecordTheTargetDefinedAndSaysSo()
    {
        var r = CreateTools.Create(_svc, records: Je($"[{Spec(TakenEditorId, "Overwritten")}]"),
            in_place: UserName, acknowledge: true, replace: true);
        Assert.DoesNotContain("error:", r);
        Assert.Contains("REPLACED", r);
        Assert.Equal("Overwritten", WrittenName());
    }

    /// <summary>The replace keeps the record's own FormID, so anything already pointing at it — a script property, a
    /// SkyPatcher line — still resolves.</summary>
    [Fact]
    public void TheReplacedRecordKeepsItsFormId()
    {
        var doc = JsonDocument.Parse(CreateTools.Create(_svc, records: Je($"[{Spec(TakenEditorId, "Overwritten")}]"),
            in_place: UserName, acknowledge: true, replace: true, format: "json"));
        var created = doc.RootElement.GetProperty("created")[0];
        Assert.True(created.GetProperty("replaced_existing").GetBoolean());
        Assert.Equal(_faction.ToString(), created.GetProperty("formid").GetString());
    }

    /// <summary>An editorid the target does NOT define is untouched by the guard — the refusal is about the collision,
    /// not about creating in place.</summary>
    [Fact]
    public void AnInPlaceCreateWhoseEditoridIsFreeStillWrites()
    {
        var r = CreateTools.Create(_svc, records: Je($"[{Spec("HcClashFresh", "Brand new")}]"),
            in_place: UserName, acknowledge: true);
        Assert.DoesNotContain("error:", r);
        Assert.DoesNotContain("REPLACED", r);
        Assert.Equal("Original faction", WrittenName());   // the record that was already there is left alone
    }

    // ---- the patch lane is unmoved ----------------------------------------------------------------

    /// <summary>into= regenerates houseCARL's OWN patch, where a re-run of the same create must stay idempotent: the
    /// record is still rebuilt at a stable FormID, with no replace= asked for.</summary>
    [Fact]
    public void AnIntoReRunStillReplacesAtTheSameFormId()
    {
        var first = CreateTools.Create(_svc, records: Je($"[{Spec("HcClashPatchFaction", "First")}]"),
            patch: "HcClashPatch", format: "json");
        var firstId = JsonDocument.Parse(first).RootElement.GetProperty("created")[0].GetProperty("formid").GetString();
        Assert.NotNull(firstId);

        var again = JsonDocument.Parse(CreateTools.Create(_svc, records: Je($"[{Spec("HcClashPatchFaction", "Second")}]"),
            into: "HcClashPatch.esp", format: "json"));
        var rerun = again.RootElement.GetProperty("created")[0];
        Assert.True(rerun.GetProperty("replaced_existing").GetBoolean());
        Assert.Equal(firstId, rerun.GetProperty("formid").GetString());
    }

    /// <summary>replace= answers the in-place collision and nothing else, so it is refused by name off that lane
    /// rather than accepted and ignored.</summary>
    [Fact]
    public void ReplaceWithoutInPlaceIsRefusedByName()
    {
        var r = CreateTools.Create(_svc, records: Je($"[{Spec("HcClashUnused", "x")}]"),
            patch: "HcClashNever", replace: true);
        Assert.Contains("error:", r);
        Assert.Contains("replace=", r);
        Assert.Contains("in_place=", r);
    }

    public void Dispose()
    {
        CorpusRulebook.CorpusPath = _priorCorpusPath;
        _svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }
}

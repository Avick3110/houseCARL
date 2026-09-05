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
/// <para>The world is built PER TEST (xUnit constructs the class once per test method) and one of these mutates it:
/// the in-place create rewrites a plugin, which would poison a shared instance. Sharing the read-mostly ones through
/// an IClassFixture was measured at ~0.5 s of the class's runtime, so the isolation is kept.</para>
/// </summary>
[Trait("tier", "integration")]
public sealed class WriteSilentAddAndMasterGrowTests : IDisposable
{
    const string MasterName = "HcDupMaster.esm";
    const string UserName = "HcDupUser.esp";

    readonly string _root;
    readonly string _priorCorpusPath;
    readonly LoadOrderService _svc;
    readonly FormKey _weapon, _kwA, _kwB, _lvli;

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

        // A leveled list carrying ONE entry, so a composes=/compose= Add of an identical entry is a real duplicate —
        // and the family where a repeat is legitimate weighting, which is what the note's wording has to suit.
        var ll = master.LeveledItems.AddNew();
        ll.EditorID = "HcDupList";
        ll.Entries = new Noggog.ExtendedList<LeveledItemEntry>
        {
            new LeveledItemEntry { Data = new LeveledItemEntryData { Level = 1, Count = 1, Reference = new FormLink<IItemGetter>(_weapon) } },
        };
        _lvli = ll.FormKey;

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

    /// <summary>The bracketed apply-time note off an op line, so two renders can be compared as the SAME sentence
    /// rather than by each asserting its own copy of the wording.</summary>
    static string NoteOf(string render)
    {
        int a = render.IndexOf("[duplicate", StringComparison.Ordinal);
        Assert.True(a >= 0, "no duplicate note in:\n" + render);
        return render[(a + 1)..render.IndexOf(']', a)];
    }

    static int CountOf(string s, string needle)
    {
        int n = 0;
        for (int i = s.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = s.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

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

    /// <summary>A dry run wrote nothing, so the note may not claim the append happened, and its remedy may not read as
    /// something to do now — following "Remove it by value" after a dry run deletes the record's ONLY copy.</summary>
    [Fact]
    public void TheDryRunNoteDoesNotClaimTheAppendAlreadyHappened()
    {
        var r = ApplyTools.Apply(_svc,
            ops: Je($@"[{{""formid"":""{Fid(_weapon)}"",""field_path"":""Keywords"",""op"":""Add"",""value"":""{Fid(_kwA)}""}}]"),
            dry_run: true);
        Assert.Contains("DRY RUN", r);
        Assert.Contains("is already in", r);                  // the measured before-state, present tense
        Assert.Contains("once this write lands", r);          // the consequence, conditional like "would become"
        Assert.DoesNotContain("was already in", r);
        Assert.DoesNotContain("the list now", r);
        // The SAME string is what the applied render carries — one note, not a per-lane pair that can drift.
        Assert.Equal(NoteOf(AddKeyword(_kwA)), NoteOf(r));
    }

    /// <summary>Presence is all Contains answers, so the note may not assert a count: a list that already held the
    /// element twice now holds three.</summary>
    [Fact]
    public void TheDuplicateNoteClaimsNoMultiplicity()
    {
        var r = AddKeyword(_kwA);
        Assert.DoesNotContain("twice", r);
        Assert.Contains("another copy", r);
    }

    /// <summary>The compact read-back prints a per-op clause of its own, and the in-place lane FORCES it on, so every
    /// in-place duplicate Add hits this: the apply-time note belongs to the op line only, or one Add prints the same
    /// sentence twice.</summary>
    [Fact]
    public void TheDuplicateNoteIsPrintedOnce()
    {
        var r = ApplyTools.Apply(_svc,
            ops: Je($@"[{{""formid"":""{Fid(_weapon)}"",""field_path"":""Keywords"",""op"":""Add"",""value"":""{Fid(_kwA)}""}}]"),
            in_place: MasterName, acknowledge: true);
        Assert.DoesNotContain("error:", r);
        Assert.Contains("re-read off the written file", r);
        Assert.Equal(1, CountOf(r, "duplicate: "));
    }

    /// <summary>A COMPOSED element is compared too. Mutagen's element classes override Equals structurally, so a
    /// freshly built identical entry IS found — the exclusion this used to carry dropped the check on exactly the
    /// families a repeated bulk write bites hardest.</summary>
    [Fact]
    public void AComposedAddOfAnEntryTheListAlreadyCarriesSaysItIsADuplicate()
    {
        var r = ApplyTools.Apply(_svc, ops: Je($@"[{{""formid"":""{Fid(_lvli)}"",""field_path"":""Entries"",""op"":""Add"",""compose"":{{""type"":""LeveledItemEntry"",""sets"":[{{""path"":""Data.Level"",""value"":""1""}},{{""path"":""Data.Count"",""value"":""1""}},{{""path"":""Data.Reference"",""value"":""{Fid(_weapon)}""}}]}}}}]"));
        Assert.DoesNotContain("error:", r);
        Assert.Contains("duplicate", r);
        // A repeated leveled-list entry is legitimate weighting, so the note reports and does not scold; and the
        // remedy is by INDEX, because Remove-by-value takes a plain value a composed element does not have.
        Assert.Contains("Remove by index", r);
    }

    /// <summary>The other half of the same fact: a composed element the list does not carry says nothing, so the note
    /// distinguishes the two cases rather than firing on every composed Add.</summary>
    [Fact]
    public void AComposedAddOfANewEntrySaysNothingAboutDuplicates()
    {
        var r = ApplyTools.Apply(_svc, ops: Je($@"[{{""formid"":""{Fid(_lvli)}"",""field_path"":""Entries"",""op"":""Add"",""compose"":{{""type"":""LeveledItemEntry"",""sets"":[{{""path"":""Data.Level"",""value"":""7""}},{{""path"":""Data.Count"",""value"":""1""}},{{""path"":""Data.Reference"",""value"":""{Fid(_weapon)}""}}]}}}}]"));
        Assert.DoesNotContain("error:", r);
        Assert.DoesNotContain("duplicate", r);
    }

    /// <summary>composes= appends many built elements in one op and counts them, so the note is one sentence over the
    /// batch rather than one per element.</summary>
    [Fact]
    public void AComposesBatchCountsTheDuplicatesItAppended()
    {
        string Entry(int level) => $@"{{""type"":""LeveledItemEntry"",""sets"":[{{""path"":""Data.Level"",""value"":""{level}""}},{{""path"":""Data.Count"",""value"":""1""}},{{""path"":""Data.Reference"",""value"":""{Fid(_weapon)}""}}]}}";
        var r = ApplyTools.Apply(_svc, ops: Je($@"[{{""formid"":""{Fid(_lvli)}"",""field_path"":""Entries"",""op"":""Add"",""composes"":[{Entry(1)},{Entry(9)}]}}]"));
        Assert.DoesNotContain("error:", r);
        Assert.Contains("1 of the 2 composed elements", r);
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

    /// <summary>An EXTEND has the same before-state and the same hazard: the caller already enabled and sorted the
    /// patch, and a new record's FormLink pulling in a master it did not have leaves it unloadable until the order is
    /// re-sorted. The full master list the render already prints says nothing about which of them is new.</summary>
    [Fact]
    public void ExtendingAPatchThatGrowsItsMasterHeaderSaysToReSort()
    {
        var first = CreateTools.Create(_svc,
            records: Je(@"[{""record_type"":""FormList"",""editorid"":""HcExtEmpty""}]"),
            patch: "HcExtPatch");
        Assert.DoesNotContain("error:", first);
        Assert.DoesNotContain("was added as a master", first);   // a fresh patch's whole header is new by construction

        var r = CreateTools.Create(_svc,
            records: Je($@"[{{""record_type"":""FormList"",""editorid"":""HcExtRefList"",""ops"":[{{""field_path"":""Items"",""op"":""Add"",""value"":""{Fid(_weapon)}""}}]}}]"),
            into: "HcExtPatch.esp");
        Assert.DoesNotContain("error:", r);
        Assert.Contains($"{MasterName} was added as a master", r);
    }

    public void Dispose()
    {
        CorpusRulebook.CorpusPath = _priorCorpusPath;
        _svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }
}

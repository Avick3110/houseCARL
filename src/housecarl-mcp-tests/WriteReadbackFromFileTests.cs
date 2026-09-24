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
/// #683: an Add on a LeveledItem's Entries, into an existing patch that already overrides the record, answered with
/// a grown element count while the file was unchanged. The count came from the in-memory record; only
/// full_readback=true read the file. These tests pin the shape of that report: the file grows, and the per-edit line
/// carries what a fresh read of the written file says.
/// </summary>
[Trait("tier", "integration")]
public sealed class WriteReadbackFromFileTests : IDisposable
{
    const string MasterName = "HcRbMaster.esm";
    const string PatchName = "HcRbPatch.esp";
    const string PatchFolder = "houseCARL - HcRbPatch";

    readonly string _root, _patchPath;
    readonly LoadOrderService _svc;
    readonly FormKey _weapon, _lvli, _topic;

    public WriteReadbackFromFileTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-readback-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "game", "Data"));

        var master = new SkyrimMod(new ModKey("HcRbMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var w = master.Weapons.AddNew();
        w.EditorID = "HcRbSword";
        w.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1 };
        _weapon = w.FormKey;

        var ll = master.LeveledItems.AddNew();
        ll.EditorID = "HcRbList";
        ll.Entries = Ten();
        _lvli = ll.FormKey;

        // A topic whose Subtype an edit can move, so the SNAM marker sync appends its explanation op.
        var topic = master.DialogTopics.AddNew();
        topic.EditorID = "HcRbTopic";
        topic.Subtype = DialogTopic.SubtypeEnum.Custom;
        topic.SubtypeName = new RecordType("CUST");
        _topic = topic.FormKey;

        // The patch from #683: an ACTIVE plugin already overriding the leveled list, so the write's winner IS the
        // file the write extends.
        var patch = new SkyrimMod(new ModKey("HcRbPatch", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var over = patch.LeveledItems.GetOrAddAsOverride(ll);
        over.Entries = Ten();

        var instance = Path.Combine(_root, "inst");
        var mods = Path.Combine(instance, "mods");
        Directory.CreateDirectory(Path.Combine(mods, "RbMasterMod"));
        Directory.CreateDirectory(Path.Combine(mods, PatchFolder));
        master.BeginWrite.ToPath(Path.Combine(mods, "RbMasterMod", MasterName))
            .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        _patchPath = Path.Combine(mods, PatchFolder, PatchName);
        patch.BeginWrite.ToPath(_patchPath).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();
        // The owner marker: into= only extends a folder houseCARL made.
        File.WriteAllText(Path.Combine(mods, PatchFolder, "meta.ini"),
            "[General]\r\ngameName=skyrimse\r\n\r\n[houseCARL]\r\ngenerated=true\r\nplugin=" + PatchName + "\r\n");


        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + MasterName + "\r\n" + PatchName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + MasterName + "\r\n*" + PatchName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+" + PatchFolder + "\r\n+RbMasterMod\r\n");

        _svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "user.json")));

        Noggog.ExtendedList<LeveledItemEntry> Ten()
        {
            var list = new Noggog.ExtendedList<LeveledItemEntry>();
            for (int i = 1; i <= 10; i++)
                list.Add(new LeveledItemEntry
                {
                    Data = new LeveledItemEntryData { Level = (short)i, Count = 1, Reference = new FormLink<IItemGetter>(w.FormKey) },
                });
            return list;
        }
    }

    static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";
    static JsonElement Je(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>How many entries the patch FILE holds right now — a fresh open every time, never the write's own view.</summary>
    int EntriesOnDisk()
    {
        using var back = (IDisposable)SkyrimMod.CreateFromBinaryOverlay(_patchPath, SkyrimRelease.SkyrimSE);
        var mod = (ISkyrimModGetter)back;
        var rec = mod.LeveledItems.First(r => r.FormKey == _lvli);
        return rec.Entries?.Count ?? 0;
    }

    string AddEntry(short level, string? format = null) => ApplyTools.Apply(_svc,
        ops: Je($@"[{{""formid"":""{Fid(_lvli)}"",""field_path"":""Entries"",""op"":""Add"",""compose"":{{""type"":""LeveledItemEntry"",""sets"":[{{""path"":""Data.Level"",""value"":""{level}""}},{{""path"":""Data.Count"",""value"":""1""}},{{""path"":""Data.Reference"",""value"":""{Fid(_weapon)}""}}]}}}}]"),
        into: PatchName, format: format);

    /// <summary>#683's exact shape: the Add must reach the file, not just the record the call held.</summary>
    [Fact]
    public void AnAddIntoAnExistingPatchGrowsTheFile()
    {
        Assert.Equal(10, EntriesOnDisk());
        var before = new FileInfo(_patchPath).Length;
        var r = AddEntry(42);
        Assert.DoesNotContain("error:", r);
        Assert.Equal(11, EntriesOnDisk());
        Assert.True(new FileInfo(_patchPath).Length > before, "patch did not grow");
    }

    /// <summary>And the line the caller reads must say what the file says. #683's response printed
    /// "[list: 11 item(s)]" over a file holding 10.</summary>
    [Fact]
    public void ThePerEditLineMatchesAFreshReadOfTheFile()
    {
        var r = AddEntry(42);
        Assert.DoesNotContain("error:", r);
        Assert.Contains($"[list: {EntriesOnDisk()} item(s)]", r);
    }

    /// <summary>The json document carries the file's reading under its own key, so a consumer never has to tell the
    /// two apart by guessing which of them `after` is.</summary>
    [Fact]
    public void TheJsonOpCarriesTheFilesOwnReading()
    {
        var doc = JsonDocument.Parse(AddEntry(42, format: "json"));
        var op = doc.RootElement.GetProperty("ops")[0];
        Assert.True(doc.RootElement.GetProperty("verify_ran").GetBoolean());
        Assert.Equal("written_file", op.GetProperty("landed_source").GetString());
        Assert.Equal($"[list: {EntriesOnDisk()} item(s)]", op.GetProperty("after_on_disk").GetString());
    }

    /// <summary>Many Adds into one list is the documented bulk shape, and the file answers for the LEAF even where it
    /// cannot answer for each op. Every line carries the file's reading, not a run of value-free ones.</summary>
    [Fact]
    public void EveryLineOfAManyOpRunIntoOneListCarriesTheFilesReading()
    {
        string Entry(int level) => $@"{{""formid"":""{Fid(_lvli)}"",""field_path"":""Entries"",""op"":""Add"",""compose"":{{""type"":""LeveledItemEntry"",""sets"":[{{""path"":""Data.Level"",""value"":""{level}""}},{{""path"":""Data.Count"",""value"":""1""}},{{""path"":""Data.Reference"",""value"":""{Fid(_weapon)}""}}]}}}}";
        var r = ApplyTools.Apply(_svc,
            ops: Je($"[{Entry(41)},{Entry(42)},{Entry(43)}]"), into: PatchName);
        Assert.DoesNotContain("error:", r);
        Assert.Equal(13, EntriesOnDisk());
        Assert.DoesNotContain("not-checked", r);
        // Three lines, each the file's own count for the leaf; the two the last op superseded say so beside it.
        Assert.Equal(3, CountOf(r, "[list: 13 item(s)]"));
        Assert.Equal(2, CountOf(r, "a later op in this call wrote it too"));
    }

    /// <summary>The marker sync's op carries a sentence about what the write did, not a field reading, and the per-edit
    /// line is the only place it is ever printed.</summary>
    [Fact]
    public void TheTopicMarkerSyncStillExplainsItself()
    {
        var r = ApplyTools.Apply(_svc,
            ops: Je($@"[{{""formid"":""{Fid(_topic)}"",""field_path"":""Subtype"",""op"":""Set"",""value"":""Hello""}}]"),
            patch: "HcRbSnam");
        Assert.DoesNotContain("error:", r);
        Assert.Contains("the game buckets by the SNAM marker, so it was synced to match", r);
        Assert.DoesNotContain("not-checked", r);
    }

    static int CountOf(string s, string needle)
    {
        int n = 0;
        for (int i = s.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = s.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    public void Dispose()
    {
        _svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>
/// The render half of #683, driven through the seam the two readings meet at: an outcome whose in-memory value and
/// whose file value deliberately disagree. The response may print only the file's, and where the file gave none it
/// may print no value at all.
/// </summary>
[Trait("tier", "unit")]
public sealed class WriteEditLineSourceTests
{
    const string Memory = "[list: 999 item(s)]";
    const string Disk = "[list: 10 item(s)]";

    static string Render(IReadOnlyList<WritePatchBuilder.OpResult> ops, int maxChars = 0) => WriteTools.Render(
        new WritePatchBuilder.PatchOutcome(true, null, Path.Combine("mods", "houseCARL - X", "X.esp"), true,
            new[] { "Skyrim.esm" }, ops, 1692), maxChars);

    static string RenderOne(WritePatchBuilder.OpResult op) => Render(new[] { op });

    static string RenderJson(IReadOnlyList<WritePatchBuilder.OpResult> ops, int maxChars = 0) => JsonWire.RenderPatchOutcome(
        new WritePatchBuilder.PatchOutcome(true, null, Path.Combine("mods", "houseCARL - X", "X.esp"), true,
            new[] { "Skyrim.esm" }, ops, 1692), maxChars, false, "patch");

    static WritePatchBuilder.OpResult Op(string id = "09ABAB:Test.esm") => new(
        FormKey.Factory(id), "LeveledItem", "Add Entries", true, null, Memory, Memory);

    /// <summary>Many ops, one of them on a record the file does not hold, rendered under a budget small enough that
    /// the op list is cut before reaching it. The hoisted line must still name it, and the cut must not claim every
    /// edit applied.</summary>
    static IReadOnlyList<WritePatchBuilder.OpResult> RunWithOneAbsentAtTheEnd()
    {
        var ops = new List<WritePatchBuilder.OpResult>();
        for (int i = 0; i < 40; i++)
            ops.Add(Op($"{i:X6}:Test.esm") with { AfterOnDisk = Disk, LandedOnDisk = Disk, VerifyAttempted = true });
        ops.Add(Op("0FFFFF:Test.esm") with { RecordAbsentFromFile = true, VerifyAttempted = true });
        return ops;
    }

    [Fact]
    public void ThePerEditLinePrintsTheFileValueNotTheAppliedOne()
    {
        var r = RenderOne(Op() with { AfterOnDisk = Disk, LandedOnDisk = Disk, VerifyAttempted = true });
        Assert.Contains(Disk, r);
        Assert.DoesNotContain(Memory, r);
    }

    [Fact]
    public void AnOpTheFileCouldNotAnswerForIsNotCheckedRatherThanTheAppliedValue()
    {
        var r = RenderOne(Op() with { VerifyAttempted = true });
        Assert.Contains("not-checked", r);
        Assert.DoesNotContain(Memory, r);
    }

    /// <summary>A leaf a later op in the same call overwrote: the line still takes the file's reading of the leaf —
    /// marked as the leaf's final state, since it is not this op's own result — and never the mid-sequence one.</summary>
    [Fact]
    public void AnOpASiblingSupersededTakesTheLeafsFinalFileReading()
    {
        var r = RenderOne(Op() with { SupersededInCall = true, VerifyAttempted = true, AfterOnDisk = Disk });
        Assert.Contains(Disk, r);
        Assert.Contains("a later op in this call wrote it too", r);
        Assert.DoesNotContain(Memory, r);
    }

    /// <summary>The record is not in the file the call just wrote. That is a verdict, not an ambiguity — #683's own
    /// failure mode — so it is said outright and again above the ops, where an op-list cut cannot remove it.</summary>
    [Fact]
    public void ARecordMissingFromTheWrittenFileIsSaidOutright()
    {
        var r = RenderOne(Op() with { RecordAbsentFromFile = true, VerifyAttempted = true });
        Assert.Contains("DID NOT LAND", r);
        Assert.Contains("does not contain this record", r);
        Assert.Contains("1 edit did NOT land", r);
        Assert.DoesNotContain("not-checked", r);
        Assert.DoesNotContain(Memory, r);
    }

    /// <summary>A walk that FAILED says nothing about whether the record is there, so it stays unchecked rather than
    /// inventing the verdict above.</summary>
    [Fact]
    public void AFailedWalkIsNotCheckedRatherThanAVerdict()
    {
        var r = RenderOne(Op() with { VerifyAttempted = true });
        Assert.Contains("not-checked", r);
        Assert.DoesNotContain("DID NOT LAND", r);
    }

    /// <summary>The hoisted line names the records, so a cut that drops their rows cannot take the only statement of
    /// WHICH edits did not land with it.</summary>
    [Fact]
    public void ACutOpListStillNamesTheRecordsThatDidNotLand()
    {
        var r = Render(RunWithOneAbsentAtTheEnd(), maxChars: 900);
        Assert.Contains("truncated:", r);                    // the absent op's own row is past the cut
        Assert.DoesNotContain("0FFFFF:Test.esm  Add Entries", r);
        Assert.Contains("1 edit did NOT land", r);
        Assert.Contains("0FFFFF:Test.esm", r);               // …named in the hoisted line regardless
    }

    /// <summary>And the cut may not assert the opposite of the line above it.</summary>
    [Fact]
    public void ACutOpListDoesNotClaimEveryEditAppliedWhenOneDidNot()
    {
        Assert.DoesNotContain("every one WAS applied", Render(RunWithOneAbsentAtTheEnd(), maxChars: 900));
        // …and with nothing absent the ordinary wording is unchanged.
        var clean = RunWithOneAbsentAtTheEnd().Where(op => !op.RecordAbsentFromFile).ToList();
        Assert.Contains("every one WAS applied", Render(clean, maxChars: 900));
    }

    /// <summary>The json document carries the same verdict outside the array a cut truncates, or a consumer reads a
    /// document saying the write succeeded with the contradicting evidence dropped.</summary>
    [Fact]
    public void TheJsonHoistsTheAbsentVerdictOutOfTheOpsArray()
    {
        var doc = JsonDocument.Parse(RenderJson(RunWithOneAbsentAtTheEnd(), maxChars: 900));
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("ops_record_absent").GetInt32());
        Assert.Equal("0FFFFF:Test.esm", doc.RootElement.GetProperty("record_absent_formids")[0].GetString());
        Assert.DoesNotContain("every one WAS applied", doc.RootElement.GetProperty("truncated_note").GetString());
    }

    /// <summary>A superseded op's `after_on_disk` is present and is NOT that op's own result, so json marks it the way
    /// the text line does rather than leaving a consumer to infer it from `landed_source`.</summary>
    [Fact]
    public void TheJsonMarksASupersededOpsValueAsTheLeafsFinalState()
    {
        var doc = JsonDocument.Parse(RenderJson(new[]
            { Op() with { SupersededInCall = true, VerifyAttempted = true, AfterOnDisk = Disk } }));
        var op = doc.RootElement.GetProperty("ops")[0];
        Assert.Equal(Disk, op.GetProperty("after_on_disk").GetString());
        Assert.True(op.GetProperty("after_on_disk_is_final_leaf").GetBoolean());
        Assert.Equal("superseded", op.GetProperty("landed_source").GetString());
    }
}

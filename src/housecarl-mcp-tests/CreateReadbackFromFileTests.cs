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
/// #763: housecarl_create reported its per-field values from the record it held in memory, not from the file it had
/// just written — the same class of claim #683 was filed about, and the shape #743 fixed on housecarl_apply. These
/// tests pin the create lane's half: the field lines come off the written file, a created record the file does not
/// hold says so, and the CK-parity fills, which have no leaf to re-read, keep their sentences.
/// </summary>
[Trait("tier", "integration")]
public sealed class CreateReadbackFromFileTests : IDisposable
{
    const string MasterName = "HcCrMaster.esm";

    readonly string _root, _priorCorpusPath, _mods;
    readonly LoadOrderService _svc;
    readonly FormKey _topic;

    public CreateReadbackFromFileTests()
    {
        _priorCorpusPath = CorpusRulebook.CorpusPath;
        _root = Path.Combine(Path.GetTempPath(), "hc-create-readback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "game", "Data"));

        var master = new SkyrimMod(new ModKey("HcCrMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        // The parent a nested create has to drag into the artifact to host its child.
        var topic = master.DialogTopics.AddNew();
        topic.EditorID = "HcCrTopic";
        topic.Subtype = DialogTopic.SubtypeEnum.Custom;
        topic.SubtypeName = new RecordType("CUST");
        _topic = topic.FormKey;

        var instance = Path.Combine(_root, "inst");
        _mods = Path.Combine(instance, "mods");
        Directory.CreateDirectory(Path.Combine(_mods, "CrMasterMod"));
        master.BeginWrite.ToPath(Path.Combine(_mods, "CrMasterMod", MasterName))
            .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var genDir = Path.Combine(_root, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(_root, "corpus-ref"));
        CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + MasterName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + MasterName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+CrMasterMod\r\n");

        _svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "user.json")));
    }

    static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";
    static JsonElement Je(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>The patch this call wrote, opened fresh off disk — never the write's own view.</summary>
    ISkyrimModGetter OpenWritten(string patchName, out IDisposable handle)
    {
        var path = Path.Combine(_mods, "houseCARL - " + patchName, patchName + ".esp");
        Assert.True(File.Exists(path), "no patch written at " + path);
        var overlay = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        handle = (IDisposable)overlay;
        return overlay;
    }

    /// <summary>The request spells a precision a float32 leaf cannot hold, so what the file stores is NOT the
    /// string that was asked for: 9.5000001 is written, and read back, as 9.5. A response echoing the request would
    /// print the long one.</summary>
    const string RequestedWeight = "9.5000001";
    const string StoredWeight = "9.5";

    string CreateWeapon(string patch, string? format = null, bool readback = false) => CreateTools.Create(_svc,
        records: Je(@"[{""record_type"":""Weapon"",""editorid"":""HcCrSword"",""ops"":[" +
                    @"{""field_path"":""BasicStats.Damage"",""value"":""42""}," +
                    @"{""field_path"":""BasicStats.Weight"",""value"":""" + RequestedWeight + @"""}]}]"),
        patch: patch, format: format, readback: readback);

    /// <summary>The field line must say what the WRITTEN FILE says, and the response must no longer disclaim that its
    /// values were never re-read.</summary>
    [Fact]
    public void AFlatCreatesFieldLineMatchesAFreshReadOfTheFile()
    {
        var r = CreateWeapon("HcCrFlat");
        Assert.DoesNotContain("error:", r);
        var mod = OpenWritten("HcCrFlat", out var handle);
        using (handle)
        {
            var w = mod.Weapons.Single();
            Assert.Equal(42, w.BasicStats!.Damage);
            Assert.Equal(9.5f, w.BasicStats.Weight);
        }
        Assert.Contains("42", r);
        Assert.DoesNotContain("not-checked", r);
        Assert.DoesNotContain("DID NOT LAND", r);
        Assert.DoesNotContain("were not re-read", r);
        // The value clause is a READING, not the request echoed back: the label carries what was asked for and the
        // clause carries what the file stores, and for this leaf they are different strings.
        Assert.Contains("= " + RequestedWeight + "  -> " + StoredWeight, r);
    }

    /// <summary>json names where each value came from, so a consumer never has to guess whether the file was read.</summary>
    [Fact]
    public void TheJsonCreateCarriesTheFilesOwnReading()
    {
        var doc = JsonDocument.Parse(CreateWeapon("HcCrJson", format: "json"));
        Assert.True(doc.RootElement.GetProperty("verify_ran").GetBoolean());
        Assert.Equal(0, doc.RootElement.GetProperty("records_absent").GetInt32());
        var rec = doc.RootElement.GetProperty("created")[0];
        Assert.True(rec.GetProperty("verified").GetBoolean());
        Assert.False(rec.GetProperty("absent_from_file").GetBoolean());
        var op = rec.GetProperty("ops")[0];
        Assert.Equal("written_file", op.GetProperty("landed_source").GetString());
        Assert.Equal("42", op.GetProperty("after_on_disk").GetString());
        // …and the leaf whose stored value differs from the request: `after_on_disk` is the file's spelling.
        var weight = rec.GetProperty("ops")[1];
        Assert.Equal(StoredWeight, weight.GetProperty("after_on_disk").GetString());
        Assert.Equal("written_file", weight.GetProperty("landed_source").GetString());
    }

    /// <summary>A nested create drags its PARENT into the artifact to host the child. Both have to be in the written
    /// file — a child inside a parent the file does not hold is not in the file either.</summary>
    [Fact]
    public void ANestedCreateVerifiesTheChildAndTheDraggedInParent()
    {
        var r = CreateTools.Create(_svc,
            records: Je($@"[{{""record_type"":""DialogResponses"",""editorid"":""HcCrLine"",""parent"":""{Fid(_topic)}"",""ops"":[{{""field_path"":""Prompt"",""value"":""Hello there""}}]}}]"),
            patch: "HcCrNested");
        Assert.DoesNotContain("error:", r);
        Assert.DoesNotContain("DID NOT LAND", r);
        var mod = OpenWritten("HcCrNested", out var handle);
        using (handle)
        {
            // The parent override is in the file, and the child is inside it.
            var hosted = mod.DialogTopics.Single(t => t.FormKey == _topic);
            Assert.Single(hosted.Responses);
            Assert.Contains("Hello there", r);
        }
    }

    /// <summary>The CK-parity fills have no leaf behind them — their reading is a sentence about what the write did.
    /// The file check must leave them alone rather than reporting them as unanswerable.</summary>
    [Fact]
    public void TheCkParityFillsKeepTheirSentences()
    {
        var r = CreateTools.Create(_svc,
            records: Je(@"[{""record_type"":""DialogTopic"",""editorid"":""HcCrNewTopic"",""ops"":[{""field_path"":""Subtype"",""value"":""Hello""}]}]"),
            patch: "HcCrFills");
        Assert.DoesNotContain("error:", r);
        Assert.Contains("SNAM subtype marker", r);
        Assert.Contains("a new topic with a blank marker is a load CTD", r);
        Assert.DoesNotContain("not-checked", r);
    }

    /// <summary>readback=true still reads every created record back in full — the verify above widened nothing away.</summary>
    [Fact]
    public void ReadbackTrueStillGivesTheFullDump()
    {
        var r = CreateWeapon("HcCrDump", readback: true);
        Assert.DoesNotContain("error:", r);
        Assert.Contains("re-read", r);
        Assert.Contains("BasicStats", r);
    }

    public void Dispose()
    {
        CorpusRulebook.CorpusPath = _priorCorpusPath;
        _svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>
/// The render half of #763, driven through the seam the two readings meet at: a create outcome whose in-memory value
/// and whose file value deliberately disagree. The response may print only the file's; where the file gave none it
/// prints no value at all; and a record the file does not contain is a verdict, hoisted where a cut cannot drop it.
/// </summary>
[Trait("tier", "unit")]
public sealed class CreateFieldLineSourceTests
{
    const string Memory = "999";
    const string Disk = "42";

    static string Render(IReadOnlyList<WritePatchBuilder.CreatedRecord> created, int maxChars = 0) =>
        WriteTools.RenderCreate(new WritePatchBuilder.CreateOutcome(
            true, null, Path.Combine("mods", "houseCARL - X", "X.esp"), false, created, new[] { "Skyrim.esm" }, 1692),
            maxChars);

    static string RenderJson(IReadOnlyList<WritePatchBuilder.CreatedRecord> created, int maxChars = 0) =>
        JsonWire.RenderCreateOutcome(new WritePatchBuilder.CreateOutcome(
            true, null, Path.Combine("mods", "houseCARL - X", "X.esp"), false, created, new[] { "Skyrim.esm" }, 1692),
            maxChars, false, "patch");

    static WritePatchBuilder.OpResult Op(FormKey key) =>
        new(key, "Weapon", "Set BasicStats.Damage", true, null, Memory, Memory);

    static WritePatchBuilder.CreatedRecord Rec(string id, WritePatchBuilder.OpResult op, string edid = "HcCrSword") =>
        new(FormKey.Factory(id), "Weapon", edid, new[] { op });

    static WritePatchBuilder.CreatedRecord Verified(string id = "000800:X.esp") =>
        Rec(id, Op(FormKey.Factory(id)) with { AfterOnDisk = Disk, LandedOnDisk = Disk, VerifyAttempted = true })
            with { VerifyAttempted = true };

    /// <summary>Many created records, one of them missing from the file, rendered under a budget that cuts its row.</summary>
    static IReadOnlyList<WritePatchBuilder.CreatedRecord> RunWithOneAbsentAtTheEnd()
    {
        var created = new List<WritePatchBuilder.CreatedRecord>();
        for (int i = 0; i < 40; i++) created.Add(Verified($"{0x800 + i:X6}:X.esp") with { });
        created.Add(Rec("0FFFFF:X.esp", Op(FormKey.Factory("0FFFFF:X.esp")) with
            { RecordAbsentFromFile = true, VerifyAttempted = true })
            with { VerifyAttempted = true, AbsentFromFile = true });
        return created;
    }

    [Fact]
    public void ThePerFieldLinePrintsTheFileValueNotTheAppliedOne()
    {
        var r = Render(new[] { Verified() });
        Assert.Contains(Disk, r);
        Assert.DoesNotContain(Memory, r);
    }

    [Fact]
    public void AFieldTheFileCouldNotReadIsNotCheckedRatherThanTheAppliedValue()
    {
        var r = Render(new[] { Rec("000800:X.esp", Op(FormKey.Factory("000800:X.esp")) with { VerifyAttempted = true })
            with { VerifyAttempted = true } });
        Assert.Contains("not-checked", r);
        Assert.DoesNotContain(Memory, r);
    }

    /// <summary>A CK-parity fill's reading is a sentence about what the write did, and the line prints it as it
    /// stands rather than routing it through the file arms and dropping it.</summary>
    [Fact]
    public void ACkParityFillStillPrintsItsSentence()
    {
        const string Sentence = "CUST — derived from Subtype=Custom";
        var key = FormKey.Factory("000800:X.esp");
        var r = Render(new[]
        {
            new WritePatchBuilder.CreatedRecord(key, "DialogTopic", "HcCrTopic", new[]
            {
                new WritePatchBuilder.OpResult(key, "DialogTopic", "SubtypeName auto-set to CUST", true, null, Sentence)
                    { AfterIsNote = true },
            }) { VerifyAttempted = true },
        });
        Assert.Contains(Sentence, r);
        Assert.DoesNotContain("not-checked", r);
    }

    /// <summary>The created record is not in the file the call just wrote. That is a verdict, not an ambiguity, and it
    /// is said on the row AND above the rows, where a cut cannot remove it.</summary>
    [Fact]
    public void ACreatedRecordMissingFromTheWrittenFileIsSaidOutright()
    {
        var key = FormKey.Factory("000800:X.esp");
        var r = Render(new[] { Rec("000800:X.esp", Op(key) with { RecordAbsentFromFile = true, VerifyAttempted = true })
            with { VerifyAttempted = true, AbsentFromFile = true } });
        Assert.Contains("DID NOT LAND", r);
        Assert.Contains("does not contain this record", r);
        Assert.Contains("1 created record did NOT land", r);
        Assert.DoesNotContain(Memory, r);
    }

    /// <summary>A nested child is only in the file if its PARENT is. A missing parent is the child's verdict too, and
    /// the parent is named — the child's own FormID would send the reader to look for the wrong record.</summary>
    [Fact]
    public void AChildWhoseParentIsMissingDidNotLandEither()
    {
        var key = FormKey.Factory("000800:X.esp");
        var parent = FormKey.Factory("00ABCD:HcCrMaster.esm");
        var r = Render(new[] { Rec("000800:X.esp", Op(key) with { AfterOnDisk = Disk, LandedOnDisk = Disk, VerifyAttempted = true })
            with { VerifyAttempted = true, ParentKey = parent, ParentAbsentFromFile = true } });
        Assert.Contains("DID NOT LAND", r);
        Assert.Contains("00ABCD:HcCrMaster.esm", r);
        Assert.Contains("1 created record did NOT land", r);
    }

    /// <summary>A walk that FAILED says nothing about whether a record is there, so it stays unchecked rather than
    /// inventing the verdict above.</summary>
    [Fact]
    public void AFailedWalkIsNotCheckedRatherThanAVerdict()
    {
        var key = FormKey.Factory("000800:X.esp");
        var r = Render(new[] { Rec("000800:X.esp", Op(key)) });
        Assert.Contains("not-checked", r);
        Assert.DoesNotContain("DID NOT LAND", r);
    }

    /// <summary>The hoisted line names the records, so a cut that drops their rows cannot take the only statement of
    /// WHICH creates did not land with it — and the cut may not assert the opposite of the line above it.</summary>
    [Fact]
    public void ACutCreatedListStillNamesTheRecordsThatDidNotLand()
    {
        var r = Render(RunWithOneAbsentAtTheEnd(), maxChars: 900);
        Assert.Contains("truncated:", r);
        Assert.Contains("1 created record did NOT land", r);
        Assert.Contains("0FFFFF:X.esp", r);
        Assert.DoesNotContain("every one WAS created", r);
        // …and with nothing absent the ordinary wording is unchanged.
        var clean = RunWithOneAbsentAtTheEnd().Where(c => !c.AbsentFromFile).ToList();
        Assert.Contains("every one WAS created", Render(clean, maxChars: 900));
    }

    /// <summary>The DID NOT LAND remedy is the CREATE lane's, not the edit lane's: re-issuing an edit is safe and
    /// re-issuing a create allocates the records a second time, which is the trap the truncation sentence a few lines
    /// away forbids. The two must not say opposite things in one response.</summary>
    [Fact]
    public void TheCreateDidNotLandRemedyDoesNotTellTheCallerToReIssue()
    {
        var key = FormKey.Factory("000800:X.esp");
        var r = Render(new[] { Rec("000800:X.esp", Op(key) with { RecordAbsentFromFile = true, VerifyAttempted = true })
            with { VerifyAttempted = true, AbsentFromFile = true } });
        Assert.DoesNotContain("re-issue the edit", r);
        Assert.Contains("do NOT re-issue the create", r);
        Assert.Contains("allocates the records AGAIN", r);
        Assert.Contains(ToolNames.Records, r);          // the read it sends you to instead
        // One record, its row rendered, and it did not land: the response may not then close by telling the caller
        // that the FormID on that row is how to reference the record.
        Assert.DoesNotContain("the new FormID above is how you reference this record", r);
        Assert.Contains("do not reference them", r);
    }

    /// <summary>The cap that drops EVERY row is where the hoist is load-bearing, and the sentence under it must not
    /// then assert that all of them were created.</summary>
    [Fact]
    public void ACapThatCutsEveryRowDoesNotClaimEveryRecordWasCreated()
    {
        var r = Render(RunWithOneAbsentAtTheEnd(), maxChars: 400);
        Assert.Contains("no records are listed above", r);
        Assert.DoesNotContain("WERE created", r);
        Assert.Contains("1 created record did NOT land", r);
    }

    /// <summary>A PARENT's FormID is not a FormID this call created. After a cut the hoist is all that survives, so
    /// it may not offer the two under one heading.</summary>
    [Fact]
    public void TheHoistedListLabelsAParentAsAParent()
    {
        var key = FormKey.Factory("000800:X.esp");
        var parent = FormKey.Factory("00ABCD:HcCrMaster.esm");
        var r = Render(new[] { Rec("000800:X.esp", Op(key) with { AfterOnDisk = Disk, LandedOnDisk = Disk, VerifyAttempted = true })
            with { VerifyAttempted = true, ParentKey = parent, ParentAbsentFromFile = true } }, maxChars: 400);
        Assert.Contains("Their parent record(s)", r);
        // The child's own id is NOT offered as a record the file does not hold — the file holds it; its parent is gone.
        var hoist = r.Substring(0, r.IndexOf("created 1 record", StringComparison.Ordinal));
        Assert.Contains("00ABCD:HcCrMaster.esm", hoist);
        Assert.DoesNotContain("Record(s): 000800:X.esp", hoist);
    }

    /// <summary>Three INFOs created under ONE Skyrim.esm DIAL whose dragged-in override did not serialize. A child
    /// lives inside its parent's group, so the missing parent takes all three with it and every one of them carries
    /// BOTH flags — which is every real missing-parent case. The row must name the PARENT (the child's own id
    /// sends the reader to look for the wrong record), and the hoist must carry both the three children and the one
    /// parent, since after a row cut the hoist is all that is left.</summary>
    static IReadOnlyList<WritePatchBuilder.CreatedRecord> ThreeInfosUnderOneMissingTopic()
    {
        var parent = FormKey.Factory("0130A1:Skyrim.esm");
        var made = new List<WritePatchBuilder.CreatedRecord>();
        for (int i = 0; i < 3; i++)
        {
            var key = FormKey.Factory($"{0x800 + i:X6}:X.esp");
            made.Add(new WritePatchBuilder.CreatedRecord(key, "DialogResponses", "HcCrLine" + i, new[]
                { Op(key) with { RecordAbsentFromFile = true, VerifyAttempted = true } })
                { VerifyAttempted = true, AbsentFromFile = true, ParentKey = parent, ParentAbsentFromFile = true });
        }
        return made;
    }

    [Fact]
    public void ThreeChildrenUnderOneMissingParentNameThatParent()
    {
        var r = Render(ThreeInfosUnderOneMissingTopic());
        Assert.Contains("3 created records did NOT land", r);
        // Every row names the parent as the cause, not the generic clause under the child's own id.
        Assert.Equal(3, CountOf(r, "its parent 0130A1:Skyrim.esm is not in the written file"));
        // …and the hoist carries both lists: the three children, and the one record whose absence took them.
        var hoist = r.Substring(0, r.IndexOf("created 3 records", StringComparison.Ordinal));
        Assert.Contains("Their parent record(s)", hoist);
        Assert.Contains("0130A1:Skyrim.esm", hoist);
        Assert.Contains("000800:X.esp", hoist);
    }

    [Fact]
    public void TheJsonCountsThreeChildrenAndOneMissingParent()
    {
        var doc = JsonDocument.Parse(RenderJson(ThreeInfosUnderOneMissingTopic()));
        Assert.Equal(3, doc.RootElement.GetProperty("records_absent").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("record_absent_formids_total").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("parent_absent_formids_total").GetInt32());
        Assert.Equal("0130A1:Skyrim.esm", doc.RootElement.GetProperty("parent_absent_formids")[0].GetString());
    }

    static int CountOf(string s, string needle)
    {
        int n = 0;
        for (int i = s.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = s.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>json keeps them apart for the same reason, and counts each.</summary>
    [Fact]
    public void TheJsonKeepsParentAbsencesInTheirOwnArray()
    {
        var key = FormKey.Factory("000800:X.esp");
        var parent = FormKey.Factory("00ABCD:HcCrMaster.esm");
        var doc = JsonDocument.Parse(RenderJson(new[]
        {
            Rec("000800:X.esp", Op(key) with { AfterOnDisk = Disk, LandedOnDisk = Disk, VerifyAttempted = true })
                with { VerifyAttempted = true, ParentKey = parent, ParentAbsentFromFile = true },
        }));
        Assert.Equal(1, doc.RootElement.GetProperty("records_absent").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("record_absent_formids_total").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("parent_absent_formids_total").GetInt32());
        Assert.Equal("00ABCD:HcCrMaster.esm", doc.RootElement.GetProperty("parent_absent_formids")[0].GetString());
    }

    /// <summary>A walk that threw part way still reached some records, and their ops carry the file's readings. The
    /// record row may not then say it was never checked over op lines that came off the file.</summary>
    [Fact]
    public void ARecordTheWalkReachedIsNotReportedUnchecked()
    {
        var key = FormKey.Factory("000800:X.esp");
        var doc = JsonDocument.Parse(RenderJson(new[] { Verified("000800:X.esp") }));
        var rec = doc.RootElement.GetProperty("created")[0];
        Assert.True(rec.GetProperty("verified").GetBoolean());
        Assert.Equal("written_file", rec.GetProperty("ops")[0].GetProperty("landed_source").GetString());
        // …and the contradiction the flag exists to prevent: no op under an unverified record claims the file.
        var un = JsonDocument.Parse(RenderJson(new[] { Rec("000800:X.esp", Op(key)) }));
        var unrec = un.RootElement.GetProperty("created")[0];
        Assert.False(unrec.GetProperty("verified").GetBoolean());
        Assert.NotEqual("written_file", unrec.GetProperty("ops")[0].GetProperty("landed_source").GetString());
    }

    /// <summary>A blob the file answered with was never PARSED — an opaque field re-reads byte-identical whatever
    /// FormVersion its layout suits (#529). Printing it off the file without that clause reads as a judged value.</summary>
    [Fact]
    public void AnOpaqueLeafPrintedFromTheFileKeepsItsStructureCaveat()
    {
        var key = FormKey.Factory("000800:X.esp");
        var r = Render(new[] { Rec("000800:X.esp", Op(key) with
            { AfterOnDisk = "020000000000000000000000", LandedOnDisk = "0200", VerifyAttempted = true, AfterOnDiskBytes = 12 })
            with { VerifyAttempted = true } });
        Assert.Contains("020000000000000000000000", r);
        Assert.Contains("structure NOT checked", r);
        Assert.Contains("12 opaque byte(s)", r);
    }

    /// <summary>The json document carries the same verdict outside the array a cut truncates.</summary>
    [Fact]
    public void TheJsonHoistsTheAbsentVerdictOutOfTheCreatedArray()
    {
        var doc = JsonDocument.Parse(RenderJson(RunWithOneAbsentAtTheEnd(), maxChars: 900));
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("verify_ran").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("records_absent").GetInt32());
        Assert.Equal("0FFFFF:X.esp", doc.RootElement.GetProperty("record_absent_formids")[0].GetString());
        Assert.DoesNotContain("every one WAS created", doc.RootElement.GetProperty("truncated_note").GetString());
    }
}

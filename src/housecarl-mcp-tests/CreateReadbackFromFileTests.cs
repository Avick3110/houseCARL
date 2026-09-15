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

    string CreateWeapon(string patch, string? format = null, bool readback = false) => CreateTools.Create(_svc,
        records: Je(@"[{""record_type"":""Weapon"",""editorid"":""HcCrSword"",""ops"":[" +
                    @"{""field_path"":""BasicStats.Damage"",""value"":""42""}]}]"),
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
        }
        Assert.Contains("42", r);
        Assert.DoesNotContain("not-checked", r);
        Assert.DoesNotContain("DID NOT LAND", r);
        Assert.DoesNotContain("were not re-read", r);
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

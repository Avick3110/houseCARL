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
/// #529: a write's verify sentence used to say "re-read clean" about a record carrying an opaque blob it never
/// parsed, and a read rendered that blob as unannotated hex. Mutagen models <c>Model.Data</c> (MODT) as raw bytes,
/// so the forced in-place re-read cannot fail on a blob whose layout does not suit the record's FormVersion — the
/// plugin loads here and crashes the game. Nothing here decodes MODT; the two fixes are what the answer SAYS.
///
/// <para>The world is built PER TEST (xUnit constructs the class once per method) because the in-place edit rewrites
/// the plugin, which would poison a shared instance.</para>
/// </summary>
[Trait("tier", "integration")]
public sealed class OpaqueBytesVerifyTests : IDisposable
{
    const string PluginName = "HcBlobPlugin.esp";

    readonly string _root;
    readonly string _priorCorpusPath;
    readonly LoadOrderService _svc;
    readonly FormKey _stat;
    readonly FormKey _cell, _refr, _matObject;

    /// <summary>How many blobs the list-carrying fixture record holds — more than the caveat names, so the bound is
    /// exercised rather than assumed.</summary>
    const int MatBlobCount = 8;

    /// <summary>The FormVersion the containing CELL is stamped at — deliberately NOT the 44 its placed reference
    /// carries, so a '*parent'-hopped read annotated off the wrong record would be visibly wrong.</summary>
    const ushort CellFormVersion = 43;

    /// <summary>The MODT-shaped blob the fixture record carries: real length, arbitrary content. Its BYTES never
    /// matter to any assertion here — only that houseCARL says it did not judge them.</summary>
    static readonly byte[] Blob = Enumerable.Range(0, 24).Select(i => (byte)(i * 7)).ToArray();

    public OpaqueBytesVerifyTests()
    {
        _priorCorpusPath = CorpusRulebook.CorpusPath;
        _root = Path.Combine(Path.GetTempPath(), "hc-blob-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "game", "Data"));

        var mod = new SkyrimMod(new ModKey("HcBlobPlugin", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var st = mod.Statics.AddNew();
        st.EditorID = "HcBlobStatic";
        st.Model = new Model { File = "meshes\\hcblob.nif", Data = new Noggog.MemorySlice<byte>(Blob) };
        _stat = st.FormKey;

        // A record carrying a LIST of blobs — every element is its own leaf at the read-back's depth, which is what
        // the caveat's bound is for.
        var mo = mod.MaterialObjects.AddNew();
        mo.EditorID = "HcBlobMat";
        for (int i = 0; i < MatBlobCount; i++)
            mo.DNAMs.Add(new Noggog.MemorySlice<byte>(new byte[] { (byte)i, 1, 2, 3 }));
        _matObject = mo.FormKey;

        // An interior cell carrying its OWN blob at a DIFFERENT FormVersion from the reference inside it: the
        // '*parent' hop reads the blob off the cell, so the annotation must name the cell's stamp, not the REFR's.
        _cell = new FormKey(mod.ModKey, 0xC01);
        _refr = new FormKey(mod.ModKey, 0xC10);
        var cell = new Cell(_cell, SkyrimRelease.SkyrimSE)
        {
            EditorID = "HcBlobCell",
            Flags = Cell.Flag.IsInteriorCell,
            FormVersion = CellFormVersion,
            OcclusionData = new Noggog.MemorySlice<byte>(Blob),
        };
        cell.Temporary.Add(new PlacedObject(_refr, SkyrimRelease.SkyrimSE) { EditorID = "HcBlobRef" });
        var block = new CellBlock { BlockNumber = 1, GroupType = GroupTypeEnum.InteriorCellBlock };
        var sub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        sub.Cells.Add(cell);
        block.SubBlocks.Add(sub);
        mod.Cells.Records.Add(block);

        var instance = Path.Combine(_root, "inst");
        var mods = Path.Combine(instance, "mods");
        Directory.CreateDirectory(Path.Combine(mods, "BlobMod"));
        mod.BeginWrite.ToPath(Path.Combine(mods, "BlobMod", PluginName))
            .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var genDir = Path.Combine(_root, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(_root, "corpus-ref"));
        CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + PluginName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + PluginName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+BlobMod\r\n");

        _svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "user.json")));
    }

    static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";
    static JsonElement Je(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>An in-place edit onto the blob-carrying STAT — the lane whose verify is FORCED on, which is where the
    /// "re-read clean" claim is made. The field edited is beside the point; the verify sentence is under test.</summary>
    string EditInPlace(string? format = null) => ApplyTools.Apply(_svc,
        ops: Je($@"[{{""formid"":""{Fid(_stat)}"",""field_path"":""EditorID"",""op"":""Set"",""value"":""HcBlobStaticEdited""}}]"),
        in_place: PluginName, acknowledge: true, format: format);

    // ---- the verify sentence names what it could not judge ----------------------------------------

    [Fact]
    public void TheVerifySentenceNamesTheOpaqueFieldItReReadAsBytesOnly()
    {
        var r = EditInPlace();
        Assert.DoesNotContain("error:", r);
        var line = r.Split('\n').Single(l => l.Contains("re-read clean", StringComparison.Ordinal)
                                          && l.Contains("Model.Data", StringComparison.Ordinal));
        Assert.Contains("Model.Data (" + Blob.Length + " byte(s))", line);
        Assert.Contains("re-read as bytes only", line);
        Assert.Contains("NOT checked", line);
    }

    /// <summary>A record carrying a LIST of blobs (MaterialObject.DNAMs, DialogView.TNAMs) expands to one leaf per
    /// element at the read-back's depth, so the caveat names the first few and counts the rest — the compact lane
    /// exists to stay under the host's cap and must not be the thing that blows it.</summary>
    [Fact]
    public void ARecordWithManyBlobsNamesAFewAndCountsTheRest()
    {
        var r = ApplyTools.Apply(_svc,
            ops: Je($@"[{{""formid"":""{Fid(_matObject)}"",""field_path"":""EditorID"",""op"":""Set"",""value"":""HcBlobMatEdited""}}]"),
            in_place: PluginName, acknowledge: true);
        Assert.DoesNotContain("error:", r);
        var line = r.Split('\n').Single(l => l.Contains("re-read clean", StringComparison.Ordinal)
                                          && l.Contains("DNAMs[0]", StringComparison.Ordinal));
        Assert.Contains("DNAMs[0] (4 byte(s))", line);
        Assert.Contains("and " + (MatBlobCount - 3) + " more", line);
        Assert.DoesNotContain("DNAMs[" + (MatBlobCount - 1) + "]", line);
    }

    /// <summary>The folded row/dense render takes the SHORT form: a cell there is positional and width-bounded, and
    /// the sentence the read lane renders would cut a scan far shorter than it used to.</summary>
    [Fact]
    public void TheFoldedRowRenderUsesTheShortAnnotation()
    {
        var r = RecordsTools.Records(_svc, formids: new[] { Fid(_matObject) },
            project: new RecordsTools.RecordsProject { form = "rows", fields = new[] { "DNAMs" }, depth = 4 });
        Assert.DoesNotContain("error:", r);
        Assert.Contains("[opaque 4B @FV44]", r);
        Assert.DoesNotContain("layout follows this record", r);
    }

    /// <summary>The caveat is carried by the blob, not pinned to a record type: a leaf with no bytes field gets no
    /// marker at all, so the verify sentence stays plain on the common write.</summary>
    [Fact]
    public void ARecordThatCarriesNoBlobIsMarkedNowhere()
    {
        var kw = new SkyrimMod(new ModKey("HcNoBlob", ModType.Plugin), SkyrimRelease.SkyrimSE).Keywords.AddNew();
        kw.EditorID = "HcNoBlobKeyword";
        var read = ReadEngine.ReadFields(kw, null, 4);
        Assert.DoesNotContain(read.Fields, f => f.Bytes is not null);
    }

    // ---- a read annotates the blob with the record's FormVersion ----------------------------------

    [Fact]
    public void AReadAnnotatesTheBlobWithItsByteCountAndTheRecordsFormVersion()
    {
        var r = RecordsTools.Records(_svc, formids: new[] { Fid(_stat) },
            project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Model.Data" }, depth = 4 });
        Assert.DoesNotContain("error:", r);
        Assert.Contains("opaque bytes, " + Blob.Length + " byte(s)", r);
        Assert.Contains("FormVersion 44", r);
        Assert.Contains("not parsed", r);
    }

    /// <summary>The annotation is display-only: the hex token a write feeds straight back is untouched.</summary>
    [Fact]
    public void TheAnnotationLeavesTheRoundTripHexTokenAlone()
    {
        var r = RecordsTools.Records(_svc, formids: new[] { Fid(_stat) },
            project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Model.Data" }, depth = 4 });
        Assert.Contains(Convert.ToHexString(Blob), r);
    }

    /// <summary>A '*parent'-hopped blob is annotated off the record the leaf was READ on, not the record the read
    /// started from. Stamping it with the entry record's FormVersion would make a real mismatch read as a match,
    /// which is worse than no annotation at all.</summary>
    [Fact]
    public void AParentHoppedBlobCarriesTheContainingRecordsFormVersion()
    {
        var direct = RecordsTools.Records(_svc, formids: new[] { Fid(_cell) },
            project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "OcclusionData" }, depth = 4 });
        Assert.Contains("FormVersion " + CellFormVersion, direct);

        var hopped = RecordsTools.Records(_svc, formids: new[] { Fid(_refr) },
            project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "*parent.OcclusionData" }, depth = 4 });
        Assert.DoesNotContain("error:", hopped);
        Assert.Contains("FormVersion " + CellFormVersion, hopped);
        Assert.DoesNotContain("FormVersion 44", hopped);
    }

    /// <summary>The blob is marked STRUCTURALLY, so a consumer deciding whether a value was judged never has to match
    /// a hex-looking token or parse the prose.</summary>
    [Fact]
    public void TheBlobCarriesItsByteLengthOnTheFieldItself()
    {
        var mod = new SkyrimMod(new ModKey("HcBlobMem", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var st = mod.Statics.AddNew();
        st.Model = new Model { File = "meshes\\mem.nif", Data = new Noggog.MemorySlice<byte>(Blob) };
        var read = ReadEngine.ReadFields(st, new[] { "Model.Data" }, 4);
        Assert.Equal(Blob.Length, read.Fields.Single(x => x.Path == "Model.Data").Bytes);
    }

    /// <summary>…and it reaches the json read lane, which is the one a field-level blob copy would be driven from:
    /// the length is a NUMBER there, so a consumer never regexes the prose to learn a value went unjudged.</summary>
    [Fact]
    public void TheJsonReadLaneCarriesTheByteLengthAsANumber()
    {
        var r = RecordsTools.Records(_svc, formids: new[] { Fid(_stat) },
            project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Model.Data" }, depth = 4 },
            format: "json");
        using var doc = JsonDocument.Parse(r);
        var field = doc.RootElement.GetProperty("records")[0].GetProperty("fields")
            .EnumerateArray().Single(f => f.GetProperty("path").GetString() == "Model.Data");
        Assert.Equal(Blob.Length, field.GetProperty("opaque_bytes").GetInt32());
    }

    public void Dispose()
    {
        CorpusRulebook.CorpusPath = _priorCorpusPath;
        try { Directory.Delete(_root, true); } catch { }
    }
}

/// <summary>
/// #529's probe: does Mutagen's <c>Duplicate(newKey)</c> carry the SOURCE record's FormVersion, or re-stamp the
/// duplicate? It decides whether a copy off an old-FormVersion record can hand a stale blob to a record stamped new,
/// which is the crash the issue reports. This test states the answer; it fixes nothing.
/// </summary>
[Trait("tier", "unit")]
public sealed class DuplicateFormVersionProbeTests
{
    [Fact]
    public void DuplicateCarriesTheSourceRecordsFormVersion()
    {
        var mod = new SkyrimMod(new ModKey("HcFvProbe", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var src = mod.Statics.AddNew();
        src.EditorID = "HcFvSource";
        src.FormVersion = 39;
        src.Model = new Model { File = "meshes\\old.nif", Data = new Noggog.MemorySlice<byte>(new byte[] { 1, 2, 3, 4 }) };

        var dup = (IMajorRecordGetter)src.Duplicate(mod.GetNextFormKey());

        // The answer the issue asked for, asserted rather than described.
        Assert.Equal((ushort?)39, dup.FormVersion);
    }

    /// <summary>The half that decides whether the game ever sees it: the duplicate goes through Mutagen's WRITER, so
    /// the FormVersion on disk is the one that matters. Written and read straight back.</summary>
    [Fact]
    public void TheWrittenFileKeepsTheDuplicatesCarriedFormVersion()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hc-fv-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var mod = new SkyrimMod(new ModKey("HcFvWrite", ModType.Plugin), SkyrimRelease.SkyrimSE);
            var src = mod.Statics.AddNew();
            src.EditorID = "HcFvSource";
            src.FormVersion = 39;
            src.Model = new Model { File = "meshes\\old.nif", Data = new Noggog.MemorySlice<byte>(new byte[] { 1, 2, 3, 4 }) };
            var dup = (Static)src.Duplicate(mod.GetNextFormKey());
            dup.EditorID = "HcFvCopy";
            mod.Statics.Add(dup);

            var path = Path.Combine(dir, "HcFvWrite.esp");
            mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            using var back = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            var onDisk = back.Statics.Single(s => s.EditorID == "HcFvCopy");
            Assert.Equal((ushort?)39, onDisk.FormVersion);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

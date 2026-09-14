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
        var line = r.Split('\n').Single(l => l.Contains("re-read clean", StringComparison.Ordinal));
        Assert.Contains("Model.Data", line);
        Assert.Contains("opaque byte(s) only", line);
        Assert.Contains("NOT checked", line);
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

using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// Subrecords Mutagen writes at 0 bytes on a new record (DECISIONS 2026-10-25 (5), (6)): houseCARL fills nothing, a
/// create that leaves one says so, an author's value (an explicit empty included) is silent, and records read from
/// disk and carried keep their bytes with no warning. Membership is the generator's bare-construct probe.
/// </summary>
[Trait("tier", "integration")]
public sealed class EmptyOnCreateTests
{
    /// <summary>A master with a 4-byte-INAM MESG, and two plugins (one with another) to merge and compact.</summary>
    sealed class World : IDisposable
    {
        readonly ScratchMo2 _mo2 = new("hc-empty-on-create-");
        public LoadOrderService Svc { get; }
        public static readonly ModKey Master = new("HcEoMaster", ModType.Master);
        public static readonly ModKey A = new("HcEoA", ModType.Plugin);
        public static readonly ModKey B = new("HcEoB", ModType.Plugin);
        public FormKey MasterMessage { get; }

        public World()
        {
            MasterMessage = Write(Master, "HcEoMasterMsg");
            Write(A, "HcEoAMsg");
            Write(B, "HcEoBMsg");
            var names = new[] { Master, A, B }.Select(k => k.FileName.String).ToList();
            _mo2.Profile(string.Join("\r\n", names) + "\r\n", string.Join("", names.Select(n => "*" + n + "\r\n")),
                string.Join("", new[] { B, A, Master }.Select(k => "+" + k.Name + "\r\n")));
            Svc = _mo2.Open();
        }

        /// <summary>One plugin in its own mod folder holding one MESG with the 4-byte INAM vanilla carries.</summary>
        FormKey Write(ModKey key, string editorId)
        {
            var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
            var msg = m.Messages.AddNew(editorId);
            msg.INAM = new byte[4];
            m.BeginWrite.ToPath(_mo2.InMod(key.Name, key)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            return msg.FormKey;
        }

        public WritePatchBuilder.CreateOutcome Create(string type, string editorId, string? into = null, params BulkOp[] ops) =>
            Svc.CreateRecordsBatch(new[] { new CreateOp { RecordType = type, Editorid = editorId, Operations = ops } },
                into is null ? editorId : null, into);

        public void Dispose()
        {
            Svc.Dispose();
            _mo2.Delete();
        }
    }

    static BulkOp Set(string path, string value) => new() { FieldPath = path, Verb = "Set", Value = value };

    /// <summary>The payload lengths of one subrecord signature on one record, as the written file holds them.</summary>
    static List<int> Lengths(string pluginPath, Func<FormKey, bool> which, string sig)
    {
        var walk = SubrecordInventory.Walk(File.ReadAllBytes(pluginPath), ModKey.FromFileName(Path.GetFileName(pluginPath)));
        return walk.Where(kv => which(kv.Key)).SelectMany(kv => kv.Value.Order).Where(k => k.Sig == sig).Select(k => k.Length).ToList();
    }

    static WritePatchBuilder.CreatedRecord Created(WritePatchBuilder.CreateOutcome o)
    {
        Assert.True(o.Success, "refused: " + o.Error);
        return Assert.Single(o.Created);
    }

    // Each probed field: a bare create writes it at 0 bytes as today, and the result names it.
    [Theory]
    [InlineData("Message", "MESG", "INAM")]
    [InlineData("IdleAnimation", "IDLE", "ANAM")]
    [InlineData("CameraPath", "CPTH", "ANAM")]
    [InlineData("Package", "PACK", "XNAM")]
    [InlineData("FootstepSet", "FSTS", "DATA")]
    public void BareCreate_WritesTheEmptySubrecordAndSaysSo(string type, string recSig, string sig)
    {
        using var w = new World();
        var o = w.Create(type, "HcEoBare" + recSig);
        var rec = Created(o);

        Assert.Equal(new[] { 0 }, Lengths(o.OutputPath, k => k == rec.FormKey, sig));
        var line = Assert.Single(rec.EmptySubrecords);
        Assert.Contains($"{recSig} {sig} at 0 bytes", line);
        Assert.Contains($"{recSig} {sig} at 0 bytes", WriteTools.RenderCreate(o, 20000));
        Assert.Contains($"{recSig} {sig} at 0 bytes", JsonWire.RenderCreateOutcome(o, 20000, readback: false, lane: "patch"));
    }

    // Upsert-replace: re-creating the patch's own record at the same FormID says the same.
    [Fact]
    public void UpsertReplaceCreate_SaysTheSame()
    {
        using var w = new World();
        var first = w.Create("Message", "HcEoUpsert");
        Created(first);
        var o = w.Create("Message", "HcEoUpsert", into: Path.GetFileName(first.OutputPath));
        var rec = Created(o);

        Assert.True(rec.ReplacedExisting);
        Assert.Equal(new[] { 0 }, Lengths(o.OutputPath, k => k == rec.FormKey, "INAM"));
        Assert.Contains("MESG INAM at 0 bytes", Assert.Single(rec.EmptySubrecords));
    }

    // Aaron's pick (DECISIONS 2026-10-25 (6)): an explicit empty the author sets is the author's value, silent.
    [Fact]
    public void ExplicitEmpty_IsSilent()
    {
        using var w = new World();
        var o = w.Create("Message", "HcEoExplicit", null, Set("INAM", ""));
        var rec = Created(o);

        Assert.Equal(new[] { 0 }, Lengths(o.OutputPath, k => k == rec.FormKey, "INAM"));
        Assert.Empty(rec.EmptySubrecords);
    }

    // An edit of an unrelated field does not silence it: only the probed field's own edit does.
    [Fact]
    public void UnrelatedEdit_StillSaysSo()
    {
        using var w = new World();
        var o = w.Create("Message", "HcEoUnrelated", null, Set("Description", "Hello"));
        var rec = Created(o);

        Assert.Equal(new[] { 0 }, Lengths(o.OutputPath, k => k == rec.FormKey, "INAM"));
        Assert.Contains("MESG INAM at 0 bytes", Assert.Single(rec.EmptySubrecords));
    }

    /// <summary>One batch of <paramref name="count"/> bare Messages into one fresh patch.</summary>
    static WritePatchBuilder.CreateOutcome CreateMessages(World w, string prefix, int count)
    {
        var o = w.Svc.CreateRecordsBatch(Enumerable.Range(0, count)
            .Select(i => new CreateOp { RecordType = "Message", Editorid = prefix + i }).ToArray(), prefix, null);
        Assert.True(o.Success, o.Error);
        Assert.Equal(count, o.Created.Count);
        return o;
    }

    static int Occurrences(string haystack, string needle) =>
        (haystack.Length - haystack.Replace(needle, "").Length) / needle.Length;

    // A batch of bare records of one type says the line once, in text and json.
    [Fact]
    public void BatchOfOneType_SaysTheLineOnce()
    {
        using var w = new World();
        var o = CreateMessages(w, "HcEoBatch", 3);

        Assert.All(o.Created, c => Assert.Single(c.EmptySubrecords));
        Assert.Single(o.EmptySubrecordLines);
        Assert.Equal(1, Occurrences(WriteTools.RenderCreate(o, 20000), "MESG INAM at 0 bytes"));
        Assert.Equal(1, Occurrences(JsonWire.RenderCreateOutcome(o, 20000, readback: false, lane: "patch"), "MESG INAM at 0 bytes"));
    }

    // A max_chars that cuts the created rows keeps the line: the text's `!` line and the json's empty_subrecords.
    [Fact]
    public void RowCut_KeepsTheLine()
    {
        using var w = new World();
        var o = CreateMessages(w, "HcEoCut", 40);

        // A cap below the header cuts before the first row, so the line cannot ride on a listed row.
        var text = WriteTools.RenderCreate(o, 10);
        Assert.Contains("[truncated: 0 of 40 ", text);
        Assert.Contains("  ! Mutagen writes MESG INAM at 0 bytes", text);
        var json = System.Text.Json.JsonDocument.Parse(JsonWire.RenderCreateOutcome(o, 10, readback: false, lane: "patch")).RootElement;
        Assert.Contains("MESG INAM at 0 bytes", Assert.Single(json.GetProperty("empty_subrecords").EnumerateArray()).GetString());
    }

    // An author's value fills it: the bytes are the author's, and nothing is said.
    [Fact]
    public void AuthorValue_IsWrittenAndSilent()
    {
        using var w = new World();
        var o = w.Create("Message", "HcEoValue", null, Set("INAM", "00000000"));
        var rec = Created(o);

        Assert.Equal(new[] { 4 }, Lengths(o.OutputPath, k => k == rec.FormKey, "INAM"));
        Assert.Empty(rec.EmptySubrecords);
    }

    // Vanilla 0-byte markers (NPC_ DATA, RACE markers) stay 0 bytes on a created record and raise nothing (unchanged bytes).
    [Theory]
    [InlineData("Npc", "DATA")]
    [InlineData("Race", "MNAM")]
    public void VanillaMarker_StaysEmptyAndSilent(string type, string sig)
    {
        using var w = new World();
        var o = w.Create(type, "HcEoMarker" + type);
        var rec = Created(o);

        Assert.Contains(0, Lengths(o.OutputPath, k => k == rec.FormKey, sig));
        Assert.Empty(rec.EmptySubrecords);
    }

    // GLOB / GMST / interior cell creates are outside the class: silent (unchanged bytes; nothing here writes them).
    [Theory]
    [InlineData("GlobalFloat")]
    [InlineData("GameSettingFloat")]
    [InlineData("Cell")]
    public void OutsideTheClass_IsSilent(string type)
    {
        using var w = new World();
        Assert.Empty(Created(w.Create(type, "HcEoOut" + type)).EmptySubrecords);
    }

    // A nested create (a topic and its INFO) is outside the class: silent.
    [Fact]
    public void NestedCreate_IsSilent()
    {
        using var w = new World();
        var o = w.Svc.CreateRecordsBatch(new[]
        {
            new CreateOp { RecordType = "DialogTopic", Editorid = "HcEoTopic", Operations = new[] { Set("Subtype", "Custom") } },
            new CreateOp { RecordType = "DialogResponses", Editorid = "HcEoInfo", Parent = "HcEoTopic" },
        }, "HcEoNested", null);
        Assert.True(o.Success, o.Error);
        Assert.All(o.Created, c => Assert.Empty(c.EmptySubrecords));
    }

    // Forward carries the master's 4-byte INAM unchanged (unchanged behaviour; the forward lane has no create warning).
    [Fact]
    public void Forward_CarriesTheBytes()
    {
        using var w = new World();
        var o = w.Svc.ForwardRecords(new[] { FormIdToken.Of(w.MasterMessage) }, World.Master.FileName.String, "HcEoFwd", null);
        Assert.True(o.Success, o.Error);
        Assert.Equal(new[] { 4 }, Lengths(o.OutputPath, k => k == w.MasterMessage, "INAM"));
    }

    // Merge carries each donor's 4-byte INAM unchanged.
    [Fact]
    public void Merge_CarriesTheBytes()
    {
        using var w = new World();
        var o = w.Svc.MergePlugins(new[] { World.A.FileName.String, World.B.FileName.String }, "HcEoMerged");
        Assert.True(o.Success, o.Error);
        Assert.Equal(new[] { 4, 4 }, Lengths(o.OutputPath, _ => true, "INAM"));
    }

    // Compact carries the 4-byte INAM unchanged.
    [Fact]
    public void Compact_CarriesTheBytes()
    {
        using var w = new World();
        var o = w.Svc.CompactPlugin(World.A.FileName.String);
        Assert.True(o.Success, o.Error);
        Assert.Equal(new[] { 4 }, Lengths(o.OutputPath, _ => true, "INAM"));
    }

    // The probe reaches every record type (flat, arm, cell and nested alike) with no anomaly, and finds only the class.
    [Fact]
    public void Probe_ReachesEveryRecordTypeWithNoAnomaly()
    {
        var asm = typeof(Message).Assembly;
        var classes = HousecarlGenerator.CorpusGenerator.RecordClasses(asm)
            .Where(c => HousecarlGenerator.CorpusGenerator.GetterInterfaceFor(c) is not null).ToList();
        var anomalies = new List<string>();
        var found = HousecarlGenerator.EmptyOnCreateProbe.Run(classes, anomalies);
        Assert.Empty(anomalies);
        Assert.Equal(new[] { "CameraPath", "FootstepSet", "IdleAnimation", "Message", "Package" },
            found.Keys.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    // The generator's bare-construct probe lists exactly the class; a marker-only type lists nothing.
    [Fact]
    public void Probe_ListsExactlyTheClass()
    {
        var corpus = CorpusRulebook.LoadCorpus(TestCorpus.Path);
        var listed = corpus.Types.Values.Where(t => t.EmptyOnCreate is { Count: > 0 })
            .SelectMany(t => t.EmptyOnCreate!.Select(e => $"{t.Signature}.{e.Subrecord}")).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "CPTH.ANAM", "FSTS.DATA", "IDLE.ANAM", "MESG.INAM", "PACK.XNAM" }, listed);
        Assert.Null(corpus.Types["Npc"].EmptyOnCreate);
        Assert.Null(corpus.Types["Race"].EmptyOnCreate);
        // Every field whose fill makes it non-empty, per member.
        Assert.Equal(new[] { "INAM" }, corpus.Types["Message"].EmptyOnCreate![0].Fields);
        Assert.Equal(new[] { "RelatedIdles" }, corpus.Types["IdleAnimation"].EmptyOnCreate![0].Fields);
        Assert.Equal(new[] { "RelatedPaths" }, corpus.Types["CameraPath"].EmptyOnCreate![0].Fields);
        Assert.Equal(new[] { "XnamMarker" }, corpus.Types["Package"].EmptyOnCreate![0].Fields);
        Assert.Equal(new[] { "RunForwardAlternateFootsteps", "RunForwardFootsteps", "WalkForwardAlternateFootsteps",
            "WalkForwardAlternateFootsteps2", "WalkForwardFootsteps" }, corpus.Types["FootstepSet"].EmptyOnCreate![0].Fields);
    }
}

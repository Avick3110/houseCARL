using System.Reflection;
using System.Text;
using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;
using Xunit;
using static HousecarlMcpTests.WriteSurfaceReads;

namespace HousecarlMcpTests;

/// <summary>
/// Renders every write outcome the old write-surface-guard twin arm rendered, on both transports, and records which
/// shared sentences each lane emitted. Real outcomes off the service for the four write verbs; built outcomes handed to
/// the real renderers for the report blocks, the in-place and dry-run lanes, write_seq's states, copy, merge and compact;
/// place_asset's refusals off a throwaway instance of their own.
/// </summary>
public sealed class TwinRenderSweep : IDisposable
{
    public WriteSurfaceWorld World { get; } = new();
    public IReadOnlyList<(string Name, string Sentence)> Twins { get; }
    public IReadOnlyList<(string Name, string Sentence)> Outer { get; }
    public HashSet<string> SeenText { get; } = new(StringComparer.Ordinal);
    public HashSet<string> SeenJson { get; } = new(StringComparer.Ordinal);
    public HashSet<string> SeenOuter { get; } = new(StringComparer.Ordinal);

    /// <summary>label -> (true total, text render at a cap, json render at a cap, json total member, text total phrase).</summary>
    public Dictionary<string, (int Total, Func<int, string> Text, Func<int, string> Json, string JsonTotal, string TextTotal)> Budgets { get; } = new();

    public TwinRenderSweep()
    {
        Twins = TwinSentences();
        Outer = OuterSentences();
        var fx = World;

        var createOutcome = fx.Svc.CreateRecordsBatch(new[] { "W2TwinA", "W2TwinB", "W2TwinC" }
            .Select(e => new CreateOp { RecordType = "Keyword", Editorid = e }).ToList(), "W2Twin", null);
        Budgets["create"] = (createOutcome.Created.Count,
            cap => WriteTools.RenderCreate(createOutcome, cap),
            cap => JsonWire.RenderCreateOutcome(createOutcome, cap, false, "patch"),
            "total_created", $"created {createOutcome.Created.Count} records");
        Observe(WriteTools.RenderCreate(createOutcome), JsonWire.RenderCreateOutcome(createOutcome, 0, false, "patch"));
        Observe(WriteTools.RenderCreate(createOutcome, 60), JsonWire.RenderCreateOutcome(createOutcome, 60, false, "patch"));
        var createWithOps = fx.Svc.CreateRecordsBatch(
            new[] { new CreateOp { RecordType = "Keyword", Editorid = "W2TwinSet",
                                   Operations = new[] { new BulkOp { FieldPath = "EditorID", Value = "W2TwinSet" } } } },
            "W2TwinSetPatch", null);
        Observe(WriteTools.RenderCreate(createWithOps), JsonWire.RenderCreateOutcome(createWithOps, 0, false, "patch"));

        var fwdOutcome = fx.Svc.ForwardRecords(new[] { fx.SubjectFid, fx.MasterOnlyFid }, fx.MasterName, "W2TwinFwd", null);
        Budgets["forward"] = (fwdOutcome.Forwarded.Count,
            cap => WriteTools.RenderForward(fwdOutcome, cap),
            cap => JsonWire.RenderForwardOutcome(fwdOutcome, cap, false, "patch"),
            "total_forwarded", $"forwarded {fwdOutcome.Forwarded.Count} records");
        Observe(WriteTools.RenderForward(fwdOutcome), JsonWire.RenderForwardOutcome(fwdOutcome, 0, false, "patch"));

        var applyOutcome = fx.Svc.ApplyEdits(
            new[] { fx.SubjectFid, fx.MasterOnlyFid }.Select(f => new BulkOp { Formid = f, FieldPath = "EditorID", Value = "W2TwinEd" }).ToList(),
            "W2TwinApply", null);
        Budgets["apply"] = (applyOutcome.Ops.Count,
            cap => WriteTools.Render(applyOutcome, cap),
            cap => JsonWire.RenderPatchOutcome(applyOutcome, cap, false, "patch"),
            "total_ops", $"{applyOutcome.Ops.Count} edits");
        Observe(WriteTools.Render(applyOutcome, 60), JsonWire.RenderPatchOutcome(applyOutcome, 60, false, "patch"));
        // The one apply state a real write cannot be driven into: an edit whose record the written file does not contain.
        var absentOutcome = applyOutcome with
            { Ops = applyOutcome.Ops.Select(o => o with { RecordAbsentFromFile = true, VerifyAttempted = true }).ToList() };
        Observe(WriteTools.Render(absentOutcome), JsonWire.RenderPatchOutcome(absentOutcome, 0, false, "patch"));

        var rmOutcome = fx.Svc.RemoveRecords(new[] { fx.SubjectFid, fx.MasterOnlyFid }, "W2TwinFwd.esp");
        Budgets["remove"] = (rmOutcome.Removed.Count,
            cap => WriteTools.RenderRemoval(rmOutcome, cap),
            cap => JsonWire.RenderRemovalOutcome(rmOutcome, cap, "into"),
            "total_removed", $"removed {rmOutcome.Removed.Count} records");
        Observe(WriteTools.RenderRemoval(rmOutcome, 60), JsonWire.RenderRemovalOutcome(rmOutcome, 60, "into"));

        // remove's no-usable-patch refusal, and the un-owned half of the same resolver.
        Observe(RemoveTools.Remove(fx.Svc, new[] { fx.SubjectFid }, into: "W2TwinNoSuchPatch"),
                RemoveTools.Remove(fx.Svc, new[] { fx.SubjectFid }, into: "W2TwinNoSuchPatch", format: "json"));
        var foreignOps = Json($$"""[{"formid":"{{fx.SubjectFid}}","field_path":"Name","value":"x"}]""");
        Observe(ApplyTools.Apply(fx.Svc, ops: foreignOps, into: "W2Repl"),
                ApplyTools.Apply(fx.Svc, ops: foreignOps, into: "W2Repl", format: "json"));

        // The three post-write report blocks, uncut and cut.
        var lines = Enumerable.Range(0, 40).Select(i => new VoiceLine(
            default, "W2TwinTopic", i,
            $@"sound\voice\W2.esp\MaleNord\W2TwinTopic_{i:D4}.fuz", false, null, false,
            $@"sound\voice\W2.esp\MaleNord\W2TwinTopic_{i:D4}.lip", false, false)).ToList();
        var findings = Enumerable.Range(0, 40).Select(i => new ScriptBindingFinding(
            default, "W2TwinTopic", ScriptBindingStatus.ScriptNotCompiled,
            new[] { "W2TwinFrag" }, new[] { $@"scripts\W2TwinFrag{i:D2}.pex" }, false,
            "the bound script has no compiled .pex on disk")).ToList();
        var shells = Enumerable.Range(0, 30).Select(i => new CellShell(
            default, $"W2TwinCell{i:D2}", i % 2 == 0,
            new[] { "lighting template", "terrain / landscape", "water height", "navmesh" })).ToList();
        var reports = new WritePatchBuilder.CreateOutcome(
            true, null, @"C:\mods\W2Twin\W2Twin.esp", false,
            new[] { new WritePatchBuilder.CreatedRecord(default, "DialogResponses", "W2TwinL1", Array.Empty<WritePatchBuilder.OpResult>()) },
            Array.Empty<string>(), 512)
        {
            Stamp = new OrderStamp("deadbeefdeadbeef", Array.Empty<string>()),
            Voice = new VoiceReport(lines, Array.Empty<VoiceUndetermined>()),
            ScriptBinding = new ScriptBindingReport(findings),
            CellShell = new CellShellReport(shells),
        };
        Observe(WriteTools.RenderCreate(reports), JsonWire.RenderCreateOutcome(reports, 0, false, "patch"));
        Observe(WriteTools.RenderCreate(reports, maxChars: 900), JsonWire.RenderCreateOutcome(reports, 900, false, "patch"));

        // The in-place and dry-run lanes, on built outcomes.
        var inPlaceCreate = new WritePatchBuilder.CreateOutcome(
            true, null, @"C:\mods\W2TwinIP\W2TwinIP.esp", false,
            new[] { new WritePatchBuilder.CreatedRecord(default, "Keyword", "W2TwinIPKw", Array.Empty<WritePatchBuilder.OpResult>()) },
            Array.Empty<string>(), 512)
            { Stamp = new OrderStamp("deadbeefdeadbeef", Array.Empty<string>()), InPlace = true };
        Observe(WriteTools.RenderCreate(inPlaceCreate), JsonWire.RenderCreateOutcome(inPlaceCreate, 0, false, "in_place"));
        var inPlaceDryApply = new WritePatchBuilder.PatchOutcome(
            true, null, @"C:\mods\W2TwinIP\W2TwinIP.esp", false,
            Array.Empty<string>(), Array.Empty<WritePatchBuilder.OpResult>(), 512)
            { Stamp = new OrderStamp("deadbeefdeadbeef", Array.Empty<string>()), InPlace = true, DryRun = true };
        Observe(WriteTools.Render(inPlaceDryApply), JsonWire.RenderPatchOutcome(inPlaceDryApply, 0, false, "in_place"));

        // write_seq: every state with its own sentence, and a cut quest list.
        var quests = Enumerable.Range(0, 60)
            .Select(i => new SeqFile.SeqQuest(default, $"W2TwinQ{i:D2}", (uint)(0x800 + i))).ToList();
        SeqOutcome Seq(IReadOnlyList<SeqFile.SeqQuest> qs) => new(
            true, null, @"C:\mods\W2TwinSeq\SEQ\W2Twin.seq", "W2TwinSeq", qs, "W2Twin.esp", false)
            { ResolvedFrom = "direct path", PluginPath = @"C:\mods\W2TwinSeq\W2Twin.esp" };
        var seqStates = new[]
        {
            Seq(Array.Empty<SeqFile.SeqQuest>()),
            Seq(quests) with { Unchanged = true, TimestampRefreshed = true },
            Seq(quests) with { Replaced = true, ReplacedSameBytes = true },
            Seq(quests) with { Replaced = true, UserChoseOutput = true },
            Seq(quests) with { Replaced = true },
        };
        foreach (var st in seqStates) Observe(SeqTools.Render(st), JsonWire.RenderSeqOutcome(st, 0));
        Observe(SeqTools.Render(seqStates[2], maxChars: 400), JsonWire.RenderSeqOutcome(seqStates[2], 400));
        Budgets["write_seq"] = (quests.Count,
            cap => SeqTools.Render(seqStates[2], cap),
            cap => JsonWire.RenderSeqOutcome(seqStates[2], cap),
            "quest_count", $"{quests.Count} start-game-enabled quests");

        // One-transport renders: their sentences only have to reach a render.
        foreach (var render in PlaceSourceRefusalRenders(fx.Root)) Observe(render, "{}");
        foreach (var render in CopyOutcomeRenders()) Observe(render, "{}");
        foreach (var render in MergeCompactOutcomeRenders()) Observe(render, "{}");
    }

    void Observe(string text, string json)
    {
        var jsonText = JsonStrings(json);
        foreach (var (name, sentence) in Twins)
        {
            if (text.Contains(sentence, StringComparison.Ordinal)) SeenText.Add(name);
            if (jsonText.Contains(sentence, StringComparison.Ordinal)) SeenJson.Add(name);
        }
        foreach (var (name, sentence) in Outer)
            if (text.Contains(sentence, StringComparison.Ordinal) || jsonText.Contains(sentence, StringComparison.Ordinal))
                SeenOuter.Add(name);
    }

    /// <summary>Every string value in a json document, unescaped (the writer escapes non-ASCII).</summary>
    static string JsonStrings(string raw)
    {
        var sb = new StringBuilder();
        using var doc = JsonDocument.Parse(raw);
        Walk(doc.RootElement, sb);
        return sb.ToString();

        static void Walk(JsonElement e, StringBuilder sb)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) Walk(p.Value, sb); break;
                case JsonValueKind.Array: foreach (var v in e.EnumerateArray()) Walk(v, sb); break;
                case JsonValueKind.String: sb.Append(e.GetString()).Append('\n'); break;
            }
        }
    }

    public static IReadOnlyList<(string Name, string Sentence)> TwinSentences()
        => typeof(WriteSentences.Twins)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (f.Name, (string)f.GetRawConstantValue()!))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

    public static IReadOnlyList<(string Name, string Sentence)> OuterSentences()
        => typeof(WriteSentences)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.GetCustomAttribute<MustStateAttribute>() is not null)
            .Select(f => (f.Name, (string)f.GetRawConstantValue()!))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>The real place service driven to each of its source-selection refusals, on its own throwaway instance.</summary>
    static List<string> PlaceSourceRefusalRenders(string root)
    {
        const string rel = @"meshes\hcw2\twin.nif";
        var inst = Path.Combine(root, "place-sentences");
        var mods = Path.Combine(inst, "mods");
        var prof = Path.Combine(inst, "profiles", "Default");
        foreach (var d in new[] { mods, prof, Path.Combine(inst, "game", "Data") }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(inst, "game").Replace(@"\", @"\\") + ")\r\n");

        var providers = new[] { "W2AssetA", "W2AssetB" };
        foreach (var m in providers)
        {
            var dir = Path.Combine(mods, m, "meshes", "hcw2");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "twin.nif"), new byte[] { 1, 2, 3 });
        }
        File.WriteAllText(Path.Combine(mods, providers[0], "Dummy.esp"), "x");
        // A bound archive that will not open, so the scan is read-incomplete and the caveat sentence reaches a render.
        File.WriteAllBytes(Path.Combine(mods, providers[0], "Dummy.bsa"), new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        Directory.CreateDirectory(Path.Combine(mods, "W2Offline"));
        Directory.CreateDirectory(Path.Combine(mods, "W2Broken"));
        File.WriteAllBytes(Path.Combine(mods, "W2Broken", "Broken.bsa"), new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\nDummy.esp\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*Dummy.esp\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"),
            "# header\r\n" + string.Join("\r\n", providers.Select(p => "+" + p)) + "\r\n");
        File.WriteAllText(Path.Combine(prof, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");

        using var svc = LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(root, "place-sentences.user.json")));
        string Render(PlaceRequest req) => PlaceWire.Render(svc.PlaceAssets(new[] { req }, null, null), 80_000);
        var bothSlots = PlaceTools.Place(svc, new[]
        {
            new PlaceTarget { Formid = "000800:Dummy.esp", Source = Path.Combine(root, "w2-ondisk.nif") },
        });
        return new List<string>
        {
            bothSlots,
            Render(new PlaceRequest(rel, null, null)),
            Render(new PlaceRequest(rel, rel, "W2NoSuchMod")),
            Render(new PlaceRequest(rel, Path.Combine(root, "w2-ondisk.nif"), providers[0])),
            Render(new PlaceRequest(@"meshes\hcw2\nothing-provides-this.nif", null, null)),
            Render(new PlaceRequest(rel, @"meshes\hcw2\absent-everywhere.nif", "Dummy.bsa")),
            Render(new PlaceRequest(rel, rel, @"..\nope")),
            Render(new PlaceRequest(rel, rel, "W2Offline")),
            Render(new PlaceRequest(rel, rel, "W2Broken")),
        };
    }

    /// <summary>One successful merge and one successful compact outcome, for the runtime-config reminder both carry.</summary>
    static List<string> MergeCompactOutcomeRenders()
    {
        var merge = new WritePatchBuilder.MergeOutcome(
            true, null, Path.Combine(Path.GetTempPath(), "MergeFolder", "Merged.esp"), "Merged.esp",
            new[] { "DonorA.esp" }, new[] { "Skyrim.esm" }, 1, 1,
            Array.Empty<RemapEngine.MergeDonorRemap>(), Array.Empty<RemapEngine.MergeConflict>(),
            Array.Empty<string>(), Array.Empty<string>(), 3, 0, Array.Empty<string>(), 1024);
        var compact = new WritePatchBuilder.CompactOutcome(
            true, null, false, Path.Combine(Path.GetTempPath(), "CompactFolder", "Compacted.esp"), "Compacted.esp", false, true,
            new[] { "Skyrim.esm" }, 1, 1, 1024,
            Array.Empty<string>(), Array.Empty<WritePatchBuilder.RepointReport>(), 3, 0, Array.Empty<string>());
        return new List<string> { WriteTools.RenderMerge(merge), WriteTools.RenderCompact(compact) };
    }

    /// <summary>Copy outcomes covering the sentences no end-to-end fixture reaches, handed to the real render.</summary>
    static List<string> CopyOutcomeRenders()
    {
        var src = new ModKey("CopySrc", ModType.Plugin);
        var patch = new ModKey("CopyPatch", ModType.Plugin);
        var outPath = Path.Combine(Path.GetTempPath(), "CopyPatchFolder", "CopyPatch.esp");
        var copied = new List<CopiedRecord>
        {
            new(new FormKey(src, 0x800), new FormKey(patch, 0x800), "HeadPart", "SrcHair", 0, "CopySrc.esp", "Npc.HeadParts"),
        };
        var stripped = new List<StripEntry> { new("Factions[0]", new FormKey(src, 0x802).ToString()) };
        var sources = new[]
        {
            new SourceArmRef("CopySrc.esp", SourceArmKind.File, new SourceLayer(SourceLayerKind.ModFolder, "TheDonorMod")),
        };
        var manySources = new[]
        {
            new SourceArmRef("Override.esp", SourceArmKind.File, new SourceLayer(SourceLayerKind.ModFolder, "TheOverhaulMod")),
            new SourceArmRef("winner", SourceArmKind.ActiveOrder, null),
            new SourceArmRef("Active.esp", SourceArmKind.ActiveOrder, new SourceLayer(SourceLayerKind.ModFolder, "AnEnabledMod")),
            new SourceArmRef("Collide.esp", SourceArmKind.File, new SourceLayer(SourceLayerKind.ModFolder, "Data")),
            new SourceArmRef("Dropped.esp", SourceArmKind.File, new SourceLayer(SourceLayerKind.Overwrite, "overwrite")),
            new SourceArmRef("Unticked.esp", SourceArmKind.File, new SourceLayer(SourceLayerKind.GameData, "Data")),
            new SourceArmRef(Path.Combine(Path.GetTempPath(), "backup", "CopySrc.esp"), SourceArmKind.File, null),
        };

        ClosureCopyOutcome Make(bool mastered, string? warning, IReadOnlyList<StripEntry> strips,
                               IReadOnlyList<StripEntry>? attach = null, IReadOnlyList<WalkBoundary>? kept = null,
                               IReadOnlyList<string>? assets = null, IReadOnlyList<SourceArmRef>? srcs = null,
                               bool nothingBound = false) => new(
            true, null, null, null, strips.Count > 0 ? "clone" : "attach",
            new FormKey(src, 0x803), new FormKey(patch, 0x900), outPath, false,
            copied, kept ?? Array.Empty<WalkBoundary>(), Array.Empty<WalkCycle>(),
            attach ?? Array.Empty<StripEntry>(), strips, srcs ?? sources,
            (srcs ?? sources)[0], assets ?? Array.Empty<string>(),
            new[] { "Skyrim.esm" }, mastered, nothingBound, 1234, warning);

        var keptBoth = new List<WalkBoundary>
        {
            new(new FormKey(new ModKey("Vanilla", ModType.Master), 0x811), "Npc.HeadParts", "outside", Excluded: false),
            new(new FormKey(src, 0x812), "Npc.WornArmor", "excluded (Race)", Excluded: true),
        };

        return new List<string>
        {
            CopyTools.Render(Make(false, null, Array.Empty<StripEntry>())),
            CopyTools.Render(Make(true, null, Array.Empty<StripEntry>())),
            CopyTools.Render(Make(false, "read-back blew up", Array.Empty<StripEntry>())),
            CopyTools.Render(Make(false, null, stripped)),
            CopyTools.Render(Make(false, null, Array.Empty<StripEntry>(),
                attach: new List<StripEntry> { new("HeadParts", "2 link(s)"), new("WornArmor", "cleared", Cleared: true) },
                kept: keptBoth,
                assets: new[] { @"meshes\actors\character\facegendata\facegeom\CopySrc.esp\00000800.nif" },
                srcs: manySources)),
            CopyTools.Render(Make(false, null, Array.Empty<StripEntry>(), srcs: new[]
            {
                new SourceArmRef("Disabled.esp", SourceArmKind.File,
                    new SourceLayer(SourceLayerKind.ModFolder, "ASwitchedOffMod", ModFolderStanding.SwitchedOff)),
                new SourceArmRef("AlsoOff.esp", SourceArmKind.File,
                    new SourceLayer(SourceLayerKind.ModFolder, "AnotherSwitchedOffMod", ModFolderStanding.SwitchedOff)),
                new SourceArmRef("Fresh.esp", SourceArmKind.File,
                    new SourceLayer(SourceLayerKind.ModFolder, "AnUnregisteredMod", ModFolderStanding.Unregistered)),
            })),
            CopyTools.Render(ClosureCopyOutcome.Fail(
                walk: new WalkRefusal(WalkRefusalKind.SourceMiss, new FormKey(src, 0x820), "Npc.HeadParts",
                    Array.Empty<FormKey>(), "", Miss: null),
                sources: manySources)),
            CopyTools.Render(ClosureCopyOutcome.Fail(
                walk: new WalkRefusal(WalkRefusalKind.SourceFault, new FormKey(src, 0x821), "Npc.HeadParts",
                    Array.Empty<FormKey>(), "the record could not be parsed",
                    Fault: new SourceFault(new FormKey(src, 0x821), "Npc.HeadParts", 0,
                        new SourceArm("CopySrc.esp", SourceArmKind.File, "on disk", _ => null,
                            new SourceLayer(SourceLayerKind.ModFolder, "TheDonorMod")), "the record could not be parsed")),
                sources: manySources)),
            CopyTools.Render(ClosureCopyOutcome.Fail(
                walk: new WalkRefusal(WalkRefusalKind.UnsupportedSeedShape, new FormKey(src, 0x822), "",
                    Array.Empty<FormKey>(), "'Factions' on Npc is a list of link-BEARING entries, not a list of record links"),
                sources: sources)),
            CopyTools.Render(ClosureCopyOutcome.Fail(
                copy: new CopyRefusal(CopyRefusalKind.DonorLeak, "a link into the source universe survived on the target",
                    ClosureCopy.ExclusionLeakMarker, new FormKey(src, 0x823)),
                sources: sources)),
            CopyTools.Render(ClosureCopyOutcome.Fail(
                copy: new CopyRefusal(CopyRefusalKind.StopOffOrder, "CopySrc.esp", Key: new FormKey(src, 0x824)),
                sources: sources)),
            CopyTools.Render(ClosureCopyOutcome.Fail(
                copy: new CopyRefusal(CopyRefusalKind.UnsupportedTargetShape, "PlacedNpc", Key: new FormKey(src, 0x825)),
                sources: sources)),
            CopyTools.Render(Make(false, null, Array.Empty<StripEntry>(), nothingBound: true)),
            CopyTools.Render(Make(false, null,
                new List<StripEntry> { new("VirtualMachineAdapter", new FormKey(src, 0x826).ToString(), WholeProperty: true) })),
            CopyTools.Render(ClosureCopyOutcome.Fail(
                copy: new CopyRefusal(CopyRefusalKind.UnwritableTarget,
                    "'WornArmor' is a record link on the source but the target's is not writable", "WornArmor"),
                sources: sources)),
            CopyTools.Render(ClosureCopyOutcome.Fail(
                copy: new CopyRefusal(CopyRefusalKind.PatchOffOrderLink, "Ghost.esp",
                    "Npc 'OlderClone' (000801:CopyPatch.esp)", new FormKey(new ModKey("Ghost", ModType.Plugin), 0x800)),
                sources: sources)),
            CopyTools.Render(ClosureCopyOutcome.Fail(
                copy: new CopyRefusal(CopyRefusalKind.CopiedOffOrderLink, "Ghost.esp",
                    "Npc 'WideNpc' (000804:CopySrc.esp)", new FormKey(new ModKey("Ghost", ModType.Plugin), 0x800)),
                sources: sources)),
            CopyTools.Render(ClosureCopyOutcome.Fail(
                walk: new WalkRefusal(WalkRefusalKind.NoSeeds, new FormKey(src, 0x827), "", Array.Empty<FormKey>(), ""),
                sources: sources)),
        };
    }

    public void Dispose() => World.Dispose();
}

/// <summary>One write outcome, both transports: every shared sentence reaches both lanes, a cap that cuts one lane cuts
/// the other, and every WriteSentences const declares what it must say. Migrated from write-surface-guard's twin arm.</summary>
[Trait("tier", "integration")]
public sealed class WriteSurfaceTwinParityTests : IClassFixture<TwinRenderSweep>
{
    readonly TwinRenderSweep _s;
    public WriteSurfaceTwinParityTests(TwinRenderSweep s) => _s = s;

    public static IEnumerable<object[]> Verbs() =>
        new[] { "create", "forward", "apply", "remove", "write_seq" }.Select(v => new object[] { v });

    static bool? RootTruncated(string raw)
    {
        using var d = JsonDocument.Parse(raw);
        return d.RootElement.TryGetProperty("truncated", out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? e.GetBoolean() : null;
    }

    // probe: the twin inventory is non-empty (a reflection miss would make every check below vacuous)
    [Fact]
    public void TwinInventoryIsNonEmpty() => Assert.NotEmpty(_s.Twins);

    // probe: every Twins member is a shape this arm can enumerate (an unreadable one is an unchecked twin reported as covered)
    [Fact]
    public void EveryTwinsMemberIsAConstString()
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var t = typeof(WriteSentences.Twins);
        var bad = t.GetFields(All).Where(f => !(f.IsLiteral && f.FieldType == typeof(string))).Select(f => "field " + f.Name)
            .Concat(t.GetProperties(All).Select(p => "property " + p.Name))
            .Concat(t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Select(n => "nested type " + n.Name))
            .Concat(t.GetMethods(All).Select(m => "method " + m.Name))
            .Concat(t.GetEvents(All).Select(e => "event " + e.Name));
        Assert.Empty(bad);
    }

    // probe: every Twins sentence still states the claims it declares (a construction pin cannot see this — it reads the same constant the render does)
    [Fact]
    public void EverySentenceStatesItsDeclaredClaims()
    {
        var bad = new List<string>();
        foreach (var owner in new[] { typeof(WriteSentences.Twins), typeof(WriteSentences) })
        foreach (var f in owner.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
                     .Where(f => f.IsLiteral && f.FieldType == typeof(string)))
        {
            var sentence = (string)f.GetRawConstantValue()!;
            var attr = f.GetCustomAttribute<MustStateAttribute>();
            var optOut = f.GetCustomAttribute<NoClaimsAttribute>();
            if (attr is not null && optOut is not null) { bad.Add($"{f.Name}: both [MustState] and [NoClaims]"); continue; }
            if (optOut is not null) { if (optOut.Reason.Trim().Length == 0) bad.Add($"{f.Name}: [NoClaims] with no reason"); continue; }
            if (attr is null || attr.Phrases.Length == 0) { bad.Add($"{f.Name}: neither [MustState] phrases nor [NoClaims]"); continue; }
            if (sentence.Length == 0) { bad.Add($"{f.Name}: empty sentence"); continue; }
            foreach (var phrase in attr.Phrases)
                if (phrase.Length == 0) bad.Add($"{f.Name}: empty phrase");
                else if (!sentence.Contains(phrase, StringComparison.Ordinal)) bad.Add($"{f.Name}: no longer states \"{phrase}\"");
        }
        Assert.Empty(bad);
    }

    // probe: {label}: a cap that truncates one transport truncates the other
    [Theory]
    [MemberData(nameof(Verbs))]
    public void ACapCutsBothTransports(string verb)
    {
        var b = _s.Budgets[verb];
        var text = b.Text(60);
        var json = b.Json(60);
        Assert.True(b.Total > 0);
        Assert.Contains("... [truncated:", text);
        Assert.True(RootTruncated(json), json);
    }

    // probe: {label}: uncapped, NEITHER transport reports a cut
    [Theory]
    [MemberData(nameof(Verbs))]
    public void UncappedNeitherTransportCuts(string verb)
    {
        var b = _s.Budgets[verb];
        Assert.DoesNotContain("[truncated:", b.Text(0));
        Assert.NotEqual(true, RootTruncated(b.Json(0)));
    }

    // probe: {label}: both transports state the SAME total, and state it even when cut
    [Theory]
    [MemberData(nameof(Verbs))]
    public void BothTransportsStateTheSameTotal(string verb)
    {
        var b = _s.Budgets[verb];
        using var d = JsonDocument.Parse(b.Json(60));
        Assert.Equal(b.Total, d.RootElement.GetProperty(b.JsonTotal).GetInt32());
        Assert.Contains(b.TextTotal, b.Text(60));
    }

    // probe: every WriteSentences.Twins member is rendered by the TEXT lane
    [Fact]
    public void EveryTwinReachesTheTextLane() =>
        Assert.Empty(_s.Twins.Select(t => t.Name).Where(n => !_s.SeenText.Contains(n)));

    // probe: every WriteSentences.Twins member is rendered by the JSON lane
    [Fact]
    public void EveryTwinReachesTheJsonLane() =>
        Assert.Empty(_s.Twins.Select(t => t.Name).Where(n => !_s.SeenJson.Contains(n)));

    // probe: every [MustState] sentence on WriteSentences itself reaches a render (the wiring half a content pin cannot provide)
    [Fact]
    public void EveryOuterSentenceReachesARender() =>
        Assert.Empty(_s.Outer.Select(t => t.Name).Where(n => !_s.SeenOuter.Contains(n)));
}

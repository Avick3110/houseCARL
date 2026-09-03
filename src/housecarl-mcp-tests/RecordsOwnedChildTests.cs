using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The synthetic MO2 world for the owned-child content annotation (#342): a BASE master that DECLARES child
/// records, a MID plugin that touches the same cell declaring nothing, and a TOP winner shaped like an
/// Occlusion.esp override — it touches the parent and carries no children at all.
///
/// <para>Its own world rather than the shared one: every arm here needs a parent record whose winner carries an
/// EMPTY child collection that a lower plugin fills, and no shared-world record has that shape.</para>
/// </summary>
public sealed class OwnedChildWorld : IDisposable
{
    public string Root { get; }
    public LoadOrderService Svc { get; }

    public string BaseName { get; }
    public string MidName { get; }
    public string TopName { get; }

    /// <summary>The false-empty cell: winner touches it carrying nothing, base declares Temporary/Persistent/Landscape.</summary>
    public FormKey CellA { get; }
    /// <summary>DISJOINT — base declares 1 reference, the winner declares 4 OTHER ones.</summary>
    public FormKey CellB { get; }
    /// <summary>Touched by exactly one plugin.</summary>
    public FormKey CellC { get; }
    /// <summary>EQUAL — one reference each side.</summary>
    public FormKey CellD { get; }
    /// <summary>SELF — only the winner declares.</summary>
    public FormKey CellE { get; }
    /// <summary>TWO LOWER DECLARERS — base AND mid declare Temporary and Persistent; the winner declares nothing,
    /// and nothing anywhere declares Landscape or NavigationMeshes. The precise tier's own fixture (#485): the
    /// positive names two plugins, and the two untouched fields are the negative it must state rather than omit.</summary>
    public FormKey CellF { get; }
    public FormKey Topic { get; }
    /// <summary>A 3-toucher record with no child-bearing field at all.</summary>
    public FormKey Weapon { get; }
    /// <summary>REACH — the base holds one block with 3 real cells; the winner holds 2 empty blocks.</summary>
    public FormKey Worldspace { get; }

    public static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

    public OwnedChildWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-owned-child-tests-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profiles = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        foreach (var d in new[] { profiles, mods, Path.Combine(Root, "game", "Data") }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        var baseKey = new ModKey("HcOcBase", ModType.Master);
        var midKey = new ModKey("HcOcMid", ModType.Plugin);
        var topKey = new ModKey("HcOcTop", ModType.Plugin);
        BaseName = baseKey.FileName.String; MidName = midKey.FileName.String; TopName = topKey.FileName.String;

        CellA = new FormKey(baseKey, 0xC01); CellB = new FormKey(baseKey, 0xC02); CellC = new FormKey(baseKey, 0xC03);
        CellD = new FormKey(baseKey, 0xC04); CellE = new FormKey(baseKey, 0xC05); CellF = new FormKey(baseKey, 0xC06);
        Topic = new FormKey(baseKey, 0xD01); Weapon = new FormKey(baseKey, 0xE01); Worldspace = new FormKey(baseKey, 0xF01);

        var baseDir = Path.Combine(mods, "BaseMod"); Directory.CreateDirectory(baseDir);
        var basePath = Path.Combine(baseDir, BaseName);
        {
            var m = new SkyrimMod(baseKey, SkyrimRelease.SkyrimSE);

            var a = new Cell(CellA, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellA", Flags = Cell.Flag.IsInteriorCell };
            for (int i = 0; i < 3; i++)
                a.Temporary.Add(new PlacedObject(new FormKey(baseKey, (uint)(0xC10 + i)), SkyrimRelease.SkyrimSE) { EditorID = $"HcOcTemp{i}" });
            a.Persistent.Add(new PlacedObject(new FormKey(baseKey, 0xC1A), SkyrimRelease.SkyrimSE) { EditorID = "HcOcPers0" });
            a.Landscape = new Landscape(new FormKey(baseKey, 0xC1B), SkyrimRelease.SkyrimSE) { EditorID = "HcOcLand" };
            FileInterior(m, a);

            var b = new Cell(CellB, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellB", Flags = Cell.Flag.IsInteriorCell };
            b.Temporary.Add(new PlacedObject(new FormKey(baseKey, 0xC20), SkyrimRelease.SkyrimSE) { EditorID = "HcOcBTemp0" });
            FileInterior(m, b);

            var c = new Cell(CellC, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellC", Flags = Cell.Flag.IsInteriorCell };
            c.Temporary.Add(new PlacedObject(new FormKey(baseKey, 0xC30), SkyrimRelease.SkyrimSE) { EditorID = "HcOcCTemp0" });
            FileInterior(m, c);

            var d = new Cell(CellD, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellD", Flags = Cell.Flag.IsInteriorCell };
            d.Temporary.Add(new PlacedObject(new FormKey(baseKey, 0xC40), SkyrimRelease.SkyrimSE) { EditorID = "HcOcDTemp0" });
            FileInterior(m, d);

            FileInterior(m, new Cell(CellE, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellE", Flags = Cell.Flag.IsInteriorCell });

            var f = new Cell(CellF, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellF", Flags = Cell.Flag.IsInteriorCell };
            for (int i = 0; i < 2; i++)
                f.Temporary.Add(new PlacedObject(new FormKey(baseKey, (uint)(0xC60 + i)), SkyrimRelease.SkyrimSE) { EditorID = $"HcOcFTemp{i}" });
            f.Persistent.Add(new PlacedObject(new FormKey(baseKey, 0xC6A), SkyrimRelease.SkyrimSE) { EditorID = "HcOcFPers0" });
            FileInterior(m, f);

            var t = new DialogTopic(Topic, SkyrimRelease.SkyrimSE) { EditorID = "HcOcTopic" };
            for (int i = 0; i < 2; i++)
            {
                var info = new DialogResponses(new FormKey(baseKey, (uint)(0xD10 + i)), SkyrimRelease.SkyrimSE);
                info.Responses.Add(new DialogResponse { Text = $"base line {i}" });
                t.Responses.Add(info);
            }
            m.DialogTopics.Add(t);

            m.Weapons.Add(new Weapon(Weapon, SkyrimRelease.SkyrimSE)
                { EditorID = "HcOcWeap", BasicStats = new WeaponBasicStats { Damage = 5 } });

            var ws = new Worldspace(Worldspace, SkyrimRelease.SkyrimSE) { EditorID = "HcOcWrld" };
            var blk = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
            var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
            for (int i = 0; i < 3; i++)
                sub.Items.Add(new Cell(new FormKey(baseKey, (uint)(0xF10 + i)), SkyrimRelease.SkyrimSE) { EditorID = $"HcOcWsCell{i}" });
            blk.Items.Add(sub); ws.SubCells.Add(blk);
            m.Worldspaces.Add(ws);

            m.BeginWrite.ToPath(basePath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }

        var midDir = Path.Combine(mods, "MidMod"); Directory.CreateDirectory(midDir);
        {
            using var baseOv = SkyrimMod.CreateFromBinaryOverlay(basePath, SkyrimRelease.SkyrimSE);
            var m = new SkyrimMod(midKey, SkyrimRelease.SkyrimSE);
            FileInterior(m, new Cell(CellA, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellA", Flags = Cell.Flag.IsInteriorCell });

            var f = new Cell(CellF, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellF", Flags = Cell.Flag.IsInteriorCell };
            f.Temporary.Add(new PlacedObject(new FormKey(midKey, 0xA60), SkyrimRelease.SkyrimSE) { EditorID = "HcOcMidFTemp0" });
            f.Persistent.Add(new PlacedObject(new FormKey(midKey, 0xA6A), SkyrimRelease.SkyrimSE) { EditorID = "HcOcMidFPers0" });
            FileInterior(m, f);

            m.Weapons.GetOrAddAsOverride(baseOv.Weapons.First(w => w.FormKey == Weapon)).BasicStats!.Damage = 7;
            m.BeginWrite.ToPath(Path.Combine(midDir, MidName)).WithLoadOrder(new ISkyrimModGetter[] { baseOv }).Write();
        }

        var topDir = Path.Combine(mods, "TopMod"); Directory.CreateDirectory(topDir);
        {
            using var baseOv = SkyrimMod.CreateFromBinaryOverlay(basePath, SkyrimRelease.SkyrimSE);
            var m = new SkyrimMod(topKey, SkyrimRelease.SkyrimSE);
            FileInterior(m, new Cell(CellA, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellA", Flags = Cell.Flag.IsInteriorCell });

            var b = new Cell(CellB, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellB", Flags = Cell.Flag.IsInteriorCell };
            for (int i = 0; i < 4; i++)
                b.Temporary.Add(new PlacedObject(new FormKey(topKey, (uint)(0xB10 + i)), SkyrimRelease.SkyrimSE) { EditorID = $"HcOcTopTemp{i}" });
            FileInterior(m, b);

            var d = new Cell(CellD, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellD", Flags = Cell.Flag.IsInteriorCell };
            d.Temporary.Add(new PlacedObject(new FormKey(topKey, 0xB40), SkyrimRelease.SkyrimSE) { EditorID = "HcOcTopDTemp0" });
            FileInterior(m, d);

            var e = new Cell(CellE, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellE", Flags = Cell.Flag.IsInteriorCell };
            e.Temporary.Add(new PlacedObject(new FormKey(topKey, 0xB50), SkyrimRelease.SkyrimSE) { EditorID = "HcOcTopETemp0" });
            FileInterior(m, e);

            // CellF's winner: it touches the cell and declares NOTHING — the Occlusion.esp shape, with two lower
            // plugins declaring below it.
            FileInterior(m, new Cell(CellF, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellF", Flags = Cell.Flag.IsInteriorCell });

            var t = new DialogTopic(Topic, SkyrimRelease.SkyrimSE) { EditorID = "HcOcTopic" };
            var only = new DialogResponses(new FormKey(baseKey, 0xD10), SkyrimRelease.SkyrimSE);
            only.Responses.Add(new DialogResponse { Text = "patched line 0" });
            t.Responses.Add(only);
            m.DialogTopics.Add(t);

            var ws = new Worldspace(Worldspace, SkyrimRelease.SkyrimSE) { EditorID = "HcOcWrld" };
            for (int bx = 0; bx < 2; bx++)
            {
                var eb = new WorldspaceBlock { BlockNumberX = (short)bx, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
                eb.Items.Add(new WorldspaceSubBlock { BlockNumberX = (short)bx, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock });
                ws.SubCells.Add(eb);
            }
            m.Worldspaces.Add(ws);

            m.Weapons.GetOrAddAsOverride(baseOv.Weapons.First(w => w.FormKey == Weapon)).BasicStats!.Damage = 9;
            m.BeginWrite.ToPath(Path.Combine(topDir, TopName)).WithLoadOrder(new ISkyrimModGetter[] { baseOv }).Write();
        }

        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + BaseName + "\r\n" + MidName + "\r\n" + TopName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + BaseName + "\r\n*" + MidName + "\r\n*" + TopName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+TopMod\r\n+MidMod\r\n+BaseMod\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
        Svc.Stats();
    }

    public string Scratch(params string[] parts)
    {
        var p = Path.Combine(new[] { Root, "scratch" }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        return p;
    }

    /// <summary>Mutagen writes interior cells through the group tree, not a flat list.</summary>
    static void FileInterior(SkyrimMod mod, Cell cell)
    {
        uint id = cell.FormKey.ID;
        int blockN = (int)(id % 10), subN = (int)((id / 10) % 10);
        var records = mod.Cells.Records;
        var block = records.FirstOrDefault(b => b.BlockNumber == blockN);
        if (block is null) { block = new CellBlock { BlockNumber = blockN, GroupType = GroupTypeEnum.InteriorCellBlock }; records.Add(block); }
        var sub = block.SubBlocks.FirstOrDefault(s => s.BlockNumber == subN);
        if (sub is null) { sub = new CellSubBlock { BlockNumber = subN, GroupType = GroupTypeEnum.InteriorCellSubBlock }; block.SubBlocks.Add(sub); }
        sub.Cells.Add(cell);
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>One world per class — every arm below is a read.</summary>
public sealed class OwnedChildFixture : IDisposable
{
    public OwnedChildWorld W { get; } = new();
    public void Dispose() => W.Dispose();
}

/// <summary>
/// #342 — the owned-child content annotation, driven through <c>housecarl_records</c>: a parent's child records
/// (a cell's placed references, a topic's INFO lines, a worldspace's cells) are declared per plugin and assembled
/// by the game from every plugin that declares them, so a winner that touches the parent for an unrelated reason
/// reports an empty collection the game fills.
///
/// <para>The arms come from the tool-layer half of <c>OwnedChildContentProbe</c> — the ones whose subject was a
/// value returned by <c>read_record</c> / <c>batch_record_detail</c>. Both tiers are here now: the CHEAP one on
/// the default read, and — restored by #485 after the cut deleted it with the <c>conflict_tree=true</c> lever
/// that was its only caller — the PRECISE one on <c>project={"form":"tree"}</c>, the 2.0 form that fetches every
/// provider body anyway. The probe's engine-level arms (<c>OwnedChildContent.DeclaresChild</c> / <c>ShapeOf</c> /
/// <c>Fields</c>, and <c>ReadSentences.DeclarersNote</c>'s own composition) stay where they are.</para>
/// </summary>
[Trait("tier", "integration")]
public sealed class RecordsOwnedChildTests : IClassFixture<OwnedChildFixture>
{
    readonly OwnedChildWorld _w;
    public RecordsOwnedChildTests(OwnedChildFixture f) => _w = f.W;

    LoadOrderService Svc => _w.Svc;

    static RecordsTools.RecordsProject Everything => new() { form = "everything" };

    string Read(FormKey fk, RecordsTools.RecordsProject? project = null, string? format = null, int maxChars = 0,
                string? source = null, string? toFile = null) =>
        RecordsTools.Records(Svc, formids: new[] { OwnedChildWorld.Fid(fk) },
                             source: source is null ? null : JsonDocument.Parse("\"" + source + "\"").RootElement.Clone(),
                             project: project ?? Everything, format: format, max_chars: maxChars, to_file: toFile);

    string ReadBoth(FormKey a, FormKey b, string? format = null, int maxChars = 0) =>
        RecordsTools.Records(Svc, formids: new[] { OwnedChildWorld.Fid(a), OwnedChildWorld.Fid(b) },
                             project: Everything, format: format, max_chars: maxChars);

    // ---- the fixture's own premise ---------------------------------------------------------------

    [Fact]
    public void TheWinnersOwnChildCollectionReadsEmptyOnACellALowerPluginFills()
    {
        var r = Read(_w.CellA);
        Assert.Contains("winner=" + _w.TopName, r);
        Assert.StartsWith("Temporary = [list: 0 item(s)]", FieldLine(r, "Temporary"));
    }

    [Fact]
    public void TheDeclaringPluginsOwnBodyCarriesTheThreeReferencesTheWinnerDoesNotShow() =>
        Assert.StartsWith("Temporary = [list: 3 item(s)]", FieldLine(Read(_w.CellA, source: _w.BaseName), "Temporary"));

    // ---- the cheap tier: what every read states from the index alone ------------------------------

    [Fact]
    public void AChildBearingFieldSaysOtherPluginsTouchThisRecordAndTheirDeclarationsWereNotRead() =>
        Assert.Contains("2 " + ReadSentences.NotRead, FieldLine(Read(_w.CellA), "Temporary"));

    [Fact]
    public void TheCheapTierClaimsNothingAboutWhoDeclares_NoDeclarerNamingOnTheDefaultLane()
    {
        var r = Read(_w.CellA);
        Assert.DoesNotContain(ReadSentences.DeclaredBy, r);
        Assert.DoesNotContain(ReadSentences.CarriedBy, r);
    }

    /// <summary>The grid is the record type's OWN child-bearing set, so a Mutagen bump that grows it grows this
    /// theory — and it covers fields nobody in this world declares, which is the claim: "not read" is true of all.</summary>
    public static TheoryData<string> CellChildBearingFields()
    {
        var data = new TheoryData<string>();
        foreach (var f in OwnedChildContent.Fields(new Cell(FormKey.Null, SkyrimRelease.SkyrimSE)).Keys
                                           .OrderBy(x => x, StringComparer.Ordinal))
            data.Add(f);
        return data;
    }

    [Theory]
    [MemberData(nameof(CellChildBearingFields))]
    public void EveryChildBearingFieldOfTheTypeCarriesTheAnnotation_IncludingOnesNobodyDeclares(string field) =>
        Assert.Contains(ReadSentences.NotRead, FieldLine(Read(_w.CellA), field));

    [Fact]
    public void TheNotReadClauseIsStatedOnceOverTheWholeResponse_NotOncePerAnnotatedField() =>
        Assert.Equal(1, Occurrences(Read(_w.CellA), ClauseHead(ReadSentences.NotReadFraming)));

    [Fact]
    public void TheClauseNamesTheAnnotatedFieldsAndPointsAtNoPositionAtAll()
    {
        var clause = ClauseLine(Read(_w.CellA), ReadSentences.NotReadFraming);
        Assert.Contains("Temporary", clause);
        Assert.Contains("Persistent", clause);
        Assert.DoesNotContain("above", clause, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("below", clause, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheClausesFieldNamesAreDerived_EveryFieldItNamesIsOneTheResponseAnnotated()
    {
        var r = Read(_w.CellA);
        var named = NamedFields(ClauseLine(r, ReadSentences.NotReadFraming), ReadSentences.NotReadFraming);
        Assert.NotEmpty(named);
        foreach (var f in named) Assert.Contains(ReadSentences.NotRead, FieldLine(r, f));
    }

    [Fact]
    public void ARecordOnlyOnePluginTouchesIsNotAnnotatedAtAll() =>
        Assert.DoesNotContain(ReadSentences.NotRead, FieldLine(Read(_w.CellC), "Temporary"));

    [Fact]
    public void AProjectionThatRequestsNoChildBearingFieldCarriesNoAnnotationAndNoClause()
    {
        var r = Read(_w.CellA, new RecordsTools.RecordsProject { form = "fields", fields = new[] { "EditorID" } });
        Assert.DoesNotContain(ReadSentences.NotRead, r);
        Assert.Null(ClauseLineOrNull(r, ReadSentences.NotReadFraming));
    }

    [Fact]
    public void AThreeToucherRecordWithNoChildBearingFieldCarriesNoAnnotation()
    {
        var r = Read(_w.Weapon);
        Assert.Contains("winner=" + _w.TopName, r);
        Assert.DoesNotContain(ReadSentences.NotRead, r);
    }

    [Fact]
    public void ADialogTopicsResponsesIsAnnotated_TheFieldSetIsDerivedNotAListOfCellFields() =>
        Assert.Contains(ReadSentences.NotRead, FieldLine(Read(_w.Topic), "Responses"));

    [Fact]
    public void AWorldspacesSubCellsIsAnnotated_TheSameDerivedFieldSetReachesAThirdType() =>
        Assert.Contains(ReadSentences.NotRead, FieldLine(Read(_w.Worldspace), "SubCells"));

    [Fact]
    public void AtDepthTwoTheContainersOwnSummaryLineStillCarriesTheAnnotation() =>
        Assert.Contains(ReadSentences.NotRead,
                        FieldLine(Read(_w.CellA, new RecordsTools.RecordsProject { form = "everything", depth = 2 }), "Temporary"));

    [Fact]
    public void TwoAnnotatedRecordsInOneResponseStillStateTheClauseOnce() =>
        Assert.Equal(1, Occurrences(ReadBoth(_w.CellA, _w.CellB), ClauseHead(ReadSentences.NotReadFraming)));

    [Fact]
    public void AResponseWithNothingAnnotatedCarriesNoClause() =>
        Assert.Equal(0, Occurrences(Read(_w.Weapon), ClauseHead(ReadSentences.NotReadFraming)));

    // ---- emission: the clause is earned by a field LINE, not by the decision to annotate ----------

    [Fact]
    public void ACapThatTruncatesTheAnnotatedFieldAwayStatesNoClauseOverIt()
    {
        var r = Read(_w.CellA, maxChars: 300);
        Assert.Contains("truncated: showing", r);
        Assert.DoesNotContain(ReadSentences.NotRead, r);
        Assert.Null(ClauseLineOrNull(r, ReadSentences.NotReadFraming));
    }

    [Fact]
    public void TheSameReadWithRoomForTheAnnotatedFieldStatesTheClauseOverIt()
    {
        var r = Read(_w.CellA);
        Assert.Contains(ReadSentences.NotRead, r);
        Assert.NotNull(ClauseLineOrNull(r, ReadSentences.NotReadFraming));
    }

    /// <summary>The annotated fields go FIRST so they survive the cut, with filler behind them so the body
    /// outgrows the cap either way. The response may then exceed max_chars by its own longest line and by nothing
    /// else — which it cannot manage if the clause is appended on top of the budget instead of held back from it.</summary>
    static string[] PaddedCellFields =>
        new[] { "Temporary", "Persistent", "Landscape", "NavigationMeshes" }
            .Concat(Enumerable.Repeat(new[] { "Name", "Flags", "Grid", "Lighting", "OcclusionData", "MaxHeightData",
                                              "LightingTemplate", "WaterHeight", "Regions", "Location", "Water",
                                              "Owner", "FactionRank", "LockList" }, 4).SelectMany(x => x)).ToArray();

    [Fact]
    public void AnAnnotatedResponseAnswersInsideItsMaxChars_TheClauseIsReservedNotAppended()
    {
        var r = Read(_w.CellA, new RecordsTools.RecordsProject { form = "fields", fields = PaddedCellFields }, maxChars: 1400);
        int longest = r.Split('\n').Max(l => l.Length + 1);
        Assert.Contains("truncated: showing", r);
        Assert.NotNull(ClauseLineOrNull(r, ReadSentences.NotReadFraming));
        Assert.True(r.Length <= 1400 + longest, $"len={r.Length} cap=1400 longest-line={longest}");
    }

    [Fact]
    public void TheCutQuotesTheCallersOwnMaxCharsNotTheReducedBudget()
    {
        var r = Read(_w.CellA, new RecordsTools.RecordsProject { form = "fields", fields = PaddedCellFields }, maxChars: 1400);
        Assert.Contains("max_chars=1400", r);
        Assert.DoesNotContain("max_chars=" + (1400 - ReadSentences.ClauseReserve(true)), r);
    }

    // ---- json ------------------------------------------------------------------------------------

    [Fact]
    public void Json_TheClauseIsAResponseLevelMemberBuiltFromTheSameSourceAsText()
    {
        using var doc = JsonDocument.Parse(Read(_w.CellA, format: "json"));
        Assert.True(doc.RootElement.TryGetProperty("owned_child_note", out var note));
        var s = note.GetString()!;
        Assert.StartsWith(ClauseHead(ReadSentences.NotReadFraming), s);
        Assert.Contains("Temporary", NamedFields(s, ReadSentences.NotReadFraming));
    }

    [Fact]
    public void Json_TheClauseIsWrittenAfterTheFieldsArrayItIsAboutNeverAheadOfIt()
    {
        var r = Read(_w.CellA, format: "json");
        int note = r.IndexOf("\"owned_child_note\"", StringComparison.Ordinal);
        int fields = r.IndexOf("\"fields\"", StringComparison.Ordinal);
        Assert.True(note > fields, $"note-at={note} fields-at={fields}");
    }

    [Fact]
    public void Json_TheAnnotationsPerFieldHalfRidesDisplay() =>
        Assert.Contains(ReadSentences.NotRead, JsonField(Read(_w.CellA, format: "json"), "Temporary", "display"));

    /// <summary>The annotation is DISPLAY-ONLY: it never replaces the leaf's own round-trip token, on either lane.</summary>
    [Fact]
    public void TheLeafsOwnTokenIsUnchangedOnBothLanes_TheAnnotationNeverReplacesTheValue()
    {
        Assert.StartsWith("[list: 0 item(s)]", JsonField(Read(_w.CellA, format: "json"), "Temporary", "note"));
        Assert.StartsWith("Temporary = [list: 0 item(s)]", FieldLine(Read(_w.CellA), "Temporary"));
    }

    [Fact]
    public void Json_AFieldsArrayTruncatedBeforeTheAnnotatedFieldStatesNoClause()
    {
        using var doc = JsonDocument.Parse(Read(_w.CellA, format: "json", maxChars: 300));
        Assert.Contains(doc.RootElement.GetProperty("records")[0].GetProperty("fields").EnumerateArray(),
                        f => f.TryGetProperty("note", out var n)
                             && n.GetString()!.StartsWith("[truncated at max_chars", StringComparison.Ordinal));
        Assert.False(doc.RootElement.TryGetProperty("owned_child_note", out _));
    }

    [Fact]
    public void Json_ABatchTruncatedBeforeItsOnlyAnnotatedRowStatesNoClause()
    {
        using var doc = JsonDocument.Parse(ReadBoth(_w.Weapon, _w.CellA, format: "json", maxChars: 400));
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.False(doc.RootElement.TryGetProperty("owned_child_note", out _));
    }

    [Fact]
    public void Json_AManifestOnlyResponseStatesNoClauseOverRowsItDidNotRender()
    {
        using var doc = JsonDocument.Parse(Read(_w.CellA, format: "json", toFile: _w.Scratch("spill.jsonl")));
        Assert.Equal(0, doc.RootElement.GetProperty("rendered").GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("owned_child_note", out _));
    }

    // ---- artifacts: a to_file job carries the annotation into the FILE ---------------------------

    [Fact]
    public void Artifact_TheAnnotationReachesTheFilesOwnRows()
    {
        var art = _w.Scratch("rows.jsonl");
        Read(_w.CellA, toFile: art);
        using var doc = JsonDocument.Parse(File.ReadAllLines(art)[1]);
        Assert.Contains(doc.RootElement.GetProperty("fields").EnumerateArray(),
                        f => f.TryGetProperty("path", out var p) && p.GetString() == "Temporary"
                             && f.TryGetProperty("display", out var d)
                             && d.GetString()!.Contains(ReadSentences.NotRead, StringComparison.Ordinal));
    }

    /// <summary>The manifest is line 1 and the rows are lines 2..N, so a clause pointing "above" would point away
    /// from its own subject for an artifact re-opened with no conversation attached. It names the fields instead.</summary>
    [Fact]
    public void Artifact_TheManifestClauseNamesTheAnnotatedFieldsAndClaimsNoPosition()
    {
        var art = _w.Scratch("manifest.jsonl");
        Read(_w.CellA, toFile: art);
        using var doc = JsonDocument.Parse(File.ReadAllLines(art)[0]);
        var note = doc.RootElement.GetProperty("notes")[0].GetString()!;
        Assert.Contains("Temporary", NamedFields(note, ReadSentences.NotReadFraming));
        Assert.DoesNotContain("above", note, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("below", note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Artifact_TheManifestOnlyResponseDoesNotStateAClauseOverRowsItDidNotRender() =>
        Assert.Null(ClauseLineOrNull(Read(_w.CellA, toFile: _w.Scratch("inline.jsonl")), ReadSentences.NotReadFraming));

    // ---- the remedy the clause names ------------------------------------------------------------
    //
    // The clause tells the caller where to get the read this one did not do: the same formids under
    // project={"form": "tree"}. These arms MAKE that call, so the remedy is pinned by what comes back rather
    // than by the wording — a sentence naming a lane nobody drives is how the clause came to name two deleted
    // tools in the first place. One test per promise the sentence makes.

    string Tree(FormKey fk, string? format = null, string? toFile = null, int maxChars = 0) =>
        RecordsTools.Records(Svc, formids: new[] { OwnedChildWorld.Fid(fk) }, format: format, to_file: toFile,
                             max_chars: maxChars, project: new RecordsTools.RecordsProject { form = "tree" });

    [Fact]
    public void TheRemedyIsServedOnTheSameFormidsTheAnnotatedReadUsed()
    {
        var r = Tree(_w.CellA);
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains("form=tree", r);
    }

    [Fact]
    public void TheRemedyNamesEveryPluginTouchingTheRecord() =>
        Assert.Contains($"3 plugin(s) touch this record (load order, winner last):\n    1. {_w.BaseName}\n"
                        + $"    2. {_w.MidName}\n    3. {_w.TopName}  (winner)",
                        Tree(_w.CellA).Replace("\r\n", "\n"));

    /// <summary>The whole point of the remedy: the content the annotated read could not show. The winner carries
    /// 0 and the cheap tier could only say so; the tree names the declarer and its count.</summary>
    [Fact]
    public void TheRemedyShowsWhatTheDeclaringPluginHoldsInTheAnnotatedField() =>
        Assert.Contains($"{_w.BaseName}: ", DiffBlock(Tree(_w.CellA)));

    [Fact]
    public void TheRemedyStatesTheDeclarersCountForTheFieldTheWinnerReadsEmpty() =>
        Assert.Contains($"Temporary: 3 vs {_w.TopName} 0 item(s)", Tree(_w.CellA));

    /// <summary>…and it answers the half the cheap tier could not: a child-bearing field the clause annotated
    /// because SOMEBODY might declare it, that in fact nobody does, draws no delta at all. The field is taken
    /// from the clause rather than typed here, so the two responses are tied to each other.</summary>
    [Fact]
    public void TheRemedySaysNothingForAnAnnotatedFieldNobodyElseDeclares()
    {
        var named = NamedFields(ClauseLine(Read(_w.CellA), ReadSentences.NotReadFraming), ReadSentences.NotReadFraming);
        Assert.Contains("NavigationMeshes", named);
        Assert.DoesNotContain("NavigationMeshes", DiffBlock(Tree(_w.CellA)));
    }

    [Fact]
    public void TheRemedyAnswersInJsonToo_TheDeclarationRidesTheDeclaringPluginsNode()
    {
        using var doc = JsonDocument.Parse(Tree(_w.CellA, format: "json"));
        var node = doc.RootElement.GetProperty("rows")[0].GetProperty("nodes").EnumerateArray()
                      .Single(n => n.GetProperty("plugin").GetString() == _w.BaseName);
        Assert.Contains(node.GetProperty("deltas").EnumerateArray(),
                        d => d.GetString()!.StartsWith($"Temporary: 3 vs {_w.TopName} 0 item(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void TheRemedySpillsToToFileLikeAnyOtherForm()
    {
        var art = _w.Scratch("remedy-tree.jsonl");
        var r = Tree(_w.CellA, toFile: art);
        Assert.True(File.Exists(art));
        Assert.Contains(art, r);
    }

    // ---- the precise tier the remedy now carries (#485) -------------------------------------------
    //
    // The cheap tier says "other plugins touch this record and their declarations were not read". The tree
    // form has already read them, so it says WHICH — and, the half no cheap tier can reach, that NONE do.
    // Every arm below pins a WHOLE rendered line composed from the sentence consts and the fixture's own
    // plugin names, so a second branch of DeclarersNote cannot satisfy it.

    [Fact]
    public void ThePreciseTierNamesEveryProviderDeclaringInACollectionField() =>
        Assert.Contains($"Temporary: {ReadSentences.DeclaredBy} {_w.BaseName}, {_w.MidName}",
                        DeclarersBlock(Tree(_w.CellF)));

    /// <summary>The whole block for the two-declarer cell, in one assertion: two collection fields naming both
    /// lower plugins, and two fields nobody declares stating so — Landscape in its own SINGULAR voice (a count,
    /// never the collection negative's plural "declares child record**s**", round-1 review-B L4), NavigationMeshes
    /// in the collection one. A tier that emitted only the positives, or only the fields it had something to say
    /// about, fails here rather than passing on a substring.</summary>
    [Fact]
    public void ThePreciseTierStatesEveryChildBearingFieldOfTheType_PositiveAndNegativeAlike() =>
        Assert.Equal(new[]
        {
            $"Landscape: {ReadSentences.CarriedBy} 0 provider(s)",
            $"NavigationMeshes: {ReadSentences.NoDeclarers}",
            $"Persistent: {ReadSentences.DeclaredBy} {_w.BaseName}, {_w.MidName}",
            $"Temporary: {ReadSentences.DeclaredBy} {_w.BaseName}, {_w.MidName}",
        }, DeclarersBlock(Tree(_w.CellF)));

    /// <summary>The SINGULAR negative, on its own: a count of zero, never the collection voice's plural claim
    /// (round-1 review-B L4 — "Saying 'not the merged total' about a singular child is simply false" applies
    /// just as hard to the empty answer as to the positive one).</summary>
    [Fact]
    public void ASingularFieldNobodyCarriesIsCountedZero_NeverTheCollectionVoice()
    {
        var line = DeclarersBlock(Tree(_w.CellF)).Single(l => l.StartsWith("Landscape: ", StringComparison.Ordinal));
        Assert.Equal($"Landscape: {ReadSentences.CarriedBy} 0 provider(s)", line);
        Assert.DoesNotContain("child records", line);
    }

    /// <summary>The NEGATIVE on its own, and the claim is that it is a SENTENCE. The tier this restores said
    /// nothing at all here, which a caller cannot tell apart from the tier not having run.</summary>
    [Fact]
    public void AFieldNoProviderDeclaresInGetsTheNoneSentence_NeverSilence() =>
        Assert.Contains($"NavigationMeshes: {ReadSentences.NoDeclarers}", DeclarersBlock(Tree(_w.CellF)));

    /// <summary>The SINGULAR arm: Cell.Landscape is ONE record its providers override, so the line is a COUNT.
    /// Naming them would be the collection sentence, which is false of this shape.</summary>
    [Fact]
    public void ASingularChildFieldIsCountedNotNamed() =>
        Assert.Contains($"Landscape: {ReadSentences.CarriedBy} 1 provider(s)", DeclarersBlock(Tree(_w.CellA)));

    /// <summary>The block is about DECLARATIONS, not differences, so it is emitted for a record only one plugin
    /// touches — where the tree renders no diff at all.</summary>
    [Fact]
    public void ASoleProviderRecordStillGetsTheBlock_ItIsNotADiff() =>
        Assert.Contains($"Temporary: {ReadSentences.DeclaredBy} {_w.BaseName}", DeclarersBlock(Tree(_w.CellC)));

    /// <summary>…and a record whose type owns no children gets no block at all — the field set is the type's own,
    /// so there is nothing to state.</summary>
    [Fact]
    public void ARecordTypeThatOwnsNoChildrenGetsNoBlockAtAll() =>
        Assert.DoesNotContain(ReadSentences.DeclarersLead, Tree(_w.Weapon));

    /// <summary>The remedy arm the review standard asks for: the cheap tier's clause tells a caller the tree form
    /// names the declarers. This MAKES that call, on the fields the clause itself named, and asserts every one of
    /// them comes back with a precise answer — so the promise is pinned by what returns, not by the wording.</summary>
    [Fact]
    public void TheRemedyNamedByTheCheapClauseAnswersPreciselyForEveryFieldTheClauseNamed()
    {
        var named = NamedFields(ClauseLine(Read(_w.CellF), ReadSentences.NotReadFraming), ReadSentences.NotReadFraming);
        Assert.NotEmpty(named);
        var block = DeclarersBlock(Tree(_w.CellF));
        foreach (var f in named)
            Assert.Contains(block, l => l.StartsWith(f + ": ", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDefaultReadOfTheSameCellStillStatesOnlyTheCheapTier()
    {
        var r = Read(_w.CellF);
        Assert.Contains("2 " + ReadSentences.NotRead, FieldLine(r, "Temporary"));
        Assert.DoesNotContain(ReadSentences.DeclarersLead, r);
        Assert.DoesNotContain(ReadSentences.DeclaredBy, r);
        Assert.DoesNotContain(ReadSentences.CarriedBy, r);
    }

    /// <summary>json carries the same answer, composed from the same TreeRow rather than rebuilt — the structured
    /// halves a caller can filter on, plus the sentence the text lane renders.</summary>
    [Fact]
    public void ThePreciseTierRidesJsonToo_StructuredHalvesAndTheSameSentence()
    {
        using var doc = JsonDocument.Parse(Tree(_w.CellF, format: "json"));
        var byField = doc.RootElement.GetProperty("rows")[0].GetProperty("child_declarers").EnumerateArray()
                         .ToDictionary(e => e.GetProperty("field").GetString()!);
        Assert.Equal(new[] { _w.BaseName, _w.MidName },
                     byField["Temporary"].GetProperty("declaring").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal($"{ReadSentences.DeclaredBy} {_w.BaseName}, {_w.MidName}",
                     byField["Temporary"].GetProperty("note").GetString());
        Assert.Equal(ReadSentences.NoDeclarers, byField["NavigationMeshes"].GetProperty("note").GetString());
        Assert.Empty(byField["NavigationMeshes"].GetProperty("declaring").EnumerateArray());
    }

    /// <summary>The precise block is emitted with the provider list it is about, ABOVE the diff — a provider whose
    /// content in a child-bearing field equals the reference's is omitted from the diff, so a declarations
    /// statement living inside the diff would silently drop half its subjects.</summary>
    [Fact]
    public void TheBlockSitsWithTheProviderListNotInsideTheDiff()
    {
        var t = Tree(_w.CellF).Replace("\r\n", "\n");
        Assert.True(t.IndexOf(ReadSentences.DeclarersLead, StringComparison.Ordinal)
                    < t.IndexOf("diff (field deltas", StringComparison.Ordinal));
    }

    // ---- the declarers block respects max_chars like every other row content (review-A H1) --------
    //
    // AppendChildDeclarers/WriteTreeRow used to append the whole block with no cap check of their own. A
    // sole-provider row (CellC: nothing to diff, so nothing else in the row loop would ever notice the overrun)
    // measurably returned 889 chars against a 200-char max_chars with truncated=false — the ClauseReserve class
    // of bug (ReadSentences.cs's own docstring on that const), which means the auto-spill that exists to make an
    // over-cap answer complete never fires.

    [Fact]
    public void ATextRowsDeclarersBlockAloneCanTripMaxChars_AndTheResponseIsMarkedTruncated()
    {
        // CellC is a SOLE-toucher row: row.Nodes.Count <= 1, so the diff loop (which has its own cap check) never
        // runs — before the fix this was the one path with NO cap check between the block and the end of the row.
        var r = Tree(_w.CellC, maxChars: 200);
        Assert.Contains("spilled: complete result", r);
    }

    [Fact]
    public void Json_ATextRowsDeclarersBlockAloneCanTripMaxChars_AndTheResponseIsMarkedTruncated()
    {
        // 400: past the envelope + row header + declarers block (so WriteTreeRow's own cap check fires and cuts
        // it mid-row, writing an inline "[child declarers cut …]" note), short of the whole row (889 chars
        // uncapped) — the shape that used to leave the RESPONSE-level truncated flag false while the row's own
        // content already says it was cut.
        using var doc = JsonDocument.Parse(Tree(_w.CellC, format: "json", maxChars: 400));
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("spilled", out _));
    }

    [Fact]
    public void ARowWithRoomForItsDeclarersBlockIsNotMarkedTruncatedByIt()
    {
        // The control: the same sole-provider shape, but a cap generous enough that nothing spills — proves the
        // fix's cap check does not fire on every row, only an over-cap one.
        var r = Tree(_w.CellC, maxChars: 4000);
        Assert.DoesNotContain("spilled:", r);
    }

    // ---- the block's own TAIL, not just its per-line checks (round-1 review-A MEDIUM2 / review-B L5-L7) ------
    //
    // AppendChildDeclarers checked cap BEFORE each field line and never after the last one, so the LAST line
    // pushing sb.Length past cap went unnoticed on a sole-provider row (nothing downstream to catch it — the diff
    // loop never runs). Measured on CellC (4-line block, full text 844 chars — the SINGULAR Landscape line reads
    // "carried by 0 provider(s)" now, round-1 review-B L4): a StringBuilder holding exactly cap-or-more AFTER the
    // final line (before TrimEnd('\n') removes one char) is the boundary, at max_chars=845; 846 is the first cap
    // the same content fits inside with room to spare.

    [Fact]
    public void ATextRowsDeclarersBlockTailAloneCanTripMaxChars_AndTheResponseIsMarkedTruncated() =>
        Assert.Contains("spilled: complete result", Tree(_w.CellC, maxChars: 845));

    [Fact]
    public void ARowWhoseDeclarersBlockFitsExactlyAtTheTailIsNotMarkedTruncated() =>
        Assert.DoesNotContain("spilled:", Tree(_w.CellC, maxChars: 846));

    /// <summary>When the block IS cut on a multi-provider row, the row stops there — it used to fall through into
    /// an unconditional "diff (field deltas…):" header for a section the cap already forbade, and the FIRST diff
    /// node then printed a SECOND, redundant cut notice (review-B L5). Measured on CellF (3 touchers): at every cap
    /// that cuts the declarers block, no diff header and no second "[nodes cut" notice follow it.</summary>
    [Fact]
    public void ACutDeclarersBlockEndsTheRow_NoEmptyDiffHeaderAndNoSecondCutNotice()
    {
        var r = Tree(_w.CellF, maxChars: 600);
        Assert.Contains("[child declarers cut", r);
        Assert.DoesNotContain("diff (field deltas", r);
        Assert.DoesNotContain("[nodes cut", r);
    }

    /// <summary>The lead is invariant framing text, not per-record content, so a multi-row response states it once
    /// — matching json's `child_declarers_note` and the artifact's manifest note, which never repeated it per row
    /// (round-1 review-A MEDIUM1 / review-B M1). Both CellA and CellF carry declarers, so a per-row repeat would
    /// show two occurrences; the fix shows one.</summary>
    [Fact]
    public void TheLeadIsStatedOnceAcrossMultipleRowsInText_NotPerRow()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { OwnedChildWorld.Fid(_w.CellA), OwnedChildWorld.Fid(_w.CellF) },
                                     project: new RecordsTools.RecordsProject { form = "tree" });
        Assert.Equal(1, Occurrences(r, ReadSentences.DeclarersLead));
        // …and each row still gets ITS OWN field lines under that one lead.
        Assert.Contains($"Temporary: {ReadSentences.DeclaredBy} {_w.BaseName}", r);
        Assert.Contains($"Temporary: {ReadSentences.DeclaredBy} {_w.BaseName}, {_w.MidName}", r);
    }

    // ---- json's response-level lead respects cap too, not just the row-level array (review-A MEDIUM2 / review-B L7) ----
    //
    // child_declarers_note used to be written unconditionally after `truncated` was already computed — a
    // Utf8JsonWriter cannot un-write it once appended, so the cap has to be checked (via DeclarersLeadReserve,
    // JsonWire's own measured cost for the property) BEFORE deciding to write it, not after. Measured on CellC's
    // json tree (full 1911 chars): 1884 is the last cap that drops the note and spills; 1886 is the first that
    // keeps it.

    [Fact]
    public void Json_TheResponseLevelLeadIsDroppedRatherThanOverrunningCap_AndTruncatedIsSet()
    {
        using var doc = JsonDocument.Parse(Tree(_w.CellC, format: "json", maxChars: 1884));
        Assert.False(doc.RootElement.TryGetProperty("child_declarers_note", out _));
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("spilled", out _));
    }

    [Fact]
    public void Json_TheResponseLevelLeadRidesWhenItFitsWithRoomToSpare()
    {
        using var doc = JsonDocument.Parse(Tree(_w.CellC, format: "json", maxChars: 1886));
        Assert.Equal(ReadSentences.DeclarersLead, doc.RootElement.GetProperty("child_declarers_note").GetString());
        Assert.False(doc.RootElement.GetProperty("truncated").GetBoolean());
    }

    // ---- the precise tier's lead reaches json and the artifact too, not just text (review-A M3) ----

    [Fact]
    public void Json_ThePreciseTiersLeadRidesTheResponseToo_OnceNotPerRow()
    {
        using var doc = JsonDocument.Parse(Tree(_w.CellF, format: "json"));
        Assert.Equal(ReadSentences.DeclarersLead, doc.RootElement.GetProperty("child_declarers_note").GetString());
    }

    [Fact]
    public void Json_ARecordTypeThatOwnsNoChildrenCarriesNoLead()
    {
        using var doc = JsonDocument.Parse(Tree(_w.Weapon, format: "json"));
        Assert.False(doc.RootElement.TryGetProperty("child_declarers_note", out _));
    }

    /// <summary>Marked optional ("child_declarers?"), like every other column most rows don't carry
    /// ("matches?", "note?", "cycles?" …) — a record type that owns no children writes a tree row with no such
    /// key at all (round-1 review: both reviewers independently, seeded-A LOW4 / gate-B L1).</summary>
    [Fact]
    public void Artifact_ThePreciseTiersRowSchemaNamesTheChildDeclarersColumnAsOptional()
    {
        var art = _w.Scratch("tree-schema.jsonl");
        Tree(_w.CellF, toFile: art);
        using var doc = JsonDocument.Parse(File.ReadAllLines(art)[0]);
        Assert.Contains(doc.RootElement.GetProperty("row_schema").EnumerateArray().Select(e => e.GetString()),
                        s => s == "child_declarers?");
    }

    [Fact]
    public void Artifact_ThePreciseTiersLeadRidesTheManifestNotes()
    {
        var art = _w.Scratch("tree-notes.jsonl");
        Tree(_w.CellF, toFile: art);
        using var doc = JsonDocument.Parse(File.ReadAllLines(art)[0]);
        Assert.Equal(ReadSentences.DeclarersLead, doc.RootElement.GetProperty("notes")[0].GetString());
    }

    [Fact]
    public void Artifact_ARecordTypeThatOwnsNoChildrenCarriesNoNotes()
    {
        var art = _w.Scratch("tree-no-notes.jsonl");
        Tree(_w.Weapon, toFile: art);
        using var doc = JsonDocument.Parse(File.ReadAllLines(art)[0]);
        Assert.False(doc.RootElement.TryGetProperty("notes", out _));
    }

    /// <summary>The tree's owned-child block: the lines under its lead, trimmed — so an arm about what the block
    /// says cannot pass on a match in the toucher list above it or the diff below it.</summary>
    static IReadOnlyList<string> DeclarersBlock(string tree)
    {
        var lines = tree.Replace("\r\n", "\n").Split('\n');
        int i = Array.FindIndex(lines, l => l.Trim() == ReadSentences.DeclarersLead);
        Assert.True(i >= 0, "the tree rendered no owned-child declarers block");
        var block = new List<string>();
        for (int j = i + 1; j < lines.Length && lines[j].StartsWith("    ", StringComparison.Ordinal); j++)
            block.Add(lines[j].Trim());
        return block;
    }

    /// <summary>The tree's per-plugin delta block, i.e. everything after the "diff (field deltas…)" header —
    /// so an arm about what the diff says cannot pass on a match in the toucher list above it.</summary>
    static string DiffBlock(string tree)
    {
        int i = tree.IndexOf("diff (field deltas", StringComparison.Ordinal);
        Assert.True(i >= 0, "the tree rendered no diff block");
        return tree[i..];
    }

    // ---- helpers ---------------------------------------------------------------------------------

    /// <summary>The text lane's rendered "  Path = value   (annotation)" line, trimmed.</summary>
    static string FieldLine(string render, string path)
    {
        foreach (var line in render.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith(path + " = ", StringComparison.Ordinal)) return t;
        }
        throw new Xunit.Sdk.XunitException($"no rendered line for field '{path}'");
    }

    static string JsonField(string json, string path, string member)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var f in doc.RootElement.GetProperty("records")[0].GetProperty("fields").EnumerateArray())
            if (f.GetProperty("path").GetString() == path && f.TryGetProperty(member, out var v))
                return v.GetString() ?? "";
        throw new Xunit.Sdk.XunitException($"no '{member}' on json field '{path}'");
    }

    /// <summary>A clause's field-INDEPENDENT head, up to the "{0}" its derived field list fills — so "is it
    /// stated, and how often" stays a separate question from "which fields does it name".</summary>
    static string ClauseHead(string framing) => framing[..framing.IndexOf("{0}", StringComparison.Ordinal)];

    static string? ClauseLineOrNull(string render, string framing)
    {
        var head = ClauseHead(framing);
        foreach (var line in render.Split('\n'))
            if (line.StartsWith(head, StringComparison.Ordinal)) return line;
        return null;
    }

    static string ClauseLine(string render, string framing) =>
        ClauseLineOrNull(render, framing) ?? throw new Xunit.Sdk.XunitException("the response states no clause");

    /// <summary>The field names a rendered clause actually derived — the "({0})" list, parsed back out.</summary>
    static IReadOnlyList<string> NamedFields(string clause, string framing)
    {
        var rest = clause[ClauseHead(framing).Length..];
        int close = rest.IndexOf(')');
        return close < 0
            ? Array.Empty<string>()
            : rest[..close].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                           .Where(s => s != "…").ToList();
    }

    static int Occurrences(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}


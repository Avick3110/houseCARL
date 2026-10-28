using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>#1209 read half: a partial-form override (record flag 0x4000) loads none of its data in game, so it does
/// not win. The winner is the full copy below it, for every read; the partial's plugin still touches the key.</summary>
[Trait("tier", "integration")]
public sealed class PartialFormWinnerTests : IDisposable
{
    const string MasterName = "HcPfMaster.esm";
    const string MasterPartialName = "HcPfMasterPartial.esm";
    const string PartialName = "HcPfPartial.esp";
    const string LateName = "HcPfLate.esp";
    const int Partial = 0x4000;

    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-partial-form-" + Guid.NewGuid().ToString("N"));
    readonly string _mods;
    readonly LoadOrderService _svc;
    readonly FormKey _cell;        // the master's interior cell; the esp overrides it as a partial form
    readonly FormKey _ref;         // a placed object in that cell
    readonly FormKey _location;    // the cell's location, which only the full copy carries
    readonly FormKey _masterCell;  // a master interior cell; the master-flagged plugin overrides it as a partial form
    readonly FormKey _gridCell;    // a temporary exterior cell; the esp overrides it with 0x4000 set
    readonly FormKey _plainCell;   // a master interior cell the esp overrides normally
    readonly FormKey _strayCell;   // a master interior cell the esp overrides as a partial form carrying a grid
    readonly FormKey _dupCell;     // a master interior cell the esp holds twice: full, then partial
    readonly FormKey _deepCell;    // a master interior cell both later plugins override as partial forms
    readonly FormKey _persistCell; // a master exterior cell the esp overrides as a persistent partial carrying a grid
    readonly FormKey _world;       // the master's worldspace; the esp overrides it as a partial form
    readonly FormKey _topic;       // a master topic; the esp overrides it as a partial form
    readonly FormKey _mixed;       // a master static the esp overrides as a partial DIAL, so the key's class is mixed
    readonly FormKey _ghost;       // a cell whose definer is absent: partial, then full (the esp), then partial
    readonly FormKey _ghost2;      // a cell whose definer is absent and every copy is partial
    readonly FormKey _winBack;     // a master static: a partial DIAL, then a full DIAL (the esp), then a partial DIAL

    public PartialFormWinnerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "game", "Data"));
        var master = new SkyrimMod(new ModKey("HcPfMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var loc = master.Locations.AddNew(); loc.EditorID = "HcPfLocation";
        _location = loc.FormKey;
        var obj = master.Statics.AddNew(); obj.EditorID = "HcPfStatic";
        Cell Interior(FormKey fk, string edid) => new(fk, SkyrimRelease.SkyrimSE)
            { EditorID = edid, Flags = Cell.Flag.IsInteriorCell | Cell.Flag.PublicArea };
        var cell = Interior(master.GetNextFormKey(), "HcPfCell");
        cell.Location.SetTo(loc);
        var placed = new PlacedObject(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcPfRef" };
        placed.Base.SetTo(obj);
        cell.Temporary.Add(placed);
        var masterCell = Interior(master.GetNextFormKey(), "HcPfMasterCell");
        var plainCell = Interior(master.GetNextFormKey(), "HcPfPlainCell");
        var strayCell = Interior(master.GetNextFormKey(), "HcPfStrayCell");
        var dupCell = Interior(master.GetNextFormKey(), "HcPfDupCell");
        var deepCell = Interior(master.GetNextFormKey(), "HcPfDeepCell");
        deepCell.Location.SetTo(loc);
        FileInterior(master, cell, masterCell, plainCell, strayCell, dupCell, deepCell);
        var ws = new Worldspace(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcPfWorld" };
        var grid = new Cell(master.GetNextFormKey(), SkyrimRelease.SkyrimSE)
            { EditorID = "HcPfGridCell", Flags = Cell.Flag.HasWater, Grid = new CellGrid { Point = new P2Int(0, 0) } };
        var persist = new Cell(master.GetNextFormKey(), SkyrimRelease.SkyrimSE)
            { EditorID = "HcPfPersistCell", Flags = Cell.Flag.HasWater, Grid = new CellGrid { Point = new P2Int(1, 0) } };
        FileExterior(ws, grid, persist);
        master.Worldspaces.Add(ws);
        var topic = master.DialogTopics.AddNew(); topic.EditorID = "HcPfTopic";
        var mixed = master.Statics.AddNew(); mixed.EditorID = "HcPfMixed";
        var winBack = master.Statics.AddNew(); winBack.EditorID = "HcPfWinBack";
        _winBack = winBack.FormKey;
        DialogTopic Topic(int flags) => new(_winBack, SkyrimRelease.SkyrimSE) { EditorID = "HcPfWinBack", MajorRecordFlagsRaw = flags };
        _cell = cell.FormKey; _ref = placed.FormKey; _masterCell = masterCell.FormKey;
        _gridCell = grid.FormKey; _plainCell = plainCell.FormKey; _strayCell = strayCell.FormKey; _dupCell = dupCell.FormKey; _deepCell = deepCell.FormKey;
        _persistCell = persist.FormKey; _world = ws.FormKey; _topic = topic.FormKey; _mixed = mixed.FormKey;
        var gone = new ModKey("HcPfGone", ModType.Master);
        _ghost = new FormKey(gone, 0x800); _ghost2 = new FormKey(gone, 0x801);

        // Each override carries its EDID only, as an xEdit-made partial form does (Mutagen pads the rest).
        Cell Over(Cell of, int flags, Cell.Flag cellFlags) => new(of.FormKey, SkyrimRelease.SkyrimSE)
            { EditorID = of.EditorID, MajorRecordFlagsRaw = flags, Flags = cellFlags };
        Cell Ghost(FormKey fk, int flags) => new(fk, SkyrimRelease.SkyrimSE)
            { EditorID = "HcPfGhost" + fk.ID, MajorRecordFlagsRaw = flags, Flags = Cell.Flag.IsInteriorCell };

        var masterPartial = new SkyrimMod(new ModKey("HcPfMasterPartial", ModType.Master), SkyrimRelease.SkyrimSE);
        FileInterior(masterPartial, Over(masterCell, Partial, 0), Over(deepCell, Partial, 0), Ghost(_ghost, Partial));
        masterPartial.DialogTopics.Add(Topic(Partial));

        var esp = new SkyrimMod(ModKey.FromNameAndExtension(PartialName), SkyrimRelease.SkyrimSE);
        // The stray cell's partial carries a grid it should not; it is still interior, so still partial.
        var stray = Over(strayCell, Partial, 0);
        stray.Grid = new CellGrid { Point = new P2Int(3, 3) };
        FileInterior(esp, Over(cell, Partial, 0), Over(plainCell, 0, Cell.Flag.IsInteriorCell), stray,
                     Over(dupCell, 0, Cell.Flag.IsInteriorCell), Over(dupCell, Partial, 0), Over(deepCell, Partial, 0),
                     Ghost(_ghost, 0), Ghost(_ghost2, Partial));
        var espWs = new Worldspace(ws.FormKey, SkyrimRelease.SkyrimSE) { EditorID = ws.EditorID, MajorRecordFlagsRaw = Partial };
        // A persistent exterior partial loses though it carries a grid.
        var persistOver = Over(persist, Partial | (int)Cell.MajorFlag.Persistent, 0);
        persistOver.Grid = new CellGrid { Point = new P2Int(1, 0) };
        FileExterior(espWs, Over(grid, Partial, 0), persistOver);
        esp.Worldspaces.Add(espWs);
        esp.DialogTopics.Add(new DialogTopic(topic.FormKey, SkyrimRelease.SkyrimSE) { EditorID = topic.EditorID, MajorRecordFlagsRaw = Partial });
        // The mixed key's partial copy is another class than the full copy below it.
        esp.DialogTopics.Add(new DialogTopic(mixed.FormKey, SkyrimRelease.SkyrimSE) { EditorID = mixed.EditorID, MajorRecordFlagsRaw = Partial });
        esp.DialogTopics.Add(Topic(0));

        var late = new SkyrimMod(ModKey.FromNameAndExtension(LateName), SkyrimRelease.SkyrimSE);
        FileInterior(late, Ghost(_ghost, Partial), Ghost(_ghost2, Partial));
        late.DialogTopics.Add(Topic(Partial));

        _mods = Path.Combine(_root, "inst", "mods");
        void Place(string mod, SkyrimMod m, params ModKey[] order)
        {
            Directory.CreateDirectory(Path.Combine(_mods, mod));
            // The esp holds one cell twice, which Mutagen refuses unless told not to check.
            m.BeginWrite.ToPath(Path.Combine(_mods, mod, m.ModKey.FileName)).WithLoadOrder(order)
             .WithKnownMasters(order.Select(k => new KeyedMasterStyle(k, MasterStyle.Full)).ToArray()).NoFormIDUniquenessCheck().Write();
        }
        Place("MasterMod", master);
        Place("MasterPartialMod", masterPartial, gone, master.ModKey);
        Place("PartialMod", esp, gone, master.ModKey);
        Place("LateMod", late, gone, master.ModKey);

        var inst = Path.Combine(_root, "inst");
        File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(inst, "profiles", "Default");
        Directory.CreateDirectory(prof);
        var names = new[] { MasterName, MasterPartialName, PartialName, LateName };
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", names) + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), string.Join("", names.Select(n => "*" + n + "\r\n")));
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+LateMod\r\n+PartialMod\r\n+MasterPartialMod\r\n+MasterMod\r\n");
        _svc = LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(_root, "user.json")));
    }

    public void Dispose()
    {
        _svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }

    static void FileInterior(SkyrimMod m, params Cell[] cells)
    {
        var block = new CellBlock { BlockNumber = 1, GroupType = GroupTypeEnum.InteriorCellBlock };
        var sub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        foreach (var c in cells) sub.Cells.Add(c);
        block.SubBlocks.Add(sub); m.Cells.Records.Add(block);
    }

    static void FileExterior(Worldspace ws, params Cell[] cells)
    {
        var blk = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
        var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
        foreach (var c in cells) sub.Items.Add(c);
        blk.Items.Add(sub); ws.SubCells.Add(blk);
    }

    static string Fid(FormKey fk) => FormIdToken.Of(fk);

    string Fields(FormKey fk, JsonElement? source, params string[] fields) =>
        RecordsTools.Records(_svc, formids: new[] { Fid(fk) }, source: source,
                             project: new RecordsTools.RecordsProject { form = "fields", fields = fields });

    static JsonElement Pole(string plugin) => JsonDocument.Parse(JsonSerializer.Serialize(plugin)).RootElement.Clone();

    [Fact]
    public void ResolveNamesTheFullCopyBelowAPartialFormAsTheWinner()
    {
        Assert.Equal(MasterName, _svc.CaptureView().ResolveWinner(_cell)!.Value.WinnerPlugin);
        var r = Fields(_cell, null, "Location", "Flags");
        Assert.Contains("winner=" + MasterName, r);
        Assert.Contains(Fid(_location), r);
        Assert.Contains("PublicArea", r);
    }

    [Fact]
    public void TheTreeLabelsAndDiffsAgainstTheFullCopy()
    {
        var r = RecordsTools.Records(_svc, formids: new[] { Fid(_cell) },
                                     project: new RecordsTools.RecordsProject { form = "tree", fields = new[] { "Location" } });
        Assert.Contains("1. " + MasterName + "  (winner)", r);
        Assert.DoesNotContain(PartialName + "  (winner)", r);
        Assert.Contains("vs " + MasterName, r);
    }

    [Fact]
    public void AWinnerScanCountsThePartialsCellUnderTheFullCopysPlugin()
    {
        var r = RecordsTools.Records(_svc, types: new[] { "CELL" }, where: new[] { "winner = " + PartialName },
                                     project: new RecordsTools.RecordsProject { form = "summary" });
        Assert.DoesNotContain("HcPfCell ", r);
        Assert.Contains("HcPfPlainCell", r);
        var m = RecordsTools.Records(_svc, types: new[] { "CELL" }, where: new[] { "winner = " + MasterName },
                                     project: new RecordsTools.RecordsProject { form = "summary" });
        Assert.Contains("HcPfCell ", m);
    }

    [Fact]
    public void AParentHopFromARefInThatCellReadsTheFullCopy() =>
        Assert.Contains("PublicArea", Fields(_ref, null, "*parent.Flags"));

    [Fact]
    public void ANamedReadOfThePartialsPluginStillReturnsItsOwnCopy()
    {
        var r = Fields(_cell, Pole(PartialName), "MajorFlags", "Location");
        Assert.Contains("bit14", r);
        Assert.DoesNotContain(Fid(_location), r);
    }

    /// <summary>Pins unchanged behaviour: the partial's plugin still touches the key and counts toward its depth.</summary>
    [Fact]
    public void MembershipStillCountsThePartialsPlugin()
    {
        var view = _svc.CaptureView();
        Assert.Equal(new[] { MasterName, PartialName }, view.TouchingPlugins(_cell));
        Assert.Equal(2, view.ResolveWinner(_cell)!.Value.OverrideDepth);
        Assert.True(view.Touches(_cell, PartialName));
    }

    [Fact]
    public void AnApplyBuildsItsOverrideFromTheFullCopyWithoutThePartialFlag()
    {
        var r = ApplyTools.Apply(_svc, ops: JsonDocument.Parse(JsonSerializer.Serialize(new[]
            { new { formid = Fid(_cell), field_path = "EditorID", value = "HcPfCellEdited" } })).RootElement.Clone(), patch: "HcPfApply");
        var path = Directory.GetFiles(_mods, "HcPfApply*.esp", SearchOption.AllDirectories).Single();
        using var patch = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        var cell = patch.EnumerateMajorRecords<ICellGetter>().Single(c => c.FormKey == _cell);
        Assert.True((cell.MajorRecordFlagsRaw & Partial) == 0, r);
        Assert.Equal(_location, cell.Location.FormKey);
        Assert.True(cell.Flags.HasFlag(Cell.Flag.PublicArea), r);
    }

    [Fact]
    public void APartialFormInAMasterFlaggedPluginDoesNotWinEither() =>
        Assert.Equal(MasterName, _svc.CaptureView().ResolveWinner(_masterCell)!.Value.WinnerPlugin);

    /// <summary>xEdit's rule: 0x4000 on a temporary exterior cell is not a partial form, so the override wins, though
    /// its EDID-only copy carries no grid.</summary>
    [Fact]
    public void AFlaggedTemporaryExteriorCellWinsAsANormalOverride() =>
        Assert.Equal(PartialName, _svc.CaptureView().ResolveWinner(_gridCell)!.Value.WinnerPlugin);

    /// <summary>An interior cell is told by where it sits, so a stray grid on its partial does not make it win.</summary>
    [Fact]
    public void AnInteriorPartialCarryingAGridStillLoses() =>
        Assert.Equal(MasterName, _svc.CaptureView().ResolveWinner(_strayCell)!.Value.WinnerPlugin);

    string Tree(FormKey fk, string? format = null) =>
        RecordsTools.Records(_svc, formids: new[] { Fid(fk) }, format: format,
                             project: new RecordsTools.RecordsProject { form = "tree", fields = new[] { "Location" } });

    [Fact]
    public void NoTextCallsThePartialsPluginTheWinner()
    {
        var tree = Tree(_cell);
        Assert.DoesNotContain("winner last", tree);
        Assert.Contains("(load order; a partial form above the winner does not win)", tree);
        Assert.Contains("\"winner\": \"" + MasterName + "\"", Tree(_cell, "json"));

        var delta = RecordsTools.Records(_svc, formids: new[] { Fid(_deepCell) }, source: Pole(MasterPartialName),
                                         versus: Pole("previous_provider"), project: new RecordsTools.RecordsProject { form = "delta" });
        Assert.Contains("stack above the subject (load order; the winner is " + MasterName + ", a partial form above it does not win): " + PartialName, delta);

        var refused = Fields(_cell, Pole(MasterPartialName), "Location");
        Assert.Contains("(load order; the winner is " + MasterName + ", a partial form above it does not win): " + MasterName + ", " + PartialName, refused);
    }

    /// <summary>A refused tree row and a JSON delta row each name the winner their stacks do not end on.</summary>
    [Fact]
    public void EveryJsonRowNamesTheWinnerAPartialFormTops()
    {
        var refused = RecordsTools.Records(_svc, formids: new[] { Fid(_cell) }, versus: Pole(LateName), format: "json",
                                           project: new RecordsTools.RecordsProject { form = "tree" });
        Assert.Contains("\"error\": \"versus:", refused);
        Assert.Contains("\"winner\": \"" + MasterName + "\"", refused);

        var delta = RecordsTools.Records(_svc, formids: new[] { Fid(_deepCell) }, source: Pole(MasterPartialName), format: "json",
                                         versus: Pole("previous_provider"), project: new RecordsTools.RecordsProject { form = "delta" });
        Assert.Contains("\"winner\": \"" + MasterName + "\"", delta);
        // Pins unchanged behaviour: with no partial form the delta row adds no winner.
        Assert.DoesNotContain("\"winner\"", RecordsTools.Records(_svc, formids: new[] { Fid(_plainCell) }, source: Pole(PartialName), format: "json",
                                                              versus: Pole("previous_provider"), project: new RecordsTools.RecordsProject { form = "delta" }));
    }

    [Fact]
    public void TheTreeArtifactsManifestDeclaresTheWinnerItWrites()
    {
        string Schema(FormKey fk, string name)
        {
            var art = Path.Combine(_root, name + ".jsonl");
            RecordsTools.Records(_svc, formids: new[] { Fid(fk) }, to_file: art, project: new RecordsTools.RecordsProject { form = "tree" });
            using var doc = JsonDocument.Parse(File.ReadLines(art).First());
            return string.Join(",", doc.RootElement.GetProperty("row_schema").EnumerateArray().Select(e => e.GetString()));
        }
        Assert.Contains("touchers,winner?,", Schema(_cell, "partial"));
        // Pins unchanged behaviour: with no partial form the schema is as before.
        Assert.DoesNotContain("winner", Schema(_plainCell, "plain"));
    }

    [Fact]
    public void TheDeltaArtifactsManifestDeclaresTheWinnerItWrites()
    {
        string Schema(FormKey fk, string subject, string name)
        {
            var art = Path.Combine(_root, name + ".jsonl");
            RecordsTools.Records(_svc, formids: new[] { Fid(fk) }, source: Pole(subject), versus: Pole("previous_provider"), to_file: art,
                                 project: new RecordsTools.RecordsProject { form = "delta" });
            using var doc = JsonDocument.Parse(File.ReadLines(art).First());
            return string.Join(",", doc.RootElement.GetProperty("row_schema").EnumerateArray().Select(e => e.GetString()));
        }
        Assert.Contains("stack_above?,winner?,", Schema(_deepCell, MasterPartialName, "delta-partial"));
        // Pins unchanged behaviour: with no partial form the schema is as before.
        Assert.DoesNotContain("winner", Schema(_plainCell, PartialName, "delta-plain"));
    }

    /// <summary>A partial copy above the winner is read while its plugin's share is walked, not after the winner: once
    /// the winner is handed on, its node reads nothing more, so a file changed then is never opened.</summary>
    [Fact]
    public void AHeldPartialIsReadBeforeTheWinnerArrives()
    {
        var pin = _svc.CapturePin();
        using var session = pin.Resolver.OpenSession();
        var reads = new Dictionary<string, string?>();
        _svc.ReadArea.FoldTreeChunkPinned(pin, session, new[] { _persistCell }, new[] { "*parent.EditorID" }, (_, _, plugin, read, isWinner) =>
        {
            if (isWinner)
            {
                // The parent hop reads the worldspace's winner; a read after this point would reopen the master and find it changed.
                session.ReleaseOverlay(MasterName);
                File.SetLastWriteTimeUtc(Path.Combine(_mods, "MasterMod", MasterName), DateTime.UtcNow.AddSeconds(5));
            }
            reads[plugin] = read.EditorId;
            return true;
        });
        Assert.Equal("HcPfPersistCell", reads[PartialName]);
    }

    /// <summary>A held partial answers each child-bearing field from its own copy: declaring or not, never unreadable.</summary>
    [Fact]
    public void AHeldPartialAnswersItsChildDeclarersFromItsOwnCopy()
    {
        var pin = _svc.CapturePin();
        using var session = pin.Resolver.OpenSession();
        var declarers = _svc.ReadArea.FoldTreeChunkPinned(pin, session, new[] { _cell }, null, (_, _, _, _, _) => true)[0]!.ChildDeclarers;
        Assert.Contains(declarers, cd => cd.Declaring.Contains(MasterName));
        Assert.All(declarers, cd => Assert.DoesNotContain(PartialName, cd.Declaring.Concat(cd.Unreadable)));
    }

    /// <summary>A hand-built row carries no winner position; it reads winner last and calls no toucher a partial form.</summary>
    [Fact]
    public void ARowWithNoWinnerPositionLabelsNoPartialForm()
    {
        var row = new RecordReads.TreeRow(Fid(_cell), "Cell", "HcPfCell", new[] { MasterName, PartialName }, MasterName,
            new[] { new RecordReads.TreeNodeDelta(MasterName, false, true, null, null),
                    new RecordReads.TreeNodeDelta(PartialName, true, false, null, new FieldsDiff.Result(Array.Empty<string>(), true, 0, Array.Empty<string>(), 0)) },
            null, Array.Empty<ChildDeclarers>());
        var r = RecordsTools.RenderRecordsTree(new[] { row }, 1, 1, 0, false, "records  form=tree", null, 100_000, null, out _);
        Assert.DoesNotContain("partial form", r);
        Assert.Contains("2. " + PartialName + "  (winner)", r);
    }

    [Fact]
    public void TheTreeLabelsEachPartialAboveTheWinner()
    {
        var r = Tree(_deepCell);
        Assert.Contains("1. " + MasterName + "  (winner)\n", r);
        Assert.Contains("2. " + MasterPartialName + "  (partial form, does not win)\n", r);
        Assert.Contains("3. " + PartialName + "  (partial form, does not win)\n", r);
        Assert.DoesNotContain("partial form, does not win", Tree(_plainCell));
    }

    [Fact]
    public void APartialWorldspaceAndAPartialTopicEachLoseToTheFullCopy()
    {
        var view = _svc.CaptureView();
        Assert.Equal(MasterName, view.ResolveWinner(_world)!.Value.WinnerPlugin);
        Assert.Equal(MasterName, view.ResolveWinner(_topic)!.Value.WinnerPlugin);
    }

    [Fact]
    public void APersistentExteriorPartialCarryingAGridLoses() =>
        Assert.Equal(MasterName, _svc.CaptureView().ResolveWinner(_persistCell)!.Value.WinnerPlugin);

    /// <summary>With the definer absent, a partial below the full copy and one above it both lose; with every copy
    /// partial the newest keeps the key.</summary>
    [Fact]
    public void AKeyWhoseDefinerIsAbsentWinsOnItsFullCopy()
    {
        var view = _svc.CaptureView();
        Assert.Equal(PartialName, view.ResolveWinner(_ghost)!.Value.WinnerPlugin);
        Assert.Equal(LateName, view.ResolveWinner(_ghost2)!.Value.WinnerPlugin);
    }

    [Fact]
    public void APartialAboveAFullWinnerLeavesItHoldingTheKeyOnce() => Assert.True(_svc.CaptureView().WinnerHoldsOnce(_cell));

    [Fact]
    public void AMixedClassKeyReportsTheFullCopysClass() =>
        Assert.Contains("Static", _svc.CaptureView().WinnerClassOf(_mixed)!.Name);

    [Fact]
    public void BothMaterialisedTreesNameTheFullCopyTheirWinner()
    {
        var pin = _svc.CapturePin();
        Assert.Equal(MasterName, _svc.ReadArea.ResolveTreePinned(pin, _cell, null)!.Winner.Plugin);
        using var session = pin.Resolver.OpenSession();
        Assert.Equal(MasterName, pin.Resolver.ResolveTree(session, _cell)!.Winner.Plugin);
    }

    /// <summary>One chunk: a displaced-winner row beside a normal row of the same two plugins, and a three-deep stack;
    /// each row labels and diffs against its own winner.</summary>
    [Fact]
    public void AChunkOfMixedRowsLabelsAndDiffsEachAgainstItsWinner()
    {
        var r = RecordsTools.Records(_svc, formids: new[] { Fid(_cell), Fid(_plainCell), Fid(_deepCell) },
                                     project: new RecordsTools.RecordsProject { form = "tree", fields = new[] { "Location" } });
        var rows = r.Split("\n" + Fid(_plainCell));
        var plainAndDeep = rows[1].Split("\n" + Fid(_deepCell));
        Assert.Contains("1. " + MasterName + "  (winner)", rows[0]);
        Assert.Contains("Location", rows[0]);
        Assert.Contains("2. " + PartialName + "  (winner)", plainAndDeep[0]);
        Assert.DoesNotContain("Location", plainAndDeep[0]);
        Assert.Contains("1. " + MasterName + "  (winner)", plainAndDeep[1]);
        Assert.Contains("Location", plainAndDeep[1]);
    }

    /// <summary>A read answered from the full copy under a partial relied on the partial's plugin too: its flag decided the winner.</summary>
    [Fact]
    public void AMarkedReadRefusesWhenThePartialsPluginChangesMidCall()
    {
        Assert.Contains("changed on disk during this call", ReadWhilePartialChanges(_cell));
        // Pins unchanged behaviour: a key the partial's plugin does not touch is not refused for it.
        Assert.DoesNotContain("changed on disk", ReadWhilePartialChanges(_location));
    }

    /// <summary>Every copy of an all-partial key decided its winner, so a read of it relied on each copy's plugin.</summary>
    [Fact]
    public void AMarkedReadOfAnAllPartialKeyRefusesWhenItsLowerPartialChanges()
    {
        Assert.Contains("changed on disk during this call", ReadWhileChanges(_ghost2, LateName, "PartialMod", PartialName));
        // Pins unchanged behaviour: a key with no partial copy does not refuse for a plugin it does not answer from.
        Assert.DoesNotContain("changed on disk", ReadWhileChanges(_plainCell, PartialName, "MasterPartialMod", MasterPartialName));
    }

    /// <summary>Pins unchanged behaviour: a key with no partial copy relies on its winner alone, not on the plugin below it.</summary>
    [Fact]
    public void AMarkedReadOfAPlainKeyDoesNotRefuseWhenTheProviderBelowItsWinnerChanges() =>
        Assert.DoesNotContain("changed on disk", ReadWhileChanges(_plainCell, PartialName, "MasterMod", MasterName));

    string ReadWhilePartialChanges(FormKey fk) => ReadWhileChanges(fk, MasterName, "PartialMod", PartialName);

    string ReadWhileChanges(FormKey fk, string trigger, string mod, string changed)
    {
        var path = Path.Combine(_mods, mod, changed);
        bool saved = false;
        MidCallChange.BeforeCheck.Value = name =>
        {
            if (!saved && name == trigger) { File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5 + _bumps++)); saved = true; }
        };
        try { return Fields(fk, null, "EditorID"); }
        finally { MidCallChange.BeforeCheck.Value = null; }
    }
    int _bumps;

    /// <summary>Pins unchanged behaviour: with no partial form the sentences say winner last, and the JSON row adds no winner.</summary>
    [Fact]
    public void AKeyWithNoPartialKeepsTheWinnerLastText()
    {
        Assert.Contains("2 plugin(s) touch this record (load order, winner last):", Tree(_plainCell));
        Assert.DoesNotContain("\"winner\": ", Tree(_plainCell, "json"));
        Assert.Contains("Touched by (load order, winner last): " + MasterName + ", " + PartialName,
                        Fields(_plainCell, Pole(MasterPartialName), "Location"));
    }

    [Fact]
    public void APluginHoldingAKeyFullThenPartialWinsOnItsFullCopy()
    {
        Assert.Equal(1, _svc.CaptureView().WinnerAt(_dupCell));
        var r = RecordsTools.Records(_svc, formids: new[] { Fid(_dupCell) }, project: new RecordsTools.RecordsProject { form = "tree" });
        Assert.Contains("2. " + PartialName + "  (winner)", r);
        Assert.DoesNotContain("3. " + PartialName + "  (winner)", r);
    }

    /// <summary>In one chunk, each row's reference node is its own winner's copy, not its top provider, and every
    /// other node is diffed against it.</summary>
    [Fact]
    public void EachRowOfAChunkDiffsAgainstItsOwnWinnersCopy()
    {
        var json = RecordsTools.Records(_svc, formids: new[] { Fid(_cell), Fid(_plainCell), Fid(_deepCell) }, format: "json",
                                        project: new RecordsTools.RecordsProject { form = "tree", fields = new[] { "Location" } });
        using var doc = JsonDocument.Parse(json);
        var rows = doc.RootElement.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("formid").GetString()!);
        void Baseline(FormKey fk, string winner, int differing)
        {
            var nodes = rows[Fid(fk)].GetProperty("nodes").EnumerateArray().ToList();
            Assert.Equal(winner, nodes.Single(n => n.GetProperty("is_reference").GetBoolean()).GetProperty("plugin").GetString());
            Assert.Equal(differing, nodes.Count(n => n.TryGetProperty("delta_count", out var c) && c.GetInt32() > 0));
        }
        Baseline(_cell, MasterName, 1);
        Baseline(_plainCell, PartialName, 0);
        Baseline(_deepCell, MasterName, 2);
    }

    /// <summary>A key going full, partial, full, partial wins on its second full copy, its class included.</summary>
    [Fact]
    public void AFullCopyOverAPartialFormWinsTheKeyBack()
    {
        var view = _svc.CaptureView();
        Assert.Equal(PartialName, view.ResolveWinner(_winBack)!.Value.WinnerPlugin);
        Assert.Equal(2, view.WinnerAt(_winBack));
        Assert.Contains("DialogTopic", view.WinnerClassOf(_winBack)!.Name);
        var r = RecordsTools.Records(_svc, formids: new[] { Fid(_winBack) }, project: new RecordsTools.RecordsProject { form = "tree" });
        Assert.Contains("3. " + PartialName + "  (winner)", r);
        Assert.Contains("4. " + LateName + "  (partial form, does not win)", r);
    }

    [Fact]
    public void APluginHoldingTheWinningKeyTwiceDoesNotHoldItOnce() => Assert.False(_svc.CaptureView().WinnerHoldsOnce(_dupCell));

    /// <summary>A lane that asks only the winner's class relied on the partial above it, whose flag decided it.</summary>
    [Fact]
    public void AWinnerClassAnswerReliesOnThePartialAboveTheWinner()
    {
        using var scope = MidCallChange.Begin();
        Assert.NotNull(_svc.CaptureView().WinnerClassOf(_cell));
        File.SetLastWriteTimeUtc(Path.Combine(_mods, "PartialMod", PartialName), DateTime.UtcNow.AddSeconds(5));
        Assert.Contains($"'{PartialName}' changed on disk during this call", scope.Settle());
    }

    /// <summary>The fold walks each provider plugin once per chunk, a displaced winner's plugin included.</summary>
    [Fact]
    public void AChunkMixingADisplacedWinnerWithARowSharingItsPluginWalksItOnce()
    {
        long before = _svc.Counters.CollectPasses;
        RecordsTools.Records(_svc, formids: new[] { Fid(_cell), Fid(_plainCell) },
                             project: new RecordsTools.RecordsProject { form = "tree", fields = new[] { "Location" } });
        Assert.Equal(2, _svc.Counters.CollectPasses - before);
    }

    /// <summary>Pins unchanged behaviour: an override with no partial flag wins as before.</summary>
    [Fact]
    public void AnUnflaggedOverrideStillWins() =>
        Assert.Equal(PartialName, _svc.CaptureView().ResolveWinner(_plainCell)!.Value.WinnerPlugin);
}

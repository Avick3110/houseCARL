using System.Text;
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
/// The synthetic MO2 world for the owned-child content annotation: a BASE master that declares child records,
/// a MID plugin that touches the same cell declaring nothing, and a TOP winner that touches the parent and
/// carries no children at all. Its own world — no shared-world record has a winner whose child collection is
/// empty while a lower plugin fills it.
/// </summary>
public sealed class OwnedChildWorld : IDisposable
{
    public string Root { get; }
    public LoadOrderService Svc { get; }

    public string BaseName { get; }
    public string MidName { get; }
    /// <summary>The MID plugin's file on disk, so a test can take an exclusive handle on it and see what a read
    /// does when a sibling body will not open.</summary>
    public string MidPath { get; }
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
    /// and nothing anywhere declares Landscape or NavigationMeshes, so the precise tier has both a positive
    /// naming two plugins and a negative it must state rather than omit.</summary>
    public FormKey CellF { get; }
    /// <summary>OVERLAPPING — the base declares 3 references and the mid plugin re-declares the FIRST of them
    /// plus one of its own. The union is 4, and a concatenation would say 5.</summary>
    public FormKey CellG { get; }
    /// <summary>OVER THE MEMBER CAP — the base declares 101 references and the mid plugin overrides the cell
    /// declaring none, so the union is 101 against a json member cap of 100.</summary>
    public FormKey CellH { get; }
    public FormKey Topic { get; }
    /// <summary>A 3-toucher record with no child-bearing field at all.</summary>
    public FormKey Weapon { get; }
    /// <summary>REACH — the base holds one block with 3 real cells; the winner holds 2 empty blocks.</summary>
    public FormKey Worldspace { get; }

    public static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

    /// <summary>What <c>CorpusRulebook.CorpusPath</c> named before this world repointed it.</summary>
    readonly string _priorCorpusPath;

    public OwnedChildWorld()
    {
        // CorpusRulebook.CorpusPath is a process-global this world repoints at its own generated corpus.
        // Capture the prior value here so Dispose can put it back: Dispose deletes Root, and a static left
        // naming a path under Root would name a directory that no longer exists.
        _priorCorpusPath = CorpusRulebook.CorpusPath;

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
        CellG = new FormKey(baseKey, 0xC07); CellH = new FormKey(baseKey, 0xC08);
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

            var g = new Cell(CellG, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellG", Flags = Cell.Flag.IsInteriorCell };
            for (int i = 0; i < 3; i++)
                g.Temporary.Add(new PlacedObject(new FormKey(baseKey, (uint)(0xC70 + i)), SkyrimRelease.SkyrimSE) { EditorID = $"HcOcGTemp{i}" });
            FileInterior(m, g);

            var h = new Cell(CellH, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellH", Flags = Cell.Flag.IsInteriorCell };
            for (int i = 0; i < 101; i++)
                h.Temporary.Add(new PlacedObject(new FormKey(baseKey, (uint)(0x1000 + i)), SkyrimRelease.SkyrimSE) { EditorID = $"HcOcHTemp{i}" });
            FileInterior(m, h);

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
        MidPath = Path.Combine(midDir, MidName);
        {
            using var baseOv = SkyrimMod.CreateFromBinaryOverlay(basePath, SkyrimRelease.SkyrimSE);
            var m = new SkyrimMod(midKey, SkyrimRelease.SkyrimSE);
            FileInterior(m, new Cell(CellA, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellA", Flags = Cell.Flag.IsInteriorCell });

            var f = new Cell(CellF, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellF", Flags = Cell.Flag.IsInteriorCell };
            f.Temporary.Add(new PlacedObject(new FormKey(midKey, 0xA60), SkyrimRelease.SkyrimSE) { EditorID = "HcOcMidFTemp0" });
            f.Persistent.Add(new PlacedObject(new FormKey(midKey, 0xA6A), SkyrimRelease.SkyrimSE) { EditorID = "HcOcMidFPers0" });
            FileInterior(m, f);

            // CellH: the mid plugin touches the cell for an unrelated reason and declares nothing, so the union
            // is the base's 101 -- one past the json member cap.
            FileInterior(m, new Cell(CellH, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellH", Flags = Cell.Flag.IsInteriorCell });

            // CellG: the mid plugin RE-DECLARES the base's first reference and adds one of its own, so the union
            // has to be keyed by FormID rather than concatenated.
            var g = new Cell(CellG, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellG", Flags = Cell.Flag.IsInteriorCell };
            g.Temporary.Add(new PlacedObject(new FormKey(baseKey, 0xC70), SkyrimRelease.SkyrimSE) { EditorID = "HcOcGTemp0" });
            g.Temporary.Add(new PlacedObject(new FormKey(midKey, 0xA70), SkyrimRelease.SkyrimSE) { EditorID = "HcOcMidGTemp0" });
            FileInterior(m, g);

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

            // CellF's winner touches the cell and declares nothing, with two lower plugins declaring below it.
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

        // The scan lanes validate against the corpus rulebook, so this world generates one like the others.
        var genDir = Path.Combine(Root, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(Root, "corpus-ref"));
        CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

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
        CorpusRulebook.CorpusPath = _priorCorpusPath;   // before the delete below takes the path it named
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>One world per class — every test below is a read.</summary>
public sealed class OwnedChildFixture : IDisposable
{
    public OwnedChildWorld W { get; } = new();
    public void Dispose() => W.Dispose();
}

/// <summary>
/// The owned-child content annotation, driven through <c>housecarl_records</c>: a parent's child records (a
/// cell's placed references, a topic's INFO lines, a worldspace's cells) are declared per plugin and assembled
/// by the game from every plugin that declares them, so a winner that touches the parent for an unrelated reason
/// reports an empty collection the game fills. Both tiers are covered: the cheap one on the default read, and
/// the precise one on <c>project={"form":"tree"}</c>, the form that fetches every provider body anyway.
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

    // ---- the union: what the game assembles, on every read ----------------------------------------

    /// <summary>#342 in one assertion: the winner's own list reads 0 and the union says the cell holds 3, naming
    /// the plugin that declares them.</summary>
    [Fact]
    public void AChildBearingFieldStatesTheAdditiveUnionTheGameAssembles() =>
        Assert.Contains($"{ReadSentences.UnionLabel}: 3 child record(s) across 1 plugin(s) — {_w.BaseName} 3; "
                        + "this body's own list carries 0", FieldLine(Read(_w.CellA), "Temporary"));

    /// <summary>The value beside the union is still the read body's OWN list, in its own order — those are the
    /// indices a Remove addresses, and a union spliced into them would move them.</summary>
    [Fact]
    public void TheUnionNeverReplacesTheBodysOwnList() =>
        Assert.StartsWith("Temporary = [list: 0 item(s)]", FieldLine(Read(_w.CellA), "Temporary"));

    /// <summary>A child two plugins both declare is ONE child. CellG's mid plugin re-declares the base's first
    /// reference and adds one of its own, so a naive concatenation would say 5 where the game has 4.</summary>
    [Fact]
    public void AChildTwoPluginsBothDeclareIsCountedOnce_NotConcatenated() =>
        Assert.Contains($"{ReadSentences.UnionLabel}: 4 child record(s) across 2 plugin(s) — "
                        + $"{_w.BaseName} 3, {_w.MidName} 2", FieldLine(Read(_w.CellG), "Temporary"));

    /// <summary>A plugin=-scoped read is unioned too, and its "own list" is that plugin's, not the winner's: the
    /// plugins above a base master declare children it cannot see.</summary>
    [Fact]
    public void APluginScopedReadIsUnionedAgainstTheWholeOrder_AndOwnIsThatPluginsOwn() =>
        Assert.Contains("this body's own list carries 3",
                        FieldLine(Read(_w.CellA, source: _w.BaseName), "Temporary"));

    /// <summary>A SINGULAR owned child is not a union — its declarers override one record — so the note says
    /// which plugin's copy is live rather than adding counts that would be a fiction.</summary>
    [Fact]
    public void ASingularChildSaysWhichPluginsCopyIsLive_NeverAUnionCount()
    {
        var line = FieldLine(Read(_w.CellA), "Landscape");
        Assert.Contains($"the live copy is {_w.BaseName}'s", line);
        Assert.DoesNotContain(ReadSentences.UnionLabel, line);
    }

    [Fact]
    public void AFieldNobodyTouchingTheRecordDeclaresSaysSo_NeverSilence() =>
        Assert.Contains(ReadSentences.NoUnionMembers, FieldLine(Read(_w.CellA), "NavigationMeshes"));

    [Fact]
    public void TheDefaultReadDoesNotBorrowTheTreeFormsDeclarersBlock()
    {
        var r = Read(_w.CellA);
        Assert.DoesNotContain(ReadSentences.DeclarersLead, r);
        Assert.DoesNotContain(ReadSentences.DeclaredBy, r);
        Assert.DoesNotContain(ReadSentences.CarriedBy, r);
    }

    /// <summary>The grid is the record type's OWN child-bearing set, so a Mutagen bump that grows it grows this
    /// theory — and it covers fields nobody in this world declares, which is the claim: every child-bearing field
    /// is answered, positive or negative.</summary>
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
        Assert.Contains(ReadSentences.ChildContent, FieldLine(Read(_w.CellA), field));

    [Fact]
    public void TheNotReadClauseIsStatedOnceOverTheWholeResponse_NotOncePerAnnotatedField() =>
        Assert.Equal(1, Occurrences(Read(_w.CellA), ClauseHead(ReadSentences.UnionFraming)));

    [Fact]
    public void TheClauseNamesTheAnnotatedFieldsAndPointsAtNoPositionAtAll()
    {
        var clause = ClauseLine(Read(_w.CellA), ReadSentences.UnionFraming);
        Assert.Contains("Temporary", clause);
        Assert.Contains("Persistent", clause);
        Assert.DoesNotContain("above", clause, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("below", clause, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheClausesFieldNamesAreDerived_EveryFieldItNamesIsOneTheResponseAnnotated()
    {
        var r = Read(_w.CellA);
        var named = NamedFields(ClauseLine(r, ReadSentences.UnionFraming), ReadSentences.UnionFraming);
        Assert.NotEmpty(named);
        foreach (var f in named) Assert.Contains(ReadSentences.ChildContent, FieldLine(r, f));
    }

    [Fact]
    public void ARecordOnlyOnePluginTouchesIsNotAnnotatedAtAll() =>
        Assert.DoesNotContain(ReadSentences.ChildContent, FieldLine(Read(_w.CellC), "Temporary"));

    [Fact]
    public void AProjectionThatRequestsNoChildBearingFieldCarriesNoAnnotationAndNoClause()
    {
        var r = Read(_w.CellA, new RecordsTools.RecordsProject { form = "fields", fields = new[] { "EditorID" } });
        Assert.DoesNotContain(ReadSentences.ChildContent, r);
        Assert.Null(ClauseLineOrNull(r, ReadSentences.UnionFraming));
    }

    [Fact]
    public void AThreeToucherRecordWithNoChildBearingFieldCarriesNoAnnotation()
    {
        var r = Read(_w.Weapon);
        Assert.Contains("winner=" + _w.TopName, r);
        Assert.DoesNotContain(ReadSentences.ChildContent, r);
    }

    /// <summary>The base declares two INFOs and the winner re-declares the first, so the union is 2 — the same
    /// FormID-keyed rule on a second record type, from the same derived field set.</summary>
    [Fact]
    public void ADialogTopicsResponsesIsUnioned_TheFieldSetIsDerivedNotAListOfCellFields() =>
        Assert.Contains($"{ReadSentences.UnionLabel}: 2 child record(s) across 2 plugin(s)",
                        FieldLine(Read(_w.Topic), "Responses"));

    /// <summary>A worldspace's cells sit two container levels down (block → sub-block → cell) and each cell holds
    /// references of its own. The union is the three CELLS the base declares, never the contents of those cells:
    /// the walk stops at the first record level.</summary>
    [Fact]
    public void AWorldspacesSubCellsIsUnionedToItsCells_NotToWhatThoseCellsContain() =>
        Assert.Contains($"{ReadSentences.UnionLabel}: 3 child record(s) across 1 plugin(s) — {_w.BaseName} 3",
                        FieldLine(Read(_w.Worldspace), "SubCells"));

    /// <summary>A nested field's VALUE counts its containers and its union counts the records under them. The
    /// winner here declares two EMPTY blocks, so the line carries "2 item(s)" and an own share of 0 at once —
    /// true of two different units, and a contradiction unless the note says which it is counting.</summary>
    [Fact]
    public void ANestedFieldsNoteNamesItsUnit_TheValueCountsContainersAndTheUnionCountsRecords()
    {
        var line = FieldLine(Read(_w.Worldspace), "SubCells");
        Assert.StartsWith("SubCells = [list: 2 item(s)]", line);
        Assert.Contains("this body declares 0 of them", line);
        Assert.Contains("counts the CONTAINERS holding them", line);
        // The flat wording would put "own list carries 0" beside a value of 2 with nothing saying they differ.
        Assert.DoesNotContain("own list carries", line);
    }

    /// <summary>And the flat shape keeps the plain wording: a cell's Temporary IS the list of its children, so
    /// the value and the own share are one unit and the note says so without a caveat.</summary>
    [Fact]
    public void AFlatFieldsNoteStillReadsAsAShareOfTheValueBesideIt()
    {
        var line = FieldLine(Read(_w.CellG), "Temporary");
        Assert.StartsWith("Temporary = [list: 2 item(s)]", line);
        Assert.Contains("this body's own list carries 2", line);
        Assert.DoesNotContain("CONTAINERS", line);
    }

    /// <summary>json carries the unit as a key rather than only in the prose, because a consumer comparing
    /// <c>own</c>/<c>total</c> against <c>value</c> has no sentence to read.</summary>
    [Fact]
    public void JsonMarksTheNestedFieldAndLeavesTheFlatOneUnmarked()
    {
        Assert.True(UnionKey(Read(_w.Worldspace, format: "json"), "SubCells", "nested")?.GetBoolean());
        Assert.Null(UnionKey(Read(_w.CellG, format: "json"), "Temporary", "nested"));
    }

    // ---- what a batch of named records opens ------------------------------------------------------

    /// <summary>The union opens a body per touching plugin, so a `formids=` batch used to re-mmap every toucher
    /// once per row — the session that caches overlays died with each record, and the union memo dedupes a
    /// repeated FormID, not a repeated plugin. One session for the call bounds the opens by the ORDER's size
    /// instead of by the row count.</summary>
    [Fact]
    public void ABatchOpensEachPluginOnce_NotOncePerRecordItUnions()
    {
        var before = LoadOrderResolver.SessionOverlayOpens;
        ReadBoth(_w.CellA, _w.CellF);           // two cells whose touchers overlap; three plugins in the order
        var opens = LoadOrderResolver.SessionOverlayOpens - before;

        Assert.True(opens <= 3, $"a two-record batch paid {opens} overlay opens over a three-plugin order — " +
                                "the session is not shared across the batch's records");
        // And the answers are the ones the per-record sessions gave: a shared overlay cache is a cost change.
        Assert.Contains(ReadSentences.UnionLabel, FieldLine(ReadBoth(_w.CellA, _w.CellF), "Temporary"));
    }

    /// <summary>A single named record still opens its own session and closes it — the batch's cache is the
    /// batch's, and nothing is held between calls.</summary>
    [Fact]
    public void ASingleReadStillPaysItsOwnOpens()
    {
        var before = LoadOrderResolver.SessionOverlayOpens;
        Read(_w.CellF);
        Assert.True(LoadOrderResolver.SessionOverlayOpens > before,
                    "a read that unions three touchers opened no overlay at all");
    }

    [Fact]
    public void AtDepthTwoTheContainersOwnSummaryLineStillCarriesTheAnnotation() =>
        Assert.Contains(ReadSentences.ChildContent,
                        FieldLine(Read(_w.CellA, new RecordsTools.RecordsProject { form = "everything", depth = 2 }), "Temporary"));

    [Fact]
    public void TwoAnnotatedRecordsInOneResponseStillStateTheClauseOnce() =>
        Assert.Equal(1, Occurrences(ReadBoth(_w.CellA, _w.CellB), ClauseHead(ReadSentences.UnionFraming)));

    [Fact]
    public void AResponseWithNothingAnnotatedCarriesNoClause() =>
        Assert.Equal(0, Occurrences(Read(_w.Weapon), ClauseHead(ReadSentences.UnionFraming)));

    // ---- emission: the clause is earned by a field LINE, not by the decision to annotate ----------

    [Fact]
    public void ACapThatTruncatesTheAnnotatedFieldAwayStatesNoClauseOverIt()
    {
        var r = Read(_w.CellA, maxChars: 300);
        Assert.Contains("truncated: showing", r);
        Assert.DoesNotContain(ReadSentences.ChildContent, r);
        Assert.Null(ClauseLineOrNull(r, ReadSentences.UnionFraming));
    }

    [Fact]
    public void TheSameReadWithRoomForTheAnnotatedFieldStatesTheClauseOverIt()
    {
        var r = Read(_w.CellA);
        Assert.Contains(ReadSentences.ChildContent, r);
        Assert.NotNull(ClauseLineOrNull(r, ReadSentences.UnionFraming));
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
        Assert.NotNull(ClauseLineOrNull(r, ReadSentences.UnionFraming));
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
        Assert.StartsWith(ClauseHead(ReadSentences.UnionFraming), s);
        Assert.Contains("Temporary", NamedFields(s, ReadSentences.UnionFraming));
    }

    [Fact]
    public void Json_TheClauseIsWrittenAfterTheFieldsArrayItIsAboutNeverAheadOfIt()
    {
        var r = Read(_w.CellA, format: "json");
        int note = r.IndexOf("\"owned_child_note\"", StringComparison.Ordinal);
        int fields = r.IndexOf("\"fields\"", StringComparison.Ordinal);
        Assert.True(note > fields, $"note-at={note} fields-at={fields}");
    }

    /// <summary>The union is RETURNED, not only described: the member FormIDs ride the field object, so a caller
    /// reads them back through the same <c>formids=</c> door it came in by.</summary>
    [Fact]
    public void Json_TheUnionsMembersAreTheFormIdsOfTheChildrenTheGameAssembles()
    {
        using var doc = JsonDocument.Parse(Read(_w.CellA, format: "json"));
        var u = doc.RootElement.GetProperty("records")[0].GetProperty("fields").EnumerateArray()
                   .Single(f => f.GetProperty("path").GetString() == "Temporary")
                   .GetProperty("owned_child_union");
        Assert.Equal(3, u.GetProperty("total").GetInt32());
        Assert.Equal(0, u.GetProperty("own").GetInt32());
        Assert.Equal(new[] { "000C10", "000C11", "000C12" }.Select(h => $"{h}:{_w.BaseName}"),
                     u.GetProperty("members").EnumerateArray().Select(m => m.GetString()));
    }

    [Fact]
    public void Json_TheOverlappingUnionListsEachChildOnce()
    {
        using var doc = JsonDocument.Parse(Read(_w.CellG, format: "json"));
        var u = doc.RootElement.GetProperty("records")[0].GetProperty("fields").EnumerateArray()
                   .Single(f => f.GetProperty("path").GetString() == "Temporary")
                   .GetProperty("owned_child_union");
        Assert.Equal(4, u.GetProperty("total").GetInt32());
        Assert.Equal(4, u.GetProperty("members").GetArrayLength());
    }

    [Fact]
    public void Json_TheAnnotationsPerFieldHalfRidesDisplay() =>
        Assert.Contains(ReadSentences.ChildContent, JsonField(Read(_w.CellA, format: "json"), "Temporary", "display"));

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
                             && d.GetString()!.Contains(ReadSentences.ChildContent, StringComparison.Ordinal));
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
        Assert.Contains("Temporary", NamedFields(note, ReadSentences.UnionFraming));
        Assert.DoesNotContain("above", note, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("below", note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Artifact_TheManifestOnlyResponseDoesNotStateAClauseOverRowsItDidNotRender() =>
        Assert.Null(ClauseLineOrNull(Read(_w.CellA, toFile: _w.Scratch("inline.jsonl")), ReadSentences.UnionFraming));

    // ---- the member sample: what json lists, and what it counts instead ---------------------------

    static JsonElement Union(string json, FormKey _, string field)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("records")[0].GetProperty("fields").EnumerateArray()
                  .Single(f => f.GetProperty("path").GetString() == field)
                  .GetProperty("owned_child_union").Clone();
    }

    /// <summary>A union past the cap lists a SAMPLE and counts the rest — the array is never the whole set, and a
    /// caller who round-trips it can see from members_omitted that it is not.</summary>
    [Fact]
    public void Json_AUnionPastTheMemberCapListsASampleAndCountsWhatItLeftOut()
    {
        var u = Union(Read(_w.CellH, format: "json"), _w.CellH, "Temporary");
        Assert.Equal(101, u.GetProperty("total").GetInt32());
        Assert.Equal(100, u.GetProperty("members").GetArrayLength());
        Assert.Equal(1, u.GetProperty("members_omitted").GetInt32());
    }

    /// <summary>The sample is bounded by what is LEFT of max_chars too. A hundred FormKey strings is ~3KB, so a
    /// field object that only obeyed the flat cap could double a small response before the field loop noticed.</summary>
    [Fact]
    public void Json_TheMemberSampleShrinksToTheRemainingBudget_AndTheOmissionIsStillCounted()
    {
        var u = Union(Read(_w.CellH, new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Temporary" } },
                           format: "json", maxChars: 2000), _w.CellH, "Temporary");
        int listed = u.GetProperty("members").GetArrayLength();
        Assert.InRange(listed, 0, 99);
        Assert.Equal(101 - listed, u.GetProperty("members_omitted").GetInt32());
    }

    /// <summary>An additive union has no single live declarer — every declarer's children are live — so the json
    /// names the highest declarer as that, and keeps live_plugin for the SINGULAR shape where it is true.</summary>
    [Fact]
    public void Json_ACollectionNamesItsHighestDeclarer_AndOnlyASingularNamesALivePlugin()
    {
        var json = Read(_w.CellA, format: "json");
        var coll = Union(json, _w.CellA, "Temporary");
        Assert.Equal(_w.BaseName, coll.GetProperty("highest_declarer").GetString());
        Assert.False(coll.TryGetProperty("live_plugin", out _));
        var sing = Union(json, _w.CellA, "Landscape");
        Assert.Equal(_w.BaseName, sing.GetProperty("live_plugin").GetString());
        Assert.False(sing.TryGetProperty("highest_declarer", out _));
    }

    // ---- a plugin the union could not open --------------------------------------------------------

    /// <summary>A sibling plugin holding an exclusive handle (xEdit, MO2, an AV scan) must not fault the read: the
    /// subject's own body answers, and the plugin that would not open is NAMED beside the union rather than
    /// counted into it as declaring nothing.</summary>
    [Fact]
    public void ASiblingPluginThatWillNotOpenIsNamedBesideTheUnion_NeverFatalAndNeverANegative()
    {
        string line;
        using (new FileStream(_w.MidPath, FileMode.Open, FileAccess.Read, FileShare.None))
            line = FieldLine(Read(_w.CellF), "Temporary");
        Assert.Contains(ReadSentences.CouldNotRead, line);
        Assert.Contains(_w.MidName, line);
        // The base's declaration still counted: the read degraded by one plugin, and said so.
        Assert.Contains(ReadSentences.UnionLabel, line);
    }

    /// <summary>And the same read once the handle is gone: the plugin is back in the union, so the arm above is a
    /// statement about THAT read, not a sticky verdict.</summary>
    [Fact]
    public void TheSameReadAfterTheHandleIsReleasedNamesNothingUnreadable() =>
        Assert.DoesNotContain(ReadSentences.CouldNotRead, FieldLine(Read(_w.CellF), "Temporary"));

    // ---- the scan lanes: annotated, but index-only --------------------------------------------------

    string ScanCells(string? format = null) =>
        RecordsTools.Records(Svc, types: new[] { "CELL" },
                             project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Temporary" } },
                             format: format);

    /// <summary>A scan discovers its row count, so it does not open a body per touching plugin per row. It still
    /// annotates — the field is a false-empty either way — with the index-only note.</summary>
    [Fact]
    public void AScanStatesTheIndexOnlyNote_NotTheUnionItWouldPayPerRowFor()
    {
        var r = ScanCells();
        Assert.Contains(ReadSentences.NotRead, r);
        // The clause's remedy NAMES the union, so the absent thing is the union NOTE — a label with a count after it.
        Assert.DoesNotContain(ReadSentences.UnionLabel + ":", r);
    }

    /// <summary>And the clause over those rows is the index-only tier's, naming the lane that DOES assemble the
    /// union — a scan must not ship a sentence describing a quantity its rows do not carry.</summary>
    [Fact]
    public void AScansClauseIsTheIndexOnlyOneAndNamesTheFormidsLane()
    {
        using var doc = JsonDocument.Parse(ScanCells("json"));
        var note = doc.RootElement.GetProperty("owned_child_note").GetString()!;
        Assert.StartsWith(ClauseHead(ReadSentences.NotReadFraming), note);
        Assert.Contains("formids=", note);
    }

    /// <summary>The same cell named by formid IS unioned, so the two lanes differ by what the caller asked for,
    /// not by what is true.</summary>
    [Fact]
    public void TheSameCellNamedByFormidIsUnioned() =>
        Assert.Contains(ReadSentences.UnionLabel, FieldLine(Read(_w.CellA), "Temporary"));

    // ---- the remedy the clause names ------------------------------------------------------------
    //
    // The clause tells the caller where to get the read this one did not do: the same formids under
    // project={"form": "tree"}. These tests MAKE that call, so the remedy is pinned by what comes back rather
    // than by the wording. One test per promise the sentence makes.

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
        var named = NamedFields(ClauseLine(Read(_w.CellA), ReadSentences.UnionFraming), ReadSentences.UnionFraming);
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

    // ---- the precise tier the remedy carries -------------------------------------------------------
    //
    // The cheap tier says "other plugins touch this record and their declarations were not read". The tree
    // form has already read them, so it says WHICH — and, the half no cheap tier can reach, that NONE do.
    // Each test below pins a WHOLE rendered line composed from the sentence consts and the fixture's own
    // plugin names, so a second branch of DeclarersNote cannot satisfy it.

    [Fact]
    public void ThePreciseTierNamesEveryProviderDeclaringInACollectionField() =>
        Assert.Contains($"Temporary: {ReadSentences.DeclaredBy} {_w.BaseName}, {_w.MidName}",
                        DeclarersBlock(Tree(_w.CellF)));

    /// <summary>The whole block for the two-declarer cell in one assertion: two collection fields naming both
    /// lower plugins, and two fields nobody declares stating so — Landscape in the SINGULAR voice (a count, never
    /// the collection negative's plural), NavigationMeshes in the collection one. A tier that emitted only the
    /// positives, or only the fields it had something to say about, fails here.</summary>
    [Fact]
    public void ThePreciseTierStatesEveryChildBearingFieldOfTheType_PositiveAndNegativeAlike() =>
        Assert.Equal(new[]
        {
            $"Landscape: {ReadSentences.CarriedBy} 0 provider(s)",
            $"NavigationMeshes: {ReadSentences.NoDeclarers}",
            $"Persistent: {ReadSentences.DeclaredBy} {_w.BaseName}, {_w.MidName}",
            $"Temporary: {ReadSentences.DeclaredBy} {_w.BaseName}, {_w.MidName}",
        }, DeclarersBlock(Tree(_w.CellF)));

    /// <summary>The SINGULAR negative on its own: a count of zero, never the collection voice's plural claim,
    /// which is false about a singular child whether the answer is empty or not.</summary>
    [Fact]
    public void ASingularFieldNobodyCarriesIsCountedZero_NeverTheCollectionVoice()
    {
        var line = DeclarersBlock(Tree(_w.CellF)).Single(l => l.StartsWith("Landscape: ", StringComparison.Ordinal));
        Assert.Equal($"Landscape: {ReadSentences.CarriedBy} 0 provider(s)", line);
        Assert.DoesNotContain("child records", line);
    }

    /// <summary>The negative on its own, and the claim is that it is a SENTENCE: silence here is
    /// indistinguishable to a caller from the tier not having run.</summary>
    [Fact]
    public void AFieldNoProviderDeclaresInGetsTheNoneSentence_NeverSilence() =>
        Assert.Contains($"NavigationMeshes: {ReadSentences.NoDeclarers}", DeclarersBlock(Tree(_w.CellF)));

    /// <summary>The SINGULAR case: Cell.Landscape is ONE record its providers override, so the line is a COUNT.
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

    /// <summary>The cheap tier's clause tells a caller the tree form names the declarers. This MAKES that call, on
    /// the fields the clause itself named, so the promise is pinned by what returns, not by the wording.</summary>
    [Fact]
    public void TheRemedyNamedByTheCheapClauseAnswersPreciselyForEveryFieldTheClauseNamed()
    {
        var named = NamedFields(ClauseLine(Read(_w.CellF), ReadSentences.UnionFraming), ReadSentences.UnionFraming);
        Assert.NotEmpty(named);
        var block = DeclarersBlock(Tree(_w.CellF));
        foreach (var f in named)
            Assert.Contains(block, l => l.StartsWith(f + ": ", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDefaultReadOfTheSameCellUnionsItWithoutBorrowingTheTreesBlock()
    {
        var r = Read(_w.CellF);
        Assert.Contains($"{ReadSentences.UnionLabel}: 3 child record(s) across 2 plugin(s)", FieldLine(r, "Temporary"));
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

    // ---- the declarers block respects max_chars like every other row content ------------------------------
    //
    // A sole-provider row has nothing to diff, so nothing else in the row loop notices an overrun: without a cap
    // check in AppendChildDeclarers/WriteTreeRow the block returns its full text with truncated=false, and the
    // auto-spill that exists to make an over-cap answer complete never fires.

    [Fact]
    public void ATextRowsDeclarersBlockAloneCanTripMaxChars_AndTheResponseIsMarkedTruncated()
    {
        // CellC is a SOLE-toucher row: row.Nodes.Count <= 1, so the diff loop (which has its own cap check) never
        // runs, leaving the block-to-end-of-row stretch as the only path a cap check has to cover.
        var r = Tree(_w.CellC, maxChars: 200);
        Assert.Contains("spilled: complete result", r);
    }

    [Fact]
    public void Json_ATextRowsDeclarersBlockAloneCanTripMaxChars_AndTheResponseIsMarkedTruncated()
    {
        // 400 is past the envelope + row header + declarers block, so WriteTreeRow's cap check fires and cuts
        // mid-row with an inline "[child declarers cut …]" note, and short of the whole row (1911 chars uncapped
        // in json) — the shape where the response-level truncated flag has to agree with the row's own note.
        using var doc = JsonDocument.Parse(Tree(_w.CellC, format: "json", maxChars: 400));
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("spilled", out _));
    }

    [Fact]
    public void ARowWithRoomForItsDeclarersBlockIsNotMarkedTruncatedByIt()
    {
        // The control: the same sole-provider shape at a cap generous enough that nothing spills, so the cap
        // check is shown to fire only on an over-cap row.
        var r = Tree(_w.CellC, maxChars: 4000);
        Assert.DoesNotContain("spilled:", r);
    }

    // ---- the block's own TAIL, not just its per-line checks -------------------------------------------------
    //
    // The per-field checks run BEFORE each line and never after the last one, so a final line that pushes
    // sb.Length past the cap goes unnoticed — nothing downstream catches it once the row ends there.
    //
    // The tail-trip case is driven on CellF (3 touchers), not CellC: `truncated` says the ANSWER is incomplete
    // and drives the spill, so it is set at the tail only for a row that LOST something — the diff a
    // multi-provider row never reached. CellC at the same tail loses nothing and is the DoesNotContain control
    // two tests down (ASoleProviderRowWhoseCompleteBlockEndsPastTheCapClaimsNothingWasCut).

    [Fact]
    public void ATextRowsDeclarersBlockTailAloneCanTripMaxChars_AndTheResponseIsMarkedTruncated() =>
        Assert.Contains("spilled: complete result", Tree(_w.CellF, maxChars: 830));

    [Fact]
    public void ARowWhoseDeclarersBlockFitsExactlyAtTheTailIsNotMarkedTruncated() =>
        Assert.DoesNotContain("spilled:", Tree(_w.CellC, maxChars: 846));

    /// <summary>When the block is cut on a multi-provider row the row stops there: no "diff (field deltas…):"
    /// header may follow for a section the cap already forbade. The row does carry the "[nodes cut" notice — the
    /// diff it never reached is a real loss, and each notice claims one thing.</summary>
    [Fact]
    public void ACutDeclarersBlockEndsTheRow_NoEmptyDiffHeaderOverASectionThatNeverRendered()
    {
        var r = Tree(_w.CellF, maxChars: 600);
        Assert.Contains("[child declarers cut", r);
        Assert.DoesNotContain("diff (field deltas", r);
        Assert.Contains("[nodes cut at max_chars=600", r);
    }

    // ---- what the tail cut notice CLAIMS, not just that the tail check exists ------------------------------
    //
    // The tail is reachable only when the field loop ran to completion, so at the tail nothing in the block was
    // ever cut. A "[child declarers cut …]" notice there is false in every case it can fire, and its remedy
    // (project.fields=) points at narrowing a block that is already complete. What the row loses at the tail is
    // its DIFF — or, on a sole-provider row, nothing at all.

    [Fact]
    public void ASoleProviderRowWhoseCompleteBlockEndsPastTheCapClaimsNothingWasCut()
    {
        var r = Tree(_w.CellC, maxChars: 845);
        Assert.Contains("Temporary: ", r);              // the block ran to completion…
        Assert.Contains("NavigationMeshes: ", r);
        Assert.DoesNotContain("[child declarers cut", r);   // …so nothing may say it was cut,
        Assert.DoesNotContain("[nodes cut", r);             // and a sole provider loses no diff either.
        // …and nothing else may say it either: `truncated` reaches TreeResponse, which writes a JSONL artifact
        // and re-renders with "spilled: complete result". A row that lost nothing does not spill.
        Assert.DoesNotContain("spilled", r);
    }

    [Fact]
    public void AMultiProviderRowWhoseCompleteBlockEndsPastTheCapNamesTheDiffItLost()
    {
        var r = Tree(_w.CellF, maxChars: 830);
        Assert.Contains("Temporary: ", r);
        Assert.Contains("NavigationMeshes: ", r);
        Assert.DoesNotContain("[child declarers cut", r);
        Assert.Contains("[nodes cut at max_chars=830", r);   // what actually went
        Assert.DoesNotContain("diff (field deltas", r);      // and no header over a section that never rendered
    }

    /// <summary>The other side: when declarer lines really ARE dropped, the notice still says so. 660 cuts CellF's
    /// block after its first field line (Landscape), leaving the other three unwritten. A multi-provider row that
    /// stops there loses its DIFF as well, so it names BOTH — the declarers it dropped and the nodes it never
    /// reached. Each notice claims one thing; neither claims the other's loss.</summary>
    [Fact]
    public void ABlockCutMidWayStillSaysTheDeclarersWereCut()
    {
        var r = Tree(_w.CellF, maxChars: 660);
        Assert.Contains("[child declarers cut at max_chars=660", r);
        Assert.Contains("Landscape: ", r);
        Assert.DoesNotContain("NavigationMeshes: ", r);
        Assert.Contains("[nodes cut at max_chars=660", r);
    }

    /// <summary>The SOLE-provider control for the same cut branch: CellC at 700 drops declarer lines (its block
    /// runs 588-805 before completing), so the declarers notice fires — and there is no diff to lose, so the
    /// nodes notice must NOT — which pins each notice to the row's actual loss rather than to the branch it
    /// came back through.</summary>
    [Fact]
    public void ASoleProviderRowCutMidBlockSaysTheDeclarersWereCutAndNamesNoDiff()
    {
        var r = Tree(_w.CellC, maxChars: 700);
        Assert.Contains("[child declarers cut at max_chars=700", r);
        Assert.Contains("NavigationMeshes: ", r);        // the block got two of its four field lines out…
        Assert.DoesNotContain("Temporary: ", r);         // …and was cut before the rest,
        Assert.DoesNotContain("[nodes cut", r);          // with no diff to lose.
    }

    /// <summary>The block's OTHER early return — the framing reserve, which returns before a single declarer line
    /// is written — comes back through the same caller line, so a multi-provider row refused the framing carries
    /// both notices too. On CellF, 625 is the last cap that refuses the framing line and 626 the first it fits
    /// inside.</summary>
    [Fact]
    public void TheFramingReserveBranchOnAMultiProviderRowNamesBothTheDeclarersAndTheDiff()
    {
        var r = Tree(_w.CellF, maxChars: 625);
        Assert.DoesNotContain(ReadSentences.DeclarersLead, r);        // not one declarer line was written…
        Assert.Contains("[child declarers cut at max_chars=625", r);  // …which the block says,
        Assert.Contains("[nodes cut at max_chars=625", r);            // …and the diff loss the caller says.
    }

    // ---- the framing line is RESERVED against max_chars, not written and regretted -------------------------
    //
    // The block's framing line is invariant text of a known length, so checking only sb.Length < cap before it
    // writes its whole length past the cap with nothing able to take it back — json reserves the identical
    // sentence (JsonWire's DeclarersLeadReserve) and the cheap tier reserves its own clause (ClauseReserve).
    // On CellC the block starts at 294 chars, so 587 is the last cap the framing does not fit in and 588 the
    // first that it does.

    [Fact]
    public void TheFramingLineIsReservedAgainstMaxChars_NotWrittenPastIt()
    {
        var r = Tree(_w.CellC, maxChars: 587);
        Assert.DoesNotContain(ReadSentences.DeclarersLead, r);
        Assert.Contains("[child declarers cut at max_chars=587", r);
    }

    [Fact]
    public void TheFramingLineRidesAtTheFirstCapItFitsInside()
    {
        Assert.Contains(ReadSentences.DeclarersLead, Tree(_w.CellC, maxChars: 588));
    }

    /// <summary>The lead is invariant framing text, not per-record content, so a multi-row response states it once
    /// — matching json's `child_declarers_note` and the artifact's manifest note, which never repeated it per row
    /// at all. Both CellA and CellF carry declarers, so a per-row repeat would
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

    /// <summary>The row that did NOT carry the full lead still labels its own block — an unlabelled set of field
    /// lines flush against the numbered toucher list above it is indistinguishable from more touchers. The
    /// label is a SHORT one; the full shape explanation is not repeated.</summary>
    [Fact]
    public void TheSecondRowsBlockCarriesTheShortHeaderNotTheFullLeadAgain()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { OwnedChildWorld.Fid(_w.CellA), OwnedChildWorld.Fid(_w.CellF) },
                                     project: new RecordsTools.RecordsProject { form = "tree" });
        Assert.Equal(1, Occurrences(r, ReadSentences.DeclarersLead));
        Assert.Equal(1, Occurrences(r, ReadSentences.DeclarersHeader));
        Assert.True(r.IndexOf(ReadSentences.DeclarersLead, StringComparison.Ordinal)
                    < r.IndexOf(ReadSentences.DeclarersHeader, StringComparison.Ordinal));
    }

    // ---- the new remedy sentences never name a lever housecarl_records lacks -------------------------------
    //
    // RecordsRemedyGrammarTests' harvest (RemedyHarvest.cs) reaches the tree form only through RecordsWorld,
    // which carries no child-bearing record type — WEAP owns no children — so the "[child declarers cut ...]"
    // notices never reach the wrong-lever grid there. RecordsWorld is a frozen shared fixture, so the same check
    // runs here instead, against a fixture that has a child-bearing type.

    [Fact]
    public void TheChildDeclarersCutNoticesNameNoWrongLever()
    {
        var text = Tree(_w.CellC, maxChars: 200);
        var jsonR = Tree(_w.CellC, format: "json", maxChars: 300);

        var textHits = text.Split('\n').Where(l => RemedyHarvest.RemedyLine.IsMatch(l)).ToList();
        var jsonHits = RemedyHarvest.HarvestAllStrings(jsonR);

        Assert.Contains(textHits, h => h.Contains("child declarers cut", StringComparison.Ordinal));
        Assert.Contains(jsonHits, h => h.Contains("child declarers cut", StringComparison.Ordinal));

        foreach (var (pattern, claim) in RemedyHarvest.WrongLevers)
        {
            Assert.DoesNotContain(textHits, h => System.Text.RegularExpressions.Regex.IsMatch(h, pattern));
            Assert.DoesNotContain(jsonHits, h => System.Text.RegularExpressions.Regex.IsMatch(h, pattern));
        }
    }

    // ---- WriteTreeRow's bool return, isolated from the declarers path ---------------------------------------
    //
    // On a row WITH declarers, the response-level lead-reserve check covers for a missing return value, so the
    // existing cap tests pass whether or not WriteTreeRow's own return is plumbed into rowsTruncated. WEAP has
    // no child-bearing fields at all — anyDeclarers is false, so this test depends ONLY on the nodes-branch
    // return value plumbed into rowsTruncated, with nothing else able to cover for it.

    [Fact]
    public void Json_ANoChildrenRowsNodesCutStillSetsTruncated_NotCoveredByTheDeclarersLeadCheck()
    {
        using var doc = JsonDocument.Parse(Tree(_w.Weapon, format: "json", maxChars: 1077));
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("spilled", out _));
    }

    [Fact]
    public void Json_TheSameNoChildrenRowFitsWithRoomToSpareIsNotMarkedTruncated()
    {
        using var doc = JsonDocument.Parse(Tree(_w.Weapon, format: "json", maxChars: 1078));
        Assert.False(doc.RootElement.GetProperty("truncated").GetBoolean());
    }

    // ---- the block's fields= narrowing -----------------------------------------------------------------------
    //
    // Nothing else in the suite pins the fields= filter in ResolveTreePinned's `wanted` derivation: deleting it
    // reddens only what follows.

    [Fact]
    public void FieldsNarrowsTheBlockToTheNamedTopLevelField_NotTheWholeType()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { OwnedChildWorld.Fid(_w.CellF) },
                                     project: new RecordsTools.RecordsProject { form = "tree", fields = new[] { "Persistent" } });
        Assert.Contains($"Persistent: {ReadSentences.DeclaredBy} {_w.BaseName}, {_w.MidName}", r);
        Assert.DoesNotContain("Temporary:", r);
        Assert.DoesNotContain("Landscape:", r);
        Assert.DoesNotContain("NavigationMeshes:", r);
    }

    /// <summary>A path INSIDE a child-bearing field narrows the block to NOTHING: it matches the caller's request
    /// by NAME, not by the path the response emitted.</summary>
    [Fact]
    public void FieldsWithABracketedPathInsideAChildBearingFieldYieldsNoBlockAtAll()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { OwnedChildWorld.Fid(_w.CellF) },
                                     project: new RecordsTools.RecordsProject { form = "tree", fields = new[] { "Temporary[0]" } });
        Assert.DoesNotContain(ReadSentences.DeclarersLead, r);
        Assert.DoesNotContain("Temporary:", r);
    }

    // ---- the overflow remedy is TEXT-only; json and the artifact already carry every name -------------------
    //
    // The fixture's widest COLLECTION field has 2 declarers; DeclarerNameCap is 3, so no live read ever crosses
    // it. AppendChildDeclarers and JsonWire.WriteTreeRow are internal for exactly this — driven directly against
    // a hand-built TreeRow with 5 declarers on one field, no MO2 fixture extension needed.

    static LoadOrderService.TreeRow FiveDeclarerRow() => new(
        "000001:Test.esm", "Cell", "TestCell",
        new[] { "A.esp", "B.esp", "C.esp", "D.esp", "E.esp" }, "E.esp",
        new[] { new LoadOrderService.TreeNodeDelta("E.esp", true, true, Array.Empty<string>(), 0, true, null) },
        null,
        new[] { new ChildDeclarers("Persistent", OwnedChildShape.Collection,
                                   new[] { "A.esp", "B.esp", "C.esp", "D.esp", "E.esp" }, Array.Empty<string>()) });

    [Fact]
    public void TheOverflowRemedyNamesFormatJson_TextOnly()
    {
        var sb = new StringBuilder();
        bool leadWritten = false;
        RecordsTools.AppendChildDeclarers(sb, FiveDeclarerRow(), cap: 100_000, ref leadWritten, out _);
        Assert.Contains(
            $"Persistent: {ReadSentences.DeclaredBy} A.esp, B.esp, C.esp (+2 more){ReadSentences.DeclarersOverflowRemedy}",
            sb.ToString());
    }

    [Fact]
    public void Json_TheOverflowIsNeverCapped_AndCarriesNoRemedyText()
    {
        var row = FiveDeclarerRow();
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
            JsonWire.WriteTreeRow(w, row, ms, cap: 100_000);
        using var doc = JsonDocument.Parse(ms.ToArray());
        var field = doc.RootElement.GetProperty("child_declarers").EnumerateArray().Single();
        Assert.Equal(new[] { "A.esp", "B.esp", "C.esp", "D.esp", "E.esp" },
                     field.GetProperty("declaring").EnumerateArray().Select(x => x.GetString()));
        Assert.DoesNotContain("format=json", field.GetProperty("note").GetString());
    }

    // ---- …and the UNREADABLE half of the same sentence elides the same way, so it gets the same remedy -------
    //
    // DeclarersNote drops unreadable names past DeclarerNameCap behind a bare ", …", on a field of EITHER shape.
    // The remedy has to be appended for the unreadable elision as well as a COLLECTION field's `declaring`
    // overflow: a SINGULAR field (Cell.Landscape) never reaches the Collection guard, so gating on it alone
    // leaves that elision with no pointer at all.

    static LoadOrderService.TreeRow UnreadableRow(OwnedChildShape shape, int declaring, int unreadable) => new(
        "000001:Test.esm", "Cell", "TestCell",
        new[] { "A.esp", "B.esp", "C.esp", "D.esp", "E.esp" }, "E.esp",
        new[] { new LoadOrderService.TreeNodeDelta("E.esp", true, true, Array.Empty<string>(), 0, true, null) },
        null,
        new[] { new ChildDeclarers("Landscape", shape,
                                   Names("D", declaring), Names("U", unreadable)) });

    static string[] Names(string prefix, int n) =>
        Enumerable.Range(1, n).Select(i => $"{prefix}{i}.esp").ToArray();

    static string RenderRow(LoadOrderService.TreeRow row)
    {
        var sb = new StringBuilder();
        bool leadWritten = false;
        RecordsTools.AppendChildDeclarers(sb, row, cap: 100_000, ref leadWritten, out _);
        return sb.ToString();
    }

    /// <summary>A SINGULAR field — where the Collection guard never fires at all — whose unreadable list overflows
    /// still points the text reader at the medium carrying the elided names.</summary>
    [Fact]
    public void TheOverflowRemedyAlsoNamesFormatJsonForAnUnreadableOverflow_SingularShape()
    {
        var r = RenderRow(UnreadableRow(OwnedChildShape.Singular, declaring: 0, unreadable: 5));
        Assert.Contains(", …)", r);                                            // names were elided…
        Assert.Equal(1, Occurrences(r, ReadSentences.DeclarersOverflowRemedy)); // …and exactly one pointer follows.
    }

    /// <summary>Both clauses overflowing is still ONE remedy on the line — the pointer is the same pointer, and
    /// naming it twice would read as two different answers.</summary>
    [Fact]
    public void BothOverflowsOnOneLineStillCarryASingleRemedy()
    {
        var r = RenderRow(UnreadableRow(OwnedChildShape.Collection, declaring: 5, unreadable: 5));
        Assert.Contains("(+2 more)", r);                                       // the declaring half elided…
        Assert.Contains(", …)", r);                                            // …and the unreadable half too,
        Assert.Equal(1, Occurrences(r, ReadSentences.DeclarersOverflowRemedy)); // …with one pointer, not two.
    }

    /// <summary>json's twin: the same row carries every unreadable name in its own array and no remedy text, so
    /// the pointer the text lane appends is always followable and never duplicated into the medium it names.</summary>
    [Fact]
    public void Json_TheUnreadableOverflowIsNeverCapped_AndCarriesNoRemedyText()
    {
        var row = UnreadableRow(OwnedChildShape.Collection, declaring: 5, unreadable: 5);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
            JsonWire.WriteTreeRow(w, row, ms, cap: 100_000);
        using var doc = JsonDocument.Parse(ms.ToArray());
        var field = doc.RootElement.GetProperty("child_declarers").EnumerateArray().Single();
        Assert.Equal(Names("U", 5), field.GetProperty("unreadable").EnumerateArray().Select(x => x.GetString()));
        Assert.DoesNotContain("format=json", field.GetProperty("note").GetString());
    }

    // ---- json's response-level lead respects cap too, not just the row-level array --------------------------
    //
    // A Utf8JsonWriter cannot un-write child_declarers_note once appended, so the cap is checked BEFORE deciding
    // to write it, and against every byte that still lands afterwards: the note's own cost
    // (DeclarersLeadReserve), the `truncated` boolean written between the check and the note
    // (TruncatedPropertyReserve), and the root close (Framing.RootClose). On CellC's json tree (1911 chars full),
    // 1911 is the last cap that drops the note and spills, 1912 the first that keeps it.

    [Fact]
    public void Json_TheResponseLevelLeadIsDroppedRatherThanOverrunningCap_AndTruncatedIsSet()
    {
        using var doc = JsonDocument.Parse(Tree(_w.CellC, format: "json", maxChars: 1911));
        Assert.False(doc.RootElement.TryGetProperty("child_declarers_note", out _));
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("spilled", out _));
    }

    [Fact]
    public void Json_TheResponseLevelLeadRidesWhenItFitsWithRoomToSpare()
    {
        using var doc = JsonDocument.Parse(Tree(_w.CellC, format: "json", maxChars: 1912));
        Assert.Equal(ReadSentences.DeclarersLead, doc.RootElement.GetProperty("child_declarers_note").GetString());
        Assert.False(doc.RootElement.GetProperty("truncated").GetBoolean());
    }

    /// <summary>The invariant the two pinned caps above are single samples of, asserted as a PROPERTY over a band
    /// of caps rather than a third hand-pinned number: wherever the response-level framing sentence is present,
    /// the document it rides in is within the caller's max_chars. That is the claim the architecture note makes
    /// for invariant framing ("Content lines still overshoot the cap by at most one line … invariant framing does
    /// not"), and a reserve short by any term falsifies it in a narrow window a pinned pair can step straight
    /// over — which is exactly what the `truncated` property's own ~21 bytes did.</summary>
    [Fact]
    public void Json_WhereverTheResponseLevelLeadIsPresent_TheDocumentIsWithinCap()
    {
        int seenWith = 0, seenWithout = 0;
        for (int cap = 1871; cap <= 1952; cap++)      // the measured boundary (1911/1912) +/- 40
        {
            var r = Tree(_w.CellC, format: "json", maxChars: cap);
            using var doc = JsonDocument.Parse(r);
            if (doc.RootElement.TryGetProperty("child_declarers_note", out _))
            {
                seenWith++;
                Assert.True(r.Length <= cap,
                            $"max_chars={cap}: the framing sentence rode a {r.Length}-char document");
            }
            else seenWithout++;
        }
        // The band has to straddle the boundary, or the property is vacuous on one side of it.
        Assert.True(seenWith > 0 && seenWithout > 0, $"band did not straddle: with={seenWith} without={seenWithout}");
    }

    // ---- the precise tier's lead reaches json and the artifact too, not just text ---------------------------

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
    /// key at all.</summary>
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

    /// <summary>The tree's owned-child block: the lines under its lead, trimmed, so an assertion about what the
    /// block says cannot pass on a match in the toucher list above it or the diff below it.</summary>
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
    /// so an assertion about what the diff says cannot pass on a match in the toucher list above it.</summary>
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

    /// <summary>One key of a field's <c>owned_child_union</c>, or NULL when the union does not carry it — a claim
    /// about a key's PRESENCE, which <see cref="JsonField"/> cannot make because it throws on an absent one.</summary>
    static JsonElement? UnionKey(string json, string path, string key)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var f in doc.RootElement.GetProperty("records")[0].GetProperty("fields").EnumerateArray())
            if (f.GetProperty("path").GetString() == path)
                return f.GetProperty("owned_child_union").TryGetProperty(key, out var v) ? v.Clone() : null;
        throw new Xunit.Sdk.XunitException($"no json field '{path}'");
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


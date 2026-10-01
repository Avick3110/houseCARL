using System.Reflection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The owned-child layer under the read render: the shape classifier, DeclaresChild's child-not-element and
/// null-not-false answers, the getter-to-concrete hop for every child-bearing type, a declarer that stops opening
/// leaving the order by name, and the read and check sentence consts with the composed declarers note. Migrated from
/// the owned-child-content-guard probe.</summary>
[Trait("tier", "integration")]
public sealed class OwnedChildContentTests : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-owned-child-content-");
    readonly ModKey _baseKey = new("HcOcBase", ModType.Master), _topKey = new("HcOcTop", ModType.Plugin);
    readonly FormKey _cellA, _weapon, _wrld;
    readonly string _basePath, _topPath;

    public OwnedChildContentTests()
    {
        _cellA = new FormKey(_baseKey, 0xC01);
        _weapon = new FormKey(_baseKey, 0xE01);
        _wrld = new FormKey(_baseKey, 0xF01);

        var b = new SkyrimMod(_baseKey, SkyrimRelease.SkyrimSE);
        var a = new Cell(_cellA, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellA", Flags = Cell.Flag.IsInteriorCell };
        a.Temporary.Add(new PlacedObject(new FormKey(_baseKey, 0xC10), SkyrimRelease.SkyrimSE) { EditorID = "HcOcTemp0" });
        a.Landscape = new Landscape(new FormKey(_baseKey, 0xC1B), SkyrimRelease.SkyrimSE) { EditorID = "HcOcLand" };
        FileInterior(b, a);
        b.Weapons.Add(new Weapon(_weapon, SkyrimRelease.SkyrimSE) { EditorID = "HcOcWeap", BasicStats = new WeaponBasicStats { Damage = 5 } });
        var ws = new Worldspace(_wrld, SkyrimRelease.SkyrimSE) { EditorID = "HcOcWrld" };
        var blk = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
        var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
        for (int i = 0; i < 3; i++)
            sub.Items.Add(new Cell(new FormKey(_baseKey, (uint)(0xF10 + i)), SkyrimRelease.SkyrimSE) { EditorID = $"HcOcWsCell{i}" });
        blk.Items.Add(sub); ws.SubCells.Add(blk);
        b.Worldspaces.Add(ws);
        _basePath = _mo2.InMod("BaseMod", _baseKey);
        b.BeginWrite.ToPath(_basePath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        // The winner: cell A with no children, and the worldspace as two blocks holding no cells.
        using var baseOv = SkyrimMod.CreateFromBinaryOverlay(_basePath, SkyrimRelease.SkyrimSE);
        var t = new SkyrimMod(_topKey, SkyrimRelease.SkyrimSE);
        FileInterior(t, new Cell(_cellA, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellA", Flags = Cell.Flag.IsInteriorCell });
        var tws = new Worldspace(_wrld, SkyrimRelease.SkyrimSE) { EditorID = "HcOcWrld" };
        for (int bx = 0; bx < 2; bx++)
        {
            var eb = new WorldspaceBlock { BlockNumberX = (short)bx, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
            eb.Items.Add(new WorldspaceSubBlock { BlockNumberX = (short)bx, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock });
            tws.SubCells.Add(eb);
        }
        t.Worldspaces.Add(tws);
        t.Weapons.GetOrAddAsOverride(baseOv.Weapons.First(w => w.FormKey == _weapon)).BasicStats!.Damage = 9;
        _topPath = _mo2.InMod("TopMod", _topKey);
        t.BeginWrite.ToPath(_topPath).WithLoadOrder(new ISkyrimModGetter[] { baseOv }).Write();

        _mo2.Profile(_baseKey.FileName + "\r\n" + _topKey.FileName + "\r\n",
                     "*" + _baseKey.FileName + "\r\n*" + _topKey.FileName + "\r\n",
                     "+TopMod\r\n+BaseMod\r\n");
    }

    public void Dispose() => _mo2.Delete();

    static void FileInterior(SkyrimMod mod, Cell cell)
    {
        var block = new CellBlock { BlockNumber = (int)(cell.FormKey.ID % 10), GroupType = GroupTypeEnum.InteriorCellBlock };
        var sub = new CellSubBlock { BlockNumber = (int)(cell.FormKey.ID / 10 % 10), GroupType = GroupTypeEnum.InteriorCellSubBlock };
        sub.Cells.Add(cell); block.SubBlocks.Add(sub); mod.Cells.Records.Add(block);
    }

    static T On<T>(string path, FormKey fk, Func<IMajorRecordGetter, T> ask)
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        return ask(ov.EnumerateMajorRecords().First(r => r.FormKey == fk));
    }

    // NO-CHILDREN: a weapon's child-bearing field set is EMPTY, so no read of one can annotate anything
    [Fact]
    public void AWeaponHasNoChildBearingFields() => Assert.Empty(On(_topPath, _weapon, OwnedChildContent.Fields));

    // SHAPE: the classifier answers the two shapes off the TYPE, before any body is read
    [Fact]
    public void TheShapeClassifierAnswersSingularAndCollectionOffTheType()
    {
        Assert.Equal(OwnedChildShape.Singular, On(_topPath, _cellA, b => OwnedChildContent.ShapeOf(b, "Landscape")));
        Assert.Equal(OwnedChildShape.Collection, On(_topPath, _cellA, b => OwnedChildContent.ShapeOf(b, "Temporary")));
        Assert.Equal(OwnedChildShape.Singular, On(_basePath, _wrld, b => OwnedChildContent.ShapeOf(b, "TopCell")));
        Assert.Equal(OwnedChildShape.Collection, On(_basePath, _wrld, b => OwnedChildContent.ShapeOf(b, "SubCells")));
        Assert.Equal(OwnedChildShape.None, On(_topPath, _cellA, b => OwnedChildContent.ShapeOf(b, "EditorID")));
    }

    // REACH: DeclaresChild answers the CHILD question, not the element question, on both bodies
    [Fact]
    public void EmptyBlockScaffoldingDeclaresNoCellsButRealCellsTwoLevelsDownDo()
    {
        Assert.True(On(_basePath, _wrld, b => OwnedChildContent.DeclaresChild(b, "SubCells")));
        Assert.False(On(_topPath, _wrld, b => OwnedChildContent.DeclaresChild(b, "SubCells")));
    }

    // BY CONSTRUCTION: every concrete child-bearing type's overlay maps back to it (the hop the field set rides)
    [Fact]
    public void EveryChildBearingTypesOverlayMapsBackToIt()
    {
        var bad = new List<string>();
        foreach (var t in typeof(Weapon).Assembly.GetTypes())
        {
            if (!t.IsClass || t.IsAbstract || t.Name.EndsWith("BinaryOverlay", StringComparison.Ordinal)) continue;
            if (!typeof(IMajorRecord).IsAssignableFrom(t) || WriteEngine.ChildBearingProperties(t).Count == 0) continue;
            var overlay = typeof(Weapon).Assembly.GetType(t.FullName + "BinaryOverlay");
            var getter = overlay is null ? null : WriteEngine.PrimaryGetter(overlay);
            var back = getter is null ? null : WriteEngine.ConcreteOf(getter);
            if (back != t) bad.Add($"{t.Name} -> {back?.Name ?? "(null)"}");
        }
        Assert.Empty(bad);
    }

    // UNREADABLE: an unopenable declarer leaves the order, and the load-order layer NAMES the failure
    [Fact]
    public void ADeclarerThatStopsOpeningIsNamedInTheLoadFailures()
    {
        using var svc = _mo2.Open();
        File.WriteAllBytes(_basePath, new byte[] { 0x00, 0x01, 0x02, 0x03 });
        var stats = svc.Stats();
        Assert.Contains(stats.loadFailures, f => f.Contains(_baseKey.FileName.String, StringComparison.OrdinalIgnoreCase));
    }

    // UNREADABLE: DeclaresChild on a field the body does not have answers NULL, never false;
    // …and a body that HAS the field but declares nothing answers false
    [Fact]
    public void AMissingFieldAnswersNullAndAnEmptyOneAnswersFalse()
    {
        Assert.Null(On(_topPath, _cellA, b => OwnedChildContent.DeclaresChild(b, "NoSuchFieldHere")));
        Assert.False(On(_topPath, _cellA, b => OwnedChildContent.DeclaresChild(b, "Temporary")));
    }

    // SENTENCE: every ReadSentences and CheckSentences const decides ([MustState] phrases or [NoClaims] with a reason) and states them
    [Fact]
    public void EveryReadAndCheckSentenceConstDecidesAndStatesItsPhrases()
    {
        var bad = new List<string>();
        foreach (var f in new[] { typeof(ReadSentences), typeof(CheckSentences) }
                     .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)))
        {
            if (!f.IsLiteral) { bad.Add($"{f.Name}: not a const"); continue; }
            if (f.FieldType != typeof(string)) continue;
            var text = (string?)f.GetRawConstantValue() ?? "";
            var must = f.GetCustomAttribute<MustStateAttribute>();
            var none = f.GetCustomAttribute<NoClaimsAttribute>();
            if (must is not null && none is not null) { bad.Add($"{f.Name}: both"); continue; }
            if (must is null && none is null) { bad.Add($"{f.Name}: neither"); continue; }
            if (none is not null && none.Reason.Trim().Length == 0) bad.Add($"{f.Name}: [NoClaims] with no reason");
            foreach (var phrase in must?.Phrases ?? Array.Empty<string>())
                if (!text.Contains(phrase, StringComparison.Ordinal)) bad.Add($"{f.Name}: no longer states \"{phrase}\"");
        }
        Assert.Empty(bad);
    }

    // SENTENCE: the COLLECTION note is built from the consts the net covers, and caps its names
    [Fact]
    public void TheCollectionNoteNamesDeclarersUpToTheCap()
    {
        var note = ReadSentences.DeclarersNote(OwnedChildShape.Collection, new[] { "A.esp", "B.esp", "C.esp", "D.esp" }, new[] { "E.esp" });
        Assert.Contains(ReadSentences.DeclaredBy, note);
        Assert.Contains(ReadSentences.CouldNotRead, note);
        Assert.Contains("(+1 more)", note);
        Assert.DoesNotContain("D.esp", note);
    }

    // SENTENCE: the SINGULAR note counts and never floods names
    [Fact]
    public void TheSingularNoteCountsInsteadOfNaming()
    {
        var note = ReadSentences.DeclarersNote(OwnedChildShape.Singular, new[] { "A.esp", "B.esp", "C.esp", "D.esp" }, Array.Empty<string>());
        Assert.Contains($"{ReadSentences.CarriedBy} 4 provider(s)", note);
        Assert.DoesNotContain("A.esp", note);
    }

    // SENTENCE: nothing to say is SAID — never silence
    [Fact]
    public void NoDeclarersIsStated()
        => Assert.Equal(ReadSentences.NoDeclarers,
            ReadSentences.DeclarersNote(OwnedChildShape.Collection, Array.Empty<string>(), Array.Empty<string>()));

    // SENTENCE: 'nobody declares' never absorbs a body that could not be READ — it is stated beside it
    [Fact]
    public void NoDeclarersKeepsTheUnreadableBodiesBesideIt()
    {
        var note = ReadSentences.DeclarersNote(OwnedChildShape.Collection, Array.Empty<string>(), new[] { "A.esp", "B.esp", "C.esp", "D.esp" });
        Assert.StartsWith(ReadSentences.NoDeclarers, note);
        Assert.Contains($"4 provider(s) {ReadSentences.CouldNotRead}", note);
        Assert.Contains(", …)", note);
        Assert.DoesNotContain("D.esp", note);
    }
}

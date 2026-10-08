using System.Text.Json;
using Mutagen.Bethesda;
using Noggog;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>An MO2 instance whose one plugin holds three armor addons: two carry only a male world model, one carries
/// both. The plugin is written to disk, so the scan reads Mutagen's binary overlay, where a missing female arm is null.</summary>
public sealed class MaleOnlyAddonWorld : IDisposable
{
    public string Root { get; }
    public LoadOrderService Svc { get; }
    public const string MaleNif = "hc\\maleonly.nif";
    public const string FemaleNif = "hc\\female.nif";

    public FormKey MaleOnly0 { get; }
    public FormKey BothArms { get; }

    public MaleOnlyAddonWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-male-only-arma-tests-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profiles = Path.Combine(instance, "profiles", "Default");
        var modDir = Path.Combine(instance, "mods", "ArmaMod");
        foreach (var d in new[] { profiles, modDir, Path.Combine(Root, "game", "Data") }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        var key = new ModKey("HcMaleOnlyArma", ModType.Master);
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        for (int i = 0; i < 2; i++)
        {
            var a = mod.ArmorAddons.AddNew();
            a.EditorID = $"HcMaleOnly{i}";
            a.WorldModel = new GenderedItem<Model?>(new Model { File = MaleNif }, null);
            if (i == 0) MaleOnly0 = a.FormKey;
        }
        var both = mod.ArmorAddons.AddNew();
        both.EditorID = "HcBothArms";
        BothArms = both.FormKey;
        both.WorldModel = new GenderedItem<Model?>(new Model { File = MaleNif }, new Model
        {
            File = FemaleNif,
            AlternateTextures = new ExtendedList<AlternateTexture> { new() { Name = "HcAlt", Index = 1 } },
        });
        mod.BeginWrite.ToPath(Path.Combine(modDir, key.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + key.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + key.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+ArmaMod\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
        Svc.Stats();
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

public sealed class MaleOnlyAddonFixture : IDisposable
{
    public MaleOnlyAddonWorld W { get; } = new();
    public void Dispose() => W.Dispose();
}

/// <summary>A where= over a gendered arm the record does not carry counts that record as unset, not as a read fault:
/// a male-only addon is a valid record, and calling it a parse failure reports a Mutagen gap that is not there (#1071).</summary>
[Trait("tier", "integration")]
public sealed class GenderedArmAccountingTests : IClassFixture<MaleOnlyAddonFixture>, IClassFixture<TruncatedSubFieldFixture>
{
    readonly MaleOnlyAddonWorld _w;
    readonly TruncatedSubFieldWorld _trunc;
    public GenderedArmAccountingTests(MaleOnlyAddonFixture f, TruncatedSubFieldFixture t) { _w = f.W; _trunc = t.W; }

    string Scan(string clause, bool countsOnly) =>
        RecordsTools.Records(_w.Svc, types: new[] { "ARMA" }, where: new[] { clause }, counts_only: countsOnly);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMissingFemaleArmIsUnset_NotAReadFault(bool countsOnly)
    {
        var r = Scan("WorldModel[1].File contains female", countsOnly);
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains("unset — null or absent (2)", r);
        Assert.DoesNotContain("read fault", r);
    }

    [Fact]
    public void TheRecordThatCarriesTheArmStillMatches()
    {
        var r = Scan("WorldModel[1].File contains female", countsOnly: false);
        Assert.Contains("HcBothArms", r);
        Assert.DoesNotContain("HcMaleOnly0", r);
    }

    [Fact]
    public void ADirectReadOfTheMissingArmSaysAbsent()
    {
        var r = RecordsTools.Records(_w.Svc, types: new[] { "ARMA" }, where: new[] { "editorid = HcMaleOnly0" },
            project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "WorldModel[1].File" } });
        Assert.Contains("WorldModel[1].File = (absent)", r);
        Assert.DoesNotContain("(unreadable", r);
    }

    [Theory]
    [InlineData("WorldModel[1] missing", 2)]
    [InlineData("WorldModel[1] exists", 1)]
    public void PresenceOverTheGenderedArmCountsTheAddonsThatLackIt(string clause, int matches)
    {
        var r = Scan(clause, countsOnly: true);
        Assert.Contains($"scan: {matches} match", r);
        Assert.DoesNotContain("read fault", r);
    }

    [Theory]
    [InlineData("WorldModel[1].AlternateTextures[*all].Index = 0")]
    [InlineData("WorldModel[1].AlternateTextures[*none].Index = 1")]
    [InlineData("WorldModel[1].AlternateTextures[*count] = 0")]
    public void AQuantifiedStepBehindAMissingArmFoldsAnEmptyList(string clause)
    {
        var r = Scan(clause, countsOnly: false);
        Assert.Contains("HcMaleOnly0", r);
        Assert.Contains("HcMaleOnly1", r);
        Assert.DoesNotContain("HcBothArms", r);
        Assert.DoesNotContain("read fault", r);
    }

    [Fact]
    public void CopyFromASourceWithoutTheArmSaysTheSourceHasNothingToCopy()
    {
        var o = _w.Svc.ApplyEdits(new[]
        {
            new BulkOp { Formid = ScratchMo2.Fid(_w.MaleOnly0), FieldPath = "WorldModel[1].File", Verb = "CopyFrom", FromPlugin = "HcMaleOnlyArma.esm" },
        }, "HcCopyNoArm", null);
        Assert.False(o.Success);
        Assert.Contains("the source plugin's version has no value at 'WorldModel[1]'", o.Error);
        Assert.DoesNotContain("engine error", o.Error);
    }

    [Fact]
    public void CopyFromPastTheEndOfASourceListKeepsTheElementCount()
    {
        var o = _w.Svc.ApplyEdits(new[]
        {
            new BulkOp { Formid = ScratchMo2.Fid(_w.BothArms), FieldPath = "WorldModel[1].AlternateTextures[3].Name", Verb = "CopyFrom", FromPlugin = "HcMaleOnlyArma.esm" },
        }, "HcCopyPastEnd", null);
        Assert.False(o.Success);
        Assert.Contains("has no value at 'WorldModel[1].AlternateTextures[3]' (list has 1 element(s))", o.Error);
    }

    /// <summary>The genuine fault keeps its own bucket: a DATA subrecord cut short still reads as a read fault.</summary>
    [Fact]
    public void AFieldMutagenCannotParseIsStillAReadFault()
    {
        var r = RecordsTools.Records(_trunc.Svc, source: JsonDocument.Parse("\"" + _trunc.TruncName + "\"").RootElement.Clone(),
            types: new[] { "WEAP" }, where: new[] { "BasicStats.Damage >= 0" }, counts_only: true);
        Assert.Contains("could not be READ on any of 1 scanned record(s) — a read FAULT", r);
        Assert.DoesNotContain("UNSET", r);
    }
}

/// <summary>The neighbours of a missing arm in the same read walk: an index past a list's end and an absent list are
/// unset steps too, not parse failures.</summary>
[Trait("tier", "unit")]
public sealed class AbsentStepAccountingTests
{
    static string NoteOver(string clause, IEnumerable<IMajorRecordGetter> bodies)
    {
        var (set, err) = FieldPredicateSet.Parse(new[] { clause });
        Assert.Null(err);
        foreach (var b in bodies) set!.Matches(b);
        return set!.AccountingNote() ?? "";
    }

    [Fact]
    public void AnIndexPastTheEndOfAListIsUnset()
    {
        var mod = new SkyrimMod(new ModKey("HcAbsentStep", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var kw = mod.Keywords.AddNew();
        var bodies = new List<IMajorRecordGetter>();
        for (int i = 0; i < 3; i++)
        {
            var w = mod.Weapons.AddNew();
            w.Keywords = new() { kw.ToLink() };
            bodies.Add(w);
        }
        var full = mod.Weapons.AddNew();
        full.Keywords = new();
        for (int i = 0; i < 5; i++) full.Keywords.Add(kw.ToLink());
        bodies.Add(full);
        var note = NoteOver($"Keywords[4] = {kw.FormKey.ID:X6}:{kw.FormKey.ModKey.FileName}", bodies);
        Assert.Contains("unset — null or absent (3)", note);
        Assert.DoesNotContain("read fault", note);
    }

    [Theory]
    [InlineData("Effects[2].Conditions[*all].CompareOperator = EqualTo")]
    [InlineData("Effects[2].Conditions[*none].CompareOperator = GreaterThan")]
    [InlineData("Effects[2].Conditions[*count] = 0")]
    public void AQuantifiedStepBehindAnIndexPastTheEndFoldsAnEmptyList(string clause)
    {
        var mod = new SkyrimMod(new ModKey("HcPastEndFold", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var shortSpell = mod.Spells.AddNew();
        shortSpell.Effects.Add(new Effect());
        var longSpell = mod.Spells.AddNew();
        for (int i = 0; i < 3; i++) longSpell.Effects.Add(new Effect());
        longSpell.Effects[2].Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.GreaterThan, Data = new GetIsIDConditionData() });
        var (set, err) = FieldPredicateSet.Parse(new[] { clause });
        Assert.Null(err);
        Assert.True(set!.Matches(shortSpell));
        Assert.False(set.Matches(longSpell));
        Assert.DoesNotContain("read fault", set.AccountingNote() ?? "");
    }

    [Fact]
    public void ADirectReadPastTheEndOfAListKeepsTheElementCount()
    {
        var mod = new SkyrimMod(new ModKey("HcPastEnd", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var kw = mod.Keywords.AddNew();
        var w = mod.Weapons.AddNew();
        w.Keywords = new() { kw.ToLink(), kw.ToLink(), kw.ToLink() };
        foreach (var depth in new[] { 1, 2 })
        {
            var f = Assert.Single(ReadEngine.ReadFields(w, new[] { "Keywords[4]" }, depth).Fields);
            Assert.Equal("(absent: list has 3 element(s))", f.Note);
            Assert.True(f.Readable);
        }
    }

    [Fact]
    public void ARowDropsAnAbsentCellThatCarriesItsDetail()
    {
        var folded = RowProjection.Fold(new[]
        {
            new FieldValue("Effects[0]", false, null, "[Effect]", Present: true),
            new FieldValue("Effects[0].Conditions[3]", false, null, "(absent: list has 1 element(s))", Present: false),
        }, new[] { "Effects" }, RowProjection.DefaultDepth);
        Assert.Equal("[Effect]", Assert.Single(folded).Note);
    }

    [Fact]
    public void AnAbsentGenderedFieldIsUnset()
    {
        var mod = new SkyrimMod(new ModKey("HcAbsentGendered", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var bodies = new List<IMajorRecordGetter>();
        for (int i = 0; i < 2; i++) bodies.Add(mod.ArmorAddons.AddNew());   // WorldModel itself null
        var carried = mod.ArmorAddons.AddNew();
        carried.WorldModel = new GenderedItem<Model?>(new Model { File = "x.nif" }, null);
        bodies.Add(carried);
        var note = NoteOver("WorldModel[0].File contains x", bodies);
        Assert.Contains("unset — null or absent (2)", note);
        Assert.DoesNotContain("read fault", note);
    }
}

using System.Text.Json;
using Mutagen.Bethesda;
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
        }
        var both = mod.ArmorAddons.AddNew();
        both.EditorID = "HcBothArms";
        both.WorldModel = new GenderedItem<Model?>(new Model { File = MaleNif }, new Model { File = FemaleNif });
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

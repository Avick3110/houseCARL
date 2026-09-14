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
/// #683: an Add on a LeveledItem's Entries, into an existing patch that already overrides the record, answered with
/// a grown element count while the file was unchanged. The count came from the in-memory record; only
/// full_readback=true read the file. These tests pin the shape of that report: the file grows, and the per-edit line
/// carries what a fresh read of the written file says.
/// </summary>
[Trait("tier", "integration")]
public sealed class WriteReadbackFromFileTests : IDisposable
{
    const string MasterName = "HcRbMaster.esm";
    const string PatchName = "HcRbPatch.esp";
    const string PatchFolder = "houseCARL - HcRbPatch";

    readonly string _root, _priorCorpusPath, _patchPath;
    readonly LoadOrderService _svc;
    readonly FormKey _weapon, _lvli;

    public WriteReadbackFromFileTests()
    {
        _priorCorpusPath = CorpusRulebook.CorpusPath;
        _root = Path.Combine(Path.GetTempPath(), "hc-readback-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "game", "Data"));

        var master = new SkyrimMod(new ModKey("HcRbMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var w = master.Weapons.AddNew();
        w.EditorID = "HcRbSword";
        w.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1 };
        _weapon = w.FormKey;

        var ll = master.LeveledItems.AddNew();
        ll.EditorID = "HcRbList";
        ll.Entries = Ten();
        _lvli = ll.FormKey;

        // The patch from #683: an ACTIVE plugin already overriding the leveled list, so the write's winner IS the
        // file the write extends.
        var patch = new SkyrimMod(new ModKey("HcRbPatch", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var over = patch.LeveledItems.GetOrAddAsOverride(ll);
        over.Entries = Ten();

        var instance = Path.Combine(_root, "inst");
        var mods = Path.Combine(instance, "mods");
        Directory.CreateDirectory(Path.Combine(mods, "RbMasterMod"));
        Directory.CreateDirectory(Path.Combine(mods, PatchFolder));
        master.BeginWrite.ToPath(Path.Combine(mods, "RbMasterMod", MasterName))
            .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        _patchPath = Path.Combine(mods, PatchFolder, PatchName);
        patch.BeginWrite.ToPath(_patchPath).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();
        // The owner marker: into= only extends a folder houseCARL made.
        File.WriteAllText(Path.Combine(mods, PatchFolder, "meta.ini"),
            "[General]\r\ngameName=skyrimse\r\n\r\n[houseCARL]\r\ngenerated=true\r\nplugin=" + PatchName + "\r\n");

        var genDir = Path.Combine(_root, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(_root, "corpus-ref"));
        CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + MasterName + "\r\n" + PatchName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + MasterName + "\r\n*" + PatchName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+" + PatchFolder + "\r\n+RbMasterMod\r\n");

        _svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "user.json")));

        Noggog.ExtendedList<LeveledItemEntry> Ten()
        {
            var list = new Noggog.ExtendedList<LeveledItemEntry>();
            for (int i = 1; i <= 10; i++)
                list.Add(new LeveledItemEntry
                {
                    Data = new LeveledItemEntryData { Level = (short)i, Count = 1, Reference = new FormLink<IItemGetter>(w.FormKey) },
                });
            return list;
        }
    }

    static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";
    static JsonElement Je(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>How many entries the patch FILE holds right now — a fresh open every time, never the write's own view.</summary>
    int EntriesOnDisk()
    {
        using var back = (IDisposable)SkyrimMod.CreateFromBinaryOverlay(_patchPath, SkyrimRelease.SkyrimSE);
        var mod = (ISkyrimModGetter)back;
        var rec = mod.LeveledItems.First(r => r.FormKey == _lvli);
        return rec.Entries?.Count ?? 0;
    }

    string AddEntry(short level, string? format = null) => ApplyTools.Apply(_svc,
        ops: Je($@"[{{""formid"":""{Fid(_lvli)}"",""field_path"":""Entries"",""op"":""Add"",""compose"":{{""type"":""LeveledItemEntry"",""sets"":[{{""path"":""Data.Level"",""value"":""{level}""}},{{""path"":""Data.Count"",""value"":""1""}},{{""path"":""Data.Reference"",""value"":""{Fid(_weapon)}""}}]}}}}]"),
        into: PatchName, format: format);

    /// <summary>#683's exact shape: the Add must reach the file, not just the record the call held.</summary>
    [Fact]
    public void AnAddIntoAnExistingPatchGrowsTheFile()
    {
        Assert.Equal(10, EntriesOnDisk());
        var before = new FileInfo(_patchPath).Length;
        var r = AddEntry(42);
        Assert.DoesNotContain("error:", r);
        Assert.Equal(11, EntriesOnDisk());
        Assert.True(new FileInfo(_patchPath).Length > before, "patch did not grow");
    }

    /// <summary>And the line the caller reads must say what the file says. #683's response printed
    /// "[list: 11 item(s)]" over a file holding 10.</summary>
    [Fact]
    public void ThePerEditLineMatchesAFreshReadOfTheFile()
    {
        var r = AddEntry(42);
        Assert.DoesNotContain("error:", r);
        Assert.Contains($"[list: {EntriesOnDisk()} item(s)]", r);
    }

    /// <summary>The json document carries the file's reading under its own key, so a consumer never has to tell the
    /// two apart by guessing which of them `after` is.</summary>
    [Fact]
    public void TheJsonOpCarriesTheFilesOwnReading()
    {
        var doc = JsonDocument.Parse(AddEntry(42, format: "json"));
        var op = doc.RootElement.GetProperty("ops")[0];
        Assert.True(doc.RootElement.GetProperty("verify_ran").GetBoolean());
        Assert.Equal("written_file", op.GetProperty("landed_source").GetString());
        Assert.Equal($"[list: {EntriesOnDisk()} item(s)]", op.GetProperty("after_on_disk").GetString());
    }

    public void Dispose()
    {
        CorpusRulebook.CorpusPath = _priorCorpusPath;
        _svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>
/// The render half of #683, driven through the seam the two readings meet at: an outcome whose in-memory value and
/// whose file value deliberately disagree. The response may print only the file's, and where the file gave none it
/// may print no value at all.
/// </summary>
[Trait("tier", "unit")]
public sealed class WriteEditLineSourceTests
{
    const string Memory = "[list: 999 item(s)]";
    const string Disk = "[list: 10 item(s)]";

    static string RenderOne(WritePatchBuilder.OpResult op) => WriteTools.Render(
        new WritePatchBuilder.PatchOutcome(true, null, Path.Combine("mods", "houseCARL - X", "X.esp"), true,
            new[] { "Skyrim.esm" }, new[] { op }, 1692));

    static WritePatchBuilder.OpResult Op() => new(
        FormKey.Factory("09ABAB:Test.esm"), "LeveledItem", "Add Entries", true, null, Memory, Memory);

    [Fact]
    public void ThePerEditLinePrintsTheFileValueNotTheAppliedOne()
    {
        var r = RenderOne(Op() with { AfterOnDisk = Disk, LandedOnDisk = Disk, VerifyAttempted = true });
        Assert.Contains(Disk, r);
        Assert.DoesNotContain(Memory, r);
    }

    [Fact]
    public void AnOpTheFileCouldNotAnswerForIsNotCheckedRatherThanTheAppliedValue()
    {
        var r = RenderOne(Op() with { VerifyAttempted = true });
        Assert.Contains("not-checked", r);
        Assert.DoesNotContain(Memory, r);
    }

    /// <summary>A leaf a later op in the same call overwrote: the file's final state is that op's, so this op's line
    /// carries no value either — the mid-sequence reading is not something the file vouches for.</summary>
    [Fact]
    public void AnOpASiblingSupersededIsNotCheckedToo()
    {
        var r = RenderOne(Op() with { SupersededInCall = true, VerifyAttempted = true });
        Assert.Contains("not-checked", r);
        Assert.DoesNotContain(Memory, r);
    }
}

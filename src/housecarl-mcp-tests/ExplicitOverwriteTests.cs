using HousecarlCore;
using HousecarlGenerator;
using Mutagen.Bethesda;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Explicit-paths mode reads HouseCarl:OverwriteDir as MO2's overwrite layer (#1387): an overwrite plugin is
/// served over an enabled mod's copy and over Data's, an overwrite loose file and an overwrite archive win, and the
/// layer is labelled "overwrite" — the answers instance mode already gives, which each test pins beside its explicit
/// twin. Unset or blank keeps the no-overwrite-layer answer; a set root that is not a folder refuses in one sentence.</summary>
[Trait("tier", "integration")]
public sealed class ExplicitOverwriteTests : IDisposable
{
    const string Dup = "HcXoDup.esp";          // in an enabled mod AND overwrite
    const string FromData = "HcXoData.esp";    // in the game's Data folder AND overwrite
    const string Tool = "HcXoTool.esp";        // overwrite only, as a tool output would be
    const string Texture = @"textures\hcxo\x.dds";
    const string MeshInBsa = @"meshes\hcxo\a.nif";

    static readonly byte[] ModBytes = BsaBuilder.Bytes("HCXO-MOD", 32);
    static readonly byte[] OverwriteBytes = BsaBuilder.Bytes("HCXO-OVW", 48);

    readonly PlaceInstance _p = new();
    readonly string _ovw;
    readonly ToolPathResolver _tools;
    readonly UserConfigStore _store;
    readonly List<LoadOrderService> _opened = new();
    FormKey _dupWeapon, _dataWeapon;

    public ExplicitOverwriteTests()
    {
        _ovw = Path.Combine(_p.Inst, "overwrite");       // where instance mode's default overwrite root points too
        Directory.CreateDirectory(_ovw);
        _store = new UserConfigStore(Path.Combine(_p.Root, "user-explicit.json"));
        _tools = new ToolPathResolver(_store);

        var mod = _p.Mod("ModA");
        WritePlugin(mod, Dup, "HcXoFromMod");
        _dupWeapon = WritePlugin(_ovw, Dup, "HcXoFromOverwrite");
        WritePlugin(_p.Data, FromData, "HcXoFromData");
        _dataWeapon = WritePlugin(_ovw, FromData, "HcXoFromOverwriteData");
        WritePlugin(_ovw, Tool, "HcXoFromTool");

        PlaceInstance.Loose(mod, Texture, ModBytes);
        PlaceInstance.Loose(_ovw, Texture, OverwriteBytes);
        // Named for the active plugin, so both copies are discovered as that plugin's archive and only one can win.
        File.WriteAllBytes(Path.Combine(mod, "HcXoDup.bsa"), Archive(ModBytes));
        File.WriteAllBytes(Path.Combine(_ovw, "HcXoDup.bsa"), Archive(OverwriteBytes));

        _p.Profile(new[] { Dup, FromData, Tool }, new[] { "*" + Dup, "*" + FromData, "*" + Tool }, new[] { "+ModA" });
    }

    public void Dispose()
    {
        foreach (var s in _opened) s.Dispose();
        _p.Dispose();
    }

    static byte[] Archive(byte[] body) => BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames,
        new[] { (Path.GetDirectoryName(MeshInBsa)!, new[] { (Path.GetFileName(MeshInBsa), body) }) });

    static FormKey WritePlugin(string dir, string fileName, string editorId)
    {
        var mod = new SkyrimMod(ModKey.FromFileName(fileName), SkyrimRelease.SkyrimSE);
        var weapon = mod.Weapons.AddNew();
        weapon.EditorID = editorId;
        mod.BeginWrite.ToPath(Path.Combine(dir, fileName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        return weapon.FormKey;
    }

    LoadOrderService Explicit(string? overwriteDir)
    {
        var svc = LoadOrderService.WithExplicitPaths(_p.Data, _p.Mods, _p.Prof, 0, _store, overwriteDir);
        _opened.Add(svc);
        return svc;
    }

    string Status(LoadOrderService svc, string filter) => StatusTools.LoadOrderStatus(svc, _tools, filter: filter);

    static AssetProvider Winner(LoadOrderService svc, string rel) => svc.AssetStatus(new[] { rel }).Results[0].Hit!.Winner!;

    static string? EditorIdOf(LoadOrderService svc, FormKey fk) =>
        svc.ReadArea.ResolveRead(fk, null, null, conflictTree: false).Record?.EditorId;

    [Fact]
    public void AnOverwritePluginIsServedOverAnEnabledModsCopyAndOverDatas()
    {
        var svc = Explicit(_ovw);

        Assert.Contains("served from: overwrite", Status(svc, Dup));
        Assert.Contains("also in:     mod 'ModA' (enabled)", Status(svc, Dup));
        Assert.Contains("served from: overwrite", Status(svc, FromData));
        Assert.Contains("served from: overwrite", Status(svc, Tool));
        Assert.Equal("HcXoFromOverwrite", EditorIdOf(svc, _dupWeapon));
        Assert.Equal("HcXoFromOverwriteData", EditorIdOf(svc, _dataWeapon));
    }

    /// <summary>Pins unchanged behaviour: instance mode answers the same for the same folders.</summary>
    [Fact]
    public void InstanceModeServesTheSameOverwritePlugins()
    {
        var svc = _p.Open();

        Assert.Contains("served from: overwrite", StatusTools.LoadOrderStatus(svc, _tools, filter: Dup));
        Assert.Contains("served from: overwrite", StatusTools.LoadOrderStatus(svc, _tools, filter: FromData));
        Assert.Equal("HcXoFromOverwrite", EditorIdOf(svc, _dupWeapon));
    }

    [Fact]
    public void AnOverwriteLooseFileAndAnOverwriteArchiveWinAndTheLayerIsLabelledOverwrite()
    {
        var svc = Explicit(_ovw);

        var loose = Winner(svc, Texture);
        Assert.Equal("overwrite", loose.Source);
        Assert.Equal(AssetKind.Loose, loose.Kind);

        var archived = Winner(svc, MeshInBsa);
        Assert.Equal(AssetKind.Bsa, archived.Kind);
        Assert.Equal("overwrite", archived.OwningMod);
    }

    /// <summary>Pins unchanged behaviour: instance mode answers the same for the same folders.</summary>
    [Fact]
    public void InstanceModeWinsTheSameLooseFileAndArchive()
    {
        var svc = _p.Open();

        var loose = Winner(svc, Texture);
        Assert.Equal("overwrite", loose.Source);
        Assert.Equal(AssetKind.Loose, loose.Kind);
        Assert.Equal("overwrite", Winner(svc, MeshInBsa).OwningMod);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnsetOrBlankOverwriteRootKeepsTheNoOverwriteLayerAnswer(string? configured)
    {
        var svc = Explicit(configured);

        Assert.Contains("served from: mod 'ModA' (enabled)", Status(svc, Dup));
        Assert.Contains("served from: none", Status(svc, Tool));
        Assert.Equal("HcXoFromMod", EditorIdOf(svc, _dupWeapon));
        Assert.Equal("ModA", Winner(svc, Texture).Source);
        Assert.Equal("ModA", Winner(svc, MeshInBsa).OwningMod);
    }

    [Fact]
    public void AnOverwriteRootThatIsNotAFolderRefusesInOneSentence()
    {
        var missing = Path.Combine(_p.Root, "no-such-overwrite");
        var svc = Explicit(missing);

        var text = Status(svc, Dup);
        Assert.StartsWith("error: HouseCarl:OverwriteDir is '" + missing + "', which is not a folder", text);
        Assert.DoesNotContain("failed unexpectedly", text);
        Assert.Contains("leave it unset", text);
    }

    /// <summary>Pins unchanged behaviour: instance mode's own overwrite folder gating nothing — a missing one reads as
    /// empty, because MO2 recreates it at every start.</summary>
    [Fact]
    public void AMissingOverwriteFolderInInstanceModeStillAnswers()
    {
        Directory.Delete(_ovw, recursive: true);
        var svc = _p.Open();

        Assert.Contains("served from: mod 'ModA' (enabled)", StatusTools.LoadOrderStatus(svc, _tools, filter: Dup));
        Assert.Equal("HcXoFromMod", EditorIdOf(svc, _dupWeapon));
    }
}

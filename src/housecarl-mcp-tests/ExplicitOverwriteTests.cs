using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Explicit-paths mode reads HouseCarl:OverwriteDir as MO2's overwrite layer (#1387): an overwrite plugin is
/// served over an enabled mod's copy and over Data's, an overwrite loose file and an overwrite archive win, and the
/// layer is labelled "overwrite". Each of those runs over the same folders in instance mode too, where the answer must
/// be the one main already gives. Unset or blank keeps the no-overwrite-layer answer; a set root that is not a folder
/// refuses in one sentence, where a missing overwrite folder in instance mode still answers.</summary>
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
    readonly ToolPathResolver _tools;
    readonly FormKey _dupWeapon, _dataWeapon;

    public ExplicitOverwriteTests()
    {
        Directory.CreateDirectory(_p.Overwrite);
        _tools = new ToolPathResolver(new UserConfigStore(Path.Combine(_p.Root, "user-tools.json")));

        var mod = _p.Mod("ModA");
        WritePlugin(mod, Dup, "HcXoFromMod");
        _dupWeapon = WritePlugin(_p.Overwrite, Dup, "HcXoFromOverwrite");
        WritePlugin(_p.Data, FromData, "HcXoFromData");
        _dataWeapon = WritePlugin(_p.Overwrite, FromData, "HcXoFromOverwriteData");
        WritePlugin(_p.Overwrite, Tool, "HcXoFromTool");

        PlaceInstance.Loose(mod, Texture, ModBytes);
        PlaceInstance.Loose(_p.Overwrite, Texture, OverwriteBytes);
        // Named for the active plugin, so both copies are discovered as that plugin's archive and only one can win.
        File.WriteAllBytes(Path.Combine(mod, "HcXoDup.bsa"), Archive(ModBytes));
        File.WriteAllBytes(Path.Combine(_p.Overwrite, "HcXoDup.bsa"), Archive(OverwriteBytes));

        _p.Profile(new[] { Dup, FromData, Tool }, new[] { "*" + Dup, "*" + FromData, "*" + Tool }, new[] { "+ModA" });
    }

    public void Dispose() => _p.Dispose();

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

    /// <summary>The same folders read either way: explicit mode pointed at the overwrite root, or instance mode
    /// deriving the same one, whose answer is main's and must not move.</summary>
    LoadOrderService Svc(bool explicitMode) => explicitMode ? _p.OpenExplicit(_p.Overwrite) : _p.Open();

    string Status(LoadOrderService svc, string filter) => StatusTools.LoadOrderStatus(svc, _tools, filter: filter);

    static AssetProvider Winner(LoadOrderService svc, string rel) => svc.AssetStatus(new[] { rel }).Results[0].Hit!.Winner!;

    static string? EditorIdOf(LoadOrderService svc, FormKey fk) =>
        svc.ReadArea.ResolveRead(fk, null, null, conflictTree: false).Record?.EditorId;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnOverwritePluginIsServedOverAnEnabledModsCopyAndOverDatas(bool explicitMode)
    {
        var svc = Svc(explicitMode);

        var dup = Status(svc, Dup);
        Assert.Contains("served from: overwrite", dup);
        Assert.Contains("also in:     mod 'ModA' (enabled)", dup);
        Assert.Contains("served from: overwrite", Status(svc, FromData));
        Assert.Contains("served from: overwrite", Status(svc, Tool));
        Assert.Equal("HcXoFromOverwrite", EditorIdOf(svc, _dupWeapon));
        Assert.Equal("HcXoFromOverwriteData", EditorIdOf(svc, _dataWeapon));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnOverwriteLooseFileAndAnOverwriteArchiveWinAndTheLayerIsLabelledOverwrite(bool explicitMode)
    {
        var svc = Svc(explicitMode);

        var loose = Winner(svc, Texture);
        Assert.Equal("overwrite", loose.Source);
        Assert.Equal(AssetKind.Loose, loose.Kind);

        var archived = Winner(svc, MeshInBsa);
        Assert.Equal(AssetKind.Bsa, archived.Kind);
        Assert.Equal("overwrite", archived.OwningMod);
    }

    /// <summary>Explicit mode's only readback of the root it was given; instance mode's line is unchanged, as it names
    /// its overwrite folder in the set_mo2_instance answer instead.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheStatusInstanceLineNamesTheConfiguredExplicitOverwriteRoot(bool explicitMode)
    {
        var line = InstanceLine(Status(Svc(explicitMode), Dup));

        Assert.Equal(explicitMode
            ? "instance: explicit-paths mode (no MO2 instance configured) · overwrite: " + _p.Overwrite
            : "instance: " + _p.Inst, line);
    }

    [Fact]
    public void ExplicitModeWithNoOverwriteRootNamesNoOverwriteFolder()
    {
        Assert.Equal("instance: explicit-paths mode (no MO2 instance configured)",
                     InstanceLine(Status(_p.OpenExplicit(), Dup)));
    }

    /// <summary>The configured root is taken as instance mode's roots are — made full and stripped of a trailing
    /// separator — so one folder is one string wherever it is read back or compared.</summary>
    [Fact]
    public void TheConfiguredOverwriteRootIsNormalizedAsInstanceModesRootsAre()
    {
        var roundabout = Path.Combine(_p.Inst, "profiles", "..", "overwrite") + Path.DirectorySeparatorChar;

        var status = Status(_p.OpenExplicit(roundabout), Dup);

        Assert.Equal("instance: explicit-paths mode (no MO2 instance configured) · overwrite: " + _p.Overwrite,
                     InstanceLine(status));
        Assert.Contains("served from: overwrite", status);
    }

    static string InstanceLine(string status) =>
        status.Split('\n').First(l => l.StartsWith("instance: ", StringComparison.Ordinal)).TrimEnd('\r');

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnsetOrBlankOverwriteRootKeepsTheNoOverwriteLayerAnswer(string? configured)
    {
        var svc = _p.OpenExplicit(configured);

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

        var text = Status(_p.OpenExplicit(missing), Dup);

        Assert.StartsWith("error: HouseCarl:OverwriteDir is '" + missing + "', which is not a folder", text);
        Assert.DoesNotContain("failed unexpectedly", text);
        // The three remedies, the first because nothing recreates this folder, where MO2 recreates its own at every start.
        Assert.Contains("Create it (nothing else will)", text);
        Assert.Contains("point the setting at the folder", text);
        Assert.Contains("leave it unset", text);
    }

    /// <summary>"Every call that reads the load order", past load_order_status: the asset, record and output lanes each
    /// refuse on a cold service, and the root is re-read per call, so a warm one refuses the moment the folder goes.</summary>
    [Fact]
    public void ARefusedOverwriteRootRefusesTheAssetRecordAndOutputLanesToo()
    {
        var cold = _p.OpenExplicit(Path.Combine(_p.Root, "no-such-overwrite"));

        Assert.Throws<ExplicitRootRefusedException>(() => { cold.AssetStatus(new[] { Texture }); });
        Assert.Throws<ExplicitRootRefusedException>(() => { EditorIdOf(cold, _dupWeapon); });
        Assert.Throws<ExplicitRootRefusedException>(() => { cold.ResolveExplicitScriptFolder(_p.Mods, out _); });

        var warm = _p.OpenExplicit(_p.Overwrite);
        Assert.Equal("HcXoFromOverwrite", EditorIdOf(warm, _dupWeapon));   // an index built while the folder was there
        Directory.Delete(_p.Overwrite, recursive: true);

        Assert.Throws<ExplicitRootRefusedException>(() => { warm.AssetStatus(new[] { Texture }); });
    }

    /// <summary>The three doors that swallow instance-mode derivation let the refused explicit root through, so the
    /// decompile hierarchy, the compiler's game-dir hint and the mods-root read refuse rather than answer degraded.
    /// Instance mode's own unusable-instance swallow is pinned by <c>CompileGameDirHintTests</c>.</summary>
    [Fact]
    public void ARefusedOverwriteRootIsNotSwallowedByTheDoorsThatSwallowDerivation()
    {
        var svc = _p.OpenExplicit(Path.Combine(_p.Root, "no-such-overwrite"));

        Assert.Throws<ExplicitRootRefusedException>(() => svc.ClassParentsForDecompile());
        Assert.Throws<ExplicitRootRefusedException>(() => svc.GameDirOrNull());
        Assert.Throws<ExplicitRootRefusedException>(() => _ = svc.ModsRootOrNull);
    }

    /// <summary>Pins unchanged behaviour: instance mode's overwrite folder gates nothing — a missing one reads as
    /// empty, because MO2 recreates it at every start.</summary>
    [Fact]
    public void AMissingOverwriteFolderInInstanceModeStillAnswers()
    {
        Directory.Delete(_p.Overwrite, recursive: true);
        var svc = _p.Open();

        Assert.Contains("served from: mod 'ModA' (enabled)", Status(svc, Dup));
        Assert.Equal("HcXoFromMod", EditorIdOf(svc, _dupWeapon));
    }
}

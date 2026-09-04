using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// Reading a record by the FormID a log, the console or a crash log printed, and printing that form back.
///
/// <para>The world is built per test because two of these change the load order, which is the whole point: the
/// light index moves when the order does, so a cached answer would be wrong the next day.</para>
/// </summary>
[Trait("tier", "integration")]
public sealed class RuntimeFormIdTests
{
    // ---- the parser, no world needed --------------------------------------------------------------

    [Theory]
    [InlineData("FE028B19")]
    [InlineData("0xFE028B19")]
    [InlineData("fe028b19")]
    public void ALightRuntimeFormIdParsesWithOrWithoutTheHexPrefix(string token)
    {
        Assert.True(RuntimeFormId.TryParse(token, out uint v));
        Assert.Equal(0xFE028B19u, v);
        Assert.True(RuntimeFormId.IsLight(v));
        Assert.Equal(0x028u, RuntimeFormId.LightIndex(v));
    }

    [Theory]
    [InlineData("0A00B19C")]
    [InlineData("0x0A00B19C")]
    public void AFullRuntimeFormIdParsesWithOrWithoutTheHexPrefix(string token)
    {
        Assert.True(RuntimeFormId.TryParse(token, out uint v));
        Assert.Equal(0x0A00B19Cu, v);
        Assert.False(RuntimeFormId.IsLight(v));
        Assert.Equal(0x0Au, RuntimeFormId.LoadIndex(v));
    }

    [Theory]
    [InlineData("000800:Skyrim.esm")]      // the plugin-qualified form is never a runtime FormID
    [InlineData("FE028B1")]                // seven digits
    [InlineData("FE028B199")]              // nine digits
    [InlineData("not-a-formid")]
    public void OnlyAnEightHexTokenWithNoPluginIsARuntimeFormId(string token)
        => Assert.False(RuntimeFormId.TryParse(token, out _));

    // ---- reading by a runtime FormID --------------------------------------------------------------

    [Fact]
    public void ALightPluginsRecordReadsByItsRuntimeFormId()
    {
        using var w = new World();
        Served(Read(w, World.LightRuntime(0, w.LightWeapon)), "HcRtLightWeapon", World.LightName);
    }

    [Fact]
    public void AFullPluginsRecordReadsByItsLoadIndex()
    {
        using var w = new World();
        Served(Read(w, World.FullRuntime(1, w.FullWeapon)), "HcRtFullWeapon", World.FullName);
    }

    [Fact]
    public void ALightIndexNoActivePluginOccupiesIsRefused()
    {
        using var w = new World();
        var response = Read(w, "FE0FF800");
        Assert.Contains("light index 0x0FF", response);
        Assert.Contains("XXXXXX:Plugin.esp", response);
    }

    [Fact]
    public void ALoadIndexNoActivePluginOccupiesIsRefused()
    {
        using var w = new World();
        var response = Read(w, "5A000800");
        Assert.Contains("load index 0x5A", response);
        Assert.Contains("XXXXXX:Plugin.esp", response);
    }

    [Fact]
    public void ADynamicFormIdIsRefusedAsBelongingToNoPlugin()
    {
        using var w = new World();
        Assert.Contains("save game", Read(w, "FF000800"));
    }

    // ---- printing it back -------------------------------------------------------------------------

    [Fact]
    public void TheRuntimeFormIdIsPrintedBesideTheRecordsOwnFormId()
    {
        using var w = new World();
        Served(Read(w, World.Fid(w.LightWeapon)), "runtime=" + World.LightRuntime(0, w.LightWeapon));
    }

    [Fact]
    public void ThePrintedRuntimeFormIdReadsBackTheSameRecord()
    {
        using var w = new World();
        var printed = Token(Read(w, World.Fid(w.LightWeapon)), "runtime=");
        Served(Read(w, printed), "HcRtLightWeapon", World.LightName);
    }

    [Fact]
    public void TheScanSummaryRowCarriesTheRuntimeFormId()
    {
        using var w = new World();
        Served(Scan(w, World.LightName), "runtime=" + World.LightRuntime(0, w.LightWeapon));
    }

    [Fact]
    public void TheDenseScanLaneCarriesTheRuntimeFormIdColumn()
    {
        using var w = new World();
        var response = Scan(w, World.LightName, format: "dense");
        Assert.Contains("runtime_formid", response);
        Assert.Contains(World.LightRuntime(0, w.LightWeapon), response);
    }

    // ---- a light plugin whose records sit outside the ESL window -----------------------------------

    /// <summary>An esp-fe flagged light but never compacted: masking its 0x001801 record down to 0x801 would
    /// print the same eight digits as the plugin's own 0x000801 record, and reading that back returns the
    /// other one. The answer must say the plugin is out of window instead of stating a form that lies.</summary>
    [Fact]
    public void ARecordOutsideTheEslWindowGetsNoRuntimeFormIdButAReason()
    {
        using var w = new World();
        var response = Read(w, World.Fid(w.WideWeapon));
        Assert.DoesNotContain("runtime=FE", response);
        Assert.Contains("ESL window", response);
    }

    [Fact]
    public void TheInWindowRecordOfTheSameUncompactedPluginStillPrintsItsRuntimeFormId()
    {
        using var w = new World();
        Served(Read(w, World.Fid(w.NarrowWeapon)), "runtime=" + World.LightRuntime(1, w.NarrowWeapon));
    }

    // ---- the tables follow the order --------------------------------------------------------------

    [Fact]
    public void TheLightIndexMovesWhenTheLoadOrderDoes()
    {
        using var w = new World();
        Served(Read(w, World.Fid(w.LightWeapon)), "runtime=" + World.LightRuntime(0, w.LightWeapon));

        w.ActivateSecondLightPluginFirst();

        Served(Read(w, World.Fid(w.LightWeapon)), "runtime=" + World.LightRuntime(1, w.LightWeapon));
        Served(Read(w, World.LightRuntime(1, w.LightWeapon)), "HcRtLightWeapon", World.LightName);
    }

    // ---- a runtime FormID is read-only: every write door refuses it ---------------------------------

    [Fact]
    public void ApplyRefusesARuntimeFormIdAndNamesThePluginForm()
    {
        using var w = new World();
        var outcome = w.Svc.ApplyEdits(
            new[] { new BulkOp { Formid = Runtime(w), FieldPath = "BasicStats.Damage", Verb = "Set", Value = "99" } },
            "HcRtPatch", null);
        RefusedNothingWritten(w, outcome.Error);
    }

    /// <summary>create's only FormID is parent=, which also takes an EditorID — a runtime FormID there is still
    /// refused, rather than falling through to "no sibling of that name was declared earlier".</summary>
    [Fact]
    public void CreateRefusesARuntimeFormIdParentAndNamesThePluginForm()
    {
        using var w = new World();
        var outcome = w.Svc.CreateRecordsBatch(
            new[] { new CreateOp { RecordType = "Weapon", Editorid = "HcRtNewWeapon", Parent = Runtime(w) } },
            "HcRtPatch", null);
        RefusedNothingWritten(w, outcome.Error);
    }

    /// <summary>A runtime token the index cannot translate — an FF dynamic form — is bad input, so it comes back as
    /// this record's own problem rather than escaping as an internal failure.</summary>
    [Fact]
    public void CreateReportsAnUntranslatableRuntimeParentAsThisRecordsProblem()
    {
        using var w = new World();
        var outcome = w.Svc.CreateRecordsBatch(
            new[] { new CreateOp { RecordType = "Weapon", Editorid = "HcRtDynParent", Parent = "FF001234" } },
            "HcRtPatch", null);
        Assert.False(outcome.Success);
        Assert.NotNull(outcome.Error);
        Assert.Contains("parent:", outcome.Error);
        Assert.Contains("dynamic form", outcome.Error);
        Assert.Equal(w.ModFolders, Directory.GetDirectories(w.ModsDir).Length);
    }

    /// <summary>parent= also takes a same-call sibling's EditorID, and modders write eight-hex ones — such a parent
    /// is a reference, never a FormID, so the nested create still lands.</summary>
    [Fact]
    public void CreateTakesAnEightHexSiblingEditoridAsAParent()
    {
        using var w = new World();
        var outcome = w.Svc.CreateRecordsBatch(
            new[]
            {
                new CreateOp { RecordType = "DialogTopic", Editorid = "DEADBEEF" },
                new CreateOp { RecordType = "DialogResponses", Editorid = "HcRtSiblingLine", Parent = "DEADBEEF" },
            },
            "HcRtSiblingPatch", null);
        Assert.True(outcome.Success, "refused: " + outcome.Error);
    }

    [Fact]
    public void RemoveRefusesARuntimeFormIdAndNamesThePluginForm()
    {
        using var w = new World();
        var outcome = w.Svc.RemoveRecords(new[] { Runtime(w) }, patch: "HcRtPatch.esp");
        RefusedNothingWritten(w, outcome.Error);
    }

    [Fact]
    public void ForwardRefusesARuntimeFormIdAndNamesThePluginForm()
    {
        using var w = new World();
        var outcome = w.Svc.ForwardRecords(new[] { Runtime(w) }, World.MasterName, "HcRtPatch", null);
        RefusedNothingWritten(w, outcome.Error);
    }

    [Fact]
    public void CopyRefusesARuntimeFormIdAndNamesThePluginForm()
    {
        using var w = new World();
        var response = CopyTools.Copy(w.Svc, from: Runtime(w), seed_paths: new[] { "BasicStats" },
                                      new_editorid: "HcRtCopyClone");
        RefusedNothingWritten(w, response);
    }

    [Fact]
    public void CopyNpcAppearanceRefusesARuntimeFormIdAndNamesThePluginForm()
    {
        using var w = new World();
        var outcome = w.Svc.CopyNpcAppearance(Runtime(w), null, null, null, "HcRtNpcClone", null, null, null);
        RefusedNothingWritten(w, outcome.Error);
    }

    /// <summary>The same door parses the target too — a well-formed token there is declined in the refusal's own
    /// words, not wrapped in the "bad target formid" sentence a malformed one gets.</summary>
    [Fact]
    public void CopyNpcAppearanceRefusesARuntimeTargetFormIdAndNamesThePluginForm()
    {
        using var w = new World();
        var outcome = w.Svc.CopyNpcAppearance(World.Fid(w.FullWeapon), null, null, Runtime(w), null, null, null, null);
        RefusedNothingWritten(w, outcome.Error);
        Assert.DoesNotContain("bad target formid", outcome.Error);
    }

    [Fact]
    public void PlaceAssetRefusesARuntimeFormIdAndNamesThePluginForm()
    {
        using var w = new World();
        var response = PlaceAssetTools.PlaceAsset(w.Svc, formid: Runtime(w), kind: "mesh");
        RefusedNothingWritten(w, response);
    }

    [Fact]
    public void BulkPlaceAssetRefusesARuntimeFormIdAndNamesThePluginForm()
    {
        using var w = new World();
        var response = PlaceAssetTools.BulkPlaceAsset(
            w.Svc, new[] { new PlaceAssetSpec { Formid = Runtime(w), Kind = "mesh" } });
        RefusedNothingWritten(w, response);
    }

    /// <summary>The token every write door above refuses still reads — the ruling is read-only, not banned.</summary>
    [Fact]
    public void TheTokenTheWriteDoorsRefuseStillReads()
    {
        using var w = new World();
        Served(Read(w, Runtime(w)), "HcRtLightWeapon", World.LightName);
    }

    // ---- helpers ----------------------------------------------------------------------------------

    /// <summary>The runtime FormID of the light plugin's weapon — the token the write doors are given.</summary>
    static string Runtime(World w) => World.LightRuntime(0, w.LightWeapon);

    /// <summary>A write refusal that hands back the plugin form to paste in the token's place, having written
    /// nothing: no mod folder was created for the patch.</summary>
    static void RefusedNothingWritten(World w, string? message)
    {
        Assert.NotNull(message);
        Assert.Contains("runtime FormID", message);
        Assert.Contains(World.Fid(w.LightWeapon), message);
        Assert.Equal(w.ModFolders, Directory.GetDirectories(w.ModsDir).Length);
    }

    static string Read(World w, string formid)
        => RecordsTools.Records(w.Svc, formids: new[] { formid });

    /// <summary>The cross-plugin scan lane over one plugin's weapons — the summary rows, not a formids= read.</summary>
    static string Scan(World w, string plugin, string? format = null)
        => RecordsTools.Records(w.Svc, types: new[] { "WEAP" },
                                plugins: new RecordsTools.RecordsScope { names = new[] { plugin } }, format: format);

    static void Served(string response, params string[] mustName)
    {
        Assert.False(response.StartsWith("error:", StringComparison.Ordinal), "refused: " + response);
        Assert.DoesNotContain("error=", response);
        foreach (var s in mustName) Assert.Contains(s, response);
    }

    /// <summary>The value of a "key=value" token in a rendered line.</summary>
    static string Token(string response, string key)
    {
        int i = response.IndexOf(key, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{key}' is not in the response: {response}");
        return new string(response[(i + key.Length)..].TakeWhile(c => !char.IsWhiteSpace(c)).ToArray());
    }

    /// <summary>
    /// A master, a full plugin, and two light plugins — the second one inactive until a test activates it, which
    /// pushes the first light plugin's index up by one.
    /// </summary>
    sealed class World : IDisposable
    {
        public const string MasterName = "HcRtMaster.esm";
        public const string FullName = "HcRtFull.esp";
        public const string LightName = "HcRtLight.esp";
        public const string SpareLightName = "HcRtSpare.esp";
        public const string WideName = "HcRtWide.esp";

        readonly string _root;
        readonly string _profileDir;
        readonly string _priorCorpusPath;

        public LoadOrderService Svc { get; }

        /// <summary>The instance's mods folder, and how many folders it held before any test ran — a write that
        /// should have been refused shows up here as a new patch folder.</summary>
        public string ModsDir { get; }
        public int ModFolders { get; }

        public FormKey FullWeapon { get; }
        public FormKey LightWeapon { get; }

        /// <summary>The 0x001801 record in the light-flagged, never-compacted plugin — outside the ESL window.</summary>
        public FormKey WideWeapon { get; }

        /// <summary>Its 0x000801 neighbour in the SAME plugin — the record a masked 0x001801 would collide with.</summary>
        public FormKey NarrowWeapon { get; }

        public static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

        /// <summary>What the game prints for a record in the light plugin at <paramref name="lightIndex"/>: the
        /// shared FE index, the light index, and the record's own low 12 bits.</summary>
        public static string LightRuntime(int lightIndex, FormKey fk) => $"FE{lightIndex:X3}{fk.ID:X3}";

        /// <summary>What the game prints for a record in the full plugin at <paramref name="loadIndex"/>.</summary>
        public static string FullRuntime(int loadIndex, FormKey fk) => $"{loadIndex:X2}{fk.ID:X6}";

        public World()
        {
            // CorpusRulebook.CorpusPath is a process-global: capture before repointing, restore on dispose.
            _priorCorpusPath = CorpusRulebook.CorpusPath;
            _root = Path.Combine(Path.GetTempPath(), "hc-runtime-formid-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "game", "Data"));

            var masterKey = new ModKey("HcRtMaster", ModType.Master);
            var master = new SkyrimMod(masterKey, SkyrimRelease.SkyrimSE);
            var mw = master.Weapons.AddNew(); mw.EditorID = "HcRtMasterWeapon";
            mw.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1 };

            var full = new SkyrimMod(new ModKey("HcRtFull", ModType.Plugin), SkyrimRelease.SkyrimSE);
            var fw = full.Weapons.AddNew(); fw.EditorID = "HcRtFullWeapon";
            fw.BasicStats = new WeaponBasicStats { Damage = 20, Weight = 1 };
            FullWeapon = fw.FormKey;

            // An esp-fe: the .esp extension with the light flag set in the header, which is how most light
            // plugins ship. The tables must read the FLAG, not the filename.
            var light = new SkyrimMod(new ModKey("HcRtLight", ModType.Plugin), SkyrimRelease.SkyrimSE) { IsSmallMaster = true };

            var lw = light.Weapons.AddNew(); lw.EditorID = "HcRtLightWeapon";
            lw.BasicStats = new WeaponBasicStats { Damage = 30, Weight = 1 };
            LightWeapon = lw.FormKey;

            var spare = new SkyrimMod(new ModKey("HcRtSpare", ModType.Plugin), SkyrimRelease.SkyrimSE) { IsSmallMaster = true };

            var sw = spare.Weapons.AddNew(); sw.EditorID = "HcRtSpareWeapon";
            sw.BasicStats = new WeaponBasicStats { Damage = 40, Weight = 1 };

            // The light-flagged but NEVER COMPACTED plugin: one record inside the ESL window and one above it.
            // It is written as a full plugin because Mutagen refuses to write an out-of-window record into a
            // light one; the header flag is flipped on disk afterwards, which is how such a plugin is made.
            var wideKey = new ModKey("HcRtWide", ModType.Plugin);
            var wide = new SkyrimMod(wideKey, SkyrimRelease.SkyrimSE);
            NarrowWeapon = new FormKey(wideKey, 0x000801);
            WideWeapon = new FormKey(wideKey, 0x001801);
            wide.Weapons.Add(new Weapon(NarrowWeapon, SkyrimRelease.SkyrimSE)
                { EditorID = "HcRtNarrowWeapon", BasicStats = new WeaponBasicStats { Damage = 50, Weight = 1 } });
            wide.Weapons.Add(new Weapon(WideWeapon, SkyrimRelease.SkyrimSE)
                { EditorID = "HcRtWideWeapon", BasicStats = new WeaponBasicStats { Damage = 60, Weight = 1 } });

            var instance = Path.Combine(_root, "inst");
            var mods = Path.Combine(instance, "mods");
            foreach (var d in new[] { "MasterMod", "FullMod", "LightMod", "SpareMod", "WideMod" })
                Directory.CreateDirectory(Path.Combine(mods, d));
            master.BeginWrite.ToPath(Path.Combine(mods, "MasterMod", MasterName))
                  .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            full.BeginWrite.ToPath(Path.Combine(mods, "FullMod", FullName))
                .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            light.BeginWrite.ToPath(Path.Combine(mods, "LightMod", LightName))
                 .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            spare.BeginWrite.ToPath(Path.Combine(mods, "SpareMod", SpareLightName))
                 .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            var widePath = Path.Combine(mods, "WideMod", WideName);
            wide.BeginWrite.ToPath(widePath)
                .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();
            SetLightFlag(widePath);

            File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
                "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");
            _profileDir = Path.Combine(instance, "profiles", "Default");
            Directory.CreateDirectory(_profileDir);
            WriteOrder(spareFirst: false);
            File.WriteAllText(Path.Combine(_profileDir, "modlist.txt"),
                "# header\r\n+WideMod\r\n+SpareMod\r\n+LightMod\r\n+FullMod\r\n+MasterMod\r\n");

            var genDir = Path.Combine(_root, "corpus-gen");
            CorpusGenerator.GenerateAll(genDir, Path.Combine(_root, "corpus-ref"));
            CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

            ModsDir = mods;
            ModFolders = Directory.GetDirectories(mods).Length;
            Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "user.json")));
        }

        /// <summary>Tick the spare light plugin ahead of the other one — the ordinary load-order movement that
        /// renumbers every light index below it.</summary>
        public void ActivateSecondLightPluginFirst() => WriteOrder(spareFirst: true);

        /// <summary>Set the light (ESL) flag in a written plugin's TES4 header — the record flags are the four
        /// bytes at offset 8, and 0x00000200 is the light bit.</summary>
        static void SetLightFlag(string path)
        {
            var bytes = File.ReadAllBytes(path);
            bytes[9] |= 0x02;
            File.WriteAllBytes(path, bytes);
        }

        void WriteOrder(bool spareFirst)
        {
            var names = spareFirst
                ? new[] { MasterName, FullName, SpareLightName, LightName, WideName }
                : new[] { MasterName, FullName, LightName, WideName };
            File.WriteAllText(Path.Combine(_profileDir, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", names) + "\r\n");
            File.WriteAllText(Path.Combine(_profileDir, "plugins.txt"), string.Join("\r\n", names.Select(n => "*" + n)) + "\r\n");
        }

        public void Dispose()
        {
            CorpusRulebook.CorpusPath = _priorCorpusPath;
            Svc.Dispose();
            try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
        }
    }
}

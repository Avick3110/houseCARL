using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
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

    // ---- helpers ----------------------------------------------------------------------------------

    static string Read(World w, string formid)
        => RecordsTools.Records(w.Svc, formids: new[] { formid });

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

        readonly string _root;
        readonly string _profileDir;

        public LoadOrderService Svc { get; }
        public FormKey FullWeapon { get; }
        public FormKey LightWeapon { get; }

        public static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

        /// <summary>What the game prints for a record in the light plugin at <paramref name="lightIndex"/>: the
        /// shared FE index, the light index, and the record's own low 12 bits.</summary>
        public static string LightRuntime(int lightIndex, FormKey fk) => $"FE{lightIndex:X3}{fk.ID:X3}";

        /// <summary>What the game prints for a record in the full plugin at <paramref name="loadIndex"/>.</summary>
        public static string FullRuntime(int loadIndex, FormKey fk) => $"{loadIndex:X2}{fk.ID:X6}";

        public World()
        {
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

            var instance = Path.Combine(_root, "inst");
            var mods = Path.Combine(instance, "mods");
            foreach (var d in new[] { "MasterMod", "FullMod", "LightMod", "SpareMod" })
                Directory.CreateDirectory(Path.Combine(mods, d));
            master.BeginWrite.ToPath(Path.Combine(mods, "MasterMod", MasterName))
                  .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            full.BeginWrite.ToPath(Path.Combine(mods, "FullMod", FullName))
                .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            light.BeginWrite.ToPath(Path.Combine(mods, "LightMod", LightName))
                 .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            spare.BeginWrite.ToPath(Path.Combine(mods, "SpareMod", SpareLightName))
                 .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
                "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");
            _profileDir = Path.Combine(instance, "profiles", "Default");
            Directory.CreateDirectory(_profileDir);
            WriteOrder(spareFirst: false);
            File.WriteAllText(Path.Combine(_profileDir, "modlist.txt"),
                "# header\r\n+SpareMod\r\n+LightMod\r\n+FullMod\r\n+MasterMod\r\n");

            Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "user.json")));
        }

        /// <summary>Tick the spare light plugin ahead of the other one — the ordinary load-order movement that
        /// renumbers every light index below it.</summary>
        public void ActivateSecondLightPluginFirst() => WriteOrder(spareFirst: true);

        void WriteOrder(bool spareFirst)
        {
            var names = spareFirst
                ? new[] { MasterName, FullName, SpareLightName, LightName }
                : new[] { MasterName, FullName, LightName };
            File.WriteAllText(Path.Combine(_profileDir, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", names) + "\r\n");
            File.WriteAllText(Path.Combine(_profileDir, "plugins.txt"), string.Join("\r\n", names.Select(n => "*" + n)) + "\r\n");
        }

        public void Dispose()
        {
            Svc.Dispose();
            try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
        }
    }
}

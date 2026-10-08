using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>load_order_status' filter=&lt;plugin&gt; names the folder serving the plugin and every other same-named copy
/// (#1001), and the header facts the localized read already opens: flags, masters and HEDR's record count (#1096).</summary>
[Trait("tier", "integration")]
public sealed class StatusPluginFactsTests : IClassFixture<StatusPluginFactsWorld>
{
    readonly StatusPluginFactsWorld _w;
    public StatusPluginFactsTests(StatusPluginFactsWorld w) => _w = w;

    string Lookup(string name) => StatusTools.LoadOrderStatus(_w.Svc, _w.Tools, filter: name);

    static string Records(string path) => BitConverter.ToInt32(File.ReadAllBytes(path), 34).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);   // HEDR's count, from the raw header

    static string Bytes(string path) => new FileInfo(path).Length.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " bytes";

    /// <summary>A dummy copy in an enabled folder serves the name while the real one sits in a disabled folder: both are named, with sizes, and the enabled one is the served one.</summary>
    [Fact]
    public void APluginInAnEnabledAndADisabledFolderNamesBothAndMarksTheServedOne()
    {
        var text = Lookup(StatusPluginFactsWorld.Dup);

        Assert.Contains("served from: mod 'DummyMod' (enabled) · " + Bytes(_w.DummyCopy), text);
        Assert.Contains("also in:     mod 'RealMod' (DISABLED) · " + Bytes(_w.RealCopy), text);
    }

    /// <summary>Two enabled folders hold the name: the higher-priority one serves, and the lower one is marked shadowed.</summary>
    [Fact]
    public void TwoEnabledCopiesMarkTheLowerOneShadowed()
    {
        var text = Lookup(StatusPluginFactsWorld.Two);

        Assert.Contains("served from: mod 'TwoHigh' (enabled) · " + Bytes(_w.TwoHighCopy) + "\n", text);
        Assert.Contains("also in:     mod 'TwoLow' (enabled) · " + Bytes(_w.TwoLowCopy) + " · shadowed\n", text);
    }

    /// <summary>The served copy need not be the first one found: a disabled mod's copy comes before game Data's, and game Data serves.</summary>
    [Fact]
    public void TheServedCopyIsTheFirstEnabledOneNotTheFirstFound()
    {
        var text = Lookup(StatusPluginFactsWorld.Base);

        Assert.Contains("served from: game Data · " + Bytes(_w.BaseDataCopy) + "\n", text);
        Assert.Contains("also in:     mod 'BaseOff' (DISABLED) · " + Bytes(_w.BaseOffCopy) + "\n", text);
    }

    /// <summary>A .esl with both header bits clear still loads as a light master, and the header line says why.</summary>
    [Fact]
    public void AnEslWithClearBitsSaysItLoadsLightByItsExtension()
    {
        var text = Lookup(StatusPluginFactsWorld.Lite);

        Assert.Equal(0u, BitConverter.ToUInt32(File.ReadAllBytes(_w.LiteCopy), 8) & 0x201);   // the fixture's master and ESL bits really are clear
        Assert.Contains("header:      master flag no, master by .esl extension · ESL flag no, light by .esl extension · ", text);
    }

    /// <summary>The ESL flag, the masters in order and the record count match what the fixture's TES4 header holds, read here from the raw bytes.</summary>
    [Fact]
    public void TheHeaderFactsMatchTheFixtureHeader()
    {
        var text = Lookup(StatusPluginFactsWorld.Hdr);

        var raw = File.ReadAllBytes(_w.HdrCopy);
        uint flags = BitConverter.ToUInt32(raw, 8);
        Assert.Equal("HEDR", System.Text.Encoding.ASCII.GetString(raw, 24, 4));
        int records = BitConverter.ToInt32(raw, 34);                 // HEDR: 4-byte type, 2-byte size, float version, then the count
        Assert.True((flags & 0x200) != 0 && (flags & 0x1) == 0);     // the fixture really is ESL-flagged and not master-flagged

        Assert.Contains("header:      master flag no · ESL flag YES · " + records.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " records (HEDR)", text);
        Assert.Contains("masters (2): HcFactA.esm, HcFactB.esm", text);
    }

    /// <summary>A plugin with nothing wrong in its header gets facts, never a warning or an unknown.</summary>
    [Fact]
    public void APluginWithAHealthyHeaderPrintsNoNewWarnings()
    {
        var text = Lookup(StatusPluginFactsWorld.Hdr);

        Assert.DoesNotContain("[!]", text);
        Assert.DoesNotContain("UNKNOWN", text);
        Assert.DoesNotContain("also in:", text);
    }

    /// <summary>Nothing serves the name and one copy exists: its header is read, and the line names that copy as not served.</summary>
    [Fact]
    public void ALoneUnservedCopysHeaderIsNamedAsFromThatCopy()
    {
        var text = Lookup(StatusPluginFactsWorld.Off);

        Assert.Contains("served from: none — no enabled folder holds this file\n", text);
        Assert.Contains("header:      from mod 'OffOnly' (DISABLED), not served · master flag no · ESL flag no · " + Records(_w.OffCopy) + " records (HEDR)\n", text);
    }

    /// <summary>Several copies and none serving: no copy is the plugin's, so no header is read and the localized line says so.</summary>
    [Fact]
    public void SeveralUnservedCopiesReadNoHeader()
    {
        var text = Lookup(StatusPluginFactsWorld.Many);

        Assert.DoesNotContain("header:", text);
        Assert.DoesNotContain("masters (", text);
        Assert.Contains("localized:   UNKNOWN — several copies and none served, so no header was read.", text);
    }

    /// <summary>A listed name with no file anywhere: the localized line says no read was tried, not that a read failed.</summary>
    [Fact]
    public void ANameWithNoFileSaysNoHeaderWasRead()
    {
        var text = Lookup(StatusPluginFactsWorld.Gone);

        Assert.Contains("served from: none — no folder holds a file of this name\n", text);
        Assert.Contains("localized:   UNKNOWN — no file of this name, so no header was read.", text);
    }

    /// <summary>Ticked in plugins.txt but missing from loadorder.txt: the ACTIVE line and the facts agree it is a plugin.</summary>
    [Fact]
    public void ATickedPluginMissingFromLoadorderStillGetsItsFacts()
    {
        var text = Lookup(StatusPluginFactsWorld.Stale);

        Assert.Contains("as a plugin: ACTIVE", text);
        Assert.Contains("served from: mod 'StaleMod' (enabled) · " + Bytes(_w.StaleCopy) + "\n", text);
    }
}

/// <summary>A synthetic MO2 instance: two master files, one ESL-flagged plugin overriding a record from each, and one
/// plugin filename that an enabled mod provides as an empty dummy and a disabled mod provides as the real file.</summary>
public sealed class StatusPluginFactsWorld : IDisposable
{
    public string Root { get; }
    public LoadOrderService Svc { get; }
    public ToolPathResolver Tools { get; }
    public string DummyCopy { get; }
    public string RealCopy { get; }
    public string HdrCopy { get; }
    public string TwoHighCopy { get; }
    public string TwoLowCopy { get; }
    public string BaseDataCopy { get; }
    public string BaseOffCopy { get; }
    public string LiteCopy { get; }
    public string StaleCopy { get; }
    public string OffCopy { get; }

    public const string Dup = "HcFactDup.esp";
    public const string Hdr = "HcFactHdr.esp";
    public const string Two = "HcFactTwo.esp";
    public const string Base = "HcFactBase.esp";
    public const string Lite = "HcFactLite.esl";
    public const string Off = "HcFactOff.esp";
    public const string Many = "HcFactMany.esp";
    public const string Gone = "HcFactGone.esp";
    public const string Stale = "HcFactStale.esp";

    public StatusPluginFactsWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-status-facts-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profile = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        foreach (var d in new[] { profile, mods, Path.Combine(Root, "game", "Data") }) Directory.CreateDirectory(d);

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        var a = Master(mods, "AMod", "HcFactA");
        var b = Master(mods, "BMod", "HcFactB");

        var hdr = new SkyrimMod(ModKey.FromFileName(Hdr), SkyrimRelease.SkyrimSE);
        hdr.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small;
        hdr.Weapons.Add(new Weapon(new FormKey(a.ModKey, 0x801), SkyrimRelease.SkyrimSE) { EditorID = "HcFactAWeap" });
        hdr.Weapons.Add(new Weapon(new FormKey(b.ModKey, 0x801), SkyrimRelease.SkyrimSE) { EditorID = "HcFactBWeap" });
        HdrCopy = Save(mods, "HdrMod", hdr, a, b);

        var dummy = new SkyrimMod(ModKey.FromFileName(Dup), SkyrimRelease.SkyrimSE);
        DummyCopy = Save(mods, "DummyMod", dummy);
        var real = new SkyrimMod(ModKey.FromFileName(Dup), SkyrimRelease.SkyrimSE);
        for (uint i = 0; i < 5; i++)
            real.Weapons.Add(new Weapon(new FormKey(real.ModKey, 0x801 + i), SkyrimRelease.SkyrimSE) { EditorID = "HcFactRealWeap" + i });
        RealCopy = Save(mods, "RealMod", real);

        TwoHighCopy = Save(mods, "TwoHigh", new SkyrimMod(ModKey.FromFileName(Two), SkyrimRelease.SkyrimSE));
        TwoLowCopy = Save(mods, "TwoLow", Filled(Two, 3));
        BaseDataCopy = Save(Path.Combine(Root, "game"), "Data", Filled(Base, 2));
        BaseOffCopy = Save(mods, "BaseOff", new SkyrimMod(ModKey.FromFileName(Base), SkyrimRelease.SkyrimSE));
        LiteCopy = Save(mods, "LiteMod", Filled(Lite, 1));
        using (var f = new FileStream(LiteCopy, FileMode.Open, FileAccess.ReadWrite))   // clear the master and ESL bits Mutagen may set for a .esl
        {
            var flags = new byte[4];
            f.Position = 8; f.ReadExactly(flags);
            uint v = BitConverter.ToUInt32(flags) & ~0x201u;
            f.Position = 8; f.Write(BitConverter.GetBytes(v));
        }

        OffCopy = Save(mods, "OffOnly", Filled(Off, 4));
        Save(mods, "ManyA", Filled(Many, 1));
        Save(mods, "ManyB", Filled(Many, 2));
        StaleCopy = Save(mods, "StaleMod", Filled(Stale, 1));

        var order = new[] { "HcFactA.esm", "HcFactB.esm", Lite, Hdr, Dup, Two, Base };
        var unticked = new[] { Off, Many, Gone };   // listed but unchecked: plugins with no copy, one disabled copy, two
        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", order.Concat(unticked)) + "\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"),
            string.Join("\r\n", order.Append(Stale).Select(p => "*" + p).Concat(unticked)) + "\r\n");   // Stale is ticked but not in loadorder.txt
        File.WriteAllText(Path.Combine(profile, "modlist.txt"),
            "# header\r\n+DummyMod\r\n-RealMod\r\n+HdrMod\r\n+BMod\r\n+AMod\r\n+TwoHigh\r\n+TwoLow\r\n-BaseOff\r\n+LiteMod\r\n" +
            "-OffOnly\r\n-ManyA\r\n-ManyB\r\n+StaleMod\r\n");

        var store = new UserConfigStore(Path.Combine(Root, "houseCARL.user.json"));
        Svc = LoadOrderService.WithInstance(instance, 0, store);
        Tools = new ToolPathResolver(store);
    }

    static SkyrimMod Master(string mods, string folder, string name)
    {
        var m = new SkyrimMod(new ModKey(name, ModType.Master), SkyrimRelease.SkyrimSE);
        m.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Master;
        m.Weapons.Add(new Weapon(new FormKey(m.ModKey, 0x801), SkyrimRelease.SkyrimSE) { EditorID = name + "Weap" });
        Save(mods, folder, m);
        return m;
    }

    static SkyrimMod Filled(string name, int weapons)
    {
        var m = new SkyrimMod(ModKey.FromFileName(name), SkyrimRelease.SkyrimSE);
        for (uint i = 0; i < weapons; i++)
            m.Weapons.Add(new Weapon(new FormKey(m.ModKey, 0x801 + i), SkyrimRelease.SkyrimSE) { EditorID = m.ModKey.Name + "Weap" + i });
        return m;
    }

    static string Save(string mods, string folder, SkyrimMod m, params ISkyrimModGetter[] masters)
    {
        var dir = Path.Combine(mods, folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, m.ModKey.FileName.String);
        m.BeginWrite.ToPath(path).WithLoadOrder(masters).Write();
        return path;
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

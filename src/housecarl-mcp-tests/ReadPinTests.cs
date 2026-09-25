using System.Reflection;
using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A source= pole's view, MO2 roots and Data folder all come from one profile across a profile switch.</summary>
[Trait("tier", "integration")]
public sealed class ReadPinTests : IDisposable
{
    const string BaseName = "HcRpBase.esp";
    const string OffName = "HcRpOff.esp";
    const string OffMod = "OffMod";
    const string NameA = "HcRpNameA";
    const string NameB = "HcRpNameB";
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    readonly string _root;
    readonly string _instance;
    readonly string _ini;
    readonly string _dataA;
    readonly string _dataB;
    readonly string _weapon;
    readonly LoadOrderService _svc;
    readonly ManualResetEventSlim _switcherReady = new();
    readonly ManualResetEventSlim _switcherInGate = new();
    Task? _switcher;

    public ReadPinTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-read-pin-" + Guid.NewGuid().ToString("N"));
        _instance = Path.Combine(_root, "instance");
        var mods = Path.Combine(_instance, "mods");
        _dataA = Path.Combine(_root, "gameA", "Data");
        _dataB = Path.Combine(_root, "gameB", "Data");
        foreach (var d in new[] { _dataA, _dataB, Path.Combine(mods, "BaseMod"), Path.Combine(mods, OffMod) })
            Directory.CreateDirectory(d);
        _ini = Path.Combine(_instance, "ModOrganizer.ini");
        File.WriteAllText(_ini, IniFor("Default", Path.GetDirectoryName(_dataA)!));

        var baseMod = new SkyrimMod(ModKey.FromFileName(BaseName), SkyrimRelease.SkyrimSE);
        baseMod.Weapons.AddNew().EditorID = "HcRpBaseWeap";
        baseMod.BeginWrite.ToPath(Path.Combine(mods, "BaseMod", BaseName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        // The localized off-order copy has no tables beside it: game A's Strings name it NameA, game B's NameB.
        var offKey = ModKey.FromFileName(OffName);
        var off = new SkyrimMod(offKey, SkyrimRelease.SkyrimSE) { UsingLocalization = true };
        var weap = new Weapon(new FormKey(offKey, 0x800), SkyrimRelease.SkyrimSE)
                   { EditorID = "HcRpOffWeap", Name = new TranslatedString(Language.English, NameA) };
        off.Weapons.Add(weap);
        _weapon = weap.FormKey.ToString();
        WriteWithTablesIn(off, Path.Combine(mods, OffMod), _dataA);
        weap.Name = new TranslatedString(Language.English, NameB);
        var scratch = Path.Combine(_root, "scratch");
        Directory.CreateDirectory(scratch);
        WriteWithTablesIn(off, scratch, _dataB);

        // Default disables OffMod; Other enables it with the plugin unticked, so both profiles have the same active order.
        WriteProfile("Default", "*" + BaseName + "\r\n", "+BaseMod\r\n-" + OffMod + "\r\n");
        WriteProfile("Other", "*" + BaseName + "\r\n" + OffName + "\r\n", "+" + OffMod + "\r\n+BaseMod\r\n");

        _svc = LoadOrderService.WithInstance(_instance, 0, new UserConfigStore(Path.Combine(_root, "houseCARL.user.json")));
    }

    public void Dispose()
    {
        try { _switcher?.Wait(); } catch (AggregateException) { /* a joined test already rethrew it */ }
        _svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }

    static string IniFor(string profile, string game)
        => "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(" + profile + ")\r\ngamePath=@ByteArray("
           + game.Replace(@"\", @"\\") + ")\r\n";

    void WriteProfile(string name, string plugins, string modlist)
    {
        var dir = Path.Combine(_instance, "profiles", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "loadorder.txt"), "# header\r\n" + BaseName + "\r\n" + OffName + "\r\n");
        File.WriteAllText(Path.Combine(dir, "plugins.txt"), plugins);
        File.WriteAllText(Path.Combine(dir, "modlist.txt"), "# header\r\n" + modlist);
    }

    /// <summary>Write the plugin into <paramref name="dir"/> and move the tables it wrote beside it into <paramref name="data"/>.</summary>
    static void WriteWithTablesIn(SkyrimMod mod, string dir, string data)
    {
        mod.BeginWrite.ToPath(Path.Combine(dir, OffName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        Directory.Move(Path.Combine(dir, "Strings"), Path.Combine(data, "Strings"));
    }

    object Gate() => typeof(LoadOrderService).GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_svc)!;

    void SelectOtherProfileAndGameB() => File.WriteAllText(_ini, IniFor("Other", Path.GetDirectoryName(_dataB)!));

    /// <summary>Selects Other and game B, then starts a switcher that takes the service's gate and re-derives the roots.</summary>
    void SwitchFromAnotherThread()
    {
        _svc.AfterReadPinForGuard = null;
        SelectOtherProfileAndGameB();
        var gate = Gate();
        _switcher = Task.Run(() =>
        {
            _switcherReady.Set();
            lock (gate) { _switcherInGate.Set(); _svc.CaptureView(); }
        });
        Assert.True(_switcherReady.Wait(Timeout), "the switcher never started");
        // Held by this thread, the gate stops the switcher until the hold ends; not held, the switcher must be past it.
        Assert.True(Monitor.IsEntered(gate) || _switcherInGate.Wait(Timeout), "the switcher never took the free gate");
    }

    (LoadOrderService.PoleInfo Pole, string Name) Read(IReadOnlyList<string> formids)
    {
        var outcomes = _svc.ResolveBatchFromPole(formids, OffName, OffMod, new[] { "Name" }, 1, false, null,
                                                 out var pole, out var refusal, out _);
        Assert.Null(refusal);
        var o = Assert.Single(outcomes);
        Assert.Null(o.Error);
        return (pole!, o.Record!.Fields.Single(f => f.Path == "Name").Token!);
    }

    LoadOrderService.PoleInfo Probe()
    {
        var pole = _svc.ProbeSourceArm(OffName, OffMod, out var error);
        Assert.Null(error);
        return pole!;
    }

    [Fact]
    public void AProfileSwitchAfterThePinDoesNotSplitThePoleFromItsRoots()
    {
        var before = Read(new[] { _weapon });                                   // warms the index on the Default profile
        Assert.False(before.Pole.InOrder);
        Assert.Equal(_dataA, before.Pole.DataDir);
        Assert.Equal(NameA, before.Name);
        _svc.AfterReadPinForGuard = SwitchFromAnotherThread;

        // The FormIDs are read after the locate and before the overlay opens; the switch has landed by then.
        var during = Read(new JoinOnFirstRead(_weapon, () => _switcher!.GetAwaiter().GetResult()));

        // Located under the Default profile's roots, as the view is, and opened with game A's Data folder.
        Assert.Equal(before.Pole.Where, during.Pole.Where);
        Assert.Equal(_dataA, during.Pole.DataDir);
        Assert.Equal(NameA, during.Name);

        // The switch landed for the next call: Other enables OffMod and reads game B's tables.
        var after = Read(new[] { _weapon });
        Assert.NotEqual(before.Pole.Where, after.Pole.Where);
        Assert.Equal(_dataB, after.Pole.DataDir);
        Assert.Equal(NameB, after.Name);
    }

    [Fact]
    public void ASwitchToAProfileWithNoActivePluginsKeepsTheOldRootsWithTheOldView()
    {
        var before = Probe();                                                   // warms the index on the Default profile
        Assert.Equal(_dataA, before.DataDir);

        // Other's order files are empty, the shape of MO2 mid-write, so its order is empty and the old build is kept.
        var other = Path.Combine(_instance, "profiles", "Other");
        File.WriteAllText(Path.Combine(other, "loadorder.txt"), "");
        File.WriteAllText(Path.Combine(other, "plugins.txt"), "");
        SelectOtherProfileAndGameB();
        var during = Probe();
        Assert.Equal(before.Epoch, during.Epoch);
        Assert.Equal(before.Where, during.Where);
        Assert.Equal(_dataA, during.DataDir);

        // Once Other lists its plugins, the next call takes its order and its roots together.
        File.WriteAllText(Path.Combine(other, "loadorder.txt"), "# header\r\n" + BaseName + "\r\n" + OffName + "\r\n");
        File.WriteAllText(Path.Combine(other, "plugins.txt"), "*" + BaseName + "\r\n*" + OffName + "\r\n");
        var after = Probe();
        Assert.True(after.InOrder);
        Assert.NotEqual(before.Epoch, after.Epoch);
        Assert.Equal(_dataB, ((ILoadOrderHost)_svc).CaptureRoots().DataDir);
    }

    [Fact]
    public void ASwitchToAProfileHeldOpenKeepsTheOldRoots()
    {
        _svc.CaptureView();                                                     // warms the index on the Default profile
        var roots = (ILoadOrderHost)_svc;
        var otherPlugins = Path.Combine(_instance, "profiles", "Other", "plugins.txt");

        // MO2 holding Other's plugins.txt makes its read throw ProfileUnreadableException, so the record lane refuses.
        using (new FileStream(otherPlugins, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            SelectOtherProfileAndGameB();
            Assert.ThrowsAny<ProfileUnreadableException>(() => _svc.CaptureView());
            Assert.Equal("Default", _svc.ProfileName);
            Assert.Equal(_dataA, roots.CaptureRoots().DataDir);
        }

        // Released, the next call takes Other's order and its roots together.
        _svc.CaptureView();
        Assert.Equal("Other", _svc.ProfileName);
        Assert.Equal(_dataB, roots.CaptureRoots().DataDir);
    }

    /// <summary>A one-item list that runs <paramref name="onFirstRead"/> the first time its item is read.</summary>
    sealed class JoinOnFirstRead(string item, Action onFirstRead) : IReadOnlyList<string>
    {
        bool _fired;
        public int Count => 1;

        public string this[int index]
        {
            get
            {
                if (index != 0) throw new ArgumentOutOfRangeException(nameof(index));
                if (!_fired) { _fired = true; onFirstRead(); }
                return item;
            }
        }

        public IEnumerator<string> GetEnumerator() { yield return this[0]; }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A source= pole is located under the roots taken in the same hold as its view, and its overlay opens with
/// the Data folder of those roots, even when MO2 switches profile and game folder mid-call.</summary>
[Trait("tier", "integration")]
public sealed class ReadPinTests : IDisposable
{
    const string BaseName = "HcRpBase.esp";
    const string OffName = "HcRpOff.esp";
    const string OffMod = "OffMod";
    const string NameA = "HcRpNameA";
    const string NameB = "HcRpNameB";

    readonly string _root;
    readonly string _ini;
    readonly string _dataA;
    readonly string _dataB;
    readonly string _weapon;
    readonly LoadOrderService _svc;
    Thread? _switcher;

    public ReadPinTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-read-pin-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(_root, "instance");
        var mods = Path.Combine(instance, "mods");
        _dataA = Path.Combine(_root, "gameA", "Data");
        _dataB = Path.Combine(_root, "gameB", "Data");
        foreach (var d in new[] { _dataA, _dataB, Path.Combine(mods, "BaseMod"), Path.Combine(mods, OffMod) })
            Directory.CreateDirectory(d);
        _ini = Path.Combine(instance, "ModOrganizer.ini");
        File.WriteAllText(_ini, IniFor("Default", Path.GetDirectoryName(_dataA)!));

        var baseMod = new SkyrimMod(ModKey.FromFileName(BaseName), SkyrimRelease.SkyrimSE);
        baseMod.Weapons.AddNew().EditorID = "HcRpBaseWeap";
        baseMod.BeginWrite.ToPath(Path.Combine(mods, "BaseMod", BaseName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        // The off-order copy is localized and its folder carries no tables, so its name comes from the Data folder's
        // Strings: game A's table says NameA, game B's says NameB.
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

        // Default: OffMod is disabled. Other: OffMod is enabled and the plugin unchecked. The active order is the
        // same in both, so the switch re-derives the roots without swapping the resolver.
        WriteProfile(instance, "Default", "*" + BaseName + "\r\n", "+BaseMod\r\n-" + OffMod + "\r\n");
        WriteProfile(instance, "Other", "*" + BaseName + "\r\n" + OffName + "\r\n", "+" + OffMod + "\r\n+BaseMod\r\n");

        _svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "houseCARL.user.json")));
    }

    public void Dispose()
    {
        _switcher?.Join();
        _svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }

    static string IniFor(string profile, string game)
        => "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(" + profile + ")\r\ngamePath=@ByteArray("
           + game.Replace(@"\", @"\\") + ")\r\n";

    static void WriteProfile(string instance, string name, string plugins, string modlist)
    {
        var dir = Path.Combine(instance, "profiles", name);
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

    /// <summary>Points MO2 at the Other profile and game B, then re-derives the roots on another thread, which waits
    /// for the gate. Inside the pole lane's hold it is still waiting when the roots are read; outside it, it has run.</summary>
    void SwitchFromAnotherThread()
    {
        _svc.AfterReadPinForGuard = null;
        File.WriteAllText(_ini, IniFor("Other", Path.GetDirectoryName(_dataB)!));
        _switcher = new Thread(() => _svc.CaptureView());
        _switcher.Start();
        SpinWait.SpinUntil(() => !_switcher.IsAlive || _switcher.ThreadState.HasFlag(ThreadState.WaitSleepJoin),
                           TimeSpan.FromSeconds(30));
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

    [Fact]
    public void AProfileSwitchAfterThePinDoesNotSplitThePoleFromItsRoots()
    {
        var before = Read(new[] { _weapon });                                   // warms the index on the Default profile
        Assert.False(before.Pole.InOrder);
        Assert.Equal(_dataA, before.Pole.DataDir);
        Assert.Equal(NameA, before.Name);
        _svc.AfterReadPinForGuard = SwitchFromAnotherThread;

        // The switcher has re-derived the roots by the time the FormIDs are read, which is after the locate and
        // before the overlay opens.
        var during = Read(new JoinOnFirstRead(_weapon, () => _switcher!.Join()));

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

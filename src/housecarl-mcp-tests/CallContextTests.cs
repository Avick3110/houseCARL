using System.Reflection;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>One tool call reads one view of the load order: its records pinned at the first record read, its files
/// taken only at the first file read, and a profile change between the two refused.</summary>
[Trait("tier", "integration")]
public sealed class CallContextTests : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-call-context-");
    readonly ModKey _base = new("HcCcBase", ModType.Plugin), _late = new("HcCcLate", ModType.Plugin);
    readonly string _weapon;
    readonly LoadOrderService _svc;
    ILoadOrderHost Host => _svc;

    public CallContextTests()
    {
        var mod = new SkyrimMod(_base, SkyrimRelease.SkyrimSE);
        var w = mod.Weapons.AddNew();
        w.EditorID = "HcCcWeap";
        _weapon = ScratchMo2.Fid(w.FormKey);
        mod.BeginWrite.ToPath(_mo2.InMod("CcBase", _base)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        new SkyrimMod(_late, SkyrimRelease.SkyrimSE).BeginWrite.ToPath(_mo2.InMod("CcLate", _late)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        File.WriteAllText(Path.Combine(_mo2.ModsDir, "CcBase", "hc_cc.txt"), "loose");
        BaseOnly(_mo2.Profiles);
        BaseOnly(Path.Combine(_mo2.Instance, "profiles", "Other"));
        _svc = _mo2.Open();
    }

    public void Dispose() { _svc.Dispose(); _mo2.Delete(); }

    void BaseOnly(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "loadorder.txt"), "# header\r\n" + _base.FileName + "\r\n");
        File.WriteAllText(Path.Combine(dir, "plugins.txt"), "*" + _base.FileName + "\r\n");
        File.WriteAllText(Path.Combine(dir, "modlist.txt"), "# header\r\n+CcBase\r\n");
    }

    void AddLate()
    {
        var names = new[] { _base, _late }.Select(k => k.FileName.String).ToList();
        _mo2.Profile(string.Join("\r\n", names) + "\r\n", string.Join("", names.Select(n => "*" + n + "\r\n")), "+CcLate\r\n+CcBase\r\n");
    }

    void SelectOther()
    {
        var ini = Path.Combine(_mo2.Instance, "ModOrganizer.ini");
        File.WriteAllText(ini, File.ReadAllText(ini).Replace("@ByteArray(Default)", "@ByteArray(Other)"));
    }

    bool FilesBuilt() => typeof(LoadOrderService).GetField("_assetResolver", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_svc) is not null;

    /// <summary>Run <paramref name="body"/> as one tool call, which must be served.</summary>
    static void Call(Action body) => Assert.Equal("ok", Answer(body));

    /// <summary>Run <paramref name="body"/> as one tool call and return its rendered answer; a failed assertion inside it fails the test.</summary>
    static string Answer(Action body)
    {
        Xunit.Sdk.XunitException? failed = null;
        var answer = Guard.Tool("housecarl_probe", _ =>
        {
            try { body(); }
            catch (Xunit.Sdk.XunitException e) { failed = e; }
            return ToolAnswer.Served("ok");
        });
        if (failed is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failed);
        return answer;
    }

    /// <summary>Run <paramref name="body"/> outside any call, as another call's thread would.</summary>
    static void OutsideTheCall(Action body)
    {
        Task t;
        using (ExecutionContext.SuppressFlow()) t = Task.Run(body);
        t.Wait();
    }

    // Pins unchanged behaviour (R8): a records call never builds the file view, and the first file call does.
    [Fact]
    public void ARecordsOnlyCallBuildsNoFileView()
    {
        var answer = RecordsTools.Records(_svc, formids: new[] { _weapon });
        Assert.Contains("HcCcWeap", answer);
        Assert.False(FilesBuilt());
        Call(() => Host.CaptureAssets());
        Assert.True(FilesBuilt());
    }

    [Fact]
    public void OneCallReadsOnePinAcrossAChangeTheNextCallSees()
    {
        LoadOrderService.ViewPin? first = null, second = null;
        Call(() =>
        {
            first = _svc.CapturePin();
            AddLate();
            second = _svc.CapturePin();
            Assert.Same(first.Resolver, Host.Resolver);
        });
        Assert.Same(first, second);
        Assert.DoesNotContain(_late.FileName.String, second!.Resolver.PluginNames);
        Call(() => Assert.Contains(_late.FileName.String, _svc.CapturePin().Resolver.PluginNames));
    }

    [Fact]
    public void AWriteTakesItsPinInsideTheGateAndTheCallKeepsIt()
    {
        Call(() =>
        {
            var before = _svc.CapturePin();
            AddLate();
            LoadOrderService.ViewPin inGate;
            lock (Host.WriteGate) inGate = _svc.CapturePin();
            Assert.NotSame(before, inGate);
            Assert.Contains(_late.FileName.String, inGate.Resolver.PluginNames);
            Assert.Same(inGate, _svc.CapturePin());
        });
    }

    [Fact]
    public void AFileReadInsideTheWriteGateIsCheckedAgain()
    {
        Call(() =>
        {
            Host.CaptureAssets();
            var files = _svc.Counters.FileFreshnessChecks;
            lock (Host.WriteGate) Host.CaptureAssets();
            Assert.Equal(files + 1, _svc.Counters.FileFreshnessChecks);
        });
    }

    [Fact]
    public void ACallChecksRecordsOnceAndFilesOnceAtTheirFirstRead()
    {
        Call(() => Host.CaptureAssets());                       // warm both sides
        var (records, files) = (_svc.Counters.RecordFreshnessChecks, _svc.Counters.FileFreshnessChecks);
        Call(() =>
        {
            for (int i = 0; i < 3; i++) _ = Host.Resolver;
            Assert.Equal(files, _svc.Counters.FileFreshnessChecks);   // a records read checks no files
            for (int i = 0; i < 3; i++) Host.CaptureAssets();
        });
        // The profile is checked at the first record read and again at the first file read, which must refuse a switch between them.
        Assert.Equal(records + 2, _svc.Counters.RecordFreshnessChecks);
        Assert.Equal(files + 1, _svc.Counters.FileFreshnessChecks);
    }

    [Fact]
    public void APluginRebuiltByAnotherCallLeavesTheCallOnItsView()
    {
        Call(() =>
        {
            var view = _svc.CaptureView();
            var mod = new SkyrimMod(_base, SkyrimRelease.SkyrimSE);
            mod.Weapons.AddNew().EditorID = "HcCcWeapA";
            mod.Weapons.AddNew().EditorID = "HcCcWeapB";
            mod.BeginWrite.ToPath(Path.Combine(_mo2.ModsDir, "CcBase", _base.FileName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            OutsideTheCall(() => _ = Host.Resolver);            // another call re-indexes the changed plugin
            Assert.Equal(view.Epoch, _svc.CaptureView().Epoch);
        });
    }

    [Fact]
    public void ACallKeepsItsRootsAndFilesWhenAnotherCallSwitchesProfile()
    {
        Call(() => Host.CaptureAssets());                       // warm both sides on Default
        Call(() =>
        {
            _svc.CapturePin();
            var roots = Host.CaptureRoots();
            var files = Host.CaptureAssets();
            SelectOther();
            OutsideTheCall(() => Host.CaptureAssets());           // another call follows the switch and rebuilds the files
            Assert.Equal(roots, Host.CaptureRoots());
            Assert.Equal(roots, Host.ConfiguredRoots().Roots);
            Assert.Equal("Default", Host.CaptureAssets().ProfileName);
            Assert.Equal(files.Roots, Host.CaptureAssets().Roots);
        });
        Call(() => Assert.Equal("Other", Host.CaptureAssets().ProfileName));
    }

    void Change(string kind) { if (kind == "switch") SelectOther(); else AddLate(); }

    // A switch moves the roots; an added plugin keeps them and rebuilds the order.
    [Theory]
    [InlineData("switch")]
    [InlineData("plugin")]
    public void AProfileChangeBetweenTheRecordPinAndTheFirstFileReadRefusesTheCall(string kind)
    {
        Call(() => Host.CaptureAssets());                       // warm both sides on Default
        var answer = Answer(() =>
        {
            _svc.CapturePin();
            Change(kind);
            Host.CaptureAssets();
        });
        Assert.Equal(Wire.RefusalPrefix + CallContext.MovedBetweenViews, answer);
        Call(() => Host.CaptureAssets());                       // the rerun answers off the changed profile
    }

    [Theory]
    [InlineData("switch")]
    [InlineData("plugin")]
    public void AProfileChangeBetweenTheFirstFileReadAndTheRecordPinRefusesTheCall(string kind)
    {
        Call(() => Host.CaptureAssets());                       // warm both sides on Default
        var answer = Answer(() =>
        {
            Host.CaptureAssets();
            Change(kind);
            _svc.CapturePin();
        });
        Assert.Equal(Wire.RefusalPrefix + CallContext.MovedBetweenViews, answer);
    }
}

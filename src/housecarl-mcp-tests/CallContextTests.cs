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
        foreach (var profile in new[] { "Default", "Other" })
            _mo2.Profile(_base.FileName + "\r\n", "*" + _base.FileName + "\r\n", "+CcBase\r\n", profile);
        _svc = _mo2.Open();
    }

    public void Dispose() { _svc.Dispose(); _mo2.Delete(); }

    void AddLate()
    {
        var names = new[] { _base, _late }.Select(k => k.FileName.String).ToList();
        _mo2.Profile(string.Join("\r\n", names) + "\r\n", string.Join("", names.Select(n => "*" + n + "\r\n")), "+CcLate\r\n+CcBase\r\n");
    }

    void SelectOther() => _mo2.SelectProfile("Other");

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
        Exception? failed = null;
        var t = new Thread(() => { try { Assert.Null(CallContext.Open); body(); } catch (Exception e) { failed = e; } });
        t.UnsafeStart();                                         // no execution context, so none of the call's scopes
        t.Join();
        if (failed is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failed);
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
            lock (Host.WriteGate)
            {
                inGate = _svc.CapturePin();
                Assert.Same(inGate, _svc.CapturePin());          // a re-read of an unmoved build keeps the pin
            }
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
        var (profile, files) = (_svc.Counters.ProfileFreshnessChecks, _svc.Counters.FileFreshnessChecks);
        Call(() =>
        {
            for (int i = 0; i < 3; i++) _ = Host.Resolver;
            Assert.Equal(files, _svc.Counters.FileFreshnessChecks);   // a records read checks no files
            for (int i = 0; i < 3; i++) Host.CaptureAssets();
            Host.CapturePinAndAssets(null);                     // the one-hold capture reads the held files too
        });
        // The profile is checked at the first record read and again at the first file read, which must refuse a switch between them.
        Assert.Equal(profile + 2, _svc.Counters.ProfileFreshnessChecks);
        Assert.Equal(files + 1, _svc.Counters.FileFreshnessChecks);
    }

    [Fact]
    public void APluginRebuiltByAnotherCallLeavesTheCallOnItsView()
    {
        Call(() =>
        {
            var first = Host.Resolver;                           // the call's first record read, as a lane head takes it
            var view = _svc.CaptureView();
            var mod = new SkyrimMod(_base, SkyrimRelease.SkyrimSE);
            mod.Weapons.AddNew().EditorID = "HcCcWeapA";
            mod.Weapons.AddNew().EditorID = "HcCcWeapB";
            mod.BeginWrite.ToPath(Path.Combine(_mo2.ModsDir, "CcBase", _base.FileName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            string? rebuilt = null;
            OutsideTheCall(() => rebuilt = Host.Resolver.Epoch);   // another call re-indexes the changed plugin
            Assert.NotEqual(view.Epoch, rebuilt);
            Assert.Equal(view.Epoch, _svc.CaptureView().Epoch);
            Assert.Equal(view.Epoch, Host.Resolver.Capture().Epoch);   // the held resolver is a frozen copy, not the rebuilt one (r1 #1)
            Assert.Equal(view.Epoch, first.Capture().Epoch);
        });
    }

    // An asset-only write's first file read inside the gate takes its view: a record read after it takes the build its
    // files named, with no refresh and no refusal, and the change is the next call's.
    [Fact]
    public void ARecordReadAfterAWritesInGateFileReadTakesTheBuildItsFilesNamed()
    {
        Call(() => Host.CaptureAssets());                       // warm both sides on Default
        Call(() =>
        {
            lock (Host.WriteGate)
            {
                Host.CaptureAssets();
                AddLate();
                Assert.DoesNotContain(_late.FileName.String, _svc.CapturePin().Resolver.PluginNames);
            }
        });
        Call(() => Assert.Contains(_late.FileName.String, _svc.CapturePin().Resolver.PluginNames));
    }

    // Pins unchanged behaviour (r1 #5): an in-gate pin that moves the build lets go of the files the call held from
    // before the gate, so its post-commit files follow the pin.
    [Fact]
    public void AnInGatePinThatMovesTheBuildLetsGoOfTheCallsFiles()
    {
        Call(() => Host.CaptureAssets());                       // warm both sides on Default
        Call(() =>
        {
            _svc.CapturePin();
            Assert.Equal("Default", Host.CaptureAssets().ProfileName);
            SelectOther();
            lock (Host.WriteGate)
            {
                _svc.CapturePin();
                Assert.Equal("Other", Host.CaptureAssets().ProfileName);
            }
            Assert.Equal("Other", Host.CaptureAssets().ProfileName);
        });
    }

    // An in-gate pin adopts a mod-list change that kept the plugin paths: the call's held profile read is the new one.
    [Fact]
    public void AnInGatePinAdoptsAProfileChangeThatKeptThePlugins()
    {
        Call(() =>
        {
            _svc.CapturePin();
            _mo2.Profile(_base.FileName + "\r\n", "*" + _base.FileName + "\r\nHcCcGhost.esp\r\n", "+CcBase\r\n");
            lock (Host.WriteGate)
            {
                _svc.CapturePin();
                Assert.Contains("UNTICKED", Ghost());
                _mo2.Profile(_base.FileName + "\r\n", "*" + _base.FileName + "\r\n", "+CcBase\r\n");
                // Every later read in the gate serves the pin's profile read, the head's own reads included.
                Assert.Contains("HcCcGhost.esp", _svc.StatusData().Composition.InactivePluginNames);
            }
        });
    }

    // The reverse index a call builds through its frozen pin is the live resolver's too, so the next call does not walk the order again.
    [Fact]
    public void AReverseIndexBuiltThroughTheCallsPinIsKeptForTheNextCall()
    {
        Call(() => _svc.CaptureView().EnsureReverseIndex());
        OutsideTheCall(() => Assert.NotNull(Host.Resolver.ReverseIndex));
    }

    static bool Present(AssetCapture files) => files.View.Resolve("hc_cc.txt").Exists;

    // r1 #3: the held file view is a frozen copy, so another call's content refresh does not reach it.
    [Fact]
    public void AFileRefreshedByAnotherCallLeavesTheCallOnItsFiles()
    {
        Call(() => Host.CaptureAssets());                       // warm both sides
        Call(() =>
        {
            Assert.True(Present(Host.CaptureAssets()));          // warms the loose subtree
            File.Delete(Path.Combine(_mo2.ModsDir, "CcBase", "hc_cc.txt"));
            OutsideTheCall(() => Assert.False(Present(Host.CaptureAssets())));   // another call's content check rebuilds
            Assert.True(Present(Host.CaptureAssets()));
            Assert.True(Host.Assets.Capture().Resolve("hc_cc.txt").Exists);
        });
        Call(() => Assert.False(Present(Host.CaptureAssets())));
    }

    // Q2 (#1128): a file read during another call's write runs the content check, since that write reads its own frozen copy.
    [Fact]
    public void AFileReadDuringAnotherCallsWriteSeesAChangedFile()
    {
        Call(() => Assert.True(Present(Host.CaptureAssets())));  // warms the loose subtree
        File.Delete(Path.Combine(_mo2.ModsDir, "CcBase", "hc_cc.txt"));
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var writer = new Thread(() => { lock (Host.WriteGate) { held.Set(); release.Wait(); } });
        writer.Start();
        held.Wait();
        try { Call(() => Assert.False(Present(Host.CaptureAssets()))); }
        finally { release.Set(); writer.Join(); }
    }

    // Pins unchanged behaviour: a write refused before its commit builds no file view.
    [Fact]
    public void AWriteRefusedBeforeItsCommitBuildsNoFileView()
    {
        Call(() =>
        {
            var refused = _svc.ApplyEdits(new[] { new BulkOp { Formid = "000FFF:" + _base.FileName, FieldPath = "EditorID", Value = "HcCcNope" } }, "HcCcRefused", null);
            Assert.False(refused.Success);
        });
        Assert.False(FilesBuilt());
    }

    /// <summary>Move every root another call can follow: the mods and profiles under a new base folder, whose profile
    /// lists an unticked plugin, and the game under a new folder; both games' Data ship Papyrus sources.</summary>
    void MoveRoots()
    {
        var alt = Path.Combine(_mo2.Root, "alt");
        var game2 = Path.Combine(_mo2.Root, "game2");
        foreach (var f in Directory.GetFiles(Path.Combine(_mo2.ModsDir, "CcBase")))
        {
            Directory.CreateDirectory(Path.Combine(alt, "mods", "CcBase"));
            File.Copy(f, Path.Combine(alt, "mods", "CcBase", Path.GetFileName(f)));
        }
        var profile = Path.Combine(alt, "profiles", "Moved");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\n" + _base.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*" + _base.FileName + "\r\nHcCcGhost.esp\r\n");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "# header\r\n+CcBase\r\n");
        Directory.CreateDirectory(Path.Combine(game2, "Data", "Source", "Scripts"));
        File.WriteAllText(Path.Combine(game2, "Data", "Source", "Scripts", "HcCc.psc"), "ScriptName HcCc");
        File.WriteAllText(Path.Combine(_mo2.Instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Moved)\r\ngamePath=@ByteArray("
            + game2.Replace(@"\", @"\\") + ")\r\n[Settings]\r\nbase_directory=@ByteArray(" + alt.Replace(@"\", @"\\") + ")\r\n");
    }

    string? Ghost() => _svc.ResolveBatch(new[] { "000800:HcCcGhost.esp" }, null, false).Single().Error;

    // r1 #48: the head's root reads answer the call's roots and profile read, after another call followed a move.
    [Fact]
    public void TheHeadsRootReadsAnswerTheCallsRootsAfterAnotherCallMovedThem()
    {
        var source = Path.Combine(_mo2.DataDir, "Source", "Scripts");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "HcCc.psc"), "ScriptName HcCc");
        Call(() =>
        {
            _svc.CapturePin();
            Host.CaptureAssets();
            var status = _svc.StatusData();
            MoveRoots();
            OutsideTheCall(() => _ = Host.Resolver);             // another call follows the move
            var after = _svc.StatusData();
            Assert.Equal((status.ProfileDir, status.ModsDir, status.DataDir, status.ProfileName), (after.ProfileDir, after.ModsDir, after.DataDir, after.ProfileName));
            Assert.DoesNotContain("HcCcGhost.esp", after.Composition.InactivePluginNames);
            Assert.Equal(_mo2.ModsDir, _svc.ModsRootOrNull);
            Assert.Equal(Path.GetFullPath(source), _svc.PapyrusSourceImportDirs().GameDataSources);
            Assert.DoesNotContain("UNTICKED", Ghost());
        });
        Call(() => Assert.Contains("UNTICKED", Ghost()));          // the next call reads the moved roots
    }

    /// <summary>A status filter= in a call, after another call followed the roots elsewhere: the plugin's copy has not
    /// moved in the call's build, so no index move is reported.</summary>
    [Fact]
    public void AStatusFilterAfterAnotherCallMovedTheRootsReportsNoIndexMove()
    {
        Call(() =>
        {
            var status = _svc.StatusData();
            MoveRoots();
            OutsideTheCall(() => _ = Host.Resolver);             // another call follows the move
            Assert.Null(_svc.PluginFactsFor(_base.FileName, status)!.Moved);
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
            Assert.Equal("Default", Host.CapturePinAndAssets(null).Assets.ProfileName);   // the one-hold capture serves the held files
        });
        Call(() => Assert.Equal("Other", Host.CaptureAssets().ProfileName));
    }

    [Fact]
    public void AFilesFirstCallKeepsItsRootsWhenAnotherCallSwitchesProfile()
    {
        Call(() => Host.CaptureAssets());                       // warm both sides on Default
        Mo2Roots? held = null;
        Call(() =>
        {
            held = Host.CaptureAssets().Roots;
            Assert.Equal(held, Host.CaptureRoots());             // no switch: the same answer as before
            SelectOther();
            OutsideTheCall(() => Host.CaptureAssets());           // another call follows the switch
            Assert.Equal(held, Host.CaptureRoots());
            Assert.Equal(held, Host.ConfiguredRoots().Roots);
        });
        Call(() => Assert.NotEqual(held, Host.CaptureRoots()));   // the next call reads the switched profile
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

    // Pins unchanged behaviour: a file read inside the write gate re-takes the files, and the call keeps that view after
    // the gate; with no pin its roots stay the first file view's (known limits r1 #5, r2 #1).
    [Fact]
    public void AFileReadInsideTheWriteGateReplacesTheCallsFiles()
    {
        Call(() => { _ = Host.Resolver; Host.CaptureAssets(); });   // warm both sides on Default
        Call(() =>
        {
            var first = Host.CaptureAssets();
            Assert.Equal("Default", first.ProfileName);
            SelectOther();
            lock (Host.WriteGate) Assert.Equal("Other", Host.CaptureAssets().ProfileName);
            Assert.Equal("Other", Host.CaptureAssets().ProfileName);
            Assert.Equal(first.Roots, Host.CaptureAssets().Roots);
            Assert.Equal(first.Roots, Host.CaptureRoots());
        });
    }

    // A lane that folds the throw into a row still has its whole call refused.
    [Fact]
    public void ASplitALaneFoldsIntoARowStillRefusesTheCall()
    {
        Call(() => Host.CaptureAssets());                       // warm both sides on Default
        var answer = Answer(() =>
        {
            _svc.CapturePin();
            SelectOther();
            try { Host.CaptureAssets(); } catch (LoadOrderMovedMidCallException) { }
        });
        Assert.Equal(Wire.RefusalPrefix + CallContext.MovedBetweenViews, answer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASplitAfterAWriteCommittedNamesTheFilesWritten(bool pluginChangedToo)
    {
        Call(() => Host.CaptureAssets());                       // warm both sides on Default
        var written = Path.Combine(_mo2.ModsDir, "CcOut", "HcCcOut.esp");
        var offOrder = Path.Combine(_mo2.ModsDir, "CcBase", "hc_cc.txt");
        var answer = Answer(() =>
        {
            _svc.CapturePin();
            HousecarlCore.MidCallChange.AfterCommit(written);
            if (pluginChangedToo)
            {
                HousecarlCore.MidCallChange.Open(offOrder, () => 0);
                File.WriteAllText(offOrder, "changed after the commit");
                try { HousecarlCore.MidCallChange.Open(offOrder, () => 0); } catch (HousecarlCore.LoadOrderChangedMidCallException) { }
            }
            SelectOther();
            try { Host.CaptureAssets(); } catch (LoadOrderMovedMidCallException) { }
        });
        Assert.StartsWith(Wire.RefusalPrefix + "the load order changed while this call was running", answer);
        Assert.Contains("after houseCARL had written HcCcOut.esp", answer);
        Assert.DoesNotContain("nothing was answered or written", answer);
        if (pluginChangedToo) Assert.Contains("'hc_cc.txt' changed on disk during this call", answer);
        else Assert.DoesNotContain("changed on disk", answer);
    }

    // A write's first file read, inside the gate after its in-gate pin and its commit, takes the pin's build: a switch
    // after the pin is the next call's, and every read in between serves the pin with no refresh.
    [Fact]
    public void AFirstFileReadInsideTheWriteGateAfterACommitTakesThePinsBuild()
    {
        Call(() => Host.CaptureAssets());                       // warm both sides on Default
        Call(() =>
        {
            lock (Host.WriteGate)
            {
                var pin = _svc.CapturePin();
                var roots = Host.CaptureRoots();
                HousecarlCore.MidCallChange.AfterCommit(Path.Combine(_mo2.ModsDir, "CcOut", "HcCcOut.esp"));
                SelectOther();
                var (checks, fileChecks) = (_svc.Counters.ProfileFreshnessChecks, _svc.Counters.FileFreshnessChecks);
                var files = Host.CaptureAssets();
                Assert.Equal("Default", files.ProfileName);
                Assert.Equal(roots, files.Roots);
                Host.CaptureAssets();                            // the write's held files: checked once, at its first file read
                Assert.Equal(fileChecks + 1, _svc.Counters.FileFreshnessChecks);
                Assert.Same(pin, _svc.CapturePin());
                Assert.Equal(roots, Host.ConfiguredRoots().Roots);
                Assert.Equal(checks, _svc.Counters.ProfileFreshnessChecks);
            }
        });
        Call(() => Assert.Equal("Other", Host.CaptureAssets().ProfileName));
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

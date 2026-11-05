using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>#1196: a same-name copy of an active plugin appears in (or leaves) a higher-priority mod with no profile
/// change. The in-place writes, a read naming a mod, and load_order_status notice it; each test builds its own instance.</summary>
[Trait("tier", "integration")]
public sealed class ServingCopyTests
{
    static string Md5(string path) => Convert.ToHexString(MD5.HashData(File.ReadAllBytes(path)));
    static JsonElement Je(string json) => JsonDocument.Parse(json).RootElement.Clone();

    WritePatchBuilder.RemovalOutcome RemoveInPlace(ServingCopyWorld w) =>
        w.Svc.RemoveRecords(new[] { ServingCopyWorld.Fid(w.Keep) }, null, target: ServingCopyWorld.Target, inPlace: true, acknowledge: true);

    static string Read(ServingCopyWorld w, JsonElement? source = null) =>
        RecordsTools.Records(w.Svc, formids: new[] { ServingCopyWorld.Fid(w.Weapon) }, source: source);

    /// <summary>A higher copy appears: the in-place remove refuses with both files untouched, and the rerun writes the higher copy only.</summary>
    [Fact]
    public void AnInPlaceRemoveRefusesWhenAHigherCopyAppearsAndTheRerunWritesThatCopy()
    {
        using var w = new ServingCopyWorld(highFirst: false);
        w.Svc.Stats();                                          // the index is built off the low copy
        w.AddHighCopy();
        string low = Md5(w.LowPath), high = Md5(w.HighPath);

        var refused = RemoveInPlace(w);

        Assert.False(refused.Success);
        Assert.Contains("HighMod", refused.Error);
        Assert.Equal(low, Md5(w.LowPath));
        Assert.Equal(high, Md5(w.HighPath));

        var rerun = RemoveInPlace(w);

        Assert.True(rerun.Success, rerun.Error);
        Assert.NotEqual(high, Md5(w.HighPath));
        Assert.Equal(low, Md5(w.LowPath));
    }

    /// <summary>Inside a call the in-place remove still refuses with both files untouched, but the write takes no rebuild
    /// after its in-gate pin: the order is re-read at the next call, whose rerun writes the higher copy.</summary>
    [Fact]
    public void AWriteInACallRefusesWithNoRebuildAndTheNextCallReReads()
    {
        using var w = new ServingCopyWorld(highFirst: false);
        w.Svc.Stats();                                          // the index is built off the low copy
        w.AddHighCopy();
        string low = Md5(w.LowPath), high = Md5(w.HighPath);
        WritePatchBuilder.RemovalOutcome? refused = null, rerun = null;

        Assert.Equal("ok", Guard.Tool("housecarl_probe", _ => { refused = RemoveInPlace(w); return ToolAnswer.Served("ok"); }));

        Assert.False(refused!.Success);
        Assert.Contains("HighMod", refused.Error);
        Assert.Contains("nothing was written and the index was not refreshed: it is re-read at the start of the next call", refused.Error);
        Assert.Equal(0, w.Svc.ServingCopyReResolves);
        Assert.Equal(low, Md5(w.LowPath));
        Assert.Equal(high, Md5(w.HighPath));

        Assert.Equal("ok", Guard.Tool("housecarl_probe", _ => { rerun = RemoveInPlace(w); return ToolAnswer.Served("ok"); }));

        Assert.True(rerun!.Success, rerun.Error);
        Assert.NotEqual(high, Md5(w.HighPath));
        Assert.Equal(low, Md5(w.LowPath));
    }

    static void InCall(Action body) => Assert.Equal("ok", Guard.Tool("housecarl_probe", _ => { body(); return ToolAnswer.Served("ok"); }));

    /// <summary>The composition of the service's kept profile read, by reference; a re-read replaces it.</summary>
    static object? ProfileRead(LoadOrderService s) =>
        (typeof(LoadOrderService).GetField("_profileRead", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(s) as ITuple)?[0];

    /// <summary>The re-read a write put off runs at the next call only: the call after that re-reads nothing.</summary>
    [Fact]
    public void ThePutOffReReadRunsOnceAndTheCallAfterItReReadsNothing()
    {
        using var w = new ServingCopyWorld(highFirst: false);
        w.Svc.Stats();
        w.AddHighCopy();
        var host = (ILoadOrderHost)w.Svc;
        InCall(() => Assert.False(RemoveInPlace(w).Success));   // the write puts the re-read off
        object? read = null;
        InCall(() => { GC.KeepAlive(host.Resolver); read = ProfileRead(w.Svc); });   // the put-off re-read
        InCall(() => GC.KeepAlive(host.Resolver));

        Assert.Same(read, ProfileRead(w.Svc));
    }

    /// <summary>An instance switch drops the re-read the old instance put off: the new instance's cold build clears it, so
    /// after a records-first call the next call re-reads nothing.</summary>
    [Fact]
    public void AnInstanceSwitchDropsTheReReadTheOldInstancePutOff()
    {
        using var w = new ServingCopyWorld(highFirst: false);
        w.Svc.Stats();
        w.AddHighCopy();
        var host = (ILoadOrderHost)w.Svc;
        InCall(() => Assert.False(RemoveInPlace(w).Success));   // the write puts the re-read off
        w.Svc.SetInstance(Path.Combine(w.Root, "instance"));
        object? read = null;
        InCall(() => { GC.KeepAlive(host.Resolver); read = ProfileRead(w.Svc); });   // records first: the new instance's first build
        InCall(() => GC.KeepAlive(host.Resolver));

        Assert.Same(read, ProfileRead(w.Svc));
    }

    /// <summary>Q1: the target's mod switched off after the write's in-gate pin; the write finishes on the pin's copy with no refusal.</summary>
    [Theory]
    [InlineData("apply")]
    [InlineData("compact")]
    public void AnInPlaceWriteFinishesOnItsPinsCopyWhenTheTargetsModGoesOffAfterThePin(string lane)
    {
        using var w = new ServingCopyWorld(highFirst: true);
        w.Svc.Stats();                                          // the index is built off the high copy
        string low = Md5(w.LowPath), high = Md5(w.HighPath);
        (bool Success, string? Error) r = default;
        w.Svc.WriteArea.AfterInGatePinForGuard = w.SwitchHighModOff;

        Assert.Equal("ok", Guard.Tool("housecarl_probe", _ =>
        {
            r = lane == "apply"
                ? Out(w.Svc.ApplyEdits(new[] { new BulkOp { Formid = ServingCopyWorld.Fid(w.Weapon), FieldPath = "EditorID", Value = "HcServeEdited" } },
                                       null, null, target: ServingCopyWorld.Target, inPlace: true, acknowledge: true))
                : Out(w.Svc.CompactPlugin(ServingCopyWorld.Target, inPlace: true, acknowledge: true));
            return ToolAnswer.Served("ok");
        }));
        w.Svc.WriteArea.AfterInGatePinForGuard = null;

        Assert.DoesNotContain("index still had", r.Error ?? "");
        Assert.True(r.Success, r.Error);
        Assert.NotEqual(high, Md5(w.HighPath));                 // the pin's copy was written
        Assert.Equal(low, Md5(w.LowPath));

        static (bool, string?) Out(dynamic o) => (o.Success, o.Error);
    }

    /// <summary>HighMod switched on after the call's pin, then a read naming it: the read is judged on the live profile,
    /// so it refuses saying the index is now refreshed, after one rebuild.</summary>
    [Fact]
    public void ANamedCopyReadAfterItsModIsSwitchedOnPostPinSaysTheIndexIsRefreshed()
    {
        using var w = new ServingCopyWorld(highFirst: true);
        w.SwitchHighModOff();
        w.Svc.Stats();                                          // the index is built off the low copy
        string read = "";

        InCall(() =>
        {
            w.Svc.CapturePin();
            w.SwitchHighModOn();
            read = Read(w, Je($"{{\"file\": \"{ServingCopyWorld.Target}\", \"mod\": \"HighMod\"}}"));
        });

        Assert.Contains("HighMod", read);
        Assert.Contains("the index is now refreshed and nothing was read", read);
        Assert.DoesNotContain(ServingCopyWorld.LowEdid, read);
        Assert.Equal(1, w.Svc.ServingCopyReResolves);
    }

    /// <summary>Pins unchanged behaviour, a Known limit as on main: a read naming HighMod, in a call after another call
    /// followed a switch to a profile with HighMod off, is judged on the live roots, where the indexed low copy still
    /// serves, so it refuses saying the served copy changed again and rebuilds nothing.</summary>
    [Fact]
    public void ANamedCopyReadAfterAnotherCallsSwitchIsJudgedOnTheLiveRoots()
    {
        using var w = new ServingCopyWorld(highFirst: false);
        w.Svc.Stats();                                          // the index is built off the low copy
        string read = "";

        InCall(() =>
        {
            w.Svc.CapturePin();
            w.AddHighCopy();
            w.SelectOtherWithHighModOff();
            var other = new Thread(() => GC.KeepAlive(((ILoadOrderHost)w.Svc).Resolver));   // another call follows the switch
            other.UnsafeStart();
            other.Join();
            read = Read(w, Je($"{{\"file\": \"{ServingCopyWorld.Target}\", \"mod\": \"HighMod\"}}"));
        });

        Assert.Contains("the served copy changed again during this call", read);
        Assert.Contains("nothing was read", read);
        Assert.DoesNotContain(ServingCopyWorld.LowEdid, read);
        Assert.Equal(0, w.Svc.ServingCopyReResolves);
    }

    /// <summary>The door in a call, after another call followed a move of MO2's base folder, judges on the call's roots,
    /// where the indexed low copy still serves; on the moved roots the low copy is another path.</summary>
    [Fact]
    public void TheDoorInACallAfterAnotherCallMovedTheRootsJudgesOnTheCallsRoots()
    {
        using var w = new ServingCopyWorld(highFirst: false);
        w.Svc.Stats();                                          // the index is built off the low copy
        ServingCopyMove? moved = null;

        InCall(() =>
        {
            w.Svc.CapturePin();
            w.MoveBaseFolder();
            var other = new Thread(() => GC.KeepAlive(((ILoadOrderHost)w.Svc).Resolver));   // another call follows the move
            other.UnsafeStart();
            other.Join();
            moved = ((ILoadOrderHost)w.Svc).ServingCopyMoved([(ServingCopyWorld.Target, w.LowPath)]);
        });

        Assert.Null(moved);
    }

    /// <summary>source={file, mod: HighMod} never answers from the low copy, and the rerun reads the high copy's bytes.</summary>
    [Fact]
    public void ASourceNamingTheHigherModReadsThatModsCopy()
    {
        using var w = new ServingCopyWorld(highFirst: false);
        w.Svc.Stats();
        w.AddHighCopy();
        var src = Je($"{{\"file\": \"{ServingCopyWorld.Target}\", \"mod\": \"HighMod\"}}");

        var first = Read(w, src);
        var rerun = Read(w, src);

        Assert.DoesNotContain(ServingCopyWorld.LowEdid, first);
        Assert.Contains(ServingCopyWorld.HighEdid, rerun);
    }

    /// <summary>load_order_status after the copy appears refreshes the index, and a plain read then answers from the higher copy.</summary>
    [Fact]
    public void StatusAfterTheCopyAppearsRefreshesTheIndexForThePlainReadAfterIt()
    {
        using var w = new ServingCopyWorld(highFirst: false);
        Assert.Contains(ServingCopyWorld.LowEdid, Read(w));
        w.AddHighCopy();

        var status = StatusTools.LoadOrderStatus(w.Svc, w.Tools, filter: ServingCopyWorld.Target);

        Assert.Contains("refreshed now", status);
        Assert.Contains(ServingCopyWorld.HighEdid, Read(w));
    }

    /// <summary>The higher copy removed again: the in-place remove refuses, and the rerun writes the low copy that now serves.</summary>
    [Fact]
    public void TheHigherCopyRemovedIsNoticedTheSameWay()
    {
        using var w = new ServingCopyWorld(highFirst: true);
        w.Svc.Stats();                                          // the index is built off the high copy
        File.Delete(w.HighPath);
        var low = Md5(w.LowPath);

        var refused = RemoveInPlace(w);

        Assert.False(refused.Success);
        Assert.Contains("LowMod", refused.Error);
        Assert.Equal(low, Md5(w.LowPath));

        var rerun = RemoveInPlace(w);

        Assert.True(rerun.Success, rerun.Error);
        Assert.NotEqual(low, Md5(w.LowPath));
    }

    /// <summary>Each in-place lane refuses once a higher copy appears, and neither copy changes.</summary>
    [Theory]
    [InlineData("apply")]
    [InlineData("create")]
    [InlineData("remove")]
    [InlineData("forward")]
    [InlineData("compact")]
    public void EachInPlaceLaneRefusesWhenAHigherCopyAppears(string lane)
    {
        using var w = new ServingCopyWorld(highFirst: false);
        w.Svc.Stats();
        w.AddHighCopy();
        string low = Md5(w.LowPath), high = Md5(w.HighPath);
        const string T = ServingCopyWorld.Target;

        (bool Success, string? Error) r = lane switch
        {
            "apply" => Out(w.Svc.ApplyEdits(new[] { new BulkOp { Formid = ServingCopyWorld.Fid(w.Weapon), FieldPath = "EditorID", Value = "HcServeEdited" } },
                                            null, null, target: T, inPlace: true, acknowledge: true)),
            "create" => Out(w.Svc.CreateRecordsBatch(new[] { new CreateOp { RecordType = "Keyword", Editorid = "HcServeNewKw" } },
                                                     null, null, target: T, inPlace: true, acknowledge: true)),
            "remove" => Out(RemoveInPlace(w)),
            "forward" => Out(w.Svc.ForwardRecords(new[] { "000801:HcServeBase.esm" }, "HcServeBase.esm", null, null,
                                                  target: T, inPlace: true, acknowledge: true)),
            _ => Out(w.Svc.CompactPlugin(T, inPlace: true, acknowledge: true)),
        };

        Assert.False(r.Success);
        Assert.Contains("HighMod", r.Error);
        Assert.Equal(low, Md5(w.LowPath));
        Assert.Equal(high, Md5(w.HighPath));

        static (bool, string?) Out(dynamic o) => (o.Success, o.Error);
    }

    /// <summary>Compacting the master in place with repoint_externals refuses when a referencer's higher copy appears; no file changes.</summary>
    [Fact]
    public void ACompactRepointRefusesWhenAReferencerHasAHigherCopy()
    {
        using var w = new ServingCopyWorld(highFirst: false);
        w.Svc.Stats();
        w.AddHighCopy();
        string low = Md5(w.LowPath), high = Md5(w.HighPath), master = Md5(w.MasterPath);

        var refused = w.Svc.CompactPlugin("HcServeBase.esm", inPlace: true, repointExternals: true, acknowledge: true);

        Assert.False(refused.Success);
        Assert.Contains("HighMod", refused.Error);
        Assert.Equal(low, Md5(w.LowPath));
        Assert.Equal(high, Md5(w.HighPath));
        Assert.Equal(master, Md5(w.MasterPath));
    }

    /// <summary>The profile's load order reads back empty: the in-place remove refuses naming the empty read, no rebuild is counted, no file changes.</summary>
    [Fact]
    public void AnEmptyLoadOrderReadRefusesNamingTheEmptyRead()
    {
        using var w = new ServingCopyWorld(highFirst: false);
        w.Svc.Stats();
        w.AddHighCopy();
        File.WriteAllText(Path.Combine(w.Root, "instance", "profiles", "Default", "loadorder.txt"), "# header\r\n");
        string low = Md5(w.LowPath), high = Md5(w.HighPath);

        var refused = RemoveInPlace(w);

        Assert.False(refused.Success);
        Assert.Contains("load order read back empty", refused.Error);
        Assert.Equal(0, w.Svc.ServingCopyReResolves);
        Assert.Equal(low, Md5(w.LowPath));
        Assert.Equal(high, Md5(w.HighPath));
    }

    /// <summary>Another thread holds the write gate: the mod-named read refuses naming the running write and rebuilds nothing.</summary>
    [Fact]
    public void AReadWhileAnotherWriteRunsRefusesNamingTheWrite()
    {
        using var w = new ServingCopyWorld(highFirst: false);
        w.Svc.Stats();
        w.AddHighCopy();
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var gate = ((ILoadOrderHost)w.Svc).WriteGate;
        var writer = new Thread(() => { lock (gate) { held.Set(); release.Wait(); } });
        writer.Start();
        held.Wait();
        string read;
        try { read = Read(w, Je($"{{\"file\": \"{ServingCopyWorld.Target}\", \"mod\": \"HighMod\"}}")); }
        finally { release.Set(); writer.Join(); }

        Assert.Contains("another write is running", read);
        Assert.DoesNotContain(ServingCopyWorld.LowEdid, read);
        Assert.Equal(0, w.Svc.ServingCopyReResolves);
    }

    /// <summary>A read pinned before a concurrent refresh passes the old copy: it refuses, and neither the order nor the asset resolver is rebuilt.</summary>
    [Fact]
    public void AReadPinnedBeforeARefreshRefusesWithoutAnotherRebuild()
    {
        using var w = new ServingCopyWorld(highFirst: false);
        w.Svc.Stats();
        w.AddHighCopy();
        StatusTools.LoadOrderStatus(w.Svc, w.Tools, filter: ServingCopyWorld.Target);   // the refresh: the index now holds the high copy
        var host = (ILoadOrderHost)w.Svc;
        var assets = host.Assets;
        int reResolves = w.Svc.ServingCopyReResolves;

        var moved = host.ServingCopyMoved([(ServingCopyWorld.Target, w.LowPath)]);

        Assert.NotNull(moved);
        Assert.Contains("now refreshed", moved!.Refusal("nothing was read"));
        Assert.Equal(reResolves, w.Svc.ServingCopyReResolves);
        Assert.Same(assets, host.Assets);
    }

    /// <summary>Nothing moved: the write, the named read and the status call run no re-resolve and refuse nothing.</summary>
    [Fact]
    public void WhenTheServingCopyIsUnchangedNothingReResolves()
    {
        using var w = new ServingCopyWorld(highFirst: false);
        w.Svc.Stats();

        var write = RemoveInPlace(w);
        var read = Read(w, Je($"{{\"file\": \"{ServingCopyWorld.Target}\", \"mod\": \"LowMod\"}}"));
        StatusTools.LoadOrderStatus(w.Svc, w.Tools, filter: ServingCopyWorld.Target);

        Assert.True(write.Success, write.Error);
        Assert.Contains(ServingCopyWorld.LowEdid, read);
        Assert.Equal(0, w.Svc.ServingCopyReResolves);
    }
}

/// <summary>One instance: a master in game Data, and the target plugin in LowMod, below an enabled HighMod that holds
/// a copy only when asked. The two copies differ in one weapon's EditorID, so a read says which copy answered.</summary>
public sealed class ServingCopyWorld : IDisposable
{
    public const string Target = "HcServeTarget.esp";
    public const string LowEdid = "HcServeLowCopy";
    public const string HighEdid = "HcServeHighCopy";
    public string Root { get; }
    public LoadOrderService Svc { get; }
    public ToolPathResolver Tools { get; }
    public string LowPath { get; }
    public string HighPath { get; }
    public string MasterPath { get; }
    public FormKey Weapon { get; }
    public FormKey Keep { get; }
    readonly SkyrimMod _master;

    public static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

    public ServingCopyWorld(bool highFirst)
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-serving-copy-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profile = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        var data = Path.Combine(Root, "game", "Data");
        foreach (var d in new[] { profile, Path.Combine(mods, "LowMod"), Path.Combine(mods, "HighMod"), data }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        _master = new SkyrimMod(new ModKey("HcServeBase", ModType.Master), SkyrimRelease.SkyrimSE);
        _master.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Master;
        _master.Weapons.Add(new Weapon(new FormKey(_master.ModKey, 0x801), SkyrimRelease.SkyrimSE) { EditorID = "HcServeBaseWeap" });
        MasterPath = Path.Combine(data, "HcServeBase.esm");
        _master.BeginWrite.ToPath(MasterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        Weapon = new FormKey(ModKey.FromFileName(Target), 0x801);
        Keep = new FormKey(ModKey.FromFileName(Target), 0x802);

        LowPath = Path.Combine(mods, "LowMod", Target);
        HighPath = Path.Combine(mods, "HighMod", Target);
        Write(LowPath, LowEdid);
        if (highFirst) AddHighCopy();

        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\nHcServeBase.esm\r\n" + Target + "\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*" + Target + "\r\n");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "# header\r\n+HighMod\r\n+LowMod\r\n");

        var store = new UserConfigStore(Path.Combine(Root, "houseCARL.user.json"));
        Svc = LoadOrderService.WithInstance(instance, 0, store);
        Tools = new ToolPathResolver(store);
    }

    /// <summary>Drop the high copy into HighMod; no profile file changes.</summary>
    public void AddHighCopy() => Write(HighPath, HighEdid);

    /// <summary>Switch HighMod off in the Default profile's mod list, as a toggle in MO2 does.</summary>
    public void SwitchHighModOff() => File.WriteAllText(Path.Combine(Root, "instance", "profiles", "Default", "modlist.txt"), "# header\r\n-HighMod\r\n+LowMod\r\n");

    /// <summary>Switch HighMod back on in the Default profile's mod list.</summary>
    public void SwitchHighModOn() => File.WriteAllText(Path.Combine(Root, "instance", "profiles", "Default", "modlist.txt"), "# header\r\n+HighMod\r\n+LowMod\r\n");

    /// <summary>Select a profile "Other" with the same plugins and HighMod off, as a profile switch in MO2 does.</summary>
    public void SelectOtherWithHighModOff()
    {
        var instance = Path.Combine(Root, "instance");
        var other = Path.Combine(instance, "profiles", "Other");
        Directory.CreateDirectory(other);
        foreach (var f in new[] { "loadorder.txt", "plugins.txt" }) File.Copy(Path.Combine(instance, "profiles", "Default", f), Path.Combine(other, f));
        File.WriteAllText(Path.Combine(other, "modlist.txt"), "# header\r\n-HighMod\r\n+LowMod\r\n");
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Other)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
    }

    /// <summary>Point MO2's base_directory at a copy of the profile and LowMod's plugin, as moving the base folder does.</summary>
    public void MoveBaseFolder()
    {
        var instance = Path.Combine(Root, "instance");
        var moved = Path.Combine(Root, "moved");
        var profile = Path.Combine(moved, "profiles", "Default");
        Directory.CreateDirectory(profile);
        foreach (var f in new[] { "loadorder.txt", "plugins.txt", "modlist.txt" }) File.Copy(Path.Combine(instance, "profiles", "Default", f), Path.Combine(profile, f));
        Directory.CreateDirectory(Path.Combine(moved, "mods", "LowMod"));
        File.Copy(LowPath, Path.Combine(moved, "mods", "LowMod", Target));
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n[Settings]\r\nbase_directory=" + moved.Replace(@"\", @"\\") + "\r\n");
    }

    void Write(string path, string edid)
    {
        var m = new SkyrimMod(ModKey.FromFileName(Target), SkyrimRelease.SkyrimSE);
        m.Weapons.Add(new Weapon(Weapon, SkyrimRelease.SkyrimSE)
            { EditorID = edid, Template = new FormLinkNullable<IWeaponGetter>(new FormKey(_master.ModKey, 0x801)) });   // a reference into the master
        m.Weapons.Add(new Weapon(Keep, SkyrimRelease.SkyrimSE) { EditorID = edid + "Keep" });
        m.Weapons.Add(new Weapon(new FormKey(_master.ModKey, 0x801), SkyrimRelease.SkyrimSE) { EditorID = "HcServeBaseWeap" });
        m.BeginWrite.ToPath(path).WithLoadOrder(new ISkyrimModGetter[] { _master }).Write();
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

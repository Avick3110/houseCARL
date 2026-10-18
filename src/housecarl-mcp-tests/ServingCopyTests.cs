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

        Assert.Contains("refreshed", status);
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

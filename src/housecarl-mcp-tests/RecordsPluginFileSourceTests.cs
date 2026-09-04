using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The source pole that reads a plugin FILE rather than the load order, and the delta projection's
/// two renders. Driven at the tool layer, so what is held is the rendered form.</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsPluginFileSourceTests : RecordsTestBase
{
    public RecordsPluginFileSourceTests(RecordsFixture f) : base(f) { }

    static RecordsTools.RecordsProject Delta => new() { form = "delta" };

    string OffOrderFields(string? format = null) =>
        RecordsTools.Records(Svc, source: Plugin(W.OldName), types: new[] { "WEAP" }, format: format,
                             project: Fields("BasicStats.Damage"));

    string Delta01(string? format = null) =>
        RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) }, source: Plugin(W.MasterName),
                             versus: Plugin(W.OverrideName), format: format, project: Delta);

    // ---- the file's own body, rendered ------------------------------------------------------------

    /// <summary>The value is already pinned elsewhere; what this holds is the FORM it is rendered in — the
    /// "path = token" line a caller reads a raw file body by.</summary>
    [Fact]
    public void AnOffOrderFileReadRendersItsFieldInThePathEqualsTokenFormat() =>
        Assert.Contains("BasicStats.Damage = 55", OffOrderFields());

    [Fact]
    public void AnOffOrderFileReadInJsonIsAValidDocument() =>
        JsonDocument.Parse(OffOrderFields("json")).Dispose();

    [Fact]
    public void AnOffOrderFileReadInJsonCarriesTheSameFieldTokenAsTheTextRender()
    {
        using var doc = JsonDocument.Parse(OffOrderFields("json"));
        var field = doc.RootElement.GetProperty("records")[0].GetProperty("fields")[0];
        Assert.Equal("BasicStats.Damage", field.GetProperty("path").GetString());
        Assert.Equal("55", field.GetProperty("value").GetString());
    }

    /// <summary>The FILENAME lane's not-active cause. A plugin addressed by NAME out of a switched-off mod names
    /// the mod folder's state and its remedy, and says each once — the twin of <c>RecordsOffOrderPathTests.FactB4</c>,
    /// whose path lane has no mod folder in its label to lean on.</summary>
    [Fact]
    public void APluginNamedOutOfASwitchedOffModSaysThatModFolderIsOff()
    {
        var r = OffOrderFields();
        Served(r, "OUT-OF-LOAD-ORDER", "NOT active — that mod folder is switched OFF in MO2 — switch it on, then re-sort");
        var subject = Assert.Single(r.Split('\n'), l => l.Contains("OUT-OF-LOAD-ORDER", StringComparison.Ordinal));
        Assert.Equal(1, CountOf(subject, "switch it on"));
        Assert.Equal(1, CountOf(subject, "'OldMod'"));
    }

    [Fact]
    public void AnOffOrderFileReadInJsonStampsTheOffOrderArmOnItsSourceEnvelope()
    {
        using var doc = JsonDocument.Parse(OffOrderFields("json"));
        Assert.Contains("OUT-OF-LOAD-ORDER", doc.RootElement.GetProperty("source").GetString());
    }

    // ---- the delta render ------------------------------------------------------------------------

    [Fact]
    public void TheDeltaTextRenderCarriesTheRecordHeaderTheFieldDeltaAndTheReferenceLabel()
    {
        var r = Delta01();
        Assert.Contains(Fid(W.Weapons[0]) + "  Weapon  HcRecW0", r);
        Assert.Contains("BasicStats.Damage=10", r);
        Assert.Contains("(" + W.OverrideName + " 99)", r);
    }

    [Fact]
    public void TheDeltaJsonRenderCarriesTheRowsDeltasAndItsCompleteness()
    {
        using var doc = JsonDocument.Parse(Delta01("json"));
        var row = doc.RootElement.GetProperty("rows")[0];
        Assert.True(row.GetProperty("complete").GetBoolean());
        Assert.Equal("BasicStats.Damage=10 (" + W.OverrideName + " 99)", row.GetProperty("deltas")[0].GetString());
    }
}

/// <summary>A three-plugin order whose middle plugin is a valid header followed by a truncated body — the
/// overlay open throws, so the load-order index EXCLUDES it.</summary>
public sealed class ExcludedPluginWorld : IDisposable
{
    public string Root { get; }
    public LoadOrderService Svc { get; }
    public string BrokenName { get; }
    public string CleanName { get; }
    /// <summary>Originates in the master, overridden by both the broken plugin and the clean one.</summary>
    public string SubjectFid { get; }

    public ExcludedPluginWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-excluded-plugin-tests-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profiles = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        foreach (var d in new[] { profiles, mods, Path.Combine(Root, "game", "Data") }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        var mKey = new ModKey("HcXMaster", ModType.Master);
        var bKey = new ModKey("HcXBroken", ModType.Plugin);
        var cKey = new ModKey("HcXClean", ModType.Plugin);
        BrokenName = bKey.FileName.String; CleanName = cKey.FileName.String;

        string P(string folder, ModKey k)
        {
            var p = Path.Combine(mods, folder, k.FileName.String);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            return p;
        }

        var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
        var subject = m.Weapons.AddNew();
        subject.EditorID = "XSubject";
        subject.BasicStats = new WeaponBasicStats { Damage = 10 };
        SubjectFid = $"{subject.FormKey.ID:X6}:{mKey.FileName}";
        m.BeginWrite.ToPath(P("XMaster", mKey)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var b = new SkyrimMod(bKey, SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(b, subject)).BasicStats = new WeaponBasicStats { Damage = 30 };
        var brokenPath = P("XBroken", bKey);
        b.BeginWrite.ToPath(brokenPath).WithLoadOrder(new ISkyrimModGetter[] { m }).Write();

        var c = new SkyrimMod(cKey, SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(c, subject)).BasicStats = new WeaponBasicStats { Damage = 20 };
        c.BeginWrite.ToPath(P("XClean", cKey)).WithLoadOrder(new ISkyrimModGetter[] { m, b }).Write();

        // …and NOW break it: a valid header followed by a truncated body. Written last so the clean plugin above
        // could be built against it.
        var whole = File.ReadAllBytes(brokenPath);
        File.WriteAllBytes(brokenPath, whole[..(whole.Length - 12)]);

        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"),
            "# header\r\n" + mKey.FileName + "\r\n" + bKey.FileName + "\r\n" + cKey.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"),
            "*" + mKey.FileName + "\r\n*" + bKey.FileName + "\r\n*" + cKey.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+XClean\r\n+XBroken\r\n+XMaster\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
        Svc.Stats();
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

public sealed class ExcludedPluginFixture : IDisposable
{
    public ExcludedPluginWorld W { get; } = new();
    public void Dispose() => W.Dispose();
}

[Trait("tier", "integration")]
public sealed class RecordsExcludedPluginSourceTests : IClassFixture<ExcludedPluginFixture>
{
    readonly ExcludedPluginWorld _w;
    public RecordsExcludedPluginSourceTests(ExcludedPluginFixture f) => _w = f.W;

    /// <summary>The records lane answers per item, and the excluded plugin is NAMED with the reason rather
    /// than quietly served some other body.</summary>
    [Fact]
    public void APoleNamingTheExcludedPluginAnswersAPerItemErrorNamingItAndTheReason()
    {
        var r = RecordsTools.Records(_w.Svc, formids: new[] { _w.SubjectFid },
                                     source: JsonDocument.Parse("\"" + _w.BrokenName + "\"").RootElement.Clone());
        Assert.Contains("error=Plugin '" + _w.BrokenName + "' was excluded from this session", r);
        Assert.Contains("could not be opened", r);
    }

    [Fact]
    public void ReadsAreUnaffectedByTheExclusion_TheCleanOverrideStillResolvesAsTheWinner()
    {
        var r = RecordsTools.Records(_w.Svc, formids: new[] { _w.SubjectFid });
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains("winner=" + _w.CleanName, r);
    }
}

/// <summary>Two ENABLED mod folders shipping the same plugin filename, the higher-priority one served into the
/// active order — the ESP-replacer shape the {file, mod} source form exists for. The one weapon's Damage differs
/// per copy, so which physical file was read is visible in the answer.</summary>
public sealed class ShadowedCopyWorld : IDisposable
{
    public string Root { get; }
    public LoadOrderService Svc { get; }
    /// <summary>The one filename both mod folders provide.</summary>
    public string FileName { get; }
    public string WinnerMod => "SWinner";
    public string LoserMod => "SLoser";
    /// <summary>Damage in the served copy / in the shadowed copy.</summary>
    public const int WinnerDamage = 99;
    public const int LoserDamage = 44;
    public string SubjectFid { get; }
    /// <summary>The full path to one mod folder's copy.</summary>
    public string PathIn(string mod) => Path.Combine(Root, "instance", "mods", mod, FileName);

    public ShadowedCopyWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-shadowed-copy-tests-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profiles = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        foreach (var d in new[] { profiles, mods, Path.Combine(Root, "game", "Data") }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        var mKey = new ModKey("HcSMaster", ModType.Master);
        var rKey = new ModKey("HcSReplacer", ModType.Plugin);
        FileName = rKey.FileName.String;

        string P(string folder, ModKey k)
        {
            var p = Path.Combine(mods, folder, k.FileName.String);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            return p;
        }

        var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
        var subject = m.Weapons.AddNew();
        subject.EditorID = "SSubject";
        subject.BasicStats = new WeaponBasicStats { Damage = 10 };
        SubjectFid = $"{subject.FormKey.ID:X6}:{mKey.FileName}";
        m.BeginWrite.ToPath(P("SMaster", mKey)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        foreach (var (folder, damage) in new[] { (WinnerMod, WinnerDamage), (LoserMod, LoserDamage) })
        {
            var r = new SkyrimMod(rKey, SkyrimRelease.SkyrimSE);
            ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(r, subject)).BasicStats = new WeaponBasicStats { Damage = (ushort)damage };
            r.BeginWrite.ToPath(P(folder, rKey)).WithLoadOrder(new ISkyrimModGetter[] { m }).Write();
        }

        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"),
            "# header\r\n" + mKey.FileName + "\r\n" + rKey.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"),
            "*" + mKey.FileName + "\r\n*" + rKey.FileName + "\r\n");
        // modlist.txt is TOP = highest priority, so SWinner provides the copy MO2 serves.
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+SWinner\r\n+SLoser\r\n+SMaster\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
        Svc.Stats();
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

public sealed class ShadowedCopyFixture : IDisposable
{
    public ShadowedCopyWorld W { get; } = new();
    public void Dispose() => W.Dispose();
}

/// <summary>The {file, mod} source form against a filename that IS active: it addresses the named folder's copy,
/// which is the only reason the form exists.</summary>
[Trait("tier", "integration")]
public sealed class RecordsModFolderSourceTests : IClassFixture<ShadowedCopyFixture>
{
    readonly ShadowedCopyWorld _w;
    public RecordsModFolderSourceTests(ShadowedCopyFixture f) => _w = f.W;

    string Read(string mod) =>
        RecordsTools.Records(_w.Svc, formids: new[] { _w.SubjectFid },
                             source: JsonDocument.Parse("{\"file\": \"" + _w.FileName + "\", \"mod\": \"" + mod + "\"}").RootElement.Clone(),
                             project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "BasicStats.Damage" } });

    [Fact]
    public void NamingTheShadowedModReadsThatFoldersCopy()
    {
        var r = Read(_w.LoserMod);
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains("BasicStats.Damage = " + ShadowedCopyWorld.LoserDamage, r);
    }

    [Fact]
    public void NamingTheShadowedModSaysItIsNotTheActiveCopyAndWhichModIsActive()
    {
        var r = Read(_w.LoserMod);
        Assert.Contains("OUT-OF-LOAD-ORDER", r);
        Assert.Contains("SHADOWED", r);
        Assert.Contains("'" + _w.WinnerMod + "'", r);
    }

    [Fact]
    public void NamingTheServingModReadsTheActiveCopyOnTheActiveArm()
    {
        var r = Read(_w.WinnerMod);
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains("BasicStats.Damage = " + ShadowedCopyWorld.WinnerDamage, r);
        Assert.Contains("active in the load order", r);
    }

    [Fact]
    public void AModFolderThatDoesNotCarryTheFilenameIsStillRefused()
    {
        var r = Read("SMaster");
        Assert.StartsWith("error:", r);
        Assert.Contains("does not provide", r);
    }

    /// <summary>A file= that is a PATH addresses that file, and a mod= beside it does not redirect the read to
    /// another folder's copy of the same filename.</summary>
    [Fact]
    public void APathFileWinsOverAModFolderBesideIt()
    {
        var r = RecordsTools.Records(_w.Svc, formids: new[] { _w.SubjectFid },
                                     source: JsonDocument.Parse(JsonSerializer.Serialize(new { file = _w.PathIn(_w.WinnerMod), mod = _w.LoserMod })).RootElement.Clone(),
                                     project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "BasicStats.Damage" } });
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains("BasicStats.Damage = " + ShadowedCopyWorld.WinnerDamage, r);
    }

    /// <summary>previous_provider is a position in the ACTIVE touching stack, which an off-order copy does not
    /// hold — even when its filename is active as a different file. Refused, never anchored on the served copy.
    /// </summary>
    [Fact]
    public void PreviousProviderAgainstTheShadowedCopyIsRefusedRatherThanAnchoredOnTheServedCopy()
    {
        var r = RecordsTools.Records(_w.Svc, formids: new[] { _w.SubjectFid },
                                     source: JsonDocument.Parse("{\"file\": \"" + _w.FileName + "\", \"mod\": \"" + _w.LoserMod + "\"}").RootElement.Clone(),
                                     versus: JsonDocument.Parse("\"previous_provider\"").RootElement.Clone(),
                                     project: new RecordsTools.RecordsProject { form = "delta" });
        Assert.Contains("no position in the active touching stack", r);
    }
}

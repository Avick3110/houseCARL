using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The source pole that reads a plugin FILE rather than the load order: the arms come from the tool-layer half of
/// <c>ReadPluginFileProbe</c> (the four <c>ReadTools.ReadPluginFile</c> renders — the rest of that probe drives
/// <c>LoadOrderService.ReadPluginFile</c> directly and survives the cut untouched) and from the two
/// <c>ReadTools.DiffRecord</c> renders in <c>BulkPrimitivesWave3Probe</c>.
/// </summary>
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

    [Fact]
    public void AnOffOrderFileReadInJsonStampsTheOffOrderArmOnItsSourceEnvelope()
    {
        using var doc = JsonDocument.Parse(OffOrderFields("json"));
        Assert.Contains("OUT-OF-LOAD-ORDER", doc.RootElement.GetProperty("source").GetString());
    }

    // ---- the delta render (diff_record's two tool-layer arms) -------------------------------------

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

/// <summary>
/// A three-plugin order whose middle plugin is a valid header followed by a truncated body — the overlay open
/// throws, so the load-order index EXCLUDES it. The reads below are the two <c>read_record</c> arms of
/// <c>ExcludedMasterWriteProbe</c>, whose subject is the read side of that exclusion; that probe's write arms
/// drive shipped 2.0 tools and stay where they are.
/// </summary>
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

    /// <summary>1.x refused the whole call; the records lane answers per item, which is its own contract. What
    /// carries over unchanged is that the plugin is NAMED with the reason, never quietly served some other body.</summary>
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

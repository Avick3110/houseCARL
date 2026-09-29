using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A service's read-cost counters are the ones its resolver increments, through a profile-change rebuild
/// and on a service handed a prebuilt resolver; a counter that stops moving would let every cost assert pass.</summary>
[Trait("tier", "integration")]
public sealed class CostCountersTests : IDisposable
{
    const string NameA = "HcCcA.esp";
    const string NameB = "HcCcB.esp";

    readonly string _root;
    readonly string _profile;
    readonly string _pathA;
    readonly string _weapA;
    readonly string _weapB;

    public CostCountersTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-cost-counters-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(_root, "inst");
        var mods = Path.Combine(instance, "mods");
        Directory.CreateDirectory(Path.Combine(_root, "game", "Data"));
        _pathA = WritePlugin(mods, "ModA", NameA, "HcCcWeapA", out _weapA);
        WritePlugin(mods, "ModB", NameB, "HcCcWeapB", out _weapB);

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace("\\", "\\\\") + ")\r\n");
        _profile = Path.Combine(instance, "profiles", "Default");
        Directory.CreateDirectory(_profile);
        WriteProfile(NameA);
    }

    static string WritePlugin(string mods, string mod, string name, string edid, out string weapon)
    {
        var m = new SkyrimMod(ModKey.FromFileName(name), SkyrimRelease.SkyrimSE);
        var w = m.Weapons.AddNew();
        w.EditorID = edid;
        weapon = $"{w.FormKey.ID:X6}:{name}";
        Directory.CreateDirectory(Path.Combine(mods, mod));
        var path = Path.Combine(mods, mod, name);
        m.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        return path;
    }

    void WriteProfile(params string[] plugins)
    {
        File.WriteAllText(Path.Combine(_profile, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", plugins) + "\r\n");
        File.WriteAllText(Path.Combine(_profile, "plugins.txt"), string.Join("", plugins.Select(p => "*" + p + "\r\n")));
        File.WriteAllText(Path.Combine(_profile, "modlist.txt"), "# header\r\n+ModB\r\n+ModA\r\n");
    }

    static string Read(LoadOrderService svc, string formId)
        => RecordsTools.Records(svc, formids: new[] { formId },
                                project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "EditorID" } });

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }

    [Fact]
    public void AProfileChangeRebuildKeepsCountingOnTheService()
    {
        using var svc = LoadOrderService.WithInstance(Path.Combine(_root, "inst"), 0,
                                                      new UserConfigStore(Path.Combine(_root, "user.json")));
        var before = svc.Counters.SessionOverlayOpens;
        Assert.Contains("HcCcWeapA", Read(svc, _weapA));
        Assert.True(svc.Counters.SessionOverlayOpens > before, "the first build's read did not count its overlay open.");

        WriteProfile(NameA, NameB);
        before = svc.Counters.SessionOverlayOpens;
        Assert.Contains("HcCcWeapB", Read(svc, _weapB));     // B is only in the order after the rebuild
        Assert.True(svc.Counters.SessionOverlayOpens > before,
                    "a read after the profile-change rebuild did not count on the service's counters.");
    }

    [Fact]
    public void AServiceOnAPrebuiltResolverCountsWhatThatResolverReads()
    {
        var resolver = LoadOrderResolver.Build(new[] { _pathA });
        using var svc = LoadOrderService.ForGuard(resolver, new UserConfigStore(Path.Combine(_root, "guard-user.json")));
        var before = svc.Counters.SessionOverlayOpens;

        Assert.Contains("HcCcWeapA", Read(svc, _weapA));

        Assert.Same(resolver.Counters, svc.Counters);
        Assert.True(svc.Counters.SessionOverlayOpens > before, "the read's overlay open did not count on the service.");
    }
}

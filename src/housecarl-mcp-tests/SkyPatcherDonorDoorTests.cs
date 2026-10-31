using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;
using Noggog;
using Xunit;
using static HousecarlMcpTests.ApplyGuardWorld;

namespace HousecarlMcpTests;

/// <summary>Six recipes in one plugin craft one weapon in a master, and a SkyPatcher line filters the recipes by that
/// weapon's keywords (#1282): the replay reads the donor once for the call, not once per recipe.</summary>
[Trait("tier", "unit")]
public sealed class SkyPatcherDonorDoorTests : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-sp-donor-");
    static readonly ModKey A = new("HcSpDonorA", ModType.Master), B = new("HcSpDonorB", ModType.Plugin);
    readonly List<FormKey> _recipes = new();
    readonly LoadOrderService _svc;

    public SkyPatcherDonorDoorTests()
    {
        var a = new SkyrimMod(A, SkyrimRelease.SkyrimSE);
        var kw = a.Keywords.AddNew("HcSpDonorKw");
        var weapon = a.Weapons.AddNew("HcSpDonorWeap");
        weapon.Keywords = new ExtendedList<IFormLinkGetter<IKeywordGetter>> { kw.ToLink() };
        a.BeginWrite.ToPath(_mo2.InMod("SpA", A)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        var b = new SkyrimMod(B, SkyrimRelease.SkyrimSE);
        for (int i = 0; i < 6; i++)
        {
            var r = b.ConstructibleObjects.AddNew("HcSpDonorRecipe" + i);
            r.CreatedObject.SetTo(weapon.FormKey);
            r.CreatedObjectCount = 1;
            _recipes.Add(r.FormKey);
        }
        b.BeginWrite.ToPath(_mo2.InMod("SpB", B)).WithLoadOrder(new ISkyrimModGetter[] { a }).Write();
        var ini = Path.Combine(_mo2.ModsDir, "SpIni", "SKSE", "Plugins", "SkyPatcher", "constructibleObject");
        Directory.CreateDirectory(ini);
        File.WriteAllText(Path.Combine(ini, "donor.ini"), $"filterByKeywords={A.FileName}|{kw.FormKey.ID:X}:count=5\r\n");
        var names = new[] { A, B }.Select(k => k.FileName.String).ToList();
        _mo2.Profile(string.Join("\r\n", names) + "\r\n", string.Join("", names.Select(n => "*" + n + "\r\n")), "+SpIni\r\n+SpB\r\n+SpA\r\n");
        _svc = _mo2.Open();
    }

    public void Dispose() { _svc.Dispose(); _mo2.Delete(); }

    string Read() => RecordsTools.Records(_svc, formids: _recipes.Select(ScratchMo2.Fid).ToArray(),
        source: Je("{\"overlay\": \"skypatcher\", \"state\": \"post\"}"),
        project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "CreatedObjectCount" } }, format: "json");

    [Fact]
    public void ADonorNamedForEveryRecordIsReadOnce()
    {
        Read();   // the index and the asset build, paid once
        var before = new Dictionary<(string Plugin, string Group), int>(_svc.Counters.GroupReads);
        var seeks = _svc.Counters.BodySeeks;
        var r = Read();
        Assert.False(r.StartsWith("error"), r);
        Assert.Equal(6, r.Split("\"5\"").Length - 1);   // every recipe matched through its weapon's keyword
        Assert.Equal(0, _svc.Counters.BodySeeks - seeks);
        Assert.Equal(1, _svc.Counters.GroupReads.GetValueOrDefault((A.FileName.String, "Weapons")) - before.GetValueOrDefault((A.FileName.String, "Weapons")));
    }
}

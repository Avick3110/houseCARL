using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The service-layer read contract under the records tool: bulk FormID to identity with per-item errors, the
/// engine-implicit Player and PlayerRef forms, resolve_names annotating a link without replacing its token, and the
/// container depth hint present on a read and absent on a write read-back. A master defines keyword KA, a dangling
/// keyword link, and two named weapons; a replacer overrides W1 and defines W3.</summary>
[Trait("tier", "integration")]
public sealed class ResolveRefsIdentityTests : IDisposable
{
    const string MasterName = "hcw2Master.esp", ReplName = "hcw2Repl.esp";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-resolve-refs-identity-" + Guid.NewGuid().ToString("N"));
    readonly LoadOrderResolver _resolver;
    readonly LoadOrderService _svc;
    readonly Weapon _w1;
    readonly FormKey _ka, _ghost, _w3;

    public ResolveRefsIdentityTests()
    {
        _ = TestCorpus.Path;
        Directory.CreateDirectory(_dir);
        var master = new SkyrimMod(ModKey.FromNameAndExtension(MasterName), SkyrimRelease.SkyrimSE);
        var ka = master.Keywords.AddNew(); ka.EditorID = "hcw2KwA"; _ka = ka.FormKey;
        _ghost = new FormKey(master.ModKey, 0x000FFF);   // nothing defines it; in the master's own space, so no missing master
        _w1 = master.Weapons.AddNew(); _w1.EditorID = "hcw2Sword1"; _w1.Name = "Iron Sword";
        _w1.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>>
            { new FormLink<IKeywordGetter>(_ka), new FormLink<IKeywordGetter>(_ghost) };
        var w2 = master.Weapons.AddNew(); w2.EditorID = "hcw2Sword2"; w2.Name = "Steel Sword";
        master.BeginWrite.ToPath(Path.Combine(_dir, MasterName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var repl = new SkyrimMod(ModKey.FromNameAndExtension(ReplName), SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(repl, _w1)).BasicStats = new WeaponBasicStats { Damage = 15 };
        var w3 = repl.Weapons.AddNew(); w3.EditorID = "hcw2Sword3"; w3.Name = "Ebony Sword"; _w3 = w3.FormKey;
        repl.BeginWrite.ToPath(Path.Combine(_dir, ReplName)).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

        _resolver = LoadOrderResolver.Build(new[] { Path.Combine(_dir, MasterName), Path.Combine(_dir, ReplName) });
        _svc = LoadOrderService.ForGuard(_resolver, new UserConfigStore(Path.Combine(_dir, "houseCARL.user.json")));
    }

    public void Dispose()
    {
        _resolver.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* temp cleanup best-effort */ }
    }

    // Probe: "resolve returns one row per input, in order"; "W1 → Weapon/hcw2Sword1/name 'Iron Sword'/winner hcw2Repl.esp
    // (winner is the OVERRIDE, not the master)"; "keyword KA → Keyword/hcw2KwA with name=null"; "W3 → Weapon/hcw2Sword3/
    // name 'Ebony Sword'".
    [Fact]
    public void EachFormIdResolvesToItsTypeEditorIdNameAndWinnerInInputOrder()
    {
        var refs = _svc.ReadArea.ResolveRefs(new[] { _w1.FormKey.ToString(), _ka.ToString(), _w3.ToString() });

        Assert.Equal(3, refs.Count);
        Assert.True(refs[0] is { Resolved: true, Type: "Weapon", EditorId: "hcw2Sword1", Name: "Iron Sword", Winner: ReplName });
        Assert.True(refs[1] is { Resolved: true, Type: "Keyword", EditorId: "hcw2KwA", Name: null });
        Assert.True(refs[2] is { Resolved: true, Type: "Weapon", EditorId: "hcw2Sword3", Name: "Ebony Sword" });
    }

    // Probe: "a valid-but-absent FormID → Resolved=false, carrying the three-cause reason, not the malformed-input one";
    // "a malformed FormID → per-item error, the batch still returns the other 4 rows".
    [Fact]
    public void AnAbsentAndAMalformedFormIdEachGetTheirOwnErrorRowAndTheGoodRowSurvives()
    {
        var refs = _svc.ReadArea.ResolveRefs(new[] { "000800:Nonexist.esp", "not-a-formid", _ka.ToString() });

        Assert.Equal(3, refs.Count);
        Assert.False(refs[0].Resolved);
        Assert.Equal("000800:Nonexist.esp", refs[0].Token);
        Assert.NotNull(refs[0].Error);
        Assert.DoesNotContain("bad FormID", refs[0].Error!, StringComparison.OrdinalIgnoreCase);
        Assert.False(refs[1].Resolved);
        Assert.Contains("bad FormID", refs[1].Error!, StringComparison.OrdinalIgnoreCase);
        Assert.True(refs[2].Resolved);
    }

    // Probe: "a target repeated in one batch resolves identically (memoised)". Pins the identical answer; whether it
    // came from the memo is not observable from here.
    [Fact]
    public void ATargetRepeatedInOneBatchResolvesIdentically()
    {
        var dup = _svc.ReadArea.ResolveRefs(new[] { _ka.ToString(), _ka.ToString() });
        Assert.Equal(new[] { "hcw2KwA", "hcw2KwA" }, dup.Select(r => r.EditorId));
    }

    // Probe: "PlayerRef (000014:Skyrim.esm) → Resolved, PlacedNpc/PlayerRef, winner <engine>"; "Player (000007:Skyrim.esm)
    // → Resolved, Npc/Player, winner <engine>"; "a NON-implicit sub-0x800 form (000015:Skyrim.esm) is STILL unresolved".
    [Fact]
    public void TheTwoEngineImplicitFormsResolveAndTheNextReservedFormStillDangles()
    {
        var ei = _svc.ReadArea.ResolveRefs(new[] { "000014:Skyrim.esm", "000007:Skyrim.esm", "000015:Skyrim.esm" });

        Assert.True(ei[0] is { Resolved: true, Type: "PlacedNpc", EditorId: "PlayerRef", Winner: "<engine>" });
        Assert.True(ei[1] is { Resolved: true, Type: "Npc", EditorId: "Player", Winner: "<engine>" });
        Assert.False(ei[2].Resolved);
    }

    // Probe: "resolve_names read surfaced the 2 keyword elements"; "the KA element's ROUND-TRIP TOKEN is unchanged"; "its
    // Link annotation resolves to the keyword identity (editorid hcw2KwA)"; "a link whose target no active plugin defines
    // is annotated UNRESOLVED, token still intact".
    [Fact]
    public void ResolveNamesAnnotatesEachLinkAndKeepsItsToken()
    {
        var fields = _svc.ReadArea.ResolveRead(_w1.FormKey, null, new[] { "Keywords" }, false, depth: 2, resolveNames: true)
            .Record!.Fields.Where(f => f.Path.StartsWith("Keywords[", StringComparison.Ordinal)).ToList();

        Assert.Equal(2, fields.Count);
        var ka = Assert.Single(fields, f => f.Token == _ka.ToString());
        Assert.True(ka is { HasValue: true, Link: { Resolved: true, EditorId: "hcw2KwA" } });
        var ghost = Assert.Single(fields, f => f.Token == _ghost.ToString());
        Assert.True(ghost is { HasValue: true, Link: { Resolved: false } });
    }

    // Probe: "without resolve_names, NO leaf carries a Link annotation (default behavior unchanged)". Strengthened: the
    // probe's All() passed on an empty field list; this first requires the two keyword leaves to be there.
    [Fact]
    public void WithoutResolveNamesNoLeafCarriesALink()
    {
        var fields = _svc.ReadArea.ResolveRead(_w1.FormKey, null, new[] { "Keywords" }, false, depth: 2, resolveNames: false).Record!.Fields;

        Assert.Equal(2, fields.Count(f => f.Path.StartsWith("Keywords[", StringComparison.Ordinal)));
        Assert.All(fields, f => Assert.Null(f.Link));
    }

    // Probe: "read_record (HAS depth=) keeps the classic ' — pass depth=2 to expand' hint"; "containerHint:null (the write
    // read-back lane) renders the bare count — no hint at all".
    [Fact]
    public void AReadNamesTheDepthKnobAndAWriteReadBackRendersTheBareCount()
    {
        var read = _svc.ReadArea.ResolveRead(_w1.FormKey, null, new[] { "Keywords" }, false)
            .Record?.Fields.FirstOrDefault(f => f.Path == "Keywords")?.Note;
        Assert.Contains("pass depth=2 to expand", read);

        var bare = ReadEngine.ReadFields(_w1, new[] { "Keywords" }, containerHint: null).Fields.FirstOrDefault()?.Note;
        Assert.StartsWith("[list:", bare);
        Assert.DoesNotContain("depth", bare);
    }
}

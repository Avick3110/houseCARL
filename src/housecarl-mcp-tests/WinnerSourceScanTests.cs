using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>An order whose winners are spread across THREE plugins, which is what a winner-source scan has to get
/// right: a master defining 30 weapons, a first plugin overriding the first ten and a second overriding a middle
/// ten that overlaps it. So five records win in the first plugin, ten in the second and fifteen in the master, and
/// a scan that fetched the wrong plugin's body — or the first declarer's — matches a different set.</summary>
public sealed class WinnerSourceWorld : IDisposable
{
    public const int Weapons = 30;
    public const ushort MasterDamage = 5, LowDamage = 20, MidDamage = 30;

    public string Root { get; }
    public LoadOrderService Svc { get; }
    public string MasterName { get; }
    public string LowName { get; }
    public string MidName { get; }
    public IReadOnlyList<FormKey> Keys { get; }

    readonly string _priorCorpusPath;

    public WinnerSourceWorld()
    {
        _priorCorpusPath = CorpusRulebook.CorpusPath;
        Root = Path.Combine(Path.GetTempPath(), "hc-winner-source-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profiles = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        foreach (var d in new[] { profiles, mods, Path.Combine(Root, "game", "Data") }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        var masterKey = new ModKey("HcWsrcBase", ModType.Master);
        var lowKey = new ModKey("HcWsrcLow", ModType.Plugin);
        var midKey = new ModKey("HcWsrcMid", ModType.Plugin);
        MasterName = masterKey.FileName.String; LowName = lowKey.FileName.String; MidName = midKey.FileName.String;

        var keys = new List<FormKey>();
        var masterDir = Path.Combine(mods, "BaseMod"); Directory.CreateDirectory(masterDir);
        var master = new SkyrimMod(masterKey, SkyrimRelease.SkyrimSE);
        for (int i = 0; i < Weapons; i++)
        {
            var fk = new FormKey(masterKey, (uint)(0xA00 + i));
            keys.Add(fk);
            master.Weapons.Add(new Weapon(fk, SkyrimRelease.SkyrimSE)
            {
                EditorID = "HcWsrcWeap" + i,
                BasicStats = new WeaponBasicStats { Damage = MasterDamage, Value = 10, Weight = 1 },
            });
        }
        Keys = keys;
        var masterPath = Path.Combine(masterDir, MasterName);
        master.BeginWrite.ToPath(masterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        var masterOv = SkyrimMod.CreateFromBinaryOverlay(masterPath, SkyrimRelease.SkyrimSE);

        Write(mods, "LowMod", lowKey, masterOv, 0, 10, LowDamage);
        Write(mods, "MidMod", midKey, masterOv, 5, 15, MidDamage);

        var order = new[] { MasterName, LowName, MidName };
        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", order) + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"), string.Concat(order.Select(n => "*" + n + "\r\n")));
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+MidMod\r\n+LowMod\r\n+BaseMod\r\n");

        var genDir = Path.Combine(Root, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(Root, "corpus-ref"));
        CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
        Svc.Stats();
    }

    void Write(string mods, string folder, ModKey key, ISkyrimModGetter masterOv, int from, int to, ushort damage)
    {
        var dir = Path.Combine(mods, folder); Directory.CreateDirectory(dir);
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        for (int i = from; i < to; i++)
        {
            var w = m.Weapons.GetOrAddAsOverride(masterOv.Weapons.First(x => x.FormKey == Keys[i]));
            w.BasicStats!.Damage = damage;
        }
        m.BeginWrite.ToPath(Path.Combine(dir, key.FileName.String)).WithLoadOrder(new[] { masterOv }).Write();
    }

    public void Dispose()
    {
        Svc.Dispose();
        CorpusRulebook.CorpusPath = _priorCorpusPath;
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

public sealed class WinnerSourceFixture : IDisposable
{
    public WinnerSourceWorld W { get; } = new();
    public void Dispose() => W.Dispose();
}

/// <summary>
/// <c>where_source=winner</c> over a scope whose records win in three different plugins (#251). The winner bodies
/// are gathered by PLUGIN — one enumeration per distinct winner plugin — instead of one whole-overlay walk per
/// candidate, so these pin that the batched gather answers with the same set the per-record fetch did: the true
/// winner's body for every candidate, including the ones the scoped plugin itself wins.
/// </summary>
[Trait("tier", "integration")]
public sealed class WinnerSourceScanTests : IClassFixture<WinnerSourceFixture>
{
    readonly WinnerSourceWorld _w;
    public WinnerSourceScanTests(WinnerSourceFixture f) => _w = f.W;

    const string DamagePath = "BasicStats.Damage";

    string[] Matched(ushort damage, string[]? scope = null)
    {
        var json = RecordsTools.Records(_w.Svc,
            plugins: new RecordsTools.RecordsScope { names = scope ?? new[] { _w.MasterName } },
            where: new[] { $"{DamagePath} = {damage}" }, where_source: "winner",
            project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "EditorID" } },
            format: "json", max_chars: 200000, limit: 1000);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("error", out _), json);
        return doc.RootElement.GetProperty("matches").EnumerateArray()
                  .Select(m => m.GetProperty("formid").GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    string[] Expected(int from, int to) =>
        Enumerable.Range(from, to - from).Select(i => _w.Keys[i])
                  .Select(fk => fk.ToString()).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    /// <summary>The middle ten win in the LAST plugin, and only those match its damage — a gather that took the
    /// first declarer, or the scoped body, would answer a different set.</summary>
    [Fact]
    public void TheRecordsWonByTheHighestPluginMatchOnThatPluginsBody() =>
        Assert.Equal(Expected(5, 15), Matched(WinnerSourceWorld.MidDamage));

    /// <summary>The first five are overridden by the low plugin and NOT by the high one, so they win there — the
    /// gather has to fetch from a second winner plugin in the same scan.</summary>
    [Fact]
    public void TheRecordsWonByTheMiddlePluginMatchOnThatOne() =>
        Assert.Equal(Expected(0, 5), Matched(WinnerSourceWorld.LowDamage));

    /// <summary>And the fifteen nobody overrides win in the MASTER, which is also the scoped plugin: a gather that
    /// only looked at plugins above the scope would lose half the order.</summary>
    [Fact]
    public void TheRecordsNobodyOverridesWinInTheScopedPluginItself() =>
        Assert.Equal(Expected(15, WinnerSourceWorld.Weapons), Matched(WinnerSourceWorld.MasterDamage));

    /// <summary>Every candidate is accounted for exactly once across the three winner plugins — no record is
    /// dropped by the gather and none is counted twice.</summary>
    [Fact]
    public void TheThreeWinnerPluginsPartitionTheScopeWithNothingLostOrDoubled()
    {
        var all = Matched(WinnerSourceWorld.MidDamage).Concat(Matched(WinnerSourceWorld.LowDamage))
                  .Concat(Matched(WinnerSourceWorld.MasterDamage)).ToArray();
        Assert.Equal(WinnerSourceWorld.Weapons, all.Length);
        Assert.Equal(WinnerSourceWorld.Weapons, all.Distinct().Count());
    }

    /// <summary>A scope spanning several plugins still de-dups to one verdict per record: the winner answer is
    /// FormKey-intrinsic, so a key touched by two scoped plugins must not match twice.</summary>
    [Fact]
    public void AMultiPluginScopeStillAnswersOncePerRecord() =>
        Assert.Equal(Expected(5, 15),
                     Matched(WinnerSourceWorld.MidDamage, new[] { _w.MasterName, _w.LowName, _w.MidName }));

    /// <summary>The same answer with a type scope, which narrows each winner plugin's walk to that type's GRUP.</summary>
    [Fact]
    public void ATypeScopedWinnerSourceScanAnswersTheSameSet()
    {
        var json = RecordsTools.Records(_w.Svc, types: new[] { "WEAP" },
            plugins: new RecordsTools.RecordsScope { names = new[] { _w.MasterName } },
            where: new[] { $"{DamagePath} = {WinnerSourceWorld.MidDamage}" }, where_source: "winner",
            project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "EditorID" } },
            format: "json", max_chars: 200000, limit: 1000);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(Expected(5, 15),
                     doc.RootElement.GetProperty("matches").EnumerateArray()
                        .Select(m => m.GetProperty("formid").GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    /// <summary>The gather itself, under the tool: every candidate's winner body, in one enumeration per winner
    /// plugin. Its answer must be the winner's body — the damage each record's winner declares — for all thirty.</summary>
    [Fact]
    public void EveryCandidateGetsItsOwnWinnersBody_NotSomeOtherPluginsCopy()
    {
        for (int i = 0; i < WinnerSourceWorld.Weapons; i++)
        {
            ushort expected = i < 5 ? WinnerSourceWorld.LowDamage
                            : i < 15 ? WinnerSourceWorld.MidDamage
                            : WinnerSourceWorld.MasterDamage;
            var one = RecordsTools.Records(_w.Svc, formids: new[] { _w.Keys[i].ToString() },
                                           project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { DamagePath } },
                                           max_chars: 20000);
            Assert.Contains($"{DamagePath} = {expected}", one);
        }
    }
}

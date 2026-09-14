using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The placement paragraph (#718): where the merged plugin has to load, and which plugins between the first
/// and last donor change meaning depending on where it sits.</summary>
[Trait("tier", "integration")]
public sealed class MergePlacementTests : IClassFixture<MergePlacementWorld>
{
    readonly MergePlacementWorld _w;
    public MergePlacementTests(MergePlacementWorld w) => _w = w;

    /// <summary>Donors at either end of the order: the paragraph gives both positions, sends the merge to the last
    /// donor's position, and names the one plugin in between that overrides a donor record — not the one that does
    /// not.</summary>
    [Fact]
    public void ThePlacementParagraphNamesTheInterveningPluginThatTouchesADonorRecord()
    {
        var o = _w.Svc.MergePlugins(new[] { MergePlacementWorld.Early, MergePlacementWorld.Late }, "HcPlaceSpread");

        Assert.True(o.Success, o.Error);
        var rendered = WriteTools.RenderMerge(o);
        Assert.Contains("placement: the donors sat at load-order positions 2–5", rendered);
        Assert.Contains("at position 5, where the last donor sat", rendered);
        Assert.Contains(MergePlacementWorld.Middle, rendered[rendered.IndexOf("placement:", StringComparison.Ordinal)..]);
        Assert.DoesNotContain(MergePlacementWorld.Bystander, rendered);
    }

    /// <summary>The master clause comes off the written header: the merged plugin must load after its last master.</summary>
    [Fact]
    public void ThePlacementParagraphNamesTheLastMasterAndItsPosition()
    {
        var o = _w.Svc.MergePlugins(new[] { MergePlacementWorld.Early, MergePlacementWorld.Late }, "HcPlaceMaster");

        Assert.True(o.Success, o.Error);
        Assert.Contains("after its last master " + MergePlacementWorld.Master + " (position 1)", WriteTools.RenderMerge(o));
    }

    /// <summary>A rename has no interval at all, so the paragraph gives the one position and claims nothing about what
    /// sits around it — a "nothing touches these records" line would be vacuously true and read as a finding.</summary>
    [Fact]
    public void ASingleDonorGetsItsOwnPositionAndClaimsNothingAboutAnInterval()
    {
        var o = _w.Svc.MergePlugins(new[] { MergePlacementWorld.Late }, "HcPlaceRename");

        Assert.True(o.Success, o.Error);
        var rendered = WriteTools.RenderMerge(o);
        Assert.Contains("placement: the donor sat at load-order position 5", rendered);
        Assert.Contains("a rename has no interval", rendered);
        Assert.DoesNotContain("no plugin between the first and last donor", rendered);
    }

    /// <summary>The negative claims only what the pass looked for — the records the donors ORIGINATE — because an
    /// override a donor carries at its master's FormID was never one of its targets.</summary>
    [Fact]
    public void TheNegativeSaysWhichRecordsItCovers()
    {
        var o = _w.Svc.MergePlugins(new[] { MergePlacementWorld.Middle, MergePlacementWorld.Late }, "HcPlaceScope");

        Assert.True(o.Success, o.Error);
        var rendered = WriteTools.RenderMerge(o);
        Assert.Contains("no plugin between the first and last donor references or overrides a record the donors ORIGINATE", rendered);
        Assert.Contains("overrides the donors carry at their masters' FormIDs are outside what this pass looked for", rendered);
    }
}

/// <summary>The siting derivation itself: the position contract, and the plugins in the range the identify pass could
/// not look into.</summary>
[Trait("tier", "unit")]
public sealed class MergeSitingTests
{
    /// <summary>Positions come in 0-based (what the resolver hands out) and come back 1-based (what a report prints),
    /// and a plugin in the range the pass could not read is counted rather than silently read as absence.</summary>
    [Fact]
    public void AnUnreadablePluginBetweenTheDonorsIsCountedNotTakenForAbsence()
    {
        var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            { ["A.esp"] = 9, ["X.esp"] = 19, ["B.esp"] = 29, ["Far.esp"] = 40 };

        var s = MergeLoadPosition.Derive(
            new[] { ("A.esp", 9), ("B.esp", 29) }, Array.Empty<string>(),
            Array.Empty<string>(), new[] { "X.esp", "Far.esp" },
            p => positions.TryGetValue(p, out var i) ? i : null);

        Assert.Equal(10, s.FirstPosition);
        Assert.Equal(30, s.LastPosition);
        Assert.Empty(s.Intervening);
        Assert.Equal(1, s.UnreadBetween);          // Far.esp is past the last donor, so it is not in the range
    }
}

/// <summary>A synthetic MO2 instance whose donors sit at either end of a five-plugin order: a master, the early donor,
/// a plugin that overrides the early donor's weapon, a plugin that touches nothing of theirs, and the late donor.</summary>
public sealed class MergePlacementWorld : IDisposable
{
    public string Root { get; }
    public LoadOrderService Svc { get; }

    public const string Master = "HcPlMaster.esm";
    public const string Early = "HcPlEarly.esp";        // donor, position 2
    public const string Middle = "HcPlMiddle.esp";      // overrides the early donor's weapon, position 3
    public const string Bystander = "HcPlBystander.esp";// touches nothing of the donors', position 4
    public const string Late = "HcPlLate.esp";          // donor, position 5

    public MergePlacementWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-merge-placement-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profile = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        foreach (var d in new[] { profile, mods, Path.Combine(Root, "game", "Data") }) Directory.CreateDirectory(d);

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        var masterKey = new ModKey("HcPlMaster", ModType.Master);
        var earlyKey = new ModKey("HcPlEarly", ModType.Plugin);
        var masterPath = Write(mods, "MasterMod", masterKey, deps: null);
        using (var master = SkyrimMod.CreateFromBinaryOverlay(masterPath, SkyrimRelease.SkyrimSE))
        {
            // Both donors override the master's weapon, so the merged plugin carries it as a master.
            var earlyPath = Write(mods, "EarlyMod", earlyKey, deps: new[] { master });
            Write(mods, "BystanderMod", new ModKey("HcPlBystander", ModType.Plugin), deps: null);
            using var early = SkyrimMod.CreateFromBinaryOverlay(earlyPath, SkyrimRelease.SkyrimSE);
            Write(mods, "MiddleMod", new ModKey("HcPlMiddle", ModType.Plugin), deps: new[] { (ISkyrimModGetter)early });
            Write(mods, "LateMod", new ModKey("HcPlLate", ModType.Plugin), deps: new[] { master });
        }

        var order = new[] { Master, Early, Middle, Bystander, Late };
        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", order) + "\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), string.Join("\r\n", order.Select(p => "*" + p)) + "\r\n");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"),
            "# header\r\n+LateMod\r\n+BystanderMod\r\n+MiddleMod\r\n+EarlyMod\r\n+MasterMod\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
    }

    /// <summary>One plugin with a weapon of its own, plus an override of the first weapon of each dependency.</summary>
    static string Write(string mods, string folder, ModKey key, IReadOnlyList<ISkyrimModGetter>? deps)
    {
        var dir = Path.Combine(mods, folder);
        Directory.CreateDirectory(dir);
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        m.Weapons.Add(new Weapon(new FormKey(key, 0x800), SkyrimRelease.SkyrimSE) { EditorID = key.Name + "Own" });
        foreach (var dep in deps ?? Array.Empty<ISkyrimModGetter>())
            m.Weapons.Add(new Weapon(dep.Weapons.First().FormKey, SkyrimRelease.SkyrimSE) { EditorID = key.Name + "Over" });
        var path = Path.Combine(dir, key.FileName.String);
        m.BeginWrite.ToPath(path).WithLoadOrder(deps ?? Array.Empty<ISkyrimModGetter>()).Write();
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best-effort */ }
    }
}

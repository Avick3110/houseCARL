using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The read cache's freshness key. It was the last-write time alone, so an edit that landed inside the
/// filesystem's timestamp granularity — or one whose tool restored the timestamp it found — served stale parsed
/// state with nothing saying so (#406). The key carries the file's LENGTH beside its last-write, and both
/// <see cref="LoadOrderResolver.RefreshIfStale"/> and the epoch fingerprint read it.</summary>
[Trait("tier", "integration")]
public sealed class FreshnessKeyTests : IDisposable
{
    readonly string _root;
    readonly ModKey _key = new("HcFreshKey", ModType.Plugin);
    readonly string _path;

    public FreshnessKeyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-freshkey-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, _key.FileName.String);
        Write(weapons: 1);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ } }

    /// <summary>Rewrite the plugin with <paramref name="weapons"/> records — a different file LENGTH, same name and
    /// same path.</summary>
    void Write(int weapons)
    {
        var mod = new SkyrimMod(_key, SkyrimRelease.SkyrimSE);
        for (int i = 0; i < weapons; i++)
            mod.Weapons.Add(new Weapon(new FormKey(_key, (uint)(0x800 + i)), SkyrimRelease.SkyrimSE)
                            { EditorID = $"HcFreshWeap{i:D2}" });
        mod.ModHeader.Stats.NextFormID = (uint)(0x800 + weapons);
        mod.BeginWrite.ToPath(_path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();
    }

    /// <summary>Rewrite the file and put the last-write time back where it was — what an mtime-only key cannot
    /// see, and the shape a tool that preserves timestamps produces on a real install.</summary>
    void EditWithoutMovingMtime(int weapons)
    {
        var before = File.GetLastWriteTimeUtc(_path);
        Write(weapons);
        File.SetLastWriteTimeUtc(_path, before);
        Assert.Equal(before, File.GetLastWriteTimeUtc(_path));   // the premise of the test, not an assumption
    }

    [Fact]
    public void AnEditThatLeavesTheMtimeAloneIsStillSeenAsStale()
    {
        using var resolver = LoadOrderResolver.Build(new[] { _path });
        Assert.False(resolver.RefreshIfStale());                 // nothing changed yet

        EditWithoutMovingMtime(weapons: 4);

        Assert.True(resolver.RefreshIfStale());                  // the length moved, so the parsed state is rebuilt
        Assert.False(resolver.RefreshIfStale());                 // …and the new stamp is the baseline from here
    }

    /// <summary>The epoch is a coverage claim over the build it names, so the same edit has to fingerprint
    /// differently — an unchanged epoch across a changed file is the claim over-stating what it covers.</summary>
    [Fact]
    public void TheEpochChangesOverAnEditThatLeavesTheMtimeAlone()
    {
        using var resolver = LoadOrderResolver.Build(new[] { _path });
        var before = resolver.Capture().Epoch;

        EditWithoutMovingMtime(weapons: 4);
        resolver.RefreshIfStale();

        Assert.NotEqual(before, resolver.Capture().Epoch);
    }

    /// <summary>…and the cheap no-change path still says nothing changed: a key that reported stale every call
    /// would rebuild the whole index on every read.</summary>
    [Fact]
    public void AnUntouchedOrderIsNotReportedStale()
    {
        using var resolver = LoadOrderResolver.Build(new[] { _path });

        Assert.False(resolver.RefreshIfStale());
        Assert.False(resolver.RefreshIfStale());
    }
}

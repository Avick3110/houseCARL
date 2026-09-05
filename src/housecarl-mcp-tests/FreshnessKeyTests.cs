using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The read cache's freshness key. It was the last-write time alone, so an edit that landed inside the
/// filesystem's timestamp granularity — or one whose tool restored the timestamp it found — served stale parsed
/// state with nothing saying so (#406). The key carries the file's LENGTH beside its last-write, and both
/// <see cref="LoadOrderResolver.RefreshIfStale"/> and the epoch fingerprint read it — as does every sibling cache,
/// through the one shared <see cref="FileStamp"/>.</summary>
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
    /// see, and the shape a tool that preserves timestamps produces on a real install. Returns the timestamp that
    /// must not have moved, for the caller to re-check AFTER the act: asserting it here only would leave the test
    /// green off the mtime term if anything (a lazily flushed write, a scanner) nudged the stamp in between.</summary>
    DateTime EditWithoutMovingMtime(int weapons)
    {
        var before = File.GetLastWriteTimeUtc(_path);
        Write(weapons);
        File.SetLastWriteTimeUtc(_path, before);
        return before;
    }

    /// <summary>The premise, checked after the act: nothing moved the last-write time across the whole window, so
    /// the staleness the test observed can only have come from the length term.</summary>
    void AssertMtimeNeverMoved(DateTime before) => Assert.Equal(before, File.GetLastWriteTimeUtc(_path));

    [Fact]
    public void AnEditThatLeavesTheMtimeAloneIsStillSeenAsStale()
    {
        using var resolver = LoadOrderResolver.Build(new[] { _path });
        Assert.False(resolver.RefreshIfStale());                 // nothing changed yet

        var before = EditWithoutMovingMtime(weapons: 4);

        Assert.True(resolver.RefreshIfStale());                  // the length moved, so the parsed state is rebuilt
        Assert.False(resolver.RefreshIfStale());                 // …and the new stamp is the baseline from here
        AssertMtimeNeverMoved(before);
    }

    /// <summary>The epoch is a coverage claim over the build it names, so the same edit has to fingerprint
    /// differently — an unchanged epoch across a changed file is the claim over-stating what it covers.</summary>
    [Fact]
    public void TheEpochChangesOverAnEditThatLeavesTheMtimeAlone()
    {
        using var resolver = LoadOrderResolver.Build(new[] { _path });
        var before = resolver.Capture().Epoch;

        var mtime = EditWithoutMovingMtime(weapons: 4);
        resolver.RefreshIfStale();

        Assert.NotEqual(before, resolver.Capture().Epoch);
        AssertMtimeNeverMoved(mtime);
    }

    /// <summary>The key itself, which every sibling cache now shares: two files identical but for their length
    /// stamp differently, so the BSA tables, the MO2 profile gate and the SkyPatcher parse cache answer the same
    /// question the read cache does.</summary>
    [Fact]
    public void TheSharedStampSeparatesTwoFilesThatDifferOnlyInLength()
    {
        var a = Path.Combine(_root, "a.txt");
        var b = Path.Combine(_root, "b.txt");
        File.WriteAllText(a, "one");
        File.WriteAllText(b, "one and more");
        var when = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(a, when);
        File.SetLastWriteTimeUtc(b, when);

        Assert.Equal(FileStamp.Of(a).Mtime, FileStamp.Of(b).Mtime);   // the mtime term alone calls them one file
        Assert.NotEqual(FileStamp.Of(a), FileStamp.Of(b));
    }

    /// <summary>A path that cannot be statted collapses to one sentinel, and that sentinel is not a real stamp —
    /// so a file coming back is a change and a file staying gone is not. Directories carry no length, so their
    /// stamp is the last-write with the size term pinned; the sentinel still separates present from absent, which
    /// is what the loose-subtree cache reads it for.</summary>
    [Fact]
    public void AnUnstattablePathIsTheAbsentSentinelForFilesAndDirectoriesAlike()
    {
        var gone = Path.Combine(_root, "not-there");
        Assert.Equal(FileStamp.Absent, FileStamp.Of(gone));
        Assert.Equal(FileStamp.Absent, FileStamp.OfDirectory(gone));

        var dir = Path.Combine(_root, "subtree");
        Directory.CreateDirectory(dir);
        Assert.NotEqual(FileStamp.Absent, FileStamp.OfDirectory(dir));
        Assert.Equal(FileStamp.Absent, FileStamp.Of(dir));            // a directory is not a file to the file stamp
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

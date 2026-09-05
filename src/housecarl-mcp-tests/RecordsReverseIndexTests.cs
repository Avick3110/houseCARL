using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The unbounded reverse question — <c>references=</c> with no <c>types=</c>/<c>plugins=</c> scope —
/// answered off the reverse-reference index, driven through the tool.</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsReverseIndexTests : RecordsTestBase
{
    public RecordsReverseIndexTests(RecordsFixture f) : base(f) { }

    [Fact]
    public void AnUnboundedReferencesFindsEveryReferencerWithNoScope()
    {
        var r = RecordsTools.Records(Svc, references: new[] { Fid(W.MgefA) });
        Served(r, "HcRecSpellA", "HcRecSpellC");
        Assert.DoesNotContain("HcRecSpellB", r);
    }

    [Fact]
    public void AnUnboundedReferencesOverTwoTargetsUnionsThem()
    {
        var r = RecordsTools.Records(Svc, references: new[] { Fid(W.MgefA), Fid(W.MgefB) });
        Served(r, "HcRecSpellA", "HcRecSpellB", "HcRecSpellC");
    }

    [Fact]
    public void AnUnboundedReferencesOnATargetNothingLinksMatchesNothing()
    {
        var r = RecordsTools.Records(Svc, references: new[] { Fid(W.SpellB) });
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.DoesNotContain("HcRecSpell", r);
    }

    [Fact]
    public void AnUnboundedNegatedReferencesKeepsTheRecordsThatDoNotLinkTheTarget()
    {
        var r = RecordsTools.Records(Svc, references: new[] { "!" + Fid(W.MgefA) });
        Served(r, "HcRecSpellB");
        Assert.DoesNotContain("HcRecSpellA", r);
        Assert.DoesNotContain("HcRecSpellC", r);
    }

    /// <summary>SPEC §2.2's and §3.2's 2026-09-05 amendments read the unbounded negated spelling as the orphan
    /// sweep, so a record something DOES reference is outside the universe however the target reads.</summary>
    [Fact]
    public void AnUnboundedNegatedReferencesIsTheOrphanSweep()
    {
        var r = RecordsTools.Records(Svc, references: new[] { "!" + Fid(W.SpellB) }, limit: 500);
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains("ORPHAN sweep", r);
        Assert.Contains("HcRecSpellA", r);            // nothing links a spell — an orphan
        Assert.DoesNotContain("HcRecMgefFire", r);    // two spells link it — not an orphan
        Assert.DoesNotContain("HcRecNpcParent", r);   // the child's Template links it — not an orphan
    }

    /// <summary>The orphan sweep's universe is millions of records on a real order, and the comparison forms
    /// consume every match uncapped — so the pair is refused with the bound named.</summary>
    [Fact]
    public void TheOrphanSweepIsRefusedUnderAComparisonForm() =>
        Refused(RecordsTools.Records(Svc, references: new[] { "!" + Fid(W.MgefA) }, project: Form("tree")),
                "orphan sweep");

    /// <summary>A positive unbounded references= is NOT the sweep — its universe is what links the target — so the
    /// same forms compose with it.</summary>
    [Fact]
    public void APositiveUnboundedReferencesStillComposesWithAComparisonForm()
    {
        var r = RecordsTools.Records(Svc, references: new[] { Fid(W.MgefA) }, project: Form("tree"));
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
    }

    /// <summary>Every form that consumes the scan as a SELECTION renders the index's accounting — otherwise a
    /// 24-second build and a coverage gap go unmentioned because the response came from another pipeline.</summary>
    [Theory]
    [InlineData("everything")]
    [InlineData("tree")]
    public void TheIndexAccountingRidesTheDerivedForms(string form)
    {
        var r = RecordsTools.Records(Svc, references: new[] { Fid(W.MgefA) }, project: Form(form));
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains("reverse-reference index", r);
        Assert.Contains("key=", r);
    }

    [Fact]
    public void TheIndexAccountingRidesTheJsonLane()
    {
        var r = RecordsTools.Records(Svc, references: new[] { Fid(W.MgefA) }, format: "json");
        Assert.Contains("\"notes\"", r);
        Assert.Contains("reverse-reference index", r);
        Assert.Contains("key=", r);
    }

    [Fact]
    public void AnUnboundedWhereIsStillRefusedNamingTheBound() =>
        Refused(RecordsTools.Records(Svc, where: new[] { "EditorID = HcRecSpellA" }), "where=");

    [Fact]
    public void AnUnboundedReferencesWithAWhereIsStillRefused() =>
        Refused(RecordsTools.Records(Svc, references: new[] { Fid(W.MgefA) },
                                     where: new[] { "EditorID = HcRecSpellA" }), "types=");
}

/// <summary>The index's own lifecycle: what a build costs, that a second call pays nothing, that a plugin whose
/// bytes changed rebuilds only its own slice, and that neither a snapshot swap nor a RESOLVER swap drops it. Each
/// test owns its world, because the accounting these assert on is "what THIS call did" and a shared world would
/// have been indexed by whichever test ran first.</summary>
[Trait("tier", "integration")]
public sealed class ReverseIndexLifecycleTests
{
    [Fact]
    public void TheFirstUnboundedCallReportsTheBuildAndItsPerPluginFreshnessKey()
    {
        using var w = new RecordsWorld();
        var r = RecordsTools.Records(w.Svc, references: new[] { RecordsWorld.Fid(w.MgefA) });
        Assert.Contains("reverse-reference index", r);
        Assert.Contains("key=", r);
        Assert.Contains("partition", r);
    }

    /// <summary>The build clause is only true of the call that paid it; the freshness key is true of every answer
    /// the index serves. A cached call that dropped the key would read as unindexed.</summary>
    [Fact]
    public void ACachedCallStillCarriesTheFreshnessKey()
    {
        using var w = new RecordsWorld();
        RecordsTools.Records(w.Svc, references: new[] { RecordsWorld.Fid(w.MgefA) });
        var second = RecordsTools.Records(w.Svc, references: new[] { RecordsWorld.Fid(w.MgefA) });
        Assert.Contains("reverse-reference index: unchanged", second);
        Assert.Contains("key=", second);
        Assert.DoesNotContain("built ", second);
    }

    [Fact]
    public void ANegatedUnboundedReferencesDeclaresThatItIsTheOrphanSweep()
    {
        using var w = new RecordsWorld();
        var r = RecordsTools.Records(w.Svc, references: new[] { "!" + RecordsWorld.Fid(w.MgefA) });
        Assert.Contains("ORPHAN sweep", r);
        Assert.Contains("nothing in the order references", r);
    }

    [Fact]
    public void TheIndexIsBuiltOnceAndASecondCallRebuildsNothing()
    {
        using var w = new RecordsWorld();
        var first = w.Svc.CaptureView().EnsureReverseIndex();
        Assert.True(first.Rebuilt > 0);
        Assert.True(first.Partitions > 1, "the world must hold more than one plugin for the partition claim to mean anything");
        var second = w.Svc.CaptureView().EnsureReverseIndex();
        Assert.Equal(0, second.Rebuilt);
        Assert.Equal(first.Partitions, second.Partitions);
        Assert.Equal(first.Key, second.Key);
    }

    [Fact]
    public void AChangedPluginRebuildsOnlyItsOwnPartition()
    {
        using var w = new RecordsWorld();
        var first = w.Svc.CaptureView().EnsureReverseIndex();
        File.SetLastWriteTimeUtc(w.OverrideFile, DateTime.UtcNow.AddHours(1));
        var after = w.Svc.CaptureView().EnsureReverseIndex();   // the capture rebuilds the SNAPSHOT; the index must not follow it wholesale
        Assert.Equal(1, after.Rebuilt);
        Assert.Equal(first.Partitions, after.Partitions);
        Assert.NotEqual(first.Key, after.Key);
    }

    /// <summary>A target FormKey is master-qualified and independent of the partition that carries the link, so a
    /// touched MASTER invalidates its own partition and nothing else — and the answers other partitions hold about
    /// its records still stand.</summary>
    [Fact]
    public void AMasterMtimeChangeRebuildsOnlyTheMasterAndKeepsEveryAnswerAboutIt()
    {
        using var w = new RecordsWorld();
        var before = RecordsTools.Records(w.Svc, references: new[] { RecordsWorld.Fid(w.MgefA) });
        Assert.Contains("HcRecSpellA", before);
        File.SetLastWriteTimeUtc(Path.Combine(w.ModsDir, "MasterMod", w.MasterName), DateTime.UtcNow.AddHours(1));
        var after = w.Svc.CaptureView().EnsureReverseIndex();
        Assert.Equal(1, after.Rebuilt);
        Assert.Contains("HcRecSpellA", RecordsTools.Records(w.Svc, references: new[] { RecordsWorld.Fid(w.MgefA) }));
    }

    /// <summary>The (path, mtime) key cannot see an edit that preserves the timestamp. That is the disclosed
    /// limit of the key, pinned here so the day the key changes shape something notices.</summary>
    [Fact]
    public void AnEditThatPreservesTheMtimeIsNotSeen()
    {
        using var w = new RecordsWorld();
        var first = w.Svc.CaptureView().EnsureReverseIndex();
        var stamp = File.GetLastWriteTimeUtc(w.OverrideFile);
        var bytes = File.ReadAllBytes(w.OverrideFile);
        File.WriteAllBytes(w.OverrideFile, bytes);
        File.SetLastWriteTimeUtc(w.OverrideFile, stamp);
        var after = w.Svc.CaptureView().EnsureReverseIndex();
        Assert.Equal(0, after.Rebuilt);
        Assert.Equal(first.Key, after.Key);
    }

    /// <summary>Ticking a plugin in MO2 replaces the RESOLVER, not just the snapshot. The index carries across, so
    /// the plugins whose files did not move are not re-walked — the whole point of the partition key.</summary>
    [Fact]
    public void ARemovedPluginDropsItsPartitionAndRebuildsNothingElse()
    {
        using var w = new RecordsWorld();
        var first = w.Svc.CaptureView().EnsureReverseIndex();
        Untick(w, w.MidName);
        var after = w.Svc.CaptureView().EnsureReverseIndex();
        Assert.Equal(first.Partitions - 1, after.Partitions);
        Assert.Equal(0, after.Rebuilt);
        Assert.Contains("HcRecSpellA", RecordsTools.Records(w.Svc, references: new[] { RecordsWorld.Fid(w.MgefA) }));
    }

    [Fact]
    public void AReorderedOrderRebuildsNothingAndStillAnswers()
    {
        using var w = new RecordsWorld();
        var first = w.Svc.CaptureView().EnsureReverseIndex();
        Order(w, w.MasterName, w.OverrideName, w.MidName);
        var after = w.Svc.CaptureView().EnsureReverseIndex();
        Assert.Equal(first.Partitions, after.Partitions);
        Assert.Equal(0, after.Rebuilt);
        Assert.Contains("HcRecSpellC", RecordsTools.Records(w.Svc, references: new[] { RecordsWorld.Fid(w.MgefA) }));
    }

    /// <summary>A plugin the walk cannot read contributes nothing, and the answer is short by whatever it
    /// references — said on EVERY answer, including the cached ones.</summary>
    [Fact]
    public void AnUnreadablePluginIsNamedOnEveryAnswer()
    {
        using var w = new RecordsWorld();
        RecordsTools.Records(w.Svc, references: new[] { RecordsWorld.Fid(w.MgefA) });
        File.WriteAllBytes(Path.Combine(w.ModsDir, "MidMod", w.MidName), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        var first = RecordsTools.Records(w.Svc, references: new[] { RecordsWorld.Fid(w.MgefA) });
        Assert.Contains("the answer is short", first);
        Assert.Contains(w.MidName, first);
        var second = RecordsTools.Records(w.Svc, references: new[] { RecordsWorld.Fid(w.MgefA) });
        Assert.Contains("the answer is short", second);
    }

    /// <summary>Two entries in the order can share a plugin FILENAME, so partitions are keyed on the PATH: a name
    /// key would overwrite one copy's edges and re-walk both plugins on every single call.</summary>
    [Fact]
    public void TwoCopiesOfOneFilenameEachKeepTheirOwnPartition()
    {
        using var w = new RecordsWorld();
        var copyDir = Path.Combine(w.ModsDir, "OldModCopy");
        Directory.CreateDirectory(copyDir);
        var copy = Path.Combine(copyDir, w.OldName);
        File.Copy(w.OldFile, copy);

        using var resolver = LoadOrderResolver.Build(new[]
        {
            Path.Combine(w.ModsDir, "MasterMod", w.MasterName), w.OldFile, copy,
        });
        var first = resolver.Capture().EnsureReverseIndex();
        Assert.Equal(3, first.Partitions);
        Assert.Equal(3, first.Rebuilt);
        var second = resolver.Capture().EnsureReverseIndex();
        Assert.Equal(0, second.Rebuilt);            // a filename key would rebuild the duplicate pair forever
        Assert.Equal(first.Key, second.Key);
    }

    /// <summary>Reads take no build lock, so a refresh must publish a whole generation rather than mutate the one
    /// being read: a torn read is either a thrown collection-modified or a silently short referencer set.</summary>
    [Fact]
    public void ConcurrentReadsAndRefreshesNeverTearTheIndex()
    {
        using var w = new RecordsWorld();
        var view = w.Svc.CaptureView();
        view.EnsureReverseIndex();
        var index = view.ReverseIndex!;
        var targets = new[] { w.MgefA };
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        var stop = false;

        var refresher = Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < 40; i++)
                {
                    File.SetLastWriteTimeUtc(w.OverrideFile, DateTime.UtcNow.AddMinutes(i + 1));
                    w.Svc.CaptureView().EnsureReverseIndex();
                }
            }
            catch (Exception ex) { failures.Add("refresh: " + ex); }
            finally { Volatile.Write(ref stop, true); }
        });
        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            try
            {
                while (!Volatile.Read(ref stop))
                {
                    if (index.ReferencersOf(targets).Count == 0) failures.Add("a read saw an empty index");
                    index.HasAnyReferencer(w.MgefA);
                }
            }
            catch (Exception ex) { failures.Add("read: " + ex); }
        })).ToArray();

        Task.WaitAll(readers.Append(refresher).ToArray());
        Assert.Empty(failures);
    }

    /// <summary>Rewrite the profile so the named plugin is no longer active.</summary>
    static void Untick(RecordsWorld w, string plugin)
    {
        var names = new[] { w.MasterName, w.MidName, w.OverrideName }.Where(n => n != plugin).ToArray();
        Order(w, names);
    }

    /// <summary>Rewrite the profile's plugin list, in this order.</summary>
    static void Order(RecordsWorld w, params string[] names)
    {
        var prof = Path.Combine(w.Instance, "profiles", "Default");
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", names) + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), string.Join("\r\n", names.Select(n => "*" + n)) + "\r\n");
    }
}

using System.Diagnostics;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The delta and tree meter (#932): from the second chunk that read, with more than one chunk left, the batch projects
/// its rate so far over the rows left, adds the time spent since the call began, and refuses past ten minutes with
/// every row dropped and nothing further read. Each lane is metered: list delta, list tree, scan, off-order.
/// </summary>
[Trait("tier", "integration")]
public sealed class ComparisonMeterTests : IClassFixture<RenderCostFixture>
{
    readonly RenderCostWorld _w;
    public ComparisonMeterTests(RenderCostFixture f) => _w = f.W;

    LoadOrderService Svc => _w.Svc;

    static readonly string[] Weap = { "WEAP" };
    const int Chunk = 32;

    static long Ticks(double ms) => (long)(ms * Stopwatch.Frequency / 1000);

    /// <summary>A clock read in order: the call's start, the batch's start, then one read per check.</summary>
    static Func<long> Reads(params double[] ms)
    {
        int i = 0;
        return () => Ticks(ms[Math.Min(i++, ms.Length - 1)]);
    }

    /// <summary>A clock that reads <paramref name="msPerBody"/> for every provider body the tree fold has read.</summary>
    Func<long> PerBody(double msPerBody) => () => Ticks(Svc.Counters.TreeBodiesRead * msPerBody);

    static string Metered(Func<long> clock, Func<string> call)
    {
        var prior = ComparisonMeter.TestClock.Value;
        ComparisonMeter.TestClock.Value = clock;
        try { return call(); }
        finally { ComparisonMeter.TestClock.Value = prior; }
    }

    static System.Text.Json.JsonElement Json(string s) => System.Text.Json.JsonDocument.Parse(s).RootElement.Clone();

    string[] WeaponIds => Svc.CrossQuery(Weap, null, null, false, null, null, RenderCostWorld.Weapons)
                             .Keys.Select(k => k.ToString()).ToArray();

    string[] Armors => _w.TopCoveredIds.Concat(_w.PlainContestedIds).ToArray();

    /// <summary>The 60 weapons (one provider each), then the 40 contested armors (four or five).</summary>
    string[] Hundred => WeaponIds.Concat(Armors).ToArray();

    /// <summary>Weapons, armors, weapons again: 160 rows, dear in the middle.</summary>
    string[] Hundred60 => Hundred.Concat(WeaponIds).ToArray();

    string ListDelta(string[] ids, string? source = null) =>
        RecordsTools.Records(Svc, formids: ids, source: source is null ? null : Json("\"" + source + "\""),
                             versus: Json("\"" + _w.MasterName + "\""),
                             project: new RecordsTools.RecordsProject { form = "delta" }, counts_only: true);

    string ListTree(string[] ids) => RecordsTools.Records(Svc, formids: ids,
                                                          project: new RecordsTools.RecordsProject { form = "tree" }, counts_only: true);

    long Passes() => Svc.Counters.CollectPasses;
    long Bodies() => Svc.Counters.TreeBodiesRead;

    /// <summary>12 s a row over the first 64 rows: 768 s spent, 1,200 s projected for all 100.</summary>
    static Func<long> Dear => Reads(0, 0, 64 * 12_000);

    const string DearHead = "measured 12 s a row over its first 64 records, about 20.0 minutes for all 100, past the ten-minute budget";

    [Fact]
    public void AListDeltaPastTheBudgetRefusesAfterTwoChunksNamingTheRate()
    {
        var ids = Hundred;
        long twoChunks = Delta(Passes, () => ListDelta(ids.Take(2 * Chunk).ToArray()));
        long before = Passes();
        var r = Metered(Dear, () => ListDelta(ids));
        Assert.StartsWith("error: this delta " + DearHead +
                          " (about 45 rows fit at that rate); name only the fields you need with fields= on this same call", r);
        Assert.Contains(RenderBudget.ComparisonListLever, r);
        Assert.Equal(twoChunks, Passes() - before);                  // the first two chunks only, nothing further
    }

    /// <summary>The figure the refusal names clears it when fed back, under the same rate, through two checks.</summary>
    [Fact]
    public void TheRowsThatFitRunWhenFedBack()
    {
        var r = Metered(Reads(0, 0, 64 * 4_000), () => ListDelta(Hundred60));   // 640 s projected for 160
        var fits = int.Parse(System.Text.RegularExpressions.Regex.Match(r, @"about (\d+) rows fit").Groups[1].Value);
        Assert.Equal(135, fits);
        var again = Metered(Reads(0, 0, 64 * 4_000, 96 * 4_000), () => ListDelta(Hundred60.Take(fits).ToArray()));
        Assert.False(again.StartsWith("error:"), again);
        Assert.Contains($"count={fits} ", again);
    }

    /// <summary>A projection just past the budget never prints as ten minutes.</summary>
    [Fact]
    public void AProjectionJustPastTheBudgetReadsAsPastIt()
    {
        var r = Metered(Reads(0, 0, 64 * 6_030), () => ListDelta(Hundred));   // 6.03 s a row, 603 s for all 100
        Assert.StartsWith("error: this delta measured 6.03 s a row over its first 64 records, about 10.1 minutes for all 100", r);
    }

    /// <summary>When the time before the batch leaves no room, the refusal names it and gives no row figure.</summary>
    [Fact]
    public void TimeSpentBeforeTheBatchIsNamedAsTheCause()
    {
        var r = Metered(Reads(0, 590_000, 590_000 + 64 * 1_000), () => ListDelta(Hundred));
        Assert.StartsWith("error: this delta measured 1 s a row over its first 64 records", r);
        Assert.Contains("the selection and index build alone took about 9.9 minutes before the first record was compared", r);
        Assert.DoesNotContain("rows fit", r);
    }

    /// <summary>With no more than one chunk left, the work already paid for is kept rather than refused.</summary>
    [Fact]
    public void OneChunkLeftIsNeverRefused()
    {
        var r = Metered(Reads(0, 0, 1_000_000_000), () => ListDelta(Hundred.Take(2 * Chunk + Chunk).ToArray()));
        Assert.False(r.StartsWith("error:"), r);
    }

    /// <summary>A dear first chunk followed by a cheap second is judged on the two together, and runs where the first
    /// chunk's rate alone would have refused.</summary>
    [Fact]
    public void ADearFirstChunkThenACheapSecondDoesNotRefuse()
    {
        var ids = Armors.Concat(WeaponIds).ToArray();
        long b1 = Delta(Bodies, () => ListTree(ids.Take(Chunk).ToArray()));
        long b2 = Delta(Bodies, () => ListTree(ids.Take(2 * Chunk).ToArray()));
        const double ms = 1_500;
        Assert.True(b1 * ms + b1 * ms / Chunk * (100 - Chunk) > RenderBudget.CeilingMillis);   // the first chunk alone projects past
        Assert.True(b2 * ms + b2 * ms / (2 * Chunk) * (100 - 2 * Chunk) < RenderBudget.CeilingMillis);
        long all = Delta(Bodies, () => ListTree(ids));
        long before = Bodies();
        var r = Metered(PerBody(ms), () => ListTree(ids));
        Assert.False(r.StartsWith("error:"), r);
        Assert.Contains("count=100 ", r);
        Assert.Equal(all, Bodies() - before);                         // every row read once
    }

    [Fact]
    public void AListDeltaUnderTheBudgetReadsEveryRowOnce()
    {
        var ids = Hundred;
        long head = Delta(Passes, () => ListDelta(ids.Take(2 * Chunk).ToArray()));
        long tail = Delta(Passes, () => ListDelta(ids.Skip(2 * Chunk).ToArray()));
        long before = Passes();
        var r = Metered(Reads(0), () => ListDelta(ids));
        Assert.False(r.StartsWith("error:"), r);
        Assert.Contains("count=100 ", r);
        Assert.Equal(head + tail, Passes() - before);                // the first chunks kept, the rest read once
    }

    /// <summary>Malformed and unresolved ids are settled up front, so the chunks, and the two-chunk rule, are over the rows that read.</summary>
    [Fact]
    public void ADeltaMetersOnlyTheRowsThatRead()
    {
        var junk = Enumerable.Range(0xF00000, 40).Select(i => $"{i:X6}:{_w.MasterName}").Append("not-a-formid");
        var r = Metered(Dear, () => ListDelta(junk.Concat(Hundred).ToArray()));
        Assert.StartsWith("error: this delta " + DearHead, r);
    }

    /// <summary>A named source that does not touch an id answers it from the index: no plugin is walked for it.</summary>
    [Fact]
    public void ANamedSourceThatDoesNotTouchAnIdReadsNothingForIt()
    {
        long before = Passes();
        var r = ListDelta(WeaponIds, source: _w.OverriderNames[0]);
        Assert.Contains("errors=60", r);
        Assert.Equal(0, Passes() - before);
    }

    /// <summary>A census over a scan names its figure as a narrower selection, since limit= does not lower what it reads.</summary>
    [Theory]
    [InlineData("delta")]
    [InlineData("tree")]
    public void AScanPastTheBudgetIsMeteredOnBothForms(string form)
    {
        string Call() => RecordsTools.Records(Svc, types: new[] { "WEAP", "ARMO" }, versus: Json("\"" + _w.MasterName + "\""),
                                              project: new RecordsTools.RecordsProject { form = form }, counts_only: true);
        var r = Metered(Dear, Call);
        Assert.StartsWith($"error: this {form} {DearHead} (narrow the selection to about 45 matches); ", r);
        Assert.Contains(RenderBudget.ComparisonWholeSelectionLever, r);
        Assert.False(Metered(Reads(0), Call).StartsWith("error:"));
    }

    /// <summary>A list whose first chunks are cheap and whose middle rows are dear is caught at the chunk where the
    /// rate so far says so, with nothing read past it.</summary>
    [Fact]
    public void ASkewedListIsRefusedAtTheChunkThatShowsIt()
    {
        var ids = Hundred60;
        long firstThree = Delta(Bodies, () => ListTree(ids.Take(3 * Chunk).ToArray()));
        long before = Bodies();
        var r = Metered(PerBody(2_500), () => ListTree(ids));
        Assert.StartsWith("error: this tree measured 5.83 s a row over its first 96 records, about 15.6 minutes for all 160", r);
        Assert.Equal(firstThree, Bodies() - before);
    }

    [Fact]
    public void AListTreePastTheBudgetRefusesAfterTwoChunks()
    {
        var ids = Hundred;
        long twoChunks = Delta(Bodies, () => ListTree(ids.Take(2 * Chunk).ToArray()));
        long before = Bodies();
        var r = Metered(Dear, () => ListTree(ids));
        Assert.StartsWith("error: this tree " + DearHead, r);
        Assert.Equal(twoChunks, Bodies() - before);
    }

    [Fact]
    public void AListTreeUnderTheBudgetRendersEveryRowOnce()
    {
        long all = Delta(Bodies, () => ListTree(Hundred));
        long before = Bodies();
        var r = Metered(Reads(0), () => RecordsTools.Records(Svc, formids: Hundred, format: "json",
                                                             project: new RecordsTools.RecordsProject { form = "tree" }, max_chars: 4_000_000));
        var rows = System.Text.Json.JsonDocument.Parse(r).RootElement.GetProperty("rows").EnumerateArray()
                         .Select(x => x.GetProperty("formid").GetString()).ToList();
        Assert.Equal(100, rows.Count);
        Assert.Equal(100, rows.Distinct().Count());
        Assert.Equal(all, Bodies() - before);
    }

    /// <summary>A switched-off file overriding every weapon and contested armor.</summary>
    string OffOrderOverrides()
    {
        var key = new ModKey("HcMeterOff", ModType.Plugin);
        var dir = Path.Combine(_w.Root, "inst", "mods", "MeterOffMod");
        var path = Path.Combine(dir, key.FileName.String);
        if (File.Exists(path)) return key.FileName.String;
        var master = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(_w.Root, "inst", "mods", "CostMasterMod", _w.MasterName), SkyrimRelease.SkyrimSE);
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        foreach (var w in master.Weapons)
            mod.Weapons.GetOrAddAsOverride(w).BasicStats!.Damage = 99;
        foreach (var a in master.Armors)
            mod.Armors.GetOrAddAsOverride(a).Value = 7;
        Directory.CreateDirectory(dir);
        mod.BeginWrite.ToPath(path).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();
        return key.FileName.String;
    }

    [Theory]
    [InlineData("delta")]
    [InlineData("tree")]
    public void AnOffOrderComparisonPastTheBudgetIsMetered(string form)
    {
        var file = OffOrderOverrides();
        string Call() => RecordsTools.Records(Svc, types: new[] { "WEAP", "ARMO" }, source: Json("\"" + file + "\""),
                                              versus: Json("\"winner\""), project: new RecordsTools.RecordsProject { form = form },
                                              counts_only: true);
        var r = Metered(Dear, Call);
        Assert.StartsWith($"error: this {form} {DearHead}", r);
        var cheap = Metered(Reads(0), Call);
        Assert.False(cheap.StartsWith("error:"), cheap);
    }

    /// <summary>Ids the off-order subject lacks settle from its sweep, so they are not rows the rate is taken over.</summary>
    [Fact]
    public void IdsTheOffOrderFileLacksAreNotMetered()
    {
        var file = OffOrderOverrides();
        var ammo = Svc.CrossQuery(new[] { "AMMO" }, null, null, false, null, null, 1_000).Keys.Select(k => k.ToString());
        var r = Metered(Dear, () => ListDelta(ammo.Concat(Hundred).ToArray(), source: file));
        Assert.StartsWith("error: this delta " + DearHead, r);
    }

    long Delta(Func<long> counter, Func<string> call)
    {
        long before = counter();
        Assert.False(call().StartsWith("error:"));
        return counter() - before;
    }
}

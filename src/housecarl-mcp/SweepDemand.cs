using HousecarlCore;

namespace HousecarlMcp;

/// <summary>What each subject of a merged <c>check</c> response wants, measured before the render so
/// <see cref="BodyAllocation"/> can water-fill over it. Measured never estimated, and bounded so the pass
/// costs O(budget) rather than O(all rows); the reserves are computed here too because that room is outside
/// allocation entirely. Contract in docs/architecture/render-budget.md.</summary>
internal static class SweepDemand
{
    /// <summary>A subject's measured demand, and what the response will hold back for fixed parts.</summary>
    internal readonly record struct Result(Dictionary<SweepSubject, int> Demand, int Reserved);

    /// <summary>Accumulates one subject's units, stopping the moment the total passes <paramref name="room"/>.</summary>
    sealed class Tally
    {
        readonly Dictionary<SweepSubject, int> _d = new();
        readonly int _room;
        internal Tally(int room) { _room = Math.Max(0, room); }

        internal void Add(SweepSubject s, int width)
        {
            if (!_d.TryGetValue(s, out var had)) had = 0;
            if (had == BodyAllocation.Unconstrained) return;
            long next = (long)had + width;
            _d[s] = next > _room ? BodyAllocation.Unconstrained : (int)next;
        }

        /// <summary>Declare a subject that exists but has measured nothing yet, so a planned subject with no units is
        /// a measured zero rather than a missing key.</summary>
        internal void Declare(SweepSubject s) { if (!_d.ContainsKey(s)) _d[s] = 0; }

        internal bool Done(SweepSubject s) => _d.TryGetValue(s, out var n) && n == BodyAllocation.Unconstrained;
        internal Dictionary<SweepSubject, int> Take() => _d;
    }

    // ---- text ---------------------------------------------------------------------------------------

    internal static Result ForText(CheckOutcome o, int room, int histogramLimit)
    {
        var s = o.Sweep;
        var t = new Tally(room);
        int reserved = 0;
        Roster(t, o, n => Wire.ComposeExcludedRow(o.ExcludedPlugins, n).Length);

        if (s.Errors is { Error: null } e)
        {
            if (e.CountsOnly)
            {
                foreach (var a in Wire.ErrorsAxes(e))
                {
                    reserved += a.TextFixed;
                    Rows(t, a, histogramLimit);
                }
                t.Declare(SweepSubject.UnreadRows);
                foreach (var p in e.Reports)
                {
                    if (t.Done(SweepSubject.UnreadRows)) break;
                    t.Add(SweepSubject.UnreadRows, Wire.ComposeUnreadRow(p).Length);
                }
            }
            else
            {
                t.Declare(SweepSubject.PluginSections);
                t.Declare(SweepSubject.DanglingEntries);
                foreach (var p in e.Reports)
                {
                    if (!t.Done(SweepSubject.PluginSections))
                        t.Add(SweepSubject.PluginSections, Wire.ComposeErrorSection(p).Length);
                    if (t.Done(SweepSubject.DanglingEntries)) continue;
                    foreach (var d in p.Dangling)
                    {
                        if (t.Done(SweepSubject.DanglingEntries)) break;
                        t.Add(SweepSubject.DanglingEntries, Wire.ComposeDanglingLine(d).Length);
                    }
                }
            }
        }

        if (s.Scripts is { Error: null } sc)
        {
            if (sc.CountsOnly)
            {
                foreach (var a in Wire.ScriptsAxes(sc))
                {
                    reserved += a.TextFixed;
                    Rows(t, a, histogramLimit);
                }
                t.Declare(SweepSubject.ScriptScanRows);
                foreach (var rec in sc.Reports)
                {
                    if (rec.ScanError is null) continue;
                    if (t.Done(SweepSubject.ScriptScanRows)) break;
                    t.Add(SweepSubject.ScriptScanRows, Wire.ComposeScriptRecordUnit(rec).Length);
                }
            }
            else
            {
                t.Declare(SweepSubject.ScriptRecords);
                foreach (var rec in sc.Reports)
                {
                    if (t.Done(SweepSubject.ScriptRecords)) break;
                    t.Add(SweepSubject.ScriptRecords, Wire.ComposeScriptRecordUnit(rec).Length);
                }
            }
        }

        if (s.FaceGen is { Error: null } fg)
        {
            if (fg.CountsOnly)
                foreach (var a in FaceGenSweepRender.Axes(fg))
                {
                    reserved += a.TextFixed;
                    Rows(t, a, histogramLimit);
                }
            else
            {
                t.Declare(SweepSubject.FaceGenRows);
                foreach (var row in fg.Findings)
                {
                    if (t.Done(SweepSubject.FaceGenRows)) break;
                    t.Add(SweepSubject.FaceGenRows, FaceGenSweepRender.ComposeRow(row).Length);
                }
            }
        }

        if (s.Dialogue is { Error: null } d2)
        {
            if (!d2.CountsOnly)
            {
                t.Declare(SweepSubject.DialogueSeeds);
                t.Declare(SweepSubject.DialogueTopics);
                foreach (var seed in d2.Resolved)
                {
                    if (!t.Done(SweepSubject.DialogueSeeds))
                        t.Add(SweepSubject.DialogueSeeds, DialogueSweepRender.ComposeSeedUnit(seed).Length);
                    if (t.Done(SweepSubject.DialogueTopics)) continue;
                    foreach (var topic in seed.Report!.Topics)
                    {
                        if (t.Done(SweepSubject.DialogueTopics)) break;
                        t.Add(SweepSubject.DialogueTopics, DialogueSweepRender.ComposeTopicBlock(topic).Length);
                    }
                }
            }
            t.Declare(SweepSubject.DialogueSeedRefusals);
            foreach (var seed in d2.Unresolved)
            {
                if (t.Done(SweepSubject.DialogueSeedRefusals)) break;
                t.Add(SweepSubject.DialogueSeedRefusals, DialogueSweepRender.ComposeRefusalRow(seed).Length);
            }
        }

        return new Result(t.Take(), reserved);
    }

    /// <summary>One axis's rows, in the row order the render will use — the first row carries the axis head.</summary>
    static void Rows(Tally t, HistogramAxis a, int rowLimit)
    {
        t.Declare(a.Subject);
        if (a.Rows is not { } rows) return;
        for (int i = 0; i < rows.Count && i < rowLimit; i++)
        {
            if (t.Done(a.Subject)) break;
            t.Add(a.Subject, Wire.ComposeHistogramRow(a, rows[i], i == 0).Length);
        }
    }

    /// <summary>The excluded-plugin roster's demand, measured through the same composer the render writes. A demand
    /// and not a reserve: the roster is a response-level participant in the allocation.</summary>
    static void Roster(Tally t, CheckOutcome o, Func<int, int> costOf)
    {
        if (o.ExcludedPlugins.Count == 0) return;
        t.Declare(SweepSubject.ExcludedRows);
        for (int i = 0; i < o.ExcludedPlugins.Count; i++)
        {
            if (t.Done(SweepSubject.ExcludedRows)) break;
            t.Add(SweepSubject.ExcludedRows, costOf(i));
        }
    }

    // ---- json ---------------------------------------------------------------------------------------

    /// <summary>The same question in the other transport, measured by the same cost helpers the render's
    /// <c>Emit</c> calls declare, at the same depth and sibling position.</summary>
    /// <param name="depths">where each unit sits in the document; the render reads its anchor off the live writer,
    /// and this pass must be handed the same anchor.</param>
    internal static Result ForJson(CheckOutcome o, int room, int histogramLimit, JsonWire.JsonUnitDepths depths)
    {
        var s = o.Sweep;
        var t = new Tally(room);
        int reserved = 0;
        Roster(t, o, n => JsonWire.ExcludedRowCostFor(o.ExcludedPlugins, n));

        if (s.Errors is { Error: null } e)
        {
            if (e.CountsOnly)
            {
                // Gated the way the render gates it: a frame only where `a.Rows is not null`.
                foreach (var a in Wire.ErrorsAxes(e))
                {
                    if (a.Rows is not null) reserved += JsonWire.HistogramFrameCostFor(a, depths.AxisFrame);
                    JsonRows(t, a, histogramLimit, depths);
                }
                t.Declare(SweepSubject.UnreadRows);
                int unread = 0;
                foreach (var p in e.Reports)
                {
                    if (t.Done(SweepSubject.UnreadRows)) break;
                    t.Add(SweepSubject.UnreadRows,
                          JsonWire.UnreadRowCostFor(p, depths.HistogramRows, unread > 0));
                    unread++;
                }
            }
            else
            {
                t.Declare(SweepSubject.PluginSections);
                t.Declare(SweepSubject.DanglingEntries);
                int sections = 0;
                foreach (var p in e.Reports)
                {
                    if (!t.Done(SweepSubject.PluginSections))
                    {
                        t.Add(SweepSubject.PluginSections,
                              JsonWire.PluginHeadCostFor(p, depths.PluginSections, sections > 0));
                        sections++;
                    }
                    if (t.Done(SweepSubject.DanglingEntries)) continue;
                    int entries = 0;
                    foreach (var d in p.Dangling)
                    {
                        if (t.Done(SweepSubject.DanglingEntries)) break;
                        t.Add(SweepSubject.DanglingEntries,
                              JsonWire.DanglingEntryCostFor(d, depths.DanglingEntries, entries > 0));
                        entries++;
                    }
                }
            }
        }

        if (s.Scripts is { Error: null } sc)
        {
            if (sc.CountsOnly)
            {
                foreach (var a in Wire.ScriptsAxes(sc))
                {
                    if (a.Rows is not null) reserved += JsonWire.HistogramFrameCostFor(a, depths.AxisFrame);
                    JsonRows(t, a, histogramLimit, depths);
                }
                t.Declare(SweepSubject.ScriptScanRows);
                int rows = 0;
                foreach (var rec in sc.Reports)
                {
                    if (rec.ScanError is null) continue;
                    if (t.Done(SweepSubject.ScriptScanRows)) break;
                    // HistogramRows, not ScriptRecords: the wrapped honesty layer lands two levels down.
                    t.Add(SweepSubject.ScriptScanRows,
                          JsonWire.ScanErrorRowCostFor(rec, depths.HistogramRows, rows > 0));
                    rows++;
                }
            }
            else
            {
                t.Declare(SweepSubject.ScriptRecords);
                int records = 0;
                foreach (var rec in sc.Reports)
                {
                    if (t.Done(SweepSubject.ScriptRecords)) break;
                    t.Add(SweepSubject.ScriptRecords,
                          JsonWire.ScriptRecordCostFor(rec, depths.ScriptRecords, records > 0));
                    records++;
                }
            }
        }

        if (s.FaceGen is { Error: null } fg)
        {
            if (fg.CountsOnly)
                foreach (var a in FaceGenSweepRender.Axes(fg))
                {
                    if (a.Rows is not null) reserved += JsonWire.HistogramFrameCostFor(a, depths.AxisFrame);
                    JsonRows(t, a, histogramLimit, depths);
                }
            else
            {
                t.Declare(SweepSubject.FaceGenRows);
                int rows = 0;
                foreach (var row in fg.Findings)
                {
                    if (t.Done(SweepSubject.FaceGenRows)) break;
                    t.Add(SweepSubject.FaceGenRows,
                          FaceGenSweepRender.RowCostFor(row, depths.FaceGenRows, rows > 0));
                    rows++;
                }
            }
        }

        if (s.Dialogue is { Error: null } d2)
        {
            if (!d2.CountsOnly)
            {
                t.Declare(SweepSubject.DialogueSeeds);
                t.Declare(SweepSubject.DialogueTopics);
                int seeds = 0;
                foreach (var seed in d2.Resolved)
                {
                    if (!t.Done(SweepSubject.DialogueSeeds))
                    {
                        t.Add(SweepSubject.DialogueSeeds,
                              DialogueSweepRender.SeedHeadCostFor(seed, depths.DialogueSeeds, seeds > 0));
                        seeds++;
                    }
                    if (t.Done(SweepSubject.DialogueTopics)) continue;
                    int topics = 0;
                    foreach (var topic in seed.Report!.Topics)
                    {
                        if (t.Done(SweepSubject.DialogueTopics)) break;
                        t.Add(SweepSubject.DialogueTopics,
                              DialogueSweepRender.TopicRowCostFor(topic, depths.DialogueTopics, topics > 0));
                        topics++;
                    }
                }
            }
            t.Declare(SweepSubject.DialogueSeedRefusals);
            int refusals = 0;
            foreach (var seed in d2.Unresolved)
            {
                if (t.Done(SweepSubject.DialogueSeedRefusals)) break;
                t.Add(SweepSubject.DialogueSeedRefusals,
                      DialogueSweepRender.UnreachableRowCostFor(seed, depths.DialogueSeeds, refusals > 0));
                refusals++;
            }
        }

        return new Result(t.Take(), reserved);
    }

    static void JsonRows(Tally t, HistogramAxis a, int rowLimit, JsonWire.JsonUnitDepths depths)
    {
        t.Declare(a.Subject);
        if (a.Rows is not { } rows) return;
        for (int i = 0; i < rows.Count && i < rowLimit; i++)
        {
            if (t.Done(a.Subject)) break;
            t.Add(a.Subject, JsonWire.HistogramRowCostFor(rows[i], depths.HistogramRows, i > 0));
        }
    }
}

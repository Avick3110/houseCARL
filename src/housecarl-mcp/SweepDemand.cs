using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// WHAT EACH SUBJECT OF A MERGED <c>check</c> RESPONSE WANTS, measured before the render so
/// <see cref="BodyAllocation"/> can water-fill over it (#394, advisor ruling 2026-08-21, revised by the phase-3
/// escalation).
///
/// <para><b>Why demand has to exist at all.</b> Max-min fairness gives every child <c>min(its demand, λ)</c>. Both
/// halves of that need the demand: a child wanting less than an equal share must take only what it wants, or the
/// rest is stranded (measured: a merged response stopping at 49,440 of an 80,000 cap, cutting 5 of 40 record
/// sections, with 30,560 characters unspent). The alternative — discovering a child was short at render time and
/// handing the leftover to whoever came next — is what made the old rule order-dependent and non-monotone.</para>
///
/// <para><b>MEASURED, never estimated.</b> A demand is the cumulative width of a subject's ACTUAL units, composed
/// by the SAME helper that will write them, in the transport's own unit. Nothing here multiplies a row count by a
/// mean width. The composers are shared deliberately (<c>Wire.ComposeErrorSection</c>,
/// <c>DialogueSweepRender.ComposeTopicBlock</c>, …) so that "what was measured" and "what was written" cannot be
/// two different strings — and <c>ALLOCATION-EQUALS-SPEND</c> is the arm that holds it.</para>
///
/// <para><b>BOUNDED, so the pass costs O(budget) rather than O(all rows).</b> A subject whose units exceed the
/// room its parent could possibly give it is <see cref="BodyAllocation.Unconstrained"/>, and measuring stops
/// there. That is not an approximation: an unconstrained subject is one that will be cut whatever λ turns out to
/// be, and <c>min(demand, λ)</c> needs nothing more precise about it. It matters — the scripts family's live-order
/// sweep carries 180,028 findings across 10,944 records, and composing all of them to learn "more than 80,000"
/// would be work thrown away.</para>
///
/// <para><b>The RESERVES are computed here too.</b> The histogram axes hold back their own closing disclosure, and
/// that room is outside allocation entirely. It used to be reserved DURING the render, one family at a time, so an
/// allocation built at the first write divided room that families rendering later had not yet claimed. Measuring
/// it here is what lets the allocation be built before anything is written.</para>
/// </summary>
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

        /// <summary>Declare a subject that exists but has measured nothing yet, so a planned subject with no units
        /// is a MEASURED zero rather than a missing key (which the allocation reads as unconstrained).</summary>
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

    /// <summary>One axis's rows, in the row order the render will use — the FIRST row carries the axis head, so it
    /// is measured as the first row rather than as "a row".</summary>
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

    /// <summary>THE EXCLUDED-PLUGIN ROSTER'S DEMAND, measured row by row through the same composer the render
    /// writes. It is a demand and not a reserve: the roster is a RESPONSE-level participant in the allocation
    /// (<c>CheckOutcome.ResponseSubjects</c>), so what it wants is measured here exactly as a family's subjects are,
    /// and the fill gives it <c>min(demand, lambda)</c>.
    ///
    /// <para>Reserved instead, its room was subtracted from the row budget and then spent against the GLOBAL test,
    /// which no plan governs — so the roster took the whole body budget before the first family head was written
    /// and the fixed part landed past the cap (measured: 4,494 chars against a 4,000 cap, with a printed remedy
    /// that never converged).</para></summary>
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

    /// <summary>The same question in the other transport. Its units are measured by the SAME cost helpers the
    /// render's <c>Emit</c> calls declare, at the SAME depth and sibling position, so demand and the emission test
    /// read one number.</summary>
    /// <param name="depths">where each unit sits in the document (<see cref="JsonWire.JsonUnitDepths"/>). The render
    /// reads its anchor off the live writer; this pass is handed the same anchor, and
    /// <c>ALLOCATION-EQUALS-SPEND</c> is what catches the two disagreeing.</param>
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
                // GATED THE WAY THE RENDER GATES IT. `JsonWire.WriteHistograms` reserves a frame only where
                // `a.Rows is not null`, and `WriteHistogram` returns without writing for a null-rows axis, so a
                // frame cost added here unconditionally holds back room for an object the response never opens
                // (measured: ~180 chars on `counts_only=true findings=['missing_masters']`, where both errors axes
                // are null). The text lane never had this — `a.TextFixed` is 0 for a null-rows axis, so its
                // unconditional add and its unconditional Reserve agree by construction. The property that says
                // the two lanes now agree is RESERVE-DECLARED-IS-RESERVE-DEMANDED.
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
                    // HistogramRows, not ScriptRecords: the counts_only honesty layer is WRAPPED
                    // ({total, rows, rendered, truncated}), so its rows land two levels under the family object
                    // exactly as `unread.rows` does — the demand and the write read the one depth table.
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

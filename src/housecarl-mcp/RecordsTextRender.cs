using System.Text;

namespace HousecarlMcp;

// The records text renders: the delta, tree, chain, effect-chain, info_order, summary and list-aggregate forms, and the budget helpers they share.

static partial class RecordsTools
{
    /// <summary>The delta form's text render: header counts, then per record the two pole lines, the stack-above fact stated neutrally, and the delta-line grammar, where a truncated deep read is never 'identical'; max_chars is a CEILING, per docs/architecture/render-budget.md.</summary>
    /// <remarks>Internal so a test can drive a row shape no fixture produces — an incomplete deep read with enough delta lines to be cut.</remarks>
    internal static string RenderRecordsDelta(IReadOnlyList<LoadOrderService.DeltaRow> rows, int total, int differing, int identical,
                                     int noVerdict, int errors,
                                     string headerLine, OrderStamp? epoch, int maxChars, SpillState? spill, out bool truncated,
                                     bool unreserved = false)
    {
        truncated = false;
        int cap = maxChars > 0 ? maxChars : Wire.DefaultMaxChars;
        if (!unreserved)
        {
            var whole = RenderRecordsDelta(rows, total, differing, identical, noVerdict, errors, headerLine, epoch,
                                           maxChars, spill, out _, unreserved: true);
            if (whole.Length <= cap) return whole;
        }
        bool manifestOnly = spill?.ManifestOnly ?? false;
        var sb = new StringBuilder();
        sb.Append(headerLine).Append('\n');
        sb.Append(total).Append(" record(s): ").Append(differing).Append(" differing, ").Append(identical)
          .Append(" identical, ");
        // Named only when there are any, since such a record is in neither of the two counts above.
        if (noVerdict > 0) sb.Append(noVerdict).Append(" with a field that could not be read, ");
        sb.Append(errors).Append(" error(s)");
        if (epoch is not null) sb.Append(Wire.EpochInline(epoch));
        sb.Append('\n');
        int rendered = 0;
        string Notice(int r) =>
            "... [rendered " + r + " of " + rows.Count + " rows at max_chars=" + cap + "]\n";
        var spillText = Wire.SpillText(spill);
        int budget = unreserved ? Unbounded : Math.Max(cap - spillText.Length - Notice(rows.Count).Length, 0);
        string deltaCut = CutNotice("delta lines", cap);
        foreach (var row in rows)
        {
            if (manifestOnly) break;
            int mark = sb.Length;
            // said: the delta list stopped inside the budget and named what it held back, so the record stays and
            // the render stops after it. mute: no room to say so, and the whole record goes back out.
            bool said = false, mute = false;
            sb.Append('\n').Append(row.Formid);
            if (row.Error is not null)
            {
                sb.Append("  error=").Append(row.Error).Append('\n');
                if (row.StackAbove is { Count: > 0 })
                    sb.Append("  stack above the subject (closer to winning, winner last): ").Append(string.Join(", ", row.StackAbove)).Append('\n');
                if (Crossed(sb, mark, budget, Notice(rendered), ref truncated)) break;
                rendered++;
                continue;
            }
            var s = row.Subject!; var r = row.Reference!; var d = row.Diff!;
            sb.Append("  ").Append(s.RecordType ?? "?").Append("  ").Append(s.EditorId ?? "<no editorid>").Append('\n');
            sb.Append("  subject:   ").Append(s.Plugin).Append(" [").Append(s.Where).Append("]\n");
            sb.Append("  reference: ").Append(r.Plugin).Append(" [").Append(r.Where).Append("]\n");
            if (row.StackAbove is { Count: > 0 })
                sb.Append("  stack above the subject (closer to winning, winner last): ").Append(string.Join(", ", row.StackAbove)).Append('\n');
            if (row.Note is not null) sb.Append("  note: ").Append(row.Note).Append('\n');
            if (d.Deltas.Count == 0)
            {
                if (!d.Complete)
                    sb.Append("  no differing fields in what was read, but the deep read was TRUNCATED at the cap — NOT a clean 'identical' (Q3). Narrow with ").Append(LeverNames.Records.Fields).Append(" to compare in full.\n");
                else if (d.AgreedCount > 0)
                    sb.Append("  identical across the fields read (").Append(d.AgreedCount).Append(" value leaf/leaves agree).\n");
                else
                    sb.Append("  identical across the fields read (no differing fields).\n");
            }
            else
            {
                // The block heads with the VALUE differences and names the no-verdict lines apart from them.
                int values = d.Deltas.Count - d.NoVerdictCount;
                sb.Append("  ");
                if (values > 0) sb.Append(values).Append(values == 1 ? " difference" : " differences");
                if (d.NoVerdictCount > 0)
                    sb.Append(values > 0 ? " and " : "").Append(d.NoVerdictCount)
                      .Append(d.NoVerdictCount == 1 ? " field that could not be read" : " fields that could not be read");
                sb.Append(" — each value line: ").Append(s.LabelVersus(r.Plugin)).Append("'s value (reference = ")
                  .Append(r.LabelVersus(s.Plugin)).Append("):\n");
                // INCOMPLETE says deltas were never COMPUTED, where the cut notice says computed lines did not fit,
                // so the note is reserved beside every line and written either way.
                string incomplete = d.Complete ? ""
                    : "  note: the comparison is INCOMPLETE — a field above could not be read (nothing at or under it was compared), or the deep read hit the cap (which suppresses list-content and one-sided-presence deltas for the whole record). Narrow with " + LeverNames.Records.Fields + " to compare those in full.\n";
                foreach (var delta in d.Deltas)
                {
                    // The line goes in only where its own cut notice still fits beside it.
                    string line = "    - " + delta + "\n";
                    if (sb.Length + line.Length + deltaCut.Length + incomplete.Length > budget)
                    {
                        said = Said(sb, deltaCut, budget - incomplete.Length);
                        mute = !said;
                        break;
                    }
                    sb.Append(line);
                }
                if (!mute) sb.Append(incomplete);
            }
            if (mute || !said)
            {
                if (Crossed(sb, mark, budget, Notice(rendered), ref truncated, force: mute)) break;
                rendered++;
                continue;
            }
            // The record was laid to the budget and says what it held back: it stays, and nothing more fits.
            rendered++;
            Stopped(sb, Notice(rendered), rendered, rows.Count, ref truncated);
            break;
        }
        sb.Append(spillText);
        return RenderCap.Settle(sb.ToString().TrimEnd('\n'), cap);
    }

    /// <summary>The budget of the unreserved pass every bounded render here makes first: no unit can cross it, so that pass lays the COMPLETE render, and the reserves are charged only once the whole thing is known not to fit at this cap.</summary>
    const int Unbounded = int.MaxValue / 2;

    /// <summary>Whole units only: a unit written from <paramref name="mark"/> that crossed <paramref name="budget"/>, or stopped early with no room to say so (<paramref name="force"/>), is taken back out entire and the caller's notice put in its place; true means the render stops here.</summary>
    static bool Crossed(StringBuilder sb, int mark, int budget, string notice, ref bool truncated, bool force = false)
    {
        if (!force && sb.Length <= budget) return false;
        sb.Length = mark;
        sb.Append(notice);
        truncated = true;
        return true;
    }

    /// <summary>The end a unit that said what it held back gets: it stays, the render stops after it, and the units it never reached are counted in <paramref name="notice"/>, which a last unit leaves unwritten.</summary>
    static void Stopped(StringBuilder sb, string notice, int rendered, int total, ref bool truncated)
    {
        truncated = true;
        if (rendered < total) sb.Append(notice);
    }

    /// <summary>A section that ran out of room says so INSIDE the budget or not at all: false means the notice did not fit, so the row is taken back out whole.</summary>
    static bool Said(StringBuilder sb, string notice, int budget)
    {
        if (sb.Length + notice.Length > budget) return false;
        sb.Append(notice);
        return true;
    }

    /// <summary>The tree form's text render: per record the touching list in load order with the winner last, then each provider's delta against the reference, under the delta form's own wording rules and the same max_chars ceiling.</summary>
    /// <remarks>Internal so a test can drive a node shape no fixture produces — an incomplete comparison on a record only one in-order plugin touches.</remarks>
    internal static string RenderRecordsTree(IReadOnlyList<LoadOrderService.TreeRow> rows, int total, int contested, int errors,
                                    bool fieldsNarrow, string headerLine, OrderStamp? epoch, int maxChars,
                                    SpillState? spill, out bool truncated, bool unreserved = false)
    {
        truncated = false;
        int cap = maxChars > 0 ? maxChars : Wire.DefaultMaxChars;
        if (!unreserved)
        {
            var whole = RenderRecordsTree(rows, total, contested, errors, fieldsNarrow, headerLine, epoch, maxChars,
                                          spill, out _, unreserved: true);
            if (whole.Length <= cap) return whole;
        }
        bool manifestOnly = spill?.ManifestOnly ?? false;
        var sb = new StringBuilder();
        sb.Append(headerLine).Append('\n');
        sb.Append(total).Append(" record(s): ").Append(contested).Append(" contested, ").Append(errors).Append(" error(s)");
        if (epoch is not null) sb.Append(Wire.EpochInline(epoch));
        sb.Append('\n');
        int rendered = 0;
        bool declarersLeadWritten = false;
        string Notice(int r) =>
            "... [rendered " + r + " of " + rows.Count + " rows at max_chars=" + cap + "]\n";
        var spillText = Wire.SpillText(spill);
        var room = unreserved ? new RenderCap(cap, Unbounded)
                              : RenderCap.For(cap, spillText.Length + Notice(rows.Count).Length);
        int budget = room.Budget;
        string nodesCut = CutNotice("nodes", cap);
        foreach (var row in rows)
        {
            if (manifestOnly) break;
            int mark = sb.Length;
            bool leadMark = declarersLeadWritten;
            // said: this row stopped inside the budget and named what it held back, so it stays and the render
            // stops after it. mute: no room to say so, and the whole row goes back out.
            bool said = false, mute = false;
            sb.Append('\n').Append(row.Formid);
            if (row.Error is not null)
            {
                sb.Append("  error=").Append(row.Error).Append('\n');
                if (Crossed(sb, mark, budget, Notice(rendered), ref truncated)) break;
                rendered++; continue;
            }
            sb.Append("  ").Append(row.Type ?? "?").Append("  ").Append(row.EditorId ?? "<no editorid>").Append('\n');
            sb.Append("  ").Append(row.Touchers.Count).Append(" plugin(s) touch this record (load order, winner last):\n");
            for (int i = 0; i < row.Touchers.Count; i++)
                sb.Append("    ").Append(i + 1).Append(". ").Append(row.Touchers[i])
                  .Append(i == row.Touchers.Count - 1 ? "  (winner)" : "").Append('\n');
            // The row ends at the block when the block was cut or ran the budget out, and a sole provider ends
            // there too, having nothing to diff against.
            bool ended = AppendChildDeclarers(sb, row, room, row.Nodes.Count > 1 ? nodesCut.Length : 0,
                                              ref declarersLeadWritten, out bool declarersCut, out bool declarersMute);
            if (ended)
            {
                mute = declarersMute;
                // The row lost something when declarer lines were dropped; a sole-provider row whose complete
                // block merely ended at the budget lost nothing.
                said = declarersCut;
                // A multi-provider row loses its diff either way, and each notice claims one thing, so a cut row
                // carries both.
                if (!mute && row.Nodes.Count > 1)
                {
                    if (Said(sb, nodesCut, budget)) said = true;
                    else mute = true;
                }
            }
            else if (row.Nodes.Count > 1)
            {
                // The diff heading carries its own cut notice's room, or it would end the row in silence.
                string diffHead = "  diff (field deltas vs " + row.ReferencePlugin +
                                  "; identical fields omitted; list contents compared by content, element reorders flagged):\n";
                if (sb.Length + diffHead.Length + nodesCut.Length > budget)
                {
                    said = Said(sb, nodesCut, budget);
                    mute = !said;
                    goto measure;
                }
                sb.Append(diffHead);
                foreach (var n in row.Nodes)
                {
                    if (n.IsReference) continue;
                    // The incompleteness note goes on EVERY incomplete node, not only the one with no deltas.
                    string body = n.Deltas.Count > 0
                        ? string.Join("; ", n.Deltas) +
                          (n.Complete ? "" : " — the comparison is INCOMPLETE: a field could not be read (nothing at or under it was compared), or the deep read hit the cap (which suppresses list-content and one-sided-presence deltas for the whole record)") + "\n"
                        : !n.Complete
                            ? "no differing fields in what was read, but the comparison is INCOMPLETE — the deep read was TRUNCATED at the cap, so this is not a clean 'identical'.\n"
                            : fieldsNarrow
                                ? $"identical to {row.ReferencePlugin} across the fields read ({n.AgreedCount} leaf/leaves agree)\n"
                                : $"identical to {row.ReferencePlugin} (whole record; {n.AgreedCount} leaf/leaves agree)\n";
                    // Composed before it is priced, so the notice cannot land past the node that crossed.
                    string line = "    " + n.Plugin + (n.IsWinner ? " (winner)" : "") + ": " + body;
                    if (sb.Length + line.Length + nodesCut.Length > budget)
                    {
                        said = Said(sb, nodesCut, budget);
                        mute = !said;
                        break;
                    }
                    sb.Append(line);
                }
            }
        measure:
            if (mute || !said)
            {
                if (Crossed(sb, mark, budget, Notice(rendered), ref truncated, force: mute))
                { declarersLeadWritten = leadMark; break; }
                rendered++;
                continue;
            }
            // The row was laid to the budget and says what it held back: it stays, and nothing more fits.
            rendered++;
            Stopped(sb, Notice(rendered), rendered, rows.Count, ref truncated);
            break;
        }
        sb.Append(spillText);
        return RenderCap.Settle(sb.ToString().TrimEnd('\n'), cap);
    }

    /// <summary>The records text lane's cut notice, composed in one place, and returned rather than written so its room can be held back before the line it follows is laid.</summary>
    /// <param name="what">What was cut — the notice claims this and nothing else.</param>
    static string CutNotice(string what, int cap) =>
        "    ... [" + what + " cut at max_chars=" + cap + " — raise max_chars or narrow with " +
        LeverNames.Records.Fields + "]\n";

    /// <summary>The tree's precise owned-child block: which providers declare children per child-bearing field, and the negative sentence when none do; returns true when the row ends here, and the contract is in docs/architecture/records-owned-child-declarers.md.</summary>
    /// <param name="leadWritten">Set once the framing line has been stated; every later row gets the short <see cref="ReadSentences.DeclarersHeader"/>.</param>
    /// <param name="blockCut">true only when declarer lines were dropped AND the block said so; false with a true return means the block is complete and the row ends at <paramref name="cap"/>.</param>
    /// <param name="tailReserve">Room the CALLER still owes below this block, held back here so its notice lands inside the budget too.</param>
    /// <param name="mute">true when the block stopped with no room to say it was cut, so the caller takes the whole row back out.</param>
    internal static bool AppendChildDeclarers(StringBuilder sb, LoadOrderService.TreeRow row, RenderCap cap,
                                              int tailReserve, ref bool leadWritten, out bool blockCut, out bool mute)
    {
        blockCut = false;
        mute = false;
        if (row.ChildDeclarers.Count == 0) return false;
        // The framing line has a known length, so it is reserved rather than written and regretted, as
        // JsonWire.RenderTree reserves the same sentence; the cut notice is reserved beside it.
        string framing = leadWritten ? ReadSentences.DeclarersHeader : ReadSentences.DeclarersLead;
        string cut = CutNotice("child declarers", cap.Cap);
        int budget = Math.Max(cap.Budget - tailReserve, 0);
        // 3: the two-space indent and the newline around it.
        if (sb.Length + framing.Length + 3 + cut.Length >= budget)
        {
            blockCut = Said(sb, cut, budget);
            mute = !blockCut;
            return true;
        }
        sb.Append("  ").Append(framing).Append('\n');
        leadWritten = true;
        foreach (var cd in row.ChildDeclarers)
        {
            // The line is composed before it is priced. DeclarersNote elides past DeclarerNameCap in two clauses,
            // both followable only in json, and one remedy covers a line where both fired.
            bool overflowed = (cd.Shape == OwnedChildShape.Collection && cd.Declaring.Count > ReadSentences.DeclarerNameCap)
                              || cd.Unreadable.Count > ReadSentences.DeclarerNameCap;
            string line = "    " + cd.Field + ": " + ReadSentences.DeclarersNote(cd.Shape, cd.Declaring, cd.Unreadable)
                          + (overflowed ? ReadSentences.DeclarersOverflowRemedy : "") + "\n";
            if (sb.Length + line.Length + cut.Length > budget)
            {
                blockCut = Said(sb, cut, budget);
                mute = !blockCut;
                return true;
            }
            sb.Append(line);
        }
        // Every declarer line was written and each was priced with the notice beside it, so the block is complete
        // and its notice's room is still there.
        return false;
    }

    /// <summary>The chain form's text render: per seed the reached nodes in BFS order with what pulled each one in, the recorded cycles, the cap-truncation note and the NPC TemplateFlags inheritance report, under the same max_chars ceiling the delta and tree renders carry.</summary>
    /// <remarks>Internal so a test can drive a seed shape no fixture produces — a walk that hit its node cap with nodes enough for max_chars to cut.</remarks>
    internal static string RenderRecordsChain(IReadOnlyList<LoadOrderService.WalkSeedResult> rows, int total, int reached,
                                     int errors, string headerLine, OrderStamp? epoch, int maxChars,
                                     SpillState? spill, out bool truncated, bool unreserved = false)
    {
        truncated = false;
        int cap = maxChars > 0 ? maxChars : Wire.DefaultMaxChars;
        if (!unreserved)
        {
            var whole = RenderRecordsChain(rows, total, reached, errors, headerLine, epoch, maxChars, spill, out _,
                                           unreserved: true);
            if (whole.Length <= cap) return whole;
        }
        bool manifestOnly = spill?.ManifestOnly ?? false;
        var sb = new StringBuilder();
        sb.Append(headerLine).Append('\n');
        sb.Append(total).Append(" seed(s), ").Append(reached).Append(" node(s) reached, ").Append(errors).Append(" error(s)");
        if (epoch is not null) sb.Append(Wire.EpochInline(epoch));
        sb.Append('\n');
        int rendered = 0;
        string Notice(int r) =>
            "... [rendered " + r + " of " + rows.Count + " seeds at max_chars=" + cap + "]\n";
        string nodesCut = "    ... [nodes cut at max_chars=" + cap + " — raise max_chars, or to_file= for the complete walk]\n";
        var spillText = Wire.SpillText(spill);
        int budget = unreserved ? Unbounded : Math.Max(cap - spillText.Length - Notice(rows.Count).Length, 0);
        foreach (var row in rows)
        {
            if (manifestOnly) break;
            int mark = sb.Length;
            // said: this seed's node list stopped inside the budget and named what it held back, so the seed stays
            // and the render stops after it. mute: no room to say so, and the seed goes back out.
            bool said = false, mute = false;
            sb.Append('\n').Append(row.Seed);
            if (row.Error is not null)
            {
                sb.Append("  error=").Append(row.Error).Append('\n');
                if (Crossed(sb, mark, budget, Notice(rendered), ref truncated)) break;
                rendered++; continue;
            }
            sb.Append("  ").Append(row.Type ?? "?").Append("  ").Append(row.EditorId ?? "<no editorid>").Append('\n');
            if (row.Nodes.Count == 0)
                sb.Append("  no links to follow from this seed").Append(row.TruncationNote is null ? ".\n" : " before the cap.\n");
            // What the seed says about its WALK is a different loss from the nodes max_chars held back, so the tail
            // is reserved beside every node line and written whether or not the list was cut; the cycle list, which
            // only the walked fanout bounds, is held to its own half of the budget.
            string tail = SeedTail(row, budget >= Unbounded ? Unbounded : Math.Max(budget / 2, 0), cap);
            foreach (var n in row.Nodes)
            {
                // Composed before it is priced, so the notice lands inside the budget, not past the node that crossed.
                string line = "    d" + n.Depth + "  " + n.Key
                              + (n.Type is not null ? "  " + n.Type + "  " + (n.EditorId ?? "<no editorid>") : "")
                              + "  [" + n.Status + ']'
                              + (n.Note is not null ? "  " + n.Note : "")
                              + "  <- " + n.PulledBy + "\n";
                if (sb.Length + line.Length + nodesCut.Length + tail.Length > budget)
                {
                    said = Said(sb, nodesCut, budget - tail.Length);
                    mute = !said;
                    break;
                }
                sb.Append(line);
            }
            if (!mute) sb.Append(tail);
            if (mute || !said)
            {
                if (Crossed(sb, mark, budget, Notice(rendered), ref truncated, force: mute)) break;
                rendered++;
                continue;
            }
            // The seed was laid to the budget and says what it held back: it stays, and nothing more fits.
            rendered++;
            Stopped(sb, Notice(rendered), rendered, rows.Count, ref truncated);
            break;
        }
        sb.Append(spillText);
        return RenderCap.Settle(sb.ToString().TrimEnd('\n'), cap);
    }

    /// <summary>What a walked seed states after its nodes — the cycles it found, the walk.max_nodes cap it hit, and the NPC TemplateFlags inheritance report — composed apart from the node loop because these are claims about the WALK that a max_chars cut may not swallow.</summary>
    /// <param name="cycleRoom">The room the unbounded cycle list is held to; past it a line says how many were held back and how to get them, while the cap note and the template report are always written.</param>
    static string SeedTail(LoadOrderService.WalkSeedResult row, int cycleRoom, int cap)
    {
        var t = new StringBuilder();
        for (int i = 0; i < row.Cycles.Count; i++)
        {
            var line = "  cycle: " + row.Cycles[i] + "\n";
            var held = "  ... [" + (row.Cycles.Count - i) + " more cycle(s) held back at max_chars=" + cap
                       + " — raise max_chars, or to_file= for the complete walk]\n";
            if (t.Length + line.Length + held.Length > cycleRoom) { t.Append(held); break; }
            t.Append(line);
        }
        if (row.CyclesCapped)
            t.Append("  ... [the cycle search stopped at its ")
             .Append(LoadOrderService.WalkCycleCap)
             .Append("-cycle cap — this seed holds more loops than are listed or counted]\n");
        if (row.TruncationNote is not null)
            t.Append("  [!] ").Append(row.TruncationNote).Append('\n');
        if (row.TemplateReport is { } tr)
        {
            t.Append("  template inheritance (TemplateFlags — a SET flag means the category is INHERITED and the seed's own local data for it is MASKED):\n");
            foreach (var c in tr)
            {
                t.Append("    ").Append(c.Category).Append(": ");
                if (!c.InheritedAtSeed) t.Append("local data ACTIVE");
                else if (c.ProviderKey is not null)
                    t.Append("INHERITED from ").Append(c.ProviderKey).Append(" (").Append(c.ProviderEditorId ?? "<no editorid>").Append(')');
                else t.Append("INHERITED");
                if (c.Note is not null && c.InheritedAtSeed) t.Append("  — ").Append(c.Note);
                t.Append('\n');
            }
        }
        return t.ToString();
    }

    /// <summary>The reverse MGEF lane's text render: a header census over the complete seed list, then each windowed seed's carriers through the shared effect-chain render, which is told what this one has spent and reports back a cut nothing here can measure.</summary>
    /// <remarks>Internal so a test can drive a seed whose carriers are wider than the auto-spill block, which no fixture has.</remarks>
    internal static string RenderRecordsEffectChains(IReadOnlyList<(string Seed, EffectChainResult Result)> results,
                                            int totalSeeds, int carrierRows, int carrierTotal, int errors, string headerLine,
                                            OrderStamp? epoch, int maxChars, SpillState? spill, out bool truncated,
                                            bool unreserved = false)
    {
        truncated = false;
        int cap = maxChars > 0 ? maxChars : Wire.DefaultMaxChars;
        if (!unreserved)
        {
            var whole = RenderRecordsEffectChains(results, totalSeeds, carrierRows, carrierTotal, errors, headerLine,
                                                  epoch, maxChars, spill, out _, unreserved: true);
            if (whole.Length <= cap) return whole;
        }
        bool manifestOnly = spill?.ManifestOnly ?? false;
        var sb = new StringBuilder();
        sb.Append(headerLine).Append('\n');
        sb.Append(totalSeeds).Append(" seed(s), ").Append(carrierRows).Append(" carrier row(s)");
        if (carrierTotal != carrierRows) sb.Append(" of ").Append(carrierTotal).Append(" total");
        sb.Append(", ").Append(errors).Append(" error(s)");
        if (epoch is not null) sb.Append(Wire.EpochInline(epoch));
        sb.Append('\n');
        int rendered = 0;
        string Notice(int r) =>
            "... [rendered " + r + " of " + results.Count + " seeds at max_chars=" + cap + "]\n";
        var spillText = Wire.SpillText(spill);
        var room = unreserved ? new RenderCap(cap, Unbounded)
                              : RenderCap.For(cap, spillText.Length + Notice(results.Count).Length);
        foreach (var (seed, result) in results)
        {
            if (manifestOnly) break;
            int mark = sb.Length;
            sb.Append('\n').Append("seed ").Append(seed).Append('\n');
            // The shared render builds its own buffer, so it is told what this one has spent, and still quotes the
            // caller's max_chars in its own cut notice.
            sb.Append(Wire.RenderEffectChain(result, room, sb.Length + 1, "walk.max_nodes", out bool said)).Append('\n');
            if (Crossed(sb, mark, room.Budget, Notice(rendered), ref truncated)) break;
            rendered++;
            // The shared render keeps its output inside the budget, so the cut it reports is the only thing saying
            // this answer is incomplete, and it drives the spill.
            if (!said) continue;
            Stopped(sb, Notice(rendered), rendered, results.Count, ref truncated);
            break;
        }
        sb.Append(spillText);
        return RenderCap.Settle(sb.ToString().TrimEnd('\n'), cap);
    }

    /// <summary>The info_order form's text render: per topic its identity, then the merged-order body from the shared <see cref="Wire.AppendInfoOrderView"/>, bounded by what this render has left rather than by the whole cap.</summary>
    static string RenderRecordsInfoOrder(IReadOnlyList<LoadOrderService.InfoOrderRow> rows, int total, int contested,
                                         int errors, string headerLine, OrderStamp? epoch, int maxChars,
                                         SpillState? spill, out bool truncated, bool unreserved = false)
    {
        truncated = false;
        int cap = maxChars > 0 ? maxChars : Wire.DefaultMaxChars;
        if (!unreserved)
        {
            var whole = RenderRecordsInfoOrder(rows, total, contested, errors, headerLine, epoch, maxChars, spill,
                                               out _, unreserved: true);
            if (whole.Length <= cap) return whole;
        }
        bool manifestOnly = spill?.ManifestOnly ?? false;
        var sb = new StringBuilder();
        sb.Append(headerLine).Append('\n');
        sb.Append(total).Append(" topic(s): ").Append(contested).Append(" contested, ").Append(errors).Append(" error(s)");
        if (epoch is not null) sb.Append(Wire.EpochInline(epoch));
        sb.Append('\n');
        int rendered = 0;
        string Notice(int r) =>
            "... [rendered " + r + " of " + rows.Count + " rows at max_chars=" + cap + "]\n";
        var spillText = Wire.SpillText(spill);
        int budget = unreserved ? Unbounded : Math.Max(cap - spillText.Length - Notice(rows.Count).Length, 0);
        foreach (var row in rows)
        {
            if (manifestOnly) break;
            int mark = sb.Length;
            sb.Append('\n').Append(row.Formid);
            if (row.Error is not null)
            {
                sb.Append("  error=").Append(row.Error).Append('\n');
                if (Crossed(sb, mark, budget, Notice(rendered), ref truncated)) break;
                rendered++; continue;
            }
            sb.Append("  ").Append(row.Type ?? "?").Append("  ").Append(row.EditorId ?? "<no editorid>")
              // No winner is a FACT on a folded read, where '?' would read as "could not be determined".
              .Append("  winner=").Append(row.WinnerPlugin ?? "<none: no active plugin has this record; only the folded file defines it>")
              .Append('\n');
            if (row.Order is null)
                sb.Append("  [!] the merge could not be computed for this topic (its key did not resolve in the touching index).\n");
            else if (row.Order.Order.Count == 0 && row.Order.Complete)
                sb.Append("  no INFO lines — every touching plugin's child list is empty.\n");
            // The view's own stop signal is kept rather than re-derived from Crossed, whose agreement depends on
            // where the view appends its marker — the view's business, not this render's.
            else if (!Wire.AppendInfoOrderView(sb, row.Order, budget))
                truncated = true;
            if (Crossed(sb, mark, budget, Notice(rendered), ref truncated)) break;
            rendered++;
        }
        sb.Append(spillText);
        return RenderCap.Settle(sb.ToString().TrimEnd('\n'), cap);
    }

    /// <summary>The list-lane summary render: one identity-and-winner line per outcome or its per-item error, the batch shape of the scan lane's summary rows, with the spill marker in-band on both transports.</summary>
    static string RenderRecordsSummary(IReadOnlyList<ReadOutcome> outcomes, bool json, string headerLine,
                                       List<KeyValuePair<string, string>> envelope, int maxChars, SpillState? spill,
                                       (int RowsRead, long Millis) bodyCost, out bool truncated)
    {
        truncated = false;
        int cap = maxChars > 0 ? maxChars : Wire.DefaultMaxChars;
        bool manifestOnly = spill?.ManifestOnly ?? false;
        var epoch = outcomes.FirstOrDefault(o => o.Stamp is not null)?.Stamp;
        if (json) return JsonWire.RenderRecordsSummary(outcomes, cap, envelope, spill, bodyCost, out truncated);

        var sb = new StringBuilder();
        sb.Append(headerLine).Append('\n');
        sb.Append(outcomes.Count).Append(" record(s)");
        if (epoch is not null) sb.Append(Wire.EpochInline(epoch));
        sb.Append('\n');
        int rendered = 0;
        string Notice(int r) =>
            "... [rendered " + r + " of " + outcomes.Count + " at max_chars=" + cap + "]\n";
        var spillText = Wire.SpillText(spill);
        // The notice, the spill block and the accounting line close this response, so all three are charged before
        // the first row; docs/architecture/render-budget.md.
        int budget = cap - spillText.Length - Notice(outcomes.Count).Length - RenderBudget.AccountingReserve;
        foreach (var o in outcomes)
        {
            if (manifestOnly) break;
            int mark = sb.Length;
            if (o.Error is not null) sb.Append(FormIdToken.Of(o.FormKey)).Append("  error=").Append(o.Error).Append('\n');
            else
            {
                sb.Append(FormIdToken.Of(o.FormKey));
                Wire.AppendRuntime(sb, o.RuntimeFormId, o.RuntimeFormIdNote);
                sb.Append("  ").Append(o.Record!.Type)
                  .Append("  ").Append(o.Record.EditorId ?? "<no editorid>")
                  .Append("  source=").Append(o.SourcePlugin ?? "?");
                if (o.WinnerPlugin is not null) sb.Append("  winner=").Append(o.WinnerPlugin).Append("  override_depth=").Append(o.OverrideDepth);
                sb.Append('\n');
            }
            if (sb.Length > budget)
            {
                sb.Length = mark;
                sb.Append(Notice(rendered));
                truncated = true;
                break;
            }
            rendered++;
        }
        // What reading this list's bodies cost — the count is the LIST's, not this window's.
        sb.Append(RenderBudget.BodiesLine(bodyCost.RowsRead, bodyCost.Millis));
        sb.Append(spillText);
        return RenderCap.Settle(sb.ToString(), cap);
    }

    /// <summary>The list-lane aggregate render: the resolved rows counted by winner, type or defined_in — the batch twin of the scan lane's count table — with per-item errors in their own named bucket and the same response envelope every other form carries.</summary>
    /// <param name="requestedTypes">The display names of the types the call NAMED, or null when it named none; under group_by=type each one gets a row, so a requested type with no records reads as 0.</param>
    /// <param name="rowLimit">the caller's limit= as the TABLE's row cap (0 = uncapped): a count table caps with
    /// limit= and does not page (#810), and the counts above it stay the whole tally.</param>
    static string RenderListAggregate(IReadOnlyList<ReadOutcome> outcomes, string groupBy, bool json, bool dense, OrderStamp? epoch,
                                      string headerLine, List<KeyValuePair<string, string>> envelope,
                                      (int RowsRead, long Millis) bodyCost, int maxChars,
                                      IReadOnlyList<string>? requestedTypes = null, int rowLimit = 0)
    {
        var gb = groupBy.Trim().ToLowerInvariant();
        if (gb is not ("winner" or "type" or "defined_in"))
            return Wire.Refuse(json, $"error: project.group_by='{groupBy}' is not a count key — use 'winner', 'type', or 'defined_in'.");
        int cap = maxChars > 0 ? maxChars : Wire.DefaultMaxChars;
        var groups = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // A type the call asked for is in the census whether or not it has records.
        if (gb == "type" && requestedTypes is { Count: > 0 })
            foreach (var t in requestedTypes) groups.TryAdd(t, 0);
        int errors = 0;
        foreach (var o in outcomes)
        {
            if (o.Error is not null) { errors++; continue; }
            var key = gb switch
            {
                "type" => o.Record!.Type,
                "defined_in" => FormIdToken.Plugin(o.FormKey.ModKey.FileName.String),
                _ => o.WinnerPlugin ?? "?",
            };
            groups[key] = groups.GetValueOrDefault(key) + 1;
        }
        var all = groups.OrderByDescending(g => g.Value).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        // A zero row is a type the call ASKED for that matched nothing, stated apart from the counted table because
        // it sorts last and is what a cap discards first.
        var empties = all.Where(g => g.Value == 0).Select(g => g.Key).ToList();
        var rows = empties.Count == 0 ? all : all.Where(g => g.Value > 0).ToList();
        if (json || dense)
            return JsonWire.RenderListAggregate(gb, rows, outcomes.Count, errors, epoch, bodyCost, cap, envelope, empties, rowLimit);
        var sb = new StringBuilder();
        sb.Append(headerLine).Append("  group_by=").Append(gb).Append('\n');
        sb.Append(outcomes.Count).Append(" record(s)");
        if (errors > 0) sb.Append("  (").Append(errors).Append(" per-item error(s) — counted apart, listed via form='summary')");
        if (epoch is not null) sb.Append(Wire.EpochInline(epoch));
        sb.Append('\n');
        // The notice, the accounting line and the empty-type line close this response, so all three are charged
        // before the first group row; docs/architecture/render-budget.md.
        string Notice(int r) => "... [truncated: rendered " + r + " of " + rows.Count +
                                " groups before hitting max_chars=" + cap + "; the counts above are exact — raise max_chars]\n";
        string LimitNotice(int r) => "... [" + (rows.Count - r) + " more group(s) — raise limit= to see them; the " +
                                     "counts above are exact]\n";
        var emptyLine = Wire.EmptyGroupsLine(empties, Math.Max(cap / 2, 120));
        // Either marker can close the table, so the room held back is the wider of the two.
        int budget = cap - Math.Max(Notice(rows.Count).Length, LimitNotice(0).Length)
                   - emptyLine.Length - RenderBudget.AccountingReserve;
        int shown = rowLimit > 0 ? Math.Min(rowLimit, rows.Count) : rows.Count;
        int renderedGroups = 0;
        foreach (var (key, count) in rows.Select(r => (r.Key, r.Value)))
        {
            if (renderedGroups >= shown)   // limit= caps the table's rows; the counts above stay the whole tally
            {
                sb.Append(LimitNotice(renderedGroups));
                break;
            }
            var row = "  " + count.ToString().PadLeft(6) + "  " + key + "\n";
            if (sb.Length + row.Length > budget)
            {
                sb.Append(Notice(renderedGroups));
                break;
            }
            sb.Append(row);
            renderedGroups++;
        }
        sb.Append(emptyLine);
        // What reading the bodies this table counted cost, stated on text as it is on json.
        sb.Append(RenderBudget.BodiesLine(bodyCost.RowsRead, bodyCost.Millis));
        return RenderCap.Settle(sb.ToString(), cap);
    }
}

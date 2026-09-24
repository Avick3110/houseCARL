using System.Text;
using HousecarlCore;

namespace HousecarlMcp;

// The check text render: the errors and scripts families, the shared sweep-render pieces and the merged check response.

static partial class Wire
{
    // ---- the errors family ----
    /// <summary>The errors family's own head: what it swept and what it found, above the first thing a budget can refuse and below the response title, which belongs to the caller.</summary>
    static void AppendErrorsHead(StringBuilder sb, ErrorCheckResult r, CheckAccounting acct)
    {
        bool didDangling = r.Classes.HasFlag(ErrorFindingClass.Dangling);
        bool didMasters = r.Classes.HasFlag(ErrorFindingClass.MissingMasters);
        sb.Append("scanned ").Append(r.PluginsScanned).Append(r.PluginsScanned == 1 ? " plugin · " : " plugins · ")
          .Append(didDangling ? $"{r.TotalDangling} dangling ref(s)" : "dangling refs NOT CHECKED (findings= excluded 'dangling')").Append(" · ")
          .Append(didMasters ? $"{r.TotalMissingMasters} missing master(s)" : "missing masters NOT CHECKED (findings= excluded 'missing_masters')").Append(" · ")
          .Append(didDangling ? $"{r.TotalUnscannableRecords} unscannable record(s)" : "unscannable records NOT COUNTED (the record walk was skipped)");
        // The excluded roster is on the result, so the head line counts what it is holding.
        if (r.Epoch is not null) sb.Append(" · epoch=").Append(r.Epoch).Append(OrderDegraded.Clause(r.ExcludedPlugins.Count)).Append(EpochOffOrderQualifier(r.OffOrderScanned));
        sb.Append('\n');
        if (r.FilterNote is not null) sb.Append(r.FilterNote).Append('\n');
        if (r.OffOrderScanned is { Count: > 0 } off)
            sb.Append(string.Format(ReadSentences.SweepOffOrderScanned, string.Join(", ", off),
                                    ReadSentences.SweepOffOrderErrorsCoverage)).Append('\n');
        AppendBaselineSplit(sb, r, acct);   // how much of the dangling total is vanilla baseline
    }

    /// <summary>The errors family's two counts_only axes, built once and read by both the render and the demand pass.</summary>
    internal static HistogramAxis[] ErrorsAxes(ErrorCheckResult r) => new[]
    {
        new HistogramAxis(SweepSubject.HistogramByTarget, r.Histogram,
                          "dangling ref(s) by TARGET plugin (the plugin the broken refs point INTO)",
                          "counts_only=true — totals above are exact; no per-plugin listing was built.",
                          "no dangling histogram, by target or by source — the link walk was not run (findings= excluded 'dangling')."),
        new HistogramAxis(SweepSubject.HistogramBySource, r.DanglingBySource,
                          "dangling ref(s) by SOURCE plugin (the plugin the broken refs come FROM)"),
    };

    /// <summary>The scripts family's one counts_only axis. Same reason.</summary>
    internal static HistogramAxis[] ScriptsAxes(ScriptCheckResult r) => new[]
    {
        new HistogramAxis(SweepSubject.HistogramByProperty, r.Histogram, "unbound properties by NAME",
                          "counts_only=true — totals above are exact; no per-record listing was built.",
                          "no unbound histogram — findings= excluded both unbound classes, so nothing was tallied."),
    };

    /// <summary>One plugin section's fixed part, everything besides its dangling entries, as one unit emitted whole or not at all; shared so the demand pass and the write read one source.</summary>
    internal static string ComposeErrorSection(PluginErrors p)
    {
        var fixedPart = new StringBuilder("\n[ERROR] ").Append(p.Plugin).Append('\n');
        if (p.ScanError is not null)
            fixedPart.Append("  scan error: ").Append(p.ScanError).Append('\n');
        // The two shortfalls are named apart because their remedies differ, install versus enable; null means the split was not made, so the combined wording stands in.
        if (p.MissingMasters.Count > 0 && p.InstalledButInactiveMasters is { } inactive)
        {
            var notInstalled = p.MissingMasters.Where(m => !inactive.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
            if (notInstalled.Count > 0)
                fixedPart.Append("  missing master(s) NOT installed anywhere in the MO2 install: ").Append(string.Join(", ", notInstalled))
                         .Append("   [install them — this plugin's refs into them dangle until you do]\n");
            if (inactive.Count > 0)
                fixedPart.Append("  missing master(s) installed but NOT ACTIVE in the load order (in a disabled mod, or unchecked): ")
                         .Append(string.Join(", ", inactive))
                         .Append("   [enable them — this plugin's refs into them dangle until you do]\n");
        }
        else if (p.MissingMasters.Count > 0)
            fixedPart.Append("  missing master(s): ").Append(string.Join(", ", p.MissingMasters))
                     .Append("   [declared as a dependency but not present in the active order — install/enable it, or this plugin's refs into it dangle]\n");
        if (p.UnscannableRecords > 0)
        {
            fixedPart.Append("  ").Append(p.UnscannableRecords).Append(" record(s) could not be scanned (Mutagen could not parse their content)");
            if (p.UnscannableSamples.Count > 0) fixedPart.Append(": ").Append(string.Join("; ", p.UnscannableSamples));
            fixedPart.Append('\n');
        }
        if (p.Dangling.Count > 0)
            fixedPart.Append("  dangling reference(s) (").Append(p.Dangling.Count).Append("):\n");
        return fixedPart.ToString();
    }

    /// <summary>One dangling entry, the one thing this family's accounting states a unit at a time; see <see cref="ComposeErrorSection"/>.</summary>
    internal static string ComposeDanglingLine(DanglingRef d)
    {
            return "    " + d.Source + " (" + d.SourceType
                     + (string.IsNullOrEmpty(d.SourceEditorId) ? "" : " '" + d.SourceEditorId + "'")
                     + ") -> " + d.Target + "   [target not defined by any active plugin]\n";
    }

    /// <summary>One unread-plugin row, the <c>counts_only</c> lane's honesty layer, shared for the same reason.</summary>
    internal static string ComposeUnreadRow(PluginErrors p)
    {
        var line = new StringBuilder("\n[UNREAD] ").Append(p.Plugin).Append(": ");
        if (p.ScanError is not null) line.Append(p.ScanError).Append(' ');
        if (p.UnscannableRecords > 0)
        {
            line.Append(p.UnscannableRecords).Append(" record(s) could not be scanned");
            if (p.UnscannableSamples.Count > 0) line.Append(": ").Append(string.Join("; ", p.UnscannableSamples));
        }
        line.Append('\n');
        return line.ToString();
    }

    /// <summary>One histogram row; the first row of an axis carries the axis head, so the demand pass must ask with the row index the write will use.</summary>
    internal static string ComposeHistogramRow(HistogramAxis axis, SweepCount row, bool first)
    {
        var line = "  " + row.Count.ToString().PadLeft(6) + "  " + row.Key + "\n";
        return first ? axis.Head + line : line;
    }

    /// <summary>The errors family's body: everything a cap can refuse and nothing else, the roster, accounting and boundary belonging to the response.</summary>
    static void AppendErrorsSection(StringBuilder sb, ErrorCheckResult r, BoundedBody body, int histogramLimit)
    {
        if (r.CountsOnly)
        {
            // Both axes are handed over together so both are reserved before either renders; the source axis carries no note and no not-computed line.
            AppendHistograms(sb, body, histogramLimit,
                ErrorsAxes(r));
            AppendScanErrorTail(sb, body, r.Reports);
            return;
        }

        if (r.Reports.Count == 0 && r.ExcludedPlugins.Count == 0)
            sb.Append("\nNo errors found in the scanned scope.\n");

        foreach (var p in r.Reports)
        {
            // A section is emitted whole or not at all, except for its dangling entries, which the accounting states one at a time; composing the fixed part first leaves only those two droppable units.
            var section = ComposeErrorSection(p);
            if (!body.Emit(SweepSubject.PluginSections, section.Length, () => sb.Append(section))) break;

            foreach (var d in p.Dangling)
            {
                var line = ComposeDanglingLine(d);
                if (!body.Emit(SweepSubject.DanglingEntries, line.Length, () => sb.Append(line), p.Plugin)) break;
            }
        }
    }

    /// <summary>The baseline split: how much of the dangling total came from the base-game masters and how much from the rest, naming the subset actually swept (<see cref="ErrorCheckResult.BaseMastersSwept"/>) rather than Mutagen's whole <c>BaseMasters</c> set.</summary>
    static void AppendBaselineSplit(StringBuilder sb, ErrorCheckResult r, CheckAccounting acct)
    {
        if (!r.Classes.HasFlag(ErrorFindingClass.Dangling) || r.BaseMastersSwept is not { Count: > 0 } swept) return;
        sb.Append("baseline: ").Append(r.BaselineDangling).Append(" of ").Append(r.TotalDangling)
          .Append(" dangling ref(s) come from the base-game master(s) this sweep covered (").Append(string.Join(", ", swept))
          .Append(") — vanilla leftovers rather than anything this load order introduced; ")
          .Append(r.TotalDangling - r.BaselineDangling).Append(" come from the rest of the swept scope.").Append('\n');
        // Only stated where the phase order decided something and both groups exist; on a base-masters-only scope there is no "every other plugin" for the clause to order against.
        if (acct.OmittedByBudget > 0 && r.BaselineDangling > 0 && r.NonBaseInScope)
            sb.Append("  the listing budget (limit=) is spent on every other plugin BEFORE those, so baseline findings ")
              .Append("cannot crowd the rest out of the list; the sections below stay in load order.").Append('\n');
    }

    // ---- shared sweep-render pieces ----
    /// <summary>The epoch stamp's coverage qualifier: off-order file content is outside the fingerprint, so equal epochs across such sweeps do not mean equal inputs.</summary>
    static string EpochOffOrderQualifier(IReadOnlyList<string>? offOrderScanned) =>
        offOrderScanned is { Count: > 0 } ? " (indexed plugins only — off-order file content is outside the fingerprint)" : "";

    /// <summary>Reserve every axis's fixed part — its unconditional lines and its closing disclosure — then render them all, in two passes so no axis finds a sibling has spent its room.</summary>
    internal static void AppendHistogramAxes(StringBuilder sb, BoundedBody body, int rowLimit, params HistogramAxis[] axes)
        => AppendHistograms(sb, body, rowLimit, axes);

    static void AppendHistograms(StringBuilder sb, BoundedBody body, int rowLimit, params HistogramAxis[] axes)
    {
        foreach (var a in axes) body.Reserve(a.Subject, a.TextFixed);
        foreach (var a in axes) AppendHistogram(sb, body, rowLimit, a);
    }

    /// <summary>Render one <c>counts_only=</c> histogram axis, capped at <paramref name="rowLimit"/> with the true distinct-key count always stated; a null histogram reads as "not requested" and an empty one as "found nothing".</summary>
    /// <param name="body">The one bounded emission path: the axis's rows go through it and can be refused, while its own statement about itself is written out of reserved room.</param>
    static void AppendHistogram(StringBuilder sb, BoundedBody body, int rowLimit, HistogramAxis axis)
    {
        // The note and the not-computed line are fixed text no budget may drop, written through `body` so the fixed part is measured.
        if (axis.NoteLine.Length > 0) body.Fixed(axis.Subject, () => sb.Append(axis.NoteLine));
        if (axis.Rows is not { } rows)
        {
            if (axis.NotComputedLine.Length > 0) body.Fixed(axis.Subject, () => sb.Append(axis.NotComputedLine));
            body.Release(axis.Subject);
            return;
        }
        // The title rides the empty case too, and "nothing to tally" is this axis's entire answer, so it CLOSES with it rather than emitting it.
        if (rows.Count == 0) { body.Close(axis.Subject, () => sb.Append(axis.EmptyLine)); return; }
        var head = axis.Head;
        int shown = 0;
        bool cutByBudget = false;
        foreach (var row in rows)
        {
            if (shown >= rowLimit) break;
            var unit = ComposeHistogramRow(axis, row, shown == 0);
            // A row pays for itself only: the closing line's cost is already held back.
            if (!body.Emit(axis.Subject, unit.Length, () => sb.Append(unit))) { cutByBudget = true; break; }
            shown++;
        }
        // The closing disclosure, from one computation the json lane reads too, naming the knob that stopped THIS axis. An axis that rendered every row says nothing and gives its room back; one admitted no rows still prints its head and count.
        if (HistogramCut.For(rows.Count, shown, cutByBudget) is not { } cut) { body.Release(axis.Subject); return; }
        if (shown == 0) body.Close(axis.Subject, () => sb.Append(head).Append(cut.Line));
        else body.Close(axis.Subject, () => sb.Append(cut.Line));
    }

    /// <summary>The named, reasoned list of plugins the index build could not parse, shared by the listing and <c>counts_only=</c> paths; the accounting states the row count from the same registrations.</summary>
    static void AppendExcludedPlugins(StringBuilder sb, BoundedBody body, IReadOnlyDictionary<string, string> excluded)
    {
        for (int i = 0; i < excluded.Count; i++)
        {
            var unit = ComposeExcludedRow(excluded, i);
            if (!body.Emit(SweepSubject.ExcludedRows, unit.Length, () => sb.Append(unit))) return;
        }
    }

    /// <summary>One roster row, composed by the helper the demand pass measures; the head rides the first row, so the list is whole or absent.</summary>
    internal static string ComposeExcludedRow(IReadOnlyDictionary<string, string> excluded, int index)
    {
        const string head = "\nexcluded plugins (could not be parsed — NOT checked):\n";
        var kv = excluded.ElementAt(index);
        return (index == 0 ? head : "") + "  " + kv.Key + ": " + kv.Value + "\n";
    }

    /// <summary>Under <c>counts_only=</c> the reports list carries only what could not be read, emitted verbatim so the census still names it.</summary>
    static void AppendScanErrorTail(StringBuilder sb, BoundedBody body, IReadOnlyList<PluginErrors> reports)
    {
        foreach (var p in reports)
        {
            var row = ComposeUnreadRow(p);
            if (!body.Emit(SweepSubject.UnreadRows, row.Length, () => sb.Append(row))) return;
        }
    }

    // ---- the merged, multi-family check response ----
    /// <summary>The merged sweep: one header, one section per selected family with its own accounting, one boundary block, and the excluded-plugin roster once; the body budget is divided rather than spent in series, per docs/architecture/render-budget.md.</summary>
    public static string RenderCheck(CheckSweep s, int maxChars, int histogramLimit = 1000)
        => RenderCheck(s, maxChars, histogramLimit, out _);

    /// <summary>The same render, handing back the allocation it built so a test can assert what each subject was given and spent; an internal seam.</summary>
    internal static string RenderCheck(CheckSweep s, int maxChars, int histogramLimit, out BoundedBody? measured)
    {
        measured = null;
        // What this response actually did, composed once and handed to everything below, the skeleton pass included.
        var o = CheckOutcome.For(s);
        if (o.Error is not null)
            // The fold frames a refusal as much as a finding: the seeds were looked for in the projection.
            return (s.Dialogue?.Folded is { } errFrame ? errFrame : "")
                   + "error: " + o.Error + (o.Epoch is not null ? $"\nepoch={o.Epoch}" : "")
                   + (o.OrderExcluded.Count > 0 ? "\n" + OrderDegraded.Sentence(o.OrderExcluded) : "");
        int cap = Cap(maxChars);
        var sections = o.Sections;
        var accts = o.Accountings(cap);
        // The reserve: one accounting line and one boundary line per family, held back before anything renders.
        int reserve = 0;
        for (int i = 0; i < accts.Count; i++)
            reserve += accts[i].TextAccountingReserve
                     + accts[i].Boundary.Length
                     + string.Format(ReadSentences.SweepBoundaryLabelFor,
                                     SweepFamilySelection.Token(sections[i])).Length + BoundaryWrap;
        int budget = Math.Max(0, cap - reserve);

        // What each subject wants, measured before anything is written, so the allocation can water-fill over it.
        var demand = SweepDemand.ForText(o, budget, histogramLimit);
        // And the WHOLE fixed part the response owes whatever the budget says, measured by composing it.
        var skeleton = new StringBuilder();
        var skeletonAccts = o.Accountings(cap);
        var skeletonBody = BoundedBody.Skeleton(skeletonAccts, () => skeleton.Length);
        Compose(skeleton, o, sections, skeletonAccts, skeletonBody, histogramLimit, cap);
        int fixedPart = skeleton.Length - skeletonBody.ReservedWritten - skeletonBody.BodyTotal;

        var sb = new StringBuilder();
        var body = BoundedBody.ForFamilies(accts, budget, () => sb.Length, o.Plan(),
                                           demand.Demand, demand.Reserved + fixedPart, o.ResponseSubjects,
                                           demand.Reserved);
        measured = body;
        Compose(sb, o, sections, accts, body, histogramLimit, cap);

        // The overrun question, asked of the finished response, which the notice is part of — so it settles to a fixed point; docs/architecture/render-budget.md.
        var response = sb.ToString().TrimEnd('\n');
        int needed = body.FixedPart(response.Length);
        // The first accounting states it once: the sentence is about the whole response rather than any family.
        var overrun = accts.Count > 0 ? accts[0] : null;
        if (overrun is null) return response;
        // How many times this response prints the cap back, counted in the response itself.
        int sites = overrun.CapPrintsIn(response);
        if (overrun.CapTooSmall(response.Length, needed, 0, sites) is not { } notice) return response;
        var settled = overrun.CapTooSmall(response.Length + notice.Length, needed, notice.Length, sites)!;
        if (settled.Length != notice.Length)
            settled = overrun.CapTooSmall(response.Length + settled.Length, needed, settled.Length, sites)!;
        return response + settled;
    }

    /// <summary>The whole merged response bar its overrun notice, composed through one <paramref name="body"/>, run twice per render: once with a <see cref="BoundedBody.Skeleton"/> to leave the fixed part to be measured, and once for real.</summary>
    static void Compose(StringBuilder sb, CheckOutcome o, IReadOnlyList<SweepFamily> sections,
                        IReadOnlyList<CheckAccounting> accts, BoundedBody body, int histogramLimit, int cap)
    {
        var s = o.Sweep;
        sb.Append(ReadSentences.SweepMergedTitle).Append('\n');
        // The scope sentence, above everything a budget can refuse: which families answered, which refused, and which registered ones were never asked.
        sb.Append(o.ScopeSentence()).Append('\n');
        // A short order is a response-level fact, stated here once rather than left to whichever families ran.
        if (o.OrderExcluded.Count > 0)
            sb.Append(OrderDegraded.Sentence(o.OrderExcluded)).Append('\n');
        // WHICH loose roots the asset build could not read, at the response root because ONE build feeds every
        // family that hedges on it; bounded and counted by the shared renderer.
        sb.Append(BatchRender.RootFailureLines(o.RootFailures, cap));

        // The excluded-plugin roster goes ABOVE the family sections, where each family's accounting can see its rows, and is a response-level participant in the allocation taking its share of the row budget.
        AppendExcludedPlugins(sb, body, o.ExcludedPlugins);

        for (int i = 0; i < sections.Count; i++)
        {
            var f = sections[i];
            sb.Append('\n').Append(string.Format(ReadSentences.SweepFamilySectionHead,
                                                 SweepFamilySelection.Token(f), SweepFamilySelection.Title(f)))
              .Append('\n');
            // A family that refused fills its OWN section with the refusal, never the whole response.
            if (o.Refusal(f) is { } refusal)
            {
                // A refused dialogue family still says what world it refused in, so the frame rides above it.
                if (f == SweepFamily.Dialogue && s.Dialogue?.Folded is { } foldedFrame) sb.Append(foldedFrame);
                sb.Append(refusal).Append('\n');
            }
            else if (f == SweepFamily.Errors)
            {
                AppendErrorsHead(sb, s.Errors!, accts[i]);
                AppendErrorsSection(sb, s.Errors!, body, histogramLimit);
            }
            else if (f == SweepFamily.Scripts)
            {
                AppendScriptsHead(sb, s.Scripts!);
                AppendScriptsSection(sb, s.Scripts!, body, histogramLimit);
            }
            else if (f == SweepFamily.Facegen)
            {
                FaceGenSweepRender.AppendHead(sb, s.FaceGen!);
                FaceGenSweepRender.AppendSection(sb, s.FaceGen!, body, histogramLimit);
            }
            else
            {
                // The dialogue family is SEEDED, so the scope parameters beside it did not narrow it and its head has to say so.
                DialogueSweepRender.AppendHead(sb, o);
                DialogueSweepRender.AppendSection(sb, o, body);
            }
            // This family's accounting, under this family's section, out of the room held for it.
            if (accts[i].TextLine() is { } line)
                body.Reserved(() => sb.Append('\n').Append(line).Append('\n'));
        }

        // One boundary block, one line per family that ran, written through the reserve its room came from.
        for (int i = 0; i < sections.Count; i++)
        {
            int at = i;
            body.Reserved(() => sb.Append('\n')
                                  .Append(string.Format(ReadSentences.SweepBoundaryLabelFor,
                                                        SweepFamilySelection.Token(sections[at])))
                                  .Append(accts[at].Boundary).Append('\n'));
        }
    }

    /// <summary>The headroom a boundary line's wrapping newlines are held back with, per block.</summary>
    internal const int BoundaryWrap = 32;

    // ---- the scripts family ----
    /// <summary>The scripts family's own head: what it swept and what it found, every count stating its own scope so no number reads as a wider claim than it is.</summary>
    static void AppendScriptsHead(StringBuilder sb, ScriptCheckResult r)
    {
        bool didObject = r.Classes.HasFlag(ScriptFindingClass.UnboundObject);
        bool didScalar = r.Classes.HasFlag(ScriptFindingClass.UnboundScalar);
        bool didNull = r.Classes.HasFlag(ScriptFindingClass.BoundNull);

        sb.Append("scanned ").Append(r.PluginsScanned).Append(r.PluginsScanned == 1 ? " plugin · " : " plugins · ")
          .Append(r.RecordsWithScripts).Append(" record(s) with scripts · ")
          // A class the caller excluded reads as NOT CHECKED, never as 0.
          .Append(ReadSentences.ScriptUnboundTotal(r, didObject, didScalar))
          .Append(" · ")
          .Append(ReadSentences.ScriptNullTotal(r, didNull))
          .Append(" · ")
          .Append(r.TotalUnverifiable).Append(" unverifiable");
        if (r.Epoch is not null) sb.Append(" · epoch=").Append(r.Epoch).Append(OrderDegraded.Clause(r.ExcludedPlugins.Count)).Append(EpochOffOrderQualifier(r.OffOrderScanned));
        sb.Append('\n');
        if (r.FilterNote is not null) sb.Append(r.FilterNote).Append('\n');
        if (r.OffOrderScanned is { Count: > 0 } off)
            sb.Append(string.Format(ReadSentences.SweepOffOrderScanned, string.Join(", ", off),
                                    ReadSentences.SweepOffOrderScriptsCoverage)).Append('\n');
        if (r.UnverifiableCollapsed > 0)
            sb.Append(string.Format(ReadSentences.SweepScriptUnverifiableCollapsed, r.UnverifiableCollapsed)).Append('\n');
        if (r.ReadIncomplete)
            sb.Append("note: a BSA or a loose mod folder failed to read this build — a '.pex not on disk' below may merely be unscanned, not truly absent (Q3).\n");
    }

    /// <summary>The scripts family's body: everything a cap can refuse, and like the errors family's no roster, accounting or boundary.</summary>
    static void AppendScriptsSection(StringBuilder sb, ScriptCheckResult r, BoundedBody body, int histogramLimit)
    {
        if (r.CountsOnly)
        {
            AppendHistograms(sb, body, histogramLimit,
                ScriptsAxes(r));
            // Plugins whose record enumeration faulted, its own subject so a cut response says how many it named.
            foreach (var rec in r.Reports)
            {
                if (rec.ScanError is null) continue;
                var row = ComposeScriptRecordUnit(rec);
                if (!body.Emit(SweepSubject.ScriptScanRows, row.Length, () => sb.Append(row))) break;
            }
            return;
        }

        if (r.Reports.Count == 0 && r.ExcludedPlugins.Count == 0)
            sb.Append("\nNo unbound script properties found in the scanned scope.\n");

        foreach (var rec in r.Reports)
        {
            // A record section is emitted whole or not at all — the errors family's rule in this family's units.
            var section = ComposeScriptRecordUnit(rec);
            if (!body.Emit(SweepSubject.ScriptRecords, section.Length, () => sb.Append(section))) break;
        }
    }

    /// <summary>One record's whole section, composed before it is offered to the budget, as the errors family's plugin sections are.</summary>
    internal static string ComposeScriptRecordUnit(RecordScriptFindings rec)
    {
        if (rec.ScanError is not null)
            return "\n[SCAN ERROR] " + rec.Plugin + ": " + rec.ScanError + "\n";

        var sb = new StringBuilder();
        sb.Append('\n').Append(rec.Unbound.Count > 0 ? "[UNBOUND] " : "[CHECK] ")
          .Append(FormIdToken.Of(rec.Record)).Append(" (").Append(rec.RecordType);
        if (!string.IsNullOrEmpty(rec.EditorId)) sb.Append(" '").Append(rec.EditorId).Append('\'');
        sb.Append(") in ").Append(rec.Plugin).Append('\n');

        // Unbound findings, object/form types first — those are the silent None — then uninitialized scalars.
        foreach (var u in rec.Unbound.OrderByDescending(u => u.IsObjectType))
        {
            sb.Append("  ").Append(u.IsObjectType ? "! " : "· ")
              .Append(u.PropertyName).Append(" (").Append(u.PexTypeName).Append(") on script ").Append(u.Script);
            if (!string.Equals(u.DeclaringScript, u.Script, StringComparison.OrdinalIgnoreCase))
                sb.Append(" [declared in ").Append(u.DeclaringScript).Append(']');
            sb.Append(u.IsObjectType
                ? " — declared but NOT bound → None at runtime (HIGH: object/form type — the silent no-op)\n"
                : " — declared but NOT bound → defaults to 0/false/\"\" (scalar, no baked default)\n");
        }
        if (rec.NullObjects.Count > 0)
            sb.Append("  bound-but-null object propert").Append(rec.NullObjects.Count == 1 ? "y: " : "ies: ")
              .Append(string.Join(", ", rec.NullObjects.Select(n => $"{n.PropertyName} ({n.Script})")))
              .Append("   [advisory — a None link; sometimes intentional, filled at runtime]\n");
        foreach (var uv in rec.Unverifiable)
            sb.Append("  could not verify script ").Append(uv.Script).Append(": ").Append(uv.Reason).Append('\n');
        return sb.ToString();
    }
}

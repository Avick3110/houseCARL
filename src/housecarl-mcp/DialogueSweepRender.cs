using System.Text;
using System.Text.Json;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>The dialogue family's section in both transports, through the same <see cref="CheckAccounting"/> and
/// <see cref="BoundedBody"/> machinery the siblings use; contract in docs/architecture/dialogue.md.</summary>
internal static class DialogueSweepRender
{
    // ---- the units, composed once so the demand pass and the write measure the same spelling ----------

    /// <summary>One seed's head: its identity line plus the findings that belong to the seed record.</summary>
    internal static string ComposeSeedUnit(DialogueSeedResult seed)
    {
        var report = seed.Report!;
        // The bracket is a TEXT annotation on the name, never part of it (see TopicValidation.WinnerIsFolded).
        var winner = (report.InputWinnerPlugin ?? "<unknown>")
                   + (report.InputWinnerIsFolded ? " [the folded off-order copy]" : "");
        return string.Format(ReadSentences.DialogueSeedHead, seed.Seed, KindLabel(report.InputKind),
                             Edid(report.InputEditorId), winner,
                             report.Topics.Count)
             + ComposeSeedBody(report);
    }

    /// <summary>One topic block, composed whole and emitted whole, so no finding is dropped unaccounted.</summary>
    internal static string ComposeTopicBlock(TopicValidation t)
    {
        // int.MaxValue: the cap that decides is the emitter's.
        var one = new StringBuilder();
        DialogueWire.AppendTopic(one, t, indent: true, int.MaxValue, includeInfoOrder: false);
        return one.ToString();
    }

    /// <summary>One unreachable-seed row.</summary>
    internal static string ComposeRefusalRow(DialogueSeedResult seed)
        => string.Format(ReadSentences.DialogueSeedRefused, seed.Seed, seed.Refusal);

    /// <summary>The family's head, which a budget may never refuse: scope note, counts, outright refusal.</summary>
    internal static void AppendHead(StringBuilder sb, CheckOutcome o)
    {
        var d = o.Dialogue!.Value;
        // The fold frames everything under it, so it leads the section and no budget may refuse it.
        if (o.Sweep.Dialogue?.Folded is { } folded) sb.Append(folded);
        // The scope note sits above this family's own counts and inside its own section.
        sb.Append(ScopeNote(d)).Append('\n');
        // Every number here comes off the outcome, so the counts and the scope sentence cannot disagree.
        sb.Append(string.Format(ReadSentences.DialogueCounts, d.SeedsValidated, d.SeedsReached, d.TopicsFound,
                                d.FindingsFound));
        if (o.Sweep.Dialogue?.Epoch is { } epoch)
        {
            // The degraded clause sits beside the stamp exactly as the sibling families print it.
            var clause = OrderDegraded.Clause(o.Sweep.OrderExcluded.Count);
            sb.Append(UncoveredBy(d) is { Length: > 0 } unc
                          ? string.Format(ReadSentences.DialogueEpochBound, epoch, clause, string.Join(", ", unc))
                          : string.Format(ReadSentences.DialogueEpochWhole, epoch, clause));
        }
        if (d.CountsOnly) sb.Append(ReadSentences.DialogueCountsOnly);
    }

    /// <summary>The verdict classes the record fingerprint does not describe, read by both transports.</summary>
    internal static readonly string[] EpochUncovered =
    {
        ReadSentences.DialogueUncoveredVoice,
        ReadSentences.DialogueUncoveredScripts,
        ReadSentences.DialogueUncoveredSeq,
    };

    /// <summary>Which of those classes THIS response carried, off the same per-kind table the boundary reads.</summary>
    internal static string[] UncoveredBy(DialogueOutcome d)
        => d.ChecksRun.HasFlag(DialogueChecks.TopicGraph) ? EpochUncovered : Array.Empty<string>();

    /// <summary>The family's rows, all through <paramref name="body"/>, so every refusal is accounted for.</summary>
    internal static void AppendSection(StringBuilder sb, CheckOutcome o, BoundedBody body)
    {
        if (o.Sweep.Dialogue is not { Error: null } r) return;

        if (!r.CountsOnly)
        {
            var resolved = r.Resolved.ToArray();
            for (int i = 0; i < resolved.Length; i++)
            {
                var seed = resolved[i];
                var report = seed.Report!;
                string head = ComposeSeedUnit(seed);
                if (!body.Emit(SweepSubject.DialogueSeeds, head.Length, () => sb.Append(head))) break;
                // A topic block the budget refuses ends THIS seed's blocks and nothing else.
                foreach (var t in report.Topics)
                {
                    string block = ComposeTopicBlock(t);
                    if (!body.Emit(SweepSubject.DialogueTopics, block.Length, () => sb.Append(block))) break;
                }
            }
        }

        // The unreachable seeds bound the answer, so counts_only does not silence them.
        foreach (var seed in r.Unresolved)
        {
            string row = ComposeRefusalRow(seed);
            if (!body.Emit(SweepSubject.DialogueSeedRefusals, row.Length, () => sb.Append(row))) break;
        }
    }

    /// <summary>One seed's own findings — the quest-level CK parity and the SEQ lint, printed once here.</summary>
    static string ComposeSeedBody(DialogueValidationReport r)
    {
        var sb = new StringBuilder();
        var checks = DialogueKindChecks.For(r.InputKind);
        // "owns none" is a claim a fan-out that lost a plugin cannot make; it says what it covered instead.
        if (r.InputKind == "quest" && r.Topics.Count == 0)
            sb.Append(r.ScanGaps.Count > 0 ? ReadSentences.DialogueSeedNoTopicsRead : ReadSentences.DialogueSeedNoTopics);
        foreach (var gap in r.ScanGaps) sb.Append(string.Format(ReadSentences.DialogueSeedScanGap, gap));
        DialogueWire.AppendSeq(sb, r.SeqLint);
        // The seed record's own CK parity, stated pass or fail, for every kind DialogueKindChecks gives one.
        if (checks.HasFlag(DialogueChecks.RecordParity) && r.InputIssues.Count == 0
            && DialogueKindChecks.ParityOkLine(r.InputKind) is { } ok) sb.Append(ok);
        DialogueWire.AppendIssues(sb, r.InputIssues, "  ", int.MaxValue);
        // On a seed that owns no INFO list, say what this verdict does not cover.
        if (!checks.HasFlag(DialogueChecks.TopicGraph) && checks != DialogueChecks.None)
            sb.Append("  ").Append(ReadSentences.DialogueRecordLevelScope).Append('\n');
        return sb.ToString();
    }

    /// <summary>The scope sentence, off what the call reached rather than what the caller named.</summary>
    static string ScopeNote(DialogueOutcome d)
    {
        // The reached count comes off the outcome: a seed that produced a named refusal is not a validated one.
        var howMany = d.SeedsReached < d.SeedsNamed
            ? string.Format(ReadSentences.DialogueScopeSomeSeeds, d.SeedsReached, d.SeedsNamed)
            : string.Format(ReadSentences.DialogueScopeAllSeeds, d.SeedsNamed);
        return string.Format(ReadSentences.DialogueScopeNote, howMany);
    }

    static string KindLabel(string kind) => kind switch
    {
        "quest" => "quest (QUST)",
        "topic" => "topic (DIAL)",
        "view" => "dialogue view (DLVW)",
        "branch" => "dialogue branch (DLBR)",
        _ => kind,
    };

    static string Edid(string? e) => string.IsNullOrEmpty(e) ? "<none>" : e;

    // ---- json ---------------------------------------------------------------------------------------

    /// <summary>The family's json head, its sentences carried verbatim from the text lane; every quantity once.</summary>
    internal static void WriteHead(Utf8JsonWriter w, CheckOutcome o)
    {
        var d = o.Dialogue!.Value;
        w.WriteString("scope", ScopeNote(d));
        w.WriteBoolean("seeded_not_swept", true);
        // The same frame the text head leads with, so no json consumer reads a projection as the live order.
        if (o.Sweep.Dialogue?.Folded is { } folded) w.WriteString("folded", folded.TrimEnd('\n'));
        // The stamp in the shape the swept families write, with the asset-verdict bound declared.
        JsonWire.WriteSweepEpoch(w, o.Sweep.Dialogue?.Epoch, o.Sweep.OrderExcluded.Count, null, UncoveredBy(d));
        w.WriteBoolean("counts_only", d.CountsOnly);
        w.WriteNumber("seeds_named", d.SeedsNamed);
        w.WriteNumber("seeds_reached", d.SeedsReached);
        w.WriteNumber("seeds_validated", d.SeedsValidated);
        // `..._total` because `seeds_unreachable` is the roster array below, and two members cannot share a name.
        w.WriteNumber("seeds_unreachable_total", d.SeedsUnreachable);
        w.WriteNumber("topics_found", d.TopicsFound);
        w.WriteNumber("findings_found", d.FindingsFound);
    }

    /// <summary>One json row per seed, its topics nested, each measured before it is written (<see cref="TopicRowCost"/>).</summary>
    internal static void WriteSection(Utf8JsonWriter w, CheckOutcome o, BoundedBody body)
    {
        if (o.Sweep.Dialogue is not { Error: null } r) return;
        var depths = new JsonWire.JsonUnitDepths(w.CurrentDepth);

        // The seeds array is gated on the lane: a field named for a subject is present exactly where that subject is.
        if (!r.CountsOnly)
        {
            w.WriteStartArray("seeds");
            var resolved = r.Resolved.ToArray();
            for (int i = 0; i < resolved.Length; i++)
            {
                var seed = resolved[i];
                if (!body.Emit(SweepSubject.DialogueSeeds,
                               SeedHeadCost(seed, depths.DialogueSeeds, i > 0),
                               () => WriteSeedHead(w, seed))) break;
                // A topic row the budget refuses ends THIS seed's rows and nothing else, as in the text lane.
                int topics = 0;
                foreach (var t in seed.Report!.Topics)
                {
                    var topic = t;
                    if (!body.Emit(SweepSubject.DialogueTopics,
                                   TopicRowCost(topic, depths.DialogueTopics, topics > 0),
                                   () => WriteTopicRow(w, topic))) break;
                    topics++;
                }
                // The closing brackets finish an admitted unit, so SeedHeadCost measured them as part of it.
                body.Complete(SweepSubject.DialogueSeeds, () => { w.WriteEndArray(); w.WriteEndObject(); });
            }
            w.WriteEndArray();
        }

        w.WriteStartArray("seeds_unreachable");
        int refusals = 0;
        foreach (var seed in r.Unresolved)
        {
            var row = seed;
            if (!body.Emit(SweepSubject.DialogueSeedRefusals,
                           UnreachableRowCost(row, depths.DialogueSeeds, refusals > 0),
                           () => WriteUnreachable(w, row))) break;
            refusals++;
        }
        w.WriteEndArray();
    }

    /// <summary>Opens the seed object and its topics array; the caller closes both, deliberately not a second helper.</summary>
    static void WriteSeedHead(Utf8JsonWriter w, DialogueSeedResult seed)
    {
        var r = seed.Report!;
        w.WriteStartObject();
        w.WriteString("seed", seed.Seed);
        w.WriteString("kind", r.InputKind);
        w.WriteString("editor_id", r.InputEditorId ?? "");
        w.WriteString("winner_plugin", r.InputWinnerPlugin ?? "");
        // The provenance beside the name, never inside it: a consumer indexes winner_plugin as a filename.
        if (r.InputWinnerIsFolded) w.WriteBoolean("winner_folded", true);
        w.WriteNumber("topic_count", r.Topics.Count);
        w.WriteBoolean("read_incomplete", r.ReadIncomplete);
        // Which checks this seed's kind ran, as data: an empty `input_issues` alone cannot say.
        w.WriteStartArray("checks_run");
        foreach (var name in DialogueKindChecks.Names(DialogueKindChecks.For(r.InputKind))) w.WriteStringValue(name);
        w.WriteEndArray();
        WriteIssues(w, "input_issues", r.InputIssues);
        // Its own key: a consumer reading input_issues as the parity result must not find a file lock in it.
        w.WriteStartArray("scan_gaps");
        foreach (var gap in r.ScanGaps) w.WriteStringValue(gap);
        w.WriteEndArray();
        if (r.SeqLint is { QuestIsSge: true } seq)
        {
            w.WriteStartObject("seq");
            w.WriteString("defining_plugin", seq.DefiningPlugin);
            w.WriteString("winner_plugin", seq.WinnerPlugin);
            w.WriteBoolean("seq_exists", seq.SeqExists);
            if (seq.SeqContainsQuest is { } c) w.WriteBoolean("lists_this_quest", c); else w.WriteNull("lists_this_quest");
            if (seq.SeqNewerThanPlugin is { } n) w.WriteBoolean("newer_than_plugin", n); else w.WriteNull("newer_than_plugin");
            if (seq.Note is { } note) w.WriteString("note", note);
            w.WriteEndObject();
        }
        w.WriteStartArray("topics");
    }

    static void WriteTopicRow(Utf8JsonWriter w, TopicValidation t)
    {
        w.WriteStartObject();
        w.WriteString("topic", FormIdToken.Of(t.Topic));
        w.WriteString("editor_id", t.TopicEditorId);
        w.WriteString("winner_plugin", t.WinnerPlugin);
        if (t.WinnerIsFolded) w.WriteBoolean("winner_folded", true);
        w.WriteNumber("info_count", t.InfoCount);
        w.WriteNumber("conditioned_info_count", t.ConditionedInfoCount);
        w.WriteNumber("deleted_info_count", t.DeletedInfoCount);
        w.WriteNumber("fragment_info_count", t.FragmentInfoCount);
        w.WriteString("category", t.Category);
        w.WriteString("subtype", t.Subtype);
        w.WriteString("subtype_marker", t.SubtypeName);
        // Emitted always, so a consumer never has to infer the stale-number verdict from the issues text.
        w.WriteBoolean("subtype_stale", t.SubtypeDisagreesWithMarker);
        // The marker-derived name beside the flag, so no consumer re-implements the marker→name table.
        w.WriteString("subtype_from_marker", t.SubtypeFromMarker);
        WriteIssues(w, "issues", t.Issues);
        w.WriteStartArray("silent_lines");
        foreach (var l in t.VoiceLines)
        {
            if (l.FuzPresent) continue;
            w.WriteStartObject();
            w.WriteString("info", FormIdToken.Of(l.Info));
            w.WriteNumber("response", l.ResponseNumber);
            w.WriteString("fuz_path", l.FuzPath);
            w.WriteBoolean("lip_present", l.LipPresent);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("result_scripts");
        foreach (var f in t.ScriptFindings)
        {
            if (f.Status == ScriptBindingStatus.BoundAndCompiled) continue;
            w.WriteStartObject();
            w.WriteString("info", FormIdToken.Of(f.Info));
            w.WriteString("status", f.Status.ToString());
            w.WriteString("detail", f.Detail);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    static void WriteUnreachable(Utf8JsonWriter w, DialogueSeedResult seed)
    {
        w.WriteStartObject();
        w.WriteString("seed", seed.Seed);
        w.WriteString("reason", seed.Refusal ?? "");
        w.WriteEndObject();
    }

    static void WriteIssues(Utf8JsonWriter w, string name, IReadOnlyList<DialogueIssue> issues)
    {
        w.WriteStartArray(name);
        foreach (var i in issues)
        {
            w.WriteStartObject();
            w.WriteString("severity", i.Severity == DialogueIssueSeverity.Problem ? "problem" : "warning");
            w.WriteString("message", i.Message);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    // ---- the pre-write costs: a row is measured by serializing it through JsonWire.MeasureUnit ---------

    static int TopicRowCost(TopicValidation t, int depth, bool subsequent)
        => JsonWire.MeasureUnit(depth, subsequent, w => WriteTopicRow(w, t));

    /// <summary>The seed's head and the brackets that close it: one unit, one subject, one cost.</summary>
    static int SeedHeadCost(DialogueSeedResult seed, int depth, bool subsequent)
        => JsonWire.MeasureUnit(depth, subsequent, (w, size) =>
        {
            int before = size();
            WriteSeedHead(w, seed);
            int head = size() - before;
            // A non-empty topics array closes on its own line; the throwaway row below buys the right answer.
            if (seed.Report!.Topics.Count > 0) WriteTopicRow(w, seed.Report.Topics[0]);
            before = size();
            w.WriteEndArray();
            w.WriteEndObject();
            return head + (size() - before);
        });

    static int UnreachableRowCost(DialogueSeedResult seed, int depth, bool subsequent)
        => JsonWire.MeasureUnit(depth, subsequent, w => WriteUnreachable(w, seed));

    // ---- unit costs, exposed for the demand pass (see SweepDemand) ---------------------------------
    internal static int TopicRowCostFor(TopicValidation t, int depth, bool subsequent)
        => TopicRowCost(t, depth, subsequent);
    internal static int SeedHeadCostFor(DialogueSeedResult seed, int depth, bool subsequent)
        => SeedHeadCost(seed, depth, subsequent);
    internal static int UnreachableRowCostFor(DialogueSeedResult seed, int depth, bool subsequent)
        => UnreachableRowCost(seed, depth, subsequent);
}

using System.Text;
using System.Text.Json;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>The dialogue family's section in both transports — its head, its rows and its unreachable-seed roster —
/// rendered through the same <see cref="CheckAccounting"/> and <see cref="BoundedBody"/> machinery the sibling
/// families use, and assembled out of <see cref="DialogueWire"/>'s composers. The effective merged INFO order is
/// deliberately not rendered here: it is an ordered sequence rather than a findings list, and belongs to
/// <c>records project=info_order</c>, which the family's boundary sentence says.</summary>
internal static class DialogueSweepRender
{
    // ---- text ---------------------------------------------------------------------------------------

    // ---- the units, composed once and read by both the demand pass and the write --------------------
    //
    // The allocation water-fills over measured demand, so a subject's demand is the cumulative width of its actual
    // units. These composers exist so the demand pass and the write measure the same spelling.

    /// <summary>One seed's head: its identity line plus the findings that belong to the seed record rather than to
    /// any topic — the quest-level CK parity and the SEQ lint.</summary>
    internal static string ComposeSeedUnit(DialogueSeedResult seed)
    {
        var report = seed.Report!;
        return string.Format(ReadSentences.DialogueSeedHead, seed.Seed, KindLabel(report.InputKind),
                             Edid(report.InputEditorId), report.InputWinnerPlugin ?? "<unknown>",
                             report.Topics.Count)
             + ComposeSeedBody(report);
    }

    /// <summary>One topic block, composed whole and emitted whole: the block is one finding set, and a per-line
    /// "append if it fits" would drop findings with no subject accounting for the loss.</summary>
    internal static string ComposeTopicBlock(TopicValidation t)
    {
        // int.MaxValue because the cap that decides is the emitter's, never this composer's own inline test.
        var one = new StringBuilder();
        DialogueWire.AppendTopic(one, t, indent: true, int.MaxValue, includeInfoOrder: false);
        return one.ToString();
    }

    /// <summary>One unreachable-seed row.</summary>
    internal static string ComposeRefusalRow(DialogueSeedResult seed)
        => string.Format(ReadSentences.DialogueSeedRefused, seed.Seed, seed.Refusal);

    /// <summary>The family's head, which a budget may never refuse: the scope note, the counts, and — where the
    /// family refused outright — the refusal, which is the whole section.</summary>
    internal static void AppendHead(StringBuilder sb, CheckOutcome o)
    {
        var d = o.Dialogue!.Value;
        // The scope note sits above this family's own counts and inside its own section: a caller who passed
        // plugins= alongside would otherwise read a seeded answer as a scoped one.
        sb.Append(ScopeNote(d)).Append('\n');
        // Every number here comes off the outcome, so the counts line and the scope sentence above it cannot print
        // different quantities under the same word.
        sb.Append(string.Format(ReadSentences.DialogueCounts, d.SeedsValidated, d.SeedsReached, d.TopicsFound,
                                d.FindingsFound));
        if (o.Sweep.Dialogue?.Epoch is { } epoch)
            sb.Append(string.Format(ReadSentences.DialogueEpochBound, epoch, string.Join(", ", EpochUncovered)));
        if (d.CountsOnly) sb.Append(ReadSentences.DialogueCountsOnly);
    }

    /// <summary>The verdict classes this family reports that the record fingerprint does not describe — the ASSET
    /// substrate half of the answer. Data, read by both transports, so the text sentence and <c>epoch_uncovered</c>
    /// cannot name different sets.</summary>
    internal static readonly string[] EpochUncovered =
    {
        ReadSentences.DialogueUncoveredVoice,
        ReadSentences.DialogueUncoveredScripts,
        ReadSentences.DialogueUncoveredSeq,
    };

    /// <summary>The family's rows. Everything here goes through <paramref name="body"/>, so everything here is
    /// refusable and everything refused is accounted for.</summary>
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
                // A topic block the budget refuses ends THIS seed's blocks and nothing else — breaking the seed
                // loop too would leave later seeds' heads unwritten while their subject still held unspent room.
                // No seed hands its share back: water-filling allocates the seed heads their measured demand and
                // the topic blocks everything else before either writes.
                foreach (var t in report.Topics)
                {
                    string block = ComposeTopicBlock(t);
                    if (!body.Emit(SweepSubject.DialogueTopics, block.Length, () => sb.Append(block))) break;
                }
            }
        }

        // The unreachable seeds bound the answer rather than sitting inside it, so counts_only does not silence them.
        foreach (var seed in r.Unresolved)
        {
            string row = ComposeRefusalRow(seed);
            if (!body.Emit(SweepSubject.DialogueSeedRefusals, row.Length, () => sb.Append(row))) break;
        }
    }

    /// <summary>One seed's own findings — the quest-level CK parity and the SEQ lint, which belong to the seed record
    /// rather than to any one topic and are printed once here.</summary>
    static string ComposeSeedBody(DialogueValidationReport r)
    {
        var sb = new StringBuilder();
        var checks = DialogueKindChecks.For(r.InputKind);
        // "owns none" is a claim about the load order, and a fan-out that lost a plugin cannot make it — it says what
        // it covered instead, and the gap lines below name what it did not.
        if (r.InputKind == "quest" && r.Topics.Count == 0)
            sb.Append(r.ScanGaps.Count > 0 ? ReadSentences.DialogueSeedNoTopicsRead : ReadSentences.DialogueSeedNoTopics);
        foreach (var gap in r.ScanGaps) sb.Append(string.Format(ReadSentences.DialogueSeedScanGap, gap));
        DialogueWire.AppendSeq(sb, r.SeqLint);
        // The seed record's own CK parity, stated both when it passes and when it fails, for every kind that has one.
        // Which kinds those are, and what each verdict says, comes from DialogueKindChecks rather than a literal
        // here, because the family's boundary asks the same question one level up.
        if (checks.HasFlag(DialogueChecks.RecordParity) && r.InputIssues.Count == 0
            && DialogueKindChecks.ParityOkLine(r.InputKind) is { } ok) sb.Append(ok);
        DialogueWire.AppendIssues(sb, r.InputIssues, "  ", int.MaxValue);
        // On a seed that owns no INFO list, say what this verdict does not cover: the family's boundary states one
        // scope for the whole call, and a call that also carries a quest or a topic states the wide one.
        if (!checks.HasFlag(DialogueChecks.TopicGraph) && checks != DialogueChecks.None)
            sb.Append("  ").Append(ReadSentences.DialogueRecordLevelScope).Append('\n');
        return sb.ToString();
    }

    /// <summary>The scope sentence, composed once for both transports. How many seeds it validated is read from what
    /// the call actually reached, never from what the caller named: with <c>limit=</c> below the seed count the two
    /// differ, and the accounting states the cut.</summary>
    static string ScopeNote(DialogueOutcome d)
    {
        // The reached count comes off the outcome, not from summing the two collections here: a seed that produced a
        // named refusal is not a validated one.
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

    /// <summary>The family's json head. Its sentences are carried verbatim from the text lane so the two transports
    /// state one thing. Every quantity the family found is stated here once, in <see cref="DialogueOutcome"/>'s
    /// vocabulary; <see cref="CheckAccounting"/> states only what the response rendered of them and which knob moves
    /// the rest — no quantity is written by both, or one family object would carry two values under one name.</summary>
    internal static void WriteHead(Utf8JsonWriter w, CheckOutcome o)
    {
        var d = o.Dialogue!.Value;
        w.WriteString("scope", ScopeNote(d));
        w.WriteBoolean("seeded_not_swept", true);
        // The stamp in the shape the swept families write, with the bound declared: this family also reports asset
        // verdicts, so it names them rather than claiming the fingerprint covers them.
        JsonWire.WriteSweepEpoch(w, o.Sweep.Dialogue?.Epoch, o.Sweep.OrderExcluded.Count, null, EpochUncovered);
        w.WriteBoolean("counts_only", d.CountsOnly);
        w.WriteNumber("seeds_named", d.SeedsNamed);
        w.WriteNumber("seeds_reached", d.SeedsReached);
        w.WriteNumber("seeds_validated", d.SeedsValidated);
        // `..._total` because `seeds_unreachable` is the roster array this family writes below, and two members of
        // one object cannot share a name. The sibling families spell their totals the same way.
        w.WriteNumber("seeds_unreachable_total", d.SeedsUnreachable);
        w.WriteNumber("topics_found", d.TopicsFound);
        w.WriteNumber("findings_found", d.FindingsFound);
    }

    /// <summary>One json row per seed, its topics nested. Each row's width is measured before it is written (see
    /// <see cref="TopicRowCost"/>), which is what the allocation's per-subject ceiling needs.</summary>
    internal static void WriteSection(Utf8JsonWriter w, CheckOutcome o, BoundedBody body)
    {
        if (o.Sweep.Dialogue is not { Error: null } r) return;
        var depths = new JsonWire.JsonUnitDepths(w.CurrentDepth);

        // The seeds array is gated on the lane, as both sibling families gate their row arrays: a field named for a
        // subject is present exactly where that subject is. Opened outside the gate, a counts_only response would
        // carry `"seeds": []` beside a non-zero seeds_validated.
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
                // The seed's own closing brackets finish a unit already admitted, so they are charged to the subject
                // that opened it; SeedHeadCost measured them as part of that same unit.
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

    /// <summary>Opens the seed object and its topics array; the topic rows are emitted into it one at a time and the
    /// caller closes both. Deliberately not split into two helpers — an opener whose closer lives at another call
    /// site is how a bounded json render leaves a half-written object behind.</summary>
    static void WriteSeedHead(Utf8JsonWriter w, DialogueSeedResult seed)
    {
        var r = seed.Report!;
        w.WriteStartObject();
        w.WriteString("seed", seed.Seed);
        w.WriteString("kind", r.InputKind);
        w.WriteString("editor_id", r.InputEditorId ?? "");
        w.WriteString("winner_plugin", r.InputWinnerPlugin ?? "");
        w.WriteNumber("topic_count", r.Topics.Count);
        w.WriteBoolean("read_incomplete", r.ReadIncomplete);
        // Which checks this seed's kind ran, as data: the text lane says it by printing the verdict, while here an
        // empty `input_issues` alone cannot tell a check that ran and passed from one that never ran.
        w.WriteStartArray("checks_run");
        foreach (var name in DialogueKindChecks.Names(DialogueKindChecks.For(r.InputKind))) w.WriteStringValue(name);
        w.WriteEndArray();
        WriteIssues(w, "input_issues", r.InputIssues);
        // Its own key for the same reason the text lane gives it its own line: a consumer reading input_issues as the
        // parity result must not find a file lock in it, and must still be able to see the report is bounded.
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
        w.WriteString("topic", t.Topic.ToString());
        w.WriteString("editor_id", t.TopicEditorId);
        w.WriteString("winner_plugin", t.WinnerPlugin);
        w.WriteNumber("info_count", t.InfoCount);
        w.WriteNumber("conditioned_info_count", t.ConditionedInfoCount);
        w.WriteNumber("deleted_info_count", t.DeletedInfoCount);
        w.WriteNumber("fragment_info_count", t.FragmentInfoCount);
        w.WriteString("category", t.Category);
        w.WriteString("subtype", t.Subtype);
        w.WriteString("subtype_marker", t.SubtypeName);
        WriteIssues(w, "issues", t.Issues);
        w.WriteStartArray("silent_lines");
        foreach (var l in t.VoiceLines)
        {
            if (l.FuzPresent) continue;
            w.WriteStartObject();
            w.WriteString("info", l.Info.ToString());
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
            w.WriteString("info", f.Info.ToString());
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

    // ---- the pre-write costs ------------------------------------------------------------------------
    //
    // A Utf8JsonWriter cannot measure an object without writing one, so each row's width is taken by serializing it
    // into a throwaway writer through JsonWire.MeasureUnit. It needs the response's own WriterOptions, the depth the
    // row is written at, and whether a sibling precedes it: all three change what the row costs the document, and
    // none is knowable from the row alone — measured at the wrong depth, a row under-counts its whole indentation.

    static int TopicRowCost(TopicValidation t, int depth, bool subsequent)
        => JsonWire.MeasureUnit(depth, subsequent, w => WriteTopicRow(w, t));

    /// <summary>The seed's head and the brackets that close it after its topics: one unit, one subject, one
    /// cost.</summary>
    static int SeedHeadCost(DialogueSeedResult seed, int depth, bool subsequent)
        => JsonWire.MeasureUnit(depth, subsequent, (w, size) =>
        {
            int before = size();
            WriteSeedHead(w, seed);
            int head = size() - before;
            // A topics array that ends up non-empty closes on a line of its own; an empty one closes with a single
            // bracket. The throwaway row below buys the right answer and is counted by neither span.
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

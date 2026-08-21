using System.Text;
using System.Text.Json;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// The DIALOGUE family's SECTION, in both transports — its head, its rows and its unreachable-seed roster, rendered
/// through the same <see cref="CheckAccounting"/> + <see cref="BoundedBody"/> machinery the sibling families use.
///
/// <para><b>Why its own file rather than beside the two sweep families' sections.</b> The rule those sections follow
/// is that a render lives with the helpers it is assembled out of. This family's helpers are
/// <see cref="DialogueWire"/>'s — the topic block, the SEQ block, the issue rows — not <c>Wire</c>'s, so putting it
/// in <c>Wire</c> would have split it from everything it calls and grown a file CLAUDE.md §8 already names. What
/// crosses a file boundary here is one call per transport, not a dozen widened members.</para>
///
/// <para><b>Class 8 is not rendered here.</b> The effective merged INFO order is an ordered sequence, not a findings
/// list; SPEC §6.1 sends it to <c>records project=info_order</c>, and the topic composer is asked for a block
/// without it. The family's BOUNDARY says so, so a caller chasing "why does the wrong line play" is told where that
/// answer lives rather than left to read a clean dialogue section as having looked.</para>
/// </summary>
internal static class DialogueSweepRender
{
    // ---- text ---------------------------------------------------------------------------------------

    /// <summary>The family's head: what a budget may never refuse. The scope note, the counts, and — where the
    /// family refused outright — the refusal, which IS the section.</summary>
    internal static void AppendHead(StringBuilder sb, CheckSweep s)
    {
        if (s.Refusal(SweepFamily.Dialogue) is { } refusal) { sb.Append(refusal).Append('\n'); return; }

        var r = s.Dialogue!;
        // The scope asymmetry, above this family's own counts and inside its own section: a caller who passed
        // plugins= alongside would otherwise read a seeded answer as a scoped one, and nothing would say which.
        sb.Append(string.Format(ReadSentences.DialogueScopeNote, r.SeedsNamed)).Append('\n');
        sb.Append(string.Format(ReadSentences.DialogueCounts, r.Resolved.Count(), r.TopicsFound, r.ProblemsFound));
        if (r.CountsOnly) sb.Append(ReadSentences.DialogueCountsOnly);
    }

    /// <summary>The family's rows. Everything here goes through <paramref name="body"/>, so everything here is
    /// refusable and everything refused is accounted for.</summary>
    internal static void AppendSection(StringBuilder sb, CheckSweep s, BoundedBody body)
    {
        if (s.Dialogue is not { Error: null } r) return;

        if (!r.CountsOnly)
        {
            var resolved = r.Resolved.ToArray();
            for (int i = 0; i < resolved.Length; i++)
            {
                var seed = resolved[i];
                var report = seed.Report!;
                string head = string.Format(ReadSentences.DialogueSeedHead, seed.Seed, KindLabel(report.InputKind),
                                            Edid(report.InputEditorId), report.InputWinnerPlugin ?? "<unknown>",
                                            report.Topics.Count)
                            + ComposeSeedBody(report);
                if (!body.Emit(SweepSubject.DialogueSeeds, head.Length, () => sb.Append(head))) break;
                // The LAST seed head is written, so this subject has nothing further to say and its unspent share
                // belongs to the topic blocks. Told rather than assumed: a subject's ceiling is fixed on its FIRST
                // unit against the siblings still PENDING, so without this the topics of a one-seed call are capped
                // at half the family's share and the other half — held for seed heads that will never be written —
                // goes nowhere. Measured on the live order (ARR 2.0, one 235-topic quest, plain defaults): 53 topics in
                // 40,296 chars of an 80,000 cap before this, 82 in 79,186 after.
                if (i == resolved.Length - 1) body.Release(SweepSubject.DialogueSeeds);

                bool stopped = false;
                foreach (var t in report.Topics)
                {
                    var one = new StringBuilder();
                    // Composed WHOLE, then emitted whole: the block is one finding set, and a per-line "append if it
                    // fits" drops findings with no subject accounting for the loss. int.MaxValue because the cap
                    // that decides is the emitter's, never this composer's own inline test.
                    DialogueWire.AppendTopic(one, t, indent: true, int.MaxValue, includeInfoOrder: false);
                    string block = one.ToString();
                    if (!body.Emit(SweepSubject.DialogueTopics, block.Length, () => sb.Append(block))) { stopped = true; break; }
                }
                if (stopped) break;
            }
        }

        // The unreachable seeds, in BOTH lanes. They bound the answer rather than sitting inside it, so
        // counts_only does not silence them either.
        foreach (var seed in r.Unresolved)
        {
            string row = string.Format(ReadSentences.DialogueSeedRefused, seed.Seed, seed.Refusal);
            if (!body.Emit(SweepSubject.DialogueSeedRefusals, row.Length, () => sb.Append(row))) break;
        }
    }

    /// <summary>One seed's own findings — the quest-level CK parity and the SEQ lint, which belong to the seed
    /// record rather than to any one topic and are printed ONCE here.</summary>
    static string ComposeSeedBody(DialogueValidationReport r)
    {
        var sb = new StringBuilder();
        if (r.InputKind == "quest" && r.Topics.Count == 0) sb.Append(ReadSentences.DialogueSeedNoTopics);
        DialogueWire.AppendSeq(sb, r.SeqLint);
        DialogueWire.AppendIssues(sb, r.InputIssues, "  ", int.MaxValue);
        return sb.ToString();
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

    /// <summary>The family's json section — the same facts as data. The head's sentences are carried verbatim so the
    /// two transports state one thing, and the rows carry the structure a machine consumer would otherwise have to
    /// parse back out of prose.</summary>
    internal static void WriteHead(Utf8JsonWriter w, CheckSweep s)
    {
        if (s.Refusal(SweepFamily.Dialogue) is { } refusal) { w.WriteString("refused", refusal); return; }

        var r = s.Dialogue!;
        w.WriteString("scope", string.Format(ReadSentences.DialogueScopeNote, r.SeedsNamed));
        w.WriteBoolean("seeded_not_swept", true);
        w.WriteBoolean("counts_only", r.CountsOnly);
        w.WriteNumber("seeds_named", r.SeedsNamed);
        w.WriteNumber("seeds_validated", r.Resolved.Count());
        w.WriteNumber("topics_validated", r.TopicsFound);
        w.WriteNumber("findings_found", r.ProblemsFound);
    }

    /// <summary>One json row per seed, its topics nested. Each row's width is measured before it is written
    /// (<see cref="TopicRowCost"/>), which is what the allocation's per-subject ceiling needs and what
    /// <c>dialogue-width-measure</c> established before this family was built.</summary>
    internal static void WriteSection(Utf8JsonWriter w, CheckSweep s, BoundedBody body)
    {
        if (s.Dialogue is not { Error: null } r) return;

        w.WriteStartArray("seeds");
        if (!r.CountsOnly)
        {
            var resolved = r.Resolved.ToArray();
            for (int i = 0; i < resolved.Length; i++)
            {
                var seed = resolved[i];
                if (!body.Emit(SweepSubject.DialogueSeeds, SeedHeadCost(seed), () => WriteSeedHead(w, seed))) break;
                if (i == resolved.Length - 1) body.Release(SweepSubject.DialogueSeeds);   // see the text lane
                bool stopped = false;
                foreach (var t in seed.Report!.Topics)
                {
                    if (!body.Emit(SweepSubject.DialogueTopics, TopicRowCost(t), () => WriteTopicRow(w, t))) { stopped = true; break; }
                }
                w.WriteEndArray();      // topics
                w.WriteEndObject();     // the seed
                if (stopped) break;
            }
        }
        w.WriteEndArray();

        w.WriteStartArray("seeds_unreachable");
        foreach (var seed in r.Unresolved)
        {
            if (!body.Emit(SweepSubject.DialogueSeedRefusals, UnreachableRowCost(seed), () => WriteUnreachable(w, seed))) break;
        }
        w.WriteEndArray();
    }

    /// <summary>Opens the seed object AND its topics array — the topic rows are emitted into it one at a time, and
    /// the caller closes both. Deliberately not two helpers: an opener whose closer lives at another call site is
    /// how a bounded json render leaves a half-written object behind.</summary>
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
        WriteIssues(w, "input_issues", r.InputIssues);
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
    // into a throwaway writer inside the frame the live one writes it in. The number is therefore MEASURED and an
    // upper bound on the write (the frame is counted too) — which is what BoundedBody's ceiling asks for, since it
    // charges the ceiling with what the row actually wrote.

    static int TopicRowCost(TopicValidation t) => Measure(w => WriteTopicRow(w, t));

    static int SeedHeadCost(DialogueSeedResult seed) => Measure(w => { WriteSeedHead(w, seed); w.WriteEndArray(); w.WriteEndObject(); });

    static int UnreachableRowCost(DialogueSeedResult seed) => Measure(w => WriteUnreachable(w, seed));

    static int Measure(Action<Utf8JsonWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteStartArray("rows");
            write(w);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return (int)ms.Length;
    }
}

using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

// ======================================================================
//  DialogueValidate — the ON-DEMAND whole-topic dialogue-graph validator
//  (nested-dialogue plan §3.6, Layer B unit C part C2; the on-demand counterpart of the per-create
//   VoiceCheck (unit B) + DialogueScriptCheck (unit C1)). Where those run at CREATE time over the lines a
//   single call just wrote, THIS runs on demand over a WHOLE topic resolved against the LOAD-ORDER WINNERS —
//   what the game actually sees — and audits EVERY existing INFO in the topic, not just freshly-created ones.
//   That closes unit B's deferred edit-path voice/script audit gap (the WriteTools edit-path note repoints here).
//
//  WHAT IT CHECKS (per topic, all on the RESOLVED WINNING DialogTopic record). The in-game INFO set IS the winning
//  topic's Responses: an INFO cannot exist without its parent DIAL, and overriding an INFO pulls the DIAL in with it,
//  so INFO+DIAL travel together and the winning DIAL's child list is authoritative (the "DIAL-wins-wholesale" model —
//  exactly why two mods touching one topic cause the classic dropped-line conflict; Aaron-confirmed 2026-06-19):
//    • Quest wiring — DialogTopic.Quest set and resolving to a real QUST (an unowned topic may never present).
//    • Branch wiring — if DialogTopic.Branch is set, it must resolve to a real DLBR (an unset Branch is normal).
//    • INFO.LinkTo conversation chain — the REAL topic→next-topic hand-off; a set link to a missing DIAL is a broken chain.
//    • Dangling PNAM — a SET PreviousDialog that resolves to no INFO. ABSENCE is NOT flagged: vanilla leaves PNAM empty
//      and selects intra-topic by Conditions, not by a previous-link chain (empirically confirmed; the §3.6 "PNAM chain
//      in response order" model was wrong for the existing corpus, so that check was dropped — review fold 2026-06-19).
//    • Category / Subtype — surfaced as facts (what the game will use), not judged.
//    • Voice + result-script — the existing per-INFO VoiceCheck.CheckInfo + DialogueScriptCheck.CheckInfo run over every
//      LIVE INFO (reused verbatim, so the create-path and the validator can never drift). DELETED INFOs (a removed line)
//      are skipped, not validated.
//
//  BOUNDARY (Q3): an INFO another plugin contributes but the WINNING topic override does not re-list is dropped in game
//  (the conflict above) and is likewise not seen here — a clean pass is over the winning topic's INFO set, which IS what
//  plays. The standing-limits footer names this.
//
//  WHAT IT DELIBERATELY CANNOT CHECK (grill-rev C2 — the validator is the ONLY non-advisory enforcement, so it
//  must NAME the gaps, never let "checks passed" read as "this will play"): the CTDA conditions that gate when a
//  line fires are semantic and only the game evaluates them; lip-sync accuracy + audio content are out of the
//  data layer. Both are declared as standing limits the render surfaces loudly (Q3).
//
//  RESOLUTION SCOPE (Aaron 2026-06-19): LOAD-ORDER-AWARE, like every other houseCARL read — it validates what
//  the active load order resolves (the modlist-author's "does this play in THIS list" view). An isolated
//  mod-author "master-closure" scope (validate within {plugin + its masters} only) is a recognised, deliberately
//  DEFERRED cross-tool capability (post-1.3), not a C2 knob — see memory project_parked_and_planned_work.
//
//  NEVER THROWS over a verify step: a per-topic walk is defensive, and the service wraps the whole run so a
//  resolve/asset failure rides DialogueValidationReport.CheckError, surfaced never silently swallowed (Q3).
// ======================================================================

/// <summary>How serious a graph finding is. <see cref="Problem"/> = a broken link (a Quest/Branch/LinkTo/PNAM pointing
/// at a missing record) the game cannot honour; <see cref="Warning"/> = a suspicious-but-not-fatal shape (e.g. an
/// unowned topic) the author should verify. There is deliberately no "info" level — Category/Subtype facts ride
/// <see cref="TopicValidation"/> fields, so the issue list is only ever things worth a second look.</summary>
public enum DialogueIssueSeverity { Problem, Warning }

/// <summary>One whole-topic graph finding: its <see cref="Severity"/> and a human-readable <see cref="Message"/>
/// that names the offending FormKey + what is wrong (Q3 — never a bare "invalid").</summary>
public sealed record DialogueIssue(DialogueIssueSeverity Severity, string Message);

/// <summary>One topic's whole-graph validation: identity (<see cref="Topic"/>, <see cref="TopicEditorId"/>,
/// <see cref="WinnerPlugin"/>), the surfaced Category/Subtype facts, the graph <see cref="Issues"/> (quest/branch/
/// LinkTo/dangling-PNAM), and the reused per-INFO voice (<see cref="VoiceLines"/> / <see cref="VoiceUndetermined"/>) +
/// result-script (<see cref="ScriptFindings"/>) verdicts over every LIVE INFO. <see cref="InfoCount"/> counts INFO
/// records (one INFO may carry several spoken rows, or none) — NOT spoken lines. <see cref="ConditionedInfoCount"/>
/// feeds the standing CTDA limit (grill-rev C2). <see cref="DeletedInfoCount"/> = INFOs skipped as removed (deleted).</summary>
public sealed record TopicValidation(
    FormKey Topic, string TopicEditorId, string WinnerPlugin,
    int InfoCount, int ConditionedInfoCount, int DeletedInfoCount,
    string Category, string Subtype, string SubtypeName,
    IReadOnlyList<DialogueIssue> Issues,
    IReadOnlyList<VoiceLine> VoiceLines,
    IReadOnlyList<VoiceUndetermined> VoiceUndetermined,
    IReadOnlyList<ScriptBindingFinding> ScriptFindings);

/// <summary>The whole-validation report for one <c>housecarl_validate_dialogue</c> call: the resolved input
/// (<see cref="Input"/>, <see cref="InputKind"/> = "topic"/"quest"/"error", <see cref="InputEditorId"/>) and the
/// per-topic validations. A top-level recoverable miss (the FormID isn't in the order, or resolves to neither a
/// DIAL nor a QUST) is a NAMED <see cref="Error"/>; a mid-run throw is surfaced on <see cref="CheckError"/> — both
/// honest, never a silent empty pass (Q3). <see cref="ReadIncomplete"/> carries the asset-layer caveat (a BSA that
/// failed to read, so an "absent" voice/.pex may merely be unscanned).</summary>
public sealed record DialogueValidationReport(
    FormKey Input, string InputKind, string? InputEditorId, string? InputWinnerPlugin,
    IReadOnlyList<TopicValidation> Topics)
{
    public string? Error { get; init; }
    public string? CheckError { get; init; }
    public bool ReadIncomplete { get; init; }

    /// <summary>The FormID isn't in the active order, or resolves to neither a DIAL nor a QUST — a recoverable,
    /// named error the tool renders as guidance (Q3), not a thrown failure.</summary>
    public static DialogueValidationReport ForError(FormKey fk, string error) =>
        new(fk, "error", null, null, Array.Empty<TopicValidation>()) { Error = error };

    /// <summary>The validate threw mid-run (a resolve/asset failure) — surfaced, never silently swallowed (Q3).</summary>
    public static DialogueValidationReport ForCheckError(FormKey fk, string err) =>
        new(fk, "error", null, null, Array.Empty<TopicValidation>()) { CheckError = err };
}

public static class DialogueValidate
{
    /// <summary>The type filter for the quest fan-out scan — every winning DialogTopic (DIAL) in the order.</summary>
    static readonly Type[] DialTypes = { typeof(IDialogTopicGetter) };

    /// <summary>Resolve <paramref name="fk"/> to its load-order winner and validate the dialogue graph: a DIAL →
    /// validate that one topic; a QUST → fan out to EVERY topic the quest owns (a whole-order DIAL winner scan,
    /// filtered by DialogTopic.Quest, because a topic points UP at its quest — the quest holds no topic list).
    /// Builds the load-order winner <c>Resolve</c> closure (each INFO's Speaker → NPC → VoiceType, and the topic's
    /// Quest) + the asset view off the live resolvers, and opens ONE overlay session for the run. NEVER throws: a
    /// recoverable miss (not in the order, or neither a DIAL nor a QUST) is a NAMED
    /// <see cref="DialogueValidationReport.Error"/>; a mid-run throw rides
    /// <see cref="DialogueValidationReport.CheckError"/> (Q3 — surfaced, never a silent empty pass).</summary>
    public static DialogueValidationReport Run(LoadOrderResolver resolver, AssetResolver assets, FormKey fk)
    {
        try
        {
            var view = resolver.Capture();                       // pin ONE index build for the whole validation
            using var session = resolver.OpenSession();          // one set of overlays, disposed at run end (Option B)
            var av = assets.Capture();                           // …and ONE asset build, so presence + ReadIncomplete agree

            // Load-order winner resolver for each INFO's Speaker → NPC → VoiceType and the topic's Quest. Cached for
            // the run so a topic full of lines sharing a speaker doesn't re-enumerate a master per line.
            var loCache = new Dictionary<FormKey, IMajorRecordGetter?>();
            IMajorRecordGetter? Resolve(FormKey k)
            {
                if (k.IsNull) return null;
                if (loCache.TryGetValue(k, out var c)) return c;
                IMajorRecordGetter? g = view.ResolveWinner(k) is { } w ? view.GetRecord(session, w.WinnerPlugin, k) : null;
                loCache[k] = g;
                return g;
            }

            var win = view.ResolveWinner(fk);
            if (win is null)
                return DialogueValidationReport.ForError(fk,
                    $"{fk} is not in the active load order — nothing to validate. Pass a dialogue topic (DIAL) FormID to validate one topic, or a quest (QUST) FormID to validate all of a quest's topics.");

            var body = view.GetRecord(session, win.Value.WinnerPlugin, fk);
            if (body is null)
                return DialogueValidationReport.ForError(fk,
                    $"{fk} resolves to a winner in {win.Value.WinnerPlugin} but its body could not be fetched (the plugin may have changed since the index was built) — re-run to rebuild and try again.");

            if (body is IDialogTopicGetter topic)
            {
                var tv = ValidateTopic(topic, win.Value.WinnerPlugin, Resolve, av);
                return new DialogueValidationReport(fk, "topic", topic.EditorID ?? "", win.Value.WinnerPlugin, new[] { tv })
                    { ReadIncomplete = av.ReadIncomplete };
            }

            if (body is IQuestGetter quest)
            {
                // Fan out: a topic points UP at its quest, so scan every winning DialogTopic in the order and keep
                // the ones whose Quest is this quest. A whole-order DIAL winner scan (accuracy over perf — an
                // on-demand validate, not a hot path). Each scanned body is FULLY walked by ValidateTopic before the
                // scan iterator advances (and disposes that overlay) — the WinnerRecordsOfType consume-before-advance
                // contract.
                var topics = new List<TopicValidation>();
                foreach (var (tfk, _, tbody) in view.WinnerRecordsOfType(DialTypes))
                {
                    if (tbody is not IDialogTopicGetter dt) continue;
                    if (NonNull(dt.Quest.FormKeyNullable) is not { } qk || qk != fk) continue;
                    var wp = view.ResolveWinner(tfk)?.WinnerPlugin ?? win.Value.WinnerPlugin;
                    topics.Add(ValidateTopic(dt, wp, Resolve, av));
                }
                return new DialogueValidationReport(fk, "quest", quest.EditorID ?? "", win.Value.WinnerPlugin, topics)
                    { ReadIncomplete = av.ReadIncomplete };
            }

            return DialogueValidationReport.ForError(fk,
                $"{fk} resolves to a {RecordNaming.StripOverlay(body.GetType().Name)} in {win.Value.WinnerPlugin}, not a dialogue topic (DIAL) or quest (QUST). Pass a DIAL FormID to validate one topic, or a QUST FormID to validate every topic a quest owns.");
        }
        catch (Exception ex)
        {
            return DialogueValidationReport.ForCheckError(fk, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Validate ONE already-resolved winning <paramref name="topic"/> against the load order: Quest/Branch
    /// wiring, the INFO.LinkTo conversation chain, dangling PNAM links, and the reused per-INFO voice + result-script
    /// checks over every LIVE INFO (all resolved via <paramref name="resolve"/> — the load-order winner resolver —
    /// against <paramref name="assetView"/>). Deleted INFOs are skipped, not validated. Pure walk over the in-memory
    /// topic getter; the service owns the never-throw boundary.
    ///
    /// NOTE on PNAM (DialogResponses.PreviousDialog): vanilla Skyrim leaves it EMPTY and selects among a topic's INFOs
    /// by their Conditions, NOT by a previous-link chain — so absence is the universal norm and is never flagged; only
    /// a SET-but-unresolvable PNAM is reported. The real conversation chain is INFO.LinkTo (topic → next topic), checked
    /// here for dangling targets. (Empirically confirmed 2026-06-19 against the live load order — the §3.6 "PNAM chain
    /// in response order" model was wrong for the existing corpus, so that check was dropped.)</summary>
    public static TopicValidation ValidateTopic(IDialogTopicGetter topic, string winnerPlugin,
        Func<FormKey, IMajorRecordGetter?> resolve, AssetResolver.AssetView assetView)
    {
        var edid = topic.EditorID ?? "";
        var issues = new List<DialogueIssue>();
        var voiceLines = new List<VoiceLine>();
        var voiceUndet = new List<VoiceUndetermined>();
        var scriptFindings = new List<ScriptBindingFinding>();

        // --- Quest wiring: most functional topics are owned by a quest; an unowned one may never present its lines.
        var questFk = NonNull(topic.Quest.FormKeyNullable);
        if (questFk is null)
            issues.Add(new(DialogueIssueSeverity.Warning,
                "DialogTopic.Quest is unset — this topic is not owned by a quest. Most dialogue topics are; an unowned topic may never present its lines in game. Verify this is intentional."));
        else if (resolve(questFk.Value) is not IQuestGetter)
            issues.Add(new(DialogueIssueSeverity.Problem,
                $"DialogTopic.Quest points at {questFk.Value}, which does not resolve to a quest (QUST) in the active load order — the owning quest is missing/disabled."));

        // --- Branch wiring: optional (many topics have none), but if set it must resolve to a real DLBR.
        var branchFk = NonNull(topic.Branch.FormKeyNullable);
        if (branchFk is not null && resolve(branchFk.Value) is not IDialogBranchGetter)
            issues.Add(new(DialogueIssueSeverity.Problem,
                $"DialogTopic.Branch points at {branchFk.Value}, which does not resolve to a dialogue branch (DLBR) in the active load order — the branch wiring is broken."));

        // --- Per-INFO walk over the topic's LIVE INFOs. A deleted INFO is a REMOVED line — skip it entirely (don't
        //     count it, chain it, or voice/script-check it; tally it for an honest "N skipped" note, Q3). InfoCount is
        //     INFO RECORDS, not spoken rows (one INFO can carry several DialogResponse rows, or none).
        int infoCount = 0, conditioned = 0, deleted = 0;
        foreach (var info in topic.Responses)
        {
            if (info.IsDeleted) { deleted++; continue; }
            infoCount++;

            // PNAM (PreviousDialog): vanilla leaves it empty and orders intra-topic by Conditions, so ABSENCE is the
            // norm and is NEVER flagged. Only a SET previous-link that resolves to no INFO is a real dangling reference.
            var pnam = NonNull(info.PreviousDialog.FormKeyNullable);
            if (pnam is not null && resolve(pnam.Value) is not IDialogResponsesGetter)
                issues.Add(new(DialogueIssueSeverity.Problem,
                    $"INFO {info.FormKey} has a previous-link (PNAM -> {pnam.Value}) that resolves to no dialogue line (INFO) in the active load order — a dangling reference."));

            // LinkTo: the REAL conversation chain — this line hands off to the next topic(s). A set link to a missing
            // DialogTopic is a broken chain; an empty LinkTo is a normal terminal line (never flagged).
            foreach (var link in info.LinkTo)
            {
                var lk = link.FormKey;
                if (!lk.IsNull && resolve(lk) is not IDialogTopicGetter)
                    issues.Add(new(DialogueIssueSeverity.Problem,
                        $"INFO {info.FormKey} links (LinkTo) to {lk}, which resolves to no dialogue topic (DIAL) in the active load order — the conversation chain is broken."));
            }

            if (info.Conditions.Count > 0) conditioned++;

            // Reuse the per-INFO voice + result-script checks over every LIVE INFO (resolved-winner view). These are the
            // exact methods the per-create teeth run, so the create path and the validator can never drift.
            VoiceCheck.CheckInfo(info, topic, resolve, assetView, voiceLines, voiceUndet);
            DialogueScriptCheck.CheckInfo(info, edid, assetView, scriptFindings);
        }

        return new TopicValidation(
            topic.FormKey, edid, winnerPlugin, infoCount, conditioned, deleted,
            topic.Category.ToString(), topic.Subtype.ToString(), DescribeSubtypeName(topic.SubtypeName),
            issues, voiceLines, voiceUndet, scriptFindings);
    }

    /// <summary>A 4-char SubtypeName marker as text, or "&lt;none&gt;" for the empty/default RecordType — so a
    /// missing marker reads as a fact, never as a blank (Q3).</summary>
    static string DescribeSubtypeName(RecordType rt)
    {
        var s = rt.ToString();
        return string.IsNullOrWhiteSpace(s) || s.All(c => c == '\0') ? "<none>" : s;
    }

    /// <summary>A nullable FormLink's target as a real FormKey, or null when the link is unset OR explicitly Null
    /// (00000000) — both mean "no target". Mirrors <see cref="VoiceCheck"/>'s sibling so the two read links the
    /// same way.</summary>
    static FormKey? NonNull(FormKey? fk) => fk is { } v && !v.IsNull ? v : null;
}

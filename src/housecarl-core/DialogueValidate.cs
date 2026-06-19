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
//  WHAT IT CHECKS (per topic, all on the RESOLVED WINNING DialogTopic record — a plugin that overrides any INFO
//  must carry its parent topic, so the winning topic's authored Responses ARE the in-game response set + order):
//    • PNAM previous-link chain — each INFO's PreviousDialog should chain to the line before it in response
//      order; the first line should have none. A disagreement means the playback order and the chain conflict.
//    • Quest wiring — DialogTopic.Quest set and resolving to a real QUST (an unowned topic may never present).
//    • Branch wiring — if DialogTopic.Branch is set, it must resolve to a real DLBR.
//    • Category / Subtype — surfaced as facts (what the game will use), not judged.
//    • Voice + result-script — the existing per-INFO VoiceCheck.CheckInfo + DialogueScriptCheck.CheckInfo run
//      over every INFO (reused verbatim, so the create-path and the validator can never drift).
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

/// <summary>How serious a graph finding is. <see cref="Problem"/> = a broken link (a Quest/Branch pointing at a
/// missing record) the game cannot honour; <see cref="Warning"/> = a suspicious-but-not-fatal shape (a PNAM chain
/// that disagrees with response order, an unowned topic) the author should verify. There is deliberately no "info"
/// level — Category/Subtype facts ride <see cref="TopicValidation"/> fields, so the issue list is only ever things
/// worth a second look.</summary>
public enum DialogueIssueSeverity { Problem, Warning }

/// <summary>One whole-topic graph finding: its <see cref="Severity"/> and a human-readable <see cref="Message"/>
/// that names the offending FormKey + what is wrong (Q3 — never a bare "invalid").</summary>
public sealed record DialogueIssue(DialogueIssueSeverity Severity, string Message);

/// <summary>One topic's whole-graph validation: identity (<see cref="Topic"/>, <see cref="TopicEditorId"/>,
/// <see cref="WinnerPlugin"/>), the surfaced Category/Subtype facts, the graph <see cref="Issues"/> (PNAM/quest/
/// branch), and the reused per-INFO voice (<see cref="VoiceLines"/> / <see cref="VoiceUndetermined"/>) + result-
/// script (<see cref="ScriptFindings"/>) verdicts over EVERY INFO. <see cref="ConditionedInfoCount"/> feeds the
/// standing CTDA limit (grill-rev C2).</summary>
public sealed record TopicValidation(
    FormKey Topic, string TopicEditorId, string WinnerPlugin,
    int InfoCount, int ConditionedInfoCount,
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

    /// <summary>Validate ONE already-resolved winning <paramref name="topic"/> against the load order: the PNAM
    /// chain in response order, Quest/Branch wiring (resolved via <paramref name="resolve"/> — the load-order
    /// winner resolver), and the reused per-INFO voice + result-script checks over EVERY INFO (against
    /// <paramref name="assetView"/>). Pure walk over the in-memory topic getter — the only external calls are
    /// <paramref name="resolve"/> (the service's session) and the asset view; the service owns the never-throw
    /// boundary.</summary>
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

        // --- PNAM previous-link chain, walked in response order. Well-formed: line[0] has no PNAM; line[k] chains
        //     to line[k-1]'s FormKey. A disagreement means the authored order and the PNAM chain conflict (the
        //     game plays by the chain, so the lines may run out of order or not at all). Surfaced as warnings —
        //     unusual-but-not-provably-fatal — never silently (Q3).
        var responses = topic.Responses;
        var topicKeys = new HashSet<FormKey>();
        foreach (var info in responses) topicKeys.Add(info.FormKey);

        int conditioned = 0;
        FormKey? prevKey = null;
        for (int i = 0; i < responses.Count; i++)
        {
            var info = responses[i];
            var pnam = NonNull(info.PreviousDialog.FormKeyNullable);
            if (i == 0)
            {
                if (pnam is not null)
                    issues.Add(new(DialogueIssueSeverity.Warning,
                        $"the first response line ({info.FormKey}) has a previous-link (PNAM -> {pnam.Value}); the first line in a topic normally has none — the playback chain may be malformed."));
            }
            else if (pnam is null)
                issues.Add(new(DialogueIssueSeverity.Warning,
                    $"response line #{i + 1} ({info.FormKey}) has no previous-link (PNAM) but is not first; it should chain to the line before it ({prevKey!.Value}) — the playback order may break."));
            else if (pnam.Value != prevKey!.Value)
                issues.Add(new(DialogueIssueSeverity.Warning,
                    topicKeys.Contains(pnam.Value)
                        ? $"response line #{i + 1} ({info.FormKey}) chains (PNAM) to {pnam.Value}, not the line before it in response order ({prevKey!.Value}) — the order and the chain disagree."
                        : $"response line #{i + 1} ({info.FormKey}) chains (PNAM) to {pnam.Value}, which is not a line in this topic — the chain leaves the topic."));
            prevKey = info.FormKey;

            if (info.Conditions.Count > 0) conditioned++;

            // Reuse the per-INFO voice + result-script checks over EVERY INFO (resolved-winner view). These are the
            // exact methods the per-create teeth run, so the create path and the validator can never drift.
            VoiceCheck.CheckInfo(info, topic, resolve, assetView, voiceLines, voiceUndet);
            DialogueScriptCheck.CheckInfo(info, edid, assetView, scriptFindings);
        }

        return new TopicValidation(
            topic.FormKey, edid, winnerPlugin, responses.Count, conditioned,
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

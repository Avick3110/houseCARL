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
/// feeds the standing CTDA limit (grill-rev C2). <see cref="DeletedInfoCount"/> = INFOs skipped as removed (deleted).
/// <see cref="FragmentInfoCount"/> = live INFOs carrying a result-script fragment — lines that run Papyrus code
/// which CAN surface in Papyrus.log (on an error or an explicit trace), where a plain voiced line has no code path
/// that ever can (item 8).</summary>
public sealed record TopicValidation(
    FormKey Topic, string TopicEditorId, string WinnerPlugin,
    int InfoCount, int ConditionedInfoCount, int DeletedInfoCount, int FragmentInfoCount,
    string Category, string Subtype, string SubtypeName,
    IReadOnlyList<DialogueIssue> Issues,
    IReadOnlyList<VoiceLine> VoiceLines,
    IReadOnlyList<VoiceUndetermined> VoiceUndetermined,
    IReadOnlyList<ScriptBindingFinding> ScriptFindings);

/// <summary>The SEQ staleness/coverage lint result for a QUEST-input validation (item 7); null for a non-SGE quest
/// or a DIAL input. A Start-Game-Enabled quest needs a <c>.seq</c> that LISTS it (by its on-disk FormID) and is NEWER
/// than its defining plugin, or it is dormant on a fresh save — its dialogue never shows. <see cref="SeqContainsQuest"/>
/// and <see cref="SeqNewerThanPlugin"/> are null when undeterminable (the winning <c>.seq</c> is inside a BSA, or
/// unreadable — <see cref="Note"/> says why); <see cref="OnDiskFormId"/> is the 4-byte value a <c>.seq</c> must
/// contain for this quest (0 when it couldn't be computed). The check keys off <see cref="DefiningPlugin"/>;
/// <see cref="WinnerPlugin"/> is the plugin whose record the game actually reads (the load-order winner) — when it
/// differs from the defining plugin, an override is in play and the render softens a not-covered verdict to an
/// ambiguity (the override may be the plugin that flags SGE and needs its own .seq) rather than a confident
/// "dormant" against the defining plugin (Q3).</summary>
public sealed record SeqLintFinding(
    bool QuestIsSge, string DefiningPlugin, string WinnerPlugin, uint OnDiskFormId,
    bool SeqExists, bool? SeqContainsQuest, bool? SeqNewerThanPlugin, string? Note);

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

    /// <summary>The SEQ staleness/coverage lint (item 7), set only for a Start-Game-Enabled QUEST input; null
    /// otherwise (a non-SGE quest, or a DIAL input — a topic isn't a quest, so a .seq isn't its concern).</summary>
    public SeqLintFinding? SeqLint { get; init; }

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

            // Cheap O(1) existence check (the index dict, no body fetch): a dangling/missing reference — the common
            // breakage — is caught here, so only a PRESENT link pays Resolve's body fetch (to name a wrong type). See
            // ValidateTopic.BadRef.
            bool InOrder(FormKey k) => !k.IsNull && view.ResolveWinner(k) is not null;

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
                var tv = ValidateTopic(topic, win.Value.WinnerPlugin, InOrder, Resolve, av);
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
                // SEQ staleness/coverage lint (item 7): keyed on the QUEST input, independent of its topics — a
                // start-game-enabled quest needs a .seq that lists it, or it (and all its dialogue) stays dormant.
                var seqLint = CheckSeq(view, av, fk, quest, win.Value.WinnerPlugin);

                var topics = new List<TopicValidation>();
                foreach (var (tfk, _, tbody) in view.WinnerRecordsOfType(DialTypes))
                {
                    if (tbody is not IDialogTopicGetter dt) continue;
                    if (NonNull(dt.Quest.FormKeyNullable) is not { } qk || qk != fk) continue;
                    var wp = view.ResolveWinner(tfk)?.WinnerPlugin ?? win.Value.WinnerPlugin;
                    topics.Add(ValidateTopic(dt, wp, InOrder, Resolve, av));
                }
                return new DialogueValidationReport(fk, "quest", quest.EditorID ?? "", win.Value.WinnerPlugin, topics)
                    { ReadIncomplete = av.ReadIncomplete, SeqLint = seqLint };
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
    /// in response order" model was wrong for the existing corpus, so that check was dropped.)
    ///
    /// References are vetted by <paramref name="inOrder"/> first (a cheap O(1) index lookup — a dangling/missing target,
    /// the common breakage, costs no body fetch); only a PRESENT target pays <paramref name="resolve"/> to name a wrong
    /// type. The voice/script reuse still uses <paramref name="resolve"/> for the speaker/quest bodies it must read.</summary>
    internal static TopicValidation ValidateTopic(IDialogTopicGetter topic, string winnerPlugin,
        Func<FormKey, bool> inOrder, Func<FormKey, IMajorRecordGetter?> resolve, AssetResolver.AssetView assetView)
    {
        var edid = topic.EditorID ?? "";
        var issues = new List<DialogueIssue>();
        var voiceLines = new List<VoiceLine>();
        var voiceUndet = new List<VoiceUndetermined>();
        var scriptFindings = new List<ScriptBindingFinding>();

        // Text-encoding lint (item 1): flag non-ASCII in the player-facing strings — the topic Name (once), each
        // INFO Prompt, and each spoken response Text — because the CK/Papyrus user-facing text surface is
        // effectively Windows-1252/ASCII, so an em-dash, ellipsis, or smart quote renders as in-game mojibake.
        // WARN only (a heuristic — HTML/Ultralight UIs render Unicode fine) and REPORT-ONLY (this validator never
        // mutates; it suggests the ASCII substitute, never performs it).
        CheckEncoding(topic.Name?.String, $"DialogTopic.Name ({edid})", issues);

        // Classify a SET reference: cheap-existence first (a dangling/missing target needs no body fetch), then a body
        // fetch ONLY for the rarer present-but-wrong-type case (so the message names what it actually is). Returns null
        // when the reference is fine, else the clause describing what's wrong — sharper than one "doesn't resolve":
        // "missing or disabled" vs "resolves to a Weapon" (Q3), and it skips most full-plugin enumerations.
        string? BadRef(FormKey target, string expects, Func<IMajorRecordGetter, bool> isExpected)
        {
            if (!inOrder(target)) return $"is not in the active load order ({expects} missing or disabled)";
            var body = resolve(target);
            return body is not null && isExpected(body) ? null
                : $"resolves to {(body is null ? "an unreadable record" : "a " + RecordNaming.StripOverlay(body.GetType().Name))}, not {expects}";
        }

        // --- Quest wiring: most functional topics are owned by a quest; an unowned one may never present its lines.
        var questFk = NonNull(topic.Quest.FormKeyNullable);
        if (questFk is null)
            issues.Add(new(DialogueIssueSeverity.Warning,
                "DialogTopic.Quest is unset — this topic is not owned by a quest. Most dialogue topics are; an unowned topic may never present its lines in game. Verify this is intentional."));
        else if (BadRef(questFk.Value, "a quest (QUST)", b => b is IQuestGetter) is { } qwhy)
            issues.Add(new(DialogueIssueSeverity.Problem,
                $"DialogTopic.Quest points at {questFk.Value}, which {qwhy} — the owning quest is unresolved."));

        // --- Branch wiring: optional (many topics have none), but if set it must resolve to a real DLBR.
        var branchFk = NonNull(topic.Branch.FormKeyNullable);
        if (branchFk is not null && BadRef(branchFk.Value, "a dialogue branch (DLBR)", b => b is IDialogBranchGetter) is { } bwhy)
            issues.Add(new(DialogueIssueSeverity.Problem,
                $"DialogTopic.Branch points at {branchFk.Value}, which {bwhy} — the branch wiring is broken."));

        // --- Per-INFO walk over the topic's LIVE INFOs. A deleted INFO is a REMOVED line — skip it entirely (don't
        //     count it, chain it, or voice/script-check it; tally it for an honest "N skipped" note, Q3). InfoCount is
        //     INFO RECORDS, not spoken rows (one INFO can carry several DialogResponse rows, or none).
        int infoCount = 0, conditioned = 0, deleted = 0, fragmentInfos = 0;
        foreach (var info in topic.Responses)
        {
            if (info.IsDeleted) { deleted++; continue; }
            infoCount++;

            // Fragment-presence tally (item 8): does this line carry a result-script FRAGMENT (a code path that can
            // surface in Papyrus.log)? Via the single fragment-presence home, so this never drifts from the per-INFO
            // HasFragment the script check sets.
            if (DialogueScriptCheck.HasResultFragment(info)) fragmentInfos++;

            // Text-encoding lint (item 1) — this line's player-facing strings: its menu Prompt and each spoken row.
            CheckEncoding(info.Prompt?.String, $"INFO {info.FormKey} Prompt", issues);
            int rnum = 0;
            foreach (var resp in info.Responses)
                CheckEncoding(resp.Text?.String, $"INFO {info.FormKey} response {++rnum} text", issues);

            // PNAM (PreviousDialog): vanilla leaves it empty and orders intra-topic by Conditions, so ABSENCE is the
            // norm and is NEVER flagged. Only a SET previous-link that doesn't resolve to an INFO is a real defect.
            var pnam = NonNull(info.PreviousDialog.FormKeyNullable);
            if (pnam is not null && BadRef(pnam.Value, "a dialogue line (INFO)", b => b is IDialogResponsesGetter) is { } pwhy)
                issues.Add(new(DialogueIssueSeverity.Problem,
                    $"INFO {info.FormKey} has a previous-link (PNAM -> {pnam.Value}) that {pwhy}."));

            // LinkTo: the REAL conversation chain — this line hands off to the next topic(s). A set link to a missing
            // DialogTopic is a broken chain; an empty LinkTo is a normal terminal line (never flagged).
            foreach (var link in info.LinkTo)
            {
                var lk = link.FormKey;
                if (!lk.IsNull && BadRef(lk, "a dialogue topic (DIAL)", b => b is IDialogTopicGetter) is { } lwhy)
                    issues.Add(new(DialogueIssueSeverity.Problem,
                        $"INFO {info.FormKey} links (LinkTo) to {lk}, which {lwhy} — the conversation chain is broken."));
            }

            if (info.Conditions.Count > 0) conditioned++;

            // Reuse the per-INFO voice + result-script checks over every LIVE INFO (resolved-winner view). These are the
            // exact methods the per-create teeth run, so the create path and the validator can never drift.
            VoiceCheck.CheckInfo(info, topic, resolve, assetView, voiceLines, voiceUndet);
            DialogueScriptCheck.CheckInfo(info, edid, assetView, scriptFindings);
        }

        return new TopicValidation(
            topic.FormKey, edid, winnerPlugin, infoCount, conditioned, deleted, fragmentInfos,
            topic.Category.ToString(), topic.Subtype.ToString(), DescribeSubtypeName(topic.SubtypeName),
            issues, voiceLines, voiceUndet, scriptFindings);
    }

    /// <summary>SEQ staleness/coverage lint (item 7) for a QUEST input: if the quest is Start-Game-Enabled, does its
    /// DEFINING plugin have a <c>.seq</c> that LISTS it (by its on-disk FormID) and is NEWER than the plugin? An SGE
    /// quest with a missing / non-listing / stale <c>.seq</c> is dormant on a fresh save — its dialogue never shows
    /// (the silent-failure class houseCARL refuses, Q3). Returns null for a non-SGE quest (no <c>.seq</c> needed, no
    /// lint). Fault-isolated: any IO/parse failure yields a NAMED note rather than a throw, so a SEQ-check failure
    /// can't sink the whole validation. The winning <c>.seq</c> is resolved via the VFS (loose beats BSA); a
    /// BSA-resident <c>.seq</c> has no loose path, so its contents + mtime are undeterminable here — surfaced as a
    /// note, never a false "OK" or a false "stale" (Q3). The check keys off the DEFINING plugin, which is correct for
    /// the common case (a quest authored SGE in its own plugin) and for a vanilla override that keeps SGE (the base
    /// .seq already lists it). It carries <paramref name="winnerPlugin"/> too, so the render can SOFTEN to an
    /// ambiguity note rather than assert a confident "dormant" against the defining plugin when the WINNING record is
    /// an override (winner != defining) — that override may itself be the plugin that flags SGE and would need its
    /// OWN .seq (Q3 — don't falsely attribute the gap to the wrong plugin).</summary>
    internal static SeqLintFinding? CheckSeq(LoadOrderResolver.IndexView view, AssetResolver.AssetView av,
        FormKey fk, IQuestGetter quest, string winnerPlugin)
    {
        if (!quest.Flags.HasFlag(Quest.Flag.StartGameEnabled)) return null;   // not SGE → no .seq needed, no lint
        var defining = fk.ModKey.FileName;
        try
        {
            var pluginPath = view.PluginPath(defining);
            if (pluginPath is null)
                return new SeqLintFinding(true, defining, winnerPlugin, 0, false, null, null,
                    $"could not locate the defining plugin '{defining}' on disk to check its .seq.");

            uint onDisk = SeqFile.OnDiskFormIdFromPlugin(pluginPath, fk);
            var seqRel = $@"SEQ\{Path.GetFileNameWithoutExtension(defining)}.seq";
            var seqSource = av.ResolveForPlacement(seqRel).Sources.FirstOrDefault();

            if (seqSource is null)                                           // no .seq anywhere in the VFS
                return new SeqLintFinding(true, defining, winnerPlugin, onDisk, false, null, null, null);

            if (seqSource.LooseFilePath is null)                            // the winning .seq is inside a BSA
                return new SeqLintFinding(true, defining, winnerPlugin, onDisk, true, null, null,
                    "the winning .seq is inside a BSA, so its contents and modification time can't be checked here.");

            var seqBytes = File.ReadAllBytes(seqSource.LooseFilePath);
            bool contains = SeqFile.SeqContains(seqBytes, onDisk);
            bool newer = File.GetLastWriteTimeUtc(seqSource.LooseFilePath) >= File.GetLastWriteTimeUtc(pluginPath);
            return new SeqLintFinding(true, defining, winnerPlugin, onDisk, true, contains, newer, null);
        }
        catch (Exception ex)
        {
            return new SeqLintFinding(true, defining, winnerPlugin, 0, false, null, null, $"the .seq check could not run: {ex.Message}");
        }
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

    /// <summary>The common non-ASCII offenders with a known ASCII substitute, for the encoding lint's suggestion
    /// (item 1). Any OTHER non-ASCII char is still flagged, just without a substitution.</summary>
    static readonly IReadOnlyDictionary<char, string> AsciiSubstitute = new Dictionary<char, string>
    {
        ['—'] = "-",    // em dash
        ['–'] = "-",    // en dash
        ['…'] = "...",  // ellipsis
        ['‘'] = "'",    // left single quote
        ['’'] = "'",    // right single quote / apostrophe
        ['“'] = "\"",   // left double quote
        ['”'] = "\"",   // right double quote
        ['•'] = "*",    // bullet
    };

    /// <summary>Text-encoding lint (item 1): if <paramref name="s"/> carries any non-ASCII char (&gt; 0x7F), add ONE
    /// WARNING for this <paramref name="locus"/> naming the offending char(s) and the ASCII substitute where known.
    /// The CK/Papyrus user-facing surface is effectively Windows-1252/ASCII, so these usually render as in-game
    /// mojibake. WARN, never blocks (heuristic); REPORT-ONLY (the validator is read-only — it never rewrites the
    /// string). A bare "invalid" is never emitted: the message names exactly which characters and what to use (Q3).</summary>
    static void CheckEncoding(string? s, string locus, List<DialogueIssue> issues)
    {
        if (string.IsNullOrEmpty(s)) return;
        var offenders = new List<char>();
        foreach (var ch in s) if ((int)ch > 0x7F && !offenders.Contains(ch)) offenders.Add(ch);
        if (offenders.Count == 0) return;

        var desc = string.Join(", ", offenders.Select(c => $"U+{(int)c:X4} '{c}'"));
        var subs = offenders.Where(AsciiSubstitute.ContainsKey).Select(c => $"'{c}'->\"{AsciiSubstitute[c]}\"").ToList();
        var sug = subs.Count > 0 ? $" Suggested ASCII: {string.Join(", ", subs)}." : "";
        issues.Add(new(DialogueIssueSeverity.Warning,
            $"{locus} contains non-ASCII char(s) {desc} — the CK/Papyrus user-facing text surface is Windows-1252/ASCII, so these usually render as in-game mojibake.{sug}"));
    }
}

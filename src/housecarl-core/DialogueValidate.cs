using System.Reflection;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

// ======================================================================
//  DialogueValidate — the on-demand whole-topic dialogue-graph validator. Where VoiceCheck and
//  DialogueScriptCheck run at CREATE time over the lines one call just wrote, this runs on demand over a
//  whole topic resolved against the LOAD-ORDER WINNERS and audits every existing INFO.
//
//  The in-game INFO set is NOT just the winning topic's Responses: every touching plugin's INFOs MERGE, so a
//  line no later plugin re-lists is not dropped, it is REORDERED (DialogueInfoOrder carries the model). The
//  per-INFO body checks here still walk the WINNING topic's child list, so an INFO another plugin contributes
//  but this winner does not re-list is not body-checked — a clean pass means "every line this winner lists is
//  sound", not "every line in this topic is". The effective ORDER view is the merge across all of them.
//
//  Per-INFO checks: quest wiring, branch wiring, the INFO.LinkTo topic→next-topic chain, a SET-but-dangling
//  PNAM, INFO CK-parity (CNAM/ENAM), and the reused VoiceCheck/DialogueScriptCheck. Category/Subtype are
//  surfaced as facts, not judged. Deleted INFOs are skipped. PNAM ABSENCE is never flagged — vanilla leaves it
//  empty and selects intra-topic by Conditions, not by a previous-link chain.
//
//  Standing limits the render must state rather than let "checks passed" read as "this will play": CTDA
//  conditions are semantic and only the game evaluates them, and lip-sync/audio content is outside the data
//  layer. DIAL Priority (PNAM) is a permanent CK-parity boundary — a non-nullable float with no "unset" signal
//  on a finished record, so flagging its absence would false-positive every legitimately-priority-0 topic. The
//  rest of the CK-parity family is checked: INFO CNAM/ENAM per live INFO, DLVW DNAM/ENAM and DLBR TNAM as
//  their own input kinds, QUST ANAM/objective FNAM once per quest input.
//
//  Resolution scope is load-order-aware, like every other houseCARL read: it validates what the active order
//  resolves. Validating within {plugin + its masters} only is a deliberately deferred capability.
//
//  Never throws over a verify step: the whole run is wrapped so a resolve/asset failure rides
//  DialogueValidationReport.CheckError rather than being silently swallowed.
// ======================================================================

/// <summary>How serious a graph finding is. <see cref="Problem"/> = a broken link (a Quest/Branch/LinkTo/PNAM pointing
/// at a missing record) the game cannot honour; <see cref="Warning"/> = a suspicious-but-not-fatal shape (e.g. an
/// unowned topic) the author should verify. There is deliberately no "info" level — Category/Subtype facts ride
/// <see cref="TopicValidation"/> fields, so the issue list is only ever things worth a second look.</summary>
public enum DialogueIssueSeverity { Problem, Warning }

/// <summary>One whole-topic graph finding: its <see cref="Severity"/> and a <see cref="Message"/> that names the
/// offending FormKey and what is wrong — never a bare "invalid".</summary>
public sealed record DialogueIssue(DialogueIssueSeverity Severity, string Message);

/// <summary>One topic's whole-graph validation: identity (<see cref="Topic"/>, <see cref="TopicEditorId"/>,
/// <see cref="WinnerPlugin"/>), the surfaced Category/Subtype facts, the graph <see cref="Issues"/> (quest/branch/
/// LinkTo/dangling-PNAM), and the reused per-INFO voice (<see cref="VoiceLines"/> / <see cref="VoiceUndetermined"/>) +
/// result-script (<see cref="ScriptFindings"/>) verdicts over every LIVE INFO. <see cref="InfoCount"/> counts INFO
/// records (one INFO may carry several spoken rows, or none) — NOT spoken lines. <see cref="ConditionedInfoCount"/>
/// feeds the standing CTDA limit. <see cref="DeletedInfoCount"/> = INFOs skipped as removed (deleted).
/// <see cref="FragmentInfoCount"/> = live INFOs carrying a result-script fragment — lines that run Papyrus code, so
/// they can surface in Papyrus.log on an error or an explicit trace, where a plain voiced line never can.</summary>
public sealed record TopicValidation(
    FormKey Topic, string TopicEditorId, string WinnerPlugin,
    int InfoCount, int ConditionedInfoCount, int DeletedInfoCount, int FragmentInfoCount,
    string Category, string Subtype, string SubtypeName,
    IReadOnlyList<DialogueIssue> Issues,
    IReadOnlyList<VoiceLine> VoiceLines,
    IReadOnlyList<VoiceUndetermined> VoiceUndetermined,
    IReadOnlyList<ScriptBindingFinding> ScriptFindings)
{
    /// <summary>The effective, merged INFO order for this topic — the sequence the game walks top-to-bottom, merged
    /// across every touching plugin's child list (xEdit INOA parity). Null only when the merge could not be built
    /// (the topic resolved to no touching plugins). This is the one view that shows a pure REORDER, which changes
    /// which line plays while leaving every field identical — see <see cref="DialogueInfoOrder"/>.</summary>
    public InfoOrderView? InfoOrder { get; init; }
}

/// <summary>The SEQ staleness/coverage lint result for a QUEST-input validation; null for a non-SGE quest or a DIAL
/// input. A Start-Game-Enabled quest needs a <c>.seq</c> that LISTS it (by its on-disk FormID) and is NEWER than its
/// defining plugin, or it is dormant on a fresh save — its dialogue never shows. <see cref="SeqContainsQuest"/> and
/// <see cref="SeqNewerThanPlugin"/> are null when undeterminable (the winning <c>.seq</c> is inside a BSA, or
/// unreadable — <see cref="Note"/> says why); <see cref="OnDiskFormId"/> is the 4-byte value a <c>.seq</c> must
/// contain for this quest (0 when it couldn't be computed). The check keys off <see cref="DefiningPlugin"/>;
/// <see cref="WinnerPlugin"/> is the plugin whose record the game actually reads. When they differ an override is in
/// play, and the render must soften a not-covered verdict to an ambiguity rather than assert "dormant" against the
/// defining plugin — the override may itself be the plugin that flags SGE and needs its own .seq.</summary>
public sealed record SeqLintFinding(
    bool QuestIsSge, string DefiningPlugin, string WinnerPlugin, uint OnDiskFormId,
    bool SeqExists, bool? SeqContainsQuest, bool? SeqNewerThanPlugin, string? Note);

/// <summary>The whole-validation report for one <c>housecarl_validate_dialogue</c> call: the resolved input
/// (<see cref="Input"/>, <see cref="InputKind"/> = "topic"/"quest"/"view"/"branch"/"error",
/// <see cref="InputEditorId"/>) and the per-topic validations. A top-level recoverable miss (the FormID isn't in
/// the order, or resolves to none of the four input types) is a NAMED <see cref="Error"/>; a mid-run throw is
/// surfaced on <see cref="CheckError"/> — never a silent empty pass.
/// <see cref="ReadIncomplete"/> carries the asset-layer caveat (a BSA that failed to read, so an "absent"
/// voice/.pex may merely be unscanned). <see cref="InputIssues"/> carries INPUT-LEVEL findings that belong to the
/// input record itself, not to any one topic: the QUST ANAM/objective-FNAM CK-parity gaps (checked once — a
/// multi-topic quest is never nagged N times) and the DLVW/DLBR CK-parity gaps (those inputs have no Topics at
/// all — Topics stays empty and the render says what WAS checked).</summary>
public sealed record DialogueValidationReport(
    FormKey Input, string InputKind, string? InputEditorId, string? InputWinnerPlugin,
    IReadOnlyList<TopicValidation> Topics)
{
    public string? Error { get; init; }
    public string? CheckError { get; init; }
    public bool ReadIncomplete { get; init; }

    /// <summary>Input-level findings that belong to the INPUT record itself, not any one topic: quest CK-parity gaps
    /// (ANAM / objective FNAM, checked once per QUEST input) and the DLVW/DLBR CK-parity gaps (the whole finding set
    /// for those input kinds). Empty for a DIAL input and for a gap-free record.</summary>
    public IReadOnlyList<DialogueIssue> InputIssues { get; init; } = Array.Empty<DialogueIssue>();

    /// <summary>Plugins the topic fan-out could not read, one sentence each. NOT a finding about the input record,
    /// so deliberately not in <see cref="InputIssues"/>: that is the CK-parity channel, and a file lock is not a
    /// parity failure. It bounds what the report covers — a report carrying one lists fewer topics than the order
    /// really holds, so no render may state "owns none" over it.</summary>
    public IReadOnlyList<string> ScanGaps { get; init; } = Array.Empty<string>();

    /// <summary>The SEQ staleness/coverage lint, set only for a Start-Game-Enabled QUEST input; null otherwise
    /// (a non-SGE quest, or a DIAL input — a topic isn't a quest, so a .seq isn't its concern).</summary>
    public SeqLintFinding? SeqLint { get; init; }

    /// <summary>The FormID isn't in the active order, or resolves to none of the four input types (DIAL, QUST,
    /// DLVW, DLBR) — a recoverable, named error the tool renders as guidance, not a thrown failure.</summary>
    public static DialogueValidationReport ForError(FormKey fk, string error) =>
        new(fk, "error", null, null, Array.Empty<TopicValidation>()) { Error = error };

    /// <summary>The validate threw mid-run (a resolve/asset failure) — surfaced, never silently swallowed.</summary>
    public static DialogueValidationReport ForCheckError(FormKey fk, string err) =>
        new(fk, "error", null, null, Array.Empty<TopicValidation>()) { CheckError = err };
}

public static class DialogueValidate
{
    /// <summary>The type filter for the quest fan-out scan — every winning DialogTopic (DIAL) in the order.</summary>
    static readonly Type[] DialTypes = { typeof(IDialogTopicGetter) };

    /// <summary>The one home for rendering a <see cref="CkParityGap"/> as a validator finding, always a Warning —
    /// every member of the family is a CK-editor / byte-parity shape the game itself tolerates. All four gap
    /// surfaces (per-INFO, quest InputIssues, DLVW, DLBR) go through this so they cannot drift apart.</summary>
    static DialogueIssue GapIssue(string noun, FormKey fk, CkParityGap gap) =>
        new(DialogueIssueSeverity.Warning, $"{noun} {fk} is missing the {gap.Subrecord} subrecord — {gap.Detail}");

    /// <summary>Resolve <paramref name="fk"/> to its load-order winner and validate the dialogue graph: a DIAL →
    /// validate that one topic; a QUST → fan out to EVERY topic the quest owns (a whole-order DIAL winner scan,
    /// filtered by DialogTopic.Quest, because a topic points UP at its quest — the quest holds no topic list) plus
    /// the quest's own CK-parity gaps (ANAM / objective FNAM) as InputIssues; a DLVW or DLBR → a RECORD-LEVEL
    /// CK-parity check (the DNAM/ENAM and TNAM subrecords DialogueCkParity fills on create — no topic graph to
    /// walk, so Topics stays empty and the render names the narrower scope). Builds the load-order winner
    /// <c>Resolve</c> closure (each INFO's Speaker → NPC → VoiceType, and the topic's Quest) + the asset view off
    /// the live resolvers, and opens ONE overlay session for the run. NEVER throws: a recoverable miss (not in the
    /// order, or none of the four input types) is a NAMED <see cref="DialogueValidationReport.Error"/>; a mid-run
    /// throw rides <see cref="DialogueValidationReport.CheckError"/>, never a silent empty pass.</summary>
    /// <summary>The effective merged INFO order for a set of topics, off an ALREADY-CAPTURED view and open
    /// session — one build for the whole batch, shared with Run's own per-topic order. Per topic, every touching
    /// plugin's child list is loaded in ONE typed DIAL pass per plugin (never a per-(topic,plugin) whole-overlay
    /// scan), unreadable contributors are CARRIED as data rather than silently dropped (the Complete /
    /// BaselineTrusted gates), and the merge runs on plain data after the overlays are gone.</summary>
    public static Dictionary<FormKey, InfoOrderView> InfoOrders(
        LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session, IReadOnlyCollection<FormKey> topicFks)
    {
        // The fallback resolver serves a PNAM target that appears in none of the topic's own lists (rare);
        // targets within the topic are served from the loaded lines, so this per-record lookup is almost
        // never reached.
        var loCache = new Dictionary<FormKey, IMajorRecordGetter?>();
        (InfoLine Line, string Plugin)? ResolveInfo(FormKey k)
        {
            if (k.IsNull || view.ResolveWinner(k) is not { } w) return null;
            if (!loCache.TryGetValue(k, out var g))
                loCache[k] = g = view.GetRecord(session, w.WinnerPlugin, k);
            return g is IDialogResponsesGetter r
                ? (DialogueInfoOrder.LineOf(r), w.WinnerPlugin)      // the ONE projection, shared with LinesOf
                : null;
        }

        var touchingOf = new Dictionary<FormKey, IReadOnlyList<string>>();
        var wantedIn = new Dictionary<string, HashSet<FormKey>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tfk in topicFks)
        {
            if (view.TouchingPlugins(tfk) is not { } touching) continue;
            touchingOf[tfk] = touching;
            foreach (var p in touching)
            {
                if (!wantedIn.TryGetValue(p, out var set)) wantedIn[p] = set = new HashSet<FormKey>();
                set.Add(tfk);
            }
        }

        // Keyed plugin-name -> topic -> lines, with the OUTER dictionary case-insensitive to match wantedIn and
        // TouchingPlugins. A flat (string, FormKey) tuple key would compare its string ORDINALLY, so a plugin
        // whose name reached the store and the lookup with different casing would miss — silently dropping that
        // plugin from the topic's merge.
        var lines = new Dictionary<string, Dictionary<FormKey, IReadOnlyList<InfoLine>>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (plugin, wanted) in wantedIn)
        {
            var perTopic = new Dictionary<FormKey, IReadOnlyList<InfoLine>>();
            // A plugin that cannot be read now (moved, or held open by another program) leaves its topics
            // unread, which the absentee accounting below states — never a silently thinner merge.
            try
            {
                foreach (var (rfk, _, body, _) in view.RecordsIn(new[] { plugin }, DialTypes))
                    if (wanted.Contains(rfk) && body is IDialogTopicGetter dt)
                        perTopic[rfk] = DialogueInfoOrder.LinesOf(dt);
            }
            catch (PluginUnreadableException) { perTopic.Clear(); }
            lines[plugin] = perTopic;
        }

        var built = new Dictionary<FormKey, InfoOrderView>();
        foreach (var (tfk, touching) in touchingOf)
        {
            var groups = new List<(string, IReadOnlyList<InfoLine>)>(touching.Count);
            var unread = new List<string>();
            foreach (var p in touching)
            {
                if (lines.TryGetValue(p, out var perTopic) && perTopic.TryGetValue(tfk, out var l))
                    groups.Add((p, l));
                else
                    unread.Add(p);                  // the index says it TOUCHES this topic — see below
            }

            // A plugin moved or locked by MO2/xEdit mid-call contributes no lines — and if that drop leaves
            // one contributor the render would claim "single plugin,
            // nothing merges here" for a genuinely contested topic, hiding the very reorder this exists to
            // surface. Carry the absentees so the note can state it.
            // Built UNCONDITIONALLY, including when nothing read: gating on groups.Count > 0 would leave
            // InfoOrder null on a total drop and the render would return before saying anything. Safe because
            // TouchingPlugins never yields an empty list, so no groups implies unread is non-empty implies
            // Complete is false, and the incomplete render branch reads only counts — it never indexes
            // ContributingPlugins.
            //
            // The move baseline is the first contributing group with a NON-EMPTY list, so it is trustworthy only
            // if no unread plugin sits BEFORE that plugin in load order. Testing `touching[0] is unread` is too
            // weak: a first plugin that read but carries an empty child list contributes no baseline, so an
            // unread SECOND plugin still shifts it.
            int firstWithLines = groups.FindIndex(g => g.Item2.Count > 0);
            string? baselinePlugin = firstWithLines >= 0 ? groups[firstWithLines].Item1 : null;
            bool baselineTrusted = unread.Count == 0
                || (baselinePlugin is not null
                    && !touching.TakeWhile(p => !p.Equals(baselinePlugin, StringComparison.OrdinalIgnoreCase))
                                .Any(p => unread.Contains(p, StringComparer.OrdinalIgnoreCase)));

            built[tfk] = DialogueInfoOrder.Compute(groups, ResolveInfo, unread, baselineTrusted);
        }
        return built;
    }

    public static DialogueValidationReport Run(LoadOrderResolver resolver, AssetResolver assets, FormKey fk)
    {
        try
        {
            var view = resolver.Capture();                       // pin ONE index build for the whole validation
            using var session = resolver.OpenSession();          // one set of overlays, disposed at run end
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


            // Load every needed topic's per-plugin child lists in ONE typed DIAL pass per contributing plugin.
            // Deliberately NOT view.GetRecord per (topic, plugin): that is an UNINDEXED whole-overlay scan, so a
            // quest fan-out would rescan a big master (Skyrim.esm, USSEP) once per topic — hundreds of full scans
            // for one call. Typed enumeration walks the DIAL group only, and one pass serves every topic at once.
            // Lines are PROJECTED inside the loop, while each body is still live (consume-before-advance), so the
            // merge itself runs on plain data after the overlays are gone.
            Dictionary<FormKey, InfoOrderView> OrdersFor(IReadOnlyCollection<FormKey> topicFks)
                => InfoOrders(view, session, topicFks);


            var win = view.ResolveWinner(fk);
            if (win is null)
                return DialogueValidationReport.ForError(fk,
                    $"{fk} is not in the active load order — nothing to validate. Pass a dialogue topic (DIAL) FormID to validate one topic, a quest (QUST) FormID to validate all of a quest's topics, or a dialogue view (DLVW) / branch (DLBR) FormID for a record-level CK-parity check.");

            var body = view.GetRecord(session, win.Value.WinnerPlugin, fk);
            if (body is null)
                return DialogueValidationReport.ForError(fk,
                    $"{fk} resolves to a winner in {win.Value.WinnerPlugin} but its body could not be fetched (the plugin may have changed since the index was built) — re-run to rebuild and try again.");

            if (body is IDialogTopicGetter topic)
            {
                var tv = ValidateTopic(topic, win.Value.WinnerPlugin, InOrder, Resolve, av)
                    with { InfoOrder = OrdersFor(new[] { fk }).GetValueOrDefault(fk) };
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
                // SEQ staleness/coverage lint: keyed on the QUEST input, independent of its topics — a
                // start-game-enabled quest needs a .seq that lists it, or it (and all its dialogue) stays dormant.
                var seqLint = CheckSeq(view, av, fk, quest, win.Value.WinnerPlugin);

                var topics = new List<TopicValidation>();
                // A plugin that cannot be read now contributes no topics to the fan-out. Collected so the sweep
                // covers the rest of the order, and named below — the same never-a-silently-thinner-answer rule the
                // per-topic unread accounting follows.
                var unreadable = new List<PluginUnreadableException>();
                foreach (var (tfk, _, tbody) in view.WinnerRecordsOfType(DialTypes, unreadable))
                {
                    if (tbody is not IDialogTopicGetter dt) continue;
                    if (NonNull(dt.Quest.FormKeyNullable) is not { } qk || qk != fk) continue;
                    var wp = view.ResolveWinner(tfk)?.WinnerPlugin ?? win.Value.WinnerPlugin;
                    topics.Add(ValidateTopic(dt, wp, InOrder, Resolve, av));
                }

                // Effective INFO order for EVERY topic in one batch, built AFTER the winner scan closes — never
                // inside it: that scan owns its overlay under the consume-before-advance contract, while this opens
                // other plugins' overlays. An InfoOrderView holds only FormKeys, names and indices (no overlay-backed
                // body), so building it late is safe where reading a body late would not be. Batched so the whole
                // fan-out costs one typed pass per contributing plugin, not one per (topic, plugin).
                var orders = OrdersFor(topics.Select(t => t.Topic).ToList());
                for (int i = 0; i < topics.Count; i++)
                    topics[i] = topics[i] with { InfoOrder = orders.GetValueOrDefault(topics[i].Topic) };

                // Quest-level CK-parity gaps (ANAM / objective FNAM) — checked ONCE on the winning quest record and
                // surfaced as InputIssues, never per topic (a multi-topic quest would repeat the same quest gap N
                // times). The absence tests are DialogueCkParity's — the same ones ApplyQuestDefaults fills
                // through, so fill and check cannot drift. Byte-parity, so Warning. PRESENCE only: the validator
                // never judges the ANAM VALUE (an edited quest's ANAM is a legitimate CK high-water mark).
                var questGaps = DialogueCkParity.MissingQuestDefaults(quest)
                    .Select(g => GapIssue("Quest", fk, g)).ToList();
                // A plugin the fan-out could not read may own topics of this quest, so the topic list below it is
                // incomplete. Its own carrier, not the parity channel: a file lock is not a CK-parity failure.
                var scanGaps = unreadable
                    .Select(u => $"{u.Message} Any topic of this quest that plugin owns is missing from this report.")
                    .ToList();

                return new DialogueValidationReport(fk, "quest", quest.EditorID ?? "", win.Value.WinnerPlugin, topics)
                    { ReadIncomplete = av.ReadIncomplete, SeqLint = seqLint, InputIssues = questGaps, ScanGaps = scanGaps };
            }

            // DLVW / DLBR inputs: a RECORD-LEVEL CK-parity check — these carry no INFO list, so there is no topic
            // graph/voice/script surface to walk; the finding set IS the CK-parity gaps (the same subrecords
            // DialogueCkParity fills on create, via the same shared presence predicates, so they cannot drift).
            // Topics stays empty; the render names the narrower scope so a clean pass never reads as a graph
            // validation.
            if (body is IDialogViewGetter dlvw)
            {
                var gaps = DialogueCkParity.MissingViewDefaults(dlvw)
                    .Select(g => GapIssue("DialogView", fk, g)).ToList();
                return new DialogueValidationReport(fk, "view", dlvw.EditorID ?? "", win.Value.WinnerPlugin,
                    Array.Empty<TopicValidation>()) { InputIssues = gaps };
            }

            if (body is IDialogBranchGetter dlbr)
            {
                var gaps = DialogueCkParity.MissingBranchDefaults(dlbr)
                    .Select(g => GapIssue("DialogBranch", fk, g)).ToList();
                return new DialogueValidationReport(fk, "branch", dlbr.EditorID ?? "", win.Value.WinnerPlugin,
                    Array.Empty<TopicValidation>()) { InputIssues = gaps };
            }

            return DialogueValidationReport.ForError(fk,
                $"{fk} resolves to a {RecordNaming.StripOverlay(body.GetType().Name)} in {win.Value.WinnerPlugin}, not a dialogue topic (DIAL), quest (QUST), dialogue view (DLVW), or dialogue branch (DLBR). Pass a DIAL FormID to validate one topic, a QUST FormID to validate every topic a quest owns, or a DLVW/DLBR FormID for a record-level CK-parity check.");
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
    /// here for dangling targets.
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

        // Text-encoding lint: flag non-ASCII in the player-facing strings — the topic Name, each INFO Prompt, and
        // each spoken response Text — because the CK/Papyrus user-facing text surface is effectively
        // Windows-1252/ASCII, so an em-dash, ellipsis, or smart quote renders as in-game mojibake. WARN only (a
        // heuristic — HTML/Ultralight UIs render Unicode fine) and report-only: this validator never mutates.
        CheckEncoding(topic.Name?.String, $"DialogTopic.Name ({edid})", issues);

        // Classify a SET reference: cheap-existence first (a dangling/missing target needs no body fetch), then a body
        // fetch ONLY for the rarer present-but-wrong-type case (so the message names what it actually is). Returns null
        // when the reference is fine, else the clause describing what's wrong — "missing or disabled" vs "resolves to a
        // Weapon", sharper than one "doesn't resolve", and it skips most full-plugin enumerations.
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

        // --- BNAM absent on a Custom topic: a Custom (player-authored) topic with NO Branch back-link is byte-valid
        //     and plays fine in game, but the Creation Kit's Dialogue Views editor auto-wraps a branch-less topic in
        //     a container branch and then null-derefs rendering the flowchart (a CKPE FlowchartX64 crash). BNAM is
        //     AUTHOR-SUPPLIED — houseCARL cannot reliably derive which branch a topic belongs to — so this is a WARN
        //     rather than an auto-fill. Gated on Custom because the crash is the player-topic / dialogue-views case;
        //     system subtypes (Hello/Goodbye/combat) don't live in views.
        if (topic.Subtype == DialogTopic.SubtypeEnum.Custom && branchFk is null)
            issues.Add(new(DialogueIssueSeverity.Warning,
                "DialogTopic.Branch (BNAM) is unset on this Custom topic — it plays fine in game, but the Creation "
                + "Kit's Dialogue Views editor auto-wraps a branch-less topic in a container branch and then crashes "
                + "rendering the flowchart (FlowchartX64). Set Branch to the owning DialogBranch (DLBR) before opening "
                + "this topic in the CK."));

        // --- SNAM subtype marker: a blank one (0000) is malformed — the engine buckets topics by this 4-char marker.
        //     Severity is scoped by whether the winning record originates here or is an override:
        //       • Own/new topic (winner == the FormKey's defining master): a blank marker is a load CTD → Problem.
        //       • Override of a topic defined in a master: the base record's marker plausibly still applies, and a
        //         blank-SNAM override has been observed shipping in a working, actively-played mod — malformed but
        //         not a confirmed crash → Warning. Don't cry "guaranteed CTD" over working content.
        //     The blank test is DialogueSubtype's, shared with the create-path auto-fill so they can't drift, and the
        //     expected marker is named so the fix is a copy-paste rather than a bare "invalid".
        if (DialogueSubtype.IsBlankMarker(topic.SubtypeName))
        {
            var expected = DialogueSubtype.MarkerFor((int)topic.Subtype);
            bool isOverride = !string.Equals(topic.FormKey.ModKey.FileName.String, winnerPlugin, StringComparison.OrdinalIgnoreCase);
            var fix = expected is not null
                ? $"Set it to {expected} (the marker for Subtype={topic.Subtype}); houseCARL's create tools now auto-fill it, or {ToolNames.Apply} on SubtypeName with value={expected}."
                : $"Set it to the correct 4-char marker for Subtype={topic.Subtype} via {ToolNames.Apply} on SubtypeName.";
            issues.Add(isOverride
                ? new(DialogueIssueSeverity.Warning,
                    $"DialogTopic.SubtypeName (the SNAM subtype marker) is empty (0000) on this OVERRIDE of {topic.FormKey.ModKey.FileName} — "
                    + "the game buckets topics by this 4-char marker; the base record's marker may still apply (a blank-SNAM override ships in "
                    + $"some working mods), but the record is malformed. {fix}")
                : new(DialogueIssueSeverity.Problem,
                    "DialogTopic.SubtypeName (the SNAM subtype marker) is empty (0000) — the game buckets topics by this 4-char marker, "
                    + $"and this plugin DEFINES the topic, so a blank marker is a load CTD on load (#131); the record is malformed. {fix}"));
        }

        // Static condition lints need the owning quest's reference-alias IDs — resolved ONCE here off the load-order
        // WINNER quest, because an alias index in a condition is quest-relative, so it's the owning quest's set that
        // decides whether the index is live. NULL when the quest is unset or unresolvable (already surfaced above):
        // then the alias-index lints are SKIPPED rather than guessed, never a false "dead alias" against a quest we
        // couldn't read. Non-alias condition lints don't need this and run regardless.
        HashSet<uint>? ownerAliasIds = null;
        // The EditorIDs of the globals in the owning quest's TextDisplayGlobals list — the set a `<Global=X>`
        // dialogue-text tag must name to render in game. NULL when the owning quest is unresolvable, and then the tag
        // lint is skipped rather than guessed, exactly as the alias-index lints skip.
        HashSet<string>? ownerTextGlobals = null;
        string ownerQuestLabel = "the owning quest";
        if (questFk is { } ownerFk && resolve(ownerFk) is IQuestGetter ownerQuest)
        {
            ownerAliasIds = ownerQuest.Aliases.Select(a => a.ID).ToHashSet();
            // Resolve each TextDisplayGlobals FormLink to its GLOB and collect the EditorID (case-insensitive — the engine's
            // tag match is). A null/unresolvable entry contributes no name (it can't cover a tag anyway); globals resolve
            // reliably, so this doesn't spuriously mark a real one absent.
            ownerTextGlobals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in ownerQuest.TextDisplayGlobals)
                if (!g.FormKey.IsNull && resolve(g.FormKey) is IGlobalGetter glob && glob.EditorID is { Length: > 0 } gid)
                    ownerTextGlobals.Add(gid);
            ownerQuestLabel = $"the owning quest {ownerQuest.EditorID ?? ownerFk.ToString()}";
        }

        // --- Per-INFO walk over the topic's LIVE INFOs. A deleted INFO is a REMOVED line — skip it entirely (don't
        //     count it, chain it, or voice/script-check it), but tally it so the render can say "N skipped".
        //     InfoCount is INFO RECORDS, not spoken rows (one INFO can carry several DialogResponse rows, or none).
        int infoCount = 0, conditioned = 0, deleted = 0, fragmentInfos = 0;
        foreach (var info in topic.Responses)
        {
            if (info.IsDeleted) { deleted++; continue; }
            infoCount++;

            // CK-parity subrecords (INFO CNAM/ENAM): a winning INFO missing the FavorLevel (CNAM) or response-Flags
            // (ENAM) subrecord that Mutagen omits when unset is the shape that crashes the Creation Kit when this
            // topic is opened in the dialogue editor (the game itself tolerates it → WARN, matching the BNAM lint).
            // The absence test is DialogueCkParity's — the SAME one the create path fills through — so the fill and
            // this check can never drift. Real CK/xEdit-authored INFOs always carry both, so this fires only on a
            // bare-authored record. (The rest of the CK-parity family lives elsewhere: DLVW DNAM/ENAM and DLBR TNAM
            // are their own input kinds, QUST ANAM/objective FNAM rides the quest input's InputIssues, and DIAL PNAM
            // is the one permanent boundary — a non-nullable float with no "unset" signal.)
            foreach (var gap in DialogueCkParity.MissingInfoDefaults(info))
                issues.Add(GapIssue("INFO", info.FormKey, gap));

            // Does this line carry a result-script fragment (a code path that can surface in Papyrus.log)? Via the
            // single fragment-presence home, so this never drifts from the per-INFO HasFragment the script check sets.
            if (DialogueScriptCheck.HasResultFragment(info)) fragmentInfos++;

            // Text-encoding lint over this line's player-facing strings: its menu Prompt and each spoken row.
            CheckEncoding(info.Prompt?.String, $"INFO {info.FormKey} Prompt", issues);
            int rnum = 0;
            foreach (var resp in info.Responses)
                CheckEncoding(resp.Text?.String, $"INFO {info.FormKey} response {++rnum} text", issues);

            // A `<Global=X>` tag in a Prompt/response renders as `[...]` in game unless X names a global in the owning
            // quest's TextDisplayGlobals. A silent failure — the record is byte-valid and the tag just fails to
            // substitute — so it's a WARN. Skipped, never guessed, when the owning quest is unresolvable.
            if (ownerTextGlobals is not null)
                CheckGlobalTags(info, ownerTextGlobals, ownerQuestLabel, issues);

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

            if (info.Conditions.Count > 0)
            {
                conditioned++;
                // Static condition (CTDA) well-formedness lints — the data-layer-decidable, true-positive subset.
                // They catch MALFORMED conditions; they do not and cannot evaluate whether a well-formed one passes,
                // which stays the running game's job (the standing CTDA limit, restated in the render).
                CheckConditions(info, ownerAliasIds, ownerQuestLabel, inOrder, resolve, issues);
            }

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

    /// <summary>SEQ staleness/coverage lint for a QUEST input: if the quest is Start-Game-Enabled, does its DEFINING
    /// plugin have a <c>.seq</c> that LISTS it (by its on-disk FormID) and is NEWER than the plugin? An SGE quest with
    /// a missing / non-listing / stale <c>.seq</c> is dormant on a fresh save — its dialogue never shows. Returns null
    /// for a non-SGE quest (no <c>.seq</c> needed, no lint). Fault-isolated: any IO/parse failure yields a NAMED note
    /// rather than a throw, so a SEQ-check failure can't sink the whole validation. The winning <c>.seq</c> is
    /// resolved via the VFS (loose beats BSA); a BSA-resident <c>.seq</c> has no loose path, so its contents and mtime
    /// are undeterminable here and are surfaced as a note, never a false "OK" or a false "stale". The check keys off
    /// the DEFINING plugin, which is correct for a quest authored SGE in its own plugin and for a vanilla override
    /// that keeps SGE (the base .seq already lists it). It carries <paramref name="winnerPlugin"/> too so the render
    /// can soften to an ambiguity rather than assert "dormant" against the defining plugin when the winning record is
    /// an override — that override may itself be the plugin that flags SGE and would need its own .seq.</summary>
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
    /// missing marker reads as a fact, never as a blank. The blank test is <see cref="DialogueSubtype.IsBlankMarker"/>,
    /// shared with the create-path auto-fill and the Problem escalation so all three agree on "no marker".</summary>
    static string DescribeSubtypeName(RecordType rt) => DialogueSubtype.IsBlankMarker(rt) ? "<none>" : rt.Type;

    /// <summary>A nullable FormLink's target as a real FormKey, or null when the link is unset OR explicitly Null
    /// (00000000) — both mean "no target". Mirrors <see cref="VoiceCheck"/>'s sibling so the two read links the
    /// same way.</summary>
    static FormKey? NonNull(FormKey? fk) => fk is { } v && !v.IsNull ? v : null;

    /// <summary>The common non-ASCII offenders with a known ASCII substitute, for the encoding lint's suggestion.
    /// Any OTHER non-ASCII char is still flagged, just without a substitution.</summary>
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

    /// <summary>Text-encoding lint: if <paramref name="s"/> carries any non-ASCII char (&gt; 0x7F), add ONE WARNING
    /// for this <paramref name="locus"/> naming the offending char(s) and the ASCII substitute where known. The
    /// CK/Papyrus user-facing surface is effectively Windows-1252/ASCII, so these usually render as in-game mojibake.
    /// WARN, never blocks (heuristic), and report-only — the validator never rewrites the string. The message names
    /// exactly which characters and what to use, never a bare "invalid".</summary>
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

    /// <summary>The <c>&lt;Global=X&gt;</c> text-replacement tags in a Prompt/response — the engine substitutes each
    /// with the named global's value at runtime. Group 1 is the global's EditorID (<c>[^&lt;&gt;]+</c> — everything up
    /// to the closing bracket, trimmed by the caller). The optional <c>(?:\.\w+)?</c> covers the CK's formatting-subtag
    /// variants — <c>&lt;Global.Time=X&gt;</c>, <c>&lt;Global.Hour12=X&gt;</c>, <c>&lt;Global.Minutes=X&gt;</c> — where
    /// the modifier sits BEFORE the <c>=</c> and the EditorID after it (CK wiki: Text Replacement). Those name a global
    /// that ALSO must be in the quest's TextDisplayGlobals, so the lint must catch a missing one there too — a plain
    /// <c>&lt;Global=</c> anchor would silently skip them, which is the exact failure this lint exists to catch.
    /// Case-insensitive on the tag word.</summary>
    static readonly Regex GlobalTagRx = new(@"<Global(?:\.\w+)?=([^<>]+)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary><c>&lt;Global=X&gt;</c> text-replacement lint: scan this INFO's player-facing strings (menu Prompt +
    /// each spoken response) for <c>&lt;Global=X&gt;</c> tags and WARN on any X that is NOT the EditorID of a global in
    /// the owning quest's <see cref="IQuestGetter.TextDisplayGlobals"/> — such a tag renders as <c>[...]</c> in game,
    /// a silent failure the record's byte-validity hides. WARN and report-only (the validator never rewrites); one
    /// WARN per distinct missing name per INFO. The caller invokes this only when the owning quest resolved, so
    /// <paramref name="ownerTextGlobals"/> is the real set — possibly EMPTY, which correctly means every tag is
    /// uncovered — never a guess.</summary>
    static void CheckGlobalTags(IDialogResponsesGetter info, HashSet<string> ownerTextGlobals, string ownerQuestLabel, List<DialogueIssue> issues)
    {
        var flagged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // one WARN per distinct missing name per INFO
        void Scan(string? text, string locus)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (Match m in GlobalTagRx.Matches(text))
            {
                var name = m.Groups[1].Value.Trim();
                if (name.Length == 0 || ownerTextGlobals.Contains(name) || !flagged.Add(name)) continue;
                issues.Add(new(DialogueIssueSeverity.Warning,
                    $"INFO {info.FormKey} {locus} uses the text-replacement tag {m.Value}, but {name} is not a "
                    + $"global in {ownerQuestLabel}'s TextDisplayGlobals — in game the tag renders as [...] (the global is "
                    + $"never substituted). Add {name} to the quest's Text Display Globals."));
            }
        }
        Scan(info.Prompt?.String, "Prompt");
        int rnum = 0;
        foreach (var resp in info.Responses) Scan(resp.Text?.String, $"response {++rnum} text");
    }

    // Engine-implicit forms (PlayerRef 000014, Player 000007) — the sub-0x800 hardcoded references the index can't
    // resolve — are exempted from condition lints 1 + 3 so a standard player-state gate validates clean. The precise
    // set + rationale live in the ONE shared home (EngineImplicit), also used by the check_errors integrity sweep.

    /// <summary>Static condition-lint suite over one INFO's <c>Conditions</c> (CTDA rows) — the data-layer-decidable
    /// subset. Every lint here is a TRUE-positive STRUCTURAL defect (a malformed condition), never a behavioural
    /// guess: houseCARL cannot EVALUATE whether a well-formed condition passes — only the running game can (the
    /// standing CTDA limit, restated in the render). All emit WARNING, never block: a dead gate misfires that one
    /// line, it doesn't break the topic's chain, and a few load-order shapes are legitimately edge-y.
    ///
    /// The lints (each decidable from the record + the form index alone):
    ///   1. Run On a specific Reference with NO reference set — the gate evaluates against nothing.
    ///   2. A DEAD ALIAS INDEX — Run On:QuestAlias, or a GetIsAliasRef / GetInCurrentLocAlias param, naming an alias
    ///      ID the OWNING quest does not define. SKIPPED (never guessed) when the owning quest is unknown (ownerAliasIds null).
    ///   3. A DANGLING form parameter — any condition-function form target (a plain FormLink, or a FORM-mode
    ///      FormLinkOrIndex) pointing at a form not in the active load order. Generic by construction (reflection over
    ///      the Data arm's properties), so it covers EVERY function Mutagen models, not a hand-listed subset —
    ///      cornerstone-consistent. The Run On Reference slot is excluded (lint 1 owns it), so an unused/parked
    ///      Reference is never mis-flagged; alias/package-data-mode FLOIs are gated out (see the per-condition note).
    ///   4. A DANGLING global comparison value — a ConditionGlobal compared against a GLOB not in the load order.
    ///   5. GetIsID pointed at a PLACED reference instead of a base object — GetIsID compares the run-on actor's BASE
    ///      form, so a placed-instance FormID can never match as intended.
    ///
    /// DELIBERATELY NOT LINTED (semantic, not structural — flagging them would be a behavioural guess that risks
    /// false positives, so they stay the author's call under the standing limit): Run On
    /// Subject-vs-Target INTENT (a player-shaped function left on Subject), the faction-rank gate VALUE (&lt; 0 vs == 0),
    /// and intra-topic Info-variant ORDERING (a specific line shadowed by a generic sibling). Scope is INFO conditions
    /// (the dialogue surface); a quest's own DialogConditions/alias conditions are out of this pass.</summary>
    internal static void CheckConditions(IDialogResponsesGetter info, HashSet<uint>? ownerAliasIds, string ownerQuestLabel,
        Func<FormKey, bool> inOrder, Func<FormKey, IMajorRecordGetter?> resolve, List<DialogueIssue> issues)
    {
        int n = 0;
        foreach (var cond in info.Conditions)
        {
            n++;
            var data = cond.Data;
            var fn = data.Function.ToString();
            var refKey = data.Reference.FormKey;   // the Run On reference slot — owned by lint 1, excluded from the param sweep

            // FLOI MODE GATE (the load-bearing one). A condition form-parameter is a FormLinkOrIndex (Quest, Object,
            // Perk, …): a FORM only when the arm is in form mode; in alias / package-data mode the SAME slot is an
            // INDEX. On the binary OVERLAY the validator reads (CreateFromBinaryOverlay), an index-mode FLOI's .Link
            // is a BOGUS low FormKey synthesised from the index bytes — NOT null — so reading it as a form would
            // false-flag a perfectly well-formed alias-mode gate (e.g. "run-on actor has the perk held by quest-alias
            // N"). Mirrors the write-side gate (WriteEngine's condition scan and ReadEngine.EmitFloi): treat an FLOI
            // as a form ONLY when UseAliases and UsePackageData are both false.
            bool floiIsForm = !data.UseAliases && !data.UsePackageData;

            // 1. Run On a specific Reference but none / a missing one is set — the function runs against nothing.
            if (data.RunOnType == Condition.RunOnType.Reference)
            {
                if (data.Reference.IsNull)
                    issues.Add(new(DialogueIssueSeverity.Warning,
                        $"INFO {info.FormKey} condition #{n} ({fn}) is set to Run On a specific reference, but no reference is set — it evaluates against nothing, so the gate never behaves as intended."));
                else if (!inOrder(refKey) && !EngineImplicit.IsImplicit(refKey))
                    issues.Add(new(DialogueIssueSeverity.Warning,
                        $"INFO {info.FormKey} condition #{n} ({fn}) Run On reference {refKey} is not in the active load order — the gate evaluates against nothing."));
            }

            // 2. Dead alias index — only when the owning quest's alias set is known; else skip, never guess.
            if (ownerAliasIds is not null)
            {
                if (data.RunOnType == Condition.RunOnType.QuestAlias && BadAlias(data.RunOnTypeIndex, ownerAliasIds))
                    issues.Add(AliasIssue(info.FormKey, n, fn, data.RunOnTypeIndex, "is set to Run On", "reference-alias", ownerQuestLabel));
                if (data is IGetIsAliasRefConditionDataGetter gar && BadAlias(gar.ReferenceAliasIndex, ownerAliasIds))
                    issues.Add(AliasIssue(info.FormKey, n, fn, gar.ReferenceAliasIndex, "references", "reference-alias", ownerQuestLabel));
                if (data is IGetInCurrentLocAliasConditionDataGetter gla && BadAlias(gla.LocationAliasIndex, ownerAliasIds))
                    issues.Add(AliasIssue(info.FormKey, n, fn, gla.LocationAliasIndex, "references", "location-alias", ownerQuestLabel));
            }

            // 3. Dangling form-link PARAMETER — generic, BY CONSTRUCTION: reflect over the Data arm's properties and
            //    take every form TARGET it carries — a plain FormLink (Faction, Spell, Keyword, FormList, …) AND a
            //    FORM-MODE FormLinkOrIndex (the condition union — Quest, GetIsID's Object, …; read via the write-side
            //    ReadFloiFormKey). An alias/package-data-mode FLOI is GATED OUT by floiIsForm (its index is not a form,
            //    and its overlay .Link is a bogus low key — see the mode-gate note above); the alias-INDEX paths it
            //    leaves are lint 2's int-property domain, and an alias-mode FLOI *parameter* is deliberately out of
            //    scope (a future extension, not a dangling-form case). The Run On Reference slot is skipped by name
            //    (lint 1 owns it). A param pointing at a form not in the active order is a dead reference. Mirrors the
            //    write engine's own FLOI handling, so it covers EVERY function Mutagen models — never a hand-listed subset.
            foreach (var p in data.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.Name == "Reference" || p.GetIndexParameters().Length != 0) continue;   // run-on ref → lint 1
                object? v; try { v = p.GetValue(data); } catch { continue; }
                // Branch 1 (plain FormLink) is defensive: on a ConditionData arm the ONLY plain FormLink is the
                // (skipped) run-on Reference — every function TARGET is the FormLinkOrIndex union (branch 2). A FLOI
                // is read via its .Link, not as an IFormLinkGetter, so it never falls into branch 1 and bypasses the
                // mode gate. Branch 1 has no index mode anyway, so it needs no gate; it correctly handles a
                // plain-FormLink param should any arm ever carry one.
                FormKey? paramFk =
                    v is IFormLinkGetter fl && !fl.IsNull ? fl.FormKey
                    : floiIsForm && WriteEngine.IsFormLinkOrIndex(p.PropertyType) ? WriteEngine.ReadFloiFormKey(v) : null;
                if (paramFk is { } pk && !inOrder(pk) && !EngineImplicit.IsImplicit(pk))
                    issues.Add(new(DialogueIssueSeverity.Warning,
                        $"INFO {info.FormKey} condition #{n} ({fn}) references {pk}, which is not in the active load order — a deleted/disabled form or a wrong FormID, so the condition can't evaluate as intended."));
            }

            // 4. Dangling global comparison value — a ConditionGlobal compared against a GLOB not in the order. The
            //    comparison value lives on the Condition, not the Data arm, so it's outside the param sweep above.
            if (cond is IConditionGlobalGetter cg && !cg.ComparisonValue.IsNull && !inOrder(cg.ComparisonValue.FormKey))
                issues.Add(new(DialogueIssueSeverity.Warning,
                    $"INFO {info.FormKey} condition #{n} ({fn}) compares against global {cg.ComparisonValue.FormKey}, which is not in the active load order."));

            // 5. GetIsID pointed at a PLACED reference — the wrong KIND of form (a dangling one is already caught by
            //    lint 3). GetIsID compares the run-on actor's BASE form, so a placed-instance FormID can never match.
            //    Gated on floiIsForm too: an alias-mode GetIsID.Object is an index, not a form to resolve (without the
            //    gate its bogus overlay .Link could trip resolve()); a form-mode Object is the real base-vs-placed case.
            if (data is IGetIsIDConditionDataGetter gid && floiIsForm && WriteEngine.ReadFloiFormKey(gid.Object) is { } objFk
                && inOrder(objFk) && resolve(objFk) is IPlacedGetter)
                issues.Add(new(DialogueIssueSeverity.Warning,
                    $"INFO {info.FormKey} condition #{n} (GetIsID) points at the placed reference {objFk}, but GetIsID compares the run-on actor's BASE form — pass the base NPC_/object, not a placed instance."));
        }
    }

    /// <summary>An alias index is dead when it's negative or not one of the owning quest's reference-alias IDs (the
    /// alias <c>ID</c> is a <c>uint</c>; a negative condition index can never be a real ID).</summary>
    static bool BadAlias(int idx, HashSet<uint> ownerAliasIds) => idx < 0 || !ownerAliasIds.Contains((uint)idx);

    /// <summary>The dead-alias-index warning (lint 2), worded so it names the function, the offending index, and the
    /// owning quest — never a bare "invalid".</summary>
    static DialogueIssue AliasIssue(FormKey infoFk, int n, string fn, int idx, string verb, string aliasKind, string ownerQuestLabel) =>
        new(DialogueIssueSeverity.Warning,
            $"INFO {infoFk} condition #{n} ({fn}) {verb} {ownerQuestLabel}'s {aliasKind} #{idx}, but that quest defines no alias with that ID — the gate evaluates against nothing.");
}

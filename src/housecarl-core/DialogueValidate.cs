using System.Reflection;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

// DialogueValidate — the on-demand whole-topic dialogue-graph validator, over a topic resolved against the
// LOAD-ORDER WINNERS: quest and branch wiring, the INFO.LinkTo chain, a SET-but-dangling PNAM, INFO CK-parity, and
// the reused VoiceCheck/DialogueScriptCheck. What a clean pass means, what PNAM absence means, the standing limits
// and the deferred {plugin + masters} scope are contracts in docs/architecture/dialogue-validation.md. Never throws over a
// verify step: the whole run is wrapped, so a failure rides CheckError.

/// <summary>How serious a graph finding is; no "info" level, as facts ride <see cref="TopicValidation"/>.</summary>
public enum DialogueIssueSeverity { Problem, Warning }

/// <summary>One whole-topic graph finding, naming the offending FormKey and what is wrong.</summary>
public sealed record DialogueIssue(DialogueIssueSeverity Severity, string Message);

/// <summary>One topic's whole-graph validation; <see cref="InfoCount"/> counts INFO RECORDS, not spoken rows.</summary>
public sealed record TopicValidation(
    FormKey Topic, string TopicEditorId, string WinnerPlugin,
    int InfoCount, int ConditionedInfoCount, int DeletedInfoCount, int FragmentInfoCount,
    string Category, string Subtype, string SubtypeName,
    IReadOnlyList<DialogueIssue> Issues,
    IReadOnlyList<VoiceLine> VoiceLines,
    IReadOnlyList<VoiceUndetermined> VoiceUndetermined,
    IReadOnlyList<ScriptBindingFinding> ScriptFindings)
{
    /// <summary>The effective, merged INFO order for this topic — see <see cref="DialogueInfoOrder"/>.</summary>
    public InfoOrderView? InfoOrder { get; init; }

    /// <summary>True when the numeric <see cref="Subtype"/> and the SNAM marker name different subtypes.</summary>
    public bool SubtypeDisagreesWithMarker { get; init; }

    /// <summary>The record this validation read came from the FOLDED file.</summary>
    public bool WinnerIsFolded { get; init; }

    /// <summary>The subtype name the SNAM marker names — the honest label when the number is stale.</summary>
    public string SubtypeFromMarker { get; init; } = "";
}

/// <summary>The SEQ lint for a QUEST input: a Start-Game-Enabled quest needs a <c>.seq</c> that LISTS it and is
/// NEWER than its defining plugin, or it is dormant on a fresh save. The two bools are null where undeterminable,
/// and where <see cref="WinnerPlugin"/> differs the render must soften the verdict.</summary>
public sealed record SeqLintFinding(
    bool QuestIsSge, string DefiningPlugin, string WinnerPlugin, uint OnDiskFormId,
    bool SeqExists, bool? SeqContainsQuest, bool? SeqNewerThanPlugin, string? Note);

/// <summary>The whole-validation report for one call; a miss is a NAMED <see cref="Error"/>, a throw rides
/// <see cref="CheckError"/>.</summary>
public sealed record DialogueValidationReport(
    FormKey Input, string InputKind, string? InputEditorId, string? InputWinnerPlugin,
    IReadOnlyList<TopicValidation> Topics)
{
    public string? Error { get; init; }
    public string? CheckError { get; init; }
    public bool ReadIncomplete { get; init; }

    /// <summary>The input record itself came from the FOLDED file; provenance rides beside the name.</summary>
    public bool InputWinnerIsFolded { get; init; }

    /// <summary>Findings belonging to the INPUT record itself rather than to any one topic.</summary>
    public IReadOnlyList<DialogueIssue> InputIssues { get; init; } = Array.Empty<DialogueIssue>();

    /// <summary>Plugins the topic fan-out could not read; no render may state "owns none" over one.</summary>
    public IReadOnlyList<string> ScanGaps { get; init; } = Array.Empty<string>();

    /// <summary>The SEQ lint, set only for a Start-Game-Enabled QUEST input; null otherwise.</summary>
    public SeqLintFinding? SeqLint { get; init; }

    /// <summary>The FormID is not in the order, or is none of DIAL/QUST/DLVW/DLBR — a recoverable named error.</summary>
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

    /// <summary>The one home for rendering a <see cref="CkParityGap"/> as a finding, always a Warning.</summary>
    static DialogueIssue GapIssue(string noun, FormKey fk, CkParityGap gap) =>
        new(DialogueIssueSeverity.Warning, $"{noun} {FormIdToken.Of(fk)} is missing the {gap.Subrecord} subrecord — {gap.Detail}");

    /// <summary>The effective merged INFO order for a set of topics, off an ALREADY-CAPTURED view and session —
    /// one typed DIAL pass per plugin, and unreadable contributors CARRIED as data.</summary>
    public static Dictionary<FormKey, InfoOrderView> InfoOrders(
        LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session, IReadOnlyCollection<FormKey> topicFks,
        DialogueFold? fold = null)
    {
        // The fallback serves a PNAM target in none of the topic's own lists, so it is almost never reached.
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

        // Where this file would load, decided once against this build before anything is merged.
        fold?.PlaceIn(view);

        var touchingOf = new Dictionary<FormKey, IReadOnlyList<string>>();
        var wantedIn = new Dictionary<string, HashSet<FormKey>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tfk in topicFks)
        {
            if (view.TouchingPlugins(tfk) is not { } touching)
            {
                // A topic no active plugin touches: a fold that defines it IS the whole merge.
                if (fold?.Topic(tfk) is not null) touchingOf[tfk] = Array.Empty<string>();
                continue;
            }
            touchingOf[tfk] = touching;
            foreach (var p in touching)
            {
                if (!wantedIn.TryGetValue(p, out var set)) wantedIn[p] = set = new HashSet<FormKey>();
                set.Add(tfk);
            }
        }

        // plugin-name -> topic -> lines, case-insensitive: a flat tuple key would compare its string ordinally.
        var lines = new Dictionary<string, Dictionary<FormKey, IReadOnlyList<InfoLine>>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (plugin, wanted) in wantedIn)
        {
            var perTopic = new Dictionary<FormKey, IReadOnlyList<InfoLine>>();
            // A plugin that cannot be read now leaves its topics unread, which the accounting below states.
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

            // Built UNCONDITIONALLY: gating on groups.Count would leave InfoOrder null on a total drop. Safe
            // because no groups implies Complete is false, and the incomplete render branch reads only counts —
            // it never indexes ContributingPlugins. The move baseline is the first contributing group with a
            // NON-EMPTY list; the fold goes in by SlotIndex.
            if (fold?.Topic(tfk) is { } folded)
            {
                // A shadowed copy REPLACES the active copy's contribution: one list at that slot, not two.
                if (fold.PlacementKind == DialogueFold.Where3.ActiveSlot)
                    groups.RemoveAll(g => g.Item1.Equals(fold.Plugin, StringComparison.OrdinalIgnoreCase));
                int at = groups.FindIndex(g => view.OrderIndexOf(g.Item1) > fold.SlotIndex);
                groups.Insert(at < 0 ? groups.Count : at, (fold.Label, folded.Lines));
            }

            // The baseline is the DEFINING plugin's list, which the fold IS only when it defines this topic;
            // otherwise it is skipped as a candidate, because a master fold can land AHEAD of the definer.
            bool foldDefinesTopic = fold is not null
                && tfk.ModKey.FileName.String.Equals(fold.Plugin, StringComparison.OrdinalIgnoreCase);
            string? projectedGroup = foldDefinesTopic ? null : fold?.Label;
            int firstWithLines = groups.FindIndex(g => g.Item2.Count > 0
                                                    && !g.Item1.Equals(projectedGroup, StringComparison.OrdinalIgnoreCase));
            string? baselinePlugin = firstWithLines >= 0 ? groups[firstWithLines].Item1 : null;
            bool baselineTrusted = unread.Count == 0
                || (baselinePlugin is not null
                    && !touching.TakeWhile(p => !p.Equals(baselinePlugin, StringComparison.OrdinalIgnoreCase))
                                .Any(p => unread.Contains(p, StringComparer.OrdinalIgnoreCase)));

            built[tfk] = DialogueInfoOrder.Compute(groups, ResolveInfo, unread, baselineTrusted, projectedGroup)
                with { FoldedPlugin = fold?.Label, FoldedPlacement = fold?.Placement };
        }
        return built;
    }

    /// <summary>Resolve <paramref name="fk"/> to its winner and validate the dialogue graph: a DIAL is one topic,
    /// a QUST fans out to every topic it owns, a DLVW or DLBR is a record-level check. NEVER throws.</summary>
    /// <param name="pinned">a build a CALLER already pinned, so a sweep's seeds read the stamped one.</param>
    /// <param name="fold">ONE <see cref="DialogueFold.Open"/> fold: what it carries WINS, and the response says
    /// the frame is a projection.</param>
    public static DialogueValidationReport Run(LoadOrderResolver resolver, AssetResolver assets, FormKey fk,
                                               LoadOrderResolver.IndexView? pinned = null,
                                               IReadOnlyCollection<string>? forceLoaded = null,
                                               DialogueFold? fold = null)
    {
        try
        {
            var view = pinned ?? resolver.Capture();             // pin ONE index build for the whole validation
            using var session = resolver.OpenSession();          // one set of overlays, disposed at run end
            var av = assets.Capture();                           // …and ONE asset build, so presence + ReadIncomplete agree
            fold?.PlaceIn(view);                                 // where this file would load, against THIS build

            // Winner resolver for each INFO's Speaker → NPC → VoiceType and the topic's Quest, cached for the run.
            var loCache = new Dictionary<FormKey, IMajorRecordGetter?>();
            // Does the FOLD win this record? The same question the merge asks, off the same placement.
            bool FoldWins(FormKey k)
                => fold?.Holds(k) == true && fold.WinsAgainst(view, view.TouchingPlugins(k));

            IMajorRecordGetter? Resolve(FormKey k)
            {
                if (k.IsNull) return null;
                if (FoldWins(k) && fold!.Record(k) is { } folded) return folded;
                if (loCache.TryGetValue(k, out var c)) return c;
                IMajorRecordGetter? g = view.ResolveWinner(k) is { } w ? view.GetRecord(session, w.WinnerPlugin, k) : null;
                loCache[k] = g;
                return g;
            }

            // Which plugin the projected order says provides a record — the plain FILENAME, as this value is DATA.
            bool FoldProvides(FormKey k) => FoldWins(k);
            string? ProviderOf(FormKey k)
                => FoldProvides(k) ? fold!.Plugin : view.ResolveWinner(k)?.WinnerPlugin;

            // Cheap O(1) existence check, so only a PRESENT link pays Resolve's body fetch. See ValidateTopic.BadRef.
            bool InOrder(FormKey k) => !k.IsNull && (fold?.Holds(k) == true || view.ResolveWinner(k) is not null);

            // The topic's copy in its DEFINING master, for the SNAM ownership gate: a cached, typed DIAL seek.
            var baseCache = new Dictionary<FormKey, IDialogTopicGetter?>();
            IDialogTopicGetter? BaseCopy(FormKey k)
            {
                if (baseCache.TryGetValue(k, out var c)) return c;
                // Only where the fold DEFINES the record is its copy the base one; else the order's copy is.
                bool foldDefines = fold is not null
                                && k.ModKey.FileName.String.Equals(fold.Plugin, StringComparison.OrdinalIgnoreCase);
                var g = (foldDefines ? fold!.Record(k) as IDialogTopicGetter : null)
                     ?? (foldDefines && !view.ContainsPlugin(fold!.Plugin)
                            ? null
                            : view.GetRecord(session, k.ModKey.FileName.String, k, typeof(IDialogTopicGetter)) as IDialogTopicGetter);
                baseCache[k] = g;
                return g;
            }


            // ONE typed DIAL pass per plugin; view.GetRecord per (topic, plugin) is an unindexed overlay scan.
            Dictionary<FormKey, InfoOrderView> OrdersFor(IReadOnlyCollection<FormKey> topicFks)
                => InfoOrders(view, session, topicFks, fold);


            var win = view.ResolveWinner(fk);
            // The seed's provider under the projection; the fold's own body only where the fold WINS it.
            var seedFoldBody = FoldWins(fk) ? fold!.Record(fk) : null;
            if (win is null && seedFoldBody is null)
                return DialogueValidationReport.ForError(fk,
                    $"{FormIdToken.Of(fk)} is not in the active load order — nothing to validate. Pass a dialogue topic (DIAL) FormID to validate one topic, a quest (QUST) FormID to validate all of a quest's topics, or a dialogue view (DLVW) / branch (DLBR) FormID for a record-level CK-parity check."
                    + (fold is null ? "" : $" The folded file '{fold.Plugin}' carries no version of it either."));

            var provider = ProviderOf(fk)!;
            var body = seedFoldBody ?? view.GetRecord(session, win!.Value.WinnerPlugin, fk);
            if (body is null)
                return DialogueValidationReport.ForError(fk,
                    $"{FormIdToken.Of(fk)} resolves to a winner in {win!.Value.WinnerPlugin} but its body could not be fetched (the plugin may have changed since the index was built) — re-run to rebuild and try again.");

            if (body is IDialogTopicGetter topic)
            {
                var tv = ValidateTopic(topic, provider, InOrder, Resolve, av, BaseCopy, forceLoaded)
                    with { InfoOrder = OrdersFor(new[] { fk }).GetValueOrDefault(fk), WinnerIsFolded = FoldProvides(fk) };
                return new DialogueValidationReport(fk, "topic", topic.EditorID ?? "", provider, new[] { tv })
                    { ReadIncomplete = av.ReadIncomplete, InputWinnerIsFolded = FoldProvides(fk) };
            }

            if (body is IQuestGetter quest)
            {
                // A topic points UP at its quest, so this is a whole-order DIAL winner scan, consumed in order.
                var seqLint = CheckSeq(view, av, fk, quest, provider, fold);

                // Nullable entries, because a fold can DROP one; the slot is emptied in place and compacted below.
                var topics = new List<TopicValidation?>();
                // Where each topic's validation sits, so a folded copy REPLACES rather than duplicates it.
                var topicAt = new Dictionary<FormKey, int>();
                // A plugin that cannot be read contributes no topics; collected and named below.
                var unreadable = new List<PluginUnreadableException>();
                foreach (var (tfk, _, tbody) in view.WinnerRecordsOfType(DialTypes, unreadable))
                {
                    if (tbody is not IDialogTopicGetter dt) continue;
                    if (NonNull(dt.Quest.FormKeyNullable) is not { } qk || qk != fk) continue;
                    var wp = view.ResolveWinner(tfk)?.WinnerPlugin ?? provider;
                    topicAt[tfk] = topics.Count;
                    topics.Add(ValidateTopic(dt, wp, InOrder, Resolve, av, BaseCopy, forceLoaded));
                }

                // The folded file's own topics, run after the winner scan, which owns its overlay while it streams.
                if (fold is not null)
                    foreach (var ft in fold.TopicBodies)
                    {
                        // A topic the fold does not win keeps the active verdict.
                        if (!FoldWins(ft.FormKey)) continue;
                        bool ownsIt = NonNull(ft.Quest.FormKeyNullable) is { } fq && fq == fk;
                        bool listed = topicAt.TryGetValue(ft.FormKey, out int at);
                        // A topic the fold RE-PARENTS away is dropped rather than left standing.
                        if (!ownsIt) { if (listed) topics[at] = null; continue; }
                        var tv = ValidateTopic(ft, fold.Plugin, InOrder, Resolve, av, BaseCopy, forceLoaded)
                            with { WinnerIsFolded = true };
                        if (listed) topics[at] = tv;
                        else { topicAt[ft.FormKey] = topics.Count; topics.Add(tv); }
                    }

                // Built AFTER the winner scan closes, which owns its overlay; a view holds no overlay-backed body.
                var kept = topics.Where(t => t is not null).Select(t => t!).ToList();
                var orders = OrdersFor(kept.Select(t => t.Topic).ToList());
                for (int i = 0; i < kept.Count; i++)
                    kept[i] = kept[i] with { InfoOrder = orders.GetValueOrDefault(kept[i].Topic) };

                // Quest-level CK-parity gaps, checked ONCE. PRESENCE only: it never judges the ANAM VALUE.
                var questGaps = DialogueCkParity.MissingQuestDefaults(quest)
                    .Select(g => GapIssue("Quest", fk, g)).ToList();
                // Its own carrier, not the parity channel: a file lock is not a CK-parity failure.
                var scanGaps = unreadable
                    .Select(u => $"{u.Message} Any topic of this quest that plugin owns is missing from this report.")
                    .ToList();

                return new DialogueValidationReport(fk, "quest", quest.EditorID ?? "", provider, kept)
                    { ReadIncomplete = av.ReadIncomplete, SeqLint = seqLint, InputIssues = questGaps, ScanGaps = scanGaps,
                      InputWinnerIsFolded = FoldProvides(fk) };
            }

            // DLVW / DLBR: a RECORD-LEVEL check, so Topics stays empty and the render names the narrower scope.
            if (body is IDialogViewGetter dlvw)
            {
                var gaps = DialogueCkParity.MissingViewDefaults(dlvw)
                    .Select(g => GapIssue("DialogView", fk, g)).ToList();
                return new DialogueValidationReport(fk, "view", dlvw.EditorID ?? "", provider,
                    Array.Empty<TopicValidation>()) { InputIssues = gaps, InputWinnerIsFolded = FoldProvides(fk) };
            }

            if (body is IDialogBranchGetter dlbr)
            {
                var gaps = DialogueCkParity.MissingBranchDefaults(dlbr)
                    .Select(g => GapIssue("DialogBranch", fk, g)).ToList();
                return new DialogueValidationReport(fk, "branch", dlbr.EditorID ?? "", provider,
                    Array.Empty<TopicValidation>()) { InputIssues = gaps, InputWinnerIsFolded = FoldProvides(fk) };
            }

            return DialogueValidationReport.ForError(fk,
                $"{FormIdToken.Of(fk)} resolves to a {RecordNaming.StripOverlay(body.GetType().Name)} in {provider}, not a dialogue topic (DIAL), quest (QUST), dialogue view (DLVW), or dialogue branch (DLBR). Pass a DIAL FormID to validate one topic, a QUST FormID to validate every topic a quest owns, or a DLVW/DLBR FormID for a record-level CK-parity check.");
        }
        catch (Exception ex)
        {
            return DialogueValidationReport.ForCheckError(fk, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Validate ONE already-resolved winning <paramref name="topic"/>: Quest and Branch wiring, the
    /// INFO.LinkTo chain, dangling PNAM links, and the reused per-INFO voice and result-script checks.</summary>
    /// <param name="baseCopy">the topic's body as its DEFINING master holds it; only asked for where SNAM warns.</param>
    /// <param name="forceLoaded">the force-loaded names beyond the base masters; a null leaves the gate knowing
    /// only those five — a louder answer, never a quieter one.</param>
    internal static TopicValidation ValidateTopic(IDialogTopicGetter topic, string winnerPlugin,
        Func<FormKey, bool> inOrder, Func<FormKey, IMajorRecordGetter?> resolve, AssetResolver.AssetView assetView,
        Func<FormKey, IDialogTopicGetter?> baseCopy, IReadOnlyCollection<string>? forceLoaded)
    {
        var edid = topic.EditorID ?? "";
        var issues = new List<DialogueIssue>();
        var voiceLines = new List<VoiceLine>();
        var voiceUndet = new List<VoiceUndetermined>();
        var scriptFindings = new List<ScriptBindingFinding>();

        // Text-encoding lint over the player-facing strings; WARN only, and report-only.
        CheckEncoding(topic.Name?.String, $"DialogTopic.Name ({edid})", issues);

        // Classify a SET reference: cheap existence first, then a body fetch only for the wrong-type case.
        string? BadRef(FormKey target, string expects, Func<IMajorRecordGetter, bool> isExpected)
        {
            if (!inOrder(target)) return $"is not in the active load order ({expects} missing or disabled)";
            var body = resolve(target);
            return body is not null && isExpected(body) ? null
                : $"resolves to {(body is null ? "an unreadable record" : "a " + RecordNaming.StripOverlay(body.GetType().Name))}, not {expects}";
        }

        // --- Quest wiring: an unowned topic may never present its lines.
        var questFk = NonNull(topic.Quest.FormKeyNullable);
        if (questFk is null)
            issues.Add(new(DialogueIssueSeverity.Warning,
                "DialogTopic.Quest is unset — this topic is not owned by a quest. Most dialogue topics are; an unowned topic may never present its lines in game. Verify this is intentional."));
        else if (BadRef(questFk.Value, "a quest (QUST)", b => b is IQuestGetter) is { } qwhy)
            issues.Add(new(DialogueIssueSeverity.Problem,
                $"DialogTopic.Quest points at {FormIdToken.Of(questFk.Value)}, which {qwhy} — the owning quest is unresolved."));

        // --- Branch wiring: optional, but if set it must resolve to a real DLBR.
        var branchFk = NonNull(topic.Branch.FormKeyNullable);
        if (branchFk is not null && BadRef(branchFk.Value, "a dialogue branch (DLBR)", b => b is IDialogBranchGetter) is { } bwhy)
            issues.Add(new(DialogueIssueSeverity.Problem,
                $"DialogTopic.Branch points at {branchFk.Value}, which {bwhy} — the branch wiring is broken."));

        // --- BNAM absent on a Custom topic: it plays fine, but crashes the CK's Dialogue Views editor. WARN.
        if (topic.Subtype == DialogTopic.SubtypeEnum.Custom && branchFk is null)
            issues.Add(new(DialogueIssueSeverity.Warning,
                "DialogTopic.Branch (BNAM) is unset on this Custom topic — it plays fine in game, but the Creation "
                + "Kit's Dialogue Views editor auto-wraps a branch-less topic in a container branch and then crashes "
                + "rendering the flowchart (FlowchartX64). Set Branch to the owning DialogBranch (DLBR) before opening "
                + "this topic in the CK."));

        // --- SNAM subtype marker: a blank one (0000) is malformed — a Problem where this plugin DEFINES the
        //     topic, a Warning on an override. The blank test is DialogueSubtype's.
        // Ownership of the record the check reads, shared by every SNAM finding below.
        bool isOverride = !string.Equals(topic.FormKey.ModKey.FileName.String, winnerPlugin, StringComparison.OrdinalIgnoreCase);
        // Content the modder neither wrote nor can act on: the base masters plus the rest of the force-loaded set.
        bool modAuthored = !ErrorCheck.IsBaseMaster(winnerPlugin)
            && !(forceLoaded?.Contains(winnerPlugin, StringComparer.OrdinalIgnoreCase) ?? false);

        // The (Subtype, SNAM) pair the override INHERITED, read once and only for a finding that needs it.
        (int Subtype, RecordType Marker)? basePairCache = null;
        bool basePairRead = false;
        (int Subtype, RecordType Marker)? BasePair()
        {
            if (basePairRead) return basePairCache;
            basePairRead = true;
            if (isOverride && baseCopy(topic.FormKey) is { } b) basePairCache = ((int)b.Subtype, b.SubtypeName);
            return basePairCache;
        }

        // The caveat every recommendation DERIVED from the numeric Subtype must carry, shared by two arms.
        const string derived = " That comes from the numeric Subtype, which is stale on topics authored before the "
            + "Dragonborn-era Creation Kit renumbered the subtype enum — check the base record's SNAM before writing it.";

        if (DialogueSubtype.IsBlankMarker(topic.SubtypeName))
        {
            var expected = DialogueSubtype.MarkerFor((int)topic.Subtype);
            var fix = (expected is not null
                ? $"Set it to {expected} (the marker for Subtype={topic.Subtype}); houseCARL's create tools now auto-fill it, or {ToolNames.Apply} on SubtypeName with value={expected}."
                : $"Set it to the correct 4-char marker for Subtype={topic.Subtype} via {ToolNames.Apply} on SubtypeName.") + derived;
            issues.Add(isOverride
                ? new(DialogueIssueSeverity.Warning,
                    $"DialogTopic.SubtypeName (the SNAM subtype marker) is empty (0000) on this OVERRIDE of {topic.FormKey.ModKey.FileName} — "
                    + "the game buckets topics by this 4-char marker; the base record's marker may still apply (a blank-SNAM override ships in "
                    + $"some working mods), but the record is malformed. {fix}")
                : new(DialogueIssueSeverity.Problem,
                    "DialogTopic.SubtypeName (the SNAM subtype marker) is empty (0000) — the game buckets topics by this 4-char marker, "
                    + $"and this plugin DEFINES the topic, so a blank marker is a load CTD on load (#131); the record is malformed. {fix}"));
        }

        // --- Subtype vs SNAM disagreement: SNAM wins, and this is reported, never "fixed". Scoped to a record a
        //     mod AUTHORED the pair on (Aaron's ruling); contract in docs/architecture/dialogue-validation.md.
        else if (DialogueSubtype.MarkerDisagreesWithSubtype(topic))
        {
            // An inherited pair is the base record's statement: the override changed neither field.
            bool inherited = BasePair() is { } b
                && b.Subtype == (int)topic.Subtype
                && string.Equals(b.Marker.Type, topic.SubtypeName.Type, StringComparison.Ordinal);
            int fromIndex = DialogueSubtype.IndexForMarker(topic.SubtypeName)!.Value;
            var fromMarker = DialogueSubtype.LabelForMarker(topic.SubtypeName)!;
            if (modAuthored && !inherited)
            {
                var head = $"DialogTopic.Subtype reads {topic.Subtype} ((int){(int)topic.Subtype}) but the SNAM marker is "
                    + $"{topic.SubtypeName.Type} ({fromMarker}) — they disagree, and the MARKER is authoritative: the game "
                    + "buckets topics by SNAM. ";
                if (DialogueSubtype.IsRenumberedVintage(fromIndex, (int)topic.Subtype))
                    issues.Add(new(DialogueIssueSeverity.Warning, head
                        + $"The numbers carry the renumbering signature (stored exactly {DialogueSubtype.RenumberOffset} below the "
                        + "marker's modern index), so the record is not necessarily broken: Bethesda's Dragonborn-era Creation Kit "
                        + $"inserted {DialogueSubtype.RenumberOffset} FlyingMount* values at index 20, and a topic authored before that "
                        + "stores the older, lower number which every reader labels too early. "
                        + $"Treat this topic's subtype as {fromMarker}, not {topic.Subtype}."));
                else
                    issues.Add(new(DialogueIssueSeverity.Warning, head
                        + "The numbers do NOT carry the Dragonborn-era renumbering signature, so this is not an old file: the two "
                        + $"fields were edited apart. The topic still buckets as {fromMarker}, which makes the Subtype value an "
                        + $"in-game no-op. If {fromMarker} is the intended subtype, set Subtype to it and leave SNAM alone; if "
                        + $"{topic.Subtype} is, sync SNAM to "
                        + $"{DialogueSubtype.MarkerFor((int)topic.Subtype) ?? $"the marker for {topic.Subtype}"} via "
                        + $"{ToolNames.Apply} on SubtypeName — setting Subtype through houseCARL does that sync for you."));
            }
        }

        // --- A non-blank marker this table does not model: neither check above catches it. Same ownership gate.
        else if (modAuthored && DialogueSubtype.IndexForMarker(topic.SubtypeName) is null)
        {
            // What to set it TO: the base record's SNAM where modeled, else the numeric Subtype with its caveat.
            var expected = DialogueSubtype.MarkerFor((int)topic.Subtype);
            var fix = BasePair() is { } b && DialogueSubtype.LabelForMarker(b.Marker) is { } baseName
                ? $"Set it to {b.Marker.Type} ({baseName}) — the marker on the base record in {topic.FormKey.ModKey.FileName}, "
                  + $"which is the bucket this override inherits — via {ToolNames.Apply} on SubtypeName."
                : expected is not null
                    ? $"Set it to {expected} (the marker for Subtype={topic.Subtype}) via {ToolNames.Apply} on SubtypeName." + derived
                    : $"Set it to the correct 4-char marker for Subtype={topic.Subtype} via {ToolNames.Apply} on SubtypeName.";
            issues.Add(new(DialogueIssueSeverity.Warning,
                $"DialogTopic.SubtypeName (the SNAM subtype marker) is {topic.SubtypeName.Type}, which is not a marker houseCARL "
                + "models — the game buckets topics by this 4-char tag, so an invented or mis-cased one (the tags are fixed case: "
                + $"HELO, not helo) puts the topic in a bucket no dialogue handler reads and it never plays. {fix} Or, if "
                + $"{topic.SubtypeName.Type} is a real marker, report it: houseCARL's table is missing a row."));
        }

        // The owning quest's alias IDs, resolved once; NULL skips the alias-index lints rather than guessing.
        HashSet<uint>? ownerAliasIds = null;
        // The EditorIDs a `<Global=X>` tag must name to render; NULL and skipped when the quest is unresolvable.
        HashSet<string>? ownerTextGlobals = null;
        string ownerQuestLabel = "the owning quest";
        if (questFk is { } ownerFk && resolve(ownerFk) is IQuestGetter ownerQuest)
        {
            ownerAliasIds = ownerQuest.Aliases.Select(a => a.ID).ToHashSet();
            // Case-insensitive, as the engine's tag match is; an unresolvable entry contributes no name.
            ownerTextGlobals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in ownerQuest.TextDisplayGlobals)
                if (!g.FormKey.IsNull && resolve(g.FormKey) is IGlobalGetter glob && glob.EditorID is { Length: > 0 } gid)
                    ownerTextGlobals.Add(gid);
            ownerQuestLabel = $"the owning quest {ownerQuest.EditorID ?? FormIdToken.Of(ownerFk)}";
        }

        // --- Per-INFO walk over the LIVE INFOs; a deleted INFO is skipped but tallied.
        int infoCount = 0, conditioned = 0, deleted = 0, fragmentInfos = 0;
        foreach (var info in topic.Responses)
        {
            if (info.IsDeleted) { deleted++; continue; }
            infoCount++;

            // INFO CK-parity (CNAM/ENAM), off DialogueCkParity's own absence tests. WARN, matching the BNAM lint.
            foreach (var gap in DialogueCkParity.MissingInfoDefaults(info))
                issues.Add(GapIssue("INFO", info.FormKey, gap));

            // Via the single fragment-presence home, so this never drifts from the script check's HasFragment.
            if (DialogueScriptCheck.HasResultFragment(info)) fragmentInfos++;

            // Text-encoding lint over this line's menu Prompt and each spoken row.
            CheckEncoding(info.Prompt?.String, $"INFO {FormIdToken.Of(info.FormKey)} Prompt", issues);
            int rnum = 0;
            foreach (var resp in info.Responses)
                CheckEncoding(resp.Text?.String, $"INFO {FormIdToken.Of(info.FormKey)} response {++rnum} text", issues);

            // A `<Global=X>` tag renders as `[...]` unless the quest's TextDisplayGlobals covers it — a WARN.
            if (ownerTextGlobals is not null)
                CheckGlobalTags(info, ownerTextGlobals, ownerQuestLabel, issues);

            // PNAM: absence is the norm and is NEVER flagged; only a SET unresolvable link is a defect.
            var pnam = NonNull(info.PreviousDialog.FormKeyNullable);
            if (pnam is not null && BadRef(pnam.Value, "a dialogue line (INFO)", b => b is IDialogResponsesGetter) is { } pwhy)
                issues.Add(new(DialogueIssueSeverity.Problem,
                    $"INFO {FormIdToken.Of(info.FormKey)} has a previous-link (PNAM -> {FormIdToken.Of(pnam.Value)}) that {pwhy}."));

            // LinkTo: the REAL conversation chain; an empty one is a normal terminal line and is never flagged.
            foreach (var link in info.LinkTo)
            {
                var lk = link.FormKey;
                if (!lk.IsNull && BadRef(lk, "a dialogue topic (DIAL)", b => b is IDialogTopicGetter) is { } lwhy)
                    issues.Add(new(DialogueIssueSeverity.Problem,
                        $"INFO {FormIdToken.Of(info.FormKey)} links (LinkTo) to {FormIdToken.Of(lk)}, which {lwhy} — the conversation chain is broken."));
            }

            if (info.Conditions.Count > 0)
            {
                conditioned++;
                // Static condition (CTDA) lints: they catch MALFORMED conditions and never evaluate one.
                CheckConditions(info, ownerAliasIds, ownerQuestLabel, inOrder, resolve, issues);
            }

            // The exact methods the per-create teeth run, so the create path and the validator cannot drift.
            VoiceCheck.CheckInfo(info, topic, resolve, assetView, voiceLines, voiceUndet);
            DialogueScriptCheck.CheckInfo(info, edid, assetView, scriptFindings);
        }

        return new TopicValidation(
            topic.FormKey, edid, winnerPlugin, infoCount, conditioned, deleted, fragmentInfos,
            topic.Category.ToString(), topic.Subtype.ToString(), DescribeSubtypeName(topic.SubtypeName),
            issues, voiceLines, voiceUndet, scriptFindings)
        {
            SubtypeDisagreesWithMarker = DialogueSubtype.MarkerDisagreesWithSubtype(topic),
            SubtypeFromMarker = DialogueSubtype.LabelForMarker(topic.SubtypeName) ?? "",
        };
    }

    /// <summary>SEQ lint for a QUEST input: does the DEFINING plugin have a <c>.seq</c> that LISTS this
    /// Start-Game-Enabled quest and is NEWER than it? Fault-isolated — an IO or parse failure, or a BSA-resident
    /// <c>.seq</c>, yields a NAMED note rather than a false verdict.</summary>
    /// <param name="fold">the off-order plugin folded in, whose own path serves a quest it defines.</param>
    internal static SeqLintFinding? CheckSeq(LoadOrderResolver.IndexView view, AssetResolver.AssetView av,
        FormKey fk, IQuestGetter quest, string winnerPlugin, DialogueFold? fold = null)
    {
        if (!quest.Flags.HasFlag(Quest.Flag.StartGameEnabled)) return null;   // not SGE → no .seq needed, no lint
        var defining = fk.ModKey.FileName;
        try
        {
            // The FOLD's own file wins where the quest came from it: an active namesake would stat another file.
            var pluginPath = (fold is not null && fold.Holds(fk)
                              && defining.String.Equals(fold.Plugin, StringComparison.OrdinalIgnoreCase)
                                  ? fold.Path : null)
                ?? view.PluginPath(defining)
                ?? (fold is not null && defining.String.Equals(fold.Plugin, StringComparison.OrdinalIgnoreCase)
                        ? fold.Path : null);
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

    /// <summary>A 4-char SubtypeName marker as text, or "&lt;none&gt;", over DialogueSubtype's blank test.</summary>
    static string DescribeSubtypeName(RecordType rt) => DialogueSubtype.IsBlankMarker(rt) ? "<none>" : rt.Type;

    /// <summary>A nullable FormLink's target, or null when it is unset or explicitly Null — both mean no target.</summary>
    static FormKey? NonNull(FormKey? fk) => fk is { } v && !v.IsNull ? v : null;

    /// <summary>Non-ASCII offenders with a known substitute; any other is still flagged, without one.</summary>
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

    /// <summary>Text-encoding lint: ONE WARNING per <paramref name="locus"/> carrying a non-ASCII char.</summary>
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

    /// <summary>The <c>&lt;Global=X&gt;</c> tags; the optional <c>(?:\.\w+)?</c> covers the CK's subtag variants.</summary>
    static readonly Regex GlobalTagRx = new(@"<Global(?:\.\w+)?=([^<>]+)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary><c>&lt;Global=X&gt;</c> lint: WARN on any tag naming a global not in the owning quest's
    /// TextDisplayGlobals, one per distinct missing name per INFO.</summary>
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
                    $"INFO {FormIdToken.Of(info.FormKey)} {locus} uses the text-replacement tag {m.Value}, but {name} is not a "
                    + $"global in {ownerQuestLabel}'s TextDisplayGlobals — in game the tag renders as [...] (the global is "
                    + $"never substituted). Add {name} to the quest's Text Display Globals."));
            }
        }
        Scan(info.Prompt?.String, "Prompt");
        int rnum = 0;
        foreach (var resp in info.Responses) Scan(resp.Text?.String, $"response {++rnum} text");
    }

    // Engine-implicit forms are exempted from condition lints 1 and 3; the set lives in EngineImplicit.

    /// <summary>Static condition-lint suite over one INFO's <c>Conditions</c> — the data-layer-decidable subset,
    /// all emitting WARNING; what is deliberately not linted is in docs/architecture/dialogue-validation.md.</summary>
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

            // FLOI MODE GATE: a FormLinkOrIndex is a form ONLY when UseAliases and UsePackageData are both false.
            bool floiIsForm = !data.UseAliases && !data.UsePackageData;

            // 1. Run On a specific Reference but none or a missing one is set.
            if (data.RunOnType == Condition.RunOnType.Reference)
            {
                if (data.Reference.IsNull)
                    issues.Add(new(DialogueIssueSeverity.Warning,
                        $"INFO {FormIdToken.Of(info.FormKey)} condition #{n} ({fn}) is set to Run On a specific reference, but no reference is set — it evaluates against nothing, so the gate never behaves as intended."));
                else if (!inOrder(refKey) && !EngineImplicit.IsImplicit(refKey))
                    issues.Add(new(DialogueIssueSeverity.Warning,
                        $"INFO {FormIdToken.Of(info.FormKey)} condition #{n} ({fn}) Run On reference {FormIdToken.Of(refKey)} is not in the active load order — the gate evaluates against nothing."));
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

            // 3. Dangling form-link PARAMETER, by construction: reflect over the Data arm and take every form
            //    target. An alias-mode FLOI is gated out; the Run On Reference slot is skipped by name.
            foreach (var p in data.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.Name == "Reference" || p.GetIndexParameters().Length != 0) continue;   // run-on ref → lint 1
                object? v; try { v = p.GetValue(data); } catch { continue; }
                // Branch 1 is defensive: an FLOI is read via .Link, so it never bypasses the mode gate here.
                FormKey? paramFk =
                    v is IFormLinkGetter fl && !fl.IsNull ? fl.FormKey
                    : floiIsForm && WriteEngine.IsFormLinkOrIndex(p.PropertyType) ? WriteEngine.ReadFloiFormKey(v) : null;
                if (paramFk is { } pk && !inOrder(pk) && !EngineImplicit.IsImplicit(pk))
                    issues.Add(new(DialogueIssueSeverity.Warning,
                        $"INFO {FormIdToken.Of(info.FormKey)} condition #{n} ({fn}) references {FormIdToken.Of(pk)}, which is not in the active load order — a deleted/disabled form or a wrong FormID, so the condition can't evaluate as intended."));
            }

            // 4. Dangling global comparison value: it lives on the Condition, outside the param sweep above.
            if (cond is IConditionGlobalGetter cg && !cg.ComparisonValue.IsNull && !inOrder(cg.ComparisonValue.FormKey))
                issues.Add(new(DialogueIssueSeverity.Warning,
                    $"INFO {FormIdToken.Of(info.FormKey)} condition #{n} ({fn}) compares against global {FormIdToken.Of(cg.ComparisonValue.FormKey)}, which is not in the active load order."));

            // 5. GetIsID pointed at a PLACED reference — the wrong KIND of form; gated on floiIsForm.
            if (data is IGetIsIDConditionDataGetter gid && floiIsForm && WriteEngine.ReadFloiFormKey(gid.Object) is { } objFk
                && inOrder(objFk) && resolve(objFk) is IPlacedGetter)
                issues.Add(new(DialogueIssueSeverity.Warning,
                    $"INFO {FormIdToken.Of(info.FormKey)} condition #{n} (GetIsID) points at the placed reference {FormIdToken.Of(objFk)}, but GetIsID compares the run-on actor's BASE form — pass the base NPC_/object, not a placed instance."));
        }
    }

    /// <summary>An alias index is dead when negative, or not one of the owning quest's reference-alias IDs.</summary>
    static bool BadAlias(int idx, HashSet<uint> ownerAliasIds) => idx < 0 || !ownerAliasIds.Contains((uint)idx);

    /// <summary>The dead-alias-index warning (lint 2), naming the function, the index and the owning quest.</summary>
    static DialogueIssue AliasIssue(FormKey infoFk, int n, string fn, int idx, string verb, string aliasKind, string ownerQuestLabel) =>
        new(DialogueIssueSeverity.Warning,
            $"INFO {FormIdToken.Of(infoFk)} condition #{n} ({fn}) {verb} {ownerQuestLabel}'s {aliasKind} #{idx}, but that quest defines no alias with that ID — the gate evaluates against nothing.");
}

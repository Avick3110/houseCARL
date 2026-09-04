using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

// VoiceCheck — the on-disk voice (.fuz/.lip) presence check for created dialogue lines. A byte-valid
// INFO with no .fuz on disk plays nothing, so a create is followed by this read-only diagnostic: it
// computes each response line's expected voice path, checks it against the live VFS (loose + BSA), and
// reports either "will be silent" or a named reason the path could not be computed — never a false "fine".
// It adds no write logic; the create path itself is untouched.
//
// Resolving the graph (a voice path needs more than the FormKey — see VoicePath):
//   • parent topic — found by walking the written patch's DialogTopics; the created INFO sits in some
//     topic's Responses, so topic EditorID and the Quest link come off the patch for both a same-call
//     parent and an existing one (whose override the patch carries with EditorID + Quest intact).
//   • voice type — INFO.Speaker -> Npc.Voice -> VoiceType.EditorID, each resolved patch-first then
//     load-order; a same-call record lives in the patch, an existing one in the order.
//   • quest EditorID — the topic's Quest, empty when the topic has none.
// Resolution reuses the read idiom (ResolveWinner -> GetRecord), cached per FormKey for the run.

/// <summary>One created INFO response line's voice verdict: the expected <see cref="FuzPath"/> (the spoken
/// audio — absence ⇒ the line is SILENT) and <see cref="LipPath"/> (lip-sync — absence ⇒ no mouth movement,
/// audio still plays), each with its on-disk presence + the winning provider, plus the
/// <see cref="ReadIncomplete"/> caveat (a BSA failed to read, so an "absent" may merely be unscanned).</summary>
public sealed record VoiceLine(
    FormKey Info, string TopicEditorId, int ResponseNumber,
    string FuzPath, bool FuzPresent, string? FuzWinner, bool FuzAmbiguous,
    string LipPath, bool LipPresent,
    bool ReadIncomplete);

/// <summary>A created INFO whose voice path could NOT be computed, with the named reason: no Speaker
/// (voice type assigned at runtime from a quest alias), an unresolvable speaker/voice-type, etc.
/// The whole INFO is reported once; its lines are not checked.</summary>
public sealed record VoiceUndetermined(FormKey Info, string TopicEditorId, string Reason);

/// <summary>The voice-coverage report for one create call: per-line presence verdicts and per-INFO
/// undeterminable reasons. <see cref="IsEmpty"/> when the call created no voiced INFO lines.</summary>
public sealed record VoiceReport(IReadOnlyList<VoiceLine> Lines, IReadOnlyList<VoiceUndetermined> Undetermined)
{
    /// <summary>The voice check itself could not run (the patch wouldn't re-open, the walk threw) — surfaced,
    /// never a silent skip. The create ALREADY SUCCEEDED when this is set; it means "voice coverage unverified",
    /// not "the write failed". Null on a clean run.</summary>
    public string? CheckError { get; init; }

    public bool IsEmpty => Lines.Count == 0 && Undetermined.Count == 0 && CheckError is null;
    public static readonly VoiceReport Empty = new(Array.Empty<VoiceLine>(), Array.Empty<VoiceUndetermined>());
}

public static class VoiceCheck
{
    /// <summary>The catalog name (RecordNaming.StripGetterInterface of IDialogResponsesGetter) the create flow
    /// stamps on a created INFO — the filter for "which created records are dialogue lines".</summary>
    public const string InfoCatalogName = "DialogResponses";

    /// <summary>Run the voice-presence check over the INFOs created by ONE create call. <paramref name="patchPath"/> is
    /// the just-written patch file (re-opened here read-only, then disposed — the overlay lifetime lives in core, so the
    /// service needs no Mutagen.Skyrim dependency); <paramref name="created"/> is the call's CreatedRecord list (filtered
    /// here to INFOs); <paramref name="resolver"/> resolves existing speaker/voice-type/quest records from the load order;
    /// <paramref name="assets"/> answers on-disk presence (loose + BSA). Returns <see cref="VoiceReport.Empty"/> when the
    /// call created no INFOs. A resolve MISS is a named undetermined reason; a whole-check failure (the patch won't
    /// re-open, the walk throws) is surfaced on <see cref="VoiceReport.CheckError"/> — NEVER thrown (the create already
    /// succeeded; this is a verify step, not the write).</summary>
    public static VoiceReport Run(string patchPath, IReadOnlyList<WritePatchBuilder.CreatedRecord> created,
                                  LoadOrderResolver resolver, AssetResolver assets)
    {
        // Which created records are dialogue lines (INFOs) — only these get a voice check.
        var infoKeys = new HashSet<FormKey>();
        foreach (var c in created)
            if (string.Equals(c.RecordType, InfoCatalogName, StringComparison.Ordinal))
                infoKeys.Add(c.FormKey);
        if (infoKeys.Count == 0) return VoiceReport.Empty;

        ISkyrimModGetter? patch = null;
        try
        {
            patch = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            return RunOver(patch, infoKeys, resolver, assets);
        }
        catch (Exception ex)
        {
            return VoiceReport.Empty with { CheckError = $"{ex.GetType().Name}: {ex.Message}" };
        }
        finally { (patch as IDisposable)?.Dispose(); }
    }

    /// <summary>The walk over the re-opened patch (split out so <see cref="Run"/> can wrap the overlay open + any
    /// walk-level throw into <see cref="VoiceReport.CheckError"/>, while per-INFO resolve misses stay NAMED undetermined).</summary>
    static VoiceReport RunOver(ISkyrimModGetter writtenPatch, HashSet<FormKey> infoKeys,
                               LoadOrderResolver resolver, AssetResolver assets)
    {
        var lines = new List<VoiceLine>();
        var undetermined = new List<VoiceUndetermined>();

        // Same-call record lookup off the patch (an NPC / quest / topic created in THIS call lives here, not the
        // load order). One pass; FormKeys are unique within a mod.
        var patchByKey = new Dictionary<FormKey, IMajorRecordGetter>();
        foreach (var rec in writtenPatch.EnumerateMajorRecords())
            patchByKey[rec.FormKey] = rec;

        using var session = resolver.OpenSession();
        var view = resolver.Capture();                       // pin ONE index build for every resolve in this run
        var av = assets.Capture();                           // …and ONE asset build, so presence + ReadIncomplete agree
        var loCache = new Dictionary<FormKey, IMajorRecordGetter?>();

        // Resolve a FormKey to its record getter: the patch first (same-call records), else the load-order winner
        // (existing records). Cached so a bulk_create sharing a speaker doesn't re-enumerate a master per line.
        IMajorRecordGetter? Resolve(FormKey fk)
        {
            if (patchByKey.TryGetValue(fk, out var p)) return p;
            if (loCache.TryGetValue(fk, out var c)) return c;
            IMajorRecordGetter? g = view.ResolveWinner(fk) is { } w ? view.GetRecord(session, w.WinnerPlugin, fk) : null;
            loCache[fk] = g;
            return g;
        }

        // Walk the patch's topics; each created INFO is in exactly one topic's Responses (its structural parent).
        var foundInfos = new HashSet<FormKey>();
        foreach (var topic in writtenPatch.DialogTopics)
        {
            foreach (var info in topic.Responses)
            {
                if (!infoKeys.Contains(info.FormKey)) continue;   // a pre-existing INFO the patch carried, or not ours
                foundInfos.Add(info.FormKey);
                CheckInfo(info, topic, Resolve, av, lines, undetermined);
            }
        }

        // A created INFO not found under any topic is a real inconsistency — surfaced, never silently dropped.
        foreach (var fk in infoKeys)
            if (!foundInfos.Contains(fk))
                undetermined.Add(new VoiceUndetermined(fk, "",
                    "created but not found under any topic in the written patch — can't determine its voice path; inspect the patch in xEdit."));

        return new VoiceReport(lines, undetermined);
    }

    /// <summary>Resolve one INFO's voice graph (topic+quest EDIDs, speaker voice type) and emit either a per-line
    /// presence verdict for each spoken response, or ONE named undetermined reason when the voice folder can't
    /// be computed. An INFO with no spoken response lines (a link/branch node) yields nothing — there is no voice to check.</summary>
    // internal, not private: DialogueValidate reuses this exact per-INFO walk over every INFO in a topic, so the
    // per-create check and the on-demand validator cannot drift on what counts as silent.
    internal static void CheckInfo(IDialogResponsesGetter info, IDialogTopicGetter topic,
                          Func<FormKey, IMajorRecordGetter?> resolve, AssetResolver.AssetView av,
                          List<VoiceLine> lines, List<VoiceUndetermined> undetermined)
    {
        var topicEdid = topic.EditorID ?? "";

        // No own response lines: either a link/branch node (no spoken audio — skip silently) or one that BORROWS
        // another INFO's audio via ResponseData (it IS voiced, but under the OTHER INFO's path, not computable here).
        // The borrowed case must be named, not silently produce nothing.
        if (info.Responses.Count == 0)
        {
            var sharedFk = NonNull(info.ResponseData.FormKeyNullable);
            if (sharedFk is { } sfk)
                undetermined.Add(new VoiceUndetermined(info.FormKey, topicEdid,
                    $"no own response lines — this line draws its audio from shared response data ({sfk}); voice is not checked here, verify that INFO's .fuz."));
            return;   // ResponseData null ⇒ a genuine link/branch node: no spoken audio to check
        }

        // Speaker -> the voice type (folder). Null Speaker is the runtime quest-alias case: no computable path.
        var speakerFk = NonNull(info.Speaker.FormKeyNullable);
        if (speakerFk is null)
        {
            undetermined.Add(new VoiceUndetermined(info.FormKey, topicEdid,
                "no Speaker set — the voice type (folder) is assigned at runtime from the quest alias, so the .fuz path can't be computed. " +
                "Set Speaker on this line to make it checkable, or verify the audio yourself."));
            return;
        }
        // Two distinct misses, two distinct messages — don't say "not found" for a record that WAS found: the
        // FormKey resolves to nothing, vs it resolves to a record that isn't an NPC (Speaker is typed as a FormLink to
        // an NPC, but real/odd data can point it elsewhere, and the voice type is derived only from an NPC's Voice).
        var speaker = resolve(speakerFk.Value);
        if (speaker is null)
        {
            undetermined.Add(new VoiceUndetermined(info.FormKey, topicEdid,
                $"Speaker {speakerFk.Value} not found in the patch or load order — can't resolve the voice type."));
            return;
        }
        if (speaker is not INpcGetter npc)
        {
            undetermined.Add(new VoiceUndetermined(info.FormKey, topicEdid,
                $"Speaker {speakerFk.Value} resolves to a non-NPC record — houseCARL derives the voice type from an NPC's Voice, so it can't compute a voice path here; verify the audio yourself."));
            return;
        }
        var voiceFk = NonNull(npc.Voice.FormKeyNullable);
        if (voiceFk is null)
        {
            undetermined.Add(new VoiceUndetermined(info.FormKey, topicEdid,
                $"Speaker NPC {speakerFk.Value} has no Voice type set — can't compute the voice folder."));
            return;
        }
        var voiceType = (resolve(voiceFk.Value) as IVoiceTypeGetter)?.EditorID;
        if (string.IsNullOrEmpty(voiceType))
        {
            undetermined.Add(new VoiceUndetermined(info.FormKey, topicEdid,
                $"the speaker's Voice type {voiceFk.Value} has no resolvable EditorID — can't name the voice folder."));
            return;
        }

        // Quest EDID — the topic's quest's EditorID (empty when the topic has no quest; that's a real on-disk shape).
        var questFk = NonNull(topic.Quest.FormKeyNullable);
        var questEdid = questFk is { } qfk ? (resolve(qfk) as IQuestGetter)?.EditorID ?? "" : "";

        // One .fuz/.lip check per spoken response line; its ResponseNumber names the file, used as authored.
        foreach (var resp in info.Responses)
        {
            int num = resp.ResponseNumber;
            var fuz = VoicePath.For(info.FormKey, voiceType, questEdid, topicEdid, num, VoiceFile.Fuz);
            var lip = VoicePath.For(info.FormKey, voiceType, questEdid, topicEdid, num, VoiceFile.Lip);
            var fhit = av.Resolve(fuz);
            var lhit = av.Resolve(lip);
            lines.Add(new VoiceLine(
                info.FormKey, topicEdid, num,
                fuz, fhit.Exists, fhit.Winner?.Source, fhit.Ambiguous,
                lip, lhit.Exists,
                av.ReadIncomplete));
        }
    }

    /// <summary>A nullable FormLink's target as a real FormKey, or null when the link is unset OR explicitly Null
    /// (00000000) — both mean "no target", and a Null-FormKey segment would resolve nothing.</summary>
    static FormKey? NonNull(FormKey? fk) => fk is { } v && !v.IsNull ? v : null;
}

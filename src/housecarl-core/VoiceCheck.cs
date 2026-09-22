using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

// VoiceCheck — the on-disk voice presence check for created dialogue lines: compute each response's expected path,
// resolve it against the live VFS, and report "will be silent" or a named reason the path could not be computed.
// Read-only, and the graph it has to resolve to get a path is in docs/architecture/assets.md.

/// <summary>One created INFO response line's voice verdict: the expected .fuz (absence means SILENT) and .lip paths,
/// each with its presence and winning provider, plus the read-incomplete caveat.</summary>
public sealed record VoiceLine(
    FormKey Info, string TopicEditorId, int ResponseNumber,
    string FuzPath, bool FuzPresent, string? FuzWinner, bool FuzAmbiguous,
    string LipPath, bool LipPresent,
    bool ReadIncomplete);

/// <summary>A created INFO whose voice path could NOT be computed, with the named reason. Its lines are not checked.</summary>
public sealed record VoiceUndetermined(FormKey Info, string TopicEditorId, string Reason);

/// <summary>The voice-coverage report for one create call: per-line verdicts and per-INFO undeterminable reasons.</summary>
public sealed record VoiceReport(IReadOnlyList<VoiceLine> Lines, IReadOnlyList<VoiceUndetermined> Undetermined)
{
    /// <summary>The voice check itself could not run — surfaced, never a silent skip. The create ALREADY SUCCEEDED
    /// when this is set: it means "voice coverage unverified", not "the write failed".</summary>
    public string? CheckError { get; init; }

    /// <summary>The loose roots this scan could not walk or list, each named with the reason, so the "may merely be
    /// unscanned" note says WHICH folder; empty when every root read.</summary>
    public IReadOnlyList<string> RootFailures { get; init; } = Array.Empty<string>();

    public bool IsEmpty => Lines.Count == 0 && Undetermined.Count == 0 && CheckError is null;
    public static readonly VoiceReport Empty = new(Array.Empty<VoiceLine>(), Array.Empty<VoiceUndetermined>());
}

public static class VoiceCheck
{
    /// <summary>The catalog name the create flow stamps on a created INFO — the filter for which created records are dialogue lines.</summary>
    public const string InfoCatalogName = "DialogResponses";

    /// <summary>Run the voice-presence check over the INFOs created by ONE create call. <paramref name="patchPath"/>
    /// is the just-written patch, re-opened read-only here so the service needs no Mutagen.Skyrim dependency. A
    /// resolve miss is a named undetermined reason; a whole-check failure rides
    /// <see cref="VoiceReport.CheckError"/> and is NEVER thrown — the create already succeeded.</summary>
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
            patch = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(patchPath));
            return RunOver(patch, infoKeys, resolver, assets);
        }
        catch (Exception ex)
        {
            return VoiceReport.Empty with { CheckError = $"{ex.GetType().Name}: {ex.Message}" };
        }
        finally { (patch as IDisposable)?.Dispose(); }
    }

    /// <summary>The walk over the re-opened patch, split out so <see cref="Run"/> can wrap an overlay open or a walk-level throw into CheckError.</summary>
    static VoiceReport RunOver(ISkyrimModGetter writtenPatch, HashSet<FormKey> infoKeys,
                               LoadOrderResolver resolver, AssetResolver assets)
    {
        var lines = new List<VoiceLine>();
        var undetermined = new List<VoiceUndetermined>();

        // Same-call record lookup off the patch: an NPC, quest or topic created in THIS call lives here, not the order.
        var patchByKey = new Dictionary<FormKey, IMajorRecordGetter>();
        foreach (var rec in writtenPatch.EnumerateMajorRecords())
            patchByKey[rec.FormKey] = rec;

        using var session = resolver.OpenSession();
        var view = resolver.Capture();                       // pin ONE index build for every resolve in this run
        var av = assets.Capture();                           // …and ONE asset build, so presence + ReadIncomplete agree
        var loCache = new Dictionary<FormKey, IMajorRecordGetter?>();

        // Resolve a FormKey: the patch first, else the load-order winner. Cached so a bulk_create sharing a speaker
        // does not re-enumerate a master per line.
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

        return new VoiceReport(lines, undetermined) { RootFailures = av.RootFailures };
    }

    /// <summary>Resolve one INFO's voice graph and emit either a per-line presence verdict for each spoken response,
    /// or ONE named undetermined reason when the voice folder cannot be computed.</summary>
    // internal, not private: DialogueValidate reuses this exact walk, so the two cannot drift on what counts as silent.
    internal static void CheckInfo(IDialogResponsesGetter info, IDialogTopicGetter topic,
                          Func<FormKey, IMajorRecordGetter?> resolve, AssetResolver.AssetView av,
                          List<VoiceLine> lines, List<VoiceUndetermined> undetermined)
    {
        var topicEdid = topic.EditorID ?? "";

        // No own response lines: a link/branch node has no audio, but one that BORROWS another INFO's via ResponseData
        // IS voiced under the other INFO's path, which is not computable here — so it is named, never silent.
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
        // Two distinct misses, two distinct messages: the FormKey resolving to nothing is not the same as resolving
        // to a record that is not an NPC, and the voice type is derived only from an NPC's Voice.
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

    /// <summary>A nullable FormLink's target as a real FormKey, or null when the link is unset OR explicitly Null.</summary>
    static FormKey? NonNull(FormKey? fk) => fk is { } v && !v.IsNull ? v : null;
}

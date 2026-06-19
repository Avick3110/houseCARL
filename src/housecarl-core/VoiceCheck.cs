using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

// ======================================================================
//  VoiceCheck — the on-disk voice (.fuz/.lip) PRESENCE check for created dialogue lines
//  (nested-dialogue plan §3.5, Layer B unit B). A byte-valid INFO with no .fuz on disk plays
//  NOTHING — the exact silent-failure class houseCARL refuses (Q3). This runs as a POST-WRITE
//  step on a successful create: for every CREATED voiced INFO it computes each response line's
//  expected voice path (VoicePath), checks it against the live VFS (AssetResolver — loose + BSA),
//  and reports a loud "WILL BE SILENT" line when the audio is absent, OR a loud, NAMED reason
//  when the path can't even be computed (no Speaker, etc.) — never a false "fine".
//
//  CORNERSTONE-CLEAN: this is a DIAGNOSTIC over records the engine already wrote — it adds NO
//  per-record write logic and is deliberately SEPARATE from the proven WritePatchBuilder.CreateRecords
//  path (which is untouched). It is driven by BOTH the service (post-create, with the live
//  AssetResolver) and the nested-create CI guard (with a temp AssetResolver over a planted .fuz),
//  so the present/silent/undeterminable verdict is end-to-end testable in CI.
//
//  HOW IT RESOLVES THE GRAPH (the voice path needs more than the FormKey — see VoicePath):
//    • parent topic — found by WALKING the written patch's DialogTopics: the created INFO lives in
//      some topic's Responses (for an existing parent the patch carries its OVERRIDE, deep-copied
//      with EditorID + Quest intact; for a same-call parent it's the new topic). So topic EDID + the
//      Quest link come straight off the patch — no spec threading, works for same-call AND existing parents.
//    • voice type — INFO.Speaker -> Npc.Voice -> VoiceType.EditorID, each resolved patch-first then
//      load-order (a same-call NPC/quest lives in the patch; an existing one in the order). Speaker
//      null -> the runtime quest-alias case -> NO computable path (a NAMED undetermined reason, Q3).
//    • quest EDID — the topic's Quest -> Quest.EditorID (empty when the topic has no quest).
//
//  Resolution reuses the read idiom (ResolveWinner -> GetRecord re-enumerates the winner overlay),
//  cached per FormKey for the run — a create post-step, not a hot bulk scan; a handful of resolves.
// ======================================================================

/// <summary>One created INFO response line's voice verdict: the expected <see cref="FuzPath"/> (the spoken
/// audio — absence ⇒ the line is SILENT) and <see cref="LipPath"/> (lip-sync — absence ⇒ no mouth movement,
/// audio still plays), each with its on-disk presence + the winning provider, plus the
/// <see cref="ReadIncomplete"/> caveat (a BSA failed to read, so an "absent" may merely be unscanned — Q3).</summary>
public sealed record VoiceLine(
    FormKey Info, string TopicEditorId, int ResponseNumber,
    string FuzPath, bool FuzPresent, string? FuzWinner, bool FuzAmbiguous,
    string LipPath, bool LipPresent,
    bool ReadIncomplete);

/// <summary>A created INFO whose voice path could NOT be computed, with the NAMED reason (Q3 — never a
/// silent "fine"): no Speaker (voice type assigned at runtime from a quest alias), an unresolvable
/// speaker/voice-type, etc. The whole INFO is reported once; its lines are not checked.</summary>
public sealed record VoiceUndetermined(FormKey Info, string TopicEditorId, string Reason);

/// <summary>The voice-coverage report for one create call: per-line presence verdicts and per-INFO
/// undeterminable reasons. <see cref="IsEmpty"/> when the call created no voiced INFO lines.</summary>
public sealed record VoiceReport(IReadOnlyList<VoiceLine> Lines, IReadOnlyList<VoiceUndetermined> Undetermined)
{
    /// <summary>The voice check itself could not run (the patch wouldn't re-open, the walk threw) — surfaced, never a
    /// silent skip (Q3). The create ALREADY SUCCEEDED when this is set; it just means "I couldn't verify voice
    /// coverage", not "the write failed". Null on a clean run.</summary>
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
    /// call created no INFOs. A resolve MISS is a NAMED undetermined reason (Q3); a whole-check failure (the patch won't
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

        // A created INFO not found under any topic is a real inconsistency — surfaced, never silently dropped (Q3).
        foreach (var fk in infoKeys)
            if (!foundInfos.Contains(fk))
                undetermined.Add(new VoiceUndetermined(fk, "",
                    "created but not found under any topic in the written patch — can't determine its voice path; inspect the patch in xEdit."));

        return new VoiceReport(lines, undetermined);
    }

    /// <summary>Resolve one INFO's voice graph (topic+quest EDIDs, speaker voice type) and emit either a per-line
    /// presence verdict for each spoken response, or ONE named undetermined reason (Q3) when the voice folder can't
    /// be computed. An INFO with no spoken response lines (a link/branch node) yields nothing — there is no voice to check.</summary>
    static void CheckInfo(IDialogResponsesGetter info, IDialogTopicGetter topic,
                          Func<FormKey, IMajorRecordGetter?> resolve, AssetResolver.AssetView av,
                          List<VoiceLine> lines, List<VoiceUndetermined> undetermined)
    {
        var topicEdid = topic.EditorID ?? "";

        // Speaker -> the voice type (folder). Null Speaker is the runtime quest-alias case: no computable path (Q3).
        var speakerFk = NonNull(info.Speaker.FormKeyNullable);
        if (speakerFk is null)
        {
            undetermined.Add(new VoiceUndetermined(info.FormKey, topicEdid,
                "no Speaker set — the voice type (folder) is assigned at runtime from the quest alias, so the .fuz path can't be computed. " +
                "Set Speaker on this line to make it checkable, or verify the audio yourself."));
            return;
        }
        if (resolve(speakerFk.Value) is not INpcGetter npc)
        {
            undetermined.Add(new VoiceUndetermined(info.FormKey, topicEdid,
                $"Speaker NPC {speakerFk.Value} not found in the patch or load order — can't resolve the voice type."));
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

        // One .fuz/.lip check per spoken response line (its ResponseNumber names the file — used as-authored, Q3).
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

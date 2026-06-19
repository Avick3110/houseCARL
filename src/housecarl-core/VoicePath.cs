using Mutagen.Bethesda.Plugins;

namespace HousecarlCore;

// ======================================================================
//  VoicePath — a dialogue line's voice .fuz/.lip path is a PURE TRANSFORM of
//  {the INFO's FormKey, the speaker's voice type, the parent quest + topic EditorIDs,
//  the response number} (nested-dialogue plan §3.5; sibling of FaceGenPath). NO load
//  order, NO runtime FormID, NO file read: Skyrim resolves the spoken audio by
//  FILESYSTEM CONVENTION (there is no Voice/Wav/Lip/FileName field on INFO/DialogResponse
//  anywhere in the Mutagen corpus — see memory reference_skyrim_voice_file_naming), so a
//  byte-valid INFO with no .fuz on disk plays NOTHING. This computes the path that audio
//  must live at, so the create flow can check it and warn loud (Q3 — never a silent dead line).
//
//  THE RULE (the keystone — get it wrong and the "place audio here" path is wrong, a Q3
//  silent-wrong answer; authoritative source is xEdit's InfoFileName, Export dialogues.pas,
//  verified empirically 2026-06-19 against real loose .fuz files — e.g.
//  Walks-Distant-Sands: WDSMainQue_WDSMainQuest1Co_00000013_1.fuz):
//    Sound\Voice\<DefiningPlugin>\<VoiceType>\<QuestEDID[..10]>_<TopicEDID[..15]>_<00+6hexLocalID>_<ResponseNum>.fuz
//
//    • FOLDER1 = the DEFINING PLUGIN — the plugin in the INFO's FormKey (fk.ModKey.FileName),
//      WITH extension, NOT the conflict winner. For create-into-a-new-patch that's the new
//      patch (clean). For an override/in-place edit it would be the WINNER's plugin and the
//      audio lives with the original (the override trap — out of scope for the create path,
//      which assigns fresh patch-local 0x800+ ids).
//    • FOLDER2 = the speaker's VoiceType EditorID (INFO.Speaker -> Npc.Voice -> VoiceType.EditorID),
//      empirically the on-disk folder name (e.g. "FemaleArgonian"). Resolved by the caller — when
//      it can't be (no Speaker; the runtime quest-alias case), there is NO path to compute (Q3).
//    • QUEST seg = the parent topic's Quest's EditorID, truncated to 10 chars (empirically
//      "WDSMainQue" = 10). Empty when the topic has no quest (the segment is just empty).
//    • TOPIC seg = the parent DialogTopic's EditorID, truncated to 15 chars (empirically
//      "WDSMainQuest1Co" = 15). Often EMPTY (a topic with no EditorID) -> the "__" double underscore.
//    • ID    = the load-order index byte MASKED to "00", then the 6-hex local id: "00" + fk.ID("X6")
//      (xEdit IntToHex(InfoFormID and $FFFFFF, 8)) — load-order-INDEPENDENT, exactly the
//      FaceGenPath masking. e.g. local 0x13 -> "00000013".
//    • NUM   = the DialogResponse's ResponseNumber (one .fuz per spoken line: _1, _2, …).
//
//  Built in core (not in the create tool) so the read side (a future voice-coverage diagnostic /
//  the Unit C completeness validator) reuses the EXACT same transform — one home for the keystone.
//  Backslash-separated to match the AssetResolver match form (backslash, OrdinalIgnoreCase);
//  resolution is case-insensitive, so EDID case is preserved as-authored and the hex is upper.
// ======================================================================

/// <summary>Which voice file: the spoken audio (.fuz — absence = the line is SILENT) or the
/// lip-sync (.lip — absence = no mouth movement, audio still plays). The "will be silent" verdict
/// keys on the .fuz; the .lip is reported as a secondary caveat.</summary>
public enum VoiceFile { Fuz, Lip }

public static class VoicePath
{
    /// <summary>xEdit InfoFileName truncates the Quest EditorID to its first 10 chars (Copy(QuestEDID, 1, 10)),
    /// verified empirically ("WDSMainQue" = 10).</summary>
    public const int QuestEdidMax = 10;

    /// <summary>…and the Topic/DIAL EditorID to its first 15 chars (Copy(TopicEDID, 1, 15)), verified
    /// empirically ("WDSMainQuest1Co" = 15).</summary>
    public const int TopicEdidMax = 15;

    /// <summary>The Data-relative voice path for one created INFO line + slot — the pure transform (see the
    /// file header). <paramref name="info"/> supplies BOTH the defining-plugin folder (info.ModKey.FileName)
    /// and the 8-hex id ("00" + info.ID:X6); <paramref name="voiceType"/> is the speaker's VoiceType EditorID
    /// folder; <paramref name="questEdid"/>/<paramref name="topicEdid"/> are the parent topic's quest+topic
    /// EditorIDs (each truncated; null/empty -> an empty segment, a real on-disk shape); <paramref name="responseNumber"/>
    /// is the spoken line's ResponseNumber. Backslash-separated.</summary>
    public static string For(FormKey info, string voiceType, string? questEdid, string? topicEdid,
                             int responseNumber, VoiceFile kind)
    {
        var plugin = info.ModKey.FileName.ToString();        // the DEFINING plugin in the FormKey, WITH extension
        var id = "00" + info.ID.ToString("X6");              // index byte masked to 00, then the 6-hex local id
        var q = Trunc(questEdid, QuestEdidMax);
        var t = Trunc(topicEdid, TopicEdidMax);
        var ext = kind == VoiceFile.Fuz ? "fuz" : "lip";
        return $@"Sound\Voice\{plugin}\{voiceType}\{q}_{t}_{id}_{responseNumber}.{ext}";
    }

    /// <summary>First <paramref name="max"/> chars of an EditorID (xEdit's Copy(edid, 1, max)); empty stays empty
    /// (the legitimate "__" double-underscore shape for a topic with no EditorID).</summary>
    static string Trunc(string? s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max));
}

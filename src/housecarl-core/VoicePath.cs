using Mutagen.Bethesda.Plugins;

namespace HousecarlCore;

// VoicePath — a dialogue line's voice .fuz/.lip path as a PURE TRANSFORM of the INFO's FormKey, the speaker's voice
// type, the parent quest and topic EditorIDs and the response number; the shape is in docs/architecture/assets.md.

/// <summary>Which voice file: the spoken audio (.fuz — absence means the line is SILENT) or the lip-sync (.lip).</summary>
public enum VoiceFile { Fuz, Lip }

public static class VoicePath
{
    /// <summary>xEdit InfoFileName truncates the Quest EditorID to its first 10 chars (Copy(QuestEDID, 1, 10)).</summary>
    public const int QuestEdidMax = 10;

    /// <summary>…and the Topic/DIAL EditorID to its first 15 chars (Copy(TopicEDID, 1, 15)).</summary>
    public const int TopicEdidMax = 15;

    /// <summary>The Data-relative voice path for one INFO line + slot. <paramref name="voiceType"/> is the speaker's
    /// VoiceType EditorID folder; the two EditorIDs are truncated, and empty stays empty.</summary>
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

    /// <summary>First <paramref name="max"/> chars of an EditorID (xEdit's Copy(edid, 1, max)); empty stays empty.</summary>
    static string Trunc(string? s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max));
}

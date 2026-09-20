using System.Globalization;

namespace HousecarlCore;

/// <summary>The runtime FormID notation — the eight-hex form the game, the console and the logs print, with no plugin name in it. Pure text; turning one into a FormKey needs the order's index tables.</summary>
/// <summary>What the order can say about one record's runtime address: the eight-hex <see cref="FormId"/>, or a <see cref="Note"/> saying why there is no unambiguous one; both null means no runtime address.</summary>
public readonly record struct RuntimeAddress(string? FormId, string? Note);

public static class RuntimeFormId
{
    /// <summary>The high-byte signature of a DYNAMIC runtime FormID; the number lives in <see cref="FormIdRange"/>.</summary>
    public const uint DynamicPrefix = FormIdRange.DynamicIndexPrefix;

    /// <summary>The high-byte mask that isolates a runtime FormID's index byte.</summary>
    public const uint IndexByteMask = FormIdRange.IndexByteMask;

    /// <summary>How many hex digits a runtime FormID has, once an optional <c>0x</c> is stripped.</summary>
    public const int Digits = 8;

    /// <summary>True when <paramref name="text"/> is a runtime FormID: eight hex digits, optionally <c>0x</c>-prefixed; a plugin-qualified token carries a colon and never lands here.</summary>
    public static bool TryParse(string? text, out uint value)
    {
        value = 0;
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s) || s.Contains(':')) return false;
        if (s.Length > 2 && s[0] == '0' && (s[1] == 'x' || s[1] == 'X')) s = s[2..];
        if (s.Length != Digits) return false;
        return uint.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>The sentence for a HYBRID token — eight runtime digits AND a plugin suffix — naming the six-digit plugin form to write instead; null for anything that is not a hybrid.</summary>
    public static string? HybridNote(string? text)
    {
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        int c = s.IndexOf(':');
        if (c <= 0 || c == s.Length - 1) return null;
        var plugin = s[(c + 1)..];
        if (!TryParse(s[..c], out uint v)) return null;
        uint local = FormIdRange.LocalObjectId(v);
        return $"'{s}' mixes the two FormID forms: eight digits is the RUNTIME form the game, the console and the " +
               $"logs print — its leading digits are the load-order index, not part of the record's id — and a " +
               $"plugin-qualified FormID takes SIX. Write '{local:X6}:{plugin}'.";
    }

    /// <summary>Is this token addressed to the light block (the shared <c>FE</c> index)?</summary>
    public static bool IsLight(uint value) => (value & IndexByteMask) == FormIdRange.LightMasterIndexPrefix;

    /// <summary>Is this token a dynamic form the game made at runtime (<c>FF</c>)?</summary>
    public static bool IsDynamic(uint value) => (value & IndexByteMask) == DynamicPrefix;

    /// <summary>The 12-bit light index of an <see cref="IsLight"/> token — which light plugin it addresses.</summary>
    public static uint LightIndex(uint value) => (value >> FormIdRange.LightIndexShift) & FormIdRange.LightIndexMask;

    /// <summary>The load index of a full-plugin token — which non-light plugin it addresses.</summary>
    public static uint LoadIndex(uint value) => value >> FormIdRange.FullIndexShift;

    /// <summary>The eight-hex spelling, the way the console and the logs print it.</summary>
    public static string Format(uint value) => value.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>Build the runtime FormID of a record; false when a LIGHT plugin's record sits above the ESL window, where masking it into the window would name another record too.</summary>
    public static bool TryCompose(bool light, int slot, uint localId, out uint value)
    {
        if (light && FormIdRange.AboveEslWindow(localId)) { value = 0; return false; }
        value = light
            ? FormIdRange.LightMasterIndexPrefix | ((uint)slot << FormIdRange.LightIndexShift) | (localId & FormIdRange.LightObjectIdMask)
            : ((uint)slot << FormIdRange.FullIndexShift) | (localId & FormIdRange.ObjectIdMask);
        return true;
    }

    /// <summary>The one sentence a lane prints where the runtime FormID would have gone, when <see cref="TryCompose"/> refused it.</summary>
    public static string OutOfWindowNote(string plugin, uint localId) =>
        $"no runtime FormID: '{plugin}' is flagged light but not compacted, and this record's object ID " +
        $"0x{localId:X6} is above the ESL window ceiling 0x{FormIdRange.EslWindowCeiling:X3}, so the form the " +
        "game prints for it names another record too — address it as 'XXXXXX:Plugin.esp'.";
}

using System.Globalization;

namespace HousecarlCore;

/// <summary>
/// The runtime FormID notation — the eight-hex form the game, Papyrus logs, the console, SKSE logs and crash logs
/// print, with no plugin name in it: <c>FExxxYYY</c> for a light plugin (the shared <c>FE</c> index, a 3-hex light
/// index, a 3-hex local id) and <c>XX######</c> for a full one (a 2-hex load index, a 6-hex local id).
///
/// <para>This class is pure text — splitting a token into its index and local id, and putting one back together.
/// Turning a runtime FormID into a FormKey needs the active order's index tables, which live on
/// <see cref="LoadOrderResolver"/>; every tool door goes through <c>IndexView.ParseFormId</c> rather than doing
/// its own arithmetic.</para>
/// </summary>
/// <summary>What the order can say about one record's runtime address: the eight-hex <see cref="FormId"/> the game
/// prints, or — when there is no unambiguous one — a <see cref="Note"/> saying why. Both null means the order gives
/// the record no runtime address at all (its plugin is not active in this build), which lanes render as nothing.</summary>
public readonly record struct RuntimeAddress(string? FormId, string? Note);

public static class RuntimeFormId
{
    /// <summary>The high-byte signature of a DYNAMIC runtime FormID. The game creates these while playing and they
    /// live only in a save game, so no plugin defines one. The number lives in <see cref="FormIdRange"/>.</summary>
    public const uint DynamicPrefix = FormIdRange.DynamicIndexPrefix;

    /// <summary>The high-byte mask that isolates a runtime FormID's index byte — from
    /// <see cref="FormIdRange.IndexByteMask"/>, the one home for the split.</summary>
    public const uint IndexByteMask = FormIdRange.IndexByteMask;

    /// <summary>How many hex digits a runtime FormID has, once an optional <c>0x</c> is stripped.</summary>
    public const int Digits = 8;

    /// <summary>True when <paramref name="text"/> is a runtime FormID: eight hex digits, optionally prefixed
    /// <c>0x</c>. A <c>XXXXXX:Plugin.esp</c> token always carries a colon and a plugin name, so it never lands
    /// here — that is the whole ambiguity rule between the two notations.</summary>
    public static bool TryParse(string? text, out uint value)
    {
        value = 0;
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s) || s.Contains(':')) return false;
        if (s.Length > 2 && s[0] == '0' && (s[1] == 'x' || s[1] == 'X')) s = s[2..];
        if (s.Length != Digits) return false;
        return uint.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
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

    /// <summary>Build the runtime FormID of a record: its plugin's runtime slot plus the record's local id. False
    /// when a LIGHT plugin's record sits above the ESL window (<see cref="FormIdRange.AboveEslWindow"/>) — the
    /// plugin was flagged light but never compacted, so masking it into the 12-bit window would print the same
    /// eight digits as its in-window neighbour and read back to that other record. There is no unambiguous answer,
    /// so there is no composed value; the caller says why instead (<see cref="OutOfWindowNote"/>).</summary>
    public static bool TryCompose(bool light, int slot, uint localId, out uint value)
    {
        if (light && FormIdRange.AboveEslWindow(localId)) { value = 0; return false; }
        value = light
            ? FormIdRange.LightMasterIndexPrefix | ((uint)slot << FormIdRange.LightIndexShift) | (localId & FormIdRange.LightObjectIdMask)
            : ((uint)slot << FormIdRange.FullIndexShift) | (localId & FormIdRange.ObjectIdMask);
        return true;
    }

    /// <summary>The one sentence a lane prints where the runtime FormID would have gone, when
    /// <see cref="TryCompose"/> refused it.</summary>
    public static string OutOfWindowNote(string plugin, uint localId) =>
        $"no runtime FormID: '{plugin}' is flagged light but not compacted, and this record's object ID " +
        $"0x{localId:X6} is above the ESL window ceiling 0x{FormIdRange.EslWindowCeiling:X3}, so the form the " +
        "game prints for it names another record too — address it as 'XXXXXX:Plugin.esp'.";
}

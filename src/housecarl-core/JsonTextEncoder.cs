using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace HousecarlCore;

/// <summary>How houseCARL's json spells a character — the ONE encoder behind every json the server writes, inline
/// response and spilled artifact alike, so the same row cannot spell the same name two ways depending on where it
/// landed.
///
/// <para><c>Utf8JsonWriter</c>'s default escapes every character above ASCII to <c>\uXXXX</c>, which turned a
/// Japanese or accented name into a run of escapes (#754). <c>UnicodeRanges.All</c> widens only that — the
/// HTML-sensitive characters (<c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>, <c>'</c>, <c>+</c>) are escaped exactly as
/// before.</para>
///
/// <para>The bound is the plane: <c>UnicodeRanges.All</c> is the Basic Multilingual Plane, U+0000 to U+FFFF, so a
/// character ABOVE it — an emoji, a CJK Extension-B ideograph — still rides as its <c>\uXXXX\uXXXX</c> surrogate
/// pair. .NET offers no encoder that widens past the BMP without also unescaping the HTML-sensitive set
/// (<c>UnsafeRelaxedJsonEscaping</c> does both), and the escapes parse back to the identical string, so the plane is
/// where this stops. A cap measured in characters is right on both sides of it: an escape is ASCII and
/// <see cref="CharCountedStream"/> counts it as the characters it is.</para></summary>
public static class JsonTextEncoder
{
    /// <summary>The encoder itself, for a writer that needs its own options (a different indentation, say).</summary>
    public static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.Create(UnicodeRanges.All);

    /// <summary>One line, one row: the artifact's JSONL rows and its manifest line, which are newline-delimited and
    /// so cannot be indented.</summary>
    public static readonly JsonWriterOptions OneLine = new() { Encoder = Encoder };
}

using System.Text;
using System.Text.Unicode;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Binary.Streams;
using Mutagen.Bethesda.Strings;
using Mutagen.Bethesda.Strings.DI;

namespace HousecarlCore;

/// <summary>
/// The text encoding every plugin read and write uses. One home, so the read side and the write side cannot disagree
/// about what a name's bytes mean.
///
/// <para>Mutagen picks an encoding from the target language, and Skyrim SE + English is the one language pairing
/// whose answer (<c>MutagenEncoding._1252</c>) carries no UTF-8 lane at all. The Japanese community's standard
/// setup is <c>sLanguage=ENGLISH</c> with the <c>*_English.STRINGS</c> tables replaced by UTF-8 translations, and
/// non-localized ESPs whose inline <c>FULL</c> fields carry UTF-8 too, so every translated name read as Windows-1252
/// mojibake — and, worse, a Windows-1252 ENCODER turns any character outside 1252 into <c>?</c>, so an in-place edit
/// or a copy of such a record destroyed the text silently.</para>
///
/// <para><b>The two sides are not symmetric, and that is the point.</b> READING, the bytes are ambiguous and UTF-8
/// goes first: valid UTF-8 is read as UTF-8, anything else falls back to what Mutagen would have chosen for the
/// language. WRITING, nothing is ambiguous — the value is already a string — so Windows-1252 goes first and UTF-8
/// carries only what 1252 has no spelling for. A UTF-8-first write would re-encode every Western plugin it
/// re-serializes, and an in-place edit re-serializes the WHOLE file: a French name, and an accented asset path in a
/// plain string subrecord, that were 1252 bytes before the edit would come back as UTF-8 bytes the game does not
/// read at <c>sLanguage=ENGLISH</c>.</para>
///
/// <para><b>Why not Mutagen's own <c>_utf8_1252</c>.</b> Its UTF-8 half is <c>Encoding.UTF8</c>, which replaces
/// invalid bytes with U+FFFD instead of throwing, so the 1252 fallback beneath it is never reached and a real
/// Windows-1252 name comes back as replacement characters.</para>
///
/// <para><b>The two theoretical misses, one per side.</b> Reading, a Windows-1252 string whose bytes also happen to
/// be valid UTF-8 is read as UTF-8. Writing, a Latin-accented string that came out of a UTF-8 file is written back
/// as Windows-1252, because 1252 can spell it. Neither is guarded: the bytes are genuinely ambiguous and any guard
/// would have to guess.</para>
/// </summary>
public static class PluginTextEncoding
{
    /// <summary>UTF-8 that throws on invalid bytes, so it is never the one that silently substitutes.</summary>
    static readonly Encoding StrictUtf8Encoding =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static readonly IMutagenEncoding StrictUtf8 = new MutagenEncodingWrapper(StrictUtf8Encoding);

    /// <summary>Windows-1252 that THROWS on a character it cannot spell rather than substituting <c>?</c> — the
    /// throw is what lets the UTF-8 fallback beneath it fire.</summary>
    static readonly IMutagenEncoding Strict1252 = new MutagenEncodingWrapper(
        CodePagesEncodingProvider.Instance.GetEncoding(
            1252, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback)!);

    /// <summary>The READ encoding for a plain string subrecord: valid UTF-8 is UTF-8, everything else is
    /// Windows-1252 (which is what this half defaults to, unconditionally and never from the language).</summary>
    public static readonly IMutagenEncoding Utf8Then1252 = new Utf8First(MutagenEncoding._1252);

    /// <summary>The encoding for a LOCALIZED plugin's <c>.STRINGS</c> tables, per language. Language selection is
    /// untouched: Mutagen still chooses for the target language, and its choice becomes the fallback beneath UTF-8.
    /// English gains the UTF-8 lane it lacked; every other language is unchanged for any byte sequence that is not
    /// valid UTF-8.</summary>
    public static readonly IMutagenEncodingProvider Provider = new LanguageProvider();

    /// <summary>The read parameters for a plain overlay open or mutable import.</summary>
    public static readonly BinaryReadParameters Read =
        BinaryReadParameters.Default with { StringsParam = Strings(null) };

    /// <summary>The encodings a write embeds in the plugin: Windows-1252 first, so a plugin that was already 1252
    /// comes back byte-identical, and UTF-8 only for a string 1252 cannot spell, so a Japanese name is written as
    /// itself instead of as <c>?</c>. 1252 is Mutagen's own write default and what the game reads at the language
    /// houseCARL targets.</summary>
    public static readonly EncodingBundle Write = new(
        new MutagenEncodingFallbackWrapper(Strict1252, StrictUtf8),
        new MutagenEncodingFallbackWrapper(Strict1252, StrictUtf8));

    /// <summary>Apply the encoding to a strings-read parameter set the caller has already shaped (the resolver's
    /// game-Data redirect), leaving everything else on it alone.
    ///
    /// <para><c>NonTranslated</c> — plain string subrecords — defaults to Windows-1252 unconditionally, never from
    /// the language, so it takes an explicit override. <c>NonLocalized</c> — an inline <c>FULL</c> in a plugin that
    /// is not flagged localized — is read from the provider for the target language, so it is deliberately left to
    /// the provider rather than pinned by an override: that is what keeps language selection untouched.</para></summary>
    public static StringsReadParameters Strings(StringsReadParameters? baseline)
        => (baseline ?? new StringsReadParameters()) with
        {
            EncodingProvider = Provider,
            NonTranslatedEncodingOverride = Utf8Then1252,
        };

    /// <summary>UTF-8 over whatever Mutagen would have chosen for the language.</summary>
    sealed class LanguageProvider : IMutagenEncodingProvider
    {
        public IMutagenEncoding GetEncoding(GameRelease release, Language language)
            => new Utf8First(MutagenEncoding.GetEncoding(release, language));
    }

    /// <summary>Decode as UTF-8 when the bytes ARE UTF-8, otherwise as the fallback — decided by a validity check,
    /// not by catching a decoder exception. Mutagen's own <c>MutagenEncodingFallbackWrapper</c> is a try/catch, so a
    /// strict-UTF-8 primary would make every non-ASCII Windows-1252 string in a sweep pay a throw; <c>Utf8.IsValid</c>
    /// answers the same question as a branch. The encode direction is UTF-8 — a read encoding never encodes, and what
    /// a write embeds is <see cref="Write"/>.</summary>
    sealed class Utf8First : IMutagenEncoding
    {
        readonly IMutagenEncoding _fallback;
        internal Utf8First(IMutagenEncoding fallback) => _fallback = fallback;

        public string GetString(ReadOnlySpan<byte> bytes)
            => Utf8.IsValid(bytes) ? StrictUtf8Encoding.GetString(bytes) : _fallback.GetString(bytes);

        public int GetByteCount(ReadOnlySpan<char> str) => StrictUtf8Encoding.GetByteCount(str);

        public int GetBytes(ReadOnlySpan<char> chars, Span<byte> bytes) => StrictUtf8Encoding.GetBytes(chars, bytes);
    }
}

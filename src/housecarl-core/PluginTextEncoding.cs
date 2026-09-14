using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Binary.Streams;
using Mutagen.Bethesda.Strings;
using Mutagen.Bethesda.Strings.DI;

namespace HousecarlCore;

/// <summary>
/// The text encoding every plugin read and write uses — strict UTF-8 first, Windows-1252 for anything that is not
/// valid UTF-8. One home, so the read side and the write side cannot disagree about what a name's bytes mean.
///
/// <para>Mutagen picks an encoding from the target language, and Skyrim SE + English is the one language pairing
/// whose answer (<c>MutagenEncoding._1252</c>) carries no UTF-8 lane at all. The Japanese community's standard
/// setup is <c>sLanguage=ENGLISH</c> with the <c>*_English.STRINGS</c> tables replaced by UTF-8 translations, and
/// non-localized ESPs whose inline <c>FULL</c> fields carry UTF-8 too, so every translated name read as Windows-1252
/// mojibake — and, worse, a Windows-1252 ENCODER turns any character outside 1252 into <c>?</c>, so an in-place edit
/// or a copy of such a record destroyed the text silently.</para>
///
/// <para><b>Why not Mutagen's own <c>_utf8_1252</c>.</b> Its UTF-8 half is <c>Encoding.UTF8</c>, which replaces
/// invalid bytes with U+FFFD instead of throwing, so the 1252 fallback beneath it is never reached and a real
/// Windows-1252 name comes back as replacement characters. The fallback wrapper is Mutagen's and is used as-is; only
/// the UTF-8 half is rebuilt strict (<c>throwOnInvalidBytes</c>), which is what makes the fallback work.</para>
///
/// <para><b>The one theoretical miss.</b> A Windows-1252 string whose bytes also happen to be valid UTF-8 decodes as
/// UTF-8 and reads differently than it did. Nothing guards it: the bytes are genuinely ambiguous, and any guard
/// would have to guess.</para>
/// </summary>
public static class PluginTextEncoding
{
    /// <summary>UTF-8 that THROWS on invalid bytes — the strictness is the whole mechanism, because the fallback
    /// below only fires on an exception.</summary>
    static readonly IMutagenEncoding StrictUtf8 =
        new MutagenEncodingWrapper(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));

    /// <summary>Strict UTF-8, falling back to Windows-1252. Encoding always emits UTF-8 (a .NET string never fails
    /// the strict encoder), so a write can no longer turn a Japanese name into <c>?</c>.</summary>
    public static readonly IMutagenEncoding Utf8Then1252 =
        new MutagenEncodingFallbackWrapper(StrictUtf8, MutagenEncoding._1252);

    /// <summary>The encoding for a LOCALIZED plugin's <c>.STRINGS</c> tables, per language. Language selection is
    /// untouched: Mutagen still chooses for the target language, and its choice becomes the fallback beneath strict
    /// UTF-8. English gains the UTF-8 lane it lacked; every other language is unchanged for any byte sequence that is
    /// not valid UTF-8.</summary>
    public static readonly IMutagenEncodingProvider Provider = new LanguageProvider();

    /// <summary>The read parameters for a plain overlay open or mutable import.</summary>
    public static readonly BinaryReadParameters Read =
        BinaryReadParameters.Default with { StringsParam = Strings(null) };

    /// <summary>The encodings a write embeds in the plugin — the same encoding as the read side, so a record read and
    /// written straight back round-trips.</summary>
    public static readonly EncodingBundle Write = new(Utf8Then1252, Utf8Then1252);

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

    /// <summary>Strict UTF-8 over whatever Mutagen would have chosen for the language.</summary>
    sealed class LanguageProvider : IMutagenEncodingProvider
    {
        public IMutagenEncoding GetEncoding(GameRelease release, Language language)
            => new MutagenEncodingFallbackWrapper(StrictUtf8, MutagenEncoding.GetEncoding(release, language));
    }
}

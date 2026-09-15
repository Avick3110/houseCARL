using System.Collections.Concurrent;
using System.Text;
using System.Text.Unicode;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Binary.Streams;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Strings;
using Mutagen.Bethesda.Strings.DI;

namespace HousecarlCore;

/// <summary>Which encoding a plugin's own bytes turned out to be in, as the read resolved it.</summary>
public enum PluginTextLane
{
    /// <summary>Nothing outside ASCII was read from it, so the two encodings agree and either writes it back
    /// unchanged.</summary>
    AsciiOnly,

    /// <summary>At least one non-ASCII string in it was valid UTF-8.</summary>
    Utf8,

    /// <summary>Non-ASCII was read from it and none of it was valid UTF-8, so it is in the language's legacy
    /// codepage.</summary>
    Legacy,
}

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
/// <para><b>A file has ONE encoding, and the decision is per file — never per string.</b> Reading, the bytes are
/// ambiguous, so each string is decoded as UTF-8 when it IS valid UTF-8 and as the language's codepage otherwise —
/// and which lane each plugin's own strings came back through is recorded as it is read (<see cref="LaneOf"/>).
/// Writing, nothing is ambiguous, so one encoding covers the whole file: an in-place write uses the lane the read of
/// that same file resolved to, and a NEW file (a patch, a merge, a compacted copy) uses UTF-8 if any plugin
/// contributing to it resolved as UTF-8 and the language default otherwise. Deciding per string instead would flip a
/// UTF-8 French plugin's <c>é</c> from <c>C3 A9</c> to <c>E9</c> on an in-place edit — a round trip that was
/// byte-exact before any of this — and would leave a Japanese plugin carrying one accented Latin name written half
/// in UTF-8 and half in 1252, which no single-encoding reader gets right.</para>
///
/// <para><b>Why not Mutagen's own <c>_utf8_1252</c>.</b> Its UTF-8 half is <c>Encoding.UTF8</c>, which replaces
/// invalid bytes with U+FFFD instead of throwing, so the 1252 fallback beneath it is never reached and a real
/// Windows-1252 name comes back as replacement characters. Nothing here throws to decide a lane, on either side:
/// Mutagen's fallback wrapper is a try/catch, and a sweep of a translated order would pay one throw per string.</para>
///
/// <para><b>The misses, and they are not guarded.</b> Reading, a Windows-1252 string whose bytes also happen to be
/// valid UTF-8 is read as UTF-8 — the bytes are genuinely ambiguous and a guard would have to guess. Writing, a value
/// typed by hand into a patch whose contributing plugins were all ASCII-only is written in the language default, so a
/// Japanese name pasted into an ASCII-only patch still becomes <c>?</c>; that is the one shape this rule does not
/// reach.</para>
///
/// <para>The lane record is keyed by plugin FILENAME, which is how houseCARL names a plugin everywhere. Two installed
/// plugins sharing a filename share one lane record; a fresh open of a name replaces it, so a file edited on disk is
/// re-resolved the next time it is read.</para>
/// </summary>
public static class PluginTextEncoding
{
    /// <summary>UTF-8 that throws on invalid bytes, so it is never the one that silently substitutes.</summary>
    static readonly Encoding StrictUtf8Encoding =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static readonly IMutagenEncoding StrictUtf8 = new MutagenEncodingWrapper(StrictUtf8Encoding);

    /// <summary>The language default for the release and language houseCARL targets — Mutagen's own answer, so a
    /// plugin that is not UTF-8 is written exactly as Mutagen would have written it.</summary>
    static readonly IMutagenEncoding LanguageDefault =
        MutagenEncoding.GetEncoding(GameRelease.SkyrimSE, Language.English);

    static readonly EncodingBundle Utf8Bundle = new(StrictUtf8, StrictUtf8);
    static readonly EncodingBundle LegacyBundle = new(LanguageDefault, LanguageDefault);

    /// <summary>What each plugin's own strings decoded as, by filename. Written by the read, read by the write.</summary>
    static readonly ConcurrentDictionary<string, LaneSink> Lanes =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The read parameters for opening one plugin — an overlay or a mutable import. Per plugin, because the
    /// lane it resolves to is recorded against that plugin's name as its strings are decoded.</summary>
    public static BinaryReadParameters ReadFor(string pluginPath) => ReadWith(pluginPath, null);

    /// <summary>The same, over a strings-read parameter set the caller has already shaped (the resolver's game-Data
    /// redirect), leaving everything else on it alone.
    ///
    /// <para><c>NonTranslated</c> — plain string subrecords — defaults to Windows-1252 unconditionally, never from
    /// the language, so it takes an explicit override. <c>NonLocalized</c> — an inline <c>FULL</c> in a plugin that
    /// is not flagged localized — is read from the provider for the target language, so it is deliberately left to
    /// the provider rather than pinned by an override: that is what keeps language selection untouched.</para></summary>
    public static BinaryReadParameters ReadWith(string pluginPath, StringsReadParameters? baseline)
    {
        // A fresh sink per open, so a file replaced on disk is re-resolved rather than keeping the old answer.
        var sink = new LaneSink();
        Lanes[Path.GetFileName(pluginPath)] = sink;
        var reader = new Utf8First(LanguageDefault, sink);
        return BinaryReadParameters.Default with
        {
            StringsParam = (baseline ?? new StringsReadParameters()) with
            {
                EncodingProvider = new LanguageProvider(sink),
                NonTranslatedEncodingOverride = reader,
            },
        };
    }

    /// <summary>What the read resolved that plugin's own bytes to be, or <see cref="PluginTextLane.AsciiOnly"/> for a
    /// plugin nothing has read a non-ASCII string out of.</summary>
    public static PluginTextLane LaneOf(string pluginNameOrPath)
        => Lanes.TryGetValue(Path.GetFileName(pluginNameOrPath), out var s) ? s.Lane : PluginTextLane.AsciiOnly;

    /// <summary>The encodings an IN-PLACE write embeds: the lane the read of that same file resolved to, so a UTF-8
    /// plugin comes back UTF-8 and a Windows-1252 one comes back Windows-1252, both byte-identical where the write
    /// did not change the text.</summary>
    public static EncodingBundle WriteInPlace(string targetPath)
        => LaneOf(targetPath) == PluginTextLane.Utf8 ? Utf8Bundle : LegacyBundle;

    /// <summary>The encodings a NEW file embeds — a patch, a merge, a compacted copy: UTF-8 if any plugin
    /// contributing to it resolved as UTF-8, the language default otherwise.
    ///
    /// <para>Three places say who contributed, cheapest first: the file's own name (the extend lane rewrites a patch
    /// that was read before it was added to), its declared masters, and — the one that always holds — the plugin each
    /// record it carries came FROM, which is its FormKey's ModKey. The master list is the obvious answer and not a
    /// sufficient one: Mutagen computes it during the write, so before the write it can still be empty.</para>
    ///
    /// <para>The bound: a lane that REMAPS its records into the output's own ModKey — a merge — has no provenance
    /// left in the FormKeys, so it answers from the donors' master lists and the output name alone. And the miss, not
    /// guarded: a value typed by hand into a patch whose contributing plugins were all ASCII-only is written in the
    /// language default, so a Japanese name pasted into an ASCII-only patch still becomes <c>?</c>.</para></summary>
    public static EncodingBundle WriteNew(IModGetter mod)
    {
        if (LaneOf(mod.ModKey.FileName.String) == PluginTextLane.Utf8) return Utf8Bundle;
        foreach (var m in mod.MasterReferences)
            if (LaneOf(m.Master.FileName.String) == PluginTextLane.Utf8) return Utf8Bundle;
        foreach (var r in mod.EnumerateMajorRecords())
            if (LaneOf(r.FormKey.ModKey.FileName.String) == PluginTextLane.Utf8) return Utf8Bundle;
        return LegacyBundle;
    }

    /// <summary>What each plugin's strings decoded as, accumulated across one open. UTF-8 wins over legacy: a plugin
    /// carrying one string that IS valid UTF-8 is a UTF-8 file whose other non-ASCII strings the ambiguity rule read
    /// the other way.</summary>
    sealed class LaneSink
    {
        volatile bool _utf8, _legacy;
        internal void SawUtf8() => _utf8 = true;
        internal void SawLegacy() => _legacy = true;
        internal PluginTextLane Lane =>
            _utf8 ? PluginTextLane.Utf8 : _legacy ? PluginTextLane.Legacy : PluginTextLane.AsciiOnly;
    }

    /// <summary>UTF-8 over whatever Mutagen would have chosen for the language — the encoding a LOCALIZED plugin's
    /// <c>.STRINGS</c> tables are read with. Language selection is untouched: Mutagen still chooses for the target
    /// language, and its choice is what UTF-8 falls back to. English gains the UTF-8 lane it lacked; every other
    /// language is unchanged for any byte sequence that is not valid UTF-8.</summary>
    sealed class LanguageProvider : IMutagenEncodingProvider
    {
        readonly LaneSink _sink;
        internal LanguageProvider(LaneSink sink) => _sink = sink;

        public IMutagenEncoding GetEncoding(GameRelease release, Language language)
            => new Utf8First(MutagenEncoding.GetEncoding(release, language), _sink);
    }

    /// <summary>Decode as UTF-8 when the bytes ARE UTF-8, otherwise as the language's fallback — decided by a validity
    /// check, not by catching a decoder exception — and record which lane answered. A string that is pure ASCII says
    /// nothing about the file's encoding, so it records neither.
    ///
    /// <para>The encode direction throws: this is a READ encoding, and what a write embeds is chosen per file by
    /// <see cref="WriteInPlace"/> / <see cref="WriteNew"/>. Encoding through it would put a plain string subrecord out
    /// in a different encoding from the rest of the file, which is the one thing this type exists to prevent — so it
    /// fails by name rather than by writing bytes nobody chose.</para></summary>
    sealed class Utf8First : IMutagenEncoding
    {
        readonly IMutagenEncoding _fallback;
        readonly LaneSink _sink;
        internal Utf8First(IMutagenEncoding fallback, LaneSink sink) { _fallback = fallback; _sink = sink; }

        public string GetString(ReadOnlySpan<byte> bytes)
        {
            if (!HasNonAscii(bytes)) return StrictUtf8Encoding.GetString(bytes);
            if (Utf8.IsValid(bytes)) { _sink.SawUtf8(); return StrictUtf8Encoding.GetString(bytes); }
            _sink.SawLegacy();
            return _fallback.GetString(bytes);
        }

        public int GetByteCount(ReadOnlySpan<char> str) => throw Unsupported();

        public int GetBytes(ReadOnlySpan<char> chars, Span<byte> bytes) => throw Unsupported();

        static NotSupportedException Unsupported() =>
            new("read encoding; a write picks its encoding per file through PluginTextEncoding.WriteInPlace/WriteNew");

        static bool HasNonAscii(ReadOnlySpan<byte> bytes)
        {
            foreach (var b in bytes) if (b >= 0x80) return true;
            return false;
        }
    }
}

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
    /// <summary>Nothing outside ASCII was read from it, so the two encodings agree and either writes it back unchanged.</summary>
    AsciiOnly,

    /// <summary>At least one non-ASCII string in it was valid UTF-8.</summary>
    Utf8,

    /// <summary>Non-ASCII was read from it and none of it was valid UTF-8, so it is in the language's legacy codepage.</summary>
    Legacy,
}

/// <summary>The text encoding every plugin read and write uses, one home so the read and write sides cannot disagree; a file has ONE encoding and the decision is per file, never per string.
/// <para>The one miss, and it is not guarded: reading, a Windows-1252 string whose bytes also happen to be valid UTF-8 is read as UTF-8, because the bytes are genuinely ambiguous and a guard would have to guess.</para></summary>
public static class PluginTextEncoding
{
    /// <summary>UTF-8 that throws on invalid bytes, so it is never the one that silently substitutes.</summary>
    static readonly Encoding StrictUtf8Encoding =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static readonly IMutagenEncoding StrictUtf8 = new MutagenEncodingWrapper(StrictUtf8Encoding);

    /// <summary>The encodings for a file resolved as UTF-8 — it can spell anything, so nothing here can refuse.</summary>
    public static readonly EncodingBundle Utf8Bundle = new(StrictUtf8, StrictUtf8);

    /// <summary>The strict legacy bundle per code page, built once each.</summary>
    static readonly ConcurrentDictionary<int, EncodingBundle> LegacyBundles = new();

    /// <summary>Mutagen's answer for a language, its code-page half and that page's number. Mutagen's non-English
    /// answer tries a lenient UTF-8 first, which never fails, so the code page under it is taken here. Where Mutagen
    /// has no code page (Chinese, Korean, Thai…) the legacy lane is Windows-1252, which decodes every byte, so a
    /// non-UTF-8 file's bytes are written back as read; a new file with text 1252 cannot spell goes out UTF-8.</summary>
    static readonly (IMutagenEncoding Answer, IMutagenEncoding Legacy, int CodePage)[] MutagenPages =
    {
        (MutagenEncoding._1252, MutagenEncoding._1252, 1252),
        (MutagenEncoding._utf8_1252, MutagenEncoding._1252, 1252),
        (MutagenEncoding._utf8_1250, MutagenEncoding._1250, 1250),
        (MutagenEncoding._utf8_1251, MutagenEncoding._1251, 1251),
        (MutagenEncoding._utf8_1253, MutagenEncoding._1253, 1253),
        (MutagenEncoding._utf8_1254, MutagenEncoding._1254, 1254),
        (MutagenEncoding._utf8_1256, MutagenEncoding._1256, 1256),
        (MutagenEncoding._utf8_932, MutagenEncoding._932, 932),
        (MutagenEncoding._utf8, MutagenEncoding._1252, 1252),
    };

    /// <summary>What a non-UTF-8 string decodes as in that language, and the code page a new legacy-lane file is written in: Mutagen's own, for every language.</summary>
    static (IMutagenEncoding Decoder, int CodePage) Legacy(Language language)
    {
        var answer = MutagenEncoding.GetEncoding(GameRelease.SkyrimSE, language);
        foreach (var p in MutagenPages)
            if (ReferenceEquals(p.Answer, answer)) return (p.Legacy, p.CodePage);
        throw new NotSupportedException($"Mutagen answers {language} with an encoding houseCARL does not know; update PluginTextEncoding.MutagenPages.");
    }

    /// <summary>The encodings for a file resolved to the language default, strict so an unspellable value stops the write instead of landing as <c>?</c>; the code page is this call's game language's.</summary>
    public static EncodingBundle LegacyBundle
    {
        get
        {
            var cp = Legacy(GameLanguage.Current.Language).CodePage;
            return LegacyBundles.GetOrAdd(cp, c =>
            {
                // The language default that THROWS on a character it cannot spell instead of substituting '?', so the question is the encoder's own answer at the moment it writes.
                var strict = new SpellCheck(CodePagesEncodingProvider.Instance.GetEncoding(
                    c, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback)!);
                return new EncodingBundle(strict, strict);
            });
        }
    }

    /// <summary>Whether Mutagen gives this call's game language no code page, so its text is UTF-8 and Windows-1252 only
    /// keeps a legacy file's own bytes as read.</summary>
    static bool NoCodePage
        => ReferenceEquals(MutagenEncoding.GetEncoding(GameRelease.SkyrimSE, GameLanguage.Current.Language), MutagenEncoding._utf8);

    /// <summary>The name a refusal gives this call's legacy code page.</summary>
    static string CodePageName()
        => Legacy(GameLanguage.Current.Language).CodePage switch { 932 => "Shift-JIS", var cp => $"Windows-{cp}" };

    /// <summary>What each plugin's own strings decoded as, by game language and filename, against the file version it was resolved from. Written by the read, read by the write.</summary>
    static readonly ConcurrentDictionary<string, (string Stamp, LaneSink Sink)> Lanes =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The read parameters for opening one plugin, in this call's game language with its string tables from
    /// their winning MO2 layer (<see cref="InstanceText.FoldersFor"/>); per plugin, because the lane it resolves to is
    /// recorded against that plugin's name as its strings are decoded. <paramref name="dataDir"/> is the game Data a
    /// caller outside any order build redirects to.</summary>
    public static BinaryReadParameters ReadFor(string pluginPath, string? dataDir = null)
    {
        var text = GameLanguage.Current;   // once per open, so one open never mixes two order builds
        var sink = SinkFor(pluginPath, text.Language);
        var strings = new StringsReadParameters
        {
            // A plugin's lane is its own bytes'; the provider decodes a plain plugin's own text and a localized one's
            // tables, which count toward it only in English, so an English write stays the bytes it always was.
            EncodingProvider = new LanguageProvider(text.Language == Language.English || !IsLocalized(pluginPath, sink) ? sink : new LaneSink()),
            NonTranslatedEncodingOverride = new Utf8First(Legacy(text.Language).Decoder, sink),
        };
        if (text.FoldersFor(pluginPath, dataDir) is { } folders)
            strings = strings with { StringsFolderOverride = folders.Strings, BsaFolderOverride = folders.Archives };
        // English is Mutagen's own default, left unset so an English read is the read it always was.
        if (text.Language != Language.English) strings = strings with { TargetLanguage = text.Language };
        return BinaryReadParameters.Default with { StringsParam = strings };
    }

    /// <summary>The lane record for one open, shared by every open of the SAME file version in the same language and replaced only when the file changes, so a header-only open cannot erase a lane an eager read resolved.</summary>
    static LaneSink SinkFor(string pluginPath, Language language)
    {
        var name = LaneKey(pluginPath, language);
        string? stamp;
        try
        {
            var fi = new FileInfo(pluginPath);
            stamp = fi.LastWriteTimeUtc.Ticks + ":" + fi.Length;
        }
        catch { stamp = null; }

        if (Lanes.TryGetValue(name, out var have) && (stamp is null || have.Stamp == stamp)) return have.Sink;
        if (stamp is null) return new LaneSink();          // nothing recorded and no stamp: this open answers for itself
        var fresh = new LaneSink();
        Lanes[name] = (stamp, fresh);
        return fresh;
    }

    /// <summary>Whether the plugin's header sets the localized flag, peeked from its first 12 bytes once per file version;
    /// an unreadable header counts as localized, so nothing uncertain feeds the lane.</summary>
    static bool IsLocalized(string pluginPath, LaneSink sink)
    {
        if (sink.Localized is { } known) return known;
        bool localized;
        try
        {
            Span<byte> head = stackalloc byte[12];
            using (var fs = new FileStream(pluginPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1))
                localized = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length
                            || (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(head[8..]) & 0x80) != 0;
        }
        catch { localized = true; }
        return (bool)(sink.Localized = localized);
    }

    /// <summary>What the read resolved that plugin's own bytes to be, or <see cref="PluginTextLane.AsciiOnly"/> for a plugin nothing has read a non-ASCII string out of.</summary>
    public static PluginTextLane LaneOf(string pluginNameOrPath)
        => Lanes.TryGetValue(LaneKey(pluginNameOrPath, GameLanguage.Current.Language), out var s) ? s.Sink.Lane : PluginTextLane.AsciiOnly;

    /// <summary>A lane record's key: the language it was read in, since a plugin's lane depends on it, and the filename.</summary>
    static string LaneKey(string pluginNameOrPath, Language language) => $"{language}|{Path.GetFileName(pluginNameOrPath)}";

    /// <summary>The encodings an IN-PLACE write embeds: the lane the read of that same file resolved to, so text the write did not change comes back byte-identical.</summary>
    public static EncodingBundle WriteInPlace(string targetPath)
        => InPlaceIsUtf8(targetPath) ? Utf8Bundle : LegacyBundle;

    /// <summary>Whether an in-place rewrite of that file goes out as UTF-8 — the lane its own read resolved to; an
    /// ASCII-only file takes UTF-8 in a language with no code page.</summary>
    public static bool InPlaceIsUtf8(string targetPath) => LaneOf(targetPath) switch
    {
        PluginTextLane.Utf8 => true,
        PluginTextLane.AsciiOnly => NoCodePage,
        _ => false,
    };


    /// <summary>Whether a new file's contributing plugins put it in the UTF-8 lane before a single string is written — the provenance half of the new-file encoding choice.</summary>
    public static bool NewFileIsUtf8(IModGetter mod)
    {
        if (LaneOf(mod.ModKey.FileName.String) == PluginTextLane.Utf8) return true;
        foreach (var m in mod.MasterReferences)
            if (LaneOf(m.Master.FileName.String) == PluginTextLane.Utf8) return true;
        foreach (var r in mod.EnumerateMajorRecords())
            if (LaneOf(r.FormKey.ModKey.FileName.String) == PluginTextLane.Utf8) return true;
        return false;
    }

    /// <summary>The refusal for an IN-PLACE write whose file is in the language default and whose new value that encoding cannot spell; the staged temp is discarded and nothing is written.</summary>
    public static string UnspellableRefusal(UnspellableTextException ex, string pluginFileName)
    {
        var (its, which) = NoCodePage
            ? ("not UTF-8", "its bytes have")
            : ("in " + CodePageName(), CodePageName() + " has");
        return $"houseCARL did not write '{pluginFileName}' — the file is unchanged and nothing was staged. Its text is "
             + $"{its} (that is what reading it resolved to), and the value \"{ex.Text}\" contains "
             + $"'{ex.Character}' (U+{(int)ex.Character:X4}), which {which} no spelling for — writing it here "
             + "would either store a '?' in place of the character or convert every other name in the file to UTF-8. "
             + "Write this value into a new patch instead (drop in_place=), which is free to be a UTF-8 file.";
    }

    /// <summary>Unwrap a serialize-boundary throw to the unspellable-value failure inside it, or null; re-stamped only when EVERY leaf is this failure.</summary>
    public static UnspellableTextException? RootUnspellable(Exception ex)
    {
        static UnspellableTextException? Root(Exception e)
        {
            while (true)
            {
                if (e is UnspellableTextException u) return u;
                if (e.InnerException is not { } inner) return null;
                e = inner;
            }
        }
        if (ex is AggregateException agg)
        {
            var leaves = agg.Flatten().InnerExceptions;
            if (leaves.Count == 0) return null;
            UnspellableTextException? first = null;
            foreach (var leaf in leaves)
            {
                if (Root(leaf) is not { } u) return null;   // a leaf that is something else → not purely this failure
                first ??= u;
            }
            return first;
        }
        return Root(ex);
    }

    /// <summary>The language default, made to answer rather than substitute: a character it cannot spell throws with the WHOLE value in hand.</summary>
    sealed class SpellCheck : IMutagenEncoding
    {
        readonly Encoding _enc;
        internal SpellCheck(Encoding enc) => _enc = enc;

        public string GetString(ReadOnlySpan<byte> bytes) => _enc.GetString(bytes);

        public int GetByteCount(ReadOnlySpan<char> str)
        {
            try { return _enc.GetByteCount(str); }
            catch (EncoderFallbackException ex) { throw Unspellable(str, ex); }
        }

        public int GetBytes(ReadOnlySpan<char> chars, Span<byte> bytes)
        {
            try { return _enc.GetBytes(chars, bytes); }
            catch (EncoderFallbackException ex) { throw Unspellable(chars, ex); }
        }

        static UnspellableTextException Unspellable(ReadOnlySpan<char> text, EncoderFallbackException ex)
            => new(new string(text), ex.CharUnknown);
    }

    /// <summary>What each plugin's strings decoded as, accumulated across one open; UTF-8 wins over legacy.</summary>
    sealed class LaneSink
    {
        volatile bool _utf8, _legacy;
        internal bool? Localized;   // the file's localized flag, once peeked
        internal void SawUtf8() => _utf8 = true;
        internal void SawLegacy() => _legacy = true;
        internal PluginTextLane Lane =>
            _utf8 ? PluginTextLane.Utf8 : _legacy ? PluginTextLane.Legacy : PluginTextLane.AsciiOnly;
    }

    /// <summary>UTF-8 over whatever Mutagen would have chosen for the language — the encoding a LOCALIZED plugin's <c>.STRINGS</c> tables are read with; language selection is untouched.</summary>
    sealed class LanguageProvider : IMutagenEncodingProvider
    {
        readonly LaneSink _sink;
        internal LanguageProvider(LaneSink sink) => _sink = sink;

        public IMutagenEncoding GetEncoding(GameRelease release, Language language)
            => new Utf8First(Legacy(language).Decoder, _sink);
    }

    /// <summary>Decode as UTF-8 when the bytes ARE UTF-8, otherwise as the language's fallback, recording which lane answered; the encode direction throws, because a write picks its encoding per file.</summary>
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
            new("read encoding; a write picks its encoding per file through PluginTextEncoding.WriteInPlace/LegacyBundle");

        static bool HasNonAscii(ReadOnlySpan<byte> bytes)
        {
            foreach (var b in bytes) if (b >= 0x80) return true;
            return false;
        }
    }
}

/// <summary>A load order whose game language houseCARL cannot read, refused by the order build in one sentence.</summary>
public sealed class GameLanguageRefusedException(string sentence) : InvalidOperationException(sentence);

/// <summary>A value the file's own encoding has no spelling for, raised by the strict encoder AS it writes; it carries the whole value and the offending character.</summary>
public sealed class UnspellableTextException : InvalidOperationException
{
    public UnspellableTextException(string text, char character)
        : base($"\"{text}\" contains '{character}' (U+{(int)character:X4}), which this plugin's encoding cannot spell")
    {
        Text = text;
        Character = character;
    }

    /// <summary>The same failure re-stamped with the lane's own one-sentence refusal, keeping the value and the character.</summary>
    public UnspellableTextException(string sentence, UnspellableTextException source) : base(sentence)
    {
        Text = source.Text;
        Character = source.Character;
    }

    /// <summary>The whole string being written, as the caller would recognise it.</summary>
    public string Text { get; }

    /// <summary>The first character the encoding has no spelling for.</summary>
    public char Character { get; }
}

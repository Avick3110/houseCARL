using System.Text;

namespace HousecarlCore;

// SksePeek — the string half of the SKSE static peek (housecarl_skse findings='inventory' peek=true). It answers "what does this
// unfamiliar DLL's image statically contain": imports ride the manifest read in SksePluginReader because they
// are free there, while the embedded strings live here because scanning a whole image is not free and stays
// opt-in per DLL.
//
// A string in an image is what the image CONTAINS, never what the code DOES, and absence proves nothing — some
// plugins embed no plugin names at all because their form references live in configs or are built at runtime.
// "Nothing embedded" is a fact about the image, never "no dependencies", and every renderer of this data must
// carry that framing. It is also why this file filters instead of dumping.

/// <summary>What one DLL's image statically embeds, filtered to the extraction classes worth reading.
/// <see cref="RunsScanned"/> against the list sizes states the cut rather than implying it: the classes are a filter
/// over the haystack. A raw full-strings dump is deliberately not offered — it is the noise these classes exist to
/// remove, and anyone who needs it has real <c>strings</c> tooling.</summary>
public sealed record SksePeekResult(
    IReadOnlyList<string> ConfigPaths,
    IReadOnlyList<string> PluginRefs,
    int RunsScanned,
    long BytesScanned,
    string? Note)
{
    /// <summary>Why the scan produced nothing — set ONLY on failure (unreadable image / past the size cap), null on
    /// every successful scan. There is deliberately no partial-scan state: an image past <see cref="SksePeek.SizeCap"/>
    /// is refused and NAMED rather than half-read, because a half-read image's "nothing embedded" would be a silent
    /// lie. So <see cref="Note"/> is exactly the failure channel, and <see cref="Failed"/> reads off it.</summary>
    public string? Note { get; init; } = Note;

    /// <summary>True when the scan did not happen — the caller must not render this as a clean "embeds nothing".
    /// <see cref="Note"/> carries the reason.</summary>
    public bool Failed => Note is not null;
}

public static class SksePeek
{
    /// <summary>Per-image byte cap for the string scan. SKSE plugin DLLs are single-digit MB; 64 MB is far above any real
    /// one, so the cap trips only on the pathological case it exists to NAME (a scan cut is reported, never silent).</summary>
    public const long SizeCap = 64L * 1024 * 1024;

    /// <summary>Shortest run counted as a string. 4 is the <c>strings(1)</c> convention: shorter runs are overwhelmingly
    /// code bytes that happen to be printable, and every extraction class here needs more than 4 chars anyway.</summary>
    const int MinRun = 4;

    static readonly string[] PluginExts = [".esp", ".esm", ".esl"];
    static readonly string[] ConfigExts = [".ini", ".toml", ".json", ".yaml", ".yml"];

    /// <summary>Peek one DLL off disk. Never throws — an unreadable image returns a <see cref="SksePeekResult.Failed"/>
    /// result carrying the reason, never an empty-but-clean-looking one. Read-share + closed before return, the
    /// no-handle-at-rest discipline <see cref="SksePluginReader"/> holds.</summary>
    public static SksePeekResult Scan(string filePath)
    {
        try
        {
            var fi = new FileInfo(filePath);
            if (fi.Length > SizeCap)
                return new SksePeekResult([], [], 0, 0,
                    $"image is {fi.Length / (1024 * 1024)} MB — past the {SizeCap / (1024 * 1024)} MB peek cap; NOT scanned");
            return ScanBytes(File.ReadAllBytes(filePath));
        }
        catch (Exception ex)
        {
            return new SksePeekResult([], [], 0, 0, $"could not read the image: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The pure scan over image bytes — pure so a test can pin extraction against planted fixtures with no
    /// real DLL. Walks the bytes for printable runs in BOTH encodings (ASCII and UTF-16LE: modern C++ plugins use wide
    /// strings, so scanning only ASCII would silently halve coverage), then keeps only the runs matching an extraction
    /// class.</summary>
    public static SksePeekResult ScanBytes(ReadOnlySpan<byte> bytes)
    {
        var configs = new List<string>();
        var plugins = new List<string>();
        var seenCfg = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenPlg = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int runs = 0;

        foreach (var run in Runs(bytes))
        {
            runs++;
            // Plugin-ref FIRST: "Data\Skyrim.esm" is both path-shaped and a plugin reference, and the plugin reference is
            // the sharper signal (it cross-checks against the load order), so it wins the classification.
            if (PluginRefIn(run) is { } p) { if (seenPlg.Add(p)) plugins.Add(p); }
            else if (IsConfigPath(run) && seenCfg.Add(run)) configs.Add(run);
        }
        return new SksePeekResult(configs, plugins, runs, bytes.Length, null);
    }

    /// <summary>Every printable run of at least <see cref="MinRun"/> chars, ASCII then UTF-16LE. UTF-16 is scanned at both
    /// byte alignments (an image's wide strings are usually 2-aligned, but nothing guarantees it); the two encodings can't
    /// double-count, because a wide string's interleaved NULs break every ASCII run at length 1.</summary>
    static IEnumerable<string> Runs(ReadOnlySpan<byte> b)
    {
        // A span can't cross an iterator boundary, so collect eagerly — bounded by SizeCap.
        var outp = new List<string>();
        var sb = new StringBuilder(64);

        for (int i = 0; i <= b.Length; i++)                       // <= : the final iteration flushes a run ending AT the buffer end
        {
            if (i < b.Length && IsPrintable(b[i])) { sb.Append((char)b[i]); continue; }
            if (sb.Length >= MinRun) outp.Add(sb.ToString());
            sb.Clear();
        }

        for (int align = 0; align < 2; align++)
        {
            sb.Clear();
            for (int i = align; i <= b.Length; i += 2)
            {
                if (i + 1 < b.Length && IsPrintable(b[i]) && b[i + 1] == 0) { sb.Append((char)b[i]); continue; }
                if (sb.Length >= MinRun) outp.Add(sb.ToString());
                sb.Clear();
            }
        }
        return outp;
    }

    static bool IsPrintable(byte c) => c is >= 0x20 and < 0x7F;

    /// <summary>The plugin FILENAME a run references, or null. Returns the filename alone (not the whole run) because the
    /// load-order cross-check keys on it — "Data\Dawnguard.esm" and "Dawnguard.esm" are the same reference. A run is a
    /// plugin ref only when the name ENDS the run: a .esp mid-string is a format template or a substring, not a name.
    ///
    /// This classifier is held to a STRICTER bar than <see cref="IsConfigPath"/>, and deliberately: a config path is only
    /// ever SHOWN ("suggestive of its config surface"), whereas a plugin name is ADJUDICATED against the load order and
    /// can come back "[!] NOT in your load order". A false positive here is therefore a false ALARM, not just noise — so
    /// anything that isn't shaped like a real filename is dropped rather than guessed at.</summary>
    static string? PluginRefIn(string run)
    {
        if (!PluginExts.Any(e => run.EndsWith(e, StringComparison.OrdinalIgnoreCase))) return null;
        int cut = run.LastIndexOfAny(['\\', '/']);
        string name = cut >= 0 ? run[(cut + 1)..] : run;
        if (name.Length <= 4) return null;                        // ".esp" alone — an extension constant, not a reference
        // A plugin filename is a filename: a run carrying quotes/separators past the last slash is a sentence about a
        // plugin, not the name of one. Bethesda names allow spaces, dashes, apostrophes, parens — so the shape check is
        // this forbidden-char set, and it must carry BOTH format-string dialects:
        //   %  → printf ("%s.esp")
        //   {} → fmt / spdlog / std::format ("{}.esp", "loading {}.esp") — the DOMINANT modern shape, because
        //        CommonLibSSE-NG plugins log through spdlog. Missing these would adjudicate a log template against the
        //        load order and flag it ABSENT on every healthy install.
        return name.Any(ch => ch is '"' or '\'' or '<' or '>' or '|' or '*' or '?' or ':' or '%' or '{' or '}') ? null : name;
    }

    /// <summary>Whether a run is path-shaped enough to be part of the DLL's CONFIG surface — the "which files does this
    /// image reach for" signal, complementing the config audit from the other side (that audits what a config DECLARES;
    /// this shows the folder a DLL embeds). Suggestive, never a claim the DLL reads it — it is a string in an image.</summary>
    static bool IsConfigPath(string run)
    {
        bool hasCfgExt = ConfigExts.Any(e => run.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        bool underData = run.Contains("Data\\", StringComparison.OrdinalIgnoreCase)
                      || run.Contains("Data/", StringComparison.OrdinalIgnoreCase)
                      || run.Contains("SKSE\\Plugins", StringComparison.OrdinalIgnoreCase)
                      || run.Contains("SKSE/Plugins", StringComparison.OrdinalIgnoreCase);
        if (!hasCfgExt && !underData) return false;
        // Drop the compiler's own noise: a bare extension, and the C++ type/format soup that trips the extension test
        // ("%s.json", "basic_string<...>.ini"). A real config path has a separator or is a plain filename.
        if (run.Length <= 5) return false;
        // NOTE the asymmetry with PluginRefIn, which also rejects fmt's {} placeholder: a {}-bearing path is a TEMPLATE
        // the DLL fills in, and it is still real config-surface signal — "Data/SKSE/Plugins/versionlib-{}.bin" tells you
        // this plugin reads Address Library, which is worth showing. A config path is only ever SHOWN, so a template
        // costs nothing; a plugin name is ADJUDICATED, so a template would become a false "NOT in your load order".
        return !run.Contains('%') && !run.Contains('<') && !run.Contains('"');
    }
}

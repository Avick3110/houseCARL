using System.Text;

namespace HousecarlCore;

// ======================================================================
//  SksePeek — the STRING half of the SKSE tier-D static peek
//  (housecarl_skse_inventory peek=true; plan dev/plans/SKSE_TIER_D_STATIC_PEEK_PLAN_2026-07-16.md).
//
//  Tier D answers "what does this unfamiliar DLL's IMAGE statically contain" — its imports (which ride
//  the manifest read in SksePluginReader, because they're free there) and the high-signal strings it
//  embeds, which live here because scanning a whole image is NOT free and so stays opt-in per-DLL.
//
//  The honest frame, and the reason this file filters instead of dumping: a string in an image is what
//  the image CONTAINS, never what the code DOES (tier E is the ceiling), and ABSENCE PROVES NOTHING —
//  RequiemLP.dll and YASTM.dll embed no plugin names at all, because their form references live in
//  configs or are built at runtime. "Nothing embedded" is therefore a fact about the image; it is never
//  "no dependencies". Every renderer of this data must carry that framing.
// ======================================================================

/// <summary>What one DLL's image statically embeds, filtered to the extraction classes worth reading (tier D).
/// <see cref="RunsScanned"/> vs the list sizes is the Q3 accounting: the classes are a FILTER over the haystack, and the
/// cut is stated rather than implied. A raw full-strings dump is deliberately not offered — it is the noisy 95% these
/// classes exist to remove, and anyone who needs it has real <c>strings</c> tooling.</summary>
public sealed record SksePeekResult(
    IReadOnlyList<string> ConfigPaths,
    IReadOnlyList<string> PluginRefs,
    int RunsScanned,
    long BytesScanned,
    string? Note)
{
    /// <summary>True when nothing was scanned at all (an unreadable image) — the caller must not render this as a clean
    /// "embeds nothing" (Q3). <see cref="Note"/> carries the reason.</summary>
    public bool Failed => Note is not null && BytesScanned == 0;
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
    /// result carrying the reason, never an empty-but-clean-looking one (Q3). Read-share + closed before return, the
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

    /// <summary>The pure scan over image bytes — PURE so the skse-peek-guard can pin extraction against planted fixtures
    /// with no real DLL. Walks the bytes for printable runs in BOTH encodings (ASCII and UTF-16LE: modern C++ plugins use
    /// wide strings, so scanning only ASCII would silently halve coverage — the exact class of silent gap Q3 forbids),
    /// then keeps only the runs matching an extraction class.</summary>
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
    /// plugin ref only when the name ENDS the run: a .esp mid-string is a format template or a substring, not a name.</summary>
    static string? PluginRefIn(string run)
    {
        if (!PluginExts.Any(e => run.EndsWith(e, StringComparison.OrdinalIgnoreCase))) return null;
        int cut = run.LastIndexOfAny(['\\', '/']);
        string name = cut >= 0 ? run[(cut + 1)..] : run;
        if (name.Length <= 4) return null;                        // ".esp" alone — an extension constant, not a reference
        // A plugin filename is a filename: a run carrying separators/quotes past the last slash is a sentence about a
        // plugin, not the name of one. Bethesda names allow spaces, dashes, apostrophes, parens.
        return name.Any(ch => ch is '"' or '\'' or '<' or '>' or '|' or '*' or '?' or ':' or '%') ? null : name;
    }

    /// <summary>Whether a run is path-shaped enough to be part of the DLL's CONFIG surface — the "which files does this
    /// image reach for" signal that complements tier B from the other side (tier B audits what a config DECLARES; this
    /// shows the folder a DLL actually embeds). Suggestive, never a claim the DLL reads it — it is a string in an image.</summary>
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
        return !run.Contains('%') && !run.Contains('<') && !run.Contains('"');
    }
}

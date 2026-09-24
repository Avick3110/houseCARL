using System.Text;

namespace HousecarlCore;

// The string half of the SKSE static peek, opt-in per DLL; contract in docs/architecture/skse-layer.md.

/// <summary>What one DLL's image statically embeds, filtered to the extraction classes worth reading.</summary>
public sealed record SksePeekResult(
    IReadOnlyList<string> ConfigPaths,
    IReadOnlyList<string> PluginRefs,
    int RunsScanned,
    long BytesScanned,
    string? Note)
{
    /// <summary>Why the scan produced nothing — the failure channel, set ONLY on failure; there is no partial-scan state.</summary>
    public string? Note { get; init; } = Note;

    public bool Failed => Note is not null;
}

public static class SksePeek
{
    /// <summary>Per-image byte cap for the string scan; an image past it is NAMED, never half-read.</summary>
    public const long SizeCap = 64L * 1024 * 1024;

    /// <summary>Shortest run counted as a string — the <c>strings(1)</c> convention.</summary>
    const int MinRun = 4;

    static readonly string[] PluginExts = [".esp", ".esm", ".esl"];
    static readonly string[] ConfigExts = [".ini", ".toml", ".json", ".yaml", ".yml"];

    /// <summary>Peek one DLL off disk — never throws, and holds no handle at rest.</summary>
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

    /// <summary>The pure scan over image bytes, in ASCII AND UTF-16LE; pinned by SksePeekScanTests.</summary>
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
            // Plugin-ref FIRST: a run can be both path-shaped and a plugin reference, and the plugin reference wins.
            if (PluginRefIn(run) is { } p) { if (seenPlg.Add(p)) plugins.Add(p); }
            else if (IsConfigPath(run) && seenCfg.Add(run)) configs.Add(run);
        }
        return new SksePeekResult(configs, plugins, runs, bytes.Length, null);
    }

    /// <summary>Every printable run of at least <see cref="MinRun"/> chars, ASCII then UTF-16LE at both byte alignments.</summary>
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

    /// <summary>The plugin FILENAME a run references, or null — a stricter bar than <see cref="IsConfigPath"/>; contract in docs/architecture/skse-layer.md.</summary>
    static string? PluginRefIn(string run)
    {
        if (!PluginExts.Any(e => run.EndsWith(e, StringComparison.OrdinalIgnoreCase))) return null;
        int cut = run.LastIndexOfAny(['\\', '/']);
        string name = cut >= 0 ? run[(cut + 1)..] : run;
        if (name.Length <= 4) return null;                        // ".esp" alone — an extension constant, not a reference
        // A forbidden-char set, carrying both format-string dialects (printf %, fmt/spdlog {}), so a log template is not adjudicated.
        return name.Any(ch => ch is '"' or '\'' or '<' or '>' or '|' or '*' or '?' or ':' or '%' or '{' or '}') ? null : name;
    }

    /// <summary>Whether a run is path-shaped enough to be part of the DLL's CONFIG surface — suggestive, never a claim the DLL reads it.</summary>
    static bool IsConfigPath(string run)
    {
        bool hasCfgExt = ConfigExts.Any(e => run.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        bool underData = run.Contains("Data\\", StringComparison.OrdinalIgnoreCase)
                      || run.Contains("Data/", StringComparison.OrdinalIgnoreCase)
                      || run.Contains("SKSE\\Plugins", StringComparison.OrdinalIgnoreCase)
                      || run.Contains("SKSE/Plugins", StringComparison.OrdinalIgnoreCase);
        if (!hasCfgExt && !underData) return false;
        // Drop the compiler's own noise: a bare extension, and the C++ type/format soup that trips the extension test.
        if (run.Length <= 5) return false;
        return !run.Contains('%') && !run.Contains('<') && !run.Contains('"');
    }
}

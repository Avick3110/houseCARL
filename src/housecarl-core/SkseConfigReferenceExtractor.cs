using System.Globalization;
using System.Text.RegularExpressions;

namespace HousecarlCore;

/// <summary>The catalog-FREE extractor for the SKSE config audit, pure and line-local; contract in docs/architecture/skse-layer.md.</summary>
public static class SkseConfigReferenceExtractor
{
    // A hex FormID and a plugin filename joined by '|' or '~' in either order; every shape is pinned by SkseConfigAuditProbe.
    const string PluginRun = @"[^|~""=,:{}()\[\]/\\\r\n]*?\.es[lmp]";
    const string HexRun = @"(?:0x)?[0-9A-Fa-f]{1,16}";
    static readonly Regex FormToken = new(
        @"(?<![0-9A-Za-z])(?:" +
            $@"(?<hexA>{HexRun})\s*(?<delimA>[|~])\s*(?<pluginA>{PluginRun})" + "|" +
            $@"(?<pluginB>{PluginRun})\s*(?<delimB>[|~])\s*(?<hexB>{HexRun})" +
        @")(?![0-9A-Za-z])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Every form-shaped reference and path-segment gate a config declares — pure, and faithful: duplicates included.</summary>
    public static IReadOnlyList<SkseConfigRef> Extract(string relPath, string text)
    {
        var refs = new List<SkseConfigRef>();

        // 1) Path-segment plugin gates: a DIRECTORY component that is a plugin filename, deduped per file.
        var segs = (relPath ?? "").Split('\\', '/');
        var seenGate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < segs.Length - 1; i++)   // exclude the last segment (the file itself)
        {
            var seg = segs[i];
            if (EndsInPlugin(seg) && seenGate.Add(seg))
                refs.Add(new SkseConfigRef(seg, SkseRefShape.PathSegmentGate, seg, null, null, 0, null));
        }

        // 2) Form-shaped tokens, one regex pass per physical line (references are line-local).
        if (!string.IsNullOrEmpty(text))
        {
            int line = 0;
            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                line++;
                if (raw.IndexOf('|') < 0 && raw.IndexOf('~') < 0) continue;   // no possible delimiter → skip the regex
                foreach (Match m in FormToken.Matches(raw))
                {
                    bool altA = m.Groups["hexA"].Success;
                    string rawHex = (altA ? m.Groups["hexA"] : m.Groups["hexB"]).Value;
                    // Strip a leading TOML single-quote / whitespace that rode in with the plugin name.
                    string plugin = (altA ? m.Groups["pluginA"] : m.Groups["pluginB"]).Value.Trim().Trim('\'');
                    refs.Add(BuildTokenRef(m.Value, plugin, rawHex, line));
                }
            }
        }
        return refs;
    }

    /// <summary>Normalize one matched token — an over-wide or unparseable hex is named LOUDLY, never guessed.</summary>
    static SkseConfigRef BuildTokenRef(string rawMatch, string plugin, string rawHex, int line)
    {
        string digits = rawHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? rawHex[2..] : rawHex;
        if (digits.Length == 0 || digits.Length > 8 || !uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var runtimeId))
            return new SkseConfigRef(rawMatch, SkseRefShape.FormToken, plugin, null, rawHex, line,
                $"'{rawHex}' is not a 32-bit FormID (needs 1–8 hex digits) — cannot normalize");
        return new SkseConfigRef(rawMatch, SkseRefShape.FormToken, plugin, FormIdRange.LocalObjectId(runtimeId), rawHex, line, null);
    }

    static bool EndsInPlugin(string s) =>
        s.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) ||
        s.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) ||
        s.EndsWith(".esl", StringComparison.OrdinalIgnoreCase);
}

public enum SkseRefShape
{
    /// <summary>A hex FormID paired with a plugin filename (<c>0xHEX|Plugin.esp</c> / <c>Plugin.esp|0xHEX</c> / tilde form).</summary>
    FormToken,
    /// <summary>A directory component that is a plugin filename — gates the whole file on that plugin's presence.</summary>
    PathSegmentGate,
}

/// <summary>One reference a config file declares, pre-verdict; <see cref="Unparseable"/> carries the reason a matched token could not be normalized.</summary>
public sealed record SkseConfigRef(
    string Raw,
    SkseRefShape Shape,
    string Plugin,
    uint? LocalId,
    string? RawHex,
    int Line,
    string? Unparseable);

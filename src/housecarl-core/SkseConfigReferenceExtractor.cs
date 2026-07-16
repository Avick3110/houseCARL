using System.Globalization;
using System.Text.RegularExpressions;

namespace HousecarlCore;

/// <summary>
/// The catalog-FREE, framework-AGNOSTIC extractor for the SKSE config audit (tier B — plan
/// dev/plans/SKSE_TIER_B_CONFIG_AUDIT_PLAN_2026-07-16.md). It scans one config file's TEXT for the two
/// things that can be validated against the load order without knowing what any framework MEANS:
/// <list type="number">
///   <item><b>Form-shaped tokens</b> — a hex FormID paired with a plugin filename by <c>|</c> or <c>~</c>,
///     in EITHER order (<c>0xHEX|Plugin.esp</c> as DSD/CDF/po3-lineage write it, <c>Plugin.esp|0xHEX</c> as
///     SkyPatcher writes it, and the <c>~</c> tilde form). The 8-hex light-runtime FormID (<c>FExxxYYY</c>) is
///     normalized to its local object id through the ONE shared home, <see cref="FormIdRange.LocalObjectId"/>.</item>
///   <item><b>Path-segment plugin gates</b> — a directory component that is itself a plugin filename
///     (<c>DynamicStringDistributor\Plugin.esp\file.json</c>): the folder gates the whole file on that plugin's
///     presence.</item>
/// </list>
///
/// <para><b>Pure + line-local (Q3, §4d/§4e).</b> Extraction is a HEURISTIC over token SHAPES, not a per-framework
/// parse: a hex+plugin token in a comment or a disabled block still surfaces (we don't model enabled/disabled
/// semantics — the framing is "references this file declares", never "references the DLL will use"). No JSON/TOML
/// object model is built; references are line-local, so the scan is one regex pass per line. What a reference is
/// FOR — a filter, a swap target, an AV alias — is framework semantics and stays at the skill layer (§4a).</para>
///
/// <para><b>Scope boundary.</b> This finds explicit form-SHAPED references only. Bare EditorID / name strings
/// (<c>"LocSetNordicRuin"</c>) and name-grammars (AVG alias names) are NOT validated here — a JSON string is not
/// unambiguously an EditorID, and validating every string would drown the signal (Wave 2, §4a/§7). The verdict
/// (OK / PLUGIN MISSING / DANGLING / UNPARSEABLE) is the SERVICE layer's job: this stage produces the references;
/// resolving them against the active order is <c>LoadOrderService</c>'s.</para>
/// </summary>
public static class SkseConfigReferenceExtractor
{
    // A form-shaped reference: a hex FormID and a plugin filename joined by '|' or '~', in EITHER order. The plugin
    // side is any run (spaces/dashes/APOSTROPHES/'&' allowed — "kryptopyr's ... .esp", "Dynamic Activation Key -
    // Addons Collection.esp") that ends in .esl/.esm/.esp and contains none of the delimiters / double-quote / path
    // separators / PARENTHESES / JSON-INI structure that would mark a token boundary. Two deliberate charset choices, both
    // forced by the live gate: (1) apostrophe is ALLOWED — excluding it truncated real names mid-word at the ' (the
    // false-MISSING on kryptopyr's / Sanguine's mods); a leading TOML single-quote that rides in is stripped in
    // BuildTokenRef. (2) parentheses are EXCLUDED — a config COMMENT/description embedding a token in prose ("will cast
    // fireball (Skyrim.esm|0x5)") otherwise grabbed the whole prose prefix as the plugin name; the '(' now bounds it to the
    // real name. Cost: a plugin file literally named "Mod (v2).esp" is missed — rare, and the honest tradeoff for killing
    // prose false-positives. The hex side is up to 16 digits so an OVERFLOW token is CAPTURED (as UNPARSEABLE) not silently
    // missed. The alnum look-around stops a token gluing onto an adjacent identifier.
    const string PluginRun = @"[^|~""=,:{}()\[\]/\\\r\n]*?\.es[lmp]";
    const string HexRun = @"(?:0x)?[0-9A-Fa-f]{1,16}";
    static readonly Regex FormToken = new(
        @"(?<![0-9A-Za-z])(?:" +
            $@"(?<hexA>{HexRun})\s*(?<delimA>[|~])\s*(?<pluginA>{PluginRun})" + "|" +
            $@"(?<pluginB>{PluginRun})\s*(?<delimB>[|~])\s*(?<hexB>{HexRun})" +
        @")(?![0-9A-Za-z])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Extract every form-shaped reference and path-segment plugin gate a config declares. <paramref name="relPath"/>
    /// is the file's path under Data (e.g. <c>SKSE\Plugins\DynamicStringDistributor\Plugin.esp\file.json</c>) — its directory
    /// components are checked for plugin-named gates; <paramref name="text"/> is the file's decoded content. Pure: no I/O, no
    /// load-order knowledge. Faithful: every occurrence is returned (the service dedupes for rendering), duplicates included.</summary>
    public static IReadOnlyList<SkseConfigRef> Extract(string relPath, string text)
    {
        var refs = new List<SkseConfigRef>();

        // 1) Path-segment plugin gates: any DIRECTORY component (every segment but the final filename) that is a plugin
        //    filename gates the whole file on that plugin. Deduped — one folder named Plugin.esp is one gate for the file.
        var segs = (relPath ?? "").Split('\\', '/');
        var seenGate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < segs.Length - 1; i++)   // exclude the last segment (the file itself)
        {
            var seg = segs[i];
            if (EndsInPlugin(seg) && seenGate.Add(seg))
                refs.Add(new SkseConfigRef(seg, SkseRefShape.PathSegmentGate, seg, null, null, 0, null));
        }

        // 2) Form-shaped tokens, one regex pass per physical line (references are line-local — §4e).
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
                    // Strip a leading TOML single-quote / whitespace that rode in with the plugin name (the match ends at
                    // .esX, so only a LEADING delimiter is possible — a real filename never starts with a quote/space).
                    string plugin = (altA ? m.Groups["pluginA"] : m.Groups["pluginB"]).Value.Trim().Trim('\'');
                    refs.Add(BuildTokenRef(m.Value, plugin, rawHex, line));
                }
            }
        }
        return refs;
    }

    /// <summary>Normalize one matched token into a reference — strip <c>0x</c>, reject an over-wide / unparseable hex LOUDLY
    /// (never guessed), else mask to the local object id via the shared <see cref="FormIdRange.LocalObjectId"/> home.</summary>
    static SkseConfigRef BuildTokenRef(string rawMatch, string plugin, string rawHex, int line)
    {
        string digits = rawHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? rawHex[2..] : rawHex;
        if (digits.Length == 0 || digits.Length > 8 || !uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var runtimeId))
            return new SkseConfigRef(rawMatch, SkseRefShape.FormToken, plugin, null, rawHex, line,
                $"'{rawHex}' is not a 32-bit FormID (needs 1–8 hex digits) — cannot normalize");
        return new SkseConfigRef(rawMatch, SkseRefShape.FormToken, plugin, FormIdRange.LocalObjectId(runtimeId), rawHex, line, null);
    }

    /// <summary>True when a path/name segment ends in a plugin extension (.esl/.esm/.esp), case-insensitively.</summary>
    static bool EndsInPlugin(string s) =>
        s.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) ||
        s.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) ||
        s.EndsWith(".esl", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Whether a <see cref="SkseConfigRef"/> is a hex FormID token or a plugin-named directory gate.</summary>
public enum SkseRefShape
{
    /// <summary>A hex FormID paired with a plugin filename (<c>0xHEX|Plugin.esp</c> / <c>Plugin.esp|0xHEX</c> / tilde form).</summary>
    FormToken,
    /// <summary>A directory component that is a plugin filename — gates the whole file on that plugin's presence.</summary>
    PathSegmentGate,
}

/// <summary>
/// One reference a config file declares, as extracted (pre-verdict). <see cref="Plugin"/> is the named plugin filename;
/// <see cref="LocalId"/> is the normalized local object id (null for a <see cref="SkseRefShape.PathSegmentGate"/>, which
/// is plugin-presence only, and null when <see cref="Unparseable"/> is set); <see cref="RawHex"/> is the hex as written;
/// <see cref="Line"/> is the 1-based source line (0 for a path gate); <see cref="Unparseable"/> carries the loud reason a
/// shape-matched token could not be normalized (Q3 — never a silent guess). The OK / PLUGIN MISSING / DANGLING verdict is
/// assigned by the service against the load order.
/// </summary>
public sealed record SkseConfigRef(
    string Raw,
    SkseRefShape Shape,
    string Plugin,
    uint? LocalId,
    string? RawHex,
    int Line,
    string? Unparseable);

using System.Text;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>The heading and cut notices all three housecarl_skse renders use; the version text inventory and pairing use.</summary>
static class SkseRenderParts
{
    /// <summary>A section heading, laid whole; false means the budget had no room to start the section at all.</summary>
    internal static bool Head(StringBuilder sb, int cap, string head)
    {
        if (sb.Length + head.Length > cap) return false;
        sb.Append(head);
        return true;
    }

    /// <summary>The line that says how many sections the budget could not start, naming the max_chars the CALLER passed.</summary>
    internal static string SectionsMissed(int missed, int cap) =>
        "  ... [" + missed + " section(s) omitted at max_chars=" + cap + "; raise max_chars to see them]\n";

    /// <summary>The advice a cut row list carries where narrowing the answer is the other way out.</summary>
    internal const string FilterHint = " or use filter= to see all";

    /// <summary>The shorter spelling of that advice, which names filter= without promising it lists everything.</summary>
    internal const string NarrowHint = " or use filter=";

    /// <summary>The one cut notice a capped row list ends on, spelled once so its widest form can be charged up front.</summary>
    internal static string Showing(int shown, int total, string noun = "", string hint = "") =>
        "  ... [showing " + shown + " of " + total + (noun.Length > 0 ? " " + noun : "") + "; raise max_chars" + hint + "]\n";

    /// <summary>The chars a capped row list must hold back for that notice.</summary>
    internal static int CutRoom(int total, string noun = "", string hint = "") => Showing(total, total, noun, hint).Length;

    /// <summary>A plugin's version with the SOURCE it was read from, plus every other version in sight that disagrees;
    /// everything after the leading number is parenthesised, so a row joining its fields with " — " keeps one separator.
    /// The three version sources are in docs/architecture/skse-layer.md; pinned by SkseVersionSourceTests.</summary>
    internal static string VersionText(SksePluginReader.SksePluginInfo? p, string? modVersion)
    {
        string declared = p?.Version?.PluginVersion ?? "";
        string file = p?.FileVersion ?? "";
        string mod = modVersion ?? "";
        // No manifest: whatever version WAS read is the answer, labelled for what it is.
        if (declared.Length == 0)
        {
            if (file.Length == 0) return mod.Length == 0 ? "" : $"{mod} (mod meta.ini)";
            return Differs(file, mod) ? $"{file} (DLL file version; meta.ini {mod})" : $"{file} (DLL file version)";
        }
        var others = new List<string>();
        if (Differs(declared, file)) others.Add($"DLL file version {file}");
        // meta.ini is held back only when the file version already carries it.
        if (Differs(declared, mod) && (file.Length == 0 || Differs(file, mod))) others.Add($"meta.ini {mod}");
        return others.Count == 0
            ? $"{declared} (SKSE manifest)"
            : $"{declared} (SKSE manifest; {string.Join(", ", others)})";
    }

    /// <summary>Two version strings both present and NOT the same version, compared on their numeric prefix: a modder's
    /// tag is UNKNOWN, not different, so it is not reported as a disagreement.</summary>
    static bool Differs(string? a, string? b)
    {
        if (a is not { Length: > 0 } || b is not { Length: > 0 }) return false;   // an unread version says nothing about the one that was read
        string na = NumericPrefix(a), nb = NumericPrefix(b);
        if (na.Length == 0 || nb.Length == 0) return false;
        return !SksePluginReader.VersionsEqual(na, nb);
    }

    /// <summary>The dotted numeric head of a version string, a leading "v" dropped and any trailing tag cut.</summary>
    static string NumericPrefix(string s)
    {
        var t = s.Trim();
        if (t.Length > 0 && (t[0] == 'v' || t[0] == 'V')) t = t[1..];
        int end = 0;
        while (end < t.Length && (char.IsAsciiDigit(t[end]) || t[end] == '.')) end++;
        return t[..end].Trim('.');
    }
}

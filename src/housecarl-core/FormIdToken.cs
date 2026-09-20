using Mutagen.Bethesda.Plugins;

namespace HousecarlCore;

/// <summary>The ONE place a <c>XXXXXX:Plugin.esp</c> FormID token is spelled, so a token always carries the plugin's on-disk filename whatever the FormKey's own ModKey spells (#664).</summary>
public static class FormIdToken
{
    // Replaced whole on publish, never mutated in place, so a reader sees one order's table or the other's.
    static volatile Dictionary<string, string> Canon = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Take these plugin filenames as canonical, REPLACING whatever order published before.</summary>
    public static void Publish(IReadOnlyList<string> pluginFileNames)
    {
        var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in pluginFileNames)
            if (!string.IsNullOrEmpty(n)) next[n] = n;
        Canon = next;
    }

    /// <summary>The canonical spelling of one plugin filename, or the name unchanged when the order now loaded does not carry it.</summary>
    public static string Plugin(string fileName)
        => Canon.TryGetValue(fileName, out var c) ? c : fileName;

    /// <summary>This FormKey as a token — byte-for-byte <see cref="FormKey.ToString"/> except for the canonically respelled plugin half, so it still parses back through <see cref="FormKey.Factory"/>.</summary>
    public static string Of(FormKey fk)
    {
        var s = fk.ToString();
        int i = s.LastIndexOf(':');
        if (i < 0) return s;
        var name = s[(i + 1)..];
        var canon = Plugin(name);
        return string.Equals(canon, name, StringComparison.Ordinal) ? s : string.Concat(s.AsSpan(0, i + 1), canon);
    }
}

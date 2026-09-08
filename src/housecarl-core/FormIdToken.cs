using System.Collections.Concurrent;
using Mutagen.Bethesda.Plugins;

namespace HousecarlCore;

/// <summary>
/// The ONE place a <c>XXXXXX:Plugin.esp</c> FormID token is spelled. Every print of a FormKey goes through
/// <see cref="Of(FormKey)"/>, so a token carries the plugin's filename as it exists ON DISK — one canonical
/// spelling — no matter where the FormKey came from.
///
/// <para>Why it is needed: a FormKey's ModKey is only as canonical as its source. A record's OWN key is spelled
/// from the file Mutagen opened, but a LINK's key is spelled from the master list inside the plugin holding the
/// link, which spells its masters however that plugin's author typed them, and a key parsed from a caller's token
/// is spelled however the caller typed it. ModKey compares case-insensitively, so every lookup still lands — but
/// the two spellings PRINT differently, and two reads of the same record then disagree in text, which is a silent
/// wrong answer to anything that joins them (#664).</para>
///
/// <para>The canonical spelling is the load order's own: <see cref="LoadOrderResolver"/> publishes its plugin
/// filenames (each <c>Path.GetFileName</c> of a real path) here as it builds. A name no order has published — an
/// off-order plugin read straight from disk, a token naming a plugin that is not installed — keeps the spelling it
/// arrived with, which for an off-order read is already the on-disk one. The table is process-wide because the
/// print sites are static and deep (a reflected link value in <see cref="ReadEngine"/> has no load order to ask);
/// one process serves one load order, and entries are additive and keyed case-insensitively, so a later build
/// only ever corrects a spelling.</para>
/// </summary>
public static class FormIdToken
{
    static readonly ConcurrentDictionary<string, string> Canon = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Take these plugin filenames as canonical — called by <see cref="LoadOrderResolver.Build"/> with the
    /// names it derived from the ordered paths.</summary>
    public static void Publish(IReadOnlyList<string> pluginFileNames)
    {
        foreach (var n in pluginFileNames)
            if (!string.IsNullOrEmpty(n)) Canon[n] = n;
    }

    /// <summary>The canonical spelling of one plugin filename, or the name unchanged when no load order has
    /// published it.</summary>
    public static string Plugin(string fileName)
        => Canon.TryGetValue(fileName, out var c) ? c : fileName;

    /// <summary>This FormKey as a token. Byte-for-byte <see cref="FormKey.ToString"/> except for the plugin half,
    /// which is respelled canonically — so the local-ID formatting and the null form can never drift from
    /// Mutagen's, and the token still parses back through <see cref="FormKey.Factory"/>.</summary>
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

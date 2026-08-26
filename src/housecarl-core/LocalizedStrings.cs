using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Strings;

namespace HousecarlCore;

/// <summary>Which strings shape a plugin is in — the one classifier behind every localized-write decision: the in-place
/// write's allow/refuse gate, the compact lane's localized-P′ gate, and the load-order frequency sweep.</summary>
public enum LocalizedShape
{
    /// <summary>Not flagged localized: its text is inside the plugin and none of this applies.</summary>
    NotLocalized,

    /// <summary>A loose <c>Strings\</c> beside the plugin, every language present carrying all three table kinds, and
    /// no competing set in game-Data. The ONE shape the write may commit tables for.</summary>
    LooseComplete,

    /// <summary>A loose set beside the plugin in which some language is missing a table kind. Re-serializing
    /// MATERIALISES the missing files holding empty values, so a player on that language loses the fallback they had.</summary>
    LoosePartial,

    /// <summary>A loose set beside the plugin AND a set for the same plugin in game-Data. Committing tables beside the
    /// plugin leaves the game-Data set stale, and — the reason this cannot merely be tolerated — the game-Data set
    /// still resolves during the commit's own window, so the blank window the write relies on would not be blank.</summary>
    LooseWithGameDataDuplicate,

    /// <summary>The plugin's strings are embedded in a <c>.bsa</c> beside it, which a plugin write cannot rewrite.</summary>
    BsaEmbedded,

    /// <summary>No strings beside the plugin; they resolve from game-Data (the "Cleaned Base Game Masters" /
    /// translation-mod pattern). A write that put tables beside the plugin would SHADOW that set rather than replace
    /// it — the values would be faithful, but the game-Data set would silently go stale.</summary>
    GameDataOnly,

    /// <summary>Flagged localized, and houseCARL can find no strings source for it — the residual case (#371).
    ///
    /// <para>This says what houseCARL could FIND, not what exists: MO2's VFS merges mod folders at runtime, so a
    /// plugin in one mod folder can resolve its strings from a <c>.bsa</c> in ANOTHER, which no path houseCARL walks
    /// can see. That is not hypothetical — on the measured order all 31 plugins in this shape are Creation Club
    /// content whose plugin sits in a "Cleaned Masters" folder while its archive sits in the content's own folder.
    /// Anything user-facing must therefore say houseCARL cannot find the strings, never that the plugin has none.</para></summary>
    Nowhere,
}

/// <param name="Shape">The classification.</param>
/// <param name="Languages">Languages found beside the plugin (empty unless a loose set is there).</param>
/// <param name="IncompleteLanguages">Those of <paramref name="Languages"/> missing at least one table kind, with the
/// kinds they are missing — what <see cref="LocalizedShape.LoosePartial"/>'s refusal names.</param>
/// <param name="GameDataLanguages">Languages found for this plugin in game-Data.</param>
/// <param name="BsaPath">The archive that embeds (or could not be read to rule out) this plugin's strings.</param>
/// <param name="BsaInGameData">That archive was found in the game's Data folder rather than beside the plugin. The
/// refusal has to say which, because "the archive beside it" is a checkable claim and it is false for the whole
/// game-Data class — the vanilla masters and every plugin whose tables live in a game archive.</param>
/// <param name="BsaUnreadable">The archive named by <paramref name="BsaPath"/> could not be parsed, so whether it
/// embeds this plugin's strings is unknown. Classified as embedded — a shape we cannot see into is refused, never
/// assumed harmless (Q3).</param>
/// <param name="GameDataUnknown">No game-Data folder was supplied, so whether a competing set lives there could not be
/// checked. This is NOT the same as having checked and found none: it makes
/// <see cref="LocalizedShape.LooseComplete"/> unsafe to act on, because the duplicate it cannot rule out is precisely
/// what would keep the commit's blank window from being blank.</param>
public sealed record LocalizedAssessment(
    LocalizedShape Shape,
    IReadOnlyList<string> Languages,
    IReadOnlyDictionary<string, IReadOnlyList<string>> IncompleteLanguages,
    IReadOnlyList<string> GameDataLanguages,
    string? BsaPath,
    bool BsaUnreadable,
    bool GameDataUnknown,
    bool BsaInGameData = false)
{
    /// <summary>May a COMPACTION keep its output localized off this source? A complete loose set beside the plugin,
    /// with a game-Data duplicate positively ruled OUT rather than merely unexamined — because a duplicate makes it
    /// ambiguous which set describes the plugin, and P′ would carry whichever one this read happened to resolve.
    ///
    /// <para>This is the compact lane's gate (Q2-A) and nothing else. It is NOT a licence to rewrite a localized
    /// plugin in place: that is refused for every shape, this one included — see
    /// <see cref="LocalizedTableCommit"/> for what was cut and why.</para></summary>
    public bool CanKeepLocalized => Shape is LocalizedShape.LooseComplete && !GameDataUnknown;
}

/// <summary>
/// Classifies where a localized plugin's <c>.STRINGS</c> / <c>.DLSTRINGS</c> / <c>.ILSTRINGS</c> actually live, so a
/// write can decide whether it may commit its own emitted tables beside the plugin or must refuse.
///
/// <para>The shapes and their rulings were measured, not assumed (localized-write-probe, 2026-08-26): committing the
/// emitted tables with the plugin round-trips every value faithfully across all three table kinds; Mutagen loads and
/// re-emits EVERY language present beside the plugin, so a complete loose set of any size round-trips; and a language
/// present only partially has its missing kinds materialised holding empty values. Detection was priced in the same
/// run — the language enumeration at ~0.1 ms, and asking a real 101 MB archive whether it embeds strings keyed to a
/// given plugin at under 1 ms.</para>
/// </summary>
public static class LocalizedStrings
{
    /// <summary>The three table kinds a localized plugin's text is split across. A language is COMPLETE only with all
    /// three: the serialize emits all three per covered language, so a language missing one gets that file invented.</summary>
    static readonly string[] Kinds = { "STRINGS", "DLSTRINGS", "ILSTRINGS" };

    /// <summary>Classify the plugin at <paramref name="pluginPath"/>. <paramref name="dataDir"/> is the resolver's real
    /// game-Data folder (<c>IndexView.DataDir</c>); null means the order has no Skyrim.esm to derive one from, in which
    /// case no game-Data set can be seen and the game-Data shapes are unreachable.</summary>
    public static LocalizedAssessment Assess(string pluginPath, string? dataDir)
    {
        if (!WriteEngine.PluginIsLocalized(pluginPath)) return Plain(LocalizedShape.NotLocalized);

        var folder = Path.GetDirectoryName(pluginPath);
        if (folder is null) return Plain(LocalizedShape.Nowhere);
        var stem = Path.GetFileNameWithoutExtension(pluginPath);

        var own = LanguagesIn(Path.Combine(folder, "Strings"), stem);
        var gameData = dataDir is null
            ? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            : LanguagesIn(Path.Combine(dataDir, "Strings"), stem);

        // An archive the write cannot rewrite decides the shape regardless of what else is on disk — a loose set beside
        // a BSA that also embeds this plugin's strings is an ambiguous source, and which one Mutagen prefers is not
        // something this classifier should be guessing at when the answer is "refuse either way".
        var (bsaPath, bsaUnreadable) = BsaEmbedding(folder, stem);
        if (bsaPath is not null)
            return new LocalizedAssessment(LocalizedShape.BsaEmbedded, Names(own), Incomplete(own), Names(gameData),
                                           bsaPath, bsaUnreadable, dataDir is null);

        if (own.Count > 0)
        {
            var incomplete = Incomplete(own);
            if (incomplete.Count > 0)
                return new LocalizedAssessment(LocalizedShape.LoosePartial, Names(own), incomplete, Names(gameData), null, false, dataDir is null);
            if (gameData.Count > 0)
                return new LocalizedAssessment(LocalizedShape.LooseWithGameDataDuplicate, Names(own), incomplete, Names(gameData), null, false, false);
            return new LocalizedAssessment(LocalizedShape.LooseComplete, Names(own), incomplete, Names(gameData), null, false, dataDir is null);
        }

        if (gameData.Count > 0)
            return new LocalizedAssessment(LocalizedShape.GameDataOnly, Array.Empty<string>(), NoneIncomplete, Names(gameData), null, false, false);

        // Nothing loose anywhere and no archive beside the plugin — but the vanilla masters' strings live in the
        // game-Data ARCHIVES (Skyrim - Interface.bsa carries the base and DLC tables both), so a classifier that
        // stopped here would call every DLC master "no strings anywhere". That is a silently wrong classification
        // rather than a conservative one, and on a real order it was 35 plugins including all four DLC masters.
        // Searched LAST and only when nothing nearer was found, so an ordinary plugin never pays for it.
        if (dataDir is not null)
        {
            var (gdBsa, gdUnreadable) = BsaEmbedding(dataDir, stem);
            if (gdBsa is not null)
                return new LocalizedAssessment(LocalizedShape.BsaEmbedded, Array.Empty<string>(), NoneIncomplete,
                                               Array.Empty<string>(), gdBsa, gdUnreadable, false, BsaInGameData: true);
        }

        return Plain(LocalizedShape.Nowhere, dataDir is null);
    }

    /// <summary>Should an in-place write of <paramref name="pluginPath"/> be refused, and if so with what sentence?
    /// Null means the lane may proceed. THE one home for that decision: the write's own choke point calls it, and so
    /// does every service pre-flight that has to predict the same answer before spending a consent or reporting a dry
    /// run. Two spellings of this would drift, and a dry run whose answer differs from the real call is the defect the
    /// pre-flights exist to prevent.</summary>
    /// <param name="laneClause">The calling lane's own remedy clause, appended to the shape's explanation. The shape
    /// says WHERE this plugin's text lives; the lane says what to do instead. Only the lane knows whether it has a
    /// new-plugin equivalent to offer, which is why the remedy is the caller's to supply and not this file's.</param>
    public static string? RefusalFor(string pluginPath, string pluginFileName, string? dataDir, string? laneClause = null)
    {
        var a = Assess(pluginPath, dataDir);
        if (a.Shape == LocalizedShape.NotLocalized) return null;
        return LocalizedTargetUnsupportedException.Shaped(pluginFileName, a, laneClause);
    }

    /// <summary>The same decision as <see cref="RefusalFor"/>, rendered WITHOUT the "houseCARL did not write X" head —
    /// for a lane reporting on a plugin the caller did not ask about.</summary>
    public static string? RefusalReasonFor(string pluginPath, string pluginFileName, string? dataDir)
    {
        var a = Assess(pluginPath, dataDir);
        if (a.Shape == LocalizedShape.NotLocalized) return null;
        return LocalizedTargetUnsupportedException.ShapeBody(a);
    }

    /// <summary>The loose table files this plugin's own folder carries, in the order a commit should write them —
    /// the set <see cref="LocalizedShape.LooseComplete"/>'s write backs up, deletes, and replaces.</summary>
    public static IReadOnlyList<string> OwnTableFiles(string pluginPath)
    {
        var folder = Path.GetDirectoryName(pluginPath);
        if (folder is null) return Array.Empty<string>();
        return TableFilesIn(Path.Combine(folder, "Strings"), Path.GetFileNameWithoutExtension(pluginPath));
    }

    /// <summary>The table files in <paramref name="stringsDir"/> that belong to the plugin named
    /// <paramref name="stem"/> — never a neighbour's. Public because the commit path needs the same stem filter this
    /// classifier uses: a <c>Strings\</c> folder is shared by every plugin beside it, and matching on anything looser
    /// makes one plugin's write act on another plugin's files.</summary>
    public static IReadOnlyList<string> TableFilesIn(string stringsDir, string stem)
    {
        if (!Directory.Exists(stringsDir)) return Array.Empty<string>();
        try
        {
            return Directory.EnumerateFiles(stringsDir)
                            .Where(p => Parse(Path.GetFileName(p), stem) is not null)
                            .ToList();
        }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    /// <summary>language → the table kinds present for it, for one strings folder and one plugin stem.</summary>
    static Dictionary<string, List<string>> LanguagesIn(string stringsDir, string stem)
    {
        var found = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in TableFilesIn(stringsDir, stem))
        {
            if (Parse(Path.GetFileName(p), stem) is not { } hit) continue;
            if (!found.TryGetValue(hit.Language, out var kinds)) found[hit.Language] = kinds = new List<string>();
            if (!kinds.Contains(hit.Kind, StringComparer.OrdinalIgnoreCase)) kinds.Add(hit.Kind);
        }
        return found;
    }

    /// <summary>Every language Mutagen models, as the token it writes into a table's file name. The set of languages
    /// this classifier understands IS Mutagen's set, by construction — and it is load-bearing rather than cosmetic:
    /// two plugins in one mod folder share a <c>Strings\</c> folder, so for a plugin named <c>ksws07_quest</c> a bare
    /// prefix match also swallows <c>ksws07_quest_shrubs_English.STRINGS</c>, which belongs to
    /// <c>ksws07_quest_shrubs.esp</c>. A write acting on that assessment would back up and delete the OTHER plugin's
    /// tables. Measured on a real load order, where exactly that pair sits in one folder.</summary>
    static readonly HashSet<string> LanguageNames =
        new(Enum.GetNames(typeof(Language)), StringComparer.OrdinalIgnoreCase);

    /// <summary>Split "<c>MyMod_English.DLSTRINGS</c>" into its language and kind, for the plugin named
    /// <paramref name="stem"/>. Null when the file belongs to a different plugin, names no language Mutagen models, or
    /// is not a table at all — the check that keeps one plugin's assessment from reading another's files out of a
    /// shared <c>Strings\</c> folder.</summary>
    static (string Language, string Kind)? Parse(string fileName, string stem)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.');
        if (!Kinds.Contains(ext, StringComparer.OrdinalIgnoreCase)) return null;
        var bare = Path.GetFileNameWithoutExtension(fileName);
        if (!bare.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase)) return null;
        var lang = bare[(stem.Length + 1)..];
        return LanguageNames.Contains(lang) ? (lang, ext.ToUpperInvariant()) : null;
    }

    /// <summary>Does a <c>.bsa</c> beside the plugin embed strings for it? Returns the archive's path when it does, or
    /// when the archive could not be parsed at all — an archive we cannot see into is refused rather than assumed
    /// harmless (a malformed one is not merely opaque: it takes the plugin's own open down with an ArchiveException).
    /// No archive is opened unless one is present, which is the common case at ~0.06 ms.</summary>
    static (string? Path, bool Unreadable) BsaEmbedding(string folder, string stem)
    {
        // A POSITIVE hit wins over an unreadable one, and the pass order is why: returning on the first unreadable
        // archive made a single unparseable .bsa anywhere in game-Data answer for every plugin that searched there —
        // naming an archive that neither sits beside the plugin nor carries its tables. An archive we cannot see into
        // still refuses (Q3), but only once no archive has actually claimed the stem.
        string? unreadable = null;
        foreach (var e in ArchiveStrings(folder))
        {
            if (e.Unreadable) { unreadable ??= e.Archive; continue; }
            if (e.Stems.Contains(stem)) return (e.Archive, false);
        }
        return unreadable is null ? (null, false) : (unreadable, true);
    }

    /// <summary>Per archive in a folder: which plugin stems it embeds strings for. Cached by folder, because game-Data
    /// holds dozens of archives and a whole-order sweep would otherwise re-enumerate every one of them once per
    /// plugin. Keyed on the folder's own write time, so an archive added or replaced between calls invalidates the
    /// entry rather than serving a stale answer.</summary>
    static readonly Dictionary<string, (DateTime Stamp, List<ArchiveEntry> Entries)> ArchiveCache =
        new(StringComparer.OrdinalIgnoreCase);

    readonly record struct ArchiveEntry(string Archive, HashSet<string> Stems, bool Unreadable);

    static readonly List<ArchiveEntry> NoArchives = new();

    static List<ArchiveEntry> ArchiveStrings(string folder)
    {
        DateTime stamp;
        string[] archives;
        try
        {
            stamp = Directory.GetLastWriteTimeUtc(folder);
            archives = Directory.GetFiles(folder, "*.bsa");
        }
        catch (IOException) { return NoArchives; }
        catch (UnauthorizedAccessException) { return NoArchives; }
        if (archives.Length == 0) return NoArchives;

        lock (ArchiveCache)
            if (ArchiveCache.TryGetValue(folder, out var hit) && hit.Stamp == stamp) return hit.Entries;

        var entries = new List<ArchiveEntry>();
        foreach (var a in archives)
        {
            var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool unreadable = false;
            try
            {
                var reader = Archive.CreateReader(GameRelease.SkyrimSE, a);
                foreach (var entry in reader.Files)
                {
                    var p = entry.Path.ToString();
                    if (!p.StartsWith("strings", StringComparison.OrdinalIgnoreCase)) continue;
                    var bare = System.IO.Path.GetFileNameWithoutExtension(p);
                    // "<stem>_<Language>" — split at the LAST underscore and keep it only if the tail names a language
                    // Mutagen models, so a stem that itself contains underscores survives intact.
                    var cut = bare.LastIndexOf('_');
                    if (cut > 0 && LanguageNames.Contains(bare[(cut + 1)..])) stems.Add(bare[..cut]);
                }
            }
            catch (Exception) { unreadable = true; }
            entries.Add(new ArchiveEntry(a, stems, unreadable));
        }

        lock (ArchiveCache) ArchiveCache[folder] = (stamp, entries);
        return entries;
    }

    static IReadOnlyList<string> Names(Dictionary<string, List<string>> m) => m.Keys.OrderBy(x => x).ToList();

    static IReadOnlyDictionary<string, IReadOnlyList<string>> Incomplete(Dictionary<string, List<string>> m)
        => m.Where(kv => kv.Value.Count < Kinds.Length)
            .ToDictionary(kv => kv.Key,
                          kv => (IReadOnlyList<string>)Kinds.Where(k => !kv.Value.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList(),
                          StringComparer.OrdinalIgnoreCase);

    static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoneIncomplete =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    static LocalizedAssessment Plain(LocalizedShape shape, bool gameDataUnknown = false)
        => new(shape, Array.Empty<string>(), NoneIncomplete, Array.Empty<string>(), null, false, gameDataUnknown);
}

using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Strings;

namespace HousecarlCore;

/// <summary>What a plugin's own header said when houseCARL asked whether it is flagged LOCALIZED — three answers, because "could not read it" is not "not localized".</summary>
public enum LocalizedFlagRead
{
    NotLocalized,

    Localized,

    /// <summary>The header could not be read at all; whether it is localized is UNKNOWN, and every caller must treat it as unknown rather than as an answer.</summary>
    Unreadable,
}

/// <summary>Which strings shape a plugin is in — the one classifier behind every localized-write decision; the shapes are listed in docs/architecture/write-path.md.</summary>
public enum LocalizedShape
{
    NotLocalized,

    /// <summary>The plugin could not be read, so whether it is localized is UNKNOWN; a lane that must not act on an unclassifiable destination refuses on this.</summary>
    Unreadable,

    LooseComplete,

    LoosePartial,

    LooseWithGameDataDuplicate,

    BsaEmbedded,

    GameDataOnly,

    StringsFolderUnreadable,

    ModFolderUnreadable,

    Nowhere,
}

/// <summary>The table files in the <c>Strings\</c> folder beside a plugin that houseCARL did NOT match to it: the capped names a refusal may quote, and the TRUE count they were taken from.</summary>
/// <param name="Names">The names the sentence may quote — at most <see cref="Cap"/> of them, ordered.</param>
/// <param name="Total">How many unmatched table files are actually there. Never less than <c>Names.Count</c>.</param>
public sealed record UnmatchedTableFiles(IReadOnlyList<string> Names, int Total)
{
    public const int Cap = 8;

    public int Unnamed => Total - Names.Count;

    public static readonly UnmatchedTableFiles None = new(Array.Empty<string>(), 0);
}

/// <param name="IncompleteLanguages">Those of <paramref name="Languages"/> missing at least one table kind, with the kinds they are missing.</param>
/// <param name="BsaInGameData">The archive was found away from the plugin (the game's Data folder, or <paramref name="BsaModFolder"/>), which the refusal has to say.</param>
/// <param name="BsaModFolder">The MO2 mod folder that away-from-the-plugin archive was found in, or null for the game's Data folder.</param>
/// <param name="BsaUnreadable">The archive could not be parsed, so whether it embeds this plugin's strings is unknown; classified as embedded rather than assumed harmless.</param>
/// <param name="GameDataUnknown">No game-Data folder was supplied, so a competing set there could not be checked — not the same as having checked and found none.</param>
/// <param name="UnmatchedTables">Table-extension files in the <c>Strings\</c> folder beside the plugin that houseCARL did NOT match to it in a language Mutagen models.</param>
/// <param name="StringsModFolder">For <see cref="LocalizedShape.GameDataOnly"/>: the MO2 layer whose loose Strings folder the read takes the tables from, or null for the game's Data folder.</param>
/// <param name="OrderLanguage">The order's language when it is not English: only its tables count, and a <see cref="LocalizedShape.Nowhere"/> shape's <paramref name="Languages"/> are the other languages found beside the plugin.</param>
public sealed record LocalizedAssessment(
    LocalizedShape Shape,
    IReadOnlyList<string> Languages,
    IReadOnlyDictionary<string, IReadOnlyList<string>> IncompleteLanguages,
    IReadOnlyList<string> GameDataLanguages,
    string? BsaPath,
    bool BsaUnreadable,
    bool GameDataUnknown,
    bool BsaInGameData = false,
    UnmatchedTableFiles? UnmatchedTables = null,
    string? BsaModFolder = null,
    string? StringsModFolder = null,
    string? OrderLanguage = null)
{
    public UnmatchedTableFiles UnmatchedTables { get; init; } = UnmatchedTables ?? UnmatchedTableFiles.None;
}

/// <summary>Classifies where a localized plugin's <c>.STRINGS</c> / <c>.DLSTRINGS</c> / <c>.ILSTRINGS</c> actually live; it supplies WORDS, not the in-place outcome (docs/architecture/write-path.md).</summary>
public static class LocalizedStrings
{
    /// <summary>The three table kinds a localized plugin's text is split across; a language is COMPLETE only with all three.</summary>
    internal static readonly string[] Kinds = { "STRINGS", "DLSTRINGS", "ILSTRINGS" };

    /// <summary>Classify the plugin at <paramref name="pluginPath"/>; a null <paramref name="dataDir"/> means no game-Data set can be seen and the game-Data shapes are unreachable.</summary>
    public static LocalizedAssessment Assess(string pluginPath, string? dataDir)
    {
        // THREE answers, and the third is not a shape: an unclassifiable file refuses on Unreadable, in those words.
        switch (WriteEngine.PluginIsLocalized(pluginPath))
        {
            case LocalizedFlagRead.NotLocalized: return Plain(LocalizedShape.NotLocalized);
            case LocalizedFlagRead.Unreadable: return Plain(LocalizedShape.Unreadable, dataDir is null);
        }

        var folder = Path.GetDirectoryName(pluginPath);
        if (folder is null) return Plain(LocalizedShape.Nowhere);
        var stem = Path.GetFileNameWithoutExtension(pluginPath);

        // The read's answer: the order's language (any language counts for English, as before #1212) and its winners.
        var text = GameLanguage.Current;
        var only = text.Language == Language.English ? null : text.Language.ToString();
        var ownFolder = ReadStringsFolder(Path.Combine(folder, "Strings"), stem);
        var own = InLanguage(ownFolder.Languages, only);
        var unmatched = ownFolder.Unmatched;
        // The game-Data side stays two-answer on purpose: nothing rendered off it asserts an absence.
        var gameData = dataDir is null
            ? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            : InLanguage(ReadStringsFolder(Path.Combine(dataDir, "Strings"), stem).Languages, only);

        // An archive the write cannot rewrite decides the shape whatever else is on disk; the folder's third answer is carried, not dropped.
        var (bsaPath, bsaUnreadable, folderUnlistable) = BsaEmbedding(folder, stem, only);
        if (bsaPath is not null)
            return new LocalizedAssessment(LocalizedShape.BsaEmbedded, Names(own), Incomplete(own), Names(gameData),
                                           bsaPath, bsaUnreadable, dataDir is null, UnmatchedTables: unmatched);

        // THE FOLDER'S THIRD ANSWER: a Strings folder that is there and could not be LISTED tells us nothing about what is in it.
        if (ownFolder.Read == StringsFolderRead.Unlistable)
            return new LocalizedAssessment(LocalizedShape.StringsFolderUnreadable, Array.Empty<string>(), NoneIncomplete,
                                           Names(gameData), null, false, dataDir is null, UnmatchedTables: unmatched);

        if (own.Count > 0)
        {
            var incomplete = Incomplete(own);
            if (incomplete.Count > 0)
                return new LocalizedAssessment(LocalizedShape.LoosePartial, Names(own), incomplete, Names(gameData), null, false, dataDir is null, UnmatchedTables: unmatched);
            if (gameData.Count > 0)
                return new LocalizedAssessment(LocalizedShape.LooseWithGameDataDuplicate, Names(own), incomplete, Names(gameData), null, false, false, UnmatchedTables: unmatched);
            return new LocalizedAssessment(LocalizedShape.LooseComplete, Names(own), incomplete, Names(gameData), null, false, dataDir is null, UnmatchedTables: unmatched);
        }

        // A translation mod's or the overwrite folder's loose Strings, when the read takes the tables from there.
        if (text.WinnersFor(pluginPath)?.Loose is { } lw && lw.Layer != AssetResolver.DataLayerName
            && !string.Equals(lw.Dir, Path.Combine(folder, "Strings"), StringComparison.OrdinalIgnoreCase)
            && InLanguage(ReadStringsFolder(lw.Dir, stem).Languages, only) is { Count: > 0 } there)
            return new LocalizedAssessment(LocalizedShape.GameDataOnly, Array.Empty<string>(), NoneIncomplete, Names(there), null, false, false,
                                           UnmatchedTables: unmatched, StringsModFolder: lw.Layer);

        if (gameData.Count > 0)
            return new LocalizedAssessment(LocalizedShape.GameDataOnly, Array.Empty<string>(), NoneIncomplete, Names(gameData), null, false, false, UnmatchedTables: unmatched);

        // The vanilla masters' strings live in the game-Data ARCHIVES and a Creation Club plugin's in its archive's mod
        // folder: the same home the read opens, searched LAST so an ordinary plugin never pays for it.
        if (dataDir is not null)
        {
            var home = ArchiveHomeFor(pluginPath, dataDir);
            var (gdBsa, gdUnreadable, _) = BsaEmbedding(home.Folder, stem, only);
            if (gdBsa is not null)
                return new LocalizedAssessment(LocalizedShape.BsaEmbedded, Array.Empty<string>(), NoneIncomplete,
                                               Array.Empty<string>(), gdBsa, gdUnreadable, false, BsaInGameData: true,
                                               UnmatchedTables: unmatched, BsaModFolder: home.Mod);
        }

        // NOTHING FOUND ANYWHERE, and the mod folder itself would not list — it still refuses, but it claims no absence.
        if (folderUnlistable)
            return Plain(LocalizedShape.ModFolderUnreadable, dataDir is null) with { UnmatchedTables = unmatched };

        return Plain(LocalizedShape.Nowhere, dataDir is null) with
        {
            UnmatchedTables = unmatched,
            Languages = only is null ? Array.Empty<string>() : Names(ownFolder.Languages),
            OrderLanguage = only,
        };
    }

    /// <summary>Only <paramref name="language"/>'s tables, or every language's when it is null.</summary>
    static Dictionary<string, List<string>> InLanguage(Dictionary<string, List<string>> languages, string? language)
        => language is null ? languages
         : languages.Where(kv => kv.Key.Equals(language, StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>Should an in-place write of <paramref name="pluginPath"/> be refused, and with what sentence? Null is returned for exactly one answer, "read the header, the flag is clear".</summary>
    /// <param name="laneClause">The calling lane's own remedy clause, appended to the shape's explanation.</param>
    public static string? RefusalFor(string pluginPath, string pluginFileName, string? dataDir, string? laneClause = null)
    {
        var a = Assess(pluginPath, dataDir);
        if (a.Shape == LocalizedShape.NotLocalized) return null;
        return LocalizedTargetUnsupportedException.Shaped(pluginFileName, a, laneClause);
    }

    /// <summary>Did houseCARL actually READ this plugin's header and find the LOCALIZED flag set? The predicate a lane needs before its output SAYS something about localization.</summary>
    public static bool ConfirmedLocalized(LocalizedShape shape) => shape switch
    {
        LocalizedShape.LooseComplete or LocalizedShape.LoosePartial or LocalizedShape.LooseWithGameDataDuplicate
            or LocalizedShape.BsaEmbedded or LocalizedShape.GameDataOnly or LocalizedShape.StringsFolderUnreadable
            or LocalizedShape.ModFolderUnreadable or LocalizedShape.Nowhere => true,

        LocalizedShape.NotLocalized => false,

        LocalizedShape.Unreadable => false,

        _ => false,
    };

    /// <summary>The same decision as <see cref="RefusalFor"/>, rendered WITHOUT the "houseCARL did not write X" head.</summary>
    public static string? RefusalReasonFor(string pluginPath, string pluginFileName, string? dataDir)
        => RefusalShapeFor(pluginPath, pluginFileName, dataDir)?.Why;

    /// <summary>The same decision as <see cref="RefusalReasonFor"/>, carrying the SHAPE it was made on, for a lane reporting on a set of blocked plugins.</summary>
    public static (LocalizedShape Shape, string Why)? RefusalShapeFor(string pluginPath, string pluginFileName, string? dataDir)
    {
        var a = Assess(pluginPath, dataDir);
        if (a.Shape == LocalizedShape.NotLocalized) return null;
        return (a.Shape, LocalizedTargetUnsupportedException.ShapeBody(a));
    }

    /// <summary>Where a plugin whose tables are not beside it finds its archive: the folder of the archive that wins its tables in the game's language when that is another MO2 layer (#1075), else the game Data. The read's own winner (<see cref="InstanceText.WinnersFor"/>), so census and read cannot disagree.</summary>
    public static (string Folder, string? Mod) ArchiveHomeFor(string pluginPath, string dataDir)
        => GameLanguage.Current.WinnersFor(pluginPath) is { Archive: { } a }
           && a.Layer != AssetResolver.DataLayerName && Path.GetDirectoryName(a.Path) is { } folder
           && !string.Equals(folder, Path.GetDirectoryName(pluginPath), StringComparison.OrdinalIgnoreCase)
            ? (folder, a.Layer) : (dataDir, null);

    /// <summary>Does the plugin's OWN folder carry its tables, in <paramref name="language"/>'s when it is not English (any language's for English, as before #1212)? The read's rule for a plugin no MO2 layer carries tables for; any IO fault answers TRUE (#369).</summary>
    public static bool OwnFolderCarriesStringsFor(string pluginPath, Language language = Language.English)
        => OwnFolderCarries(pluginPath, language == Language.English ? null : language.ToString());

    static bool OwnFolderCarries(string pluginPath, string? language)
    {
        try
        {
            var folder = Path.GetDirectoryName(pluginPath);
            // A folder that is not there is not a clean, empty read, and the redirect is only ever taken on a clean one.
            if (folder is null || !Directory.Exists(folder)) return true;
            var stem = Path.GetFileNameWithoutExtension(pluginPath);
            var loose = ReadStringsFolder(Path.Combine(folder, "Strings"), stem);
            if (loose.Read == StringsFolderRead.Unlistable) return true;
            if (language is null ? loose.Languages.Count > 0 : loose.Languages.ContainsKey(language)) return true;
            // BsaEmbedding answers non-null both when the archive embeds this plugin's tables and when it could not be parsed.
            var bsa = BsaEmbedding(folder, stem, language);
            if (bsa.FolderUnlistable) return true;
            return bsa.Path is not null;
        }
        catch { return true; }
    }

    /// <summary>Could houseCARL find NO strings source at all for a plugin it read and found flagged localized (#371)? <see cref="LocalizedShape.Unreadable"/> is deliberately not one of them.</summary>
    public static bool ResolvesNowhere(LocalizedShape shape) => shape switch
    {
        LocalizedShape.Nowhere or LocalizedShape.StringsFolderUnreadable or LocalizedShape.ModFolderUnreadable => true,
        _ => false,
    };

    /// <summary>The loose table files this plugin's own folder carries — what a read resolves against, and what a refusal has to leave byte-identical.</summary>
    public static IReadOnlyList<string> OwnTableFiles(string pluginPath)
    {
        var folder = Path.GetDirectoryName(pluginPath);
        if (folder is null) return Array.Empty<string>();
        return TableFilesIn(Path.Combine(folder, "Strings"), Path.GetFileNameWithoutExtension(pluginPath));
    }

    /// <summary>The table files in <paramref name="stringsDir"/> that belong to the plugin named <paramref name="stem"/>, never a neighbour's.</summary>
    public static IReadOnlyList<string> TableFilesIn(string stringsDir, string stem)
        => ReadStringsFolder(stringsDir, stem).MatchedFiles;

    /// <summary>What happened when houseCARL looked in a <c>Strings\</c> folder — THREE answers, for the same reason the plugin's own header read has three.</summary>
    public enum StringsFolderRead
    {
        Absent,

        Listed,

        Unlistable,
    }

    /// <summary>One look inside a <c>Strings\</c> folder: which languages matched the plugin, which table files did NOT, and whether the folder could be read at all — all from ONE enumeration.</summary>
    readonly record struct StringsFolder(
        StringsFolderRead Read,
        Dictionary<string, List<string>> Languages,
        UnmatchedTableFiles Unmatched,
        IReadOnlyList<string> MatchedFiles);

    static StringsFolder EmptyFolder(StringsFolderRead read)
        => new(read, new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase),
               UnmatchedTableFiles.None, Array.Empty<string>());

    static StringsFolder ReadStringsFolder(string stringsDir, string stem)
    {
        if (!Directory.Exists(stringsDir)) return EmptyFolder(StringsFolderRead.Absent);

        List<string> files;
        // .ToList() INSIDE the try: EnumerateFiles is lazy, so an access failure surfaces here rather than at the call.
        try { files = Directory.EnumerateFiles(stringsDir).ToList(); }
        catch (IOException) { return EmptyFolder(StringsFolderRead.Unlistable); }
        catch (UnauthorizedAccessException) { return EmptyFolder(StringsFolderRead.Unlistable); }

        var langs = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var matched = new List<string>();
        var unmatched = new List<string>();
        foreach (var p in files)
        {
            var name = Path.GetFileName(p);
            if (Parse(name, stem) is { } hit)
            {
                matched.Add(p);
                if (!langs.TryGetValue(hit.Language, out var kinds)) langs[hit.Language] = kinds = new List<string>();
                if (!kinds.Contains(hit.Kind, StringComparer.OrdinalIgnoreCase)) kinds.Add(hit.Kind);
            }
            else if (Kinds.Contains(Path.GetExtension(name).TrimStart('.'), StringComparer.OrdinalIgnoreCase))
                unmatched.Add(name);
        }
        unmatched.Sort(StringComparer.OrdinalIgnoreCase);
        return new StringsFolder(StringsFolderRead.Listed, langs,
                                 new UnmatchedTableFiles(unmatched.Take(UnmatchedTableFiles.Cap).ToList(), unmatched.Count),
                                 matched);
    }

    /// <summary>Every language Mutagen models, as the token it writes into a table's file name; the classifier's language set IS Mutagen's set, by construction.</summary>
    static readonly HashSet<string> LanguageNames =
        new(Enum.GetNames(typeof(Language)), StringComparer.OrdinalIgnoreCase);

    /// <summary>Split "<c>MyMod_English.DLSTRINGS</c>" into its language and kind for the plugin named <paramref name="stem"/>; null when it belongs to another plugin or names no modelled language.</summary>
    static (string Language, string Kind)? Parse(string fileName, string stem)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.');
        if (!Kinds.Contains(ext, StringComparer.OrdinalIgnoreCase)) return null;
        var bare = Path.GetFileNameWithoutExtension(fileName);
        if (!bare.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase)) return null;
        var lang = bare[(stem.Length + 1)..];
        return LanguageNames.Contains(lang) ? (lang, ext.ToUpperInvariant()) : null;
    }

    /// <summary>Does a <c>.bsa</c> in <paramref name="folder"/> embed strings for the plugin, in <paramref name="language"/> when one is named? Returns the archive's path when it does, or when the archive could not be parsed at all.</summary>
    static (string? Path, bool Unreadable, bool FolderUnlistable) BsaEmbedding(string folder, string stem, string? language = null)
    {
        var (folderUnlistable, entries) = ArchiveStrings(folder);
        // The folder would not list, so which archives are in it was never established — its own answer, not "no archive".
        if (folderUnlistable) return (null, false, true);

        // A POSITIVE hit wins over an unreadable one, so one unparseable .bsa cannot answer for every plugin that searched there.
        string? unreadable = null;
        foreach (var e in entries)
        {
            if (e.Unreadable) { unreadable ??= e.Archive; continue; }
            if (language is null ? e.Stems.Contains(stem) : e.Tables.Contains(stem + "_" + language)) return (e.Archive, false, false);
        }
        return unreadable is null ? (null, false, false) : (unreadable, true, false);
    }

    /// <summary>Per archive in a folder: which plugin stems it embeds strings for, cached by folder and keyed on each archive's OWN name, length and write time.
    /// <para>Nothing pins the stale-versus-fresh case: observing it needs an archive that PARSES, and Mutagen exposes no in-process archive builder.</para></summary>
    static readonly Dictionary<string, (string Stamp, List<ArchiveEntry> Entries)> ArchiveCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <param name="Tables">Every "<c>stem_Language</c>" the archive carries a table for.</param>
    readonly record struct ArchiveEntry(string Archive, HashSet<string> Stems, HashSet<string> Tables, bool Unreadable);

    static readonly List<ArchiveEntry> NoArchives = new();

    /// <summary>The cache key for one folder's archives: every <c>.bsa</c>'s name, length and write time.</summary>
    static string ArchiveStamp(string[] archives)
    {
        var parts = new List<string>(archives.Length);
        foreach (var a in archives.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            // An archive we cannot stat is keyed as un-stattable rather than skipped, so two folder states cannot share a key.
            try
            {
                var fi = new FileInfo(a);
                parts.Add($"{fi.Name}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}");
            }
            catch (IOException) { parts.Add(Path.GetFileName(a) + "|?"); }
            catch (UnauthorizedAccessException) { parts.Add(Path.GetFileName(a) + "|?"); }
        }
        return string.Join(" ", parts);
    }

    /// <summary>The string-table file names one archive carries under <c>strings\</c>, cached against the archive's own stamp; none for an archive that will not parse.</summary>
    internal static IReadOnlyCollection<string> ArchiveTableFiles(string archivePath)
    {
        var stamp = FileStamp.Of(archivePath);
        lock (TableFileCache)
            if (TableFileCache.TryGetValue(archivePath, out var hit) && hit.Stamp == stamp) return hit.Files;
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Archive.CreateReader(GameRelease.SkyrimSE, archivePath).TryGetFolder("strings", out var folder))
                foreach (var f in folder.Files)
                    if (Kinds.Contains(System.IO.Path.GetExtension(f.Path).TrimStart('.'), StringComparer.OrdinalIgnoreCase))
                        files.Add(System.IO.Path.GetFileName(f.Path));
        }
        catch (Exception) { }   // Mutagen's own read of an unparseable archive throws where it is used; it wins nothing here
        lock (TableFileCache) TableFileCache[archivePath] = (stamp, files);
        return files;
    }

    static readonly Dictionary<string, (FileStamp Stamp, HashSet<string> Files)> TableFileCache = new(StringComparer.OrdinalIgnoreCase);

    static (bool Unlistable, List<ArchiveEntry> Entries) ArchiveStrings(string folder)
    {
        string[] archives;
        // THE SAME THREE ANSWERS the loose look has: a folder that could not be listed is not one found to hold no archives.
        try { archives = Directory.GetFiles(folder, "*.bsa"); }
        catch (IOException) { return (true, NoArchives); }
        catch (UnauthorizedAccessException) { return (true, NoArchives); }
        if (archives.Length == 0) return (false, NoArchives);
        var stamp = ArchiveStamp(archives);

        lock (ArchiveCache)
            if (ArchiveCache.TryGetValue(folder, out var hit) && hit.Stamp == stamp) return (false, hit.Entries);

        var entries = new List<ArchiveEntry>();
        foreach (var a in archives)
        {
            var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool unreadable = false;
            try
            {
                var reader = Archive.CreateReader(GameRelease.SkyrimSE, a);
                foreach (var entry in reader.Files)
                {
                    var p = entry.Path.ToString();
                    if (!p.StartsWith("strings", StringComparison.OrdinalIgnoreCase)) continue;
                    var bare = System.IO.Path.GetFileNameWithoutExtension(p);
                    // "<stem>_<Language>" — split at the LAST underscore, so a stem that itself contains underscores survives intact.
                    var cut = bare.LastIndexOf('_');
                    if (cut > 0 && LanguageNames.Contains(bare[(cut + 1)..])) { stems.Add(bare[..cut]); tables.Add(bare); }
                }
            }
            catch (Exception) { unreadable = true; }
            entries.Add(new ArchiveEntry(a, stems, tables, unreadable));
        }

        lock (ArchiveCache) ArchiveCache[folder] = (stamp, entries);
        return (false, entries);
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

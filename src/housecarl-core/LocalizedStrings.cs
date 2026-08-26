using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Strings;

namespace HousecarlCore;

/// <summary>What a plugin's own header said when houseCARL asked whether it is flagged LOCALIZED — three answers, not
/// two, because "could not read it" is not "not localized" and a decision that treats them the same fails OPEN.
///
/// <para>The two-answer form of this is why this enum exists: the read returned a bool, a fault answered
/// <c>false</c>, and a destination held by another process for the instant of the check — an AV scan, MO2 refreshing,
/// xEdit, the game — classified as not-localized and let a write through. Measured on a fixture that then read every
/// value back empty.</para></summary>
public enum LocalizedFlagRead
{
    /// <summary>Read the header; the LOCALIZED flag is clear.</summary>
    NotLocalized,

    /// <summary>Read the header; the LOCALIZED flag is set.</summary>
    Localized,

    /// <summary>The header could not be read at all — the file is absent, locked, or not a plugin. Whether it is
    /// localized is UNKNOWN, and every caller must treat it as unknown rather than as an answer.</summary>
    Unreadable,
}

/// <summary>Which strings shape a plugin is in — the one classifier behind every localized-write decision: what the
/// in-place refusal SAYS (the outcome is the same for all of them), and the load-order frequency sweep.</summary>
public enum LocalizedShape
{
    /// <summary>Not flagged localized: its text is inside the plugin and none of this applies.</summary>
    NotLocalized,

    /// <summary>The plugin could not be read, so whether it is localized — let alone where its text lives — is
    /// UNKNOWN. Distinct from every shape below, each of which is something houseCARL looked at and found. A lane that
    /// must not act on a destination it cannot classify refuses on this rather than proceeding.</summary>
    Unreadable,

    /// <summary>A loose <c>Strings\</c> beside the plugin, every language present carrying all three table kinds, and
    /// no competing set in game-Data. The arrangement houseCARL classifies most confidently — and, like every other
    /// shape, still refused for an in-place write.</summary>
    LooseComplete,

    /// <summary>A loose set beside the plugin in which some language is missing a table kind. Re-serializing
    /// MATERIALISES the missing files holding empty values, so a player on that language loses the fallback they had.</summary>
    LoosePartial,

    /// <summary>A loose set beside the plugin AND a set for the same plugin in game-Data. Which of the two describes
    /// the plugin is ambiguous, and only one of them is where a write beside the plugin would land.</summary>
    LooseWithGameDataDuplicate,

    /// <summary>The plugin's strings are embedded in a <c>.bsa</c> beside it, which a plugin write cannot rewrite.</summary>
    BsaEmbedded,

    /// <summary>No strings beside the plugin; they resolve from game-Data (the "Cleaned Base Game Masters" /
    /// translation-mod pattern). A write that put tables beside the plugin would SHADOW that set rather than replace
    /// it — the values would be faithful, but the game-Data set would silently go stale.</summary>
    GameDataOnly,

    /// <summary>Flagged localized, with a <c>Strings\</c> folder beside it that houseCARL could not LIST — an ACL that
    /// denies enumeration, a path the filesystem refuses, a folder held by something else.
    ///
    /// <para>Its own shape because the folder needs the third answer the plugin already has: "enumerated it and found
    /// nothing" and "could not enumerate it" are different facts, and collapsing them classified a folder holding a
    /// complete loose set as <see cref="Nowhere"/> — whose sentence then told the modder there were no .STRINGS files
    /// beside a plugin whose folder is full. The same unchecked-absence falsehood that arm was rewritten twice to
    /// remove, arriving through the exception path instead of the matching path.</para></summary>
    StringsFolderUnreadable,

    /// <summary>Flagged localized, and houseCARL can find no strings source for it — the residual case (#371).
    ///
    /// <para>This says what houseCARL could FIND, not what exists: MO2's VFS merges mod folders at runtime, so a
    /// plugin in one mod folder can resolve its strings from a <c>.bsa</c> in ANOTHER, which no path houseCARL walks
    /// can see. That is not hypothetical — on the measured order all 31 plugins in this shape are Creation Club
    /// content whose plugin sits in a "Cleaned Masters" folder while its archive sits in the content's own folder.
    /// Anything user-facing must therefore say houseCARL cannot find the strings, never that the plugin has none.</para></summary>
    Nowhere,
}

/// <summary>The table files in the <c>Strings\</c> folder beside a plugin that houseCARL did NOT match to it: the
/// NAMES a refusal may quote — capped, because one folder can hold a translation mod's whole set — and the TRUE
/// count those names were taken from.
///
/// <para><b>One type carrying both, so a sentence cannot render the cap as the count.</b> The refusal used to say
/// "the Strings folder beside it holds {list.Count} .STRINGS file(s) — a, b, c, d, e, f, g, h" off a list that ended
/// <c>.Take(8)</c>, so a folder holding thirty was described as holding eight and the list stopped without saying so.
/// That is an assertion about the modder's disk the code never checked — the same class as the two absence claims
/// this sentence was already rewritten twice to remove, arriving through the cap instead of the matching.</para></summary>
/// <param name="Names">The names the sentence may quote — at most <see cref="Cap"/> of them, ordered.</param>
/// <param name="Total">How many unmatched table files are actually there. Never less than <c>Names.Count</c>.</param>
public sealed record UnmatchedTableFiles(IReadOnlyList<string> Names, int Total)
{
    /// <summary>How many names a refusal quotes before it starts counting instead.</summary>
    public const int Cap = 8;

    /// <summary>The unmatched files the sentence did not name — the "and N more" it owes the reader.</summary>
    public int Unnamed => Total - Names.Count;

    public static readonly UnmatchedTableFiles None = new(Array.Empty<string>(), 0);
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
/// checked. This is NOT the same as having checked and found none, and the refusal sentences say which.</param>
/// <param name="UnmatchedTables">File names in the <c>Strings\</c> folder beside the plugin that carry a table
/// extension and that houseCARL did NOT match to this plugin in a language it models — a neighbouring plugin's tables
/// out of the shared folder, or this plugin's own tables named for a language token Mutagen does not model
/// (<c>ZRef_ptbr.STRINGS</c>). Carried so the <see cref="LocalizedShape.Nowhere"/> sentence can describe the folder
/// the modder is looking at instead of claiming nothing is in it — the falsehood that survived one directed fix by
/// being re-stated a second way. Names and true count travel together; see
/// <see cref="UnmatchedTableFiles"/>.</param>
public sealed record LocalizedAssessment(
    LocalizedShape Shape,
    IReadOnlyList<string> Languages,
    IReadOnlyDictionary<string, IReadOnlyList<string>> IncompleteLanguages,
    IReadOnlyList<string> GameDataLanguages,
    string? BsaPath,
    bool BsaUnreadable,
    bool GameDataUnknown,
    bool BsaInGameData = false,
    UnmatchedTableFiles? UnmatchedTables = null)
{
    /// <summary>The unmatched table files beside the plugin, never null — see the parameter's own note.</summary>
    public UnmatchedTableFiles UnmatchedTables { get; init; } = UnmatchedTables ?? UnmatchedTableFiles.None;
}

/// <summary>
/// Classifies where a localized plugin's <c>.STRINGS</c> / <c>.DLSTRINGS</c> / <c>.ILSTRINGS</c> actually live, so a
/// refusal can tell the caller where their text is.
///
/// <para><b>It supplies WORDS, not the in-place outcome.</b> That answer is the same for every shape — no — and at
/// the write's own choke point it is decided off the mod already in memory, which cannot fail to be read. This
/// classifier re-opens the file on disk, which can; making the OUTCOME depend on that re-read is what let a locked
/// destination through, so it decides only which sentence the refusal carries. At the SERVICE pre-flights, which
/// hold no such mod, an unreadable file refuses too — see <see cref="RefusalFor"/>.</para>
///
/// <para>The shapes were measured, not assumed (localized-write-probe, 2026-08-26): Mutagen loads and re-emits EVERY
/// language present beside a plugin, so a complete loose set of any size round-trips through a serialize; a language
/// present only partially has its missing kinds materialised holding empty values; and detection was priced in the
/// same run — the language enumeration at ~0.1 ms, and asking a real 101 MB archive whether it embeds strings keyed
/// to a given plugin at under 1 ms.</para>
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
        // THREE answers, and the third is not a shape. A plugin houseCARL could not read is not a plugin houseCARL
        // knows to be safe: every lane that must not act on an unclassifiable file refuses on Unreadable, and the
        // sentence says so in those words rather than describing an arrangement nobody looked at.
        switch (WriteEngine.PluginIsLocalized(pluginPath))
        {
            case LocalizedFlagRead.NotLocalized: return Plain(LocalizedShape.NotLocalized);
            case LocalizedFlagRead.Unreadable: return Plain(LocalizedShape.Unreadable, dataDir is null);
        }

        var folder = Path.GetDirectoryName(pluginPath);
        if (folder is null) return Plain(LocalizedShape.Nowhere);
        var stem = Path.GetFileNameWithoutExtension(pluginPath);

        var ownFolder = ReadStringsFolder(Path.Combine(folder, "Strings"), stem);
        var own = ownFolder.Languages;
        var unmatched = ownFolder.Unmatched;
        // The game-Data side stays two-answer on purpose, and it is safe to: nothing rendered off it asserts an
        // absence. The Nowhere sentence says houseCARL "cannot find" the text, AlsoLoose names a game-Data set only
        // when one was found, and GameDataUnknown already carries the one absence claim there is (no Data folder to
        // search). The claim that WAS false — "no .STRINGS files beside it" — is about the folder below.
        var gameData = dataDir is null
            ? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            : ReadStringsFolder(Path.Combine(dataDir, "Strings"), stem).Languages;

        // An archive the write cannot rewrite decides the shape regardless of what else is on disk — a loose set beside
        // a BSA that also embeds this plugin's strings is an ambiguous source, and which one Mutagen prefers is not
        // something this classifier should be guessing at when the answer is "refuse either way".
        var (bsaPath, bsaUnreadable) = BsaEmbedding(folder, stem);
        if (bsaPath is not null)
            return new LocalizedAssessment(LocalizedShape.BsaEmbedded, Names(own), Incomplete(own), Names(gameData),
                                           bsaPath, bsaUnreadable, dataDir is null, UnmatchedTables: unmatched);

        // THE FOLDER'S THIRD ANSWER. A Strings folder that is there and could not be LISTED tells us nothing about
        // what is in it — so it cannot fall through to the loose shapes (which would report zero languages) nor to
        // Nowhere (whose sentence then asserts there are no .STRINGS files beside a plugin whose folder may hold a
        // complete set). Checked after the archive, which is decisive whatever else is on disk, and before every shape
        // that reasons from what the folder was seen to contain.
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

        if (gameData.Count > 0)
            return new LocalizedAssessment(LocalizedShape.GameDataOnly, Array.Empty<string>(), NoneIncomplete, Names(gameData), null, false, false, UnmatchedTables: unmatched);

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
                                               Array.Empty<string>(), gdBsa, gdUnreadable, false, BsaInGameData: true,
                                               UnmatchedTables: unmatched);
        }

        return Plain(LocalizedShape.Nowhere, dataDir is null) with { UnmatchedTables = unmatched };
    }

    /// <summary>Should an in-place write of <paramref name="pluginPath"/> be refused, and if so with what sentence?
    /// Null means the lane may proceed — and it is returned for exactly ONE answer, "read the header, the flag is
    /// clear". A plugin houseCARL could not read refuses with a sentence saying that, because a pre-flight that
    /// answered "go ahead" on a file it never managed to open is how this decision failed OPEN.
    ///
    /// <para>THE one home for the pre-flight decision: every service lane that has to predict the write's answer
    /// before spending a consent or reporting a dry run calls this. The write's own choke point does NOT — it decides
    /// off the mod it already holds in memory, which cannot fail to be read, and calls
    /// <see cref="LocalizedTargetUnsupportedException.Shaped"/> only for the WORDS. The two questions differ: a
    /// pre-flight asks "is the file I am about to write localized", the write asks "the mod I hold IS localized, so
    /// where does the file at this path keep its text".</para></summary>
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

    /// <summary>The loose table files this plugin's own folder carries — what a read resolves against, and what a
    /// refusal has to leave byte-identical.</summary>
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
        => ReadStringsFolder(stringsDir, stem).MatchedFiles;

    /// <summary>What happened when houseCARL looked in a <c>Strings\</c> folder — THREE answers, for the same reason
    /// the plugin's own header read has three. "Enumerated it and found nothing" and "could not enumerate it" are
    /// different facts, and a classifier that collapses them makes an absence claim it never checked.</summary>
    public enum StringsFolderRead
    {
        /// <summary>No <c>Strings\</c> folder beside the plugin at all. A checked absence, and sayable as one.</summary>
        Absent,

        /// <summary>Listed it. Whatever is reported is what is in there.</summary>
        Listed,

        /// <summary>The folder is there and could not be listed. Nothing may be concluded about its contents.</summary>
        Unlistable,
    }

    /// <summary>One look inside a <c>Strings\</c> folder, answering everything this classifier asks of it: which
    /// languages matched the plugin, which table files did NOT, and whether the folder could be read at all.
    ///
    /// <para>ONE enumeration on purpose. The matched pass and the unmatched pass used to walk the same folder
    /// separately, so they could disagree with each other — one succeeding while the other threw would produce an
    /// assessment claiming a folder held nothing unmatched while the languages said otherwise. Every claim made about
    /// this folder now comes from the same listing.</para>
    ///
    /// <para>The unmatched files are reported by NAME and deliberately NOT attributed. Two plugins share a
    /// <c>Strings\</c> folder, and telling a neighbour's <c>ksws07_quest_shrubs_English.STRINGS</c> apart from this
    /// plugin's own <c>ZRef_ptbr.STRINGS</c> means guessing where the stem ends — the exact guess whose first spelling
    /// made one plugin's assessment read another's files. What is checkable without guessing is that the files are
    /// there and that none of them matched, and that is all the sentence claims.</para></summary>
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
        // .ToList() INSIDE the try: EnumerateFiles is lazy, so an access failure surfaces while the sequence is being
        // walked rather than at the call.
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
    /// plugin — measured at ~0.7 ms for a real 101 MB archive, which a 3800-plugin sweep would pay per plugin.
    ///
    /// <para>Keyed on each archive's OWN name, length and write time, not on the folder's write time. The folder's
    /// stamp was the first spelling and it is measurably insufficient: on NTFS, rewriting a file's contents in place
    /// leaves the parent directory's <c>LastWriteTimeUtc</c> byte-identical (measured 2026-08-26, identical to
    /// 100 ns), so <c>housecarl_bsa_repack</c> rewriting a <c>.bsa</c> inside one server process kept serving the
    /// pre-rewrite stem list. Add, delete and rename change the folder stamp; overwrite does not, and overwrite is the
    /// one this server does to itself.</para>
    ///
    /// <para><b>Unarmed, deliberately, and this note is the whole ground</b> (same shape as the
    /// <see cref="BsaEmbedding"/> ordering fix): observing a stale-vs-fresh stem list needs an archive that PARSES,
    /// and Mutagen exposes no in-process archive builder — packing shells out to <c>bsarch</c>, which no guard may
    /// depend on. A malformed fixture archive reads "unreadable" before and after any rewrite, so it cannot tell the
    /// two answers apart. The key is stated to be what it is; nothing here claims it was measured.</para></summary>
    static readonly Dictionary<string, (string Stamp, List<ArchiveEntry> Entries)> ArchiveCache =
        new(StringComparer.OrdinalIgnoreCase);

    readonly record struct ArchiveEntry(string Archive, HashSet<string> Stems, bool Unreadable);

    static readonly List<ArchiveEntry> NoArchives = new();

    /// <summary>The cache key for one folder's archives: every <c>.bsa</c>'s name, length and write time. A file whose
    /// contents were replaced in place changes its own length-or-stamp even where the folder's stamp does not move.</summary>
    static string ArchiveStamp(string[] archives)
    {
        var parts = new List<string>(archives.Length);
        foreach (var a in archives.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            // An archive we cannot stat is keyed as un-stattable rather than skipped: dropping it would make two
            // different folder states share a key, which is the failure this whole key exists to avoid.
            try
            {
                var fi = new FileInfo(a);
                parts.Add($"{fi.Name}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}");
            }
            catch (IOException) { parts.Add(Path.GetFileName(a) + "|?"); }
            catch (UnauthorizedAccessException) { parts.Add(Path.GetFileName(a) + "|?"); }
        }
        return string.Join(" ", parts);
    }

    static List<ArchiveEntry> ArchiveStrings(string folder)
    {
        string[] archives;
        try { archives = Directory.GetFiles(folder, "*.bsa"); }
        catch (IOException) { return NoArchives; }
        catch (UnauthorizedAccessException) { return NoArchives; }
        if (archives.Length == 0) return NoArchives;
        var stamp = ArchiveStamp(archives);

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

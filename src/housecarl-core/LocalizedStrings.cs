using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Strings;

namespace HousecarlCore;

/// <summary>What a plugin's own header said when houseCARL asked whether it is flagged LOCALIZED — three answers, not
/// two, because "could not read it" is not "not localized" and a decision that treats them the same fails OPEN.
///
/// <para>A bool cannot carry this: a fault would answer false, so a destination held by another process for the
/// instant of the check — an AV scan, MO2 refreshing, xEdit, the game — would classify as not-localized and let a
/// write through that reads every value back empty.</para></summary>
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
    /// <para>Its own shape because the folder needs the third answer the plugin header already has: "enumerated it and
    /// found nothing" and "could not enumerate it" are different facts. Collapsing them classifies a folder holding a
    /// complete loose set as <see cref="Nowhere"/>, whose sentence then tells the modder there are no .STRINGS files
    /// beside a plugin whose folder is full — an absence claim nothing checked.</para></summary>
    StringsFolderUnreadable,

    /// <summary>Flagged localized, and houseCARL can find no strings source for it — the residual case.
    ///
    /// <para>This says what houseCARL could FIND, not what exists: MO2's VFS merges mod folders at runtime, so a
    /// plugin in one mod folder can resolve its strings from a <c>.bsa</c> in ANOTHER, which no path houseCARL walks
    /// can see. Creation Club content routinely lands here, its plugin in a "Cleaned Masters" folder and its archive
    /// in the content's own. Anything user-facing must say houseCARL cannot find the strings, never that the plugin
    /// has none.</para></summary>
    Nowhere,
}

/// <summary>The table files in the <c>Strings\</c> folder beside a plugin that houseCARL did NOT match to it: the
/// NAMES a refusal may quote — capped, because one folder can hold a translation mod's whole set — and the TRUE
/// count those names were taken from.
///
/// <para><b>One type carrying both, so a sentence cannot render the cap as the count.</b> Counting a capped list
/// describes a folder holding thirty files as holding eight — an assertion about the modder's disk nothing
/// checked.</para></summary>
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
/// assumed harmless.</param>
/// <param name="GameDataUnknown">No game-Data folder was supplied, so whether a competing set lives there could not be
/// checked. This is NOT the same as having checked and found none, and the refusal sentences say which.</param>
/// <param name="UnmatchedTables">File names in the <c>Strings\</c> folder beside the plugin that carry a table
/// extension and that houseCARL did NOT match to this plugin in a language it models — a neighbouring plugin's tables
/// out of the shared folder, or this plugin's own tables named for a language token Mutagen does not model
/// (<c>ZRef_ptbr.STRINGS</c>). Carried so the <see cref="LocalizedShape.Nowhere"/> sentence can describe the folder
/// the modder is looking at instead of claiming nothing is in it. Names and true count travel together; see
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
/// <para>Mutagen loads and re-emits EVERY language present beside a plugin, so a complete loose set of any size
/// round-trips through a serialize, while a language present only partially has its missing kinds materialised
/// holding empty values. Detection is cheap: the language enumeration ~0.1 ms, and asking a 101 MB archive whether
/// it embeds strings for a given plugin under 1 ms.</para>
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
        // The game-Data side stays two-answer on purpose, and safely: nothing rendered off it asserts an absence. The
        // Nowhere sentence says houseCARL "cannot find" the text, AlsoLoose names a game-Data set only when one was
        // found, and GameDataUnknown carries the one absence claim there is (no Data folder to search).
        var gameData = dataDir is null
            ? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            : ReadStringsFolder(Path.Combine(dataDir, "Strings"), stem).Languages;

        // An archive the write cannot rewrite decides the shape regardless of what else is on disk — a loose set beside
        // a BSA that also embeds this plugin's strings is an ambiguous source, and which one Mutagen prefers is not
        // something this classifier should be guessing at when the answer is "refuse either way".
        // The folder-unlistable answer is not consumed here: a mod folder that will not list also hides its
        // Strings\ folder, so this classification falls through to the shapes that resolve NOWHERE and every lane
        // reading it refuses. Fail-CLOSED is the right default for a write; the read side's gate takes the third
        // answer, because its default is to leave the open unchanged.
        var (bsaPath, bsaUnreadable, _) = BsaEmbedding(folder, stem);
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
        // stopped here would silently call every DLC master "no strings anywhere". Searched LAST and only when
        // nothing nearer was found, so an ordinary plugin never pays for it.
        if (dataDir is not null)
        {
            var (gdBsa, gdUnreadable, _) = BsaEmbedding(dataDir, stem);
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

    /// <summary>Did houseCARL actually READ this plugin's header and find the LOCALIZED flag set?
    ///
    /// <para>The predicate any lane needs before its output SAYS something about localization — as opposed to before
    /// it REFUSES, which is the wider, fail-closed "anything but NotLocalized". A report note, a census, a label: all
    /// of them assert, and an unreadable plugin gives them nothing to assert. Written as an exhaustive switch rather
    /// than <c>!= NotLocalized</c> so a shape added later has to be answered here instead of being swept into
    /// whichever side the operator happens to fall on.</para></summary>
    public static bool ConfirmedLocalized(LocalizedShape shape) => shape switch
    {
        // The header was read and the flag is set. Where the text lives varies; that it is localized does not.
        LocalizedShape.LooseComplete or LocalizedShape.LoosePartial or LocalizedShape.LooseWithGameDataDuplicate
            or LocalizedShape.BsaEmbedded or LocalizedShape.GameDataOnly or LocalizedShape.StringsFolderUnreadable
            or LocalizedShape.Nowhere => true,

        // Read it, flag clear.
        LocalizedShape.NotLocalized => false,

        // Never read at all — the answer is unknown, and "unknown" is not "yes".
        LocalizedShape.Unreadable => false,

        _ => false,
    };

    /// <summary>The same decision as <see cref="RefusalFor"/>, rendered WITHOUT the "houseCARL did not write X" head —
    /// for a lane reporting on a plugin the caller did not ask about.</summary>
    public static string? RefusalReasonFor(string pluginPath, string pluginFileName, string? dataDir)
        => RefusalShapeFor(pluginPath, pluginFileName, dataDir)?.Why;

    /// <summary>The same decision as <see cref="RefusalReasonFor"/>, carrying the SHAPE it was made on.
    ///
    /// <para>For a lane that reports on a SET of blocked plugins. Its hits are not homogeneous — a referencer that is
    /// flagged localized and one houseCARL could not open both block a repoint, and both must — so a caller rendering
    /// them needs to know which is which. Without it the count and the label collapse onto the wider one and every
    /// hit reads as "is localized", including the file nobody managed to read.</para></summary>
    public static (LocalizedShape Shape, string Why)? RefusalShapeFor(string pluginPath, string pluginFileName, string? dataDir)
    {
        var a = Assess(pluginPath, dataDir);
        if (a.Shape == LocalizedShape.NotLocalized) return null;
        return (a.Shape, LocalizedTargetUnsupportedException.ShapeBody(a));
    }

    /// <summary>Does the plugin's OWN folder carry a strings source FOR THIS PLUGIN — a loose table beside it, or a
    /// <c>.bsa</c> there that embeds one? The read side's redirect gate (<see cref="LoadOrderResolver.OpenOverlay"/>):
    /// false means the folder-adjacent lookup would resolve nothing for this plugin, so the open points at game-Data
    /// instead.
    ///
    /// <para>Answers for the NAMED plugin, never for the folder in general. A <c>Strings\</c> folder holding only a
    /// NEIGHBOUR's tables, and an asset-only <c>.bsa</c> (meshes/textures — the common case), are not this plugin's
    /// source; counting either suppressed the redirect for a whole population of localized plugins whose text was
    /// reachable in game-Data and read back blank everywhere (#369).</para>
    ///
    /// <para>Cost: the loose look is one directory listing, and the archive look is the folder-keyed
    /// <see cref="ArchiveStrings"/> cache the classifier already builds — so a mod folder's archives are enumerated
    /// once per server process rather than once per open. Any IO fault answers TRUE, keeping the unchanged
    /// folder-adjacent open: we only ever redirect on a clean, empty read.</para></summary>
    public static bool OwnFolderCarriesStringsFor(string pluginPath)
    {
        try
        {
            var folder = Path.GetDirectoryName(pluginPath);
            // A folder that is not there is not a clean, empty read — it is a bad one, and the redirect is only ever
            // taken on a clean one.
            if (folder is null || !Directory.Exists(folder)) return true;
            var stem = Path.GetFileNameWithoutExtension(pluginPath);
            var loose = ReadStringsFolder(Path.Combine(folder, "Strings"), stem);
            // A folder that is there and could not be LISTED tells us nothing about what is in it — keep the default.
            if (loose.Read == StringsFolderRead.Unlistable) return true;
            if (loose.Languages.Count > 0) return true;
            // BsaEmbedding returns the archive's path both when it embeds this plugin's tables and when it could not
            // be parsed at all, so a non-null answer covers "found it" and "cannot see in there" alike — and the
            // second keeps the unchanged open rather than redirecting past an archive that may hold the strings.
            var bsa = BsaEmbedding(folder, stem);
            // The mod folder itself would not list — the same fact as the unlistable Strings\ folder above, and the
            // same answer: nothing is known about what is beside the plugin, so the default open stands.
            if (bsa.FolderUnlistable) return true;
            return bsa.Path is not null;
        }
        catch { return true; }
    }

    /// <summary>Could houseCARL find NO strings source at all for a plugin it read and found flagged localized? True
    /// for the two shapes where a read resolves nothing: nothing found anywhere, and a <c>Strings\</c> folder beside
    /// the plugin that could not be listed. Every value such a plugin carries reads EMPTY, so a lane that would bake
    /// that read into a new file refuses rather than writing blanks (#371).
    ///
    /// <para><see cref="LocalizedShape.Unreadable"/> is deliberately NOT here: the plugin's own header was never
    /// read, so nothing is known about its strings, and a lane that must act on that answers it as unreadable in its
    /// own words.</para></summary>
    public static bool ResolvesNowhere(LocalizedShape shape) => shape switch
    {
        LocalizedShape.Nowhere or LocalizedShape.StringsFolderUnreadable => true,
        _ => false,
    };

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
    /// <para>ONE enumeration on purpose: separate matched and unmatched passes over the same folder can disagree —
    /// one succeeding while the other throws yields an assessment claiming nothing is unmatched while the languages
    /// say otherwise. Every claim made about this folder comes from the same listing.</para>
    ///
    /// <para>The unmatched files are reported by NAME and deliberately NOT attributed. Two plugins share a
    /// <c>Strings\</c> folder, and telling a neighbour's <c>ksws07_quest_shrubs_English.STRINGS</c> apart from this
    /// plugin's own <c>ZRef_ptbr.STRINGS</c> means guessing where the stem ends. What is checkable without guessing
    /// is that the files are there and that none of them matched, and that is all the sentence claims.</para></summary>
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
    /// tables.</summary>
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
    static (string? Path, bool Unreadable, bool FolderUnlistable) BsaEmbedding(string folder, string stem)
    {
        var (folderUnlistable, entries) = ArchiveStrings(folder);
        // The folder would not list, so which archives are in it — and whether any carries this stem — was never
        // established. Reported as its own answer rather than as "no archive": there is no archive to name, and a
        // caller that must not conclude an absence needs to be told it cannot.
        if (folderUnlistable) return (null, false, true);

        // A POSITIVE hit wins over an unreadable one: returning on the first unreadable archive would let a single
        // unparseable .bsa anywhere in game-Data answer for every plugin that searched there, naming an archive that
        // neither sits beside the plugin nor carries its tables. An archive we cannot see into still refuses, but only
        // once no archive has actually claimed the stem.
        string? unreadable = null;
        foreach (var e in entries)
        {
            if (e.Unreadable) { unreadable ??= e.Archive; continue; }
            if (e.Stems.Contains(stem)) return (e.Archive, false, false);
        }
        return unreadable is null ? (null, false, false) : (unreadable, true, false);
    }

    /// <summary>Per archive in a folder: which plugin stems it embeds strings for. Cached by folder, because game-Data
    /// holds dozens of archives and a whole-order sweep would otherwise re-enumerate every one of them once per
    /// plugin — ~0.7 ms for a 101 MB archive, which a 3800-plugin sweep would otherwise pay per plugin.
    ///
    /// <para>Keyed on each archive's OWN name, length and write time, not on the folder's write time: on NTFS,
    /// rewriting a file's contents in place leaves the parent directory's <c>LastWriteTimeUtc</c> byte-identical, so a
    /// folder stamp would keep serving the pre-rewrite stem list after <c>housecarl_bsa_repack</c> rewrites a
    /// <c>.bsa</c> inside one server process. Add, delete and rename move the folder stamp; overwrite does not, and
    /// overwrite is the one this server does to itself.</para>
    ///
    /// <para>No test covers the stale-vs-fresh case: observing it needs an archive that PARSES, and Mutagen exposes no
    /// in-process archive builder (packing shells out to <c>bsarch</c>, which a test may not depend on). A malformed
    /// fixture archive reads "unreadable" before and after any rewrite, so it cannot tell the two apart.</para></summary>
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

    static (bool Unlistable, List<ArchiveEntry> Entries) ArchiveStrings(string folder)
    {
        string[] archives;
        // THE SAME THREE ANSWERS the loose look has, for the same reason: a folder that could not be listed is not a
        // folder found to hold no archives, and returning the empty list for both made the read side's gate conclude
        // an absence it never checked.
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

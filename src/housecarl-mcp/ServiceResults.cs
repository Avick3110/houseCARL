using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcp;

/// <summary>How a calling operation can produce a patch that does not exist yet — the operation's own statement, and
/// the only thing the shared <c>into=</c> resolver's not-found refusal may say about creating one. It is never
/// inferred there, because the write lane and the naming semantics do not coincide: a rider's <c>patch=</c> can name
/// a .bsa, and a copy's fresh stem is an EditorID.</summary>
internal enum FreshPatchRemedy
{
    /// <summary>The default and the safe one: this operation claims no fresh-write path, so the refusal offers none.
    /// A removal needs it, because it edits a patch that must already exist, and a weaker default would tell its
    /// callers to omit the lane, which a removal itself refuses.</summary>
    None = 0,

    /// <summary>Omitting <c>into=</c> creates one, under a stem this call site chooses rather than the default —
    /// the copy lanes, whose stem is a new EditorID, and every rider lane, whose stem is its own artifact.</summary>
    CreatedByOmittingInto,

    /// <summary><c>patch=</c> on this tool names a new patch and defaults to "Patch", so the refusal can hand back a
    /// working call with the caller's own guessed name already in it.</summary>
    NamedByPatchParam,
}

/// <summary>The outcome of a read_record resolve+read. <see cref="Error"/> non-null ⇒ the read failed (with a
/// recoverable, named reason); otherwise <see cref="Record"/> carries the fields read off <see cref="SourcePlugin"/>.</summary>
public sealed record ReadOutcome(
    FormKey FormKey,
    RecordFields? Record,
    string? SourcePlugin,
    string? WinnerPlugin,
    int OverrideDepth,
    IReadOnlyList<string>? TouchingPlugins,
    string? Error)
{
    /// <summary>The captured build this outcome was answered from, stamped at the capture boundary so refusals carry
    /// it too: a "not present" is an answer ABOUT a build. Null only where no view was ever consulted, such as a
    /// malformed-FormID parse failure.</summary>
    public OrderStamp? Stamp { get; init; }

    /// <summary>That build's fingerprint. Reads through the stamp, so an outcome cannot carry an epoch without the
    /// health of the build it names.</summary>
    public string? Epoch => Stamp?.Epoch;

    /// <summary>The RUNTIME FormID of this record in the build that answered — the eight-hex form the game, the
    /// console, Papyrus logs and crash logs print. Rendered beside the FormKey so a reader can carry the record
    /// either way. Null when the order gives the record no runtime address — the read came from a plugin outside the
    /// active order, or the tables could not be built (<see cref="LoadOrderResolver.IndexView.RuntimeFormIdOf"/> says
    /// when). Carried per outcome because the light index moves whenever the order does.</summary>
    public string? RuntimeFormId { get; init; }

    /// <summary>Why this record has no runtime FormID, when the order can address the plugin but not the record —
    /// today, a light-flagged plugin that was never compacted. Rendered where the form would have gone, so the
    /// answer is never a silently missing field.</summary>
    public string? RuntimeFormIdNote { get; init; }

    /// <summary>Carry a resolved runtime address onto this outcome — the one place the two halves are set, so a
    /// lane cannot keep one and drop the other.</summary>
    public ReadOutcome WithRuntime(RuntimeAddress a) => this with { RuntimeFormId = a.FormId, RuntimeFormIdNote = a.Note };

    /// <summary>The resolver and view this outcome was answered from, carried beside <see cref="Epoch"/> so the
    /// render's conflict-tree fill reads the same build the stamp names. Internal render plumbing.</summary>
    internal LoadOrderService.ViewPin? Pin { get; init; }

    /// <summary>Which fields in <see cref="Record"/> carry the owned-child annotation, each with the
    /// <see cref="ChildUnion"/> the order assembles there, so a response render can state the clause once over the
    /// fields it actually emitted and name them. Null when this read annotated nothing.
    /// <para>Carried structurally rather than recovered by scanning the rendered prose for a marker, and carrying the
    /// paths rather than a bool: a clause that merely knows something was annotated cannot tell whether that
    /// something survived the medium's own truncation.</para></summary>
    public IReadOnlyDictionary<string, ChildUnion?>? OwnedChildFields { get; init; }

    /// <summary>Did this read ASSEMBLE the union, or state the index-only note? The scan lanes annotate without
    /// opening the other bodies, and the response-level clause has to say which of the two it is stating.</summary>
    public bool OwnedChildUnioned => OwnedChildFields is { } m && m.Values.Any(v => v is not null);

    /// <summary>Did this read annotate anything at all — the cheap question, for callers that only need to know
    /// whether a clause is POSSIBLE (the budget reservation) rather than which fields it would name.</summary>
    public bool OwnedChildNoted => OwnedChildFields is { Count: > 0 };

    public static ReadOutcome Fail(FormKey fk, string error) => new(fk, null, null, null, 0, null, error);
}

/// <summary>The outcome of a cross-plugin scan. <see cref="Error"/> non-null ⇒ the query was rejected (with a
/// recoverable, named reason — bad filter combo / unknown type / plugin not in order). Otherwise <see cref="Keys"/>
/// are the matched FormKeys (at most `limit`); <see cref="Prefilled"/> (parallel to Keys) carries the in-hand
/// summaries for the type/plugins paths, or is null for the conflicts_only-alone path (the renderer fills those
/// lazily, bounded by max_chars). <see cref="Sources"/> (parallel to Keys) is the plugin whose body produced each
/// match — the scoped plugin under plugins=, or null under type=/conflicts_only (⇒ the renderer displays the
/// winner) — so the detail render shows the SAME body the scan filtered and display never contradicts filter.
/// <see cref="Total"/> is the true match count; <see cref="Capped"/> is true when Total exceeded what was returned.</summary>
public sealed record CrossQueryOutcome(
    IReadOnlyList<FormKey> Keys, IReadOnlyList<RecordSummary>? Prefilled, int Total, bool Capped, string? Error,
    string? PredicateNote = null, IReadOnlyList<string?>? Sources = null, string? ScanNote = null,
    IReadOnlyList<string?>? MatchedTargets = null, IReadOnlyList<GroupCount>? Groups = null,
    string? GroupBy = null, string? ScopeLabel = null, int Offset = 0,
    bool WhereWinner = false, string? WhereSourceNote = null)   // WhereWinner means the match decided on the live winner; WhereSourceNote carries the type=-scope redundancy note
{
    /// <summary>The captured build the scan ran over. The render stamps it into the in-band accounting so paged
    /// windows are checkably from the same build. Null on the pre-scan refusals.</summary>
    public OrderStamp? Stamp { get; init; }

    /// <summary>That build's fingerprint. Reads through the stamp, so an outcome cannot carry an epoch without the
    /// health of the build it names.</summary>
    public string? Epoch => Stamp?.Epoch;

    /// <summary>The reverse-reference index's accounting for this call: what a build it triggered cost, the index's
    /// own per-plugin freshness key, and the whole-order universe declaration. Null when the call did not use it.</summary>
    public string? ReverseIndexNote { get; init; }

    /// <summary>Plugins the winner scan could not open, by filename. A zero-match answer with one of these is bounded
    /// by the lock, not by the filter, and the render must not tell the caller otherwise.</summary>
    public IReadOnlyList<string> UnreadPlugins { get; init; } = Array.Empty<string>();

    /// <summary>The getter types the scan's own types= resolved to, or null when it named none. The render's bulk
    /// body gather passes them to <see cref="LoadOrderResolver.IndexView.CollectRecords"/>, which then seeks the
    /// GRUPs those types live in instead of walking the whole plugin. Never serialized.</summary>
    internal IReadOnlyList<Type>? GetterTypes { get; init; }

    /// <summary>The scan's pinned resolver and view, carried so the render's per-match fills — detail bodies,
    /// summaries, conflict trees — read off the same build the scan matched and <see cref="Epoch"/> names. Without
    /// it a freshness rebuild landing mid-render would make the response a single-build claim it does not satisfy.
    /// Pure data: an immutable snapshot reference holding no handles, and never serialized.</summary>
    internal LoadOrderService.ViewPin? Pin { get; init; }

    public static CrossQueryOutcome Fail(string error) => new(Array.Empty<FormKey>(), null, 0, false, error);
}

/// <summary>One row of a scan's <c>group_by=</c> aggregation: a group key (winner plugin / record type /
/// defining plugin) and how many matches fell in it. Emitted instead of per-match lines when group_by is set.</summary>
public sealed record GroupCount(string Key, int Count);

/// <summary>A compact, header-only record summary (no field dump) — the per-match line a scan emits
/// by default. <see cref="Error"/> non-null ⇒ the winner couldn't be summarised (named, recoverable).</summary>
public sealed record RecordSummary(FormKey FormKey, string Type, string? EditorId, string Winner, int OverrideDepth, string? Error)
{
    /// <summary>The runtime FormID of this row's record in the build that answered — the same identity the detail
    /// lanes print, so a scan row a modder takes to the console carries the form the console wants.</summary>
    public string? RuntimeFormId { get; init; }

    /// <summary>Why the row has no runtime FormID — see <see cref="ReadOutcome.RuntimeFormIdNote"/>.</summary>
    public string? RuntimeFormIdNote { get; init; }

    /// <summary>Carry a resolved runtime address onto this row; the one place the two halves are set.</summary>
    public RecordSummary WithRuntime(RuntimeAddress a) => this with { RuntimeFormId = a.FormId, RuntimeFormIdNote = a.Note };
}

/// <summary>The MATERIALISED conflict tree the render layer consumes — each touching plugin's name + the fields read
/// off its own body, in priority order (winner last). Built by <see cref="LoadOrderService.ResolveTreePinned"/> with the
/// per-call session already disposed, so it carries NO live overlay (Option B — the renderer never holds a handle).</summary>
public sealed record ConflictTreeView(IReadOnlyList<ConflictNodeView> Nodes,
                                      IReadOnlyList<ChildDeclarers> ChildDeclarers)
{
    public ConflictNodeView Winner => Nodes[^1];
}

/// <summary>The precise owned-child answer for one child-bearing field of one record:
/// which of the record's providers declare child records there, and which could not be read.
///
/// <para><see cref="Declaring"/> empty with <see cref="Unreadable"/> empty is the answer the cheap tier can never
/// give — nobody declares anything here — and it is rendered as a sentence, never as an omitted line.</para></summary>
public sealed record ChildDeclarers(string Field, OwnedChildShape Shape,
                                    IReadOnlyList<string> Declaring, IReadOnlyList<string> Unreadable);

/// <summary>One node of a <see cref="ConflictTreeView"/>: the plugin name + that plugin's record fields (already read).</summary>
public sealed record ConflictNodeView(string Plugin, RecordFields Record);

/// <summary>The data behind housecarl_load_order_status. <see cref="Composition"/> is the fresh enabled/disabled picture;
/// <see cref="ResolvedPluginCount"/> + <see cref="Warnings"/> are the resolver's actual last-build state;
/// <see cref="ProfileChanged"/> is true only when a refresh was attempted but is still pending (e.g. MO2 was mid-write) —
/// houseCARL re-reads automatically on the next tool call; no restart. <see cref="ExcludedPlugins"/> (name → reason) are
/// plugins dropped from the index this build (unopenable, or carrying a record Mutagen can't parse) — surfaced so the
/// user can fix/remove them.</summary>
public sealed record LoadOrderStatusData(
    Mo2Composition Composition,
    IReadOnlyList<string> Warnings,
    int ResolvedPluginCount,
    int MaxPlugins,
    bool ProfileChanged,
    string ProfileDir,
    string ProfileName,         // the ACTIVE profile (instance mode: MO2's selected_profile; explicit: the dir name) — captured under the gate, not re-derived at render
    string? InstanceDir,        // the resolved MO2 instance folder houseCARL is pointed at; null ⇒ explicit-paths / unconfigured mode
    IReadOnlyDictionary<string, string> ExcludedPlugins,
    string? Epoch = null,       // the resolver's current build fingerprint (SPEC §2.1.1) — the status line names it so a caller can match responses/artifacts to the build; nullable like every other carrier
    int ContainedRecordCount = 0);  // children this build recorded a containing record for — the '*parent' map's size, declared in band per SPEC §2.1 rather than left for a user to discover as memory

/// <summary>The data behind housecarl_update_status: MO2's own local Nexus update cache read from meta.ini, with no
/// network. <see cref="Entries"/> is one row per Nexus-linked mod (installed vs newest version, modid, enabled state);
/// <see cref="UntrackedCount"/> is how many mod folders were skipped as not Nexus-linked (no meta.ini or no modid).
/// <see cref="Problems"/> carries any read faults, such as a missing mods folder, never a silent empty.</summary>
public sealed record UpdateCacheData(
    string ModsDir,
    string? InstanceDir,
    IReadOnlyList<ModUpdateEntry> Entries,
    IReadOnlyList<string> Problems,
    int UntrackedCount);

/// <summary>One Nexus-linked mod's update-cache row. <see cref="Newest"/> empty ⇒ MO2 never learned a newer version.
/// MO2's own "update available" rule: <see cref="Newest"/> is set, non-empty, != <see cref="Installed"/>, and !=
/// <see cref="Ignored"/>. <see cref="Enabled"/> is null when the mod isn't in the active profile (state unknown).
/// <see cref="LastUpdate"/> is unix-seconds of MO2's last Nexus check (staleness signal). <see cref="InstalledFileIds"/>
/// are the exact Nexus file id(s) MO2 installed (from meta.ini <c>[installedFiles]</c>) — the FILE-level currency join
/// key that makes a live check immune to the multi-file-page false positive; empty for a FOMOD/manual install.</summary>
public sealed record ModUpdateEntry(
    string Folder, bool? Enabled, int ModId, string? Installed, string? Newest, string? Ignored, string? LastUpdate,
    IReadOnlyList<int> InstalledFileIds);

/// <summary>The result of <see cref="LoadOrderService.NamedProfileComposition"/> — the profiles affordance behind
/// housecarl_load_order_status' profile= param. <see cref="InstanceMode"/> is false in explicit-paths mode (no profiles
/// root — a named read refuses loud). <see cref="AvailableProfiles"/> lists the profile folders (instance mode; empty in
/// explicit mode), used both for the default-status discovery line and to name the options when a requested profile isn't
/// found. <see cref="RequestedName"/> echoes the trimmed name asked for (null if none). <see cref="Composition"/> +
/// <see cref="ResolvedProfileDir"/> are set ONLY when a requested profile was found and read; a non-null RequestedName with
/// a null Composition is the "not found" case (AvailableProfiles names the real options, never a silent empty).
/// <see cref="Warnings"/> carries any notes from reading the inspected profile (e.g. a missing modlist.txt — so a
/// 0-enabled-mods render is never mistaken for a genuinely-empty profile); empty unless a profile was found and read.</summary>
public sealed record NamedProfileResult(
    bool InstanceMode,
    IReadOnlyList<string> AvailableProfiles,
    string? RequestedName,
    string? ResolvedProfileDir,
    Mo2Composition? Composition,
    IReadOnlyList<string> Warnings);

/// <summary>One queried asset path's resolution behind housecarl_asset_status: the resolver's <see cref="AssetHit"/>
/// (which sources have it + which wins + an ambiguity flag), or an <see cref="Error"/> when the path was rejected (a
/// drive-rooted or '..'-escaping path — named per path, never failing the batch). <see cref="Hit"/> is null iff
/// <see cref="Error"/> is set.
/// <para><see cref="PrefixSuggestions"/> — on an ABSENT answer only, the root-prefixed forms of this path that
/// a real active mod or BSA DOES provide (<see cref="AssetPathHint"/>), for the common case of a path taken straight
/// off a record and therefore missing its <c>meshes\</c> / <c>textures\</c> root. Verified by re-resolution, so a
/// suggestion always names a file that exists; empty when there is nothing honest to offer.</para>
/// <para>The FaceGen members are set only on a row the <c>formids=</c> SELECT derived (see <see cref="FaceGenSeed"/>):
/// <see cref="FormId"/> is the NPC the path was computed from, <see cref="Slot"/> which half of the pair this row is,
/// and <see cref="PairPath"/> / <see cref="PairHit"/> the OTHER half resolved beside it — the dark-face question is
/// whether the two halves win from the same source, which one row per path cannot answer alone.</para></summary>
public sealed record AssetPathResult(string RelPath, AssetHit? Hit, string? Error,
                                     IReadOnlyList<string>? PrefixSuggestions = null,
                                     string? FormId = null, FaceGenSlot? Slot = null,
                                     string? PairPath = null, AssetHit? PairHit = null)
{
    /// <summary>Both halves resolved and they come from DIFFERENT MODS — the split the dark-face diagnosis is looking
    /// for. False when either half is absent (that is a different class, and the row already says so) and on any row
    /// with no pair.
    /// <para>Compared by the OWNING MOD, not by the provider name: the vanilla game ships every head in
    /// <c>Skyrim - Meshes0.bsa</c> and every tint in <c>Skyrim - Textures0.bsa</c>, two provider names for one
    /// product. Measured on the ARR order, comparing names called 2,719 NPCs split and comparing owners called
    /// 375 — the 2,344 difference is that one shape, and every one of them would have been a false report.</para></summary>
    public bool PairDiffers =>
        Hit is { Exists: true, Winner: { } a } && PairHit is { Exists: true, Winner: { } b }
        && !string.Equals(Owner(a), Owner(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Who ships this copy: the mod folder behind a BSA, or the provider's own name where there is none —
    /// a loose provider IS its layer, and a BSA the resolver could not trace to a folder has only its filename. The
    /// same fallback <see cref="FaceGenCheck"/>'s pair classifier uses, EMPTY STRING included: an owner spelled ""
    /// would otherwise compare equal to every other empty owner and hide a split.</summary>
    internal static string Owner(AssetProvider p) => p.OwningMod is { Length: > 0 } m ? m : p.Source;
}

/// <summary>One entry of <c>asset_status</c>'s <c>formids=</c> SELECT: the caller's raw token, the FormKey it parsed
/// to, and the sentence to report instead when it did not. A malformed token is ONE error row, never a failed call —
/// the same posture a malformed asset path takes on this tool.
/// <para>The FormKey is all the derivation needs: a FaceGen path is a pure transform of it
/// (<see cref="FaceGenPath"/>), so this lane reads no record and pays no per-id winner seek.</para></summary>
public sealed record FaceGenSeed(string Token, FormKey? Key, string? Error);

/// <summary>The data behind housecarl_asset_status: one <see cref="AssetPathResult"/> per queried path, plus the
/// build-level caveats — <see cref="BsaFailures"/> (archives that couldn't be read) and <see cref="ReadIncomplete"/>
/// (an Exists=false answer may be wrong because a BSA failed to read) — and <see cref="Warnings"/> from archive
/// discovery (e.g. a Skyrim.ini that couldn't be found, so base-game BSAs weren't scanned). <see cref="ProfileName"/>
/// names the active profile the answer describes.
/// <para><see cref="SelectorNotes"/> carries what each <c>under=</c> directory / glob selector had to say for itself
/// (a selector that matched nothing, or was rejected), <see cref="Total"/> is how many paths the whole selection named
/// before paging, <see cref="Offset"/> where the rendered window starts, and <see cref="Limit"/> the window size the
/// caller asked for (0 = none), which the next-page advice repeats so a caller following it keeps paging. A negative
/// <see cref="Total"/> means nothing paged — the results ARE the selection.</para>
/// <para><see cref="BoundRefusal"/> is the declared-cost refusal (<see cref="RenderBudget.RefuseAssetPaths"/>): the
/// selection was made, counted, and found past the per-call bound, so NOTHING was resolved and the sentence names
/// the count, the estimate and the lever. Set means the other members carry no answer.</para></summary>
public sealed record AssetStatusData(
    IReadOnlyList<AssetPathResult> Results,
    IReadOnlyList<string> BsaFailures,
    bool ReadIncomplete,
    IReadOnlyList<string> Warnings,
    string ProfileName,
    IReadOnlyList<string>? SelectorNotes = null,
    int Total = -1,
    int Offset = 0,
    int Limit = 0,
    string? BoundRefusal = null)
{
    /// <summary>How many paths the selection named — <see cref="Results"/>'s own count when nothing paged.</summary>
    public int Selected => Total < 0 ? Results.Count : Total;
}

/// <summary>One provider of an SKSE-layer file: the mod / "overwrite" / "Data" / BSA-filename, and whether it's a "loose" file or
/// a "BSA" entry. The winner-first-then-losers ordering lives in <see cref="SkseFileEntry.Providers"/>.</summary>
public sealed record SkseProvider(string Name, string Kind);

/// <summary>One file found under Data\SKSE\Plugins in the active load order (housecarl_skse findings='inventory'). <see cref="Group"/> is the
/// immediate subfolder it sits in ("" = top level) — the derived render-grouping key. <see cref="Providers"/> is the FULL conflict
/// chain — every mod that ships this exact file, WINNER FIRST then the losers in precedence order (the same winner→loser
/// transparency the asset tools give), each tagged loose/BSA; empty ⇒ nothing active provides it. <see cref="Plugin"/> is the tier-C
/// static manifest, set ONLY for a <c>.dll</c> whose winning copy is loose (null for configs and for a BSA-only/unresolved DLL);
/// <see cref="Note"/> carries the reason when a DLL has no readable manifest or isn't loader-scoped.</summary>
public sealed record SkseFileEntry(
    string RelPath,
    string FileName,
    string Group,
    IReadOnlyList<SkseProvider> Providers,
    SksePluginReader.SksePluginInfo? Plugin,
    string? Note,
    SksePeekResult? Peek = null,
    string? ModVersion = null)
{
    /// <summary>The version the winning mod's MO2 meta.ini records, or null when the provider has no meta.ini (Stock
    /// Game, overwrite, a hand-installed mod). The THIRD number for a DLL — what the modder installed — next to the
    /// SKSE manifest's declaration and the DLL's own file version, which routinely disagree with it.</summary>
    public string? ModVersion { get; init; } = ModVersion;

    /// <summary>The string peek of this DLL's image (<c>peek=true</c>), or null when not requested / not a loose
    /// DLL. Computed ONLY for entries the peek filter matched — the scan reads the whole image, so it is opt-in per-DLL
    /// by design. The import half needs no flag and lives on <see cref="SksePluginReader.SksePluginInfo.Imports"/>.</summary>
    public SksePeekResult? Peek { get; init; } = Peek;

    /// <summary>Whether this DLL entry matches a user <c>filter=</c> — the one predicate, shared by the renderer's
    /// filtered view and the service's peek gate. Shared on purpose: two hand-kept copies would drift, and a drift
    /// here means peeking a different DLL than the one rendered. Matches filename, winning provider, subfolder, or
    /// the declared plugin name and author, case-insensitively.</summary>
    public bool MatchesDll(string filter)
    {
        bool In(string? s) => s is not null && s.Contains(filter, StringComparison.OrdinalIgnoreCase);
        return In(FileName) || In(WinningProvider) || In(Group)
            || (Plugin?.Version is { } v && (In(v.Name) || In(v.Author)));
    }

    /// <summary>The VFS winner (first provider), or null if nothing active provides the file.</summary>
    public SkseProvider? Winner => Providers.Count > 0 ? Providers[0] : null;
    /// <summary>The winning provider's name (mod / overwrite / Data / BSA), or null.</summary>
    public string? WinningProvider => Winner?.Name;
    /// <summary>The winner's kind ("loose" | "BSA"), or "none" when unprovided.</summary>
    public string ProviderKind => Winner?.Kind ?? "none";
    /// <summary>How many mods ship this exact file — &gt; 1 is contention worth surfacing.</summary>
    public int ProviderCount => Providers.Count;
}

/// <summary>The data behind housecarl_skse findings='inventory': the SKSE-plugin layer of the active load order — <see cref="Dlls"/> (each a
/// plugin DLL with its winning provider + static manifest) and <see cref="Configs"/> (their .ini/.toml/.json/.yaml with the
/// winning provider), plus <see cref="OtherFileCount"/> (uncategorized files like .pdb/.txt, counted not listed). The build-level
/// caveats <see cref="BsaFailures"/> / <see cref="ReadIncomplete"/> and discovery <see cref="Warnings"/> ride along; <see cref="ProfileName"/>
/// names the active profile the answer describes.</summary>
public sealed record SkseInventoryData(
    IReadOnlyList<SkseFileEntry> Dlls,
    IReadOnlyList<SkseFileEntry> Configs,
    int OtherFileCount,
    string? InstalledRuntime,
    IReadOnlyList<string> BsaFailures,
    bool ReadIncomplete,
    IReadOnlyList<string> Warnings,
    string ProfileName,
    IReadOnlySet<string>? ActivePlugins = null,
    bool PeekRequested = false)
{
    /// <summary>The plugin filenames the game actually loads (active + force-loaded implicit) — resolved ONLY for a
    /// peek, which cross-checks a DLL's embedded plugin names against it. <c>null</c> ⇒ NOT RESOLVED (the
    /// profile's plugin lists were missing or unreadable), so a renderer must NOT call any embedded name "absent from
    /// the load order" (an unasked question has no answer). Never handed over EMPTY — see the producer.</summary>
    public IReadOnlySet<string>? ActivePlugins { get; init; } = ActivePlugins;

    /// <summary>Whether the caller asked for a peek. Distinct from "any entry HAS a peek": a filter can match
    /// only configs, or only BSA-only DLLs, and then the flag was honored with nothing to show — which the renderer
    /// must SAY rather than silently drop.</summary>
    public bool PeekRequested { get; init; } = PeekRequested;
}

/// <summary>The load-order verdict for one reference an SKSE config declares (housecarl_skse findings='config').</summary>
public enum SkseRefVerdict
{
    /// <summary>Plugin in the active order, and (for a form token) the FormID resolves to a record in it.</summary>
    Ok,
    /// <summary>The named plugin is not in the active load order — the whole entry (or, for a path-segment gate, the whole file) is inert.</summary>
    PluginMissing,
    /// <summary>Plugin present, but no record with that (masked) FormID exists in it — a dead reference.</summary>
    Dangling,
    /// <summary>The token matched the reference SHAPE but couldn't be normalized (hex overflow, unusable plugin name) — flagged loud, never guessed.</summary>
    Unparseable,
}

/// <summary>One reference a config declares (<see cref="HousecarlCore.SkseConfigRef"/>) paired with its load-order
/// <see cref="Verdict"/> and a <see cref="Detail"/> line: the resolved FormKey for OK, the reason for a dead or unparseable verdict.</summary>
public sealed record SkseAuditedRef(HousecarlCore.SkseConfigRef Ref, SkseRefVerdict Verdict, string? Detail);

/// <summary>One config file's audit: its VFS provenance (winning provider + the full winner-first conflict chain — only the
/// WINNER is read, the losers are shown for transparency), every reference it declares with a verdict, and a named
/// <see cref="ReadError"/> when the winning copy couldn't be read/decoded or was over the size cap.</summary>
public sealed record SkseConfigFileAudit(
    string RelPath,
    string FileName,
    string Group,
    string? WinningProvider,
    int ProviderCount,
    IReadOnlyList<SkseProvider> Providers,
    IReadOnlyList<SkseAuditedRef> Refs,
    string? ReadError);

/// <summary>The data behind housecarl_skse findings='config': every SKSE-plugin config with the references it
/// declares resolved to OK / PLUGIN MISSING / DANGLING / UNPARSEABLE, plus the build-level caveats
/// (<see cref="BsaFailures"/> / <see cref="ReadIncomplete"/> / <see cref="Warnings"/>) and the active <see cref="ProfileName"/>.</summary>
public sealed record SkseConfigAuditData(
    IReadOnlyList<SkseConfigFileAudit> Files,
    int ConfigCount,
    IReadOnlyList<string> BsaFailures,
    bool ReadIncomplete,
    IReadOnlyList<string> Warnings,
    string ProfileName);

/// <summary>Who implements a native class's declarations (housecarl_skse findings='pairing').</summary>
public enum NativeProvenance
{
    /// <summary>The class's provider chain includes an OFFICIAL archive — implemented by the game executable. Baseline;
    /// accounting only (this holds even when a mod's loose copy WINS the file — SKSE overrides vanilla classes).</summary>
    Engine,
    /// <summary>An skse64-scripts-payload class (StringUtil, UI, …) — implemented by the game-root skse64 loader, not
    /// anything under SKSE\Plugins. Detected structurally: an otherwise-unpaired class whose winning provider also
    /// provides an ENGINE class (the payload co-ships vanilla overrides with its new classes). Baseline.</summary>
    SkseCore,
    /// <summary>Anything else — the pairing ladder runs.</summary>
    ThirdParty,
}

/// <summary>The pairing-evidence rung a THIRD-PARTY class landed on, by evidence strength.</summary>
public enum NativePairingRung
{
    /// <summary>The winning .pex's own provider mod ships ≥1 candidate DLL — the strong co-shipment signal.</summary>
    SameMod,
    /// <summary>A mod elsewhere in the .pex's conflict chain ships the DLL — the bundling case (a patch mod wins the
    /// script file; the framework mod beneath ships the implementation).</summary>
    ChainMod,
    /// <summary>No mod shipping this class's file (winner or chain) ships any candidate DLL. A VERIFY flag, never
    /// "broken" — a declaration copy of an absent framework lands here, but registration is runtime behavior.</summary>
    Unpaired,
}

/// <summary>One candidate DLL a paired mod ships: its VFS identity, the winning copy's manifest (loose winners
/// only), and <see cref="LoadBlocker"/> — the static reason it will NOT load (BSA-only / subfolder / 32-bit /
/// unreadable), null when no static check rules it out. version-LOCKED-vs-runtime is adjudicated at render time
/// against <see cref="NativePairingAuditData.InstalledRuntime"/> (it needs the game version, which may be unknown).</summary>
public sealed record NativePairedDll(
    string RelPath,
    string FileName,
    string Group,
    string? WinningProvider,
    SksePluginReader.SksePluginInfo? Info,
    string? LoadBlocker);

/// <summary>One script class declaring native functions, with its VFS provenance, its <see cref="Provenance"/> class,
/// and — for a third-party class — the pairing <see cref="Rung"/>, the paired mod, and that mod's candidate DLLs.
/// <see cref="Rung"/>/<see cref="PairedMod"/> are null for baseline (ENGINE / SKSE CORE) classes. The winner/count
/// facts are derived from the one <see cref="Providers"/> list (hand-kept
/// copies of a derivable fact drift). Deadness has exactly one owner — the renderer's Judge/BestFate, which also
/// adjudicates version-locked-vs-runtime — deliberately not a record property.</summary>
public sealed record NativeClassEntry(
    string RelPath,
    string ClassName,
    IReadOnlyList<string> NativeFunctions,
    IReadOnlyList<SkseProvider> Providers,
    NativeProvenance Provenance,
    NativePairingRung? Rung,
    string? PairedMod,
    IReadOnlyList<NativePairedDll> PairedDlls)
{
    /// <summary>How many native functions the class declares — always <see cref="NativeFunctions"/>' count.</summary>
    public int NativeCount => NativeFunctions.Count;
    /// <summary>The VFS winner's provider name (first in <see cref="Providers"/>), or null if nothing provides it.</summary>
    public string? WinningProvider => Providers.Count > 0 ? Providers[0].Name : null;
    /// <summary>The winner's kind ("loose" | "BSA"), or "none" when unprovided.</summary>
    public string ProviderKind => Providers.Count > 0 ? Providers[0].Kind : "none";
    /// <summary>How many sources ship this exact file — &gt; 1 is contention worth surfacing.</summary>
    public int ProviderCount => Providers.Count;
}

/// <summary>A .pex whose winning copy could not be parsed — a NAMED note, never a silent skip.</summary>
public sealed record NativeUnreadablePex(string RelPath, string? WinningProvider, string Reason);

/// <summary>The data behind housecarl_skse findings='pairing': every native-declaring class classified and (for third
/// parties) paired, the scan accounting (<see cref="PexScanned"/> total compiled scripts examined), the unreadable
/// notes, whether an skse64 loader is visible (<see cref="SkseLoaderSeen"/> — the SKSE-CORE sanity note; tri-state:
/// null = the check itself failed, "could not check", never rendered as a definite absence), the installed game
/// runtime when resolvable (<see cref="InstalledRuntime"/>, null = unknown → version-LOCKED findings degrade to
/// "verify"), and the build-level caveats.</summary>
public sealed record NativePairingAuditData(
    IReadOnlyList<NativeClassEntry> Classes,
    int PexScanned,
    IReadOnlyList<NativeUnreadablePex> Unreadable,
    bool? SkseLoaderSeen,
    string? InstalledRuntime,
    IReadOnlyList<string> BsaFailures,
    bool ReadIncomplete,
    IReadOnlyList<string> Warnings,
    string ProfileName);

/// <summary>The data behind the whole-layer SkyPatcher scan: the ordered discovery scan, the
/// per-folder INI-vs-INI set collisions, the three ITM classes (intra-file dead writes, cross-INI
/// duplicates, no-op writes), and the build-level caveats.</summary>
public sealed record SkyPatcherLayerData(
    HousecarlCore.SkyPatcherDiscovery.LayerScan Scan,
    IReadOnlyList<HousecarlCore.SkyPatcherConflicts.SkyPatcherConflict> Conflicts,
    IReadOnlyList<HousecarlCore.SkyPatcherConflicts.SkyPatcherItm> Itms,
    IReadOnlyList<HousecarlCore.SkyPatcherConflicts.SkyPatcherDuplicate> Duplicates,
    IReadOnlyList<SkyPatcherNoOpWrite> NoOps,
    IReadOnlyList<string> NoOpNotes,
    bool ReadIncomplete,
    IReadOnlyList<string> AssetWarnings,
    string ProfileName);

/// <summary>One no-op write (the third ITM class — true ITM): a SET-class op that applied to the
/// record in the full replay but wrote the value the record already had at that point.
/// <see cref="Already"/> is that value (the overlay's before == after leaf token).</summary>
public sealed record SkyPatcherNoOpWrite(
    string Subfolder, string FormKey, string? EditorId, string FieldPath,
    string File, int Line, string Op, string Value, string Already);

/// <summary>One SkyPatcher type folder's replay outcome for the record. <see cref="Result"/> is null when the
/// active order ships no (interpretable) INIs for the folder — a named nothing, not an empty guess.
/// <see cref="Enabled"/> false ⇒ SkyPatcher.ini toggles the whole folder off (its INIs exist but the DLL
/// skips them — counts are zero BY that fact, and the render must say so).</summary>
public sealed record SkyPatcherFolderOutcome(
    string Subfolder,
    int IniCount,
    int LineCount,
    HousecarlCore.SkyPatcherOverlay.SkyPatcherOverlayResult? Result,
    bool Enabled);

/// <summary>One provider of a mesh path: the mod / "overwrite" / "Data" / BSA-filename, and whether it's a "loose" file
/// or a "BSA" entry. Winner-first ordering lives in <see cref="NifInspectData.Providers"/>.</summary>
public sealed record NifProvider(string Name, string Kind, bool OffOrder = false, bool OwnerEnabled = false)
{
    /// <summary>The provenance line when these bytes came out of a copy the game is NOT loading — a mod folder MO2
    /// does not tick, or an enabled mod's root archive no active plugin binds. Null for an in-order provider. Reading
    /// such a copy is legitimate (naming the mod is the consent), but a response that did not SAY so would read as
    /// "this is what the game shows", which is the one thing it is not.</summary>
    public string? Provenance => OffOrder ? WriteSentences.PlaceSourceOffOrder(Name, OwnerEnabled) : null;

    /// <summary>The spelling every listing prints — the name inside a delimiter a Windows name cannot contain, with
    /// the kind outside it, through the one formatter the asset surface uses. The printed token is the token
    /// <c>source_provider=</c> accepts, so a caller can copy it back verbatim (#340).</summary>
    public string Text => HousecarlCore.AssetSourceSelection.Describe(Name, Kind);
}

/// <summary>The per-path data behind housecarl_nif_inspect: the VFS resolution of ONE mesh path joined to the
/// format-level <see cref="HousecarlCore.NifInspect"/> of the copy that was read. <see cref="Inspected"/> is the
/// provider whose bytes were parsed (the winner, or the <c>source_provider=</c>-named copy); <see cref="Providers"/> is the FULL
/// winner→loser chain (asset-tool parity), <see cref="Ambiguous"/> flags file-layer contention. <see cref="Absent"/>
/// marks the no-provider outcome specifically, so the renderer can hedge THAT error at point of use on the
/// batch-level scan caveats (an ABSENT is only authoritative when the scan was complete — asset_status parity).
/// Exactly one of <see cref="Inspect"/> (the mesh model) and <see cref="Error"/> (ABSENT / bad path / unreadable /
/// parse-refused — all named) is set on any given result. The batch-level caveats (BSA failures, discovery
/// warnings, profile) live on <see cref="NifInspectBatchData"/> — captured once for the whole batch.</summary>
public sealed record NifInspectData(
    string RelPath,
    NifProvider? Inspected,
    IReadOnlyList<NifProvider> Providers,
    bool Ambiguous,
    bool Absent,
    HousecarlCore.NifInspect? Inspect,
    string? Error)
{
    public static NifInspectData Fail(string relPath, string error)
        => new(relPath, null, Array.Empty<NifProvider>(), false, false, null, error);
}

/// <summary>The batch behind housecarl_nif_inspect: per-path <see cref="Results"/> in INPUT ORDER, plus the
/// build-level caveats shared by the whole batch (one asset capture pins every path): <see cref="BsaFailures"/>
/// (archives that couldn't be read this build — an ABSENT result may be incomplete), discovery
/// <see cref="Warnings"/>, and the active <see cref="ProfileName"/>.</summary>
public sealed record NifInspectBatchData(
    IReadOnlyList<NifInspectData> Results,
    IReadOnlyList<string> BsaFailures,
    IReadOnlyList<string> Warnings,
    string ProfileName);

/// <summary>The data behind housecarl_nif_set: the VFS resolution joined to the verified write outcome. Exactly one of
/// {<see cref="Report"/> (a verified write happened)}, {<see cref="Error"/> (a named refusal — NOTHING written)},
/// and {<see cref="NeedsAcknowledge"/> (the in-place first-touch consent prompt — a required confirmation, not an
/// error)} describes the result. <see cref="OutputModFolder"/> is set on the default-lane success (enable it; on the
/// into= lane also sort it above <see cref="CurrentWinner"/>); <see cref="InPlacePath"/> is set on the in-place success
/// (the file overwritten in place).</summary>
public sealed record NifSetResult(
    string RelPath,
    NifProvider? Edited,
    IReadOnlyList<NifProvider> Providers,
    bool Ambiguous,
    HousecarlCore.NifSetReport? Report,
    string? Error,
    bool NeedsAcknowledge,
    string? AckPrompt,
    bool InPlace,
    bool EditedIsWinner,
    string? OutputModFolder,
    string? InPlacePath,
    string? CurrentWinner,
    IReadOnlyList<string> Warnings,
    string ProfileName)
{
    public static NifSetResult Fail(string error, IReadOnlyList<NifProvider>? providers = null, string profileName = "")
        => new("", null, providers ?? Array.Empty<NifProvider>(), false, null, error, false, null, false, false, null, null, null, Array.Empty<string>(), profileName);

    public static NifSetResult NeedsAck(string prompt, NifProvider edited, IReadOnlyList<NifProvider> providers, string profileName)
        => new("", edited, providers, false, null, null, true, prompt, true, false, null, null, null, Array.Empty<string>(), profileName);

    /// <summary>True when the edited mesh landed in a mod folder this call CREATED, false when into= added it to an
    /// existing one. MO2 registers an unseen folder at the highest priority, so a fresh folder out-ranks the current
    /// winner on enable while an into= folder has to be sorted above it.</summary>
    public bool FreshFolder { get; init; }

    /// <summary>Whether <see cref="CurrentWinner"/> is MO2's overwrite folder — the TOP loose root, above every mod
    /// folder, so neither enabling nor sorting takes the mesh off it and the only remedy is to move that copy.</summary>
    public bool WinnerIsOverwrite { get; init; }

    /// <summary>Whether <see cref="CurrentWinner"/> IS the folder the edited mesh was written into — an into= re-edit
    /// of the same mesh. The edit already wins; sorting that folder above itself is not an instruction.</summary>
    public bool WinnerIsDestination { get; init; }

    /// <summary>Whether <see cref="CurrentWinner"/> is a BSA or the game's Data folder. Either loses to an enabled
    /// mod's loose copy at any priority, so the edit wins on enable with no sort — on both lanes.</summary>
    public bool WinnerLosesOnEnable { get; init; }

    public static NifSetResult OkNewFolder(string rel, NifProvider edited, IReadOnlyList<NifProvider> providers, bool ambiguous,
        HousecarlCore.NifSetReport report, string modFolder, bool freshFolder, string? winner, IReadOnlyList<string> warnings, string profileName)
        => new(rel, edited, providers, ambiguous, report, null, false, null, false, true, modFolder, null, winner, warnings, profileName)
            { FreshFolder = freshFolder };

    public static NifSetResult OkInPlace(string rel, NifProvider edited, IReadOnlyList<NifProvider> providers, bool ambiguous, bool editedIsWinner,
        HousecarlCore.NifSetReport report, string inPlacePath, IReadOnlyList<string> warnings, string profileName)
        => new(rel, edited, providers, ambiguous, report, null, false, null, true, editedIsWinner, null, inPlacePath, null, warnings, profileName);
}

/// <summary>One asset to PLACE (housecarl_place). <see cref="AssetPath"/> is the resolved Data-relative
/// DESTINATION (the tool computes it from a FormID+slot for FaceGen, or takes a raw path). <see cref="Source"/> is the
/// copy to place — a Data-relative path resolved through the VFS, a fully-qualified loose file path,
/// "&lt;archive.bsa&gt;|&lt;entry&gt;", or a fully-qualified ".bsa" path (entry := AssetPath); null/blank ⇒ the VFS lane
/// pointed at the destination path. <see cref="SourceProvider"/> picks the pole for a VFS source: a provider NAME on
/// its own, or the sigiled winner token (<see cref="AssetSourceChoice.WinnerToken"/> — a bare name always means a
/// provider of that name, so the two spaces cannot collide); null/blank ⇒ the sole provider, with contention refused
/// per-asset. A Source naming a DIFFERENT path from AssetPath is a RENAME — the mechanism behind carrying one
/// NPC's baked facegen onto another's FormID path; the same path is not, and renders without the rename prefix.</summary>
public sealed record PlaceRequest(string AssetPath, string? Source, string? SourceProvider = null)
{
    /// <summary>This request is one half of a FaceGen pair expanded from a formid with no kind — two destinations
    /// sharing one member's source. A refusal about that source must not recommend a form the pair cannot take.</summary>
    public bool BothSlots { get; init; }
}

/// <summary>One placed asset's outcome. <see cref="Placed"/> false ⇒ <see cref="Error"/> names why (recoverable, per-asset
/// per asset). <see cref="CurrentWinner"/> is the source that currently wins the VFS for this path (the placed copy does
/// NOT win until the mod is enabled; on the into= lane it must also be sorted above this winner, while a fresh folder
/// out-ranks it on enable), or null if nothing provided it before.</summary>
public sealed record PlaceResult(string AssetPath, bool Placed, long Bytes, string? SourceDesc, string? CurrentWinner, string? Error)
{
    /// <summary>The mod folder these bytes were read out of when it is NOT one the active profile includes — the
    /// off-order source lane. Null for every read served by the active order. Non-null is a fact the response must
    /// state: the bytes are the ones the caller named, out of a mod the game is not currently loading.</summary>
    public string? SourceOffOrderProvider { get; init; }

    /// <summary>Whether that off-order mod is one MO2 TICKS — which of the two off-order reasons applies. True means
    /// the bytes came out of a root archive no active plugin binds, not out of an unticked mod, and the response has
    /// to say the one that is true.</summary>
    public bool SourceOffOrderOwnerEnabled { get; init; }

    /// <summary>Whether <see cref="CurrentWinner"/> is MO2's overwrite folder. It is the TOP loose root, above every
    /// mod folder, so neither enabling a fresh folder nor any left-pane sort takes the path off it — the only remedy
    /// is to move or delete the overwrite copy, and the render has to say that instead.</summary>
    public bool WinnerIsOverwrite { get; init; }

    /// <summary>Whether <see cref="CurrentWinner"/> IS the folder these bytes were placed into — a re-place into an
    /// enabled houseCARL patch. The placement already wins; telling the caller to sort the folder above itself is an
    /// instruction nobody can follow.</summary>
    public bool WinnerIsDestination { get; init; }

    /// <summary>Whether <see cref="CurrentWinner"/> is a BSA or the game's Data folder — the bottom of the root list.
    /// A BSA wins only when no loose copy exists and Data only when no mod provides the path, so the placed loose copy
    /// beats either the moment the mod is enabled, at any priority. No sort is owed, on either lane.</summary>
    public bool WinnerLosesOnEnable { get; init; }

    public static PlaceResult Fail(string assetPath, string error, string? currentWinner = null)
        => new(assetPath, false, 0, null, currentWinner, error);
}

/// <summary>The outcome of housecarl_place. <see cref="Error"/> non-null ⇒ the whole call was rejected
/// before any placement (unconfigured, an into= folder houseCARL doesn't own, the asset layer wouldn't build). Else
/// <see cref="Results"/> is per-asset; <see cref="ModFolder"/> is the houseCARL mod the placed files landed in (null when
/// none placed); <see cref="Warnings"/> carries the asset-discovery caveats; <see cref="LeftoverFolder"/> names a
/// fresh folder kept because it holds a partial result (no orphan is left for an all-failed fresh batch);
/// <see cref="FreshFolder"/> says which LANE ran.</summary>
public sealed record PlaceOutcome(
    IReadOnlyList<PlaceResult> Results, string? ModFolder, IReadOnlyList<string> Warnings, string? LeftoverFolder, string? Error)
{
    /// <summary>True when the files landed in a mod folder this call CREATED (the default lane), false when they were
    /// added to an existing folder (into=). MO2 registers a folder it has not seen at the highest priority, so a fresh
    /// folder out-ranks the current winner the moment it is ticked; an into= folder's priority is already fixed and has
    /// to be sorted. The two lanes therefore owe the caller different instructions.</summary>
    public bool FreshFolder { get; init; }

    /// <summary>Whether the CALL was served at all — not whether every destination placed. A served call with
    /// failed rows is a success carrying per-row errors, the way every other write outcome reads.</summary>
    public bool Success => Error is null;

    public static PlaceOutcome Fail(string error)
        => new(Array.Empty<PlaceResult>(), null, Array.Empty<string>(), null, error);
}

/// <summary>The outcome of housecarl_write_seq. <see cref="Error"/> non-null ⇒ the call was rejected (no plugin, unreadable
/// plugin, an into= folder houseCARL doesn't own, a failed write). On success: <see cref="Quests"/> is every SGE quest
/// covered (EMPTY ⇒ the plugin had none, so <see cref="SeqPath"/> is null and nothing was written — a clean no-op, not a
/// failure); <see cref="SeqPath"/> is the written <c>.seq</c> and <see cref="ModFolder"/> the houseCARL mod it landed in;
/// <see cref="WroteIntoPluginFolder"/> is true when it defaulted into the plugin's OWN folder (so one mod enables both).
/// A leftover path (a fresh folder kept because the write half-landed) is folded into <see cref="Error"/>.</summary>
public sealed record SeqOutcome(
    bool Success, string? Error, string? SeqPath, string? ModFolder,
    IReadOnlyList<HousecarlCore.SeqFile.SeqQuest> Quests, string PluginFileName, bool WroteIntoPluginFolder)
{
    /// <summary>Where the source plugin resolved from: "direct path", or the located hit's own label (its mod folder and state). A .seq is derived from
    /// ONE file's records, so which copy was read is load-bearing — a disabled folder's older copy yields a
    /// different quest set than the served one, silently, unless the arm is stated. Null on a refusal taken before
    /// the source resolved.</summary>
    public string? ResolvedFrom { get; init; }

    /// <summary>The absolute path the source resolved TO — the second half of the arm statement (the label says
    /// which layer, this says which file).</summary>
    public string? PluginPath { get; init; }

    /// <summary>The destination already held EXACTLY these bytes, so NOTHING was written (<see cref="SeqPath"/>
    /// names the file that was already correct). A success, and a DISTINCT one: "written" and "already current" are
    /// different facts about the disk, and collapsing them would make a skipped write indistinguishable from a done
    /// one. False on every path that actually wrote.</summary>
    public bool Unchanged { get; init; }

    /// <summary>The byte-identical destination was OLDER than the plugin, so its timestamp was stamped forward
    /// without rewriting a byte. The dialogue check judges .seq staleness by mtime, so a skipped write would
    /// otherwise leave that lint permanently calling a byte-perfect file stale — two tools contradicting each other
    /// about one file. False when no stamp was needed (the file was already newer) or nothing was skipped.</summary>
    public bool TimestampRefreshed { get; init; }

    /// <summary>The write REPLACED a file that was already there, rather than creating one. On the
    /// <c>out_path=</c> lane that file can be the mod's OWN shipped <c>.seq</c>, and houseCARL keeps no backup, so
    /// "wrote" and "replaced yours" are different facts about the disk and are reported as such.</summary>
    public bool Replaced { get; init; }

    /// <summary>The replaced file held EXACTLY the bytes just written, so nothing was lost. Only reachable when
    /// the byte-identical short-circuit was taken and its timestamp refresh then FAILED, sending an unchanged file
    /// down the write path: without this the response cries "OVERWRITTEN, no backup is kept"
    /// about a file it re-wrote identically.</summary>
    public bool ReplacedSameBytes { get; init; }

    /// <summary>The caller named <c>out_path=</c>, so the .seq landed in a folder the USER owns and no
    /// houseCARL mod folder was cut. Drives the confirmation: "enable this houseCARL mod in MO2" is the wrong next
    /// step for a file written into the user's own mod.</summary>
    public bool UserChoseOutput { get; init; }

    /// <summary>The note for an <c>out_path=</c> that neither MO2 nor the game reads SEQ files from. The
    /// .seq is correct; the engine will never see it, and every start-game-enabled quest in the plugin stays silently
    /// dead until it moves. Null when the destination deploys (and on every non-out_path lane, which lands in a
    /// houseCARL mod folder by construction).</summary>
    public string? DeployWarning { get; init; }

    public static SeqOutcome Fail(string error)
        => new(false, error, null, null, Array.Empty<HousecarlCore.SeqFile.SeqQuest>(), "", false);
}

/// <summary>The decompiler's class hierarchy and what is missing from it. <see cref="BaselineNote"/> is non-null when
/// the shipped vanilla baseline could not be read, <see cref="TopUpMissing"/> when the MO2 mods-tree top-up did not
/// run, naming the reason. Both are soft: a missing edge costs an explicit cast in the output, never wrong source.
/// They can be missing together, so the caller says what the hierarchy IS in one sentence rather than one note
/// each.</summary>
public sealed record ClassParents(
    Dictionary<string, string> Edges, string? BaselineNote, string? TopUpMissing);

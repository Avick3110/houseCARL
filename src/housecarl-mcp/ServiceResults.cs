using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcp;

/// <summary>How a calling operation can produce a patch that does not exist yet — the operation's own statement, and the only thing the shared <c>into=</c> resolver's not-found refusal may say about creating one.</summary>
internal enum FreshPatchRemedy
{
    /// <summary>The default: this operation claims no fresh-write path, so the refusal offers none.</summary>
    None = 0,

    /// <summary>Omitting <c>into=</c> creates one, under a stem this call site chooses rather than the default.</summary>
    CreatedByOmittingInto,

    /// <summary><c>patch=</c> on this tool names a new patch, so the refusal can hand back a working call.</summary>
    NamedByPatchParam,
}

/// <summary>The outcome of a read_record resolve+read. <see cref="Error"/> non-null ⇒ the read failed; otherwise <see cref="Record"/> carries the fields read off <see cref="SourcePlugin"/>.</summary>
public sealed record ReadOutcome(
    FormKey FormKey,
    RecordFields? Record,
    string? SourcePlugin,
    string? WinnerPlugin,
    int OverrideDepth,
    IReadOnlyList<string>? TouchingPlugins,
    string? Error)
{
    /// <summary>The captured build this outcome was answered from, stamped at the capture boundary so refusals carry it too; null only where no view was ever consulted.</summary>
    public OrderStamp? Stamp { get; init; }

    /// <summary>That build's fingerprint, read through the stamp so an outcome cannot carry an epoch without the build's health.</summary>
    public string? Epoch => Stamp?.Epoch;

    /// <summary>The RUNTIME FormID of this record in the build that answered, or null when the order gives it no runtime address (<see cref="LoadOrderResolver.IndexView.RuntimeFormIdOf"/> says when).</summary>
    public string? RuntimeFormId { get; init; }

    /// <summary>Why this record has no runtime FormID, when the order can address the plugin but not the record.</summary>
    public string? RuntimeFormIdNote { get; init; }

    /// <summary>Carry a resolved runtime address onto this outcome — the one place the two halves are set.</summary>
    public ReadOutcome WithRuntime(RuntimeAddress a) => this with { RuntimeFormId = a.FormId, RuntimeFormIdNote = a.Note };

    /// <summary>The resolver and view this outcome was answered from, so the render's conflict-tree fill reads the build the stamp names. Internal render plumbing.</summary>
    internal LoadOrderService.ViewPin? Pin { get; init; }

    /// <summary>Which fields in <see cref="Record"/> carry the owned-child annotation, each with the <see cref="ChildUnion"/> assembled there, so a render can state the clause over the fields it actually emitted.</summary>
    public IReadOnlyDictionary<string, ChildUnion?>? OwnedChildFields { get; init; }

    /// <summary>Did this read ASSEMBLE the union, or state the index-only note? The response-level clause has to say which.</summary>
    public bool OwnedChildUnioned => OwnedChildFields is { } m && m.Values.Any(v => v is not null);

    /// <summary>Did this read annotate anything at all — the cheap question, for the budget reservation.</summary>
    public bool OwnedChildNoted => OwnedChildFields is { Count: > 0 };

    public static ReadOutcome Fail(FormKey fk, string error) => new(fk, null, null, null, 0, null, error);
}

/// <summary>The outcome of a cross-plugin scan: the matched <see cref="Keys"/> with their parallel <see cref="Prefilled"/> summaries and <see cref="Sources"/> bodies, the true <see cref="Total"/>, or a named <see cref="Error"/>.</summary>
public sealed record CrossQueryOutcome(
    IReadOnlyList<FormKey> Keys, IReadOnlyList<RecordSummary>? Prefilled, int Total, bool Capped, string? Error,
    string? PredicateNote = null, IReadOnlyList<string?>? Sources = null, string? ScanNote = null,
    IReadOnlyList<string?>? MatchedTargets = null, IReadOnlyList<GroupCount>? Groups = null,
    string? GroupBy = null, string? ScopeLabel = null, int Offset = 0,
    bool WhereWinner = false, string? WhereSourceNote = null)   // WhereWinner means the match decided on the live winner; WhereSourceNote carries the type=-scope redundancy note
{
    /// <summary>The captured build the scan ran over, stamped into the in-band accounting so paged windows are checkably from one build. Null on the pre-scan refusals.</summary>
    public OrderStamp? Stamp { get; init; }

    /// <summary>That build's fingerprint, read through the stamp so an outcome cannot carry an epoch without the build's health.</summary>
    public string? Epoch => Stamp?.Epoch;

    /// <summary>The reverse-reference index's accounting for this call: a triggered build's cost, its per-plugin freshness key, and the universe declaration. Null when unused.</summary>
    public string? ReverseIndexNote { get; init; }

    /// <summary>Plugins the winner scan could not open: a zero-match answer carrying one is bounded by the lock, not by the filter.</summary>
    public IReadOnlyList<string> UnreadPlugins { get; init; } = Array.Empty<string>();

    /// <summary>The getter types the scan's types= resolved to, so the bulk body gather can seek their GRUPs instead of walking the plugin. Never serialized.</summary>
    internal IReadOnlyList<Type>? GetterTypes { get; init; }

    /// <summary>The scan's pinned resolver and view, so the render's per-match fills read off the same build the scan matched. Never serialized.</summary>
    internal LoadOrderService.ViewPin? Pin { get; init; }

    public static CrossQueryOutcome Fail(string error) => new(Array.Empty<FormKey>(), null, 0, false, error);
}

/// <summary>One row of a scan's <c>group_by=</c> aggregation: a group key and how many matches fell in it.</summary>
public sealed record GroupCount(string Key, int Count);

/// <summary>A compact, header-only record summary — the per-match line a scan emits by default. <see cref="Error"/> non-null ⇒ the winner could not be summarised.</summary>
public sealed record RecordSummary(FormKey FormKey, string Type, string? EditorId, string Winner, int OverrideDepth, string? Error)
{
    /// <summary>The runtime FormID of this row's record in the build that answered — the same identity the detail lanes print.</summary>
    public string? RuntimeFormId { get; init; }

    /// <summary>Why the row has no runtime FormID — see <see cref="ReadOutcome.RuntimeFormIdNote"/>.</summary>
    public string? RuntimeFormIdNote { get; init; }

    /// <summary>Carry a resolved runtime address onto this row; the one place the two halves are set.</summary>
    public RecordSummary WithRuntime(RuntimeAddress a) => this with { RuntimeFormId = a.FormId, RuntimeFormIdNote = a.Note };
}

/// <summary>The MATERIALISED conflict tree the render layer consumes — each touching plugin's name and the fields read off its own body, winner last; it carries no live overlay, so the renderer never holds a handle.</summary>
public sealed record ConflictTreeView(IReadOnlyList<ConflictNodeView> Nodes,
                                      IReadOnlyList<ChildDeclarers> ChildDeclarers)
{
    public ConflictNodeView Winner => Nodes[^1];
}

/// <summary>The precise owned-child answer for one child-bearing field: which of the record's providers declare child records there, and which could not be read. Both lists empty means nobody declares anything, rendered as a sentence.</summary>
public sealed record ChildDeclarers(string Field, OwnedChildShape Shape,
                                    IReadOnlyList<string> Declaring, IReadOnlyList<string> Unreadable);

/// <summary>One node of a <see cref="ConflictTreeView"/>: the plugin name + that plugin's record fields (already read).</summary>
public sealed record ConflictNodeView(string Plugin, RecordFields Record);

/// <summary>The data behind housecarl_load_order_status: the fresh <see cref="Composition"/>, the resolver's last-build state, a still-pending refresh (<see cref="ProfileChanged"/>), and the plugins this build dropped with their reasons.</summary>
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

/// <summary>The data behind housecarl_update_status: MO2's own local Nexus update cache read from meta.ini with no network — one <see cref="Entries"/> row per linked mod, the skipped <see cref="UntrackedCount"/>, and any read <see cref="Problems"/>.</summary>
public sealed record UpdateCacheData(
    string ModsDir,
    string? InstanceDir,
    IReadOnlyList<ModUpdateEntry> Entries,
    IReadOnlyList<string> Problems,
    int UntrackedCount);

/// <summary>One Nexus-linked mod's update-cache row. MO2's own "update available" rule is <see cref="Newest"/> set, non-empty and unequal to both <see cref="Installed"/> and <see cref="Ignored"/>; <see cref="InstalledFileIds"/> is the FILE-level join key a live check needs, empty for a FOMOD or manual install.</summary>
public sealed record ModUpdateEntry(
    string Folder, bool? Enabled, int ModId, string? Installed, string? Newest, string? Ignored, string? LastUpdate,
    IReadOnlyList<int> InstalledFileIds);

/// <summary>The result of <see cref="LoadOrderService.NamedProfileComposition"/> — the affordance behind housecarl_load_order_status' profile= param. <see cref="Composition"/> and <see cref="ResolvedProfileDir"/> are set only when a requested profile was found and read, so a non-null <see cref="RequestedName"/> with a null Composition is the not-found case.</summary>
public sealed record NamedProfileResult(
    bool InstanceMode,
    IReadOnlyList<string> AvailableProfiles,
    string? RequestedName,
    string? ResolvedProfileDir,
    Mo2Composition? Composition,
    IReadOnlyList<string> Warnings);

/// <summary>The outcome of housecarl_write_seq: a non-null <see cref="Error"/> means the call was rejected, else the <see cref="Quests"/> covered — empty is a clean no-op with no file written — the written <see cref="SeqPath"/> and its <see cref="ModFolder"/>, and whether it defaulted into the plugin's OWN folder.</summary>
public sealed record SeqOutcome(
    bool Success, string? Error, string? SeqPath, string? ModFolder,
    IReadOnlyList<HousecarlCore.SeqFile.SeqQuest> Quests, string PluginFileName, bool WroteIntoPluginFolder)
{
    /// <summary>Where the source plugin resolved from: "direct path", or the located hit's own label; which copy was read decides which quest set the .seq covers. Null on a refusal taken before the source resolved.</summary>
    public string? ResolvedFrom { get; init; }

    /// <summary>The absolute path the source resolved TO — the second half of the arm statement, where the label says which layer.</summary>
    public string? PluginPath { get; init; }

    /// <summary>The destination already held EXACTLY these bytes, so NOTHING was written; a distinct success, since "written" and "already current" are different facts about the disk.</summary>
    public bool Unchanged { get; init; }

    /// <summary>The byte-identical destination was OLDER than the plugin, so its timestamp was stamped forward without rewriting a byte, which is what keeps the dialogue check's mtime-based staleness test from calling it stale forever.</summary>
    public bool TimestampRefreshed { get; init; }

    /// <summary>The write REPLACED a file that was already there; on the <c>out_path=</c> lane that can be the mod's OWN shipped <c>.seq</c> and houseCARL keeps no backup, so it is reported as its own fact.</summary>
    public bool Replaced { get; init; }

    /// <summary>The replaced file held EXACTLY the bytes just written, so nothing was lost — only reachable when the byte-identical short-circuit's timestamp refresh FAILED and sent an unchanged file down the write path.</summary>
    public bool ReplacedSameBytes { get; init; }

    /// <summary>The caller named <c>out_path=</c>, so the .seq landed in a folder the USER owns and no houseCARL mod folder was cut, which is the wrong lane for an "enable this houseCARL mod" confirmation.</summary>
    public bool UserChoseOutput { get; init; }

    /// <summary>The note for an <c>out_path=</c> neither MO2 nor the game reads SEQ files from: the .seq is correct, the engine will never see it, and the plugin's start-game-enabled quests stay silently dead until it moves.</summary>
    public string? DeployWarning { get; init; }

    public static SeqOutcome Fail(string error)
        => new(false, error, null, null, Array.Empty<HousecarlCore.SeqFile.SeqQuest>(), "", false);
}

/// <summary>The decompiler's class hierarchy and what is missing from it, one named reason per source and any of them can be set; all are soft, since a missing edge costs an explicit cast in the output, never wrong source.</summary>
/// <param name="SiblingPexMissing">Why the .pex files beside the input were not all read; the service cannot know it, so the decompile lane sets it after its own sibling walk.</param>
public sealed record ClassParents(
    Dictionary<string, string> Edges, string? BaselineNote, string? TopUpMissing, string? SiblingPexMissing = null);

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

/// <summary>One queried asset path's resolution behind housecarl_asset_status: the resolver's <see cref="AssetHit"/> or a per-path <see cref="Error"/> that never fails the batch, the re-resolution-verified <see cref="PrefixSuggestions"/> an ABSENT answer carries, and the FaceGen members a <c>formids=</c> SELECT row adds for the other half of the pair.</summary>
public sealed record AssetPathResult(string RelPath, AssetHit? Hit, string? Error,
                                     IReadOnlyList<string>? PrefixSuggestions = null,
                                     string? FormId = null, FaceGenSlot? Slot = null,
                                     string? PairPath = null, AssetHit? PairHit = null)
{
    /// <summary>Both halves resolved and they come from DIFFERENT MODS — the dark-face split. Compared by the OWNING MOD, never the provider name, since one product ships heads and tints in two differently-named archives.</summary>
    public bool PairDiffers =>
        Hit is { Exists: true, Winner: { } a } && PairHit is { Exists: true, Winner: { } b }
        && !string.Equals(Owner(a), Owner(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Who ships this copy: the mod folder behind a BSA, else the provider's own name — the same fallback <see cref="FaceGenCheck"/>'s pair classifier uses, empty string included.</summary>
    internal static string Owner(AssetProvider p) => p.OwningMod is { Length: > 0 } m ? m : p.Source;
}

/// <summary>One entry of <c>asset_status</c>'s <c>formids=</c> SELECT: the caller's raw token, the FormKey it parsed to, or the sentence to report instead. A malformed token is ONE error row, never a failed call.</summary>
public sealed record FaceGenSeed(string Token, FormKey? Key, string? Error);

/// <summary>The data behind housecarl_asset_status: one <see cref="AssetPathResult"/> per queried path, the build-level caveats that make an Exists=false answer provisional, what each <c>under=</c> selector had to say, the paging window (a negative <see cref="Total"/> means nothing paged), and <see cref="BoundRefusal"/>, the declared-cost refusal under which nothing was resolved and no other member carries an answer.</summary>
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
    string? BoundRefusal = null,
    IReadOnlyList<string>? RootFailures = null)
{
    /// <summary>How many paths the selection named — <see cref="Results"/>'s own count when nothing paged.</summary>
    public int Selected => Total < 0 ? Results.Count : Total;

    /// <summary>The loose roots a walk could not enumerate this build, each named with the reason; empty when every root walked.</summary>
    public IReadOnlyList<string> UnwalkedRoots => RootFailures ?? Array.Empty<string>();
}

/// <summary>One provider of an SKSE-layer file: the mod, "overwrite", "Data" or BSA filename, and whether it is a "loose" file or a "BSA" entry.</summary>
public sealed record SkseProvider(string Name, string Kind);

/// <summary>One file under Data\SKSE\Plugins in the active order (housecarl_skse findings='inventory'): its render-grouping subfolder, the FULL winner-first provider chain, the static <see cref="Plugin"/> manifest for a loose-winning DLL, and a <see cref="Note"/> when there is none.</summary>
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
    /// <summary>The version the winning mod's MO2 meta.ini records — what the modder installed — or null when the provider has no meta.ini.</summary>
    public string? ModVersion { get; init; } = ModVersion;

    /// <summary>The string peek of this DLL's image (<c>peek=true</c>), or null when not requested or not a loose DLL; computed only for entries the peek filter matched, since the scan reads the whole image.</summary>
    public SksePeekResult? Peek { get; init; } = Peek;

    /// <summary>Whether this DLL entry matches a user <c>filter=</c> — the one predicate shared by the renderer's filtered view and the service's peek gate, over filename, winning provider, subfolder and declared name and author.</summary>
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

/// <summary>The data behind housecarl_skse findings='inventory': the active order's SKSE-plugin layer as <see cref="Dlls"/> and <see cref="Configs"/>, the uncategorized <see cref="OtherFileCount"/>, and the build-level caveats.</summary>
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
    /// <summary>The plugin filenames the game actually loads, resolved ONLY for a peek; null means NOT RESOLVED, so a renderer must not call any embedded name absent from the load order. Never handed over empty.</summary>
    public IReadOnlySet<string>? ActivePlugins { get; init; } = ActivePlugins;

    /// <summary>Whether the caller asked for a peek — distinct from any entry HAVING one, which the renderer must say rather than silently drop.</summary>
    public bool PeekRequested { get; init; } = PeekRequested;
}

/// <summary>The load-order verdict for one reference an SKSE config declares (housecarl_skse findings='config').</summary>
public enum SkseRefVerdict
{
    /// <summary>Plugin in the active order, and (for a form token) the FormID resolves to a record in it.</summary>
    Ok,
    /// <summary>The named plugin is not in the active load order, so the whole entry — or, for a path-segment gate, the whole file — is inert.</summary>
    PluginMissing,
    /// <summary>Plugin present, but no record with that (masked) FormID exists in it — a dead reference.</summary>
    Dangling,
    /// <summary>The token matched the reference SHAPE but could not be normalized — flagged loud, never guessed.</summary>
    Unparseable,
}

/// <summary>One reference a config declares, paired with its load-order <see cref="Verdict"/> and a <see cref="Detail"/> line: the resolved FormKey for OK, the reason otherwise.</summary>
public sealed record SkseAuditedRef(HousecarlCore.SkseConfigRef Ref, SkseRefVerdict Verdict, string? Detail);

/// <summary>One config file's audit: its VFS provenance as the full winner-first chain, of which only the WINNER is read, every reference it declares with a verdict, and a named <see cref="ReadError"/> when the winning copy could not be read.</summary>
public sealed record SkseConfigFileAudit(
    string RelPath,
    string FileName,
    string Group,
    string? WinningProvider,
    int ProviderCount,
    IReadOnlyList<SkseProvider> Providers,
    IReadOnlyList<SkseAuditedRef> Refs,
    string? ReadError);

/// <summary>The data behind housecarl_skse findings='config': every SKSE-plugin config with each reference it declares resolved to a verdict, plus the build-level caveats.</summary>
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
    /// <summary>The class's provider chain includes an OFFICIAL archive — implemented by the game executable. Baseline, accounting only.</summary>
    Engine,
    /// <summary>An skse64-scripts-payload class, implemented by the game-root loader rather than anything under SKSE\Plugins; detected structurally as an unpaired class whose winning provider also provides an ENGINE class. Baseline.</summary>
    SkseCore,
    /// <summary>Anything else — the pairing ladder runs.</summary>
    ThirdParty,
}

/// <summary>The pairing-evidence rung a THIRD-PARTY class landed on, by evidence strength.</summary>
public enum NativePairingRung
{
    /// <summary>The winning .pex's own provider mod ships ≥1 candidate DLL — the strong co-shipment signal.</summary>
    SameMod,
    /// <summary>A mod elsewhere in the .pex's conflict chain ships the DLL — the bundling case, where a patch mod wins the script file and the framework beneath ships the implementation.</summary>
    ChainMod,
    /// <summary>No mod shipping this class's file ships any candidate DLL. A VERIFY flag, never "broken", since registration is runtime behavior.</summary>
    Unpaired,
}

/// <summary>One candidate DLL a paired mod ships: its VFS identity, the loose winning copy's manifest, and <see cref="LoadBlocker"/>; static-load rule in docs/architecture/skse-layer.md.</summary>
public sealed record NativePairedDll(
    string RelPath,
    string FileName,
    string Group,
    string? WinningProvider,
    SksePluginReader.SksePluginInfo? Info,
    string? LoadBlocker);

/// <summary>One script class declaring native functions, with its VFS provenance, its <see cref="Provenance"/> class, and — for a third party — the pairing <see cref="Rung"/>, paired mod and candidate DLLs, which are null for baseline classes. Deadness has exactly one owner, the renderer's Judge/BestFate, deliberately not a record property.</summary>
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

/// <summary>The data behind housecarl_skse findings='pairing': every native-declaring class classified and, for third parties, paired, the scan accounting and unreadable notes, plus two tri-states — <see cref="SkseLoaderSeen"/> and <see cref="InstalledRuntime"/> — whose null means "could not check", never a definite absence.</summary>
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

/// <summary>The data behind the whole-layer SkyPatcher scan: the ordered discovery scan, the per-folder INI-vs-INI collisions, the three ITM classes, and the build-level caveats.</summary>
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

/// <summary>One no-op write, the true-ITM class: a SET-class op that applied in the full replay but wrote the value <see cref="Already"/> there.</summary>
public sealed record SkyPatcherNoOpWrite(
    string Subfolder, string FormKey, string? EditorId, string FieldPath,
    string File, int Line, string Op, string Value, string Already);

/// <summary>One SkyPatcher type folder's replay outcome for the record. <see cref="Result"/> is null when the active order ships no interpretable INIs for the folder, and <see cref="Enabled"/> false means SkyPatcher.ini toggles the whole folder off, so its zero counts are zero BY that fact.</summary>
public sealed record SkyPatcherFolderOutcome(
    string Subfolder,
    int IniCount,
    int LineCount,
    HousecarlCore.SkyPatcherOverlay.SkyPatcherOverlayResult? Result,
    bool Enabled);

/// <summary>One provider of a mesh path: the mod, "overwrite", "Data" or BSA filename, and whether it is a "loose" file or a "BSA" entry.</summary>
public sealed record NifProvider(string Name, string Kind, bool OffOrder = false, bool OwnerEnabled = false)
{
    /// <summary>The provenance line when these bytes came out of a copy the game is NOT loading, null for an in-order provider; a response that did not say so would read as what the game shows.</summary>
    public string? Provenance => OffOrder ? WriteSentences.PlaceSourceOffOrder(Name, OwnerEnabled) : null;

    /// <summary>The spelling every listing prints, through the one formatter the asset surface uses; the printed token is the token <c>source_provider=</c> accepts, so a caller can copy it back verbatim (#340).</summary>
    public string Text => HousecarlCore.AssetSourceSelection.Describe(Name, Kind);
}

/// <summary>The per-path data behind housecarl_nif_inspect: one mesh path's VFS resolution — the <see cref="Inspected"/> provider whose bytes were parsed, the full winner-to-loser chain, contention and <see cref="Absent"/> flags — joined to exactly one of <see cref="Inspect"/> and a named <see cref="Error"/>. The batch-level caveats live on <see cref="NifInspectBatchData"/>.</summary>
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

/// <summary>The batch behind housecarl_nif_inspect: per-path <see cref="Results"/> in INPUT ORDER, plus the build-level caveats one asset capture pins for the whole batch, which is what makes an ABSENT result provisional.</summary>
public sealed record NifInspectBatchData(
    IReadOnlyList<NifInspectData> Results,
    IReadOnlyList<string> BsaFailures,
    IReadOnlyList<string> Warnings,
    string ProfileName);

/// <summary>The data behind housecarl_nif_set: the VFS resolution joined to the verified write outcome, described by exactly one of <see cref="Report"/>, a named <see cref="Error"/> with nothing written, and <see cref="NeedsAcknowledge"/>, the in-place first-touch consent prompt.</summary>
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

    /// <summary>True when the edited mesh landed in a mod folder this call CREATED, which MO2 registers at the highest priority, so it out-ranks the current winner on enable while an into= folder has to be sorted above it.</summary>
    public bool FreshFolder { get; init; }

    /// <summary>Whether <see cref="CurrentWinner"/> is MO2's overwrite folder — the TOP loose root, so neither enabling nor sorting takes the mesh off it and the only remedy is to move that copy.</summary>
    public bool WinnerIsOverwrite { get; init; }

    /// <summary>Whether <see cref="CurrentWinner"/> IS the folder the edited mesh was written into; the edit already wins, and sorting that folder above itself is not an instruction.</summary>
    public bool WinnerIsDestination { get; init; }

    /// <summary>Whether <see cref="CurrentWinner"/> is a BSA or the game's Data folder, either of which loses to an enabled mod's loose copy at any priority, so no sort is owed.</summary>
    public bool WinnerLosesOnEnable { get; init; }

    public static NifSetResult OkNewFolder(string rel, NifProvider edited, IReadOnlyList<NifProvider> providers, bool ambiguous,
        HousecarlCore.NifSetReport report, string modFolder, bool freshFolder, string? winner, IReadOnlyList<string> warnings, string profileName)
        => new(rel, edited, providers, ambiguous, report, null, false, null, false, true, modFolder, null, winner, warnings, profileName)
            { FreshFolder = freshFolder };

    public static NifSetResult OkInPlace(string rel, NifProvider edited, IReadOnlyList<NifProvider> providers, bool ambiguous, bool editedIsWinner,
        HousecarlCore.NifSetReport report, string inPlacePath, IReadOnlyList<string> warnings, string profileName)
        => new(rel, edited, providers, ambiguous, report, null, false, null, true, editedIsWinner, null, inPlacePath, null, warnings, profileName);
}

/// <summary>One asset to PLACE (housecarl_place): the resolved Data-relative <see cref="AssetPath"/> destination, the <see cref="Source"/> copy to place as a VFS path, a loose file path or a BSA entry, and the <see cref="SourceProvider"/> pole for a VFS source, either a provider name or the sigiled winner token. A Source naming a DIFFERENT path is a RENAME.</summary>
public sealed record PlaceRequest(string AssetPath, string? Source, string? SourceProvider = null)
{
    /// <summary>This request is one half of a FaceGen pair expanded from a formid with no kind, so a refusal about the shared source must not recommend a form the pair cannot take.</summary>
    public bool BothSlots { get; init; }
}

/// <summary>One placed asset's outcome; <see cref="Placed"/> false means <see cref="Error"/> names why, per asset. <see cref="CurrentWinner"/> is the source that currently wins the VFS for this path, or null if nothing provided it before.</summary>
public sealed record PlaceResult(string AssetPath, bool Placed, long Bytes, string? SourceDesc, string? CurrentWinner, string? Error)
{
    /// <summary>The mod folder these bytes were read out of when it is NOT one the active profile includes; non-null is a fact the response must state.</summary>
    public string? SourceOffOrderProvider { get; init; }

    /// <summary>Whether that off-order mod is one MO2 TICKS — true means the bytes came out of a root archive no active plugin binds, not out of an unticked mod.</summary>
    public bool SourceOffOrderOwnerEnabled { get; init; }

    /// <summary>Whether <see cref="CurrentWinner"/> is MO2's overwrite folder — the TOP loose root, so the only remedy is to move or delete that copy, which the render has to say instead.</summary>
    public bool WinnerIsOverwrite { get; init; }

    /// <summary>Whether <see cref="CurrentWinner"/> IS the folder these bytes were placed into; the placement already wins, and sorting a folder above itself is an instruction nobody can follow.</summary>
    public bool WinnerIsDestination { get; init; }

    /// <summary>Whether <see cref="CurrentWinner"/> is a BSA or the game's Data folder, the bottom of the root list, so the placed loose copy beats either on enable at any priority and no sort is owed.</summary>
    public bool WinnerLosesOnEnable { get; init; }

    public static PlaceResult Fail(string assetPath, string error, string? currentWinner = null)
        => new(assetPath, false, 0, null, currentWinner, error);
}

/// <summary>The outcome of housecarl_place: a non-null <see cref="Error"/> means the whole call was rejected before any placement, else per-asset <see cref="Results"/>, the <see cref="ModFolder"/> they landed in, the discovery <see cref="Warnings"/>, a <see cref="LeftoverFolder"/> kept because it holds a partial result, and which lane ran.</summary>
public sealed record PlaceOutcome(
    IReadOnlyList<PlaceResult> Results, string? ModFolder, IReadOnlyList<string> Warnings, string? LeftoverFolder, string? Error)
{
    /// <summary>True when the files landed in a mod folder this call CREATED, which MO2 registers at the highest priority; an into= folder's priority is already fixed and has to be sorted, so the two lanes owe different instructions.</summary>
    public bool FreshFolder { get; init; }

    /// <summary>Whether the CALL was served at all, not whether every destination placed: a served call with failed rows is a success carrying per-row errors.</summary>
    public bool Success => Error is null;

    public static PlaceOutcome Fail(string error)
        => new(Array.Empty<PlaceResult>(), null, Array.Empty<string>(), null, error);
}

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

/// <summary>The decompiler's class hierarchy and what is missing from it, as two named reasons that can both be set; both are soft, since a missing edge costs an explicit cast in the output, never wrong source.</summary>
public sealed record ClassParents(
    Dictionary<string, string> Edges, string? BaselineNote, string? TopUpMissing);

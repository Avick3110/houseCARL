using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

// The result records the asset lanes return: asset_status, skse, SkyPatcher, NIF and place.

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
    /// <summary>The loose roots this build could not walk or list, each named with the reason; empty when every root read.</summary>
    IReadOnlyList<string> RootFailures,
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
    /// <summary>The loose roots this build could not walk or list, each named with the reason; empty when every root read.</summary>
    IReadOnlyList<string> RootFailures,
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
    /// <summary>The loose roots this build could not walk or list, each named with the reason; empty when every root read.</summary>
    IReadOnlyList<string> RootFailures,
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
    /// <summary>The loose roots this build could not walk or list, each named with the reason; empty when every root read.</summary>
    IReadOnlyList<string> RootFailures,
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
    /// <summary>The loose roots this build could not walk or list, each named with the reason; empty when every root read.</summary>
    IReadOnlyList<string> RootFailures,
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
    /// <summary>The loose roots this build could not walk or list, each named with the reason; empty when every root read.</summary>
    IReadOnlyList<string> RootFailures,
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

    /// <summary>The loose roots the call's asset build could not walk or list, each named with the reason, so a refusal's
    /// "may merely be unscanned" hedge has a folder to point at; empty when every root read.</summary>
    public IReadOnlyList<string> RootFailures { get; init; } = Array.Empty<string>();

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

    /// <summary>The loose roots the batch's asset build could not walk or list, each named with the reason, so a row's
    /// "may merely be unscanned" hedge has a folder to point at; empty when every root read.</summary>
    public IReadOnlyList<string> RootFailures { get; init; } = Array.Empty<string>();

    /// <summary>Whether the CALL was served at all, not whether every destination placed: a served call with failed rows is a success carrying per-row errors.</summary>
    public bool Success => Error is null;

    public static PlaceOutcome Fail(string error)
        => new(Array.Empty<PlaceResult>(), null, Array.Empty<string>(), null, error);
}

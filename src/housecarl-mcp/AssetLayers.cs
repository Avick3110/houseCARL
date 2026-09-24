using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcp;

/// <summary>One asset build with its warnings, profile and roots, all taken in one <c>_gate</c> hold.</summary>
internal readonly record struct AssetCapture(AssetResolver.AssetView View, IReadOnlyList<string> Warnings, string ProfileName,
                                             string ProfileDir, string DataDir, string ModsDir, string OverwriteDir,
                                             IReadOnlyList<ActiveArchive> Archives, IReadOnlyList<string> EnabledMods)
{
    /// <summary>The captured mods root, or null when there is none.</summary>
    public string? ModsRootOrNull => string.IsNullOrWhiteSpace(ModsDir) ? null : ModsDir;
}

/// <summary>The head members more than one area takes; contract in docs/architecture/load-order-service.md.</summary>
internal interface ILoadOrderHost
{
    /// <summary>One asset build with its warnings, profile and roots, in one <c>_gate</c> hold.</summary>
    AssetCapture CaptureAssets();

    /// <summary>The write gate; lock it before any capture, never inside one.</summary>
    object WriteGate { get; }
}

/// <summary>Everything the assets area takes from outside itself.</summary>
internal interface IAssetHost : ILoadOrderHost
{
    /// <summary>An asset capture and an index capture in one <c>_gate</c> hold, so neither is from a later build than the other.</summary>
    (AssetCapture Assets, LoadOrderResolver.IndexView Index) CaptureAssetsAndIndex();

    /// <summary>A pinned index and the asset build that pairs with it, in one <c>_gate</c> hold; <paramref name="afterPin"/> runs between the two.</summary>
    (LoadOrderService.ViewPin Pin, AssetCapture Assets) CapturePinAndAssets(Action? afterPin);

    /// <summary>The installed game runtime version, or null.</summary>
    string? InstalledGameRuntime();

    // Relayed from output until it is its own class (W4 plan ledger).
    LoadOrderService.RiderFolder ResolvePatchModFolder(string? patchName, string? into, string defaultStem, LoadOrderService.RiderNaming? naming);

    // Relayed from output until it is its own class (W4 plan ledger).
    string? RemoveOrNameRiderResidue(LoadOrderService.RiderFolder folder);

    // Relayed from writes (the consent store) until in-place consent is its own type (W4 plan ledger).
    bool IsInPlaceAcknowledged(string path);

    // Relayed from writes until in-place consent is its own type (W4 plan ledger).
    string? PersistInPlaceConsent(bool owed, string targetPath, string what, string subject);

    // Relayed from reads until it is its own class (W4 plan ledger).
    Dictionary<string, List<Type>> TypeLookup { get; }

    // Relayed from reads until it is its own class (W4 plan ledger).
    string UnresolvedFormId(LoadOrderResolver.IndexView view, FormKey fk);
}

public sealed partial class LoadOrderService
{
    /// <summary>Every head member this area takes, and nothing else.</summary>
    IAssetHost Host => this;

    /// <summary>Resolve a batch of Data-relative asset paths through the MO2 VFS (housecarl_asset_status): which
    /// source provides each, and which copy wins. ONE <see cref="AssetResolver.Capture"/> for the batch, so every
    /// path and the build-level caveats describe a single build; a bad path is a per-path error, never a batch
    /// failure. <paramref name="under"/> is the directory/glob SELECT (<see cref="AssetGlob"/>),
    /// <paramref name="seeds"/> the FaceGen one, and <paramref name="wholeSelection"/> <see cref="AssetArtifact"/>'s never-a-window <c>to_file=</c> disposition.</summary>
    public AssetStatusData AssetStatus(
        IReadOnlyList<string> relPaths,
        IReadOnlyList<string>? under = null,
        int limit = 0,
        int offset = 0,
        IReadOnlyList<FaceGenSeed>? seeds = null,
        bool wholeSelection = false)
    {
        var captured = Host.CaptureAssets();   // the view is pinned and handle-free, so the body runs outside the gate
        var view = captured.View; var warnings = captured.Warnings; var profileName = captured.ProfileName;
        var notes = new List<string>();
        var selected = new List<Selection>(relPaths.Count);
        foreach (var p in relPaths) selected.Add(new Selection(p ?? "", null, null, null));   // explicit paths first, in the order given, never deduped
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in relPaths)
            try { seen.Add(AssetResolver.ValidateRelPath((p ?? "").Trim())); } catch (ArgumentException) { /* a bad explicit path answers per-path below */ }

        // formids=: each NPC contributes BOTH halves of its pair, a pure transform of the FormKey, so no record is read.
        foreach (var seed in seeds ?? Array.Empty<FaceGenSeed>())
        {
            if (seed.Error is not null || seed.Key is not { } fk)
            {
                selected.Add(new Selection(seed.Token, seed.Token, null, null, seed.Error));
                continue;
            }
            var mesh = FaceGenPath.For(fk, FaceGenSlot.Mesh);
            var tint = FaceGenPath.For(fk, FaceGenSlot.Tint);
            selected.Add(new Selection(mesh, seed.Token, FaceGenSlot.Mesh, tint));
            selected.Add(new Selection(tint, seed.Token, FaceGenSlot.Tint, mesh));
            seen.Add(mesh);
            seen.Add(tint);
        }

        // The bound is on what this call RESOLVES: a window's walk runs to the end, while a whole-selection resolve stops the enumeration the moment it would cross it.
        bool wholeIsResolved = wholeSelection || limit <= 0;
        bool overBound = false;
        foreach (var raw in under ?? Array.Empty<string>())
        {
            if (overBound) break;
            var sel = (raw ?? "").Trim();
            if (sel.Length == 0) { notes.Add("under: an empty selector was skipped — pass a Data-relative directory or glob."); continue; }
            try
            {
                // One past what is left of the budget: a selector that fills it has proved the selection is over. 0 = no cap.
                int room = wholeIsResolved ? Math.Max(RenderBudget.MaxAssetPaths - selected.Count, 0) + 1 : 0;
                var matched = AssetGlob.Select(view, sel, out var namedOneFile, room, out var stopped);
                overBound |= stopped;
                // A selector that named a FILE is said out loud too, so the sweep's own count is explained.
                if (namedOneFile)
                    notes.Add($"under '{sel}' names a file, not a directory — it was resolved as that one path.");
                // A selector that matched nothing is said out loud: silent, a typo would read as a clean sweep.
                else if (matched.Count == 0)
                    notes.Add($"under '{sel}' matched no file in the active load order — check the spelling, or nothing enabled provides that folder.");
                foreach (var m in matched) if (seen.Add(m)) selected.Add(new Selection(m, null, null, null));
            }
            catch (ArgumentException ex) { notes.Add($"under '{sel}': {ex.Message}"); }
        }
        if (overBound)
            return new AssetStatusData(Array.Empty<AssetPathResult>(), view.BsaFailures, view.RootFailures,
                                       view.ReadIncomplete,
                                       warnings, profileName, notes, selected.Count, Math.Max(offset, 0),
                                       Math.Max(limit, 0),
                                       // Dedup can leave the running count at the bound rather than past it; the walk stopping is the proof.
                                       RenderBudget.RefuseAssetPaths(Math.Max(selected.Count, RenderBudget.MaxAssetPaths + 1),
                                                                     wholeSelection, atLeast: true)!);

        var total = selected.Count;
        var start = wholeSelection ? 0 : Math.Min(Math.Max(offset, 0), total);
        var window = wholeSelection
            ? selected
            : selected.Skip(start).Take(limit > 0 ? limit : int.MaxValue).ToList();

        // The declared cost, stated before a path is resolved: past the bound the call says what it would have spent.
        int toResolve = window.Count(s => s.Error is null)
                      + (window.Count > 0 && window[^1].PairPath is { } lastPair
                         && (window.Count < 2 || !string.Equals(lastPair, window[^2].Path, StringComparison.OrdinalIgnoreCase))
                         ? 1 : 0);
        if (RenderBudget.RefuseAssetPaths(toResolve, wholeSelection) is { } tooBig)
            return new AssetStatusData(Array.Empty<AssetPathResult>(), view.BsaFailures, view.RootFailures,
                                       view.ReadIncomplete,
                                       warnings, profileName, notes, total, Math.Max(offset, 0),
                                       Math.Max(limit, 0), tooBig);

        // A TWO-ENTRY lookaside, not a call-scoped memo: a pair's halves are adjacent, so one row of history gives the identical dedup at O(1) retention.
        string? seenA = null, seenB = null;
        AssetHit? seenAHit = null, seenBHit = null;
        AssetHit Resolve(string p)
        {
            if (seenA is not null && string.Equals(p, seenA, StringComparison.OrdinalIgnoreCase)) return seenAHit!;
            if (seenB is not null && string.Equals(p, seenB, StringComparison.OrdinalIgnoreCase)) return seenBHit!;
            return view.Resolve(p);
        }

        var results = new List<AssetPathResult>(window.Count);
        foreach (var sel in window)
        {
            var p = (sel.Path ?? "").Trim();
            if (sel.Error is not null) { results.Add(new AssetPathResult(p, null, sel.Error, null, sel.FormId)); continue; }
            try
            {
                var hit = Resolve(p);
                // Only on ABSENT, and only VERIFIED prefixes: a path taken off a record is stored relative to its root folder.
                var suggest = hit.Exists ? Array.Empty<string>()
                                         : AssetPathHint.VerifiedPrefixes(view, p, AssetPathHint.AssetRoots);
                var pair = sel.PairPath is null ? null : Resolve(sel.PairPath);
                results.Add(new AssetPathResult(p, hit, null, suggest, sel.FormId, sel.Slot, sel.PairPath, pair));
                seenA = p; seenAHit = hit;
                seenB = sel.PairPath; seenBHit = pair;
            }
            catch (ArgumentException ex) { results.Add(new AssetPathResult(p, null, ex.Message, null, sel.FormId)); }   // bad path → per-path note, never a batch failure
        }
        // Root failures are read AFTER the reads that fill them; warnings and profile name are the capture's, pinned with the view.
        return new AssetStatusData(results, view.BsaFailures, view.RootFailures, view.ReadIncomplete,
                                   warnings, profileName,
                                   notes, total, Math.Max(offset, 0),    // the offset ASKED for, so a past-the-end page can say so
                                   Math.Max(limit, 0));                  // the limit ASKED for, so the next-page advice repeats it
    }

    /// <summary>One entry of an <c>asset_status</c> selection: the path, and on a <c>formids=</c> row the NPC, the slot and the other half's path. <c>Error</c> answers as its own row.</summary>
    readonly record struct Selection(string Path, string? FormId, FaceGenSlot? Slot, string? PairPath, string? Error = null);

    // ---- SKSE-plugin-layer visibility: the DLLs, configs, winning provider and each DLL's declared manifest ----

    /// <summary>Inventory the SKSE-plugin layer as the active order resolves it: every .dll and every config at any
    /// depth under Data\SKSE\Plugins, with the mod that wins each and every DLL's declared manifest. Every file is
    /// accounted for and the subfolder group is derived, never a hardcoded framework list; what stops a DLL loading
    /// is the static-load rule in docs/architecture/skse-layer.md.</summary>
    /// <param name="peekFilter">When non-null, a matching DLL entry is also string-scanned; per-DLL, because the scan reads the whole image.</param>
    public SkseInventoryData SkseInventory(string? peekFilter = null)
    {
        var captured = Host.CaptureAssets();   // build/refresh the asset resolver under the gate, ONCE
        var view = captured.View; var warnings = captured.Warnings; var profileName = captured.ProfileName; var profileDir = captured.ProfileDir;
        // The plugin names a peek's cross-check adjudicates against, skipped entirely without peek=. The set is what
        // the game loads: plugins.txt entries plus the force-loaded base and CC masters, which never appear there.
        IReadOnlySet<string>? activePlugins = null;
        if (peekFilter is { Length: > 0 })
        {
            var compWarnings = new List<string>();
            activePlugins = PeekPluginSet(Mo2LoadOrder.ReadComposition(profileDir, compWarnings));
            if (compWarnings.Count > 0) warnings = [.. warnings, .. compWarnings];
        }
        // Outside the gate: the view is pinned and handle-free, so this cannot race a refresh into wrongness.
        const string pre = "SKSE\\Plugins\\";
        var dlls = new List<SkseFileEntry>();
        var configs = new List<SkseFileEntry>();
        var modVersions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);   // mod root → its meta.ini version, read once per mod
        int otherFiles = 0;
        foreach (var rel in view.EnumerateUnder("SKSE\\Plugins").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var ext = Path.GetExtension(rel).ToLowerInvariant();
            bool isDll = ext is ".dll";
            bool isConfig = ext is ".ini" or ".toml" or ".json" or ".yaml" or ".yml";
            if (!isDll && !isConfig) { otherFiles++; continue; }  // content/other (.hkx/.txt/.pdb/…) — counted, not listed

            string group = SkseGroupOf(rel, pre);                 // "" = top-level; else the immediate subfolder (the derived group key)
            var place = view.ResolveForPlacement(rel);
            var providers = place.Sources
                .Select(s => new SkseProvider(s.ProviderName, KindLabel(s.Kind)))   // shared kind→label helper (explicit switch, never a defaulted "loose")
                .ToList();
            var winner = place.Sources.Count > 0 ? place.Sources[0] : null;

            if (isDll)
            {
                SksePluginReader.SksePluginInfo? info = null;
                string? modVersion = null;
                string? note = null;
                if (winner is { Kind: AssetKind.Loose, LooseFilePath: { } path })
                {
                    info = SksePluginReader.Read(path);           // the winning loose copy
                    // What MO2 recorded for the mod that ships it, read once per mod, and only for a DLL that IS an SKSE plugin.
                    if (info.Kind is SksePluginReader.SksePluginKind.Modern or SksePluginReader.SksePluginKind.LegacyQuery
                        && Mo2ModMeta.ModRootForLooseFile(path, rel) is { } modRoot)
                    {
                        if (!modVersions.TryGetValue(modRoot, out modVersion))
                            modVersions[modRoot] = modVersion = Mo2ModMeta.Read(Path.Combine(modRoot, "meta.ini"))?.Version;
                    }
                }
                else if (winner is null) note = "no active mod provides this DLL";
                else note = "provided ONLY inside a BSA — the SKSE loader scans loose Data\\SKSE\\Plugins only, so this DLL will not load";
                if (group.Length > 0 && note is null)
                    note = $"in subfolder '{group}' — NOT on SKSE's loader path (scans SKSE\\Plugins\\*.dll top-level only); a bundled/parent-loaded DLL, not a plugin SKSE loads";
                var entry = new SkseFileEntry(rel, Path.GetFileName(rel), group, providers, info, note, ModVersion: modVersion);
                // String peek only for a loose winner — the copy SKSE would load (docs/architecture/skse-layer.md).
                if (peekFilter is { Length: > 0 } && entry.MatchesDll(peekFilter)
                    && winner is { Kind: AssetKind.Loose, LooseFilePath: { } peekPath })
                    entry = entry with { Peek = SksePeek.Scan(peekPath) };
                dlls.Add(entry);
            }
            else
                configs.Add(new SkseFileEntry(rel, Path.GetFileName(rel), group, providers, null, null));
        }
        return new SkseInventoryData(dlls, configs, otherFiles, Host.InstalledGameRuntime(), view.BsaFailures, view.RootFailures,
            view.ReadIncomplete, warnings, profileName, activePlugins, peekFilter is { Length: > 0 });
    }

    /// <summary>Why a loose, loader-scoped SKSE plugin DLL statically cannot load, or null; the rule and its blocker chain are in docs/architecture/skse-layer.md.</summary>
    internal static string? LooseDllBlocker(SksePluginReader.SksePluginInfo info, Func<string, bool> resolvable)
    {
        if (info.Kind == SksePluginReader.SksePluginKind.Unreadable) return $"not a readable SKSE plugin ({info.Note})";
        if (info.Is64Bit == false) return "a 32-bit image — cannot load in Skyrim SE/AE";
        return SksePluginReader.DebugCrtBlocker(info, resolvable);
    }

    /// <summary>The plugin names a peek adjudicates an embedded reference against — active plus the force-loaded
    /// implicit masters. Returns null, never a partial set, when the answer is unknowable.</summary>
    internal static IReadOnlySet<string>? PeekPluginSet(Mo2Composition comp)
    {
        if (comp.OrderedPluginNames.Count == 0) return null;   // no loadorder.txt ⇒ the implicit masters are unknowable, not absent
        var set = new HashSet<string>(comp.ActivePluginNames, StringComparer.OrdinalIgnoreCase);
        set.UnionWith(comp.ImplicitPluginNames);
        return set.Count > 0 ? set : null;
    }

    /// <summary>The immediate subfolder under SKSE\Plugins a file sits in ("" = top level) — the DERIVED grouping key, since a hardcoded framework list would miscategorize anything not on it.</summary>
    static string SkseGroupOf(string rel, string pre)
    {
        if (!rel.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) return "";
        int slash = rel.IndexOf('\\', pre.Length);
        return slash < 0 ? "" : rel.Substring(pre.Length, slash - pre.Length);
    }

    // ---- SKSE config audit: the form references SKSE-plugin configs declare, against the real records ----

    /// <summary>Per-file byte cap for the config scan: a larger config is a named skip, so 16 MB trips only on content mislabeled as a config.</summary>
    const long SkseConfigSizeCap = 16L * 1024 * 1024;

    /// <summary>Audit the SKSE-plugin config layer: read each config's winning copy, extract the form references and
    /// plugin gates it declares, and resolve each into a verdict. Framework-agnostic; two captures pin the scan.</summary>
    public SkseConfigAuditData SkseConfigAudit()
    {
        // One gate hold for both captures, so a rebuild cannot pair a config read from one build against an index from the next.
        var (captured, index) = Host.CaptureAssetsAndIndex();   // the index is a pure snapshot: ContainsPlugin / ResolveWinner read only this build
        var view = captured.View; var warnings = captured.Warnings; var profileName = captured.ProfileName;

        const string pre = "SKSE\\Plugins\\";
        var files = new List<SkseConfigFileAudit>();
        foreach (var rel in view.EnumerateUnder("SKSE\\Plugins").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var ext = Path.GetExtension(rel).ToLowerInvariant();
            if (ext is not (".ini" or ".toml" or ".json" or ".yaml" or ".yml")) continue;   // configs only (DLLs/content are SkseInventory's)

            string group = SkseGroupOf(rel, pre);
            var place = view.ResolveForPlacement(rel);
            var winner = place.Sources.Count > 0 ? place.Sources[0] : null;
            var providers = place.Sources.Select(s => new SkseProvider(s.ProviderName, KindLabel(s.Kind))).ToList();

            string? readError = null;
            string text = "";
            if (winner is null)
                readError = "no active mod provides this config";   // shouldn't happen for an enumerated file — named, not assumed
            else if (winner.Kind == AssetKind.Loose && winner.LooseFilePath is { } lp && File.Exists(lp) && new FileInfo(lp).Length > SkseConfigSizeCap)
                readError = OverCapNote(new FileInfo(lp).Length);
            else
            {
                var (bytes, err) = AssetResolver.ReadPlacementSource(winner);
                if (err is not null) readError = err;
                else if (bytes!.Length > SkseConfigSizeCap) readError = OverCapNote(bytes.Length);
                else text = DecodeConfigText(bytes);
            }

            // Path-segment gates come from the relPath, so they surface even when the file could not be read.
            var extracted = SkseConfigReferenceExtractor.Extract(rel, readError is null ? text : "");
            var audited = new List<SkseAuditedRef>(extracted.Count);
            foreach (var r in extracted) audited.Add(Adjudicate(r, index));

            files.Add(new SkseConfigFileAudit(rel, Path.GetFileName(rel), group,
                winner?.ProviderName, providers.Count, providers, audited, readError));
        }
        return new SkseConfigAuditData(files, files.Count, view.BsaFailures, view.RootFailures, view.ReadIncomplete, warnings, profileName);
    }

    /// <summary>Resolve one extracted reference into a verdict: a path-segment gate is plugin-presence only, a form token also checks the record exists. Never speculates about runtime behavior.</summary>
    internal static SkseAuditedRef Adjudicate(SkseConfigRef r, LoadOrderResolver.IndexView index)   // internal: a test drives it over a synthetic order
    {
        if (r.Unparseable is not null)
            return new SkseAuditedRef(r, SkseRefVerdict.Unparseable, r.Unparseable);

        if (!index.ContainsPlugin(r.Plugin))
            return new SkseAuditedRef(r, SkseRefVerdict.PluginMissing, $"'{r.Plugin}' is not in the active load order");

        if (r.Shape == SkseRefShape.PathSegmentGate)
            return new SkseAuditedRef(r, SkseRefVerdict.Ok, null);   // gate satisfied — the plugin is present

        if (!ModKey.TryFromNameAndExtension(r.Plugin, out var mk))
            return new SkseAuditedRef(r, SkseRefVerdict.Unparseable, $"'{r.Plugin}' is not a valid plugin name");
        var fk = new FormKey(mk, r.LocalId!.Value);
        return index.ResolveWinner(fk) is not null
            ? new SkseAuditedRef(r, SkseRefVerdict.Ok, FormIdToken.Of(fk))
            : new SkseAuditedRef(r, SkseRefVerdict.Dangling, $"{FormIdToken.Of(fk)} resolves to no record in '{r.Plugin}'");
    }

    /// <summary>Decode a config's bytes, honoring a BOM when present and defaulting to UTF-8, which the config formats are in practice.</summary>
    static string DecodeConfigText(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var sr = new StreamReader(ms, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return sr.ReadToEnd();
    }

    /// <summary>The over-cap skip note; the size carries one decimal, so a 16.4 MB file does not read "16 MB (> 16 MB cap)".</summary>
    static string OverCapNote(long len) =>
        $"config is {len / (1024.0 * 1024):0.0} MB (> {SkseConfigSizeCap / (1024 * 1024)} MB cap) — not scanned";

    // ---- Native-function pairing audit: the native Papyrus functions the order declares, against the DLLs ----

    /// <summary>Audit the declaration-to-implementation pairing of every native Papyrus class in the order: one pass
    /// over the winning .pex files for native-flagged declarations, one over SKSE\Plugins for each mod's DLL
    /// candidates, then an evidence ladder per third-party class — same-mod DLL, chain DLL, or UNPAIRED, which is a
    /// verify flag and never "broken". A class whose chain includes an official archive is ENGINE; a third-party class
    /// whose winning provider also provides an ENGINE class is SKSE CORE. An unreadable .pex is a named entry.</summary>
    public NativePairingAuditData NativePairingAudit()
    {
        // Archives and enabled mods are the same build as the view, so the loader scan below walks the mod set the view describes, never a second unpinned profile read.
        var captured = Host.CaptureAssets();
        var view = captured.View; var warnings = captured.Warnings; var profileName = captured.ProfileName;
        var dataDir = captured.DataDir; var modsDir = captured.ModsDir; var overwriteDir = captured.OverwriteDir;
        var archives = captured.Archives; var enabledMods = captured.EnabledMods;

        // ---- the official-archive set: the ENGINE anchor. Keyed by filename, because a BSA provider's name IS the archive filename. ----
        var officialArchives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var baseMasters = Mutagen.Bethesda.Plugins.Implicits.Get(Mutagen.Bethesda.GameRelease.SkyrimSE).BaseMasters;
        foreach (var a in archives)
            if (IsOfficialArchive(a, baseMasters))
                officialArchives.Add(Path.GetFileName(a.Path));

        // Pairing identity needs the MOD that ships an archive, not the archive filename, or a mod whose scripts ride its own BSA reads as UNPAIRED.
        var archiveShipper = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in archives)
            if (LayerOfInstallPath(a.Path, modsDir, overwriteDir, dataDir) is { } shipper)
                archiveShipper[Path.GetFileName(a.Path)] = shipper;

        // ---- DLL candidates: one SKSE\Plugins pass. A mod "ships" a DLL when it appears anywhere in that file's
        //      chain; a winner that PE-reads as NotSkse is a bundled dependency, not a candidate. ----
        const string skseRootPre = "SKSE\\Plugins\\";
        var modDlls = new Dictionary<string, List<NativePairedDll>>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in view.EnumerateUnder("SKSE\\Plugins").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (!Path.GetExtension(rel).Equals(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            string group = SkseGroupOf(rel, skseRootPre);
            var place = view.ResolveForPlacement(rel);
            var winner = place.Sources.Count > 0 ? place.Sources[0] : null;

            SksePluginReader.SksePluginInfo? info = null;
            string? blocker = null;
            if (winner is null) blocker = "no active mod provides it";
            else if (winner.Kind != AssetKind.Loose)
            {
                blocker = "provided only inside a BSA — the SKSE loader scans loose DLLs only, so it will not load";
                try
                {
                    // PE-screen the packed copy too: a packed NotSkse dependency must not count as a candidate.
                    if (AssetResolver.TryReadArchiveEntry(winner.ArchivePath!, winner.EntryPath) is { } bytes)
                        info = SksePluginReader.ReadBytes(Path.GetFileName(rel), bytes);
                }
                catch { /* unreadable archive rides the view's BsaFailures caveat; the candidate keeps its blocker */ }
                if (info?.Kind == SksePluginReader.SksePluginKind.NotSkse) continue;
            }
            else
            {
                info = SksePluginReader.Read(winner.LooseFilePath!);
                if (info.Kind == SksePluginReader.SksePluginKind.NotSkse) continue;   // bundled dependency — not a candidate
                blocker = LooseDllBlocker(info, SksePluginReader.IsSystemDllResolvable);
            }
            if (blocker is null && group.Length > 0)
                blocker = $"in subfolder '{group}' — not on SKSE's loader path (scans SKSE\\Plugins\\*.dll top-level only)";

            var dll = new NativePairedDll(rel, Path.GetFileName(rel), group, winner?.ProviderName, info, blocker);
            foreach (var src in place.Sources)
            {
                var mod = PairingIdentity(src, archiveShipper);
                if (!modDlls.TryGetValue(mod, out var list)) modDlls[mod] = list = new();
                if (!list.Any(d => d.RelPath.Equals(rel, StringComparison.OrdinalIgnoreCase))) list.Add(dll);
            }
        }

        // ---- the .pex sweep, two phases: loose winners parse in place, BSA winners defer to a per-archive batch,
        //      because a per-entry read re-opens the archive and walks its whole table each time. ----
        var pexPaths = view.EnumerateUnder("Scripts")
            .Where(p => Path.GetExtension(p).Equals(".pex", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

        var natives = new System.Collections.Concurrent.ConcurrentBag<(string Rel, IReadOnlyList<HousecarlCore.NativeClassDecl> Decls)>();
        var unreadable = new System.Collections.Concurrent.ConcurrentBag<NativeUnreadablePex>();
        var bsaWanted = new System.Collections.Concurrent.ConcurrentBag<(string Rel, string ArchivePath, string EntryPath, string Provider)>();

        void ParsePex(string rel, string? provider, Func<Mutagen.Bethesda.Pex.PexFile> load)
        {
            try
            {
                var decls = HousecarlCore.NativePairing.ExtractNativeClasses(load());
                if (decls.Count > 0) natives.Add((rel, decls));
            }
            catch (Exception ex)
            {
                unreadable.Add(new NativeUnreadablePex(rel, provider, $"Mutagen cannot read it ({ex.GetType().Name}: {ex.Message}) — the known unreadable-pex class"));
            }
        }

        System.Threading.Tasks.Parallel.ForEach(pexPaths, rel =>
        {
            var place = view.ResolveForPlacement(rel);
            var winner = place.Sources.Count > 0 ? place.Sources[0] : null;
            if (winner is null) { unreadable.Add(new NativeUnreadablePex(rel, null, "enumerated but no active source provides it")); return; }
            if (winner.LooseFilePath is { } lp)
                ParsePex(rel, winner.ProviderName, () => Mutagen.Bethesda.Pex.PexFile.CreateFromFile(lp, Mutagen.Bethesda.GameCategory.Skyrim));
            else
                bsaWanted.Add((rel, winner.ArchivePath!, winner.EntryPath, winner.ProviderName));
        });

        foreach (var g in bsaWanted.GroupBy(w => w.ArchivePath, StringComparer.OrdinalIgnoreCase))
        {
            Dictionary<string, byte[]> got;
            try { got = AssetResolver.TryReadArchiveEntries(g.Key, g.Select(w => w.EntryPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList()); }
            catch (Exception ex)
            {
                foreach (var w in g)
                    unreadable.Add(new NativeUnreadablePex(w.Rel, w.Provider, $"archive '{Path.GetFileName(g.Key)}' could not be read ({ex.GetType().Name}: {ex.Message})"));
                continue;
            }
            System.Threading.Tasks.Parallel.ForEach(g, w =>
            {
                if (!got.TryGetValue(w.EntryPath, out var bytes))
                    unreadable.Add(new NativeUnreadablePex(w.Rel, w.Provider, $"vanished from '{Path.GetFileName(g.Key)}' between listing and read"));
                else
                    ParsePex(w.Rel, w.Provider, () =>
                    {
                        using var ms = new MemoryStream(bytes);
                        return Mutagen.Bethesda.Pex.PexFile.CreateFromStream(ms, Mutagen.Bethesda.GameCategory.Skyrim);
                    });
            });
        }

        // ---- classify and pair. Provenance keys on the enum-typed PlacementSource, never the render label. ----
        var native = natives.OrderBy(s => s.Rel, StringComparer.OrdinalIgnoreCase)
            .Select(s => (s.Rel, s.Decls, Sources: view.ResolveForPlacement(s.Rel).Sources))
            .ToList();

        // Pass 1: the SKSE-CORE rescue pool — every non-official pairing identity shipping a copy of an ENGINE class.
        // The rescue's residual edges are in docs/architecture/skse-layer.md.
        var engineProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in native)
            if (HasOfficialSource(s.Sources, officialArchives))
                foreach (var src in s.Sources)
                    if (!(src.Kind == AssetKind.Bsa && officialArchives.Contains(src.ProviderName)))
                        engineProviders.Add(PairingIdentity(src, archiveShipper));
        // "overwrite" is excluded from the rescue: a recompiled vanilla .pex there is routine, and letting it rescue
        // every orphan declaration copy would silence the flag this tool exists for. "Data" stays.
        engineProviders.Remove("overwrite");

        // Pass 2: build the entries (Classify carries the decision order: engine → ladder → rescue).
        var classes = new List<NativeClassEntry>();
        foreach (var s in native)
        {
            bool engine = HasOfficialSource(s.Sources, officialArchives);
            var identities = s.Sources.Select(src => PairingIdentity(src, archiveShipper)).ToList();
            var display = s.Sources.Select(src => new SkseProvider(src.ProviderName, KindLabel(src.Kind))).ToList();
            foreach (var d in s.Decls)
            {
                var (prov, rung, pairedMod, pairedDlls) = Classify(engine, identities, modDlls, engineProviders);
                classes.Add(new NativeClassEntry(s.Rel, d.ClassName, d.NativeFunctions, display,
                    prov, rung, pairedMod, pairedDlls));
            }
        }

        // Is an skse64 loader visible at all — the game root, or a mod's Root\ folder? Tri-state: a check that threw yields null, never a false absent.
        bool? loaderSeen;
        try
        {
            static bool LoaderIn(string dir) => Directory.Exists(dir)
                && (File.Exists(Path.Combine(dir, "skse64_loader.exe"))
                    || Directory.EnumerateFiles(dir, "skse64_*.dll").Any());
            var gameDir = dataDir.Length > 0 ? Path.GetDirectoryName(dataDir.TrimEnd('\\', '/')) : null;
            loaderSeen = (gameDir is { Length: > 0 } && LoaderIn(gameDir))
                || (modsDir.Length > 0 && enabledMods.Any(m => LoaderIn(Path.Combine(modsDir, m, "Root"))));
        }
        catch { loaderSeen = null; }

        return new NativePairingAuditData(classes, pexPaths.Count,
            unreadable.OrderBy(u => u.RelPath, StringComparer.OrdinalIgnoreCase).ToList(),
            loaderSeen, Host.InstalledGameRuntime(),
            view.BsaFailures, view.RootFailures, view.ReadIncomplete, warnings, profileName);
    }

    /// <summary>The MO2 LAYER a physical file path belongs to, as a NAME. A caller that has to say WHICH of the three
    /// answered takes <see cref="InstallLayerOfPath"/> instead, because a mod folder may itself be called "Data".</summary>
    internal static string? LayerOfInstallPath(string archivePath, string modsDir, string overwriteDir, string dataDir) =>
        InstallLayerOfPath(archivePath, modsDir, overwriteDir, dataDir)?.Name;

    /// <summary>The MO2 layer a physical file path belongs to, as the BRANCH that answered plus the name it produced.</summary>
    internal static SourceLayer? InstallLayerOfPath(string archivePath, string modsDir, string overwriteDir, string dataDir)
    {
        // Full-path-normalize both sides, or a trailing separator or '..' from config makes this test disagree with the rest of the plumbing.
        static string Norm(string p) { try { return Path.GetFullPath(p); } catch { return p; } }
        archivePath = Norm(archivePath);
        static bool Under(string path, string root, out string remainder)
        {
            remainder = "";
            if (root.Length == 0) return false;
            var r = Norm(root).TrimEnd('\\', '/') + "\\";
            if (!path.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return false;
            remainder = path.Substring(r.Length);
            return true;
        }
        if (Under(archivePath, overwriteDir, out _))
            return new SourceLayer(SourceLayerKind.Overwrite, AssetResolver.OverwriteLayerName);
        if (Under(archivePath, modsDir, out var rest))
        {
            int slash = rest.IndexOfAny(new[] { '\\', '/' });
            // a .bsa directly in mods\ belongs to no mod — no translation
            return slash > 0 ? new SourceLayer(SourceLayerKind.ModFolder, rest[..slash]) : null;
        }
        if (Under(archivePath, dataDir, out _))
            return new SourceLayer(SourceLayerKind.GameData, AssetResolver.DataLayerName);
        return null;
    }

    /// <summary>An archive is OFFICIAL — its scripts' natives are the engine's own — when it loads from Skyrim.ini's base block or is owned by a base master (Mutagen's implicit list, never a name list).</summary>
    internal static bool IsOfficialArchive(ActiveArchive a, IReadOnlyList<ModKey> baseMasters) =>
        a.OwningPlugin.Equals(ArchiveDiscovery.IniArchiveOwner, StringComparison.OrdinalIgnoreCase)   // ignore-case like every other archive compare — this must not hinge on the marker's casing
        || (ModKey.TryFromNameAndExtension(a.OwningPlugin, out var mk) && baseMasters.Contains(mk));

    /// <summary>True when any source in a file's chain is an official archive — the ENGINE provenance test, keyed on the <see cref="AssetKind"/> enum, never the render label.</summary>
    internal static bool HasOfficialSource(IReadOnlyList<PlacementSource> sources, HashSet<string> officialArchives) =>
        sources.Any(s => s.Kind == AssetKind.Bsa && officialArchives.Contains(s.ProviderName));

    /// <summary>One source's PAIRING IDENTITY — the mod it means: a BSA translates to the mod shipping the archive.</summary>
    internal static string PairingIdentity(PlacementSource src, IReadOnlyDictionary<string, string> archiveShipper) =>
        src.Kind == AssetKind.Bsa && archiveShipper.TryGetValue(src.ProviderName, out var mod) ? mod : src.ProviderName;

    /// <summary>The pairing-evidence ladder for one third-party class, over the chain's identities winner first: the
    /// winning identity ships a candidate DLL, one deeper does, or nobody does (UNPAIRED). The descent past a dead
    /// candidate rides the static-load rule in docs/architecture/skse-layer.md. Structural, never semantic.</summary>
    internal static (NativePairingRung Rung, string? PairedMod, IReadOnlyList<NativePairedDll> Dlls) Ladder(
        IReadOnlyList<string> identities, IReadOnlyDictionary<string, List<NativePairedDll>> modDlls)
    {
        int firstAny = -1;
        for (int i = 0; i < identities.Count; i++)
        {
            if (!modDlls.TryGetValue(identities[i], out var dlls) || dlls.Count == 0) continue;
            if (dlls.Any(d => d.LoadBlocker is null))
                return (i == 0 ? NativePairingRung.SameMod : NativePairingRung.ChainMod, identities[i], dlls);
            if (firstAny < 0) firstAny = i;
        }
        if (firstAny >= 0)
            return (firstAny == 0 ? NativePairingRung.SameMod : NativePairingRung.ChainMod, identities[firstAny], modDlls[identities[firstAny]]);
        return (NativePairingRung.Unpaired, null, Array.Empty<NativePairedDll>());
    }

    /// <summary>The full per-class decision, in order: ENGINE, then the ladder, then the SKSE-CORE rescue for an
    /// UNPAIRED class whose winning identity also ships an ENGINE-class copy. Pairing evidence beats the rescue.</summary>
    internal static (NativeProvenance Provenance, NativePairingRung? Rung, string? PairedMod, IReadOnlyList<NativePairedDll> Dlls) Classify(
        bool engine, IReadOnlyList<string> identities,
        IReadOnlyDictionary<string, List<NativePairedDll>> modDlls, HashSet<string> engineProviders)
    {
        if (engine) return (NativeProvenance.Engine, null, null, Array.Empty<NativePairedDll>());
        var (rung, pairedMod, dlls) = Ladder(identities, modDlls);
        if (rung == NativePairingRung.Unpaired && identities.Count > 0 && engineProviders.Contains(identities[0]))
            return (NativeProvenance.SkseCore, null, null, Array.Empty<NativePairedDll>());
        return (NativeProvenance.ThirdParty, rung, pairedMod, dlls);
    }

    // ---- SkyPatcher distributor: the whole-layer scan. Read-only. The per-record replay is in SkyPatcherReplay.cs. ----

    /// <summary>Test seam: invoked in <see cref="SkyPatcherLayer"/> after the pin and before the asset capture; null in the product.</summary>
    internal Action? AfterSkyPatcherPinForGuard;

    /// <summary>Scan the whole SkyPatcher layer: every loose INI as the DLL reads it, the same-field SET collisions,
    /// and the three ITM classes including the no-op writes the per-record replay finds. Report-only.</summary>
    public SkyPatcherLayerData SkyPatcherLayer()
    {
        // No epoch is stamped: the INI layer is outside the index fingerprint, so a bare index epoch would overclaim.
        // One hold, one profile refresh: a warm asset build pairs with the pinned index; a cold one reads the profile itself.
        var (pin, captured) = Host.CapturePinAndAssets(AfterSkyPatcherPinForGuard);   // the seam is null in the product

        var view = pin.View;
        using var session = pin.Resolver.OpenSession();
        var replay = OpenSkyPatcherReplay(captured, view, session, out _)!;   // no draft, so never refused
        var assets = replay.Assets;
        var catalog = replay.Catalog;
        var fieldMap = replay.FieldMap;
        var scan = replay.Scan;
        var conflicts = new List<SkyPatcherConflicts.SkyPatcherConflict>();
        var itms = new List<SkyPatcherConflicts.SkyPatcherItm>();
        var duplicates = new List<SkyPatcherConflicts.SkyPatcherDuplicate>();
        foreach (var folder in scan.Folders)
        {
            var report = SkyPatcherConflicts.Detect(folder, catalog, fieldMap);
            conflicts.AddRange(report.Conflicts);
            itms.AddRange(report.Itms);
            duplicates.AddRange(report.Duplicates);
        }

        // ---- the TRUE-ITM scan: replay every explicitly-targeted record through the same per-record core the post-state
        //      read uses, and flag SET ops whose before == after. Broad (type-wide) lines see only the explicit targets ----
        var noOps = new List<SkyPatcherNoOpWrite>();
        var noOpNotes = new List<string>();
        {
            var targets = new HashSet<FormKey>();
            int broadLines = 0, unresolvedTargets = 0, failedReplays = 0;
            foreach (var folder in scan.Folders)
            {
                if (folder.Catalog is null || !folder.PatchingEnabled) continue;
                var eids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ol in SkyPatcherDiscovery.OrderedLines(folder))
                    SkyPatcherConflicts.CollectExplicitPrimaryTargets(ol.Parsed, catalog, folder.Catalog, targets, eids, ref broadLines);
                var folderTypes = fieldMap.ForSubfolder(folder.Subfolder).Select(m => m.RecordType).ToList();
                foreach (var eid in eids)
                {
                    var rfk = folderTypes.Select(t => replay.ResolveEditorId(eid, t)).FirstOrDefault(x => x is not null);
                    if (rfk is not null) targets.Add(rfk.Value); else unresolvedTargets++;
                }
            }
            foreach (var fk in targets)
            {
                var r = replay.Replay(fk);
                if (r.Error is not null) { failedReplays++; continue; }
                foreach (var fo in r.Folders)
                {
                    if (fo.Result is not { } res) continue;
                    var map = fieldMap.For(fo.Subfolder, r.TypeName!);
                    foreach (var a in res.Applied)
                        if (SkyPatcherConflicts.IsNoOpWrite(a, map))
                            noOps.Add(new SkyPatcherNoOpWrite(fo.Subfolder, FormIdToken.Of(fk), r.EditorId,
                                a.FieldPath, a.File, a.LineNumber, a.Op, a.RawValue, a.Before!));
                }
            }
            // Stable output: targets is a hash set, so without this a re-run cannot be diffed against the previous one.
            noOps.Sort((x, y) =>
            {
                int c = string.Compare(x.File, y.File, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                c = x.Line.CompareTo(y.Line);
                return c != 0 ? c : string.Compare(x.FormKey, y.FormKey, StringComparison.OrdinalIgnoreCase);
            });
            if (broadLines > 0) noOpNotes.Add($"no-op scan: {broadLines} broad (type-wide) line(s) were evaluated only against the explicitly-targeted records, not every record of their type.");
            if (unresolvedTargets > 0) noOpNotes.Add($"no-op scan: {unresolvedTargets} explicit target(s) did not resolve (the overlay's per-record warnings name them; read one with {ToolNames.Records} formids=[\"<FormID>\"] source={{\"overlay\": \"skypatcher\", \"state\": \"post\"}}).");
            if (failedReplays > 0) noOpNotes.Add($"no-op scan: {failedReplays} targeted record(s) could not be replayed (not in the order / unpatchable type / copy failure / an EditorID lookup that could not be completed).");
            // Emitted AFTER the replays: a plugin can first turn out unreadable in a sweep the replay itself runs.
            foreach (var msg in replay.Unreadable.Select(u => u.Message).Distinct())
                noOpNotes.Add($"no-op scan: {msg} Lines naming a record it defines could not be resolved.");
        }

        return new SkyPatcherLayerData(scan, conflicts, itms, duplicates, noOps, noOpNotes, assets.RootFailures,
            scan.ReadIncomplete || assets.ReadIncomplete, replay.AssetWarnings, replay.ProfileName);
    }

    /// <summary>A form-scope string to getter Types: a catalog name or signature via the type lookup, or a Mutagen link-interface group name resolved as every corpus record getter assignable to <c>I{name}Getter</c>, derived from the real interfaces rather than a hand-kept list. Null means it names neither, which the caller surfaces loudly.</summary>
    internal IReadOnlyList<Type>? ResolveFormScope(string type)
    {
        var t = type.Trim();
        if (Host.TypeLookup.TryGetValue(t, out var types)) return types;
        var iface = typeof(SkyrimMod).Assembly.GetType($"Mutagen.Bethesda.Skyrim.I{t}Getter");
        if (iface is null) return null;
        var matches = Host.TypeLookup.Values.SelectMany(v => v).Distinct().Where(iface.IsAssignableFrom).ToList();
        return matches.Count > 0 ? matches : null;
    }

    // ---- NIF layer: read the data values inside one or many meshes (housecarl_nif_inspect) ----

    /// <summary>Inspect the data values inside one or many Skyrim meshes: capture the resolver once under the gate,
    /// then per path and outside it resolve through the VFS to the winning copy (or <paramref name="mod"/>'s), read the
    /// bytes in process — a loose file, or one BSA entry, no disk extraction — and hand them to
    /// <see cref="NifService.Inspect"/>. A per-path failure never aborts the batch; the build-level caveats ride it.</summary>
    public NifInspectBatchData NifInspect(IReadOnlyList<string> relPaths, string? sourceProvider)
    {
        var captured = Host.CaptureAssets();   // build/refresh the asset resolver under the gate, once per batch
        var view = captured.View; var warnings = captured.Warnings; var profileName = captured.ProfileName;

        var modsRoot = captured.ModsRootOrNull;   // the same build as the view, so a raw mods path is judged against the tree the view describes
        var results = new List<NifInspectData>(relPaths.Count);
        foreach (var raw in relPaths)
        {
            var rel = (raw ?? "").Trim();
            // A raw path into the mods tree reads past the VFS, so it is this path's own refusal with the address form.
            if (ModsPathAddress.Split(rel, modsRoot) is { } hit)
            {
                results.Add(NifInspectData.Fail(rel, ModsPathAddress.Refusal("", rel,
                    ModsPathAddress.Address(hit.ModFolder, hit.RelPath, "mesh_paths", "source_provider"))));
                continue;
            }
            // Per-path isolation by construction: anything unexpected becomes that path's named error.
            try { results.Add(NifInspectOne(view, rel, sourceProvider)); }
            catch (Exception ex) { results.Add(NifInspectData.Fail(rel, $"unexpected error inspecting this path — {ex.GetType().Name}: {ex.Message}")); }
        }
        return new NifInspectBatchData(results, view.BsaFailures, view.RootFailures, warnings, profileName);
    }

    /// <summary>One path's inspect against the already-captured view. Every failure is a named per-path outcome, never a throw.</summary>
    static NifInspectData NifInspectOne(AssetResolver.AssetView view, string rel, string? sourceProvider)
    {
        if (rel.Length == 0)
            return NifInspectData.Fail("", "empty mesh path. Pass a Data-relative path, e.g. 'meshes\\actors\\character\\facegendata\\facegeom\\Skyrim.esm\\00000007.nif'.");

        PlacementResolution place;
        try { place = view.ResolveForPlacement(rel); }
        catch (ArgumentException ex) { return NifInspectData.Fail(rel, $"invalid path — {ex.Message}"); }

        var providers = place.Sources.Select(s => new NifProvider(s.ProviderName, KindLabel(s.Kind))).ToList();

        // Pick the copy to read: the VFS winner, or the provider source_provider= names — answered FIRST, ahead of the ABSENT return.
        PlacementSource chosen;
        if (!string.IsNullOrWhiteSpace(sourceProvider))
        {
            var pick = NifPick(view, place, rel, sourceProvider!.Trim());
            if (pick.Error is not null)
                return new NifInspectData(rel, null, providers, place.Ambiguous, pick.Absent, null, pick.Error);
            chosen = pick.Source!;
        }
        else
        {
            if (place.Sources.Count == 0)
            {
                // A model path off a record is stored relative to meshes\, so a flat ABSENT is a dead end. The hint is re-resolved, never guessed.
                var hint = AssetPathHint.MeshHint(view, rel);
                return new NifInspectData(rel, null, providers, place.Ambiguous, Absent: true, null,
                    "ABSENT — no active mod or BSA provides this mesh path." + (hint is null ? "" : " " + hint));
            }
            chosen = place.Sources[0];
        }

        var (bytes, readErr) = AssetResolver.ReadPlacementSource(chosen);
        if (bytes is null)
            return new NifInspectData(rel, NifProviderFor(chosen), providers, place.Ambiguous,
                false, null, readErr ?? "could not read the resolved mesh bytes.");

        var outcome = NifService.Inspect(bytes);
        return new NifInspectData(rel, NifProviderFor(chosen), providers, place.Ambiguous,
            false, outcome.Inspect, outcome.Error);
    }

    /// <summary>The provider record for a CHOSEN source, carrying the off-order provenance only the copy actually read can have.</summary>
    static NifProvider NifProviderFor(PlacementSource s)
        => new(s.ProviderName, KindLabel(s.Kind), s.OffOrder, s.OwnerEnabled);

    /// <summary>Answer <c>source_provider=</c> for the NIF surface through the ONE source policy every asset caller
    /// rides, or hand back the refusal sentence. Shared by nif_inspect and nif_set, which have drifted once before.</summary>
    static (PlacementSource? Source, string? Error, bool Absent) NifPick(AssetResolver.AssetView view, PlacementResolution place, string rel, string sourceProvider)
    {
        // Parse, not Named: the refusal's tail teaches the '*winner' pole, so this surface has to take it.
        var choice = AssetSourceChoice.Parse(sourceProvider);
        var pick = AssetSourceSelection.Select(place, choice, n => view.TryResolveOffOrderProvider(n, rel));
        if (pick.Verdict == AssetSourceVerdict.Selected) return (pick.Source, null, false);
        // The winner pole over an empty universe is the ABSENT case, not a named miss, and Absent travels with it.
        if (choice.Pole != AssetSourcePole.Named)
            return (null, "ABSENT — no active mod or BSA provides '" + rel + "', so there is no winner to read."
                        + (AssetPathHint.MeshHint(view, rel) is { } wh ? " " + wh : ""), true);
        // A named miss is NOT an absence — it says which mod and carries its own inline caveat instead.
        return (null, WriteSentences.PlaceSourceNamedAbsent(
            sourceProvider, rel, pick.ProviderNames,
            pick.OffOrderReason, pick.OffOrderUnreadableName, pick.OffOrderUnreadableCause,
            AssetPathHint.MeshHint(view, rel), place.ReadIncomplete), false);
    }

    /// <summary>Render an <see cref="AssetKind"/> as the tool-facing label. An explicit switch, so a new kind renders its real name instead of being mislabelled.</summary>
    static string KindLabel(AssetKind k) => k switch { AssetKind.Bsa => "BSA", AssetKind.Loose => "loose", var other => other.ToString() };

    // ---- NIF layer: whitelisted writes into a mesh (housecarl_nif_set) ----

    /// <summary>Apply the whitelisted write ops to a mesh: resolve to the winning copy (or
    /// <paramref name="sourceProvider"/>'s), read the bytes in process, hand them to <see cref="NifService.Set"/>,
    /// which verifies or refuses loudly, then place the verified bytes. Two lanes, mirroring the record writes: a
    /// loose override in a houseCARL folder, or IN-PLACE behind the same consent handshake with no backup. Serialized
    /// on the write gate, and "wrote it" is never "it wins" (docs/architecture/assets.md).</summary>
    public NifSetResult NifSet(string relPath, IReadOnlyList<NifSetOp> ops, string? sourceProvider, string? patchName, string? into, bool inPlace, bool acknowledge)
    {
        var rel = (relPath ?? "").Trim();
        if (rel.Length == 0) return NifSetResult.Fail("no mesh path given. Pass a Data-relative path, e.g. 'meshes\\armor\\iron\\cuirass_1.nif'.");
        if (ops is null || ops.Count == 0) return NifSetResult.Fail("no write op given — pass at least one op (e.g. set_flags, rename_shape).");
        if (inPlace && !string.IsNullOrWhiteSpace(into))
            return NifSetResult.Fail("in_place and into are mutually exclusive — in_place overwrites the winning file where it sits; into= names a NEW houseCARL folder.");

        // Lock order is the write gate, then the capture's hold; contract in docs/architecture/load-order-service.md.
        lock (Host.WriteGate)
        {
            AssetCapture captured;
            try { captured = Host.CaptureAssets(); }
            catch (Exception ex) { return NifSetResult.Fail($"could not resolve the asset layer (the MO2 instance may not be readable): {ex.Message}"); }

            // Every answer built off the view carries the roots it could not read, so no refusal arm has to remember them.
            return NifSetOn(captured.View, captured.Warnings, captured.ProfileName, rel, ops, sourceProvider, patchName, into, inPlace, acknowledge)
                   with { RootFailures = captured.View.RootFailures };
        }
    }

    /// <summary>nif_set once the asset view is captured: resolve, pick, apply, verify and write. Caller holds the write gate.</summary>
    NifSetResult NifSetOn(AssetResolver.AssetView view, IReadOnlyList<string> warnings, string profileName, string rel,
                          IReadOnlyList<NifSetOp> ops, string? sourceProvider, string? patchName, string? into,
                          bool inPlace, bool acknowledge)
    {
        PlacementResolution place;
        try { place = view.ResolveForPlacement(rel); }
        catch (ArgumentException ex) { return NifSetResult.Fail($"invalid path — {ex.Message}"); }

        var providers = place.Sources.Select(s => new NifProvider(s.ProviderName, KindLabel(s.Kind))).ToList();
        // An ABSENT over a build that did not fully read may merely be unscanned; nif_inspect hedges its own per mesh.
        string HedgedAbsent(string absent)
            => place.ReadIncomplete ? absent + " " + WriteSentences.PlaceSourceScanIncomplete : absent;

        // Pick the copy to read/edit: the VFS winner, or source_provider='s, answered ahead of the ABSENT return.
        PlacementSource chosen;
        if (!string.IsNullOrWhiteSpace(sourceProvider))
        {
            var pick = NifPick(view, place, rel, sourceProvider!.Trim());
            if (pick.Error is not null)
                return NifSetResult.Fail(pick.Absent ? HedgedAbsent(pick.Error) : pick.Error, providers, profileName);
            chosen = pick.Source!;
        }
        else
        {
            if (place.Sources.Count == 0)
            {
                var hint = AssetPathHint.MeshHint(view, rel);   // same verified re-resolve as nif_inspect's ABSENT
                return NifSetResult.Fail(HedgedAbsent(
                    $"ABSENT — no active mod or BSA provides '{rel}', so there is no copy to edit." + (hint is null ? "" : " " + hint)),
                    providers, profileName);
            }
            chosen = place.Sources[0];
        }

        var (bytes, readErr) = AssetResolver.ReadPlacementSource(chosen);
        if (bytes is null) return NifSetResult.Fail(readErr ?? "could not read the resolved mesh bytes.", providers, profileName);

        // ---- apply and verify (pure; nothing is written unless this returns verified bytes) ----
        var outcome = NifService.Set(bytes, ops);
        if (outcome.Error is not null) return NifSetResult.Fail(outcome.Error, providers, profileName);
        var editedBytes = outcome.WrittenBytes!;
        var report = outcome.Report!;
        var chosenProv = NifProviderFor(chosen);
        // Whether the edited copy is the VFS winner or a source_provider=-named loser. Drives the "is it live" wording.
        bool editedIsWinner = place.Sources.Count > 0 && ReferenceEquals(chosen, place.Sources[0]);

        // ---- IN-PLACE lane ----
        if (inPlace)
        {
            // The lane overwrites the WINNING file with no backup, and its handshake is written about that file — so a copy the game is not loading is declined.
            if (chosen.OffOrder)
                return NifSetResult.Fail(
                    $"in-place edits the copy the game loads, but '{chosen.ProviderName}' supplied one it does not "
                    + "(see the provenance note on a read). Drop in_place to write the edited mesh into a new houseCARL "
                    + "folder instead (the default lane).", providers, profileName);
            if (chosen.Kind != AssetKind.Loose || string.IsNullOrEmpty(chosen.LooseFilePath))
                return NifSetResult.Fail(
                    $"in-place needs a LOOSE copy to overwrite, but '{rel}' resolves to {chosen.ProviderName} ({KindLabel(chosen.Kind)}). " +
                    "Drop in_place to write a loose winning override into a new houseCARL folder instead (the default lane).", providers, profileName);
            var targetPath = chosen.LooseFilePath!;
            var meshName = Path.GetFileName(targetPath);

            // The acknowledgement is recorded only once the overwrite has landed and verified, so neither the pre-flight nor a failed write spends the caller's one-time confirmation.
            bool already = Host.IsInPlaceAcknowledged(targetPath);
            if (!already && !acknowledge)
                return NifSetResult.NeedsAck(NifInPlaceHandshakeText(meshName, targetPath), chosenProv, providers, profileName);
            bool owesConsent = !already && acknowledge;

            if (InPlaceParentUnwritable(targetPath, out var why)) return NifSetResult.Fail(why, providers, profileName);
            try { AtomicFile.WriteAllBytes(targetPath, editedBytes); }
            catch (Exception ex) { return NifSetResult.Fail($"could not overwrite '{targetPath}' in place: {ex.Message}. Nothing was written.", providers, profileName); }
            long sz; try { sz = new FileInfo(targetPath).Length; } catch { sz = -1; }
            if (sz != editedBytes.Length)
                return NifSetResult.Fail($"wrote '{meshName}' but its on-disk size ({sz}) does not match the {editedBytes.Length} verified byte(s) — verify before relying on it.", providers, profileName);

            var ackNote = Host.PersistInPlaceConsent(owesConsent, targetPath, "edit", subject: "file");
            return NifSetResult.OkInPlace(rel, chosenProv, providers, place.Ambiguous, editedIsWinner, report, targetPath,
                MergeWarnings(report.Warnings, warnings, ackNote), profileName);
        }

        // ---- DEFAULT (new-folder) lane ----
        RiderFolder rf;
        try { rf = Host.ResolvePatchModFolder(patchName, into, "houseCARL_NifEdit", new RiderNaming("patch")); }
        catch (InvalidOperationException ex) { return NifSetResult.Fail(ex.Message, providers, profileName); }

        var dest = Path.Combine(rf.OutputDir, rel);
        try { Directory.CreateDirectory(Path.GetDirectoryName(dest)!); AtomicFile.WriteAllBytes(dest, editedBytes); }
        catch (Exception ex)
        {
            var residue = Host.RemoveOrNameRiderResidue(rf);
            return NifSetResult.Fail($"could not write '{rel}' into the patch folder: {ex.Message}"
                + (residue is null ? "" : $" The freshly created mod folder was left at '{residue}'."), providers, profileName);
        }
        long size; try { size = new FileInfo(dest).Length; } catch { size = -1; }
        if (size != editedBytes.Length)
        {
            Host.RemoveOrNameRiderResidue(rf);
            return NifSetResult.Fail($"wrote '{rel}' but its on-disk size ({size}) does not match the {editedBytes.Length} verified byte(s) — verify before relying on it.", providers, profileName);
        }

        string? winner = providers.Count > 0 ? providers[0].Text : null;
        // MO2's overwrite folder is the TOP loose root, so no mod folder out-ranks it and no left-pane sort reaches it.
        var winSrc = place.Sources.Count > 0 ? place.Sources[0] : null;
        bool winnerIsOverwrite = winSrc is { Kind: AssetKind.Loose }
            && string.Equals(winSrc.ProviderName, AssetResolver.OverwriteLayerName, StringComparison.OrdinalIgnoreCase);
        // The folder just written into is already the winner — an into= re-edit. Sorting it above itself is not an instruction.
        bool winnerIsDestination = winSrc is not null
            && string.Equals(winSrc.ProviderName, Path.GetFileName(rf.ModFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                             StringComparison.OrdinalIgnoreCase);
        // A BSA or the game's Data folder loses to any enabled mod's loose copy, at any priority.
        bool winnerLosesOnEnable = winSrc is not null && !winnerIsDestination
            && (winSrc.Kind == AssetKind.Bsa
                || string.Equals(winSrc.ProviderName, AssetResolver.DataLayerName, StringComparison.OrdinalIgnoreCase));
        return NifSetResult.OkNewFolder(rel, chosenProv, providers, place.Ambiguous, report, rf.ModFolder, rf.CreatedFresh, winner, MergeWarnings(report.Warnings, warnings, null), profileName)
            with { WinnerIsOverwrite = winnerIsOverwrite, WinnerIsDestination = winnerIsDestination,
                   WinnerLosesOnEnable = winnerLosesOnEnable };
    }

    /// <summary>Merge the write report's notes with the asset-layer warnings and an optional extra, so a disclosure cannot vanish by riding the other lane's list.</summary>
    static IReadOnlyList<string> MergeWarnings(IReadOnlyList<string> reportWarnings, IReadOnlyList<string> assetWarnings, string? extra)
    {
        var list = new List<string>(reportWarnings.Count + assetWarnings.Count + 1);
        list.AddRange(reportWarnings);
        list.AddRange(assetWarnings);
        if (extra is not null) list.Add(extra);
        return list;
    }

    /// <summary>The mesh-specific in-place consent prompt: it shares its lead with the plugin handshake and diverges after it, because a mesh write is a whole-file re-serialization.</summary>
    static string NifInPlaceHandshakeText(string meshName, string path) =>
        InPlaceHandshakeLead(meshName, path, "mesh", "overwrites") +
        "  • The written mesh is a WHOLE-FILE re-serialization through NiflySharp's canonical writer (the way NifSkope / BodySlide rewrite a mesh on save), NOT a byte-surgical patch — then VERIFIED (only the value you edited changed; it reloads as a valid SE mesh).\n" +
        "  • It still refuses if the mesh can't be parsed or isn't a Skyrim SE stream.\n" +
        "  • The default lane (a NEW mod folder, originals untouched) stays the recommended way — this is the explicit opt-in.\n" +
        "Re-call the SAME edit with acknowledge=true to proceed.";

    // ---- place assets so the correct copies win the VFS (housecarl_place) ----

    /// <summary>Place one or more assets into a houseCARL-owned MO2 mod folder so the correct copy can win the VFS:
    /// resolve each request's providers (auto-resolving only a sole provider), read the bytes in process, and write
    /// them crash-atomically. Originals untouched; "wrote it" is not "it wins" (docs/architecture/assets.md).</summary>
    public PlaceOutcome PlaceAssets(IReadOnlyList<PlaceRequest> requests, string? patchName, string? into)
    {
        if (requests is null || requests.Count == 0) return PlaceOutcome.Fail("no assets to place.");

        lock (Host.WriteGate)                                             // one placement batch at a time: resolve, stage, commit
        {
            // Precondition: the write gate is held for the WHOLE method, which straddles two gate holds. Do not call PlaceOne or capture assets outside that hold.
            RiderFolder rf;
            try { rf = Host.ResolvePatchModFolder(patchName, into, "houseCARL_Assets", new RiderNaming("patch")); }   // neutral default stem; a caller with a better name passes patch
            catch (InvalidOperationException ex) { return PlaceOutcome.Fail(ex.Message); }

            // One asset build for the whole batch, captured rather than live, so no two placements describe two builds.
            AssetCapture captured;
            try { captured = Host.CaptureAssets(); }
            catch (Exception ex)
            {
                var residue = Host.RemoveOrNameRiderResidue(rf);              // nothing placed yet → a fresh folder is an orphan
                return PlaceOutcome.Fail($"could not resolve the asset layer (the MO2 instance may not be readable): {ex.Message}"
                    + (residue is null ? "" : $" The freshly created mod folder was left at '{residue}'."));
            }

            var view = captured.View;
            var results = new List<PlaceResult>(requests.Count);
            int placed = 0;
            foreach (var req in requests)
            {
                var r = PlaceOne(req, view, captured.ModsRootOrNull, rf.OutputDir);
                results.Add(r);
                if (r.Placed) placed++;
            }

            // Nothing placed into a fresh folder means an orphan to remove; a reused into= folder is never touched.
            string? leftover = placed == 0 ? Host.RemoveOrNameRiderResidue(rf) : null;
            // Taken AFTER the rows, because the view names a root only once a lookup has asked about it.
            return new PlaceOutcome(results, placed > 0 ? rf.ModFolder : null, captured.Warnings, leftover, null)
                { FreshFolder = rf.CreatedFresh, RootFailures = view.RootFailures };
        }
    }

    /// <summary>The refusal for a <c>source=</c> that reaches into MO2's mods tree, or null. An archive-plus-entry pair
    /// is judged on the archive's path and keeps its entry in the remedy, as a both-slots member keeps the archive.</summary>
    static string? RawModsSourceRefusal(string source, bool bothSlots, string? modsRoot)
    {
        var v = source.Trim().Trim('"');
        int pipe = v.IndexOf('|');
        if (ModsPathAddress.Split(pipe >= 0 ? v.Substring(0, pipe) : v, modsRoot) is not { } hit) return null;
        var remedy = bothSlots
            ? $"Address that archive instead with source_provider='{Path.GetFileName(hit.RelPath ?? v)}' and no source= — a BSA filename is a provider name, and each FaceGen slot then derives its own entry from that archive."
            : ModsPathAddress.Address(hit.ModFolder, hit.RelPath is null || pipe < 0 ? hit.RelPath : hit.RelPath + v.Substring(pipe),
                                      "source", "source_provider");
        return ModsPathAddress.Refusal("", v, remedy);
    }

    /// <summary>Place one asset: validate the destination rel-path, get the source bytes, and write them atomically
    /// under <paramref name="outDir"/>. Reports the CURRENT winner, because the placed file does not win until enabled.
    /// <paramref name="modsRoot"/> is from the same capture as <paramref name="view"/>.</summary>
    PlaceResult PlaceOne(PlaceRequest req, AssetResolver.AssetView view, string? modsRoot, string outDir)
    {
        string rel;
        try { rel = AssetResolver.ValidateRelPath(req.AssetPath); }
        catch (ArgumentException ex) { return PlaceResult.Fail(req.AssetPath, ex.Message); }

        var res = view.ResolveForPlacement(rel);                         // rel already validated — won't throw
        var winnerSrc = res.Sources.Count > 0 ? res.Sources[0] : null;
        var winner = winnerSrc is null ? null : DescribeSource(winnerSrc);
        bool winnerIsOverwrite = winnerSrc is { Kind: AssetKind.Loose }
            && string.Equals(winnerSrc.ProviderName, AssetResolver.OverwriteLayerName, StringComparison.OrdinalIgnoreCase);
        // The destination folder is ALREADY the winner — a re-place. Sorting it above itself is not an instruction.
        bool winnerIsDestination = winnerSrc is not null
            && string.Equals(winnerSrc.ProviderName, Path.GetFileName(outDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                             StringComparison.OrdinalIgnoreCase);
        // The other end of the root list: a placed loose copy beats a BSA or Data on enable, at any mod priority.
        bool winnerLosesOnEnable = winnerSrc is not null && !winnerIsDestination
            && (winnerSrc.Kind == AssetKind.Bsa
                || string.Equals(winnerSrc.ProviderName, AssetResolver.DataLayerName, StringComparison.OrdinalIgnoreCase));

        // ---- source bytes: three shapes reach here — an on-disk file named exactly, a Data-relative path resolved
        //      through the VFS under a pole, and no source at all, which is that same lane aimed at the destination ----
        byte[] bytes; string sourceDesc;
        // The mod folder an off-order read was served from, typed rather than baked into sourceDesc.
        string? offOrderProvider = null;
        bool offOrderOwnerEnabled = false;                 // WHICH off-order reason — an unticked mod, or a ticked mod's unloaded archive
        // One normalization point for source=, ahead of the classification and every consumer, or a quoted Data-relative source classifies one way and is read another.
        var explicitSrc = NormalizeSourceArg(req.Source);
        var providerSel = req.SourceProvider?.Trim();
        if (!string.IsNullOrEmpty(explicitSrc) && !IsVfsSource(explicitSrc!))
        {
            // A raw path into the mods tree reads past the VFS, judged on the ARCHIVE's own path for a pair source.
            if (RawModsSourceRefusal(explicitSrc!, req.BothSlots, modsRoot) is { } rawErr)
                return PlaceResult.Fail(rel, rawErr, winner);
            // An on-disk source already IS one exact copy, so a pole cannot apply to it — said, never dropped.
            if (!string.IsNullOrEmpty(providerSel))
                return PlaceResult.Fail(rel, WriteSentences.PlaceSourceProviderNeedsRelPath, winner);
            var (b, desc, err) = ReadExplicitSource(explicitSrc!, rel);
            if (err is not null) return PlaceResult.Fail(rel, err, winner);
            bytes = b!; sourceDesc = desc!;
        }
        else
        {
            // The SOURCE path: the Data-relative one the caller named, else the destination (the original lane).
            bool sourceNamed = !string.IsNullOrEmpty(explicitSrc);
            string srcRel = rel;
            if (sourceNamed)
            {
                try { srcRel = AssetResolver.ValidateRelPath(explicitSrc!); }
                catch (ArgumentException ex) { return PlaceResult.Fail(rel, $"source '{explicitSrc}': {ex.Message}", winner); }
            }
            var srcRes = sourceNamed ? view.ResolveForPlacement(srcRel) : res;
            // The off-order lane goes through the one source policy rather than being spelled here.
            var choice = AssetSourceChoice.Parse(providerSel);
            var pick = AssetSourceSelection.Select(srcRes, choice,
                                                   n => view.TryResolveOffOrderProvider(n, srcRel));

            // Both named-provider misses render as one refusal. Gated on the POLE, not on a passed string: '*winner' parses to the winner pole.
            if (choice.Pole == AssetSourcePole.Named
                && pick.Verdict is AssetSourceVerdict.NamedAbsent or AssetSourceVerdict.NoProvider)
                return PlaceResult.Fail(rel, WriteSentences.PlaceSourceNamedAbsent(
                    providerSel!, srcRel, pick.ProviderNames,
                    pick.OffOrderReason, pick.OffOrderUnreadableName, pick.OffOrderUnreadableCause,
                    // The root-prefix hint, verified before it is offered, like every other site that shows it.
                    AssetPathHint.AssetRootHint(view, srcRel),
                    // srcRes, not res: the caveat has to describe the scan that answered for the SOURCE path.
                    srcRes.ReadIncomplete), winner);
            if (pick.Verdict == AssetSourceVerdict.Ambiguous)
                return PlaceResult.Fail(rel, WriteSentences.PlaceSourceAmbiguous(srcRel, pick.ProviderNames), winner);
            if (pick.Verdict == AssetSourceVerdict.NoProvider && sourceNamed)
                return PlaceResult.Fail(rel,
                    $"nothing in the active load order provides the source '{srcRel}'."
                    + (AssetPathHint.AssetRootHint(view, srcRel) is { } srcHint ? " " + srcHint : "")
                    + (srcRes.ReadIncomplete ? " " + WriteSentences.PlaceSourceScanIncomplete : "")
                    // The other dead end: a Data-relative source= with no source_provider=, given the same route out.
                    + " " + WriteSentences.PlaceSourceNameReachesUnticked,
                    winner);
            if (pick.Verdict == AssetSourceVerdict.NoProvider)
            {
                // A path off a record is relative to its root folder, so passing it verbatim is the normal way here. Not offered on the explicit-source= arm.
                var hint = AssetPathHint.AssetRootHint(view, rel);
                // Order is load-bearing: the hint names a destination, not a source, so it must not trail an imperative about sources.
                return PlaceResult.Fail(rel,
                    $"nothing in the active load order provides '{rel}', so there is no copy to auto-place."
                    + (hint is null ? "" : " " + hint)
                    + " Pass source= the copy to place — a Data-relative path (resolved through the VFS, with"
                    + " source_provider= to name which mod's copy), a full loose path, '<archive.bsa>|<entry>', or a '.bsa' path."
                    // The commonest way here is that the only copy lives in a mod MO2 does not load, and naming a mod reaches it.
                    + " " + WriteSentences.PlaceSourceNameReachesUnticked
                    + (res.ReadIncomplete ? " " + WriteSentences.PlaceSourceScanIncomplete : ""),
                    winner);
            }
            var (b, desc, err) = ReadResolvedSource(pick.Source!);
            if (err is not null) return PlaceResult.Fail(rel, err, winner);
            bytes = b!;
            if (pick.Source!.OffOrder) { offOrderProvider = pick.Source.ProviderName; offOrderOwnerEnabled = pick.Source.OwnerEnabled; }
            // A source read from a different path than the destination is a RENAME and the render has to say so; keyed on the paths differing, not on a source being named.
            sourceDesc = sourceNamed && !SameAssetPath(srcRel, rel) ? $"{srcRel} — {desc}" : desc!;
        }

        // ---- crash-atomic place under the owned folder (originals untouched; same-volume staging done in the core) ----
        var dest = Path.Combine(outDir, rel);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            AtomicFile.WriteAllBytes(dest, bytes);
        }
        catch (Exception ex) { return PlaceResult.Fail(rel, $"could not write '{rel}' into the patch folder: {ex.Message}", winner); }

        // ---- integrity: truncation / short-write detection, not a content hash — the swap is atomic ----
        long size; try { size = new FileInfo(dest).Length; } catch { size = -1; }
        if (size != bytes.Length)
            return PlaceResult.Fail(rel,
                $"wrote '{rel}' but its on-disk size ({size}) does not match the {bytes.Length} source byte(s) — verify before relying on it.", winner);
        return new PlaceResult(rel, true, bytes.Length, sourceDesc, winner, null)
            { SourceOffOrderProvider = offOrderProvider, SourceOffOrderOwnerEnabled = offOrderOwnerEnabled,
              WinnerIsOverwrite = winnerIsOverwrite, WinnerIsDestination = winnerIsDestination,
              WinnerLosesOnEnable = winnerLosesOnEnable };
    }

    /// <summary>Read an ON-DISK source= the caller named exactly: an archive-plus-entry pair, a '.bsa' path (the entry
    /// is the destination rel-path), or a fully-qualified loose path. A Data-relative source never reaches here.</summary>
    static (byte[]? bytes, string? desc, string? error) ReadExplicitSource(string source, string destRel)
    {
        // The whole-string trim happened in NormalizeSourceArg; the per-part trims stay, because each side of a pair can carry its own quotes.
        int bar = source.IndexOf('|');
        if (bar >= 0)
            return ReadBsaEntry(source[..bar].Trim().Trim('"'), source[(bar + 1)..].Trim().Trim('"'));
        if (source.EndsWith(".bsa", StringComparison.OrdinalIgnoreCase))
            return ReadBsaEntry(source, destRel);                        // .bsa with no explicit entry → entry := the destination path
        string path;
        try { path = Path.GetFullPath(source); }
        catch (Exception ex) { return (null, null, $"source '{source}' is not a usable path: {ex.Message}"); }
        if (!File.Exists(path)) return (null, null, $"source file not found: '{path}'.");
        try { return (File.ReadAllBytes(path), $"loose file {path}", null); }
        catch (Exception ex) { return (null, null, $"could not read source file '{path}': {ex.Message}"); }
    }

    /// <summary>Read the bytes of an AUTO-resolved provider: loose off disk, or a BSA's single entry natively.</summary>
    static (byte[]? bytes, string? desc, string? error) ReadResolvedSource(PlacementSource s)
    {
        if (s.Kind == AssetKind.Loose)
        {
            var p = s.LooseFilePath!;
            if (!File.Exists(p)) return (null, null, $"the resolved loose source '{p}' is no longer on disk.");
            try { return (File.ReadAllBytes(p), $"loose file {p} (from {s.ProviderName})", null); }
            catch (Exception ex) { return (null, null, $"could not read resolved source '{p}': {ex.Message}"); }
        }
        return ReadBsaEntry(s.ArchivePath!, s.EntryPath);
    }

    /// <summary>Read one entry out of a BSA natively, with named errors for a missing archive, a missing entry, or an unreadable archive.</summary>
    static (byte[]? bytes, string? desc, string? error) ReadBsaEntry(string archive, string entry)
    {
        string ap;
        try { ap = Path.GetFullPath(archive.Trim('"')); }
        catch (Exception ex) { return (null, null, $"source archive '{archive}' is not a usable path: {ex.Message}"); }
        if (!File.Exists(ap)) return (null, null, $"source archive not found: '{ap}'.");
        try
        {
            var b = AssetResolver.TryReadArchiveEntry(ap, entry);
            if (b is null) return (null, null, $"entry '{entry}' not found inside archive '{Path.GetFileName(ap)}'.");
            return (b, $"{Path.GetFileName(ap)}|{entry}", null);
        }
        catch (Exception ex) { return (null, null, $"could not read archive '{Path.GetFileName(ap)}': {ex.Message}"); }
    }

    /// <summary>A human label for the current winner (the sort target). "ModX (loose)" / "Y.bsa (BSA)".</summary>
    static string DescribeSource(PlacementSource s) => $"{s.ProviderName} ({(s.Kind == AssetKind.Bsa ? "BSA" : "loose")})";

    /// <summary>Do two Data-relative paths name the SAME asset, by the key the VFS resolves on? Both are already validated, so the remaining axis is CASE.</summary>
    static bool SameAssetPath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The ONE normalization of a caller's source= — trim, then strip surrounding quotes — applied ahead of the routing decision and every consumer.</summary>
    static string? NormalizeSourceArg(string? source)
    {
        var s = source?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        s = s.Trim('"').Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    /// <summary>Whether a <c>source=</c> is one a provider pole can apply to: nothing named, or a Data-relative path.
    /// False for an on-disk file, so a CALL-LEVEL pole must not be attached to such a member at all.</summary>
    internal static bool SourceTakesAProvider(string? source)
    {
        var s = NormalizeSourceArg(source);
        return string.IsNullOrEmpty(s) || IsVfsSource(s!);
    }

    /// <summary>Does this source= name a copy through the VFS rather than one exact file on disk? Fully qualified, not
    /// merely rooted, and the qualified test runs BEFORE the extension test, since a mod may ship a .bsa as an asset.</summary>
    static bool IsVfsSource(string source)
    {
        if (source.IndexOf('|') >= 0) return false;                      // '<archive.bsa>|<entry>' — an entry, not a path
        if (!Path.IsPathFullyQualified(source)) return true;             // Data-relative ⇒ the VFS answers, whatever it ends in
        return false;                                                    // a volume or UNC path ⇒ one exact file (or archive) on disk
    }

}

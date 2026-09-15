using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcp;

public sealed partial class LoadOrderService
{
    /// <summary>Resolve a batch of Data-relative asset paths through the MO2 VFS (housecarl_asset_status): for each,
    /// which source provides it and which copy wins (loose beats BSA; among BSAs the higher plugin rank). One
    /// <see cref="AssetResolver.Capture"/> for the whole batch, so every path and the build-level BsaFailures /
    /// ReadIncomplete caveat describe a single build. A drive-rooted or '..'-escaping path is a per-path recoverable
    /// error, never a batch failure.
    /// <para><paramref name="under"/> is the directory / glob SELECT form (#246): each selector names a Data-relative
    /// folder, or a glob anchored under one, and contributes every path the VFS provides beneath it
    /// (<see cref="AssetGlob"/>). Its matches follow the explicit paths, sorted, with anything already named dropped.
    /// <paramref name="limit"/> and <paramref name="offset"/> window the SELECTION, so only the window is RESOLVED —
    /// the per-path winner and provider chain, which is the expensive half. The selector ENUMERATION is not memoized:
    /// each page re-walks the loose roots and re-scans the archive tables under the prefix, so a paged sweep pays the
    /// enumeration once per page and the resolution once per rendered path.</para></summary>
    public AssetStatusData AssetStatus(
        IReadOnlyList<string> relPaths,
        IReadOnlyList<string>? under = null,
        int limit = 0,
        int offset = 0)
    {
        lock (_gate)
        {
            var view = Assets.Capture();                          // reentrant gate; build/refresh the asset resolver once for the batch
            var notes = new List<string>();
            var selected = new List<string>(relPaths);            // explicit paths first, in the order given, never deduped
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in relPaths)
                try { seen.Add(AssetResolver.ValidateRelPath((p ?? "").Trim())); } catch (ArgumentException) { /* a bad explicit path answers per-path below */ }

            foreach (var raw in under ?? Array.Empty<string>())
            {
                var sel = (raw ?? "").Trim();
                if (sel.Length == 0) { notes.Add("under: an empty selector was skipped — pass a Data-relative directory or glob."); continue; }
                try
                {
                    var matched = AssetGlob.Select(view, sel, out var namedOneFile);
                    // A selector that named a FILE is said out loud too, so the sweep's own count is explained.
                    if (namedOneFile)
                        notes.Add($"under '{sel}' names a file, not a directory — it was resolved as that one path.");
                    // A selector that matched nothing is said out loud: read as a silent no-op it looks identical to a
                    // folder no enabled mod provides, and a typo would then read as a clean sweep.
                    else if (matched.Count == 0)
                        notes.Add($"under '{sel}' matched no file in the active load order — check the spelling, or nothing enabled provides that folder.");
                    foreach (var m in matched) if (seen.Add(m)) selected.Add(m);
                }
                catch (ArgumentException ex) { notes.Add($"under '{sel}': {ex.Message}"); }
            }

            // Page over the SELECTED set and resolve only the window, so a 15k-file sweep pays for what it renders.
            var total = selected.Count;
            var start = Math.Min(Math.Max(offset, 0), total);
            var window = selected.Skip(start).Take(limit > 0 ? limit : int.MaxValue).ToList();

            var results = new List<AssetPathResult>(window.Count);
            foreach (var raw in window)
            {
                var p = (raw ?? "").Trim();
                try
                {
                    var hit = view.Resolve(p);
                    // Only on ABSENT: a path taken off a record is stored relative to its root folder (a model path
                    // to meshes\, a texture path to textures\). Both roots are tried because this lane, unlike
                    // nif_inspect, doesn't know the path's kind, and only VERIFIED prefixes are suggested — this tool
                    // legitimately answers for sound\, scripts\, interface\ and the rest.
                    var suggest = hit.Exists ? Array.Empty<string>()
                                             : AssetPathHint.VerifiedPrefixes(view, p, AssetPathHint.AssetRoots);
                    results.Add(new AssetPathResult(p, hit, null, suggest));
                }
                catch (ArgumentException ex) { results.Add(new AssetPathResult(p, null, ex.Message)); }   // bad path → per-path note, never a batch failure
            }
            return new AssetStatusData(results, view.BsaFailures, view.ReadIncomplete, _assetWarnings, _profileName,
                                       notes, total, Math.Max(offset, 0),    // the offset ASKED for, so a past-the-end page can say so
                                       Math.Max(limit, 0));                  // the limit ASKED for, so the next-page advice repeats it
        }
    }

    // ---- SKSE-plugin-layer visibility: inventory the DLLs, configs and winning provider, plus each plugin DLL's
    //      statically declared manifest. Read-only; reuses the asset VFS and the PE reader. ----

    /// <summary>Inventory the SKSE-plugin layer as the active load order resolves it: the full depth of
    /// Data\SKSE\Plugins — every <c>.dll</c> and every <c>.ini</c>/<c>.toml</c>/<c>.json</c>/<c>.yaml</c> config at any
    /// depth — with the mod that wins the VFS for each, and for every DLL the statically declared manifest via
    /// <see cref="SksePluginReader"/>. Every file is accounted for: configs carry their derived subfolder
    /// <see cref="SkseFileEntry.Group"/> (whatever the modlist ships, never a hardcoded framework list) and non-config
    /// content is counted in <see cref="SkseInventoryData.OtherFileCount"/> rather than dropped. A subfolder DLL is
    /// listed but flagged: SKSE scans Data\SKSE\Plugins\*.dll top-level only, so it is not loaded as a plugin. One
    /// asset capture pins the whole scan, and the enumerate, resolve and PE reads run outside the gate (the captured
    /// view is a handle-free immutable snapshot) so an inventory never serializes other tool calls behind its file
    /// I/O. Distributor INIs (SPID <c>*_DISTR</c>, KID <c>*_KID</c>) live in the Data root, not here.</summary>
    /// <param name="peekFilter">When non-null, every DLL entry matching it (<see cref="SkseFileEntry.MatchesDll"/>,
    /// the same predicate the renderer filters on) also gets its image string-scanned into
    /// <see cref="SkseFileEntry.Peek"/>. Null = no scan. Per-DLL because the scan reads the whole image, unlike the
    /// import walk, which rides the manifest read every DLL already gets.</param>
    public SkseInventoryData SkseInventory(string? peekFilter = null)
    {
        AssetResolver.AssetView view;
        IReadOnlyList<string> warnings;
        string profileName, profileDir;
        lock (_gate)
        {
            EnsurePathsDerived();
            view = Assets.Capture();                              // build/refresh the asset resolver under the gate, ONCE
            warnings = _assetWarnings;
            profileName = _profileName;
            profileDir = _profileDir;
        }
        // The plugin names a peek's embedded-reference cross-check adjudicates against. A cheap three-file text parse
        // with no index build, skipped entirely without peek= so a normal inventory pays nothing for it. The set is
        // what the game actually loads: plugins.txt `*` entries plus the force-loaded base and CC masters, which load
        // despite never appearing there — omitting the implicit ones would flag Dawnguard.esm absent on an install
        // that has it.
        IReadOnlySet<string>? activePlugins = null;
        if (peekFilter is { Length: > 0 })
        {
            var compWarnings = new List<string>();
            activePlugins = PeekPluginSet(Mo2LoadOrder.ReadComposition(profileDir, compWarnings));
            if (compWarnings.Count > 0) warnings = [.. warnings, .. compWarnings];
        }
        // Outside the gate: the view is pinned and handle-free (Resolve reads only the captured snapshot and readonly
        // roots), so enumerating, resolving and PE-reading here cannot race a concurrent refresh into wrongness and
        // does not block other tools behind these file reads.
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
            // The full conflict chain, winner-first: every provider and its loose/BSA kind, not just a count.
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
                    // What MO2 recorded for the mod that ships it, read once per mod: a mod shipping several DLLs
                    // (an AIO) would otherwise re-read the same meta.ini for each of them. Carried only for a DLL that
                    // IS an SKSE plugin: a mod's version describes the plugin it ships, not the redistributable
                    // (tbb.dll, msdia140.dll) bundled beside it, where it would read as a false disagreement.
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
                // String peek only for a filter-matched DLL with a loose winner — the copy SKSE would load. A BSA-only
                // DLL never loads, so peeking it would describe an image the game never reads.
                if (peekFilter is { Length: > 0 } && entry.MatchesDll(peekFilter)
                    && winner is { Kind: AssetKind.Loose, LooseFilePath: { } peekPath })
                    entry = entry with { Peek = SksePeek.Scan(peekPath) };
                dlls.Add(entry);
            }
            else
                configs.Add(new SkseFileEntry(rel, Path.GetFileName(rel), group, providers, null, null));
        }
        return new SkseInventoryData(dlls, configs, otherFiles, InstalledGameRuntime(), view.BsaFailures, view.ReadIncomplete,
            warnings, profileName, activePlugins, peekFilter is { Length: > 0 });
    }

    /// <summary>Why a loose, loader-scoped SKSE plugin DLL statically cannot load, or null when nothing stops it. The
    /// blocker chain for the winning loose copy, in severity order; a non-null result rides
    /// <see cref="NativePairedDll.LoadBlocker"/>, which the pairing verdict already treats as dead.
    /// The debug-build check matters because a debug-built DLL is loose, top-level, x64, readable and usually
    /// version-independent — every other check passes it while the loader refuses it with error 126.
    /// <paramref name="resolvable"/> is injected so the chain can be tested without a live order or a live machine
    /// carrying the debug runtime.</summary>
    internal static string? LooseDllBlocker(SksePluginReader.SksePluginInfo info, Func<string, bool> resolvable)
    {
        if (info.Kind == SksePluginReader.SksePluginKind.Unreadable) return $"not a readable SKSE plugin ({info.Note})";
        if (info.Is64Bit == false) return "a 32-bit image — cannot load in Skyrim SE/AE";
        return SksePluginReader.DebugCrtBlocker(info, resolvable);
    }

    /// <summary>The plugin names a peek adjudicates an embedded reference against — active plus the force-loaded
    /// implicit masters, which load despite never appearing in plugins.txt. Returns <c>null</c>, never a partial set,
    /// when the answer is unknowable, because "the order could not be determined" and "the order is empty" must not
    /// render the same.
    /// The gate is <see cref="Mo2Composition.OrderedPluginNames"/> and that choice is load-bearing: the implicit set
    /// is derived by iterating the ordered list, so with loadorder.txt missing it collapses to empty while plugins.txt
    /// can still hand back a non-empty active set. Gating on the merged set instead would return an active-only set
    /// whose force-loaded masters are silently gone. Reachable in practice — reading the composition never throws on
    /// a missing profile file, and the three profile files are independently mutable.</summary>
    internal static IReadOnlySet<string>? PeekPluginSet(Mo2Composition comp)
    {
        if (comp.OrderedPluginNames.Count == 0) return null;   // no loadorder.txt ⇒ the implicit masters are unknowable, not absent
        var set = new HashSet<string>(comp.ActivePluginNames, StringComparer.OrdinalIgnoreCase);
        set.UnionWith(comp.ImplicitPluginNames);
        return set.Count > 0 ? set : null;
    }

    /// <summary>The immediate subfolder under SKSE\Plugins a file sits in ("" = top level) — the derived grouping key
    /// for the SKSE inventory. Whatever a modlist ships becomes a group; a hardcoded framework list would break the
    /// generated-coverage cornerstone and silently miscategorize anything not on it.
    /// e.g. <c>SKSE\Plugins\SkyPatcher\Weapons\x.ini</c> → "SkyPatcher"; <c>SKSE\Plugins\EngineFixes.toml</c> → "".</summary>
    static string SkseGroupOf(string rel, string pre)
    {
        if (!rel.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) return "";
        int slash = rel.IndexOf('\\', pre.Length);
        return slash < 0 ? "" : rel.Substring(pre.Length, slash - pre.Length);
    }

    // ---- SKSE config audit: cross-check the form references SKSE-plugin configs declare against the real records
    //      of the active load order. ----

    /// <summary>Per-file byte cap for the config scan: a config larger than this is a named skip, not fed to the token
    /// scanner. Real distributor configs are KB-scale, so 16 MB trips only on content mislabeled as a config.</summary>
    const long SkseConfigSizeCap = 16L * 1024 * 1024;

    /// <summary>Audit the SKSE-plugin config layer against the load order. For every .ini/.toml/.json/.yaml under
    /// Data\SKSE\Plugins, read the winning copy (the DLL never reads the losers), extract the form-shaped references
    /// and path-segment plugin gates it declares, and resolve each against the active order into a verdict: OK,
    /// PLUGIN MISSING, DANGLING, or UNPARSEABLE. Framework-agnostic — it never interprets what a reference is for.
    /// One asset capture and one resolver index pin the whole scan; the enumerate, read and resolve run outside the
    /// gate (the captured view is handle-free and the index a pure snapshot read). "No references found" is a normal
    /// per-file outcome, accounted for rather than warned about.</summary>
    public SkseConfigAuditData SkseConfigAudit()
    {
        AssetResolver.AssetView view;
        LoadOrderResolver.IndexView index;
        IReadOnlyList<string> warnings;
        string profileName;
        // Capture the asset view AND the record index under one gate hold, so a freshness rebuild cannot interleave
        // and pair a config read from one asset build against a record index from the next. Both are handle-free
        // snapshots, so the enumerate, read and resolve below run outside the gate.
        lock (_gate)
        {
            view = Assets.Capture();
            index = Resolver.Capture();   // pure snapshot: ContainsPlugin / ResolveWinner read only this build
            warnings = _assetWarnings;
            profileName = _profileName;
        }

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

            // Path-segment gates come from the relPath, so they surface even when the file could not be read — the
            // gate is a property of where the file lives, not its content. Only the token scan needs the text.
            var extracted = SkseConfigReferenceExtractor.Extract(rel, readError is null ? text : "");
            var audited = new List<SkseAuditedRef>(extracted.Count);
            foreach (var r in extracted) audited.Add(Adjudicate(r, index));

            files.Add(new SkseConfigFileAudit(rel, Path.GetFileName(rel), group,
                winner?.ProviderName, providers.Count, providers, audited, readError));
        }
        return new SkseConfigAuditData(files, files.Count, view.BsaFailures, view.ReadIncomplete, warnings, profileName);
    }

    /// <summary>Resolve one extracted reference into a verdict against the load-order index. A path-segment gate is
    /// plugin-presence only (OK / PLUGIN MISSING); a form token additionally checks the record exists (DANGLING when the
    /// plugin is present but the masked FormID resolves to nothing). Never speculates about runtime behavior.</summary>
    internal static SkseAuditedRef Adjudicate(SkseConfigRef r, LoadOrderResolver.IndexView index)   // internal: a test drives it over a synthetic order
    {
        if (r.Unparseable is not null)
            return new SkseAuditedRef(r, SkseRefVerdict.Unparseable, r.Unparseable);

        if (!index.ContainsPlugin(r.Plugin))
            return new SkseAuditedRef(r, SkseRefVerdict.PluginMissing, $"'{r.Plugin}' is not in the active load order");

        if (r.Shape == SkseRefShape.PathSegmentGate)
            return new SkseAuditedRef(r, SkseRefVerdict.Ok, null);   // gate satisfied — the plugin is present

        // Form token: the plugin is present; does the (masked) FormID resolve to a record in the order?
        if (!ModKey.TryFromNameAndExtension(r.Plugin, out var mk))
            return new SkseAuditedRef(r, SkseRefVerdict.Unparseable, $"'{r.Plugin}' is not a valid plugin name");
        var fk = new FormKey(mk, r.LocalId!.Value);
        return index.ResolveWinner(fk) is not null
            ? new SkseAuditedRef(r, SkseRefVerdict.Ok, FormIdToken.Of(fk))
            : new SkseAuditedRef(r, SkseRefVerdict.Dangling, $"{FormIdToken.Of(fk)} resolves to no record in '{r.Plugin}'");
    }

    /// <summary>Decode a config file's bytes to text, honoring a BOM (UTF-8/16) when present (real shipped configs carry
    /// one), defaulting to UTF-8 otherwise — the config formats (.ini/.toml/.json/.yaml) are all UTF-8 in practice.</summary>
    static string DecodeConfigText(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var sr = new StreamReader(ms, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return sr.ReadToEnd();
    }

    /// <summary>The over-size-cap skip note. The size carries one decimal so a 16.4 MB file reads "16.4 MB (> 16 MB
    /// cap)" rather than the self-contradictory "16 MB (> 16 MB cap)" an integer divide would give.</summary>
    static string OverCapNote(long len) =>
        $"config is {len / (1024.0 * 1024):0.0} MB (> {SkseConfigSizeCap / (1024 * 1024)} MB cap) — not scanned";

    // ---- Native-function pairing audit: cross-check the native Papyrus functions the order's scripts declare
    //      against the DLLs that must implement them. ----

    /// <summary>Audit the declaration-to-implementation pairing of every native Papyrus class in the active order. One
    /// pass over the winning <c>.pex</c> files extracts native-flagged declarations; one pass over SKSE\Plugins finds
    /// the DLL candidates each mod ships; then per third-party class an evidence ladder — same-mod DLL, conflict-chain
    /// DLL, or UNPAIRED (a verify flag, never "broken": registration is runtime behavior this cannot see).
    /// <para>A class whose provider chain includes an official archive (the Skyrim.ini base block or a BaseMaster-owned
    /// BSA) is ENGINE, implemented by the executable, even when a mod's loose copy wins it — SKSE overrides Actor,
    /// Game and others with native additions, and the official-archive presence still marks the class baseline. A
    /// third-party class whose winning provider also provides an ENGINE class is SKSE CORE: the skse64 scripts payload
    /// co-ships a hundred-odd vanilla overrides with its new classes, and its implementation is the game-root loader
    /// rather than anything under SKSE\Plugins. Known residual edges: an INI-injected third-party BSA reads official;
    /// a paid-CC archive is not BaseMaster-owned, so its engine-native classes read third-party and get a verify flag;
    /// and a mod co-shipping a vanilla-script override with a declaration copy of an absent framework gets its copy
    /// rescued into SKSE CORE, visible in the accounting and unflagged.</para>
    /// <para>One gate hold captures the asset view, the archive list and the warnings; the enumerate, parse and
    /// classify run outside the gate over the pinned handle-free view. The per-file Pex parses are parallelized (the
    /// view's caches are concurrency-safe) with deterministic output ordering. An unreadable .pex is a named entry,
    /// never a silent skip.</para></summary>
    public NativePairingAuditData NativePairingAudit()
    {
        AssetResolver.AssetView view;
        IReadOnlyList<ActiveArchive> archives;
        IReadOnlyList<string> enabledMods;
        IReadOnlyList<string> warnings;
        string profileName, dataDir, modsDir, overwriteDir;
        lock (_gate)
        {
            view = Assets.Capture();
            archives = _activeArchives;         // the same build as the view (both swapped under _gate)
            enabledMods = _enabledModsAtBuild;  // ditto — the loader scan below walks the mod set the view describes, never a second unpinned profile read
            warnings = _assetWarnings;
            profileName = _profileName;
            dataDir = _dataDir;
            modsDir = _modsDir;
            overwriteDir = _overwriteDir;
        }

        // ---- the official-archive set: the ENGINE anchor. Keyed by filename, because a BSA provider's name IS the archive filename. ----
        var officialArchives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var baseMasters = Mutagen.Bethesda.Plugins.Implicits.Get(Mutagen.Bethesda.GameRelease.SkyrimSE).BaseMasters;
        foreach (var a in archives)
            if (IsOfficialArchive(a, baseMasters))
                officialArchives.Add(Path.GetFileName(a.Path));

        // A BSA provider's name is the archive filename, but pairing identity needs the MOD that ships the archive: a
        // mod's scripts can ride its own BSA while its DLL sits loose in the same folder, and untranslated the ladder
        // would see two unrelated providers and call it UNPAIRED. The winning physical path of each active archive
        // names its shipper: mods\<mod>\X.bsa → that mod; overwrite\ → "overwrite"; Data → "Data".
        var archiveShipper = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in archives)
            if (LayerOfInstallPath(a.Path, modsDir, overwriteDir, dataDir) is { } shipper)
                archiveShipper[Path.GetFileName(a.Path)] = shipper;

        // ---- DLL candidates: one SKSE\Plugins pass. A mod "ships" a DLL when it appears anywhere in that file's
        //      chain, so the bundling case pairs through the chain; the health verdict describes the winning copy. A
        //      winner that PE-reads as NotSkse — loose or BSA-packed — is a bundled dependency, not an
        //      implementation candidate. ----
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
                    // PE-screen the packed copy too (DLLs are few, so the per-entry read is fine): a packed NotSkse
                    // dependency must not count as a candidate, or its mod gains pairing evidence it never earned.
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

        // ---- the .pex sweep, two phases. Phase 1 (parallel): resolve every path, parse loose winners in place, and
        //      defer BSA winners to a per-archive batch — a per-entry read re-opens the archive and walks its whole
        //      table each time, which is the dominant cost against the ten-thousand-script vanilla archives. Phase 2:
        //      one table walk per archive collects all its wanted entries, then the parses run parallel over the
        //      bytes. An unreadable .pex is a named entry, never a silent skip. ----
        var pexPaths = view.EnumerateUnder("Scripts")
            .Where(p => Path.GetExtension(p).Equals(".pex", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

        // Only native-declaring files are kept — a large order yields a couple of hundred — and their conflict chains
        // are re-resolved on collection, which is cheap at that count.
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

        // ---- classify and pair (sequential; cheap set lookups over a few hundred native files). Provenance and
        //      pairing key on the enum-typed PlacementSource, never the render label — the display string must not
        //      double as the semantic discriminator. ----
        var native = natives.OrderBy(s => s.Rel, StringComparer.OrdinalIgnoreCase)
            .Select(s => (s.Rel, s.Decls, Sources: view.ResolveForPlacement(s.Rel).Sources))
            .ToList();

        // Pass 1: the SKSE-CORE rescue pool — every non-official pairing identity shipping a copy of an ENGINE class.
        var engineProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in native)
            if (HasOfficialSource(s.Sources, officialArchives))
                foreach (var src in s.Sources)
                    if (!(src.Kind == AssetKind.Bsa && officialArchives.Contains(src.ProviderName)))
                        engineProviders.Add(PairingIdentity(src, archiveShipper));
        // "overwrite" is excluded from the rescue: a recompiled vanilla .pex in MO2's overwrite is routine (the
        // compile lane writes there), and letting it rescue every orphan declaration copy that also lands in overwrite
        // would silence the flag this tool exists for. "Data" stays — the manual game-folder SKSE install is the
        // layout the rescue must cover.
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

        // Is an skse64 loader visible at all? Two places to look: the game root (a manual install), and each enabled
        // mod's Root\ folder — the MO2 Root Builder layout, where the loader lives at mods\<mod>\Root\skse64_loader.exe
        // and only materializes in the game root at launch. The mod list is the same capture as the view. Tri-state:
        // a check that threw yields null, "could not check", never a false "checked and absent".
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
            loaderSeen, InstalledGameRuntime(),
            view.BsaFailures, view.ReadIncomplete, warnings, profileName);
    }

    /// <summary>The MO2 LAYER a physical file path belongs to — the mod folder behind a BSA provider name, and the
    /// same answer for a plugin file: mods\&lt;mod&gt;\X → that mod folder; the overwrite layer → "overwrite"; the
    /// game Data folder → "Data"; anywhere else → null (no translation).
    /// <para>The NAME only. A caller that has to say which of the three answered — the name alone cannot tell it,
    /// since a mod folder may be called "Data" — takes <see cref="InstallLayerOfPath"/> instead.</para></summary>
    internal static string? LayerOfInstallPath(string archivePath, string modsDir, string overwriteDir, string dataDir) =>
        InstallLayerOfPath(archivePath, modsDir, overwriteDir, dataDir)?.Name;

    /// <summary>The MO2 layer a physical file path belongs to, as the BRANCH that answered plus the name it
    /// produced. Null anywhere else, exactly as <see cref="LayerOfInstallPath"/>.</summary>
    internal static SourceLayer? InstallLayerOfPath(string archivePath, string modsDir, string overwriteDir, string dataDir)
    {
        // Full-path-normalize both sides so forward slashes, '..' segments or a trailing-separator root from config
        // cannot make the under-root test disagree with the rest of the plumbing.
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

    /// <summary>An archive is OFFICIAL — its scripts' natives are the engine's own — when it loads from Skyrim.ini's
    /// base [Archive] block or is owned by a base master (Mutagen's implicit list, by construction — never a name
    /// list).</summary>
    internal static bool IsOfficialArchive(ActiveArchive a, IReadOnlyList<ModKey> baseMasters) =>
        a.OwningPlugin.Equals(ArchiveDiscovery.IniArchiveOwner, StringComparison.OrdinalIgnoreCase)   // ignore-case like every other archive compare — this must not hinge on the marker's casing
        || (ModKey.TryFromNameAndExtension(a.OwningPlugin, out var mk) && baseMasters.Contains(mk));

    /// <summary>True when any source in a file's chain is an official archive — the ENGINE provenance test. Keys on
    /// the <see cref="AssetKind"/> enum plus the archive filename (a BSA source's provider name IS its archive
    /// filename), so a loose override winning the file still leaves the class baseline, and a render-label change
    /// cannot silently break it.</summary>
    internal static bool HasOfficialSource(IReadOnlyList<PlacementSource> sources, HashSet<string> officialArchives) =>
        sources.Any(s => s.Kind == AssetKind.Bsa && officialArchives.Contains(s.ProviderName));

    /// <summary>One source's PAIRING IDENTITY — the mod it means: a BSA source translates to the mod shipping the
    /// archive (via the archiveShipper map); everything else is its provider name (mod folder / overwrite / Data).</summary>
    internal static string PairingIdentity(PlacementSource src, IReadOnlyDictionary<string, string> archiveShipper) =>
        src.Kind == AssetKind.Bsa && archiveShipper.TryGetValue(src.ProviderName, out var mod) ? mod : src.ProviderName;

    /// <summary>The pairing-evidence ladder for one third-party class, over the chain's pairing identities, winner
    /// first: rung 1, the winning identity ships at least one candidate DLL; rung 2, an identity deeper in the chain
    /// does (a patch mod wins the script while the framework beneath ships the DLL); rung 3, nobody in sight does, so
    /// UNPAIRED — a verify flag. An identity whose candidates all carry a static LoadBlocker does not stop the descent
    /// when a deeper identity has a loadable candidate, so a bundler shipping one dead helper DLL cannot mask the real
    /// framework beneath it; if no identity has a loadable candidate, the shallowest with any candidate pairs and its
    /// deadness becomes the finding. "Loadable" here means no STATIC blocker — version-locked-versus-runtime deadness
    /// is adjudicated by the renderer, which owns that decision. The evidence is structural (file co-location and VFS
    /// chains), never semantic: which DLL implements which class is out of reach.</summary>
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

    /// <summary>The full per-class decision, in order: ENGINE (official-archive presence), then the pairing ladder,
    /// then the SKSE-CORE rescue for an UNPAIRED class whose winning identity also ships an ENGINE-class copy — the
    /// skse64 payload co-ships many vanilla overrides with its new classes. Pairing evidence beats the rescue: a class
    /// that pairs to a DLL stays third-party regardless of its provider's other files. Known residual: a provider that
    /// co-ships a vanilla-script override AND a declaration copy of an absent framework gets that copy rescued into
    /// the unflagged baseline, which for the game Data folder covers everything manually installed there. "overwrite"
    /// is excluded from the pool at the call site for that reason.</summary>
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

    // ---- SkyPatcher distributor: the per-record true post-SkyPatcher state. Read-only. ----

    /// <summary>Cross-call INI parse cache (FileStamp keyed — see <see cref="SkyPatcherDiscovery.ParseCache"/>):
    /// repeat post-state calls over an untouched layer skip every per-file read+parse.</summary>
    readonly SkyPatcherDiscovery.ParseCache _skyPatcherParseCache = new();

    /// <summary>The in-memory scratch mod the replay copy is overridden into — never written to disk.
    /// Named per the Housecarl* scratch-mod convention (<c>HousecarlWriteProof</c> is the sibling).</summary>
    static readonly ModKey SkyPatcherScratchKey = new("HousecarlSkyPatcherScratch", ModType.Plugin);

    /// <summary>The per-record SkyPatcher replay core, shared by the post-state read and the layer
    /// no-op (true-ITM) scan: resolve the winner, materialize a mutable scratch copy, apply every
    /// type folder's ordered lines in field-map order. Error is the named reason the record cannot be
    /// replayed; the caller decides whether that is a failure or a skip-with-count.</summary>
    (string? TypeName, string? WinnerPlugin, string? EditorId, List<SkyPatcherFolderOutcome> Folders, string? Error, IMajorRecord? Copy)
        ReplaySkyPatcher(
            LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
            SkyPatcherDiscovery.LayerScan scan, SkyPatcherCatalog catalog, SkyPatcherFieldMap fieldMap,
            SkyrimMod scratch, SkyPatcherOverlay.IFormResolver formResolver, FormKey fk,
            Dictionary<string, IReadOnlyList<SkyPatcherOverlay.OrderedLine>>? linesCache)
    {
        var none = new List<SkyPatcherFolderOutcome>();
        var winner = view.ResolveWinner(fk);
        if (winner is null)
            return (null, null, null, none, UnresolvedFormId(view, fk), null);

        var body = view.GetRecord(session, winner.Value.WinnerPlugin, fk);
        if (body is null)
            return (null, winner.Value.WinnerPlugin, null, none, $"Winner '{winner.Value.WinnerPlugin}' did not yield {FormIdToken.Of(fk)} on fetch — a load-order inconsistency.", null);

        var typeName = ReadEngine.ReadFields(body, new[] { "EditorID" }).Type;   // the same type naming every read tool reports
        var maps = fieldMap.ForRecordType(typeName);
        if (maps.Count == 0)
            return (typeName, winner.Value.WinnerPlugin, body.EditorID, none,
                $"Record type '{typeName}' is not a SkyPatcher-patchable type (or has no field map) — the SkyPatcher layer cannot touch {FormIdToken.Of(fk)}.", null);

        // The running copy: the winner overridden into an in-memory scratch mod (never written to disk).
        // Nested-group types (CELL / REFR / INFO…) need the source link cache to rebuild their parent
        // chain — the same RecordNeedsSourceCache + LinkCacheFor idiom every write path uses. Without it
        // those types throw unhandled instead of failing by name.
        IMajorRecord copy;
        try
        {
            Mutagen.Bethesda.Plugins.Cache.ILinkCache? cache =
                WriteEngine.RecordNeedsSourceCache(body) ? session.LinkCacheFor(winner.Value.WinnerPlugin) : null;
            copy = WriteEngine.GenericGetOrAddAsOverride(scratch, body, cache);
        }
        catch (Exception ex)
        {
            return (typeName, winner.Value.WinnerPlugin, body.EditorID, none,
                $"Could not materialize a mutable copy of {FormIdToken.Of(fk)} ({typeName}) for the replay — {ex.GetType().Name}: {ex.Message}", null);
        }

        // Watch this record's own EditorID lookups: only a replay that actually read from a table missing a
        // plugin's records is affected, so a record addressed purely by FormID answers normally.
        var spr = formResolver as SkyPatcherServiceResolver;
        spr?.WatchLookups();

        var folders = new List<SkyPatcherFolderOutcome>();
        foreach (var m in maps)
        {
            var folder = scan.Folders.FirstOrDefault(f => f.Subfolder.Equals(m.Subfolder, StringComparison.OrdinalIgnoreCase));
            if (folder is null || folder.Catalog is null)
            {
                folders.Add(new SkyPatcherFolderOutcome(m.Subfolder, 0, 0, null, true));
                continue;
            }
            IReadOnlyList<SkyPatcherOverlay.OrderedLine> lines;
            if (linesCache is null) lines = SkyPatcherDiscovery.OrderedLines(folder);
            else if (!linesCache.TryGetValue(folder.Subfolder, out lines!))
                linesCache[folder.Subfolder] = lines = SkyPatcherDiscovery.OrderedLines(folder);
            var result = SkyPatcherOverlay.Apply(copy, fk, body.EditorID, catalog, folder.Catalog, m, lines, formResolver);
            // A toggled-off folder contributes nothing: reporting its files as "applied" would assert as
            // live the INIs the DLL skips wholesale. Enabled rides along so the render can say why the
            // counts are zero.
            folders.Add(new SkyPatcherFolderOutcome(folder.Subfolder,
                folder.PatchingEnabled ? folder.Files.Count(f => f.NotApplied is null) : 0, lines.Count, result,
                folder.PatchingEnabled));
        }

        // An EditorID sweep that could not read a plugin leaves that plugin's EditorIDs out of the lookup table, so a
        // line naming one resolves to nothing and this replay would report a state the layer does not produce.
        // Named as the record's error rather than answered wrong.
        if (spr is { ConsumedIncompleteTable: true })
            return (typeName, winner.Value.WinnerPlugin, body.EditorID, none,
                $"the SkyPatcher replay of {FormIdToken.Of(fk)} resolved an EditorID against the load order, and "
                + string.Join(" ", spr.Unreadable.Select(u => u.Message).Distinct()), null);

        return (typeName, winner.Value.WinnerPlugin, body.EditorID, folders, null, copy);
    }

    /// <summary>Carry one replay's warnings (unknown key, unmapped op, unresolved filter, parse note) into the
    /// caller's sink, deduplicated: a line the layer could not apply belongs beside the answer, not in silence.
    /// Each warning already names its own file and line, so a draft's warnings name the draft's path.</summary>
    static void CollectOverlayWarnings(IReadOnlyList<SkyPatcherFolderOutcome> folders, SkyPatcherOverlay.WarningSink? sink)
    {
        if (sink is null) return;
        foreach (var f in folders)
            foreach (var w in f.Result?.Warnings ?? Array.Empty<string>())
                sink.Add(w);
    }

    /// <summary>
    /// Scan the whole SkyPatcher layer: every loose INI as the DLL reads it (ordered union, VFS
    /// same-path collisions surfaced, gates and toggles evaluated), plus the INI-vs-INI same-field SET
    /// collisions and the three ITM classes — intra-file dead writes, cross-INI duplicates, and the
    /// no-op writes found by the per-record replay below. Report-only. One record capture answers the
    /// filename gates and one asset capture pins the scan; the enumerate, parse and detect run outside
    /// the gate on the handle-free captured view. A layer with no INIs is a named outcome, never an
    /// empty guess.
    /// </summary>
    public SkyPatcherLayerData SkyPatcherLayer()
    {
        // No epoch is stamped: the INI layer is outside the index fingerprint, so a bare index epoch would overclaim.
        var view = Resolver.Capture();
        AssetResolver.AssetView assets;
        IReadOnlyList<string> assetWarnings;
        string profileName;
        lock (_gate)
        {
            assets = Assets.Capture();
            assetWarnings = _assetWarnings;
            profileName = _profileName;
        }

        var catalog = SkyPatcherCatalog.Load();
        var fieldMap = SkyPatcherFieldMap.Load();
        var scan = SkyPatcherDiscovery.Scan(assets, catalog, view.ContainsPlugin, _skyPatcherParseCache);
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

        // ---- the TRUE-ITM (no-op write) scan: replay every explicitly-targeted record through the
        //      same per-record core the post-state read uses, and flag SET-class ops whose before ==
        //      after — the line writes the value the record already has at that point in the replay
        //      (which handles chains: a set that restores an earlier INI's change is NOT a no-op).
        //      Broad (type-wide) lines are evaluated only against the explicitly-targeted records;
        //      replaying every record of a type is not attempted, and the note says so. Deliberate
        //      leave-unchanged values ('none') are the author's explicit choice, not flagged. ----
        var noOps = new List<SkyPatcherNoOpWrite>();
        var noOpNotes = new List<string>();
        {
            using var session = Resolver.OpenSession();
            var formResolver = new SkyPatcherServiceResolver(this, view, session);
            var scratch = new SkyrimMod(SkyPatcherScratchKey, SkyrimRelease.SkyrimSE);
            var linesCache = new Dictionary<string, IReadOnlyList<SkyPatcherOverlay.OrderedLine>>(StringComparer.OrdinalIgnoreCase);
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
                    var rfk = folderTypes.Select(t => formResolver.ResolveEditorId(eid, t)).FirstOrDefault(x => x is not null);
                    if (rfk is not null) targets.Add(rfk.Value); else unresolvedTargets++;
                }
            }
            foreach (var fk in targets)
            {
                var r = ReplaySkyPatcher(view, session, scan, catalog, fieldMap, scratch, formResolver, fk, linesCache);
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
            // Stable output: targets is a hash set, so without this the findings' order varies run to
            // run and a re-run cannot be diffed against the previous one.
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
            // Emitted AFTER the replays: a plugin can first turn out unreadable in a sweep the replay itself runs
            // (a value operand naming a donor by EditorID), not only in the target-collection sweep above.
            foreach (var msg in formResolver.Unreadable.Select(u => u.Message).Distinct())
                noOpNotes.Add($"no-op scan: {msg} Lines naming a record it defines could not be resolved.");
        }

        return new SkyPatcherLayerData(scan, conflicts, itms, duplicates, noOps, noOpNotes,
            scan.ReadIncomplete || assets.ReadIncomplete, assetWarnings, profileName);
    }

    /// <summary>The live-load-order lookups the overlay needs (<see cref="SkyPatcherOverlay.IFormResolver"/>),
    /// answered off the ONE pinned record view + open session the post-state call holds. EditorID resolution
    /// sweeps the requested type's winners once into an eid→FormKey table, because an INI layer typically names
    /// many EditorIDs of the same type and a per-eid sweep would walk the full order once each. A miss is null,
    /// reported loudly upstream, never a guess.</summary>
    sealed class SkyPatcherServiceResolver : SkyPatcherOverlay.IFormResolver
    {
        readonly LoadOrderService _svc;
        readonly LoadOrderResolver.IndexView _view;
        readonly LoadOrderResolver.OverlaySession _session;
        readonly Dictionary<string, Dictionary<string, FormKey>> _eidsByType = new(StringComparer.OrdinalIgnoreCase);
        readonly List<PluginUnreadableException> _unreadable = new();
        readonly HashSet<string> _incompleteTypes = new(StringComparer.OrdinalIgnoreCase);   // types whose sweep missed a plugin
        bool _consumedIncomplete;

        /// <summary>Plugins an EditorID sweep could not open. Their EditorIDs are absent from the table, so a miss
        /// here is not proof the name does not exist — the callers state the gap rather than let a lookup answer
        /// "no such record" on a plugin they never read.</summary>
        public IReadOnlyList<PluginUnreadableException> Unreadable => _unreadable;

        /// <summary>Whether a lookup since the last <see cref="WatchLookups"/> was answered from a table a plugin is
        /// missing from. The table is memoized across records, so the count of unreadable plugins does not grow on
        /// the second record that consults it — this flag is what tells a caller its OWN answer is affected.</summary>
        public bool ConsumedIncompleteTable => _consumedIncomplete;

        /// <summary>Start watching lookups for a single record's replay.</summary>
        public void WatchLookups() => _consumedIncomplete = false;

        public SkyPatcherServiceResolver(LoadOrderService svc, LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session)
        { _svc = svc; _view = view; _session = session; }

        public FormKey? ResolveEditorId(string editorId, string? mutagenType)
        {
            if (mutagenType is null || string.IsNullOrWhiteSpace(editorId)) return null;
            if (!_eidsByType.TryGetValue(mutagenType, out var eids))
            {
                eids = new Dictionary<string, FormKey>(StringComparer.OrdinalIgnoreCase);
                var types = _svc.ResolveFormScope(mutagenType);
                if (types is not null)
                {
                    int before = _unreadable.Count;
                    foreach (var (candidate, _, cBody) in _view.WinnerRecordsOfType(types, _unreadable))
                        if (cBody.EditorID is { Length: > 0 } eid && !eids.ContainsKey(eid))   // first winner keeps the slot
                            eids[eid] = candidate;
                    if (_unreadable.Count > before) _incompleteTypes.Add(mutagenType);
                }
                _eidsByType[mutagenType] = eids;
            }
            if (_incompleteTypes.Contains(mutagenType)) _consumedIncomplete = true;
            return eids.TryGetValue(editorId, out var fk) ? fk : null;
        }

        public string? ReadWinnerLeaf(FormKey donor, string path)
        {
            var w = _view.ResolveWinner(donor);
            if (w is null) return null;
            var rec = _view.GetRecord(_session, w.Value.WinnerPlugin, donor);
            if (rec is null) return null;
            var leaf = ReadEngine.ReadFields(rec, new[] { path }).Fields.FirstOrDefault();
            return leaf is { HasValue: true } ? leaf.Token : null;
        }

        public IReadOnlyList<FormKey>? KeywordsOf(FormKey record)
        {
            var w = _view.ResolveWinner(record);
            if (w is null) return null;
            var rec = _view.GetRecord(_session, w.Value.WinnerPlugin, record);
            return rec is null ? null : ReadEngine.KeywordKeys(rec);   // the ONE keyword walk (shared)
        }

        public bool PluginPresent(string pluginName) => _view.ContainsPlugin(pluginName);

        public string? WinnerPluginOf(FormKey record) => _view.ResolveWinner(record)?.WinnerPlugin;

        public string? EditorIdOf(FormKey record)
        {
            var w = _view.ResolveWinner(record);
            if (w is null) return null;
            return _view.GetRecord(_session, w.Value.WinnerPlugin, record)?.EditorID;
        }
    }

    // ---- NIF layer: read the data values inside one or many meshes (housecarl_nif_inspect) ----

    /// <summary>Inspect the data values inside one or many Skyrim meshes: capture the asset resolver once under
    /// <see cref="_gate"/>, then, per Data-relative path and outside the gate on the pinned handle-free view, resolve
    /// through the MO2 VFS to the winning copy (or the <paramref name="mod"/>-named provider), read that copy's bytes
    /// in process (a loose file, or a single entry out of a BSA — no disk extraction), and hand them to
    /// <see cref="NifService.Inspect"/>. Read-only. Results come back in input order, one per path; a per-path failure
    /// is a named <see cref="NifInspectData.Error"/> that never aborts the rest of the batch. Each path carries its
    /// full winner-to-loser provider chain and ambiguity flag, while the build-level caveats
    /// (<see cref="AssetView.BsaFailures"/>, discovery warnings) ride once on the batch so an ABSENT answer is never
    /// over-trusted. The single capture is what makes a load-order-wide sweep one call instead of one per mesh.</summary>
    public NifInspectBatchData NifInspect(IReadOnlyList<string> relPaths, string? sourceProvider)
    {
        AssetResolver.AssetView view;
        IReadOnlyList<string> warnings;
        string profileName;
        lock (_gate)
        {
            view = Assets.Capture();                              // build/refresh the asset resolver under the gate, once per batch
            warnings = _assetWarnings;
            profileName = _profileName;
        }

        // Outside the gate: the captured view is pinned and handle-free, so resolving, reading and parsing here cannot
        // race a concurrent refresh into wrongness and does not block other tools behind these file reads.
        var modsRoot = ModsRootOrNull;
        var results = new List<NifInspectData>(relPaths.Count);
        foreach (var raw in relPaths)
        {
            var rel = (raw ?? "").Trim();
            // A raw path into the mods tree reads past the VFS, so it is this path's own refusal with the address
            // form — ahead of the generic drive-rooted message, which says nothing about how to name the mod.
            if (ModsPathAddress.Split(rel, modsRoot) is { } hit)
            {
                results.Add(NifInspectData.Fail(rel, ModsPathAddress.Refusal("", rel,
                    ModsPathAddress.Address(hit.ModFolder, hit.RelPath, "mesh_paths", "source_provider"))));
                continue;
            }
            // Per-path isolation holds by construction rather than by trusting the callee: anything unexpected from
            // one path's resolve, read or parse becomes that path's named error, never the whole batch's.
            try { results.Add(NifInspectOne(view, rel, sourceProvider)); }
            catch (Exception ex) { results.Add(NifInspectData.Fail(rel, $"unexpected error inspecting this path — {ex.GetType().Name}: {ex.Message}")); }
        }
        return new NifInspectBatchData(results, view.BsaFailures, warnings, profileName);
    }

    /// <summary>One path's inspect against the already-captured view — the per-path body of <see cref="NifInspect"/>.
    /// Every failure is a named per-path outcome, never a throw.</summary>
    static NifInspectData NifInspectOne(AssetResolver.AssetView view, string rel, string? sourceProvider)
    {
        if (rel.Length == 0)
            return NifInspectData.Fail("", "empty mesh path. Pass a Data-relative path, e.g. 'meshes\\actors\\character\\facegendata\\facegeom\\Skyrim.esm\\00000007.nif'.");

        PlacementResolution place;
        try { place = view.ResolveForPlacement(rel); }
        catch (ArgumentException ex) { return NifInspectData.Fail(rel, $"invalid path — {ex.Message}"); }

        var providers = place.Sources.Select(s => new NifProvider(s.ProviderName, KindLabel(s.Kind))).ToList();

        // Pick the copy to read: the VFS winner by default, or a specific provider when source_provider= names one.
        // source_provider= is
        // answered FIRST, ahead of the ABSENT return: naming a mod reaches that mod whether or not MO2 ticks it, and
        // a donor outside the active set is exactly a path nothing active supplies — under ABSENT its name would
        // never be consulted and the answer would read as "the donor has no mesh" (#388 ii).
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
                // A model path taken straight off a record is stored relative to meshes\, so a flat ABSENT is a dead
                // end for the normal way one arrives at a mesh. The hint is re-resolved, never guessed: a "did you
                // mean" always names a file that exists, and the weaker fallback names only the convention.
                var hint = AssetPathHint.MeshHint(view, rel);
                // Absent=true lets the renderer hedge this at the point of use against the batch-level caveats; the
                // top-of-output warning alone scrolls away in a long batch.
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

    /// <summary>The provider record for a CHOSEN source, carrying the off-order provenance the chain entries never
    /// need: only the copy actually read can have come from outside what the game loads.</summary>
    static NifProvider NifProviderFor(PlacementSource s)
        => new(s.ProviderName, KindLabel(s.Kind), s.OffOrder, s.OwnerEnabled);

    /// <summary>Answer <c>source_provider=</c> for the NIF surface: pick the named provider's copy through the ONE source policy
    /// every asset caller rides, or hand back the refusal sentence. Shared by nif_inspect and nif_set, which are the
    /// same code twice and have drifted once before.
    ///
    /// <para>Two things follow from routing it here rather than matching the name in place. Naming a mod reaches
    /// that mod's loose files AND its own root archives, ticked or not (#388), so the refusal never reports a donor's
    /// mesh as absent. And the provider names the refusal lists are spelled by the same formatter the tool prints
    /// them with, so the token in the message is the token <c>source_provider=</c> takes (#340).</para></summary>
    static (PlacementSource? Source, string? Error, bool Absent) NifPick(AssetResolver.AssetView view, PlacementResolution place, string rel, string sourceProvider)
    {
        // Parse, not Named: the refusal's tail teaches the '*winner' pole, so this surface has to take it. The sigil
        // is what makes that safe — '*' cannot appear in a Windows name, so a bare token is always a provider.
        var choice = AssetSourceChoice.Parse(sourceProvider);
        var pick = AssetSourceSelection.Select(place, choice, n => view.TryResolveOffOrderProvider(n, rel));
        if (pick.Verdict == AssetSourceVerdict.Selected) return (pick.Source, null, false);
        // The winner pole over an empty universe is the ABSENT case, not a named miss; say so rather than quote
        // '*winner' back as a mod name that supplies nothing. Absent travels with it: this is the same absence the
        // no-source_provider= arm reports, so it earns the same scan-incomplete hedging at the point of use.
        if (choice.Pole != AssetSourcePole.Named)
            return (null, "ABSENT — no active mod or BSA provides '" + rel + "', so there is no winner to read."
                        + (AssetPathHint.MeshHint(view, rel) is { } wh ? " " + wh : ""), true);
        // A named miss is NOT an absence — it says which mod, and carries its own inline scan caveat instead, so it
        // must not also draw the ABSENT-worded hedges.
        return (null, WriteSentences.PlaceSourceNamedAbsent(
            sourceProvider, rel, pick.ProviderNames,
            pick.OffOrderReason, pick.OffOrderUnreadableName, pick.OffOrderUnreadableCause,
            AssetPathHint.MeshHint(view, rel), place.ReadIncomplete), false);
    }

    /// <summary>Render an <see cref="AssetKind"/> as the tool-facing label ("loose" / "BSA"). An explicit switch
    /// rather than a ternary, so a new AssetKind renders its real name instead of being mislabelled.</summary>
    static string KindLabel(AssetKind k) => k switch { AssetKind.Bsa => "BSA", AssetKind.Loose => "loose", var other => other.ToString() };

    // ---- NIF layer: whitelisted writes into a mesh (housecarl_nif_set) ----

    /// <summary>Apply the whitelisted write ops to a mesh: resolve the Data-relative <paramref name="relPath"/> to the
    /// winning copy (or <paramref name="sourceProvider"/>'s copy), read its bytes in process, hand them to
    /// <see cref="NifService.Set"/>, which applies and verifies or refuses loudly — nothing reaches disk unless it
    /// verified — then place the verified bytes. Two lanes, mirroring the record write lanes:
    ///   • DEFAULT (non-destructive): write into a new houseCARL-owned MO2 mod folder at the same relative path, which
    ///     the modder enables — and, on the into= lane, sorts above the current winner — so the edited copy wins the VFS. Originals untouched,
    ///     and a BSA-packed source becomes a loose winning override this way.
    ///   • IN-PLACE (opt-in): overwrite the winning loose file where it sits, behind the same persistent first-touch
    ///     consent handshake as the record in-place lane, keyed on the resolved file path. No backup. A BSA-only winner
    ///     has no loose file to edit and is refused with the default-lane guidance.
    /// Serialized on the write gate. For the default lane, "wrote it" is not "it wins": the render says to enable the
    /// mod (and to sort it, on the into= lane), and this never claims the fix took effect on write.</summary>
    public NifSetResult NifSet(string relPath, IReadOnlyList<NifSetOp> ops, string? sourceProvider, string? patchName, string? into, bool inPlace, bool acknowledge)
    {
        var rel = (relPath ?? "").Trim();
        if (rel.Length == 0) return NifSetResult.Fail("no mesh path given. Pass a Data-relative path, e.g. 'meshes\\armor\\iron\\cuirass_1.nif'.");
        if (ops is null || ops.Count == 0) return NifSetResult.Fail("no write op given — pass at least one op (e.g. set_flags, rename_shape).");
        if (inPlace && !string.IsNullOrWhiteSpace(into))
            return NifSetResult.Fail("in_place and into are mutually exclusive — in_place overwrites the winning file where it sits; into= names a NEW houseCARL folder.");

        lock (_writeGate)
        {
            AssetResolver.AssetView view; IReadOnlyList<string> warnings; string profileName;
            try { lock (_gate) { view = Assets.Capture(); warnings = _assetWarnings; profileName = _profileName; } }
            catch (Exception ex) { return NifSetResult.Fail($"could not resolve the asset layer (the MO2 instance may not be readable): {ex.Message}"); }

            PlacementResolution place;
            try { place = view.ResolveForPlacement(rel); }
            catch (ArgumentException ex) { return NifSetResult.Fail($"invalid path — {ex.Message}"); }

            var providers = place.Sources.Select(s => new NifProvider(s.ProviderName, KindLabel(s.Kind))).ToList();

            // pick the copy to read/edit: the VFS winner, or a specific provider when source_provider= names one.
            // source_provider= is
            // answered ahead of the ABSENT return, for the same reason nif_inspect answers it there.
            PlacementSource chosen;
            if (!string.IsNullOrWhiteSpace(sourceProvider))
            {
                var pick = NifPick(view, place, rel, sourceProvider!.Trim());
                if (pick.Error is not null) return NifSetResult.Fail(pick.Error, providers, profileName);
                chosen = pick.Source!;
            }
            else
            {
                if (place.Sources.Count == 0)
                {
                    var hint = AssetPathHint.MeshHint(view, rel);   // same verified re-resolve as nif_inspect's ABSENT
                    return NifSetResult.Fail(
                        $"ABSENT — no active mod or BSA provides '{rel}', so there is no copy to edit." + (hint is null ? "" : " " + hint),
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
                // The lane overwrites the WINNING file with no backup, and its consent handshake is written about
                // that file. A copy the game is not loading is not that file, so this lane declines it rather than
                // mutating an original the caller reached by naming a mod.
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

                // The check gates entry here; the acknowledgement is recorded below, only once the overwrite has landed
                // and verified. The parent pre-flight and the write's own failure both refuse without changing the
                // file, and neither may spend the caller's one-time confirmation.
                bool already = _store.IsInPlaceAcknowledged(targetPath);
                if (!already && !acknowledge)
                    return NifSetResult.NeedsAck(NifInPlaceHandshakeText(meshName, targetPath), chosenProv, providers, profileName);
                bool owesConsent = !already && acknowledge;

                if (InPlaceParentUnwritable(targetPath, out var why)) return NifSetResult.Fail(why, providers, profileName);
                try { AtomicFile.WriteAllBytes(targetPath, editedBytes); }
                catch (Exception ex) { return NifSetResult.Fail($"could not overwrite '{targetPath}' in place: {ex.Message}. Nothing was written.", providers, profileName); }
                long sz; try { sz = new FileInfo(targetPath).Length; } catch { sz = -1; }
                if (sz != editedBytes.Length)
                    return NifSetResult.Fail($"wrote '{meshName}' but its on-disk size ({sz}) does not match the {editedBytes.Length} verified byte(s) — verify before relying on it.", providers, profileName);

                var ackNote = PersistInPlaceConsent(owesConsent, targetPath, "edit", subject: "file");
                return NifSetResult.OkInPlace(rel, chosenProv, providers, place.Ambiguous, editedIsWinner, report, targetPath,
                    MergeWarnings(report.Warnings, warnings, ackNote), profileName);
            }

            // ---- DEFAULT (new-folder) lane ----
            RiderFolder rf;
            try { rf = ResolvePatchModFolder(patchName, into, "houseCARL_NifEdit", new RiderNaming("patch")); }
            catch (InvalidOperationException ex) { return NifSetResult.Fail(ex.Message, providers, profileName); }

            var dest = Path.Combine(rf.OutputDir, rel);
            try { Directory.CreateDirectory(Path.GetDirectoryName(dest)!); AtomicFile.WriteAllBytes(dest, editedBytes); }
            catch (Exception ex)
            {
                var residue = RemoveOrNameRiderResidue(rf);
                return NifSetResult.Fail($"could not write '{rel}' into the patch folder: {ex.Message}"
                    + (residue is null ? "" : $" The freshly created mod folder was left at '{residue}'."), providers, profileName);
            }
            long size; try { size = new FileInfo(dest).Length; } catch { size = -1; }
            if (size != editedBytes.Length)
            {
                RemoveOrNameRiderResidue(rf);
                return NifSetResult.Fail($"wrote '{rel}' but its on-disk size ({size}) does not match the {editedBytes.Length} verified byte(s) — verify before relying on it.", providers, profileName);
            }

            string? winner = providers.Count > 0 ? providers[0].Text : null;
            // MO2's overwrite folder is the TOP loose root, so no mod folder out-ranks it and no left-pane sort reaches it.
            var winSrc = place.Sources.Count > 0 ? place.Sources[0] : null;
            bool winnerIsOverwrite = winSrc is { Kind: AssetKind.Loose }
                && string.Equals(winSrc.ProviderName, AssetResolver.OverwriteLayerName, StringComparison.OrdinalIgnoreCase);
            // The folder the mesh was just written into is already the winner — an into= re-edit of the same mesh,
            // which is the normal iterate loop. Sorting that folder above itself is not an instruction.
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
    }

    /// <summary>Merge the write report's notes with the asset-layer discovery warnings and an optional extra note.
    /// Both lanes surface both sets, so a preservation disclosure cannot vanish by riding the other lane's
    /// list.</summary>
    static IReadOnlyList<string> MergeWarnings(IReadOnlyList<string> reportWarnings, IReadOnlyList<string> assetWarnings, string? extra)
    {
        var list = new List<string>(reportWarnings.Count + assetWarnings.Count + 1);
        list.AddRange(reportWarnings);
        list.AddRange(assetWarnings);
        if (extra is not null) list.Add(extra);
        return list;
    }

    /// <summary>The mesh-specific first-touch in-place consent prompt. Shares its opening lead with the plugin
    /// handshake (<see cref="InPlaceHandshakeLead"/>) and diverges after it, because that prompt's wording about
    /// re-serializing a whole plugin and engine-reserved sub-0x800 records is false for a .nif: a mesh write is a
    /// whole-file NiflySharp re-serialization, then verified.</summary>
    static string NifInPlaceHandshakeText(string meshName, string path) =>
        InPlaceHandshakeLead(meshName, path, "mesh", "overwrites") +
        "  • The written mesh is a WHOLE-FILE re-serialization through NiflySharp's canonical writer (the way NifSkope / BodySlide rewrite a mesh on save), NOT a byte-surgical patch — then VERIFIED (only the value you edited changed; it reloads as a valid SE mesh).\n" +
        "  • It still refuses if the mesh can't be parsed or isn't a Skyrim SE stream.\n" +
        "  • The default lane (a NEW mod folder, originals untouched) stays the recommended way — this is the explicit opt-in.\n" +
        "Re-call the SAME edit with acknowledge=true to proceed.";

    // ---- place assets so the correct copies win the VFS (housecarl_place) ----

    /// <summary>Place one or more assets into a new houseCARL-owned MO2 mod folder so the correct copy can win the
    /// VFS. For each request: resolve its current providers (auto-resolving a source when none was named — a sole
    /// provider is used, more than one is refused as ambiguous, none is refused with guidance), read the source bytes
    /// in process (a loose file, or a single entry out of a BSA), and write them crash-atomically under the owned
    /// folder. Originals are untouched: only a fresh or houseCARL-owned folder is ever written. On failure a fresh
    /// folder that ended up with nothing placed is removed, and a partial one is kept and named. "Wrote it" is not
    /// "it wins": the mod must be enabled — and, on the into= lane only, sorted above the current winner — which the
    /// render says, and this never claims the fix took effect on write. Serialized on the write gate.</summary>
    public PlaceOutcome PlaceAssets(IReadOnlyList<PlaceRequest> requests, string? patchName, string? into)
    {
        if (requests is null || requests.Count == 0) return PlaceOutcome.Fail("no assets to place.");

        lock (_writeGate)                                                 // one placement batch at a time: resolve, stage, commit
        {
            // Precondition: _writeGate is held for the WHOLE method. ResolvePatchModFolder and the `Assets` getter each
            // take and release _gate, so this method straddles two _gate sections — safe only because _writeGate
            // excludes every other writer and instance switch throughout, so no profile refresh can land between them.
            // Do not call PlaceOne or Assets here outside this _writeGate hold.
            RiderFolder rf;
            try { rf = ResolvePatchModFolder(patchName, into, "houseCARL_Assets", new RiderNaming("patch")); }   // neutral default stem; a caller with a better name passes patch
            catch (InvalidOperationException ex) { return PlaceOutcome.Fail(ex.Message); }

            // One asset build for the whole batch, reentrant on _gate. Captured rather than the live resolver, so every
            // request in the batch — and the missing-root suggestion's re-resolve — answers from the same snapshot and
            // a refresh landing mid-batch cannot make two placements describe two builds.
            AssetResolver.AssetView view; IReadOnlyList<string> warnings;
            try { lock (_gate) { view = Assets.Capture(); warnings = _assetWarnings; } }
            catch (Exception ex)
            {
                var residue = RemoveOrNameRiderResidue(rf);              // nothing placed yet → a fresh folder is an orphan
                return PlaceOutcome.Fail($"could not resolve the asset layer (the MO2 instance may not be readable): {ex.Message}"
                    + (residue is null ? "" : $" The freshly created mod folder was left at '{residue}'."));
            }

            var results = new List<PlaceResult>(requests.Count);
            int placed = 0;
            foreach (var req in requests)
            {
                var r = PlaceOne(req, view, rf.OutputDir);
                results.Add(r);
                if (r.Placed) placed++;
            }

            // Nothing placed into a fresh folder → remove the orphan. A reused into= folder belongs to the user and is
            // never touched. A partial fresh folder is kept and its path surfaced.
            string? leftover = placed == 0 ? RemoveOrNameRiderResidue(rf) : null;
            return new PlaceOutcome(results, placed > 0 ? rf.ModFolder : null, warnings, leftover, null)
                { FreshFolder = rf.CreatedFresh };
        }
    }

    /// <summary>The refusal for a <c>source=</c> that reaches into MO2's mods tree, or null when it does not. A
    /// <c>'&lt;archive.bsa&gt;|&lt;entry&gt;'</c> pair is judged on the archive's path and keeps its entry in the
    /// remedy. A both-slots member reaches here only with a fully-qualified '.bsa', which IS the single source that
    /// serves two slots — so the remedy keeps that archive, named as a provider, rather than telling the caller a
    /// shape the tool documents is impossible.</summary>
    string? RawModsSourceRefusal(string source, bool bothSlots)
    {
        var v = source.Trim().Trim('"');
        int pipe = v.IndexOf('|');
        if (ModsPathAddress.Split(pipe >= 0 ? v.Substring(0, pipe) : v, ModsRootOrNull) is not { } hit) return null;
        var remedy = bothSlots
            ? $"Address that archive instead with source_provider='{Path.GetFileName(hit.RelPath ?? v)}' and no source= — a BSA filename is a provider name, and each FaceGen slot then derives its own entry from that archive."
            : ModsPathAddress.Address(hit.ModFolder, hit.RelPath is null || pipe < 0 ? hit.RelPath : hit.RelPath + v.Substring(pipe),
                                      "source", "source_provider");
        return ModsPathAddress.Refusal("", v, remedy);
    }

    /// <summary>Place one asset: validate the destination rel-path (drive-rooted and '..' paths are rejected by the
    /// resolver's own check), get the source bytes (explicit source= or auto-resolve), and write them atomically under
    /// <paramref name="outDir"/>. Reports the CURRENT VFS winner, because the placed file does NOT win until the mod is
    /// enabled (the folder isn't in the active profile yet) — and on the into= lane, whose folder already has a fixed
    /// priority, until it is also sorted above that winner.
    /// A per-asset failure is a recoverable named error, never a thrown batch abort.</summary>
    PlaceResult PlaceOne(PlaceRequest req, AssetResolver.AssetView view, string outDir)
    {
        string rel;
        try { rel = AssetResolver.ValidateRelPath(req.AssetPath); }
        catch (ArgumentException ex) { return PlaceResult.Fail(req.AssetPath, ex.Message); }

        var res = view.ResolveForPlacement(rel);                         // rel already validated — won't throw
        var winnerSrc = res.Sources.Count > 0 ? res.Sources[0] : null;
        var winner = winnerSrc is null ? null : DescribeSource(winnerSrc);
        // MO2's overwrite folder is the TOP loose root (AssetResolver.BuildLooseRoots: overwrite > mods > Data), so no
        // mod folder out-ranks it and no left-pane sort reaches it — the render owes a different instruction there.
        bool winnerIsOverwrite = winnerSrc is { Kind: AssetKind.Loose }
            && string.Equals(winnerSrc.ProviderName, AssetResolver.OverwriteLayerName, StringComparison.OrdinalIgnoreCase);
        // The destination folder is ALREADY the winner — a re-place into an enabled houseCARL patch. Sorting it above
        // itself is not an instruction anyone can follow.
        bool winnerIsDestination = winnerSrc is not null
            && string.Equals(winnerSrc.ProviderName, Path.GetFileName(outDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                             StringComparison.OrdinalIgnoreCase);
        // The other end of the root list: a BSA wins only when no loose copy exists, and the game's Data folder only
        // when no mod provides the path — so a placed loose copy beats either one on enable, at any mod priority.
        bool winnerLosesOnEnable = winnerSrc is not null && !winnerIsDestination
            && (winnerSrc.Kind == AssetKind.Bsa
                || string.Equals(winnerSrc.ProviderName, AssetResolver.DataLayerName, StringComparison.OrdinalIgnoreCase));

        // ---- source bytes: an ON-DISK source= is read as named; anything else resolves through the VFS ----
        // Three source shapes reach here: an on-disk file the caller named exactly (a FULLY-QUALIFIED path, which a
        // '.bsa' must also be to count as an archive, or a '<bsa>|<entry>' pair), a DATA-RELATIVE path resolved
        // through the VFS under a pole, and no source at all — which
        // is the same VFS lane pointed at the destination path. The last two share one code path because they are
        // one question ("which provider supplies this Data-relative path") asked about different paths.
        byte[] bytes; string sourceDesc;
        // The mod folder an off-order read was served from, or null when the bytes came from the active order. Typed
        // rather than baked into sourceDesc, because the render owns the sentence and a caller cannot infer this from
        // a provider name.
        string? offOrderProvider = null;
        bool offOrderOwnerEnabled = false;                 // WHICH off-order reason — an unticked mod, or a ticked mod's unloaded archive
        // One normalization point for source=, ahead of both the classification and every consumer: whatever trimming
        // the routing decision depends on must have happened before this line, or a quoted Data-relative source
        // classifies one way and is read another.
        var explicitSrc = NormalizeSourceArg(req.Source);
        var providerSel = req.SourceProvider?.Trim();
        if (!string.IsNullOrEmpty(explicitSrc) && !IsVfsSource(explicitSrc!))
        {
            // A raw path into the mods tree reads past the VFS. Judged on the ARCHIVE's own path for a
            // '<archive.bsa>|<entry>' source, and the remedy keeps the entry — a bare archive would place the whole
            // .bsa. On a both-slots member no Data-relative source is accepted at all, so that one is told to name
            // the provider or pick a slot rather than handed a form its retry would refuse.
            if (RawModsSourceRefusal(explicitSrc!, req.BothSlots) is { } rawErr)
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
            // The off-order lane is handed to the one source policy rather than spelled here: naming a mod means that
            // mod's copy whether or not MO2 ticks it, and every caller reaches that rule through this call.
            var choice = AssetSourceChoice.Parse(providerSel);
            var pick = AssetSourceSelection.Select(srcRes, choice,
                                                   n => view.TryResolveOffOrderProvider(n, srcRel));

            // Both named-provider misses render as one refusal: they are the same fact — the named provider does not
            // supply this path — differing only in which places were searched and whether there is anyone else to
            // suggest, and the sentence takes both as inputs.
            // Gated on the POLE, not on "a provider string was passed": '*winner' is a non-empty selector that parses
            // to the winner pole, and gating on the string would quote it back as if it were a mod name and claim a
            // folder of that name had been searched, which is impossible since '*' cannot appear in a folder name.
            if (choice.Pole == AssetSourcePole.Named
                && pick.Verdict is AssetSourceVerdict.NamedAbsent or AssetSourceVerdict.NoProvider)
                return PlaceResult.Fail(rel, WriteSentences.PlaceSourceNamedAbsent(
                    providerSel!, srcRel, pick.ProviderNames,
                    pick.OffOrderReason, pick.OffOrderUnreadableName, pick.OffOrderUnreadableCause,
                    // The root-prefix hint: a path taken off a record is stored relative to meshes\ or textures\, and
                    // naming a provider does not stop that being the caller's actual mistake. Verified before it is
                    // offered, like every other site that shows it.
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
                    // The other dead end: a Data-relative source= with no source_provider=. Same sentence as the
                    // auto-resolve refusal below, so a caller who passed a source and no provider gets the same route
                    // out.
                    + " " + WriteSentences.PlaceSourceNameReachesUnticked,
                    winner);
            if (pick.Verdict == AssetSourceVerdict.NoProvider)
            {
                // A path taken off a record is stored relative to its root folder, so passing it verbatim is the
                // normal way one arrives here. Both roots are tried (this lane cannot know the path's kind) and
                // verified by re-resolving, so a suggestion always names a copy that really is provided and silence
                // is the default. Not offered on the explicit-source= arm above: placing a NEW file at a path
                // nothing provides is legitimate there.
                var hint = AssetPathHint.AssetRootHint(view, rel);
                // Order is load-bearing: the hint sits directly after the sentence about the PATH and before the
                // "Pass source=" fallback. It names a destination, not a source, and must not trail an imperative
                // about sources or it reads as one.
                return PlaceResult.Fail(rel,
                    $"nothing in the active load order provides '{rel}', so there is no copy to auto-place."
                    + (hint is null ? "" : " " + hint)
                    + " Pass source= the copy to place — a Data-relative path (resolved through the VFS, with"
                    + " source_provider= to name which mod's copy), a full loose path, '<archive.bsa>|<entry>', or a '.bsa' path."
                    // The commonest way of reaching this refusal is that the only copy lives in a mod MO2 does not
                    // load, and naming a mod reaches it, so the caller who is here is the one who needs to be told.
                    + " " + WriteSentences.PlaceSourceNameReachesUnticked
                    + (res.ReadIncomplete ? " " + WriteSentences.PlaceSourceScanIncomplete : ""),
                    winner);
            }
            var (b, desc, err) = ReadResolvedSource(pick.Source!);
            if (err is not null) return PlaceResult.Fail(rel, err, winner);
            bytes = b!;
            if (pick.Source!.OffOrder) { offOrderProvider = pick.Source.ProviderName; offOrderOwnerEnabled = pick.Source.OwnerEnabled; }
            // A source read from a different path than the destination is a rename and the render has to say so,
            // since "placed from ModX" alone hides that the bytes are another file's. Keyed on whether the two paths
            // differ, not on whether a source was named — a provider-scoped placement of the same path is not a
            // rename.
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

        // ---- integrity: the on-disk size matches the source bytes, so success is never claimed falsely ----
        // Truncation / short-write detection, not a content hash: the bytes are in memory and the swap is atomic, so a
        // same-length corruption is not a reachable failure here. Defensive; AtomicFile's swap is the real guarantee.
        long size; try { size = new FileInfo(dest).Length; } catch { size = -1; }
        if (size != bytes.Length)
            return PlaceResult.Fail(rel,
                $"wrote '{rel}' but its on-disk size ({size}) does not match the {bytes.Length} source byte(s) — verify before relying on it.", winner);
        return new PlaceResult(rel, true, bytes.Length, sourceDesc, winner, null)
            { SourceOffOrderProvider = offOrderProvider, SourceOffOrderOwnerEnabled = offOrderOwnerEnabled,
              WinnerIsOverwrite = winnerIsOverwrite, WinnerIsDestination = winnerIsDestination,
              WinnerLosesOnEnable = winnerLosesOnEnable };
    }

    /// <summary>Read an ON-DISK source= the caller named exactly. Forms: "&lt;archive.bsa&gt;|&lt;entry&gt;" (a specific
    /// BSA entry, split on the FIRST '|'); a path ending ".bsa" (the entry is the destination rel-path — the FaceGen
    /// case, where the entry inside the BSA IS the Data-relative path); a FULLY-QUALIFIED path (a loose file on disk).
    /// A Data-relative source never reaches here — <see cref="IsVfsSource"/> routes it to the VFS lane, which is the one
    /// that can say which provider's copy. Returns the bytes plus a human description, or a named error for a
    /// missing file, missing entry, or unreadable archive.</summary>
    static (byte[]? bytes, string? desc, string? error) ReadExplicitSource(string source, string destRel)
    {
        // The whole-string trim and unquote happened in NormalizeSourceArg, ahead of the routing decision. The
        // per-part trims below stay: each side of a '<archive>|<entry>' pair can carry its own quotes, which no
        // whole-string normalization can reach.
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

    /// <summary>Read the bytes of an AUTO-resolved provider (the sole VFS provider when no source= was named). A loose
    /// provider reads off disk; a BSA provider extracts its single entry natively. A named error if the resolved copy
    /// vanished between resolve and read, or the archive cannot be read.</summary>
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

    /// <summary>Read one entry out of a BSA (native Mutagen, no BSArch, zero handles at rest — see
    /// <see cref="AssetResolver.TryReadArchiveEntry"/>). Named errors for a missing archive, an entry not inside it,
    /// or an unreadable archive.</summary>
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

    /// <summary>Do two Data-relative paths name the SAME asset, by the key the VFS itself resolves on? Both inputs
    /// have already been through <see cref="AssetResolver.ValidateRelPath"/>, which folds separators and any leading
    /// separator; the remaining axis is CASE, and the asset layer's own lookups are ordinal-case-insensitive. So a
    /// raw string compare would call <c>Meshes/X.NIF</c> and <c>meshes\x.nif</c> two different files and report a
    /// rename between one file and itself.</summary>
    static bool SameAssetPath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The ONE normalization of a caller's source= — trim, then strip surrounding quotes — applied before
    /// the routing decision and before every consumer, so no two of them can disagree about what the string is.
    /// Blank becomes null (the "no source" lane) rather than an empty path nobody can resolve.</summary>
    static string? NormalizeSourceArg(string? source)
    {
        var s = source?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        s = s.Trim('"').Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    /// <summary>Whether a <c>source=</c> is one a provider pole can even apply to — nothing named (the pole then
    /// says whose copy of the DESTINATION to place), or a Data-relative path resolved through the VFS. False for an
    /// on-disk file, which already names one exact copy: <see cref="PlaceOne"/> refuses a pole there rather than
    /// dropping it, so a CALL-LEVEL pole must not be attached to such a member in the first place. Same routing
    /// decision as the placer, through the same two helpers, so the two cannot drift.</summary>
    internal static bool SourceTakesAProvider(string? source)
    {
        var s = NormalizeSourceArg(source);
        return string.IsNullOrEmpty(s) || IsVfsSource(s!);
    }

    /// <summary>Does this source= name a copy through the VFS (a Data-relative path) rather than one exact file on
    /// disk? Expects an already-<see cref="NormalizeSourceArg"/>d string. The on-disk forms are tested in the same
    /// order <see cref="ReadExplicitSource"/> routes them.
    /// <para>Fully qualified, not merely rooted: on Windows <c>Path.IsPathRooted</c> is true for a leading '\' or '/',
    /// but <c>AssetResolver.Normalize</c> trims exactly those, so <c>\meshes\…</c> is a legal Data-relative
    /// destination. A path is on-disk only when it names a volume (<c>C:\…</c>) or a UNC share.</para>
    /// <para>The qualified test runs BEFORE the extension test, because an extension says nothing about where the
    /// file is: a mod can legitimately ship <c>meshes\thing.bsa</c> as a Data-relative asset. A '.bsa' means "an
    /// archive to open" only once the caller is known to have named a file on disk.</para></summary>
    static bool IsVfsSource(string source)
    {
        if (source.IndexOf('|') >= 0) return false;                      // '<archive.bsa>|<entry>' — an entry, not a path
        if (!Path.IsPathFullyQualified(source)) return true;             // Data-relative ⇒ the VFS answers, whatever it ends in
        return false;                                                    // a volume or UNC path ⇒ one exact file (or archive) on disk
    }

    /// <summary>Whole-order stats (forces the lazy build). A test seam: the probes and tests warm the lazy index
    /// through it and read the epoch and counters off it. No shipped caller — the product reads the same numbers
    /// through <see cref="StatusData"/>.</summary>
    public (int plugins, int records, int conflicts, int maxDepth, IReadOnlyList<string> loadFailures, string epoch) Stats()
    {
        var view = Resolver.Capture();          // one build for every counter in the line
        return (view.PluginCount, view.RecordCount, view.ConflictCount, view.MaxDepth, view.LoadFailures, view.Epoch);
    }

}

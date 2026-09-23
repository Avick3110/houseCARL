using System.Text;
using System.Text.Json;
using HousecarlCore;

namespace HousecarlMcp;

// The inventory family's text and json renders for housecarl_skse; contract in docs/architecture/skse-layer.md.
/// <summary>Renders <see cref="SkseInventoryData"/>: summary and compat, the diagnostic subsets in full, the terse
/// plugin roster, then the config folders grouped by count and provider; filter= expands a group or a plugin.</summary>
static class SkseInventoryWire
{
    /// <summary>The <c>peek=</c> argument check, or null; a peek is per-DLL by design, so a bare peek=true fails.</summary>
    internal static string? PeekArgError(bool peek, string? filter) =>
        peek && string.IsNullOrWhiteSpace(filter)
            ? "error: peek=true needs filter= — a peek is per-DLL, not a whole-layer dump (it reads each matching DLL's whole " +
              "image). Pass filter='<DLL/plugin/mod name>' to name the DLL to peek, e.g. filter='SkyPatcher' peek=true."
            : null;

    /// <summary>What this family's accounting counts: the DLL rows; configs are stated in the census, and filter= counts both.</summary>
    internal const string RowNoun = "DLL(s)";
    internal const string MatchNoun = "match(es)";

    /// <summary>The DLL population split by loader scope and manifest kind — one function, so census and rows cannot drift.</summary>
    readonly record struct DllSplit(List<SkseFileEntry> Loaded, List<SkseFileEntry> Subfolder, List<SkseFileEntry> Modern,
                                    List<SkseFileEntry> Legacy, List<SkseFileEntry> NotPlugin, List<SkseFileEntry> Unreadable,
                                    List<SkseFileEntry> BsaOnly, List<SkseFileEntry> Locked);

    static DllSplit Split(IReadOnlyList<SkseFileEntry> dlls)
    {
        // DLLs split by SKSE-loader scope: top-level DLLs are what SKSE loads; subfolder DLLs are seen but not loader-scoped.
        var loaded = dlls.Where(e => e.Group.Length == 0).ToList();
        var modern = loaded.Where(e => e.Plugin is { Kind: SksePluginReader.SksePluginKind.Modern }).ToList();
        return new DllSplit(
            loaded,
            dlls.Where(e => e.Group.Length > 0).ToList(),
            modern,
            loaded.Where(e => e.Plugin is { Kind: SksePluginReader.SksePluginKind.LegacyQuery }).ToList(),
            loaded.Where(e => e.Plugin is { Kind: SksePluginReader.SksePluginKind.NotSkse }).ToList(),
            loaded.Where(e => e.Plugin is { Kind: SksePluginReader.SksePluginKind.Unreadable }).ToList(),
            loaded.Where(e => e.Plugin is null).ToList(),
            modern.Where(e => !e.Plugin!.Version!.VersionIndependent).ToList());
    }

    public static string Render(SkseInventoryData d, string? filter, int cap, RowWindow window = default, int trailer = 0)
    {
        if (filter is { Length: > 0 }) return RenderFiltered(d, filter.Trim(), cap, window, trailer);

        // The census states the WHOLE layer; limit=/offset= window only the rows listed below it.
        var all = Split(d.Dlls);
        int notes = NoteCount(d);
        var rows = window.Apply(d.Dlls);
        int reserve = TransportAccounting.Reserve(d.Dlls.Count, rows.Count, window, notes, RowNoun);
        // The scope note, caveats and filter hint are written after the rows, so they are charged before them.
        var tail = "(scope: full depth of Data\\SKSE\\Plugins. DLLs are top-level = what SKSE loads; configs at any depth are " +
                   "grouped by folder above. Non-config content (animation/mesh/etc.) is counted in the 'other file(s)' total.)\n" +
                   Caveats(d, cap) +
                   "\n→ filter='<plugin/mod/DLL name>' for a plugin's full detail, or filter='<folder>' (e.g. SkyPatcher, OStim) to list a config group.";
        // The two sections written whatever the rows cost carry their headings here, with the tail.
        string rosterHead = "\nplugins with metadata (" + Split(rows).Modern.Count + ") — name · version · compat · winning mod:\n";
        int folderGroups = d.Configs.Select(e => e.Group).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        string foldersHead = d.Configs.Count == 0 ? ""
            : "\nconfig folders (" + folderGroups + ") — folder: files ← provider(s):\n";
        // cap stays the CALLER's max_chars; budget is the room content has once everything written after it is
        // charged, each list's own cut notice included. See docs/architecture/skse-layer.md.
        int rosterCut = CutRoom(rows.Count, hint: FilterHint);
        int folderCut = CutRoom(folderGroups, "folders");
        int budget = Math.Max(1, cap - trailer - reserve - tail.Length - rosterHead.Length - rosterCut
                                 - foldersHead.Length - folderCut - SectionsMissed(9, cap).Length);
        int rosterCeil = budget + rosterHead.Length + rosterCut;
        int folderCeil = rosterCeil + foldersHead.Length + folderCut;
        int missed = 0;
        var tally = new RowTally();

        var sb = new StringBuilder();
        var w = Split(rows);
        var loaded = w.Loaded; var subfolder = w.Subfolder; var modern = w.Modern; var legacy = w.Legacy;
        var notPlugin = w.NotPlugin; var unreadable = w.Unreadable; var bsaOnly = w.BsaOnly; var locked = w.Locked;

        int addrLib = all.Modern.Count(e => e.Plugin!.Version!.UsesAddressLibrary);
        int sig = all.Modern.Count(e => e.Plugin!.Version!.UsesSignatureScanning);
        int groupCount = d.Configs.Select(e => e.Group).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        sb.Append("SKSE plugin layer — profile '").Append(d.ProfileName).Append("' — ")
          .Append(all.Loaded.Count).Append(" DLL(s), ").Append(d.Configs.Count).Append(" config(s) across ")
          .Append(groupCount).Append(" folder(s)");
        if (d.OtherFileCount > 0) sb.Append(", ").Append(d.OtherFileCount).Append(" other file(s)");
        sb.Append(" (full depth of SKSE\\Plugins)\n");
        sb.Append("plugins: ").Append(all.Modern.Count).Append(" with static metadata");
        if (all.Legacy.Count > 0) sb.Append(" · ").Append(all.Legacy.Count).Append(" legacy query-only");
        if (all.NotPlugin.Count > 0) sb.Append(" · ").Append(all.NotPlugin.Count).Append(" non-plugin (bundled deps)");
        if (all.BsaOnly.Count > 0) sb.Append(" · ").Append(all.BsaOnly.Count).Append(" BSA-only/unresolved");
        if (all.Unreadable.Count > 0) sb.Append(" · ").Append(all.Unreadable.Count).Append(" unreadable");
        if (all.Subfolder.Count > 0) sb.Append(" · ").Append(all.Subfolder.Count).Append(" in subfolders (not loader-scoped)");
        sb.Append('\n');
        sb.Append("compat: ").Append(addrLib).Append(" Address Library · ").Append(sig).Append(" signature-scanning · ")
          .Append(all.Locked.Count).Append(" version-LOCKED\n");

        // ── Diagnostic subsets, first and in full. ──

        // Debug-CRT offenders lead: the sharpest static verdict, and surfaced without peek= because the import walk is free.
        var debugCrt = loaded.Where(x => x.Plugin is { Imports: not null } pl && pl.DebugCrtImports.Count > 0).ToList();
        if (debugCrt.Count > 0 && !Head(sb, budget - CutRoom(debugCrt.Count, hint: FilterHint), "\n[!] DEBUG-BUILD plugins (" + debugCrt.Count +
                ") — they import the debug C runtime, which ships only with Visual Studio and is NOT redistributable:\n")) missed++;
        else if (debugCrt.Count > 0)
        {
            AppendCapped(sb, debugCrt, budget, x =>
            {
                var crt = x.Plugin!.DebugCrtImports;
                return $"  - {x.FileName} → needs {string.Join(", ", crt)}" +
                       $"{DebugCrtLayerVerdict(crt, SksePluginReader.IsSystemDllResolvable)}{Provider(x)}";
            }, tally);
        }

        // With the installed runtime resolved this is pass/fail per plugin; without it, it degrades to "verify each".
        string lockedHead = locked.Count == 0 ? "" : "\n[!] version-LOCKED plugins (" + locked.Count +
            ") — load ONLY on their listed runtime(s)" +
            (d.InstalledRuntime is { } rt0 ? $"; installed game runtime is {rt0}:\n" : "; a mismatch with your game version = won't load:\n");
        // The "different runtimes" line is written whatever the rows cost, so its room is charged with the heading.
        var distinctRuntimes = d.InstalledRuntime is not null ? new List<string>()
            : locked.SelectMany(e => e.Plugin!.Version!.CompatibleVersions).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string runtimesNote = distinctRuntimes.Count > 1
            ? "      ↑ these target DIFFERENT runtimes (" + string.Join(", ", distinctRuntimes) +
              ") — verify each matches your game version (the installed version could not be resolved).\n"
            : "";
        if (locked.Count > 0 && !Head(sb, budget - CutRoom(locked.Count, hint: FilterHint) - runtimesNote.Length, lockedHead)) missed++;
        else if (locked.Count > 0)
        {
            AppendCapped(sb, locked, budget - runtimesNote.Length, e =>
            {
                var v = e.Plugin!.Version!;
                string rt = v.CompatibleVersions.Count > 0 ? string.Join(", ", v.CompatibleVersions) : "(none listed!)";
                string verdict = d.InstalledRuntime is { } inst
                    ? (SksePluginReader.RuntimeCompatible(v, inst) ? "  = your game, loads" : "  ≠ your game — will NOT load")
                    : "";
                return $"  - {e.FileName} → {rt}{verdict}   [\"{v.Name}\"{Provider(e)}]";
            }, tally);
            sb.Append(runtimesNote);
        }

        if (!AppendSubset(sb, "legacy query-only (SE/VR-era — metadata set at runtime, not statically readable)", legacy, budget,
            e => $"  - {e.FileName}{Provider(e)}", tally)) missed++;
        if (!AppendSubset(sb, "non-plugin DLLs (no SKSE export — a bundled dependency, not a plugin)", notPlugin, budget,
            e => $"  - {e.FileName}{Provider(e)}", tally)) missed++;
        if (!AppendSubset(sb, "subfolder DLLs (present but NOT on SKSE's loader path — bundled/parent-loaded, not plugins SKSE loads)", subfolder, budget,
            e => $"  - {e.Group}\\{e.FileName}{Provider(e)}", tally)) missed++;
        if (!AppendSubset(sb, "BSA-only / unresolved DLLs (SKSE loads loose DLLs only — these will NOT load)", bsaOnly, budget,
            e => $"  - {e.FileName}{Provider(e)}  — {e.Note}", tally)) missed++;
        if (!AppendSubset(sb, "unreadable DLLs (not a valid PE image)", unreadable, budget,
            e => $"  - {e.FileName}{Provider(e)}  — {e.Plugin?.Note}", tally)) missed++;

        var contested = loaded.Where(e => e.ProviderCount > 1).ToList();
        if (!AppendSubset(sb, "contested DLLs (shipped by >1 mod — winner-first conflict chain; verify the winner is the one you want)", contested, budget,
            e => $"  - {e.FileName}: {Chain(e)}", tally)) missed++;

        // ── Plugin roster: the loaded, metadata-bearing plugins, terse. ──
        sb.Append(rosterHead);
        AppendCapped(sb, modern.OrderBy(e => e.FileName, StringComparer.OrdinalIgnoreCase).ToList(), rosterCeil, e =>
        {
            var v = e.Plugin!.Version!;
            return $"  - {e.FileName}  \"{v.Name}\" v{VersionText(e.Plugin, e.ModVersion)}  {CompatTag(v)}{Provider(e)}";
        }, tally);

        // ── Config folders, grouped by the derived subfolder and sorted by size. ──
        if (d.Configs.Count > 0)
        {
            var groups = d.Configs.GroupBy(e => e.Group, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Name: g.Key.Length == 0 ? "(top level)" : g.Key, Count: g.Count(),
                              Providers: g.Select(e => e.WinningProvider).Where(p => p is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                              Contested: g.Count(e => e.ProviderCount > 1)))
                .OrderByDescending(g => g.Count).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
            sb.Append(foldersHead);
            // The folder table's own cut notice was charged with its heading, so the rows lay below that room.
            int folderRoom = folderCeil - folderCut;
            int shown = 0;
            foreach (var g in groups)
            {
                int mark = sb.Length;
                string prov = g.Providers.Count switch
                {
                    0 => "(no active provider)",
                    <= 2 => string.Join(", ", g.Providers),
                    _ => $"{g.Providers.Count} mods",
                };
                sb.Append("  - ").Append(g.Name).Append(": ").Append(g.Count).Append(" ← ").Append(prov);
                if (g.Contested > 0) sb.Append("  [").Append(g.Contested).Append(" contested]");
                sb.Append('\n');
                // The row is taken back out whole when it crossed, so the response ends inside max_chars.
                if (sb.Length > folderRoom) { sb.Length = mark; sb.Append(Showing(shown, groups.Count, "folders")); break; }
                shown++;
            }
        }

        if (missed > 0) sb.Append(SectionsMissed(missed, cap));
        sb.Append(tail);
        return sb.ToString().TrimEnd('\n')
             + TransportAccounting.Compose(TransportAccounting.Tally(d.Dlls.Count, rows.Count, tally.Count, window, notes),
                                           RowNoun, everySentence: false);
    }

    /// <summary>filter=: full detail for every matching DLL, then every matching config, matched by folder, filename or provider.</summary>
    static string RenderFiltered(SkseInventoryData d, string filter, int cap, RowWindow window = default, int trailer = 0)
    {
        bool In(string? s) => s is not null && s.Contains(filter, StringComparison.OrdinalIgnoreCase);
        bool MatchCfg(SkseFileEntry e) => In(e.FileName) || In(e.WinningProvider) || In(e.Group);

        // SkseFileEntry.MatchesDll is the one DLL predicate, so the service peeks exactly the entries this view renders.
        var allDllHits = d.Dlls.Where(e => e.MatchesDll(filter)).OrderBy(e => e.FileName, StringComparer.OrdinalIgnoreCase).ToList();
        var allCfgHits = d.Configs.Where(MatchCfg).ToList();

        // The filter's population is its matches, DLLs then configs, and the header states the whole match count.
        int total = allDllHits.Count + allCfgHits.Count;
        int notes = NoteCount(d);
        var dllHits = window.Apply(allDllHits);
        var cfgHits = window.After(allDllHits.Count, dllHits.Count).Apply(allCfgHits);
        int windowed = dllHits.Count + cfgHits.Count;
        int reserve = TransportAccounting.Reserve(total, windowed, window, notes, MatchNoun);
        // The caveats close this view too, charged with the accounting rather than appended past the cap: a filtered
        // read that names no unreadable archive or mod folder reads as a complete answer.
        var tail = "\n" + Caveats(d, cap);
        // cap stays the caller's max_chars; budget is the room the blocks have once the tail is charged.
        int budget = Math.Max(1, cap - trailer - reserve - tail.Length);
        var tally = new RowTally();
        string Accounting() => TransportAccounting.Compose(
            TransportAccounting.Tally(total, windowed, tally.Count, window, notes), MatchNoun, everySentence: false);

        var sb = new StringBuilder();
        sb.Append("SKSE plugin layer — filter '").Append(filter).Append("' — ")
          .Append(allDllHits.Count).Append(" DLL + ").Append(allCfgHits.Count).Append(" config match(es) [profile '").Append(d.ProfileName).Append("']\n");

        if (total == 0)
        {
            sb.Append("\nnothing under SKSE\\Plugins matched. ")
              .Append(HousecarlCore.PluginNameSuggest.DidYouMean(filter,
                  d.Dlls.Select(e => e.FileName).Concat(d.Configs.Select(e => e.Group).Where(g => g.Length > 0)).Distinct()));
            sb.Append(tail);   // a "no match" over an incompletely-read build must carry the caveat (Q3)
            return sb.ToString().TrimEnd('\n') + Accounting();
        }

        // Everything this view writes below the DLL blocks is charged before the first one is laid.
        string dllCut = "\n  ... [remaining DLL matches omitted at max_chars=" + cap + "]\n";
        string peekNote = d.PeekRequested && allDllHits.Count == 0
            ? "\n[!] peek=true matched no DLL at all — nothing was peeked. Pass filter= the name of a loose DLL to peek it.\n"
            : "";
        int cfgRoom = cfgHits.Count == 0 ? 0
            : ("\nmatching configs (" + cfgHits.Count + "):\n").Length + CutRoom(cfgHits.Count);
        int dllRoom = budget - dllCut.Length - peekNote.Length - cfgRoom;

        var shownCfg = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in dllHits)
        {
            int mark = sb.Length;
            var added = new List<string>();
            AppendDetail(sb, e, d, shownCfg, added);
            // The block AND the configs it marked as shown come back out together; half a rollback would hide one.
            if (sb.Length > dllRoom)
            {
                sb.Length = mark;
                foreach (var path in added) shownCfg.Remove(path);
                sb.Append(dllCut);
                break;
            }
            tally.Mark(e.RelPath);
        }

        // peek= honoured with nothing to show is still an unanswered question; this covers the no-DLL-matched case.
        sb.Append(peekNote);

        // Remaining matching configs (not already shown as a DLL's paired config), grouped by folder.
        var rest = cfgHits.Where(e => !shownCfg.Contains(e.RelPath))
            .OrderBy(e => e.Group, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.FileName, StringComparer.OrdinalIgnoreCase).ToList();
        if (rest.Count > 0)
        {
            sb.Append("\nmatching configs (").Append(rest.Count).Append("):\n");
            int cfgRows = budget - CutRoom(rest.Count);
            string? curGroup = null;
            int shown = 0;
            foreach (var e in rest)
            {
                int mark = sb.Length;
                string g = e.Group.Length == 0 ? "(top level)" : e.Group;
                if (g != curGroup) { sb.Append("  ").Append(g).Append(":\n"); curGroup = g; }
                sb.Append("    - ").Append(e.FileName);
                if (e.ProviderCount > 1) sb.Append(": ").Append(Chain(e));   // contested config → the full winner→loser chain
                else sb.Append(Provider(e));
                sb.Append('\n');
                if (sb.Length > cfgRows) { sb.Length = mark; sb.Append(Showing(shown, rest.Count)); break; }
                shown++; tally.Mark(e.RelPath);
            }
        }
        // A config already shown as a DLL's paired config is a rendered row too, so it counts.
        foreach (var e in cfgHits) if (shownCfg.Contains(e.RelPath)) tally.Mark(e.RelPath);
        sb.Append(tail);
        return sb.ToString().TrimEnd('\n') + Accounting();
    }

    /// <summary>One DLL's full detail block; <paramref name="added"/> collects the paired configs it newly marked as
    /// shown, so a block the cap takes back out can un-mark them and they do not count as rendered.</summary>
    static void AppendDetail(StringBuilder sb, SkseFileEntry e, SkseInventoryData d, HashSet<string> shownCfg,
                             List<string>? added = null)
    {
        sb.Append('\n').Append(e.Group.Length > 0 ? e.Group + "\\" : "").Append(e.FileName).Append("  ← ")
          .Append(e.WinningProvider ?? "(no active provider)").Append(" (").Append(e.ProviderKind).Append(")\n");
        if (e.ProviderCount > 1)
            sb.Append("  [!] contested by ").Append(e.ProviderCount).Append(" mods — full chain (winner first): ").Append(Chain(e)).Append('\n');

        var p = e.Plugin;
        // The service-level note is shown for any kind, since a dependency or unreadable DLL also needs the loader-path flag.
        if (e.Note is { } enote) sb.Append("  [!] ").Append(enote).Append('\n');
        // A null Plugin is the BSA-only or unprovided DLL, the one entry a peek cannot read, so the notice rides here too.
        if (p is null) { if (e.Note is null) sb.Append("  no static metadata\n"); AppendPeek(sb, e, d); return; }

        switch (p.Kind)
        {
            case SksePluginReader.SksePluginKind.LegacyQuery:
            case SksePluginReader.SksePluginKind.NotSkse:
            case SksePluginReader.SksePluginKind.Unreadable:
                sb.Append("  ").Append(p.Note).Append('\n');
                // No manifest, but the image's own file version is still readable and answers "which build is this?".
                if (VersionText(p, e.ModVersion) is { Length: > 0 } other) sb.Append("  version ").Append(other).Append('\n');
                if (p.Is64Bit == false) sb.Append("  [!] NOT an x64 image — a 32-bit DLL cannot load in Skyrim SE/AE.\n");
                // The import-table verdict rides every kind, and a debug-CRT build often shows up unclassifiable.
                AppendPeek(sb, e, d);
                return;   // Is64Bit == false is EXPLICITLY-determined non-x64; null (unknown) never triggers the claim (finding #1)
        }

        var v = p.Version!;
        sb.Append("  \"").Append(v.Name).Append("\" by ").Append(v.Author.Length > 0 ? v.Author : "(no author)");
        if (v.SupportEmail.Length > 0) sb.Append(" <").Append(v.SupportEmail).Append('>');
        sb.Append("\n  version ").Append(VersionText(p, e.ModVersion)).Append('\n');
        if (p.Is64Bit == false) sb.Append("  [!] NOT an x64 image — a 32-bit DLL cannot load in Skyrim SE/AE.\n");

        if (v.VersionIndependent)
        {
            var how = new List<string>();
            if (v.UsesAddressLibrary) how.Add("Address Library");
            if (v.UsesSignatureScanning) how.Add("signature scanning");
            sb.Append("  runtime compat: version-INDEPENDENT via ").Append(string.Join(" + ", how))
              .Append(" — loads on any supported game runtime");
            if (v.UsesAddressLibrary) sb.Append(" (needs the Address Library for SKSE Plugins mod installed)");
            sb.Append('\n');
        }
        else
        {
            string rt = v.CompatibleVersions.Count > 0 ? string.Join(", ", v.CompatibleVersions) : "(none listed — will refuse every runtime!)";
            sb.Append("  runtime compat: version-LOCKED → loads ONLY on ").Append(rt)
              .Append("  [!] a game version outside this list = won't load\n");
        }
        var structs = new List<string>();
        if (v.UsesUpdatedStructs) structs.Add("post-1.6.629 structs");
        if (v.DeclaresNoStructs) structs.Add("no CommonLib structs");
        if (structs.Count > 0) sb.Append("  struct compat: ").Append(string.Join(", ", structs)).Append('\n');
        if (v.MinimumXseVersion is { } xse) sb.Append("  requires SKSE ≥ ").Append(xse).Append('\n');

        // Paired configs: any config under SKSE\Plugins whose basename stem matches the DLL. Best-effort association.
        string stem = System.IO.Path.GetFileNameWithoutExtension(e.FileName);
        var cfgs = d.Configs.Where(c => System.IO.Path.GetFileNameWithoutExtension(c.FileName)
            .StartsWith(stem, StringComparison.OrdinalIgnoreCase)).ToList();
        if (cfgs.Count > 0)
        {
            sb.Append("  configs: ").Append(string.Join(", ", cfgs.Select(c =>
                (c.Group.Length > 0 ? c.Group + "\\" : "") + c.FileName + Provider(c)))).Append('\n');
            foreach (var c in cfgs) if (shownCfg.Add(c.RelPath)) added?.Add(c.RelPath);
        }
        AppendPeek(sb, e, d);
    }

    /// <summary>The peek block for one DLL: what the image statically contains, with the framing line that says it is
    /// not what the code does; renders nothing unless a peek ran.</summary>
    static void AppendPeek(StringBuilder sb, SkseFileEntry e, SkseInventoryData d)
    {
        if (e.Peek is not { } peek)
        {
            // Per-entry, so a mixed match says it too rather than reading as an empty peek.
            if (d.PeekRequested)
                sb.Append("  (not peeked: no loose winner — SKSE loads loose DLLs only, so there is no image the game would read)\n");
            return;
        }
        sb.Append("  ── peek (what the image contains) ──\n");
        if (peek.Failed) { sb.Append("  [!] ").Append(peek.Note).Append('\n'); return; }

        // ── imports ──
        var imports = e.Plugin?.Imports;
        if (imports is null)
            sb.Append("  imports: UNKNOWN — the import directory could not be walked (corrupt or absent optional header)\n");
        else if (imports.Count == 0)
            sb.Append("  imports: none (walked, genuinely empty)\n");
        else
        {
            sb.Append("  imports (").Append(imports.Count).Append("): ").Append(string.Join(", ", imports)).Append('\n');
            var hooks = imports.Where(i => HookImports.ContainsKey(i)).ToList();
            foreach (var h in hooks) sb.Append("    → ").Append(h).Append(": ").Append(HookImports[h]).Append('\n');
            // Bundled-dependency attribution: an import satisfied by a sibling non-plugin DLL in the same layer.
            var siblings = d.Dlls.Where(x => x.Plugin is { Kind: SksePluginReader.SksePluginKind.NotSkse })
                .Select(x => x.FileName).Where(f => imports.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
            if (siblings.Count > 0)
                sb.Append("    → bundled with this plugin (a non-plugin DLL in this layer satisfies it): ")
                  .Append(string.Join(", ", siblings)).Append('\n');
        }
        AppendDebugCrt(sb, e);

        // ── config surface ──
        if (peek.ConfigPaths.Count > 0)
        {
            sb.Append("  config paths embedded (").Append(peek.ConfigPaths.Count).Append("):\n");
            foreach (var c in peek.ConfigPaths.Take(PeekListCap)) sb.Append("    - ").Append(c).Append('\n');
            if (peek.ConfigPaths.Count > PeekListCap)
                sb.Append("    ... [showing ").Append(PeekListCap).Append(" of ").Append(peek.ConfigPaths.Count).Append("]\n");
        }

        // ── plugin references, cross-checked ──
        if (peek.PluginRefs.Count > 0)
        {
            sb.Append("  plugin names embedded (").Append(peek.PluginRefs.Count).Append("):\n");
            foreach (var r in peek.PluginRefs.Take(PeekListCap))
            {
                string verdict = d.ActivePlugins is null ? ""
                    : d.ActivePlugins.Contains(r) ? "  (in your load order)"
                    : "  [!] NOT in your load order";
                sb.Append("    - ").Append(r).Append(verdict).Append('\n');
            }
            if (peek.PluginRefs.Count > PeekListCap)
                sb.Append("    ... [showing ").Append(PeekListCap).Append(" of ").Append(peek.PluginRefs.Count).Append("]\n");
        }

        sb.Append("  scanned ").Append(peek.RunsScanned).Append(" string run(s) over ")
          .Append(peek.BytesScanned / 1024).Append(" KB → showed ")
          .Append(peek.ConfigPaths.Count + peek.PluginRefs.Count)
          .Append(" (the classes above are a FILTER over the image, not the whole haystack)\n");
        sb.Append("  (imports/strings are what the image CONTAINS, never what the code DOES — behavior is unreadable by " +
                  "design. Absence proves nothing: many DLLs build their references at runtime or read them from configs.)\n");
    }

    /// <summary>Max entries per peek list before an explicit cut — a peek is per-DLL and readability is the point.</summary>
    const int PeekListCap = 40;

    /// <summary>Imports whose presence names a capability the DLL reaches for — facts about the import table, never behaviour.</summary>
    static readonly Dictionary<string, string> HookImports = new(StringComparer.OrdinalIgnoreCase)
    {
        ["d3d11.dll"] = "Direct3D 11 — touches graphics/rendering",
        ["dxgi.dll"] = "DXGI — touches the swapchain/presentation layer",
        ["d3dcompiler_47.dll"] = "D3D shader compiler — compiles shaders at runtime",
        ["dinput8.dll"] = "DirectInput — touches input handling",
        ["xinput1_3.dll"] = "XInput — touches controller input",
        ["ws2_32.dll"] = "Winsock — opens network sockets",
        ["winhttp.dll"] = "WinHTTP — makes HTTP requests",
        ["wininet.dll"] = "WinINet — makes internet requests",
    };

    /// <summary>The Debug-CRT verdict — the one peek line allowed "will not load" language, because it is a static
    /// loader fact checked against this machine; see docs/architecture/skse-layer.md.</summary>
    static void AppendDebugCrt(StringBuilder sb, SkseFileEntry e)
    {
        if (e.Plugin is not { Imports: not null } p) return;      // never walked ⇒ no claim either way
        var crt = p.DebugCrtImports;
        if (crt.Count == 0) return;
        sb.Append(DebugCrtVerdict(crt, SksePluginReader.IsSystemDllResolvable));
    }

    /// <summary>The one-line Debug-CRT verdict for the whole-layer summary; the probe is injected so both wordings are reachable.</summary>
    internal static string DebugCrtLayerVerdict(IReadOnlyList<string> crt, Func<string, bool> resolvable) =>
        crt.All(resolvable)
            ? "  loads on THIS machine (you have the debug runtime) — but error 126 for anyone without Visual Studio"
            : "  ≠ this machine — will NOT load (error 126: the debug runtime isn't here)";

    /// <summary>The Debug-CRT verdict text, pure with the machine probe injected so both wordings are reachable in one run.</summary>
    internal static string DebugCrtVerdict(IReadOnlyList<string> crt, Func<string, bool> resolvable)
    {
        var missing = crt.Where(c => !resolvable(c)).ToList();
        var sb = new StringBuilder();
        sb.Append("  [!] DEBUG BUILD — imports the debug C runtime: ").Append(string.Join(", ", crt)).Append('\n');
        if (missing.Count > 0)
            sb.Append("      → will NOT load: ").Append(string.Join(", ", missing))
              .Append(missing.Count == 1 ? " is" : " are").Append(" not present on this machine, so the loader fails with " +
                      "error 126 (ERROR_MOD_NOT_FOUND). The debug CRT ships only with Visual Studio and is not redistributable — " +
                      "this DLL was shipped as a Debug build by mistake. Ask its author for a Release build.\n");
        else
            sb.Append("      → it loads on THIS machine (you have the debug runtime installed — Visual Studio), but it will " +
                      "fail with error 126 for anyone who doesn't. If you built this, ship a Release build.\n");
        return sb.ToString();
    }

    /// <summary>The compat one-word tag for the terse roster: "AddrLib", "SigScan", or "LOCKED→[runtimes]".</summary>
    static string CompatTag(SksePluginReader.SkseVersionInfo v)
    {
        if (v.UsesAddressLibrary) return "AddrLib";
        if (v.UsesSignatureScanning) return "SigScan";
        return "LOCKED→" + (v.CompatibleVersions.Count > 0 ? string.Join("/", v.CompatibleVersions) : "?");
    }

    /// <summary>A section heading, laid whole; false means the budget had no room to start the section at all.</summary>
    internal static bool Head(StringBuilder sb, int cap, string head)
    {
        if (sb.Length + head.Length > cap) return false;
        sb.Append(head);
        return true;
    }

    /// <summary>The line that says how many sections the budget could not start, naming the max_chars the CALLER passed.</summary>
    internal static string SectionsMissed(int missed, int cap) =>
        "  ... [" + missed + " section(s) omitted at max_chars=" + cap + "; raise max_chars to see them]\n";

    /// <summary>The advice a cut row list carries where narrowing the answer is the other way out.</summary>
    internal const string FilterHint = " or use filter= to see all";

    /// <summary>The one cut notice a capped row list ends on, spelled once so its widest form can be charged up front.</summary>
    internal static string Showing(int shown, int total, string noun = "", string hint = "") =>
        "  ... [showing " + shown + " of " + total + (noun.Length > 0 ? " " + noun : "") + "; raise max_chars" + hint + "]\n";

    /// <summary>The chars a capped row list must hold back for that notice.</summary>
    internal static int CutRoom(int total, string noun = "", string hint = "") => Showing(total, total, noun, hint).Length;

    /// <summary>A plugin's version with the SOURCE it was read from, plus every other version in sight that disagrees;
    /// everything after the leading number is parenthesised, so a row joining its fields with " — " keeps one separator.
    /// The three version sources are in docs/architecture/skse-layer.md; pinned by SkseVersionSourceTests.</summary>
    internal static string VersionText(SksePluginReader.SksePluginInfo? p, string? modVersion)
    {
        string declared = p?.Version?.PluginVersion ?? "";
        string file = p?.FileVersion ?? "";
        string mod = modVersion ?? "";
        // No manifest: whatever version WAS read is the answer, labelled for what it is.
        if (declared.Length == 0)
        {
            if (file.Length == 0) return mod.Length == 0 ? "" : $"{mod} (mod meta.ini)";
            return Differs(file, mod) ? $"{file} (DLL file version; meta.ini {mod})" : $"{file} (DLL file version)";
        }
        var others = new List<string>();
        if (Differs(declared, file)) others.Add($"DLL file version {file}");
        // meta.ini is held back only when the file version already carries it.
        if (Differs(declared, mod) && (file.Length == 0 || Differs(file, mod))) others.Add($"meta.ini {mod}");
        return others.Count == 0
            ? $"{declared} (SKSE manifest)"
            : $"{declared} (SKSE manifest; {string.Join(", ", others)})";
    }

    /// <summary>Two version strings both present and NOT the same version, compared on their numeric prefix: a modder's
    /// tag is UNKNOWN, not different, so it is not reported as a disagreement.</summary>
    static bool Differs(string? a, string? b)
    {
        if (a is not { Length: > 0 } || b is not { Length: > 0 }) return false;   // an unread version says nothing about the one that was read
        string na = NumericPrefix(a), nb = NumericPrefix(b);
        if (na.Length == 0 || nb.Length == 0) return false;
        return !SksePluginReader.VersionsEqual(na, nb);
    }

    /// <summary>The dotted numeric head of a version string, a leading "v" dropped and any trailing tag cut.</summary>
    static string NumericPrefix(string s)
    {
        var t = s.Trim();
        if (t.Length > 0 && (t[0] == 'v' || t[0] == 'V')) t = t[1..];
        int end = 0;
        while (end < t.Length && (char.IsAsciiDigit(t[end]) || t[end] == '.')) end++;
        return t[..end].Trim('.');
    }

    static string Provider(SkseFileEntry e) =>
        e.WinningProvider is null ? "  (no active provider)" : $"  ← {e.WinningProvider}";

    /// <summary>The full VFS conflict chain: winner first, then losers in precedence order, each tagged loose or BSA.</summary>
    static string Chain(SkseFileEntry e) =>
        e.Providers.Count == 0 ? "(no active provider)" : string.Join(" › ", e.Providers.Select(p => $"{p.Name} ({p.Kind})"));

    static bool AppendSubset(StringBuilder sb, string label, IReadOnlyList<SkseFileEntry> items, int cap, Func<SkseFileEntry, string> line,
                             RowTally? tally = null)
    {
        if (items.Count == 0) return true;
        // The heading goes in whole or the subset does not start, and only where its rows' cut notice fits too.
        if (!Head(sb, cap - CutRoom(items.Count, hint: FilterHint), "\n" + label + " (" + items.Count + "):\n")) return false;
        AppendCapped(sb, items, cap, line, tally);
        return true;
    }

    static void AppendCapped(StringBuilder sb, IReadOnlyList<SkseFileEntry> items, int cap, Func<SkseFileEntry, string> line,
                             RowTally? tally = null)
    {
        // The cut notice's widest spelling is held back before the first row.
        int room = cap - CutRoom(items.Count, hint: FilterHint);
        int shown = 0;
        foreach (var e in items)
        {
            var row = line(e) + "\n";
            // Measured against the row about to be written, not against what the buffer already holds.
            if (sb.Length + row.Length > room) { sb.Append(Showing(shown, items.Count, hint: FilterHint)); break; }
            sb.Append(row); shown++; tally?.Mark(e.RelPath);
        }
    }

    /// <summary>How many build-level caveat notes this answer carries — the accounting's <c>notes</c> count.</summary>
    internal static int NoteCount(SkseInventoryData d) => (d.ReadIncomplete ? 1 : 0) + d.Warnings.Count + d.BsaFailures.Count + d.RootFailures.Count;

    /// <summary>The json twin of the text render's "peek=true matched no DLL" notice — one spelling, so the reserve measures it.</summary>
    const string PeekNoDllNote = "peek=true matched no DLL at all — nothing was peeked.";

    /// <summary>The json twin of <see cref="Render"/>; rows are dropped from the tail at max_chars, never the serialized string.</summary>
    public static string RenderJson(SkseInventoryData d, string? filter, int cap, RowWindow window = default)
    {
        bool filtered = filter is { Length: > 0 };
        string f = filtered ? filter!.Trim() : "";
        bool In(string? x) => x is not null && x.Contains(f, StringComparison.OrdinalIgnoreCase);

        var allDlls = filtered ? d.Dlls.Where(e => e.MatchesDll(f)).OrderBy(e => e.FileName, StringComparer.OrdinalIgnoreCase).ToList()
                               : d.Dlls.ToList();
        var allCfgs = filtered ? d.Configs.Where(e => In(e.FileName) || In(e.WinningProvider) || In(e.Group)).ToList()
                               : new List<SkseFileEntry>();
        int total = allDlls.Count + allCfgs.Count;
        var dlls = window.Apply(allDlls);
        var cfgs = window.After(allDlls.Count, dlls.Count).Apply(allCfgs);
        int windowed = dlls.Count + cfgs.Count;
        // The census states the population THIS document answers over, so no number describes a wider set than its rows.
        var all = Split(allDlls);
        var censusCfgs = filtered ? allCfgs : d.Configs;
        int notes = NoteCount(d);
        int rendered = 0;
        int folderCount = d.Configs.Select(e => e.Group).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        // The tail is paid for inside max_chars, exactly as the text render's own reserve does.
        int callerCap = cap;   // the overrun member is measured against what the CALLER passed
        // The caveat lists are cut ONCE, through the SAME cut the text tail takes off the CALLER's max_chars, so the
        // two lanes name the same entries; the reserve composes that bounded block, not the whole lists.
        var caveats = SkseJsonDoc.CutCaveats(d.ReadIncomplete, d.Warnings, d.BsaFailures, d.RootFailures, callerCap);
        cap = Math.Max(1, cap - SkseJsonDoc.TailReserve(caveats,
            TransportAccounting.Widest(total, windowed, window, notes),
            new[] { "dlls", "configs", "config_folders" },
            tw => { tw.WriteString("peek_note", PeekNoDllNote); tw.WriteNumber("config_folders_truncated", folderCount); }));

        return SkseJsonDoc.Write(SkseTools.SkseFamily.Inventory, filter, d.ProfileName, callerCap, (w, ms) =>
        {
            var depths = new JsonWire.JsonUnitDepths(w.CurrentDepth);
            SkseJsonDoc.Nullable(w, "installed_runtime", d.InstalledRuntime);
            w.WriteStartObject("totals");
            w.WriteNumber("dlls", all.Loaded.Count);
            w.WriteNumber("subfolder_dlls", all.Subfolder.Count);
            w.WriteNumber("configs", censusCfgs.Count);
            w.WriteNumber("config_folders", censusCfgs.Select(e => e.Group).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            // Uncategorized files are counted, never listed, so a filtered document does not state that number.
            if (!filtered) w.WriteNumber("other_files", d.OtherFileCount);
            w.WriteNumber("modern", all.Modern.Count);
            w.WriteNumber("legacy_query", all.Legacy.Count);
            w.WriteNumber("non_plugin", all.NotPlugin.Count);
            w.WriteNumber("bsa_only", all.BsaOnly.Count);
            w.WriteNumber("unreadable", all.Unreadable.Count);
            w.WriteNumber("address_library", all.Modern.Count(e => e.Plugin!.Version!.UsesAddressLibrary));
            w.WriteNumber("signature_scanning", all.Modern.Count(e => e.Plugin!.Version!.UsesSignatureScanning));
            w.WriteNumber("version_locked", all.Locked.Count);
            w.WriteEndObject();

            w.WriteStartArray("dlls");
            int dllRows = 0;
            foreach (var e in dlls)
            {
                if (!SkseJsonDoc.Fits(w, ms, cap, JsonWire.MeasureUnit(depths.SkseRows, dllRows > 0, mw => WriteDllJson(mw, e, d)))) break;
                WriteDllJson(w, e, d);
                dllRows++;
                rendered++;
            }
            w.WriteEndArray();

            w.WriteStartArray("configs");
            int cfgRows = 0;
            foreach (var e in cfgs)
            {
                if (!SkseJsonDoc.Fits(w, ms, cap, JsonWire.MeasureUnit(depths.SkseRows, cfgRows > 0, mw => WriteConfigFileJson(mw, e)))) break;
                WriteConfigFileJson(w, e);
                cfgRows++;
                rendered++;
            }
            w.WriteEndArray();

            // A filtered document lists its matching configs individually, so it omits this table rather than writing an empty one.
            if (!filtered)
            {
                w.WriteStartArray("config_folders");
                int folders = 0;
                foreach (var g in d.Configs.GroupBy(e => e.Group, StringComparer.OrdinalIgnoreCase)
                             .Select(g => (Name: g.Key.Length == 0 ? "(top level)" : g.Key, Count: g.Count(),
                                           Providers: g.Select(e => e.WinningProvider).Where(x => x is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                                           Contested: g.Count(e => e.ProviderCount > 1)))
                             .OrderByDescending(g => g.Count).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (!SkseJsonDoc.Fits(w, ms, cap,
                            JsonWire.MeasureUnit(depths.SkseRows, folders > 0,
                                                 mw => WriteConfigFolderJson(mw, g.Name, g.Count, g.Providers!, g.Contested)))) break;
                    WriteConfigFolderJson(w, g.Name, g.Count, g.Providers!, g.Contested);
                    folders++;
                }
                w.WriteEndArray();
                // Not row-list rows, so the accounting does not count them — the cut says so here instead.
                if (folders < folderCount) w.WriteNumber("config_folders_truncated", folderCount - folders);
            }

            if (d.PeekRequested && allDlls.Count == 0)
                w.WriteString("peek_note", PeekNoDllNote);
            SkseJsonDoc.Caveats(w, caveats);
            TransportAccounting.WriteJson(w, TransportAccounting.Tally(total, windowed, rendered, window, notes));
        });
    }

    static void WriteDllJson(Utf8JsonWriter w, SkseFileEntry e, SkseInventoryData d)
    {
        w.WriteStartObject();
        w.WriteString("rel_path", e.RelPath);
        w.WriteString("file_name", e.FileName);
        w.WriteString("group", e.Group);
        w.WriteBoolean("loader_scoped", e.Group.Length == 0);
        SkseJsonDoc.Nullable(w, "winning_provider", e.WinningProvider);
        w.WriteString("provider_kind", e.ProviderKind);
        w.WriteNumber("provider_count", e.ProviderCount);
        SkseJsonDoc.Providers(w, e.Providers);
        SkseJsonDoc.Nullable(w, "note", e.Note);
        var p = e.Plugin;
        SkseJsonDoc.Nullable(w, "kind", p is null ? null : p.Kind.ToString().ToLowerInvariant());
        if (p?.Is64Bit is { } bits) w.WriteBoolean("is_64bit", bits); else w.WriteNull("is_64bit");
        SkseJsonDoc.Strings(w, "debug_crt_imports", p?.DebugCrtImports ?? Array.Empty<string>());
        // The two versions that are NOT the manifest's declaration, each null when there was none to read.
        SkseJsonDoc.Nullable(w, "file_version", p?.FileVersion);
        SkseJsonDoc.Nullable(w, "mod_version", e.ModVersion);
        // Null, not [], when the import walk never ran or failed: absence of evidence is not evidence of absence.
        if (p?.Imports is { } imports) SkseJsonDoc.Strings(w, "imports", imports); else w.WriteNull("imports");
        if (p?.Version is { } v)
        {
            w.WriteStartObject("version");
            w.WriteString("name", v.Name);
            w.WriteString("author", v.Author);
            w.WriteString("support_email", v.SupportEmail);
            w.WriteString("plugin_version", v.PluginVersion);
            w.WriteBoolean("version_independent", v.VersionIndependent);
            w.WriteBoolean("uses_address_library", v.UsesAddressLibrary);
            w.WriteBoolean("uses_signature_scanning", v.UsesSignatureScanning);
            w.WriteBoolean("uses_updated_structs", v.UsesUpdatedStructs);
            w.WriteBoolean("declares_no_structs", v.DeclaresNoStructs);
            SkseJsonDoc.Strings(w, "compatible_versions", v.CompatibleVersions);
            SkseJsonDoc.Nullable(w, "minimum_xse_version", v.MinimumXseVersion);
            if (d.InstalledRuntime is { } rt && !v.VersionIndependent)
                w.WriteBoolean("loads_on_installed_runtime", SksePluginReader.RuntimeCompatible(v, rt));
            w.WriteEndObject();
        }
        else w.WriteNull("version");
        if (e.Peek is { } peek)
        {
            w.WriteStartObject("peek");
            SkseJsonDoc.Strings(w, "config_paths", peek.ConfigPaths);
            SkseJsonDoc.Strings(w, "plugin_refs", peek.PluginRefs);
            w.WriteNumber("runs_scanned", peek.RunsScanned);
            w.WriteNumber("bytes_scanned", peek.BytesScanned);
            SkseJsonDoc.Nullable(w, "note", peek.Note);
            w.WriteEndObject();
        }
        else w.WriteNull("peek");
        w.WriteEndObject();
    }

    static void WriteConfigFileJson(Utf8JsonWriter w, SkseFileEntry e)
    {
        w.WriteStartObject();
        w.WriteString("rel_path", e.RelPath);
        w.WriteString("file_name", e.FileName);
        w.WriteString("group", e.Group);
        SkseJsonDoc.Nullable(w, "winning_provider", e.WinningProvider);
        w.WriteString("provider_kind", e.ProviderKind);
        w.WriteNumber("provider_count", e.ProviderCount);
        SkseJsonDoc.Providers(w, e.Providers);
        w.WriteEndObject();
    }

    /// <summary>One config-folder row, written the same way the measurement measured it.</summary>
    static void WriteConfigFolderJson(Utf8JsonWriter w, string folder, int files, IEnumerable<string> providers, int contested)
    {
        w.WriteStartObject();
        w.WriteString("folder", folder);
        w.WriteNumber("files", files);
        SkseJsonDoc.Strings(w, "providers", providers);
        w.WriteNumber("contested", contested);
        w.WriteEndObject();
    }

    /// <summary>The build-level caveats as one string, so a render can charge them before its rows are laid.</summary>
    static string Caveats(SkseInventoryData d, int cap)
    {
        var sb = new StringBuilder();
        if (d.ReadIncomplete)
            sb.Append("[!] a BSA or a loose mod folder failed to read this build, so a file present only in it may be missing from this inventory (Q3).\n");
        sb.Append(BatchRender.CaveatBlockLines(cap, BatchRender.WarningList(d.Warnings),
            BatchRender.ArchiveFailureList(d.BsaFailures), BatchRender.RootFailureList(d.RootFailures)));
        return sb.ToString();
    }
}

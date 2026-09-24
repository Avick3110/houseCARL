using System.Text;
using System.Text.Json;
using HousecarlCore;

namespace HousecarlMcp;

// The pairing family's text and json renders and its DLL load verdict for housecarl_skse; contract in docs/architecture/skse-layer.md.
/// <summary>Renders <see cref="NativePairingAuditData"/>: a health summary, then the diagnostics in full, then the
/// accounted-for baseline and the paired-healthy classes grouped by implementing mod. filter= shows a class in full;
/// see docs/architecture/skse-layer.md.</summary>
static class NativePairingWire
{
    /// <summary>One candidate DLL's static load verdict: LOADS, VERIFY (locked, runtime unknown), or DEAD.</summary>
    enum DllFate { Loads, Verify, Dead }

    /// <summary>The verdict a DLL line carries, with the debug-build note appended when it applies (#417); the verdict
    /// itself is untouched by the note.</summary>
    static (DllFate Fate, string Detail) Judge(NativePairedDll d, string? runtime)
    {
        var (fate, detail) = Verdict(d, runtime);
        if (fate != DllFate.Dead && d.Info is { } info && info.DebugCrtImports.Count > 0)
        {
            var crt = string.Join(", ", info.DebugCrtImports);
            // VERIFY is a version question, so the clause is additive there rather than a 'but'.
            detail += fate == DllFate.Loads
                ? $" — but it imports the debug CRT ({crt}), so it loads HERE and fails with error 126 for anyone " +
                  "without the debug runtime"
                : $" — and it imports the debug CRT ({crt}), so even where the version matches it loads HERE only and " +
                  "fails with error 126 for anyone without the debug runtime";
        }
        return (fate, detail);
    }

    /// <summary>The static load verdict alone — the blocker, the loader's era rules, and the version lock.</summary>
    static (DllFate Fate, string Detail) Verdict(NativePairedDll d, string? runtime)
    {
        if (d.LoadBlocker is { } b) return (DllFate.Dead, b);
        if (d.Info is not { } info) return (DllFate.Verify, "no static manifest read");   // defensive: blocker-less entries carry Info by construction
        if (info.Kind == SksePluginReader.SksePluginKind.LegacyQuery)
        {
            // The query-only-on-AE arm of the load rule — this tool's headline breakage class.
            if (runtime is { } rt2 && SksePluginReader.IsAeRuntime(rt2))
                return (DllFate.Dead, $"query-only SE/VR-era plugin — the AE loader (installed game is {rt2}) loads only version-data plugins, so it will NOT load");
            if (runtime is not null)
                return (DllFate.Loads, "legacy SE/VR plugin — loads on this SE runtime, but its metadata is set at runtime (not statically verifiable)");
            return (DllFate.Verify, "legacy SE/VR-era query-only plugin — loads on SE (1.5.x) but NOT on an AE (1.6+) runtime; installed game version unknown, verify");
        }
        var v = info.Version!;
        if (v.VersionIndependent)
            return (DllFate.Loads, $"version-independent ({(v.UsesAddressLibrary ? "Address Library" : "signature scanning")})");
        string locked = v.CompatibleVersions.Count > 0 ? string.Join(", ", v.CompatibleVersions) : "(none listed!)";
        if (runtime is null)
            return (DllFate.Verify, $"version-LOCKED → {locked} — installed game version unknown, verify it matches");
        return SksePluginReader.RuntimeCompatible(v, runtime)
            ? (DllFate.Loads, $"version-LOCKED → {locked} = installed {runtime}")
            : (DllFate.Dead, $"version-LOCKED → {locked} ≠ installed {runtime} — will NOT load on this game version");
    }

    /// <summary>A paired class's verdict is the BEST fate among its candidates, since which DLL implements it is not statically knowable.</summary>
    static DllFate BestFate(NativeClassEntry c, string? runtime)
    {
        var best = DllFate.Dead;
        foreach (var d in c.PairedDlls)
        {
            var (f, _) = Judge(d, runtime);
            if (f == DllFate.Loads) return DllFate.Loads;
            if (f == DllFate.Verify) best = DllFate.Verify;
        }
        return best;
    }

    /// <summary>True when every candidate DLL that could implement the class is a debug build and at least one loads
    /// here (#417); a clean LOADS or VERIFY candidate disqualifies, and only a DEAD one is passed over.</summary>
    static bool IsDebugOnly(NativeClassEntry c, string? runtime)
    {
        bool any = false;
        foreach (var dll in c.PairedDlls)
        {
            var fate = Judge(dll, runtime).Fate;
            if (fate == DllFate.Dead) continue;
            if (dll.Info is not { } i || i.DebugCrtImports.Count == 0) return false;
            any |= fate == DllFate.Loads;
        }
        return any;
    }

    /// <summary>What this family's accounting counts: the native-declaring CLASS rows.</summary>
    internal const string RowNoun = "class(es)";

    /// <summary>How many build-level caveat notes this answer carries — the accounting's <c>notes</c> count.</summary>
    internal static int NoteCount(NativePairingAuditData d) => (d.ReadIncomplete ? 1 : 0) + d.Warnings.Count + d.BsaFailures.Count + d.RootFailures.Count;

    /// <summary>The class population split into the buckets the view reports — one function, so summary and rows cannot drift.</summary>
    readonly record struct ClassSplit(List<NativeClassEntry> Engine, List<NativeClassEntry> SkseCore,
                                      List<NativeClassEntry> Unpaired, List<NativeClassEntry> Dead,
                                      List<NativeClassEntry> Verify, List<NativeClassEntry> DebugBuilds,
                                      List<NativeClassEntry> Healthy);

    static ClassSplit Classify(IReadOnlyList<NativeClassEntry> classes, string? runtime)
    {
        var third = classes.Where(c => c.Provenance == NativeProvenance.ThirdParty).ToList();
        // One fate pass per paired class, so the section split and the per-DLL tags cannot disagree.
        var byFate = third.Where(c => c.Rung != NativePairingRung.Unpaired).ToLookup(c => BestFate(c, runtime));
        var loads = byFate[DllFate.Loads].ToList();
        // #417: a class whose only loadable candidate is a DEBUG build is a finding of its own, not a healthy-roster line.
        var debugBuilds = loads.Where(c => IsDebugOnly(c, runtime)).ToList();
        return new ClassSplit(
            classes.Where(c => c.Provenance == NativeProvenance.Engine).ToList(),
            classes.Where(c => c.Provenance == NativeProvenance.SkseCore).ToList(),
            third.Where(c => c.Rung == NativePairingRung.Unpaired).ToList(),
            byFate[DllFate.Dead].ToList(),
            byFate[DllFate.Verify].ToList(),
            debugBuilds,
            loads.Except(debugBuilds).ToList());
    }

    public static string Render(NativePairingAuditData d, string? filter, int cap, RowWindow window = default, int trailer = 0)
    {
        if (filter is { Length: > 0 }) return RenderFiltered(d, filter.Trim(), cap, window, trailer);

        // The summary states the WHOLE audit; limit=/offset= window only the classes the sections LIST.
        int notes = NoteCount(d);
        var rows = window.Apply(d.Classes);
        int reserve = TransportAccounting.Reserve(d.Classes.Count, rows.Count, window, notes, RowNoun);
        // Charged before the sections render, for the same reason the other two families charge theirs.
        var tail = "\n(scope: what the winning compiled scripts DECLARE, statically paired to what their mods ship. 'Paired' means the " +
                   "co-shipment evidence is plausible and a candidate DLL loads — NEVER that the DLL registers exactly these functions " +
                   "(registration is runtime behavior, the honest ceiling). Which mods CALL an unpaired class is not scanned (a possible Wave 2).)\n" +
                   Caveats(d, cap) +
                   "\n→ filter='<class/mod/DLL>' for full detail: native function names, pairing evidence, per-DLL manifests and load verdicts.";
        var tally = new RowTally();

        var all = Classify(d.Classes, d.InstalledRuntime);
        var w = Classify(rows, d.InstalledRuntime);
        // The accounted-for line, the loader alarm and the healthy-roster heading are charged with the tail.
        int alwaysWritten =
            ("\naccounted for: " + w.Engine.Count + " engine class(es) (carried by an official archive — implemented by the game executable) · " +
             w.SkseCore.Count + " SKSE-core class(es) (skse64's script additions — implemented by the game-root loader)").Length +
            ("\n  [!] SKSE-core classes are present but no skse64 loader is visible (game root or enabled mods' Root\\ folders) — if SKSE isn't actually installed, every one of these is dead").Length +
            ("\npaired healthy (" + w.Healthy.Count + " class(es)) — implementing mod ← its classes:\n").Length +
            HealthyCut.Length;
        // cap stays the caller's max_chars; budget is the room the sections have once the tail is charged.
        int budget = Math.Max(1, cap - trailer - reserve - tail.Length - alwaysWritten - SkseRenderParts.SectionsMissed(9, cap).Length);
        // The always-written healthy roster lays its rows in the room reserved for it, above the sections' ceiling.
        int healthyCeil = budget + alwaysWritten;
        // A section starts only where the cut notice its rows may end on fits too.
        int Room(int n) => budget - SkseRenderParts.CutRoom(n, hint: SkseRenderParts.FilterHint);
        int missed = 0;
        // Every section below the summary states the WINDOW, the accounted-for baseline included.
        var engine = w.Engine; var skseCore = w.SkseCore;
        var unpaired = w.Unpaired; var dead = w.Dead; var verify = w.Verify;
        var debugBuilds = w.DebugBuilds; var healthy = w.Healthy;
        // A baseline class is accounted for by the engine / SKSE-core count line, not by a row of its own.
        foreach (var c in rows)
            if (c.Provenance != NativeProvenance.ThirdParty) tally.Mark(c.ClassName);

        var sb = new StringBuilder();
        sb.Append("native pairing audit — profile '").Append(d.ProfileName).Append("' — ")
          .Append(d.PexScanned).Append(" compiled script(s) scanned, ").Append(d.Classes.Count)
          .Append(" class(es) declare native functions\n");
        if (d.InstalledRuntime is { } rt) sb.Append("installed game runtime: ").Append(rt).Append('\n');
        else sb.Append("installed game runtime: could not be resolved — version-LOCKED findings degrade to 'verify'\n");

        if (all.Dead.Count == 0 && all.Unpaired.Count == 0 && all.Verify.Count == 0 && all.DebugBuilds.Count == 0)
        {
            sb.Append("✓ every third-party native class pairs to a mod whose DLL statically loads — nothing dead, nothing unpaired");
            // The checkmark must not claim a universal the scan did not verify: unreadable .pex were never examined.
            sb.Append(d.Unreadable.Count > 0 ? $" ({d.Unreadable.Count} unreadable .pex NOT examined — see below).\n" : ".\n");
        }
        else
        {
            sb.Append(all.Dead.Count > 0 ? "[!] " : "✓ no dead pairings. ");
            if (all.Dead.Count > 0) sb.Append(all.Dead.Count).Append(" class(es) PAIRED BUT DEAD — scripts installed, nothing that could implement them loads");
            if (all.Verify.Count > 0) sb.Append(all.Dead.Count > 0 ? "   ·   " : "").Append(all.Verify.Count).Append(" pairing(s) need a version check");
            if (all.Unpaired.Count > 0) sb.Append(all.Dead.Count > 0 || all.Verify.Count > 0 ? "   ·   " : "").Append(all.Unpaired.Count).Append(" class(es) UNPAIRED (verify)");
            if (all.DebugBuilds.Count > 0) sb.Append(all.Dead.Count > 0 || all.Verify.Count > 0 || all.Unpaired.Count > 0 ? "   ·   " : "")
                                         .Append(all.DebugBuilds.Count).Append(" class(es) paired only to a DEBUG BUILD");
            sb.Append('\n');
        }

        // ── Diagnostics first, in full. ──
        if (dead.Count > 0 && !SkseRenderParts.Head(sb, Room(dead.Count), "\nPAIRED BUT DEAD — the high-confidence finding: every candidate DLL statically will not load, so every native these scripts declare is a silent no-op in game (" + dead.Count + "):\n")) missed++;
        else if (dead.Count > 0)
        {
            AppendCapped(sb, dead, budget, c => DeadLine(c, d.InstalledRuntime), tally, c => c.ClassName);
        }
        if (verify.Count > 0 && !SkseRenderParts.Head(sb, Room(verify.Count), "\npaired, version-LOCKED, runtime unknown — verify the listed runtime matches your game (" + verify.Count + "):\n")) missed++;
        else if (verify.Count > 0)
        {
            AppendCapped(sb, verify, budget, c => DeadLine(c, d.InstalledRuntime), tally, c => c.ClassName);
        }
        if (unpaired.Count > 0 && !SkseRenderParts.Head(sb, Room(unpaired.Count), "\nUNPAIRED — no mod shipping these scripts (winner or chain) ships any SKSE plugin DLL (" + unpaired.Count +
                "). A VERIFY flag, not 'broken': most often a declaration copy of a framework that isn't installed — the calls will silently no-op if anything uses them:\n")) missed++;
        else if (unpaired.Count > 0)
        {
            AppendCapped(sb, unpaired, budget, c =>
                $"  - {c.ClassName} ({c.NativeCount} native fn) ← {c.WinningProvider ?? "(no provider)"} ({c.ProviderKind})", tally, c => c.ClassName);
        }
        if (debugBuilds.Count > 0 && !SkseRenderParts.Head(sb, Room(debugBuilds.Count), "\nDEBUG BUILD — these load on THIS machine and nowhere else (" + debugBuilds.Count +
                "). The debug C runtime ships with Visual Studio and is not redistributable, so the DLL fails with " +
                "error 126 for anyone without it and every native these scripts declare is a silent no-op there. " +
                "If you built it, ship a Release build; if you installed it, ask its author for one:\n")) missed++;
        else if (debugBuilds.Count > 0)
        {
            AppendCapped(sb, debugBuilds, budget, c => DeadLine(c, d.InstalledRuntime), tally, c => c.ClassName);
        }
        if (d.Unreadable.Count > 0 && !SkseRenderParts.Head(sb, Room(d.Unreadable.Count), "\nunreadable .pex — could not be parsed, NOT counted as native-free (" + d.Unreadable.Count + "):\n")) missed++;
        else if (d.Unreadable.Count > 0)
        {
            AppendCapped(sb, d.Unreadable, budget, u => $"  - {u.RelPath}: {u.Reason}{(u.WinningProvider is { } p ? $"  [← {p}]" : "")}");
        }

        // ── Accounted-for baseline: every row of THIS page that is not a finding, so nothing on it is dropped. ──
        sb.Append("\naccounted for: ").Append(engine.Count).Append(" engine class(es) (carried by an official archive — implemented by the game executable) · ")
          .Append(skseCore.Count).Append(" SKSE-core class(es) (skse64's script additions — implemented by the game-root loader)");
        // Tri-state: null is "the check itself failed", never a checked-and-absent verdict. The alarm is a
        // build-level fact, so it rides the whole audit's count rather than the window's.
        if (all.SkseCore.Count > 0 && d.SkseLoaderSeen == false)
            sb.Append("\n  [!] SKSE-core classes are present but no skse64 loader is visible (game root or enabled mods' Root\\ folders) — if SKSE isn't actually installed, every one of these is dead");
        else if (all.SkseCore.Count > 0 && d.SkseLoaderSeen is null)
            sb.Append("\n  (skse64 loader visibility could not be checked)");
        sb.Append("\npaired healthy (").Append(healthy.Count).Append(" class(es)) — implementing mod ← its classes:\n");
        foreach (var g in healthy.GroupBy(c => c.PairedMod ?? "(?)", StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var row = "  - " + g.Key + ": " + string.Join(", ", g.Select(c => c.ClassName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)) + "\n";
            if (sb.Length + row.Length > healthyCeil - HealthyCut.Length) { sb.Append(HealthyCut); break; }
            sb.Append(row);
            foreach (var c in g) tally.Mark(c.ClassName);
        }

        if (missed > 0) sb.Append(SkseRenderParts.SectionsMissed(missed, cap));
        sb.Append(tail);
        return sb.ToString().TrimEnd('\n')
             + TransportAccounting.Compose(TransportAccounting.Tally(d.Classes.Count, rows.Count, tally.Count, window, notes),
                                           RowNoun, everySentence: false);
    }

    /// <summary>The healthy roster's cut marker. Spelled once so its room is charged before the first group row.</summary>
    const string HealthyCut = "  ... [remaining healthy groups omitted; raise max_chars]\n";

    /// <summary>The one-block render of a dead/verify pairing: the class line, then each candidate DLL's fate.</summary>
    static string DeadLine(NativeClassEntry c, string? runtime)
    {
        var sb = new StringBuilder();
        sb.Append("  - ").Append(c.ClassName).Append(" (").Append(c.NativeCount).Append(" native fn) ← ")
          .Append(c.WinningProvider ?? "(no provider)")
          .Append(c.Rung == NativePairingRung.ChainMod ? $" — paired via the conflict chain to {c.PairedMod}" : $" — paired to {c.PairedMod}");
        foreach (var dll in c.PairedDlls)
            sb.Append("\n      ").Append(DllLine(dll, runtime, withVersion: false));
        return sb.ToString();
    }

    /// <summary>The per-DLL fate line, shared by the default and filter= views so the two cannot drift.</summary>
    static string DllLine(NativePairedDll dll, string? runtime, bool withVersion)
    {
        var (fate, detail) = Judge(dll, runtime);
        var sb = new StringBuilder();
        sb.Append(fate switch { DllFate.Dead => "[DEAD] ", DllFate.Verify => "[VERIFY] ", _ => "[LOADS] " })
          .Append(dll.Group.Length > 0 ? dll.Group + "\\" : "").Append(dll.FileName);
        if (withVersion && dll.Info?.Version is { } v) sb.Append("  \"").Append(v.Name).Append("\" v").Append(SkseRenderParts.VersionText(dll.Info, null));
        sb.Append(" — ").Append(detail);
        return sb.ToString();
    }

    /// <summary>filter=: full detail for every matching class — the declared native functions, the pairing evidence,
    /// each candidate DLL's manifest and load verdict, and the conflict chain.</summary>
    static string RenderFiltered(NativePairingAuditData d, string filter, int cap, RowWindow window = default, int trailer = 0)
    {
        bool In(string? s) => s is not null && s.Contains(filter, StringComparison.OrdinalIgnoreCase);
        bool Match(NativeClassEntry c) => In(c.ClassName) || In(c.RelPath) || In(c.WinningProvider) || In(c.PairedMod)
            || c.PairedDlls.Any(x => In(x.FileName)) || c.Providers.Any(p => In(p.Name));
        var allHits = d.Classes.Where(Match).OrderBy(c => c.ClassName, StringComparer.OrdinalIgnoreCase).ToList();

        int notes = NoteCount(d);
        var hits = window.Apply(allHits);
        int reserve = TransportAccounting.Reserve(allHits.Count, hits.Count, window, notes, RowNoun);
        // The caveats close this view too, so they are charged with the accounting rather than appended past the cap.
        var tail = "\n" + Caveats(d, cap);
        // cap stays the caller's max_chars; budget is the room the class blocks have once the tail is charged.
        string ClassesCut(int shown) => "\n" + SkseRenderParts.Showing(shown, hits.Count, "classes");
        int budget = Math.Max(1, cap - trailer - reserve - tail.Length - ClassesCut(hits.Count).Length);
        var tally = new RowTally();
        string Accounting() => TransportAccounting.Compose(
            TransportAccounting.Tally(allHits.Count, hits.Count, tally.Count, window, notes), RowNoun, everySentence: false);

        var sb = new StringBuilder();
        sb.Append("native pairing audit — filter '").Append(filter).Append("' — ")
          .Append(allHits.Count).Append(" class(es) match [profile '").Append(d.ProfileName).Append("']\n");
        if (allHits.Count == 0)
        {
            // The suggestion pool spans every axis Match filters on; PluginNameSuggest dedups and skips empties.
            var pool = d.Classes.Select(c => c.ClassName)
                .Concat(d.Classes.Select(c => c.WinningProvider).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!))
                .Concat(d.Classes.Select(c => c.PairedMod).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!))
                .Concat(d.Classes.SelectMany(c => c.PairedDlls.Select(x => x.FileName)));
            sb.Append("\nno native-declaring class matched. ").Append(HousecarlCore.PluginNameSuggest.DidYouMean(filter, pool));
            sb.Append(tail);   // a "no match" over an incompletely-read build must carry the caveat (Q3)
            return sb.ToString().TrimEnd('\n') + Accounting();
        }

        int shown = 0;
        foreach (var c in hits)
        {
            int mark = sb.Length;
            sb.Append('\n').Append(c.ClassName).Append("  (").Append(c.RelPath).Append(")\n");
            sb.Append("  provenance: ").Append(c.Provenance switch
            {
                NativeProvenance.Engine => "ENGINE — carried by an official archive; implemented by the game executable (baseline)",
                NativeProvenance.SkseCore => "SKSE CORE — skse64's script additions; implemented by the game-root loader (baseline)",
                _ => c.Rung switch
                {
                    NativePairingRung.SameMod => $"third-party, paired to its own provider ({c.PairedMod})",
                    NativePairingRung.ChainMod => $"third-party, paired via the conflict chain to {c.PairedMod}",
                    _ => "third-party, UNPAIRED — no mod in this file's chain ships any SKSE plugin DLL (verify)",
                },
            }).Append('\n');
            if (c.ProviderCount > 1)
                sb.Append("  [!] contested by ").Append(c.ProviderCount).Append(" sources (winner scanned): ")
                  .Append(string.Join(" › ", c.Providers.Select(p => $"{p.Name} ({p.Kind})"))).Append('\n');
            else
                sb.Append("  provider: ").Append(c.WinningProvider ?? "(none)").Append(" (").Append(c.ProviderKind).Append(")\n");
            foreach (var dll in c.PairedDlls)
                sb.Append("  ").Append(DllLine(dll, d.InstalledRuntime, withVersion: true)).Append('\n');
            sb.Append("  native functions (").Append(c.NativeCount).Append("): ");
            var fns = string.Join(", ", c.NativeFunctions);
            if (sb.Length + fns.Length > budget && c.NativeCount > 8)
                sb.Append(string.Join(", ", c.NativeFunctions.Take(8))).Append(", ... [").Append(c.NativeCount - 8).Append(" more; raise max_chars]");
            else sb.Append(fns);
            sb.Append('\n');
            if (sb.Length > budget) { sb.Length = mark; sb.Append(ClassesCut(shown)); break; }
            shown++; tally.Mark(c.ClassName);
        }
        // The caveats ride the filtered view too, so a partial hit never reads as a clean answer.
        sb.Append(tail);
        return sb.ToString().TrimEnd('\n') + Accounting();
    }

    static void AppendCapped<T>(StringBuilder sb, IReadOnlyList<T> items, int cap, Func<T, string> line,
                                RowTally? tally = null, Func<T, string>? key = null)
    {
        // The cut notice is charged before the first row, so a list that cuts says so inside max_chars.
        int room = cap - SkseRenderParts.CutRoom(items.Count, hint: SkseRenderParts.FilterHint);
        int shown = 0;
        foreach (var e in items)
        {
            // Composed once: measuring this row apart from writing it walked the paired DLLs twice per row.
            var row = line(e) + "\n";
            if (sb.Length + row.Length > room) { sb.Append(SkseRenderParts.Showing(shown, items.Count, hint: SkseRenderParts.FilterHint)); break; }
            sb.Append(row); shown++;
            if (tally is not null && key is not null) tally.Mark(key(e));
        }
    }

    /// <summary>The json twin of <see cref="Render"/>, with the per-DLL fates from the same <see cref="Judge"/>.</summary>
    public static string RenderJson(NativePairingAuditData d, string? filter, int cap, RowWindow window = default)
    {
        bool filtered = filter is { Length: > 0 };
        string f = filtered ? filter!.Trim() : "";
        bool In(string? x) => x is not null && x.Contains(f, StringComparison.OrdinalIgnoreCase);

        var allClasses = filtered
            ? d.Classes.Where(c => In(c.ClassName) || In(c.RelPath) || In(c.WinningProvider) || In(c.PairedMod)
                                || c.PairedDlls.Any(x => In(x.FileName)) || c.Providers.Any(pr => In(pr.Name)))
                       .OrderBy(c => c.ClassName, StringComparer.OrdinalIgnoreCase).ToList()
            : d.Classes.ToList();
        var classes = window.Apply(allClasses);
        // Classified over the population this document answers over, never a wider one.
        var all = Classify(allClasses, d.InstalledRuntime);
        int notes = NoteCount(d);
        int rendered = 0;
        // The tail — the unreadable-pex cut marker, caveats, accounting — is paid for inside max_chars.
        int callerCap = cap;   // the overrun member is measured against what the CALLER passed
        // The caveat lists are cut ONCE, through the SAME cut the text tail takes off the CALLER's max_chars, so the
        // two lanes name the same entries; the reserve composes that bounded block, not the whole lists.
        var caveats = SkseJsonDoc.CutCaveats(d.ReadIncomplete, d.Warnings, d.BsaFailures, d.RootFailures, callerCap);
        cap = Math.Max(1, cap - SkseJsonDoc.TailReserve(caveats,
            TransportAccounting.Widest(allClasses.Count, classes.Count, window, notes),
            new[] { "classes", "unreadable_pex" },
            tw => tw.WriteNumber("unreadable_pex_truncated", d.Unreadable.Count)));

        return SkseJsonDoc.Write(SkseTools.SkseFamily.Pairing, filter, d.ProfileName, callerCap, (w, ms) =>
        {
            var depths = new JsonWire.JsonUnitDepths(w.CurrentDepth);
            SkseJsonDoc.Nullable(w, "installed_runtime", d.InstalledRuntime);
            // Tri-state: null is "the check itself failed", never a checked-and-absent verdict.
            if (d.SkseLoaderSeen is { } seen) w.WriteBoolean("skse_loader_seen", seen); else w.WriteNull("skse_loader_seen");
            w.WriteStartObject("totals");
            w.WriteNumber("classes", allClasses.Count);
            // The .pex scan and the unparseable list are the SCAN, not the selection, so a filtered document omits
            // the counts; the array stays whole, since it carries the "NOT counted as native-free" caveat.
            if (!filtered) w.WriteNumber("pex_scanned", d.PexScanned);
            w.WriteNumber("engine", all.Engine.Count);
            w.WriteNumber("skse_core", all.SkseCore.Count);
            w.WriteNumber("unpaired", all.Unpaired.Count);
            w.WriteNumber("dead", all.Dead.Count);
            w.WriteNumber("verify", all.Verify.Count);
            w.WriteNumber("debug_build", all.DebugBuilds.Count);
            w.WriteNumber("healthy", all.Healthy.Count);
            if (!filtered) w.WriteNumber("unreadable_pex", d.Unreadable.Count);
            w.WriteEndObject();

            w.WriteStartArray("classes");
            foreach (var c in classes)
            {
                if (!SkseJsonDoc.Fits(w, ms, cap,
                        JsonWire.MeasureUnit(depths.SkseRows, rendered > 0, mw => WritePairingClassJson(mw, c, d.InstalledRuntime)))) break;
                WritePairingClassJson(w, c, d.InstalledRuntime);
                rendered++;
            }
            w.WriteEndArray();

            w.WriteStartArray("unreadable_pex");
            int unreadable = 0;
            foreach (var u in d.Unreadable)
            {
                if (!SkseJsonDoc.Fits(w, ms, cap,
                        JsonWire.MeasureUnit(depths.SkseRows, unreadable > 0, mw => WriteUnreadablePexJson(mw, u)))) break;
                WriteUnreadablePexJson(w, u);
                unreadable++;
            }
            w.WriteEndArray();
            // Not row-list rows, so the accounting does not count them — the cut is named here instead.
            if (unreadable < d.Unreadable.Count) w.WriteNumber("unreadable_pex_truncated", d.Unreadable.Count - unreadable);

            SkseJsonDoc.Caveats(w, caveats);
            TransportAccounting.WriteJson(w, TransportAccounting.Tally(allClasses.Count, classes.Count, rendered, window, notes));
        });
    }

    /// <summary>One native class's row, in the json lane — written whole, and measured the same way.</summary>
    static void WritePairingClassJson(Utf8JsonWriter w, NativeClassEntry c, string? runtime)
    {
        w.WriteStartObject();
        w.WriteString("class_name", c.ClassName);
        w.WriteString("rel_path", c.RelPath);
        w.WriteString("provenance", c.Provenance switch
        {
            NativeProvenance.Engine => "engine",
            NativeProvenance.SkseCore => "skse_core",
            _ => "third_party",
        });
        SkseJsonDoc.Nullable(w, "rung", c.Rung switch
        {
            NativePairingRung.SameMod => "same_mod",
            NativePairingRung.ChainMod => "chain_mod",
            NativePairingRung.Unpaired => "unpaired",
            _ => null,
        });
        w.WriteString("verdict", VerdictName(c, runtime));
        SkseJsonDoc.Nullable(w, "winning_provider", c.WinningProvider);
        w.WriteString("provider_kind", c.ProviderKind);
        w.WriteNumber("provider_count", c.ProviderCount);
        SkseJsonDoc.Providers(w, c.Providers);
        SkseJsonDoc.Nullable(w, "paired_mod", c.PairedMod);
        w.WriteNumber("native_count", c.NativeCount);
        SkseJsonDoc.Strings(w, "native_functions", c.NativeFunctions);
        w.WriteStartArray("paired_dlls");
        foreach (var dll in c.PairedDlls)
        {
            var (fate, detail) = Judge(dll, runtime);
            w.WriteStartObject();
            w.WriteString("rel_path", dll.RelPath);
            w.WriteString("file_name", dll.FileName);
            w.WriteString("group", dll.Group);
            SkseJsonDoc.Nullable(w, "winning_provider", dll.WinningProvider);
            w.WriteString("fate", fate.ToString().ToLowerInvariant());
            w.WriteString("detail", detail);
            SkseJsonDoc.Nullable(w, "plugin_name", dll.Info?.Version?.Name);
            SkseJsonDoc.Nullable(w, "plugin_version", dll.Info?.Version?.PluginVersion);
            SkseJsonDoc.Nullable(w, "file_version", dll.Info?.FileVersion);
            SkseJsonDoc.Strings(w, "debug_crt_imports", dll.Info?.DebugCrtImports ?? Array.Empty<string>());
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>One unreadable .pex, in the json lane.</summary>
    static void WriteUnreadablePexJson(Utf8JsonWriter w, NativeUnreadablePex u)
    {
        w.WriteStartObject();
        w.WriteString("rel_path", u.RelPath);
        SkseJsonDoc.Nullable(w, "winning_provider", u.WinningProvider);
        w.WriteString("reason", u.Reason);
        w.WriteEndObject();
    }

    /// <summary>Which section of the text render this class lands in, as one word — from the same Judge.</summary>
    static string VerdictName(NativeClassEntry c, string? runtime)
    {
        if (c.Provenance != NativeProvenance.ThirdParty) return "baseline";
        if (c.Rung == NativePairingRung.Unpaired) return "unpaired";
        return BestFate(c, runtime) switch
        {
            DllFate.Dead => "dead",
            DllFate.Verify => "verify",
            _ => IsDebugOnly(c, runtime) ? "debug_build" : "healthy",
        };
    }

    /// <summary>The build-level caveats as one string, so a render can charge them against max_chars up front.</summary>
    static string Caveats(NativePairingAuditData d, int cap)
    {
        var sb = new StringBuilder();
        AppendCaveatsTo(sb, d, cap);
        return sb.ToString();
    }

    static void AppendCaveatsTo(StringBuilder sb, NativePairingAuditData d, int cap)
    {
        if (d.ReadIncomplete)
            sb.Append("[!] a BSA or a loose mod folder failed to read this build, so a script present only in it may be missing from this audit (Q3).\n");
        sb.Append(BatchRender.CaveatBlockLines(cap, BatchRender.WarningList(d.Warnings),
            BatchRender.ArchiveFailureList(d.BsaFailures), BatchRender.RootFailureList(d.RootFailures)));
    }
}

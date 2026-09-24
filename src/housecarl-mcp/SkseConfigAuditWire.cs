using System.Text;
using System.Text.Json;

namespace HousecarlMcp;

// The config family's text and json renders for housecarl_skse; contract in docs/architecture/skse-layer.md.
/// <summary>Renders <see cref="SkseConfigAuditData"/>: the health summary keeping BROKEN apart from INERT, then the
/// diagnostics in full with file:line provenance and winning provider, then the accounted-for remainder. filter=
/// lists every reference with its verdict, the OKs included; see docs/architecture/skse-layer.md.</summary>
static class SkseConfigAuditWire
{
    // A dead reference and the file it was declared in.
    readonly record struct Hit(SkseConfigFileAudit File, SkseAuditedRef Audited)
    {
        public HousecarlCore.SkseConfigRef Ref => Audited.Ref;
    }

    /// <summary>What this family's accounting counts: the config FILES the audit covers.</summary>
    internal const string RowNoun = "config(s)";

    public static string Render(SkseConfigAuditData d, string? filter, int cap, RowWindow window = default, int trailer = 0)
    {
        if (filter is { Length: > 0 }) return RenderFiltered(d, filter.Trim(), cap, window, trailer);

        // Every count below states the WHOLE audit; limit=/offset= window only the files the sections LIST.
        int notes = NoteCount(d);
        var rows = window.Apply(d.Files);
        int reserve = TransportAccounting.Reserve(d.Files.Count, rows.Count, window, notes, RowNoun);
        // The scope note, caveats and filter hint come after the sections, so they are charged before them.
        var tail = "\n(scope: form-shaped references only — a hex FormID + plugin filename, or a plugin-named folder gate. Bare " +
                   "EditorID/name strings are not validated (Wave 2). Extraction is heuristic over token shapes: a token in a comment " +
                   "or disabled block still counts — 'references this file declares', not 'the DLL will use'. A folder that SHOULD carry " +
                   "references but shows none may use a reference shape not yet recognized.)\n" + Caveats(d, cap) +
                   "\n→ filter='<folder/mod/filename/plugin>' to audit one group and see every reference (OKs included).";
        var healthyFiles0 = d.Files.Where(f => f.ReadError is null && f.Refs.Count > 0 && f.Refs.All(r => r.Verdict == SkseRefVerdict.Ok)).ToList();
        var noRefFiles0 = d.Files.Where(f => f.ReadError is null && f.Refs.Count == 0).ToList();
        int noRefGroups = noRefFiles0.Select(f => f.Group).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        // The accounted-for line, its folder heading and that list's own cut notice are written whatever the
        // sections cost, so their room is charged with the tail.
        string NoRefCut(int shown) => "    ... [" + shown + " of " + noRefGroups + " folders; raise max_chars]\n";
        int noRefCut = noRefFiles0.Count == 0 ? 0 : NoRefCut(noRefGroups).Length;
        int alwaysWritten =
            ("\naccounted for: " + healthyFiles0.Count + " file(s) with " + d.Files.Sum(f => f.Refs.Count) +
             " reference(s) all OK · " + d.Files.Sum(f => f.Refs.Count) + " more OK ref(s) in files that also carry a non-OK reference · " +
             noRefFiles0.Count + " file(s) declare no form-shaped references\n").Length +
            (noRefFiles0.Count == 0 ? 0 : ("  no-reference configs by folder (" + noRefGroups + "):\n").Length) + noRefCut;
        // cap stays the caller's max_chars — the number the notices quote; budget is the room the sections have.
        int budget = Math.Max(1, cap - trailer - reserve - tail.Length - alwaysWritten - SkseRenderParts.SectionsMissed(9, cap).Length);
        // The always-written accounted-for block lays its folder rows in the room reserved for it, above the
        // diagnostic sections' ceiling.
        int noRefCeil = budget + alwaysWritten;
        int missed = 0;
        var tally = new RowTally();

        var flatAll = d.Files.SelectMany(f => f.Refs.Select(r => new Hit(f, r))).ToList();
        var flat = rows.SelectMany(f => f.Refs.Select(r => new Hit(f, r))).ToList();
        var missingGates = flat.Where(h => h.Audited.Verdict == SkseRefVerdict.PluginMissing && h.Ref.Shape == HousecarlCore.SkseRefShape.PathSegmentGate).ToList();
        var missingToks  = flat.Where(h => h.Audited.Verdict == SkseRefVerdict.PluginMissing && h.Ref.Shape == HousecarlCore.SkseRefShape.FormToken).ToList();
        var dangling     = flat.Where(h => h.Audited.Verdict == SkseRefVerdict.Dangling).ToList();
        var unparseable  = flat.Where(h => h.Audited.Verdict == SkseRefVerdict.Unparseable).ToList();
        var readErrors   = rows.Where(f => f.ReadError is not null).ToList();

        int Count(Func<Hit, bool> p) => flatAll.Count(p);
        int danglingAll    = Count(h => h.Audited.Verdict == SkseRefVerdict.Dangling);
        int unparseableAll = Count(h => h.Audited.Verdict == SkseRefVerdict.Unparseable);
        int inertAll       = Count(h => h.Audited.Verdict == SkseRefVerdict.PluginMissing);

        int refsChecked = flatAll.Count;
        // BROKEN and INERT are kept apart in the headline; see docs/architecture/skse-layer.md.
        int broken = danglingAll + unparseableAll;
        int inert  = inertAll;
        int notOk  = broken + inert;                       // every non-OK ref (kept for the accounted-for reconciliation below)
        int filesWithRefs = d.Files.Count(f => f.Refs.Count > 0);

        var sb = new StringBuilder();
        sb.Append("SKSE config audit — profile '").Append(d.ProfileName).Append("' — ")
          .Append(d.ConfigCount).Append(" config(s) scanned, ").Append(filesWithRefs).Append(" carry references, ")
          .Append(refsChecked).Append(" reference(s) checked\n");
        if (broken == 0 && inert == 0)
            sb.Append("✓ every reference resolves against the active load order — nothing broken, nothing inert.\n");
        else if (broken == 0)
            sb.Append("✓ no broken references — every reference to an installed plugin resolves. (")
              .Append(inert).Append(" reference(s) point at plugins not in your load order — inert, usually optional support for a mod you don't have.)\n");
        else
        {
            sb.Append("[!] ").Append(broken).Append(" BROKEN reference(s): ")
              .Append(danglingAll).Append(" dangling · ").Append(unparseableAll).Append(" unparseable");
            if (inert > 0)
                sb.Append("   ·   ").Append(inert).Append(" more inert (plugin not installed — usually optional support)");
            sb.Append('\n');
        }

        // ── Diagnostics first, in full. ──
        if (!AppendHits(sb, "PLUGIN MISSING — folder gates (the plugin isn't installed, so the WHOLE file is inert)", missingGates, budget,
            h => $"  - {h.File.RelPath}: folder '{h.Ref.Plugin}' not in the load order{Prov(h.File)}", tally)) missed++;
        // Token-level plugin-missing is grouped by target plugin: a per-ref list is an unreadable wall.
        if (missingToks.Count > 0)
        {
            var byPlugin = missingToks.GroupBy(h => h.Ref.Plugin, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Plugin: g.Key, Refs: g.Count(),
                              Files: g.Select(h => h.File.RelPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                              Example: g.First().File.RelPath))
                .OrderByDescending(g => g.Refs).ThenBy(g => g.Plugin, StringComparer.OrdinalIgnoreCase).ToList();
            int byPluginCut = SkseRenderParts.CutRoom(byPlugin.Count, "plugins", SkseRenderParts.NarrowHint);
            if (!SkseRenderParts.Head(sb, budget - byPluginCut, "\nPLUGIN MISSING — target plugin not in the load order (inert; often a config shipping optional support for a mod you don't have) — by plugin (" +
                    byPlugin.Count + " plugins, " + missingToks.Count + " refs):\n")) missed++;
            else
            {
            int rows2 = budget - byPluginCut;
            int shown = 0;
            foreach (var g in byPlugin)
            {
                int mark = sb.Length;
                sb.Append("  - ").Append(g.Plugin).Append(": ").Append(g.Refs).Append(" ref(s)");
                if (g.Files.Count > 1) sb.Append(" across ").Append(g.Files.Count).Append(" file(s)");
                sb.Append("  (e.g. ").Append(g.Example).Append(")\n");
                if (sb.Length > rows2) { sb.Length = mark; sb.Append(SkseRenderParts.Showing(shown, byPlugin.Count, "plugins", SkseRenderParts.NarrowHint)); break; }
                shown++;
                foreach (var f in g.Files) tally.Mark(f);
            }
            }
        }
        if (!AppendHits(sb, "DANGLING — plugin present but no such record (a dead reference)", dangling, budget,
            h => $"  - {Loc(h)}: '{h.Ref.Raw}' → {h.Audited.Detail}{Prov(h.File)}", tally)) missed++;
        if (!AppendHits(sb, "UNPARSEABLE — shape-matched tokens that can't be normalized (flagged, never guessed)", unparseable, budget,
            h => $"  - {Loc(h)}: '{h.Ref.Raw}' → {h.Audited.Detail}{Prov(h.File)}", tally)) missed++;
        string ReadErrCut(int shown) => "  ... [" + shown + " of " + readErrors.Count + "; raise max_chars]\n";
        int readErrCut = readErrors.Count == 0 ? 0 : ReadErrCut(readErrors.Count).Length;
        if (readErrors.Count > 0 && !SkseRenderParts.Head(sb, budget - readErrCut, "\nread errors — configs that could not be read/decoded (NOT counted as clean) (" + readErrors.Count + "):\n")) missed++;
        else if (readErrors.Count > 0)
        {
            int rows3 = budget - readErrCut;
            int shown = 0;
            foreach (var f in readErrors)
            {
                var row = "  - " + f.RelPath + ": " + f.ReadError + Prov(f) + "\n";
                if (sb.Length + row.Length > rows3) { sb.Append(ReadErrCut(shown)); break; }
                sb.Append(row); shown++; tally.Mark(f.RelPath);
            }
        }

        // ── Accounted-for remainder: everything that is not a diagnostic, so nothing is dropped. ──
        var healthyFiles = d.Files.Where(f => f.ReadError is null && f.Refs.Count > 0 && f.Refs.All(r => r.Verdict == SkseRefVerdict.Ok)).ToList();
        int healthyRefs = healthyFiles.Sum(f => f.Refs.Count);
        int okInMixed = (refsChecked - notOk) - healthyRefs;   // OK refs living in a file that ALSO has a non-OK ref — so every ref reconciles: refsChecked = notOk + healthyRefs + okInMixed
        var noRefFiles = d.Files.Where(f => f.ReadError is null && f.Refs.Count == 0).ToList();
        // A clean file is accounted for by the count line above, not by a row of its own, so it counts as rendered.
        foreach (var f in rows)
            if (f.ReadError is null && f.Refs.All(r => r.Verdict == SkseRefVerdict.Ok)) tally.Mark(f.RelPath);
        sb.Append("\naccounted for: ").Append(healthyFiles.Count).Append(" file(s) with ").Append(healthyRefs)
          .Append(" reference(s) all OK");
        if (okInMixed > 0) sb.Append(" · ").Append(okInMixed).Append(" more OK ref(s) in files that also carry a non-OK reference");
        sb.Append(" · ").Append(noRefFiles.Count).Append(" file(s) declare no form-shaped references\n");
        if (noRefFiles.Count > 0)
        {
            var groups = noRefFiles.GroupBy(f => f.Group, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Name: g.Key.Length == 0 ? "(top level)" : g.Key, Count: g.Count()))
                .OrderByDescending(g => g.Count).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
            sb.Append("  no-reference configs by folder (").Append(groups.Count).Append("):\n");
            int rows4 = noRefCeil - noRefCut;
            int shown = 0;
            foreach (var g in groups)
            {
                var row = "    - " + g.Name + ": " + g.Count + "\n";
                if (sb.Length + row.Length > rows4) { sb.Append(NoRefCut(shown)); break; }
                sb.Append(row); shown++;
            }
        }

        if (missed > 0) sb.Append(SkseRenderParts.SectionsMissed(missed, cap));
        sb.Append(tail);
        return sb.ToString().TrimEnd('\n')
             + TransportAccounting.Compose(TransportAccounting.Tally(d.Files.Count, rows.Count, tally.Count, window, notes),
                                           RowNoun, everySentence: false);
    }

    /// <summary>filter=: audit just the matching configs and list every reference with its verdict, OKs included.</summary>
    static string RenderFiltered(SkseConfigAuditData d, string filter, int cap, RowWindow window = default, int trailer = 0)
    {
        bool In(string? s) => s is not null && s.Contains(filter, StringComparison.OrdinalIgnoreCase);
        bool Match(SkseConfigFileAudit f) =>
            In(f.FileName) || In(f.Group) || In(f.WinningProvider) || In(f.RelPath)
            || f.Refs.Any(r => In(r.Ref.Plugin));
        var allHits = d.Files.Where(Match)
            .OrderBy(f => f.Group, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.RelPath, StringComparer.OrdinalIgnoreCase).ToList();

        int notes = NoteCount(d);
        var hits = window.Apply(allHits);
        int reserve = TransportAccounting.Reserve(allHits.Count, hits.Count, window, notes, RowNoun);
        // cap stays the caller's max_chars; budget is the room the file blocks have once the tail is charged.
        string FilesCut(int shown) => "\n" + SkseRenderParts.Showing(shown, hits.Count, "files");
        // The caveats close this view too, charged like the pairing family's, so a filtered audit hedges what it must.
        var tail = "\n" + Caveats(d, cap);
        int budget = Math.Max(1, cap - trailer - reserve - FilesCut(hits.Count).Length - tail.Length);
        var tally = new RowTally();
        string Accounting() => TransportAccounting.Compose(
            TransportAccounting.Tally(allHits.Count, hits.Count, tally.Count, window, notes), RowNoun, everySentence: false);

        var sb = new StringBuilder();
        sb.Append("SKSE config audit — filter '").Append(filter).Append("' — ")
          .Append(allHits.Count).Append(" config(s) match [profile '").Append(d.ProfileName).Append("']\n");
        if (allHits.Count == 0)
        {
            // The suggestion pool spans every axis Match filters on; PluginNameSuggest dedups and skips empties.
            var suggestPool = d.Files.Select(f => f.FileName)
                .Concat(d.Files.Select(f => f.Group).Where(g => g.Length > 0))
                .Concat(d.Files.Select(f => f.WinningProvider).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!))
                .Concat(d.Files.SelectMany(f => f.Refs.Select(r => r.Ref.Plugin)));
            sb.Append("\nnothing under SKSE\\Plugins matched. ")
              .Append(HousecarlCore.PluginNameSuggest.DidYouMean(filter, suggestPool));
            sb.Append(tail);   // a "no match" over an incompletely-read build must carry the caveat (Q3)
            return sb.ToString().TrimEnd('\n') + Accounting();
        }

        int shownFiles = 0;
        foreach (var f in hits)
        {
            // The whole file block is written, MEASURED, and taken back out entire when it crossed.
            int mark = sb.Length;
            sb.Append('\n').Append(f.RelPath).Append("  ← ").Append(f.WinningProvider ?? "(no active provider)").Append('\n');
            if (f.ProviderCount > 1)
                sb.Append("  [!] contested by ").Append(f.ProviderCount).Append(" mods (winner audited): ")
                  .Append(string.Join(" › ", f.Providers.Select(p => $"{p.Name} ({p.Kind})"))).Append('\n');
            if (f.ReadError is not null) sb.Append("  [!] ").Append(f.ReadError).Append('\n');
            else if (f.Refs.Count == 0) sb.Append("  (no form-shaped references)\n");
            else
                foreach (var r in f.Refs)
                    sb.Append("  ").Append(Tag(r.Verdict)).Append(' ')
                      .Append(r.Ref.Shape == HousecarlCore.SkseRefShape.PathSegmentGate ? $"folder gate '{r.Ref.Plugin}'" : $"'{r.Ref.Raw}'")
                      .Append(r.Ref.Line > 0 ? $" (line {r.Ref.Line})" : "")
                      .Append(r.Detail is null ? "" : " → " + r.Detail).Append('\n');
            if (sb.Length > budget) { sb.Length = mark; sb.Append(FilesCut(shownFiles)); break; }
            shownFiles++; tally.Mark(f.RelPath);
        }
        sb.Append(tail);
        return sb.ToString().TrimEnd('\n') + Accounting();
    }

    static string Tag(SkseRefVerdict v) => v switch
    {
        SkseRefVerdict.Ok => "[OK]",
        SkseRefVerdict.PluginMissing => "[MISSING]",
        SkseRefVerdict.Dangling => "[DANGLING]",
        SkseRefVerdict.Unparseable => "[UNPARSEABLE]",
        _ => "[?]",
    };

    static string Loc(Hit h) => h.Ref.Line > 0 ? $"{h.File.RelPath}:{h.Ref.Line}" : h.File.RelPath;
    static string Prov(SkseConfigFileAudit f) => f.WinningProvider is null ? "" : $"  [← {f.WinningProvider}]";

    static bool AppendHits(StringBuilder sb, string label, IReadOnlyList<Hit> items, int cap, Func<Hit, string> line,
                           RowTally? tally = null)
    {
        if (items.Count == 0) return true;
        // Heading and rows both leave room for the cut notice this list may end on.
        int room = cap - SkseRenderParts.CutRoom(items.Count, hint: SkseRenderParts.NarrowHint);
        if (!SkseRenderParts.Head(sb, room, "\n" + label + " (" + items.Count + "):\n")) return false;
        int shown = 0;
        foreach (var h in items)
        {
            var row = line(h) + "\n";
            if (sb.Length + row.Length > room) { sb.Append(SkseRenderParts.Showing(shown, items.Count, hint: SkseRenderParts.NarrowHint)); break; }
            sb.Append(row); shown++; tally?.Mark(h.File.RelPath);
        }
        return true;
    }

    /// <summary>How many build-level caveat notes this answer carries — the accounting's <c>notes</c> count.</summary>
    internal static int NoteCount(SkseConfigAuditData d) => (d.ReadIncomplete ? 1 : 0) + d.Warnings.Count + d.BsaFailures.Count + d.RootFailures.Count;

    /// <summary>The json twin of <see cref="Render"/>: the same census, files, verdicts and accounting, in named fields.</summary>
    public static string RenderJson(SkseConfigAuditData d, string? filter, int cap, RowWindow window = default)
    {
        bool filtered = filter is { Length: > 0 };
        string f = filtered ? filter!.Trim() : "";
        bool In(string? x) => x is not null && x.Contains(f, StringComparison.OrdinalIgnoreCase);

        var allFiles = filtered
            ? d.Files.Where(x => In(x.FileName) || In(x.Group) || In(x.WinningProvider) || In(x.RelPath) || x.Refs.Any(r => In(r.Ref.Plugin)))
                     .OrderBy(x => x.Group, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.RelPath, StringComparer.OrdinalIgnoreCase).ToList()
            : d.Files.ToList();
        var files = window.Apply(allFiles);
        // Every census number is measured over the population this document answers over, never a wider one.
        var flatAll = allFiles.SelectMany(x => x.Refs).ToList();
        int notes = NoteCount(d);
        int rendered = 0;
        // The caveats and accounting tail is paid for inside max_chars rather than appended past it.
        int callerCap = cap;   // the overrun member is measured against what the CALLER passed
        // The caveat lists are cut ONCE, through the SAME cut the text tail takes off the CALLER's max_chars, so the
        // two lanes name the same entries; the reserve composes that bounded block, not the whole lists.
        var caveats = SkseJsonDoc.CutCaveats(d.ReadIncomplete, d.Warnings, d.BsaFailures, d.RootFailures, callerCap);
        cap = Math.Max(1, cap - SkseJsonDoc.TailReserve(caveats,
            TransportAccounting.Widest(allFiles.Count, files.Count, window, notes), new[] { "files" }));

        return SkseJsonDoc.Write(SkseTools.SkseFamily.Config, filter, d.ProfileName, callerCap, (w, ms) =>
        {
            var depths = new JsonWire.JsonUnitDepths(w.CurrentDepth);
            int Verdicts(SkseRefVerdict v) => flatAll.Count(r => r.Verdict == v);
            w.WriteStartObject("totals");
            w.WriteNumber("configs_scanned", filtered ? allFiles.Count : d.ConfigCount);
            w.WriteNumber("files_with_references", allFiles.Count(x => x.Refs.Count > 0));
            w.WriteNumber("references_checked", flatAll.Count);
            w.WriteNumber("ok", Verdicts(SkseRefVerdict.Ok));
            w.WriteNumber("dangling", Verdicts(SkseRefVerdict.Dangling));
            w.WriteNumber("unparseable", Verdicts(SkseRefVerdict.Unparseable));
            w.WriteNumber("plugin_missing", Verdicts(SkseRefVerdict.PluginMissing));
            // BROKEN is what should resolve and does not; INERT is a reference to a plugin you simply do not have.
            w.WriteNumber("broken", Verdicts(SkseRefVerdict.Dangling) + Verdicts(SkseRefVerdict.Unparseable));
            w.WriteNumber("inert", Verdicts(SkseRefVerdict.PluginMissing));
            w.WriteNumber("read_errors", allFiles.Count(x => x.ReadError is not null));
            w.WriteEndObject();

            w.WriteStartArray("files");
            foreach (var file in files)
            {
                // The room the row's own close needs is held back before its references spend, so a row whose
                // references the cap cut still closes inside that cap. The head is measured closed on an empty
                // references array, so the admission test carries that empty close — a few chars — as slack.
                int rowTail = ConfigRowTailCost(file, depths.SkseRows);
                if (!SkseJsonDoc.Fits(w, ms, cap - rowTail,
                        JsonWire.MeasureUnit(depths.SkseRows, rendered > 0, mw => WriteConfigRowHead(mw, file, close: true)))) break;
                WriteConfigRowHead(w, file, close: false);
                w.WriteStartArray("references");
                int refs = 0;
                foreach (var r in file.Refs)
                {
                    // One config can carry tens of thousands of form tokens, so the cap bounds the inner loop too.
                    if (!SkseJsonDoc.Fits(w, ms, cap - rowTail,
                            JsonWire.MeasureUnit(depths.SkseConfigRefs, refs > 0, mw => WriteConfigRefJson(mw, r)))) break;
                    WriteConfigRefJson(w, r);
                    refs++;
                }
                w.WriteEndArray();
                // How many of the file's references the cap cut is said here, because the accounting counts files.
                if (refs < file.Refs.Count) w.WriteNumber("references_truncated", file.Refs.Count - refs);
                w.WriteEndObject();
                rendered++;
            }
            w.WriteEndArray();

            SkseJsonDoc.Caveats(w, caveats);
            TransportAccounting.WriteJson(w, TransportAccounting.Tally(allFiles.Count, files.Count, rendered, window, notes));
        });
    }

    /// <summary>A config file row's own fields, without its references; <paramref name="close"/> closes the row on an
    /// empty references array, which is the shape the admission measurement is taken over.</summary>
    static void WriteConfigRowHead(Utf8JsonWriter w, SkseConfigFileAudit file, bool close)
    {
        w.WriteStartObject();
        w.WriteString("rel_path", file.RelPath);
        w.WriteString("file_name", file.FileName);
        w.WriteString("group", file.Group);
        SkseJsonDoc.Nullable(w, "winning_provider", file.WinningProvider);
        w.WriteNumber("provider_count", file.ProviderCount);
        SkseJsonDoc.Providers(w, file.Providers);
        SkseJsonDoc.Nullable(w, "read_error", file.ReadError);
        if (!close) return;
        w.WriteStartArray("references");
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>One resolved reference, in the json lane.</summary>
    static void WriteConfigRefJson(Utf8JsonWriter w, SkseAuditedRef r)
    {
        w.WriteStartObject();
        w.WriteString("raw", r.Ref.Raw);
        w.WriteString("shape", r.Ref.Shape == HousecarlCore.SkseRefShape.PathSegmentGate ? "path_segment_gate" : "form_token");
        w.WriteString("plugin", r.Ref.Plugin);
        SkseJsonDoc.Nullable(w, "local_id", r.Ref.LocalId is { } id ? $"0x{id:X6}" : null);
        w.WriteNumber("line", r.Ref.Line);
        w.WriteString("verdict", VerdictName(r.Verdict));
        SkseJsonDoc.Nullable(w, "detail", r.Detail);
        w.WriteEndObject();
    }

    /// <summary>What closing a row whose references were CUT costs, and nothing else: the non-empty array's close, the
    /// cut member and the object close. The row's brace, the array open and the stand-in element are written OUTSIDE the
    /// measured span — they are in the document before the references spend — and the stand-in is there only so the
    /// array closes in the shape a non-empty one does. Composed rather than hand-written, because an indented close
    /// costs its own indent.</summary>
    static int ConfigRowTailCost(SkseConfigFileAudit file, int depth)
        => JsonWire.MeasureUnit(depth, false, (w, size) =>
        {
            w.WriteStartObject();
            w.WriteStartArray("references");
            w.WriteNullValue();
            int before = size();
            w.WriteEndArray();
            w.WriteNumber("references_truncated", file.Refs.Count);
            w.WriteEndObject();
            return size() - before;
        });

    /// <summary>The verdict's wire spelling — the json twin of <see cref="Tag"/>, from the same enum.</summary>
    static string VerdictName(SkseRefVerdict v) => v switch
    {
        SkseRefVerdict.Ok => "ok",
        SkseRefVerdict.PluginMissing => "plugin_missing",
        SkseRefVerdict.Dangling => "dangling",
        SkseRefVerdict.Unparseable => "unparseable",
        _ => "unknown",
    };

    /// <summary>The build-level caveats as one string, so a render can charge them against max_chars up front.</summary>
    static string Caveats(SkseConfigAuditData d, int cap)
    {
        var sb = new StringBuilder();
        AppendCaveatsTo(sb, d, cap);
        return sb.ToString();
    }

    static void AppendCaveatsTo(StringBuilder sb, SkseConfigAuditData d, int cap)
    {
        if (d.ReadIncomplete)
            sb.Append("[!] a BSA or a loose mod folder failed to read this build, so a config present only in it may be missing from this audit (Q3).\n");
        sb.Append(BatchRender.CaveatBlockLines(cap, BatchRender.WarningList(d.Warnings),
            BatchRender.ArchiveFailureList(d.BsaFailures), BatchRender.RootFailureList(d.RootFailures)));
    }
}

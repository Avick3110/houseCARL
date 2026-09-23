using System.ComponentModel;
using System.Text;
using HousecarlCore;
using ModelContextProtocol.Server;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

/// <summary>Read-only view of the SkyPatcher distributor layer: a whole-layer inventory plus the INI-vs-INI conflict report; one record's post-SkyPatcher state is the overlay source pole on <c>housecarl_records</c> instead.</summary>
[McpServerToolType]
public static class SkyPatcherTools
{
    [McpServerTool(Name = ToolNames.SkypatcherLayer, ReadOnly = true, Title = "SkyPatcher layer (INIs, apply order, conflicts)"),
     Description(
         "Inventory the SkyPatcher distributor layer of the ACTIVE load order — the runtime record edits the record " +
         "tools are otherwise blind to. Scans Data\\SKSE\\Plugins\\SkyPatcher exactly as the DLL reads it: every " +
         "LOOSE INI (BSA-packed ones are flagged NOT applied), per type folder in filename apply order, with the mod " +
         "that wins the VFS for each file, same-path collisions (the loser's content is never read — flagged), " +
         "Plugin.esp.ini filename gates evaluated against the load order, and SkyPatcher.ini per-type toggles. Then " +
         "reports the INI-vs-INI CONFLICTS: two files setting the SAME field of the SAME record to different values " +
         "(the later-sorted file wins; add/remove ops accumulate and are not conflicts), plus the three ITM " +
         "classes: intra-file DEAD WRITES (a later line of the SAME file unconditionally re-covers every target " +
         "of an earlier set — dead regardless of value; partial or conditional-only overwrites are NOT flagged), " +
         "cross-INI DUPLICATES (two files set the same field/target to the SAME value — one copy is redundant), " +
         "and NO-OP WRITES (true ITM — the replay shows the SET writes the value the record already has). " +
         "Entries whose applicability " +
         "also hangs on other filters are flagged conditional rather than guessed. Pass filter= a type folder, mod, " +
         "or filename substring to narrow to the type folders that hold a match — each still listed in full apply order, " +
         "so the files sorting before and after a match stay visible, with the matching files expanded to their patch " +
         "lines. For ONE record's computed " +
         "post-SkyPatcher state use " + ToolNames.Records + " formids=[\"<FormID>\"] source={\"overlay\": \"skypatcher\", \"state\": \"post\"} — source= is a version pole, not a selection, so the read needs formids= (or a scan scope). " +
         "Read-only.")]
    public static string SkyPatcherLayer(
        LoadOrderService svc,
        [Description("Optional. A type-folder (e.g. 'weapon'), providing-mod, or INI filename substring (case-insensitive). " +
            "Lists only the type folders holding a match, each in full apply order so the files sorting around a match " +
            "stay visible, with the matching files expanded to their individual patch lines. Omit for the whole-layer overview.")]
            string? filter = null,
        [Description("Optional. Max characters before lists are cut with an explicit notice. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool(ToolNames.SkypatcherLayer, () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        var data = svc.SkyPatcherLayer();
        return SkyPatcherWire.RenderLayer(data, filter, max_chars > 0 ? max_chars : 80_000);
    });
}

/// <summary>Renders the SkyPatcher reader DTOs as text, bounded by max_chars with explicit cut notices; the caveats are always rendered.</summary>
static class SkyPatcherWire
{
    // ---- housecarl_skypatcher_layer ------------------------------------------------------------------

    public static string RenderLayer(SkyPatcherLayerData d, string? filter, int cap)
    {
        var sb = new StringBuilder();
        // The caveats are composed FIRST and their room held back, because they CLOSE the render: appended last
        // against a cap the body has already spent, the one thing the reader has to know is the first thing cut.
        var caveats = Caveats(d, cap);
        cap = Math.Max(1, cap - caveats.Length);
        // The closing hint and the omitted-sections line are written whatever the body costs, so both are charged first.
        int budget = Math.Max(1, cap - Hint.Length - SectionsMissed(4).Length);
        var folders = d.Scan.Folders;
        filter = string.IsNullOrWhiteSpace(filter) ? null : filter.Trim();   // a blank filter is no filter, never a match-everything
        // A filter matching nothing must never fall through to the unfiltered overview — that reads as the whole layer.
        if (filter is { } zero && !folders.Any(f => f.Files.Any(x => Matches(zero, f, x))))
            return ZeroMatch(d, zero, cap, caveats);   // the block is composed ONCE; charging it twice spends it twice
        int files = folders.Sum(f => f.Files.Count);
        int applied = folders.Sum(f => f.PatchingEnabled ? f.Files.Count(x => x.NotApplied is null) : 0);
        int lines = folders.Sum(f => f.Files.Sum(x => x.Lines.Count(l => l.Kind == SkyPatcherLineKind.Patch)));
        int appliedLines = folders.Sum(f => f.PatchingEnabled
            ? f.Files.Where(x => x.NotApplied is null).Sum(x => x.Lines.Count(l => l.Kind == SkyPatcherLineKind.Patch)) : 0);

        sb.Append("SkyPatcher layer — profile '").Append(d.ProfileName).Append("' — ")
          .Append(folders.Count).Append(" type folder(s), ").Append(files).Append(" INI(s) (")
          .Append(applied).Append(" applied), ").Append(lines).Append(" patch line(s)");
        if (appliedLines != lines) sb.Append(" (").Append(appliedLines).Append(" in applied files)");   // a gated file's lines must not read as live
        int deadWrites = d.Itms.Sum(m => m.Entries.Count);   // one entry per dead write, not one per finding
        sb.Append(", ").Append(d.Conflicts.Count).Append(" set-conflict(s); ITM: ")
          .Append(deadWrites).Append(" intra-file dead write(s), ")
          .Append(d.Duplicates.Count).Append(" cross-INI duplicate(s), ")
          .Append(d.NoOps.Count).Append(" no-op write(s)\n");
        if (folders.Count == 0)
            sb.Append("\nno SkyPatcher INIs in the active order (no Data\\SKSE\\Plugins\\SkyPatcher content, or SkyPatcher itself is not installed).\n");
        // filter= selects at the FOLDER level, so a late-sorting match is never cut by the cap and a matching folder still lists every file in apply order.
        if (filter is { } hdr)
            sb.Append("filter '").Append(hdr).Append("' — ")
              .Append(folders.Sum(f => f.Files.Count(x => Matches(hdr, f, x)))).Append(" of ").Append(files)
              .Append(" INI(s) match, in ").Append(folders.Count(f => f.Files.Any(x => Matches(hdr, f, x))))
              .Append(" of ").Append(folders.Count)
              .Append(" type folder(s); only those folders are listed below, each in full apply order with the matching " +
                      "files expanded to their lines (the counts above are the whole layer).\n");

        // Every line below is admitted by the width it is about to write, and each cut notice's room is held back
        // before the first line it could follow, so a cut lands inside the budget rather than one line past it.
        string folderCut = filter is null
            ? "... [remaining folders omitted at max_chars — raise it or pass filter=]\n"
            : "... [remaining matching folders omitted at max_chars — raise it or narrow filter=]\n";
        int listRoom = budget - folderCut.Length - FileCut.Length - LineCut.Length;
        bool listCut = false;
        foreach (var f in folders)
        {
            if (filter is { } sel && !f.Files.Any(x => Matches(sel, f, x))) continue;
            var head = FolderHead(f, filter);
            if (listCut || sb.Length + head.Length > listRoom) { sb.Append(folderCut); break; }
            sb.Append(head);
            foreach (var file in f.Files)
            {
                if (listCut) { sb.Append(FileCut); break; }
                var row = FileRow(file);
                if (sb.Length + row.Length > listRoom) { sb.Append(FileCut); listCut = true; break; }
                sb.Append(row);
                // Every file of a selected folder is listed — a match's neighbours are where it sorts; only a match expands.
                if (filter is not { } q || !Matches(q, f, file)) continue;
                for (int i = 0; i < file.Lines.Count; i++)
                {
                    var l = file.Lines[i];
                    if (l.Kind != SkyPatcherLineKind.Patch) continue;
                    var line = "      :" + (i + 1) + "  " + l.Raw.Trim() + "\n"
                               + (l.Note is null ? "" : "          [!] " + l.Note + "\n");
                    if (sb.Length + line.Length > listRoom) { sb.Append(LineCut); listCut = true; break; }
                    sb.Append(line);
                }
            }
        }

        int missed = 0;
        if (d.Conflicts.Count > 0 && !Section(sb, budget,
                "\nINI-vs-INI set conflicts (" + d.Conflicts.Count + ") — same field, same target, different values; the LAST write wins:\n",
                "  (report-only: which value SHOULD win is a merge decision — resolve by authoring a later-sorted INI via the skypatcher-authoring skill, then re-run this tool to confirm.)\n",
                d.Conflicts, "",
                c => "  - [" + c.Subfolder + "] " + c.Field + " @ " + c.Target + ":\n",
                c => c.Entries.Select((e, i) => "      " + Path.GetFileName(e.File) + ":" + e.Line + "  " + e.Op + "=" + e.Value
                    + (i == c.Entries.Count - 1 ? "   ← WINS (last in apply order)" : "")   // by index: value-equal entries must not both claim the win
                    + (e.Conditional ? "   [conditional — the line carries further filters]" : "") + "\n")))
            missed++;

        if (deadWrites > 0 && !Section(sb, budget,
                "\nintra-file dead writes (" + deadWrites + ") — ITM-class: later line(s) of the SAME file unconditionally re-cover EVERY target of the write, so it is dead weight regardless of value:\n",
                "  (report-only: in YOUR ini a dead write is an authoring slip to fix at the source; in a downloaded mod's it is usually harmless — the last write is what applies. A write partially overwritten, or overwritten only by a conditional line, is NOT listed — it may still fire.)\n",
                d.Itms, " finding(s)",
                m => "  - [" + m.Subfolder + "] " + Path.GetFileName(m.File) + ": " + m.Field + ":\n",
                m => m.Entries.Select(e => "      :" + e.Line + "  " + e.Op + "=" + e.Value + "  @ " + e.Targets
                    + "   ← DEAD (overwritten by "
                    + string.Join(", ", e.KillerLines.Select(k => k == e.Line ? $":{k} (a later op on the same line)" : $":{k}")) + ")"
                    + (e.Conditional ? "   [carries further filters — dead regardless: the overwrite is unconditional]" : "") + "\n")))
            missed++;

        if (d.Duplicates.Count > 0 && !Section(sb, budget,
                "\ncross-INI duplicate writes (" + d.Duplicates.Count + ") — ITM-class: two or more files set the same field of the same target to the SAME value; one copy is redundant (keep either — the LAST would win if they ever diverge):\n",
                "  (report-only: which copy to drop is a judgment call — a BROAD line also patches every other record of the type, so removing it loses those; prefer dropping the narrower duplicate.)\n",
                d.Duplicates, "",
                c => "  - [" + c.Subfolder + "] " + c.Field + " @ " + c.Target + ":\n",
                c => c.Entries.Select(e => "      " + Path.GetFileName(e.File) + ":" + e.Line + "  " + e.Op + "=" + e.Value
                    + (e.Conditional ? "   [conditional — the line carries further filters]" : "") + "\n")))
            missed++;

        if (d.NoOps.Count > 0 && !Section(sb, budget,
                "\nno-op writes (" + d.NoOps.Count + ") — true ITM: the SET writes the value the record already has at that point in the replay, so the op changes nothing:\n",
                "  (report-only, and relative to THIS load order: the same line matters in an order where the record's winner differs — unlike dead writes and duplicates, a no-op is not an authoring slip in the INI itself unless you author for this order.)\n",
                d.NoOps, "",
                n => "  - [" + n.Subfolder + "] " + Path.GetFileName(n.File) + ":" + n.Line + "  " + n.Op + "=" + n.Value
                    + " @ " + n.FormKey + (n.EditorId is null ? "" : $" ({n.EditorId})")
                    + " — " + n.FieldPath + " is already " + n.Already + "\n"))
            missed++;
        if (missed > 0) sb.Append(SectionsMissed(missed));

        // The omitted-sections room is spent or free by now, so the notes may use it.
        int noteRoom = cap - Hint.Length;
        foreach (var note in d.NoOpNotes.Concat(d.Scan.Notes))
        {
            var line = "[!] " + note + "\n";
            if (sb.Length + line.Length > noteRoom) break;
            sb.Append(line);
        }
        sb.Append(caveats);
        sb.Append(Hint);
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>The line every layer render closes with, spelled once so it is charged before the body.</summary>
    const string Hint = "\n→ " + ToolNames.Records + " formids=['<FormID>'] source={\"overlay\": \"skypatcher\", \"state\": \"post\"} for one record's computed post-SkyPatcher state; filter='<folder/mod/file>' for just the type folders holding a match, each listed in full apply order with the matching files expanded to their lines.";

    const string FileCut = "  ... [cut at max_chars]\n";
    const string LineCut = "      ... [lines cut at max_chars]\n";
    const string EntryCut = "      ... [entries cut at max_chars]\n";

    /// <summary>The line naming how many report sections the budget could not start.</summary>
    static string SectionsMissed(int missed) =>
        "... [" + missed + " report section(s) omitted at max_chars; raise it to see them]\n";

    /// <summary>A type folder's heading line, with its counts and flags.</summary>
    static string FolderHead(SkyPatcherDiscovery.FolderScan f, string? filter)
    {
        int fLines = f.Files.Sum(x => x.Lines.Count(l => l.Kind == SkyPatcherLineKind.Patch));
        int fMatched = filter is { } fq ? f.Files.Count(x => Matches(fq, f, x)) : f.Files.Count;
        var sb = new StringBuilder();
        sb.Append("\n").Append(f.Subfolder).Append(": ").Append(f.Files.Count).Append(" INI(s)");
        if (fMatched != f.Files.Count) sb.Append(" (").Append(fMatched).Append(" matching, expanded)");   // the header must describe the listing under it
        sb.Append(", ").Append(fLines).Append(" patch line(s)");
        if (!f.PatchingEnabled) sb.Append("  [!] toggled OFF in SkyPatcher.ini — the DLL skips this whole folder");
        if (f.Catalog is null) sb.Append("  [!] not a documented SkyPatcher record type — content listed, not interpreted");
        return sb.Append('\n').ToString();
    }

    /// <summary>One INI's row in its folder's apply-order listing.</summary>
    static string FileRow(SkyPatcherDiscovery.IniFile file)
    {
        int n = file.Lines.Count(l => l.Kind == SkyPatcherLineKind.Patch);
        var sb = new StringBuilder();
        sb.Append("  - ").Append(file.SortKey).Append("  (").Append(n).Append(" line(s)) ← ").Append(file.WinningProvider ?? "(no provider)");
        if (file.GatePlugin is not null && file.NotApplied is null) sb.Append("  [gated on ").Append(file.GatePlugin).Append(": active]");
        if (file.NotApplied is not null) sb.Append("  [!] NOT applied: ").Append(file.NotApplied);
        if (file.ShadowedProviders.Count > 0) sb.Append("  [!] shadows same-path copies from ").Append(string.Join(", ", file.ShadowedProviders));
        return sb.Append('\n').ToString();
    }

    /// <summary>One report section: its heading, its items and their entries admitted by width, and its closing line.
    /// The section starts only where its heading, its widest cut notices and its closing line all fit; false means it did not.</summary>
    static bool Section<T>(StringBuilder sb, int budget, string head, string close, IReadOnlyList<T> items, string noun,
                           Func<T, string> item, Func<T, IEnumerable<string>>? entries = null)
    {
        string Showing(int shown) => "  ... [showing " + shown + " of " + items.Count + noun + "; raise max_chars]\n";
        int room = budget - close.Length - Showing(items.Count).Length - (entries is null ? 0 : EntryCut.Length);
        if (sb.Length + head.Length > room) return false;
        sb.Append(head);
        int shown = 0;
        foreach (var x in items)
        {
            var row = item(x);
            if (sb.Length + row.Length > room) { sb.Append(Showing(shown)); break; }
            sb.Append(row);
            bool cut = false;
            foreach (var e in entries?.Invoke(x) ?? Enumerable.Empty<string>())
            {
                if (sb.Length + e.Length > room) { sb.Append(EntryCut); cut = true; break; }
                sb.Append(e);
            }
            shown++;
            if (cut) { if (shown < items.Count) sb.Append(Showing(shown)); break; }
        }
        sb.Append(close);
        return true;
    }

    /// <summary>The filter's match domain: the file's type folder, its providing mod, or its path/filename.</summary>
    static bool Matches(string filter, SkyPatcherDiscovery.FolderScan folder, SkyPatcherDiscovery.IniFile file)
    {
        bool In(string? s) => s is not null && s.Contains(filter, StringComparison.OrdinalIgnoreCase);
        return In(folder.Subfolder) || In(file.WinningProvider) || In(file.RelPath);
    }

    /// <summary>What a filter matching no INI returns: the zero count, what the filter is matched against, and the folders that are there — never the unfiltered overview.</summary>
    static string ZeroMatch(SkyPatcherLayerData d, string filter, int cap, string caveats)
    {
        var folders = d.Scan.Folders;
        int files = folders.Sum(f => f.Files.Count);
        var sb = new StringBuilder();
        sb.Append("SkyPatcher layer — filter '").Append(filter).Append("' — 0 of ").Append(files)
          .Append(" INI(s) match [profile '").Append(d.ProfileName).Append("']\n\n");
        if (folders.Count == 0)
            sb.Append(d.ReadIncomplete
                ? "nothing matched: no SkyPatcher INIs were found, and the read was incomplete (below), so the layer is not necessarily empty.\n"
                : "nothing matched: the active order has no SkyPatcher INIs at all (no Data\\SKSE\\Plugins\\SkyPatcher content, or SkyPatcher itself is not installed).\n");
        else
        {
            sb.Append("nothing matched: no type folder, providing mod, or INI filename contains '").Append(filter).Append("'.")
              .Append(PluginNameSuggest.DidYouMean(filter, folders.Select(f => f.Subfolder)
                  .Concat(folders.SelectMany(f => f.Files).Select(x => x.WinningProvider).Where(p => p is not null)!)
                  .Concat(folders.SelectMany(f => f.Files).Select(x => x.SortKey))))
              .Append(" The type folder(s) present are: ").Append(string.Join(", ", folders.Select(f => f.Subfolder)))
              .Append(". Omit filter= for the whole-layer overview.\n");
        }
        // The scan notes (shadowed copies, undocumented subfolders) are often why the filter matched nothing.
        int notes = d.NoOpNotes.Count + d.Scan.Notes.Count, shownNotes = 0;
        static string NotesCut(int shown, int total) => "... [showing " + shown + " of " + total + " note(s); raise max_chars]\n";
        int room = cap - NotesCut(notes, notes).Length;
        foreach (var note in d.NoOpNotes.Concat(d.Scan.Notes))
        {
            var line = "[!] " + note + "\n";
            if (sb.Length + line.Length > room) break;
            sb.Append(line);
            shownNotes++;
        }
        if (shownNotes < notes) sb.Append(NotesCut(shownNotes, notes));
        sb.Append(caveats);   // always rendered, as in the filtered and unfiltered renders
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>The build-level caveats as one string, so the render can charge them before its body is laid. The
    /// hedge sentence is written whatever the budget — it IS the alarm — and the lists under it are cut and counted
    /// like the note lists, since a lost archive drive or a blocked tree makes any of them long.</summary>
    static string Caveats(SkyPatcherLayerData d, int cap)
    {
        var sb = new StringBuilder();
        if (d.ReadIncomplete)
            sb.Append("[!] a BSA or a loose mod folder failed to read this build, so an INI present only in it may be missing from this scan (Q3).\n");
        // The warnings, and which mod folder would not read so the hedge names a source — bounded to a share of
        // max_chars and counted by the shared renderer, so they never take the layer's own room.
        sb.Append(BatchRender.CaveatBlockLines(cap, BatchRender.WarningList(d.AssetWarnings),
            BatchRender.RootFailureList(d.RootFailures)));
        return sb.ToString();
    }
}

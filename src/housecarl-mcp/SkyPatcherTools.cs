using System.ComponentModel;
using System.Text;
using HousecarlCore;
using ModelContextProtocol.Server;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

/// <summary>
/// houseCARL's SkyPatcher DISTRIBUTOR-layer readers (Wave 2 of the distributor subsystem; plan
/// dev/plans/SKYPATCHER_DISTRIBUTOR_TOOL_PLAN_2026-07-08.md — reader-only scope, locked 2026-07-09).
/// Read-only. The record tools answer what the PLUGINS say; these answer what the SkyPatcher INI layer
/// DOES to those records at load: <c>housecarl_skypatcher_layer</c> is the whole-layer inventory +
/// INI-vs-INI conflict report. One record's TRUE post-SkyPatcher state — the ordered, stateful replay —
/// had its own tool until the 1.x cut; it is the overlay SOURCE pole on <c>housecarl_records</c> now.
/// Authoring stays with the skypatcher-authoring skill; this reader is its verifier (a typo'd op
/// classifies Unknown loud; the computed post-state confirms an authored INI does what was intended).
/// </summary>
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
         "or filename substring to expand matching files to their patch lines. For ONE record's computed " +
         "post-SkyPatcher state use " + ToolNames.Records + " source={\"overlay\": \"skypatcher\", \"state\": \"post\"}. " +
         "Read-only.")]
    public static string SkyPatcherLayer(
        LoadOrderService svc,
        [Description("Optional. A type-folder (e.g. 'weapon'), providing-mod, or INI filename substring (case-insensitive). " +
            "Expands the matching files to their individual patch lines. Omit for the whole-layer overview.")]
            string? filter = null,
        [Description("Optional. Max characters before lists are cut with an explicit notice. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool(ToolNames.SkypatcherLayer, () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        var data = svc.SkyPatcherLayer();
        return SkyPatcherWire.RenderLayer(data, filter?.Trim(), max_chars > 0 ? max_chars : 80_000);
    });
}

/// <summary>Renders the SkyPatcher reader DTOs as compact, scannable text — bounded by max_chars with
/// explicit cut notices (Q3 — never silent truncation), caveats always rendered.</summary>
static class SkyPatcherWire
{
    // ---- housecarl_skypatcher_layer ------------------------------------------------------------------

    public static string RenderLayer(SkyPatcherLayerData d, string? filter, int cap)
    {
        var sb = new StringBuilder();
        var folders = d.Scan.Folders;
        int files = folders.Sum(f => f.Files.Count);
        int applied = folders.Sum(f => f.PatchingEnabled ? f.Files.Count(x => x.NotApplied is null) : 0);
        int lines = folders.Sum(f => f.Files.Sum(x => x.Lines.Count(l => l.Kind == SkyPatcherLineKind.Patch)));
        int appliedLines = folders.Sum(f => f.PatchingEnabled
            ? f.Files.Where(x => x.NotApplied is null).Sum(x => x.Lines.Count(l => l.Kind == SkyPatcherLineKind.Patch)) : 0);

        sb.Append("SkyPatcher layer — profile '").Append(d.ProfileName).Append("' — ")
          .Append(folders.Count).Append(" type folder(s), ").Append(files).Append(" INI(s) (")
          .Append(applied).Append(" applied), ").Append(lines).Append(" patch line(s)");
        if (appliedLines != lines) sb.Append(" (").Append(appliedLines).Append(" in applied files)");   // files vs lines units — don't let a gated file's lines read as live
        int deadWrites = d.Itms.Sum(m => m.Entries.Count);   // entries ARE the dead writes — exact units
        sb.Append(", ").Append(d.Conflicts.Count).Append(" set-conflict(s); ITM: ")
          .Append(deadWrites).Append(" intra-file dead write(s), ")
          .Append(d.Duplicates.Count).Append(" cross-INI duplicate(s), ")
          .Append(d.NoOps.Count).Append(" no-op write(s)\n");
        if (folders.Count == 0)
            sb.Append("\nno SkyPatcher INIs in the active order (no Data\\SKSE\\Plugins\\SkyPatcher content, or SkyPatcher itself is not installed).\n");

        bool In(string? s) => filter is null || (s is not null && s.Contains(filter, StringComparison.OrdinalIgnoreCase));

        foreach (var f in folders)
        {
            if (sb.Length >= cap) { sb.Append("... [remaining folders omitted at max_chars — raise it or pass filter=]\n"); break; }
            int fLines = f.Files.Sum(x => x.Lines.Count(l => l.Kind == SkyPatcherLineKind.Patch));
            sb.Append("\n").Append(f.Subfolder).Append(": ").Append(f.Files.Count).Append(" INI(s), ").Append(fLines).Append(" patch line(s)");
            if (!f.PatchingEnabled) sb.Append("  [!] toggled OFF in SkyPatcher.ini — the DLL skips this whole folder");
            if (f.Catalog is null) sb.Append("  [!] not a documented SkyPatcher record type — content listed, not interpreted");
            sb.Append('\n');
            foreach (var file in f.Files)
            {
                if (sb.Length >= cap) { sb.Append("  ... [cut at max_chars]\n"); break; }
                int n = file.Lines.Count(l => l.Kind == SkyPatcherLineKind.Patch);
                sb.Append("  - ").Append(file.SortKey).Append("  (").Append(n).Append(" line(s)) ← ").Append(file.WinningProvider ?? "(no provider)");
                if (file.GatePlugin is not null && file.NotApplied is null) sb.Append("  [gated on ").Append(file.GatePlugin).Append(": active]");
                if (file.NotApplied is not null) sb.Append("  [!] NOT applied: ").Append(file.NotApplied);
                if (file.ShadowedProviders.Count > 0) sb.Append("  [!] shadows same-path copies from ").Append(string.Join(", ", file.ShadowedProviders));
                sb.Append('\n');
                // filter= match (folder, provider, or filename) expands the file to its patch lines.
                bool expand = filter is not null && (In(f.Subfolder) || In(file.WinningProvider) || In(file.RelPath));
                if (expand)
                    for (int i = 0; i < file.Lines.Count; i++)
                    {
                        if (sb.Length >= cap) { sb.Append("      ... [lines cut at max_chars]\n"); break; }
                        var l = file.Lines[i];
                        if (l.Kind != SkyPatcherLineKind.Patch) continue;
                        sb.Append("      :").Append(i + 1).Append("  ").Append(l.Raw.Trim()).Append('\n');
                        if (l.Note is not null) sb.Append("          [!] ").Append(l.Note).Append('\n');
                    }
            }
        }

        if (d.Conflicts.Count > 0)
        {
            sb.Append("\nINI-vs-INI set conflicts (").Append(d.Conflicts.Count)
              .Append(") — same field, same target, different values; the LAST write wins:\n");
            int shown = 0;
            foreach (var c in d.Conflicts)
            {
                if (sb.Length >= cap) { sb.Append("  ... [showing ").Append(shown).Append(" of ").Append(d.Conflicts.Count).Append("; raise max_chars]\n"); break; }
                sb.Append("  - [").Append(c.Subfolder).Append("] ").Append(c.Field).Append(" @ ").Append(c.Target).Append(":\n");
                for (int i = 0; i < c.Entries.Count; i++)
                {
                    if (sb.Length >= cap) { sb.Append("      ... [entries cut at max_chars]\n"); break; }
                    var e = c.Entries[i];
                    sb.Append("      ").Append(Path.GetFileName(e.File)).Append(':').Append(e.Line)
                      .Append("  ").Append(e.Op).Append('=').Append(e.Value)
                      .Append(i == c.Entries.Count - 1 ? "   ← WINS (last in apply order)" : "")   // by INDEX — value-equal entries must not both claim the win
                      .Append(e.Conditional ? "   [conditional — the line carries further filters]" : "")
                      .Append('\n');
                }
                shown++;
            }
            sb.Append("  (report-only: which value SHOULD win is a merge decision — resolve by authoring a later-sorted INI via the skypatcher-authoring skill, then re-run this tool to confirm.)\n");
        }

        if (deadWrites > 0)
        {
            sb.Append("\nintra-file dead writes (").Append(deadWrites)
              .Append(") — ITM-class: later line(s) of the SAME file unconditionally re-cover EVERY target of the write, so it is dead weight regardless of value:\n");
            int shownItms = 0;
            foreach (var m in d.Itms)
            {
                if (sb.Length >= cap) { sb.Append("  ... [showing ").Append(shownItms).Append(" of ").Append(d.Itms.Count).Append(" finding(s); raise max_chars]\n"); break; }
                sb.Append("  - [").Append(m.Subfolder).Append("] ").Append(Path.GetFileName(m.File))
                  .Append(": ").Append(m.Field).Append(":\n");
                foreach (var e in m.Entries)
                {
                    if (sb.Length >= cap) { sb.Append("      ... [entries cut at max_chars]\n"); break; }
                    sb.Append("      :").Append(e.Line).Append("  ").Append(e.Op).Append('=').Append(e.Value)
                      .Append("  @ ").Append(e.Targets)
                      .Append("   ← DEAD (overwritten by ")
                      .Append(string.Join(", ", e.KillerLines.Select(k => k == e.Line ? $":{k} (a later op on the same line)" : $":{k}")))
                      .Append(')')
                      .Append(e.Conditional ? "   [carries further filters — dead regardless: the overwrite is unconditional]" : "")
                      .Append('\n');
                }
                shownItms++;
            }
            sb.Append("  (report-only: in YOUR ini a dead write is an authoring slip to fix at the source; in a downloaded mod's it is usually harmless — the last write is what applies. A write partially overwritten, or overwritten only by a conditional line, is NOT listed — it may still fire.)\n");
        }

        if (d.Duplicates.Count > 0)
        {
            sb.Append("\ncross-INI duplicate writes (").Append(d.Duplicates.Count)
              .Append(") — ITM-class: two or more files set the same field of the same target to the SAME value; one copy is redundant (keep either — the LAST would win if they ever diverge):\n");
            int shownDups = 0;
            foreach (var c in d.Duplicates)
            {
                if (sb.Length >= cap) { sb.Append("  ... [showing ").Append(shownDups).Append(" of ").Append(d.Duplicates.Count).Append("; raise max_chars]\n"); break; }
                sb.Append("  - [").Append(c.Subfolder).Append("] ").Append(c.Field).Append(" @ ").Append(c.Target).Append(":\n");
                foreach (var e in c.Entries)
                {
                    if (sb.Length >= cap) { sb.Append("      ... [entries cut at max_chars]\n"); break; }
                    sb.Append("      ").Append(Path.GetFileName(e.File)).Append(':').Append(e.Line)
                      .Append("  ").Append(e.Op).Append('=').Append(e.Value)
                      .Append(e.Conditional ? "   [conditional — the line carries further filters]" : "")
                      .Append('\n');
                }
                shownDups++;
            }
            sb.Append("  (report-only: which copy to drop is a judgment call — a BROAD line also patches every other record of the type, so removing it loses those; prefer dropping the narrower duplicate.)\n");
        }

        if (d.NoOps.Count > 0)
        {
            sb.Append("\nno-op writes (").Append(d.NoOps.Count)
              .Append(") — true ITM: the SET writes the value the record already has at that point in the replay, so the op changes nothing:\n");
            int shownNoOps = 0;
            foreach (var n in d.NoOps)
            {
                if (sb.Length >= cap) { sb.Append("  ... [showing ").Append(shownNoOps).Append(" of ").Append(d.NoOps.Count).Append("; raise max_chars]\n"); break; }
                sb.Append("  - [").Append(n.Subfolder).Append("] ").Append(Path.GetFileName(n.File)).Append(':').Append(n.Line)
                  .Append("  ").Append(n.Op).Append('=').Append(n.Value)
                  .Append(" @ ").Append(n.FormKey).Append(n.EditorId is null ? "" : $" ({n.EditorId})")
                  .Append(" — ").Append(n.FieldPath).Append(" is already ").Append(n.Already)
                  .Append('\n');
                shownNoOps++;
            }
            sb.Append("  (report-only, and relative to THIS load order: the same line matters in an order where the record's winner differs — unlike dead writes and duplicates, a no-op is not an authoring slip in the INI itself unless you author for this order.)\n");
        }
        foreach (var note in d.NoOpNotes)
        {
            if (sb.Length >= cap) break;
            sb.Append("[!] ").Append(note).Append('\n');
        }

        foreach (var note in d.Scan.Notes)
        {
            if (sb.Length >= cap) break;
            sb.Append("[!] ").Append(note).Append('\n');
        }
        AppendCaveats(sb, d.ReadIncomplete, d.AssetWarnings);
        sb.Append("\n→ " + ToolNames.Records + " formids=['<FormID>'] source={\"overlay\": \"skypatcher\", \"state\": \"post\"} for one record's computed post-SkyPatcher state; filter='<folder/mod/file>' to expand files to their lines.");
        return sb.ToString().TrimEnd('\n');
    }

    static void AppendCaveats(StringBuilder sb, bool readIncomplete, IReadOnlyList<string> assetWarnings)
    {
        if (readIncomplete)
            sb.Append("[!] a BSA failed to read this build, so an INI present only in it may be missing from this scan (Q3).\n");
        foreach (var w in assetWarnings) sb.Append("[!] ").Append(w).Append('\n');
    }
}

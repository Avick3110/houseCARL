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
/// INI-vs-INI conflict report, <c>housecarl_skypatcher_read</c> is one record's TRUE post-SkyPatcher
/// state (the ordered, stateful replay). Authoring stays with the skypatcher-authoring skill; these
/// readers are its verifier (a typo'd op classifies Unknown loud; the computed post-state confirms an
/// authored INI does what was intended).
/// </summary>
[McpServerToolType]
public static class SkyPatcherTools
{
    [McpServerTool(Name = "housecarl_skypatcher_layer", ReadOnly = true, Title = "SkyPatcher layer (INIs, apply order, conflicts)"),
     Description(
         "Inventory the SkyPatcher distributor layer of the ACTIVE load order — the runtime record edits the record " +
         "tools are otherwise blind to. Scans Data\\SKSE\\Plugins\\SkyPatcher exactly as the DLL reads it: every " +
         "LOOSE INI (BSA-packed ones are flagged NOT applied), per type folder in filename apply order, with the mod " +
         "that wins the VFS for each file, same-path collisions (the loser's content is never read — flagged), " +
         "Plugin.esp.ini filename gates evaluated against the load order, and SkyPatcher.ini per-type toggles. Then " +
         "reports the INI-vs-INI CONFLICTS: two files setting the SAME field of the SAME record to different values " +
         "(the later-sorted file wins; add/remove ops accumulate and are not conflicts). Entries whose applicability " +
         "also hangs on other filters are flagged conditional rather than guessed. Pass filter= a type folder, mod, " +
         "or filename substring to expand matching files to their patch lines. For ONE record's computed " +
         "post-SkyPatcher state use housecarl_skypatcher_read. Read-only.")]
    public static string SkyPatcherLayer(
        LoadOrderService svc,
        [Description("Optional. A type-folder (e.g. 'weapon'), providing-mod, or INI filename substring (case-insensitive). " +
            "Expands the matching files to their individual patch lines. Omit for the whole-layer overview.")]
            string? filter = null,
        [Description("Optional. Max characters before lists are cut with an explicit notice. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool("housecarl_skypatcher_layer", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        var data = svc.SkyPatcherLayer();
        return SkyPatcherWire.RenderLayer(data, filter?.Trim(), max_chars > 0 ? max_chars : 80_000);
    });

    [McpServerTool(Name = "housecarl_skypatcher_read", ReadOnly = true, Title = "A record's true post-SkyPatcher state"),
     Description(
         "Compute one record's TRUE state after the SkyPatcher layer applies — the answer neither the plugins nor " +
         "xEdit show. Replays every applicable loose INI's lines in the DLL's apply order (filename sort) onto the " +
         "record's load-order winner: same-field sets overwrite, …Mult/…ToAdd run on the RUNNING value, collection " +
         "add/remove accumulate, and the full filter surface is evaluated (primary/keyword/enum/flag/slot/override-" +
         "aware; the player matches only a lone bare primary). Returns each applied op as field: before → after with " +
         "its file:line provenance. TIERED HONESTY: ops with no static resolution (runtime math, non-deterministic " +
         "visual styles, copy-from-form) are returned as flagged DIRECTIVES — never a silently-wrong value — and " +
         "every unknown key, unevaluable filter, or unresolvable form is a named warning. A record type SkyPatcher " +
         "can't patch, or that no INI touches, is a named outcome. A FormID is 'XXXXXX:Plugin.esp'. For the " +
         "whole-layer inventory + conflicts use housecarl_skypatcher_layer. Read-only.")]
    public static string SkyPatcherRead(
        LoadOrderService svc,
        [Description("The record's FormID as 'XXXXXX:Plugin.esp' — 6 hex digits, a colon, then the defining master's filename. Example: '012EB7:Skyrim.esm'.")]
            string formid,
        [Description("Optional. Max characters before lists are cut with an explicit notice. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool("housecarl_skypatcher_read", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        FormKey fk;
        try { fk = FormKey.Factory(formid.Trim()); }
        catch (Exception ex) { return $"error: bad FormID '{formid}': {ex.Message}. Expected 'XXXXXX:Plugin.esp', e.g. '012EB7:Skyrim.esm'."; }
        var data = svc.SkyPatcherPostState(fk);
        return SkyPatcherWire.RenderPostState(data, max_chars > 0 ? max_chars : 80_000);
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

        sb.Append("SkyPatcher layer — profile '").Append(d.ProfileName).Append("' — ")
          .Append(folders.Count).Append(" type folder(s), ").Append(files).Append(" INI(s) (")
          .Append(applied).Append(" applied), ").Append(lines).Append(" patch line(s), ")
          .Append(d.Conflicts.Count).Append(" set-conflict(s)\n");
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
                foreach (var e in c.Entries)
                    sb.Append("      ").Append(Path.GetFileName(e.File)).Append(':').Append(e.Line)
                      .Append("  ").Append(e.Op).Append('=').Append(e.Value)
                      .Append(e == c.Winner ? "   ← WINS (last in apply order)" : "")
                      .Append(e.Conditional ? "   [conditional — the line carries further filters]" : "")
                      .Append('\n');
                shown++;
            }
            sb.Append("  (report-only: which value SHOULD win is a merge decision — resolve by authoring a later-sorted INI via the skypatcher-authoring skill, then re-run this tool to confirm.)\n");
        }

        foreach (var note in d.Scan.Notes)
        {
            if (sb.Length >= cap) break;
            sb.Append("[!] ").Append(note).Append('\n');
        }
        AppendCaveats(sb, d.ReadIncomplete, d.AssetWarnings);
        sb.Append("\n→ housecarl_skypatcher_read '<FormID>' for one record's computed post-SkyPatcher state; filter='<folder/mod/file>' to expand files to their lines.");
        return sb.ToString().TrimEnd('\n');
    }

    // ---- housecarl_skypatcher_read -------------------------------------------------------------------

    public static string RenderPostState(SkyPatcherPostStateData d, int cap)
    {
        if (d.Error is not null) return $"error: {d.Error}";

        var sb = new StringBuilder();
        sb.Append("post-SkyPatcher state — ").Append(d.RecordTypeName).Append(' ').Append(d.FormKey)
          .Append(d.EditorId is null ? "" : $" ({d.EditorId})")
          .Append("  [winner ").Append(d.WinnerPlugin).Append(", profile '").Append(d.ProfileName).Append("']\n");

        int touched = d.Folders.Sum(f => (f.Result?.Applied.Count ?? 0) + (f.Result?.Directives.Count ?? 0));
        if (touched == 0 && d.Folders.All(f => f.Result is null || f.Result.LinesMatched == 0))
            sb.Append("\nno SkyPatcher line in the active order touches this record — its plugin-resolved values ARE its in-game values (as far as this layer goes).\n");

        foreach (var f in d.Folders)
        {
            if (sb.Length >= cap) { sb.Append("... [cut at max_chars]\n"); break; }
            sb.Append("\nfolder '").Append(f.Subfolder).Append("': ").Append(f.IniCount).Append(" applied INI(s), ")
              .Append(f.LineCount).Append(" line(s) replayed");
            if (!f.Enabled) { sb.Append("  [!] toggled OFF in SkyPatcher.ini — the DLL skips this folder\n"); continue; }
            if (f.Result is null) { sb.Append("  (no INIs for this folder)\n"); continue; }
            var r = f.Result;
            sb.Append(" — ").Append(r.LinesMatched).Append(" matched this record");
            if (r.LinesSkippedUnresolvedFilter > 0) sb.Append(", ").Append(r.LinesSkippedUnresolvedFilter).Append(" skipped UNRESOLVED");
            sb.Append('\n');

            if (r.Applied.Count > 0)
            {
                sb.Append("  APPLIED (").Append(r.Applied.Count).Append(") — file:line  op=value  →  field: before → after\n");
                foreach (var a in r.Applied)
                {
                    if (sb.Length >= cap) { sb.Append("  ... [cut at max_chars]\n"); break; }
                    sb.Append("    ").Append(Path.GetFileName(a.File)).Append(':').Append(a.LineNumber)
                      .Append("  ").Append(a.Op).Append('=').Append(a.RawValue)
                      .Append("  →  ").Append(a.FieldPath).Append(": ").Append(a.Before ?? "-").Append(" → ").Append(a.After ?? "-");
                    if (a.Note is not null) sb.Append("   [").Append(a.Note).Append(']');
                    sb.Append('\n');
                }
            }
            if (r.Directives.Count > 0)
            {
                sb.Append("  NOT RESOLVED — directives (").Append(r.Directives.Count).Append("), no static answer exists (tiered honesty):\n");
                foreach (var dr in r.Directives)
                {
                    if (sb.Length >= cap) { sb.Append("  ... [cut at max_chars]\n"); break; }
                    sb.Append("    ").Append(Path.GetFileName(dr.File)).Append(':').Append(dr.LineNumber)
                      .Append("  ").Append(dr.Op).Append('=').Append(dr.RawValue).Append("   [").Append(dr.Reason).Append("]\n");
                }
            }
            foreach (var w in r.Warnings)
            {
                if (sb.Length >= cap) break;
                sb.Append("  [!] ").Append(w).Append('\n');
            }
        }

        foreach (var note in d.LayerNotes)
        {
            if (sb.Length >= cap) break;
            sb.Append("[!] ").Append(note).Append('\n');
        }
        AppendCaveats(sb, d.ReadIncomplete, d.AssetWarnings);
        return sb.ToString().TrimEnd('\n');
    }

    static void AppendCaveats(StringBuilder sb, bool readIncomplete, IReadOnlyList<string> assetWarnings)
    {
        if (readIncomplete)
            sb.Append("[!] a BSA failed to read this build, so an INI present only in it may be missing from this scan (Q3).\n");
        foreach (var w in assetWarnings) sb.Append("[!] ").Append(w).Append('\n');
    }
}

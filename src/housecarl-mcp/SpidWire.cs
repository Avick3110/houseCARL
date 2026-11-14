using System.Text;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>Renders SPID's layer as text, bounded by max_chars with explicit cut notices, and joins it to SkyPatcher's
/// render when one call reads both; SkyPatcher's part is its own render, unchanged.</summary>
static class SpidWire
{
    /// <summary>The layer tool's answer for the formats the call reads, inside <paramref name="cap"/>.</summary>
    public static string RenderLayers(SkyPatcherLayerData d, string? filter, int cap, string? selectEcho)
    {
        filter = string.IsNullOrWhiteSpace(filter) ? null : filter.Trim();
        if (d.Spid is not { } spid) return SkyPatcherWire.RenderLayer(d, filter, cap, selectEcho);
        if (!d.SkyPatcherRead) return RenderCap.Settle(Render(spid, d, filter, cap, selectEcho, owner: true), cap);
        // SkyPatcher's part keeps all the room SPID's does not need, and SPID never less than half.
        var full = Render(spid, d, filter, cap, selectEcho, owner: false);
        var sky = SkyPatcherWire.RenderLayer(d, filter, cap - Math.Min(full.Length, cap / 2) - Gap.Length, selectEcho);
        int room = cap - sky.Length - Gap.Length;
        return RenderCap.Settle(sky + Gap + (full.Length <= room ? full : Render(spid, d, filter, room, selectEcho, owner: false)), cap);
    }

    const string Gap = "\n\n";

    /// <summary>SPID's part. <paramref name="owner"/> is true when it is the whole answer, so it closes on the build caveats
    /// SkyPatcher's part otherwise carries.</summary>
    static string Render(SpidLayerData s, SkyPatcherLayerData d, string? filter, int maxChars, string? selectEcho, bool owner)
    {
        var caveats = owner ? SkyPatcherWire.Caveats(d, maxChars) : "";
        int cap = maxChars - caveats.Length;
        var sb = new StringBuilder();
        var files = s.Files;
        var g = s.Format.Lines!;
        int lines = files.Sum(f => f.Lines.Count(l => l.Skipped is null));
        int skipped = files.Sum(f => f.Lines.Count(l => l.Skipped is not null));
        sb.Append("SPID layer (").Append(s.Format.Name).Append(' ').Append(s.Format.Version).Append(" model; ")
          .Append(string.Join(", ", g.Reads.Where(v => v != s.Format.Version))).Append(" reads its files the same) — profile '")
          .Append(s.ProfileName).Append("' — ").Append(files.Count).Append(" file(s) in Data (")
          .Append(files.Count(f => f.File.NotApplied is null && f.ReadError is null)).Append(" read), ")
          .Append(lines).Append(" line(s)");
        var byType = files.SelectMany(f => f.Lines).Where(l => l.Skipped is null)
            .GroupBy(l => l.Kind == SpidLineKind.ExclusiveGroup ? l.Key : l.Type!).OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.Ordinal);
        if (lines > 0) sb.Append(": ").Append(string.Join(", ", byType.Select(x => x.Key + " " + x.Count())));
        if (skipped > 0) sb.Append("; ").Append(skipped).Append(" line(s) SPID skips");
        sb.Append('\n');
        if (files.Count == 0)
            sb.Append("no SPID files in the active order: no file directly in Data has '").Append(s.Format.Folders[0].NameToken)
              .Append("' in its name and the '").Append(s.Format.Folders[0].Extension).Append("' extension.\n");

        bool Matches(SpidFileData f) => filter is null
            || f.File.RelPath.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || (f.File.WinningProvider?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);
        var holding = s.Selected?.Keys.Select(k => k.File).ToHashSet();
        var shown = Enumerable.Range(0, files.Count).Where(i => Matches(files[i]) && (holding is null || holding.Contains(i))).ToList();
        bool expand = filter is not null || s.Selected is not null;

        if (s.Selected is not null)
        {
            int n = s.Selected.Count(kv => Matches(files[kv.Key.File]));
            sb.Append("lines naming a form of ").Append(selectEcho).Append(filter is null ? "" : " in filter '" + filter + "'")
              .Append(" — ").Append(n).Append(" line(s) in ").Append(shown.Count).Append(" of ").Append(files.Count).Append(" file(s)");
            if (n == 0)
                return Close(s, d, sb.Append(".\n\nnothing matched: no SPID line names such a form as a target, a filter form or a value. A line names a form by " +
                    "FormID~Plugin, by a 0x runtime FormID or by an EditorID, and a plugin through a term naming it; a string filter names a form " +
                    "only when it is an NPC's or a keyword's EditorID.\n"), cap, caveats, owner);
            sb.Append("; only those files are listed, each selected form labelled where the line names it.\n");
        }
        else if (filter is not null)
        {
            sb.Append("filter '").Append(filter).Append("' — ").Append(shown.Count).Append(" of ").Append(files.Count).Append(" file(s) match");
            if (shown.Count == 0)
                return Close(s, d, sb.Append(".\n\nnothing matched: no SPID file name or providing mod contains '").Append(filter).Append("'.\n"), cap, caveats, owner);
            sb.Append(", expanded to their lines with what SPID does with each.\n");
        }
        if (expand && s.Places.Count > 0)
            sb.Append("order: files by name in byte order, each file's lines by key (A to Z, any case) then line, Form lines after every typed line; " +
                      "for ").Append(string.Join(", ", g.Types.Where(t => t.Conflict == SpidConflict.FirstWins).Select(t => t.Key)))
              .Append(" the first line whose filters pass an NPC wins it.\n");

        var notes = NoteLines(s, d, owner, Math.Min(cap / BatchRender.CaveatShare, Math.Max(0, cap - sb.Length)));
        cap -= notes.Length;
        string hint = !expand && sb.Length + Hint.Length + FileCut.Length <= cap ? Hint : "";
        int room = cap - hint.Length - FileCut.Length - (expand ? LineCut.Length : 0);
        bool cut = false;
        foreach (var fi in shown)
        {
            var f = files[fi];
            var row = FileRow(f, fi + 1, files.Count);
            if (sb.Length + row.Length > room) { sb.Append(FileCut); cut = true; break; }
            sb.Append(row);
            if (!expand) continue;
            for (int li = 0; li < f.Lines.Count; li++)
            {
                SpidSelected? picked = null;
                if (s.Selected is not null && !s.Selected.TryGetValue((fi, li), out picked)) continue;
                var text = LineText(s, fi, li, picked);
                if (sb.Length + text.Length > room) { sb.Append(LineCut); cut = true; break; }
                sb.Append(text);
            }
            if (cut) break;
        }
        sb.Append(notes).Append(caveats).Append(hint);
        return sb.ToString().TrimEnd('\n');
    }

    const string Hint = "\n→ filter='<file or mod>' expands a SPID file's lines with what SPID does with each; formids= or plugins= lists the lines naming a form, labelled target, filter or value.";
    const string FileCut = "  ... [SPID files cut at max_chars — raise it or narrow filter=]\n";
    const string LineCut = "      ... [SPID lines cut at max_chars — raise it or narrow filter=, formids= or plugins=]\n";

    /// <summary>A zero answer's close: its notes and, when it is the whole answer, the caveats.</summary>
    static string Close(SpidLayerData s, SkyPatcherLayerData d, StringBuilder sb, int cap, string caveats, bool owner)
        => sb.Append(NoteLines(s, d, owner, Math.Max(0, cap - sb.Length))).Append(caveats).ToString().TrimEnd('\n');

    /// <summary>The selectors' note (when SPID's part is the whole answer; SkyPatcher's otherwise carries it) and the
    /// unreadable-plugin notes SkyPatcher's part has not already said, cut and counted as the layer's notes are.</summary>
    static string NoteLines(SpidLayerData s, SkyPatcherLayerData d, bool owner, int room)
    {
        var said = d.SkyPatcherRead ? d.UnreadableNotes.ToHashSet() : new HashSet<string>();
        var list = (owner && d.SelectNote is { } n ? new[] { n } : Array.Empty<string>())
            .Concat(s.UnreadableNotes.Where(u => !said.Contains(u))).ToList();
        return list.Count == 0 ? "" : BatchRender.CutLines(new BatchRender.CaveatList(list, "[!] ", "note(s)"), room);
    }

    /// <summary>One file's row: its place in SPID's file order, its read lines, the mod that wins it and its flags.</summary>
    static string FileRow(SpidFileData f, int place, int count)
    {
        var sb = new StringBuilder("  - ").Append(f.File.RelPath).Append("  (file ").Append(place).Append(" of ").Append(count).Append(", ")
            .Append(f.Lines.Count(l => l.Skipped is null)).Append(" line(s)) ← ").Append(f.File.WinningProvider ?? "(no provider)");
        if (f.File.NotApplied is { } why) sb.Append("  [!] NOT applied: ").Append(why);
        if (f.ReadError is { } err) sb.Append("  [!] ").Append(err);
        if (f.File.ShadowedProviders.Count > 0) sb.Append("  [!] shadows same-path copies from ").Append(string.Join(", ", f.File.ShadowedProviders)).Append(", never read");
        if (f.File.OrderNote is { } order) sb.Append("  [!] ").Append(order);
        return sb.Append('\n').ToString();
    }

    /// <summary>One line with its notes, its first-wins place and, under a selector, the labels of what it keeps.</summary>
    static string LineText(SpidLayerData s, int fi, int li, SpidSelected? picked)
    {
        var l = s.Files[fi].Lines[li];
        var sb = new StringBuilder("      :").Append(l.Number).Append("  ").Append(l.Raw.Trim()).Append('\n');
        if (l.Skipped is { } skip) return sb.Append("          [!] SPID skips this line: it ").Append(skip).Append('\n').ToString();
        foreach (var n in l.Notes) sb.Append("          [!] ").Append(n).Append('\n');
        if (s.Reads?[fi][li] is { } read)
        {
            if (read.Dropped is { } why) sb.Append("          [!] SPID drops this line: ").Append(why).Append('\n');
            foreach (var n in read.Notes) sb.Append("          [!] ").Append(n).Append('\n');
        }
        if (s.Places.TryGetValue((fi, li), out var p))
            sb.Append("          first wins: ").Append(p.Type).Append(p.Group == nameof(SpidLineKind.Regular) ? "" : " (" + p.Group + ")")
              .Append(" line ").Append(p.Index).Append(" of ").Append(p.Total).Append(" in SPID's order\n");
        foreach (var f in picked?.Forms ?? [])
            sb.Append("          ↳ ").Append(Place(f.Place)).Append(' ').Append(f.Where).Append(": ").Append(f.Token).Append(" → ").Append(FormIdToken.Of(f.Form)).Append('\n');
        foreach (var t in picked?.PluginTerms ?? [])
            sb.Append("          ↳ filter form filter: ").Append(t).Append(" (the plugin, selecting the NPCs it defines)\n");
        return sb.ToString();
    }

    static string Place(SkyPatcherOverlay.FormPlace p) => p switch
    {
        SkyPatcherOverlay.FormPlace.Target => "target",
        SkyPatcherOverlay.FormPlace.Value => "value",
        _ => "filter",
    };
}

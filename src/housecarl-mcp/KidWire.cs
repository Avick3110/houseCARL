using System.Text;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>Renders KID's layer as text, bounded by max_chars with explicit cut notices; <see cref="SpidWire.RenderLayers"/>
/// joins it after the formats before it.</summary>
static class KidWire
{
    /// <summary>KID's header line, without its line end: the model, the profile, the files and the lines by type.</summary>
    internal static string Header(KidLayerData s)
    {
        var sb = new StringBuilder();
        var files = s.Files;
        int lines = files.Sum(f => f.Lines.Count(l => l.Skipped is null));
        int skipped = files.Sum(f => f.Lines.Count(l => l.Skipped is not null));
        sb.Append("KID layer (").Append(s.Format.Name).Append(' ').Append(s.Format.Version).Append(" model; ")
          .Append(string.Join(", ", s.Format.KidLines!.Reads.Where(v => v != s.Format.Version))).Append(" reads its files the same) — profile '")
          .Append(s.ProfileName).Append("' — ").Append(files.Count).Append(" file(s) in Data (")
          .Append(files.Count(f => f.File.NotApplied is null && f.ReadError is null)).Append(" read), ")
          .Append(lines).Append(" line(s)");
        var byType = files.SelectMany(f => f.Lines).Where(l => l.Skipped is null)
            .GroupBy(l => l.IsGroup ? l.Key : l.Type!).OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.Ordinal);
        if (lines > 0) sb.Append(": ").Append(string.Join(", ", byType.Select(x => x.Key + " " + x.Count())));
        if (skipped > 0) sb.Append("; ").Append(skipped).Append(" line(s) KID drops as it reads them");
        return sb.ToString();
    }

    /// <summary>KID's part. <paramref name="after"/> is null when it is the whole answer, so it closes on the build caveats
    /// and the selectors' note; else it names the parts before it, as its cut notices say.</summary>
    internal static string Render(KidLayerData s, SkyPatcherLayerData d, string? filter, int maxChars, string? selectEcho, string? after)
    {
        bool owner = after is null;
        var caveats = owner ? SkyPatcherWire.Caveats(d, maxChars) : "";
        int cap = maxChars - caveats.Length;
        var files = s.Files;
        var sb = new StringBuilder(Header(s)).Append('\n');
        if (files.Count == 0)
            sb.Append("no KID files in the active order: no file directly in Data has '").Append(s.Format.Folders[0].NameToken)
              .Append("' in its name and the '").Append(s.Format.Folders[0].Extension).Append("' extension.\n");

        bool Matches(KidFileData f) => filter is null || f.Matches(filter);
        var holding = s.Selected?.Keys.Select(k => k.File).ToHashSet();
        var shown = Enumerable.Range(0, files.Count).Where(i => Matches(files[i]) && (holding is null || holding.Contains(i))).ToList();
        bool expand = filter is not null || s.Selected is not null;

        if (s.Selected is not null)
        {
            int n = s.Selected.Count(kv => Matches(files[kv.Key.File]));
            sb.Append("lines naming a form of ").Append(selectEcho).Append(filter is null ? "" : " in filter '" + filter + "'")
              .Append(" — ").Append(n).Append(" line(s) in ").Append(shown.Count).Append(" of ").Append(files.Count).Append(" file(s)");
            if (n == 0)
                return Close(s, d, sb.Append(".\n\nnothing matched: no KID line names such a form as a target, a filter form or a value. A line names a form by " +
                    "FormID~Plugin, by a 0x runtime FormID or by an EditorID, and a plugin through a filter naming it; a keyword KID creates names no form.\n"), cap, caveats, owner);
            sb.Append("; only those files are listed, each selected form labelled where the line names it.\n");
        }
        else if (filter is not null)
        {
            sb.Append("filter '").Append(filter).Append("' — ").Append(shown.Count).Append(" of ").Append(files.Count).Append(" file(s) match");
            if (shown.Count == 0)
                return Close(s, d, sb.Append(".\n\nnothing matched: no KID file name or providing mod contains '").Append(filter).Append("'.\n"), cap, caveats, owner);
            sb.Append(", expanded to their lines with what KID does with each.\n");
        }

        var notes = NoteLines(s, d, owner, Math.Min(cap / BatchRender.CaveatShare, Math.Max(0, cap - sb.Length)));
        cap -= notes.Length;
        string fileCut = owner ? FileCut : SpidWire.JoinedCut("  ... [KID files", after!, "kid", "KID"),
               lineCut = owner ? LineCut : SpidWire.JoinedCut("      ... [KID lines", after!, "kid", "KID");
        string hint = !expand && sb.Length + Hint.Length + fileCut.Length <= cap ? Hint : "";
        int room = cap - hint.Length - fileCut.Length - (expand ? lineCut.Length : 0);
        bool cut = false;
        foreach (var fi in shown)
        {
            var f = files[fi];
            var row = FileRow(f, fi + 1, files.Count);
            if (sb.Length + row.Length > room) { sb.Append(fileCut); cut = true; break; }
            sb.Append(row);
            if (!expand) continue;
            for (int li = 0; li < f.Lines.Count; li++)
            {
                KidSelected? picked = null;
                if (s.Selected is not null && !s.Selected.TryGetValue((fi, li), out picked)) continue;
                var text = LineText(s, fi, li, picked);
                if (sb.Length + text.Length > room) { sb.Append(lineCut); cut = true; break; }
                sb.Append(text);
            }
            if (cut) break;
        }
        sb.Append(notes).Append(caveats).Append(hint);
        return sb.ToString().TrimEnd('\n');
    }

    const string Hint = "\n→ filter='<file or mod>' expands a KID file's lines with what KID does with each; formids= or plugins= lists the lines naming a form, labelled target, filter or value.";
    const string FileCut = "  ... [KID files cut at max_chars — raise it or narrow filter=]\n";
    const string LineCut = "      ... [KID lines cut at max_chars — raise it or narrow filter=, formids= or plugins=]\n";

    static string Close(KidLayerData s, SkyPatcherLayerData d, StringBuilder sb, int cap, string caveats, bool owner)
        => sb.Append(NoteLines(s, d, owner, Math.Max(0, cap - sb.Length))).Append(caveats).ToString().TrimEnd('\n');

    /// <summary>The selectors' note (when KID's part is the whole answer) and the unreadable-plugin notes no earlier part has said.</summary>
    static string NoteLines(KidLayerData s, SkyPatcherLayerData d, bool owner, int room)
    {
        var said = (d.SkyPatcherRead ? d.UnreadableNotes : []).Concat(d.Spid?.UnreadableNotes ?? []).ToHashSet();
        var list = (owner && d.SelectNote is { } n ? new[] { n } : Array.Empty<string>())
            .Concat(s.UnreadableNotes.Where(u => !said.Contains(u))).ToList();
        return list.Count == 0 ? "" : BatchRender.CutLines(new BatchRender.CaveatList(list, "[!] ", "note(s)"), room);
    }

    /// <summary>One file's row: its place in KID's file order, its read lines, the mod that wins it and its flags.</summary>
    static string FileRow(KidFileData f, int place, int count)
    {
        var sb = new StringBuilder("  - ").Append(f.File.RelPath).Append("  (file ").Append(place).Append(" of ").Append(count).Append(", ")
            .Append(f.Lines.Count(l => l.Skipped is null)).Append(" line(s)) ← ").Append(f.File.WinningProvider ?? "(no provider)");
        if (f.File.NotApplied is { } why) sb.Append("  [!] NOT applied: ").Append(why);
        if (f.ReadError is { } err) sb.Append("  [!] ").Append(err);
        if (f.File.ShadowedProviders.Count > 0) sb.Append("  [!] shadows same-path copies from ").Append(string.Join(", ", f.File.ShadowedProviders)).Append(", never read");
        if (f.File.OrderNote is { } order) sb.Append("  [!] ").Append(order);
        return sb.Append('\n').ToString();
    }

    /// <summary>One line with its notes and, under a selector, the labels of what it keeps.</summary>
    static string LineText(KidLayerData s, int fi, int li, KidSelected? picked)
    {
        var l = s.Files[fi].Lines[li];
        var sb = new StringBuilder("      :").Append(l.Number).Append("  ").Append(l.Raw.Trim()).Append('\n');
        if (l.Skipped is { } skip) return sb.Append("          [!] KID drops this line: it ").Append(skip).Append('\n').ToString();
        if (s.Reads?[fi][li] is { } read)
        {
            if (read.Dropped is { } why) sb.Append("          [!] KID drops this line: ").Append(why).Append('\n');
            foreach (var n in read.Notes) sb.Append("          [!] ").Append(n).Append('\n');
        }
        else foreach (var n in l.Notes) sb.Append("          [!] ").Append(n).Append('\n');
        foreach (var f in picked?.Forms ?? [])
            sb.Append("          ↳ ").Append(Place(f.Place)).Append(' ').Append(f.Where).Append(": ").Append(f.Token).Append(" → ").Append(FormIdToken.Of(f.Form)).Append('\n');
        foreach (var t in picked?.PluginTerms ?? [])
            sb.Append("          ↳ filter filters: ").Append(t).Append(" (the plugin, selecting the items it defines)\n");
        return sb.ToString();
    }

    static string Place(SkyPatcherOverlay.FormPlace p) => p switch
    {
        SkyPatcherOverlay.FormPlace.Target => "target",
        SkyPatcherOverlay.FormPlace.Value => "value",
        _ => "filter",
    };
}

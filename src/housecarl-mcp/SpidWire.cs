using System.Text;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>Renders SPID's layer as text, bounded by max_chars with explicit cut notices, and joins the formats one call
/// reads: SkyPatcher's part is its own render, unchanged, and each later format takes what the earlier ones leave.</summary>
static class SpidWire
{
    /// <summary>One distributor format's part of a joined answer: its name and key, its render at a cap (as the whole
    /// answer, or after the parts the phrase names), and its one summary line.</summary>
    sealed record Part(string Name, string Key, Func<int, string?, string> Render, Func<string> Summary);

    /// <summary>The layer tool's answer for the formats the call reads, inside <paramref name="cap"/>.
    /// <paramref name="everyFormat"/> is true when <c>distributor</c> was omitted, so with no selector and no filter= the
    /// distributor formats answer one summary line each.</summary>
    public static string RenderLayers(SkyPatcherLayerData d, string? filter, int cap, string? selectEcho, bool everyFormat = false)
    {
        filter = string.IsNullOrWhiteSpace(filter) ? null : filter.Trim();
        var parts = new List<Part>();
        if (d.Spid is { } spid)
            parts.Add(new Part("SPID", "spid", (c, after) => Render(spid, d, filter, c, selectEcho, after), () => Header(spid) + Lists("spid")));
        if (d.Kid is { } kid)
            parts.Add(new Part("KID", "kid", (c, after) => KidWire.Render(kid, d, filter, c, selectEcho, after), () => KidWire.Header(kid) + Lists("kid")));
        if (parts.Count == 0) return SkyPatcherWire.RenderLayer(d, filter, cap, selectEcho);
        if (!d.SkyPatcherRead && parts.Count == 1) return RenderCap.Settle(parts[0].Render(cap, null), cap);
        // The first part is its own answer at the whole cap; each later one takes what is left (spid-layer.md, the formats one call reads).
        var shown = new List<(string Name, string Key, int Length)>();
        string joined;
        if (d.SkyPatcherRead)
        {
            joined = SkyPatcherWire.Render(d, filter, cap, selectEcho);
            shown.Add(("SkyPatcher", "skypatcher", joined.Length));
        }
        else
        {
            var lead = parts[0];
            parts.RemoveAt(0);
            joined = lead.Render(cap, null);
            shown.Add((lead.Name, lead.Key, joined.Length));
        }
        bool summary = everyFormat && filter is null && selectEcho is null;
        var unfit = new List<Part>();
        foreach (var p in parts)
        {
            int room = cap - joined.Length - Gap.Length;
            var text = summary ? p.Summary() : p.Render(room, After(shown));
            if (unfit.Count > 0 || text.Length > room) unfit.Add(p); else shown.Add((p.Name, p.Key, text.Length));
            joined += Gap + text;
        }
        return joined.Length <= cap ? joined : joined + "\n[!] " + JoinedOverran(cap, shown, unfit);
    }

    const string Gap = "\n\n";

    /// <summary>The tail of a summary line: the call that lists the format's files.</summary>
    static string Lists(string key) => "; distributor=[\"" + key + "\"] lists its files";

    /// <summary>The parts a later part's cut notice says it came after: "SkyPatcher's part", "SkyPatcher's and SPID's parts".</summary>
    static string After(IReadOnlyList<(string Name, string Key, int Length)> shown)
        => shown.Count == 1 ? shown[0].Name + "'s part"
            : SkyPatcherTools.Prose(shown.Select(x => x.Name + "'s").ToList(), "and") + " parts";

    /// <summary>The joined answer's over-cap sentence (spid-layer.md, the formats one call reads): the parts that came
    /// first with their lengths, then the parts that did not fit, each with the call that reads it alone.</summary>
    static string JoinedOverran(int cap, IReadOnlyList<(string Name, string Key, int Length)> shown, IReadOnlyList<Part> unfit)
    {
        var sb = new StringBuilder("this response runs past the max_chars=").Append(cap).Append(" it was given: ")
            .Append(shown[0].Name).Append("'s part comes first, as distributor=[\"").Append(shown[0].Key).Append("\"] answers it (")
            .Append(shown[0].Length).Append(" chars)");
        foreach (var s in shown.Skip(1)) sb.Append(", then ").Append(s.Name).Append("'s part (").Append(s.Length).Append(" chars)");
        if (unfit.Count == 1)
            return sb.Append(", and ").Append(unfit[0].Name).Append("'s part does not fit in what is left; distributor=[\"").Append(unfit[0].Key)
                .Append("\"] reads ").Append(unfit[0].Name).Append(" alone at the whole max_chars.").ToString();
        return sb.Append(", and ").Append(SkyPatcherTools.Prose(unfit.Select(u => u.Name + "'s").ToList(), "and")).Append(" parts do not fit in what is left; ")
            .Append(SkyPatcherTools.Prose(unfit.Select(u => "distributor=[\"" + u.Key + "\"]").ToList(), "and")).Append(" read each alone at the whole max_chars.").ToString();
    }

    /// <summary>SPID's header line, without its line end: the model, the profile, the files and the lines by type.</summary>
    static string Header(SpidLayerData s)
    {
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
        return sb.ToString();
    }

    /// <summary>SPID's part. <paramref name="after"/> is null when it is the whole answer, so it closes on the build
    /// caveats SkyPatcher's part otherwise carries; else it names the parts before it, as its cut notices say.</summary>
    static string Render(SpidLayerData s, SkyPatcherLayerData d, string? filter, int maxChars, string? selectEcho, string? after)
    {
        bool owner = after is null;
        var caveats = owner ? SkyPatcherWire.Caveats(d, maxChars) : "";
        int cap = maxChars - caveats.Length;
        var sb = new StringBuilder();
        var files = s.Files;
        var g = s.Format.Lines!;
        sb.Append(Header(s)).Append('\n');
        if (files.Count == 0)
            sb.Append("no SPID files in the active order: no file directly in Data has '").Append(s.Format.Folders[0].NameToken)
              .Append("' in its name and the '").Append(s.Format.Folders[0].Extension).Append("' extension.\n");

        bool Matches(SpidFileData f) => filter is null || f.Matches(filter);
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
        string fileCut = owner ? FileCut : JoinedCut("  ... [SPID files", after!, "spid", "SPID"),
               lineCut = owner ? LineCut : JoinedCut("      ... [SPID lines", after!, "spid", "SPID");
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
                SpidSelected? picked = null;
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

    const string Hint = "\n→ filter='<file or mod>' expands a SPID file's lines with what SPID does with each; formids= or plugins= lists the lines naming a form, labelled target, filter or value.";
    const string FileCut = "  ... [SPID files cut at max_chars — raise it or narrow filter=]\n";
    const string LineCut = "      ... [SPID lines cut at max_chars — raise it or narrow filter=, formids= or plugins=]\n";
    /// <summary>A later part's cut notice: the earlier parts take the cap first, so it names the call that reads the format alone.</summary>
    internal static string JoinedCut(string head, string after, string key, string name)
        => head + " cut at max_chars after " + after + " — distributor=[\"" + key + "\"] reads " + name + " alone at the whole max_chars]\n";

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

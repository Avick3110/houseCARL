using System.Text;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>A distributor format's own words in its part of the layer answer; everything else in the part is
/// <see cref="DistributorWire"/>'s, the same for every format.</summary>
/// <param name="Name">The name the answer gives the format ("SPID").</param>
/// <param name="Key">Its <c>distributor=</c> key.</param>
/// <param name="Reads">The versions its line grammar reads the same.</param>
/// <param name="Skipped">The header's clause for this many lines the format skips as it splits the file.</param>
/// <param name="SkipLead">A skipped line's lead, before ": it ".</param>
/// <param name="ZeroTail">The selector zero's close, after "or by an EditorID": how the format names a plugin, if it does.</param>
/// <param name="TermWhere">Where a plugin term sits, in its row ("form filter").</param>
/// <param name="TermDefines">What a plugin term selects of its plugin ("NPCs").</param>
/// <param name="ReadHoldsParseNotes">True when a line's read notes begin with its split notes, so a read line prints
/// its drop before them; false prints the split notes first.</param>
/// <param name="OrderLine">The order line a part with line rows states under an expanded listing; null for a format with none.</param>
sealed record DistributorWords(
    string Name, string Key, Func<DistributorFormat, IReadOnlyList<string>> Reads, Func<int, string> Skipped,
    string SkipLead, string ZeroTail, string TermWhere, string TermDefines, bool ReadHoldsParseNotes,
    Func<DistributorLayerData, string>? OrderLine = null);

/// <summary>Renders the distributor formats' parts of the layer answer, bounded by max_chars with explicit cut notices,
/// and joins the formats one call reads: SkyPatcher's part is its own render, unchanged, and each later format takes
/// what the earlier ones leave.</summary>
static class DistributorWire
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
        var read = new List<(DistributorLayerData Data, DistributorWords Words)>();
        if (d.Spid is { } spid) read.Add((spid, SpidWire.Words));
        if (d.Kid is { } kid) read.Add((kid, KidWire.Words));
        if (d.Bos is { } bos) read.Add((bos, BosWire.Words));
        if (d.Flm is { } flm) read.Add((flm, FlmWire.Words));
        // Each part skips the notes every earlier distributor part holds, whether or not that part printed them.
        var parts = read.Select((p, i) => new Part(p.Words.Name, p.Words.Key,
            (c, after) => Render(p.Data, p.Words, d, read.Take(i).Select(e => e.Data).ToList(), filter, c, selectEcho, after),
            () => Header(p.Data, p.Words) + Lists(p.Words.Key))).ToList();
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

    /// <summary>A format's header line, without its line end: the model, the profile, the files and the lines by type.</summary>
    static string Header(DistributorLayerData s, DistributorWords w)
    {
        var sb = new StringBuilder();
        var files = s.Files;
        int lines = files.Sum(f => f.Lines.Count(l => l.Skipped is null));
        int skipped = files.Sum(f => f.Lines.Count(l => l.Skipped is not null));
        // A format whose grammar only its modelled version reads names no other.
        var others = w.Reads(s.Format).Where(v => v != s.Format.Version).ToList();
        sb.Append(w.Name).Append(" layer (").Append(s.Format.Name).Append(' ').Append(s.Format.Version).Append(" model")
          .Append(others.Count == 0 ? "" : "; " + string.Join(", ", others) + " reads its files the same").Append(") — profile '")
          .Append(s.ProfileName).Append("' — ").Append(files.Count).Append(" file(s) in Data (")
          .Append(files.Count(f => f.File.NotApplied is null && f.ReadError is null)).Append(" read), ")
          .Append(lines).Append(" line(s)");
        var byType = files.SelectMany(f => f.Lines).Where(l => l.Skipped is null)
            .GroupBy(l => l.HeaderKey).OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.Ordinal);
        if (lines > 0) sb.Append(": ").Append(string.Join(", ", byType.Select(x => x.Key + " " + x.Count())));
        if (skipped > 0) sb.Append("; ").Append(w.Skipped(skipped));
        return sb.ToString();
    }

    /// <summary>A format's part. <paramref name="after"/> is null when it is the whole answer, so it closes on the build
    /// caveats and the selectors' note SkyPatcher's part otherwise carries; else it names the parts before it, as its
    /// cut notices say. <paramref name="earlier"/> are the distributor parts before it, whose notes it does not repeat.</summary>
    static string Render(DistributorLayerData s, DistributorWords w, SkyPatcherLayerData d, IReadOnlyList<DistributorLayerData> earlier,
                         string? filter, int maxChars, string? selectEcho, string? after)
    {
        bool owner = after is null;
        var caveats = owner ? SkyPatcherWire.Caveats(d, maxChars) : "";
        int cap = maxChars - caveats.Length;
        var files = s.Files;
        var sb = new StringBuilder(Header(s, w)).Append('\n');
        if (files.Count == 0)
            sb.Append("no ").Append(w.Name).Append(" files in the active order: no file directly in Data has '").Append(s.Format.Folders[0].NameToken)
              .Append("' in its name and the '").Append(s.Format.Folders[0].Extension).Append("' extension")
              .Append(string.Concat(s.Format.Folders.Skip(1).Select(f => ", and no '" + f.Extension + "' file is in Data\\" + f.Root)))
              .Append(".\n");

        bool Matches(DistributorFileData f) => filter is null || f.Matches(filter);
        var holding = s.Selected?.Keys.Select(k => k.File).ToHashSet();
        var shown = Enumerable.Range(0, files.Count).Where(i => Matches(files[i]) && (holding is null || holding.Contains(i))).ToList();
        bool expand = filter is not null || s.Selected is not null;

        if (s.Selected is not null)
        {
            int n = s.Selected.Count(kv => Matches(files[kv.Key.File]));
            sb.Append("lines naming a form of ").Append(selectEcho).Append(filter is null ? "" : " in filter '" + filter + "'")
              .Append(" — ").Append(n).Append(" line(s) in ").Append(shown.Count).Append(" of ").Append(files.Count).Append(" file(s)");
            if (n == 0)
                return Close(s, d, earlier, sb.Append(".\n\nnothing matched: no ").Append(w.Name).Append(" line names such a form as a target, a filter form or a value. " +
                    "A line names a form by FormID~Plugin, by a 0x runtime FormID or by an EditorID").Append(w.ZeroTail).Append('\n'), cap, caveats, owner);
            sb.Append("; only those files are listed, each selected form labelled where the line names it.\n");
        }
        else if (filter is not null)
        {
            sb.Append("filter '").Append(filter).Append("' — ").Append(shown.Count).Append(" of ").Append(files.Count).Append(" file(s) match");
            if (shown.Count == 0)
                return Close(s, d, earlier, sb.Append(".\n\nnothing matched: no ").Append(w.Name).Append(" file name or providing mod contains '").Append(filter).Append("'.\n"), cap, caveats, owner);
            sb.Append(", expanded to their lines with what ").Append(w.Name).Append(" does with each.\n");
        }
        if (expand && s.LineRows.Count > 0 && w.OrderLine is { } order) sb.Append(order(s));

        var notes = NoteLines(s, d, earlier, owner, Math.Min(cap / BatchRender.CaveatShare, Math.Max(0, cap - sb.Length)));
        cap -= notes.Length;
        string fileCut = owner ? "  ... [" + w.Name + " files cut at max_chars — raise it or narrow filter=]\n" : JoinedCut("  ... [" + w.Name + " files", after!, w),
               lineCut = owner ? "      ... [" + w.Name + " lines cut at max_chars — raise it or narrow filter=, formids= or plugins=]\n" : JoinedCut("      ... [" + w.Name + " lines", after!, w);
        var hintText = "\n→ filter='<file or mod>' expands a " + w.Name + " file's lines with what " + w.Name + " does with each; formids= or plugins= lists the lines naming a form, labelled target, filter or value.";
        string hint = !expand && sb.Length + hintText.Length + fileCut.Length <= cap ? hintText : "";
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
                DistributorSelected? picked = null;
                if (s.Selected is not null && !s.Selected.TryGetValue((fi, li), out picked)) continue;
                var text = LineText(s, w, fi, li, picked);
                if (sb.Length + text.Length > room) { sb.Append(lineCut); cut = true; break; }
                sb.Append(text);
            }
            if (cut) break;
        }
        sb.Append(notes).Append(caveats).Append(hint);
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>A later part's cut notice: the earlier parts take the cap first, so it names the call that reads the format alone.</summary>
    static string JoinedCut(string head, string after, DistributorWords w)
        => head + " cut at max_chars after " + after + " — distributor=[\"" + w.Key + "\"] reads " + w.Name + " alone at the whole max_chars]\n";

    /// <summary>A zero answer's close: its notes and, when it is the whole answer, the caveats.</summary>
    static string Close(DistributorLayerData s, SkyPatcherLayerData d, IReadOnlyList<DistributorLayerData> earlier, StringBuilder sb, int cap, string caveats, bool owner)
        => sb.Append(NoteLines(s, d, earlier, owner, Math.Max(0, cap - sb.Length))).Append(caveats).ToString().TrimEnd('\n');

    /// <summary>The selectors' note (when the part is the whole answer; SkyPatcher's or an earlier part otherwise carries
    /// it) and the unreadable-plugin notes no earlier part holds, cut and counted as the layer's notes are.</summary>
    static string NoteLines(DistributorLayerData s, SkyPatcherLayerData d, IReadOnlyList<DistributorLayerData> earlier, bool owner, int room)
    {
        var said = (d.SkyPatcherRead ? d.UnreadableNotes : []).Concat(earlier.SelectMany(e => e.UnreadableNotes)).ToHashSet();
        var list = (owner && d.SelectNote is { } n ? new[] { n } : Array.Empty<string>())
            .Concat(s.UnreadableNotes.Where(u => !said.Contains(u))).ToList();
        return list.Count == 0 ? "" : BatchRender.CutLines(new BatchRender.CaveatList(list, "[!] ", "note(s)"), room);
    }

    /// <summary>One file's row: its place in the format's file order, its read lines, the mod that wins it and its flags.</summary>
    static string FileRow(DistributorFileData f, int place, int count)
    {
        var sb = new StringBuilder("  - ").Append(f.File.RelPath).Append("  (file ").Append(place).Append(" of ").Append(count).Append(", ")
            .Append(f.Lines.Count(l => l.Skipped is null)).Append(" line(s)) ← ").Append(f.File.WinningProvider ?? "(no provider)");
        if (f.File.NotApplied is { } why) sb.Append("  [!] NOT applied: ").Append(why);
        if (f.ReadError is { } err) sb.Append("  [!] ").Append(err);
        if (f.File.ShadowedProviders.Count > 0) sb.Append("  [!] shadows same-path copies from ").Append(string.Join(", ", f.File.ShadowedProviders)).Append(", never read");
        if (f.File.OrderNote is { } order) sb.Append("  [!] ").Append(order);
        return sb.Append('\n').ToString();
    }

    /// <summary>One line with its notes, the format's own row under it and, under a selector, the labels of what it keeps.</summary>
    static string LineText(DistributorLayerData s, DistributorWords w, int fi, int li, DistributorSelected? picked)
    {
        var l = s.Files[fi].Lines[li];
        var sb = new StringBuilder("      :").Append(l.Number).Append("  ").Append(l.Raw.Trim()).Append('\n');
        if (l.Skipped is { } skip) return sb.Append("          [!] ").Append(w.SkipLead).Append(": it ").Append(skip).Append('\n').ToString();
        var read = s.Reads?[fi][li];
        if (read is null || !w.ReadHoldsParseNotes)
            foreach (var n in l.Notes) sb.Append("          [!] ").Append(n).Append('\n');
        if (read is not null)
        {
            if (read.Dropped is { } why) sb.Append("          [!] ").Append(w.Name).Append(" drops this line: ").Append(why).Append('\n');
            foreach (var n in read.Notes) sb.Append("          [!] ").Append(n).Append('\n');
        }
        if (s.LineRows.TryGetValue((fi, li), out var row)) sb.Append(row);
        foreach (var f in picked?.Forms ?? [])
            sb.Append("          ↳ ").Append(Place(f.Place)).Append(' ').Append(f.Where).Append(": ").Append(f.Token).Append(" → ").Append(FormIdToken.Of(f.Form)).Append('\n');
        foreach (var t in picked?.PluginTerms ?? [])
            sb.Append("          ↳ filter ").Append(w.TermWhere).Append(": ").Append(t).Append(" (the plugin, selecting the ").Append(w.TermDefines).Append(" it defines)\n");
        return sb.ToString();
    }

    static string Place(SkyPatcherOverlay.FormPlace p) => p switch
    {
        SkyPatcherOverlay.FormPlace.Target => "target",
        SkyPatcherOverlay.FormPlace.Value => "value",
        _ => "filter",
    };
}

using System.Text;
using System.Text.Json;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Pex;

namespace HousecarlMcp;

/// <summary>decompile_script over a set of scripts: the shared selection's Data paths (each read loose or out of its
/// archive, no extract step), files on disk, or both, into one output folder, one row per script; contracts in
/// docs/architecture/papyrus-decompile.md.</summary>
internal static class DecompileBatch
{
    /// <summary>One entry of <c>paths=</c>: a Data path, or a file on disk.</summary>
    internal readonly record struct Entry(string Path, bool OnDisk);

    /// <summary>One script the call decompiles: its Data path or file on disk, the copy chosen, and on a row that is not decompiled the reason.</summary>
    sealed record Row(string Path, bool OnDisk, PlacementSource? Source = null, string? Winner = null, string? Error = null);

    /// <summary>What decompiling one row did; <c>Error</c> set means it was not decompiled (or not wholly written).</summary>
    sealed record Done(Row Row, IReadOnlyList<string> Written, int Functions, int Failed, IReadOnlyList<string> Failures,
                       bool Optimizer, string? Error)
    {
        internal string Status => Error is not null ? "error" : Failed > 0 ? "partial" : "ok";
    }

    /// <summary>The call's inputs, past the tool's own refusals.</summary>
    internal sealed record Call(IReadOnlyList<Entry> Entries, IReadOnlyList<string>? Under, string? Provider, string? SourceProvider,
                                string? Patch, string? Into, string? OutPath, RowWindow Window, bool CountsOnly, bool Json, int Cap);

    internal static ToolAnswer Run(LoadOrderService svc, Call c)
    {
        bool json = c.Json;
        bool chosenOutput = !string.IsNullOrWhiteSpace(c.OutPath);
        bool ignoredLane = chosenOutput && (!string.IsNullOrWhiteSpace(c.Patch) || !string.IsNullOrWhiteSpace(c.Into));
        string provider = c.Provider?.Trim() ?? "";
        string sourceProvider = c.SourceProvider?.Trim() ?? "";
        var dataPaths = c.Entries.Where(e => !e.OnDisk).Select(e => e.Path).ToList();
        bool selecting = dataPaths.Count > 0 || c.Under is { Count: > 0 } || provider.Length > 0;

        // The selection reads the load order, and so does the default lane; out_path= over files on disk needs neither.
        if ((selecting || !chosenOutput) && svc.ConfigPromptOrNull() is { } cfgPrompt) return ToolAnswer.Refusal(json, cfgPrompt);
        if (!chosenOutput && LaneSentences.PatchIntoFolderRefusal(c.Patch, c.Into, ".psc") is { } patchInto)
            return ToolAnswer.Refusal(json, "error: " + patchInto);

        ScriptSelectionData? sel = selecting ? svc.SelectScripts(dataPaths, c.Under, provider.Length > 0 ? provider : null) : null;
        if (sel?.Refusal is { } refused) return ToolAnswer.Refusal(json, refused);

        // Rows in the order given: paths= entries as written (files on disk among them), then the selectors' matches.
        // The selection's first entries are the explicit Data paths, one each, in the order given.
        var rows = new List<Row>();
        int nextExplicit = 0;
        foreach (var e in c.Entries)
            rows.Add(e.OnDisk ? new Row(e.Path, true) : new Row(sel!.Scripts[nextExplicit++], false));
        if (sel is not null) rows.AddRange(sel.Scripts.Skip(dataPaths.Count).Select(p => new Row(p, false)));
        int total = rows.Count;
        var window = c.Window.Apply(rows).Select(r => Locate(sel?.View, r, sourceProvider)).ToList();

        var notes = new List<string>(sel?.Notes ?? Array.Empty<string>());
        if (sel is { NotScripts: > 0 } s0)
            notes.Add($"{s0.NotScripts} file(s) the selectors matched are not .pex scripts and were left out; " +
                      "under=[\"scripts/**/*.pex\"] selects only scripts.");
        // provider= picks which scripts, and each is read from its winner: one another provider wins is said, with the knob that reads the provider's own.
        int wonElsewhere = provider.Length == 0 || sourceProvider.Length > 0 ? 0
            : window.Count(r => r.Source is { } src && !AssetSourceSelection.NameMatches(src, provider));
        if (wonElsewhere > 0)
            notes.Add($"{wonElsewhere} script(s) provider '{provider}' ships are won by another provider, and the winning copy " +
                      $"was read; source_provider=\"{provider}\" reads its own copies.");
        if (sourceProvider.Length > 0 && c.Entries.Any(e => e.OnDisk))
            notes.Add("source_provider= picks whose copy of a Data path is read; a file on disk is read as itself.");

        // The output folder, resolved before any read so a folder error costs nothing; a window with nothing to read cuts none.
        OutputLocations.RiderFolder? rf = null;
        if (window.Any(r => r.Error is null))
        {
            try
            {
                rf = chosenOutput
                    ? OutputLocations.ResolveExplicitSourceFolder(c.OutPath!)
                    : svc.ResolveDecompiledSourceFolder(c.Patch, c.Into);
            }
            catch (InvalidOperationException ex)
            {
                return ToolAnswer.Refusal(json, "error: " + ex.Message + (ignoredLane
                    ? "\nnote: patch=/into= were ignored because out_path= was given, and nothing was written. Fix out_path=, or drop it to write into the patch folder patch=/into= names."
                    : ""));
            }
        }

        if (ignoredLane) notes.Add("out_path= was given, so patch=/into= are ignored (the .psc files land in out_path, not a houseCARL patch folder).");
        // Nothing in the window can be read: every row says why, and no folder was cut.
        if (rf is not { } folder)
            return Render(new Answer(window.Select(r => NotDone(r, r.Error)).ToList(), total, c.Window, provider, sourceProvider,
                                     null, chosenOutput, notes, null), c);

        // Pass 1, the class hierarchy: the baseline and mods-tree sources, every script in the window, the .pex files
        // beside each file on disk, and each script's parent chain read off the load order. Nothing parsed is kept.
        var hierarchy = svc.ClassParentsForDecompile();
        var edges = new Dictionary<string, string>(hierarchy.Edges, StringComparer.OrdinalIgnoreCase);
        var objects = new List<string>();
        for (int i = 0; i < window.Count; i++)
        {
            if (window[i].Error is not null) continue;
            var (pex, err) = Read(sel?.View, window[i]);
            if (pex is null) { window[i] = window[i] with { Error = err }; continue; }
            PapyrusClassParents.AddFromPex(edges, pex);
            objects.AddRange(pex.Objects.Select(o => o.Name).Where(n => !string.IsNullOrWhiteSpace(n)));
        }
        var thin = new List<string>();
        foreach (var dir in window.Where(r => r.OnDisk && r.Error is null).Select(r => Path.GetDirectoryName(r.Path)!)
                                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var scan = PapyrusClassParents.AddFromPexFolder(edges, dir);
            if (scan.FilesFailed > 0) thin.Add($"{scan.FilesFailed} .pex file(s) in '{dir}' could not be read");
            if (scan.ListingFailed) thin.Add($"the folder '{dir}' could not be listed to the end");
        }
        if (sel is not null) thin.AddRange(AddParentChains(sel.View, edges, objects, BaselineRoots.Value));
        hierarchy = hierarchy with { SiblingPexMissing = thin.Count > 0 ? string.Join(", and ", thin) : null };
        // What the kept half of the hierarchy note may claim: only the sources this call read. It renders only beside a
        // written row, which is a file on disk or a Data path, so the list holds two sources or three.
        var read = new List<string> { "these scripts" };
        if (window.Any(r => r.OnDisk && r.Error is null)) read.Add("the .pex files beside each file on disk");
        if (sel is not null) read.Add("each parent script read off the load order");
        var hierarchyRead = string.Join(", ", read.SkipLast(1)) + " and " + read[^1];

        // Pass 2: decompile and write, row by row; a row that cannot land is that row's error, and the rest still land.
        var done = new List<Done>(window.Count);
        foreach (var r in window)
        {
            var (pex, err) = r.Error is null ? Read(sel?.View, r) : (null, r.Error);
            if (pex is null) { done.Add(NotDone(r, err)); continue; }
            var o = DecompileTools.WriteObjects(pex, edges, folder.OutputDir);
            var name = Path.GetFileName(r.Path);
            string? why =
                o.UnnamedObject ? $"'{name}' carries an unnamed script object."
                : o.EscapingObject is not null ? $"'{name}' names a script object '{o.EscapingObject}', which is not a plain script name, so its .psc would not land in the output folder."
                : o.ExistingTarget is not null ? $"'{o.ExistingTarget}' already exists — houseCARL never overwrites a source file; move or delete it, or pass another " +
                                                 (chosenOutput ? "out_path=." : "patch= (or into= another patch folder).")
                : null;
            done.Add(new Done(r, o.Written, o.FunctionsTotal, o.FunctionsFailed, o.Failures, o.OptimizerHints > 0, why));
        }

        if (done.All(d => d.Written.Count == 0) && svc.RemoveOrNameRiderResidue(folder) is { } residue)
            notes.Add($"the freshly created mod folder at '{residue}' still holds partial output — delete it or retry with into=.");
        return Render(new Answer(done, total, c.Window, provider, sourceProvider, folder, chosenOutput, notes, (hierarchy, hierarchyRead)), c);
    }

    static ToolAnswer Render(Answer a, Call c)
    {
        var lane = NextPageLane(a, c);
        return ToolAnswer.Served(c.Json ? RenderJson(a, c.CountsOnly, c.Cap, lane) : RenderText(a, c.CountsOnly, c.Cap, lane));
    }

    /// <summary>The output knob a next page passes to land in this call's folder: out_path= or into= as given, else the
    /// patch this call wrote into (patch= as given while nothing landed).</summary>
    static (string Name, string Value)? NextPageLane(Answer a, Call c) =>
        a.ChosenOutput ? ("out_path", c.OutPath!.Trim())
        : !string.IsNullOrWhiteSpace(c.Into) ? ("into", c.Into.Trim())
        : a.Folder is { } f && a.AnyWritten ? ("into", f.Stem)
        : !string.IsNullOrWhiteSpace(c.Patch) ? ("patch", c.Patch.Trim())
        : null;

    static string LaneArg((string Name, string Value)? lane) => lane is { } l ? $" {l.Name}=\"{l.Value}\"" : "";

    /// <summary>The accounting counts: a window is decompiled whole, so what remains starts after it, not after the rows listed.</summary>
    static TransportCounts Counts(Answer a, int rendered) =>
        TransportAccounting.Tally(a.Total, a.Rows.Count, rendered, a.Window, a.Notes.Count)
            with { Remaining = Math.Max(a.Total - a.Window.Offset - a.Rows.Count, 0) };

    /// <summary>The [accounting] line, then the write lane's own paging advice: the next page after the window, into this call's folder.</summary>
    static string Accounting(TransportCounts c, int windowed, (string Name, string Value)? lane)
    {
        var composed = TransportAccounting.Compose(c, "script(s)", everySentence: false);
        int lineEnd = composed.IndexOf('\n', 2);
        var sb = new StringBuilder(lineEnd < 0 ? composed : composed[..lineEnd]).Append(NextPage(c, windowed, lane));
        if (TransportAccounting.PastEnd(c)) sb.Append('\n').Append(TransportAccounting.PastEndSentence(c, "script(s)"));
        return sb.ToString();
    }

    /// <summary>The next page after the window, into this call's folder, while scripts remain.</summary>
    static string NextPage(TransportCounts c, int windowed, (string Name, string Value)? lane) =>
        c.Remaining > 0
            ? $"\nthe selection is longer than this window: re-call with{LaneArg(lane)} limit={c.NextLimit} offset={c.Offset + windowed} for the next page."
            : "";

    static Done NotDone(Row r, string? error) => new(r, Array.Empty<string>(), 0, 0, Array.Empty<string>(), false, error);

    static PexFile ParsePex(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return PexFile.CreateFromStream(ms, GameCategory.Skyrim);
    }

    /// <summary>A row with its copy located: a file on disk checked, a Data path picked through the one source policy.</summary>
    static Row Locate(AssetResolver.AssetView? view, Row r, string sourceProvider)
    {
        if (r.OnDisk)
        {
            if (!File.Exists(r.Path)) return r with { Error = $"no such file: '{r.Path}'." };
            if (!r.Path.EndsWith(".pex", StringComparison.OrdinalIgnoreCase)) return r with { Error = $"'{Path.GetFileName(r.Path)}' is not a .pex compiled script." };
            return r with { Path = Path.GetFullPath(r.Path) };
        }
        if (!r.Path.EndsWith(".pex", StringComparison.OrdinalIgnoreCase))
            return r with { Error = $"'{r.Path}' is not a .pex compiled script." };
        var pick = AssetLayers.PickScript(view!.Value, r.Path, sourceProvider.Length > 0 ? sourceProvider : null);
        return r with { Source = pick.Source, Winner = pick.Winner, Error = pick.Error };
    }

    /// <summary>One row's script, read from disk or through the view (loose, or one archive entry), or why it cannot be.</summary>
    static (PexFile? Pex, string? Error) Read(AssetResolver.AssetView? view, Row r)
    {
        var name = Path.GetFileName(r.Path);
        PexFile pex;
        try
        {
            if (r.OnDisk) pex = PexFile.CreateFromFile(r.Path, GameCategory.Skyrim);
            else
            {
                var (bytes, err) = view!.Value.ReadSource(r.Source!);
                if (bytes is null) return (null, err ?? "could not read the resolved script bytes.");
                pex = ParsePex(bytes);
            }
        }
        catch (Exception ex)
        {
            return (null, $"Mutagen cannot read '{name}' ({ex.GetType().Name}: {ex.Message}), the known unreadable-pex class (corrupt, non-standard, or obfuscated).");
        }
        return pex.Objects.Count == 0 ? (null, $"'{name}' contains no script objects.") : (pex, null);
    }

    /// <summary>The vanilla baseline's root classes (Form, Alias, ActiveMagicEffect, ...): parents it names that have no
    /// parent of their own. Read once per process; a missing baseline gives none, so the climb reads those roots too.</summary>
    static readonly Lazy<IReadOnlySet<string>> BaselineRoots = new(() =>
    {
        var (baseline, _) = PapyrusClassParents.LoadBaseline(Path.Combine(AppContext.BaseDirectory, "vanilla-class-parents.json"));
        return baseline.Values.Where(p => !baseline.ContainsKey(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    });

    /// <summary>Climb each object's parent chain, reading a parent the edges do not know from its winning
    /// <c>scripts\&lt;parent&gt;.pex</c> in the load order; a vanilla root class (<paramref name="known"/>) is not read.
    /// The clause for each parent that would not read.</summary>
    static IEnumerable<string> AddParentChains(AssetResolver.AssetView view, Dictionary<string, string> edges, IReadOnlyList<string> objects,
                                               IReadOnlySet<string> known)
    {
        // A window script was read already: one with no edge declares no parent, so it is never read again as one.
        var tried = new HashSet<string>(objects, StringComparer.OrdinalIgnoreCase);
        var unread = new List<string>();
        foreach (var start in objects)
        {
            var cur = start;
            var climbed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // climbed ends a loop in the edges; tried reads each parent once per call.
            while (climbed.Add(cur))
            {
                if (edges.TryGetValue(cur, out var up)) { cur = up; continue; }
                if (known.Contains(cur) || !tried.Add(cur)) break;
                var rel = $@"scripts\{cur}.pex";
                PlacementResolution place;
                try { place = view.ResolveForPlacement(rel); }
                catch (ArgumentException) { break; }
                if (place.Sources.Count == 0) break;   // a root class, or a parent the order does not ship: nothing to read
                var (bytes, err) = view.ReadSource(place.Sources[0]);
                if (bytes is null) { unread.Add($"'{rel}' ({err})"); break; }
                try { PapyrusClassParents.AddFromPex(edges, ParsePex(bytes)); }
                catch (Exception ex) { unread.Add($"'{rel}' ({ex.GetType().Name})"); break; }
                if (!edges.TryGetValue(cur, out up)) break;
                cur = up;
            }
        }
        if (unread.Count > 0) yield return $"{unread.Count} parent script(s) could not be read off the load order: {string.Join(", ", unread.Take(5))}{(unread.Count > 5 ? ", …" : "")}";
    }

    /// <summary>Everything a render states.</summary>
    sealed record Answer(IReadOnlyList<Done> Rows, int Total, RowWindow Window, string Provider, string SourceProvider,
                         OutputLocations.RiderFolder? Folder, bool ChosenOutput, IReadOnlyList<string> Notes,
                         (ClassParents Parents, string Read)? Hierarchy)
    {
        internal int Ok => Rows.Count(d => d.Status == "ok");
        internal int Partial => Rows.Count(d => d.Status == "partial");
        internal int Errors => Rows.Count(d => d.Status == "error");
        internal bool AnyWritten => Rows.Any(d => d.Written.Count > 0);
    }

    const string Losses = "known format losses (every decompiler): a parameter default is not in the .pex, so it survives only where a call in the same script omitted the argument — a parameter with no such call comes back with its default missing; comments/layout are gone (docstrings survive).";
    const string OptimizerNote = "optimizer-compiled patterns detected (Caprica class) — source is correct; byte-identity vs the original .pex under the CK compiler is not expected.";
    const string PartialMeaning = "a FAILED function is a loud comment block with its raw bytecode in the .psc, which will NOT compile as-is.";
    const string CutRows = "every script in this window was decompiled whatever max_chars cuts from the listing, and the counts above include the rows cut; one not decompiled wrote nothing, or only part of its .psc files.";

    static string Head(Answer a)
    {
        var sb = new StringBuilder("decompile: ").Append(a.Rows.Count).Append(" script(s)");
        if (a.Window.IsPaged || a.Rows.Count != a.Total) sb.Append(" in this window of ").Append(a.Total).Append(" selected");
        if (a.Provider.Length > 0) sb.Append(", provider \"").Append(a.Provider).Append('"');
        if (a.SourceProvider.Length > 0) sb.Append(", source \"").Append(a.SourceProvider).Append('"');
        sb.Append(" — ").Append(a.Ok).Append(" OK, ").Append(a.Partial).Append(" PARTIAL, ").Append(a.Errors).Append(" not decompiled");
        if (a.Folder is { } f && a.AnyWritten) sb.Append("\ninto '").Append(f.OutputDir).Append('\'');
        return sb.ToString();
    }

    static string Copy(Done d) => d.Row.OnDisk ? "the file on disk"
        : AssetWalkWire.Copy(d.Row.Source!) + (d.Row.Winner is { } w ? $", not the winning copy ({w} wins)" : "");

    static string RowText(Done d)
    {
        if (d.Error is not null)
            return $"\n  ERROR {d.Row.Path} — {d.Error}" + (d.Written.Count > 0 ? $" Already written for this script: {string.Join(", ", d.Written.Select(Path.GetFileName))}." : "");
        var sb = new StringBuilder("\n  ").Append(d.Failed > 0 ? "PARTIAL " : "OK ").Append(d.Row.Path)
            .Append(" ← ").Append(Copy(d)).Append(" → ").Append(string.Join(", ", d.Written.Select(Path.GetFileName)))
            .Append(" (").Append(d.Functions).Append(" function(s)");
        if (d.Failed > 0) sb.Append(", ").Append(d.Failed).Append(" FAILED");
        sb.Append(d.Optimizer ? ", optimizer-compiled)" : ")");
        foreach (var f in d.Failures) sb.Append("\n      FAIL ").Append(f);
        return sb.ToString();
    }

    static string Tail(Answer a, bool cutPossible)
    {
        var sb = new StringBuilder();
        foreach (var n in a.Notes) sb.Append("\nnote: ").Append(n);
        if (a.Partial > 0) sb.Append("\nnote: ").Append(PartialMeaning);
        if (a.Rows.Any(d => d.Optimizer)) sb.Append("\nnote: ").Append(OptimizerNote);
        if (a.AnyWritten)
        {
            if (a.Hierarchy is { } h)
                sb.Append(DecompileTools.HierarchySentence(h.Parents, h.Read));
            sb.Append('\n').Append(Losses);
            sb.Append(a.ChosenOutput
                ? "\nthe .psc files are in the folder you named with out_path= (path above) — review them, edit them, and recompile with " + ToolNames.CompileScript + "."
                : "\nthe .psc files are in a houseCARL patch-mod folder (path above) — review them, edit them, and recompile with " + ToolNames.CompileScript + " (into= the same patch).");
        }
        if (cutPossible) sb.Append("\nnote: ").Append(CutRows);
        return sb.ToString();
    }

    static string RenderText(Answer a, bool countsOnly, int cap, (string Name, string Value)? lane)
    {
        var head = Head(a);
        if (countsOnly) return head + Tail(a, false) + NextPage(Counts(a, 0), a.Rows.Count, lane);
        int notes = a.Notes.Count;
        var tailCut = Tail(a, true);
        int room = cap - head.Length - tailCut.Length - TransportAccounting.Reserve(a.Total, a.Rows.Count, a.Window, notes, "script(s)")
                   - LaneArg(lane).Length;
        var body = new StringBuilder();
        int rendered = 0;
        foreach (var d in a.Rows)
        {
            var t = RowText(d);
            if (body.Length + t.Length > room) break;
            body.Append(t);
            rendered++;
        }
        return head + body + Tail(a, rendered < a.Rows.Count) + Accounting(Counts(a, rendered), a.Rows.Count, lane);
    }

    static string RenderJson(Answer a, bool countsOnly, int cap, (string Name, string Value)? lane)
    {
        // The rows that fit the ceiling, judged on each row's own json; the document closes with the accounting either way.
        var rowsJson = new List<string>();
        int used = 0, room = Math.Max(cap - 4096, 0);
        foreach (var d in countsOnly ? Array.Empty<Done>() : a.Rows)
        {
            var one = RowJson(d);
            if (used + one.Length > room) break;
            rowsJson.Add(one);
            used += one.Length;
        }
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("tool", ToolNames.DecompileScript);
            if (a.Provider.Length > 0) w.WriteString("provider", a.Provider);
            if (a.SourceProvider.Length > 0) w.WriteString("source_provider", a.SourceProvider);
            if (a.Folder is { } f && a.AnyWritten) w.WriteString("output_dir", f.OutputDir);
            w.WriteString("lane", a.ChosenOutput ? "out_path" : "patch");
            w.WriteNumber("ok", a.Ok);
            w.WriteNumber("partial", a.Partial);
            w.WriteNumber("errors", a.Errors);
            if (!countsOnly)
            {
                w.WritePropertyName("rows");
                w.WriteStartArray();
                foreach (var r in rowsJson) w.WriteRawValue(r, skipInputValidation: true);
                w.WriteEndArray();
            }
            w.WriteStartArray("notes");
            foreach (var line in Tail(a, !countsOnly && rowsJson.Count < a.Rows.Count).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                w.WriteStringValue(line.StartsWith("note: ", StringComparison.Ordinal) ? line[6..] : line);
            w.WriteEndArray();
            var counts = Counts(a, rowsJson.Count);
            if (countsOnly) w.WriteNumber("total", a.Total);
            else TransportAccounting.WriteJson(w, counts);
            // The next page starts after the window, not after the rows listed; offset_note never sits beside it.
            // counts_only= gives the next page too, and nothing else of the accounting.
            if (!countsOnly || counts.Remaining > 0)
                TransportAccounting.WritePageAdviceJson(w, counts with { Rendered = a.Rows.Count }, "script(s)");
            if (counts.Remaining > 0 && lane is { } l) w.WriteString("next_" + l.Name, l.Value);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    static string RowJson(Done d)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("path", d.Row.Path);
            w.WriteBoolean("on_disk", d.Row.OnDisk);
            w.WriteString("status", d.Status);
            if (d.Row.Source is { } s)
            {
                w.WriteString("provider", s.ProviderName);
                w.WriteString("kind", s.Kind == AssetKind.Bsa ? "BSA" : "loose");
                if (s.OffOrder) w.WriteBoolean("off_order", true);
            }
            if (d.Row.Winner is { } win) w.WriteString("winner", win);
            w.WriteStartArray("written");
            foreach (var p in d.Written) w.WriteStringValue(p);
            w.WriteEndArray();
            if (d.Error is null)
            {
                w.WriteNumber("functions", d.Functions);
                w.WriteNumber("functions_failed", d.Failed);
                if (d.Failures.Count > 0)
                {
                    w.WriteStartArray("failures");
                    foreach (var f in d.Failures) w.WriteStringValue(f);
                    w.WriteEndArray();
                }
                if (d.Optimizer) w.WriteBoolean("optimizer_compiled", true);
            }
            else w.WriteString("error", d.Error);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}

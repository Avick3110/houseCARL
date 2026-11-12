using HousecarlCore;

namespace HousecarlMcp;

/// <summary><c>exclude=</c> on the shared file selection: Data paths (a file, or a folder and everything beneath it),
/// globs, or one <c>@file</c> such as a walk's <c>to_file=</c> output, removed from what the selectors chose, matched
/// ignoring case and slash direction. An entry the selection does not hold is counted in a note, never refused, and an
/// exclusion that empties the selection answers zero rows (assets.md, "Selecting a set of paths").</summary>
public sealed class FileExclusion
{
    /// <summary>The exclude= parameter's text, the same on every file tool that takes it.</summary>
    internal const string Text =
        "Optional. Paths to leave out of what the other selectors chose (paths=, under=, provider=, and formids= where the " +
        "tool takes it): Data paths, each a file or a folder and everything beneath it, or globs as under= takes them, " +
        "matched ignoring case and slash direction. An entry that names no selected path removes nothing and is counted " +
        "in a note; an exclusion that removes everything answers zero rows. What a mod ships that a walk does not reach " +
        "is two calls: " + ToolNames.AssetStatus + " walk=true records=[...] source_provider=\"<mod>\" " +
        "to_file=\"<abs>/reached.jsonl\", then this tool with provider=\"<mod>\" exclude=[\"@<abs>/reached.jsonl\"]. " +
        "A file from a walk that was cut (walk.max_nodes, walk.depth or the path bound) or written on another build " +
        "is still used, and the answer opens with a note saying so." + ListText.Paths;

    readonly HashSet<string> _plain;
    readonly List<Func<string, bool>> _globs;
    readonly IReadOnlyList<string> _leading;

    /// <summary>The exclusion as a file's query echo records it: the <c>@file</c> as the door echoes it, else the inline count.</summary>
    internal string Echo { get; }

    FileExclusion(HashSet<string> plain, List<Func<string, bool>> globs, IReadOnlyList<string> leading, string echo)
        => (_plain, _globs, _leading, Echo) = (plain, globs, leading, echo);

    /// <summary>The exclusion <paramref name="arg"/> names, null when it is unset, or the refusal for a list the door
    /// refuses or an entry that names no Data path. A file from another build or a cut walk is said, not refused.</summary>
    internal static (FileExclusion? Exclusion, string? Error) Read(ListArg? arg, LoadOrderService svc)
    {
        var read = arg.Read("exclude", ListKind.Exclusion);
        if (read.Error is not null) return (null, read.Error);
        if (read.Tokens is null) return (null, null);
        var plain = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in read.Tokens)
        {
            var entry = (raw ?? "").Trim();
            if (entry.Length == 0)
                return (null, "error: exclude= has a blank entry — pass Data paths, globs, or one \"@<absolute path>\" file " +
                              "such as a walk's to_file= output.");
            try
            {
                var norm = AssetResolver.ValidateRelPath(entry).TrimEnd('\\');
                if (norm.Length == 0) throw new ArgumentException($"an empty selector names nothing: '{entry}'");
                (AssetGlob.HasWildcard(norm) ? patterns : plain).Add(norm);
            }
            catch (ArgumentException ex) { return (null, $"error: exclude= entry '{entry}': {ex.Message}"); }
        }
        return (new FileExclusion(plain, patterns.Select(AssetGlob.Matcher).ToList(), read.Demand is { } d ? Leading(d, svc) : Array.Empty<string>(),
                                  read.EchoSource ?? $"{read.Tokens.Length} inline path(s)"), null);
    }

    /// <summary>What a file entry cannot say by its paths alone: its walk was cut, or it was written on another build.</summary>
    static List<string> Leading(ArtifactDemand d, LoadOrderService svc)
    {
        var lead = new List<string>();
        foreach (var n in d.Notes ?? Array.Empty<string>())
            if (n.StartsWith(AssetWalkWire.NotePrefix, StringComparison.Ordinal) && n[AssetWalkWire.NotePrefix.Length..] is var said && WalkCut.Says(said))
                lead.Add($"exclude= file '{d.Path}' is a lower bound of what its walk reaches — {said} So a path listed here " +
                         "may be one a whole walk reaches; re-walk with to_file= and pass the new file.");
        const string Redo = "re-run the call that wrote it with to_file= and pass the new file.";
        if (d.Epoch.Length == 0)
        {
            lead.Add($"exclude= file '{d.Path}' carries no epoch, so whether it was written on this build is unknown — {Redo}");
            return lead;
        }
        var (order, why) = AssetTools.TryStamp(svc);
        if (order is null)
            lead.Add($"exclude= file '{d.Path}' was written at epoch={d.Epoch}, and this call could not read the load order " +
                     $"to compare — {why}");
        else if (order.Epoch != d.Epoch && !LoadOrderResolver.IsCurrentEpochFormat(d.Epoch))
            lead.Add($"exclude= file '{d.Path}' carries an epoch an older houseCARL wrote (epoch={d.Epoch}), which this build " +
                     $"cannot compare with its own, so whether it holds what that call answers now is unknown — {Redo}");
        else if (order.Epoch != d.Epoch)
            lead.Add($"exclude= file '{d.Path}' was written on another build (epoch={d.Epoch}; this build is epoch={order.Epoch}), " +
                     $"so it may not hold what that call answers now — {Redo}");
        return lead;
    }

    /// <summary>The <paramref name="selected"/> entries this exclusion leaves, with its notes: the file's own first, then
    /// how many paths it removed and how many of its entries matched no selected path. A row with an error is kept.</summary>
    internal List<SelectedFile> Apply(List<SelectedFile> selected, List<string> notes)
    {
        var hitPlain = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hitGlob = new bool[_globs.Count];
        var kept = new List<SelectedFile>(selected.Count);
        foreach (var f in selected)
            if (f.Error is not null || !Matches(f.Path, hitPlain, hitGlob)) kept.Add(f);
        int entries = _plain.Count + _globs.Count;
        int unmatched = entries - hitPlain.Count - hitGlob.Count(h => h);
        notes.InsertRange(0, _leading);
        notes.Add($"exclude= removed {selected.Count - kept.Count:N0} of the {selected.Count:N0} selected path(s)"
                  + (unmatched > 0 ? $"; {unmatched:N0} of its {entries:N0} entries matched no selected path, so removed nothing." : "."));
        return kept;
    }

    /// <summary>Does an entry name <paramref name="path"/>: the path itself or a folder above it, or a glob; every entry
    /// that does is marked, so the note counts the ones that named nothing.</summary>
    bool Matches(string path, HashSet<string> hitPlain, bool[] hitGlob)
    {
        string norm;
        try { norm = AssetResolver.ValidateRelPath(path.Trim()); }
        catch (ArgumentException) { return false; }   // a bad explicit path answers as its own row
        bool hit = false;
        for (var p = norm; p.Length > 0; p = p.LastIndexOf('\\') is var cut and >= 0 ? p[..cut] : "")
            if (_plain.Contains(p)) { hitPlain.Add(p); hit = true; }
        for (int i = 0; i < _globs.Count; i++)
            if (_globs[i](norm)) { hitGlob[i] = true; hit = true; }
        return hit;
    }
}

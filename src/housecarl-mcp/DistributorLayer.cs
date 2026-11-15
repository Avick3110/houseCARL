using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

// The one reader of a distributor format's layer (SPID's, KID's): its files found off the asset door, read and resolved
// in the call's replay context, so the one EditorID table serves every format the call reads. Each format gives only
// its own split, its own line read and its own extras. Read-only; nothing is kept between calls.
internal sealed partial class AssetLayers
{
    /// <summary>Read one distributor format's layer in an open replay context: every file it reads, as it finds them,
    /// each line split by <paramref name="parse"/> (given the text and the file's Data-relative path); under a selector, or a <paramref name="filter"/> some file matches,
    /// every line of every file read by <paramref name="readAll"/> in one call, the format's own row under each line
    /// it places taken by <paramref name="lineRows"/> (null for none), and, with <paramref name="wanted"/>, the forms and plugin terms the
    /// records selectors keep.</summary>
    static DistributorLayerData DistributorLayer<TLine, TRead>(
        DistributorFormat format, SkyPatcherReplay replay, string? filter,
        Func<FormKey, bool>? wanted, IReadOnlyList<string>? namedPlugins, bool formIdsGiven, CancellationToken ct,
        Func<string, string, IReadOnlyList<TLine>> parse, Func<IReadOnlyList<TLine>, IReadOnlyList<TRead?>> readAll,
        Func<IReadOnlyList<(IReadOnlyList<TLine> Lines, IReadOnlyList<TRead?> Reads)>, IReadOnlyDictionary<(int File, int Line), string>>? lineRows = null)
        where TLine : class, DistributorLines.ILine where TRead : class, DistributorLines.IRead
    {
        var files = new List<(DistributorDiscovery.ConfigFile File, IReadOnlyList<TLine> Lines, string? ReadError)>();
        foreach (var f in DistributorDiscovery.Scan(replay.Assets, format))
        {
            ct.ThrowIfCancellationRequested();
            if (f.NotApplied is not null || f.LooseFilePath is null) { files.Add((f, [], null)); continue; }
            try { files.Add((f, parse(File.ReadAllText(f.LooseFilePath), f.RelPath), null)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                files.Add((f, [], $"could not be read ({ex.Message})"));
            }
        }
        var data = new DistributorLayerData(format, files.Select(x => new DistributorFileData(x.File, x.Lines, x.ReadError)).ToList(), replay.ProfileName);
        filter = string.IsNullOrWhiteSpace(filter) ? null : filter.Trim();
        if (wanted is null && (filter is null || !data.Files.Any(f => f.Matches(filter)))) return data;

        // Every line of every file in one read, so the table walks every type once before the string lookups narrow it,
        // and a format reading across files (KID's created keywords, SPID's first-wins order) sees them all.
        var flat = readAll(files.SelectMany(x => x.Lines).ToList());
        int at = 0;
        var reads = new List<IReadOnlyList<TRead?>>(files.Count);
        foreach (var x in files) { reads.Add(flat.Skip(at).Take(x.Lines.Count).ToList()); at += x.Lines.Count; }

        Dictionary<(int, int), DistributorSelected>? selected = null;
        if (wanted is not null)
        {
            var named = new HashSet<string>(namedPlugins ?? [], StringComparer.OrdinalIgnoreCase);
            selected = new();
            for (int fi = 0; fi < files.Count; fi++)
                for (int li = 0; li < reads[fi].Count; li++)
                {
                    if (reads[fi][li] is not { } read) continue;
                    var forms = read.Forms.Where(n => wanted(n.Form)).Distinct().ToList();
                    // A plugin term selects the records its plugin defines, so it touches that plugin, defined_in or not;
                    // it names no form, so formids= never selects it, and both selectors given leave it out.
                    var terms = namedPlugins is null || formIdsGiven ? [] :
                        read.PluginTerms.Where(named.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (forms.Count > 0 || terms.Count > 0) selected[(fi, li)] = new DistributorSelected(forms, terms);
                }
        }
        return data with
        {
            Reads = reads,
            LineRows = lineRows?.Invoke(files.Select((x, i) => (x.Lines, reads[i])).ToList()) ?? data.LineRows,
            Selected = selected,
            UnreadableNotes = UnreadableNotes(replay),
        };
    }
}

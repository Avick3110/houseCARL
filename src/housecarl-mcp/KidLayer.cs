using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

// The KID layer: its _KID.ini files found off the asset door, read and resolved in the call's replay context after
// SPID's, so the one EditorID table serves SkyPatcher's, SPID's and KID's lines. Read-only; nothing is kept between calls.
internal sealed partial class AssetLayers
{
    /// <summary>Read KID's layer in an open replay context: every file KID reads, as it finds them, each line split;
    /// under a selector, or a <paramref name="filter"/> some file matches, every form a line names resolved and, with
    /// <paramref name="wanted"/>, the forms and plugin terms the records selectors keep.</summary>
    internal KidLayerData KidLayer(SkyPatcherReplay replay, LoadOrderResolver.IndexView view, string? filter,
                                   Func<FormKey, bool>? wanted, IReadOnlyList<string>? namedPlugins, bool formIdsGiven, CancellationToken ct)
    {
        var format = RuntimeFormats.Kid;
        var g = format.KidLines!;
        var files = new List<KidFileData>();
        foreach (var f in DistributorDiscovery.Scan(replay.Assets, format))
        {
            ct.ThrowIfCancellationRequested();
            if (f.NotApplied is not null || f.LooseFilePath is null) { files.Add(new KidFileData(f, [], null)); continue; }
            try { files.Add(new KidFileData(f, KidLines.Parse(File.ReadAllText(f.LooseFilePath), g), null)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                files.Add(new KidFileData(f, [], $"could not be read ({ex.Message})"));
            }
        }
        var data = new KidLayerData(format, files, replay.ProfileName);
        filter = string.IsNullOrWhiteSpace(filter) ? null : filter.Trim();
        if (wanted is null && (filter is null || !files.Any(f => f.Matches(filter)))) return data;

        // Every line of every file in one read: KID creates every line's keyword before it reads any filter.
        var all = files.SelectMany(x => x.Lines).ToList();
        var flat = KidLines.ReadAll(all, new KidLines.Context(replay.Forms, view.WinnerClassOf, format));
        int at = 0;
        var reads = new List<IReadOnlyList<KidLines.LineRead?>>(files.Count);
        foreach (var x in files) { reads.Add(flat.Skip(at).Take(x.Lines.Count).ToList()); at += x.Lines.Count; }

        Dictionary<(int, int), KidSelected>? selected = null;
        if (wanted is not null)
        {
            var named = new HashSet<string>(namedPlugins ?? [], StringComparer.OrdinalIgnoreCase);
            selected = new();
            for (int fi = 0; fi < files.Count; fi++)
                for (int li = 0; li < reads[fi].Count; li++)
                {
                    if (reads[fi][li] is not { } read) continue;
                    var forms = read.Forms.Where(n => wanted(n.Form)).Distinct().ToList();
                    // A plugin term selects the items its plugin defines, so it touches that plugin, defined_in or not;
                    // it names no form, so formids= never selects it, and both selectors given leave it out.
                    var terms = namedPlugins is null || formIdsGiven ? [] :
                        read.PluginTerms.Where(named.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (forms.Count > 0 || terms.Count > 0) selected[(fi, li)] = new KidSelected(forms, terms);
                }
        }
        return data with { Reads = reads, Selected = selected, UnreadableNotes = UnreadableNotes(replay) };
    }
}

using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

// The SPID layer: its _DISTR.ini files found off the asset door, read and resolved in the call's replay context, so the
// one EditorID table serves SkyPatcher's lines and SPID's. Read-only; nothing is kept between calls.
internal sealed partial class AssetLayers
{
    /// <summary>Read SPID's layer in an open replay context: every file SPID reads, as it finds them, each line split;
    /// with <paramref name="resolve"/> every form a line names resolved, each first-wins line placed in its type's order,
    /// and, with <paramref name="wanted"/>, the forms and plugin terms the records selectors keep.</summary>
    internal SpidLayerData SpidLayer(SkyPatcherReplay replay, LoadOrderResolver.IndexView view, bool resolve,
                                     Func<FormKey, bool>? wanted, IReadOnlyList<string>? namedPlugins, bool formIdsGiven, CancellationToken ct)
    {
        var format = RuntimeFormats.Spid;
        var g = format.Lines!;
        var files = new List<SpidFileData>();
        foreach (var f in DistributorDiscovery.Scan(replay.Assets, format))
        {
            ct.ThrowIfCancellationRequested();
            if (f.NotApplied is not null || f.LooseFilePath is null) { files.Add(new SpidFileData(f, [], null)); continue; }
            try { files.Add(new SpidFileData(f, SpidLines.Parse(File.ReadAllText(f.LooseFilePath), g), null)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                files.Add(new SpidFileData(f, [], $"could not be read ({ex.Message})"));
            }
        }
        var data = new SpidLayerData(format, files, replay.ProfileName);
        if (!resolve) return data;

        // Every line of every file in one read, so the table walks every type once before the string lookups narrow it.
        var all = files.SelectMany(x => x.Lines).ToList();
        var ctx = new SpidLines.Context(replay.Forms, view.WinnerClassOf, format);
        var flat = SpidLines.ReadAll(all, ctx);
        int at = 0;
        var reads = files.Select(x => { var r = flat.Skip(at).Take(x.Lines.Count).ToList(); at += x.Lines.Count; return (IReadOnlyList<SpidLines.LineRead?>)r; }).ToList();
        var places = SpidLines.Places(files.Select((x, i) => (x.Lines, reads[i])).ToList(), g);

        Dictionary<(int, int), SpidSelected>? selected = null;
        if (wanted is not null)
        {
            selected = new();
            for (int fi = 0; fi < files.Count; fi++)
                for (int li = 0; li < reads[fi].Count; li++)
                {
                    if (reads[fi][li] is not { } read) continue;
                    var forms = read.Forms.Where(n => wanted(n.Form)).Distinct().ToList();
                    // A plugin term selects the NPCs its plugin defines, so it touches that plugin, defined_in or not;
                    // it names no form, so formids= never selects it, and both selectors given leave it out.
                    var terms = namedPlugins is null || formIdsGiven ? [] :
                        read.PluginTerms.Where(p => namedPlugins.Contains(p, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (forms.Count > 0 || terms.Count > 0) selected[(fi, li)] = new SpidSelected(forms, terms);
                }
        }
        var unreadable = replay.Unreadable.Select(u => u.Message).Distinct()
            .Select(msg => $"EditorID lookup: {msg} Lines naming a record it defines could not be resolved.").ToList();
        return data with { Reads = reads, Places = places, Selected = selected, UnreadableNotes = unreadable };
    }
}

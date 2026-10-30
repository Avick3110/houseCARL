using System.Text;
using System.Text.Json;

namespace HousecarlMcp;

/// <summary>Renders <see cref="AssetWalkData"/>: the build alarms and the walk's notes first, then one block per node
/// in walk order, then the from-source set as the assets= array <c>housecarl_place</c> takes.</summary>
static class AssetWalkWire
{
    public static string Render(AssetWalkData d, IReadOnlyList<string>? selectorNotes, int cap, bool json)
        => json ? Json(d, selectorNotes) : Text(d, selectorNotes, cap);

    /// <summary>The nodes a place call takes: the ones the named source ships, error nodes and duplicates out.</summary>
    internal static IReadOnlyList<string> PlaceSet(AssetWalkData d)
        => d.Nodes.Where(n => n.Standing == WalkStanding.FromSource).Select(n => n.Path).ToList();

    /// <summary>The place call's assets= array for <see cref="PlaceSet"/>.</summary>
    internal static string PlaceArray(IReadOnlyList<string> paths)
        => JsonSerializer.Serialize(paths.Select(p => new Dictionary<string, string> { ["path"] = p }));

    static string Standing(AssetWalkNode n, string? source) => n.Standing switch
    {
        WalkStanding.FromSource => $"FROM SOURCE: {Copy(n.Copy!)}",
        WalkStanding.Elsewhere => $"ELSEWHERE: {Copy(n.Copy!)} wins it" + (source is null ? "" : $"; \"{source}\" does not ship it"),
        WalkStanding.Present => $"WINS: {Copy(n.Copy!)}",
        WalkStanding.Absent => "ABSENT — no active mod or BSA provides this path" + (source is null ? "" : $", and \"{source}\" does not ship it"),
        _ => "error: " + n.Error,
    };

    static string Copy(PlacementSource s)
        => AssetSourceSelection.Describe(s) + (s.OffOrder ? " (not loaded by the game)" : "");

    static string Text(AssetWalkData d, IReadOnlyList<string>? selectorNotes, int cap)
    {
        var sb = new StringBuilder("asset walk — profile '").Append(d.ProfileName.Length > 0 ? d.ProfileName : "(unconfigured)")
            .Append("'  (").Append(d.Nodes.Count).Append(" node").Append(d.Nodes.Count == 1 ? "" : "s")
            .Append(d.SourceProvider is { } s ? $", source \"{s}\"" : "")
            .Append(", records from ").Append(string.Join(" > ", d.RecordSources)).Append(")\n");
        var room = RenderCap.For(cap, 0);
        BatchRender.AppendReadFailures(sb, d.BsaFailures, "an asset", room);
        BatchRender.AppendRootFailures(sb, d.RootFailures, "an asset", room);
        BatchRender.AppendDiscoveryWarnings(sb, d.Warnings, room);
        AssetWire.AppendSelectorNotes(sb, selectorNotes, room);
        if (d.Notes.Count > 0)
        {
            sb.Append("\n[!] walk notes (").Append(d.Notes.Count).Append("):\n");
            BatchRender.AppendLines(sb, d.Notes, "note(s)", room);
        }

        var counts = d.Nodes.GroupBy(n => n.Standing).ToDictionary(g => g.Key, g => g.Count());
        sb.Append("\nnodes: from_source=").Append(counts.GetValueOrDefault(WalkStanding.FromSource))
          .Append(" elsewhere=").Append(counts.GetValueOrDefault(WalkStanding.Elsewhere))
          .Append(" present=").Append(counts.GetValueOrDefault(WalkStanding.Present))
          .Append(" absent=").Append(counts.GetValueOrDefault(WalkStanding.Absent))
          .Append(" errors=").Append(counts.GetValueOrDefault(WalkStanding.Error))
          .Append(" keyed=").Append(d.Nodes.Count(n => n.Keyed)).Append('\n');

        var place = PlaceSet(d);
        var tail = place.Count == 0 ? ""
            : $"\nplace set ({place.Count}): {ToolNames.Place} source_provider=\"{d.SourceProvider}\" assets={PlaceArray(place)}\n"
              + (d.Nodes.Any(n => n.Standing == WalkStanding.FromSource && n.Keyed)
                  ? "  keyed nodes keep their FormID-named path: place them as they are only for a copy that keeps the FormKeys; a renumbering copy carries them itself.\n"
                  : "");
        int shown = 0;
        foreach (var n in d.Nodes)
        {
            var block = new StringBuilder("\n").Append(n.Path).Append('\n')
                .Append("  ").Append(n.Edge).Append(" of ").Append(n.PulledBy).Append(n.Keyed ? "   [keyed]" : "").Append('\n')
                .Append("  ").Append(Standing(n, d.SourceProvider)).Append('\n');
            if (n.Note is { } note) block.Append("  note: ").Append(note).Append('\n');
            if (cap > 0 && sb.Length + block.Length + tail.Length + 120 > cap) break;
            sb.Append(block);
            shown++;
        }
        if (shown < d.Nodes.Count)
            sb.Append($"\n[!] {d.Nodes.Count - shown} of {d.Nodes.Count} node(s) not shown: max_chars was reached. Raise max_chars, or narrow the seeds.\n");
        sb.Append(tail);
        return sb.ToString().TrimEnd('\n');
    }

    static string Json(AssetWalkData d, IReadOnlyList<string>? selectorNotes)
    {
        var doc = new Dictionary<string, object?>
        {
            ["tool"] = ToolNames.AssetStatus,
            ["walk"] = true,
            ["profile"] = d.ProfileName,
            ["source_provider"] = d.SourceProvider,
            ["record_sources"] = d.RecordSources,
            ["bsa_failures"] = d.BsaFailures,
            ["root_failures"] = d.RootFailures,
            ["warnings"] = d.Warnings,
            ["selector_notes"] = selectorNotes ?? Array.Empty<string>(),
            ["notes"] = d.Notes,
            ["truncated"] = d.Truncated,
            ["nodes"] = d.Nodes.Select(n => new Dictionary<string, object?>
            {
                ["path"] = n.Path,
                ["edge"] = n.Edge,
                ["pulled_by"] = n.PulledBy,
                ["standing"] = n.Standing switch
                {
                    WalkStanding.FromSource => "from_source", WalkStanding.Elsewhere => "elsewhere",
                    WalkStanding.Present => "present", WalkStanding.Absent => "absent", _ => "error",
                },
                ["provider"] = n.Copy?.ProviderName,
                ["provider_kind"] = n.Copy?.Kind == AssetKind.Bsa ? "BSA" : n.Copy is null ? null : "loose",
                ["keyed"] = n.Keyed,
                ["note"] = n.Note,
                ["error"] = n.Error,
            }).ToList(),
            ["place_set"] = PlaceSet(d).Select(p => new Dictionary<string, string> { ["path"] = p }).ToList(),
        };
        return JsonSerializer.Serialize(doc);
    }
}

using System.Text;
using System.Text.Json;

namespace HousecarlMcp;

/// <summary>Renders <see cref="AssetWalkData"/>: the build alarms and the walk's notes first, then one block per node
/// in walk order, then the from-source set as the assets= array <c>housecarl_place</c> takes. A walk past max_chars is
/// written whole to a results-directory file (SPEC §2.1.1), which the response names.</summary>
static class AssetWalkWire
{
    /// <summary><paramref name="spill"/> is this call's artifact disposition; <paramref name="truncated"/> is what the caller spills on.</summary>
    public static string Render(AssetWalkData d, IReadOnlyList<string>? selectorNotes, int cap, bool json, SpillState? spill, out bool truncated)
        => json ? Json(d, selectorNotes, cap, spill, out truncated) : Text(d, selectorNotes, cap, spill, out truncated);

    /// <summary>The nodes a place call takes: the ones the named source ships, error nodes and duplicates out.</summary>
    internal static IReadOnlyList<string> PlaceSet(AssetWalkData d)
        => d.Nodes.Where(n => n.Standing == WalkStanding.FromSource).Select(n => n.Path).ToList();

    /// <summary>The place call's assets= array for <see cref="PlaceSet"/>.</summary>
    internal static string PlaceArray(IReadOnlyList<string> paths)
        => JsonSerializer.Serialize(paths.Select(p => new Dictionary<string, string> { ["path"] = p }));

    /// <summary>The columns of the walk's spilled file, one row per node.</summary>
    static readonly string[] RowSchema = { "path", "edge", "pulled_by", "standing", "provider", "provider_kind", "keyed", "note", "error" };

    /// <summary>Write every node to <paramref name="target"/>, one row each; the spill to render, or why it failed.</summary>
    internal static (SpillInfo? Spill, string? Error) Write(AssetWalkData d, ArtifactTarget target, OrderStamp? order, string? noEpochBecause)
    {
        using var writer = new ResultArtifact.Writer();
        foreach (var n in d.Nodes)
            writer.WriteRow((w, _) => JsonSerializer.Serialize(w, NodeDoc(n)));
        var notes = new List<string>
        {
            "One row per node in walk order. 'standing' from_source rows are the place set: pass their paths as "
            + $"{ToolNames.Place} assets= with source_provider set to the walk's source. A keyed row sits at a FormID-named path.",
        };
        notes.AddRange(d.Notes.Select(n => "walk note: " + n));
        if (noEpochBecause is not null)
            notes.Add("'epoch' is EMPTY: the load order could not be read for a fingerprint when this was written — " + noEpochBecause);
        var query = new List<KeyValuePair<string, string>>
        {
            new("walk", "true"),
            new("source_provider", d.SourceProvider ?? "<none>"),
            new("record_sources", d.RecordSources.Count > 0 ? string.Join(" > ", d.RecordSources) : "<none>"),
        };
        var (manifest, err) = writer.Save(target, ToolNames.AssetStatus, query, identity: "path", RowSchema,
                                          sort: "walk order", total: d.Nodes.Count, epoch: order?.Epoch ?? "", notes: notes,
                                          excludedPlugins: order?.ExcludedPlugins);
        return err is not null ? (null, err) : (new SpillInfo(target.Path, manifest!, "ceiling") { EpochUnavailable = noEpochBecause }, null);
    }

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

    static string Text(AssetWalkData d, IReadOnlyList<string>? selectorNotes, int cap, SpillState? spill, out bool truncated)
    {
        var sb = new StringBuilder("asset walk — profile '").Append(d.ProfileName.Length > 0 ? d.ProfileName : "(unconfigured)")
            .Append("'  (").Append(d.Nodes.Count).Append(" node").Append(d.Nodes.Count == 1 ? "" : "s")
            .Append(d.SourceProvider is { } s ? $", source \"{s}\"" : "")
            .Append(d.RecordSources.Count > 0 ? ", records from " + string.Join(" > ", d.RecordSources) : "").Append(")\n");
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
        var keyedNote = d.Nodes.Any(n => n.Standing == WalkStanding.FromSource && n.Keyed)
            ? "  keyed nodes keep their FormID-named path: place them as they are only for a copy that keeps the FormKeys; a renumbering copy carries them itself.\n"
            : "";
        var tail = place.Count == 0 ? ""
            : $"\nplace set ({place.Count}): {ToolNames.Place} source_provider=\"{d.SourceProvider}\" assets={PlaceArray(place)}\n" + keyedNote;
        var spillText = Wire.SpillText(spill);
        // A spilled walk whose array would not fit points at the file's from_source rows instead; a failed spill has no file.
        if (spill is not null && place.Count > 0 && cap > 0 && sb.Length + tail.Length + spillText.Length + 120 > cap)
            tail = spill.Spill is not null
                ? $"\nplace set ({place.Count}): the spilled file's rows with standing from_source, for {ToolNames.Place} source_provider=\"{d.SourceProvider}\".\n" + keyedNote
                : $"\nplace set ({place.Count}): not shown — its array does not fit max_chars and no results file was written; raise max_chars.\n";
        int shown = 0;
        foreach (var n in d.Nodes)
        {
            var block = new StringBuilder("\n").Append(n.Path).Append('\n')
                .Append("  ").Append(n.Edge).Append(" of ").Append(n.PulledBy).Append(n.Keyed ? "   [keyed]" : "").Append('\n')
                .Append("  ").Append(Standing(n, d.SourceProvider)).Append('\n');
            if (n.Note is { } note) block.Append("  note: ").Append(note).Append('\n');
            if (cap > 0 && sb.Length + block.Length + tail.Length + spillText.Length + 120 > cap) break;
            sb.Append(block);
            shown++;
        }
        truncated = shown < d.Nodes.Count;
        if (truncated)
            sb.Append($"\n[!] {d.Nodes.Count - shown} of {d.Nodes.Count} node(s) not shown: max_chars was reached.")
              .Append(spill?.Spill is null ? " Raise max_chars, or narrow the seeds.\n" : " Every node is in the file below.\n");
        sb.Append(tail).Append(spillText);
        return RenderCap.Settle(sb.ToString().TrimEnd('\n'), cap);
    }

    static Dictionary<string, object?> NodeDoc(AssetWalkNode n) => new()
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
    };

    static string Json(AssetWalkData d, IReadOnlyList<string>? selectorNotes, int cap, SpillState? spill, out bool truncated)
    {
        var nodes = d.Nodes.Select(n => JsonSerializer.Serialize(NodeDoc(n))).ToList();
        var doc = new Dictionary<string, object?>
        {
            ["tool"] = ToolNames.AssetStatus,
            ["walk"] = true,
            ["profile"] = d.ProfileName,
            ["source_provider"] = d.SourceProvider,
            ["record_sources"] = d.RecordSources.Count > 0 ? d.RecordSources : null,
            ["bsa_failures"] = d.BsaFailures,
            ["root_failures"] = d.RootFailures,
            ["warnings"] = d.Warnings,
            ["selector_notes"] = selectorNotes ?? Array.Empty<string>(),
            ["notes"] = d.Notes,
            ["truncated"] = d.Truncated,
            ["nodes"] = Array.Empty<object>(),
            ["place_set"] = PlaceSet(d).Select(p => new Dictionary<string, string> { ["path"] = p }).ToList(),
        };
        if (spill is not null)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms)) { w.WriteStartObject(); Artifacts.WriteSpillStateJson(w, spill); w.WriteEndObject(); }
            foreach (var p in JsonDocument.Parse(ms.ToArray()).RootElement.EnumerateObject()) doc[p.Name] = p.Value.Clone();
            doc["nodes_shown"] = 0;
            // A spilled walk whose set would not fit leaves it to the file's from_source rows; a failed spill has no file.
            if (cap > 0 && JsonSerializer.Serialize(doc).Length > cap)
            {
                doc["place_set"] = null;
                if (spill.Spill is not null) doc["place_set_in_file"] = true;
            }
        }
        // The document with no nodes, then each node while the whole still fits.
        int size = JsonSerializer.Serialize(doc).Length + 16, shown = 0;
        foreach (var n in nodes)
        {
            if (cap > 0 && size + n.Length + 1 > cap) break;
            size += n.Length + 1;
            shown++;
        }
        truncated = shown < nodes.Count;
        doc["nodes"] = nodes.Take(shown).Select(n => JsonDocument.Parse(n).RootElement.Clone()).ToList();
        if (spill is not null) doc["nodes_shown"] = shown;
        return JsonSerializer.Serialize(doc);
    }
}

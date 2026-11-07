using System.Text;
using System.Text.Json;

namespace HousecarlMcp;

/// <summary>Renders <see cref="AssetWalkData"/>: the build alarms and the walk's notes first, then one block per node
/// in walk order (through <c>limit=</c>/<c>offset=</c>'s window), then the from-source set as the assets= array
/// <c>housecarl_place</c> takes. A walk past max_chars is written whole to a results-directory file (SPEC §2.1.1), which
/// the response names; <c>to_file=</c> writes it there and renders the manifest only; <c>counts_only=</c> renders counts.</summary>
static class AssetWalkWire
{
    /// <summary>What a paged walk's accounting counts.</summary>
    const string NodeNoun = "node(s)";

    /// <summary><paramref name="spill"/> is this call's artifact disposition; <paramref name="window"/> narrows the node
    /// list only, never the place set, counts, notes or spill; <paramref name="truncated"/> is what the caller spills on.</summary>
    public static string Render(AssetWalkData d, IReadOnlyList<string>? selectorNotes, int cap, bool json, SpillState? spill,
                                RowWindow window, out bool truncated)
    {
        // The nodes this answer lists: the window's, or none on a to_file= answer, whose nodes are the file.
        var listed = spill?.ManifestOnly == true ? Array.Empty<AssetWalkNode>() : window.Apply(d.Nodes);
        int notes = (selectorNotes?.Count ?? 0) + d.Notes.Count;
        return json ? Json(d, selectorNotes, cap, spill, window, listed, notes, out truncated)
                    : Text(d, selectorNotes, cap, spill, window, listed, notes, out truncated);
    }

    /// <summary>The nodes a place call takes: the ones the named source ships, error nodes and duplicates out.</summary>
    internal static IReadOnlyList<string> PlaceSet(AssetWalkData d)
        => d.Nodes.Where(n => n.Standing == WalkStanding.FromSource).Select(n => n.Path).ToList();

    /// <summary>The place call's assets= array for <see cref="PlaceSet"/>.</summary>
    internal static string PlaceArray(IReadOnlyList<string> paths)
        => JsonSerializer.Serialize(paths.Select(p => new Dictionary<string, string> { ["path"] = p }));

    /// <summary>The columns of the walk's spilled file, one row per node.</summary>
    static readonly string[] RowSchema = { "path", "edge", "pulled_by", "standing", "provider", "provider_kind", "keyed", "note", "error" };

    /// <summary>Write every node to <paramref name="target"/>, one row each, <paramref name="reason"/> naming why
    /// (<c>to_file</c> or <c>ceiling</c>), <paramref name="seeds"/> the seed parameters the call gave; the spill to render, or why it failed.</summary>
    internal static (SpillInfo? Spill, string? Error) Write(AssetWalkData d, ArtifactTarget target, string reason, OrderStamp? order, string? noEpochBecause,
                                                            IReadOnlyList<KeyValuePair<string, string>> seeds)
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
        var query = new List<KeyValuePair<string, string>> { new("walk", "true") };
        query.AddRange(seeds);
        query.Add(new("source_provider", d.SourceProvider ?? "<none>"));
        query.Add(new("record_sources", d.RecordSources.Count > 0 ? string.Join(" > ", d.RecordSources) : "<none>"));
        // A hidden node adds its hidden member, so the schema names it only then and a plain walk's artifact is unchanged.
        var schema = d.Nodes.Any(n => n.Hidden is not null) ? RowSchema.Append("hidden").ToArray() : RowSchema;
        var (manifest, err) = writer.Save(target, ToolNames.AssetStatus, query, identity: "path", schema,
                                          sort: "walk order", total: d.Nodes.Count, epoch: order?.Epoch ?? "", notes: notes,
                                          excludedPlugins: order?.ExcludedPlugins);
        return err is not null ? (null, err) : (new SpillInfo(target.Path, manifest!, reason) { EpochUnavailable = noEpochBecause }, null);
    }

    static string StandingToken(WalkStanding s) => s switch
    {
        WalkStanding.FromSource => "from_source", WalkStanding.Elsewhere => "elsewhere",
        WalkStanding.Present => "present", WalkStanding.Absent => "absent", _ => "error",
    };

    static string Standing(AssetWalkNode n, string? source) => n.Standing switch
    {
        WalkStanding.FromSource => $"FROM SOURCE: {Copy(n.Copy!)}",
        WalkStanding.Elsewhere => $"ELSEWHERE: {Copy(n.Copy!)} wins it"
                                  + (source is null ? "" : n.Hidden is { } h ? $"; \"{source}\" holds it, {h.Unnamed()}" : $"; \"{source}\" does not ship it"),
        WalkStanding.Present => $"WINS: {Copy(n.Copy!)}",
        WalkStanding.Absent => "ABSENT — " + AbsentWords.Why(n.Hidden)
                               + (source is null || n.Hidden?.HeldBy(source) == true ? "" : $", and \"{source}\" does not ship it"),
        _ => "error: " + n.Error,
    };

    static string Copy(PlacementSource s)
        => AssetSourceSelection.Describe(s)
         + (!s.OffOrder ? ""
            : s is { OwnerEnabled: true, Kind: AssetKind.Loose } ? " (enabled, but its folder did not read this build)"
            : " (not loaded by the game)");

    /// <summary>The header, alarms, notes and standing counts every walk answer opens with, over the whole walk.</summary>
    static StringBuilder Lead(AssetWalkData d, IReadOnlyList<string>? selectorNotes, RenderCap room)
    {
        var sb = new StringBuilder("asset walk — profile '").Append(d.ProfileName.Length > 0 ? d.ProfileName : "(unconfigured)")
            .Append("'  (").Append(d.Nodes.Count).Append(" node").Append(d.Nodes.Count == 1 ? "" : "s")
            .Append(d.SourceProvider is { } s ? $", source \"{s}\"" : "")
            .Append(d.RecordSources.Count > 0 ? ", records from " + string.Join(" > ", d.RecordSources) : "").Append(")\n");
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
        return sb;
    }

    /// <summary>The text walk; only a paged one (Aaron, DECISIONS 2026-11-06 (25)) ends with the accounting line.</summary>
    static string Text(AssetWalkData d, IReadOnlyList<string>? selectorNotes, int cap, SpillState? spill, RowWindow window,
                       IReadOnlyList<AssetWalkNode> listed, int notes, out bool truncated)
    {
        var sb = Lead(d, selectorNotes, RenderCap.For(cap, 0));
        bool paged = window.IsPaged;
        int acctReserve = paged ? TransportAccounting.Reserve(d.Nodes.Count, listed.Count, window, notes, NodeNoun) : 0;

        var place = PlaceSet(d);
        var keyedNote = d.Nodes.Any(n => n.Standing == WalkStanding.FromSource && n.Keyed)
            ? "  keyed nodes keep their FormID-named path: place them as they are only for a copy that keeps the FormKeys; a renumbering copy carries them itself.\n"
            : "";
        var tail = place.Count == 0 ? ""
            : $"\nplace set ({place.Count}): {ToolNames.Place} source_provider=\"{d.SourceProvider}\" assets={PlaceArray(place)}\n" + keyedNote;
        var spillText = Wire.SpillText(spill);
        // What the answer writes after the nodes, plus slack for the cut sentence.
        int Owed() => tail.Length + spillText.Length + acctReserve + 120;
        bool tailOver = place.Count > 0 && cap > 0 && sb.Length + Owed() > cap;
        // A spilled walk whose array would not fit points at the file's from_source rows instead; a failed spill has no file.
        if (spill is not null && tailOver)
            tail = spill.Spill is not null
                ? $"\nplace set ({place.Count}): the spilled file's rows with standing from_source, for {ToolNames.Place} source_provider=\"{d.SourceProvider}\".\n" + keyedNote
                : $"\nplace set ({place.Count}): not shown — its array does not fit max_chars and no results file was written; raise max_chars.\n";
        int shown = 0, owed = Owed();
        foreach (var n in listed)
        {
            var block = new StringBuilder("\n").Append(n.Path).Append('\n')
                .Append("  ").Append(n.Edge).Append(" of ").Append(n.PulledBy).Append(n.Keyed ? "   [keyed]" : "").Append('\n')
                .Append("  ").Append(Standing(n, d.SourceProvider)).Append('\n');
            if (n.Note is { } note) block.Append("  note: ").Append(note).Append('\n');
            if (cap > 0 && sb.Length + block.Length + owed > cap) break;
            sb.Append(block);
            shown++;
        }
        // Counted against the window, so a window that fits is not a cut and spills nothing; a page listing no node
        // whose place set does not fit spills too, as a page whose nodes do not fit does.
        truncated = shown < listed.Count || (spill is null && tailOver);
        if (shown < listed.Count)
            sb.Append($"\n[!] {listed.Count - shown} of {listed.Count} node(s) not shown: max_chars was reached.")
              .Append(spill?.Spill is null ? " Raise max_chars, or narrow the seeds.\n" : " Every node is in the file below.\n");
        sb.Append(tail);
        if (paged)
            sb.Append(TransportAccounting.Compose(TransportAccounting.Tally(d.Nodes.Count, listed.Count, shown, window, notes),
                                                  NodeNoun, everySentence: false)).Append('\n');
        sb.Append(spillText);
        return RenderCap.Settle(sb.ToString().TrimEnd('\n'), cap);
    }

    static Dictionary<string, object?> NodeDoc(AssetWalkNode n)
    {
        var doc = new Dictionary<string, object?>
        {
            ["path"] = n.Path,
            ["edge"] = n.Edge,
            ["pulled_by"] = n.PulledBy,
            ["standing"] = StandingToken(n.Standing),
            ["provider"] = n.Copy?.ProviderName,
            ["provider_kind"] = n.Copy?.Kind == AssetKind.Bsa ? "BSA" : n.Copy is null ? null : "loose",
            ["keyed"] = n.Keyed,
            ["note"] = n.Note,
            ["error"] = n.Error,
        };
        // Only on a hidden node, so any other node is the document it always was.
        if (n.Hidden is { } h) doc["hidden"] = AbsentWords.Doc(h);
        return doc;
    }

    static Dictionary<string, object?> Head(AssetWalkData d, IReadOnlyList<string>? selectorNotes) => new()
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
    };

    static string Json(AssetWalkData d, IReadOnlyList<string>? selectorNotes, int cap, SpillState? spill, RowWindow window,
                       IReadOnlyList<AssetWalkNode> listed, int notes, out bool truncated)
    {
        var nodes = listed.Select(n => JsonSerializer.Serialize(NodeDoc(n))).ToList();
        var doc = Head(d, selectorNotes);
        doc["nodes"] = null;
        doc["place_set"] = PlaceSet(d).Select(p => new Dictionary<string, string> { ["path"] = p }).ToList();
        // A paged walk carries the row lane's accounting; at defaults the document is the one it always was.
        TransportCounts? widest = window.IsPaged ? TransportAccounting.Widest(d.Nodes.Count, listed.Count, window, notes) : null;
        if (widest is not null) doc[AccountingMembers] = null;
        if (spill is not null)
        {
            doc[SpillMembers] = null;
            doc["nodes_shown"] = 0;
            // A spilled walk whose set would not fit leaves it to the file's from_source rows; a failed spill has no file.
            if (cap > 0 && Emit(doc, nodes, 0, spill, widest).Length > cap)
            {
                doc["place_set"] = null;
                if (spill.Spill is not null) doc["place_set_in_file"] = true;
            }
        }
        // The document with no nodes, then each node while the whole still fits.
        int bare = Emit(doc, nodes, 0, spill, widest).Length, size = bare + 16, shown = 0;
        foreach (var n in nodes)
        {
            if (cap > 0 && size + n.Length + 1 > cap) break;
            size += n.Length + 1;
            shown++;
        }
        // As the text walk: a page listing no node whose place set does not fit spills too.
        truncated = shown < nodes.Count || (spill is null && cap > 0 && bare > cap && doc["place_set"] is List<Dictionary<string, string>> { Count: > 0 });
        if (spill is not null) doc["nodes_shown"] = shown;
        TransportCounts? acct = widest is null ? null
            : TransportAccounting.Tally(d.Nodes.Count, listed.Count, shown, window, notes);
        return Emit(doc, nodes, shown, spill, acct);
    }

    /// <summary>The key in a walk document where the spill state's members are written.</summary>
    const string SpillMembers = "\0spill";

    /// <summary>The key in a paged walk document where the accounting and its advice are written.</summary>
    const string AccountingMembers = "\0accounting";

    /// <summary>The walk document in key order, its first <paramref name="shown"/> nodes written as serialized.</summary>
    static string Emit(Dictionary<string, object?> doc, IReadOnlyList<string> nodes, int shown, SpillState? spill, TransportCounts? acct)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var (k, v) in doc)
            {
                if (k == SpillMembers) { Artifacts.WriteSpillStateJson(w, spill!); continue; }
                if (k == AccountingMembers)
                {
                    TransportAccounting.WriteJson(w, acct!.Value);
                    TransportAccounting.WritePageAdviceJson(w, acct.Value, NodeNoun);
                    continue;
                }
                w.WritePropertyName(k);
                if (k != "nodes") { JsonSerializer.Serialize(w, v); continue; }
                w.WriteStartArray();
                foreach (var n in nodes.Take(shown)) w.WriteRawValue(n, skipInputValidation: true);
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // ---- counts_only= -----------------------------------------------------------------------------

    const string EdgeTitle = "nodes by edge";
    const string StandingTitle = "nodes by standing";

    /// <summary>The two axes: a group-by over every node's edge and standing.</summary>
    static HistogramAxis[] Axes(AssetWalkData d)
    {
        static IReadOnlyList<SweepCount> By(IEnumerable<string> keys)
            => SweepFindings.Histogram(keys.GroupBy(k => k, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase));
        return
        [
            new(SweepSubject.AssetWalkEdgeRows, By(d.Nodes.Select(n => n.Edge)), EdgeTitle),
            new(SweepSubject.AssetWalkStandingRows, By(d.Nodes.Select(n => StandingToken(n.Standing))), StandingTitle),
        ];
    }

    /// <summary>The walk as counts: the whole walk ran; the header, alarms, notes and counts per edge and standing,
    /// no nodes, no spill, the place set as its count. <paramref name="limit"/> caps each table's rows.</summary>
    internal static string RenderCounts(AssetWalkData d, IReadOnlyList<string>? selectorNotes, int cap, bool json, int limit)
    {
        var axes = Axes(d);
        int place = PlaceSet(d).Count;
        int rowLimit = AssetCensus.RowLimit(limit);
        if (json)
        {
            // At a cap it fits, the document it always was; past it, the bounded one.
            var whole = CountsJson(d, selectorNotes, place, rowLimit, axes, cap: 0);
            return cap <= 0 || whole.Length <= cap ? whole : CountsJson(d, selectorNotes, place, rowLimit, axes, cap);
        }
        var sb = Lead(d, selectorNotes, RenderCap.For(cap, axes.Sum(a => a.TextFixed)));
        if (place > 0) sb.Append("place set: ").Append(place).Append(" node(s) from source \"").Append(d.SourceProvider).Append("\"; drop counts_only= to list them.\n");
        var body = new BoundedBody(acct: null, budget: cap, () => sb.Length);
        CheckTextRender.AppendHistogramAxes(sb, body, rowLimit, axes);
        return RenderCap.Settle(sb.ToString().TrimEnd('\n'), cap);
    }

    /// <summary>The head members a bounded counts document cuts, each with its <c>_omitted</c> count.</summary>
    static readonly HashSet<string> CappedLists = new() { "bsa_failures", "root_failures", "warnings", "selector_notes", "notes" };

    /// <summary>The json counts document; with <paramref name="cap"/> &gt; 0, bounded as the row census is
    /// (<see cref="JsonWire.RenderAssetCensus"/>): the lists and the axes' rows say what they cut, an overrun is said.</summary>
    static string CountsJson(AssetWalkData d, IReadOnlyList<string>? selectorNotes, int place, int rowLimit, HistogramAxis[] axes, int cap)
    {
        // The lists leave room for what follows them: the counters, each axis's frame, and their own omitted counts.
        int depth = new JsonWire.JsonUnitDepths(1).AxisFrame;
        int listBudget = Math.Max(cap - 128 - CappedLists.Count * 32 - axes.Sum(a => JsonWire.HistogramFrameCostFor(a, depth)), 1);
        using var ms = new CharCountedStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var (k, v) in Head(d, selectorNotes))
            {
                if (cap > 0 && CappedLists.Contains(k) && v is IReadOnlyList<string> items) { JsonWire.WriteCappedStringArray(w, ms, k, items, listBudget); continue; }
                w.WritePropertyName(k);
                JsonSerializer.Serialize(w, v);
            }
            w.WriteBoolean("counts_only", true);
            w.WriteNumber("nodes", d.Nodes.Count);
            w.WriteNumber("keyed", d.Nodes.Count(n => n.Keyed));
            w.WriteNumber("place_set_count", place);
            var body = cap > 0 ? new BoundedBody(acct: null, budget: cap - 2, () => { w.Flush(); return JsonWire.Chars(ms); }) : null;
            JsonWire.WriteHistogramAxes(w, body, rowLimit, axes);
            if (cap > 0) JsonWire.WriteCapOverrun(w, ms, cap);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}

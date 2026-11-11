using System.Text;
using System.Text.Json;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>nif_inspect over the shared selection: the meshes <see cref="AssetLayers.SelectMeshes"/> names (Data paths
/// read loose or out of their archive, files on disk read as themselves), one block per mesh, then the uniform
/// transport: a window, a census, json rows, the [accounting] line, an over-ceiling answer spilled whole, and
/// <c>to_file=</c>; contracts in docs/architecture/nif.md.</summary>
internal static class NifBatch
{
    /// <summary>The call's inputs, past the tool's own refusals.</summary>
    internal sealed record Call(IReadOnlyList<(string Path, bool OnDisk)> Entries, IReadOnlyList<string> Under, string? Provider,
                                IReadOnlyList<FaceGenSeed> Seeds, string? SourceProvider, HashSet<string> Want,
                                IReadOnlyList<string> Unknown, bool NeedView, ListRead PathsIn, ListRead UnderIn, ListRead IdsIn,
                                RowWindow Window, bool CountsOnly, bool Json, int Cap, string? ToFile);

    const string Noun = "mesh(es)";

    /// <summary>The columns every row carries, in order, in the manifest and on each format=json row.</summary>
    internal static readonly string[] RowSchema =
    {
        "path", "on_disk", "disk_note", "formid", "face_note", "status", "read_from", "providers", "ambiguous", "absent",
        "version", "user_version", "stream_version", "skyrim_se", "block_count", "blocks", "unknown_blocks",
        "unknown_block_types", "shapes", "node_count", "sections", "error",
    };

    /// <summary>The call's view and the policy each row is read under.</summary>
    sealed record Ctx(Call C, MeshSelectionData Sel, string Provider, string Source, string Profile, string? ModsRoot)
    {
        internal NifInspectData Read(MeshRow r) =>
            AssetLayers.InspectMesh(Sel.Captured?.View, ModsRoot, r, Source.Length > 0 ? Source : null, C.Want.Contains("paths"));

        /// <summary>A mesh the provider= walk matched whose winner, not <see cref="Provider"/>'s copy, was chosen.</summary>
        internal bool WonElsewhere(MeshRow r, NifInspectData d) =>
            r.Matched && Provider.Length > 0 && Source.Length == 0 && d.Inspected is { } p
            && !AssetSourceSelection.NameMatches(p.Name, p.OwningMod, Provider);

        internal NifInspectBatchData Batch(IReadOnlyList<NifInspectData> results) =>
            new(results, Sel.Captured?.View.BsaFailures ?? Array.Empty<string>(), Sel.Captured?.View.RootFailures ?? Array.Empty<string>(),
                Sel.Captured?.Warnings ?? Array.Empty<string>(), Profile);
    }

    internal static ToolAnswer Run(LoadOrderService svc, Call c)
    {
        string provider = c.Provider?.Trim() ?? "", source = c.SourceProvider?.Trim() ?? "";
        // With formids=, a source_provider= mod folder's own plugins are read after the active order, so a donor's NPC names its head.
        var sel = svc.SelectMeshes(c.Entries, c.Under, provider.Length > 0 ? provider : null, c.Seeds,
                                   c.Seeds.Count > 0 ? svc.ProviderFaceSources(source) : null, c.NeedView);
        if (sel.Refusal is { } refused) return ToolAnswer.Refusal(c.Json, refused);
        // A file on disk in the mods tree names its load-order address off the roots, with or without a captured view.
        var modsRoot = sel.Captured?.ModsRootOrNull
                       ?? (c.Entries.Any(e => e.OnDisk) && svc.ConfigPromptOrNull() is null ? svc.ModsRootOrNull : null);
        var ctx = new Ctx(c, sel, provider, source, sel.Captured?.ProfileName ?? svc.ProfileName, modsRoot);

        var notes = new List<string>(sel.Notes);
        if (sel.NotMeshes > 0)
            notes.Add($"{sel.NotMeshes} file(s) the selectors matched are not .nif meshes and were left out; " +
                      "under=[\"meshes/**/*.nif\"] selects only meshes.");
        if (sel.Tints > 0)
            notes.Add($"{sel.Tints} FaceGen tint(s) the formids= NPCs name are textures, not meshes, and were not read; " +
                      ToolNames.AssetStatus + " formids= reads them.");
        if (source.Length > 0 && c.Entries.Any(e => e.OnDisk))
            notes.Add("source_provider= picks whose copy of a Data path is read; a file on disk is read as itself.");

        if (c.CountsOnly) return Census(ctx, notes);
        if (c.ToFile is { } toFile) return ToFile(svc, ctx, notes, toFile);

        var window = c.Window.Apply(sel.Rows);
        var results = window.Select(ctx.Read).ToList();
        AddWonElsewhere(ctx, notes, results.Where((d, i) => ctx.WonElsewhere(window[i], d)).Count());
        return ToolAnswer.Served(c.Json ? InlineJson(svc, ctx, window, results, notes) : InlineText(svc, ctx, window, results, notes));
    }

    /// <summary>The count of meshes another provider wins, with the knob that reads the provider's own copies.</summary>
    static void AddWonElsewhere(Ctx ctx, List<string> notes, int wonElsewhere)
    {
        if (wonElsewhere > 0)
            notes.Add($"{wonElsewhere} mesh(es) provider '{ctx.Provider}' ships are won by another provider, and the winning " +
                      $"copy was read; source_provider=\"{ctx.Provider}\" reads its own copies.");
    }

    static string HeaderTail(Ctx ctx, int shown, int total)
    {
        var sb = new StringBuilder();
        if (ctx.C.Window.IsPaged || shown != total) sb.Append(" in this window of ").Append(total).Append(" selected");
        if (ctx.Provider.Length > 0) sb.Append(", provider \"").Append(ctx.Provider).Append('"');
        return sb.ToString();
    }

    static string NotesText(IReadOnlyList<string> notes)
    {
        var sb = new StringBuilder();
        foreach (var n in notes) sb.Append("\nnote: ").Append(n);
        return sb.ToString();
    }

    // ---- inline: one block per mesh, the [accounting] line, the spill past the ceiling ----

    static string InlineText(LoadOrderService svc, Ctx ctx, IReadOnlyList<MeshRow> window, List<NifInspectData> results, List<string> notes)
    {
        var c = ctx.C;
        int total = ctx.Sel.Rows.Count;
        var batch = ctx.Batch(results);
        var tail = NotesText(notes);
        var headerTail = HeaderTail(ctx, results.Count, total);
        int reserve = tail.Length + TransportAccounting.Reserve(total, results.Count, c.Window, notes.Count, Noun);
        var body = NifWire.Render(batch, c.Want, c.Unknown, c.Cap, out int shown, reserve, headerTail);
        // Nothing cut when the capped render is the whole one; anything cut is spilled whole, never lost (SPEC §2.1.1).
        var spill = "";
        if (body != NifWire.Render(batch, c.Want, c.Unknown, int.MaxValue / 2, out _, 0, headerTail))
        {
            spill = Wire.SpillText(Spill(svc, ctx, window, results, notes)).TrimEnd('\n');
            body = NifWire.Render(batch, c.Want, c.Unknown, c.Cap, out shown, reserve + spill.Length, headerTail);
        }
        var counts = TransportAccounting.Tally(total, results.Count, shown, c.Window, notes.Count);
        return body + tail + spill + TransportAccounting.Compose(counts, Noun, everySentence: false);
    }

    static string InlineJson(LoadOrderService svc, Ctx ctx, IReadOnlyList<MeshRow> window, List<NifInspectData> results, List<string> notes)
    {
        var c = ctx.C;
        int total = ctx.Sel.Rows.Count;
        // The rows that fit the ceiling, judged on each row's own json; the document closes with the accounting either way.
        var rowsJson = new List<string>();
        int used = 0, room = Math.Max(c.Cap - 4096 - notes.Sum(n => n.Length + 8), 0);
        for (int i = 0; i < results.Count; i++)
        {
            var one = RowJson(window[i], results[i], c.Want);
            if (used + one.Length > room) break;
            rowsJson.Add(one);
            used += one.Length;
        }
        SpillState? spill = rowsJson.Count < results.Count ? Spill(svc, ctx, window, results, notes) : null;
        var counts = TransportAccounting.Tally(total, results.Count, rowsJson.Count, c.Window, notes.Count);
        return Document(ctx, notes, w =>
        {
            w.WritePropertyName("rows");
            w.WriteStartArray();
            foreach (var r in rowsJson) w.WriteRawValue(r, skipInputValidation: true);
            w.WriteEndArray();
        }, w =>
        {
            TransportAccounting.WriteJson(w, counts);
            TransportAccounting.WritePageAdviceJson(w, counts, Noun);
            if (spill is not null) Artifacts.WriteSpillStateJson(w, spill);
        });
    }

    /// <summary>The window, written whole to the results directory: the spill a cut answer names.</summary>
    static SpillState Spill(LoadOrderService svc, Ctx ctx, IReadOnlyList<MeshRow> window, List<NifInspectData> results, List<string> notes)
    {
        var (order, noEpoch) = AssetTools.TryStamp(svc);
        using var target = ResultsStore.Reserve(svc.ResultsDir, ToolNames.NifInspect, order?.Epoch ?? "none");
        using var writer = new ResultArtifact.Writer();
        for (int i = 0; i < results.Count; i++) { var (row, d) = (window[i], results[i]); writer.WriteRow((w, _) => Row(w, row, d, ctx.C.Want)); }
        var (spill, err) = Save(writer, target, "ceiling", ctx, notes, ctx.Sel.Rows.Count, order, noEpoch);
        return err is null ? SpillState.Spilled(spill!, manifestOnly: false) : SpillState.WriteFailed(err);
    }

    // ---- to_file=: every mesh to the named file, the manifest inline ----

    static ToolAnswer ToFile(LoadOrderService svc, Ctx ctx, List<string> notes, string toFile)
    {
        var (order, noEpoch) = AssetTools.TryStamp(svc);
        using var writer = new ResultArtifact.Writer();
        int wonElsewhere = 0;
        // Each mesh is read, written and dropped, so a whole-order selection holds one parsed mesh at a time.
        foreach (var row in ctx.Sel.Rows)
        {
            var d = ctx.Read(row);
            if (ctx.WonElsewhere(row, d)) wonElsewhere++;
            writer.WriteRow((w, _) => Row(w, row, d, ctx.C.Want));
        }
        AddWonElsewhere(ctx, notes, wonElsewhere);
        var (spill, err) = Save(writer, ArtifactTarget.Named(toFile), "to_file", ctx, notes, ctx.Sel.Rows.Count, order, noEpoch);
        if (err is not null) return ToolAnswer.Refusal(ctx.C.Json, "error: " + err);
        if (ctx.C.Json) return ToolAnswer.Served(Document(ctx, notes, null, w => Artifacts.WriteSpillJson(w, spill!)));
        var sb = new StringBuilder(Head(ctx, ctx.Sel.Rows.Count, " selected"));
        AppendAlarms(sb, ctx);
        sb.Append(NotesText(notes)).Append('\n');
        Artifacts.AppendSpillText(sb, spill!);
        return ToolAnswer.Served(RenderCap.Settle(sb.ToString().TrimEnd('\n'), ctx.C.Cap));
    }

    static (SpillInfo? Spill, string? Error) Save(ResultArtifact.Writer writer, ArtifactTarget target, string reason, Ctx ctx,
                                                 IReadOnlyList<string> notes, int total, OrderStamp? order, string? noEpoch)
    {
        var c = ctx.C;
        var query = new List<KeyValuePair<string, string>>
        {
            new("paths", c.PathsIn.EchoSource ?? $"{c.Entries.Count} inline path(s)"),
            new("under", c.UnderIn.EchoSource ?? (c.Under.Count > 0 ? string.Join(",", c.Under) : "<none>")),
            new("formids", c.IdsIn.EchoSource ?? $"{c.Seeds.Count} inline formid(s)"),
            new("provider", ctx.Provider.Length > 0 ? ctx.Provider : "<none>"),
            new("source_provider", ctx.Source.Length > 0 ? ctx.Source : "<winner>"),
            new("sections", c.Want.Count > 0 ? string.Join(",", c.Want.Order(StringComparer.Ordinal)) : "<summary>"),
            new("read_incomplete", ctx.Sel.Captured?.View.ReadIncomplete == true ? "true" : "false"),
        };
        if (writer.RowCount != total)
            query.Add(new("window", $"window: rows {c.Window.Offset + 1}–{c.Window.Offset + writer.RowCount} of {total} (limit={c.Window.Limit}, offset={c.Window.Offset})"));
        var fileNotes = new List<string>
        {
            "One row per mesh, in selection order. 'read_from' is the copy whose bytes were read: the winner, or the " +
            "source_provider= copy; a file on disk has none and 'on_disk' true, and one inside the mods tree names in " +
            "'disk_note' the address that reads that copy through the load order. 'sections' is the requested detail " +
            "sections as the text answer prints them.",
            "An ABSENT row is authoritative only where the response's read-failure and discovery alarms were empty; " +
            "'read_incomplete' in the query echo names whether an archive or root failed to read.",
        };
        fileNotes.AddRange(notes);
        if (noEpoch is not null)
            fileNotes.Add("'epoch' is EMPTY: the load order could not be read for a fingerprint when this was written — " + noEpoch +
                          " The rows are read off the VFS and the mesh bytes, not off the record index.");
        var uncovered = new[]
        {
            "the MO2 VFS layer and the mesh bytes — every copy read, provider chain and mesh value in these rows is read " +
            "off mod folders, archives and files on disk, which the record-build fingerprint does not describe",
        };
        var (manifest, err) = writer.Save(target, ToolNames.NifInspect, query, identity: "path", RowSchema,
                                          sort: "paths= in the order given, then formids= head meshes, then under= / provider= matches sorted",
                                          total: total, epoch: order?.Epoch ?? "", notes: fileNotes,
                                          epochUncovered: uncovered, excludedPlugins: order?.ExcludedPlugins);
        return err is not null ? (null, err) : (new SpillInfo(target.Path, manifest!, reason) { EpochUnavailable = noEpoch }, null);
    }

    // ---- counts_only=: the census over the whole selection ----

    static ToolAnswer Census(Ctx ctx, List<string> notes)
    {
        int read = 0, errors = 0, absent = 0, loose = 0, bsa = 0, disk = 0, se = 0, notSe = 0, unknown = 0, wonElsewhere = 0;
        // Each mesh is read and dropped; the census holds no parsed mesh.
        foreach (var row in ctx.Sel.Rows)
        {
            var d = ctx.Read(row);
            if (ctx.WonElsewhere(row, d)) wonElsewhere++;
            if (d.Inspect is not { } nif) { errors++; if (d.Absent) absent++; continue; }
            read++;
            if (d.OnDisk) disk++; else if (d.Inspected!.SourceKind == AssetKind.Bsa) bsa++; else loose++;
            if (nif.IsSkyrimSE) se++; else notSe++;
            if (nif.HasUnknownBlocks) unknown++;
        }
        AddWonElsewhere(ctx, notes, wonElsewhere);
        int total = ctx.Sel.Rows.Count;
        if (ctx.C.Json)
            return ToolAnswer.Served(Document(ctx, notes, w =>
            {
                w.WriteNumber("total", total);
                w.WriteNumber("read", read);
                w.WriteNumber("not_read", errors);
                w.WriteNumber("absent", absent);
                w.WriteNumber("loose", loose);
                w.WriteNumber("bsa", bsa);
                w.WriteNumber("on_disk", disk);
                w.WriteNumber("skyrim_se", se);
                w.WriteNumber("not_skyrim_se", notSe);
                w.WriteNumber("unknown_blocks", unknown);
            }, null));
        var sb = new StringBuilder(Head(ctx, total, " selected"));
        AppendAlarms(sb, ctx);
        sb.Append("\ncensus: read=").Append(read).Append(" not read=").Append(errors).Append(" (absent=").Append(absent).Append(')')
          .Append("\nread from: loose=").Append(loose).Append(" BSA=").Append(bsa).Append(" file on disk=").Append(disk)
          .Append("\nstreams: Skyrim SE=").Append(se).Append(" other=").Append(notSe).Append("; with unknown blocks=").Append(unknown);
        sb.Append(NotesText(notes));
        return ToolAnswer.Served(RenderCap.Settle(sb.ToString(), ctx.C.Cap));
    }

    // ---- shared pieces ----

    /// <summary>The census and to_file= header: the inline one, counting the whole selection, with the source named.</summary>
    static string Head(Ctx ctx, int count, string what) =>
        NifWire.Header(ctx.Profile, count, what + (ctx.Provider.Length > 0 ? $", provider \"{ctx.Provider}\"" : "")
                                             + (ctx.Source.Length > 0 ? $", source \"{ctx.Source}\"" : ""));

    /// <summary>The build-level alarms an ABSENT depends on, written whatever the budget.</summary>
    static void AppendAlarms(StringBuilder sb, Ctx ctx)
    {
        var room = RenderCap.For(int.MaxValue / 2, 0);
        if (ctx.Sel.Captured is { } cap)
        {
            BatchRender.AppendReadFailures(sb, cap.View.BsaFailures, "a mesh", room);
            BatchRender.AppendRootFailures(sb, cap.View.RootFailures, "a mesh", room);
            BatchRender.AppendDiscoveryWarnings(sb, cap.Warnings, room);
        }
        if (ctx.C.Unknown.Count > 0)
            sb.Append("\n[!] unrecognized section(s) ignored: ").Append(string.Join(", ", ctx.C.Unknown))
              .Append("  (").Append(NifTools.KnownSectionsHint).Append(")\n");
    }

    /// <summary>The json document: the call's echo and alarms, the lane's body, the notes, then the closing members.</summary>
    static string Document(Ctx ctx, IReadOnlyList<string> notes, Action<Utf8JsonWriter>? body, Action<Utf8JsonWriter>? closing)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, JsonWire.Opts))
        {
            w.WriteStartObject();
            w.WriteString("tool", ToolNames.NifInspect);
            w.WriteString("profile", ctx.Profile);
            if (ctx.Provider.Length > 0) w.WriteString("provider", ctx.Provider);
            if (ctx.Source.Length > 0) w.WriteString("source_provider", ctx.Source);
            Strings(w, "read_failures", ctx.Sel.Captured?.View.BsaFailures);
            Strings(w, "root_failures", ctx.Sel.Captured?.View.RootFailures);
            Strings(w, "discovery_warnings", ctx.Sel.Captured?.Warnings);
            if (ctx.C.Unknown.Count > 0) Strings(w, "unknown_sections", ctx.C.Unknown);
            body?.Invoke(w);
            Strings(w, "notes", notes);
            closing?.Invoke(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    static string RowJson(MeshRow row, NifInspectData d, HashSet<string> want)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, JsonTextEncoder.OneLine)) Row(w, row, d, want);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>One mesh's row, every column present so a consumer can index by name, except <c>error</c>, written only
    /// on a mesh that did not read, as the artifact contract marks such a row.</summary>
    static void Row(Utf8JsonWriter w, MeshRow row, NifInspectData d, HashSet<string> want)
    {
        w.WriteStartObject();
        w.WriteString("path", d.RelPath);
        w.WriteBoolean("on_disk", d.OnDisk);
        Str(w, "disk_note", d.DiskNote);
        Str(w, "formid", row.FormId);
        Str(w, "face_note", d.FaceNote);
        w.WriteString("status", d.Inspect is null ? "error" : "ok");
        if (d.Inspected is { } from)
        {
            w.WriteStartObject("read_from");
            w.WriteString("name", from.Name);
            w.WriteString("kind", from.Kind);
            if (from.OffOrder) w.WriteBoolean("off_order", true);
            w.WriteEndObject();
        }
        else w.WriteNull("read_from");
        w.WriteStartArray("providers");
        foreach (var p in d.Providers)
        {
            w.WriteStartObject();
            w.WriteString("name", p.Name);
            w.WriteString("kind", p.Kind);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteBoolean("ambiguous", d.Ambiguous);
        w.WriteBoolean("absent", d.Absent);
        if (d.Inspect is { } nif)
        {
            w.WriteString("version", nif.VersionString);
            w.WriteNumber("user_version", nif.UserVersion);
            w.WriteNumber("stream_version", nif.StreamVersion);
            w.WriteBoolean("skyrim_se", nif.IsSkyrimSE);
            w.WriteNumber("block_count", nif.BlockCount);
            w.WriteStartObject("blocks");
            foreach (var b in nif.BlockTypes) w.WriteNumber(b.Type, b.Count);
            w.WriteEndObject();
            w.WriteBoolean("unknown_blocks", nif.HasUnknownBlocks);
            w.WriteStartArray("unknown_block_types");
            foreach (var u in nif.UnknownBlockTypes) w.WriteStringValue(u);
            w.WriteEndArray();
            w.WriteStartArray("shapes");
            foreach (var s in nif.Shapes) w.WriteStringValue(s.Name);
            w.WriteEndArray();
            w.WriteNumber("node_count", nif.Nodes.Count);
            Str(w, "sections", NifWire.SectionsText(d, want));
        }
        else
        {
            foreach (var col in MeshColumns) w.WriteNull(col);
            w.WriteString("error", d.Error ?? "unknown error");
        }
        w.WriteEndObject();
    }

    /// <summary>The columns a mesh that read fills, null on one that did not: RowSchema from 'version' up to 'error'.</summary>
    static readonly string[] MeshColumns = RowSchema[Array.IndexOf(RowSchema, "version")..^1];

    static void Str(Utf8JsonWriter w, string name, string? v) => JsonWire.WriteNullable(w, name, v);

    static void Strings(Utf8JsonWriter w, string name, IReadOnlyList<string>? values) =>
        JsonWire.WriteStringArray(w, name, values ?? Array.Empty<string>());
}

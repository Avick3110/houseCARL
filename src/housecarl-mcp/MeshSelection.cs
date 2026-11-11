using HousecarlCore;

namespace HousecarlMcp;

/// <summary>One mesh a nif_inspect call reads: its Data path or file on disk, on a formids= row the FormID, and the
/// row's own error or face line.</summary>
internal readonly record struct MeshRow(string Path, bool OnDisk, string? FormId = null, string? Error = null, string? FaceNote = null);

/// <summary>A mesh selection: the call's asset capture (null when only files on disk are read and nothing needs the
/// load order), the rows in order, how many matched files were not meshes, how many formids= tints were left out, the
/// selector notes, and the refusal under which nothing was selected.</summary>
internal sealed record MeshSelectionData(AssetCapture? Captured, IReadOnlyList<MeshRow> Rows, int NotMeshes, int Tints,
                                         IReadOnlyList<string> Notes, string? Refusal = null);

internal sealed partial class AssetLayers
{
    /// <summary>The meshes the shared selection names (<see cref="FileSelection.Select"/>, asset_status's selectors and
    /// refusals), over one captured view: the <paramref name="entries"/> as given (files on disk among them), then each
    /// formids= NPC's head mesh (its tint counted, not listed), then each match that is a <c>.nif</c> (the rest
    /// counted). <paramref name="needView"/> captures the view even when no entry is a Data path.</summary>
    internal MeshSelectionData SelectMeshes(IReadOnlyList<(string Path, bool OnDisk)> entries, IReadOnlyList<string>? under,
                                            string? provider, IReadOnlyList<FaceGenSeed>? seeds, FaceSources? faceSources,
                                            bool needView)
    {
        var dataPaths = entries.Where(e => !e.OnDisk).Select(e => e.Path).ToList();
        bool selecting = dataPaths.Count > 0 || under is { Count: > 0 } || !string.IsNullOrWhiteSpace(provider) || seeds is { Count: > 0 };
        if (!selecting)
        {
            var onlyDisk = entries.Select(e => new MeshRow(e.Path, true)).ToList();
            return new MeshSelectionData(needView ? _host.CaptureAssets() : null, onlyDisk, 0, 0, Array.Empty<string>());
        }
        var capture = CaptureForSelection(seeds, faceSources);
        var chosen = FileSelection.Select(capture, dataPaths, under, provider, MaxAssetPaths);
        MeshSelectionData Refused(string why) => new(capture.Captured, Array.Empty<MeshRow>(), 0, 0, chosen.Notes, why);
        if (chosen.Refusal is { } unknown) return Refused(unknown);
        if (chosen.StoppedAt is { } stoppedAt)
            return Refused($"error: the selection names at least {stoppedAt:N0} paths, past the {MaxAssetPaths:N0}-path bound one " +
                           "call is given, so nothing was read — narrow it with a tighter under= selector " +
                           "(e.g. 'meshes/**/*.nif') or fewer paths= entries.");

        // paths= entries as given, files on disk among them; the selection's first entries are the Data paths, one each, in order.
        var rows = new List<MeshRow>(chosen.Entries.Count + entries.Count);
        int next = 0;
        foreach (var e in entries)
            rows.Add(e.OnDisk ? new MeshRow(e.Path, true) : new MeshRow(chosen.Entries[next++].Path, false));
        int notMeshes = 0, tints = 0;
        foreach (var f in chosen.Entries.Skip(dataPaths.Count))
        {
            if (f.FormId is not null)
            {
                // A tint is the pair's texture half: counted, since asset_status formids= is where it is read.
                if (f.Slot == FaceGenSlot.Tint) { tints++; continue; }
                rows.Add(new MeshRow(f.Path, false, f.FormId, f.Error, f.FaceNote));
                continue;
            }
            if (!f.Path.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)) { notMeshes++; continue; }
            rows.Add(new MeshRow(f.Path, false));
        }
        return new MeshSelectionData(capture.Captured, rows, notMeshes, tints, chosen.Notes);
    }

    /// <summary>One selected mesh read: a file on disk as that file, or a Data path through <paramref name="view"/> to its
    /// winner or <paramref name="sourceProvider"/>'s copy. Every failure is that row's named error, never a throw.</summary>
    internal static NifInspectData InspectMesh(AssetResolver.AssetView? view, string? modsRoot, MeshRow row, string? sourceProvider, bool links)
    {
        var rel = (row.Path ?? "").Trim();
        if (row.Error is { } bad) return NifInspectData.Fail(rel, bad);
        NifInspectData data;
        try { data = row.OnDisk ? InspectFile(view, modsRoot, rel, links) : NifInspectOne(view!.Value, rel, sourceProvider, links); }
        catch (Exception ex) { data = NifInspectData.Fail(rel, $"unexpected error inspecting this path — {ex.GetType().Name}: {ex.Message}"); }
        return row.FaceNote is { } note ? data with { FaceNote = note } : data;
    }

    /// <summary>A mesh file on disk, read as that file. One inside the mods tree names its Data path and mod folder, the
    /// address that reads the same copy through the load order.</summary>
    static NifInspectData InspectFile(AssetResolver.AssetView? view, string? modsRoot, string path, bool links)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return NifInspectData.Fail(path, $"invalid path — {ex.Message}") with { OnDisk = true }; }
        if (!File.Exists(full)) return NifInspectData.Fail(full, $"no such file: '{full}'.") with { OnDisk = true };
        byte[] bytes;
        try { bytes = File.ReadAllBytes(full); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return NifInspectData.Fail(full, $"could not read the file — {ex.Message}") with { OnDisk = true }; }

        string? note = ModsPathAddress.Split(full, modsRoot) is { RelPath: { } inside } hit
            ? $"this file is in the mod folder '{hit.ModFolder}' and was read as the file on disk; " +
              $"paths=[\"{inside}\"] source_provider=\"{hit.ModFolder}\" reads that copy through the load order."
            : null;
        var outcome = NifService.Inspect(bytes, links);
        var data = new NifInspectData(full, null, Array.Empty<NifProvider>(), false, false, outcome.Inspect, outcome.Error)
            { OnDisk = true, DiskNote = note };
        if (!links || outcome.Inspect is null) return data;
        if (view is not { } v) return data with { LinksError = "no load order is read on this call, so the Data paths this mesh names were not resolved" };
        var (read, linksErr) = ResolveLinks(v, (outcome.Links, outcome.LinksError));
        return data with { Links = read, LinksError = linksErr };
    }
}

using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>One created Cell's structural-shell note: its kind (interior vs exterior) and the world content houseCARL
/// does NOT author for it.</summary>
public sealed record CellShell(FormKey Cell, string EditorId, bool Interior, IReadOnlyList<string> MustProvide);

/// <summary>The structural-shell report for one create call: one entry per created Cell.</summary>
public sealed record CellShellReport(IReadOnlyList<CellShell> Cells)
{
    /// <summary>The shell check itself could not run (the patch wouldn't re-open); null on a clean run. The create
    /// already succeeded when this is set.</summary>
    public string? CheckError { get; init; }

    public bool IsEmpty => Cells.Count == 0 && CheckError is null;
    public static readonly CellShellReport Empty = new(Array.Empty<CellShell>());
}

/// <summary>Post-write structural-shell report for created cells: a created cell is a structural shell, and houseCARL
/// does not author world content. The overlay re-open lives in core so the service needs no Mutagen.Skyrim dependency.</summary>
public static class CellShellCheck
{
    /// <summary>The catalog name the create flow stamps on a created cell — the filter for "which created records are
    /// cells".</summary>
    public const string CellCatalogName = "Cell";

    /// <summary>Run the structural-shell report over the cells created by one create call. A whole-check failure is
    /// surfaced on <see cref="CellShellReport.CheckError"/>, never thrown.</summary>
    public static CellShellReport Run(string patchPath, IReadOnlyList<WritePatchBuilder.CreatedRecord> created)
    {
        var cellEdids = new Dictionary<FormKey, string>();
        foreach (var c in created)
            if (string.Equals(c.RecordType, CellCatalogName, StringComparison.Ordinal))
                cellEdids[c.FormKey] = c.EditorId;
        if (cellEdids.Count == 0) return CellShellReport.Empty;

        ISkyrimModGetter? patch = null;
        try
        {
            patch = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(patchPath));
            var shells = new List<CellShell>();
            // EnumerateMajorRecords<ICellGetter> finds cells in BOTH the interior Cells group AND worldspace blocks.
            foreach (var cell in patch.EnumerateMajorRecords<ICellGetter>())
            {
                if (!cellEdids.TryGetValue(cell.FormKey, out var edid)) continue;
                bool interior = cell.Flags.HasFlag(Cell.Flag.IsInteriorCell);
                shells.Add(new CellShell(cell.FormKey, edid, interior, MustProvide(interior)));
            }
            return shells.Count == 0 ? CellShellReport.Empty : new CellShellReport(shells);
        }
        catch (Exception ex)
        {
            return CellShellReport.Empty with { CheckError = $"{ex.GetType().Name}: {ex.Message}" };
        }
        finally { (patch as IDisposable)?.Dispose(); }
    }

    /// <summary>The world content houseCARL does not author for a freshly-created cell — a standing list fixed by
    /// kind, not a field-state check.</summary>
    static IReadOnlyList<string> MustProvide(bool interior) => interior
        ? new[]
        {
            "lighting — a Lighting Template and/or lighting settings (else the cell renders pitch black)",
            "navmesh (else NPCs cannot path or spawn)",
        }
        : new[]
        {
            "terrain — a LAND record (else the cell is an empty void)",
            "water height + a region/location",
            "navmesh (else NPCs cannot path)",
        };
}

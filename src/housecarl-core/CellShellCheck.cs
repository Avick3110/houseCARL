using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>One created Cell's STRUCTURAL-SHELL note: its kind (interior vs exterior) and the world content houseCARL
/// does NOT author for it (lighting / terrain / water / navmesh — Creation-Kit work). The cell RECORD is valid and
/// correctly placed; this says that "created" is not the same as "looks right in game", the same shape as the
/// dialogue voice report's "this line will be SILENT" surface.</summary>
public sealed record CellShell(FormKey Cell, string EditorId, bool Interior, IReadOnlyList<string> MustProvide);

/// <summary>The structural-shell report for one create call: one entry per created Cell. <see cref="IsEmpty"/> when the
/// call created no cells. Mirrors <see cref="VoiceReport"/> — a post-write enrichment that NEVER fails the create
/// (the cell IS written; this only says what the author must still provide).</summary>
public sealed record CellShellReport(IReadOnlyList<CellShell> Cells)
{
    /// <summary>The shell check itself could not run (the patch wouldn't re-open) — surfaced, never a silent skip.
    /// The create ALREADY SUCCEEDED when this is set; it means "I couldn't enumerate the created cells", not "the write
    /// failed". Null on a clean run.</summary>
    public string? CheckError { get; init; }

    public bool IsEmpty => Cells.Count == 0 && CheckError is null;
    public static readonly CellShellReport Empty = new(Array.Empty<CellShell>());
}

/// <summary>Post-write structural-shell report for created cells: a created cell is a structural SHELL; houseCARL does
/// NOT author world content. Sibling to <see cref="VoiceCheck"/>: the overlay re-open lives in core so the service
/// needs no Mutagen.Skyrim dependency.</summary>
public static class CellShellCheck
{
    /// <summary>The catalog name (RecordNaming.StripGetterInterface of ICellGetter) the create flow stamps on a created
    /// cell — the filter for "which created records are cells".</summary>
    public const string CellCatalogName = "Cell";

    /// <summary>Run the structural-shell report over the cells created by ONE create call. Re-opens the just-written
    /// <paramref name="patchPath"/> read-only, reads each created cell's <c>IsInteriorCell</c> flag, and lists the world
    /// content houseCARL does NOT author (fixed by kind). Returns <see cref="CellShellReport.Empty"/> when the call
    /// created no cells. A whole-check failure (the patch won't re-open) is surfaced on
    /// <see cref="CellShellReport.CheckError"/> — NEVER thrown (the create already succeeded; this is a verify step).</summary>
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
            patch = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
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

    /// <summary>The world content houseCARL does NOT author for a freshly-created cell — fixed by kind. A STANDING list,
    /// NOT a field-state check: setting a LightingTemplate FormLink is not authoring the lit scene, so the caveat holds
    /// regardless of which cell fields the same call set.</summary>
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

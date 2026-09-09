using System.Text;
using System.Text.Json;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// The facegen family's own render, in both transports — its head, its rows, and the unit costs the demand pass
/// measures with. Its own file for the reason every family's render is: what a family says about itself is that
/// family's fact.
/// </summary>
internal static class FaceGenSweepRender
{
    // ---- text ---------------------------------------------------------------------------------------

    /// <summary>The family's head: what it swept, what it excluded, and what it found by class. Every number states
    /// its own scope, and the two things this family does NOT claim — the benign class it counts but does not list,
    /// and the clean pairs whose record test could not run — are stated here rather than left to silence.</summary>
    internal static void AppendHead(StringBuilder sb, FaceGenCheckResult r)
    {
        sb.Append("scanned ").Append(r.NpcsScanned).Append(r.NpcsScanned == 1 ? " NPC · " : " NPCs · ")
          .Append(r.NpcsTemplated).Append(" excluded (Template+Traits: no bake of their own) · ")
          .Append(r.FilesSeen).Append(" facegen file(s) on disk · ")
          .Append(r.TotalFound).Append(" finding(s)");
        if (r.Epoch is not null) sb.Append(" · epoch=").Append(r.Epoch);
        sb.Append('\n');
        if (r.ByClass is { Count: > 0 } tally)
            sb.Append("by class: ").Append(string.Join(", ", tally.Select(c => c.Key + "=" + c.Count))).Append('\n');
        if (r.FilterNote is not null) sb.Append(r.FilterNote).Append('\n');
        if (r.OffOrderScanned is { Count: > 0 } off)
            sb.Append("swept OFF-ORDER: ").Append(string.Join(", ", off))
              .Append(" — their NPCs are judged from the file's own records.\n");
        if (!r.WholeOrder)
            sb.Append("note: the file half of the population (orphaned bakes — 'inert', 'foreign_index') is reported "
                    + "only on an UNSCOPED sweep. Under plugins= a file for an NPC outside the scope is out of scope, "
                    + "not inert.\n");
        int benign = r.CountOf(FaceGenFindingClass.FamilySplit);
        if (benign > 0 && !r.Classes.HasFlag(FaceGenFindingClass.FamilySplit))
            sb.Append("note: ").Append(benign).Append(" benign 'family_split' row(s) counted above are NOT listed — ")
              .Append("ask for them with findings=[\"family_split\"].\n");
        else if (benign > 0)
            sb.Append("note: 'family_split' is a NAME-BASED inference (one product's two halves, or a repack of its "
                    + "own archive), not a verdict — a few carry real risk.\n");
        if (r.NoComparisonPole > 0)
            sb.Append("note: ").Append(r.NoComparisonPole).Append(" clean pair(s) could NOT be tested for a stale bake — ")
              .Append("the facegen owner's mod ships no plugin that defines the NPC, so there was no pole to compare "
                    + "against. Not counted clean.\n");
        if (r.ScanError is not null)
            sb.Append("[SCAN ERROR] ").Append(r.ScanError).Append('\n');
        if (r.ReadIncomplete)
            sb.Append("note: a BSA failed to read this build — an 'absent' half below may merely be unscanned.\n");
    }

    /// <summary>The family's body — everything a cap can refuse.</summary>
    internal static void AppendSection(StringBuilder sb, FaceGenCheckResult r, BoundedBody body, int histogramLimit)
    {
        if (r.CountsOnly)
        {
            Wire.AppendHistogramAxes(sb, body, histogramLimit, Axes(r));
            return;
        }
        if (r.Findings.Count == 0)
            sb.Append("\nNo facegen findings in the swept scope.\n");
        foreach (var f in r.Findings)
        {
            var unit = ComposeRow(f);
            if (!body.Emit(SweepSubject.FaceGenRows, unit.Length, () => sb.Append(unit))) break;
        }
    }

    /// <summary>One finding's whole row, composed before it is offered to the budget — measured before the write,
    /// so a unit can never land the response over its cap.</summary>
    internal static string ComposeRow(FaceGenFinding f)
    {
        var sb = new StringBuilder();
        sb.Append('\n').Append('[').Append(f.Class.ToUpperInvariant()).Append("] ")
          .Append(f.FormId ?? "<no formid>");
        if (!string.IsNullOrEmpty(f.EditorId)) sb.Append(" '").Append(f.EditorId).Append('\'');
        sb.Append('\n')
          .Append("  master=").Append(f.DefiningMaster)
          .Append("  record=").Append(f.RecordWinner ?? "<none>")
          .Append("  mesh=").Append(f.MeshWinner ?? "<absent>")
          .Append("  tint=").Append(f.TintWinner ?? "<absent>").Append('\n');
        if (f.Detail is not null) sb.Append("  ").Append(f.Detail).Append('\n');
        sb.Append("  fix: ").Append(f.Fix).Append('\n');
        return sb.ToString();
    }

    /// <summary>The two <c>counts_only=</c> axes: by finding class, and by the mod that owns the winning bake.</summary>
    internal static HistogramAxis[] Axes(FaceGenCheckResult r) => new[]
    {
        new HistogramAxis(SweepSubject.FaceGenClassRows, r.CountsOnly ? r.ByClass : null, "facegen findings by class"),
        new HistogramAxis(SweepSubject.FaceGenModRows, r.CountsOnly ? r.ByOwningMod : null,
                          "facegen findings by owning mod",
                          Note: "the owning mod is whichever provider wins the bake, not the plugin that wins the record."),
    };

    // ---- json ---------------------------------------------------------------------------------------

    internal static void WriteHead(Utf8JsonWriter w, FaceGenCheckResult r)
    {
        w.WriteNumber("npcs_scanned", r.NpcsScanned);
        w.WriteNumber("npcs_excluded_templated", r.NpcsTemplated);
        w.WriteNumber("facegen_files_on_disk", r.FilesSeen);
        w.WriteNumber("findings_found", r.TotalFound);
        w.WriteNumber("clean_pairs_without_comparison_pole", r.NoComparisonPole);
        w.WriteBoolean("whole_order", r.WholeOrder);
        w.WriteBoolean("family_split_listed", r.Classes.HasFlag(FaceGenFindingClass.FamilySplit)
                                              && r.Classes != FaceGenFindingClass.All);
        if (r.Epoch is not null) w.WriteString("epoch", r.Epoch);
        if (r.FilterNote is not null) w.WriteString("narrowed", r.FilterNote);
        if (r.ScanError is not null) w.WriteString("scan_error", r.ScanError);
        if (r.ReadIncomplete) w.WriteBoolean("read_incomplete", true);
        if (r.OffOrderScanned is { Count: > 0 } off)
        {
            w.WriteStartArray("off_order_scanned");
            foreach (var n in off) w.WriteStringValue(n);
            w.WriteEndArray();
        }
        w.WriteStartObject("counts_by_class");
        foreach (var c in r.ByClass ?? Array.Empty<SweepCount>()) w.WriteNumber(c.Key, c.Count);
        w.WriteEndObject();
    }

    internal static void WriteSection(Utf8JsonWriter w, FaceGenCheckResult r, BoundedBody body, int histogramLimit)
    {
        if (r.CountsOnly)
        {
            JsonWire.WriteHistogramAxes(w, body, histogramLimit, Axes(r));
            return;
        }
        var depths = new JsonWire.JsonUnitDepths(w.CurrentDepth);
        w.WriteStartArray("findings");
        int shown = 0;
        foreach (var f in r.Findings)
        {
            var row = f;
            if (!body.Emit(SweepSubject.FaceGenRows, RowCostFor(row, depths.FaceGenRows, shown > 0),
                           () => WriteRow(w, row))) break;
            shown++;
        }
        w.WriteEndArray();
        w.WriteNumber("findings_rendered", shown);
    }

    /// <summary>One row, in the json lane — the same facts the text row carries, and the same shape the
    /// <c>to_file=</c> artifact writes.</summary>
    internal static void WriteRow(Utf8JsonWriter w, FaceGenFinding f)
    {
        w.WriteStartObject();
        w.WriteString("class", f.Class);
        if (f.FormId is null) w.WriteNull("formid"); else w.WriteString("formid", f.FormId);
        if (f.EditorId is null) w.WriteNull("editorid"); else w.WriteString("editorid", f.EditorId);
        w.WriteString("defining_master", f.DefiningMaster);
        if (f.RecordWinner is null) w.WriteNull("record_winner"); else w.WriteString("record_winner", f.RecordWinner);
        if (f.MeshWinner is null) w.WriteNull("mesh_winner"); else w.WriteString("mesh_winner", f.MeshWinner);
        if (f.TintWinner is null) w.WriteNull("tint_winner"); else w.WriteString("tint_winner", f.TintWinner);
        if (f.OwningMod is null) w.WriteNull("owning_mod"); else w.WriteString("owning_mod", f.OwningMod);
        if (f.Detail is null) w.WriteNull("detail"); else w.WriteString("detail", f.Detail);
        w.WriteString("fix", f.Fix);
        w.WriteEndObject();
    }

    /// <summary>The row's cost, for the demand pass — measured where the unit lands, like every other family's.</summary>
    internal static int RowCostFor(FaceGenFinding f, int depth, bool subsequent)
        => JsonWire.MeasureUnit(depth, subsequent, w => WriteRow(w, f));

    /// <summary>The columns a <c>to_file=</c> artifact's rows carry, in order.</summary>
    internal static readonly string[] RowSchema =
    {
        "class", "formid", "editorid", "defining_master", "record_winner", "mesh_winner", "tint_winner",
        "owning_mod", "detail", "fix",
    };
}

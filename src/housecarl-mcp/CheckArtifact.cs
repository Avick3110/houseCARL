using System.Text.Json;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// <c>check</c>'s <c>to_file=</c> artifact: every finding this call made, one JSONL row each, under the manifest
/// convention <c>records</c> already uses (<see cref="ResultArtifact"/>).
///
/// <para><b>One artifact, one row shape, a <c>family</c> column.</b> A merged call runs several families and they
/// find different things; a file per family would leave the caller joining them, and a row shape per family would
/// leave the manifest's <c>row_schema</c> describing none of them. So every family's finding is flattened onto one
/// wide row and the columns a family does not use are null — which is what a jsonl consumer greps on anyway.</para>
///
/// <para>Rows are the sweep's OWN findings, not the render's: the artifact is written from the results, so a row is
/// never missing because the inline body ran out of characters. What <c>limit=</c> already cut before the results
/// were built is cut here too, and the manifest says so by carrying <c>total</c> above <c>row_count</c>.</para>
/// </summary>
internal static class CheckArtifact
{
    /// <summary>The columns every row carries, in order. Written into the manifest, so a consumer reading the file
    /// back has the shape without opening a row.</summary>
    internal static readonly string[] RowSchema =
    {
        "family", "class", "plugin", "formid", "editorid", "record_type", "target", "property", "script",
        "defining_master", "record_winner", "mesh_winner", "tint_winner", "owning_mod", "detail", "fix",
    };

    /// <summary>Write the artifact for one sweep. Returns the spill to render, or a named error the caller renders
    /// verbatim — never throws for an IO failure.</summary>
    internal static (SpillInfo? Spill, string? Error) Write(CheckSweep s, string path,
                                                            IReadOnlyList<KeyValuePair<string, string>> query)
    {
        using var writer = new ResultArtifact.Writer();
        int total = 0;

        if (s.Errors is { Error: null } e)
            foreach (var p in e.Reports)
            {
                foreach (var d in p.Dangling)
                {
                    total++;
                    writer.WriteRow((w, _) => Row(w, "errors", "dangling", plugin: p.Plugin,
                                                  formid: d.Source.ToString(), editorid: d.SourceEditorId,
                                                  recordType: d.SourceType, target: d.Target.ToString()));
                }
                foreach (var m in p.MissingMasters)
                {
                    total++;
                    writer.WriteRow((w, _) => Row(w, "errors", "missing_master", plugin: p.Plugin, target: m,
                                                  fix: p.InstalledButInactiveMasters?.Contains(m, StringComparer.OrdinalIgnoreCase) == true
                                                       ? "Installed but not active — enable it in MO2."
                                                       : "Not in the active order — install it, or remove the plugin that declares it."));
                }
                if (p.ScanError is { } se)
                {
                    total++;
                    writer.WriteRow((w, _) => Row(w, "errors", "scan_error", plugin: p.Plugin, detail: se));
                }
            }

        if (s.Scripts is { Error: null } sc)
            foreach (var rec in sc.Reports)
            {
                if (rec.ScanError is { } se)
                {
                    total++;
                    writer.WriteRow((w, _) => Row(w, "scripts", "scan_error", plugin: rec.Plugin, detail: se));
                    continue;
                }
                foreach (var u in rec.Unbound)
                {
                    total++;
                    writer.WriteRow((w, _) => Row(w, "scripts", u.IsObjectType ? "unbound_object" : "unbound_scalar",
                                                  plugin: rec.Plugin, formid: rec.Record.ToString(),
                                                  editorid: rec.EditorId, recordType: rec.RecordType,
                                                  property: u.PropertyName, script: u.Script,
                                                  detail: "declared in " + u.DeclaringScript + " (" + u.PexTypeName + ")"));
                }
                foreach (var n in rec.NullObjects)
                {
                    total++;
                    writer.WriteRow((w, _) => Row(w, "scripts", "bound_null", plugin: rec.Plugin,
                                                  formid: rec.Record.ToString(), editorid: rec.EditorId,
                                                  recordType: rec.RecordType, property: n.PropertyName, script: n.Script));
                }
                foreach (var uv in rec.Unverifiable)
                {
                    total++;
                    writer.WriteRow((w, _) => Row(w, "scripts", "unverifiable", plugin: rec.Plugin,
                                                  formid: rec.Record.ToString(), editorid: rec.EditorId,
                                                  recordType: rec.RecordType, script: uv.Script, detail: uv.Reason));
                }
            }

        if (s.Dialogue is { Error: null } d2)
        {
            foreach (var seed in d2.Resolved)
                foreach (var t in seed.Report!.Topics)
                    foreach (var issue in t.Issues)
                    {
                        total++;
                        writer.WriteRow((w, _) => Row(w, "dialogue", issue.Severity.ToString().ToLowerInvariant(),
                                                      plugin: t.WinnerPlugin, formid: t.Topic.ToString(),
                                                      editorid: t.TopicEditorId, recordType: "DIAL",
                                                      detail: issue.Message));
                    }
            foreach (var seed in d2.Unresolved)
            {
                total++;
                writer.WriteRow((w, _) => Row(w, "dialogue", "seed_unreachable", formid: seed.Seed, detail: seed.Refusal));
            }
        }

        if (s.FaceGen is { Error: null } fg)
            foreach (var f in fg.Findings)
            {
                total++;
                writer.WriteRow((w, _) => Row(w, "facegen", f.Class, formid: f.FormId, editorid: f.EditorId,
                                              definingMaster: f.DefiningMaster, recordWinner: f.RecordWinner,
                                              meshWinner: f.MeshWinner, tintWinner: f.TintWinner,
                                              owningMod: f.OwningMod, detail: f.Detail, fix: f.Fix));
            }

        // The facegen family counts findings its listing budget cut, so the file's own total says so rather than
        // letting row_count read as the whole answer.
        if (s.FaceGen is { Error: null } fgt) total += Math.Max(0, fgt.TotalFound - fgt.Findings.Count);

        var (manifest, err) = writer.Save(path, ToolNames.Check, query, identity: "formid", RowSchema,
                                          sort: "family, then the order each family reported",
                                          total: total, epoch: s.Epoch ?? "",
                                          notes: new[]
                                          {
                                              "One row per finding. A column a family does not use is null; the "
                                              + "'family' and 'class' columns say which taxonomy a row belongs to.",
                                              "A row's 'formid' is the SOURCE record the finding is about, which is "
                                              + "what makes it re-enterable as formids=[\"@<path>\"].",
                                          });
        return err is not null ? (null, err) : (new SpillInfo(path, manifest!, "to_file"), null);
    }

    /// <summary>The response a <c>to_file=</c> call renders: the scope sentence, each family's boundary, and the
    /// manifest — no rows, because the rows ARE the file. The same disposition <c>records</c> takes.</summary>
    internal static string RenderManifestOnly(CheckSweep s, SpillInfo spill, bool json)
    {
        var o = CheckOutcome.For(s);
        if (json)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, JsonWire.Opts))
            {
                w.WriteStartObject();
                w.WriteString("findings_scope", o.ScopeSentence());
                if (o.Epoch is not null) w.WriteString("epoch", o.Epoch);
                w.WriteStartObject("boundaries");
                foreach (var a in o.Sections.Zip(o.Accountings(0)))
                    w.WriteString(SweepFamilySelection.Token(a.First), a.Second.Boundary);
                w.WriteEndObject();
                Artifacts.WriteSpillJson(w, spill);
                w.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
        var sb = new System.Text.StringBuilder();
        sb.Append(ReadSentences.SweepMergedTitle).Append('\n').Append(o.ScopeSentence()).Append('\n');
        if (o.Epoch is not null) sb.Append("epoch=").Append(o.Epoch).Append('\n');
        var accts = o.Accountings(0);
        for (int i = 0; i < o.Sections.Count; i++)
            sb.Append('\n').Append(string.Format(ReadSentences.SweepBoundaryLabelFor,
                                                 SweepFamilySelection.Token(o.Sections[i])))
              .Append(accts[i].Boundary).Append('\n');
        Artifacts.AppendSpillText(sb, spill);
        return sb.ToString();
    }

    /// <summary>One row, every column present so a consumer can index by name without probing.</summary>
    static void Row(Utf8JsonWriter w, string family, string cls, string? plugin = null, string? formid = null,
                    string? editorid = null, string? recordType = null, string? target = null, string? property = null,
                    string? script = null, string? definingMaster = null, string? recordWinner = null,
                    string? meshWinner = null, string? tintWinner = null, string? owningMod = null,
                    string? detail = null, string? fix = null)
    {
        w.WriteStartObject();
        w.WriteString("family", family);
        w.WriteString("class", cls);
        Str(w, "plugin", plugin);
        Str(w, "formid", formid);
        Str(w, "editorid", editorid);
        Str(w, "record_type", recordType);
        Str(w, "target", target);
        Str(w, "property", property);
        Str(w, "script", script);
        Str(w, "defining_master", definingMaster);
        Str(w, "record_winner", recordWinner);
        Str(w, "mesh_winner", meshWinner);
        Str(w, "tint_winner", tintWinner);
        Str(w, "owning_mod", owningMod);
        Str(w, "detail", detail);
        Str(w, "fix", fix);
        w.WriteEndObject();
    }

    static void Str(Utf8JsonWriter w, string name, string? v)
    {
        if (v is null) w.WriteNull(name); else w.WriteString(name, v);
    }
}

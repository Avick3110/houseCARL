using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// houseCARL standalone-NPC-copy tool — the composed verb the capability chain builds toward (Stage 3): one
/// reviewable operation that forks a donor NPC's appearance into a houseCARL patch with NO donor master, no CK,
/// no .nif editing. Mechanism = Duplicate + RemapLinks (whole-record copies), so the two empirically-pinned traps
/// (HDPT.Parts morph refs → lip-sync; TextureLighting=0 → dark skin) are structurally impossible to reproduce.
/// The donor may be DISABLED — houseCARL reads its file out of load order (the read_plugin_file lane) and says so.
/// </summary>
[McpServerToolType]
public static class NpcCopyTools
{
    [McpServerTool(Name = "housecarl_copy_npc_appearance", Title = "Copy an NPC's appearance as a standalone (no donor master)"),
     Description(
         "Copy a donor NPC's whole APPEARANCE — headparts (with their texture sets, extra parts and the morph .tri " +
         "references that drive lip-sync), face morph/parts, tint layers, texture lighting, hair color, head texture, " +
         "worn armor link, weight/height — into a houseCARL patch as a TRUE STANDALONE: donor-defined records are " +
         "deep-copied under NEW FormIDs (headpart EditorIDs preserved — the engine maps baked facegeom shapes to " +
         "headparts BY NAME), the baked facegen pair (.nif geometry + .dds tint) is renamed to the new FormID's path, " +
         "and the donor-only textures/meshes the records and geometry reference are carried — so the result works with " +
         "the donor plugin REMOVED. No donor master, no CK, no .nif editing. TWO MODES (pass exactly one): " +
         "target_formid= copies the appearance ONTO AN EXISTING NPC (e.g. a follower you scaffolded); new_editorid= " +
         "mints a FULL CLONE as a new NPC record — any donor-internal NON-appearance links on the clone (factions, " +
         "outfits, packages, scripts) are STRIPPED and each strip is reported (the clone is donor-free, loudly). The " +
         "donor may be ACTIVE (source_formid resolves via the load order) or DISABLED — pass source_plugin= (its " +
         "filename; houseCARL locates it across enabled AND disabled MO2 mod folders, or an absolute path) and the " +
         "read is stamped OUT-OF-LOAD-ORDER. Writes a NEW plugin (folder-per-patch) or extends an existing houseCARL " +
         "patch via into=. Originals untouched. Refuses loud on a donor-internal custom RACE (out of scope — keep the " +
         "race mod as a master), a runaway closure, or anything that would silently master the donor.")]
    public static string CopyNpcAppearance(
        LoadOrderService svc,
        [Description("The donor NPC's FormID 'XXXXXX:Plugin.esp' (e.g. '000D62:Vivace.esp') — the appearance being copied.")]
            string source_formid,
        [Description("Optional. Read the donor from this plugin FILE instead of the active load order — the DISABLED-donor lane. A filename ('Vivace.esp'; located across enabled+disabled mod folders, overwrite and Data) or an absolute path. Omit when the donor is active.")]
            string? source_plugin = null,
        [Description("Optional (with source_plugin). The exact MO2 mod-folder name to read the plugin from, when the filename exists in several folders.")]
            string? source_mod = null,
        [Description("APPLY MODE: the EXISTING NPC to dress in the donor's appearance — 'XXXXXX:Plugin.esp'. An active NPC, or a record in the target patch itself (with into=). Pass this OR new_editorid, not both.")]
            string? target_formid = null,
        [Description("CLONE MODE: mint a full standalone clone of the donor as a NEW NPC with this EditorID. Pass this OR target_formid, not both.")]
            string? new_editorid = null,
        [Description("Optional (clone mode). The clone's display Name; defaults to the donor's.")]
            string? new_name = null,
        [Description("Optional. Base name for the NEW patch plugin + mod folder (default: the new_editorid in clone mode, 'houseCARL_NpcCopy' otherwise); auto-suffixed if taken.")]
            string? patch_name = null,
        [Description("Optional. Extend an existing houseCARL patch instead of creating a fresh one — the patch plugin's filename (found even if you renamed its MO2 folder) or the mod-folder name.")]
            string? into = null) => Guard.Tool("housecarl_copy_npc_appearance", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        return Render(svc.CopyNpcAppearance(source_formid, source_plugin, source_mod,
                                            target_formid, new_editorid, new_name, patch_name, into));
    });

    static string Render(NpcCopyOutcome o)
    {
        if (!o.Success) return "error: " + o.Error;

        var sb = new StringBuilder();
        sb.AppendLine(o.Mode == "clone"
            ? $"CLONED {o.DonorKey} → new NPC {o.NewNpcKey} in {Path.GetFileName(o.OutPath!)}{(o.Extended ? " (extended)" : " (new patch)")}."
            : $"APPLIED {o.DonorKey}'s appearance onto {o.NewNpcKey} in {Path.GetFileName(o.OutPath!)}{(o.Extended ? " (extended)" : " (new patch)")}.");
        sb.AppendLine($"donor read from: {o.DonorReadFrom}{(o.DonorOutOfLoadOrder ? "  [OUT-OF-LOAD-ORDER — the game does not load this file; the copy is what makes it live]" : "")}");
        sb.AppendLine($"plugin: {o.OutPath} ({o.Bytes:N0} bytes)");
        sb.AppendLine($"masters: {(o.Masters.Count == 0 ? "<none>" : string.Join(", ", o.Masters))}");
        sb.AppendLine(o.DonorAmongMasters
            ? "!! the donor IS among the masters — the copy is NOT standalone. This is unexpected; please report it."
            : "standalone: the donor is NOT a master of the patch.");

        if (o.Internalized.Count > 0)
        {
            sb.AppendLine($"\ninternalized {o.Internalized.Count} donor record(s) under new FormIDs (EditorIDs preserved — facegeom block-name identity):");
            foreach (var r in o.Internalized)
                sb.AppendLine($"  - {r.Type} '{r.EditorId}'  {r.OldKey} → {r.NewKey}   (via {r.PulledBy})");
        }
        if (o.KeptLinkCount > 0)
            sb.AppendLine($"kept {o.KeptLinkCount} link(s) to records that resolve in your active load order (vanilla / shared resources) — mastered normally.");

        if (o.Mode == "apply" && o.CopiedFields.Count > 0)
            sb.AppendLine($"\ncopied appearance fields: {string.Join(", ", o.CopiedFields)}");

        if (o.Mode == "clone")
        {
            if (o.Stripped.Count > 0)
            {
                sb.AppendLine($"\nSTRIPPED {o.Stripped.Count} donor-internal NON-appearance reference(s) from the clone (it must not master the donor):");
                foreach (var s in o.Stripped) sb.AppendLine($"  - {s.Field}  (was {s.Removed})");
                sb.AppendLine("  the clone keeps the donor's LOOK but not its donor-defined factions/outfits/packages/scripts — re-author those against your own or vanilla systems as needed.");
            }
            else
                sb.AppendLine("\nno donor-internal non-appearance references needed stripping.");
        }

        if (o.Assets is { } a)
        {
            sb.AppendLine($"\nassets: {a.Carried.Count} file(s) carried" +
                          $"{(a.FaceGenMeshCarried && a.FaceGenTintCarried ? " (facegen pair renamed to the new FormID path)" : "")}.");
            foreach (var c in a.Carried)
                sb.AppendLine(c.OldRelPath.Equals(c.NewRelPath, StringComparison.OrdinalIgnoreCase)
                    ? $"  - {c.NewRelPath}  ({c.Bytes:N0} B, from {c.From})"
                    : $"  - {c.OldRelPath} → {c.NewRelPath}  ({c.Bytes:N0} B, from {c.From})");
            if (!a.FaceGenMeshCarried || !a.FaceGenTintCarried)
                sb.AppendLine($"  !! facegen {(a.FaceGenMeshCarried ? "TINT" : a.FaceGenTintCarried ? "MESH" : "MESH + TINT")} not carried — see the misses below; without the pair the engine regenerates the head (grey/dark-face risk). Verify in-game.");
            foreach (var s in a.SkippedStillProvided) sb.AppendLine($"  · skipped: {s}");
            foreach (var m in a.Missing) sb.AppendLine($"  !! missing: {m}");
            foreach (var f in a.Failures) sb.AppendLine($"  !! {f}");
        }

        sb.AppendLine($"\nNEXT: enable the mod folder in MO2 (it holds the plugin AND the carried files) and sort the plugin; then test in-game — face, hair, lip-sync while speaking.");
        return sb.ToString().TrimEnd();
    }
}

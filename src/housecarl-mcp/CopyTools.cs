using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Mutagen.Bethesda.Plugins;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// houseCARL's 2.0 CLOSURE-COPY tool (SPEC §6: `copy` absorbs `copy_npc_appearance`). Generic by construction —
/// it copies a record's declared link closure out of one plugin universe and into a patch, and knows nothing about
/// what the records mean. The domain half (which fields seed a walk, which record classes must never be walked
/// into) arrives as DATA the caller supplies, which is what a skill carries.
///
/// <para>This class owns every user-facing sentence on the surface; the service and core beneath it return typed
/// data (#337's one-source-per-sentence rule). Argument-shape validation therefore lives HERE too — a refusal is
/// prose, and prose does not belong in a service.</para>
/// </summary>
[McpServerToolType]
public static class CopyTools
{
    [McpServerTool(Name = "housecarl_copy", Title = "Copy a record's link closure into a patch"),
     Description(
         "Copy a record together with the records it DEPENDS ON — its link closure — out of one plugin universe " +
         "and into a houseCARL patch, under NEW FormIDs, so the result no longer masters the plugin you copied " +
         "from. The mechanism is a whole-record duplicate, so every field carries by construction; EditorIDs are " +
         "preserved on the copies.\n\n" +
         "WHAT GETS COPIED is decided by a rule, not a list: a linked record is INTERNALIZED (duplicated under a " +
         "new FormID) when it is defined in the plugin(s) you are copying away from, or when it does not resolve " +
         "in your active load order; every other link is KEPT and mastered normally. So vanilla and active " +
         "shared-resource records stay links, and only what would VANISH with the source is copied.\n\n" +
         "WHERE THE WALK STARTS is yours: seed_paths= names the link-bearing fields to walk from (e.g. " +
         "['HeadParts','HairColor','HeadTexture','WornArmor'] for an NPC's appearance). A path that is not a field " +
         "on the record, or carries no record links, is REFUSED BY NAME rather than quietly seeding nothing. " +
         "exclude_types= names record types the walk must not enter, each as 'Type:stop' (prune it, keep the link) " +
         "or 'Type:refuse' (fail the whole copy) — a RACE is the standing 'refuse' case, since a race pulls " +
         "skeletons and sibling races rather than an appearance subtree.\n\n" +
         "FROM WHERE — from_source= is an ORDERED LIST of sources, tried in order, FIRST HIT WINS. Each element is " +
         "either 'winner' (the active load order's winning version of each record) or a plugin FILENAME, which " +
         "resolves whether that plugin is active OR sitting on disk in a DISABLED mod. Naming several is how you " +
         "read a look from an override patch while its records come from the defining plugin: " +
         "from_source=['Override.esp','Donor.esp']. This is FALLBACK, never a merge — a record present in several " +
         "sources comes from the FIRST, and the result names which source produced each record. A record no source " +
         "has is refused, naming every source consulted. ('winner' is the bare word: plugin names always carry an " +
         "extension, so the two can never collide. 'previous_provider' is refused here — it is relative to a " +
         "subject plugin, and a walk reaching records through links has no subject for it to be relative to.)\n\n" +
         "TWO DESTINATIONS (pass exactly one): target= copies onto an EXISTING record — an active one, or a record " +
         "in the patch itself when you pass into=; the seed fields are set on it pointing at the internalized " +
         "copies. new_editorid= mints a CLONE of the source record instead, and every link on it still pointing " +
         "into the source is STRIPPED and reported by name — a link the record model REQUIRES cannot be stripped, " +
         "so that refuses loud rather than writing an invented null or silently mastering the source.\n\n" +
         "Writes a NEW plugin (folder-per-patch) or extends one with into=. Originals are never touched, and a " +
         "refusal writes nothing.")]
    public static string Copy(
        LoadOrderService svc,
        [Description("The record to copy, 'XXXXXX:Plugin.esp' (e.g. '000D62:Vivace.esp').")]
            string from,
        [Description("The source universe: an ORDERED list tried first-hit-wins. Each element is 'winner' (the active load order's winning version) or a plugin filename (active, or on disk in a disabled mod). Default: ['winner'].")]
            string[]? from_source = null,
        [Description("The link-bearing field paths to start the walk from, e.g. ['HeadParts','WornArmor']. A path that is not a field, or carries no record links, is refused by name.")]
            string[]? seed_paths = null,
        [Description("Optional. Record types the walk must not enter: 'Type:stop' prunes it (the link is kept), 'Type:refuse' fails the whole copy. E.g. ['Race:refuse'].")]
            string[]? exclude_types = null,
        [Description("DESTINATION: copy onto this EXISTING record, 'XXXXXX:Plugin.esp'. An active record, or one in the patch itself (with into=). Pass this OR new_editorid.")]
            string? target = null,
        [Description("DESTINATION: mint a CLONE of the source record with this EditorID. Pass this OR target.")]
            string? new_editorid = null,
        [Description("Optional. Base name for the NEW patch plugin + mod folder; auto-suffixed if taken.")]
            string? patch = null,
        [Description("Optional. Extend an existing houseCARL patch instead of creating one — its plugin filename.")]
            string? into = null) => Guard.Tool("housecarl_copy", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;

        FormKey fromKey;
        try { fromKey = FormKey.Factory((from ?? "").Trim()); }
        catch (Exception ex) { return $"error: bad from '{from}': {ex.Message}. Expected 'XXXXXX:Plugin.esp'."; }

        bool hasTarget = !string.IsNullOrWhiteSpace(target);
        bool hasClone = !string.IsNullOrWhiteSpace(new_editorid);
        if (hasTarget == hasClone)
            return "error: pass EXACTLY ONE destination — target= (copy onto an existing record) or " +
                   "new_editorid= (mint a clone of the source record).";

        FormKey? targetKey = null;
        if (hasTarget)
        {
            try { targetKey = FormKey.Factory(target!.Trim()); }
            catch (Exception ex) { return $"error: bad target '{target}': {ex.Message}. Expected 'XXXXXX:Plugin.esp'."; }
        }

        var seeds = (seed_paths ?? Array.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        if (seeds.Count == 0)
            return "error: seed_paths is required — name the link-bearing field(s) the walk starts from " +
                   "(e.g. seed_paths=['HeadParts','WornArmor']). Without them there is nothing to copy.";

        var exclusions = new List<WalkExclusion>();
        foreach (var raw in exclude_types ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var parts = raw.Split(':', 2);
            var type = parts[0].Trim();
            var sev = parts.Length > 1 ? parts[1].Trim().ToLowerInvariant() : "refuse";
            if (type.Length == 0) return $"error: exclude_types entry '{raw}' names no record type. Use 'Type:stop' or 'Type:refuse'.";
            if (sev is not ("stop" or "refuse"))
                return $"error: exclude_types entry '{raw}' has severity '{sev}' — use 'stop' (prune, keep the link) or 'refuse' (fail the copy).";
            exclusions.Add(new WalkExclusion(type,
                sev == "stop" ? ExclusionSeverity.Stop : ExclusionSeverity.Refuse,
                $"excluded by the caller as '{sev}'"));
        }

        var poles = (from_source ?? Array.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        if (poles.Count == 0) poles.Add(SourcePoles.Winner);

        return Render(svc.CopyClosure(fromKey, poles, seeds, exclusions, targetKey, new_editorid, patch, into));
    });

    /// <summary>Render one outcome. Internal so a guard can pin the sentences without going through the wire.</summary>
    internal static string Render(ClosureCopyOutcome o)
    {
        var sb = new StringBuilder();
        if (!o.Success)
        {
            sb.Append("error: ");
            if (o.WalkRefusal is { } w) sb.Append(RenderWalkRefusal(w, o.SourcesConsulted));
            else if (o.CopyRefusal is { } c) sb.Append(RenderCopyRefusal(c));
            else sb.Append(o.EngineError ?? "the copy could not be completed.");
            sb.Append("\nNothing was written.");
            return sb.ToString();
        }

        sb.Append(o.Mode == "clone"
            ? $"CLONED {o.SourceKey} -> {o.NewKey}\n"
            : $"COPIED {o.SourceKey}'s walked fields onto {o.NewKey}\n");
        sb.Append(WriteSentences.CopySourcesConsulted(o.SourcesConsulted));
        sb.Append(WriteSentences.NewOrExtendedArtifact(o.Extended, Path.GetFileName(o.OutPath!), o.Bytes,
            Path.GetFileName(Path.GetDirectoryName(o.OutPath!)!)));

        if (o.ReadBackWarning is not null) sb.Append(WriteSentences.CopyReadBackUnverified).Append('\n');
        else
        {
            sb.Append(WriteSentences.Masters(o.Masters));
            sb.Append(o.SourceAmongMasters ? WriteSentences.CopySourceMastered : WriteSentences.CopyStandalone).Append('\n');
        }

        if (o.Copied.Count > 0)
        {
            sb.Append('\n').Append(WriteSentences.CopyInternalizedHeader).Append('\n');
            foreach (var c in o.Copied)
                sb.Append($"  - {c.TypeName} '{c.EditorId ?? "<no editorid>"}'  {c.OldKey} -> {c.NewKey}   (from {c.ArmSpelling}, via {c.PulledBy})\n");
        }
        if (o.Kept.Count > 0)
            sb.Append($"kept {o.Kept.Count} link(s) that resolve outside the source — mastered normally.\n");
        if (o.Cycles.Count > 0)
        {
            sb.Append(WriteSentences.CopyCyclesHeader).Append('\n');
            foreach (var cy in o.Cycles) sb.Append($"  - {cy.Back} is reached again from {cy.PulledBy}\n");
        }
        if (o.Attached.Count > 0)
            sb.Append($"attached: {string.Join(", ", o.Attached.Select(a => a.Field))}\n");
        if (o.Stripped.Count > 0)
        {
            sb.Append($"\nSTRIPPED {o.Stripped.Count} reference(s) that still pointed into the source:\n");
            foreach (var s in o.Stripped) sb.Append($"  - {s.Field}  (was {s.Removed})\n");
            sb.Append(WriteSentences.CopyStripConsequence).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    static string RenderWalkRefusal(WalkRefusal w, IReadOnlyList<string> sources) => w.Kind switch
    {
        WalkRefusalKind.SourceMiss => WriteSentences.CopySourceMiss($"{w.Key} (reached via {w.PulledBy})", sources),
        WalkRefusalKind.SourceFault => WriteSentences.CopySourceFault(
            $"{w.Key}", w.Fault?.Arm.Spelling ?? "a source", w.Detail),
        WalkRefusalKind.UnknownSeedPath =>
            $"{w.Detail}. seed_paths must name link-bearing fields on the record being copied.",
        WalkRefusalKind.NoSeeds =>
            "the seed fields carry no record links, so this copy would produce nothing. Check seed_paths= against the record.",
        WalkRefusalKind.Excluded =>
            $"the walk reached {w.Key} ({w.Exclusion?.TypeName}), which exclude_types= marks 'refuse'. " +
            $"Pulled in via {w.PulledBy}.",
        WalkRefusalKind.NodeCap =>
            $"the walk passed {w.Cap} records and was refused rather than truncated — a runaway, not a subtree. " +
            $"Last pull: {w.Key} via {w.PulledBy}. Chain: {string.Join(" -> ", w.Chain)}.",
        WalkRefusalKind.DepthCap =>
            $"the walk went deeper than {w.Cap} hops. Last pull: {w.Key} via {w.PulledBy}. " +
            $"Chain: {string.Join(" -> ", w.Chain)}.",
        _ => w.Detail,
    };

    static string RenderCopyRefusal(CopyRefusal c) => c.Kind switch
    {
        CopyRefusalKind.RequiredForeignLink =>
            $"the field '{c.Field}' REQUIRES a record ({c.Key}) that lives in the source you are copying away from. " +
            "It cannot be cleared without inventing data, and keeping it would master that plugin. Copy onto a " +
            "record that already has its own, using target=, instead of minting a clone.",
        CopyRefusalKind.UnclearableSubstruct =>
            $"the field '{c.Field}' carries a reference into the source and cannot be cleared. Use target= instead.",
        CopyRefusalKind.DonorLeak =>
            $"after the copy the destination still references {c.Key} in the source — refusing to write a patch that " +
            "would master it. If the destination deliberately references the source elsewhere, remove that first.",
        CopyRefusalKind.IdExhausted => c.Detail,
        _ => c.Detail,
    };
}

using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Mutagen.Bethesda.Plugins;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>The closure-copy tool: it copies a record's declared link closure out of one plugin universe into a
/// patch and knows nothing about what the records mean — which fields seed the walk and which record classes it
/// must not enter arrive as caller data. This class owns every user-facing sentence on the surface (the service and
/// core return typed data), so argument-shape validation lives here too rather than in the service.</summary>
[McpServerToolType]
public static class CopyTools
{
    [McpServerTool(Name = ToolNames.Copy, Title = "Copy a record's link closure into a patch"),
     Description(
         "Copy a record together with the records it DEPENDS ON — its link closure — out of one plugin universe " +
         "and into a houseCARL patch, under NEW FormIDs, so the result no longer masters the plugin you copied " +
         "from. The mechanism is a whole-record duplicate, so every field carries by construction; EditorIDs are " +
         "preserved on the copies.\n\n" +
         "Each axis's grammar is on its own parameters:\n" +
         "WHAT — from= is the record to copy; from_source= is the ordered universe it and its links are read " +
         "from. What the walk internalizes is decided by the plugins being copied AWAY FROM: from='s own " +
         "plugin, plus every plugin named in from_source=.\n" +
         "THE WALK — seed_paths= is where it starts; exclude_types= bounds it.\n" +
         "DESTINATION — exactly one of target= (copy onto an existing record) or new_editorid= (mint a clone of " +
         "the source record).\n" +
         "OUTPUT — patch= names a NEW plugin (folder-per-patch); into= extends an existing houseCARL patch " +
         "instead.\n\n" +
         "This tool copies RECORDS. The FILES that go with them — an NPC's FaceGen mesh and tint, say — are " +
         "placed with " + ToolNames.Place + ". Originals are never touched, and a refusal writes nothing.")]
    public static string Copy(
        LoadOrderService svc,
        [Description("The record to copy, 'XXXXXX:Plugin.esp' (e.g. '000D62:Vivace.esp').")]
            string from,
        [Description("The source universe: an ORDERED list of sources tried in order, FIRST HIT WINS. Each element is either 'winner' (the active load order's winning version of each record) or a plugin FILENAME, which resolves whether that plugin is active OR sitting on disk in a DISABLED mod. Default: ['winner']. Naming several is how you read a look from an override patch while its records come from the defining plugin: from_source=['Override.esp','Donor.esp']. This is FALLBACK, never a merge — a record present in several sources comes from the FIRST, and the result names which source produced each record — and, for a source that resolved into an MO2 MOD FOLDER (active or disabled), that folder, which is the name to pass as the provider when you place that record's files (its FaceGen, say) with " + ToolNames.Place + ". A source with no such folder — the 'winner' pole, MO2's overwrite, the game's Data folder, a file outside all of them — says so instead, so you never have to invent a folder name. ('winner' is the bare word: plugin names always carry an extension, so the two can never collide. 'previous_provider' is refused here — it is relative to a subject plugin, and a walk reaching records through links has no subject for it to be relative to.) WHAT GETS COPIED is decided by a rule, not a list: a linked record is INTERNALIZED (duplicated under a new FormID) when it is defined in one of the plugins being copied away from — from='s own defining plugin, PLUS every plugin named here; 'winner' names no plugin, so it adds none, and under the default ['winner'] from='s plugin is still the one being copied away from — or when it does not resolve in your active load order; every other link is KEPT and mastered normally. So vanilla and active shared-resource records stay links, and only what would VANISH with the source is copied. A record no source can produce — a dangling link marked for internalizing, typically — refuses the whole copy naming every source consulted, rather than writing a patch with a hole in it. When NOTHING is being copied away from — the source is a base-game master and every source you named is one too — the result is reported as an appearance transplant rather than a standalone-ization, because an always-loaded master is not being removed from anything. Name a MOD here and that mod IS being copied away from, so the ordinary standalone report applies even for a base-game FormID.")]
            string[]? from_source = null,
        [Description("WHERE THE WALK STARTS is yours: the fields to walk from, e.g. ['HeadParts','HairColor','HeadTexture','WornArmor'] for an NPC's appearance. Each must be a RECORD LINK or a LIST OF RECORD LINKS, judged on the field's DECLARED type — so a field the source happens to carry none of is still that shape. A path that is not a field, or whose entries are structures rather than links (Factions, Perks, Items), is REFUSED BY NAME rather than quietly seeding nothing: for those use " + ToolNames.Apply + "'s bundle=/assignments= zip, where op=Merge and op=ReplaceAll are your choice of merging into the target's entries or replacing them. A seed the source leaves UNSET or EMPTY clears the target's, and says so — the result is the source's look, not a mixture of both.")]
            string[]? seed_paths = null,
        [Description("Optional. Record types the walk must not enter, each as 'Type:stop' (prune it, keep the link) or 'Type:refuse' (fail the whole copy), e.g. ['Race:refuse'] — a RACE is the standing 'refuse' case, since a race pulls skeletons and sibling races rather than an appearance subtree. 'stop' KEEPS the link, so wherever the link SURVIVES it needs a plugin the patch can master: with target=, pruning a record that is NOT in your active load order is refused up front, because an artifact cannot master a plugin the game does not load. With new_editorid= the clone's strip removes links into the source anyway, so a pruned off-order record is not refused there — unless one survives the strip. Any off-order link the finished patch still carries refuses, and the refusal names WHICH cause: your 'stop', a field seed_paths never named so the copy carried the link across, or a record an earlier call already put in the patch you are extending. Every element must name a type; a blank entry is refused rather than dropped, because dropping one silently applies fewer exclusions than you passed.")]
            string[]? exclude_types = null,
        [Description("DESTINATION: copy onto this EXISTING record, 'XXXXXX:Plugin.esp' — an active record, or one in the patch itself when you pass into=; the seed fields are set on it pointing at the internalized copies. Pass this OR new_editorid.")]
            string? target = null,
        [Description("DESTINATION: mint a CLONE of the source record with this EditorID. Every link on the clone still pointing into the source is STRIPPED and reported by name — including when clearing one takes a WHOLE property with it (a script adapter, say), which the report says out loud. A link the record model REQUIRES cannot be stripped, so that refuses loud rather than writing an invented null or silently mastering the source. Pass this OR target.")]
            string? new_editorid = null,
        [Description("Optional. Base name for the NEW patch plugin + mod folder; auto-suffixed if taken.")]
            string? patch = null,
        [Description("Optional. Extend an existing houseCARL patch instead of creating one — its plugin filename.")]
            string? into = null) => Guard.Tool(ToolNames.Copy, () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;

        // One door for every token in the call, so the source and the target resolve against one index build.
        var door = svc.OpenWriteFormIdDoor();
        FormKey fromKey;
        try { fromKey = door.Parse(from); }
        catch (Exception ex) { return FormIdDoor.Sentence(ex, "error: ", $"error: bad from '{from}': {ex.Message}. Expected 'XXXXXX:Plugin.esp'."); }

        bool hasTarget = !string.IsNullOrWhiteSpace(target);
        bool hasClone = !string.IsNullOrWhiteSpace(new_editorid);
        if (hasTarget == hasClone)
            return "error: pass EXACTLY ONE destination — target= (copy onto an existing record) or " +
                   "new_editorid= (mint a clone of the source record).";

        FormKey? targetKey = null;
        if (hasTarget)
        {
            try { targetKey = door.Parse(target); }
            catch (Exception ex) { return FormIdDoor.Sentence(ex, "error: ", $"error: bad target '{target}': {ex.Message}. Expected 'XXXXXX:Plugin.esp'."); }
        }

        // Blank elements are REFUSED BY INDEX, never dropped: dropping one shifts every later index, so the
        // service's own per-element refusals would then name the wrong position back to the caller.
        var rawSeeds = seed_paths ?? Array.Empty<string>();
        for (int i = 0; i < rawSeeds.Length; i++)
            if (string.IsNullOrWhiteSpace(rawSeeds[i]))
                return $"error: seed_paths[{i}] is blank — every element must name a field on the record being " +
                       "copied. Remove the empty entry rather than leaving it for houseCARL to guess at.";
        var seeds = rawSeeds.Select(s => s.Trim()).ToList();
        if (seeds.Count == 0)
            return "error: seed_paths is required — name the link-bearing field(s) the walk starts from " +
                   "(e.g. seed_paths=['HeadParts','WornArmor']). Without them there is nothing to copy.";

        var exclusions = new List<WalkExclusion>();
        foreach (var raw in exclude_types ?? Array.Empty<string>())
        {
            // REFUSED, not skipped: skipping would run the copy with fewer exclusions than were passed, silently.
            // A blank here shifts no index the response reports, so it refuses by CONTENT rather than by index.
            if (string.IsNullOrWhiteSpace(raw))
                return "error: exclude_types has a blank entry. Every element must name a record type as " +
                       "'Type:stop' (prune it, keep the link) or 'Type:refuse' (fail the copy) — remove the empty " +
                       "entry rather than leaving it, because running with it dropped would silently apply FEWER " +
                       "exclusions than you passed.";
            var parts = raw.Split(':', 2);
            var type = parts[0].Trim();
            var sev = parts.Length > 1 ? parts[1].Trim().ToLowerInvariant() : "refuse";
            if (type.Length == 0) return $"error: exclude_types entry '{raw}' names no record type. Use 'Type:stop' or 'Type:refuse'.";
            if (sev is not ("stop" or "refuse"))
                return $"error: exclude_types entry '{raw}' has severity '{sev}' — use 'stop' (prune, keep the link) or 'refuse' (fail the copy).";
            // A duplicate type name would throw in the walk's ToDictionary, which Guard.Tool reports as an internal
            // failure rather than bad input — so it is caught here, in the layer that owns prose.
            if (exclusions.Any(x => string.Equals(x.TypeName, type, StringComparison.OrdinalIgnoreCase)))
                return $"error: exclude_types names '{type}' more than once. One severity per record type — " +
                       "'stop' (prune, keep the link) or 'refuse' (fail the copy) — so pick the one you mean.";
            exclusions.Add(new WalkExclusion(type,
                sev == "stop" ? ExclusionSeverity.Stop : ExclusionSeverity.Refuse,
                $"excluded by the caller as '{sev}'"));
        }

        var rawPoles = from_source ?? Array.Empty<string>();
        for (int i = 0; i < rawPoles.Length; i++)
            if (string.IsNullOrWhiteSpace(rawPoles[i]))
                return $"error: from_source[{i}] is blank — every element must name a source ('winner', or a plugin " +
                       "filename). Remove the empty entry; leaving it would shift the positions this call reports.";
        var poles = rawPoles.Select(s => s.Trim()).ToList();
        // Only an ABSENT or EMPTY list takes the documented default. A list with a blank IN it is a caller error,
        // refused above, rather than a list that quietly becomes something shorter.
        if (poles.Count == 0) poles.Add(SourcePoles.Winner);

        return Render(svc.CopyClosure(fromKey, poles, seeds, exclusions, targetKey, new_editorid, patch, into));
    });

    /// <summary>Render one outcome. Internal so a test can read the sentences without going through the wire.</summary>
    internal static string Render(ClosureCopyOutcome o)
    {
        var sb = new StringBuilder();
        if (!o.Success)
        {
            sb.Append("error: ");
            if (o.WalkRefusal is { } w) sb.Append(RenderWalkRefusal(w, o.SourcesConsulted));
            else if (o.CopyRefusal is { } c) sb.Append(RenderCopyRefusal(c));
            else sb.Append(o.EngineError ?? "the copy could not be completed.");
            // The route sentences are whole refusals that already end with this, so append it only when absent.
            if (!sb.ToString().TrimEnd().EndsWith("Nothing was written.", StringComparison.Ordinal))
                sb.Append("\nNothing was written.");
            return sb.ToString();
        }

        sb.Append(o.Mode == "clone"
            ? $"CLONED {o.SourceKey} -> {o.NewKey}\n"
            : $"COPIED {o.SourceKey}'s walked fields onto {o.NewKey}\n");
        sb.Append(WriteSentences.CopySourcesConsulted(o.SourcesConsulted));
        // Which source produced the record the caller ASKED for. Needed in both modes: the source record is not
        // among the internalized rows in either, so nothing else names where its own body came from.
        if (o.FromArm is { } fromArm)
            sb.Append(WriteSentences.CopyFromArmLead).Append(WriteSentences.CopyArm(fromArm)).Append(".\n");
        sb.Append(WriteSentences.NewOrExtendedArtifact(o.Extended, Path.GetFileName(o.OutPath!), o.Bytes,
            Path.GetFileName(Path.GetDirectoryName(o.OutPath!)!)));

        if (o.ReadBackWarning is not null) sb.Append(WriteSentences.CopyReadBackUnverified).Append('\n');
        else
        {
            sb.Append(WriteSentences.Masters(o.Masters));
            // Order matters: the alarm first whatever the source was, then the transplant note, which holds only
            // when NOTHING was bound — a base-game FormID read through a from_source= naming a mod still binds it.
            sb.Append(o.SourceAmongMasters ? WriteSentences.CopySourceMastered
                      : o.NothingBound ? WriteSentences.CopySourceBaseGame
                      : WriteSentences.CopyStandalone).Append('\n');
        }

        if (o.Copied.Count > 0)
        {
            sb.Append('\n').Append(WriteSentences.CopyInternalizedHeader).Append('\n');
            foreach (var c in o.Copied)
                sb.Append($"  - {c.TypeName} '{c.EditorId ?? "<no editorid>"}'  {c.OldKey} -> {c.NewKey}   (from {c.ArmSpelling}, via {c.PulledBy})\n");
        }
        // Two different facts, kept apart: a link kept because it resolves outside the source masters normally,
        // while an exclusion boundary is INSIDE the source and still points at it.
        var keptOutside = o.Kept.Count(k => !k.Excluded);
        var keptExcluded = o.Kept.Count - keptOutside;
        if (keptOutside > 0)
            sb.Append($"kept {keptOutside} ").Append(WriteSentences.CopyKeptOutside).Append('\n');
        if (keptExcluded > 0)
            sb.Append($"{keptExcluded} ").Append(WriteSentences.CopyKeptExcluded).Append('\n');
        if (o.Cycles.Count > 0)
        {
            sb.Append(WriteSentences.CopyCyclesHeader).Append('\n');
            foreach (var cy in o.Cycles) sb.Append($"  - {cy.Back} is reached again from {cy.PulledBy}\n");
        }
        if (o.Attached.Count > 0)
        {
            sb.Append($"attached: {string.Join(", ", o.Attached.Select(a => $"{a.Field} ({a.Removed})"))}\n");
            // The row above is a bare field name either way, so a cleared field and a copied one are otherwise
            // indistinguishable to the caller.
            if (o.Attached.Any(a => a.Cleared)) sb.Append(WriteSentences.CopySeedClearedNote).Append('\n');
        }
        if (o.AssetPaths.Count > 0)
        {
            sb.Append('\n').Append(WriteSentences.CopyAssetPathsHeader).Append('\n');
            foreach (var a in o.AssetPaths) sb.Append("  - ").Append(a).Append('\n');
        }
        if (o.Stripped.Count > 0)
        {
            sb.Append($"\nSTRIPPED {o.Stripped.Count} reference(s) that still pointed into the source:\n");
            foreach (var s in o.Stripped)
            {
                sb.Append($"  - {s.Field}  (was {s.Removed})\n");
                if (s.WholeProperty) sb.Append(WriteSentences.CopyStripWholeProperty).Append('\n');
            }
            sb.Append(WriteSentences.CopyStripConsequence).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    static string RenderWalkRefusal(WalkRefusal w, IReadOnlyList<SourceArmRef> sources) => w.Kind switch
    {
        WalkRefusalKind.SourceMiss => WriteSentences.CopySourceMiss($"{w.Key} (reached via {w.PulledBy})", sources),
        WalkRefusalKind.SourceFault => WriteSentences.CopySourceFault(
            $"{w.Key}", w.Fault is { } f ? SourceArmRef.Of(f.Arm) : null, w.Detail),
        WalkRefusalKind.UnknownSeedPath =>
            $"{w.Detail}. seed_paths must name link-bearing fields on the record being copied.",
        WalkRefusalKind.NoSeeds => WriteSentences.CopyNoSeeds,
        WalkRefusalKind.UnsupportedSeedShape => w.Detail + WriteSentences.CopySeedShapeRoute,
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
        CopyRefusalKind.UnsupportedSeedShape => c.Detail + WriteSentences.CopySeedShapeRoute,
        CopyRefusalKind.UnwritableTarget => c.Detail + WriteSentences.CopyUnwritableTargetRoute,
        CopyRefusalKind.StopOffOrder =>
            $"the walk pruned a record in '{c.Detail}'. That plugin" + WriteSentences.CopyStopOffOrderRoute,
        CopyRefusalKind.PatchOffOrderLink =>
            $"{c.Field} references {c.Key}, whose plugin '{c.Detail}'" + WriteSentences.CopyPatchOffOrderRoute,
        CopyRefusalKind.CopiedOffOrderLink =>
            $"the copied record {c.Field} references {c.Key}, whose plugin '{c.Detail}'" + WriteSentences.CopyCopiedOffOrderRoute,
        CopyRefusalKind.UnsupportedTargetShape =>
            $"the target {c.Key} ({c.Detail})" + WriteSentences.CopyTargetShapeRoute,
        CopyRefusalKind.DonorLeak when c.Field == ClosureCopy.ExclusionLeakMarker =>
            $"the record {c.Key}" + WriteSentences.CopyLeakFromExclusion,
        CopyRefusalKind.DonorLeak =>
            $"after the copy the destination still references {c.Key} in the source — refusing to write a patch that " +
            "would master it. If the destination deliberately references the source elsewhere, remove that first.",
        CopyRefusalKind.IdExhausted => c.Detail,
        _ => c.Detail,
    };
}

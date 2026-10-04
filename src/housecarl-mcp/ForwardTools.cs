using System.ComponentModel;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>housecarl_forward — copies a named plugin's whole version of a record as an override, over
/// <see cref="LoadOrderService.ForwardRecords"/>. <c>source=</c> resolves a plugin wherever it lives: an active one
/// through the load-order index, one only on disk (a disabled mod, an unticked plugin, an unregistered folder, or a
/// direct path) off its own overlay via <c>LoadOrderService.ResolveOffOrderForwardSource</c>.</summary>
[McpServerToolType]
public static class ForwardTools
{
    /// <summary>into= and in_place=: what happens to a record the destination already carries.</summary>
    const string ForwardReplaces = "A record it already carries is replaced by source's version, keeping the records nested under it, and flagged per record. ";

    [McpServerTool(Name = ToolNames.Forward, Title = "Forward a plugin's version of records as an override"),
     Description(
         "Forward one plugin's version of records as an override: xEdit's \"copy as override into\". " +
         ToolNames.Apply + " edits the load-order winner's fields; this copies source='s whole record unchanged, so " +
         "that plugin's version, not the winner's, becomes the patch's content. Use it to re-assert an earlier mod's " +
         "version over a later override, or to revert a record to vanilla by naming a master (Skyrim.esm, " +
         "Update.esm, …) as source=. It does not edit fields; that is " + ToolNames.Apply + ".\n\n" +
         "What to copy: formids=, all from one source=. Where it lands: a new patch (patch=), an existing houseCARL " +
         "patch (into=), or a plugin's own file (in_place= with acknowledge=); naming two lanes is refused. dry_run= " +
         "previews. How it reads back: readback=, format=, max_chars=.\n\n" +
         "All or nothing: if any record is rejected, the whole call is refused with a reason per record and nothing " +
         "is written.\n\n" +
         "To build on a forwarded copy, forward it, then " + ToolNames.Apply + " into= the same patch: the edits " +
         "apply to the patch's copy, not the load-order winner. To see what a forward would change, read the record " +
         "with " + ToolNames.Records + " (project.form='tree' for every plugin touching it, or 'delta' against a " +
         "versus= reference).")]
    public static string Forward(
        LoadOrderService svc,
        [Description("The records to forward, each 'XXXXXX:Plugin.esp', all copied from source=; to forward from another source into the same patch, call again with into=. Name each record once; a repeat is refused. A record's origin plugin, the filename after the colon, must be active or be the file this call writes. Also takes [\"@<absolute path>\"], a plain list file with one FormID per line, not a result artifact.")]
            string[]? formids = null,
        [Description("The one plugin whose version of every record is copied, e.g. 'Authoria - ATweaks.esp', or a master such as 'Skyrim.esm' to revert to vanilla. It must define or override each record. It can be active or only on disk (a disabled mod's plugin, an unticked one, a folder MO2 never registered), or a full path to any copy: pass the path when several folders hold that filename, or to read a plugin the index excluded as unparseable (refused when named by filename). A source read off disk is named in the reply. Source is not made a master for its own sake: the patch masters the record's origin plugin and whatever the copied record references. When the winner is a plugin a tool regenerates, forward the authored plugin's version and then extend the same patch with " + ToolNames.Apply + " into=.")]
            string? source = null,
        [Description(LaneSentences.PatchDefault + LaneSentences.PatchSuffix)]
            string? patch = null,
        [Description(LaneSentences.IntoLead + ForwardReplaces + LaneSentences.IntoFound)]
            string? into = null,
        [Description("Opt-in: the filename of an active plugin to forward into, in its own file" + LaneSentences.InPlaceAnyPlugin + ForwardReplaces + LaneSentences.InPlaceRewrite + "The records you forward are verified; the rest is not.")]
            string? in_place = null,
        [Description(LaneSentences.Acknowledge)]
            bool acknowledge = false,
        [Description("Resolve every record from source=, copy each in memory, and stop before anything touches disk. Returns what would be forwarded (per record: the source, the winner it would out-rank, the replaced and redundant flags) and the expected masters, or the refusal the real call would give. " + LaneSentences.DryRunLanes)]
            bool dry_run = false,
        [Description("Also return every field of each forwarded record, to check the copy matches the source's without enabling the patch. " + LaneSentences.ReadbackIsTheFile)]
            bool readback = false,
        [Description("'text' (default) or 'json' (the same data). Either way the reply states, per record, what was copied and the winner it will out-rank once enabled; a forward whose version already wins is flagged redundant. " + LaneSentences.Epoch)]
            string? format = null,
        [Description("Character limit on the reply: the forwarded-record rows, then the read-back. " + LaneSentences.MaxCharsCut + ".")]
            int max_chars = 0) => Guard.Tool(ToolNames.Forward, () =>
    {
        // format first, ahead of the unconfigured-MO2 prompt; contract in docs/architecture/write-path.md.
        bool json = Wire.WantsJson(format, out var ferr);
        if (ferr is not null) return ferr;
        if (svc.ConfigPromptOrNull() is { } prompt)
            return json ? JsonWire.RenderError(prompt, null) : prompt;
        string Refuse(string message) => json ? JsonWire.RenderError(message, null) : "error: " + message;

        // ---- LANE: mutually exclusive, and a dropped one is named; contract in docs/architecture/write-path.md ----
        var patchName = string.IsNullOrWhiteSpace(patch) ? null : patch.Trim();
        bool hasPatch = patchName is not null;
        bool hasInto = !string.IsNullOrWhiteSpace(into);
        bool hasInPlace = !string.IsNullOrWhiteSpace(in_place);
        if (hasInto && hasInPlace)
            return Refuse("into= and in_place= are different lanes — into= EXTENDS a houseCARL patch, in_place= rewrites an existing plugin's own file. Name one.");
        if (hasPatch && hasInto)
            return Refuse($"patch='{patch}' names a NEW patch to write, but into='{into}' extends an existing one — the two lanes are exclusive. Drop patch= to extend, or drop into= to write fresh.");
        if (hasPatch && hasInPlace)
            return Refuse($"patch='{patch}' names a NEW patch to write, but in_place='{in_place}' rewrites that plugin's own file — the two lanes are exclusive. Drop patch= to forward in place, or drop in_place= to write a patch.");
        if (acknowledge && !hasInPlace)
            return Refuse("acknowledge= confirms the in-place trade-off and is meaningless without in_place=<plugin filename>. Drop it, or name the file to overwrite.");

        // ---- SELECT + SOURCE ---------------------------------------------------------------------------
        if (string.IsNullOrWhiteSpace(source))
            return Refuse("source= is required — name the plugin WHOSE version of the record(s) to forward (an earlier override to re-assert, or a master like 'Skyrim.esm' to revert to vanilla).");
        if (formids is null || formids.Length == 0)
            return Refuse("formids= is empty — pass the FormID(s) to forward from source, e.g. formids=[\"0012AB:CoolMod.esp\"] (one is a set of one), or [\"@<absolute path>\"] to read the list from a file.");
        var (tokens, demand, _, xerr) = Artifacts.ExpandListInput(formids, "formids");
        if (xerr is not null) return Refuse(xerr.StartsWith("error: ", StringComparison.Ordinal) ? xerr[7..] : xerr);
        if (demand is not null)
            // An artifact's identity column is only valid at the epoch it was captured at, and the write lanes
            // capture inside the engine, so nothing here can re-check it: refuse rather than honour it unverified.
            return Refuse($"formids= names a result ARTIFACT ('{demand.Path}'), whose identity column is only valid at the epoch it was captured at ({demand.Epoch}) — the write lanes don't re-check that yet, and an unchecked artifact must not drive a write. Pass the FormIDs inline, or a plain list file (one FormID per line).");
        var targets = tokens!.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
        if (targets.Count == 0)
            return Refuse("formids= expanded to an empty list — nothing to forward.");

        var outcome = svc.ForwardRecords(targets, source.Trim(), patchName, into, readback, in_place, hasInPlace, acknowledge, dry_run);
        // The lane the CALL named; contract in docs/architecture/write-path.md.
        return json
            ? JsonWire.RenderForwardOutcome(outcome, max_chars, readback, hasInPlace ? "in_place" : hasInto ? "into" : "patch")
            : WriteTools.RenderForward(outcome, max_chars);
    });
}

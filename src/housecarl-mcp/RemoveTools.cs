using System.ComponentModel;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>housecarl_remove — the whole-record removal surface, over
/// <see cref="LoadOrderService.RemoveRecords"/>. <c>formids=</c> is set-valued because the engine present-checks and
/// re-serializes all targets in one all-or-nothing pass; the lane is <c>into=</c> rather than <c>patch=</c> because a
/// removal never creates an artifact, it edits one that already exists.</summary>
[McpServerToolType]
public static class RemoveTools
{
    [McpServerTool(Name = ToolNames.Remove, Title = "Remove whole records (the 2.0 removal surface)"),
     Description(
         "Remove WHOLE records — a literal drop-from-plugin, NOT a flag-as-deleted stub. The counterpart to " +
         ToolNames.Apply + ": where apply ADDS an override into a patch, this drops one OUT of it. ONE surface: what to " +
         "drop (formids=) x WHERE from (the LANE: into= a houseCARL patch | in_place=\"X.esp\") x how it reads back " +
         "(TRANSPORT).\n\n" +
         "WHAT A REMOVAL MEANS. You CANNOT remove a record that lives in a master or another mod; you can only drop " +
         "THIS file's override of it, which makes the load-order winner revert (the file stops touching that " +
         "record). Unused masters are pruned automatically: if a removed record held the file's last reference to a " +
         "master, that master drops from the header on the re-write.\n\n" +
         "Each axis's grammar is on its own parameters:\n" +
         "WHAT — formids=, the set of records to drop and the rule for which ones may be named.\n" +
         "LANE — into= a houseCARL patch | in_place= an existing plugin (opt-in), with acknowledge=. Exactly one " +
         "lane per call: a removal never creates an artifact, it edits one that already exists.\n" +
         "TRANSPORT — format= | max_chars=.\n\n" +
         "To remove a list ENTRY (a keyword, an item, a leveled-list line) rather than a whole record, use " +
         ToolNames.Apply + " with op='Remove' instead. Read first with " + ToolNames.Records + ".")]
    public static string Remove(
        LoadOrderService svc,
        [Description("The record(s) to drop, each 'XXXXXX:Plugin.esp' (6 hex digits, the defining master's filename) — SET-VALUED, so many records drop in ONE re-serialize (one is a set of one). Only a record the LANE FILE itself carries may be named; it reaches records in ANY group (cells, placed references, dialogue, navmesh). ALL-OR-NOTHING (Q3): a FormID the file doesn't carry is REFUSED loud — if ANY target isn't carried, the whole call is refused with per-record reasons and NOTHING is written. A singular owned child (a cell's Landscape, a worldspace's TopCell) is refused unless the records under it are named too — removing it means detaching it from its parent, which takes the records under it with it: name them in the same call so the removal reports every record it drops, or leave this one. Also accepts [\"@<absolute path>\"] to read the same list from a file, one FormID per line.")]
            string[]? formids = null,
        [Description("LANE: filename of the houseCARL patch to remove the records FROM (e.g. 'MyMerge.esp') — the same name you pass to " + ToolNames.Apply + "'s into=. It must be a patch houseCARL created that carries them, either because houseCARL created them there or because a prior apply/forward into= it accumulated them as overrides. Found by the plugin's filename even if you've renamed its MO2 mod folder; for two patches sharing a filename, pass the mod-folder name here instead. Mutually exclusive with in_place=.")]
            string? into = null,
        [Description("LANE (opt-in): the FILENAME OF THE FILE BEING REWRITTEN, e.g. \"CoolWeapons.esp\" — drop the records straight out of ANY existing active plugin, incl. one houseCARL didn't author. Your ORIGINAL file is rewritten; no houseCARL backup or undo (keep your own). It drops only a record the file itself defines or overrides. Mutually exclusive with into=.")]
            string? in_place = null,
        [Description("Confirms the one-time in-place trade-off for the plugin named by in_place= — needed only on the FIRST in-place write to a given plugin (edit, create, remove, OR forward), and not again once one has LANDED — a call that is refused records nothing, so it may be needed again. Without it that first call returns a confirmation prompt instead of writing; re-call with acknowledge=true. Waives the consent to touch your original ONLY; it NEVER skips the absence verify, which confirms on the re-opened file that every record you dropped is actually gone. Meaningless without in_place=, and refused there rather than ignored.")]
            bool acknowledge = false,
        [Description("TRANSPORT: 'text' (default) | 'json' (the same data, machine-readable, accounting in-band). Either way the response states what was removed, the remaining masters, and how many records remain (0 = the file is an inert shell). Every response answered from a build carries the epoch stamp — the identity of the index build this removal's master context came from — spelled epoch=<hex> on 'text', and as an 'epoch' member on 'json'; a refusal that consulted no build carries none.")]
            string? format = null,
        [Description("TRANSPORT: character ceiling on the render; past it trailing rows are dropped with an explicit notice (never silent). 0 = a safe default kept under the host's per-response limit.")]
            int max_chars = 0) => Guard.Tool(ToolNames.Remove, () =>
    {
        // format first, so the unconfigured-MO2 prompt answers a json caller as a document.
        bool json = Wire.WantsJson(format, out var ferr);
        if (ferr is not null) return ferr;
        if (svc.ConfigPromptOrNull() is { } prompt)
            return json ? JsonWire.RenderError(prompt, null) : prompt;
        string Refuse(string message) => json ? JsonWire.RenderError(message, null) : "error: " + message;

        // ---- LANE: exactly one destination, and a dropped one is named ---------------------------------
        bool hasInto = !string.IsNullOrWhiteSpace(into);
        bool hasInPlace = !string.IsNullOrWhiteSpace(in_place);
        if (hasInto && hasInPlace)
            return Refuse($"into='{into}' and in_place='{in_place}' are different lanes — into= drops the records from a houseCARL patch, in_place= rewrites an existing plugin's own file. Name one.");
        if (!hasInto && !hasInPlace)
            return Refuse("no lane named. Removal never creates a new artifact — it edits one that exists: pass into=<a houseCARL patch's filename> to drop the records from that patch, or in_place=<plugin filename> to drop them straight out of an existing plugin (opt-in, rewrites your original).");
        if (acknowledge && !hasInPlace)
            return Refuse("acknowledge= confirms the in-place trade-off and is meaningless without in_place=<plugin filename>. Drop it, or name the file to rewrite.");

        // ---- formids= (set-valued; the @file spelling shared with every other list input) ---------------
        if (formids is null || formids.Length == 0)
            return Refuse("formids= is empty — pass the FormID(s) to drop, e.g. formids=[\"0012AB:CoolMod.esp\"] (one is a set of one), or [\"@<absolute path>\"] to read the list from a file.");
        var (tokens, demand, _, xerr) = Artifacts.ExpandListInput(formids, "formids");
        if (xerr is not null) return Refuse(xerr.StartsWith("error: ", StringComparison.Ordinal) ? xerr[7..] : xerr);
        if (demand is not null)
            // An artifact's identity column is only valid at the epoch it was captured at, and the write lanes
            // capture inside the engine, so nothing here can re-check it: refuse rather than honour it unverified.
            return Refuse($"formids= names a result ARTIFACT ('{demand.Path}'), whose identity column is only valid at the epoch it was captured at ({demand.Epoch}) — the write lanes don't re-check that yet, and an unchecked artifact must not drive a removal. Pass the FormIDs inline, or a plain list file (one FormID per line).");
        var targets = tokens!.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
        if (targets.Count == 0)
            return Refuse("formids= expanded to an empty list — nothing to remove.");

        var outcome = svc.RemoveRecords(targets, hasInto ? into : null, in_place, hasInPlace, acknowledge);
        // The lane the CALL named — stated, not derived from the outcome's flags.
        return json
            ? JsonWire.RenderRemovalOutcome(outcome, max_chars, hasInPlace ? "in_place" : "into")
            : WriteTools.RenderRemoval(outcome, max_chars);
    });
}

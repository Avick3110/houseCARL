using System.ComponentModel;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>housecarl_remove — the whole-record removal surface, over <see cref="LoadOrderService.RemoveRecords"/>.
/// <c>formids=</c> is set-valued because the engine re-serializes every target in one all-or-nothing pass, and the
/// lane is <c>into=</c> rather than <c>patch=</c> because a removal edits an artifact instead of creating one.</summary>
[McpServerToolType]
public static class RemoveTools
{
    [McpServerTool(Name = ToolNames.Remove, Title = "Remove whole records"),
     Description(
         "Remove whole records from a plugin: a literal drop, not a deleted-flag stub; the counterpart to " +
         ToolNames.Apply + ", which adds overrides. A record a master or another mod defines cannot be removed; " +
         "dropping this file's override of it makes the record fall back to the next plugin's version. A master " +
         "the file no longer references drops from its header.\n\n" +
         "What to drop: formids=. Where from: an existing houseCARL patch (into=) or a plugin's own file (in_place= " +
         "with acknowledge=); name exactly one, since a removal edits a file that exists. How it reads back: " +
         "format=, max_chars=.\n\n" +
         "All or nothing: if any record is rejected, the whole call is refused with a reason per record and nothing " +
         "is written.\n\n" +
         "To remove a list entry (a keyword, an item, a leveled-list line) rather than a whole record, use " +
         ToolNames.Apply + " with op='Remove'. Read first with " + ToolNames.Records + ".")]
    public static string Remove(
        LoadOrderService svc,
        [Description("The records to drop, each 'XXXXXX:Plugin.esp'. Only a record the lane's file itself defines or overrides can be named, in any group (cells, placed references, dialogue, navmesh). Removing a record also drops every record nested under it (a cell's placed references, a topic's lines), and the reply lists only the records you named. A record in a parent's single-child slot (a cell's Landscape, a worldspace's TopCell) is refused unless the records under it are named too. Also takes [\"@<absolute path>\"], a file with one FormID per line.")]
            string[]? formids = null,
        [Description("Filename of the houseCARL patch to remove the records from, e.g. 'MyMerge.esp'; it must be a patch houseCARL created, and carry them. " + LaneSentences.IntoFound)]
            string? into = null,
        [Description("Opt-in: the filename of an active plugin to drop the records straight out of" + LaneSentences.InPlaceAnyPlugin + LaneSentences.InPlaceRewrite + "Every record you drop is checked gone on the re-opened file.")]
            string? in_place = null,
        [Description(LaneSentences.Acknowledge)]
            bool acknowledge = false,
        [Description("'text' (default) or 'json' (the same data). Either way the reply states what was removed, the masters left, and how many records remain (0: the file now holds none). " + LaneSentences.Epoch)]
            string? format = null,
        [Description("Character limit on the reply. " + LaneSentences.MaxCharsCut + ".")]
            int max_chars = 0) => Guard.Tool(ToolNames.Remove, () =>
    {
        // format first, ahead of the unconfigured-MO2 prompt; contract in docs/architecture/write-path.md.
        bool json = Wire.WantsJson(format, out var ferr);
        if (ferr is not null) return ferr;
        if (svc.ConfigPromptOrNull() is { } prompt)
            return json ? JsonWire.RenderError(prompt, null) : prompt;
        string Refuse(string message) => json ? JsonWire.RenderError(message, null) : "error: " + message;

        // ---- LANE: exactly one destination, named when dropped; contract in docs/architecture/write-path.md ----
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
        // The lane the CALL named; contract in docs/architecture/write-path.md.
        return json
            ? JsonWire.RenderRemovalOutcome(outcome, max_chars, hasInPlace ? "in_place" : "into")
            : WriteTools.RenderRemoval(outcome, max_chars);
    });
}

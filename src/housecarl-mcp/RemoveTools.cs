using System.ComponentModel;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>
/// housecarl_remove — the 2.0 S1 record-removal surface (tool-surface-2.0 W3 PR 2; SPEC §2.2 ACT, §5.1/§5.2, §6.1).
///
/// Absorbs <c>housecarl_remove_record</c> over the unchanged removal cleave
/// (<see cref="LoadOrderService.RemoveRecords"/> → WritePatchBuilder.RemoveRecords / RemoveRecordsInPlace). Two
/// things change beyond the vocabulary:
/// <list type="bullet">
/// <item><b>Plural by construction</b> — <c>formids=</c> is set-valued (§5.1: one is a degenerate set). The engine
/// has ALWAYS taken a list (present-check + all-or-nothing over every target, one re-serialize); the 1.x tool
/// exposed a single <c>formid=</c>, so dropping ten overrides cost ten rewrites of the same file. This is the
/// "unreachable engine capability recovered" §6.1 names — no engine change, a reachable one.</item>
/// <item><b>The lane is <c>into=</c>, not <c>patch=</c></b> — SPEC §5.1 defines <c>patch</c> as the NEW artifact a
/// write creates, and a removal never creates one: it edits an artifact that already exists, which is exactly what
/// <c>into=</c> names. (§5.3's row folded 1.x's bare <c>patch=</c> in with the <c>patch_name</c> drift instances —
/// a spelling merge, not a role decision; taking it literally would re-create the one-word-two-meanings collision
/// §5.2 exists to kill. The alias layer maps the old <c>patch=</c> onto <c>into=</c> by name.)</item>
/// </list>
/// </summary>
[McpServerToolType]
public static class RemoveTools
{
    [McpServerTool(Name = "housecarl_remove", Title = "Remove whole records (the 2.0 removal surface)"),
     Description(
         "Remove WHOLE records — a literal drop-from-plugin, NOT a flag-as-deleted stub. The counterpart to " +
         "housecarl_apply: where apply ADDS an override into a patch, this drops one OUT of it. ONE surface: what to " +
         "drop (formids=) x WHERE from (the LANE: into= a houseCARL patch | in_place=\"X.esp\") x how it reads back " +
         "(TRANSPORT).\n\n" +
         "WHAT CAN BE REMOVED. Only a record the named file ITSELF carries — one houseCARL created, or an override " +
         "the patch accumulated via a prior apply/forward into= it. You CANNOT remove a record that lives in a " +
         "master or another mod; you can only drop THIS file's override of it, which makes the load-order winner " +
         "revert (the file stops touching that record). A FormID the file doesn't carry is REFUSED loud with " +
         "nothing written (Q3). Reaches records in ANY group (cells, placed references, dialogue, navmesh).\n\n" +
         "formids= is SET-VALUED — drop many records in ONE re-serialize (one is a set of one). ALL-OR-NOTHING " +
         "(Q3): if ANY target isn't carried, the whole call is refused with per-record reasons and NOTHING is " +
         "written. It also accepts [\"@<absolute path>\"] — the same list from a file, one FormID per line.\n\n" +
         "LANE — where the removal happens. into='<a houseCARL patch's filename>' drops the records from that patch " +
         "(the same name you pass to housecarl_apply's into=); it must be a patch houseCARL created. " +
         "in_place='<plugin filename>' is the opt-in lane for ANY existing active plugin, including one houseCARL " +
         "didn't author: your ORIGINAL file is rewritten — no houseCARL backup or undo (keep your own). The FIRST " +
         "in-place write to a given plugin returns a one-time confirmation prompt (re-call with acknowledge=true); " +
         "that consent covers touching your original ONLY — it NEVER skips the absence verify, which confirms on the " +
         "re-opened file that every record you dropped is actually gone. Exactly one lane per call.\n\n" +
         "Unused masters are pruned automatically: if a removed record held the file's last reference to a master, " +
         "that master drops from the header on the re-write. Returns what was removed, the remaining masters, and " +
         "how many records remain (0 = the file is an inert shell). Every response carries epoch=<hex> — the " +
         "identity of the index build this removal's master context came from.\n\n" +
         "To remove a list ENTRY (a keyword, an item, a leveled-list line) rather than a whole record, use " +
         "housecarl_apply with op='Remove' instead. Read first with housecarl_records.")]
    public static string Remove(
        LoadOrderService svc,
        [Description("The record(s) to drop, each 'XXXXXX:Plugin.esp' (6 hex digits, the defining master's filename) — set-valued, so many records drop in ONE re-serialize. Also accepts [\"@<absolute path>\"] to read the same list from a file.")]
            string[]? formids = null,
        [Description("LANE: filename of the houseCARL patch to remove the records FROM (e.g. 'MyMerge.esp') — a patch houseCARL created that carries them. Found by the plugin's filename even if you've renamed its MO2 mod folder; for two patches sharing a filename, pass the mod-folder name here instead. Mutually exclusive with in_place=.")]
            string? into = null,
        [Description("LANE (opt-in): the FILENAME OF THE FILE BEING REWRITTEN, e.g. \"CoolWeapons.esp\" — drop the records straight out of that existing active plugin (incl. one houseCARL didn't author). Your ORIGINAL file is rewritten; no houseCARL backup or undo. It drops only a record the file itself defines or overrides. Mutually exclusive with into=.")]
            string? in_place = null,
        [Description("Confirms the one-time in-place trade-off for the plugin named by in_place= — needed only on the FIRST in-place write to a given plugin (edit, create, remove, OR forward), never again for it. Waives the consent to touch your original ONLY; it NEVER skips the absence verify. Meaningless without in_place=, and refused there rather than ignored.")]
            bool acknowledge = false,
        [Description("TRANSPORT: 'text' (default) | 'json' (the same data, machine-readable, accounting in-band).")]
            string? format = null,
        [Description("TRANSPORT: character ceiling on the render; past it trailing rows are dropped with an explicit notice (never silent). 0 = a safe default kept under the host's per-response limit.")]
            int max_chars = 0) => Guard.Tool("housecarl_remove", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;

        bool json = Wire.WantsJson(format, out var ferr);
        if (ferr is not null) return ferr;
        string Refuse(string message) => json ? JsonWire.RenderError(message, null) : "error: " + message;

        // ---- LANE: exactly one destination, and a dropped one is named (SPEC §2.1) ---------------------
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
            // An artifact's identity column is epoch-BOUND (SPEC §2.1.1): the read tools check it inside the
            // consuming call's own capture, which is the only place the comparison means anything. The write lanes
            // capture inside the engine, so that check has no home here yet — refuse by name rather than honor a
            // list whose freshness nothing verified (a stale identity column driving a REMOVAL is the worst place
            // to find out). A plain list file (one FormID per line) is unaffected.
            return Refuse($"formids= names a result ARTIFACT ('{demand.Path}'), whose identity column is only valid at the epoch it was captured at ({demand.Epoch}) — the write lanes don't re-check that yet, and an unchecked artifact must not drive a removal. Pass the FormIDs inline, or a plain list file (one FormID per line).");
        var targets = tokens!.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
        if (targets.Count == 0)
            return Refuse("formids= expanded to an empty list — nothing to remove.");

        // LANE-as-name maps onto the 1.x service contract: into= IS the patch lane's artifact, in_place= the
        // target+bool pair (§5.2). The service's own exclusivity checks stay as the second line of defence.
        var outcome = svc.RemoveRecords(targets, hasInto ? into : null, in_place, hasInPlace, acknowledge);
        // The lane the CALL named — stated, not derived from the outcome's flags (PR #311 review [medium]).
        return json
            ? JsonWire.RenderRemovalOutcome(outcome, max_chars, hasInPlace ? "in_place" : "into")
            : WriteTools.RenderRemoval(outcome, max_chars, laneAsName: true);
    });
}

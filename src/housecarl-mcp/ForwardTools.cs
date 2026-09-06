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
    [McpServerTool(Name = ToolNames.Forward, Title = "Forward a plugin's version of records as an override"),
     Description(
         "Forward a SPECIFIC plugin's version of one-or-more records as an override — xEdit's \"copy as override " +
         "into\", the INVERSE of " + ToolNames.Apply + ". apply edits the load-order WINNER; this copies source='s whole " +
         "record VERBATIM, so SOURCE's version (not the winner) becomes the patch's content. ONE surface: what to " +
         "copy (formids=) x WHOSE version (source=) x WHERE it lands (the LANE) x how it reads back (TRANSPORT).\n\n" +
         "WHAT IT IS FOR: RE-ASSERT an earlier mod's version over a later override (a late total-overhaul " +
         "re-architected a record an earlier list patch had balanced — forward the earlier plugin's version back on " +
         "top), or REVERT a record to vanilla (name a master — Skyrim.esm/Update.esm/… — as source). This copies the " +
         "record WHOLE: it does NOT edit fields (that's " + ToolNames.Apply + ") and needs no field pre-flight, because a " +
         "complete source record is legal by construction.\n\n" +
         "Each axis's grammar is on its own parameters:\n" +
         "WHAT — formids=, the set of records to copy and the rule for which ones may be named.\n" +
         "WHOSE — source=, the ONE plugin every record in the call is copied from, active or only on disk.\n" +
         "LANE — the default, a NEW patch= | into= an existing houseCARL patch | in_place= an existing plugin " +
         "(opt-in) with acknowledge= | dry_run=. Exactly one lane per call; naming two is refused, never silently " +
         "ignored.\n" +
         "TRANSPORT — readback= | format= | max_chars=.\n\n" +
         "ALL-OR-NOTHING (Q3): one rejected target refuses the WHOLE call, with a reason per record, and NOTHING is " +
         "written; each parameter above carries its own refusals.\n\n" +
         "THE STALE-WINNER BYPASS RECIPE (pinned): forward from the source you want, then " + ToolNames.Apply + " into= the " +
         "same patch — the ops edit the patch's FORWARDED copy and never re-resolve the (stale) load-order winner, " +
         "so you build on the forwarded body directly.\n\n" +
         "To see what a forward would change before writing it, read the record with " + ToolNames.Records +
         " — project.form='tree' for every plugin touching it, or 'delta' against a versus= reference.")]
    public static string Forward(
        LoadOrderService svc,
        [Description("The record(s) to forward, each 'XXXXXX:Plugin.esp' — SET-VALUED, so many records copy in ONE write (one is a set of one), and ALL copied from the SAME source; call again with into= to forward from a different source into the same patch. Name each target ONCE: a repeat is refused rather than copied twice. The record's ORIGIN plugin — the filename after the colon — must be active, or be the very file this call writes: a forward overrides the origin FormKey, so the patch would need that plugin as a master, and a record whose origin is neither is refused by name (source= itself never becomes a master — see there). ALL-OR-NOTHING (Q3): if ANY target is malformed, repeated, or has no version in source, the whole call is refused with per-record reasons and NOTHING is written. Also accepts [\"@<absolute path>\"] to read the same list from a file, one FormID per line; a result ARTIFACT's path is refused instead, because its identity column is only valid at the epoch it was captured at and the write lanes don't re-check that.")]
            string[]? formids = null,
        [Description("SOURCE: the ONE plugin WHOSE version of the record(s) to copy (e.g. 'Authoria - ATweaks.esp', or a master like 'Skyrim.esm' to revert to vanilla). It must DEFINE or override every formid; a record it doesn't touch is refused by name, never silently absent. ACTIVE, or a file that is only ON DISK — a DISABLED mod's plugin, an unticked one, a folder MO2 never registered, or a full path to any copy; pass the full path if several folders provide that filename. Re-asserting a disabled old patch's version is a first-class use of this tool, not an edge case, and an off-order read NAMES the exact file it opened. Forwarding does NOT add source as a master: the patch overrides the record's ORIGIN FormKey with the copied body, so the header carries the origin master + whatever the body references (exactly xEdit's copy-as-override-into-a-new-patch). Refused by name: a plugin found in NEITHER the load order NOR on disk (both places named, with a did-you-mean); a filename several mod folders provide (ambiguous — pass the path to the copy you mean); the file this call is writing itself (forwarding a file into itself is a no-op). A plugin EXCLUDED from the index as unparseable is refused WHEN NAMED — addressing that same file by PATH reads it directly instead, because copying one record out is not the whole-file re-serialize the exclusion guards, and the response says so.")]
            string? source = null,
        [Description("LANE: base filename for the NEW patch this call writes (default 'Patch'); auto-suffixed if taken, so a prior patch is never overwritten — except when '<name>.esp' already exists somewhere your order is not loading it (another mod folder, the overwrite folder, or game Data), which is refused rather than suffixed, naming that place and the file.")]
            string? patch = null,
        [Description("LANE: filename of an EXISTING houseCARL patch to ADD these forwards to instead of writing a fresh one (accumulate across calls — e.g. forward from a different source into the same patch). If the patch already carries a forwarded FormKey, its existing override is REPLACED by source's body — xEdit's copy-as-override overwrite, flagged per record in the report. Refused by name: NO houseCARL patch of that name (no owned folder holds its .esp and none is named for it — the refusal lists the owned patches it could name, or drop into= and pass patch= to write a fresh one); a mod folder of that name houseCARL did NOT create, because extending it would touch a mod houseCARL doesn't own; SEVERAL owned folders carrying that same .esp (ambiguous — each candidate is named, pass the CONTAINING mod-folder name here instead).")]
            string? into = null,
        [Description("LANE (opt-in): the FILENAME OF THE FILE BEING OVERWRITTEN, e.g. \"MyHandmadePatch.esp\" — forward INTO that existing active plugin's own file (incl. one houseCARL didn't author). Your ORIGINAL file is rewritten; no houseCARL backup or undo. A FormKey the target already carries is REPLACED by source's body, the same replace-on-collision semantics as into= and flagged per record. Takes the same acknowledge= consent as the sibling write tools. Refused by name, your file UNTOUCHED: a target that is not an ACTIVE plugin in the load order (name one enabled in MO2, by its plugin filename); one EXCLUDED from this session as unparseable, because houseCARL won't re-serialize a plugin it can't fully parse; a LOCALIZED plugin, which it cannot re-serialize without scrambling the text; and a parent folder that is missing or not writable, rather than degrading to a non-atomic write.")]
            string? in_place = null,
        [Description("Confirms the one-time in-place trade-off for the plugin named by in_place= — needed only on the FIRST in-place write to a given plugin (edit, create, remove, OR forward), and not again once one has LANDED — a call that is refused records nothing, so it may be needed again. Waives the consent to touch your original ONLY; it NEVER skips the record verify. Meaningless without in_place=, and refused there rather than ignored.")]
            bool acknowledge = false,
        [Description("DRY RUN: run the whole real pipeline — resolve every record from source, copy each into the in-memory would-be artifact — and STOP before anything touches disk. Returns what WOULD be forwarded (per record: source, the winner it would out-rank, replace/redundant flags) + the expected masters, or EXACTLY the refusal the real call would give. Works on every lane.")]
            bool dry_run = false,
        [Description("TRANSPORT: also return each forwarded record IN FULL, read back from the written file on disk (every field, deep) — the pre-enable verify that the copy is exactly the source's, WITHOUT enabling the patch in MO2. The written file's content, not load-order truth.")]
            bool readback = false,
        [Description("TRANSPORT: 'text' (default) | 'json' (the same data, machine-readable, accounting in-band). Either way the response states, per record, what was copied and the current winner it will out-rank once enabled — a forward whose version is ALREADY winning is flagged redundant, never silently a no-op. Every response answered from a build carries the epoch stamp — the identity of the index build the sources and the out-ranked winners were resolved from — spelled epoch=<hex> on 'text', and as an 'epoch' member on 'json'; a refusal that consulted no build carries none.")]
            string? format = null,
        [Description("TRANSPORT: character ceiling on the WHOLE render — the forwarded-record rows (each naming its source and the winner it out-ranks) and then the read-back. Past it, trailing rows are dropped with an explicit notice (never silent); the WRITE is unaffected. 0 = a safe default kept under the host's per-response limit.")]
            int max_chars = 0) => Guard.Tool(ToolNames.Forward, () =>
    {
        // format first, so the unconfigured-MO2 prompt answers a json caller as a document.
        bool json = Wire.WantsJson(format, out var ferr);
        if (ferr is not null) return ferr;
        if (svc.ConfigPromptOrNull() is { } prompt)
            return json ? JsonWire.RenderError(prompt, null) : prompt;
        string Refuse(string message) => json ? JsonWire.RenderError(message, null) : "error: " + message;

        // ---- LANE: the three destinations are mutually exclusive, and a dropped one is named ---------------
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
        // The lane the CALL named — stated, not derived from the outcome's flags.
        return json
            ? JsonWire.RenderForwardOutcome(outcome, max_chars, readback, hasInPlace ? "in_place" : hasInto ? "into" : "patch")
            : WriteTools.RenderForward(outcome, max_chars);
    });
}

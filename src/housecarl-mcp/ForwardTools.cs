using System.ComponentModel;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>
/// housecarl_forward — the 2.0 S1 whole-record override surface (tool-surface-2.0 W3 PR 2; SPEC §2.2 ACT,
/// §5.1/§5.2, §6.1).
///
/// Absorbs <c>housecarl_forward_record</c> over the unchanged forward cleave
/// (<see cref="LoadOrderService.ForwardRecords"/> → WritePatchBuilder.ForwardRecords / ForwardRecordsInPlace).
/// Vocabulary: <c>from_plugin</c> becomes <c>source</c> (§5.3 — the SOURCE axis word: WHOSE version), the
/// <c>target=</c>+<c>in_place=true</c> pair becomes <c>in_place="X.esp"</c> (§5.2), <c>patch_name</c> is
/// <c>patch</c>, <c>full_readback</c> is <c>readback</c>; TRANSPORT gains <c>format=json</c> and the §2.1.1 epoch.
///
/// <para><b>The pole's bound, stated not narrowed.</b> §4.2's <c>source</c> pole resolves a plugin wherever it lives
/// — active, or a file on disk out of the order. Forwarding resolves its bodies THROUGH the load-order index
/// (<c>view.GetRecord</c>), so the off-order arm is not reachable on this lane: <c>source=</c> here means an ACTIVE
/// plugin, and a non-active one is refused BY NAME with the reason rather than silently treated as "doesn't define
/// the record". The lifter is the off-order copy-source seam PR #310 landed for <c>CopyFrom</c>
/// (<c>ResolveOffOrderCopySources</c>) — carried as a declared bound, not a quiet narrowing of the axis.</para>
/// </summary>
[McpServerToolType]
public static class ForwardTools
{
    [McpServerTool(Name = "housecarl_forward", Title = "Forward a plugin's version of records as an override"),
     Description(
         "Forward a SPECIFIC plugin's version of one-or-more records as an override — xEdit's \"copy as override " +
         "into\", the INVERSE of housecarl_apply. apply edits the load-order WINNER; this copies source='s whole " +
         "record VERBATIM, so SOURCE's version (not the winner) becomes the patch's content. ONE surface: what to " +
         "copy (formids=) x WHOSE version (source=) x WHERE it lands (the LANE) x how it reads back (TRANSPORT).\n\n" +
         "WHAT IT IS FOR: RE-ASSERT an earlier mod's version over a later override (a late total-overhaul " +
         "re-architected a record an earlier list patch had balanced — forward the earlier plugin's version back on " +
         "top), or REVERT a record to vanilla (name a master — Skyrim.esm/Update.esm/… — as source). This copies the " +
         "record WHOLE: it does NOT edit fields (that's housecarl_apply) and needs no field pre-flight, because a " +
         "complete source record is legal by construction.\n\n" +
         "formids= is set-valued — one or more 'XXXXXX:Plugin.esp', ALL copied from the SAME source; call again with " +
         "into= to forward from a different source into the same patch. It also accepts [\"@<absolute path>\"].\n\n" +
         "source= is an ACTIVE plugin that DEFINES or overrides each record. Forwarding does NOT add source as a " +
         "master: the patch overrides the record's ORIGIN FormKey with the copied body, so the header carries the " +
         "origin master + whatever the body references (exactly xEdit's copy-as-override-into-a-new-patch). A plugin " +
         "on disk but NOT in the load order is refused by name — the bodies here resolve through the load-order " +
         "index, so an off-order version isn't reachable on this lane.\n\n" +
         "LANE — where the write lands. Default: a NEW patch named patch= (auto-suffixed if taken). into='<an " +
         "existing patch's filename>' EXTENDS that patch — and if it ALREADY carries a forwarded FormKey, its " +
         "existing override is REPLACED by source's body (xEdit's copy-as-override overwrite, flagged per record). " +
         "in_place='<plugin filename>' forwards INTO an existing plugin's OWN file (incl. one houseCARL didn't " +
         "author) — same replace-on-collision semantics, your ORIGINAL rewritten, no houseCARL backup or undo, and " +
         "the same one-time acknowledge= consent as the sibling write tools.\n\n" +
         "THE STALE-WINNER BYPASS RECIPE (pinned): forward from the source you want, then housecarl_apply into= the " +
         "same patch — the ops edit the patch's FORWARDED copy and never re-resolve the (stale) load-order winner, " +
         "so you build on the forwarded body directly.\n\n" +
         "ALL-OR-NOTHING (Q3): the whole call is refused with a named reason and NOTHING is written if source isn't " +
         "in the load order, was excluded (unparseable), is the output artifact itself, names a target twice, or " +
         "simply doesn't DEFINE/override a given record (nothing there to forward). dry_run=true resolves every " +
         "record from source, copies each into the in-memory would-be artifact, and STOPS before anything touches " +
         "disk — what WOULD be forwarded, or EXACTLY the refusal the real call would give.\n\n" +
         "Returns, per record, what was copied and the current winner it will out-rank once enabled (a forward whose " +
         "version is ALREADY winning is flagged redundant, never silently a no-op). readback=true additionally " +
         "returns each forwarded record IN FULL off the written file — the pre-enable verification that the copy is " +
         "exactly the source's, WITHOUT enabling the patch in MO2 (the written file's content, not load-order " +
         "truth). format='json' returns the same data machine-readable; max_chars caps the render with an explicit " +
         "notice. Every response carries epoch=<hex> — the identity of the index build the sources and the " +
         "out-ranked winners were resolved from.")]
    public static string Forward(
        LoadOrderService svc,
        [Description("The record(s) to forward, each 'XXXXXX:Plugin.esp' — ALL copied from the SAME source. Set-valued; also accepts [\"@<absolute path>\"] to read the same list from a file.")]
            string[]? formids = null,
        [Description("SOURCE: the plugin filename WHOSE version of the record(s) to copy (e.g. 'Authoria - ATweaks.esp', or a master like 'Skyrim.esm' to revert to vanilla). Must be ACTIVE in the load order and must DEFINE or override each formid.")]
            string? source = null,
        [Description("LANE: base filename for the NEW patch this call writes (default 'Patch'); auto-suffixed if taken, so a prior patch is never overwritten. Mutually exclusive with into= and in_place= — naming both lanes is refused, never silently ignored.")]
            string? patch = null,
        [Description("LANE: filename of an EXISTING houseCARL patch to ADD these forwards to instead of writing a fresh one (accumulate across calls — e.g. forward from a different source into the same patch). If the patch already carries a forwarded FormKey, its existing override is REPLACED by source's body.")]
            string? into = null,
        [Description("LANE (opt-in): the FILENAME OF THE FILE BEING OVERWRITTEN, e.g. \"MyHandmadePatch.esp\" — forward INTO that existing active plugin's own file (incl. one houseCARL didn't author). Your ORIGINAL file is rewritten; no houseCARL backup or undo. A FormKey the target already carries is REPLACED by source's body.")]
            string? in_place = null,
        [Description("Confirms the one-time in-place trade-off for the plugin named by in_place= — needed only on the FIRST in-place write to a given plugin (edit, create, remove, OR forward), never again for it. Waives the consent to touch your original ONLY; it NEVER skips the record verify. Meaningless without in_place=, and refused there rather than ignored.")]
            bool acknowledge = false,
        [Description("DRY RUN: run the whole real pipeline and STOP before anything touches disk. Returns what WOULD be forwarded (per record: source, the winner it would out-rank, replace/redundant flags) + the expected masters, or EXACTLY the refusal the real call would give. Works on every lane.")]
            bool dry_run = false,
        [Description("TRANSPORT: also return each forwarded record IN FULL, read back from the written file on disk (every field, deep) — the pre-enable verify that the copy is exactly the source's. The written file's content, not load-order truth.")]
            bool readback = false,
        [Description("TRANSPORT: 'text' (default) | 'json' (the same data, machine-readable, accounting in-band).")]
            string? format = null,
        [Description("TRANSPORT: character ceiling on the WHOLE render — the forwarded-record rows (each naming its source and the winner it out-ranks) and then the read-back. Past it, trailing rows are dropped with an explicit notice (never silent); the WRITE is unaffected. 0 = a safe default kept under the host's per-response limit.")]
            int max_chars = 0) => Guard.Tool("housecarl_forward", () =>
    {
        // format first, so the unconfigured-MO2 prompt answers a json caller as a DOCUMENT (PR #311 review 5 [low]).
        bool json = Wire.WantsJson(format, out var ferr);
        if (ferr is not null) return ferr;
        if (svc.ConfigPromptOrNull() is { } prompt)
            return json ? JsonWire.RenderError(prompt, null) : prompt;
        string Refuse(string message) => json ? JsonWire.RenderError(message, null) : "error: " + message;

        // ---- LANE: the three destinations are mutually exclusive, and a dropped one is named (SPEC §2.1) ----
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
            // Same bound as housecarl_remove's, for the same reason: an artifact's identity column is epoch-BOUND,
            // and the check only means something inside the consuming call's own capture — which the write lanes
            // take inside the engine. Refused by name rather than honored unchecked.
            return Refuse($"formids= names a result ARTIFACT ('{demand.Path}'), whose identity column is only valid at the epoch it was captured at ({demand.Epoch}) — the write lanes don't re-check that yet, and an unchecked artifact must not drive a write. Pass the FormIDs inline, or a plain list file (one FormID per line).");
        var targets = tokens!.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
        if (targets.Count == 0)
            return Refuse("formids= expanded to an empty list — nothing to forward.");

        // sourceParam: the refusals that name the source pole render THIS tool's word (PR #311 review 4 [low]) — the
        // engine's are shared with the 1.x forward_record, whose pole is still spelled from_plugin=.
        var outcome = svc.ForwardRecords(targets, source.Trim(), patchName, into, readback, in_place, hasInPlace, acknowledge, dry_run,
                                         sourceParam: "source=");
        // The lane the CALL named — stated, not derived from the outcome's flags (PR #311 review [medium]).
        return json
            ? JsonWire.RenderForwardOutcome(outcome, max_chars, readback, hasInPlace ? "in_place" : hasInto ? "into" : "patch")
            : WriteTools.RenderForward(outcome, max_chars, laneAsName: true);
    });
}

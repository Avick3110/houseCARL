using System.ComponentModel;
using HousecarlCore;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary><c>housecarl_check</c> — the merged derived-findings sweep, with <c>findings=</c> selecting the taxonomy.
/// This file holds the merged tool and its orchestration; each family's render lives with the helpers it is assembled
/// out of. The families do not share one scope: <c>plugins=</c> and friends narrow the sweep families, the dialogue
/// family takes seeds. No response carries deprecation prose — see
/// docs/decisions/0005-the-1x-tools-are-deleted-not-deprecated.md.</summary>
[McpServerToolType]
public static class CheckTools
{
    [McpServerTool(Name = ToolNames.Check, ReadOnly = true, Title = "Sweep the load order for derived findings"),
     Description(
         // Only what belongs to no single parameter; each family's grammar and boundary lives on the parameter it
         // is about, because this description is cut at 2,048 characters.
         "Sweeps the load order for derived findings: several finding families in one call, selected by findings=. " +
         "Read-only; resolves against the load-order winners, like every other read. " +
         "Families: 'errors' (load-order integrity), 'scripts' (VMAD script-property binding), 'dialogue' " +
         "(dialogue graph validation over seeded topics and quests) and 'facegen' (the dark-face join: which mod " +
         "wins each NPC's head .nif, which wins its face .dds, which plugin wins the record). findings= takes " +
         "whole families or the classes inside them, and says what each family reports, what it does not, and " +
         "what the default runs (omitted: the errors family alone). " +
         "Scope: errors, scripts and facegen share plugins= (off-order files included) / types= / formids= / " +
         "editorid_contains= / exclude=, plus property_contains= on scripts. The dialogue family takes seeds= " +
         "instead, source= folds one off-order plugin into it, and no plugin scope narrows it. Counts are always " +
         "for the scope actually swept, and the response says so. " +
         "Transport: counts_only= / format= / limit= / max_chars= / to_file=. A listing cut by limit= or " +
         "max_chars= says so per family: how much it carries, why the rest is absent, and which knob moves it. " +
         "Not here: the effective merged INFO order — the sequence the game walks, which line moved and which " +
         "plugin moved it, the answer to 'why does the wrong line play' — is an ordered sequence rather than a " +
         "finding and lives on " + ToolNames.Records + " project='info_order'. To create dialogue lines use " +
         ToolNames.Create + "; to inspect one record use " + ToolNames.Records + ".")]
    public static string CheckTool(
        LoadOrderService svc,
        [Description("Optional. Plugin filenames to sweep (e.g. 'MyMod.esp'); omit to sweep the whole active " +
             "order, which is heavier. A name not in the active order is found on disk in any mod folder (enabled, " +
             "disabled, or not yet listed in MO2) and swept off-order by errors, scripts and facegen: its own " +
             "records, with links resolved against the active order plus the file's own definitions, so a fresh " +
             "patch can be checked before it is enabled. The scripts family's .pex files and facegen's baked head " +
             ".nif/.dds still resolve only through mod folders MO2 has enabled, so a script shipped only inside the " +
             "not-yet-enabled mod reads unverifiable, not clean, and that mod's own facegen files are not seen, so " +
             "its NPCs read as absent bakes. Facegen resolves an NPC's race against the active order only, so NPCs " +
             "of a race the file itself adds are counted as race-unresolved, not classified. Takes [\"@<absolute path>\"] in place of the list, a file with one entry per line, each line read as written; a filename that itself starts with '@' is written '@@' inline.")]
            string[]? plugins = null,
        [Description("Optional. Record types to sweep — signatures ('WEAP') or catalog names ('Weapon'); several types sweep their union. The cheapest scope: records of other types are skipped before any link walk or .pex read.")]
            string[]? types = null,
        [Description("Optional. Sweep only these records ('0BCC84:Skyrim.esm', …) — the re-check after a fix.")]
            string[]? formids = null,
        [Description("Optional. Sweep only records whose EditorID contains this substring (case-insensitive). A record with no EditorID never matches.")]
            string? editorid_contains = null,
        [Description("Optional. Scripts family only: report only findings whose property name contains this substring (case-insensitive). A record left with no matching finding drops out of the listing.")]
            string? property_contains = null,
        [Description("Optional. Plugins to leave out of the errors, scripts and facegen sweeps. Errors and scripts skip " +
             "them entirely: no record walk, no .pex read, no limit= budget. Facegen still walks the NPCs in scope " +
             "and drops one only when every plugin touching it is excluded. It does not narrow the dialogue family. Each value is a plugin " +
             "filename with its extension ('CoolMod.esp') or a group: base_masters (the five the game ships with) or " +
             "implicit (every plugin the order force-loads without a plugins.txt line: Creation Club plugins, " +
             "_ResourcePack.esl, and the base masters). A group member not in this order is dropped. It does not " +
             "change the vanilla baseline the errors family splits out (see limit=), which is always the base-master set. Takes [\"@<absolute path>\"] in place of the list, a file with one entry per line, each line read as written; a filename that itself starts with '@' is written '@@' inline.")]
            string[]? exclude = null,
        [Description("Optional. Which finding families and classes to run. Families: " +
             "'errors', 'scripts', 'dialogue', 'facegen'. Classes: 'dangling', 'missing_masters' (errors); " +
             "'unbound_object' (high), 'unbound_scalar' (medium), 'unbound' (both), " +
             "'bound_null' (advisory) (scripts); 'tint_absent', 'mesh_absent', 'bake_absent', 'split_bake', " +
             "'stale_bake', 'family_split', 'foreign_index', 'inert', 'never_baked' (facegen). The dialogue family " +
             "has no class tokens and requires seeds=. A family token runs every class in it; a class token runs its " +
             "family narrowed to that class; several tokens run each. Default (omitted): the errors family alone, " +
             "and the response names the families that did not run and the findings= spelling that adds them. " +
             "An unscoped scripts sweep takes minutes on a large order, so scope it. Leaving out 'dangling' skips the per-record link walk, so findings=['missing_masters'] is a cheap " +
             "whole-order missing-master check. An excluded class renders as 'not checked', never as 0. Unscannable " +
             "records, scan errors and unverifiable script attachments are always reported and cannot be filtered out. " +
             // ---- family: errors ---------------------------------------------------------------------------
             "Errors family — the data-layer counterpart of the Creation Kit's 'Check For Errors' and xEdit's error check. For each plugin " +
             "in scope it walks every record's links and reports: dangling references (a non-null link whose target " +
             "no plugin in the active order defines); missing masters (a declared master absent from the active " +
             "order); parse failures (records that could not be read, and plugins excluded as unparseable). It does " +
             "not check navmesh or terrain integrity, flag a required field left null (a null link is a legal " +
             "optional), list unused masters, or link-check an owned item's ownership variable (a rank or global). " +
             // ---- family: scripts --------------------------------------------------------------------------
             "Scripts family — for each record carrying a script, it reads the attached script's .pex and every " +
             "script it extends (loose or BSA) and reports: unbound properties, declared but never bound in the " +
             "record's VMAD (an object property is then None at runtime and the code using it silently does " +
             "nothing; a scalar keeps a 0/false/\"\" default that may be wrong); and bound-but-null object properties " +
             "(advisory, sometimes filled at runtime). It checks Auto properties only, not full properties with " +
             "code; an unbound property may be filled at runtime, so a finding is a flag to verify; a script whose " +
             ".pex is not on disk reads unverifiable, never clean. " +
             // ---- family: dialogue -------------------------------------------------------------------------
             "Dialogue family — each seeded topic's graph as the game sees it: the topic is wired to a quest, the " +
             "branch resolves, no INFO.LinkTo target or previous link (PNAM) is dangling (an empty PNAM is normal " +
             "and never flagged), each voiced line's .fuz is on disk, each result script is bound and compiled, " +
             "non-ASCII player-facing text (topic name, prompt, response) is flagged as likely mojibake, each line's " +
             "conditions are checked for some malformed shapes (a dangling form reference, a dead quest-alias " +
             "index, an unset Run On reference, GetIsID on a placed reference), and a Start Game Enabled quest's " +
             ".seq is checked for coverage and staleness. It cannot evaluate whether a well-formed condition passes " +
             "and does not check lip-sync or audio content, so 'checks passed' does not mean 'this will play'. It " +
             "reports and never edits: a stale .seq, a blank subtype marker or a missing CNAM/ENAM is yours to " +
             "fix. Order rules a clean graph must still respect (quest priority for a generic greeting, the PNAM a " +
             "re-listed INFO keeps its place by): " + ReadSentences.DialogueDocUrl + ". " +
             // ---- family: facegen (the dark-face join) -----------------------------------------------------
             "Facegen family — the dark or grey face diagnosis, one row per NPC: formid, editorid, defining " +
             "master, record winner, mesh winner (provider, loose or BSA), tint winner, class and a fix sentence. " +
             "A dark face is a mismatch between two precedences, the MO2 file winner of the two baked files and " +
             "the plugin winner of the record, which xEdit does not show as a conflict. Classes: 'tint_absent' " +
             "(the mesh wins, the .dds has no provider), 'mesh_absent' (the reverse), 'bake_absent' (needs a bake " +
             "and has neither file), 'split_bake' (both win, from different products), 'stale_bake' (a same-source " +
             "pair whose winning record differs from the facegen owner's plugin on the seven face fields; HairColor " +
             "alone is not a flag), 'family_split' (both win, from one product's two mods or a repack of its own " +
             "archive; benign, counted in the header and listed only under its own token), 'foreign_index' (a " +
             "file with the same local id but a different load-order index byte), 'inert' (the file's key resolves " +
             "to a placed reference, no record, a plugin not in the order, or a malformed filename; named and " +
             "dropped, not a face bug), 'never_baked' (neither file, for the Player or a CharGen preset, which the " +
             "Creation Kit never bakes; counted in the header and listed only under its own token). It covers " +
             "every NPC_ in scope that needs a bake plus every facegen file on disk whose key resolves to nothing. " +
             "An NPC whose template carries the Traits flag has no bake of its own: excluded and counted, never " +
             "flagged. The file classes (inert, foreign_index) are reported only on an unscoped sweep; under " +
             "plugins=, exclude= or a record scope they are out of scope, not orphaned. It reports which files and " +
             "records win, never the render: it cannot read a .dds's pixels or bake geometry, so a clean row is " +
             "not a promise the face looks right. Not this family: a purple or white face (a missing texture), " +
             "player-only grey (RaceMenu), a brown weight face (save-baked weight), or an appearance distributed " +
             "at runtime by SPID. Causes and repairs: " + ReadSentences.FaceGenDocUrl + ".")]
            string[]? findings = null,
        [Description("Optional. true = only the header totals and each family's histograms, no per-plugin or per-record listing. Errors: dangling refs by target plugin (the absent dependency behind many findings) and by source plugin (vanilla baseline against what your mods introduced). Scripts: unbound by property name. Dialogue: the totals and the unreachable seeds, no per-topic blocks. Facegen: by class, and by owning mod (the mod that wins the bake, not the plugin that wins the record). Totals stay exact; limit= caps histogram rows instead. The cheap before/after comparison around a fix.")]
            bool counts_only = false,
        [Description("Optional. 'text' (default) or 'json' — the same data sectioned per family, with the totals/capped/truncated accounting in the document.")]
            string? format = null,
        [Description("Optional. Max findings listed per family (default 1000); the true totals are always " +
             "reported. Over the cap the response says so, and the errors family names the plugins that lost the " +
             "most entries, with a count each, and how many lost any. Each family has one listing, filled plugin by plugin and type by type, so under several " +
             "types= any of them can be short; the response names the knob that cut it (limit= or max_chars=), and " +
             "a type absent from a short listing is unlisted, not clean. Errors family: the base-game masters' " +
             "permanent vanilla dangling refs are split out of the total, and limit= is spent on every other plugin " +
             "first. Master-table findings and unverifiable notes are outside the cap; on the scripts family a note " +
             "repeating one already reported for the same script class is collapsed to a count (a note naming no " +
             "script class is never collapsed). A script-heavy plugin's listing can overflow the reply even under " +
             "limit=, which caps findings, not the record roster: use counts_only=true or a record scope. Under counts_only=true it caps histogram rows. For the " +
             "dialogue family it caps how many seeds one call expands, and the response names how many it did not reach.")]
            int limit = 1000,
        [Description("Optional. Dialogue family only, and required by it: the topics and quests to validate, as " +
             "FormIDs ('0F1AC1:Skyrim.esm'). A DIAL validates one topic; a QUST validates every topic that quest " +
             "owns, plus the quest's own subrecords and its .seq once; a DLVW or DLBR gets a record-level check " +
             "(a bare DLVW crashes the Creation Kit's Dialogue Views editor). plugins=/types=/formids=/" +
             "editorid_contains=/exclude= do not scope it.")]
            string[]? seeds = null,
        [Description("Optional. Dialogue family only: one plugin not in the active load order, folded in where " +
             "MO2 would load it — the end of the order for a regular plugin, after the last master for a .esm/.esl " +
             "or an ESM-flagged one, and at its own slot when the order already carries the filename (a shadowed " +
             "copy is added at that slot rather than replacing the file, so a record only the active copy holds " +
             "still reads from it, and the answer says so). A filename (\"MyPatch.esp\"), or {\"file\": " +
             "\"MyPatch.esp\", \"mod\": \"<mod folder>\"} when two mod folders ship the same name. Seeds are " +
             "validated against the active order's winners plus that file, and the file's records win; seeds= may " +
             "name its new records ('000800:MyPatch.esp'). The answer is a projection of the check once the file is " +
             "enabled, except its .fuz/.pex/.seq files, which resolve only through mod folders MO2 has enabled.")]
            System.Text.Json.JsonElement? source = null,
        [Description("Optional. Write the complete findings of every family that ran to this absolute .jsonl " +
             "path (line 1 = manifest) and render only the manifest inline, as " + ToolNames.Records + " does; the " +
             "file re-enters via formids=[\"@<path>\"]. One file with 'family' and 'class' columns; a column a " +
             "family does not use is null. The rows are the sweep's findings, so the inline character budget cuts " +
             "nothing; what limit= cut is cut here too, and the manifest shows total above row_count. Refused with " +
             "counts_only=true, but only after the sweep has run, so do not pair them.")]
            string? to_file = null,
        [Description("Optional. Max characters before the response stops with a notice. 0 = the server default (80,000). The budget is divided among the families that ran and their parts, not spent in series. Raise it for a quest that owns many topics.")]
            int max_chars = 0) => Guard.Tool(ToolNames.Check, () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        bool json = Wire.WantsJson(format, out var fmtErr);
        if (fmtErr is not null) return fmtErr;
        if (!SweepFamilySelection.TryParse(findings, out var selection, out var famErr)) return Wire.Refuse(json, "error: " + famErr);
        int lim = limit <= 0 ? 1000 : limit;

        // plugins= and exclude= are plugin-filename lists, so they take the @file spelling housecarl_records' scope takes.
        string? pluginsEcho = null;
        if (plugins is { Length: > 0 })
        {
            var (names, echo, perr) = Artifacts.ExpandPluginList(plugins, "plugins");
            if (perr is not null) return Wire.Refuse(json, perr);
            plugins = names; pluginsEcho = echo;
        }
        if (exclude is { Length: > 0 })
        {
            var (names, _, xerr) = Artifacts.ExpandPluginList(exclude, "exclude");
            if (xerr is not null) return Wire.Refuse(json, xerr);
            exclude = names;
        }

        // What every family agrees is malformed, checked before any is dispatched and rendered through the normal
        // refusal path so format='json' still gets a document. Syntax refuses here, scope matching stays family-local.
        if (SweepSharedInput.Error(svc, plugins, types, formids, editorid_contains, exclude) is { } inputErr)
        {
            var refusal = new CheckSweep(selection, SharedInputError: inputErr);
            return json ? JsonWire.RenderCheck(refusal, max_chars, lim) : CheckTextRender.RenderCheck(refusal, max_chars, lim);
        }

        // ---- the dialogue family's off-order fold ------------------------------------------------------
        // Resolved through the same one-pole probe every off-order address takes, before any family runs.
        RecordReads.PoleInfo? dialogueFold = null;
        if (source is { } srcEl && srcEl.ValueKind is not (System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined))
        {
            if (!selection.Ran.Contains(SweepFamily.Dialogue))
                return Wire.Refuse(json, "error: source= folds an off-order plugin into the DIALOGUE family's resolution, and this call runs no dialogue family — add findings=[\"dialogue\"] with seeds=, or drop source=. The swept families (errors, scripts, facegen) take an off-order plugin on plugins= instead, which sweeps that file's own records.");
            if (selection.Ran.Count > 1)
                return Wire.Refuse(json, "error: source= folds an off-order plugin into the DIALOGUE family's resolution, and it is the only family with that arm — the swept families beside it in this call (errors, scripts, facegen) take an off-order plugin on plugins= instead, which sweeps the file's own records. Run findings=[\"dialogue\"] with source= on its own, and the swept families in their own call.");
            if (ParseFoldPole(srcEl, out var foldPlugin, out var foldMod) is { } poleErr) return Wire.Refuse(json, poleErr);
            var probe = svc.ProbeSourceArm(foldPlugin!, foldMod, out var probeErr);
            if (probeErr is not null) return Wire.Refuse(json, "error: " + probeErr);
            if (probe!.InOrder)
                return Wire.Refuse(json, $"error: source='{probe.Plugin}' is ACTIVE in the load order, and the dialogue family already validates against the active order's winners — its records are what the check reads. Drop source=; it folds a plugin that is NOT enabled in MO2 into that resolution.", probe.Stamp);
            dialogueFold = probe;
        }

        // The swept families take the same plugins= list whole, sharing ONE memo: one read of the profile's composition
        // and one off-order split, so they cannot disagree about which names resolved.
        var offOrderMemo = new SweepOffOrderMemo();
        // The order every family below answers from, captured once so the response root can say whether it had lost
        // plugins once for the whole call, rather than leaving the fact to whichever families ran (#353).
        var order = svc.CaptureView().Stamp;
        ErrorCheckResult? errors = null;
        ScriptCheckResult? scripts = null;
        if (selection.Ran.Contains(SweepFamily.Errors))
            errors = svc.CheckErrors(plugins, lim, formids, editorid_contains, types,
                                     SweepFindings.Tokens(selection.ErrorClasses), counts_only, exclude, offOrderMemo);
        if (selection.Ran.Contains(SweepFamily.Scripts))
            scripts = svc.ValidateScripts(plugins, lim, formids, editorid_contains,
                                          types, property_contains, SweepFindings.Tokens(selection.ScriptClasses),
                                          counts_only, exclude, offOrderMemo);
        FaceGenCheckResult? facegen = null;
        if (selection.Ran.Contains(SweepFamily.Facegen))
            facegen = svc.CheckFaceGen(plugins, lim, formids, editorid_contains, types,
                                       FaceGenTokens(selection.FaceGenClasses), counts_only, exclude, offOrderMemo);
        DialogueCheckResult? dialogue = null;
        if (selection.Ran.Contains(SweepFamily.Dialogue))
            // Its own scope, not the plugins= list, since this family selects records. With no seeds it raises the
            // cost refusal rather than widening to the whole order.
            dialogue = svc.CheckDialogue(seeds, lim, counts_only, dialogueFold);

        // One call, one build: the root marker is a RESPONSE-level claim about the order every family answered from,
        // so a freshness rebuild between the captures is compared here and refused loud (#353).
        string? Seam(string? familyEpoch, string family) =>
            familyEpoch is not null && familyEpoch != order.Epoch
                ? $"the load order changed while this check was running (epoch={order.Epoch} when the call started, " +
                  $"epoch={familyEpoch} when the {family} family answered) — the response would describe two " +
                  "builds. Retry the call."
                : null;
        // The dialogue family is in the seam too, and a fold makes it the one that most needs to be: the file is
        // probed against one build and folded into another.
        string? foldSeam = dialogueFold?.Epoch is { } foldEpoch && dialogue?.Epoch is { } dialogueEpoch
                        && foldEpoch != dialogueEpoch
            ? $"the load order changed between resolving '{dialogueFold.Plugin}' as off-order (epoch={foldEpoch}) and "
              + $"validating against it (epoch={dialogueEpoch}) — that file may now be IN the order, and the fold "
              + "would describe a different world. Retry the call."
            : null;
        if ((Seam(errors?.Epoch, "errors") ?? Seam(scripts?.Epoch, "scripts")
             ?? Seam(facegen?.Epoch, "facegen") ?? Seam(dialogue?.Epoch, "dialogue") ?? foldSeam) is { } seam)
        {
            var torn = new CheckSweep(selection, OrderSeamError: seam);
            return json ? JsonWire.RenderCheck(torn, max_chars, lim) : CheckTextRender.RenderCheck(torn, max_chars, lim);
        }

        var sweep = new CheckSweep(selection, errors, scripts, dialogue, facegen, Order: order);

        // to_file=: the rows ARE the file, so the response is the manifest and each family's boundary. Refused
        // beside counts_only=, which returns the histograms and no rows for the file to hold.
        if (to_file?.Trim() is { Length: > 0 } path)
        {
            // The same validator the records surface runs: absolute, .jsonl, and outside the pruned results
            // directory.
            if (Artifacts.ValidateToFile(path, svc.ResultsDir) is { } verr) return Wire.Refuse(json, verr);
            if (counts_only)
                return Wire.Refuse(json, "error: counts_only= returns the histograms with no findings, and to_file= "
                                       + "writes the findings - the two contradict; drop one.");
            var query = new[]
            {
                new KeyValuePair<string, string>("findings", string.Join(",", selection.Ran.Select(SweepFamilySelection.Token))),
                new KeyValuePair<string, string>("plugins", plugins is { Length: > 0 } ? pluginsEcho ?? string.Join(",", plugins) : "<whole order>"),
                new KeyValuePair<string, string>("limit", lim.ToString()),
            };
            // No family answered, so nothing is written and the sweep renders as it would without to_file=, which is
            // where every refusal's ground is already stated.
            if (CheckOutcome.For(sweep).Ran.Count == 0)
                return json ? JsonWire.RenderCheck(sweep, max_chars, lim) : CheckTextRender.RenderCheck(sweep, max_chars, lim);
            var (spill, artErr) = CheckArtifact.Write(sweep, path, query);
            if (artErr is not null) return Wire.Refuse(json, "error: " + artErr);
            return CheckArtifact.RenderManifestOnly(sweep, spill!, json, Wire.Cap(max_chars));
        }

        return json ? JsonWire.RenderCheck(sweep, max_chars, lim) : CheckTextRender.RenderCheck(sweep, max_chars, lim);
    });

    /// <summary>Parse the dialogue fold's address — the off-order half of the <c>records</c> pole grammar, which is
    /// all this parameter takes: a filename, or {"file", "mod"} to tell two copies apart. Returns the named refusal,
    /// or null with the parts filled.</summary>
    static string? ParseFoldPole(System.Text.Json.JsonElement el, out string? plugin, out string? mod)
    {
        plugin = null; mod = null;
        if (el.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            plugin = el.GetString()!.Trim();
            return plugin.Length == 0
                ? "error: source= is blank — name the off-order plugin to fold in (e.g. \"MyPatch.esp\")."
                : null;
        }
        if (el.ValueKind == System.Text.Json.JsonValueKind.Object
            && el.TryGetProperty("file", out var f) && f.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            plugin = f.GetString()!.Trim();
            // The same guard the string form has, so a blank name gets the one sentence saying what to pass.
            if (plugin.Length == 0)
                return "error: source= names a blank file — name the off-order plugin to fold in (e.g. {\"file\": \"MyPatch.esp\", \"mod\": \"<mod folder>\"}).";
            mod = el.TryGetProperty("mod", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String
                ? m.GetString()!.Trim() : null;
            return null;
        }
        return el.ValueKind == System.Text.Json.JsonValueKind.Array
            ? $"error: source= folds ONE off-order plugin into the order, and this names {el.GetArrayLength()} — two files have no order between them until MO2 sorts them. Fold one file per call."
            : "error: source= is the off-order plugin to fold in: a filename (\"MyPatch.esp\") or {\"file\": \"MyPatch.esp\", \"mod\": \"<mod folder>\"} when two mod folders ship that name.";
    }

    /// <summary>The facegen class tokens a parsed selection spells, so the tool hands the service the same
    /// vocabulary a caller writes rather than a second representation of it.</summary>
    static string[] FaceGenTokens(FaceGenFindingClass c)
        => c == FaceGenFindingClass.All ? Array.Empty<string>()
         : FaceGenCheck.Registered.Where(r => c.HasFlag(r)).Select(FaceGenCheck.Token).ToArray();
}

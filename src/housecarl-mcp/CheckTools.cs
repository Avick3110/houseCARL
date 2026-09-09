using System.ComponentModel;
using HousecarlCore;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>
/// <c>housecarl_check</c> — the merged derived-findings sweep: the error, script-binding and dialogue findings in
/// one call, with <c>findings=</c> selecting the taxonomy.
///
/// <para>The single-family tools stay registered alongside it; the retired-name rows for their names are in
/// <see cref="AliasTable"/> and stay dormant while those names still resolve. No response carries deprecation
/// prose.</para>
///
/// <para>This file holds the merged tool and the orchestration it needs — which families to run, and what scope
/// each can take. The per-family sweeps are the existing service calls, and each family's render lives with the
/// helpers it is assembled out of: the sweep families' in their transports, the dialogue family's in
/// <see cref="DialogueSweepRender"/>.</para>
///
/// <para>The families do not share one scope. The two sweep families take plugins and records; the dialogue family
/// takes seeds, so <c>plugins=</c> / <c>exclude=</c> and friends narrow the first two and not the third. The
/// dialogue section says so rather than one parameter being given two meanings, and an unseeded dialogue call is
/// refused on cost rather than widened to the whole order.</para>
/// </summary>
[McpServerToolType]
public static class CheckTools
{
    [McpServerTool(Name = ToolNames.Check, ReadOnly = true, Title = "Sweep the load order for derived findings"),
     Description(
         // Only what belongs to no single parameter: the purpose, the family roster, one line per axis naming its
         // parameters, and the cross-tool pointers. Each family's own grammar, cost and boundary lives on the
         // parameter it is about — those arrive whole, this is cut at 2,048 characters.
         "DERIVED-FINDINGS SWEEP over the load order — one call, several finding FAMILIES, selected by findings=. " +
         "Read-only; writes nothing. Resolves against the load-order WINNERS, like every other read. " +
         "ONE surface: which FAMILIES run (findings=) x what they are run over (the SCOPE) x how it reads back " +
         "(TRANSPORT). " +
         "FAMILIES: 'errors' (load-order integrity), 'scripts' (VMAD script-property binding), 'dialogue' " +
         "(dialogue graph validation over SEEDED topics and quests) and 'facegen' (the dark-face join: which mod " +
         "wins each NPC's head .nif, which wins its face .dds, which plugin wins the record). findings= takes " +
         "whole families or the classes " +
         "inside them, and carries what each family reports, what it does NOT, and what the default runs — omitted, " +
         "it runs the errors family alone. " +
         "SCOPE: the three SWEPT families share one — plugins= (off-order files included) / types= / formids= / " +
         "editorid_contains= / exclude=, plus property_contains= on the scripts family. The dialogue family is " +
         "SEEDED instead: seeds= names what to validate, and no plugin scope narrows it. Narrowing narrows the " +
         "COUNTS too: they are always the counts for the scope actually swept, and the response says so. " +
         "TRANSPORT: counts_only= / format= / limit= / max_chars= / to_file=. Results cap at limit= and max_chars, both " +
         "overruns explicit and per family: the response states how much of each family's listing it carries, why " +
         "the rest is absent, and which knob moves it. " +
         "NOT HERE: the effective merged INFO order — the sequence the game walks, which line MOVED and which " +
         "plugin moved it, the answer to 'why does the wrong line play' — is an ordered sequence rather than a " +
         "finding and lives on " + ToolNames.Records + " project='info_order'. To CREATE dialogue lines use " +
         ToolNames.Create + "; to inspect one record use " + ToolNames.Records + ".")]
    public static string CheckTool(
        LoadOrderService svc,
        [Description("Optional. Plugin filenames to sweep (e.g. 'MyMod.esp'). A name not in the active order is " +
             "resolved on disk — any mod folder, enabled, disabled, or not yet listed in MO2 (a fresh houseCARL " +
             "patch, a disabled mod) — and swept OFF-ORDER by BOTH swept families, errors and scripts; found " +
             "nowhere (or in several folders) it is an error. OFF-ORDER means its own records, with links resolved " +
             "against the active order PLUS the file's own definitions: the pre-enable verify sweep for a patch " +
             "houseCARL just wrote. On the SCRIPTS family the .pex chain is still read from the ACTIVE order, so a " +
             "script shipped only inside the not-yet-enabled mod reads UNVERIFIABLE rather than clean. Omit to " +
             "sweep the WHOLE active order — thorough but heavier; scope to one plugin for a fast, focused check " +
             "like the CK's per-plugin 'Check For Errors'.")]
            string[]? plugins = null,
        [Description("Optional. Record types to sweep — signatures ('WEAP') or catalog names ('Weapon'); one type is a set of one, and the sweep is the sweep over their UNION with the findings merged. Applied at the record STREAM, so it is the CHEAPEST scope: skipped records cost nothing (no link walk, no .pex chain read), and a two-type sweep costs the two type groups, not the order. An unknown type is refused by name, naming what is expected.")]
            string[]? types = null,
        [Description("Optional. Sweep ONLY these records ('0BCC84:Skyrim.esm', …) — the re-check-these-few pass after a fix, which limit= cannot do. A malformed token refuses the call before the sweep runs.")]
            string[]? formids = null,
        [Description("Optional. Sweep only records whose EditorID contains this substring (case-insensitive). A record with no EditorID never matches.")]
            string? editorid_contains = null,
        [Description("Optional. The SCRIPTS family only: report only findings whose PROPERTY NAME contains this substring (case-insensitive) — chasing one property across a plugin. A record left with no matching finding drops out of the listing entirely.")]
            string? property_contains = null,
        [Description("Optional. Plugins to leave OUT of the sweep entirely — they cost no record walk, no .pex read " +
             "and no limit= budget, in every SWEPT family. (The dialogue family is seeded, not swept: it takes seeds=, " +
             "and no plugin-scope parameter narrows it — its own section says so.) Each value is either a plugin filename WITH its extension " +
             "('CoolMod.esp') or one of two group names: base_masters (the five the game ships with) or implicit " +
             "(every plugin the order force-loads because plugins.txt does not list it — this is where Creation Club " +
             "plugins and _ResourcePack.esl are, and it INCLUDES the base masters). A value that is neither is " +
             "refused before the sweep runs, whichever families you selected. A FILENAME YOU NAMED that nothing " +
             "in scope matches is refused: the swept families share one scope — the plugins you named, off-order " +
             "files included — so an unmatched name is a typo on every family that could have run, and an " +
             "exclusion that removes the whole scope is refused too rather than sweeping nothing in silence. " +
             "A group member that is not in this order " +
             "is the ordinary case and is simply dropped. This does not change what " +
             "counts as the vanilla BASELINE the errors family splits out (see limit=) — that is always Mutagen's own base-master set.")]
            string[]? exclude = null,
        [Description("Optional. Which finding FAMILIES and CLASSES to look for, in one vocabulary. Families: " +
             "'errors', 'scripts', 'dialogue', 'facegen'. Classes inside them: 'dangling', 'missing_masters' (errors); " +
             "'unbound_object' (HIGH — the silent-None footgun), 'unbound_scalar' (MEDIUM), 'unbound' (both), " +
             "'bound_null' (advisory) (scripts); 'tint_absent', 'mesh_absent', 'bake_absent', 'split_bake', " +
             "'stale_bake', 'family_split', 'foreign_index', 'inert' (facegen). The DIALOGUE family has no class token — it narrows by seeds=, " +
             "which it requires. A family token means every class in it; a class token runs its family narrowed to " +
             "that class; naming several runs each. DEFAULT (omitted) = the ERRORS family alone, and the response " +
             "STATES which families ran, which registered families did not, and the exact findings= spelling that " +
             "adds them — the default narrows only because the response says so. It cannot be every family: an " +
             "unscoped scripts sweep is ~8 minutes on a 3800-plugin order (measured), and an unscoped dialogue " +
             "sweep is refused outright (see seeds=). Excluding 'dangling' SKIPS the per-record link walk " +
             "entirely — that is how you ask 'is any master missing anywhere in my order' without paying for a full " +
             "sweep. An excluded class renders as 'not checked', never as 0. Unscannable records, scan errors and " +
             "unverifiable script attachments are ALWAYS reported and cannot be filtered out (a suppressed 'could " +
             "not read' would read as a clean result). " +
             // ---- family: errors (harvested from housecarl_check_errors) -------------------------------
             "ERRORS FAMILY — the data-layer twin of the Creation Kit's 'Check For Errors' / xEdit's error check. " +
             "For each plugin in scope it walks every record's FormLinks and reports three classes: (1) DANGLING " +
             "references — a non-null link whose target NO plugin in the ACTIVE order defines; (2) MISSING MASTERS " +
             "— a master a plugin DECLARES that is not present in the active order (the most common load-order " +
             "break); (3) PARSE failures — records houseCARL/Mutagen could not read, plus whole plugins the index " +
             "excluded as unparseable. BOUNDARY (never a silent claim of more — Q3): it covers the " +
             "FormLink-resolution / missing-master / parse class. It does NOT verify navmesh or terrain spatial " +
             "integrity (CRC/grid — a Mutagen-delta residual), does NOT flag a required field left null (a null " +
             "FormLink is a legal optional, not an error), does NOT list unused-master cleanup (a FormLink scan " +
             "cannot prove a master is unused), and does NOT link-check an owned item's ownership 'variable' word " +
             "(a rank/global Mutagen cannot type on an override without a link cache). " +
             // ---- family: scripts (harvested from housecarl_validate_scripts) --------------------------
             "SCRIPTS FAMILY — catches the silent-None footgun a byte-valid plugin hides: a record whose attached " +
             "Papyrus script DECLARES a property (e.g. 'Spell Property CallVesyraPower Auto') the record's script " +
             "data (VMAD) never BINDS, so at runtime it is None and the code that uses it no-ops while the log " +
             "looks clean (the maximally-misleading 'the function ran, the effect is absent' class — the same as " +
             "the Creation Kit's auto-add-property bug). For each record carrying a script it reads the attached " +
             "script's compiled .pex — and every script it EXTENDS — from the load order (loose or BSA), and " +
             "reports: (1) UNBOUND properties declared but not bound (an object/form type ⇒ None ⇒ the silent " +
             "no-op, ranked first; an uninitialized scalar ⇒ a 0/false/\"\" default that may be wrong); (2) " +
             "BOUND-BUT-NULL object properties (advisory — sometimes filled at runtime). BOUNDARY: it checks Auto " +
             "(CK-editable) properties only, not code-driven full properties; 'unbound may be intentional' (a " +
             "runtime-filled link), so a finding is a flag to VERIFY; and if a script's .pex is not on disk " +
             "(uncompiled / not in the order) the attachment is reported UNVERIFIABLE, never passed clean. It has " +
             "the SAME off-order lane as the errors family (see plugins=), so a fresh patch's bindings can be " +
             "checked BEFORE it is enabled. " +
             // ---- family: dialogue (harvested from housecarl_validate_dialogue) ------------------------
             "DIALOGUE FAMILY — a topic's whole graph as the GAME sees it, and it is SEEDED, not swept (see " +
             "seeds=). It checks what houseCARL CAN verify at the data layer: the topic is wired to a quest, the " +
             "branch resolves, the INFO.LinkTo conversation chain has no dangling targets, " +
             "and no previous-link (PNAM) is dangling — an EMPTY PNAM is NORMAL (vanilla selects among a topic's " +
             "lines by their conditions, not a previous-link chain), so absence is never flagged; each voiced " +
             "line's .fuz is on disk and each result script is bound + compiled; non-ASCII characters in the " +
             "player-facing text (topic name, line prompt, response text) are flagged as likely in-game MOJIBAKE " +
             "(the CK/Papyrus surface is Windows-1252/ASCII); each line's CTDA conditions are statically checked " +
             "for a meaningful subset of MALFORMED shapes (a dangling form reference, a dead quest-alias index, an " +
             "unset Run On reference, GetIsID pointed at a placed instance); and a Start-Game-Enabled quest's .seq " +
             "is checked for coverage and staleness (without it the quest is dormant on a fresh save and its " +
             "dialogue never shows). BOUNDARY: it cannot EVALUATE whether a WELL-FORMED condition passes — only " +
             "the running game can — and it does not check lip-sync or audio content, so 'checks passed' never " +
             "reads as 'this will play'. The dialogue family FLAGS, never silently rewrites: a stale .seq, a " +
             "blank subtype marker or a missing CNAM/ENAM is reported for you to fix, and nothing in the checked " +
             "plugin is touched. The ORDER rules a clean graph still has to respect — quest priority deciding " +
             "which topic a generic greeting reaches, and the PNAM a re-listed INFO carries to keep its place — " +
             "are at " + ReadSentences.DialogueDocUrl + ". " +
             // ---- family: facegen (the dark-face join) -------------------------------------------------
             "FACEGEN FAMILY - the dark/grey-face diagnosis, as ONE row per NPC: its formid, editorid, defining " +
             "master, RECORD winner, MESH winner (provider + loose/BSA), TINT winner, the mismatch CLASS and a fix " +
             "sentence. A dark face is the DESYNC between two independent precedences - the MO2 VFS decides the two " +
             "baked files, plugin load order decides the record - which is exactly why xEdit shows no conflict. " +
             "CLASSES: 'tint_absent' (the mesh wins, the .dds has no provider anywhere), 'mesh_absent' (the " +
             "mirror), 'bake_absent' (the NPC needs a bake and has NEITHER half), 'split_bake' (both win, from " +
             "different products), 'stale_bake' (a clean same-source pair whose WINNING record disagrees with the " +
             "facegen owner's plugin on the seven face fields; HairColor alone does not touch the bake and is not " +
             "a flag), 'family_split' (both win, from one product's two mods or a repack of its own archive - " +
             "BENIGN, counted in the header and listed only under its own class token), 'foreign_index' (a " +
             "same-local-id file carrying a different load-order index byte, inferred from the file itself), " +
             "'inert' (the key resolves to a placed reference, to no record, to a plugin not in the order, or the " +
             "filename is malformed - named and dropped, not a face bug). POPULATION is the UNION: every NPC_ in " +
             "scope that needs a bake, plus every facegen file on disk whose key resolves to nothing. An NPC whose " +
             "Template carries the Traits flag inherits its appearance and has no bake of its own - it is EXCLUDED " +
             "and counted, never flagged. The file half (inert/foreign_index) is reported only on an UNSCOPED " +
             "sweep: under plugins=, exclude= or a record scope a file for an NPC outside the scope is out of " +
             "scope, not orphaned. BOUNDARY: " +
             "it reports PROVENANCE, never the render - it cannot read a .dds's pixels and cannot bake geometry " +
             "(Ctrl+F4), so a clean row is not a promise the face looks right; and NOT this family: a purple or " +
             "white face (a missing texture), player-only grey (RaceMenu/SKEE), a brown weight face (save-baked " +
             "weight), or an appearance distributed at runtime by SPID. Causes and repairs: " + ReadSentences.FaceGenDocUrl + ".")]
            string[]? findings = null,
        [Description("Optional. true = return ONLY the header totals plus each running family's histograms, with no per-plugin or per-record listing. Errors: dangling-by-TARGET-plugin (which plugin the broken refs point INTO — the one absent dependency behind a wall of findings) and dangling-by-SOURCE-plugin (which plugin they come FROM — how much is vanilla baseline and how much your mods introduced). Scripts: unbound-by-PROPERTY-NAME. Dialogue: the totals and the unreachable-seed roster alone, no per-topic blocks — a seed nobody could reach bounds the answer rather than sitting inside it, so this does not silence it. The cheap before/after-a-fix comparison; totals stay exact (never limit-capped) and limit= caps the histogram ROWS instead.")]
            bool counts_only = false,
        [Description("Optional. 'text' (default) or 'json' — the machine-readable twin carrying the same data, sectioned per family, with the totals/capped/truncated accounting in-band.")]
            string? format = null,
        [Description("Optional. Max findings to list per family (default 1000). The TRUE totals are always " +
             "reported; over the cap the response says so, and for the errors family says how many plugins lost " +
             "entries, names the ones that lost the most (a count each), and states how many it did not name. " +
             "It is ONE listing per family for every type in scope, filled plugin by plugin and type by type " +
             "inside each — so under a MULTI-TYPE types= scope ANY of those types can be short in it. The " +
             "response says so whenever the listing came out short, and names the knob that cut it (limit= or " +
             "max_chars=): a type absent from the listing is UNLISTED, not clean. " +
             "BASELINE (errors family): the base-game masters carry permanent vanilla dangling refs no load order " +
             "can fix, so the response splits them out of the total and spends limit= on every other plugin FIRST " +
             "— vanilla cannot crowd mod findings out of the listing. Master-table findings and unverifiable " +
             "notes are outside this cap, never trimmed by it; on the SCRIPTS family a note repeating one already " +
             "reported for the same script class is collapsed to a count instead, so a disabled mod's unreadable " +
             "scripts cannot fill the listing (a note that names no script class is never collapsed — the record " +
             "is its only identity). A script-heavy plugin (~180 scripted records) does not fit a tool result " +
             "unnarrowed, and limit= alone will not help there because it caps FINDINGS, not the record roster — " +
             "counts_only=true or a record scope is what does. Under counts_only=true this caps the histogram " +
             "ROWS instead. For the DIALOGUE family it caps how many SEEDS one call expands, and the response " +
             "names how many it did not reach.")]
            int limit = 1000,
        [Description("Optional. The DIALOGUE family only, and required by it: the topics and quests to validate, " +
             "as FormIDs ('0F1AC1:Skyrim.esm' — 6 hex digits, a colon, then the defining master's filename). A " +
             "DIAL validates one topic; a QUST validates EVERY topic that quest owns (plus the quest's own " +
             "CK-parity subrecords and its .seq, checked once); a DLVW or DLBR runs a record-level CK-parity check " +
             "— a bare DLVW crashes the CK's Dialogue Views editor. This family is SEEDED, not swept — " +
             "plugins=/types=/formids=/editorid_contains=/exclude= do not scope it — and findings=['dialogue'] with " +
             "no seeds is REFUSED on cost, never widened to the whole order (a whole-order pass is a per-topic " +
             "graph walk across every touching plugin, and the order this bound was measured on carries 82,343 " +
             "dialogue topics). limit= caps how many seeds one call expands.")]
            string[]? seeds = null,
        [Description("Optional. TRANSPORT: write the COMPLETE findings of every family that ran to this ABSOLUTE " +
             ".jsonl path as an artifact (line 1 = manifest) and render only the manifest inline - the same " +
             "convention " + ToolNames.Records + " uses, so an artifact re-enters via formids=[\"@<path>\"]. ONE " +
             "file with a 'family' and a 'class' column, not one per family: a merged call's findings are one " +
             "answer, and a column a family does not use is null. The rows are the SWEEP's findings, not the " +
             "render's, so nothing is missing because the inline body ran out of characters; what limit= already " +
             "cut is cut here too, and the manifest says so by carrying total above row_count. Refused with " +
             "counts_only=true, which returns the histograms and no rows.")]
            string? to_file = null,
        [Description("Optional. Max characters before the response stops with an explicit notice. 0 = the server default (~80k). The budget is DIVIDED among the families that ran and their parts, not spent in series — a family that renders second does not inherit what the first one left over. Raise it for a quest that owns many topics.")]
            int max_chars = 0) => Guard.Tool(ToolNames.Check, () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        bool json = Wire.WantsJson(format, out var fmtErr);
        if (fmtErr is not null) return fmtErr;
        if (!SweepFamilySelection.TryParse(findings, out var selection, out var famErr)) return Wire.Refuse(json, "error: " + famErr);
        int lim = limit <= 0 ? 1000 : limit;

        // What every family agrees is malformed, checked before any of them is dispatched — the sweep families
        // parse these in their own service entries, which a dialogue-only call never reaches. Rendered through the
        // normal refusal path rather than returned as a bare string, so format='json' still gets a document.
        // See SweepSharedInput for the split: syntax refuses here, scope matching stays family-local.
        if (SweepSharedInput.Error(svc, plugins, types, formids, editorid_contains, exclude) is { } inputErr)
        {
            var refusal = new CheckSweep(selection, SharedInputError: inputErr);
            return json ? JsonWire.RenderCheck(refusal, max_chars, lim) : Wire.RenderCheck(refusal, max_chars, lim);
        }

        // Both swept families take the same plugins= list whole: each resolves a name the active order does not hold
        // on disk and sweeps it off-order, so one list means the same scope in both sections. They share ONE memo of
        // that split, so the default findings set does not read the MO2 composition and sweep every mod folder twice
        // for an answer that is identical both times — and the two cannot disagree about which names resolved.
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
            // Its own scope, not the plugins= list: this family selects records, not plugins, so handing it
            // `plugins` would give one parameter a second meaning. With no seeds it raises the cost refusal rather
            // than widening to the whole order.
            dialogue = svc.CheckDialogue(seeds, lim, counts_only);

        // One call, one build. The root marker is a RESPONSE-level claim about the order every family answered
        // from, and nothing holds the captures together: a freshness rebuild between them would state it from one
        // build beside a family's epoch from another — naming plugins that build did not lose, or staying silent
        // about ones it did, which is the ambiguity #353 exists to end. Compared here and refused loud, the way
        // the records seams do, rather than answered from two builds.
        string? Seam(string? familyEpoch, string family) =>
            familyEpoch is not null && familyEpoch != order.Epoch
                ? $"the load order changed while this check was running (epoch={order.Epoch} when the call started, " +
                  $"epoch={familyEpoch} when the {family} family answered) — the response would describe two " +
                  "builds. Retry the call."
                : null;
        if ((Seam(errors?.Epoch, "errors") ?? Seam(scripts?.Epoch, "scripts")
             ?? Seam(facegen?.Epoch, "facegen")) is { } seam)
        {
            var torn = new CheckSweep(selection, OrderSeamError: seam);
            return json ? JsonWire.RenderCheck(torn, max_chars, lim) : Wire.RenderCheck(torn, max_chars, lim);
        }

        var sweep = new CheckSweep(selection, errors, scripts, dialogue, facegen, Order: order);

        // to_file=: the rows ARE the file, so the response is the manifest and each family's boundary. Refused
        // beside counts_only=, which returns the histograms and no rows for the file to hold.
        if (to_file?.Trim() is { Length: > 0 } path)
        {
            // The same validator the records surface runs: absolute, .jsonl, and outside the pruned results
            // directory. Unvalidated, a relative path writes under the SERVER's working directory and the response
            // names an artifact the caller cannot find.
            if (Artifacts.ValidateToFile(path) is { } verr) return Wire.Refuse(json, verr);
            if (counts_only)
                return Wire.Refuse(json, "error: counts_only= returns the histograms with no findings, and to_file= "
                                       + "writes the findings - the two contradict; drop one.");
            var query = new[]
            {
                new KeyValuePair<string, string>("findings", string.Join(",", selection.Ran.Select(SweepFamilySelection.Token))),
                new KeyValuePair<string, string>("plugins", plugins is { Length: > 0 } ? string.Join(",", plugins) : "<whole order>"),
                new KeyValuePair<string, string>("limit", lim.ToString()),
            };
            // No family answered, so there are no findings to write: the file would be empty and its manifest would
            // read as a clean sweep. Nothing is written, and the sweep renders the way it would without to_file=,
            // which is where every refusal's ground is already stated. Where SOME family answered, the refusal
            // rides beside its family's boundary in the manifest render instead, so one family's refusal does not
            // discard another family's rows.
            if (CheckOutcome.For(sweep).Ran.Count == 0)
                return json ? JsonWire.RenderCheck(sweep, max_chars, lim) : Wire.RenderCheck(sweep, max_chars, lim);
            var (spill, artErr) = CheckArtifact.Write(sweep, path, query);
            if (artErr is not null) return Wire.Refuse(json, "error: " + artErr);
            return CheckArtifact.RenderManifestOnly(sweep, spill!, json);
        }

        return json ? JsonWire.RenderCheck(sweep, max_chars, lim) : Wire.RenderCheck(sweep, max_chars, lim);
    });

    /// <summary>The facegen class tokens a parsed selection spells, so the tool hands the service the same
    /// vocabulary a caller writes rather than a second representation of it.</summary>
    static string[] FaceGenTokens(FaceGenFindingClass c)
        => c == FaceGenFindingClass.All ? Array.Empty<string>()
         : FaceGenCheck.Registered.Where(r => c.HasFlag(r)).Select(FaceGenCheck.Token).ToArray();
}

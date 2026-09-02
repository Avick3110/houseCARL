using System.ComponentModel;
using HousecarlCore;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>
/// <c>housecarl_check</c> — the merged derived-findings sweep (SPEC §6.1): <c>housecarl_check_errors</c>,
/// <c>housecarl_validate_scripts</c> and <c>housecarl_validate_dialogue</c>'s findings (its classes 1-7), with
/// <c>findings=</c> selecting the taxonomy.
///
/// <para><b>Both ancestors stay registered.</b> That is the W2/W3 precedent (<c>housecarl_records</c> sits beside
/// the eight reads it absorbs) and it is what makes the build waves land non-breaking: the alias rows for the old
/// names are in <see cref="AliasTable"/> and are dormant by construction while the names still resolve. They
/// activate at the 2.0.0 clean cut, when the ancestors are unregistered. No response carries deprecation prose.</para>
///
/// <para><b>What lives here and what does not.</b> This file holds the merged tool and the ORCHESTRATION a merged
/// tool needs — which families to run, and what scope each of them can actually take. The per-family sweeps are
/// the ancestors' own service calls, unchanged; the sweep families' renders are their transports'
/// (<c>Wire.RenderCheck</c>, <c>JsonWire.RenderCheck</c>), where the section renderers they call already live, and
/// the dialogue family's is <see cref="DialogueSweepRender"/>, beside the <c>DialogueWire</c> helpers IT is
/// assembled out of. Both follow the same rule — a render lives with its helpers — which is why they land in
/// different files.</para>
///
/// <para><b>The families do not share one scope.</b> The two sweep families take plugins and records; the dialogue
/// family takes SEEDS (SPEC §6.1 F1.1), so <c>plugins=</c> / <c>exclude=</c> and friends narrow the first two and
/// not the third. That is stated in the dialogue section rather than resolved by giving one parameter two meanings,
/// and an unseeded dialogue call is refused on cost (F1.2) rather than widened to the whole order.</para>
/// </summary>
[McpServerToolType]
public static class CheckTools
{
    [McpServerTool(Name = ToolNames.Check, ReadOnly = true, Title = "Sweep the load order for derived findings"),
     Description(
         // ---- what it is, and what selects a family ------------------------------------------------
         "DERIVED-FINDINGS SWEEP over the load order — one call, several finding FAMILIES, selected by findings=. " +
         "Read-only; writes nothing. Resolves against the load-order WINNERS, like every other read. Three families: " +
         "'errors' (load-order integrity), 'scripts' (VMAD script-property binding) and 'dialogue' (dialogue graph " +
         "validation over SEEDED topics and quests). findings= takes whole families or the classes inside them " +
         "('dangling', 'missing_masters'; 'unbound_object', 'unbound_scalar', 'unbound', 'bound_null') — naming a " +
         "class runs its family narrowed to that class; the dialogue family narrows by seeds=, not by class. " +
         "DEFAULT: findings= omitted runs the ERRORS family alone, and the response STATES which families ran, which " +
         "registered families did not, and the exact findings= spelling that adds them — the default narrows only " +
         "because the response says so. It cannot be every family: an unscoped scripts sweep is ~8 minutes on a " +
         "3800-plugin order (measured), and an unscoped dialogue sweep is refused outright (below). " +
         // ---- family: errors (harvested from housecarl_check_errors) -------------------------------
         "ERRORS FAMILY — the data-layer twin of the Creation Kit's 'Check For Errors' / xEdit's error check. For " +
         "each plugin in scope it walks every record's FormLinks and reports three classes: (1) DANGLING references " +
         "— a non-null link whose target NO plugin in the ACTIVE order defines; (2) MISSING MASTERS — a master a " +
         "plugin DECLARES that is not present in the active order (the most common load-order break); (3) PARSE " +
         "failures — records houseCARL/Mutagen could not read, plus whole plugins the index excluded as unparseable. " +
         "A scoped name NOT in the active order is resolved on disk (any mod folder — enabled, disabled, or not yet " +
         "listed in MO2) and swept OFF-ORDER: its own records, links resolved against the active order PLUS the " +
         "file's own definitions — the pre-enable verify sweep for a patch houseCARL just wrote. ONLY this family has " +
         "an off-order lane, and a response whose scope named such a file states per family which ones it did not " +
         "sweep. BOUNDARY (never a silent claim of more — Q3): it covers the FormLink-resolution / missing-master / " +
         "parse class. It does NOT verify navmesh or terrain spatial integrity (CRC/grid — a Mutagen-delta " +
         "residual), does NOT flag a required field left null (a null FormLink is a legal optional, not an error), " +
         "does NOT list unused-master cleanup (a FormLink scan cannot prove a master is unused), and does NOT " +
         "link-check an owned item's ownership 'variable' word (a rank/global Mutagen cannot type on an override " +
         "without a link cache). BASELINE: the base-game masters carry permanent vanilla dangling refs no load order " +
         "can fix, so the response splits them out of the total and spends limit= on every other plugin FIRST — " +
         "vanilla cannot crowd mod findings out of the listing. " +
         // ---- family: scripts (harvested from housecarl_validate_scripts) --------------------------
         "SCRIPTS FAMILY — catches the silent-None footgun a byte-valid plugin hides: a record whose attached " +
         "Papyrus script DECLARES a property (e.g. 'Spell Property CallVesyraPower Auto') the record's script data " +
         "(VMAD) never BINDS, so at runtime it is None and the code that uses it no-ops while the log looks clean " +
         "(the maximally-misleading 'the function ran, the effect is absent' class — the same as the Creation Kit's " +
         "auto-add-property bug). For each record carrying a script it reads the attached script's compiled .pex — " +
         "and every script it EXTENDS — from the load order (loose or BSA), and reports: (1) UNBOUND properties " +
         "declared but not bound (an object/form type ⇒ None ⇒ the silent no-op, ranked first; an uninitialized " +
         "scalar ⇒ a 0/false/\"\" default that may be wrong); (2) BOUND-BUT-NULL object properties (advisory — " +
         "sometimes filled at runtime). BOUNDARY: it checks Auto (CK-editable) properties only, not code-driven full " +
         "properties; 'unbound may be intentional' (a runtime-filled link), so a finding is a flag to VERIFY; and if " +
         "a script's .pex is not on disk (uncompiled / not in the order) the attachment is reported UNVERIFIABLE, " +
         "never passed clean. " +
         // ---- family: dialogue (harvested from housecarl_validate_dialogue) ------------------------
         "DIALOGUE FAMILY — a topic's whole graph as the GAME sees it, and it is SEEDED, not swept: seeds= names " +
         "what to validate and findings=['dialogue'] without it is REFUSED on cost (a whole-order pass is a " +
         "per-topic graph walk across every touching plugin, and the order this bound was measured on carries " +
         "82,343 dialogue topics). A seed is a DIAL (one topic), a QUST (EVERY topic that quest owns, plus the " +
         "quest's own CK-parity subrecords and its .seq, checked once), or a DLVW/DLBR (a record-level CK-parity " +
         "check — a bare DLVW crashes the CK's Dialogue Views editor). It checks what houseCARL CAN verify at the " +
         "data layer: the topic is wired to a quest, the branch resolves, the INFO.LinkTo conversation chain has no " +
         "dangling targets, and no previous-link (PNAM) is dangling — an EMPTY PNAM is NORMAL (vanilla selects among " +
         "a topic's lines by their conditions, not a previous-link chain), so absence is never flagged; each voiced " +
         "line's .fuz is on disk and each result script is bound + compiled; non-ASCII characters in the " +
         "player-facing text (topic name, line prompt, response text) are flagged as likely in-game MOJIBAKE (the " +
         "CK/Papyrus surface is Windows-1252/ASCII); each line's CTDA conditions are statically checked for a " +
         "meaningful subset of MALFORMED shapes (a dangling form reference, a dead quest-alias index, an unset Run " +
         "On reference, GetIsID pointed at a placed instance); and a Start-Game-Enabled quest's .seq is checked for " +
         "coverage and staleness (without it the quest is dormant on a fresh save and its dialogue never shows). " +
         "BOUNDARY: it cannot EVALUATE whether a WELL-FORMED condition passes — only the running game can — and it " +
         "does not check lip-sync or audio content, so 'checks passed' never reads as 'this will play'. NOT HERE: " +
         "the effective merged INFO order — the sequence the game walks, which line MOVED and which plugin moved it, " +
         "the answer to 'why does the wrong line play' — is an ordered sequence rather than a finding and lives on " +
         ToolNames.Records + " project='info_order'. To CREATE dialogue lines use " + ToolNames.Create + "; to inspect " +
         "one record use " + ToolNames.ReadRecord + ". " +
         // ---- narrowing, shared, with each family's own cost teaching ------------------------------
         "NARROWING: beyond plugins= it takes a record scope (type= / formids= / editorid_contains=), " +
         "property_contains= (the scripts family — chasing one property name), a findings= filter, counts_only=true " +
         "for the totals plus per-family histograms (errors: dangling by TARGET plugin and by SOURCE plugin; " +
         "scripts: unbound by PROPERTY NAME; dialogue: the totals alone, no per-topic blocks), and format='json'. A script-heavy plugin (~180 scripted records) does " +
         "not fit a tool result unnarrowed, and limit= alone will not help there because it caps FINDINGS, not the " +
         "record roster — counts_only=true or a record scope is what does. Narrowing narrows the COUNTS too: they " +
         "are always the counts for the scope actually swept, and the response says so. exclude= drops named plugins " +
         "or whole groups (base_masters / implicit) out of the sweep entirely rather than merely accounting for " +
         "them; it scopes every SWEPT family. The dialogue family is seeded rather than swept, so no plugin scope " +
         "narrows it — the response says so in that family's own section. " +
         // ---- the accounting, harvested from both ---------------------------------------------------
         "Results cap at limit= and max_chars (both overruns explicit, per family): the response states how much of " +
         "each family's listing it carries, why the rest is absent, and which knob moves it — a capped errors " +
         "listing NAMES which source plugins lost entries.")]
    public static string CheckTool(
        LoadOrderService svc,
        [Description("Optional. Plugin filenames to sweep (e.g. 'MyMod.esp'). A name not in the active order is resolved on disk (a fresh houseCARL patch, a disabled mod) and swept OFF-ORDER by the ERRORS family; found nowhere (or in several folders) it is an error. The scripts family has no off-order lane and the response says per family which named files it did not sweep. Omit to sweep the WHOLE active order — thorough but heavier; scope to one plugin for a fast, focused check like the CK's per-plugin 'Check For Errors'.")]
            string[]? plugins = null,
        [Description("Optional. Record type to sweep — a 4-char signature ('WEAP') or catalog name ('Weapon'). Applied at the record STREAM, so it is the CHEAPEST scope: skipped records cost nothing (no link walk, no .pex chain read). An unknown type is refused, naming what is expected.")]
            string? type = null,
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
             "in scope matches is refused by each family that HAS a scope to match it against — where a family's " +
             "scope is empty because every plugin you named is outside it, that family reports the empty scope " +
             "instead, since the exclusion is not why it swept nothing. A group member that is not in this order " +
             "is the ordinary case and is simply dropped. This does not change what " +
             "counts as the vanilla BASELINE the errors family splits out — that is always Mutagen's own base-master set.")]
            string[]? exclude = null,
        [Description("Optional. Which finding FAMILIES and CLASSES to look for, in one vocabulary. Families: 'errors', 'scripts', 'dialogue'. Classes inside them: 'dangling', 'missing_masters' (errors); 'unbound_object' (HIGH — the silent-None footgun), 'unbound_scalar' (MEDIUM), 'unbound' (both), 'bound_null' (advisory) (scripts). The DIALOGUE family has no class token — it narrows by seeds=, which it requires. A family token means every class in it; a class token runs its family narrowed to that class; naming several runs each. DEFAULT (omitted) = the errors family alone, and the response states what it did not run and how to ask for it. Excluding 'dangling' SKIPS the per-record link walk entirely — that is how you ask 'is any master missing anywhere in my order' without paying for a full sweep. An excluded class renders as 'not checked', never as 0. Unscannable records, scan errors and unverifiable script attachments are ALWAYS reported and cannot be filtered out (a suppressed 'could not read' would read as a clean result).")]
            string[]? findings = null,
        [Description("Optional. true = return ONLY the header totals plus each running family's histograms, with no per-plugin or per-record listing. Errors: dangling-by-TARGET-plugin (which plugin the broken refs point INTO — the one absent dependency behind a wall of findings) and dangling-by-SOURCE-plugin (which plugin they come FROM — how much is vanilla baseline and how much your mods introduced). Scripts: unbound-by-PROPERTY-NAME. Dialogue: the totals and the unreachable-seed roster alone, no per-topic blocks — a seed nobody could reach bounds the answer rather than sitting inside it, so this does not silence it. The cheap before/after-a-fix comparison; totals stay exact (never limit-capped) and limit= caps the histogram ROWS instead.")]
            bool counts_only = false,
        [Description("Optional. 'text' (default) or 'json' — the machine-readable twin carrying the same data, sectioned per family, with the totals/capped/truncated accounting in-band.")]
            string? format = null,
        [Description("Optional. Max findings to list per family (default 1000). The TRUE totals are always reported; over the cap the response says so, and for the errors family says how many plugins lost entries, names the ones that lost the most (a count each), and states how many it did not name. Master-table findings and unverifiable notes are always listed in full (they are few). Under counts_only=true this caps the histogram ROWS instead. For the DIALOGUE family it caps how many SEEDS one call expands, and the response names how many it did not reach.")]
            int limit = 1000,
        [Description("Optional. The DIALOGUE family only, and required by it: the topics and quests to validate, as FormIDs ('0F1AC1:Skyrim.esm' — 6 hex digits, a colon, then the defining master's filename). A DIAL validates one topic; a QUST validates EVERY topic that quest owns (plus the quest's own CK-parity subrecords and its .seq, checked once); a DLVW or DLBR runs a record-level CK-parity check. This family is SEEDED, not swept — plugins=/type=/formids=/editorid_contains=/exclude= do not scope it — and findings=['dialogue'] with no seeds is REFUSED on cost, never widened to the whole order. limit= caps how many seeds one call expands.")]
            string[]? seeds = null,
        [Description("Optional. Max characters before the response stops with an explicit notice. 0 = the server default (~80k). The budget is DIVIDED among the families that ran and their parts, not spent in series — a family that renders second does not inherit what the first one left over. Raise it for a quest that owns many topics.")]
            int max_chars = 0) => Guard.Tool(ToolNames.Check, () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        bool json = Wire.WantsJson(format, out var fmtErr);
        if (fmtErr is not null) return fmtErr;
        if (!SweepFamilySelection.TryParse(findings, out var selection, out var famErr)) return Wire.Refuse(json, "error: " + famErr);
        int lim = limit <= 0 ? 1000 : limit;

        // WHAT EVERY FAMILY AGREES IS MALFORMED, CHECKED BEFORE ANY OF THEM IS DISPATCHED. These parameters were
        // parsed inside the two SWEEP families' service entries, and those are called only where their family was
        // selected — so a dialogue-only call never looked at them, and a blank plugins= entry was filtered out
        // below before `noneInScope` could see it and the whole order was swept. Rendered through the normal
        // refusal path rather than returned as a bare string, so format='json' still gets a document.
        // See SweepSharedInput for the split: syntax refuses here, scope MATCHING stays family-local.
        if (SweepSharedInput.Error(svc, plugins, type, formids, editorid_contains, exclude) is { } inputErr)
        {
            var refusal = new CheckSweep(selection, SharedInputError: inputErr);
            return json ? JsonWire.RenderCheck(refusal, max_chars, lim) : Wire.RenderCheck(refusal, max_chars, lim);
        }

        // WHICH FAMILY CAN SWEEP WHICH PLUGIN. The errors family resolves a name that is not in the active order on
        // disk and sweeps it off-order; the scripts family has no such lane and refuses such a name outright. On one
        // plugins= list feeding both, that asymmetry is STATED per family rather than resolved by widening one
        // family silently (capability growth, which belongs in an issue) or by refusing a call the errors family can
        // answer. So the scripts family is handed the ACTIVE subset, and the response names what it left out.
        var active = new HashSet<string>(svc.ActivePluginNames, StringComparer.OrdinalIgnoreCase);
        // The Where() is now a formality rather than a filter: a blank entry refused above, so nothing reaches
        // here for it to drop. It stayed because the trim is what makes the active-set comparison honest, and a
        // filter that can no longer discard anything is cheaper to keep than a second rule about whitespace.
        var named = (plugins ?? Array.Empty<string>()).Select(p => (p ?? "").Trim())
                                                      .Where(p => p.Length > 0).ToArray();
        var offOrder = named.Where(p => !active.Contains(p)).ToArray();
        var activeNamed = named.Where(active.Contains).ToArray();

        ErrorCheckResult? errors = null;
        ScriptCheckResult? scripts = null;
        if (selection.Ran.Contains(SweepFamily.Errors))
            errors = svc.CheckErrors(plugins, lim, formids, editorid_contains, type,
                                     SweepFindings.Tokens(selection.ErrorClasses), counts_only, exclude);
        if (selection.Ran.Contains(SweepFamily.Scripts))
            scripts = svc.ValidateScripts(activeNamed.Length > 0 ? activeNamed : null, lim, formids, editorid_contains,
                                          type, property_contains, SweepFindings.Tokens(selection.ScriptClasses),
                                          counts_only, exclude,
                                          // A caller who named plugins and had EVERY one of them resolve off-order
                                          // has given this family an empty scope. Passing null instead would widen it
                                          // to the whole order — a 3800-plugin sweep the caller did not ask for.
                                          noneInScope: named.Length > 0 && activeNamed.Length == 0);
        DialogueCheckResult? dialogue = null;
        if (selection.Ran.Contains(SweepFamily.Dialogue))
            // Its own scope, and NOT the plugins= list: this family selects records, not plugins (SPEC §6.1 F1.1).
            // Handing it `plugins` would be a second meaning for one parameter; handing it nothing when the caller
            // gave no seeds is the cost-refusal (F1.2), which DialogueSweep raises rather than widening to the order.
            dialogue = svc.CheckDialogue(seeds, lim, counts_only);

        var sweep = new CheckSweep(selection, errors, scripts, offOrder, dialogue);
        return json ? JsonWire.RenderCheck(sweep, max_chars, lim) : Wire.RenderCheck(sweep, max_chars, lim);
    });
}

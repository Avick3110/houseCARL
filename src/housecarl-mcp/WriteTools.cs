using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// houseCARL write tools (§8.4 Beat C). Both ride the PROVEN public write cleave (<see cref="WritePatchBuilder.Apply"/>)
/// through <see cref="LoadOrderService.ApplyEdits"/>: resolve each record's load-order WINNER, override it into a NEW
/// patch plugin, pre-flight EVERY edit through the corpus rulebook, apply the generic verbs, and serialize ONCE with the
/// full master set (cross-master merges included). Originals are never written. Output model (Aaron-locked): one
/// complete .esp per call; <c>into=</c> extends an existing patch (the multi-session accumulation lever).
/// </summary>
[McpServerToolType]
public static class WriteTools
{
    [McpServerTool(Name = "housecarl_create_plugin", Title = "Create an empty header-only (trigger) plugin"),
     Description(
         "Create an EMPTY, HEADER-ONLY plugin — a valid TES4 header with ZERO records and no masters, in a NEW mod " +
         "folder (originals untouched). Its only job is to EXIST so its basename resolves: the artifact that SKSE configs " +
         "binding by plugin basename need (e.g. a CraftingCategories-style trigger that must ship 'Foo.esp' so 'Foo.json' " +
         "loads), a placeholder ESL for FormID reservation, a dummy plugin for another mod to list as a master, or any " +
         "'I just need plugin Foo to be present' case. UNLIKE housecarl_create, it authors NO record — so it adds no conflict-tree footprint " +
         "(no filler override needed to make the plugin non-empty). plugin_name is used EXACTLY (the basename is " +
         "load-bearing — houseCARL will NOT auto-suffix it): if a plugin of that name is already active in the load order, " +
         "or a houseCARL folder of that name already exists, it REFUSES loud rather than rename or overwrite (Q3). Pass " +
         "esl=true for the lightest trigger (a header-only light plugin consumes no consequential load-order slot; with " +
         "zero records the ESL FormID-range rule is trivially satisfied). author/description are optional TES4 header " +
         "text. Returns the plugin path + mod folder — enable + sort it in MO2 to use it. To author actual records, use " +
         "housecarl_create instead.")]
    public static string CreatePlugin(
        LoadOrderService svc,
        [Description("The EXACT plugin name (with or without a trailing .esp/.esm/.esl; e.g. 'Authoria - CraftingCategories'). Used VERBATIM as the basename — houseCARL will not auto-suffix it, because a trigger plugin's whole job is that its basename matches the config bound to it. The written file is '<name>.esp'.")]
            string plugin_name,
        [Description("When true, flag the plugin as a light master (ESL) — the lightest possible trigger: a header-only ESL consumes no consequential load-order slot. Default false (a normal full plugin).")]
            bool esl = false,
        [Description("Optional. Author text for the TES4 header (the CNAM field). Purely informational.")]
            string? author = null,
        [Description("Optional. Description text for the TES4 header (the SNAM field). Purely informational.")]
            string? description = null) => Guard.Tool("housecarl_create_plugin", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        if (string.IsNullOrWhiteSpace(plugin_name))
            return "error: plugin_name is empty. Name the plugin to create (a header-only plugin has no record to derive a name from).";
        return RenderCreatePlugin(svc.CreatePlugin(plugin_name, esl, author, description));
    });

    [McpServerTool(Name = "housecarl_compact_plugin", Title = "Compact / ESL-renumber a plugin's FormIDs"),
     Description(
         "COMPACT a plugin's FormIDs — the data-layer twin of xEdit's \"Compact FormIDs for ESL\". Renumbers EVERY record " +
         "the plugin DEFINES (its originating records — flat AND nested: cells, placed references, dialogue lines, navmesh, " +
         "landscape) into the light/ESL range 0x800–0xFFF (the 2048-ID window), repoints every reference WITHIN the plugin, " +
         "leaves its overrides of other mods at their master FormIDs, and flags the result a light master (ESPFE) so it " +
         "frees a load-order slot. esl=false instead renumbers contiguously from 0x800 with NO light flag/ceiling (to close " +
         "FormID gaps). OUTPUT (default): a NEW plugin keeping the SOURCE'S EXACT basename (so other mods that list it as a " +
         "master still resolve) in a fresh houseCARL mod folder — your ORIGINAL is untouched; review the new one in xEdit, " +
         "then in MO2 enable its folder and DISABLE the original mod (same basename — MO2 serves one). in_place=true instead " +
         "OVERWRITES the original (xEdit's norm; rides the in-place consent, NO backup; needs acknowledge=true). " +
         "THE SAFETY (Q3): renumbering breaks any reference from OUTSIDE this plugin (they'd point at FormIDs that vanish). " +
         "houseCARL scans the WHOLE load order for such external referencers (a one-pass walk — can take ~25s on a big order): " +
         "if NONE, it's a clean compaction; if SOME, the call is REFUSED and lists them, UNLESS repoint_externals=true, which " +
         "ALSO rewrites each of them in place to follow the renumber (needs acknowledge=true; no backup of them either). " +
         "LOCALIZED PLUGINS: houseCARL does not rewrite one in place — its text lives in separate .STRINGS files it cannot " +
         "swap together with the plugin — so in_place=true on a localized target is refused, and so is repoint_externals=true " +
         "when any referencer is localized (the refusal names it and where its text is, so you learn that BEFORE choosing the " +
         "flag). The DEFAULT new-file lane still compacts a localized plugin: the output is DE-LOCALIZED — the text this read " +
         "resolved is written into the plugin itself and the source's .STRINGS files no longer describe it — and the report " +
         "says so. Read the output before swapping it in. " +
         "The target need NOT be active: a plugin on disk but not (yet) in the load order — e.g. the patch houseCARL just " +
         "wrote, before the MO2 refresh — is resolved by filename across ALL mod folders and compacted OFF-ORDER (its " +
         "declared masters must still be active). An override-only plugin with esl=true takes the FLAG-ONLY lane: nothing " +
         "to renumber, every record copies verbatim, the ESL flag is set (always valid — the light window only constrains " +
         "originating records). Refuses loud + writes nothing on: the plugin found nowhere on disk / ambiguous across " +
         "folders / unparseable; an override-only plugin with esl=false (nothing to do); MORE records than the light " +
         "range holds (the hard 2048 ESL ceiling — named, never truncated); a declared master not active; a serialize fault. " +
         "Note: references compiled into Papyrus scripts (.pex hardcoded FormIDs / GetFormFromFile) are NOT remappable — " +
         "verify scripted records after compacting.")]
    public static string CompactPlugin(
        LoadOrderService svc,
        [Description("The plugin's filename to compact (e.g. 'CoolMod.esp'). Usually active in your load order; a plugin on disk but not in the order (a fresh houseCARL patch, a disabled mod) is resolved by filename and compacted OFF-ORDER — its declared masters must still be active. The compacted output keeps this EXACT basename.")]
            string plugin,
        [Description("When true (default), renumber into the light/ESL range (0x800–0xFFF, 2048 IDs) and flag the result a light master (ESPFE) — the canonical 'compact for ESL'. false = renumber contiguously from 0x800 with no light flag or 2048 ceiling (just closes FormID gaps).")]
            bool esl = true,
        [Description("Optional, default false. IN-PLACE LANE (opt-in): OVERWRITE the original plugin with its compacted form (xEdit's norm) instead of writing a new file — NO houseCARL backup or undo (keep your own). Requires acknowledge=true. OMIT (the default) to write a NEW plugin (same basename, fresh mod folder) and leave the original untouched for review. A LOCALIZED plugin is REFUSED in this lane whatever arrangement its .STRINGS files are in; the new-plugin lane compacts it, DE-LOCALIZED, and says so.")]
            bool in_place = false,
        [Description("Optional, default false. If OTHER plugins reference records being renumbered, compaction would break them and the call REFUSES (listing them) by default. Set true to ALSO rewrite those external referencers IN PLACE to follow the renumber (requires acknowledge=true; no backup of them either). Refused when any referencer is LOCALIZED — houseCARL does not rewrite one in place — and the default refusal above says so up front rather than sending you here.")]
            bool repoint_externals = false,
        [Description("Optional, default false. Confirms the in-place trade-off when in_place=true OR repoint_externals=true (your original file(s) get rewritten, no backup). The FIRST such call without it returns a CONFIRM prompt listing exactly what will be overwritten — re-call with acknowledge=true to proceed.")]
            bool acknowledge = false,
        [Description("Optional. Base name for the NEW mod folder (new-file lane only; auto-suffixed if taken). Ignored with in_place=true. The PLUGIN inside ALWAYS keeps the source's exact basename so external masters still resolve.")]
            string? patch_name = null) => Guard.Tool("housecarl_compact_plugin", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        if (string.IsNullOrWhiteSpace(plugin))
            return "error: plugin is empty. Name the plugin filename to compact (e.g. 'CoolMod.esp').";
        return RenderCompact(svc.CompactPlugin(plugin, esl, in_place, repoint_externals, acknowledge, patch_name));
    });

    [McpServerTool(Name = "housecarl_merge_plugins", Title = "Merge plugins into one new plugin"),
     Description(
         "MERGE one or more ACTIVE plugins into ONE NEW plugin — a RECORDS operation (the zMerge/'Merge Plugins' job): the " +
         "donors' records combine under a new filename; the donor FILES and their mods are NEVER touched (new-file lane only, " +
         "no in-place). TO RENAME a plugin, pass ONE donor: with nothing to combine the merge IS a rename — the same records " +
         "under a new plugin name, keeping every object id already inside the writable range (nothing can collide; an id " +
         "BELOW the 0x800 floor still renumbers, and the per-donor line reports it), facegen/voice/seq carried to the new name. " +
         "RENUMBER is collision-first (zMerge's default): the donor EARLIEST in the load order keeps its FormID " +
         "object ids; later donors renumber ids already taken, and ANY donor's ids below the 0x800 floor renumber too " +
         "(all records necessarily move to the new plugin's identity). " +
         "Cross-donor conflicts on the SAME record resolve to the LOAD-ORDER WINNER and are each REPORTED; a losing donor's " +
         "nested children the winner doesn't re-list (a base mod's dialogue lines under a patched topic; placed refs under a " +
         "patched cell) are GRAFTED into the winner's copy — so merging a mod WITH its patches is the intended use. ASSETS " +
         "follow the renumber: every donor NPC's facegen and every voiced line are carried into the new plugin-name folders " +
         "(those paths embed the plugin NAME, so ALL donor facegen/voice moves, not just collisions), and a .seq is refreshed " +
         "when any donor shipped one. THE SAFETY (Q3): plugins OUTSIDE the merge that reference or override donor records are " +
         "WARNED and NAMED, never refused — the donors stay active until you swap in MO2, so nothing breaks at write time; the " +
         "remedy is to include those patches in the merge set or re-point them before disabling the donors. Refuses loud + " +
         "writes nothing on: a donor not active / unparseable / not on disk; an output name already in the load order; a " +
         "dangling donor-internal reference (a donor referencing a FormID no donor defines); a declared master not active. " +
         "AFTER: review the merged plugin in xEdit, enable its mod folder in MO2, then deactivate the donor PLUGINS (right " +
         "pane) but KEEP the donor MOD FOLDERS enabled (left pane) — merge carries only the FormID-keyed files the rename " +
         "breaks (facegen/voice/seq); every other donor asset (meshes, textures, scripts, BSA contents) is still referenced " +
         "BY PATH from the merged records and loads from the donor folders. Caveat: a donor .bsa stops auto-loading once its " +
         "same-named plugin is inactive — extract it into the mod folder (housecarl_bsa_extract) or load it via a same-named " +
         "dummy plugin (housecarl_create_plugin). Existing SAVES that depend on the donors will NOT survive (the records now " +
         "live under a different plugin name, and any id that had to be renumbered moved with it) — best for a new game. " +
         "A donor's HEADER does not come along: light (ESL) status, master status, and Author/Description are dropped, and " +
         "the report names each one it actually dropped. Want it light/ESL? Run housecarl_compact_plugin on the merged " +
         "plugin afterward (the tools compose) — but it renumbers object ids from 0x800 upward, so ids the merge kept move.")]
    public static string MergePlugins(
        LoadOrderService svc,
        [Description("The donor plugin filenames to merge (at least one, e.g. [\"CoolMod.esp\", \"CoolMod Patch.esp\"]) — each must be active in your load order. A SINGLE donor renames it into output=. This is a SET: a name repeated is still one donor. Argument order does not matter: houseCARL uses LOAD order for id priority and conflict resolution.")]
            string[] plugins,
        [Description("The NEW merged plugin's filename to create (e.g. 'MyMerge.esp') — must NOT already exist in the load order. The donors keep their names and files untouched.")]
            string output,
        [Description("Optional. Base name for the NEW mod folder (auto-suffixed if taken). Defaults to '<output> merged' — or '<output> renamed' for a single donor, since that folder name is what you will see in MO2 from then on.")]
            string? patch_name = null) => Guard.Tool("housecarl_merge_plugins", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        return RenderMerge(svc.MergePlugins(plugins, output, patch_name));
    });

    /// <summary>Compact, parseable confirmation (rulebook: short mutation confirmation + the IDs needed for follow-up).
    /// On refusal, the full reason (every malformed/rejected op) so the caller can fix and retry.</summary>
    internal static string Render(WritePatchBuilder.PatchOutcome o, int maxChars = 0, bool fullDump = false)   // internal: the compact-readback guard renders one outcome three ways
    {
        if (o.NeedsAcknowledge) return o.Error! + Epoch(o);  // the first-touch in-place CONSENT prompt — a required confirmation, NOT an error (Q3)
        if (!o.Success) return "error: " + o.Error + Epoch(o);
        if (o.DryRun) return RenderDryRun(o, maxChars, fullDump);
        var file = Path.GetFileName(o.OutputPath);
        var modFolder = Path.GetFileName(Path.GetDirectoryName(o.OutputPath) ?? "");
        var sb = new StringBuilder();
        if (o.InPlace)
            sb.Append("edited ").Append(file).Append(" IN PLACE (").Append(o.Bytes)
              .Append(" bytes — ").Append(WriteSentences.InPlaceRewritten).Append(")\n")
              .Append(WriteSentences.InPlaceModFolder(modFolder));
        else
            sb.Append(WriteSentences.NewOrExtendedArtifact(o.Extended, file, o.Bytes, modFolder));
        sb.Append(WriteSentences.Masters(o.Masters));
        sb.Append(o.Ops.Count).Append(o.Ops.Count == 1 ? " edit:\n" : " edits:\n");
        // BUDGETED, like every sibling render (the D2 twin arm's finding). This loop was the last unbounded row
        // list on the write surface: `bulk_apply` is set-valued, so a few hundred ops is the case the tool exists
        // for, and the json twin has always budgeted the same array and closed truncated:true. Text rendered every
        // row and let the HOST cut the oversized response out-of-band — the silent cut max_chars= promises never
        // happens, and the same divergence the created / forwarded / removed / cell-shell rows each closed in turn.
        int opCap = WriteSentences.Cap(maxChars);
        for (int i = 0; i < o.Ops.Count; i++)
        {
            if (sb.Length >= opCap)
            {
                sb.Append("  ... [truncated: ").Append(i).Append(" of ").Append(o.Ops.Count)
                  .Append(" edit(s) listed at max_chars=").Append(opCap).Append("; ")
                  .Append(WriteSentences.RowsCutOperationIntact(false, "applied"))
                  .Append(" — ").Append(ApplyAgainRemedy(o, file)).Append("]\n");
                break;
            }
            var op = o.Ops[i];
            sb.Append("  ").Append(op.RecordType).Append(' ').Append(op.Target).Append("  ").Append(op.Label)
              .Append(op.After is not null ? "  -> " + op.After : "  -> applied").Append('\n');
        }
        // Make the dialogue-coverage scope boundary VISIBLE, not silent (Q3): the .fuz/.lip presence check (unit B) AND
        // the result-script binding check (unit C1) both run on CREATE of dialogue lines, not on EDITS to existing ones.
        // An edit that adds a spoken response or a result script to an existing INFO produces the same silent-line /
        // dead-script hazard with no note here, so flag it — and point at the on-demand validator (Unit C2 shipped:
        // housecarl_validate_dialogue), which audits voice + result-script coverage AND the topic graph over the
        // edited line and every other line in the topic.
        if (o.Ops.Any(op => string.Equals(op.RecordType, VoiceCheck.InfoCatalogName, StringComparison.Ordinal)))
            sb.Append("note: this edit touched a dialogue line (INFO). Voice (.fuz) and result-script coverage are checked on CREATE, not on edits — ")
              .Append("run housecarl_validate_dialogue on the topic (or its owning quest) to audit voice + result-script coverage and the topic graph over the edited line and every other line in the topic.\n");
        // The touched-record verify (forced ON for in-place — the model-C floor substitute — and opt-in for the new-file
        // lane) renders COMPACT by default and the full field-by-field dump only on full_readback=true (HCBR-2026-06-28-01):
        // the deep dump of N records with large list fields blew past the host token cap and spilled to a file, reading as
        // "only some ops applied". The verify itself is unchanged — this is its OUTPUT, not its detection.
        if (o.ReadBack is { } rb)
        {
            if (fullDump) AppendFullReadback(sb, rb, maxChars);
            else AppendCompactReadback(sb, o.Ops, rb, maxChars);
        }
        if (o.Note is { } note) sb.Append("note: ").Append(note).Append('\n');
        sb.Append(o.InPlace
            ? InPlaceAgainHint("to make more in-place edits to this plugin", file)
            : $"to add more edits to THIS patch, pass into=\"{file}\".");
        sb.Append(Epoch(o));
        return sb.ToString();
    }

    /// <summary>The §2.1.1 stamp, one thin adapter per outcome record over the one construction in
    /// <see cref="WriteSentences.Epoch"/>. The four outcomes are deliberately independent shapes — the adapters
    /// read the property; the SENTENCE lives once.</summary>
    static string Epoch(WritePatchBuilder.PatchOutcome o) => WriteSentences.Epoch(o.Epoch);
    static string Epoch(WritePatchBuilder.CreateOutcome o) => WriteSentences.Epoch(o.Epoch);
    static string Epoch(WritePatchBuilder.RemovalOutcome o) => WriteSentences.Epoch(o.Epoch);
    static string Epoch(WritePatchBuilder.ForwardOutcome o) => WriteSentences.Epoch(o.Epoch);

    /// <summary>The "how to keep going on this plugin" line for a completed IN-PLACE write. It used to fork on a
    /// <c>laneAsName</c> flag, because the spelling differed by surface and MUST match what the CALLING tool
    /// declares (PR #311 round-2 review [medium]): the 1.x tools took the <c>target=</c> + <c>in_place=true</c>
    /// PAIR, while the 2.0 tools (apply / create / remove / forward) declare a single STRING
    /// <c>in_place="X.esp"</c> and no <c>target=</c> at all — so the old text told a 2.0 caller to send an
    /// undeclared parameter plus a BOOLEAN into a string parameter, which fails to bind or goes looking for a
    /// plugin named "true". The demolition catch-up (#468) deleted the 1.x half of that fork's population, which
    /// left the pair-spelling arm reachable from no registered tool while still being the flag's DEFAULT — an arm
    /// that cannot fail, armed to catch the next tool that forgets to pass the flag. #468 round 1 measured it:
    /// replacing that arm's text outright left the whole suite green. So the fork is gone rather than guarded.
    /// The rule it encoded stands and is now structural — there is one spelling because there is one lane.
    /// Same rule the locate contract's <c>offerModParam</c> encodes on the refusal side: a response must never
    /// send someone to a parameter their tool does not expose.</summary>
    static string InPlaceAgainHint(string verb, string file) =>
        $"{verb}, pass in_place=\"{file}\" again (no further confirmation needed for it).";

    /// <summary>#225 — the dry_run=true confirmation: the SAME pipeline ran (winner resolve, pre-flight, every verb
    /// applied in memory, the reference-resolution check) and stopped AT the point of no return, so this reports what
    /// WOULD change with NOTHING on disk. The header says so first (Q3 — a dry run must never read like a write);
    /// masters are the labeled link-derived PREVIEW; per-op lines carry the would-be values; the optional deep dump is
    /// the in-memory would-be content. A refusal never reaches here — a dry run refuses EXACTLY like the real call.</summary>
    static string RenderDryRun(WritePatchBuilder.PatchOutcome o, int maxChars, bool fullDump)
    {
        var file = Path.GetFileName(o.OutputPath);
        var sb = new StringBuilder();
        sb.Append(WriteSentences.DryRunHeader);
        sb.Append(WriteSentences.DryRunWouldWrite(o.InPlace, o.Extended, file, "edit"));
        sb.Append(WriteSentences.DryRunMasters(o.Masters));
        sb.Append(o.Ops.Count).Append(o.Ops.Count == 1 ? " edit would apply:\n" : " edits would apply:\n");
        // Budgeted for the same reason as the real render's loop, with the dry run's own arm on the cut notice:
        // a dry run wrote nothing, so it must not say the edits were applied.
        int dryCap = WriteSentences.Cap(maxChars);
        for (int i = 0; i < o.Ops.Count; i++)
        {
            if (sb.Length >= dryCap)
            {
                sb.Append("  ... [truncated: ").Append(i).Append(" of ").Append(o.Ops.Count)
                  .Append(" edit(s) listed at max_chars=").Append(dryCap).Append("; ")
                  .Append(WriteSentences.RowsCutOperationIntact(true, "applied"))
                  .Append(" — ").Append(ApplyAgainRemedy(o, file)).Append("]\n");
                break;
            }
            var op = o.Ops[i];
            sb.Append("  ").Append(op.RecordType).Append(' ').Append(op.Target).Append("  ").Append(op.Label)
              .Append(op.After is not null ? "  -> would become " + op.After : "  -> would apply").Append('\n');
        }
        if (fullDump && o.ReadBack is { } rb) AppendFullReadback(sb, rb, maxChars, dryRun: true);
        if (o.Note is { } note) sb.Append("note: ").Append(note).Append('\n');
        sb.Append(WriteSentences.DryRunClose("every op passed resolve + pre-flight", "apply"))
          .Append(Epoch(o));
        return sb.ToString();
    }

    /// <summary>The full_readback=true read-back section (HCBR-2026-06-11-02 wave (b)): each touched/created record IN FULL,
    /// re-read from the written file on disk. Labeled as exactly that — the written file's content, NOT load-order
    /// truth (the patch wins nothing until enabled in MO2) — so the caller can't mistake it for a winner read.
    /// Char-budget-bounded with an explicit notice (Q3), same convention as the read tools — now at the LOWER
    /// <see cref="Wire.ReadbackMaxChars"/> default so the cut-off output stays under the host token ceiling and the
    /// truncation note actually reaches the caller (HCBR-2026-06-28-01).</summary>
    static void AppendFullReadback(StringBuilder sb, IReadOnlyList<WritePatchBuilder.FullReadback> rb, int maxChars,
        bool dryRun = false)
    {
        int cap = WriteSentences.ReadbackCap(maxChars);
        // #225: a dry run's records are read from the IN-MEMORY would-be content — say so, never imply a file exists.
        sb.Append(dryRun
            ? "full preview — the ENTIRE record(s) as they WOULD be written, read from the in-memory would-be content (nothing is on disk):\n"
            : "full read-back — the ENTIRE record(s) as written, re-read from the patch file on disk " +
              "(the written file's content, NOT load-order truth; the patch wins nothing until enabled + sorted in MO2):\n");
        string hint = dryRun ? "; raise max_chars" : "; raise max_chars, or enable the patch in MO2 and use housecarl_read_record";
        for (int i = 0; i < rb.Count; i++)
        {
            if (sb.Length >= cap)
            {
                sb.Append("  ... [truncated: full read-back rendered ").Append(i).Append(" of ").Append(rb.Count)
                  .Append(" record(s) at max_chars=").Append(cap).Append(hint).Append("]\n");
                return;
            }
            var r = rb[i];
            if (r.Error is not null) { sb.Append("  ").Append(r.Target).Append("  error: ").Append(r.Error).Append('\n'); continue; }
            var rec = r.Record!;
            sb.Append("  ").Append(rec.Type).Append(' ').Append(rec.FormKey).Append("  editorid=").Append(rec.EditorId ?? "<none>").Append('\n');
            foreach (var f in rec.Fields)
            {
                if (sb.Length >= cap)
                {
                    sb.Append("    ... [truncated: this record's field lines hit max_chars=").Append(cap)
                      .Append("; ").Append(rb.Count - i - 1).Append(" further record(s) not rendered")
                      .Append(hint).Append("]\n");
                    return;
                }
                sb.Append("    ").Append(f.Path).Append(" = ").Append(f.HasValue ? f.Token : f.Note).Append('\n');
            }
        }
    }

    /// <summary>The DEFAULT (full_readback=false) render of the touched-record verify (HCBR-2026-06-28-01). The forced
    /// in-place re-read still RAN — corruption DETECTION is unchanged; this only reports it COMPACTLY so it can't overflow
    /// the host token cap the way the deep dump did (which spilled to a file and read as "only some ops applied"). One line
    /// per record: a re-read-CLEAN marker + field count, OR the NAMED re-read failure (Q3); then the "what landed" identity
    /// (the new scalar value, or the touched list element + new count) for each op that touched that record. Covers ALL N
    /// records — bounded by the same char cap with an explicit truncation note, never the silent spill the forced deep dump
    /// produced. The full field-by-field dump is one full_readback=true away.</summary>
    static void AppendCompactReadback(StringBuilder sb, IReadOnlyList<WritePatchBuilder.OpResult> ops,
        IReadOnlyList<WritePatchBuilder.FullReadback> rb, int maxChars)
    {
        int cap = WriteSentences.ReadbackCap(maxChars);
        // The banner covers what it can now stand behind: every edited record WAS re-read off the written file, and
        // each per-op clause below says whether it is the file's answer or the applied edit's own reading.
        sb.Append("verified — every edited record re-read off the written file (compact; pass readback=true for the ")
          .Append("full field-by-field dump):\n");
        for (int i = 0; i < rb.Count; i++)
        {
            if (sb.Length >= cap)
            {
                sb.Append("  ... [truncated: ").Append(i).Append(" of ").Append(rb.Count)
                  .Append(" record(s) shown at max_chars=").Append(cap).Append("; raise max_chars]\n");
                return;
            }
            var r = rb[i];
            // A re-read that failed is a real inconsistency — surface it LOUD and NAMED (the whole reason the in-place
            // verify is forced on), never folded into the clean count.
            if (r.Error is not null) { sb.Append("  ✗ ").Append(r.Target).Append(" — ").Append(r.Error).Append('\n'); continue; }
            var rec = r.Record!;
            sb.Append("  ✓ ").Append(rec.Type).Append(' ').Append(rec.FormKey)
              .Append(" — re-read clean (").Append(rec.Fields.Count).Append(" field(s))");
            // #308: the per-op clause is the FILE's answer when the file gave one (LandedOnDisk), and is marked as the
            // applied edit's claim when it did not — the banner above says "re-read off the written file", and this
            // line used to carry a memory-derived descriptor under it without saying so.
            var landed = ops.Where(op => op.Target == r.Target && (op.LandedOnDisk ?? op.Landed) is not null)
                             .Select(op => $"{op.Label}: {op.LandedOnDisk ?? op.Landed}" + LandedProvenance(op))
                             .ToList();
            if (landed.Count > 0) sb.Append("; ").Append(string.Join("; ", landed));
            sb.Append('\n');
        }
    }

    /// <summary>#308 — where a per-op "what landed" clause CAME FROM, when it is not the plain file answer. Silence
    /// means the file was re-read for this op and agreed; the two marked cases are a file that could not answer for
    /// the op, and an op a later op in the same call superseded (whose reading is a mid-sequence one the final file
    /// cannot corroborate — comparing it anyway is what reported correct multi-op writes as NOT landed).</summary>
    static string LandedProvenance(WritePatchBuilder.OpResult op) =>
        op.SupersededInCall ? " [as applied — a later op in this call wrote the same field; the file shows that op's result]"
        : op.LandedOnDisk is not null ? ""
        // The same split json makes, for the same reason: asserting the file was re-opened and could not answer, on a
        // call where the verify never ran, is a claim about a read that did not happen. The first arm is the reachable
        // one — the compare pass's own catch marks its ops ATTEMPTED-and-unanswered. The second is defensive: every
        // in-place op the verify reached is attempted, and the ops it does not reach (the appended SNAM syncs) carry
        // no Landed and are filtered out of this clause upstream, so no product path reaches it today (review [nit]).
        : op.VerifyAttempted ? " [as applied — the re-opened file did not answer for this op]"
        : " [as applied — this lane ran no file check]";

    /// <summary>Confirmation for housecarl_remove: what was dropped, the patch's now-lean masters, and how many
    /// records remain (0 ⇒ inert). On refusal, the named reason (Q3) so the caller can fix and retry.</summary>
    internal static string RenderRemoval(WritePatchBuilder.RemovalOutcome o, int maxChars = 0)   // internal: housecarl_remove renders the same outcome
    {
        if (o.NeedsAcknowledge) return o.Error! + Epoch(o);  // the first-touch in-place CONSENT prompt — a required confirmation, NOT an error (Q3)
        if (!o.Success) return "error: " + o.Error + Epoch(o);
        var file = Path.GetFileName(o.OutputPath);
        var modFolder = Path.GetFileName(Path.GetDirectoryName(o.OutputPath) ?? "");
        var sb = new StringBuilder();
        sb.Append("removed ").Append(o.Removed.Count).Append(o.Removed.Count == 1 ? " record from " : " records from ")
          .Append(file);
        if (o.InPlace)
            sb.Append(" IN PLACE (").Append(o.Bytes).Append(" bytes; ")
              .Append(o.RemainingRecords).Append(o.RemainingRecords == 1 ? " record remains" : " records remain")
              .Append(" — ").Append(WriteSentences.InPlaceRewritten).Append(")\n")
              .Append(WriteSentences.InPlaceModFolder(modFolder));
        else
        {
            sb.Append(" (").Append(o.Bytes).Append(" bytes; ")
              .Append(o.RemainingRecords).Append(o.RemainingRecords == 1 ? " record remains)\n" : " records remain)\n");
            // Not NewOrExtendedArtifact: a removal creates no artifact and grows no patch, so it has neither of that
            // sentence's two claims to make — only which folder the now-leaner file sits in.
            sb.Append("mod folder: ").Append(modFolder).Append('\n');
        }
        // Budgeted like every other write render (PR #311 review [medium]): removal is SET-VALUED now, so the
        // unbounded case — a few hundred dropped overrides — is the expected one, not an edge, and max_chars=
        // promises "past it trailing rows are dropped with an explicit notice (never silent)". Without this the
        // rows all rendered and the HOST cut the oversized response out-of-band: exactly the silent cut the
        // parameter's own description says never happens. The masters line and the closing guidance below are
        // outside the budget deliberately — they are the accounting a truncated report still needs.
        int cap = WriteSentences.Cap(maxChars);
        for (int i = 0; i < o.Removed.Count; i++)
        {
            if (sb.Length >= cap)
            {
                // NOT "raise max_chars to see the rest" (PR #311 review 6 [medium]). RenderCreate's comment cited
                // "a repeated remove is refused" as the reason that wording was SAFE here — which is also exactly
                // why it is a dead end: re-issuing the call answers "not carried by patch '<file>'; NOTHING
                // removed", because the records are already gone. Nothing is actually lost, though, and saying so
                // is the honest remedy: removal is all-or-nothing, so the rows ARE the formids= the caller passed.
                sb.Append("  ... [truncated: ").Append(i).Append(" of ").Append(o.Removed.Count)
                  .Append(" removed record(s) listed at max_chars=").Append(cap).Append("; ")
                  .Append(WriteSentences.RowsCutOperationIntact(false, "removed"))
                  .Append(" — ").Append(RemovedRowsRemedy).Append("]\n");
                break;
            }
            var r = o.Removed[i];
            sb.Append("  - ").Append(r.RecordType).Append(' ').Append(r.Target).Append("  ")
              .Append(r.EditorId ?? "<no editorid>").Append('\n');
        }
        sb.Append(WriteSentences.Masters(o.Masters));
        if (o.Note is { } note) sb.Append("note: ").Append(note).Append('\n');
        if (o.InPlace)
            sb.Append(o.RemainingRecords == 0
                ? "this plugin now carries no records — it's an inert shell; disable or delete the mod in MO2 if you don't need it."
                : InPlaceAgainHint("to remove more records from this plugin in place", file));
        else
            sb.Append(o.RemainingRecords == 0
                ? "this patch now carries no records — it's inert; disable or delete the mod folder in MO2 if you don't need it."
                : "re-sort in MO2 if dropping this override changes a conflict winner.");
        sb.Append(Epoch(o));
        return sb.ToString();
    }

    /// <summary>Confirmation for housecarl_forward: per record, WHAT was copied (type + FormID + editorid), the
    /// source it was copied FROM, and the current winner it out-ranks once enabled — with a redundant-forward NOTE when
    /// the copied version was already winning (Q3 — never silently a no-op). On refusal, the named reason so the caller
    /// can fix and retry. Optional full read-back rides along (the pre-enable verify that the copy is the source's).</summary>
    internal static string RenderForward(WritePatchBuilder.ForwardOutcome o, int maxChars = 0)   // internal: the dry-run guard asserts the would-be phrasing
    {
        if (o.NeedsAcknowledge) return o.Error! + Epoch(o);  // the first-touch in-place CONSENT prompt — a required confirmation, NOT an error (Q3)
        if (!o.Success) return "error: " + o.Error + Epoch(o);
        var file = Path.GetFileName(o.OutputPath);
        var modFolder = Path.GetFileName(Path.GetDirectoryName(o.OutputPath) ?? "");
        var sb = new StringBuilder();
        if (o.DryRun)
        {
            // #225 — the SAME dry-run sentences the apply lane renders, from the same source: say NOTHING was
            // written first, then what the real call would do.
            sb.Append(WriteSentences.DryRunHeader);
            sb.Append(WriteSentences.DryRunWouldWrite(o.InPlace, o.Extended, file, "forward into"));
            sb.Append(WriteSentences.DryRunMasters(o.Masters));
        }
        else if (o.InPlace)
            sb.Append("forwarded into ").Append(file).Append(" IN PLACE (").Append(o.Bytes)
              .Append(" bytes — ").Append(WriteSentences.InPlaceRewritten).Append(")\n")
              .Append(WriteSentences.InPlaceModFolder(modFolder));
        else
            sb.Append(WriteSentences.NewOrExtendedArtifact(o.Extended, file, o.Bytes, modFolder));
        if (!o.DryRun)
            sb.Append(WriteSentences.Masters(o.Masters));
        // WHICH copy an off-order source read — a fact, not derivable from the name (several install layers can provide
        // one filename, and only one of them was opened). Stated once for the call, because one source= serves it all.
        if (o.OffOrderSource is { } oo)
        {
            sb.Append("source: '").Append(oo.Plugin).Append("' is NOT in the active load order — the bodies were read OFF-ORDER from ")
              .Append(oo.Path).Append(" (").Append(oo.Where).Append("). The epoch below fingerprints the ACTIVE order, which that file is outside of.\n");
            if (oo.ExcludedReason is { } exWhy)
                sb.Append("  NOTE: that file is this session's copy of a plugin EXCLUDED from the index (").Append(exWhy)
                  .Append(") — addressing it by PATH reads it directly, which is why this resolved at all. Copying one record out is not the ")
                  .Append("whole-file re-serialize the exclusion refusal guards, but the body is only what Mutagen could parse: verify it (readback=true) before relying on it.\n");
        }
        sb.Append(o.DryRun ? "would forward " : "forwarded ").Append(o.Forwarded.Count)
          .Append(o.Forwarded.Count == 1 ? " record:\n" : " records:\n");
        // Budgeted for the same reason as the created-records block: formids= is set-valued, each row is long (type +
        // FormKey + editorid + the source clause + a REPLACED / redundant / out-ranks bracket), and the json twin
        // already truncates the identical array (PR #311 review 3 [medium]).
        int fwdCap = WriteSentences.Cap(maxChars);
        for (int fi = 0; fi < o.Forwarded.Count; fi++)
        {
            if (sb.Length >= fwdCap)
            {
                sb.Append("  ... [truncated: ").Append(fi).Append(" of ").Append(o.Forwarded.Count)
                  .Append(" record(s) listed at max_chars=").Append(fwdCap)
                  .Append("; ").Append(WriteSentences.RowsCutOperationIntact(o.DryRun, "forwarded"))
                  .Append(" — ").Append(ForwardAgainRemedy(o, file)).Append(']')
                  .Append('\n');
                break;
            }
            var f = o.Forwarded[fi];
            sb.Append("  ").Append(f.RecordType).Append(' ').Append(f.Target).Append("  ").Append(f.EditorId ?? "<no editorid>")
              .Append(o.DryRun ? "  — would be copied from " : "  — copied from ").Append(f.FromPlugin);
            // #324 — the sentence a caller acts on has to match what the replace now does. It used to say "the old
            // body is gone" flat, which was true when the drop took the child group with it. It no longer does: the
            // FIELDS are replaced and everything nested under the record is carried across. Left unchanged, the line
            // reads as a clean revert over a cell whose forty placed refs are still in the file — the caller either
            // ships what they think they removed, or re-creates a dialogue line they never lost. The count is stated
            // rather than implied, because "nested records were kept" cannot tell nothing-was-there from twelve-kept.
            if (f.ReplacedExisting)
                sb.Append(f.PreservedChildren > 0
                    ? (o.DryRun
                        ? $"  [would REPLACE the patch's own existing override of this record — the old FIELDS would be gone (xEdit's copy-as-override-into overwrite), but the {f.PreservedChildren} record(s) nested under it would be KEPT]"
                        : $"  [REPLACED the patch's own existing override of this record — the old FIELDS are gone (xEdit's copy-as-override-into overwrite); the {f.PreservedChildren} record(s) nested under it were KEPT]")
                    : (o.DryRun
                        ? "  [would REPLACE the patch's own existing override of this record — the old body would be gone (xEdit's copy-as-override-into overwrite); it carries no nested records]"
                        : "  [REPLACED the patch's own existing override of this record — the old body is gone (xEdit's copy-as-override-into overwrite); it carried no nested records]"));
            if (f.WasAlreadyWinner)
                sb.Append("  [NOTE: this source IS already the load-order winner — the override just re-asserts the content that already wins (a no-op in effect)]");
            else if (f.PriorWinner is null)
                // No active plugin defines this record — ordinary on the self-origin path (a record originating in a
                // patch not enabled yet). The old sentinel rendered "out-ranks the current winner (none)", a ranking
                // against a winner that does not exist (PR #313 review 3 [low]).
                sb.Append("  (no active plugin currently defines this record — nothing to out-rank; it takes effect once this patch is enabled)");
            else
                sb.Append("  (out-ranks the current winner ").Append(f.PriorWinner).Append(" once this patch is enabled + sorted above it)");
            sb.Append('\n');
        }
        if (o.ReadBack is { } rb) AppendFullReadback(sb, rb, maxChars, dryRun: o.DryRun);
        if (o.Note is { } note) sb.Append("note: ").Append(note).Append('\n');
        sb.Append(o.DryRun
            ? WriteSentences.DryRunClose("every record resolved from its source", "forward")
            : o.InPlace
                ? InPlaceAgainHint("to forward more into this plugin", file)
                : $"to forward more into THIS patch (incl. from a different source plugin), pass into=\"{file}\".");
        sb.Append(Epoch(o));
        return sb.ToString();
    }

    /// <summary>Confirmation for housecarl_create_plugin: the empty plugin's path + mod folder, its ESL flag, master
    /// header (none), record count (0) and byte size, plus the MO2 enable reminder and what the trigger does. On
    /// refusal, the named reason (Q3) so the caller can fix and retry.</summary>
    static string RenderCreatePlugin(WritePatchBuilder.CreatePluginOutcome o)
    {
        if (!o.Success) return "error: " + o.Error;
        var file = Path.GetFileName(o.OutputPath);
        var modFolder = Path.GetFileName(Path.GetDirectoryName(o.OutputPath) ?? "");
        var sb = new StringBuilder();
        sb.Append("wrote ").Append(file).Append(o.Esl ? " (header-only, ESL-flagged; " : " (header-only; ")
          .Append(o.Bytes).Append(" bytes, ").Append(o.RecordCount).Append(o.RecordCount == 1 ? " record)\n" : " records)\n");
        sb.Append("mod folder: ").Append(modFolder).Append("  — enable + sort it in MO2 to use it\n");
        sb.Append(WriteSentences.Masters(o.Masters));
        sb.Append("this is a trigger/placeholder plugin: it carries no records, so it changes nothing in game by itself — ")
          .Append("its only job is to make the basename '").Append(Path.GetFileNameWithoutExtension(file))
          .Append("' present in the load order (so a basename-bound SKSE config resolves, a FormID range is reserved, etc.).");
        return sb.ToString();
    }

    /// <summary>Confirmation for housecarl_compact_plugin: where the compacted P′ landed (new file vs in place), the
    /// record accounting (originating renumbered / overrides kept), masters, the external-referencer verdict (clean,
    /// or the per-plugin repoint results), the identify-pass coverage, and the un-remappable-script reminder (Q3). The
    /// NeedsAcknowledge prompt (a required in-place consent) is returned verbatim, not as an error; on refusal the named
    /// reason so the caller can fix and retry.</summary>
    internal static string RenderCompact(WritePatchBuilder.CompactOutcome o)   // internal: the seq-regen-guard renders a failure outcome to prove the SEQ WARN reaches user output
    {
        if (o.NeedsAcknowledge) return o.Error!;            // the in-place CONSENT prompt — a required confirmation, NOT an error (Q3)
        if (!o.Success) return "error: " + o.Error;
        var file = Path.GetFileName(o.OutputPath);
        var modFolder = Path.GetFileName(Path.GetDirectoryName(o.OutputPath) ?? "");
        var sb = new StringBuilder();
        if (o.InPlace)
            sb.Append("compacted ").Append(file).Append(" IN PLACE (").Append(o.Bytes)
              .Append(" bytes — ").Append(WriteSentences.InPlaceRewritten).Append(")\n")
              .Append(WriteSentences.InPlaceModFolder(modFolder));
        else
            sb.Append("wrote compacted ").Append(file).Append(" (new plugin; ").Append(o.Bytes).Append(" bytes)\n")
              .Append("mod folder: ").Append(modFolder).Append("  — enable it and DISABLE the original '").Append(file)
              .Append("' mod in MO2 (same basename — MO2 serves one). Review in xEdit first.\n");

        int overrides = o.RecordsCopied - o.RecordsRenumbered;
        sb.Append(o.Esl ? "light master (ESPFE): yes — " : "renumbered (not light-flagged): ");
        sb.Append(o.RecordsRenumbered).Append(o.RecordsRenumbered == 1 ? " originating record renumbered " : " originating records renumbered ");
        sb.Append(o.Esl ? "into the light range 0x800–0xFFF" : "contiguously from 0x800");
        if (overrides > 0) sb.Append("; ").Append(overrides).Append(overrides == 1 ? " override kept at its master FormID" : " overrides kept at their master FormIDs");
        sb.Append(".\n");
        sb.Append(WriteSentences.Masters(o.Masters));

        if (o.ExternalPlugins.Count == 0)
            sb.Append("external referencers: none — clean compaction (nothing outside this plugin pointed at a renumbered record).\n");
        else if (o.Repointed.Count > 0)
        {
            int ok = o.Repointed.Count(r => r.Success);
            sb.Append("external referencers repointed in place: ").Append(ok).Append('/').Append(o.Repointed.Count).Append(" succeeded\n");
            foreach (var rep in o.Repointed)
                sb.Append("  ").Append(rep.Success ? "OK   " : "FAIL ").Append(rep.Plugin)
                  .Append(rep.Success ? "" : "  — " + rep.Error).Append('\n');
        }
        else
        {
            sb.Append("external referencers (").Append(o.ExternalPlugins.Count).Append(", NOT repointed):\n");
            foreach (var pl in o.ExternalPlugins.Take(25)) sb.Append("  - ").Append(pl).Append('\n');
            if (o.ExternalPlugins.Count > 25) sb.Append("  - … (+").Append(o.ExternalPlugins.Count - 25).Append(" more)\n");
        }

        // External OVERRIDERS (gap #2) — plugins that OVERRIDE a renumbered record (not just reference it). They orphan
        // after the renumber (the override points at a base FormID that no longer exists), and houseCARL CANNOT auto-repoint
        // an override — that's an identity change, not a link rewrite — so this is a WARN (xEdit parity), not the referencer
        // refuse/repoint path. Named per-plugin so the user can re-point or rebuild them (better than xEdit's blanket warning).
        if (o.ExternalOverriders is { Count: > 0 } overriders)
        {
            sb.Append("external OVERRIDERS (").Append(overriders.Count).Append("): these plugins OVERRIDE a renumbered record and will ")
              .Append("ORPHAN after the renumber — houseCARL can't auto-repoint an override (identity change, not a link). ")
              .Append("Re-point or rebuild them against the new FormIDs, or don't enable the compacted plugin over them:\n");
            foreach (var pl in overriders.Take(25)) sb.Append("  ! ").Append(pl).Append('\n');
            if (overriders.Count > 25) sb.Append("  ! … (+").Append(overriders.Count - 25).Append(" more)\n");
        }

        if (o.UnscannableRecords > 0)
        {
            sb.Append("note: ").Append(o.UnscannableRecords).Append(" record(s) couldn't be scanned in the external-reference pass, so an ")
              .Append("'external referencers: none' may be incomplete — verify in xEdit. Samples: ").Append(string.Join("; ", o.UnscannableSamples)).Append('\n');
        }
        sb.Append("identify-pass scanned ").Append(o.PluginsScanned).Append(" plugin(s) for external references.\n");
        // Compact's break is the other half of merge's: the plugin NAME survives a compaction, so what moves is the
        // object id — and only the ids this run actually moved, which the accounting above states. Bounded by the same
        // claim rule; see WriteSentences.CompactRuntimeConfigs for it and for the two wrong models that produced it.
        sb.Append(WriteSentences.CompactRuntimeConfigs);

        AppendFacegenCarry(sb, o.AssetRename, o.InPlace);
        AppendVoiceCarry(sb, o.VoiceRename, o.InPlace);
        AppendSeqRegen(sb, o.SeqRegen, o.InPlace);

        if (o.Note is { } note) sb.Append("note: ").Append(note).Append('\n');
        sb.Append("reminder: FormIDs compiled into Papyrus (.pex hardcoded / GetFormFromFile) and any Mutagen-delta ")
          .Append("residual are NOT remappable — verify scripted records after compacting.");
        return sb.ToString();
    }

    // FormID-keyed assets carried WITH the renumber (Waves A1–A3, shared by compact AND merge — one render home, no
    // drift). The renumber moved records to new FormIDs (a merge additionally to a new plugin NAME), so the engine looks
    // facegen/voice up under NEW paths and a shipped .seq goes stale; carrying/refreshing them is what stops a renumbered
    // NPC mod silently dark-facing, a voiced mod going mute, and SGE quests never starting. Reported, not silent (Q3).
    // inPlace is always false for merge (it has no in-place lane).

    static void AppendFacegenCarry(StringBuilder sb, AssetRenameOutcome? outcome, bool inPlace)
    {
        if (outcome is not { } ar) return;
        if (ar.FacegenFilesCarried > 0)
            sb.Append("facegen: carried ").Append(ar.FacegenFilesCarried).Append(ar.FacegenFilesCarried == 1 ? " file for " : " files for ")
              .Append(ar.FacegenNpcsCarried).Append(ar.FacegenNpcsCarried == 1 ? " NPC to the new FormIDs" : " NPCs to the new FormIDs")
              .Append(inPlace ? " (old-FormID facegen left as harmless orphans).\n" : " (in the new mod folder — enabling it carries the faces).\n");
        else if (ar.NpcCount > 0 && ar.Failures.Count == 0)
            sb.Append("facegen: none found for ").Append(ar.NpcCount).Append(ar.NpcCount == 1 ? " NPC — nothing to carry.\n" : " NPCs — nothing to carry.\n");
        foreach (var f in ar.Failures.Take(25)) sb.Append("  facegen WARN: ").Append(f).Append('\n');
        if (ar.Failures.Count > 25) sb.Append("  facegen WARN: … (+").Append(ar.Failures.Count - 25).Append(" more)\n");
        if (ar.ReadIncomplete)
            sb.Append("  note: a BSA failed to read this scan, so a 'no facegen' result may be incomplete — verify NPC faces in-game.\n");
    }

    static void AppendVoiceCarry(StringBuilder sb, VoiceCarryOutcome? outcome, bool inPlace)
    {
        if (outcome is not { } vr) return;
        if (vr.FilesCarried > 0)
            sb.Append("voice: carried ").Append(vr.FilesCarried).Append(vr.FilesCarried == 1 ? " file for " : " files for ")
              .Append(vr.LinesCarried).Append(vr.LinesCarried == 1 ? " dialogue line to the new FormIDs" : " dialogue lines to the new FormIDs")
              .Append(inPlace ? " (old-FormID voice left as harmless orphans).\n" : " (in the new mod folder — enabling it carries the voice).\n");
        else if (vr.FilesScanned > 0 && vr.Failures.Count == 0)
            sb.Append("voice: ").Append(vr.FilesScanned).Append(vr.FilesScanned == 1 ? " voice file found, none keyed to a renumbered line" : " voice files found, none keyed to a renumbered line")
              .Append(" — nothing to carry.\n");
        foreach (var f in vr.Failures.Take(25)) sb.Append("  voice WARN: ").Append(f).Append('\n');
        if (vr.Failures.Count > 25) sb.Append("  voice WARN: … (+").Append(vr.Failures.Count - 25).Append(" more)\n");
        if (vr.ReadIncomplete)
            sb.Append("  note: a BSA failed to read this scan, so a 'no voice' result may be incomplete — verify voiced lines in-game.\n");
    }

    static void AppendSeqRegen(StringBuilder sb, SeqRegenOutcome? outcome, bool inPlace)
    {
        if (outcome is not { } sr) return;
        if (sr.Written)
            sb.Append("SEQ: regenerated — ").Append(sr.SgeQuestCount).Append(sr.SgeQuestCount == 1 ? " start-game-enabled quest" : " start-game-enabled quests")
              .Append(inPlace ? " (.seq rewritten in place).\n" : " (.seq in the new mod folder's SEQ\\ — enabling it starts the quests).\n");
        foreach (var f in sr.Failures.Take(25)) sb.Append("  SEQ WARN: ").Append(f).Append('\n');
        if (sr.Failures.Count > 25) sb.Append("  SEQ WARN: … (+").Append(sr.Failures.Count - 25).Append(" more)\n");
    }

    /// <summary>Merge confirmation (A4): the merged plugin's identity + the MO2 swap instruction, per-donor id
    /// accounting, cross-donor conflict resolutions (load-order winner — reported, never silent), the WARN surfaces
    /// (external referencers/overriders with the remedy), the asset-carry accounting, and the saves/ESL pointers.
    /// On refusal, the named reason (Q3). internal: the merge guard asserts warnings reach user output.</summary>
    internal static string RenderMerge(WritePatchBuilder.MergeOutcome o)
    {
        if (!o.Success) return "error: " + o.Error;
        var file = Path.GetFileName(o.OutputPath);
        var modFolder = Path.GetFileName(Path.GetDirectoryName(o.OutputPath) ?? "");
        var sb = new StringBuilder();
        // The operation's SHAPE is classified ONCE here and consumed by every sentence that varies with it — this
        // headline, the per-donor renumber cause, and the external-referencer remedy order. One donor is the RENAME
        // case (#345): "from 1 donors" would be both ungrammatical and a misdescription, nothing having been combined.
        // Sentences that do NOT vary by shape (masters, the swap, the asset carries, the saves reminder) read the same
        // for both and are deliberately left alone.
        bool isRename = o.Donors.Count == 1;
        if (isRename)
        {
            // What a rename did to the records is exactly what the accounting line below reports, so this sentence is
            // DERIVED from it rather than asserting alongside it. A pure-override donor — the mis-named patch this
            // capability exists for — originates nothing, so nothing is re-keyed, and claiming "its records under a
            // new identity" would be false in the very case the feature was asked for.
            sb.Append("wrote ").Append(file).Append(" (new plugin; ").Append(o.Bytes).Append(" bytes) — a RENAME of ")
              .Append(o.Donors[0]).Append(": one donor, so there is nothing to combine — ");
            // Three arms, because there are three things the accounting can say and each sentence may claim only the
            // quantity it read. The middle arm previously asserted overrides it never looked at, which made it wrong
            // for a donor with no records at all — and an empty plugin is not hypothetical: the swap instruction in
            // this same report tells the caller to create one to keep a donor .bsa loading.
            int headlineOverrides = o.RecordsCopied - o.RecordsRenumbered;
            if (o.RecordsRenumbered > 0)
                sb.Append(o.RecordsRenumbered).Append(o.RecordsRenumbered == 1 ? " record moves" : " records move")
                  .Append(" to the new plugin's identity.\n");
            else if (headlineOverrides > 0)
                sb.Append("it originates no records of its own, so nothing is re-keyed; its ").Append(headlineOverrides)
                  .Append(headlineOverrides == 1 ? " override is" : " overrides are")
                  .Append(" now served by a plugin under a new name.\n");
            else
                sb.Append("it carries no records at all, so nothing moved and nothing is overridden — an empty plugin ")
                  .Append("under a new name.\n");
        }
        else
            sb.Append("wrote merged ").Append(file).Append(" (new plugin; ").Append(o.Bytes).Append(" bytes) from ")
              .Append(o.Donors.Count).Append(" donors: ").Append(string.Join(", ", o.Donors)).Append('\n');
        sb.Append("mod folder: ").Append(modFolder).Append("  — review in xEdit, then enable + sort it in MO2.\n");
        // The swap is PLUGIN-level, not mod-level (merge is a RECORDS op): the merged records still reference the donors'
        // meshes/textures/scripts/BSA contents BY PATH, and those files live in the donor mod folders — only the
        // FormID-keyed facegen/voice/seq were carried. "Disable the donor mods" (compact's instruction, where the output
        // shares the source's basename) would yank all of that out of the VFS with every warning light green.
        sb.Append("the swap: deactivate the donor PLUGINS (right pane) — their files are untouched — but KEEP the donor mod ")
          .Append("folders enabled (left pane): the merged records still load the donors' meshes/textures/scripts by path; ")
          .Append("only facegen/voice/seq were carried. If a donor ships a .bsa, it stops auto-loading once its plugin is ")
          .Append("deactivated — extract it into the mod folder (housecarl_bsa_extract) or load it via a same-named dummy ")
          .Append("plugin (housecarl_create_plugin).\n");

        int overrides = o.RecordsCopied - o.RecordsRenumbered;
        sb.Append(o.RecordsRenumbered).Append(o.RecordsRenumbered == 1 ? " originating record" : " originating records")
          .Append(" merged under ").Append(o.OutputName);
        if (overrides > 0) sb.Append("; ").Append(overrides).Append(overrides == 1 ? " override kept at its master FormID" : " overrides kept at their master FormIDs");
        sb.Append(".\n");
        foreach (var d in o.DonorRemaps)
            sb.Append("  ").Append(d.Donor).Append(": ").Append(d.Kept).Append(" object id(s) kept, ")
              .Append(d.Renumbered)                                       // one donor has nothing to collide WITH, so
              .Append(isRename ? " renumbered (below-floor)\n"            // only one of the two causes can apply
                               : " renumbered (id collisions / below-floor)\n");
        sb.Append(WriteSentences.Masters(o.Masters));

        if (o.Conflicts.Count == 0)
            sb.Append("cross-donor conflicts: none — no record was carried by more than one donor.\n");
        else
        {
            sb.Append("cross-donor conflicts (").Append(o.Conflicts.Count).Append(") — each resolved to the LOAD-ORDER WINNER (the losing version is NOT in the merge; any un-relisted nested children were grafted):\n");
            foreach (var c in o.Conflicts.Take(25))
                sb.Append("  ").Append(c.RecordType).Append(' ').Append(c.Key).Append("  ").Append(c.WinnerDonor).Append(" won over ").Append(c.LoserDonor).Append('\n');
            if (o.Conflicts.Count > 25) sb.Append("  … (+").Append(o.Conflicts.Count - 25).Append(" more)\n");
        }

        // The A4 posture: WARN loud + proceed — the donors stay installed and ACTIVE until the user swaps in MO2, so
        // nothing is broken at write time; the report names every affected plugin and the remedy. (Unlike compact, which
        // refuses on referencers: a compact's renumber takes effect under the SAME plugin name, a merge's only when the
        // user disables the donors — the user holds the switch here.)
        if (o.ExternalPlugins.Count > 0)
        {
            sb.Append("WARNING — ").Append(o.ExternalPlugins.Count).Append(" plugin(s) OUTSIDE the merge REFERENCE donor records. Their references break ")
              // Adding the patch to the donor set yields a COMBINED plugin — a different operation from the one that
              // was asked for — so a rename leads with the remedy that keeps it a rename, and offers combining second.
              .Append(isRename
                  ? "the moment you deactivate the donor plugin: re-point them at '"
                  : "the moment you deactivate the donor plugins: include them in the merge set (re-run with them added), or re-point them at '")
              .Append(o.OutputName)
              .Append(isRename ? "' before the swap — or, to combine them instead, re-run with them added as donors:\n"
                               : "' before the swap:\n");
            foreach (var pl in o.ExternalPlugins.Take(25)) sb.Append("  ! ").Append(pl).Append('\n');
            if (o.ExternalPlugins.Count > 25) sb.Append("  ! … (+").Append(o.ExternalPlugins.Count - 25).Append(" more)\n");
        }
        else sb.Append("external referencers: none — no plugin outside the merge has a record that links to a donor record.\n");
        if (o.ExternalOverriders.Count > 0)
        {
            sb.Append("WARNING — ").Append(o.ExternalOverriders.Count).Append(" plugin(s) OUTSIDE the merge OVERRIDE a donor record; those overrides ")
              .Append(isRename
                  ? "orphan once you deactivate the donor plugin (an override can't be auto-repointed — identity, not a link). Rebuild them against '"
                  : "orphan once you deactivate the donor plugins (an override can't be auto-repointed — identity, not a link). Include them in the merge set, or rebuild them against '")
              .Append(o.OutputName)
              .Append(isRename ? "', or re-run with them added as donors to combine them instead:\n" : "':\n");
            foreach (var pl in o.ExternalOverriders.Take(25)) sb.Append("  ! ").Append(pl).Append('\n');
            if (o.ExternalOverriders.Count > 25) sb.Append("  ! … (+").Append(o.ExternalOverriders.Count - 25).Append(" more)\n");
        }
        if (o.UnscannableRecords > 0)
            sb.Append("note: ").Append(o.UnscannableRecords).Append(" record(s) couldn't be scanned in the external-reference pass, so a ")
              .Append("'none' above may be incomplete — verify in xEdit. Samples: ").Append(string.Join("; ", o.UnscannableSamples)).Append('\n');
        // The coverage caveat belongs to the PASS, not to either outcome: a "none" and a populated list are incomplete
        // in the same way, so stating it here covers both rather than qualifying one branch and leaving the other
        // absolute. What the pass reads is record links and record identity — it never opens a plugin header, so a
        // dependent that merely DECLARES a donor as a master is invisible to it, and loses a master at the swap.
        sb.Append("identify-pass scanned ").Append(o.PluginsScanned).Append(" plugin(s) — it reads record links and ")
          .Append("record identity, NOT declared masters or runtime config files (SPID, KID, SkyPatcher, Open Animation ")
          .Append("Replacer), so a plugin that only lists a donor as a master, or only names one in such a file, is not ")
          .Append("counted above.\n");
        // The caveat above says those files are not READ. This says what that costs — the half a caller cannot derive
        // from "not counted". Both clauses are bounded by the claim rule stated at WriteSentences.MergeRuntimeConfigs:
        // this tool's own behaviour, plus a break entailed by the swap this same report instructs.
        sb.Append(WriteSentences.MergeRuntimeConfigs);

        AppendFacegenCarry(sb, o.AssetRename, inPlace: false);
        AppendVoiceCarry(sb, o.VoiceRename, inPlace: false);
        AppendSeqRegen(sb, o.SeqRegen, inPlace: false);

        // The merged plugin is built as a bare mod, so what lived in a donor's HEADER does not come along. These two
        // lines are keyed on what the DONORS carried, not on the donor count — the loss is identical for one donor or
        // ten, and it was measured while each donor overlay was open. Q3: a silent header loss is exactly the kind of
        // degraded result that has to be stated. Whether these should be CARRIED is a separate decision.
        bool lightNoteShown = o.LightDonors is { Count: > 0 };
        if (o.LightDonors is { Count: > 0 } light)
        {
            sb.Append("NOTE — ").Append(string.Join(", ", light.Take(10)));
            if (light.Count > 10) sb.Append(" (+").Append(light.Count - 10).Append(" more)");
            sb.Append(" carried the LIGHT (ESL) status; ")
              .Append(o.OutputName).Append(" does NOT — it is written as a full plugin and takes a full load-order slot. ")
              .Append("To make it light again run housecarl_compact_plugin on '").Append(o.OutputName)
              .Append("' (its esl defaults true) — but that renumbers object ids from 0x800 upward, so the ids listed as ")
              .Append("kept above will move.\n");
        }
        if (o.MasterDonors is { Count: > 0 } masters)
        {
            // No remedy: nothing on the surface flags an existing plugin as a master. Bare statement, as for the
            // header text below — an invented remedy would be the worse failure.
            sb.Append("NOTE — ").Append(string.Join(", ", masters.Take(10)));
            if (masters.Count > 10) sb.Append(" (+").Append(masters.Count - 10).Append(" more)");
            sb.Append(" carried MASTER status; ").Append(o.OutputName)
              .Append(" is NOT flagged as a master — it loads as a plain plugin, in the plugin block rather than the ")
              .Append("master block, so anything depending on that ordering will see it move.\n");
        }
        if (o.HeaderMetaDonors is { Count: > 0 } meta)
        {
            // No remedy is named because none exists on the surface: author=/description= belong to create_plugin, and
            // only at create time. A bare statement of the loss is the honest whole of what can be said about it.
            sb.Append("NOTE — the header Author/Description carried by ").Append(string.Join(", ", meta.Take(10)));
            if (meta.Count > 10) sb.Append(" (+").Append(meta.Count - 10).Append(" more)");
            sb.Append(" are not carried across; ").Append(o.OutputName).Append("'s are empty.\n");
        }

        if (o.Note is { } note) sb.Append("note: ").Append(note).Append('\n');
        sb.Append("reminders: existing SAVES that depend on the donors will not survive the swap (the records now live under a ")
          .Append("different plugin name, and any id that had to be renumbered moved with it) — best for a new game. ")
          .Append("FormIDs compiled into Papyrus (.pex hardcoded / ")
          .Append("GetFormFromFile) and any Mutagen-delta residual are NOT remappable — verify scripted records.");
        // ONE home for the compact recommendation per response. When the light note fired it already named the tool
        // WITH the cost that makes the advice honest (it renumbers from the floor); repeating it flat here would
        // leave the caller's last reading of it the one without the cost. With no light note there is no other
        // pointer, so the tail stays.
        if (!lightNoteShown)
            sb.Append(" Want it light? Run housecarl_compact_plugin on '").Append(o.OutputName).Append("' (the tools compose).");
        return sb.ToString();
    }

    /// <summary>Confirmation for housecarl_create: the new record's ALLOCATED FormID + editorid + type (the FormID
    /// is the key output — the caller references the new record by it), the patch path + its (derived) masters, and the
    /// fields applied. On refusal, the named reason (Q3) so the caller can fix and retry.</summary>
    internal static string RenderCreate(WritePatchBuilder.CreateOutcome o, int maxChars = 0, bool fullDump = false)   // internal: housecarl_create renders the same outcome
    {
        if (o.NeedsAcknowledge) return o.Error! + Epoch(o);  // the first-touch in-place CONSENT prompt — a required confirmation, NOT an error (Q3)
        if (!o.Success) return "error: " + o.Error + Epoch(o);
        var file = Path.GetFileName(o.OutputPath);
        var modFolder = Path.GetFileName(Path.GetDirectoryName(o.OutputPath) ?? "");
        var sb = new StringBuilder();
        if (o.InPlace)
            // "created into X" rather than the old "X rewritten": every sibling in-place headline is verb-then-file
            // (edited / forwarded into / removed from / compacted), and create's was the one that led with the file
            // and used "rewritten" as its verb — which now stutters against the shared hazard clause behind it.
            sb.Append("created into ").Append(file).Append(" IN PLACE (").Append(o.Bytes)
              .Append(" bytes — ").Append(WriteSentences.InPlaceRewritten).Append(")\n")
              .Append(WriteSentences.InPlaceModFolder(modFolder));
        else
            sb.Append(WriteSentences.NewOrExtendedArtifact(o.Extended, file, o.Bytes, modFolder));
        sb.Append(WriteSentences.Masters(o.Masters));
        var replacedCount = o.Created.Count(c => c.ReplacedExisting);
        sb.Append("created ").Append(o.Created.Count).Append(o.Created.Count == 1 ? " record" : " records");
        if (replacedCount > 0)
            sb.Append(" (").Append(replacedCount).Append(replacedCount == 1 ? " REPLACED an existing record" : " REPLACED existing records")
              .Append(" — same FormID kept, prior contents discarded)");
        sb.Append(":\n");
        // Budgeted (PR #311 review 3 [medium]): this is the render's LARGEST block and it is SET-VALUED — a 500-record
        // authoring job from records="@<manifest>" is the case this tool exists for, not an edge. The json twin
        // already budgets the same array and closes truncated:true, so leaving this one unbounded made text and json
        // disagree about the same call, with the text lane taking a silent host-side cut. (The round-1 fold filed this
        // as a follow-up on the grounds that max_chars' description scoped the ceiling to the read-back; the D2
        // divergence is the stronger argument and it wins.)
        int createCap = WriteSentences.Cap(maxChars);
        // #300's HOST CHOICE, hoisted out of the budget below: which version of a contested parent this artifact
        // carries is a control decision the caller may want to act on (sort, or inline the winner deliberately), and
        // the per-record `parent:` lines live INSIDE the loop where a max_chars cut removes them. It is a statement,
        // not a warning — the lean residual is the right default; this just makes it visible. One line per distinct
        // contested parent; the benign cases (definer IS the winner, or the artifact already carried the parent) say
        // nothing. The host choice is not IN the record, so a cut caller could not recover it afterwards.
        // Selected on the FLAG, never by matching the sentence (review [low]). BOUNDED, too (review [medium]): one
        // line per contested parent is ~400 chars, and a bulk_create fanning children into many contested cells would
        // otherwise blow the whole response budget BEFORE the created list starts — which then truncates at "0 of N",
        // the exact HCBR-2026-06-28-01 shape. Cap the block, and say how many were not shown. The bound lives on
        // Wire (PR #323 review [medium]) because the json twin needs the SAME one and a local literal here is how the
        // two drifted apart in the first place — the json side shipped this block unbounded.
        var contested = o.Created.Where(c => c.ParentContested && c.ParentHost is not null)
                         .Select(c => c.ParentHost!).Distinct(StringComparer.Ordinal).ToList();
        foreach (var host in contested.Take(Wire.ContestedHostsShown))
            sb.Append("  ! ").Append(host).Append('\n');
        if (contested.Count > Wire.ContestedHostsShown)
            sb.Append("  ! … and ").Append(contested.Count - Wire.ContestedHostsShown)
              .Append(" further contested parent(s) — each is named on its own record's `parent:` line below.\n");
        int listed = 0;
        for (int ci = 0; ci < o.Created.Count; ci++)
        {
            if (sb.Length >= createCap)
            {
                // The remedy points at a READ, never at re-issuing this call (PR #311 review 3 round-2 [medium]).
                // The sibling renders' "raise max_chars to see the rest" is safe on THEIR lanes — a repeated remove
                // is refused, a repeated forward re-copies identical bodies — but a repeated CREATE allocates the
                // records AGAIN: on the default lane patch= auto-suffixes into a second full patch, and under into=
                // each record is re-created at its old FormID with its prior contents discarded. That is the
                // duplicate-write trap ReadBackInFull's own doc names, and an agent following the notice literally
                // walks into it.
                sb.Append("  ... [truncated: ").Append(ci).Append(" of ").Append(o.Created.Count)
                  .Append(" created record(s) listed at max_chars=").Append(createCap).Append("; ")
                  .Append(WriteSentences.CreateRowsCutRemedy(ReadBackCall(o, file))).Append("]\n");
                break;
            }
            var c = o.Created[ci];
            listed++;
            sb.Append("  ").Append(c.RecordType).Append(' ').Append(c.FormKey).Append("  ").Append(c.EditorId);
            if (c.ReplacedExisting) sb.Append("  [REPLACED: this patch already defined this editorid — re-created fresh at the same FormID; prior contents, including any housecarl_apply edits since, were discarded]");
            sb.Append('\n');
            // #300 — a nested create had to override its parent in to host the child, and WHOSE version it copied is a
            // choice the caller never made and cannot see in the record afterwards. One line, only when there was one.
            if (c.ParentHost is { } host) sb.Append("      parent: ").Append(host).Append('\n');
            foreach (var op in c.Ops)
                sb.Append("      ").Append(op.Label).Append(op.After is not null ? "  -> " + op.After : "  -> applied").Append('\n');
        }
        AppendVoiceReport(sb, o.Voice, maxChars);
        AppendScriptBindingReport(sb, o.ScriptBinding, maxChars);
        AppendCellShellReport(sb, o.CellShell, maxChars);
        // Same compact-by-default verify as the edit lane (HCBR-2026-06-28-01): the forced create-in-place re-read still
        // runs; full_readback=true gives the deep dump, the default reports it compactly (per created record: re-read clean
        // + field count, or a named failure). The created records' set fields are already listed above.
        if (o.ReadBack is { } rb)
        {
            if (fullDump) AppendFullReadback(sb, rb, maxChars);
            else AppendCompactReadback(sb, Array.Empty<WritePatchBuilder.OpResult>(), rb, maxChars);
        }
        if (o.Note is { } note) sb.Append("note: ").Append(note).Append('\n');
        // Gated on rows having actually rendered (same review finding): with a small max_chars the header alone can
        // exceed the cap and drop EVERY row, and "the new FormID above" then asserts a referent this render never
        // printed. Say where to get them instead.
        sb.Append(listed > 0
            ? "the new FormID above is how you reference this record (SkyPatcher/SPID, or a follow-up edit). "
            : $"no records are listed above — the char budget cut the whole list, though all {o.Created.Count} WERE created. Read them back with {ReadBackCall(o, file)} to get their FormIDs. ");
        sb.Append(o.InPlace
            ? InPlaceAgainHint("To create more records in this plugin", file)
            : $"To add more to THIS patch, pass into=\"{file}\".");
        sb.Append(Epoch(o));
        return sb.ToString();
    }

    /// <summary>What a truncated REMOVE render tells the caller (PR #311 review 6 [medium]). The one lane where the
    /// dropped rows carry no information the caller lacks: removal is ALL-OR-NOTHING over the <c>formids=</c> they
    /// passed, so the listed set and the passed set are the same set. Re-issuing to widen the render is not merely
    /// wasteful here — it is REFUSED, because the records no longer exist to be found, so the old
    /// "raise max_chars to see the rest" pointed at the one call guaranteed to fail.</summary>
    internal const string RemovedRowsRemedy =                       // internal: the json twin says the same thing (D2)
        "removal is all-or-nothing, so these rows are exactly the formids= you passed — nothing here is unrecoverable. "
      + "Do NOT re-issue to widen this: the records are gone, so a repeat is refused as 'not carried by' the file";

    /// <summary>What a truncated FORWARD render tells the caller to do — and it depends on the LANE (PR #311
    /// review 5 [low]). "raise max_chars and re-issue" was justified here on the grounds that a repeated forward
    /// re-copies identical bodies; that holds on <c>in_place=</c> and on <c>into=</c> (replace-on-collision, so the
    /// second call lands on the same FormKeys), and on a dry run, which writes nothing at all. It does NOT hold on
    /// the DEFAULT lane — the one a caller reaches by naming no lane — where <c>ResolveOutputPath</c>'s
    /// <c>UniqueStem</c> allocates a fresh stem, so the re-issue is a SECOND full patch mod carrying the same
    /// overrides. Quieter than create's duplicate (no new FormIDs), not absent. The remedy names the lane that
    /// makes the re-issue safe rather than leaving the caller to discover the second folder.
    /// <para>IN_PLACE is its own case (PR #311 review 6 [low]) and the first pass got it wrong by lumping it with
    /// <c>into=</c>. A re-issue there is content-idempotent, but it re-serializes the caller's OWN plugin end to
    /// end — the file this same render just said has "no houseCARL backup or undo" — purely to widen a display.
    /// It is the only lane of the four where the re-run touches something houseCARL does not own, so it gets the
    /// read-back remedy instead: the target is active by definition of the lane, so <c>housecarl_records</c>
    /// reaches it, and the FormIDs to name are the ones the caller passed.</para></summary>
    internal static string ForwardAgainRemedy(WritePatchBuilder.ForwardOutcome o, string file)   // internal: the json twin says the same thing (D2)
        => WriteAgainRemedy(o.DryRun, o.InPlace, o.Extended, file, "patch mod carrying the same overrides");

    /// <summary>The lane rule generalized (PR #311 review 6): every WRITE render's row budget faces the same
    /// question — is re-issuing this call to widen a display safe? — and the answer is a property of the LANE, not
    /// of the verb. A dry run wrote nothing; <c>into=</c> lands on the same artifact; <c>in_place=</c> re-serializes
    /// the caller's own file; the DEFAULT lane auto-suffixes a second patch. `apply` shares this with `forward`
    /// because it shares the lane axis — the alternative was fixing three of four write tools and shipping the
    /// fourth on wording the reviewer has now flagged in four consecutive rounds.</summary>
    static string WriteAgainRemedy(bool dryRun, bool inPlace, bool extended, string file, string duplicateNoun)
        => dryRun || extended
            ? "raise max_chars to see the rest"
            : inPlace
                ? $"to see the rest, read the rows back with housecarl_records source=\"{file}\" formids=[the ids you passed] — re-issuing would re-serialize your ORIGINAL file a second time just to widen this render"
                : $"to see the rest, raise max_chars AND pass into=\"{file}\" — a bare re-issue on the default patch= lane writes a SECOND {duplicateNoun}";

    /// <summary>The <c>apply</c> lane's wording of <see cref="WriteAgainRemedy"/> (PR #311 review 6, declared as an
    /// unrequested sibling): apply's json render budgets its <c>ops</c> array, and its notice carried the same
    /// "raise max_chars" the forward/remove/create/write_seq notices were all moved off.</summary>
    internal static string ApplyAgainRemedy(WritePatchBuilder.PatchOutcome o, string file)
        => WriteAgainRemedy(o.DryRun, o.InPlace, o.Extended, file, "patch mod carrying the same edits");

    /// <summary>The read-back call a truncated create render points the caller at — a call that actually RESOLVES,
    /// which is the whole point of pointing away from re-issuing the create (PR #311 review 4 [medium]).
    /// <para>Two ways the shorter spellings fail. <c>source=</c> is records' SOURCE pole (WHOSE version), not a SELECT
    /// term, so a source-only call dies on "select something — formids= …, or a scan scope". And a <c>plugins=</c>
    /// scope names ACTIVE plugins, so it cannot select the headline case at all: a patch this very call just wrote is
    /// not enabled in MO2 yet (the render's own next line says to enable it), and over an off-order file the scope is
    /// refused by name. <c>types=</c> is the SELECT term that carries on BOTH arms — active, where the named pole's
    /// records are the scan universe; off-order, where the pole is enumerated from the file directly — and the created
    /// records' own <c>RecordType</c>s are catalog names, exactly what <c>types=</c> resolves.</para></summary>
    internal static string ReadBackCall(WritePatchBuilder.CreateOutcome o, string file)   // internal: the json twin points at the SAME call (D2)
    {
        var types = o.Created.Select(c => c.RecordType).Where(t => !string.IsNullOrWhiteSpace(t))
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        // Every distinct type, never a sampled head: a partial types= would select a partial answer while reading like
        // the whole one. (Unreachable-empty — a successful create has at least one created record — but a bare
        // source= call is the refused shape, so the clause is not silently dropped either.)
        return types.Count > 0
            ? $"housecarl_records source=\"{file}\" types=[{string.Join(", ", types.Select(t => $"\"{t}\""))}]"
            : $"housecarl_records source=\"{file}\" types=[<the record types you created>]";
    }

    /// <summary>Render the Layer B unit B voice-coverage report (a dialogue-line create). The enforced Q3 teeth against a
    /// byte-valid-but-SILENT line: a LOUD "WILL BE SILENT" per created voiced response with no .fuz on disk (naming the
    /// path to put the audio at), a brief "voice present" for ones already covered, and a NAMED reason per line whose
    /// path couldn't even be computed (no Speaker, unresolvable voice type, …). Voice ACTING stays out of scope — this
    /// reports the on-disk DATA-layer boundary, never generates audio. No-op unless the call created dialogue lines.</summary>
    static void AppendVoiceReport(StringBuilder sb, VoiceReport? report, int maxChars)
    {
        if (report is null || report.IsEmpty) return;
        // Budget-bounded like the full read-back (same maxChars contract): a bulk_create authoring hundreds of voiced
        // lines must NOT silently blow the response size or starve a requested read-back — past the cap the voice
        // section stops with an explicit notice (Q3), never a silent cut.
        int cap = WriteSentences.Cap(maxChars);
        int total = report.Lines.Count + report.Undetermined.Count, rendered = 0;
        sb.Append("voice coverage — created dialogue lines (").Append(WriteSentences.Twins.VoiceStake)
          .Append("; the audio is yours to provide):\n");

        bool anyReadIncomplete = false;
        foreach (var l in report.Lines)
        {
            if (sb.Length >= cap) { AppendVoiceTrunc(sb, rendered, total, cap); return; }
            var who = string.IsNullOrEmpty(l.TopicEditorId) ? l.Info.ToString() : $"{l.TopicEditorId} ({l.Info})";
            if (l.FuzPresent)
            {
                sb.Append("  OK   ").Append(who).Append(" resp ").Append(l.ResponseNumber)
                  .Append("  — voice present (").Append(l.FuzWinner ?? "?").Append(')');
                if (l.FuzAmbiguous) sb.Append(" [more than one source provides it — contended]");
                if (!l.LipPresent) sb.Append("; no .lip (no lip-sync)");
                sb.Append('\n');
            }
            else
            {
                sb.Append("  [!] WILL BE SILENT  ").Append(who).Append(" resp ").Append(l.ResponseNumber)
                  .Append("  — no .fuz at ").Append(l.FuzPath).Append("  (place the audio here)");
                if (!l.LipPresent) sb.Append("; .lip also absent (").Append(l.LipPath).Append(')');
                sb.Append('\n');
            }
            if (l.ReadIncomplete) anyReadIncomplete = true;
            rendered++;
        }
        foreach (var u in report.Undetermined)
        {
            if (sb.Length >= cap) { AppendVoiceTrunc(sb, rendered, total, cap); return; }
            var who = string.IsNullOrEmpty(u.TopicEditorId) ? u.Info.ToString() : $"{u.TopicEditorId} ({u.Info})";
            sb.Append("  [?] ").Append(who).Append("  — ").Append(u.Reason).Append('\n');
            rendered++;
        }
        if (anyReadIncomplete) sb.Append(WriteSentences.ScanIncomplete("an \"absent\""));
        if (report.CheckError is not null)
            sb.Append(WriteSentences.CheckCouldNotRun("voice", report.CheckError, "the records", "verify voice files manually."));
    }

    /// <summary>The explicit voice-coverage truncation notice (Q3 — the same convention as the read-back's): how many
    /// of the total voice entries were rendered before the char budget was hit, and what that does and does not mean.
    /// <para>Shares its closing clause with the result-script notice and with the json <c>WriteBlockCensus</c>
    /// (PR #311 review 7 [medium]): these blocks ride the CREATE render, so "raise max_chars to see the rest" meant
    /// re-issuing a create — a second auto-suffixed patch on the default lane, or re-creation at the same FormID
    /// with prior contents discarded under <c>into=</c>. That clause is now
    /// <see cref="WriteSentences.Twins.ReportBlockCut"/>, read by both transports, so the two can no longer give
    /// opposite advice about one call.</para></summary>
    static void AppendVoiceTrunc(StringBuilder sb, int rendered, int total, int cap)
        => sb.Append("  ... [voice coverage truncated: rendered ").Append(rendered).Append(" of ").Append(total)
             .Append(" line(s) at max_chars=").Append(cap).Append("; ").Append(WriteSentences.Twins.ReportBlockCut).Append("]\n");

    /// <summary>Render the coordinate-keyed §4-(b) structural-shell report (a cell create). The enforced Q3 teeth against
    /// a created-but-EMPTY cell: a created cell is a valid, correctly-placed RECORD, but houseCARL does NOT author world
    /// content — so per created cell this lists, by kind, what the author must still provide in the Creation Kit
    /// (lighting / terrain / water / navmesh). "Created" must never read as "looks right in game". No-op unless the call
    /// created cells.</summary>
    static void AppendCellShellReport(StringBuilder sb, CellShellReport? report, int maxChars)
    {
        if (report is null || report.IsEmpty) return;
        // Budgeted like its two siblings (PR #311 review 7 [low-medium], Aaron-go). This was the last unbudgeted
        // block on the write renders: the json twin already stopped at `cap` and closed with a census, so one
        // create authoring a batch of cells rendered every row in TEXT and took the SILENT host-side cut — the
        // divergence the created-rows / forwarded-rows / removed-rows folds each closed in turn. The inner
        // MustProvide loop is bounded too: one cell with a long work list could blow the budget by itself.
        int cap = WriteSentences.Cap(maxChars);
        int total = report.Cells.Count, rendered = 0;
        bool cut = false;
        sb.Append("cell shell — ").Append(WriteSentences.Twins.CellStake).Append(" (provide these in the Creation Kit):\n");
        foreach (var c in report.Cells)
        {
            if (sb.Length >= cap) { cut = true; break; }
            sb.Append("  ").Append(c.Interior ? "INTERIOR " : "EXTERIOR ").Append(c.EditorId).Append(" (").Append(c.Cell).Append("):\n");
            foreach (var m in c.MustProvide)
            {
                if (sb.Length >= cap) { cut = true; break; }
                sb.Append("      - ").Append(m).Append('\n');
            }
            if (cut) break;
            rendered++;
        }
        // The notice, then the two Q3 notes below it — which stay OUTSIDE the budget on purpose, the same rule the
        // removal render states: they are the accounting a truncated report still needs, and the grid-occupancy
        // seam in particular must not be the thing a cut swallows.
        if (cut)
            sb.Append("  ... [cell shell truncated: rendered ").Append(rendered).Append(" of ").Append(total)
              .Append(" cell(s) at max_chars=").Append(cap).Append("; ").Append(WriteSentences.Twins.ReportBlockCut).Append("]\n");
        // Q3 — declare the un-checked grid-occupancy seam (full load-order occupancy detection is a follow-up; never a
        // silent omission). Only an EXTERIOR cell collides on a grid; an interior cell has no grid identity.
        if (report.Cells.Any(c => !c.Interior))
            sb.Append("  note: ").Append(WriteSentences.Twins.GridOccupancy).Append('\n');
        if (report.CheckError is not null)
            sb.Append(WriteSentences.CheckCouldNotRun("cell-shell", report.CheckError, "the cell(s)", "review world content manually."));
    }

    /// <summary>Render the Layer B unit C result-script coverage report (a dialogue-line create). The enforced Q3 teeth
    /// against a byte-valid-but-INERT result script: a LOUD "WILL NOT FIRE" per created line whose VMAD binds nothing
    /// usable (incomplete) or names a script with no compiled `.pex` on disk (naming the missing path), a brief "OK" for
    /// ones fully wired + compiled, and a NAMED reason for any created INFO that couldn't be located. Script compilation
    /// itself is housecarl_compile_script's job — this reports the on-disk DATA-layer boundary. No-op unless the call
    /// created scripted dialogue lines. Budget-bounded like the voice + read-back sections (same max_chars contract).</summary>
    static void AppendScriptBindingReport(StringBuilder sb, ScriptBindingReport? report, int maxChars)
    {
        if (report is null || report.IsEmpty) return;
        int cap = WriteSentences.Cap(maxChars);
        int total = report.Findings.Count, rendered = 0;
        sb.Append("result-script coverage — created dialogue lines (").Append(WriteSentences.Twins.ScriptStake).Append("):\n");

        bool anyReadIncomplete = false;
        foreach (var f in report.Findings)
        {
            if (sb.Length >= cap)
            {
                sb.Append("  ... [result-script coverage truncated: rendered ").Append(rendered).Append(" of ").Append(total)
                  .Append(" line(s) at max_chars=").Append(cap).Append("; ").Append(WriteSentences.Twins.ReportBlockCut).Append("]\n");
                return;
            }
            var who = string.IsNullOrEmpty(f.TopicEditorId) ? f.Info.ToString() : $"{f.TopicEditorId} ({f.Info})";
            switch (f.Status)
            {
                case ScriptBindingStatus.BoundAndCompiled:
                    sb.Append("  OK   ").Append(who).Append("  — ").Append(f.Detail).Append('\n');
                    break;
                case ScriptBindingStatus.ScriptNotCompiled:
                    sb.Append("  [!] WILL NOT FIRE  ").Append(who).Append("  — ").Append(f.Detail);
                    if (f.MissingPex.Count > 0) sb.Append("  (missing: ").Append(string.Join(", ", f.MissingPex)).Append(')');
                    sb.Append('\n');
                    break;
                case ScriptBindingStatus.BindingIncomplete:
                    sb.Append("  [!] WILL NOT FIRE  ").Append(who).Append("  — ").Append(f.Detail).Append('\n');
                    break;
                default: // Undetermined
                    sb.Append("  [?] ").Append(who).Append("  — ").Append(f.Detail).Append('\n');
                    break;
            }
            if (f.ReadIncomplete) anyReadIncomplete = true;
            rendered++;
        }
        if (anyReadIncomplete) sb.Append(WriteSentences.ScanIncomplete("a \"missing .pex\""));
        if (report.CheckError is not null)
            sb.Append(WriteSentences.CheckCouldNotRun("result-script", report.CheckError, "the records", "verify the script binding manually."));
    }
}

// ---- the retired 1.x wire DTOs: parked in WireNamesProbe.NonInputWireTypes, reachable from no tool's input
// ---- schema. Kept rather than collapsed into ApplyOp/CreateRecordSpec -- that reshape is deferred (#469). ----

/// <summary>One edit operation off the wire. RecordType is NOT supplied — the cleave derives it from the resolved
/// winner's runtime type. Mirrors <see cref="WritePatchBuilder.PatchEdit"/> with string FormID + dotted path +
/// optional composition.</summary>
public sealed record BulkOp
{
    [JsonPropertyName("formid"), Description("The record's FormID 'XXXXXX:Plugin.esp'.")]
    public string? Formid { get; init; }

    [JsonPropertyName("field_path"), Description("Dotted field path, e.g. 'BasicStats.Damage' or 'Entries'. Step into a list/dict element mid-path with brackets, e.g. 'Effects[0].Data.Magnitude'; at the LEAF use verb + key, not brackets.")]
    public string? FieldPath { get; init; }

    [JsonPropertyName("verb"), Description(WriteVerbs.AllRecital + " (deep-copy the field at field_path from from_plugin's version — see from_plugin). SetAtIndex OVERWRITES the element at key=; InsertAtIndex inserts a new one AT key= and shifts the rest right (key = the list's length appends).")]
    public string Verb { get; init; } = "Set";

    [JsonPropertyName("value"), Description("The value (coerced to the field's type). Omit for Remove / ReplaceAll / Merge / compose.")]
    public string? Value { get; init; }

    [JsonPropertyName("key"), Description("Dict key or list index at the leaf.")]
    public string? Key { get; init; }

    [JsonPropertyName("values"), Description("The whole new list for a list ReplaceAll.")]
    public string[]? Values { get; init; }

    [JsonPropertyName("entries"), Description("Key→value pairs for a dict Merge or dict ReplaceAll.")]
    public Dictionary<string, string>? Entries { get; init; }

    [JsonPropertyName("compose"), Description("Build a modeled struct: an arm for a polymorphic Set, or the element for a struct-element Add / InsertAtIndex / SetAtIndex (e.g. a leveled-list entry; for a polymorphic list like VMAD Scripts[i].Properties, the element's CONCRETE arm type, e.g. 'ScriptObjectProperty').")]
    public StructInput? Compose { get; init; }

    [JsonPropertyName("composes"), Description("Build MANY modeled list elements in ONE op — the batch sibling of compose (each entry the same {type, fields?, ctor_args?, sets?} shape). With verb=Add, APPENDS each element in order (e.g. 10 leveled-list entries, a whole block of condition rows in one op instead of ten Adds). With verb=ReplaceAll, CLEARS the list then appends each — the way to replace a whole modeled list (conditions, effects, entries); pass composes=[] with ReplaceAll to CLEAR the list to empty (the modeled twin of values=[]). LIST elements only; mutually exclusive with compose/value/values. All-or-nothing: a bad element refuses the whole call with per-element (composes[i]) reasons.")]
    public StructInput[]? Composes { get; init; }

    [JsonPropertyName("from_plugin"), Description("For verb=\"CopyFrom\" ONLY: the plugin whose version of THIS record to deep-copy the field at field_path from — an ACTIVE plugin, OR a plugin FILE on disk that isn't in the load order (e.g. a disabled OLD patch you want to re-assert a field from). CopyFrom takes no value/values/entries/compose/composes — the source IS from_plugin's version of the field. Honors forward-then-edit precedence: into= a patch that already carries the record copies onto the patch's own version. Copies a WHOLE field's value (scalar, formlink, modeled list, sub-struct); it can't copy owned child records (forward the whole record with housecarl_forward instead).")]
    public string? FromPlugin { get; init; }
}

/// <summary>One brand-new record to create off the wire — the retired 1.x batch element: the DECLARED
/// record_type, its editorid, optional field operations, and the optional
/// nested parent/collection (a child's parent may be an existing FormID or a same-call sibling's editorid).</summary>
public sealed record CreateOp
{
    [JsonPropertyName("record_type"), Description("The kind of record to create: a catalog name ('Keyword', 'Spell', 'DialogTopic', 'DialogResponses', 'PlacedObject') or a 4-char signature.")]
    public string? RecordType { get; init; }

    [JsonPropertyName("editorid"), Description("REQUIRED. The EditorID the new record is referenced by. A nested child's parent= can name this editorid (a same-call sibling parent).")]
    public string? Editorid { get; init; }

    [JsonPropertyName("operations"), Description("Optional. The new record's fields, same shape as housecarl_apply ops but with NO formid (and no from_plugin — there is no other version to copy from yet): {field_path, verb?, value?, key?, values?, entries?, compose?, composes?}.")]
    public BulkOp[]? Operations { get; init; }

    [JsonPropertyName("parent"), Description("Optional. For a NESTED record: the parent it nests under — an EXISTING parent's FormID 'XXXXXX:Plugin.esp', OR the editorid of a record declared EARLIER in this same records array (a same-call sibling). Omit for a flat top-level record.")]
    public string? Parent { get; init; }

    [JsonPropertyName("collection"), Description("Optional. Which of the parent's child-collections to add into, BY NAME (e.g. a cell's 'Persistent') — needed only when more than one fits. Omit when unique or when parent is omitted.")]
    public string? Collection { get; init; }

    [JsonPropertyName("grid"), Description("Optional. For an EXTERIOR cell only (record_type 'Cell' with parent= a Worldspace): the cell's grid as \"X,Y\" (e.g. \"5,-12\"). houseCARL files it into the worldspace's block tree by block=floor(grid/32), subblock=floor(grid/8). A 'Cell' with NO parent and NO grid is an INTERIOR cell (self-files by FormID). Ignored for non-Cell types.")]
    public string? Grid { get; init; }
}

/// <summary>A modeled struct built from parts (wire shape of <see cref="StructSpec"/>): the concrete type, optional
/// flat coercible sub-fields, optional positional ctor args, and nested edits applied to the built struct.</summary>
public sealed record StructInput
{
    [JsonPropertyName("type"), Description("The concrete catalog type to build (arm type for a polymorphic Set; the collection's element type for an Add, e.g. 'LeveledItemEntry'; or a polymorphic element's concrete ARM, e.g. 'ScriptObjectProperty' into VMAD Properties).")]
    public string? Type { get; init; }

    [JsonPropertyName("fields"), Description("Flat coercible sub-fields set directly on the struct: name → value.")]
    public Dictionary<string, string>? Fields { get; init; }

    [JsonPropertyName("ctor_args"), Description("Positional constructor args, for struct types that require them.")]
    public string[]? CtorArgs { get; init; }

    [JsonPropertyName("sets"), Description("Nested edits applied to the built struct (paths rooted at it), each {path, verb?, value?, key?, compose?} — e.g. {path:'Data.Reference', value:'<FormID>'}.")]
    public NestedSet[]? Sets { get; init; }
}

/// <summary>One nested edit inside a <see cref="StructInput"/> (a path+verb+value rooted at the struct being built).</summary>
public sealed record NestedSet
{
    [JsonPropertyName("path"), Description("Dotted path within the struct, e.g. 'Data.Level'.")]
    public string? Path { get; init; }

    [JsonPropertyName("verb"), Description("Set (default) | Add | Remove | SetAtIndex | InsertAtIndex.")]
    public string Verb { get; init; } = "Set";

    [JsonPropertyName("value"), Description("The value (coerced).")]
    public string? Value { get; init; }

    [JsonPropertyName("key"), Description("Dict key or list index, if the nested target is a collection.")]
    public string? Key { get; init; }

    [JsonPropertyName("compose"), Description("Build a modeled sub-struct for THIS nested target (recursive): the concrete ARM of a polymorphic sub-field (e.g. a Condition's Data → 'GetActorValueConditionData'), or the element for a struct-element Add / InsertAtIndex / SetAtIndex nested inside the struct. Omit for a coercible scalar (use value=).")]
    public StructInput? Compose { get; init; }
}

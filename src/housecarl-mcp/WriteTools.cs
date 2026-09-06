using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// The plugin-level write tools: create an empty header-only plugin, compact (renumber) a plugin's FormIDs, and merge
/// plugins into one. Each takes a whole plugin FILE as its subject and rides its own core builder — none goes through
/// the record-edit path (<see cref="LoadOrderService.ApplyEdits"/>), resolves a load-order winner, or declares
/// <c>into=</c>. create_plugin and merge_plugins only ever write a NEW plugin; compact_plugin's second lane is
/// <c>in_place=</c> + <c>acknowledge=</c>, which overwrites the original.
/// <para>This type is also the shared home of the RENDER helpers the record-write tools call
/// (<see cref="Render"/>, <see cref="RenderCreate"/>, <see cref="RenderRemoval"/>, <see cref="RenderForward"/> and
/// the remedy sentences beside them) — the bulk of what follows.</para>
/// </summary>
[McpServerToolType]
public static class WriteTools
{
    [McpServerTool(Name = ToolNames.CreatePlugin, Title = "Create an empty header-only (trigger) plugin"),
     Description(
         "Create an EMPTY, HEADER-ONLY plugin — a valid TES4 header with ZERO records and no masters, in a NEW mod " +
         "folder (originals untouched). Its only job is to EXIST so its basename resolves: the artifact that SKSE configs " +
         "binding by plugin basename need (e.g. a CraftingCategories-style trigger that must ship 'Foo.esp' so 'Foo.json' " +
         "loads), a placeholder ESL for FormID reservation, a dummy plugin for another mod to list as a master, or any " +
         "'I just need plugin Foo to be present' case. UNLIKE " + ToolNames.Create + ", it authors NO record — so it adds no conflict-tree footprint " +
         "(no filler override needed to make the plugin non-empty). plugin_name is used EXACTLY (the basename is " +
         "load-bearing — houseCARL will NOT auto-suffix it): if a plugin of that name is already active in the load order, " +
         "or a houseCARL folder of that name already exists, it REFUSES loud rather than rename or overwrite (Q3). Pass " +
         "esl=true for the lightest trigger (a header-only light plugin consumes no consequential load-order slot; with " +
         "zero records the ESL FormID-range rule is trivially satisfied). author/description are optional TES4 header " +
         "text. Returns the plugin path + mod folder — enable + sort it in MO2 to use it. To author actual records, use " +
         ToolNames.Create + " instead.")]
    public static string CreatePlugin(
        LoadOrderService svc,
        [Description("The EXACT plugin name (with or without a trailing .esp/.esm/.esl; e.g. 'Authoria - CraftingCategories'). Used VERBATIM as the basename — houseCARL will not auto-suffix it, because a trigger plugin's whole job is that its basename matches the config bound to it. The written file is '<name>.esp'.")]
            string plugin_name,
        [Description("When true, flag the plugin as a light master (ESL) — the lightest possible trigger: a header-only ESL consumes no consequential load-order slot. Default false (a normal full plugin).")]
            bool esl = false,
        [Description("Optional. Author text for the TES4 header (the CNAM field). Purely informational.")]
            string? author = null,
        [Description("Optional. Description text for the TES4 header (the SNAM field). Purely informational.")]
            string? description = null) => Guard.Tool(ToolNames.CreatePlugin, () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        if (string.IsNullOrWhiteSpace(plugin_name))
            return "error: plugin_name is empty. Name the plugin to create (a header-only plugin has no record to derive a name from).";
        return RenderCreatePlugin(svc.CreatePlugin(plugin_name, esl, author, description));
    });

    [McpServerTool(Name = ToolNames.CompactPlugin, Title = "Compact / ESL-renumber a plugin's FormIDs"),
     Description(
         "COMPACT a plugin's FormIDs — the data-layer twin of xEdit's \"Compact FormIDs for ESL\". Renumbers EVERY record " +
         "the plugin DEFINES (its originating records — flat AND nested: cells, placed references, dialogue lines, navmesh, " +
         "landscape), repoints every reference WITHIN the plugin, and leaves its overrides of other mods at their master " +
         "FormIDs.\n\n" +
         "The grammar is on the parameters: plugin= names the target — where it is resolved from, and its refusals; esl= " +
         "the window and the light flag — the ESL ceiling and the override-only lanes; in_place= which file is written — " +
         "the new-plugin default with its MO2 swap, the overwrite lane, and what each does to a LOCALIZED plugin; " +
         "repoint_externals= the external-referencer safety; acknowledge= the consent any in-place rewrite needs; " +
         "patch_name= the new mod folder.\n\n" +
         "Note: references compiled into Papyrus scripts (.pex hardcoded FormIDs / GetFormFromFile) are NOT remappable — " +
         "verify scripted records after compacting.")]
    public static string CompactPlugin(
        LoadOrderService svc,
        [Description("The plugin's filename to compact (e.g. 'CoolMod.esp'). The target need NOT be active: usually it is in your load order, but a plugin on disk and not (yet) in it — the patch houseCARL just wrote, before the MO2 refresh; a plugin inside a disabled mod — is resolved by filename across ALL mod folders and compacted OFF-ORDER. Whichever lane the target came from, active or not, its declared masters must still be active: a declared master not active is refused loud and nothing is written. The compacted output keeps this EXACT basename. Refuses loud + writes nothing on: this plugin found nowhere on disk / ambiguous across folders / unparseable; a serialize fault.")]
            string plugin,
        [Description("When true (default), renumber into the light/ESL range (0x800–0xFFF, 2048 IDs) and flag the result a light master (ESPFE) — the canonical 'compact for ESL', which frees a load-order slot. false = renumber contiguously from 0x800 with no light flag or 2048 ceiling (just closes FormID gaps). An override-only plugin with esl=true takes the FLAG-ONLY lane: nothing to renumber, every record copies verbatim, the ESL flag is set (always valid — the light window only constrains originating records); with esl=false there is nothing to do, and that is refused loud with nothing written. Refused loud with nothing written too: with esl=true, MORE records than the light range holds (the hard 2048 ESL ceiling — named, never truncated).")]
            bool esl = true,
        [Description("Optional, default false. IN-PLACE LANE (opt-in): OVERWRITE the original plugin with its compacted form (xEdit's norm) instead of writing a new file — NO houseCARL backup or undo (keep your own). Rides the in-place consent: requires acknowledge=true. OMIT (the default) to write a NEW plugin instead, keeping the SOURCE'S EXACT basename (so other mods that list it as a master still resolve) in a fresh houseCARL mod folder, leaving the original untouched: review the new plugin in xEdit, then in MO2 enable its folder and DISABLE the original mod (same basename — MO2 serves one). LOCALIZED PLUGINS: houseCARL does not rewrite one in place — its text lives in separate .STRINGS files it cannot swap together with the plugin — so a LOCALIZED plugin is REFUSED in this lane whatever arrangement its .STRINGS files are in. The new-plugin lane still compacts it — UNLESS its .STRINGS resolve NOWHERE (houseCARL can see them nowhere, or the folder holding them cannot be read), which that lane refuses too: every name, description and message would read back EMPTY (or, from a folder nothing could open, unknowable) in a plugin with nothing left in it to tell that text from one that never had any. Otherwise the output is DE-LOCALIZED — the text this read resolved is written into the plugin itself and the source's .STRINGS files no longer describe it — and the report says so. The review step above is where you catch that: read the output's TEXT before swapping it in. ONE MORE REFUSAL on this lane, before the consent gate and whatever acknowledge says: if the external-reference pass could not READ some plugin, houseCARL cannot tell whether it references records about to be renumbered, so any in-place rewrite (the target, its referencers, or both) is refused and nothing is written — the new-plugin lane carries that as a note instead.")]
            bool in_place = false,
        [Description("Optional, default false. THE SAFETY (Q3): renumbering breaks any reference from OUTSIDE this plugin (they'd point at FormIDs that vanish), so whenever there is anything to renumber houseCARL scans the WHOLE load order for such external referencers (a one-pass walk — can take ~25s on a big order): if NONE, it's a clean compaction; if SOME, the call REFUSES (listing them) by default. Set true to ALSO rewrite those external referencers IN PLACE to follow the renumber (requires in_place=true, so the target and its referrers move together, AND acknowledge=true; no backup of them either). Refused when any referencer is LOCALIZED or is one houseCARL CANNOT READ — it rewrites neither a localized plugin nor one it cannot read in place — and the default refusal above says which referencers are in which state, and where a localized one's text is, so you learn that BEFORE choosing this flag rather than being sent here.")]
            bool repoint_externals = false,
        [Description("Optional, default false. Confirms the in-place trade-off when in_place=true OR repoint_externals=true (your original file(s) get rewritten, no backup). The FIRST such call without it returns a CONFIRM prompt listing exactly what will be overwritten — re-call with acknowledge=true to proceed.")]
            bool acknowledge = false,
        [Description("Optional. Base name for the NEW mod folder (new-file lane only; auto-suffixed if taken). Ignored with in_place=true. The PLUGIN inside ALWAYS keeps the source's exact basename so external masters still resolve.")]
            string? patch_name = null) => Guard.Tool(ToolNames.CompactPlugin, () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        if (string.IsNullOrWhiteSpace(plugin))
            return "error: plugin is empty. Name the plugin filename to compact (e.g. 'CoolMod.esp').";
        return RenderCompact(svc.CompactPlugin(plugin, esl, in_place, repoint_externals, acknowledge, patch_name));
    });

    [McpServerTool(Name = ToolNames.MergePlugins, Title = "Merge plugins into one new plugin"),
     Description(
         "MERGE one or more ACTIVE plugins into ONE NEW plugin — a RECORDS operation (the zMerge/'Merge Plugins' job): the " +
         "donors' records combine under a new filename; the donor FILES and their mods are NEVER touched (new-file lane only, " +
         "no in-place).\n\n" +
         "The grammar is on the parameters: plugins= holds the donor set — the single-donor rename lane, the renumber and " +
         "cross-donor conflict rules, the outside-referencer warning, and the donor refusals; output= the new filename — the " +
         "asset carry and what it costs existing saves; patch_name= the new mod folder.\n\n" +
         "AFTER: review the merged plugin in xEdit, enable its mod folder in MO2, then deactivate the donor PLUGINS (right " +
         "pane) but KEEP the donor MOD FOLDERS enabled (left pane) — merge carries only the FormID-keyed files the rename " +
         "breaks (facegen/voice/seq); every other donor asset (meshes, textures, scripts, BSA contents) is still referenced " +
         "BY PATH from the merged records and loads from the donor folders. Caveat: a donor .bsa stops auto-loading once its " +
         "same-named plugin is inactive — extract it into the mod folder (" + ToolNames.BsaExtract + ") or load it via a same-named " +
         "dummy plugin (" + ToolNames.CreatePlugin + ").\n\n" +
         "A donor's HEADER mostly does not come along: master (ESM) status and Author/Description are always dropped, and the " +
         "report names each one it actually dropped. Light (ESL) status IS carried when every donor was light and every merged " +
         "object id fits the light window 0x800–0xFFF; otherwise it is dropped and the report says which of the two reasons it " +
         "was, and whether " + ToolNames.CompactPlugin + " can still make it light — it can, by renumbering every id into that " +
         "window (so ids the merge kept move), unless the merged plugin defines more than 2048 records, which no light plugin holds.")]
    public static string MergePlugins(
        LoadOrderService svc,
        [Description("The donor plugin filenames to merge (at least one, e.g. [\"CoolMod.esp\", \"CoolMod Patch.esp\"]) — each must be active in your load order: a donor not active is refused loud and nothing is written. This is a SET: a name repeated is still one donor. A SINGLE donor renames it into output=: with nothing to combine the merge IS a rename — the same records under a new plugin name, keeping every object id already inside the writable range (nothing can collide; an id BELOW the 0x800 floor still renumbers, and the per-donor line reports it), facegen/voice/seq carried to the new name. Argument order does not matter: houseCARL uses LOAD order for id priority and conflict resolution. RENUMBER is collision-first (zMerge's default): the donor EARLIEST in the load order keeps its FormID object ids; later donors renumber ids already taken, and ANY donor's ids below the 0x800 floor renumber too (all records necessarily move to the new plugin's identity). Cross-donor conflicts on the SAME record resolve to the LOAD-ORDER WINNER and are each REPORTED; a losing donor's nested children the winner doesn't re-list (a base mod's dialogue lines under a patched topic; placed refs under a patched cell) are GRAFTED into the winner's copy — so merging a mod WITH its patches is the intended use. THE SAFETY (Q3): plugins OUTSIDE the merge that reference or override donor records are WARNED and NAMED, never refused — the donors stay active until you swap in MO2, so nothing breaks at write time; the remedy is to include those patches in the merge set or re-point them before disabling the donors. Refuses loud + writes nothing on: a donor unparseable / not on disk; a dangling donor-internal reference (a donor referencing a FormID no donor defines); a declared master not active.")]
            string[] plugins,
        [Description("The NEW merged plugin's filename to create (e.g. 'MyMerge.esp') — must NOT already exist in the load order: an output name already there is refused loud and nothing is written. The donors keep their names and files untouched. ASSETS follow the renumber into this name: every donor NPC's facegen and every voiced line are carried into the new plugin-name folders (those paths embed the plugin NAME, so ALL donor facegen/voice moves, not just collisions), and a .seq is refreshed when any donor shipped one. Existing SAVES that depend on the donors will NOT survive (the records now live under this plugin name, and any id that had to be renumbered moved with it) — best for a new game.")]
            string output,
        [Description("Optional. Base name for the NEW mod folder (auto-suffixed if taken). Defaults to '<output> merged' — or '<output> renamed' for a single donor, since that folder name is what you will see in MO2 from then on.")]
            string? patch_name = null) => Guard.Tool(ToolNames.MergePlugins, () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        return RenderMerge(svc.MergePlugins(plugins, output, patch_name));
    });

    /// <summary>Compact, parseable confirmation of a write: what changed plus the IDs needed for follow-up. On
    /// refusal, the full reason (every malformed or rejected op) so the caller can fix and retry.</summary>
    internal static string Render(WritePatchBuilder.PatchOutcome o, int maxChars = 0, bool fullDump = false)   // internal: tests render this outcome directly
    {
        if (o.NeedsAcknowledge) return o.Error! + Epoch(o);  // the in-place consent prompt is a required confirmation, not an error
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
        // Budgeted like every sibling render: applying edits is set-valued, so a few hundred ops is the expected case,
        // and the json render budgets the same array — unbounded here, the HOST cuts the response instead of max_chars.
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
              .Append(op.After is not null ? "  -> " + op.After : "  -> applied").Append(ApplyNote(op)).Append('\n');
        }
        // The .fuz/.lip and result-script checks run on CREATE of dialogue lines, not on edits to existing ones, so an
        // edit that adds a response or result script carries the same hazard unflagged — say so, and point at the sweep.
        if (o.Ops.Any(op => string.Equals(op.RecordType, VoiceCheck.InfoCatalogName, StringComparison.Ordinal)))
            sb.Append("note: this edit touched a dialogue line (INFO). Voice (.fuz) and result-script coverage are checked on CREATE, not on edits — ")
              .Append("run " + ToolNames.Check + " findings=[\"dialogue\"] with the topic (or its owning quest) in seeds= to audit voice + result-script coverage and the topic graph over the edited line and every other line in the topic.\n");
        // The touched-record verify (forced on for in-place, opt-in otherwise) renders COMPACT by default, the full
        // field-by-field dump only on full_readback=true — a deep dump of many records overflows the host token cap.
        // The verify itself is unchanged; this is its output, not its detection.
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

    /// <summary>The epoch stamp: one thin adapter per outcome record over the single construction in
    /// <see cref="WriteSentences.Epoch"/>. The outcome types are independent shapes, so the sentence lives once.</summary>
    static string Epoch(WritePatchBuilder.PatchOutcome o) => WriteSentences.Epoch(o.Stamp);
    static string Epoch(WritePatchBuilder.CreateOutcome o) => WriteSentences.Epoch(o.Stamp);
    static string Epoch(WritePatchBuilder.RemovalOutcome o) => WriteSentences.Epoch(o.Stamp);
    static string Epoch(WritePatchBuilder.ForwardOutcome o) => WriteSentences.Epoch(o.Stamp);

    /// <summary>The "how to keep going on this plugin" line for a completed IN-PLACE write. One spelling because
    /// there is one lane: the write tools declare a single string <c>in_place="X.esp"</c>, and a response must never
    /// send someone to a parameter their tool does not expose.</summary>
    static string InPlaceAgainHint(string verb, string file) =>
        $"{verb}, pass in_place=\"{file}\" again (no further confirmation needed for it).";

    /// <summary>The dry_run=true confirmation: the SAME pipeline ran (winner resolve, pre-flight, every verb applied
    /// in memory, the reference-resolution check) and stopped at the point of no return, so this reports what WOULD
    /// change with nothing on disk. The header says so first — a dry run must never read like a write. A refusal never
    /// reaches here: a dry run refuses exactly like the real call.</summary>
    static string RenderDryRun(WritePatchBuilder.PatchOutcome o, int maxChars, bool fullDump)
    {
        var file = Path.GetFileName(o.OutputPath);
        var sb = new StringBuilder();
        sb.Append(WriteSentences.DryRunHeader);
        sb.Append(WriteSentences.DryRunWouldWrite(o.InPlace, o.Extended, file, "edit"));
        sb.Append(WriteSentences.DryRunMasters(o.Masters));
        sb.Append(o.Ops.Count).Append(o.Ops.Count == 1 ? " edit would apply:\n" : " edits would apply:\n");
        // Budgeted for the same reason as the real render's loop; the cut notice takes the dry run's wording, since
        // nothing was written and it must not say the edits were applied.
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
              .Append(op.After is not null ? "  -> would become " + op.After : "  -> would apply").Append(ApplyNote(op)).Append('\n');
        }
        if (fullDump && o.ReadBack is { } rb) AppendFullReadback(sb, rb, maxChars, dryRun: true);
        if (o.Note is { } note) sb.Append("note: ").Append(note).Append('\n');
        sb.Append(WriteSentences.DryRunClose("every op passed resolve + pre-flight", "apply"))
          .Append(Epoch(o));
        return sb.ToString();
    }

    /// <summary>The full_readback=true read-back section: each touched or created record IN FULL, re-read from the
    /// written file on disk. Labeled as exactly that — the written file's content, NOT load-order truth (the patch wins
    /// nothing until enabled in MO2). Char-budget-bounded with an explicit notice, at the lower
    /// <see cref="Wire.ReadbackMaxChars"/> default so the cut-off output stays under the host token ceiling and the
    /// truncation note reaches the caller.</summary>
    static void AppendFullReadback(StringBuilder sb, IReadOnlyList<WritePatchBuilder.FullReadback> rb, int maxChars,
        bool dryRun = false)
    {
        int cap = WriteSentences.ReadbackCap(maxChars);
        // A dry run's records come from the IN-MEMORY would-be content — say so, never imply a file exists.
        sb.Append(dryRun
            ? "full preview — the ENTIRE record(s) as they WOULD be written, read from the in-memory would-be content (nothing is on disk):\n"
            : "full read-back — the ENTIRE record(s) as written, re-read from the patch file on disk " +
              "(the written file's content, NOT load-order truth; the patch wins nothing until enabled + sorted in MO2):\n");
        string hint = dryRun ? "; raise max_chars" : "; raise max_chars, or enable the patch in MO2 and use " + ToolNames.Records;
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

    /// <summary>The DEFAULT (full_readback=false) render of the touched-record verify. The forced in-place re-read
    /// still ran — corruption detection is unchanged; this only reports it compactly so it cannot overflow the host
    /// token cap. One line per record: a re-read-clean marker + field count, or the NAMED re-read failure; then the
    /// "what landed" identity for each op that touched that record. Covers every record, bounded by the same char cap
    /// with an explicit truncation note.</summary>
    static void AppendCompactReadback(StringBuilder sb, IReadOnlyList<WritePatchBuilder.OpResult> ops,
        IReadOnlyList<WritePatchBuilder.FullReadback> rb, int maxChars)
    {
        int cap = WriteSentences.ReadbackCap(maxChars);
        // The banner claims only what it can stand behind: every edited record WAS re-read off the written file, and
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
            // verify is forced on), never counted as clean.
            if (r.Error is not null) { sb.Append("  ✗ ").Append(r.Target).Append(" — ").Append(r.Error).Append('\n'); continue; }
            var rec = r.Record!;
            sb.Append("  ✓ ").Append(rec.Type).Append(' ').Append(rec.FormKey)
              .Append(" — re-read clean (").Append(rec.Fields.Count).Append(" field(s))");
            // The per-op clause is the FILE's answer when the file gave one (LandedOnDisk), and is marked as the
            // applied edit's claim when it did not — the banner above says "re-read off the written file".
            var landed = ops.Where(op => op.Target == r.Target && (op.LandedOnDisk ?? op.Landed) is not null)
                             // No ApplyNote here: the op line above already carried it, and readback is FORCED on the
                             // in-place lane, so appending it would print the same sentence twice for the same op.
                             .Select(op => $"{op.Label}: {op.LandedOnDisk ?? op.Landed}" + LandedProvenance(op))
                             .ToList();
            if (landed.Count > 0) sb.Append("; ").Append(string.Join("; ", landed));
            sb.Append('\n');
        }
    }

    /// <summary>The op's apply-time note, as a trailing clause on its line — what the write DID that the file cannot
    /// say afterwards (a duplicate Add). Empty when there is nothing to say.</summary>
    static string ApplyNote(WritePatchBuilder.OpResult op) => op.ApplyNote is { } n ? "  [" + n + "]" : "";

    /// <summary>Where a per-op "what landed" clause came from, when it is not the plain file answer. Silence means the
    /// file was re-read for this op and agreed; the two marked cases are a file that could not answer for the op, and
    /// an op a later op in the same call superseded (a mid-sequence reading the final file cannot corroborate).</summary>
    static string LandedProvenance(WritePatchBuilder.OpResult op) =>
        op.SupersededInCall ? " [as applied — a later op in this call wrote the same field; the file shows that op's result]"
        : op.LandedOnDisk is not null ? ""
        // The same split the json render makes: asserting the file was re-opened and could not answer, on a call where
        // the verify never ran, is a claim about a read that did not happen. The first arm is the reachable one; the
        // second is defensive — ops the verify does not reach carry no Landed and are filtered out upstream.
        : op.VerifyAttempted ? " [as applied — the re-opened file did not answer for this op]"
        : " [as applied — this lane ran no file check]";

    /// <summary>Confirmation for housecarl_remove: what was dropped, the patch's now-lean masters, and how many
    /// records remain (0 ⇒ inert). On refusal, the named reason so the caller can fix and retry.</summary>
    internal static string RenderRemoval(WritePatchBuilder.RemovalOutcome o, int maxChars = 0)   // internal: housecarl_remove renders the same outcome
    {
        if (o.NeedsAcknowledge) return o.Error! + Epoch(o);  // the in-place consent prompt is a required confirmation, not an error
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
        // Budgeted like every other write render: removal is SET-VALUED, so a few hundred dropped overrides is the
        // expected case, and max_chars= promises trailing rows are dropped with an explicit notice, never silently.
        // The masters line and the closing guidance below sit outside the budget deliberately — they are the
        // accounting a truncated report still needs.
        int cap = WriteSentences.Cap(maxChars);
        for (int i = 0; i < o.Removed.Count; i++)
        {
            if (sb.Length >= cap)
            {
                // NOT "raise max_chars to see the rest": re-issuing answers "not carried by patch '<file>'; NOTHING
                // removed", because the records are already gone. Nothing is lost — removal is all-or-nothing, so the
                // rows ARE the formids= the caller passed.
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
    /// the copied version was already winning — never silently a no-op. On refusal, the named reason so the caller
    /// can fix and retry. Optional full read-back rides along (the pre-enable verify that the copy is the source's).</summary>
    internal static string RenderForward(WritePatchBuilder.ForwardOutcome o, int maxChars = 0)   // internal: a test asserts the would-be phrasing
    {
        if (o.NeedsAcknowledge) return o.Error! + Epoch(o);  // the in-place consent prompt is a required confirmation, not an error
        if (!o.Success) return "error: " + o.Error + Epoch(o);
        var file = Path.GetFileName(o.OutputPath);
        var modFolder = Path.GetFileName(Path.GetDirectoryName(o.OutputPath) ?? "");
        var sb = new StringBuilder();
        if (o.DryRun)
        {
            // The SAME dry-run sentences the apply lane renders, from the same source: say NOTHING was written
            // first, then what the real call would do.
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
        // FormKey + editorid + the source clause + a REPLACED / redundant / out-ranks bracket), and the json render
        // already truncates the identical array.
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
            // The sentence has to match what the replace does: the FIELDS are replaced and everything nested under the
            // record is carried across, so this must not read as a clean revert. The count is stated rather than
            // implied — "nested records were kept" cannot tell nothing-was-there from twelve-kept.
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
                // patch not enabled yet). Never render a ranking against a winner that does not exist.
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
    /// refusal, the named reason so the caller can fix and retry.</summary>
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

    /// <summary>Confirmation for housecarl_compact_plugin: where the compacted plugin landed (new file vs in place),
    /// the record accounting (originating renumbered / overrides kept), masters, the external-referencer verdict, the
    /// identify-pass coverage, and the un-remappable-script reminder. The NeedsAcknowledge prompt is returned verbatim,
    /// not as an error; on refusal the named reason so the caller can fix and retry.</summary>
    internal static string RenderCompact(WritePatchBuilder.CompactOutcome o)   // internal: a test renders a failure outcome to check the SEQ WARN reaches user output
    {
        if (o.NeedsAcknowledge) return o.Error!;            // the in-place consent prompt is a required confirmation, not an error
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

        // External OVERRIDERS — plugins that OVERRIDE a renumbered record, not just reference it. They orphan after the
        // renumber, and an override cannot be auto-repointed (an identity change, not a link rewrite), so this is a
        // WARN naming each plugin rather than the referencer refuse/repoint path.
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
        AppendUnscannablePlugins(sb, o.UnscannablePlugins);
        sb.Append("identify-pass scanned ").Append(o.PluginsScanned).Append(" plugin(s) for external references.\n");
        // The plugin NAME survives a compaction, so what moves is the object id — and only the ids this run actually
        // moved, which the accounting above states. Bounded by the claim rule at WriteSentences.CompactRuntimeConfigs.
        sb.Append(WriteSentences.CompactRuntimeConfigs);

        AppendFacegenCarry(sb, o.AssetRename, o.InPlace);
        AppendVoiceCarry(sb, o.VoiceRename, o.InPlace);
        AppendSeqRegen(sb, o.SeqRegen, o.InPlace);

        if (o.Note is { } note) sb.Append("note: ").Append(note).Append('\n');
        sb.Append("reminder: FormIDs compiled into Papyrus (.pex hardcoded / GetFormFromFile) and any Mutagen-delta ")
          .Append("residual are NOT remappable — verify scripted records after compacting.");
        return sb.ToString();
    }

    // A plugin the external-reference pass could not read through — shared by compact and merge, one render home.
    // Each plugin gets the sentence its own cause earns (WriteSentences.UnscannablePlugin): a file that would not
    // open is held by another program, a file that faulted mid-enumeration is not, and one blanket claim would be
    // wrong for half of them. "The external-referencer check", not "the list above": the referencer list prints
    // only on some shapes, and a plugin that faulted partway can already be in it.
    static void AppendUnscannablePlugins(StringBuilder sb, IReadOnlyList<RemapEngine.UnscannablePlugin>? plugins)
    {
        if (plugins is not { Count: > 0 }) return;
        sb.Append("note: the external-reference pass could not fully read ").Append(plugins.Count)
          .Append(plugins.Count == 1 ? " plugin, so the external-referencer check does not cover it:\n"
                                     : " plugins, so the external-referencer check does not cover them:\n");
        foreach (var p in plugins.Take(25)) sb.Append("  ! ").Append(WriteSentences.UnscannablePlugin(p)).Append('\n');
        if (plugins.Count > 25) sb.Append("  ! … (+").Append(plugins.Count - 25).Append(" more)\n");
    }

    // FormID-keyed assets carried WITH the renumber — shared by compact and merge, one render home. The renumber moves
    // records to new FormIDs (a merge also to a new plugin NAME), so facegen/voice are looked up under NEW paths and a
    // shipped .seq goes stale; carrying them is what stops a dark-faced NPC, a mute voiced mod, and SGE quests that
    // never start. Reported, never silent. inPlace is always false for merge — it has no in-place lane.

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

    /// <summary>Merge confirmation: the merged plugin's identity + the MO2 swap instruction, per-donor id accounting,
    /// cross-donor conflict resolutions (load-order winner — reported, never silent), the WARN surfaces (external
    /// referencers/overriders with the remedy), the asset-carry accounting, and the saves/ESL pointers. On refusal,
    /// the named reason. internal: a test asserts the warnings reach user output.</summary>
    internal static string RenderMerge(WritePatchBuilder.MergeOutcome o)
    {
        if (!o.Success) return "error: " + o.Error;
        var file = Path.GetFileName(o.OutputPath);
        var modFolder = Path.GetFileName(Path.GetDirectoryName(o.OutputPath) ?? "");
        var sb = new StringBuilder();
        // The operation's SHAPE is classified ONCE here and consumed by every sentence that varies with it — this
        // headline, the per-donor renumber cause, and the external-referencer remedy order. One donor is the RENAME
        // case: "from 1 donors" would be both ungrammatical and a misdescription, nothing having been combined.
        bool isRename = o.Donors.Count == 1;
        if (isRename)
        {
            // DERIVED from the accounting line below rather than asserting alongside it: a pure-override donor
            // originates nothing, so nothing is re-keyed and "its records under a new identity" would be false.
            sb.Append("wrote ").Append(file).Append(" (new plugin; ").Append(o.Bytes).Append(" bytes) — a RENAME of ")
              .Append(o.Donors[0]).Append(": one donor, so there is nothing to combine — ");
            // Three arms, because each sentence may claim only the quantity it read — including the donor with no
            // records at all, which this same report's swap instruction tells callers to create to keep a .bsa loading.
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
          .Append("deactivated — extract it into the mod folder (" + ToolNames.BsaExtract + ") or load it via a same-named dummy ")
          .Append("plugin (" + ToolNames.CreatePlugin + ").\n");

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

        // WARN loud and proceed — the donors stay installed and ACTIVE until the user swaps in MO2, so nothing is
        // broken at write time. (Compact refuses on referencers instead: its renumber takes effect under the SAME
        // plugin name, a merge's only when the user disables the donors.)
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
        // The third dependent kind, and the one a merge breaks hardest: a plugin that lists a donor as a master while
        // referencing none of its records keeps loading only while the donor does. The game refuses to load a plugin
        // missing a master, so this is a WARN with the same standing as the two above — nothing breaks until the swap.
        if (o.MasterDeclarers is { Count: > 0 } declarers)
        {
            sb.Append("WARNING — ").Append(declarers.Count).Append(" plugin(s) OUTSIDE the merge DECLARE a donor as a MASTER ")
              .Append("without referencing any of its records. They are not in the lists above (nothing links to a donor ")
              .Append("record), but a plugin missing a master does not load at all, so each one breaks the moment you ")
              .Append(isRename ? "deactivate the donor plugin" : "deactivate the donor plugins")
              // The fix is the stale master reference, not a new one: these plugins reference nothing in the donor,
              // so adding the merged plugin as a master would buy them nothing. Combining is offered second for a
              // rename, as in the two warnings above, because it stops being a rename.
              .Append(". Remove that master in xEdit (nothing in them references it)")
              .Append(isRename ? " — or, to combine them instead, re-run with them added as donors" : ", or include them in the merge set")
              .Append(":\n");
            foreach (var d in declarers.Take(25))
                sb.Append("  ! ").Append(d.Plugin).Append("  — declares ").Append(string.Join(", ", d.Declared)).Append('\n');
            if (declarers.Count > 25) sb.Append("  ! … (+").Append(declarers.Count - 25).Append(" more)\n");
        }
        if (o.UnscannableRecords > 0)
            sb.Append("note: ").Append(o.UnscannableRecords).Append(" record(s) couldn't be scanned in the external-reference pass, so a ")
              .Append("'none' above may be incomplete — verify in xEdit. Samples: ").Append(string.Join("; ", o.UnscannableSamples)).Append('\n');
        AppendUnscannablePlugins(sb, o.UnscannablePlugins);
        // The coverage caveat belongs to the PASS, not to either outcome: a "none" and a populated list are incomplete
        // the same way. Declared masters ARE read now; runtime config files still are not, and naming what is left out
        // is what keeps the pass from claiming more than it measured.
        sb.Append("identify-pass scanned ").Append(o.PluginsScanned).Append(" plugin(s) — it reads record links, record ")
          .Append("identity and declared masters, NOT runtime config files (SPID, KID, SkyPatcher, Open Animation ")
          .Append("Replacer), so a plugin that only names a donor in such a file is not counted above.\n");
        // The caveat above says those files are not READ; this says what that costs, which a caller cannot derive from
        // "not counted". Both are bounded by the claim rule at WriteSentences.MergeRuntimeConfigs: this tool's own
        // behaviour, plus a break entailed by the swap this same report instructs.
        sb.Append(WriteSentences.MergeRuntimeConfigs);

        AppendFacegenCarry(sb, o.AssetRename, inPlace: false);
        AppendVoiceCarry(sb, o.VoiceRename, inPlace: false);
        AppendSeqRegen(sb, o.SeqRegen, inPlace: false);

        // The merged plugin is built as a bare mod, so what lived in a donor's HEADER does not come along. Keyed on
        // what the DONORS carried, not on the donor count — a silent header loss has to be stated.
        bool lightNoteShown = o.LightCarried || o.LightDonors is { Count: > 0 };
        if (o.LightCarried)
        {
            // The one case where nothing is dropped, and it still gets a line: the caller has to know the output is
            // light without opening it, because that is what decides whether it costs a load-order slot.
            sb.Append("NOTE — every donor carried the LIGHT (ESL) status and every merged object id landed inside the ")
              .Append("light window (0x").Append(HousecarlCore.FormIdRange.EslWindowFloor.ToString("X3")).Append("–0x")
              .Append(HousecarlCore.FormIdRange.EslWindowCeiling.ToString("X3")).Append("), so ").Append(o.OutputName)
              .Append(" is written LIGHT too — it takes no full load-order slot.\n");
        }
        else if (o.LightDonors is { Count: > 0 } light)
        {
            sb.Append("NOTE — ").Append(string.Join(", ", light.Take(10)));
            if (light.Count > 10) sb.Append(" (+").Append(light.Count - 10).Append(" more)");
            // Why the flag was not carried, never a bare drop. The REASON is which of the two conditions failed; the
            // REMEDY is a separate question, because compact renumbers every id into the light window and so answers
            // both reasons — until the record count itself overflows that window, which is the only case it cannot
            // answer. So the reason is written per condition and the remedy per count. The ids-fit condition itself
            // fails two ways — a donor id already above the ceiling, or a merge with more records than the window
            // holds — and only the count tells them apart, so the reason reads it too rather than blaming a high
            // donor id a crowded merge does not have.
            const int lightCapacity = (int)(HousecarlCore.FormIdRange.EslWindowCeiling - HousecarlCore.FormIdRange.EslWindowFloor + 1);
            string window = "0x" + HousecarlCore.FormIdRange.EslWindowFloor.ToString("X3") + "–0x" +
                            HousecarlCore.FormIdRange.EslWindowCeiling.ToString("X3");
            bool countFits = o.OriginatingRecords <= lightCapacity;
            sb.Append(" carried the LIGHT (ESL) status; ")
              .Append(o.OutputName).Append(" does NOT — it is written as a full plugin and takes a full load-order slot. ")
              .Append(light.Count < o.Donors.Count
                  ? "Not every donor was light (" + light.Count + " of " + o.Donors.Count + " were), and merged content " +
                    "that was never light-legal as a whole cannot be carried as light. "
                  : countFits
                      ? "Every donor was light, but not every merged object id landed inside the light window (" + window +
                        ") — a merge renumbers only what it must, so an id already above the ceiling is kept where it is. "
                      : "Every donor was light, but the donors together define more records than the light window (" +
                        window + ") holds, so merged ids land outside it. ")
              .Append(countFits
                  ? "To make it light run " + ToolNames.CompactPlugin + " on '" + o.OutputName + "' (its esl defaults " +
                    "true) — but that renumbers object ids from 0x800 upward, so the ids listed as kept above will move.\n"
                  : ToolNames.CompactPlugin + " cannot make it light either: it renumbers into the same window, and " +
                    o.OriginatingRecords + " originating records do not fit its " + lightCapacity + " ids. Merge fewer " +
                    "donors if a light output is what you need.\n");
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
        // The donors' text is the other thing the bare output does not carry across as it was. The merged plugin is
        // never flagged localized, so a localized donor's values are written INTO it and its .STRINGS stop describing
        // it — the plugin's nature changed, and a report that called that an ordinary success said nothing about it.
        // Keyed on donors houseCARL READ and found flagged, so nothing is asserted about a donor it could not read.
        if (o.LocalizedDonors is { Count: > 0 } localized)
        {
            sb.Append("NOTE — ").Append(string.Join(", ", localized.Take(10)));
            if (localized.Count > 10) sb.Append(" (+").Append(localized.Count - 10).Append(" more)");
            sb.Append(localized.Count == 1 ? " is flagged LOCALIZED — its text lives" : " are flagged LOCALIZED — their text lives")
              .Append(" in separate .STRINGS files rather than in the plugin. ").Append(o.OutputName)
              .Append(" is NOT localized: it carries whatever this read of the donors produced, written into the plugin ")
              .Append("itself and with no .STRINGS files of its own — so the donors' .STRINGS no longer describe it, and ")
              .Append("any language they shipped that this read did not resolve is not in the output. Read the output ")
              .Append("before you swap it in.\n");
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
        // ONE home for the compact recommendation per response: the light note already names the tool with the cost
        // that makes the advice honest (it renumbers from the floor), so a flat repeat here would end on the version
        // without the cost. With no light note there is no other pointer, so the tail stays.
        if (!lightNoteShown)
            sb.Append(" Want it light? Run " + ToolNames.CompactPlugin + " on '").Append(o.OutputName).Append("' (the tools compose).");
        return sb.ToString();
    }

    /// <summary>Confirmation for housecarl_create: the new record's ALLOCATED FormID + editorid + type (the FormID
    /// is the key output — the caller references the new record by it), the patch path + its (derived) masters, and the
    /// fields applied. On refusal, the named reason so the caller can fix and retry.</summary>
    internal static string RenderCreate(WritePatchBuilder.CreateOutcome o, int maxChars = 0, bool fullDump = false)   // internal: housecarl_create renders the same outcome
    {
        if (o.NeedsAcknowledge) return o.Error! + Epoch(o);  // the in-place consent prompt is a required confirmation, not an error
        if (!o.Success) return "error: " + o.Error + Epoch(o);
        var file = Path.GetFileName(o.OutputPath);
        var modFolder = Path.GetFileName(Path.GetDirectoryName(o.OutputPath) ?? "");
        var sb = new StringBuilder();
        if (o.InPlace)
            // "created into X", not "X rewritten": every sibling in-place headline is verb-then-file (edited /
            // forwarded into / removed from / compacted), and "rewritten" stutters against the hazard clause behind it.
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
        // Budgeted: this is the render's LARGEST block and it is SET-VALUED — a 500-record authoring job from
        // records="@<manifest>" is the case this tool exists for. The json render budgets the same array and closes
        // truncated:true, so unbounded here the two disagree about one call and text takes a silent host-side cut.
        int createCap = WriteSentences.Cap(maxChars);
        // Which version of a contested parent this artifact carries is a choice the caller may want to act on, and it
        // is not IN the record — so it is hoisted out of the budget below, where a max_chars cut would remove the
        // per-record `parent:` lines. One line per distinct contested parent; the benign cases say nothing. Selected
        // on the FLAG, never by matching the sentence. Bounded too, or a create fanning children into many contested
        // cells eats the budget before the created list starts; the bound lives on Wire so the json render shares it.
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
                // The remedy points at a READ, never at re-issuing this call: a repeated CREATE allocates the records
                // AGAIN — on the default lane patch= auto-suffixes into a second full patch, and under into= each
                // record is re-created at its old FormID with its prior contents discarded.
                sb.Append("  ... [truncated: ").Append(ci).Append(" of ").Append(o.Created.Count)
                  .Append(" created record(s) listed at max_chars=").Append(createCap).Append("; ")
                  .Append(WriteSentences.CreateRowsCutRemedy(ReadBackCall(o, file))).Append("]\n");
                break;
            }
            var c = o.Created[ci];
            listed++;
            sb.Append("  ").Append(c.RecordType).Append(' ').Append(c.FormKey).Append("  ").Append(c.EditorId);
            if (c.ReplacedExisting) sb.Append("  [REPLACED: this patch already defined this editorid — re-created fresh at the same FormID; prior contents, including any " + ToolNames.Apply + " edits since, were discarded]");
            sb.Append('\n');
            // A nested create had to override its parent in to host the child, and WHOSE version it copied is a choice
            // the caller never made and cannot see in the record afterwards. One line, only when there was one.
            if (c.ParentHost is { } host) sb.Append("      parent: ").Append(host).Append('\n');
            foreach (var op in c.Ops)
                sb.Append("      ").Append(op.Label).Append(op.After is not null ? "  -> " + op.After : "  -> applied")
                  .Append(ApplyNote(op)).Append('\n');
        }
        AppendVoiceReport(sb, o.Voice, maxChars);
        AppendScriptBindingReport(sb, o.ScriptBinding, maxChars);
        AppendCellShellReport(sb, o.CellShell, maxChars);
        // Same compact-by-default verify as the edit lane: the forced create-in-place re-read still runs;
        // full_readback=true gives the deep dump. The created records' set fields are already listed above.
        if (o.ReadBack is { } rb)
        {
            if (fullDump) AppendFullReadback(sb, rb, maxChars);
            else AppendCompactReadback(sb, Array.Empty<WritePatchBuilder.OpResult>(), rb, maxChars);
        }
        if (o.Note is { } note) sb.Append("note: ").Append(note).Append('\n');
        // Gated on rows having actually rendered: with a small max_chars the header alone can exceed the cap and drop
        // EVERY row, and "the new FormID above" would then assert a referent this render never printed.
        sb.Append(listed > 0
            ? "the new FormID above is how you reference this record (SkyPatcher/SPID, or a follow-up edit). "
            : $"no records are listed above — the char budget cut the whole list, though all {o.Created.Count} WERE created. Read them back with {ReadBackCall(o, file)} to get their FormIDs. ");
        sb.Append(o.InPlace
            ? InPlaceAgainHint("To create more records in this plugin", file)
            : $"To add more to THIS patch, pass into=\"{file}\".");
        sb.Append(Epoch(o));
        return sb.ToString();
    }

    /// <summary>What a truncated REMOVE render tells the caller. Removal is ALL-OR-NOTHING over the <c>formids=</c>
    /// they passed, so the listed set and the passed set are the same set — and re-issuing to widen the render is
    /// REFUSED, because the records no longer exist to be found.</summary>
    internal const string RemovedRowsRemedy =                       // internal: the json render says the same thing
        "removal is all-or-nothing, so these rows are exactly the formids= you passed — nothing here is unrecoverable. "
      + "Do NOT re-issue to widen this: the records are gone, so a repeat is refused as 'not carried by' the file";

    /// <summary>What a truncated FORWARD render tells the caller to do — which depends on the LANE. Re-issuing is safe
    /// on <c>into=</c> (replace-on-collision, so the second call lands on the same FormKeys) and on a dry run, which
    /// writes nothing; on the DEFAULT lane <c>ResolveOutputPath</c> allocates a fresh stem, so a re-issue is a SECOND
    /// patch mod carrying the same overrides. <c>in_place=</c> gets the read-back remedy instead: a re-run there
    /// re-serializes the caller's OWN plugin, the one file with no houseCARL backup, purely to widen a display.</summary>
    internal static string ForwardAgainRemedy(WritePatchBuilder.ForwardOutcome o, string file)   // internal: the json render says the same thing
        => WriteAgainRemedy(o.DryRun, o.InPlace, o.Extended, file, "patch mod carrying the same overrides");

    /// <summary>The lane rule generalized: whether re-issuing a write call to widen a display is safe is a property of
    /// the LANE, not of the verb. A dry run wrote nothing; <c>into=</c> lands on the same artifact; <c>in_place=</c>
    /// re-serializes the caller's own file; the DEFAULT lane auto-suffixes a second patch.</summary>
    static string WriteAgainRemedy(bool dryRun, bool inPlace, bool extended, string file, string duplicateNoun)
        => dryRun || extended
            ? "raise max_chars to see the rest"
            : inPlace
                ? $"to see the rest, read the rows back with {ToolNames.Records} source=\"{file}\" formids=[the ids you passed] — re-issuing would re-serialize your ORIGINAL file a second time just to widen this render"
                : $"to see the rest, raise max_chars AND pass into=\"{file}\" — a bare re-issue on the default patch= lane writes a SECOND {duplicateNoun}";

    /// <summary>The <c>apply</c> lane's wording of <see cref="WriteAgainRemedy"/>.</summary>
    internal static string ApplyAgainRemedy(WritePatchBuilder.PatchOutcome o, string file)
        => WriteAgainRemedy(o.DryRun, o.InPlace, o.Extended, file, "patch mod carrying the same edits");

    /// <summary>The read-back call a truncated create render points the caller at — one that actually RESOLVES.
    /// <c>source=</c> is the SOURCE pole, not a SELECT term, and a <c>plugins=</c> scope names ACTIVE plugins, so
    /// neither can select a patch this call just wrote and MO2 has not enabled. <c>types=</c> is the select term that
    /// carries on both arms, and the created records' <c>RecordType</c>s are catalog names, exactly what it resolves.</summary>
    internal static string ReadBackCall(WritePatchBuilder.CreateOutcome o, string file)   // internal: the json render points at the SAME call
    {
        var types = o.Created.Select(c => c.RecordType).Where(t => !string.IsNullOrWhiteSpace(t))
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        // Every distinct type, never a sampled head: a partial types= would select a partial answer while reading like
        // the whole one. The empty fallback is unreachable on a successful create, but a bare source= call is refused,
        // so the clause is not dropped either.
        return types.Count > 0
            ? $"{ToolNames.Records} source=\"{file}\" types=[{string.Join(", ", types.Select(t => $"\"{t}\""))}]"
            : $"{ToolNames.Records} source=\"{file}\" types=[<the record types you created>]";
    }

    /// <summary>Render the voice-coverage report for a dialogue-line create, so a byte-valid line is never silently a
    /// silent one: a loud "WILL BE SILENT" per created voiced response with no .fuz on disk (naming the path to put the
    /// audio at), a brief "voice present" for covered ones, and a NAMED reason per line whose path could not be
    /// computed. Reports the on-disk data-layer boundary; it never generates audio. No-op unless lines were created.</summary>
    static void AppendVoiceReport(StringBuilder sb, VoiceReport? report, int maxChars)
    {
        if (report is null || report.IsEmpty) return;
        // Budget-bounded like the full read-back (same maxChars contract): a create authoring hundreds of voiced lines
        // must not blow the response size or starve a requested read-back — past the cap the voice section stops with
        // an explicit notice, never a silent cut.
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

    /// <summary>The explicit voice-coverage truncation notice: how many of the total voice entries were rendered
    /// before the char budget was hit. Its closing clause is <see cref="WriteSentences.Twins.ReportBlockCut"/>, shared
    /// with the result-script notice and the json render so the two transports cannot give opposite advice about one
    /// call — this block rides the CREATE render, where "raise max_chars and re-issue" would mean creating again.</summary>
    static void AppendVoiceTrunc(StringBuilder sb, int rendered, int total, int cap)
        => sb.Append("  ... [voice coverage truncated: rendered ").Append(rendered).Append(" of ").Append(total)
             .Append(" line(s) at max_chars=").Append(cap).Append("; ").Append(WriteSentences.Twins.ReportBlockCut).Append("]\n");

    /// <summary>Render the structural-shell report for a cell create: the cell is a valid, correctly-placed RECORD,
    /// but houseCARL does not author world content, so per created cell this lists by kind what the author must still
    /// provide in the Creation Kit (lighting / terrain / water / navmesh). "Created" must never read as "looks right
    /// in game". No-op unless the call created cells.</summary>
    static void AppendCellShellReport(StringBuilder sb, CellShellReport? report, int maxChars)
    {
        if (report is null || report.IsEmpty) return;
        // Budgeted like its two siblings, or a create authoring a batch of cells renders every row in TEXT and takes
        // the silent host-side cut the json render already avoids. The inner MustProvide loop is bounded too: one cell
        // with a long work list could blow the budget by itself.
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
        // The notice, then the two notes below it — which stay OUTSIDE the budget on purpose, the same rule the
        // removal render states: they are the accounting a truncated report still needs, and the grid-occupancy
        // seam in particular must not be the thing a cut swallows.
        if (cut)
            sb.Append("  ... [cell shell truncated: rendered ").Append(rendered).Append(" of ").Append(total)
              .Append(" cell(s) at max_chars=").Append(cap).Append("; ").Append(WriteSentences.Twins.ReportBlockCut).Append("]\n");
        // Declare the un-checked grid-occupancy seam rather than omit it silently. Only an EXTERIOR cell collides on a
        // grid; an interior cell has no grid identity.
        if (report.Cells.Any(c => !c.Interior))
            sb.Append("  note: ").Append(WriteSentences.Twins.GridOccupancy).Append('\n');
        if (report.CheckError is not null)
            sb.Append(WriteSentences.CheckCouldNotRun("cell-shell", report.CheckError, "the cell(s)", "review world content manually."));
    }

    /// <summary>Render the result-script coverage report for a dialogue-line create, so a byte-valid script is never
    /// silently an inert one: a loud "WILL NOT FIRE" per created line whose VMAD binds nothing usable or names a script
    /// with no compiled `.pex` on disk (naming the missing path), a brief "OK" for ones fully wired, and a NAMED reason
    /// for any created INFO that could not be located. Compiling is housecarl_compile_script's job. No-op unless the
    /// call created scripted dialogue lines. Budget-bounded like the voice and read-back sections.</summary>
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
// ---- schema. Kept rather than collapsed into ApplyOp/CreateRecordSpec -- that reshape is deferred. ----

/// <summary>One edit operation off the wire. RecordType is NOT supplied — it is derived from the resolved winner's
/// runtime type. Mirrors <see cref="WritePatchBuilder.PatchEdit"/> with string FormID + dotted path + optional
/// composition.</summary>
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

    [JsonPropertyName("from_plugin"), Description("For verb=\"CopyFrom\" ONLY: the plugin whose version of THIS record to deep-copy the field at field_path from — an ACTIVE plugin, OR a plugin FILE on disk that isn't in the load order (e.g. a disabled OLD patch you want to re-assert a field from). CopyFrom takes no value/values/entries/compose/composes — the source IS from_plugin's version of the field. Honors forward-then-edit precedence: into= a patch that already carries the record copies onto the patch's own version. Copies a WHOLE field's value (scalar, formlink, modeled list, sub-struct); it can't copy owned child records (forward the whole record with " + ToolNames.Forward + " instead).")]
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

    [JsonPropertyName("operations"), Description("Optional. The new record's fields, same shape as " + ToolNames.Apply + " ops but with NO formid (and no from_plugin — there is no other version to copy from yet): {field_path, verb?, value?, key?, values?, entries?, compose?, composes?}.")]
    public BulkOp[]? Operations { get; init; }

    [JsonPropertyName("parent"), Description("Optional. For a NESTED record: the parent it nests under — an EXISTING parent's FormID 'XXXXXX:Plugin.esp', OR the editorid of a record declared EARLIER in this same records array (a same-call sibling). Omit for a flat top-level record.")]
    public string? Parent { get; init; }

    [JsonPropertyName("collection"), Description("Optional. Which of the parent's child slots to add into, BY NAME — a child list (e.g. a cell's 'Persistent') or a single-child slot (a cell's 'Landscape', a worldspace's 'TopCell') — needed only when more than one fits. Omit when unique or when parent is omitted.")]
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

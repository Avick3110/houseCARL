using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>The plugin-level write tools — create a header-only plugin, compact a plugin's FormIDs, merge plugins into
/// one — each taking a whole plugin FILE as its subject, riding its own core builder rather than the record-edit path,
/// and writing a NEW plugin except on compact's <c>in_place=</c> lane.
/// <para>Also the shared home of the RENDER helpers the record-write tools call, which are the bulk of what
/// follows.</para></summary>
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
         "(no filler override needed to make the plugin non-empty). patch is used EXACTLY (the basename is " +
         "load-bearing — houseCARL will NOT auto-suffix it): if a plugin of that name is already active in the load order, " +
         "or a houseCARL folder of that name already exists, it REFUSES loud rather than rename or overwrite (Q3). Pass " +
         "esl=true for the lightest trigger (a header-only light plugin consumes no consequential load-order slot; with " +
         "zero records the ESL FormID-range rule is trivially satisfied). author/description are optional TES4 header " +
         "text. Returns the plugin path + mod folder — enable it in MO2 to make the basename exist; if it is there to load a " +
         "<stem>.bsa or to reserve a FormID range, its POSITION in the load order still decides that archive's precedence and " +
         "that range. To author actual records, use " +
         ToolNames.Create + " instead.")]
    public static string CreatePlugin(
        LoadOrderService svc,
        [Description("The EXACT plugin name (with or without a trailing .esp/.esm/.esl; e.g. 'Authoria - CraftingCategories'). Used VERBATIM as the basename — houseCARL will not auto-suffix it, because a trigger plugin's whole job is that its basename matches the config bound to it. The written file is '<name>.esp', and a name that already exists as a plugin — .esp, .esm or .esl — anywhere on your install, active or somewhere your order is not loading it, is refused, naming that place and the file.")]
            string patch,
        [Description("When true, flag the plugin as a light master (ESL) — the lightest possible trigger: a header-only ESL consumes no consequential load-order slot. Default false (a normal full plugin).")]
            bool esl = false,
        [Description("Optional. Author text for the TES4 header (the CNAM field). Purely informational.")]
            string? author = null,
        [Description("Optional. Description text for the TES4 header (the SNAM field). Purely informational.")]
            string? description = null) => Guard.Tool(ToolNames.CreatePlugin, () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        if (string.IsNullOrWhiteSpace(patch))
            return "error: patch is empty. Name the plugin to create (a header-only plugin has no record to derive a name from).";
        return RenderCreatePlugin(svc.CreatePlugin(patch, esl, author, description));
    });

    [McpServerTool(Name = ToolNames.CompactPlugin, Title = "Compact / ESL-renumber a plugin's FormIDs"),
     Description(
         "COMPACT a plugin's FormIDs — the data-layer twin of xEdit's \"Compact FormIDs for ESL\". Renumbers EVERY record " +
         "the plugin DEFINES (its originating records — flat AND nested: cells, placed references, dialogue lines, navmesh, " +
         "landscape), repoints every reference WITHIN the plugin, and leaves its overrides of other mods at their master " +
         "FormIDs.\n\n" +
         "The grammar is on the parameters: source= names the target — where it is resolved from, and its refusals; esl= " +
         "the window and the light flag — the ESL ceiling and the override-only lanes; in_place= which file is written — " +
         "the new-plugin default with its MO2 swap, the overwrite lane, and what each does to a LOCALIZED plugin; " +
         "repoint_externals= the external-referencer safety; acknowledge= the consent any in-place rewrite needs; " +
         "patch= the new mod folder.\n\n" +
         "Note: references compiled into Papyrus scripts (.pex hardcoded FormIDs / GetFormFromFile) are NOT remappable — " +
         "verify scripted records after compacting.")]
    public static string CompactPlugin(
        LoadOrderService svc,
        [Description("The plugin's filename to compact (e.g. 'CoolMod.esp'). The target need NOT be active: usually it is in your load order, but a plugin on disk and not (yet) in it — the patch houseCARL just wrote, before the MO2 refresh; a plugin inside a disabled mod — is resolved by filename across ALL mod folders and compacted OFF-ORDER. Whichever lane the target came from, active or not, its declared masters must still be active: a declared master not active is refused loud and nothing is written. The compacted output keeps this EXACT basename. Refuses loud + writes nothing on: this plugin found nowhere on disk / ambiguous across folders / unparseable; a serialize fault.")]
            string source,
        [Description("When true (default), renumber into the light/ESL range (0x800–0xFFF, 2048 IDs) and flag the result a light master (ESPFE) — the canonical 'compact for ESL', which frees a load-order slot. false = renumber contiguously from 0x800 with no light flag or 2048 ceiling (just closes FormID gaps). An override-only plugin with esl=true takes the FLAG-ONLY lane: nothing to renumber, every record copies verbatim, the ESL flag is set (always valid — the light window only constrains originating records); with esl=false there is nothing to do, and that is refused loud with nothing written. Refused loud with nothing written too: with esl=true, MORE records than the light range holds (the hard 2048 ESL ceiling — named, never truncated).")]
            bool esl = true,
        [Description("Optional, default false. IN-PLACE LANE (opt-in): OVERWRITE the original plugin with its compacted form (xEdit's norm) instead of writing a new file — NO houseCARL backup or undo (keep your own). Rides the in-place consent: requires acknowledge=true. OMIT (the default) to write a NEW plugin instead, keeping the SOURCE'S EXACT basename (so other mods that list it as a master still resolve) in a fresh houseCARL mod folder, leaving the original untouched: review the new plugin in xEdit, then in MO2 enable its folder and DISABLE the original mod (same basename — MO2 serves one). LOCALIZED PLUGINS: houseCARL does not rewrite one in place — its text lives in separate .STRINGS files it cannot swap together with the plugin — so a LOCALIZED plugin is REFUSED in this lane whatever arrangement its .STRINGS files are in. The new-plugin lane still compacts it — UNLESS its .STRINGS resolve NOWHERE (houseCARL can see them nowhere, or the folder holding them cannot be read), which that lane refuses too: every name, description and message would read back EMPTY (or, from a folder nothing could open, unknowable) in a plugin with nothing left in it to tell that text from one that never had any. Otherwise the output is DE-LOCALIZED — the text this read resolved is written into the plugin itself and the source's .STRINGS files no longer describe it — and the report says so. The review step above is where you catch that: read the output's TEXT before swapping it in. ONE MORE REFUSAL on this lane, before the consent gate and whatever acknowledge says: if the external-reference pass could not READ some plugin, houseCARL cannot tell whether it references records about to be renumbered, so any in-place rewrite (the target, its referencers, or both) is refused and nothing is written — the new-plugin lane carries that as a note instead.")]
            bool in_place = false,
        [Description("Optional, default false. THE SAFETY (Q3): renumbering breaks any reference from OUTSIDE this plugin (they'd point at FormIDs that vanish), so whenever there is anything to renumber houseCARL scans the WHOLE load order for such external referencers (a one-pass walk — can take ~25s on a big order): if NONE, it's a clean compaction; if SOME, the call REFUSES (listing them) by default. Set true to ALSO rewrite those external referencers IN PLACE to follow the renumber (requires in_place=true, so the target and its referrers move together, AND acknowledge=true; no backup of them either). Refused when any referencer is LOCALIZED or is one houseCARL CANNOT READ — it rewrites neither a localized plugin nor one it cannot read in place — and the default refusal above says which referencers are in which state, and where a localized one's text is, so you learn that BEFORE choosing this flag rather than being sent here.")]
            bool repoint_externals = false,
        [Description("Optional, default false. Confirms the in-place trade-off when in_place=true OR repoint_externals=true (your original file(s) get rewritten, no backup). The FIRST such call without it returns a CONFIRM prompt listing exactly what will be overwritten — re-call with acknowledge=true to proceed.")]
            bool acknowledge = false,
        [Description("Optional. Base name for the NEW mod folder (new-file lane only; auto-suffixed if taken). Ignored with in_place=true. The PLUGIN inside ALWAYS keeps the source's exact basename so external masters still resolve.")]
            string? patch = null) => Guard.Tool(ToolNames.CompactPlugin, () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        if (string.IsNullOrWhiteSpace(source))
            return "error: source is empty. Name the plugin filename to compact (e.g. 'CoolMod.esp').";
        return RenderCompact(svc.CompactPlugin(source, esl, in_place, repoint_externals, acknowledge, patch));
    });

    [McpServerTool(Name = ToolNames.MergePlugins, Title = "Merge plugins into one new plugin"),
     Description(
         "MERGE one or more ACTIVE plugins into ONE NEW plugin — a RECORDS operation (the zMerge/'Merge Plugins' job): the " +
         "donors' records combine under a new filename; the donor FILES and their mods are NEVER touched (new-file lane only, " +
         "no in-place).\n\n" +
         "THE NORMAL JOB: merge a FAMILY OF PATCHES into one plugin and leave the mods they patch alone and active — that is " +
         "what reclaims load-order slots without changing what any mod does. Merging a base mod TOGETHER WITH its own patches " +
         "is a narrow case, not the shape to reach for: it moves that mod's records to a new plugin identity, so everything " +
         "else that patches or references it must be merged or repointed too.\n\n" +
         "The grammar is on the parameters: plugins= holds the donor set — the single-donor rename lane, the renumber and " +
         "cross-donor conflict rules, the outside-referencer warning, and the donor refusals; patch= the new mod folder, whose " +
         "name the merged plugin inside it takes — the asset carry and what it costs existing saves.\n\n" +
         "AFTER: review the merged plugin in xEdit, enable its mod folder in MO2, then deactivate the donor PLUGINS (right " +
         "pane) but KEEP the donor MOD FOLDERS enabled (left pane) — merge carries only the FormID-keyed files the rename " +
         "breaks (facegen/voice/seq); every other donor asset (meshes, textures, scripts, BSA contents) is still referenced " +
         "BY PATH from the merged records and loads from the donor folders. Caveat: a donor .bsa stops auto-loading once its " +
         "same-named plugin is inactive — extract it into the mod folder (" + ToolNames.BsaExtract + ") or load it via a same-named " +
         "dummy plugin (" + ToolNames.CreatePlugin + ").\n\n" +
         "A donor's HEADER mostly does not come along: master (ESM) status and Author/Description are always dropped, and the " +
         "report names each drop. Light (ESL) status is carried only when every donor was light and every merged object id fits " +
         "the light window 0x800–0xFFF; otherwise the report says which of the two reasons it was, and whether " +
         ToolNames.CompactPlugin + " can still make it light.")]
    public static string MergePlugins(
        LoadOrderService svc,
        [Description("The donor plugin filenames to merge (at least one, e.g. [\"CoolMod.esp\", \"CoolMod Patch.esp\"]) — each must be active in your load order: a donor not active is refused loud and nothing is written. This is a SET: a name repeated is still one donor. A SINGLE donor renames it into patch=: with nothing to combine the merge IS a rename — the same records under a new plugin name, keeping every object id already inside the writable range (nothing can collide; an id BELOW the 0x800 floor still renumbers, and the per-donor line reports it), facegen/voice/seq carried to the new name. Argument order does not matter: houseCARL uses LOAD order for id priority and conflict resolution. RENUMBER is collision-first (zMerge's default): the donor EARLIEST in the load order keeps its FormID object ids; later donors renumber ids already taken, and ANY donor's ids below the 0x800 floor renumber too (all records necessarily move to the new plugin's identity). Cross-donor conflicts on the SAME record resolve to the LOAD-ORDER WINNER and are each REPORTED; a losing donor's nested children the winner doesn't re-list (a base mod's dialogue lines under a patched topic; placed refs under a patched cell) are GRAFTED into the winner's copy, which is what lets a patch family merge cleanly even where one patch relists a topic another one fills. THE SAFETY (Q3): plugins OUTSIDE the merge that reference or override donor records are WARNED and NAMED, never refused — the donors stay active until you swap in MO2, so nothing breaks at write time; the remedy is to include those patches in the merge set or re-point them before disabling the donors. Refuses loud + writes nothing on: a donor unparseable / not on disk; a dangling donor-internal reference (a donor referencing a FormID in donor space that no donor holds); a declared master not active. Each of those is refused before anything is written. A record INJECTED into one donor's FormID space and carried by another is merged like any other record, renumbered into the output; a plugin outside the merge carrying it too is WARNED about by name, as every external overrider is.")]
            string[] plugins,
        [Description("Base name for the NEW mod folder this merge creates (e.g. 'MyMerge'); the merged plugin inside it takes that name too, so patch='MyMerge' writes 'houseCARL - MyMerge\\MyMerge.esp'. A name already taken is REFUSED by name and nothing is written — never auto-suffixed, because the merged plugin's exact basename is what a _DISTR.ini, a _KID.ini, a config or a dependent's master entry binds to. That covers a mod folder 'houseCARL - MyMerge' already under your mods directory, an output plugin already in your load order, and a name that exists somewhere your order is not loading it (another mod folder, the overwrite folder, or game Data) — each refusal naming what is in the way. The donors keep their names and files untouched. ASSETS follow the renumber into this name: every donor NPC's facegen and every voiced line are carried into the new plugin-name folders (those paths embed the plugin NAME, so ALL donor facegen/voice moves, not just collisions), and a .seq is refreshed when any donor shipped one. Existing SAVES that depend on the donors will NOT survive (the records now live under this plugin name, and any id that had to be renumbered moved with it) — best for a new game.")]
            string patch) => Guard.Tool(ToolNames.MergePlugins, () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        return RenderMerge(svc.MergePlugins(plugins, patch));
    });

    /// <summary>Confirmation of a write: what changed plus the IDs needed for follow-up, or on a refusal every
    /// malformed or rejected op.</summary>
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
        // Said ABOVE the ops and outside their budget; contract in docs/architecture/write-path.md.
        int absent = o.Ops.Count(op => op.RecordAbsentFromFile);
        if (absent > 0)
            sb.Append("! ").Append(absent).Append(absent == 1 ? " edit did NOT land: " : " edits did NOT land: ")
              .Append(WriteSentences.RecordAbsentFromWrittenFile).Append(". ")
              .Append(WriteSentences.AbsentRecordList(
                  o.Ops.Where(op => op.RecordAbsentFromFile).Select(op => FormIdToken.Of(op.Target))
                       .Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
              .Append('\n');
        sb.Append(o.Ops.Count).Append(o.Ops.Count == 1 ? " edit:\n" : " edits:\n");
        // Budgeted like every sibling render, the json one budgeting the same array.
        int opCap = WriteSentences.Cap(maxChars);
        for (int i = 0; i < o.Ops.Count; i++)
        {
            if (sb.Length >= opCap)
            {
                sb.Append("  ... [truncated: ").Append(i).Append(" of ").Append(o.Ops.Count)
                  .Append(" edit(s) listed at max_chars=").Append(opCap).Append("; ")
                  .Append(WriteSentences.RowsCutOperationIntact(false, "applied", absent > 0))
                  .Append(" — ").Append(ApplyAgainRemedy(o, file)).Append("]\n");
                break;
            }
            var op = o.Ops[i];
            sb.Append("  ").Append(op.RecordType).Append(' ').Append(FormIdToken.Of(op.Target)).Append("  ").Append(op.Label)
              .Append(EditLineValue(op)).Append(ApplyNote(op)).Append('\n');
        }
        // The .fuz/.lip and result-script checks run on CREATE only, so an edit carries the same hazard unflagged.
        if (o.Ops.Any(op => string.Equals(op.RecordType, VoiceCheck.InfoCatalogName, StringComparison.Ordinal)))
            sb.Append("note: this edit touched a dialogue line (INFO). Voice (.fuz) and result-script coverage are checked on CREATE, not on edits — ")
              .Append("run " + ToolNames.Check + " findings=[\"dialogue\"] with the topic (or its owning quest) in seeds= to audit voice + result-script coverage and the topic graph over the edited line and every other line in the topic.\n");
        // The touched-record verify renders COMPACT by default; contract in docs/architecture/write-path.md.
        if (o.ReadBack is { } rb)
        {
            if (fullDump) AppendFullReadback(sb, rb, maxChars, freshPatch: !o.Extended && !o.InPlace);
            else AppendCompactReadback(sb, o.Ops, rb, maxChars);
        }
        if (o.Warning is { } warn) sb.Append("warning: ").Append(warn).Append('\n');
        if (o.Note is { } note) sb.Append("note: ").Append(note).Append('\n');
        sb.Append(o.InPlace
            ? InPlaceAgainHint("to make more in-place edits to this plugin", file)
            : $"to add more edits to THIS patch, pass into=\"{file}\".");
        sb.Append(Epoch(o));
        return sb.ToString();
    }

    /// <summary>The epoch stamp: one thin adapter per outcome record over <see cref="WriteSentences.Epoch"/>.</summary>
    static string Epoch(WritePatchBuilder.PatchOutcome o) => WriteSentences.Epoch(o.Stamp);
    static string Epoch(WritePatchBuilder.CreateOutcome o) => WriteSentences.Epoch(o.Stamp);
    static string Epoch(WritePatchBuilder.RemovalOutcome o) => WriteSentences.Epoch(o.Stamp);
    static string Epoch(WritePatchBuilder.ForwardOutcome o) => WriteSentences.Epoch(o.Stamp);

    /// <summary>The "keep going on this plugin" line for a completed IN-PLACE write, in the one declared spelling.</summary>
    static string InPlaceAgainHint(string verb, string file) =>
        $"{verb}, pass in_place=\"{file}\" again (no further confirmation needed for it).";

    /// <summary>The dry_run=true confirmation: the same pipeline ran and stopped at the point of no return, so this
    /// reports what WOULD change with nothing on disk, and says so first. Contract in docs/architecture/write-path.md.</summary>
    static string RenderDryRun(WritePatchBuilder.PatchOutcome o, int maxChars, bool fullDump)
    {
        var file = Path.GetFileName(o.OutputPath);
        var sb = new StringBuilder();
        sb.Append(WriteSentences.DryRunHeader);
        sb.Append(WriteSentences.DryRunWouldWrite(o.InPlace, o.Extended, file, "edit"));
        sb.Append(WriteSentences.DryRunMasters(o.Masters));
        sb.Append(o.Ops.Count).Append(o.Ops.Count == 1 ? " edit would apply:\n" : " edits would apply:\n");
        // Budgeted as the real render's loop is, the cut notice taking the dry run's own wording.
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
            sb.Append("  ").Append(op.RecordType).Append(' ').Append(FormIdToken.Of(op.Target)).Append("  ").Append(op.Label)
              .Append(op.After is not null ? "  -> would become " + op.After : "  -> would apply").Append(ApplyNote(op)).Append('\n');
        }
        if (fullDump && o.ReadBack is { } rb) AppendFullReadback(sb, rb, maxChars, dryRun: true);
        if (o.Warning is { } warn) sb.Append("warning: ").Append(warn).Append('\n');
        if (o.Note is { } note) sb.Append("note: ").Append(note).Append('\n');
        sb.Append(WriteSentences.DryRunClose("every op passed resolve + pre-flight", "apply"))
          .Append(Epoch(o));
        return sb.ToString();
    }

    /// <summary>The full_readback=true section: each touched or created record IN FULL, re-read from the written file
    /// and labelled as its content rather than load-order truth, bounded at the lower
    /// <see cref="Wire.ReadbackMaxChars"/> default.</summary>
    /// <param name="freshPatch">The LANE: a fresh patch needs only enabling, an extended or in-place one a re-sort.</param>
    static void AppendFullReadback(StringBuilder sb, IReadOnlyList<WritePatchBuilder.FullReadback> rb, int maxChars,
        bool dryRun = false, bool freshPatch = false)
    {
        int cap = WriteSentences.ReadbackCap(maxChars);
        // A dry run's records come from the IN-MEMORY would-be content — say so, never imply a file exists.
        sb.Append(dryRun
            ? "full preview — the ENTIRE record(s) as they WOULD be written, read from the in-memory would-be content (nothing is on disk):\n"
            : "full read-back — the ENTIRE record(s) as written, re-read from the patch file on disk " +
              "(the written file's content, NOT load-order truth; the patch wins nothing until it is enabled"
              + (freshPatch ? "" : " and sorted") + " in MO2):\n");
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
            if (r.Error is not null) { sb.Append("  ").Append(FormIdToken.Of(r.Target)).Append("  error: ").Append(r.Error).Append('\n'); continue; }
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
                sb.Append("    ").Append(f.Path).Append(" = ").Append(f.HasValue ? f.Token : f.Note);
                // The BLOB annotation only, gated on the bytes marker rather than on the Display a flags decode rides.
                if (f.Bytes is not null && f.Display is not null) sb.Append("   (").Append(f.Display).Append(')');
                sb.Append('\n');
            }
        }
    }

    /// <summary>The DEFAULT render of the touched-record verify: per record a re-read-clean marker and field count or
    /// the NAMED failure, then each op's "what landed" identity. The forced re-read still ran; this reports it
    /// compactly, over every record and bounded by the same cap.</summary>
    static void AppendCompactReadback(StringBuilder sb, IReadOnlyList<WritePatchBuilder.OpResult> ops,
        IReadOnlyList<WritePatchBuilder.FullReadback> rb, int maxChars)
    {
        int cap = WriteSentences.ReadbackCap(maxChars);
        // The banner claims only the re-read; each per-op clause says whose answer it is.
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
            // A re-read that failed is surfaced LOUD and NAMED, never counted as clean.
            if (r.Error is not null) { sb.Append("  ✗ ").Append(FormIdToken.Of(r.Target)).Append(" — ").Append(r.Error).Append('\n'); continue; }
            var rec = r.Record!;
            sb.Append("  ✓ ").Append(rec.Type).Append(' ').Append(rec.FormKey)
              .Append(" — re-read clean (").Append(rec.Fields.Count).Append(" field(s)")
              .Append(OpaqueBytesCaveat(rec)).Append(')');
            // The per-op clause is the FILE's answer where there was one, and marked as the edit's claim otherwise.
            var landed = ops.Where(op => op.Target == r.Target && (op.LandedOnDisk ?? op.Landed) is not null)
                             // No ApplyNote here: the op line above already carried it, and readback is FORCED on the
                             // in-place lane, so appending it would print the same sentence twice for the same op.
                             .Select(op => $"{op.Label}: {op.LandedOnDisk ?? op.Landed}" + LandedProvenance(op))
                             .ToList();
            if (landed.Count > 0) sb.Append("; ").Append(string.Join("; ", landed));
            sb.Append('\n');
        }
    }

    /// <summary>The clause that keeps "re-read clean" honest over an opaque blob (#529), bounded by
    /// <see cref="OpaqueFieldsNamed"/>; contract in docs/architecture/write-path.md.</summary>
    static string OpaqueBytesCaveat(RecordFields rec)
    {
        var opaque = rec.Fields.Where(f => f.Bytes is not null).ToList();
        if (opaque.Count == 0) return "";
        var named = string.Join(", ", opaque.Take(OpaqueFieldsNamed).Select(f => f.Path + " (" + f.Bytes + " byte(s))"));
        var more = opaque.Count > OpaqueFieldsNamed ? " and " + (opaque.Count - OpaqueFieldsNamed) + " more" : "";
        return ", except " + named + more + " — re-read as bytes only, structure NOT checked";
    }

    /// <summary>How many opaque fields the verify caveat names before it falls back to a count.</summary>
    const int OpaqueFieldsNamed = 3;

    /// <summary>The op's apply-time note as a trailing clause: what the write DID that the file cannot say afterwards.</summary>
    static string ApplyNote(WritePatchBuilder.OpResult op) => op.ApplyNote is { } n ? "  [" + n + "]" : "";

    /// <summary>The value clause on a per-edit line: what the WRITTEN FILE holds at that op's leaf (#683); contract in docs/architecture/write-path.md.</summary>
    static string EditLineValue(WritePatchBuilder.OpResult op, string? absentClause = null) =>
        // A sentence about what the write did, not a field reading — nothing to re-read.
        op.AfterIsNote && op.After is not null ? "  -> " + op.After
        // The REMEDY differs by lane and the reading does not, so the create render passes its own clause.
        : op.RecordAbsentFromFile ? "  -> DID NOT LAND — " + (absentClause ?? WriteSentences.RecordAbsentFromWrittenFile)
        : op.AfterOnDisk is { } disk
            ? "  -> " + disk + (op.SupersededInCall ? "  [the leaf as the file now holds it; a later op in this call wrote it too]" : "")
              // The file ANSWERED and nothing parsed the answer, so the value is printed only with that said (#529).
              + (op.AfterOnDiskBytes is { } n ? WriteSentences.OpaqueLeafCaveat(n) : "")
        : op.VerifyAttempted ? "  -> not-checked [the re-opened file did not answer for this op]"
        : "  -> not-checked [no file check ran for this op]";

    /// <summary>Where a per-op "what landed" clause came from when it is not the plain file answer; silence means the
    /// file was re-read for this op and agreed.</summary>
    static string LandedProvenance(WritePatchBuilder.OpResult op) =>
        op.SupersededInCall ? " [as applied — a later op in this call wrote the same field; the file shows that op's result]"
        : op.LandedOnDisk is not null ? ""
        // The same split the json render makes; the second arm is defensive, such ops being filtered out upstream.
        : op.VerifyAttempted ? " [as applied — the re-opened file did not answer for this op]"
        : " [as applied — this lane ran no file check]";

    /// <summary>Confirmation for housecarl_remove: what was dropped, the now-lean masters, and how many records remain
    /// (0 means inert), or on a refusal the named reason.</summary>
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
            // Not NewOrExtendedArtifact: a removal creates no artifact and grows no patch.
            sb.Append("mod folder: ").Append(modFolder).Append('\n');
        }
        // Budgeted, with the masters line and the closing guidance OUTSIDE the budget as the accounting still needed.
        int cap = WriteSentences.Cap(maxChars);
        for (int i = 0; i < o.Removed.Count; i++)
        {
            if (sb.Length >= cap)
            {
                // NOT "raise max_chars to see the rest": the records are gone, and the rows ARE the formids= passed.
                sb.Append("  ... [truncated: ").Append(i).Append(" of ").Append(o.Removed.Count)
                  .Append(" removed record(s) listed at max_chars=").Append(cap).Append("; ")
                  .Append(WriteSentences.RowsCutOperationIntact(false, "removed"))
                  .Append(" — ").Append(RemovedRowsRemedy).Append("]\n");
                break;
            }
            var r = o.Removed[i];
            sb.Append("  - ").Append(r.RecordType).Append(' ').Append(FormIdToken.Of(r.Target)).Append("  ")
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

    /// <summary>Confirmation for housecarl_forward: per record what was copied, the source it came FROM, the winner it
    /// out-ranks once enabled, and a NOTE where the copied version was already winning — never silently a no-op.</summary>
    internal static string RenderForward(WritePatchBuilder.ForwardOutcome o, int maxChars = 0)   // internal: a test asserts the would-be phrasing
    {
        if (o.NeedsAcknowledge) return o.Error! + Epoch(o);  // the in-place consent prompt is a required confirmation, not an error
        if (!o.Success) return "error: " + o.Error + Epoch(o);
        var file = Path.GetFileName(o.OutputPath);
        var modFolder = Path.GetFileName(Path.GetDirectoryName(o.OutputPath) ?? "");
        var sb = new StringBuilder();
        if (o.DryRun)
        {
            // The SAME dry-run sentences the apply lane renders: nothing was written first, then what would happen.
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
        // WHICH copy an off-order source read, stated once for the call, one source= serving it all.
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
        // Budgeted for the same reason as the created-records block, the json render truncating the identical array.
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
            sb.Append("  ").Append(f.RecordType).Append(' ').Append(FormIdToken.Of(f.Target)).Append("  ").Append(f.EditorId ?? "<no editorid>")
              .Append(o.DryRun ? "  — would be copied from " : "  — copied from ").Append(f.FromPlugin);
            // The sentence matches what the replace does, and states the count rather than implying it.
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
                // No active plugin defines this record, so no ranking is rendered against a winner that does not exist.
                sb.Append("  (no active plugin currently defines this record — nothing to out-rank; it takes effect once this patch is enabled)");
            else
                // Record precedence is the PLUGIN load order; where a new patch lands in it is the artifact line's job.
                sb.Append($"  (out-ranks the current winner {f.PriorWinner} once this patch is enabled and loaded after it)");
            sb.Append('\n');
        }
        if (o.ReadBack is { } rb) AppendFullReadback(sb, rb, maxChars, dryRun: o.DryRun, freshPatch: !o.Extended && !o.InPlace);
        if (o.Warning is { } warn) sb.Append("warning: ").Append(warn).Append('\n');
        if (o.Note is { } note) sb.Append("note: ").Append(note).Append('\n');
        sb.Append(o.DryRun
            ? WriteSentences.DryRunClose("every record resolved from its source", "forward")
            : o.InPlace
                ? InPlaceAgainHint("to forward more into this plugin", file)
                : $"to forward more into THIS patch (incl. from a different source plugin), pass into=\"{file}\".");
        sb.Append(Epoch(o));
        return sb.ToString();
    }

    /// <summary>Confirmation for housecarl_create_plugin: the empty plugin's path and mod folder, its ESL flag, master
    /// header, record count and size, the MO2 enable reminder and what the trigger does.</summary>
    static string RenderCreatePlugin(WritePatchBuilder.CreatePluginOutcome o)
    {
        if (!o.Success) return "error: " + o.Error;
        var file = Path.GetFileName(o.OutputPath);
        var modFolder = Path.GetFileName(Path.GetDirectoryName(o.OutputPath) ?? "");
        var sb = new StringBuilder();
        sb.Append("wrote ").Append(file).Append(o.Esl ? " (header-only, ESL-flagged; " : " (header-only; ")
          .Append(o.Bytes).Append(" bytes, ").Append(o.RecordCount).Append(o.RecordCount == 1 ? " record)\n" : " records)\n");
        sb.Append("mod folder: ").Append(modFolder).Append("  — enable it in MO2 to use it\n");
        sb.Append(WriteSentences.Masters(o.Masters));
        sb.Append("this is a trigger/placeholder plugin: it carries no records, so it changes nothing in game by itself — ")
          .Append("its only job is to make the basename '").Append(Path.GetFileNameWithoutExtension(file))
          .Append("' present in the load order (so a basename-bound SKSE config resolves, a FormID range is reserved, etc.).");
        return sb.ToString();
    }

    /// <summary>Confirmation for housecarl_compact_plugin: where it landed, the record accounting, masters, the
    /// external-referencer verdict, the pass coverage, the script reminder; NeedsAcknowledge returns verbatim.</summary>
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

        // Each external OVERRIDER is WARNED about by name; contract in docs/architecture/write-path.md.
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
        // The plugin NAME survives a compaction, so what moves is the object id, and only the ids this run moved.
        sb.Append(WriteSentences.CompactRuntimeConfigs);

        AppendFacegenCarry(sb, o.AssetRename, o.InPlace);
        // One asset build backs both passes, so the voice note names only a root the facegen note did not.
        AppendVoiceCarry(sb, o.VoiceRename, o.InPlace, o.AssetRename?.RootFailures);
        AppendSeqRegen(sb, o.SeqRegen, o.InPlace);

        if (o.Note is { } note) sb.Append("note: ").Append(note).Append('\n');
        sb.Append("reminder: FormIDs compiled into Papyrus (.pex hardcoded / GetFormFromFile) and any Mutagen-delta ")
          .Append("residual are NOT remappable — verify scripted records after compacting.");
        return sb.ToString();
    }

    // A plugin the external-reference pass could not read through — shared by compact and merge, one render home, each
    // plugin taking the sentence its own cause earns and naming the CHECK rather than the list above.
    static void AppendUnscannablePlugins(StringBuilder sb, IReadOnlyList<RemapEngine.UnscannablePlugin>? plugins)
    {
        if (plugins is not { Count: > 0 }) return;
        sb.Append("note: the external-reference pass could not fully read ").Append(plugins.Count)
          .Append(plugins.Count == 1 ? " plugin, so the external-referencer check does not cover it:\n"
                                     : " plugins, so the external-referencer check does not cover them:\n");
        foreach (var p in plugins.Take(25)) sb.Append("  ! ").Append(WriteSentences.UnscannablePlugin(p)).Append('\n');
        if (plugins.Count > 25) sb.Append("  ! … (+").Append(plugins.Count - 25).Append(" more)\n");
    }

    /// <summary>Where the merged plugin has to load, from the positions, masters and dependents the merge computed (#718).</summary>
    static void AppendPlacement(StringBuilder sb, WritePatchBuilder.MergeOutcome o)
    {
        if (o.Placement is not { } p) return;
        bool isRename = p.FirstPosition == p.LastPosition;
        sb.Append("placement: ");
        if (isRename)
            sb.Append("the donor sat at load-order position ").Append(p.LastPosition).Append(". ");
        else
            sb.Append("the donors sat at load-order positions ").Append(p.FirstPosition).Append('–').Append(p.LastPosition)
              .Append(" (").Append(p.FirstDonor).Append(" … ").Append(p.LastDonor).Append("). ");
        sb.Append("Load ").Append(o.OutputName);
        // A master that is not flagged ESM can sit after the last donor, and then no slot meets both halves.
        if (p.MasterAfterLastDonor)
        {
            sb.Append(" after its last master ").Append(p.LastMaster).Append(", which already sits at position ")
              .Append(p.LastMasterPosition).Append(", AFTER the last donor — so it cannot also sit where the donors did, ")
              .Append("and the donors' conflict outcomes will not all survive the move.\n");
            return;
        }
        if (p.LastMaster is not null)
            sb.Append(" after its last master ").Append(p.LastMaster).Append(" (position ").Append(p.LastMasterPosition).Append("), and");
        // AT that position, not "at or after": moving the output either way changes a winner.
        sb.Append(" at position ").Append(p.LastPosition).Append(", where the last donor sat")
          .Append(" — the merge resolved the donors' conflicts as they stood there, so an earlier slot lets content the ")
          .Append("donors used to beat win over the merge, and a later one puts the merge over plugins that used to beat ")
          .Append("the donors.\n");
        // What the position governs, and what it does not: the plugins named below are orphaned by the swap rather than
        // outranked by this position, and no roster of the position-sensitive records is printed.
        sb.Append("  what the position decides is the donors' OVERRIDES, kept at their masters' FormIDs: a plugin that ")
          .Append("overrides the same master records wins over the merge below it and loses above it. The donors' OWN ")
          .Append("records are renumbered into ").Append(o.OutputName).Append("'s FormID space, where nothing outside the ")
          .Append("merge shares an id with them")
          .Append(o.ExternalOverriders.Count > 0 || o.ExternalPlugins.Count > 0
              ? " — the plugins warned about below are orphaned by the swap, not outranked by this position.\n"
              : ".\n");
    }

    // FormID-keyed assets carried WITH the renumber — shared by compact and merge, reported rather than silent, and
    // inPlace is always false for merge, which has no in-place lane.
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
            sb.Append("  note: a BSA or a loose mod folder failed to read this scan, so a 'no facegen' result may be incomplete — verify NPC faces in-game.\n");
        AppendCarryRoots(sb, ar.RootFailures);
    }

    /// <param name="namedAlready">roots the facegen note above already named, so one list is not printed twice.</param>
    static void AppendVoiceCarry(StringBuilder sb, VoiceCarryOutcome? outcome, bool inPlace,
                                 IReadOnlyList<string>? namedAlready = null)
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
            sb.Append("  note: a BSA or a loose mod folder failed to read this scan, so a 'no voice' result may be incomplete — verify voiced lines in-game.\n");
        AppendCarryRoots(sb, namedAlready is { Count: > 0 } named
                             ? vr.RootFailures.Where(r => !named.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList()
                             : vr.RootFailures);
    }

    /// <summary>WHICH loose root the carry scan could not read, under the note that hedges on it — this render takes
    /// no max_chars, so it cuts at the same 25 the WARN lists above it do.</summary>
    static void AppendCarryRoots(StringBuilder sb, IReadOnlyList<string> roots)
    {
        foreach (var r in roots.Take(25)) sb.Append("  ").Append(BatchRender.RootFailureLead).Append(r).Append('\n');
        if (roots.Count > 25)
            sb.Append("  … (+").Append(roots.Count - 25).Append(" more loose root read failure(s))\n");
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

    /// <summary>Merge confirmation: the merged plugin's identity and the swap instruction, per-donor id accounting, the
    /// conflict resolutions, the WARN surfaces and their remedies, the asset carry, the saves and ESL pointers.</summary>
    internal static string RenderMerge(WritePatchBuilder.MergeOutcome o)
    {
        if (!o.Success) return "error: " + o.Error;
        var file = Path.GetFileName(o.OutputPath);
        var modFolder = Path.GetFileName(Path.GetDirectoryName(o.OutputPath) ?? "");
        var sb = new StringBuilder();
        // The operation's SHAPE is classified ONCE for every sentence that varies with it; one donor is the RENAME case.
        bool isRename = o.Donors.Count == 1;
        if (isRename)
        {
            // DERIVED from the accounting line below: a pure-override donor originates nothing to re-key.
            sb.Append("wrote ").Append(file).Append(" (new plugin; ").Append(o.Bytes).Append(" bytes) — a RENAME of ")
              .Append(o.Donors[0]).Append(": one donor, so there is nothing to combine — ");
            // Three arms, because each sentence may claim only the quantity it read, the empty donor included.
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
        sb.Append("mod folder: ").Append(modFolder).Append("  — review in xEdit, then enable it in MO2 (MO2 adds a newly activated plugin at the END of the load order).\n");
        AppendPlacement(sb, o);
        // The swap is PLUGIN-level, not mod-level; contract in docs/architecture/write-path.md.
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
                sb.Append("  ").Append(c.RecordType).Append(' ').Append(FormIdToken.Of(c.Key)).Append("  ").Append(c.WinnerDonor).Append(" won over ").Append(c.LoserDonor).Append('\n');
            if (o.Conflicts.Count > 25) sb.Append("  … (+").Append(o.Conflicts.Count - 25).Append(" more)\n");
        }

        // WARN loud and proceed: the donors stay active until the swap, so nothing is broken at write time. Compact
        // refuses on referencers instead, its renumber taking effect under the SAME plugin name.
        if (o.ExternalPlugins.Count > 0)
        {
            sb.Append("WARNING — ").Append(o.ExternalPlugins.Count).Append(" plugin(s) OUTSIDE the merge REFERENCE donor records. Their references break ")
              // Adding the patch to the donor set yields a COMBINED plugin, so a rename offers that second.
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
        // The third dependent kind: a plugin declaring a donor as a master while referencing none of its records, which
        // the game refuses to load once the donor is gone — a WARN with the same standing as the two above.
        if (o.MasterDeclarers is { Count: > 0 } declarers)
        {
            sb.Append("WARNING — ").Append(declarers.Count).Append(" plugin(s) OUTSIDE the merge DECLARE a donor as a MASTER ")
              .Append("without referencing any of its records. They are not in the lists above (nothing links to a donor ")
              .Append("record), but a plugin missing a master does not load at all, so each one breaks the moment you ")
              .Append(isRename ? "deactivate the donor plugin" : "deactivate the donor plugins")
              // The fix is the stale master reference, not a new one; combining is offered second, as above.
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
        // The coverage caveat belongs to the PASS: a "none" and a populated list are incomplete the same way, and
        // naming what is left out is what keeps the pass from claiming more than it measured.
        sb.Append("identify-pass scanned ").Append(o.PluginsScanned).Append(" plugin(s) — it reads record links, record ")
          .Append("identity and declared masters, NOT runtime config files (SPID, KID, SkyPatcher, Open Animation ")
          .Append("Replacer), so a plugin that only names a donor in such a file is not counted above.\n");
        // The caveat above says those files are not READ; this says what that costs.
        sb.Append(WriteSentences.MergeRuntimeConfigs);

        AppendFacegenCarry(sb, o.AssetRename, inPlace: false);
        // One asset build backs both passes, so the voice note names only a root the facegen note did not.
        AppendVoiceCarry(sb, o.VoiceRename, inPlace: false, namedAlready: o.AssetRename?.RootFailures);
        AppendSeqRegen(sb, o.SeqRegen, inPlace: false);

        // A donor's HEADER does not come along, and these notes are keyed on what the donors carried; contract in docs/architecture/write-path.md.
        bool lightNoteShown = o.LightCarried || o.LightDonors is { Count: > 0 };
        if (o.LightCarried)
        {
            // The one case where nothing is dropped still gets a line: whether the output costs a load-order slot.
            sb.Append("NOTE — every donor carried the LIGHT (ESL) status and every merged object id landed inside the ")
              .Append("light window (0x").Append(HousecarlCore.FormIdRange.EslWindowFloor.ToString("X3")).Append("–0x")
              .Append(HousecarlCore.FormIdRange.EslWindowCeiling.ToString("X3")).Append("), so ").Append(o.OutputName)
              .Append(" is written LIGHT too — it takes no full load-order slot.\n");
        }
        else if (o.LightDonors is { Count: > 0 } light)
        {
            sb.Append("NOTE — ").Append(string.Join(", ", light.Take(10)));
            if (light.Count > 10) sb.Append(" (+").Append(light.Count - 10).Append(" more)");
            // Why the flag was not carried, never a bare drop: the reason is written per failed condition and the
            // remedy per count, and the ids-fit condition fails two ways only the count tells apart.
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
            // No remedy, nothing on the surface flagging an existing plugin as a master; a bare statement, as below.
            sb.Append("NOTE — ").Append(string.Join(", ", masters.Take(10)));
            if (masters.Count > 10) sb.Append(" (+").Append(masters.Count - 10).Append(" more)");
            sb.Append(" carried MASTER status; ").Append(o.OutputName)
              .Append(" is NOT flagged as a master — it loads as a plain plugin, in the plugin block rather than the ")
              .Append("master block, so anything depending on that ordering will see it move.\n");
        }
        // The merged plugin is never flagged localized, so a localized donor's values are written INTO it.
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
            // No remedy is named because none exists on the surface: author=/description= belong to create_plugin.
            sb.Append("NOTE — the header Author/Description carried by ").Append(string.Join(", ", meta.Take(10)));
            if (meta.Count > 10) sb.Append(" (+").Append(meta.Count - 10).Append(" more)");
            sb.Append(" are not carried across; ").Append(o.OutputName).Append("'s are empty.\n");
        }

        if (o.Note is { } note) sb.Append("note: ").Append(note).Append('\n');
        sb.Append("reminders: existing SAVES that depend on the donors will not survive the swap (the records now live under a ")
          .Append("different plugin name, and any id that had to be renumbered moved with it) — best for a new game. ")
          .Append("FormIDs compiled into Papyrus (.pex hardcoded / ")
          .Append("GetFormFromFile) and any Mutagen-delta residual are NOT remappable — verify scripted records.");
        // ONE home for the compact recommendation per response: the light note already names the tool with its cost,
        // and with no light note there is no other pointer.
        if (!lightNoteShown)
            sb.Append(" Want it light? Run " + ToolNames.CompactPlugin + " on '").Append(o.OutputName).Append("' (the tools compose).");
        return sb.ToString();
    }

    /// <summary>Confirmation for housecarl_create: each new record's ALLOCATED FormID, editorid and type, the patch
    /// path and its derived masters, and the fields applied.</summary>
    internal static string RenderCreate(WritePatchBuilder.CreateOutcome o, int maxChars = 0, bool fullDump = false)   // internal: housecarl_create renders the same outcome
    {
        if (o.NeedsAcknowledge) return o.Error! + Epoch(o);  // the in-place consent prompt is a required confirmation, not an error
        if (!o.Success) return "error: " + o.Error + Epoch(o);
        var file = Path.GetFileName(o.OutputPath);
        var modFolder = Path.GetFileName(Path.GetDirectoryName(o.OutputPath) ?? "");
        var sb = new StringBuilder();
        if (o.InPlace)
            // "created into X", not "X rewritten": every sibling in-place headline is verb-then-file.
            sb.Append("created into ").Append(file).Append(" IN PLACE (").Append(o.Bytes)
              .Append(" bytes — ").Append(WriteSentences.InPlaceRewritten).Append(")\n")
              .Append(WriteSentences.InPlaceModFolder(modFolder));
        else
            sb.Append(WriteSentences.NewOrExtendedArtifact(o.Extended, file, o.Bytes, modFolder));
        sb.Append(WriteSentences.Masters(o.Masters));
        // Said ABOVE the created rows and outside their budget; contract in docs/architecture/write-path.md.
        var notLanded = o.Created.Where(c => c.AbsentFromFile || c.ParentAbsentFromFile).ToList();
        if (notLanded.Count > 0)
        {
            sb.Append("! ").Append(notLanded.Count)
              .Append(notLanded.Count == 1 ? " created record did NOT land: " : " created records did NOT land: ")
              .Append(WriteSentences.CreateRecordAbsentFromWrittenFile(ReadBackCall(o, file))).Append(". ");
            // TWO lists, never one; contract in docs/architecture/write-path.md.
            var absentCreated = notLanded.Where(c => c.AbsentFromFile).Select(c => FormIdToken.Of(c.FormKey))
                                         .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var absentParents = notLanded.Where(c => c.ParentAbsentFromFile).Select(c => FormIdToken.Of(c.ParentKey!.Value))
                                         .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (absentCreated.Count > 0) sb.Append(WriteSentences.AbsentRecordList(absentCreated)).Append(' ');
            if (absentParents.Count > 0)
                sb.Append("Their parent record(s), which the file does not hold either: ")
                  .Append(WriteSentences.AbsentRecordList(absentParents));
            sb.Append('\n');
        }
        var replacedCount = o.Created.Count(c => c.ReplacedExisting);
        sb.Append("created ").Append(o.Created.Count).Append(o.Created.Count == 1 ? " record" : " records");
        if (replacedCount > 0)
            sb.Append(" (").Append(replacedCount).Append(replacedCount == 1 ? " REPLACED an existing record" : " REPLACED existing records")
              .Append(" — same FormID kept, prior contents discarded)");
        sb.Append(":\n");
        // Budgeted: the render's LARGEST block, and the json render budgets the same array.
        int createCap = WriteSentences.Cap(maxChars);
        // Which version of a contested parent this artifact carries is hoisted out of the budget below, one line per
        // distinct host, selected on the FLAG, and bounded on Wire so the json render shares the bound.
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
                // The remedy points at a READ, never at re-issuing: a repeated CREATE allocates the records AGAIN.
                sb.Append("  ... [truncated: ").Append(ci).Append(" of ").Append(o.Created.Count)
                  .Append(" created record(s) listed at max_chars=").Append(createCap).Append("; ")
                  .Append(WriteSentences.CreateRowsCutRemedy(ReadBackCall(o, file), notLanded.Count > 0)).Append("]\n");
                break;
            }
            var c = o.Created[ci];
            listed++;
            sb.Append("  ").Append(c.RecordType).Append(' ').Append(FormIdToken.Of(c.FormKey)).Append("  ").Append(c.EditorId);
            // "this patch" belongs to the artifact lanes; in place the file is the caller's own plugin.
            if (c.ReplacedExisting) sb.Append("  [REPLACED: ").Append(o.InPlace ? file : "this patch")
                                      .Append(" already defined this editorid — re-created fresh at the same FormID; prior contents, including any " + ToolNames.Apply + " edits since, were discarded]");
            // The record's own verdict from the written file, beside its row, and a walk that never ran says so. The
            // PARENT arm is first, not an alternative: testing the child first would never name what went missing.
            if (c.ParentAbsentFromFile)
                sb.Append("  -> DID NOT LAND — its parent ").Append(FormIdToken.Of(c.ParentKey!.Value))
                  .Append(" is not in the written file, so this child is not in it either. ")
                  .Append(WriteSentences.CreateRecordAbsentFromWrittenFile(ReadBackCall(o, file)));
            else if (c.AbsentFromFile)
                sb.Append("  -> DID NOT LAND — ").Append(WriteSentences.CreateRecordAbsentFromWrittenFile(ReadBackCall(o, file)));
            else if (!c.VerifyAttempted)
                sb.Append("  -> not-checked [the re-opened file could not be walked]");
            sb.Append('\n');
            // WHOSE version of the parent a nested create copied in is not visible in the record afterwards.
            if (c.ParentHost is { } host) sb.Append("      parent: ").Append(host).Append('\n');
            // The SAME clause the edit lane's per-edit lines carry (#763), the READING only: the record row above
            // states the create lane's remedy in full.
            var opAbsent = WriteSentences.RecordAbsentReading
                         + ", so this field is not in it — the record line above says what to do";
            foreach (var op in c.Ops)
                sb.Append("      ").Append(op.Label).Append(EditLineValue(op, opAbsent)).Append(ApplyNote(op)).Append('\n');
        }
        // Both coverage checks read ONE asset build, so the folders it could not read are the RESPONSE's: named once
        // under the reports that hedge on them, out of room held back from both rather than spent after them.
        var roots = BatchRender.RootFailureLines(CreateRootFailures(o), WriteSentences.Cap(maxChars), indent: "  ");
        AppendVoiceReport(sb, o.Voice, maxChars, roots.Length);
        AppendScriptBindingReport(sb, o.ScriptBinding, maxChars, roots.Length);
        sb.Append(roots);
        AppendCellShellReport(sb, o.CellShell, maxChars);
        // The same compact-by-default verify as the edit lane; the created records' set fields are listed above.
        if (o.ReadBack is { } rb)
        {
            if (fullDump) AppendFullReadback(sb, rb, maxChars, freshPatch: !o.Extended && !o.InPlace);
            else AppendCompactReadback(sb, Array.Empty<WritePatchBuilder.OpResult>(), rb, maxChars);
        }
        if (o.Warning is { } warn) sb.Append("warning: ").Append(warn).Append('\n');
        if (o.Note is { } note) sb.Append("note: ").Append(note).Append('\n');
        // Gated on rows having actually rendered, a small max_chars being able to drop every row, and on the SAME
        // count as the hoist above, so no arm contradicts it or points at a FormID this render never printed.
        sb.Append(listed > 0 && notLanded.Count == 0
            ? "the new FormID above is how you reference this record (SkyPatcher/SPID, or a follow-up edit). "
            : listed > 0
            ? $"{notLanded.Count} of the record(s) above did NOT land, so their FormIDs are NOT in the written file — do not reference them. Read the artifact back with {ReadBackCall(o, file)} to see which FormIDs exist. "
            : notLanded.Count > 0
            ? $"no records are listed above — the char budget cut the whole list. All {o.Created.Count} were attempted, and the {notLanded.Count} named above did NOT land. Read them back with {ReadBackCall(o, file)} to see which FormIDs exist. "
            : $"no records are listed above — the char budget cut the whole list, though all {o.Created.Count} WERE created. Read them back with {ReadBackCall(o, file)} to get their FormIDs. ");
        sb.Append(o.InPlace
            ? InPlaceAgainHint("To create more records in this plugin", file)
            : $"To add more to THIS patch, pass into=\"{file}\".");
        sb.Append(Epoch(o));
        return sb.ToString();
    }

    /// <summary>What a truncated REMOVE render tells the caller; contract in docs/architecture/write-path.md.</summary>
    internal const string RemovedRowsRemedy =                       // internal: the json render says the same thing
        "removal is all-or-nothing, so these rows are exactly the formids= you passed — nothing here is unrecoverable. "
      + "Do NOT re-issue to widen this: the records are gone, so a repeat is refused as 'not carried by' the file";

    /// <summary>What a truncated FORWARD render tells the caller to do, which depends on the LANE.</summary>
    internal static string ForwardAgainRemedy(WritePatchBuilder.ForwardOutcome o, string file)   // internal: the json render says the same thing
        => WriteAgainRemedy(o.DryRun, o.InPlace, o.Extended, file, "patch mod carrying the same overrides");

    /// <summary>The lane rule generalized; contract in docs/architecture/write-path.md.</summary>
    static string WriteAgainRemedy(bool dryRun, bool inPlace, bool extended, string file, string duplicateNoun)
        => dryRun || extended
            ? "raise max_chars to see the rest"
            : inPlace
                ? $"to see the rest, read the rows back with {ToolNames.Records} source=\"{file}\" formids=[the ids you passed] — re-issuing would re-serialize your ORIGINAL file a second time just to widen this render"
                : $"to see the rest, raise max_chars AND pass into=\"{file}\" — a bare re-issue on the default patch= lane writes a SECOND {duplicateNoun}";

    /// <summary>The <c>apply</c> lane's wording of <see cref="WriteAgainRemedy"/>.</summary>
    internal static string ApplyAgainRemedy(WritePatchBuilder.PatchOutcome o, string file)
        => WriteAgainRemedy(o.DryRun, o.InPlace, o.Extended, file, "patch mod carrying the same edits");

    /// <summary>The read-back call a truncated create render points the caller at — one that actually RESOLVES, which
    /// means <c>types=</c>, the select term that carries over a patch MO2 has not enabled.</summary>
    internal static string ReadBackCall(WritePatchBuilder.CreateOutcome o, string file)   // internal: the json render points at the SAME call
    {
        var types = o.Created.Select(c => c.RecordType).Where(t => !string.IsNullOrWhiteSpace(t))
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        // Every distinct type, never a sampled head; the empty fallback is unreachable but a bare source= is refused.
        return types.Count > 0
            ? $"{ToolNames.Records} source=\"{file}\" types=[{string.Join(", ", types.Select(t => $"\"{t}\""))}]"
            : $"{ToolNames.Records} source=\"{file}\" types=[<the record types you created>]";
    }

    /// <summary>Render the voice-coverage report for a dialogue-line create, so a byte-valid line is never silently a
    /// silent one; it reports on-disk state, naming where audio goes, and never generates any.</summary>
    /// <param name="reserve">room this render leaves for what its caller writes under it — the named roots.</param>
    static void AppendVoiceReport(StringBuilder sb, VoiceReport? report, int maxChars, int reserve = 0)
    {
        if (report is null || report.IsEmpty) return;
        // Budget-bounded like the full read-back, stopping with an explicit notice rather than a silent cut. The tail
        // is composed BEFORE the rows and its room held back, and the loops BREAK rather than return, so a report the
        // cap cut still carries its hedge and the roots under it — the cut case is the one that needs them.
        int ceiling = WriteSentences.Cap(maxChars);
        bool anyReadIncomplete = report.Lines.Any(l => l.ReadIncomplete);
        var tail = (anyReadIncomplete ? WriteSentences.ScanIncomplete("an \"absent\"") : "")
                 + (report.CheckError is null
                        ? ""
                        : WriteSentences.CheckCouldNotRun("voice", report.CheckError, "the records",
                                                          "verify voice files manually."));
        int cap = Math.Max(1, ceiling - reserve - tail.Length);
        int total = report.Lines.Count + report.Undetermined.Count, rendered = 0;
        bool cut = false;
        sb.Append("voice coverage — created dialogue lines (").Append(WriteSentences.Twins.VoiceStake)
          .Append("; the audio is yours to provide):\n");

        foreach (var l in report.Lines)
        {
            if (sb.Length >= cap) { cut = true; break; }
            var who = string.IsNullOrEmpty(l.TopicEditorId) ? FormIdToken.Of(l.Info) : $"{l.TopicEditorId} ({FormIdToken.Of(l.Info)})";
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
            rendered++;
        }
        foreach (var u in report.Undetermined)
        {
            if (cut || sb.Length >= cap) { cut = true; break; }
            var who = string.IsNullOrEmpty(u.TopicEditorId) ? FormIdToken.Of(u.Info) : $"{u.TopicEditorId} ({FormIdToken.Of(u.Info)})";
            sb.Append("  [?] ").Append(who).Append("  — ").Append(u.Reason).Append('\n');
            rendered++;
        }
        if (cut) AppendVoiceTrunc(sb, rendered, total, ceiling);
        sb.Append(tail);
    }

    /// <summary>The explicit voice-coverage truncation notice, closing on the
    /// <see cref="WriteSentences.Twins.ReportBlockCut"/> the result-script notice and the json render share.</summary>
    static void AppendVoiceTrunc(StringBuilder sb, int rendered, int total, int cap)
        => sb.Append("  ... [voice coverage truncated: rendered ").Append(rendered).Append(" of ").Append(total)
             .Append(" line(s) at max_chars=").Append(cap).Append("; ").Append(WriteSentences.Twins.ReportBlockCut).Append("]\n");

    /// <summary>Render the structural-shell report for a cell create: houseCARL authors no world content, so this lists
    /// per cell what the author must still provide in the Creation Kit.</summary>
    static void AppendCellShellReport(StringBuilder sb, CellShellReport? report, int maxChars)
    {
        if (report is null || report.IsEmpty) return;
        // Budgeted like its two siblings, the inner MustProvide loop included, one cell's work list being able to blow
        // the budget by itself.
        int cap = WriteSentences.Cap(maxChars);
        int total = report.Cells.Count, rendered = 0;
        bool cut = false;
        sb.Append("cell shell — ").Append(WriteSentences.Twins.CellStake).Append(" (provide these in the Creation Kit):\n");
        foreach (var c in report.Cells)
        {
            if (sb.Length >= cap) { cut = true; break; }
            sb.Append("  ").Append(c.Interior ? "INTERIOR " : "EXTERIOR ").Append(c.EditorId).Append(" (").Append(FormIdToken.Of(c.Cell)).Append("):\n");
            foreach (var m in c.MustProvide)
            {
                if (sb.Length >= cap) { cut = true; break; }
                sb.Append("      - ").Append(m).Append('\n');
            }
            if (cut) break;
            rendered++;
        }
        // The notice, then the two notes, which stay OUTSIDE the budget as the removal render's accounting does.
        if (cut)
            sb.Append("  ... [cell shell truncated: rendered ").Append(rendered).Append(" of ").Append(total)
              .Append(" cell(s) at max_chars=").Append(cap).Append("; ").Append(WriteSentences.Twins.ReportBlockCut).Append("]\n");
        // The un-checked grid-occupancy seam, declared; only an EXTERIOR cell collides on a grid.
        if (report.Cells.Any(c => !c.Interior))
            sb.Append("  note: ").Append(WriteSentences.Twins.GridOccupancy).Append('\n');
        if (report.CheckError is not null)
            sb.Append(WriteSentences.CheckCouldNotRun("cell-shell", report.CheckError, "the cell(s)", "review world content manually."));
    }

    /// <summary>Render the result-script coverage report for a dialogue-line create, so a byte-valid script is never
    /// silently an inert one: "WILL NOT FIRE" with the missing path, "OK", or a NAMED reason.</summary>
    /// <param name="reserve">room this render leaves for what its caller writes under it — the named roots.</param>
    static void AppendScriptBindingReport(StringBuilder sb, ScriptBindingReport? report, int maxChars, int reserve = 0)
    {
        if (report is null || report.IsEmpty) return;
        // Its sibling's rule, for its sibling's reason: the tail is charged before the rows and the loop breaks, so a
        // cut report still says a folder went unread and which.
        int ceiling = WriteSentences.Cap(maxChars);
        bool anyReadIncomplete = report.Findings.Any(f => f.ReadIncomplete);
        var tail = (anyReadIncomplete ? WriteSentences.ScanIncomplete("a \"missing .pex\"") : "")
                 + (report.CheckError is null
                        ? ""
                        : WriteSentences.CheckCouldNotRun("result-script", report.CheckError, "the records",
                                                          "verify the script binding manually."));
        int cap = Math.Max(1, ceiling - reserve - tail.Length);
        int total = report.Findings.Count, rendered = 0;
        bool cut = false;
        sb.Append("result-script coverage — created dialogue lines (").Append(WriteSentences.Twins.ScriptStake).Append("):\n");
        foreach (var f in report.Findings)
        {
            if (sb.Length >= cap) { cut = true; break; }
            var who = string.IsNullOrEmpty(f.TopicEditorId) ? FormIdToken.Of(f.Info) : $"{f.TopicEditorId} ({FormIdToken.Of(f.Info)})";
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
            rendered++;
        }
        if (cut)
            sb.Append("  ... [result-script coverage truncated: rendered ").Append(rendered).Append(" of ").Append(total)
              .Append(" line(s) at max_chars=").Append(ceiling).Append("; ").Append(WriteSentences.Twins.ReportBlockCut).Append("]\n");
        sb.Append(tail);
    }

    /// <summary>The loose roots a create's coverage checks could not read, UNIONED: they scan different subtrees
    /// (<c>Sound\Voice</c> against <c>Scripts</c>) and each materialises the list at its own return off a dictionary
    /// that fills lazily, so the one that ran first carries the shorter one. Taking either alone would name the folder
    /// that hid a voice file and not the one that hid the .pex, which is the defect this work removes.</summary>
    internal static IReadOnlyList<string> CreateRootFailures(WritePatchBuilder.CreateOutcome o)
        => (o.Voice is { IsEmpty: false } v ? v.RootFailures : Array.Empty<string>())
           .Concat(o.ScriptBinding is { IsEmpty: false } s ? s.RootFailures : Array.Empty<string>())
           .Distinct(StringComparer.OrdinalIgnoreCase)
           .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
           .ToList();
}

// ---- the retired 1.x wire DTOs: parked in WireNamesProbe.NonInputWireTypes, reachable from no tool's input schema ----

/// <summary>One edit operation off the wire, mirroring <see cref="WritePatchBuilder.PatchEdit"/>; RecordType is derived
/// from the resolved winner's runtime type rather than supplied.</summary>
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

/// <summary>One brand-new record to create off the wire — the retired 1.x batch element: the declared record_type, its
/// editorid, optional field operations, and the optional nested parent and collection.</summary>
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

/// <summary>A modeled struct built from parts (wire shape of <see cref="StructSpec"/>): the concrete type, flat
/// coercible sub-fields, positional ctor args, and nested edits applied to the built struct.</summary>
public sealed record StructInput
{
    [SchemaRequired, JsonPropertyName("type"), Description("The concrete catalog type to build (arm type for a polymorphic Set; the collection's element type for an Add, e.g. 'LeveledItemEntry'; or a polymorphic element's concrete ARM, e.g. 'ScriptObjectProperty' into VMAD Properties).")]
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
    [SchemaRequired, JsonPropertyName("path"), Description("Dotted path within the struct, e.g. 'Data.Level'.")]
    public string? Path { get; init; }

    [SchemaValues(SchemaVocabulary.ComposeVerbs), JsonPropertyName("verb"), Description(WriteVerbs.InComposeRecital + ". The nested write runs through the same verb engine an op does, so the verb is chosen by the nested target's own cardinality. The three verbs the op surface has and this one does not each read an input slot a nested set has no member for — ReplaceAll's values=, Merge's entries=, CopyFrom's source record — and each is refused here by name rather than consuming nothing: set a collection's elements or a dict's entries one at a time, and copy a field from another record with " + ToolNames.Apply + "'s CopyFrom op.")]
    // Nullable so the generator types it ["string","null"]; the gate reads an absent or null verb as Set.
    public string? Verb { get; init; } = "Set";

    [JsonPropertyName("value"), Description("The value (coerced).")]
    public string? Value { get; init; }

    [JsonPropertyName("key"), Description("Dict key or list index, if the nested target is a collection.")]
    public string? Key { get; init; }

    [JsonPropertyName("compose"), Description("Build a modeled sub-struct for THIS nested target (recursive): the concrete ARM of a polymorphic sub-field (e.g. a Condition's Data → 'GetActorValueConditionData'), or the element for a struct-element Add / InsertAtIndex / SetAtIndex nested inside the struct. Omit for a coercible scalar (use value=).")]
    public StructInput? Compose { get; init; }
}

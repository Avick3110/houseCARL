namespace HousecarlMcp;

/// <summary>The load-bearing phrases a shared sentence MUST still contain, declared beside the sentence so they move
/// with it. A phrase is the CLAIM, not the wording — pick the span that disappears when the sentence stops being
/// true, since a bare topic word is satisfied by a sentence saying the opposite. The checker does a plain substring
/// test and nothing else: it is a backstop against a sentence being emptied, never a judgement of its quality.</summary>
[AttributeUsage(AttributeTargets.Field)]
internal sealed class MustStateAttribute : Attribute
{
    internal string[] Phrases { get; }
    internal MustStateAttribute(params string[] phrases) => Phrases = phrases;
}

/// <summary>The declared way to say a shared sentence carries NO claim worth pinning — a label, a separator, a
/// fragment whose meaning lives entirely in the sentences that compose it. It exists so the walk can demand a
/// decision: an undecorated const FAILS by name rather than going quietly unchecked. The reason is prose for the
/// next author, never parsed.</summary>
[AttributeUsage(AttributeTargets.Field)]
internal sealed class NoClaimsAttribute : Attribute
{
    internal string Reason { get; }
    internal NoClaimsAttribute(string reason) => Reason = reason;
}

/// <summary>ONE SOURCE PER SENTENCE for the write surface's user-facing prose: every outcome renders twice —
/// <see cref="WriteTools"/> for text, <see cref="JsonWire"/> for json — so no render-side literal may duplicate
/// another render's meaning. <see cref="Twins"/> holds what BOTH transports state; a sentence that is prose on one
/// transport by design, like the in-place hazard, lives directly on this class instead.</summary>
internal static class WriteSentences
{
    // ---- budgets -------------------------------------------------------------------------------------
    /// <summary>The char budget a WRITE render works to: the caller's <c>max_chars</c>, or the server default (0
    /// means default). Write renders only, both transports — the READ surface keeps its own <c>JsonWire.Cap</c> /
    /// <c>Wire.Cap</c>, deliberately not wired through here so the two budgets can diverge.</summary>
    internal static int Cap(int maxChars) => maxChars > 0 ? maxChars : Wire.DefaultMaxChars;

    /// <summary>The same for any READ-BACK dump, which is bounded well below <see cref="Cap"/> so the truncation
    /// note itself reaches the caller rather than being cut by the host (see <see cref="Wire.ReadbackMaxChars"/>).</summary>
    internal static int ReadbackCap(int maxChars) => maxChars > 0 ? maxChars : Wire.ReadbackMaxChars;

    // ---- the epoch stamp -----------------------------------------------------------------------------
    /// <summary>The index build a write resolved winners against, appended to EVERY write render — success,
    /// refusal, dry run, consent prompt — so a caller can tell whether the winner it edited is the winner a read
    /// reported a moment earlier. The read-back proves what landed in the FILE, never what wins in the ORDER.
    /// Empty when the outcome consulted no build.</summary>
    internal static string Epoch(string? epoch) => epoch is null ? "" : $"\nepoch={epoch}";

    // ---- artifact headers (text lane; json states these as typed fields) -----------------------------
    /// <summary>The IN-PLACE hazard clause — the one sentence telling a caller their own file was rewritten with
    /// nothing kept back. Every text write render (apply / create / remove / forward / compact) states it.</summary>
    [MustState("your ORIGINAL file was rewritten", "no houseCARL backup or undo")]
    internal const string InPlaceRewritten = "your ORIGINAL file was rewritten; " + NoBackupOrUndo;

    /// <summary>The same hazard in the tense a DRY RUN needs — it has not happened yet. Two spellings because the
    /// tense differs, but one source for the clause that carries the weight.</summary>
    [MustState("your ORIGINAL file rewritten", "no houseCARL backup or undo")]
    internal const string InPlaceWouldRewrite = "your ORIGINAL file rewritten; " + NoBackupOrUndo;

    /// <summary>What makes the in-place lane the lane it is: houseCARL keeps nothing to undo it with. Every
    /// sentence that mentions the rewrite is built on this rather than repeating it.</summary>
    [MustState("no houseCARL backup or undo")]
    internal const string NoBackupOrUndo = "no houseCARL backup or undo";

    // ---- the runtime-config reminder (merge + compact) -----------------------------------------------
    //
    // THE CLAIM RULE for these two sentences: state only what THIS operation did, or a break it entails scoped to the
    // addressed line's own content, and never another system's addressing grammar — the four differ, and all of them
    // also take EditorIDs, which merge and compact preserve, so those lines do not break.

    /// <summary>Merge's reminder. The middle clause says the records MOVED and stops there, direction-neutral: for a
    /// line naming the donor as an EXCLUSION the scope WIDENS rather than going dead, and which symptom a caller gets
    /// is the other system's business, not ours. The third clause is SkyPatcher-only because only its skill states the
    /// filename gate verbatim — a <c>Plugin.esp.ini</c> loads only while that plugin is active — and it earns its place
    /// as the one break the rest cannot reach: every line in that file goes dark, donor-naming or not.</summary>
    [MustState("Nothing here rewrites those files", "no longer describes those records", "stops loading at the swap")]
    internal const string MergeRuntimeConfigs =
        "Nothing here rewrites those files. After the swap a donor's records live under the merged plugin's name, so a " +
        "line that names a donor — whether to address a record in it or to filter on it — no longer describes those " +
        "records. Separately, a SkyPatcher config file whose NAME is a donor's filename (Plugin.esp.ini) is read only " +
        "while that plugin is active, so it stops loading at the swap: every line in it, including the lines that never " +
        "name a donor.\n";

    /// <summary>Compact's. The plugin name survives a compaction, so only the object id moves — and only the ids this
    /// run actually moved. "once the compacted plugin is the one loading" rather than a flat present tense, because in
    /// the new-file lane the original is still active until the swap and nothing has broken yet.</summary>
    [MustState("does not read", "nothing here rewrites them", "no longer reaches that record")]
    internal const string CompactRuntimeConfigs =
        "That pass reads plugins; it does not read runtime config files (SPID, KID, SkyPatcher, Open Animation " +
        "Replacer), and nothing here rewrites them. The plugin name is unchanged, but a line in one of them that " +
        "addresses a record by an object id this compaction moved no longer reaches that record once the compacted " +
        "plugin is the one loading.\n";

    /// <summary>The mod-folder line for an IN-PLACE write: the target is a mod the user already runs, so there is
    /// nothing to enable — only a re-sort, and only if the edit moved a winner.</summary>
    internal static string InPlaceModFolder(string modFolder) =>
        $"mod folder: {modFolder}  — already active in your load order; re-sort only if a winner changed\n";

    /// <summary>One plugin the external-referencer pass could not read through, in the words its own cause earns.
    /// A file that would not OPEN is almost always held by another program and closing it fixes the run; a file
    /// that opened and threw part way through is held by nothing, and repeating the close-your-programs advice
    /// there sends the modder round a loop. One home so the report, the prompt and the refusal cannot drift.</summary>
    internal static string UnscannablePlugin(RemapEngine.UnscannablePlugin p) => p.Cause switch
    {
        RemapEngine.UnscannableCause.Unopenable =>
            $"{p.Plugin} — could not be OPENED, probably held open by another program; close xEdit, MO2 or Skyrim and run this again ({p.Reason})",
        _ => $"could not fully read {p.Plugin}: {p.Reason}",
    };

    /// <summary>The header + mod-folder pair for a write to a NEW patch or an EXTENDED one — the default lane's
    /// "here is the artifact and what to do with it", rendered identically by apply / create / forward.</summary>
    internal static string NewOrExtendedArtifact(bool extended, string file, long bytes, string modFolder) =>
        (extended ? $"extended {file} (existing patch grown; {bytes} bytes)\n"
                  : $"wrote {file} (new patch; {bytes} bytes)\n")
      + (extended ? $"mod folder: {modFolder}\n"
                  : $"mod folder: {modFolder}  — enable + sort it in MO2 to use the patch\n");

    /// <summary>The masters line. The empty-set spelling is the part that carries meaning: a patch with no masters is
    /// a standalone, not a broken header.</summary>
    internal static string Masters(IReadOnlyList<string> masters) =>
        $"masters: {(masters.Count == 0 ? "(none)" : string.Join(", ", masters))}\n";

    // ---- closure copy --------------------------------------------------------------------------------
    /// <summary>The standalone claim: the plugin(s) the copy was taken away from are NOT masters of the artifact.
    /// The whole point of the operation, so it is stated rather than implied by their absence from the list.</summary>
    [MustState("is NOT a master")]
    internal const string CopyStandalone = "standalone: the source is NOT a master of this patch.";

    /// <summary>The opposite, and an alarm rather than a note: a copy that masters its own source has failed at the
    /// one thing it exists to do, however cleanly it wrote.</summary>
    [MustState("IS among the masters", "NOT standalone")]
    internal const string CopySourceMastered =
        "!! the source IS among the masters — this copy is NOT standalone. Nothing was silently fixed; inspect the patch before relying on it.";

    /// <summary>The third arm: the source is defined in a base-game master. Those are never bound — copying a
    /// vanilla-defined record must not internalize vanilla — so the standalone claim above would be computed over a
    /// deliberately empty set and would deny mastering a plugin the list above names. An always-loaded master is not
    /// being removed from anything, so this says what the copy IS instead.</summary>
    [MustState("base-game master", "appearance transplant", "not a standalone-ization")]
    internal const string CopySourceBaseGame =
        "note: the source is defined in a base-game master (always loaded) — nothing is being \"removed\", so links " +
        "to it are kept and mastered normally; this copy is an appearance transplant, not a standalone-ization.";

    /// <summary>Read-back failed: the patch is on disk, so the masters/standalone facts are UNKNOWN rather than
    /// false. Asserting them from default-empty values would report a source-mastered patch as standalone on
    /// exactly the path where verification broke.</summary>
    [MustState("NOT VERIFIED", "do NOT re-run")]
    internal const string CopyReadBackUnverified =
        "masters: <NOT VERIFIED — the post-write read-back failed>\n" +
        "the patch WAS written, so do NOT re-run blindly (that mints a duplicate); read its records back with " + ToolNames.Records +
        " source=\"<the patch>.esp\" types=[\"NPC_\"]. The MASTERS line above stays unverified either way — no houseCARL tool " +
        "lists a plugin's masters; check them in xEdit or the CK.";

    /// <summary>What a strip actually costs the caller. The clone keeps the look and loses the source's own
    /// factions/outfits/packages — said plainly, because "standalone" must never quietly mean "different".</summary>
    [MustState("re-author")]
    internal const string CopyStripConsequence =
        "  the clone keeps what was copied and NOT the source's own references above — re-author those against your own or vanilla records as needed.";

    /// <summary>A cycle in the copied graph. Recorded rather than silently deduped, because a record that reaches
    /// itself is a fact about the data the caller may need to act on.</summary>
    [NoClaims("a labelled list header; the claim is in the per-cycle lines it introduces")]
    internal const string CopyCyclesHeader = "cycles found while walking (recorded, not an error):";

    /// <summary>The per-record provenance header. This is where the ordered source universe becomes visible: each
    /// copied record names WHICH source produced it, which is the readback half of first-hit-wins.</summary>
    [NoClaims("a list header; every claim it introduces is per-record and rendered beside the record")]
    internal const string CopyInternalizedHeader = "internalized under new FormIDs (EditorIDs preserved):";

    // ---- the ordered source universe: parameterised sentences, split so the checks can reach them ----------
    // [MustState] is AttributeTargets.Field, so text living only inside a method is unchecked. Each sentence here
    // is an invariant CONST the render emits verbatim, with the caller's data interpolated around it.

    /// <summary>Single-source label. The claim is the source name it introduces, not the word.</summary>
    [NoClaims("a label; the claim is the source name it introduces")]
    internal const string CopySourceSingleLabel = "source: ";

    /// <summary>Multi-source header. "First hit wins" is the ordering contract in the one place a caller reads it,
    /// so it is pinned here rather than phrased freshly per render.</summary>
    [MustState("in order", "first hit wins")]
    internal const string CopySourceListLabel = "sources (in order, first hit wins): ";

    /// <summary>The miss refusal's opening claim.</summary>
    [MustState("no source produced")]
    internal const string CopySourceMissLead = "no source produced ";

    /// <summary>…and the part that makes it actionable: EVERY source consulted, not just the last one tried.
    /// Naming only the last reads as though one source was checked and sends the caller to fix the wrong file.</summary>
    [MustState("Consulted, in order:")]
    internal const string CopySourceMissConsulted = ". Consulted, in order: ";

    /// <summary>The miss remedy. Names, not paths: a path invites a paste-back into the next call, and a source is
    /// named by plugin. Both causes get their own remedy, because for a DANGLING link — the typical cause — no plugin
    /// exists to name and "add a source" sends the caller after a file that was never there.</summary>
    [MustState("from_source=", "'winner'", "dangling link", "'Type:refuse'")]
    internal const string CopySourceMissRemedy =
        ". Name the plugin that defines it in from_source=, or 'winner' for the active load order's winning version. " +
        "If the record exists NOWHERE — a dangling link, which is the typical cause — no source will produce it: " +
        "exclude its record type instead, with 'Type:refuse' to stop the copy at it, or 'Type:stop' to prune the " +
        "link and keep it (which needs its plugin in your active load order).";

    /// <summary>The fault refusal's claim — a source HAS the record and could not read it.</summary>
    [MustState("could not be read")]
    internal const string CopySourceFaultLead = " but it could not be read — ";

    /// <summary>…and why its remedy differs from a miss's. Adding another source cannot help, so saying so is the
    /// whole reason a fault is a separate sentence rather than a miss with a different cause.</summary>
    [MustState("not a missing record", "from_source=")]
    internal const string CopySourceFaultRemedy =
        ". This is not a missing record: adding another source will not help. Repair or replace that plugin, or " +
        "name a different one in from_source=.";

    /// <summary>One line per source consulted, in order. Rendered on success AND on a miss — a caller cannot judge
    /// "not found" without knowing where it was looked for.</summary>
    internal static string CopySourcesConsulted(IReadOnlyList<string> sources) =>
        sources.Count == 1
            ? $"{CopySourceSingleLabel}{sources[0]}\n"
            : $"{CopySourceListLabel}{string.Join(" -> ", sources)}\n";

    /// <summary>The miss refusal, composed from the three consts above.</summary>
    internal static string CopySourceMiss(string what, IReadOnlyList<string> sources) =>
        CopySourceMissLead + what + CopySourceMissConsulted + string.Join(", ", sources) + CopySourceMissRemedy;

    /// <summary>The fault refusal, composed from its two.</summary>
    internal static string CopySourceFault(string what, string source, string cause) =>
        $"'{source}' carries {what}" + CopySourceFaultLead + cause + CopySourceFaultRemedy;

    // ---- the seed-shape boundary ---------------------------------------------------------------------
    /// <summary>What <c>seed_paths</c> supports, and the ROUTE for what it does not. A walk seeds from record
    /// LINKS; a field whose entries are link-bearing structures is a field-bundle copy, which <c>housecarl_apply</c>
    /// already does — and there the caller picks replace-vs-merge with the grammar that lane already has, rather
    /// than this one inventing a second answer.</summary>
    [MustState("seed_paths takes a record link or a list of record links", ToolNames.Apply)]
    internal const string CopySeedShapeRoute =
        " — seed_paths takes a record link or a list of record links, and nothing else. Copying a field whose " +
        "entries carry links INSIDE them is a field-bundle copy: use " + ToolNames.Apply + "'s bundle=/assignments= zip, " +
        "where op=Merge and op=ReplaceAll are your choice between merging into the target's entries and replacing " +
        "them. Nothing was written.";

    /// <summary>An off-order link on a record that was ALREADY in the patch. The serialization failure is real and
    /// this call did not cause it, so the remedy is about the patch and the mod — never about `exclude_types`,
    /// which the caller may not even have passed.</summary>
    [MustState("was already in this patch", "this call did not create it", "enable the mod", "a NEW patch")]
    internal const string CopyPatchOffOrderRoute =
        " is not in your active load order, so this patch cannot be written at all until that link resolves. The " +
        "reference was already in this patch before this call — this call did not create it, and no exclude_types " +
        "setting affects it. Either enable the mod that provides that plugin, or write to a NEW patch instead of " +
        "extending this one with into=. Nothing was written.";

    /// <summary>An off-order link on a record THIS call copied, sitting on a field the seed set never named — so
    /// the walk never reached it to internalize it, and the whole-record duplicate carried it across. The remedy is
    /// the seed set, which is why this cannot share either sentence above.</summary>
    [MustState("this copy carried it across", "seed_paths=", "enable the mod")]
    internal const string CopyCopiedOffOrderRoute =
        " is not in your active load order, so this patch cannot be written at all. The link sits on a field " +
        "seed_paths= never named, so the walk never reached that record to copy it and this copy carried it across " +
        "as a link instead. Either name that field in seed_paths= so the record is internalized too, or enable the " +
        "mod that provides the plugin so the link can be mastered normally. Nothing was written.";

    /// <summary>The seed's shape is SUPPORTED and the TARGET cannot take it. Separate from the shape route above,
    /// which would send the caller to fix a legal seed path instead of the target's own property.</summary>
    [MustState("the TARGET's", "not the seed path", "target=")]
    internal const string CopyUnwritableTargetRoute =
        " — the seed path is fine; it is the TARGET's property that cannot be written, not the seed path. Copy onto " +
        "a record of the same type that carries its own, or mint a clone with new_editorid= instead of target=. " +
        "Nothing was written.";

    /// <summary>The walk found no links at all under the seed paths. The usual cause is named, because "check
    /// seed_paths" sends the caller to re-read a correct field list: a TEMPLATED record's appearance fields are empty
    /// BY DESIGN, and the fix is to copy the template.</summary>
    [MustState("carry no record links", "TEMPLATED", "the template it points at")]
    internal const string CopyNoSeeds =
        "the seed fields carry no record links, so this copy would produce nothing. The usual cause is a TEMPLATED " +
        "record — one whose template flags hand these fields to another record, leaving its own empty by design; " +
        "copy the template it points at instead. Otherwise check seed_paths= against the record.";

    /// <summary>Appended to a strip line that nulled a WHOLE property. The count alone names the link that forced the
    /// removal, not its cost: nulling VirtualMachineAdapter to clear one bound property drops every script.</summary>
    [MustState("the ENTIRE property was cleared", "not only the link")]
    internal const string CopyStripWholeProperty =
        "   <- the ENTIRE property was cleared to remove this, not only the link(s) named — everything else it carried is gone too.";

    /// <summary>A seed the source does not carry. The target's copy is ASSIGNED FROM the source anyway — cleared —
    /// because a copy that leaves the target's own value in place produces a face assembled from two records, which
    /// is the desync the operation exists to prevent. Said out loud, because a silent clear and a silent skip look
    /// identical afterwards.</summary>
    [MustState("CLEARED", "not a mixture")]
    internal const string CopySeedClearedNote =
        "  (the source carries none, so the target's was CLEARED — the result is the source's, not a mixture)";

    // ---- kept links, told apart ----------------------------------------------------------------------
    /// <summary>Links kept because they resolve OUTSIDE the source universe. These genuinely master normally.</summary>
    [MustState("outside the source", "mastered normally")]
    internal const string CopyKeptOutside = "link(s) resolve outside the source — mastered normally.";

    /// <summary>Links kept because an exclusion PRUNED them. These are inside the source universe and still point at
    /// it — the opposite of the sentence above, so the two must never be collapsed into one.</summary>
    [MustState("still point INTO the source", "not standalone")]
    internal const string CopyKeptExcluded =
        "link(s) were pruned by exclude_types and still point INTO the source — this artifact is not standalone " +
        "for them, and masters the plugin they live in.";

    /// <summary>The attach lane's leak refusal when the leaked key is one an exclusion pruned. The generic leak
    /// sentence blames the target for a reference THIS call wrote a moment earlier, and sends the caller to edit a
    /// record that never had it.</summary>
    [MustState("exclude_types", "this call wrote it")]
    internal const string CopyLeakFromExclusion =
        " was pruned by exclude_types and then attached to the target unmapped — this call wrote it, so the target " +
        "is not where to look. Either drop that exclusion so the record is internalized, or copy a field set that " +
        "does not reach it.";

    /// <summary>`Type:stop` against a record that lives OFF the active load order. The remedy half is both routes,
    /// because which one is right is the caller's call: `refuse` if they want the copy to stop at that record, or
    /// enabling the mod if they want the link kept and mastered.</summary>
    [MustState("master a plugin the game does not load", "'Type:refuse'", "enable the mod")]
    internal const string CopyStopOffOrderRoute =
        " is not in your active load order, and 'Type:stop' KEEPS the link — so this patch would have to master a " +
        "plugin the game does not load, which cannot be written at all. Either use 'Type:refuse' for that type, so " +
        "the copy stops there and tells you, or enable the mod so the link can be mastered normally. Nothing was written.";

    /// <summary>A target in a nested group. Named as caller input rather than surfaced as an engine fault.</summary>
    [MustState("NESTED group", "target=")]
    internal const string CopyTargetShapeRoute =
        " lives in a NESTED group (a placed reference, a cell, a dialog response), which target= cannot override. " +
        "Name a top-level record — for an NPC's appearance that is the NPC_ itself, not a reference placed in a cell.";

    // ---- the from record's own provenance ------------------------------------------------------------
    /// <summary>Which source produced the record the caller ASKED for — the one body an ordered source list exists to
    /// disambiguate, and the only one not covered by the internalized rows.</summary>
    [MustState("read from")]
    internal const string CopyFromArmLead = "the source record was read from ";

    // ---- the copied records' asset paths -------------------------------------------------------------
    /// <summary>The asset paths the copied records reference, and the route to acting on them: copy enumerates, and
    /// never fetches, places or judges. The absent-reads clause is load-bearing — <c>from_source=</c> can read a
    /// record out of a mod MO2 does not load, and <c>asset_status</c> answers only for the mods it does, so a path
    /// only that mod provides reads back absent and "absent" would otherwise be taken for "nothing to place".</summary>
    [MustState("does NOT place them", ToolNames.BulkPlaceAsset)]
    internal const string CopyAssetPathsHeader =
        "asset paths the copied records reference (this call does NOT place them — check each with " +
        ToolNames.AssetStatus + ", then place what you keep with " + ToolNames.BulkPlaceAsset + "; a path only the mod you " +
        "read FROM provides reads as absent in asset_status if MO2 does not load that mod, and is still placed by " +
        "naming it in source_provider=):";

    // ---- dry run -------------------------------------------------------------------------------------
    /// <summary>The first line of any dry run: it says NOTHING happened before it says what would, because a dry run
    /// that reads like a write is a silently wrong answer. The pinned phrases are the two claims, not the "DRY RUN"
    /// label — the label survives a rewrite saying the opposite, and they do not.</summary>
    [MustState("NOTHING was written", "originals untouched")]
    internal const string DryRunHeader =
        "DRY RUN — validated only; NOTHING was written (no patch file, no mod folder, originals untouched).\n";

    /// <summary>What the real call WOULD write, by lane. <paramref name="verb"/> is the tool's own word for what it
    /// does to the destination ("edit"/"forward into"), so the sentence names the caller's action rather than a
    /// generic one. The NEW-patch arm carries the name-preview caveat: the real write re-picks a free stem, so the
    /// auto-suffix can shift if another patch lands first — a promise the dry run must not make.</summary>
    internal static string DryRunWouldWrite(bool inPlace, bool extended, string file, string inPlaceVerb) =>
        inPlace  ? $"the real call would {inPlaceVerb} {file} IN PLACE — {InPlaceWouldRewrite}.\n"
      : extended ? $"the real call would EXTEND the existing patch {file}.\n"
                 : $"the real call would write a NEW patch {file} (name preview — the real write re-picks a free name, "
                 +  "so the auto-suffix can shift if another patch lands first).\n";

    /// <summary>The dry run's masters line, labelled as the PREVIEW it is — derived from the would-be content, not
    /// from the lean header the real write derives for itself.</summary>
    internal static string DryRunMasters(IReadOnlyList<string> masters) =>
        $"expected masters: {(masters.Count == 0 ? "(none)" : string.Join(", ", masters))}"
      + "  [derived from the would-be content; the real write derives its own lean header]\n";

    /// <summary>The dry run's closing line: what the pipeline proved, how to commit, and the limit on that proof.
    /// <paramref name="proved"/> is the per-verb clause. The parenthetical is shared because both verbs commit
    /// through the same serializer, so a body Mutagen declines to write surfaces at commit on either.</summary>
    internal static string DryRunClose(string proved, string realVerb) =>
        $"{proved}; to {realVerb} for real, repeat the call without dry_run. "
      + "(A real write can still fail at serialize/commit — disk faults and data Mutagen refuses to serialize surface only there.)";

    // ---- row-truncation notes ------------------------------------------------------------------------
    /// <summary>What a cut row list says about the OPERATION rather than the render: the rows shown were cut, the
    /// work was not. Dry-run aware on purpose — a truncated dry run must not assert a write, which is the confusion
    /// <see cref="DryRunHeader"/> exists to prevent.</summary>
    internal static string RowsCutOperationIntact(bool dryRun, string pastParticiple) =>
        dryRun ? "the dry run covered every one" : $"every one WAS {pastParticiple}";

    /// <summary>The opening of a json <c>truncated_note</c>: which ceiling was hit and what it dropped. json-only —
    /// the text renders state the same two facts inside their own truncation bracket, where the counts sit.</summary>
    internal static string JsonRowsCut(int cap) =>
        $"the render hit max_chars={cap} and dropped trailing rows";

    /// <summary>What a truncated CREATE row list tells the caller, on both transports. The re-issue trap is the
    /// load-bearing half and is a <see cref="Twins"/> member; the read-back CALL is built per outcome by
    /// <see cref="WriteTools.ReadBackCall"/>.</summary>
    internal static string CreateRowsCutRemedy(string readBackCall) =>
        $"{RowsCutOperationIntact(false, "created")}. Read them back with {readBackCall} — {Twins.CreateReissueTrap}";

    // ---- post-write report blocks (create) -----------------------------------------------------------
    /// <summary>The "check could not run" line the three post-write reports share: the check failed, the records were
    /// still CREATED, and here is what to verify by hand — a check that did not run must never read like one that
    /// passed. <paramref name="createdNoun"/> stays per-block, since that half is the block's own subject.</summary>
    internal static string CheckCouldNotRun(string checkName, string error, string createdNoun, string verifyManually) =>
        $"  {checkName} check could not run: {error} — {createdNoun} WERE created; {verifyManually}\n";

    /// <summary>The scan-incompleteness note: a BSA that failed to read makes an "absent" mean "unscanned", which
    /// is a different claim from "looked for and not found". <paramref name="absentThing"/> is what the block's own
    /// rows call the missing item.</summary>
    internal static string ScanIncomplete(string absentThing) =>
        $"  note: a BSA failed to read this scan, so {absentThing} above may merely be unscanned — verify in MO2.\n";

    /// <summary>What a CUT cell-shell block costs beyond the generic "rows were dropped": each cell row carries its
    /// own <c>must_provide</c> work list, so the dropped rows are exactly the Creation-Kit work this response existed
    /// to name. json-only — the text block's cut notice has no equivalent.</summary>
    [MustState("Creation-Kit work this response was supposed to name")]
    internal const string CellRowsCutLoss =
        "each dropped row is Creation-Kit work this response was supposed to name";

    // ---- place_asset: naming which copy to read ------------------------------------------------------
    /// <summary>The refusal's load-bearing half: houseCARL picked NOTHING. Which copy of a contended file is right is
    /// a judgement about the modlist and the tool never makes it, so the caller must read "choose", not "retry".</summary>
    [MustState("will not guess which copy is correct")]
    internal const string PlaceSourceWillNotGuess = "houseCARL will not guess which copy is correct";

    /// <summary>Contended source, no pole named. Lists the providers by NAME, each QUOTED — the quoted half is
    /// literally what <c>source_provider=</c> takes back, so the refusal hands the caller its own next call.
    /// Deliberately NOT the on-disk paths: a path round-tripped through the caller can go stale between the resolve
    /// and the read, and the whole point of naming a provider is that it cannot. The remedy is unconditional — the
    /// pole is sigiled, so no listed name can collide with it and this sentence needs no load-order state.</summary>
    internal static string PlaceSourceAmbiguous(string rel, IReadOnlyList<string> providerNames) =>
        $"{providerNames.Count} providers supply '{rel}' — {PlaceSourceWillNotGuess}. "
      + $"Pass source_provider= one of these names, quotes excluded: {string.Join("; ", providerNames)}"
      + $", or source_provider={AssetSourceChoice.WinnerToken} for whichever copy currently wins the VFS.";

    /// <summary>Why a named-provider miss is a refusal and not a fallback. The hazard is silent substitution: a
    /// mistyped mod name that quietly read some OTHER mod's copy would place bytes the caller never chose, and the
    /// file would look placed. Naming a provider means that provider or nothing.</summary>
    [MustState("nothing was substituted", "still supply it")]
    internal const string PlaceSourceNoSubstitute =
        "nothing was substituted for it — the providers below still supply it, and one of them is what you meant if the name is a typo";

    /// <summary>The naming correction on every named-provider miss: a caller who typed the pole as a bare word is
    /// told its spelling. STATIC, never conditional on what the load order contains — a sentence that knows which
    /// mods are installed is a sentence with a state to get wrong.</summary>
    [MustState("winner pole is spelled")]
    internal const string PlaceSourcePoleSpelling =
        "(the winner pole is spelled " + AssetSourceChoice.WinnerToken + " — a bare name always means a provider of that name)";

    /// <summary>What a provider NAME reaches, in ONE place for every surface that describes it — both tools'
    /// parameter descriptions and the auto-resolve refusal's remedy. It must state BOTH halves: naming is what
    /// reaches an unticked mod, and an omitted provider still sees only ticked ones, so the first half alone would
    /// leave a caller expecting auto-resolve to find a disabled mod's copy.</summary>
    [MustState("NOT currently loading", "Naming it is what reaches it")]
    internal const string PlaceSourceNameReachesUnticked =
        "Naming a mod folder MO2 is NOT currently loading reads that mod's own copy off disk — the loose file, then "
      + "that folder's own archives — and the result says so. Naming it is what reaches it: with source_provider= "
      + "omitted, only the mods MO2 loads are considered. For a mod MO2 IS loading, nothing changes: its loose files "
      + "are reached by the mod's name and a file inside its archive by that archive's filename, as before.";

    /// <summary>What was searched when the off-order lane DID run: the name was not one the active order already
    /// provides files under, so a mod folder of that name was looked for. NAMES only, never the folder's on-disk path.
    /// Rendered only when true — the universe-first gate answers before any disk look for every name the active order
    /// knows, and this must not claim a search that did not run.</summary>
    [MustState("MO2 mod folder of that name")]
    internal const string PlaceSourceDiskFolderSearched =
        "houseCARL also looked in the MO2 mod folder of that name, and read neither a loose copy there nor one inside "
      + "that folder's own archives";

    /// <summary>The scan behind the "nothing supplies it" half was INCOMPLETE — an archive failed to read this build,
    /// so an absence may be an unscanned copy. This is the ACTIVE universe's caveat, separate from the off-order
    /// lane's own unreadable outcome: both can be true at once, so they stay different clauses.</summary>
    [MustState("may merely be unscanned")]
    internal const string PlaceSourceScanIncomplete =
        "NOTE: a BSA failed to read this build, so it may merely be unscanned (see the warnings).";

    /// <summary>The folder was looked for and is not there. Distinct from finding it empty of this path, because the
    /// caller's next move differs: a name with no folder is a name to check, a folder without the file is a file to
    /// find elsewhere.</summary>
    [MustState("no MO2 mod folder of that name")]
    internal const string PlaceSourceNoSuchFolder =
        "houseCARL also looked under MO2's mods folder, and there is no MO2 mod folder of that name";

    /// <summary>The name cannot BE a folder name, so no disk look was possible. Its own outcome: collapsed into the
    /// universe-first arm, a drive-rooted path is refused as a name the load order already provides files under.</summary>
    [MustState("named, never pathed")]
    internal const string PlaceSourceNotAFolderName =
        "a provider is named, never pathed — this carries a separator, a drive, a '..' or a trailing dot or space, so "
      + "it is not a name houseCARL looks for on disk at all";

    /// <summary>The folder was searched and something in it would not READ, so an absent answer here is an unknown
    /// rather than an answer. Pairs with the NOTE clause, which names what failed.</summary>
    [MustState("could not be read")]
    internal const string PlaceSourceFolderUnreadable =
        "houseCARL also looked in the MO2 mod folder of that name, and something there could not be read";

    /// <summary>The other arm: the name means a LAYER, not a mod folder — MO2's overwrite, the game's Data folder,
    /// or an active archive's filename — so the folder scan never ran and no claim about a mod folder may be made.
    /// A mod folder name is NOT this arm any more (#388): it reaches the scan, and one of the outcomes above says
    /// what the scan found. It states only what is true — the name resolved, the path did not — and leaves the
    /// remedy to the provider list, which names the real candidates.</summary>
    [MustState("active load order already provides")]
    internal const string PlaceSourceReservedName =
        "that name is one the active load order already provides files under, and it names a layer rather than a mod folder";

    /// <summary>The named provider supplies this path in NEITHER place searched. ONE sentence for both misses: what
    /// differs is only whether anyone ELSE supplies the path, which decides whether there is a name to suggest, never
    /// whether the refusal names what the caller passed. The suggestion clause is SKIPPED on an empty list rather
    /// than printed with nothing after it, and nothing weaker is invented to fill the gap.</summary>
    internal static string PlaceSourceNamedAbsent(string provider, string rel, IReadOnlyList<string> providerNames,
                                                  OffOrderReason reason, string? unreadableName = null,
                                                  string? unreadableCause = null, string? pathHint = null,
                                                  bool scanIncomplete = false) =>
        $"'{provider}' does not supply '{rel}'"
        // ONE sentence per reason, and a switch expression with NO default arm: CS8509 then makes a new outcome a
        // build diagnostic rather than a false sentence in front of a caller.
      + reason switch
        {
            OffOrderReason.NotConsulted     => ". ",          // nothing on disk was looked at; claim nothing about it
            OffOrderReason.Found            => ". ",          // unreachable from a refusal, and silent if it ever is
            OffOrderReason.ReservedName     => $" — {PlaceSourceReservedName}. ",
            OffOrderReason.NotAFolderName   => $" — {PlaceSourceNotAFolderName}. ",
            OffOrderReason.NoSuchFolder     => $" — {PlaceSourceNoSuchFolder}. ",
            OffOrderReason.NoCopyInFolder   => $" — {PlaceSourceDiskFolderSearched}. ",
            OffOrderReason.FolderUnreadable => $" — {PlaceSourceFolderUnreadable}. ",
        }
        // An unreadable folder or archive makes this an UNKNOWN, not a miss. Rendered before the remedy, because it
        // changes what the remedy is worth. The name and cause come typed off the lookup; the sentence is built here.
      + (unreadableName is null ? "" : $"NOTE: '{unreadableName}' could not be read ({unreadableCause}), so this may be unscanned rather than absent. ")
        // The ACTIVE scan's own incompleteness: the Named verdict does not fall through to the sibling refusals that
        // render it, so it is keyed in here like every other fact this sentence takes.
      + (scanIncomplete ? PlaceSourceScanIncomplete + " " : "")
      + (pathHint is null ? "" : pathHint + " ")
      + (providerNames.Count > 0
            ? $"{PlaceSourceNoSubstitute} — pass one of these names instead, quotes excluded: "
            + $"{string.Join("; ", providerNames)}. "
            : "")
      + PlaceSourcePoleSpelling;

    /// <summary>The provenance line for bytes read out of a mod the active profile does NOT include. About the SOURCE
    /// and nothing else: the placed copy's own "does not win until you enable + sort" is the render's separate,
    /// unconditional line, and neither fact may be stated twice.</summary>
    // TODO(#388): place_asset's render still calls this without the flag, so an enabled mod's unloaded archive gets
    // the "NOT enabled in MO2" arm there. The fix is one argument at PlaceAssetTools.cs's call site, which the place
    // rewrite owns this wave. The NIF surface passes it.
    internal static string PlaceSourceOffOrder(string provider, bool ownerEnabled = false) => ownerEnabled
        // The folder is ticked, so the mod is not the reason: the built universe already answers for an enabled
        // mod's loose tree and every archive the engine loads, which leaves a root archive no active plugin binds.
        ? $"read from '{provider}', out of a root archive the engine does NOT load (no active plugin binds it) — you "
        + "named the mod, so houseCARL looked inside its own archives; the bytes are that mod's, and nothing about "
        + "that archive has to change for the copy just placed"
        : $"read from '{provider}', a mod folder that is NOT enabled in MO2 — you named it, so houseCARL read it off "
        + "disk; the bytes are that mod's, and enabling it is not required for the copy just placed";

    /// <summary>The both-slots expansion's own constraint on the pole. A FormID with no kind derives TWO destination
    /// paths, so an explicit source= (one file) cannot serve them — but the pole can, because it names whose copy
    /// rather than which file. The qualifier is required: with a source= passed, the pole is not fine here.</summary>
    [MustState("only when source= is omitted")]
    internal const string PlaceBothSlotsPoleConstraint =
        "source_provider= works here only when source= is omitted — it names WHOSE copy, not which file, and two slots are two files";

    /// <summary>A pole named against a source that is already one exact file. An input that cannot apply is said,
    /// never dropped: ignoring it silently would let a caller believe a provider was honoured.</summary>
    [MustState("only applies to a Data-relative source", "already names one exact copy")]
    internal const string PlaceSourceProviderNeedsRelPath =
        "source_provider= only applies to a Data-relative source resolved through the VFS. The source you passed is an "
      + "on-disk path, which already names one exact copy — drop source_provider=, or pass source= the Data-relative path.";

    // ---- the into=-extend not-found refusal ----------------------------------------------------------
    /// <summary>The whole remedy an operation that has stated nothing about creating a patch may offer: check what you
    /// typed. The DEFAULT tail of the shared not-found refusal, deliberately the weakest thing true for every caller —
    /// a stronger claim is one an operation makes for itself, never one the resolver assumes on its behalf.</summary>
    [MustState("Check the name")]
    internal const string ExtendCheckTheName = "Check the name.";

    /// <summary>Removal's own half of that refusal: why no create remedy is offered here. It states the RULE and
    /// stops rather than predicting what a patch created next would contain — an apply into that patch first would
    /// make a later removal succeed, so any such prediction is falsifiable in two calls.</summary>
    [MustState("will not create a patch here", "only drops a record the patch ITSELF already carries")]
    internal const string RemoveNoFreshPatch =
        "houseCARL will not create a patch here: a removal only drops a record the patch ITSELF already carries.";

    /// <summary>The other lane a removal has, in the spelling <c>housecarl_remove</c> declares. It does not spell out
    /// the first-touch confirmation and must not be read as promising one: that consent is persisted, survives the
    /// session, and is shared across the edit / create / remove in-place lanes, so a caller who acknowledged this
    /// plugin in any lane gets no prompt. The sentence lives on the TOOL because the service cannot tell which
    /// caller's parameter spelling to name.</summary>
    [MustState("pass in_place=\"<plugin filename>\"")]
    internal const string RemoveInPlaceLane =
        "To remove from an existing plugin IN PLACE instead, pass in_place=\"<plugin filename>\".";

    /// <summary>Sentences the SAME outcome must carry on BOTH transports. Members are whole invariant strings on
    /// purpose: a sentence interpolating a cap or a filename cannot be compared verbatim across lanes, so
    /// parameterised twins stay on the outer class and are checked for budget/count parity instead.</summary>
    internal static class Twins
    {
        // ---- create's post-write hazard reports ------------------------------------------------------
        /// <summary>Voice coverage — the stake, in the words both lanes use. A created voiced response with no
        /// .fuz on disk is byte-valid and SILENT in game.</summary>
        [MustState("plays SILENT in game")]
        internal const string VoiceStake = "a created voiced response with NO .fuz plays SILENT in game";

        /// <summary>Result-script coverage — the stake. A bound script that is unwired or uncompiled is byte-valid
        /// and does nothing.</summary>
        [MustState("runs NOTHING in game")]
        internal const string ScriptStake = "a bound script that is unwired or uncompiled runs NOTHING in game";

        /// <summary>Cell shell — the stake. houseCARL creates a valid, correctly-placed CELL record and does not
        /// author world content, so "created" must never read as "looks right in game".</summary>
        [MustState("houseCARL does not author world content")]
        internal const string CellStake =
            "a created cell is a valid, correctly-placed record but EMPTY — houseCARL does not author world content";

        /// <summary>The grid-occupancy seam, declared rather than silently unchecked. "(engine behavior undefined)"
        /// is the clause that tells a caller this is not a houseCARL limit they can retry past.</summary>
        [MustState("does NOT check grid-occupancy", "engine behavior undefined", "OVERRIDE it instead of creating a new one")]
        internal const string GridOccupancy =
            "houseCARL does NOT check grid-occupancy — a NEW exterior cell at a grid your load order already fills "
          + "collides (engine behavior undefined). To change an existing cell, OVERRIDE it instead of creating a new one.";

        /// <summary>Why a truncated CREATE must not be re-issued to widen its own render. The sibling verbs' rows are
        /// safe to re-ask for — a repeated remove is refused, a repeated forward re-copies identical bodies — but a
        /// repeated create ALLOCATES AGAIN, on either transport.</summary>
        [MustState("do NOT re-issue this call", "second full patch", "prior contents discarded")]
        internal const string CreateReissueTrap =
            "do NOT re-issue this call to see the rest: a repeated create allocates the records AGAIN (on the "
          + "default lane patch= auto-suffixes into a second full patch; under into= each record is re-created at "
          + "its old FormID with its prior contents discarded)";

        /// <summary>What a CUT post-write report block means, and the one action a caller must not take to widen it.
        /// These blocks ride the CREATE render only, so it names the specific consequence: re-issuing allocates the
        /// records again.</summary>
        [MustState("Do NOT re-issue the create", "compare rendered vs total")]
        internal const string ReportBlockCut =
            "the records WERE created and this block is only a render of them — compare rendered vs total rather than "
          + "reading the list as the whole answer. Do NOT re-issue the create to widen it: that allocates the records again";

        // ---- write_seq -------------------------------------------------------------------------------
        /// <summary>The destination already held exactly these bytes. A skipped write and a done one must not look
        /// alike, so this is its own statement rather than a caveat under a "wrote".</summary>
        [MustState("NOTHING was written")]
        internal const string SeqUnchanged =
            "the destination already held EXACTLY these bytes, so NOTHING was written";

        /// <summary>The byte-identical replace: the file was rewritten only because its timestamp could not be
        /// refreshed in place, so nothing was lost. Scoped deliberately — dressing this as a loss would train the
        /// reader to ignore the word REPLACED when it does mean one.</summary>
        [MustState("nothing was lost")]
        internal const string SeqReplacedSameBytes =
            "the file already at that path held EXACTLY these bytes; it was rewritten only because its timestamp "
          + "could not be refreshed in place, so nothing was lost.";

        /// <summary>The replace that CAN have cost something: on a folder the caller named, the .seq that was there
        /// may have been the mod's own, and houseCARL keeps no backup.</summary>
        [MustState("keeps no backup")]
        internal const string SeqReplacedUserFolder =
            "a .seq was already at that path and has been OVERWRITTEN; houseCARL keeps no backup, and in a folder "
          + "you named that file may have been the mod's own shipped .seq.";

        /// <summary>The ordinary regenerate: houseCARL's own earlier output in its own folder, overwritten.</summary>
        [MustState("was overwritten", "houseCARL's own earlier output")]
        internal const string SeqReplacedOwnFolder =
            "the previous .seq in that houseCARL folder was overwritten (houseCARL's own earlier output — the "
          + "ordinary regenerate case).";

        /// <summary>The timestamp stamp-forward, with the claim kept to what it establishes: THIS FILE is now newer
        /// than the plugin. The dialogue sweep lints the .seq the VFS serves, which is this one only if this folder
        /// wins the SEQ\ conflict, so no verdict from that tool may be promised here.</summary>
        [MustState("has been stamped forward", "contents untouched", ToolNames.Check + " findings=[\"dialogue\"] with the quest in seeds=", "no longer reads")]
        internal const string SeqTimestampRefreshed =
            "its mtime was older than the plugin and has been stamped forward (contents untouched); "
          + ToolNames.Check + " findings=[\"dialogue\"] with the quest in seeds= compares those two mtimes in its SEQ staleness check, so this file "
          + "no longer reads as stale — for the copy the load order actually serves, which is this one only if this "
          + "folder wins the SEQ\\ conflict.";

        /// <summary>No start-game-enabled quests: a .seq lists only SGE quests, so none is needed and nothing was
        /// written. Never a silent empty file, never a misleading "done".</summary>
        [MustState("NOTHING was written", "lists only quests with the Start Game Enabled flag")]
        internal const string SeqNoQuests =
            "a .seq lists only quests with the Start Game Enabled flag, so none is needed and NOTHING was written";

        /// <summary>The absent epoch stamp, stated as a fact with its reason. A .seq is derived from the plugin FILE
        /// alone, so nothing here consulted a load-order build — an absent stamp and a dropped one are otherwise the
        /// same observable.</summary>
        [MustState("consulted no load-order build", "not a dropped field")]
        internal const string SeqNoEpoch =
            "a .seq is derived from the plugin FILE alone (its FormID encoding is load-order-independent), so this "
          + "call consulted no load-order build — the absent stamp is a fact, not a dropped field.";

        /// <summary>write_seq's standing limit: the quests will START, which is not a claim that the quest or its
        /// dialogue is otherwise well-formed. The pointer at the tool that does check that is the half telling the
        /// caller what to do next, so it is pinned as the whole ACTION rather than the tool name alone.</summary>
        [MustState("does not verify", "use " + ToolNames.Check + " findings=[\"dialogue\"] with the quest in seeds=")]
        internal const string SeqStandingLimit =
            "this makes the quest(s) START at game start; it does not verify the quest or its dialogue is otherwise "
          + "well-formed (use " + ToolNames.Check + " findings=[\"dialogue\"] with the quest in seeds= for the dialogue "
          + "graph — that family validates the topics and quests you name and will not sweep the whole order).";

        /// <summary>What a truncated QUEST list means and what re-running costs. The .seq FILE carries every quest —
        /// only the LIST was cut — so the remedy prices the re-run rather than prescribing it (the wrong-remedy
        /// class: "raise max_chars and re-issue" would mean writing the .seq again, into a second fresh mod folder
        /// on the lane a caller reaches by naming none). Lane-neutral wording, because a shared sentence cannot
        /// point at a line only the text render prints.</summary>
        [MustState("nothing is missing from the FILE", "writes the .seq again")]
        internal const string SeqListCutRemedy =
            "the .seq itself carries ALL of them — nothing is missing from the FILE. Re-run only if you need this LIST "
          + "widened: for a plugin OUTSIDE a houseCARL folder with no lane named, that writes the .seq again into "
          + "ANOTHER fresh mod folder (name into=/output_dir=, or let a plugin in its own houseCARL folder default "
          + "there — at any named destination a byte-identical .seq is left untouched)";
    }
}

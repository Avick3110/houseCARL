namespace HousecarlMcp;

/// <summary>
/// ONE SOURCE PER SENTENCE for the write surface's user-facing prose — the response layer built by construction
/// rather than hand-wired twice (tool-surface-2.0, <c>RESPONSE_LAYER_BY_CONSTRUCTION_2026-08-11.md</c>).
///
/// <para><b>Why this exists.</b> Every write outcome is rendered twice — <see cref="WriteTools"/> for text,
/// <see cref="JsonWire"/> for json (decision D2: one write path, two renders) — and several renders repeat the
/// same sentence per verb on top of that. Each duplicate is an independent copy of one meaning, and the 2.0
/// review wave's single most numerous finding class was those copies drifting: a rule, budget, cap or wording
/// landing on one lane and not the other. Every time a shared construction landed, its finding class died and
/// stayed dead (<c>ForwardAgainRemedy</c>, <c>ReadBackCall</c>, <c>Wire.ContestedHostsShown</c>, and PR #311's
/// <c>ReportBlockCutClause</c>, whose sentence this class now holds). This class generalizes that: a sentence lives here once and both transports
/// read it, so a change reaches both by construction and cannot reach one.</para>
///
/// <para><b>The rule.</b> No render-side literal may duplicate another render's meaning. A sentence needed in two
/// places moves here in the SAME commit that deletes its copies — there is no "old path kept around" phase.</para>
///
/// <para><b><see cref="Twins"/> is the enforced half.</b> Members of that nested class are sentences BOTH transports
/// state. The write-surface guard's twin arm reflects over it and asserts each member is observed coming out of the
/// text renders at least once and out of the json renders at least once — so enrolling a new twin is adding a
/// member, and a lane that quietly re-inlines its own copy loses the constant and fails the arm by name. The check
/// is per-LANE coverage rather than per-OUTCOME co-occurrence on purpose: a few of these land in different places
/// on the two lanes (the text report blocks state their stake in the block header, the json blocks in the
/// truncation census), so demanding co-occurrence would fail honest renders. That is still the check the old
/// per-site <c>Contains("&lt;fragment&gt;")</c> arms could not provide: they pinned a string, not the fact that one
/// string serves both lanes.</para>
///
/// <para>Sentences that are prose on ONE transport by design — the in-place hazard, which json states as
/// <c>lane:"in_place"</c> plus typed flags — live directly on this class rather than in <see cref="Twins"/>.
/// They are still single-sourced (the text renders repeat them per verb), they are just not parity-checkable.</para>
/// </summary>
internal static class WriteSentences
{
    // ---- budgets -------------------------------------------------------------------------------------
    /// <summary>The char budget a WRITE render works to: the caller's <c>max_chars</c>, or the server default.
    /// One helper because "budget divergence" is one of the three drift classes this module retires — the ternary
    /// was written out at eight sites, and a site that picked the wrong default (or forgot the 0-means-default
    /// contract) diverged silently from its twin.
    /// <para>Write renders only, both transports. The READ surface keeps its own <c>JsonWire.Cap</c> / <c>Wire.Cap</c>
    /// — same formula today, and deliberately not wired through here: it is a separate surface on a separate
    /// migration, and a shared helper would silently move its budget the day the write default diverges.</para></summary>
    internal static int Cap(int maxChars) => maxChars > 0 ? maxChars : Wire.DefaultMaxChars;

    /// <summary>The same for any READ-BACK dump, which is bounded well below <see cref="Cap"/> so the truncation
    /// note itself reaches the caller rather than being cut by the host (see <see cref="Wire.ReadbackMaxChars"/>).</summary>
    internal static int ReadbackCap(int maxChars) => maxChars > 0 ? maxChars : Wire.ReadbackMaxChars;

    // ---- the §2.1.1 epoch stamp ----------------------------------------------------------------------
    /// <summary>SPEC §2.1.1 — the index build a write resolved winners against, appended to EVERY write render
    /// (success, refusal, dry run, consent prompt) exactly as the read surfaces stamp theirs. It is what lets a
    /// caller tell whether the winner it edited is the winner a read reported a moment earlier: the read-back
    /// proves what landed in the FILE, never what wins in the ORDER. Empty when the outcome consulted no build.
    /// <para>One method over a <c>string?</c> rather than one overload per outcome record: the four outcomes are
    /// deliberately independent shapes, but the STAMP is one sentence and four copies of it is four places for a
    /// format to drift.</para></summary>
    internal static string Epoch(string? epoch) => epoch is null ? "" : $"\nepoch={epoch}";

    // ---- artifact headers (text lane; json states these as typed fields) -----------------------------
    /// <summary>The IN-PLACE hazard clause — the one sentence that tells a caller their own file was rewritten with
    /// nothing kept back. Repeated by every text write render (apply / create / remove / forward / compact), which
    /// is exactly why it is here: the create render's copy had already lost "was rewritten", so the loudest
    /// response on the surface said something weaker than its four siblings for no reason anyone chose.</summary>
    internal const string InPlaceRewritten = "your ORIGINAL file was rewritten; " + NoBackupOrUndo;

    /// <summary>The same hazard in the tense a DRY RUN needs — it has not happened yet. Two spellings because the
    /// tense really does differ; ONE source for the clause that carries the weight, so the half a caller acts on
    /// cannot go missing from one of them.</summary>
    internal const string InPlaceWouldRewrite = "your ORIGINAL file rewritten; " + NoBackupOrUndo;

    /// <summary>What makes the in-place lane the lane it is: houseCARL keeps nothing to undo it with. Every
    /// sentence that mentions the rewrite is built on this rather than repeating it.</summary>
    internal const string NoBackupOrUndo = "no houseCARL backup or undo";

    /// <summary>The mod-folder line for an IN-PLACE write: the target is a mod the user already runs, so there is
    /// nothing to enable — only a re-sort, and only if the edit moved a winner. (compact's copy had lost "in your
    /// load order"; same sentence, one reading.)</summary>
    internal static string InPlaceModFolder(string modFolder) =>
        $"mod folder: {modFolder}  — already active in your load order; re-sort only if a winner changed\n";

    /// <summary>The header + mod-folder pair for a write to a NEW patch or an EXTENDED one — the default lane's
    /// "here is the artifact and what to do with it". Rendered identically by apply / create / forward; a fourth
    /// copy is how the "enable + sort it in MO2" instruction would have gone missing from one of them.</summary>
    internal static string NewOrExtendedArtifact(bool extended, string file, long bytes, string modFolder) =>
        (extended ? $"extended {file} (existing patch grown; {bytes} bytes)\n"
                  : $"wrote {file} (new patch; {bytes} bytes)\n")
      + (extended ? $"mod folder: {modFolder}\n"
                  : $"mod folder: {modFolder}  — enable + sort it in MO2 to use the patch\n");

    /// <summary>The masters line. Trivial, and repeated six times — including the empty-set spelling, which is the
    /// part that carries meaning (a patch with no masters is a standalone, not a broken header).</summary>
    internal static string Masters(IReadOnlyList<string> masters) =>
        $"masters: {(masters.Count == 0 ? "(none)" : string.Join(", ", masters))}\n";

    // ---- dry run -------------------------------------------------------------------------------------
    /// <summary>#225 — the first line of any dry run. It says NOTHING happened before it says what would: a dry run
    /// that reads like a write is the silent-wrong-answer class (Q3), and this is the line a caller skims.</summary>
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

    /// <summary>The dry run's closing line: what the pipeline actually proved, how to commit, and the honest limit
    /// on the proof. <paramref name="proved"/> is the per-verb clause (apply resolved + pre-flighted its ops;
    /// forward resolved every record from its source).
    /// <para>The parenthetical is shared, and it is the FULLER of the two readings the copies had drifted to:
    /// forward's said only "disk faults", apply's named the Mutagen serialize refusal too. Both verbs commit
    /// through the same serializer, so a body Mutagen declines to write surfaces at commit on either — the shorter
    /// copy was not a scoped claim, it was a lost clause.</para></summary>
    internal static string DryRunClose(string proved, string realVerb) =>
        $"{proved}; to {realVerb} for real, repeat the call without dry_run. "
      + "(A real write can still fail at serialize/commit — disk faults and data Mutagen refuses to serialize surface only there.)";

    // ---- row-truncation notes ------------------------------------------------------------------------
    /// <summary>What a cut row list says about the OPERATION, as opposed to the render: the rows shown were cut,
    /// the work was not. Both transports made this claim in their own words — text "every one WAS forwarded", json
    /// "the WRITE is complete and unaffected" — so it is resolved to the concrete reading, which is the one a
    /// caller can act on ("was it done?" answered directly, rather than by an abstraction over it).
    /// <para><b>Dry-run aware, and that is not cosmetic.</b> The json copy said "the WRITE is complete and
    /// unaffected" on a truncated DRY RUN — a response asserting a write on the one lane that writes nothing,
    /// which is precisely the confusion <see cref="DryRunHeader"/> exists to prevent (Q3). The text twin had it
    /// right and said the dry run covered every one. One source now, and the dry-run arm is part of it.</para></summary>
    internal static string RowsCutOperationIntact(bool dryRun, string pastParticiple) =>
        dryRun ? "the dry run covered every one" : $"every one WAS {pastParticiple}";

    /// <summary>The opening of a json <c>truncated_note</c>: which ceiling was hit and what it dropped. json-only —
    /// the text renders state the same two facts inside their own truncation bracket, where the counts sit.</summary>
    internal static string JsonRowsCut(int cap) =>
        $"the render hit max_chars={cap} and dropped trailing rows";

    /// <summary>What a truncated CREATE row list tells the caller — stated by both transports, and one of the two
    /// places the branch's own rule was still broken: the claim lived twice and had already drifted, with only the
    /// json copy naming the lane mechanics that make a re-issue expensive.
    /// <para>The trap is the load-bearing half and is a <see cref="Twins"/> member; the read-back CALL is built per
    /// outcome by <see cref="WriteTools.ReadBackCall"/>, which has been shared since PR #311.</para></summary>
    internal static string CreateRowsCutRemedy(string readBackCall) =>
        $"{RowsCutOperationIntact(false, "created")}. Read them back with {readBackCall} — {Twins.CreateReissueTrap}";

    // ---- post-write report blocks (create) -----------------------------------------------------------
    /// <summary>The "check could not run" line the three post-write reports share: the check failed, the records
    /// were still CREATED, and here is what the caller must now verify by hand. Q3 — a check that did not run must
    /// never read like a check that passed. <paramref name="createdNoun"/> stays per-block ("the records", "the
    /// cell(s)") because that half is the block's own subject, not the shared claim.</summary>
    internal static string CheckCouldNotRun(string checkName, string error, string createdNoun, string verifyManually) =>
        $"  {checkName} check could not run: {error} — {createdNoun} WERE created; {verifyManually}\n";

    /// <summary>The scan-incompleteness note: a BSA that failed to read makes an "absent" mean "unscanned", which
    /// is a different claim from "looked for and not found". <paramref name="absentThing"/> is what the block's own
    /// rows call the missing item.</summary>
    internal static string ScanIncomplete(string absentThing) =>
        $"  note: a BSA failed to read this scan, so {absentThing} above may merely be unscanned — verify in MO2.\n";

    /// <summary>What a CUT cell-shell block costs specifically, beyond the generic "rows were dropped". Each cell
    /// row carries its own <c>must_provide</c> work list, so the dropped rows are exactly the Creation-Kit work
    /// this response existed to name — a fact the counts alone do not convey. json-only today: the text block's
    /// cut notice carries no equivalent, and inventing one for it would be a behaviour change rather than the
    /// migration this is.</summary>
    internal const string CellRowsCutLoss =
        "each dropped row is Creation-Kit work this response was supposed to name";

    /// <summary>Sentences the SAME outcome must carry on BOTH transports. Reflected over by the write-surface
    /// guard's twin arm — see this class's summary. Members are whole invariant strings on purpose: a sentence
    /// interpolating a cap or a filename cannot be compared verbatim across lanes, so parameterised twins stay on
    /// the outer class and are covered by the arm's budget/count parity checks instead.</summary>
    internal static class Twins
    {
        // ---- create's post-write hazard reports ------------------------------------------------------
        /// <summary>Voice coverage — the stake, in the words both lanes use. A created voiced response with no
        /// .fuz on disk is byte-valid and SILENT in game.</summary>
        internal const string VoiceStake = "voice coverage was not fully rendered";

        /// <summary>Result-script coverage — the stake. A bound script that is unwired or uncompiled is byte-valid
        /// and does nothing. (The two copies had drifted to "that's" and "that is"; one reading now.)</summary>
        internal const string ScriptStake = "a bound script that is unwired or uncompiled runs NOTHING in game";

        /// <summary>Cell shell — the stake. houseCARL creates a valid, correctly-placed CELL record and does not
        /// author world content, so "created" must never read as "looks right in game".</summary>
        internal const string CellStake =
            "a created cell is a valid, correctly-placed record but EMPTY — houseCARL does not author world content";

        /// <summary>The grid-occupancy seam, declared rather than silently unchecked. Both lanes carried this and
        /// the json copy had lost "(engine behavior undefined)" — the clause that tells a caller the failure is not
        /// a houseCARL limitation they can work around by trying again.</summary>
        internal const string GridOccupancy = "the cell was created.";

        /// <summary>What a CUT post-write report block means, and the one action a caller must not take to widen it.
        /// <para>These blocks ride the CREATE render only, so the specific reading is the true one: re-issuing
        /// allocates the records AGAIN. The json copy had generalized to "Do NOT re-issue the write to widen this",
        /// which is weaker advice about the same call — resolved to the specific reading, which is also the one
        /// that survives a caller reading it literally.</para></summary>
        /// <summary>Why a truncated CREATE must not be re-issued to widen its own render. The sibling verbs' rows
        /// are safe to re-ask for — a repeated remove is refused, a repeated forward re-copies identical bodies —
        /// but a repeated create ALLOCATES AGAIN, and the trap does not care which transport asked. Both lanes
        /// carry this; the text copy used to state it in four words and the json copy in forty.</summary>
        internal const string CreateReissueTrap =
            "do NOT re-issue this call to see the rest: a repeated create allocates the records AGAIN (on the "
          + "default lane patch= auto-suffixes into a second full patch; under into= each record is re-created at "
          + "its old FormID with its prior contents discarded).";

        internal const string ReportBlockCut =
            "the records WERE created and this block is only a render of them — compare rendered vs total rather than "
          + "reading the list as the whole answer. Do NOT re-issue the create to widen it: that allocates the records again";

        // ---- write_seq -------------------------------------------------------------------------------
        /// <summary>#312 — the destination already held exactly these bytes. Q3: a skipped write and a done one
        /// must not look alike, so this is its own statement rather than a caveat under a "wrote".</summary>
        internal const string SeqUnchanged =
            "the destination already held EXACTLY these bytes, so NOTHING was written";

        /// <summary>The byte-identical replace: the file was rewritten only because its timestamp could not be
        /// refreshed in place, so nothing was lost. Scoped deliberately — dressing this as a loss would train the
        /// reader to ignore the word REPLACED when it does mean one.</summary>
        internal const string SeqReplacedSameBytes =
            "the file already at that path held EXACTLY these bytes; it was rewritten only because its timestamp "
          + "could not be refreshed in place, so nothing was lost.";

        /// <summary>The replace that CAN have cost something: on a folder the caller named, the .seq that was there
        /// may have been the mod's own, and houseCARL keeps no backup.</summary>
        internal const string SeqReplacedUserFolder =
            "a .seq was already at that path and has been OVERWRITTEN; houseCARL keeps no backup, and in a folder "
          + "you named that file may have been the mod's own shipped .seq.";

        /// <summary>The ordinary regenerate: houseCARL's own earlier output in its own folder, overwritten.</summary>
        internal const string SeqReplacedOwnFolder =
            "the previous .seq in that houseCARL folder was overwritten (houseCARL's own earlier output — the "
          + "ordinary regenerate case).";

        /// <summary>The timestamp stamp-forward, with the claim kept to what was established: THIS FILE is now
        /// newer than the plugin. validate_dialogue lints the .seq the VFS serves, which is this one only if this
        /// folder wins the SEQ\ conflict — so the sentence says what was done and what it is for, and does not
        /// promise a verdict from a tool that resolves its input differently.</summary>
        internal const string SeqTimestampRefreshed =
            "housecarl_validate_dialogue's SEQ staleness check compares mtimes "
          + "— for the copy the load order actually serves.";

        /// <summary>No start-game-enabled quests: a .seq lists only SGE quests, so none is needed and nothing was
        /// written. Never a silent empty file, never a misleading "done".</summary>
        internal const string SeqNoQuests =
            "a .seq lists only quests with the Start Game Enabled flag, so none is needed and NOTHING was written";

        /// <summary>The absent §2.1.1 stamp, stated as a fact with its reason. A .seq is derived from the plugin
        /// FILE alone, so nothing here consulted a load-order build — an absent epoch line and a DROPPED one are
        /// otherwise the same observable.</summary>
        internal const string SeqNoEpoch =
            "a .seq is derived from the plugin FILE alone (its FormID encoding is load-order-independent), so this "
          + "call consulted no load-order build — the absent stamp is a fact, not a dropped field.";

        /// <summary>write_seq's standing limit (Q3): the quests will START, which is not a claim that the quest or
        /// its dialogue is otherwise well-formed. The json copy had dropped the pointer at the tool that does check
        /// that — the half of the sentence that tells the caller what to do next.</summary>
        internal const string SeqStandingLimit =
            "this makes the quest(s) START at game start; it does not verify the quest or its dialogue is otherwise "
          + "well-formed (use housecarl_validate_dialogue for the dialogue graph).";

        /// <summary>What a truncated QUEST list means and what re-running costs. The .seq FILE carries every quest —
        /// only the LIST was cut — so the remedy prices the re-run rather than prescribing it (the wrong-remedy
        /// class: "raise max_chars and re-issue" would mean writing the .seq again, into a second fresh mod folder
        /// on the lane a caller reaches by naming none).
        /// <para>The text copy said "name into=/output_dir= the folder below", a reference to a line only the text
        /// render prints. A shared sentence cannot carry that deixis, so the lane-neutral reading — already the
        /// json copy's — is the one kept.</para></summary>
        internal const string SeqListCutRemedy =
            "the .seq itself carries ALL of them — nothing is missing from the FILE. Re-run only if you need this LIST "
          + "widened: for a plugin OUTSIDE a houseCARL folder with no lane named, that writes the .seq again into "
          + "ANOTHER fresh mod folder (name into=/output_dir=, or let a plugin in its own houseCARL folder default "
          + "there — at any named destination a byte-identical .seq is left untouched)";
    }
}

namespace HousecarlMcp;

/// <summary>The load-bearing phrases a shared sentence MUST still contain — declared beside the sentence, checked
/// by the write-surface guard's twin arm over every <see cref="WriteSentences.Twins"/> member.
///
/// <para><b>Why this exists.</b> A construction pin of the form <c>render.Contains(TheConstant)</c> proves the
/// render reads the shared source and NOTHING about what that source says: render and assertion read the same
/// symbol, so the assertion cannot fail on the constant being emptied. Migrating the old
/// <c>Contains("&lt;fragment&gt;")</c> arms to construction pins therefore traded a content check for a wiring
/// check — and a commit that gutted three of these sentences to placeholders passed the full guard suite green.
/// This restores the content half where it belongs: next to the sentence, so it moves with it, rather than as a
/// second copy of the sentence in a probe.</para>
///
/// <para>Phrases are the CLAIM, not the wording — the part whose loss changes what the caller is told. Rewording
/// around them is free; deleting them is not.</para>
///
/// <para><b>The norm for picking one</b> (Aaron, PR #337 review): <i>a phrase carries the hazard or the action; a
/// phrase that survives its sentence's negation is not a claim.</i> Two ways to get it wrong, both found in the
/// first set written here. A bare token — <c>"grid-occupancy"</c>, <c>"into="</c> — is satisfied by any sentence
/// that merely mentions the topic, so the sentence can be rewritten to say the opposite and still pass. And a lone
/// common word — <c>"overwritten"</c> — cannot tell "was overwritten" from "was NOT overwritten". Pick the span
/// that DISAPPEARS when the sentence stops being true: the imperative (<c>"do NOT re-issue this call"</c>), the
/// consequence (<c>"prior contents discarded"</c>), or the negated form spelled out (<c>"does NOT check
/// grid-occupancy"</c>).</para>
///
/// <para><b>This norm is for the AUTHOR, and deliberately not a rule the checker enforces.</b> Deciding whether a
/// phrase is a claim is judgment, and judgment inside a comparator is #308's shape — the thing this project already
/// killed once, where a verdict layer kept telling callers to re-issue writes that had landed. The checker does a
/// substring test and nothing else; it is a backstop against a sentence being emptied, never an assessment of
/// whether the sentence is any good. If a future round proposes teaching it to grade phrase quality, that is the
/// same mistake wearing new clothes: the answer is a better phrase, not a smarter check.</para></summary>
[AttributeUsage(AttributeTargets.Field)]
internal sealed class MustStateAttribute : Attribute
{
    internal string[] Phrases { get; }
    internal MustStateAttribute(params string[] phrases) => Phrases = phrases;
}

/// <summary>The declared way to say a shared sentence carries NO claim worth pinning — a label, a separator, a
/// fragment whose meaning lives entirely in the sentences that compose it.
///
/// <para><b>Why an explicit opt-out rather than just leaving the attribute off.</b> The outer-class walk used to
/// SKIP an undecorated const, so a sentence with real claims was unchecked simply because nobody remembered it —
/// and, in the reviewer's words, "the next sentence added there inherits this silence" (PR #337 re-review,
/// residual A). Absence-as-silence is the hole generator: it makes the safe state the one you reach by doing
/// nothing. With this attribute the walk demands a decision — phrases, or a stated reason there are none — and an
/// undecorated const FAILS by name. The reason is prose for the next author, never parsed.</para></summary>
[AttributeUsage(AttributeTargets.Field)]
internal sealed class NoClaimsAttribute : Attribute
{
    internal string Reason { get; }
    internal NoClaimsAttribute(string reason) => Reason = reason;
}

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
/// <c>lane:"in_place"</c> plus typed flags — live directly on this class rather than in <see cref="Twins"/>,
/// because per-LANE coverage is not a claim that can be made about them. They still carry
/// <see cref="MustStateAttribute"/>, and the guard's content check walks THIS class as well as <c>Twins</c>: the
/// in-place hazard is the loudest sentence on the surface and this change made it a single token, so leaving it
/// outside the net meant one edit could silently strip it from all five in-place renders at once (PR #337 review,
/// finding 1). Coverage of those is asserted directly by the lane arm instead.</para>
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
    [MustState("your ORIGINAL file was rewritten", "no houseCARL backup or undo")]
    internal const string InPlaceRewritten = "your ORIGINAL file was rewritten; " + NoBackupOrUndo;

    /// <summary>The same hazard in the tense a DRY RUN needs — it has not happened yet. Two spellings because the
    /// tense really does differ; ONE source for the clause that carries the weight, so the half a caller acts on
    /// cannot go missing from one of them.</summary>
    [MustState("your ORIGINAL file rewritten", "no houseCARL backup or undo")]
    internal const string InPlaceWouldRewrite = "your ORIGINAL file rewritten; " + NoBackupOrUndo;

    /// <summary>What makes the in-place lane the lane it is: houseCARL keeps nothing to undo it with. Every
    /// sentence that mentions the rewrite is built on this rather than repeating it.</summary>
    [MustState("no houseCARL backup or undo")]
    internal const string NoBackupOrUndo = "no houseCARL backup or undo";

    // ---- the runtime-config reminder (merge + compact) -----------------------------------------------
    //
    // THE CLAIM RULE, and why these two sentences are so bare.
    //
    // Every clause below states ONE of exactly three things:
    //   (i)   something this tool itself did or did not do;
    //   (ii)  a break ENTAILED by what this operation did, scoped by the addressed line's own content;
    //   (iii) a per-system fact, stated ONLY where a bundled skill states it verbatim, cited by file and line beside
    //         the constant — quoted, never derived, and never generalised to the systems whose skills are silent.
    // Nothing else. No quoted syntax, no addressing-model taxonomy, no exhaustiveness.
    //
    // That rule exists because the earlier drafts kept getting the grammars wrong, one layer at a time. The first
    // claimed all four address a record as <plugin>|<FormID> — true of SkyPatcher alone; SPID and KID are
    // suffix-tilde, and spid-authoring's own skill lists confusing those two as a known trap ("a SkyPatcher-style ID
    // won't resolve"), while OAR is not an .ini at all. The correction claimed they address "through the plugin that
    // defines it plus its FormID" — also false, because every one of them also takes an EditorID, and merge and
    // compact both PRESERVE EditorIDs, so those lines do not break. Two rounds, two wrong models. A sentence that
    // describes someone else's grammar is a sentence that has to be re-verified against four skills every time
    // anything changes; a sentence that describes only what THIS operation did to a line that names a donor is true
    // by construction.
    //
    // The system list is an enumeration for findability, not a claim about what they are. "Runtime config files" and
    // not "distributor configs": OAR is an animation-condition system, and calling it a distributor was itself one of
    // the false claims. Verified before shipping (2026-08-18) that each of the four has a line form naming a plugin,
    // and that no bundled skill documents a fallback keeping such a line matched once that plugin deactivates —
    // SkyPatcher's skill states the opposite outright, that a form it cannot resolve is silently skipped.

    /// <summary>Merge's reminder.
    ///
    /// <para>The middle clause says the records MOVED, and stops there. It used to say a line naming a donor "stops
    /// matching", which was wrong twice over. Factually: for a line that names the donor as an EXCLUSION — SPID's
    /// plain-plugin-name Form filter under the <c>-</c> modifier
    /// (<c>spid-authoring/references/filters.md:97-107</c>), SkyPatcher's <c>filterByModNamesExcluded</c>
    /// (<c>skypatcher-authoring/references/grammar-core.md:204</c>) — the line does not go dead, its scope WIDENS, and
    /// the donor's records start receiving exactly what they were excluded from. Over-application is the harder of the
    /// two symptoms to notice, and the caller was being sent to look for the other one. And structurally: "stops
    /// matching" is a claim about how those systems EVALUATE a line whose named plugin is gone, which is rule (iii)
    /// territory with no skill behind it — none documents unresolvable-filter semantics, so the pre-commit check that
    /// found no fallback was absence of evidence and never reached the exclusion shape at all.</para>
    ///
    /// <para>What survives is the operation's own fact: the records now live under the merged plugin's name, so a line
    /// naming a donor no longer describes them. Direction-neutral by construction — it names neither a dead line nor an
    /// over-applying one, because which of those a caller gets depends on the system and on the line, and the skills own
    /// that. Both line shapes are covered on purpose: addressing a record IN the donor and filtering ON the donor are
    /// the same fact from this side, since both name the plugin the records no longer belong to. Still no override/master
    /// carve-out — a line naming a record the donor merely overrode names the MASTER, which the scoping excludes already.</para>
    ///
    /// <para>The third clause is the only rule-(iii) claim on the surface, and it is SkyPatcher-only for a reason: its
    /// skill states the filename gate verbatim — <c>Plugin.esm.ini</c> / <c>Plugin.esp.ini</c> load "<i>Only</i> when
    /// that plugin is active in the load order; otherwise skipped" (<c>grammar-core.md:93</c>, restated at 218-219 as
    /// deciding "whether the file loads at all"). The generalised form — "any config file named for the donor" — was
    /// considered and REFUSED: SPID and KID document filename rules with no activity gate, so the general claim is
    /// underivable from our sources and would be wrong-shaped for a SPID file that merely happens to be named after a
    /// donor. It earns its place because it is the one break the rest of the sentence cannot reach: every line in that
    /// file goes dark, including the lines that never mention a donor at all.</para></summary>
    [MustState("Nothing here rewrites those files", "no longer describes those records", "stops loading at the swap")]
    internal const string MergeRuntimeConfigs =
        "Nothing here rewrites those files. After the swap a donor's records live under the merged plugin's name, so a " +
        "line that names a donor — whether to address a record in it or to filter on it — no longer describes those " +
        "records. Separately, a SkyPatcher config file whose NAME is a donor's filename (Plugin.esp.ini) is read only " +
        "while that plugin is active, so it stops loading at the swap: every line in it, including the lines that never " +
        "name a donor.\n";

    /// <summary>Compact's. The plugin name survives a compaction, so the half that moves is the object id — and only
    /// the ids this run actually moved, which the accounting above states. "once the compacted plugin is the one
    /// loading" rather than a flat present tense: in the new-file lane nothing has broken yet, since the original is
    /// still active until the swap; in the in-place lane it is already the one loading. One clause, both lanes.</summary>
    [MustState("does not read", "nothing here rewrites them", "no longer reaches that record")]
    internal const string CompactRuntimeConfigs =
        "That pass reads plugins; it does not read runtime config files (SPID, KID, SkyPatcher, Open Animation " +
        "Replacer), and nothing here rewrites them. The plugin name is unchanged, but a line in one of them that " +
        "addresses a record by an object id this compaction moved no longer reaches that record once the compacted " +
        "plugin is the one loading.\n";

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

    // ---- closure copy (PR 3b) ------------------------------------------------------------------------
    /// <summary>The standalone claim: the plugin(s) the copy was taken away from are NOT masters of the artifact.
    /// The whole point of the operation, so it is stated rather than implied by their absence from the list.</summary>
    [MustState("is NOT a master")]
    internal const string CopyStandalone = "standalone: the source is NOT a master of this patch.";

    /// <summary>The opposite, and an alarm rather than a note: a copy that masters its own source has failed at the
    /// one thing it exists to do, however cleanly it wrote.</summary>
    [MustState("IS among the masters", "NOT standalone")]
    internal const string CopySourceMastered =
        "!! the source IS among the masters — this copy is NOT standalone. Nothing was silently fixed; inspect the patch before relying on it.";

    /// <summary>The THIRD arm (inventory F24): the source record is defined in a base-game master. Those are never
    /// bound — copying a vanilla-defined record must not internalize vanilla — so the standalone claim above would
    /// be computed over a set deliberately emptied, and asserted "the source is NOT a master" three lines under a
    /// masters list naming it. "Standalone" is simply the wrong frame here: an always-loaded master is not being
    /// removed from anything, so the honest sentence says what the copy IS instead of denying something no caller
    /// asked. Carried over from the ancestor, which had this arm; the successor shipped without it until review
    /// round 3, and it lands on the appearance skill's own headline query.</summary>
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
        "the patch WAS written, so do NOT re-run blindly (that mints a duplicate); read it back with housecarl_read_plugin_file.";

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

    // ---- the ordered source universe: parameterised sentences, split so the content net can reach them ----
    // These three were written in METHOD form, and [MustState] is AttributeTargets.Field — so their text sat
    // OUTSIDE the const-decides net and all three could be replaced with "gutted" while the whole suite stayed
    // green (measured, PR 3b review round 1). A method is not the problem; text that lives only inside one is.
    // Each sentence is now an invariant CONST the render emits verbatim, with the caller's data interpolated
    // around it — so the content pin has something to pin and the coverage arm has something to observe.

    /// <summary>Single-source label. The claim is the source name it introduces, not the word.</summary>
    [NoClaims("a label; the claim is the source name it introduces")]
    internal const string CopySourceSingleLabel = "source: ";

    /// <summary>Multi-source header — R2's readback half. "First hit wins" is the ordering contract in the one
    /// place a caller reads it, so it is pinned rather than phrased freshly per render.</summary>
    [MustState("in order", "first hit wins")]
    internal const string CopySourceListLabel = "sources (in order, first hit wins): ";

    /// <summary>The miss refusal's opening claim.</summary>
    [MustState("no source produced")]
    internal const string CopySourceMissLead = "no source produced ";

    /// <summary>…and the part that makes it actionable: EVERY source consulted, not just the last one tried.
    /// Naming only the last reads as though one source was checked and sends the caller to fix the wrong file.</summary>
    [MustState("Consulted, in order:")]
    internal const string CopySourceMissConsulted = ". Consulted, in order: ";

    /// <summary>The miss remedy. Names, not paths (the standing refusal-remedy rule): a path invites a paste-back
    /// into the next call, and a source is named by plugin.</summary>
    /// <para>Both causes get a remedy, because they need different ones. The Description calls a DANGLING link the
    /// typical cause of a miss, and for that one no plugin exists to name — so a remedy offering only "add a source"
    /// sent the caller looking for a file that was never there (review round 3). The exclusion routes are the real
    /// answer in that case, and they are named here rather than left to be inferred.</para>
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

    // ---- the seed-shape boundary (shape ruling (a), Aaron-go 2026-08-15) ------------------------------
    /// <summary>What <c>seed_paths</c> supports, and the ROUTE for what it does not. A walk seeds from record
    /// LINKS; a field whose entries are link-bearing structures is a field-bundle copy, which <c>housecarl_apply</c>
    /// already does — and there the caller picks replace-vs-merge with the grammar that lane already has, rather
    /// than this one inventing a second answer. Same shape as the clone lane's required-link refusal, which names
    /// the target lane instead of guessing.</summary>
    [MustState("seed_paths takes a record link or a list of record links", "housecarl_apply")]
    internal const string CopySeedShapeRoute =
        " — seed_paths takes a record link or a list of record links, and nothing else. Copying a field whose " +
        "entries carry links INSIDE them is a field-bundle copy: use housecarl_apply's bundle=/assignments= zip, " +
        "where op=Merge and op=ReplaceAll are your choice between merging into the target's entries and replacing " +
        "them. Nothing was written.";

    /// <summary>An off-order link on a record that was ALREADY in the patch. The serialization failure is real and
    /// this call did not cause it, so the remedy is about the patch and the mod — never about `exclude_types`,
    /// which the caller may not even have passed (Aaron's review).</summary>
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

    /// <summary>The seed's shape is SUPPORTED and the TARGET cannot take it. A separate route from the shape one
    /// above, because these three refusals used to borrow it: the caller was told <c>seed_paths</c> does not accept
    /// their field and routed to <c>housecarl_apply</c>'s zip, when the path was legal and the target's property was
    /// the problem — so the remedy names the target, which is where the fix is (review round 3).</summary>
    [MustState("the TARGET's", "not the seed path", "target=")]
    internal const string CopyUnwritableTargetRoute =
        " — the seed path is fine; it is the TARGET's property that cannot be written, not the seed path. Copy onto " +
        "a record of the same type that carries its own, or mint a clone with new_editorid= instead of target=. " +
        "Nothing was written.";

    /// <summary>The walk found no links at all under the seed paths. The cause R4 attributes to it is named, because
    /// "check seed_paths" sends a caller to re-read a field list that was correct — a TEMPLATED record's appearance
    /// fields are empty BY DESIGN, and the fix is to copy the template, not to fix the paths (review round 3).</summary>
    [MustState("carry no record links", "TEMPLATED", "the template it points at")]
    internal const string CopyNoSeeds =
        "the seed fields carry no record links, so this copy would produce nothing. The usual cause is a TEMPLATED " +
        "record — one whose template flags hand these fields to another record, leaving its own empty by design; " +
        "copy the template it points at instead. Otherwise check seed_paths= against the record.";

    /// <summary>Appended to a strip line that nulled a WHOLE property. The count alone ("STRIPPED 1 reference(s)")
    /// described the link that forced the removal and not what the removal cost: nulling VirtualMachineAdapter to
    /// clear one bound script property drops every script on the clone (review round 3).</summary>
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

    // ---- kept links, told apart (review round 1) -----------------------------------------------------
    /// <summary>Links kept because they resolve OUTSIDE the source universe. These genuinely master normally.</summary>
    [MustState("outside the source", "mastered normally")]
    internal const string CopyKeptOutside = "link(s) resolve outside the source — mastered normally.";

    /// <summary>Links kept because an exclusion PRUNED them. These are inside the source universe and still point
    /// at it, which is the opposite of the sentence above — collapsing the two let one response claim a link was
    /// mastered normally while the strip list showed it removed.</summary>
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

    // ---- the from record's own provenance (R2) -------------------------------------------------------
    /// <summary>Which source produced the record the caller ASKED for. Every internalized record names its arm;
    /// this one did not, and it is the single body an ordered source list exists to disambiguate.</summary>
    [MustState("read from")]
    internal const string CopyFromArmLead = "the source record was read from ";

    // ---- the copied records' asset paths (inventory I1) ----------------------------------------------
    /// <summary>The asset paths the copied records reference. `copy` is the only thing that knows WHAT it copied,
    /// so it enumerates; it does not fetch, place, or judge them — that decision is the caller's, over
    /// housecarl_asset_status and housecarl_bulk_place_asset.</summary>
    [MustState("does NOT place them", "housecarl_bulk_place_asset")]
    internal const string CopyAssetPathsHeader =
        "asset paths the copied records reference (this call does NOT place them — check each with " +
        "housecarl_asset_status, then place what you keep with housecarl_bulk_place_asset):";

    // ---- dry run -------------------------------------------------------------------------------------
    /// <summary>#225 — the first line of any dry run. It says NOTHING happened before it says what would: a dry run
    /// that reads like a write is the silent-wrong-answer class (Q3), and this is the line a caller skims.
    /// <para>The phrases are the two CLAIMS, not the "DRY RUN" label: the label survives any rewrite that says the
    /// opposite ("this is not a DRY RUN"), while "NOTHING was written" and "originals untouched" do not.</para></summary>
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
    [MustState("Creation-Kit work this response was supposed to name")]
    internal const string CellRowsCutLoss =
        "each dropped row is Creation-Kit work this response was supposed to name";

    // ---- place_asset: naming which copy to read (S2's SOURCE poles) ----------------------------------
    /// <summary>The refusal's load-bearing half: houseCARL picked NOTHING. Which copy of a contended file is the
    /// right one is a judgement about the modlist, and the tool has never made it (the placer's own charter) — the
    /// sentence exists so a caller reads "choose" rather than "retry".</summary>
    [MustState("will not guess which copy is correct")]
    internal const string PlaceSourceWillNotGuess = "houseCARL will not guess which copy is correct";

    /// <summary>Contended source, no pole named. Lists the providers by NAME, each QUOTED — the quoted half is
    /// literally what <c>source_provider=</c> takes back, so the refusal hands the caller its own next call.
    /// Deliberately NOT the on-disk paths: a path round-tripped through the caller can go stale between the resolve
    /// and the read, and the whole point of naming a provider is that it cannot.
    /// <para>The remedy is UNCONDITIONAL. It used to be withheld when a provider was itself called "winner", because
    /// the bare token could not tell the two apart; the pole is sigiled now, so no listed name can ever collide with
    /// it and there is no load-order state this sentence has to know about.</para></summary>
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

    /// <summary>The naming correction that rides on every named-provider miss — the same shape as the in_place=true
    /// correction: a caller who reached this error by typing the pole as a bare word gets told the spelling, and a
    /// caller who genuinely mistyped a mod name loses one clause. STATIC, never conditional on what the load order
    /// happens to contain: a sentence that knows which mods are installed is a sentence with a state to get wrong,
    /// and that is exactly what the conditional winner-remedy was.</summary>
    [MustState("winner pole is spelled")]
    internal const string PlaceSourcePoleSpelling =
        "(the winner pole is spelled " + AssetSourceChoice.WinnerToken + " — a bare name always means a provider of that name)";

    /// <summary>The named provider does not supply this path (others may). The list is the same quoted-name spelling
    /// the ambiguity refusal uses, and for the same reason — this is the caller's next call, not a display.</summary>
    internal static string PlaceSourceNamedAbsent(string provider, string rel, IReadOnlyList<string> providerNames) =>
        $"'{provider}' does not supply '{rel}', so {PlaceSourceNoSubstitute} — pass one of these names instead, "
      + $"quotes excluded: {string.Join("; ", providerNames)}. {PlaceSourcePoleSpelling}";

    /// <summary>The both-slots expansion's own constraint on the pole. A FormID with no kind derives TWO destination
    /// paths, so an explicit source= (one file) cannot serve them — but the pole can, because it names whose copy
    /// rather than which file. The refusal used to assert the pole was "fine here" without that qualifier, which was
    /// false for every call that also passed a source.</summary>
    [MustState("only when source= is omitted")]
    internal const string PlaceBothSlotsPoleConstraint =
        "source_provider= works here only when source= is omitted — it names WHOSE copy, not which file, and two slots are two files";

    /// <summary>A pole named against a source that is already one exact file. Q3 — an input that cannot apply is
    /// said, never dropped: silently ignoring it would let a caller believe a provider was honoured.</summary>
    [MustState("only applies to a Data-relative source", "already names one exact copy")]
    internal const string PlaceSourceProviderNeedsRelPath =
        "source_provider= only applies to a Data-relative source resolved through the VFS. The source you passed is an "
      + "on-disk path, which already names one exact copy — drop source_provider=, or pass source= the Data-relative path.";

    // ---- the into=-extend not-found refusal (#356) ---------------------------------------------------
    /// <summary>The whole remedy an operation that has stated nothing about creating a patch may offer: check what
    /// you typed. It is the DEFAULT tail of the shared not-found refusal, and it is deliberately the weakest thing
    /// that is true for every caller — the tail it replaced offered "Omit into= to create it fresh", which removal
    /// inherited and could not honour. A stronger claim is one an operation makes for itself
    /// (<c>FreshPatchRemedy</c>), never one the resolver assumes on its behalf.</summary>
    [MustState("Check the name")]
    internal const string ExtendCheckTheName = "Check the name.";

    /// <summary>Removal's own half of that refusal: why no create remedy is offered here. Measured before it was
    /// written — creating the patch first does NOT leave anything to remove: a fresh patch carries no override of
    /// the record, and removal refuses it with "not carried by patch". So the honest sentence is that the create
    /// route does not exist for this lane, not a route the caller would have to discover is a dead end.</summary>
    [MustState("will not create a patch here", "nothing to remove")]
    internal const string RemoveNoFreshPatch =
        "houseCARL will not create a patch here: a removal only drops a record the patch ITSELF carries, so a patch "
      + "created now would have nothing to remove.";

    /// <summary>The other lane a removal has, in the spelling <c>housecarl_remove</c> declares — one string naming
    /// the file being rewritten. Measured through the TOOL, not the service: the named call reaches the first-touch
    /// in-place confirmation and, once acknowledged, removes the record. It deliberately does not spell the
    /// confirmation out; that is the in-place lane's own sentence, and it is what a caller who follows this one
    /// gets next.
    ///
    /// <para>There are two spellings because there are two tools, and the SERVICE cannot tell which one called it.
    /// So each tool hands its own down (<c>housecarl_remove</c> this one, <c>housecarl_remove_record</c>
    /// <see cref="RemoveInPlaceLaneLegacy"/>) rather than the resolver picking — the same caller-states rule the
    /// fresh-patch remedy follows, one altitude up. Sharing ONE sentence is what #356's own fix got wrong first:
    /// the 1.x pair was correct where it already rendered, and reusing it on the 2.0 tool named a parameter that
    /// tool does not declare. It worked only because the 1.x→2.0 lane shim silently re-spelled it, and that shim is
    /// scaffolding due to retire.</para></summary>
    [MustState("pass in_place=\"<plugin filename>\"")]
    internal const string RemoveInPlaceLane =
        "To remove from an existing plugin IN PLACE instead, pass in_place=\"<plugin filename>\".";

    /// <summary>The same lane in <c>housecarl_remove_record</c>'s 1.x spelling — a bool plus a separate
    /// <c>target=</c>. Rendered by that tool's not-found refusal and by the service's <c>patch is required</c>
    /// refusal, which is reachable ONLY from 1.x (measured: the 2.0 tool refuses "no lane named" first, in its own
    /// correct spelling, so that arm never renders for a 2.0 caller).</summary>
    [MustState("pass target=<plugin filename> + in_place=true")]
    internal const string RemoveInPlaceLaneLegacy =
        "To remove from an existing plugin IN PLACE instead, pass target=<plugin filename> + in_place=true.";

    /// <summary>Sentences the SAME outcome must carry on BOTH transports. Reflected over by the write-surface
    /// guard's twin arm — see this class's summary. Members are whole invariant strings on purpose: a sentence
    /// interpolating a cap or a filename cannot be compared verbatim across lanes, so parameterised twins stay on
    /// the outer class and are covered by the arm's budget/count parity checks instead.</summary>
    internal static class Twins
    {
        // ---- create's post-write hazard reports ------------------------------------------------------
        /// <summary>Voice coverage — the stake, in the words both lanes use. A created voiced response with no
        /// .fuz on disk is byte-valid and SILENT in game.</summary>
        [MustState("plays SILENT in game")]
        internal const string VoiceStake = "a created voiced response with NO .fuz plays SILENT in game";

        /// <summary>Result-script coverage — the stake. A bound script that is unwired or uncompiled is byte-valid
        /// and does nothing. (The two copies had drifted to "that's" and "that is"; one reading now.)</summary>
        [MustState("runs NOTHING in game")]
        internal const string ScriptStake = "a bound script that is unwired or uncompiled runs NOTHING in game";

        /// <summary>Cell shell — the stake. houseCARL creates a valid, correctly-placed CELL record and does not
        /// author world content, so "created" must never read as "looks right in game".</summary>
        [MustState("houseCARL does not author world content")]
        internal const string CellStake =
            "a created cell is a valid, correctly-placed record but EMPTY — houseCARL does not author world content";

        /// <summary>The grid-occupancy seam, declared rather than silently unchecked. Both lanes carried this and
        /// the json copy had lost "(engine behavior undefined)" — the clause that tells a caller the failure is not
        /// a houseCARL limitation they can work around by trying again.</summary>
        [MustState("does NOT check grid-occupancy", "engine behavior undefined", "OVERRIDE it instead of creating a new one")]
        internal const string GridOccupancy =
            "houseCARL does NOT check grid-occupancy — a NEW exterior cell at a grid your load order already fills "
          + "collides (engine behavior undefined). To change an existing cell, OVERRIDE it instead of creating a new one.";

        /// <summary>Why a truncated CREATE must not be re-issued to widen its own render. The sibling verbs' rows
        /// are safe to re-ask for — a repeated remove is refused, a repeated forward re-copies identical bodies —
        /// but a repeated create ALLOCATES AGAIN, and the trap does not care which transport asked. Both lanes
        /// carry this; the text copy used to state it in four words and the json copy in forty.</summary>
        [MustState("do NOT re-issue this call", "second full patch", "prior contents discarded")]
        internal const string CreateReissueTrap =
            "do NOT re-issue this call to see the rest: a repeated create allocates the records AGAIN (on the "
          + "default lane patch= auto-suffixes into a second full patch; under into= each record is re-created at "
          + "its old FormID with its prior contents discarded)";

        /// <summary>What a CUT post-write report block means, and the one action a caller must not take to widen it.
        /// <para>These blocks ride the CREATE render only, so the specific reading is the true one: re-issuing
        /// allocates the records AGAIN. The json copy had generalized to "Do NOT re-issue the write to widen this",
        /// which is weaker advice about the same call — resolved to the specific reading, which is also the one
        /// that survives a caller reading it literally.</para></summary>
        [MustState("Do NOT re-issue the create", "compare rendered vs total")]
        internal const string ReportBlockCut =
            "the records WERE created and this block is only a render of them — compare rendered vs total rather than "
          + "reading the list as the whole answer. Do NOT re-issue the create to widen it: that allocates the records again";

        // ---- write_seq -------------------------------------------------------------------------------
        /// <summary>#312 — the destination already held exactly these bytes. Q3: a skipped write and a done one
        /// must not look alike, so this is its own statement rather than a caveat under a "wrote".</summary>
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

        /// <summary>The timestamp stamp-forward, with the claim kept to what was established: THIS FILE is now
        /// newer than the plugin. validate_dialogue lints the .seq the VFS serves, which is this one only if this
        /// folder wins the SEQ\ conflict — so the sentence says what was done and what it is for, and does not
        /// promise a verdict from a tool that resolves its input differently.</summary>
        [MustState("has been stamped forward", "contents untouched", "housecarl_validate_dialogue's SEQ staleness check", "no longer reads")]
        internal const string SeqTimestampRefreshed =
            "its mtime was older than the plugin and has been stamped forward (contents untouched); "
          + "housecarl_validate_dialogue's SEQ staleness check compares those two mtimes, so this file no longer reads "
          + "as stale — for the copy the load order actually serves, which is this one only if this folder wins the SEQ\\ conflict.";

        /// <summary>No start-game-enabled quests: a .seq lists only SGE quests, so none is needed and nothing was
        /// written. Never a silent empty file, never a misleading "done".</summary>
        [MustState("NOTHING was written", "lists only quests with the Start Game Enabled flag")]
        internal const string SeqNoQuests =
            "a .seq lists only quests with the Start Game Enabled flag, so none is needed and NOTHING was written";

        /// <summary>The absent §2.1.1 stamp, stated as a fact with its reason. A .seq is derived from the plugin
        /// FILE alone, so nothing here consulted a load-order build — an absent epoch line and a DROPPED one are
        /// otherwise the same observable.</summary>
        [MustState("consulted no load-order build", "not a dropped field")]
        internal const string SeqNoEpoch =
            "a .seq is derived from the plugin FILE alone (its FormID encoding is load-order-independent), so this "
          + "call consulted no load-order build — the absent stamp is a fact, not a dropped field.";

        /// <summary>write_seq's standing limit (Q3): the quests will START, which is not a claim that the quest or
        /// its dialogue is otherwise well-formed. The json copy had dropped the pointer at the tool that does check
        /// that — the half of the sentence that tells the caller what to do next.</summary>
        // The phrase is the ACTION, not the token (PR #337 re-review, residual B). "housecarl_validate_dialogue"
        // alone survives a rewrite to "…confirms the dialogue graph is sound (housecarl_validate_dialogue not
        // required)", which inverts the standing limit into a claim write_seq cannot make.
        [MustState("does not verify", "use housecarl_validate_dialogue for the dialogue graph")]
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
        [MustState("nothing is missing from the FILE", "writes the .seq again")]
        internal const string SeqListCutRemedy =
            "the .seq itself carries ALL of them — nothing is missing from the FILE. Re-run only if you need this LIST "
          + "widened: for a plugin OUTSIDE a houseCARL folder with no lane named, that writes the .seq again into "
          + "ANOTHER fresh mod folder (name into=/output_dir=, or let a plugin in its own houseCARL folder default "
          + "there — at any named destination a byte-identical .seq is left untouched)";
    }
}

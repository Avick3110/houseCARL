using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

// ======================================================================
//  DialogueCkParity — the CK-parity default-populate authority for the DIAL/INFO/DLVW family.
//
//  THE ONE ASYMMETRY THIS EXISTS FOR (community reports, Heisen + Junti, 2026-07-02..04):
//    Mutagen OMITS a null/unset optional subrecord on write. The Creation Kit writes it UNCONDITIONALLY,
//    nulls included. So a record authored through houseCARL that sets only the fields the author cared about
//    differs STRUCTURALLY from a CK-authored one of the same content — and for several members of this family
//    that difference is a hard failure (a CK-editor access violation the moment the topic/view is opened).
//    There is no second mechanism; the whole class closes by default-populating the nullable fields the CK
//    always emits, at record CREATE time, inside the Mutagen model (no byte-injection, no xEdit — every field
//    here is fully modeled; confirmed via mutagen-reference).
//
//  THIS IS THE SAME FIX AS #131 (the DialogTopic SNAM marker), generalised to the rest of the family. It holds
//  the three #131 invariants for every default (see DialogueSubtype's header for the original statement):
//    1. NON-OVERRIDE — fill ONLY when the author left the field null/unset; NEVER clobber an explicit value.
//    2. NEVER SILENT (Q3) — every fill is returned as a CkParityFill the create path surfaces as an OpResult
//       (label + reason), visible in the write read-back. Nothing is populated behind the author's back.
//    3. BY CONSTRUCTION — the defaults are the values a CK-authored record of the same content carries
//       (byte-verified against this load order's reference plugins, e.g. Talos' Tease), not invented; the
//       dialogue-ckparity-guard pins each one.
//
//  SCOPE (S1 — the confirmed-CK-crash tier). Each field below is a nullable Mutagen field the CK writes
//  unconditionally, with a CONFIRMED editor crash when omitted (Heisen §3 gap-2 / gap-3, INFO-DATA report):
//    • INFO (DialogResponses) FavorLevel (CNAM)   → None
//    • INFO (DialogResponses) Flags    (ENAM)     → empty DialogResponseFlags (Flags=0, ResetHours=0)
//    • DLVW (DialogView)      DNAM                → 0x00
//    • DLVW (DialogView)      ENAM                → 0x00000000
//
//  SCOPE (S2 — the byte-only tier). Same asymmetry, no confirmed crash — a byte mismatch vs a CK-authored record
//  (Confidence BYTE). Every default VALUE below was byte-verified against CK-authored vanilla records in a live load
//  order (2026-07-04) before it was committed, per the plan's "fill the correct value, not a guessed one" gate:
//    • DLBR (DialogBranch)   Category   (TNAM)     → Player  (the enum's zero-value AND 3059/3061 vanilla branches,
//                                                    across all Flags; the rare Command branch is author-set → non-override)
//    • DIAL (DialogTopic)    Priority   (PNAM)     → 50      (the CK seed for an untouched topic; the dominant value
//                                                    on vanilla Custom topics — NON-NULLABLE float, see below)
//    • QUST (Quest)          NextAliasID (ANAM)    → next-alias-ID counter: max(existing alias ID)+1, else 0
//    • QUST QuestObjective   Flags      (FNAM)     → 0 (no flags), materialised per objective
//    • QUST QuestAlias       Flags      (FNAM)     → 0 (no flags), materialised per alias
//    • QUST QuestAlias       VoiceTypes (VTCK)     → null-link (0x00000000), materialised per alias
//
//  SCOPE (S3 — the in-game-behavior tier). The same omitted-subrecord asymmetry, but the consequence is neither a
//  CK-editor crash (S1) nor a byte-only mismatch the game shrugs off (S2): the RUNNING GAME behaves WRONG (#212).
//    • DLBR (DialogBranch)   Flags      (DNAM)     → 0 (no flags)
//  An ABSENT DNAM is read by the engine as TopLevel, so a branch the author never marked top-level is published to
//  the player's dialogue menu anyway — a nameless Say()-only topic surfaces as a selectable "..." that fires its
//  lines on click. The output is byte-valid and passes validate_dialogue, so nothing catches it before the game
//  does; that combination (valid-looking, silently wrong) is what makes this the sharpest tier, not the mildest.
//
//  ONE S2 FIELD IS NOT NULLABLE — DIAL Priority is a plain float that defaults to 0, so "the author left it unset"
//  can't be read off is-null the way every other field here is. The create path detects it from the AUTHOR'S OP LIST
//  (did any edit touch the Priority path?) and passes that in; a fill happens only when Priority was never mentioned,
//  and an explicit value — INCLUDING 0 — always wins. That's why ApplyTopicPriorityDefault takes an authorSetPriority
//  flag while the nullable-field methods don't. See ApplyTopicPriorityDefault + the create-lane call.
//
//  Add S3+ defaults here too — do not fork a parallel path.
//
//  A SEMANTIC NON-DEFAULT worth stating: the `Goodbye` conversation-ender flag lives INSIDE the INFO Flags
//  struct this fills. Materialising Flags to all-zero does NOT set Goodbye — a conversation-ending line still
//  needs Flags.Flags = Goodbye set explicitly (that's an authoring choice, not a CK-parity default). The
//  dialogue-authoring skill carries that semantic.
// ======================================================================

/// <summary>One CK-parity field that was default-populated on create: a human-readable <see cref="Label"/> (the
/// OpResult summary, e.g. "FavorLevel (CNAM subrecord) auto-set to None") and a <see cref="Reason"/> (why — the
/// CK-parity rationale). The create path renders every fill as an <c>OpResult</c> so an auto-fill is never silent
/// (Q3). A record that already carried the field produces NO fill (non-override).</summary>
public readonly record struct CkParityFill(string Label, string Reason);

/// <summary>One CK-parity subrecord a record is MISSING — the read-only counterpart of a <see cref="CkParityFill"/>.
/// <see cref="Subrecord"/> names the omitted field ("CNAM (FavorLevel)"); <see cref="Detail"/> is why it matters +
/// the fix. The on-demand dialogue validator surfaces each as a Warning (a CK-editor-crash shape the game itself
/// tolerates — the same severity class as the BNAM-absent lint). Produced by the SAME null probe the fill path
/// uses, so a create that FILLS the field and a validate that FLAGS its absence can never disagree (the shared-source
/// discipline DialogueSubtype set for the SNAM marker, generalised to the rest of the family).</summary>
public readonly record struct CkParityGap(string Subrecord, string Detail);

/// <summary>The authority for the CK-parity default-populate fields of the DIAL/INFO/DLVW family — the nullable
/// subrecords the Creation Kit always writes but Mutagen omits when unset. The create path calls the per-type
/// <c>Apply…Defaults</c> method after the author's edits, fills only the fields left null (NEVER overriding an
/// explicit value), and surfaces each fill as an OpResult. Values are what a CK-authored record of the same content
/// carries, pinned by dialogue-ckparity-guard. The DialogTopic SNAM marker is its OWN authority (DialogueSubtype)
/// because its value is DERIVED from Subtype via a non-obvious table; these fields are flat constants.</summary>
public static class DialogueCkParity
{
    // --- The byte-field defaults, as hex, so the guard pins ONE source of truth (the arrays derive from these). ---
    /// <summary>DLVW DNAM default — one zero byte, as a CK-authored DialogView carries it.</summary>
    public const string ViewDnamHex = "00";
    /// <summary>DLVW ENAM default — four zero bytes, as a CK-authored DialogView carries it.</summary>
    public const string ViewEnamHex = "00000000";

    /// <summary>INFO (DialogResponses) CK-parity defaults — FavorLevel (CNAM) and the Flags (ENAM) response-data
    /// struct. Both are omitted by Mutagen when unset; a CK-authored INFO always carries both, and an INFO missing
    /// either crashes the Creation Kit the moment its owning topic is opened in the dialogue editor (the game
    /// tolerates it — a CK-editor crash, not a load CTD; Heisen §3 gap-2 + the INFO-DATA report, both confirmed in
    /// the Basic Wenches OStim session). Non-override: fills only what the author left null. Returns the fills applied
    /// (empty when the author supplied both).</summary>
    public static IReadOnlyList<CkParityFill> ApplyInfoDefaults(IDialogResponses info)
    {
        var fills = new List<CkParityFill>(2);

        // FavorLevel (CNAM): nullable enum (FavorLevel?); None is the CK default (Talos' Tease / SexLab Solutions
        // reference INFOs all carry FavorLevel = None). Only fill when the author set no favor level. The absence
        // probe is the shared HasFavorLevel (see below) — the SAME one MissingInfoDefaults flags on, so fill + check
        // can't drift.
        if (!HasFavorLevel(info))
        {
            info.FavorLevel = FavorLevel.None;
            fills.Add(new CkParityFill(
                "FavorLevel (CNAM subrecord) auto-set to None",
                "None — every CK-authored INFO carries the CNAM (FavorLevel) subrecord; an INFO created without it "
                + "crashes the Creation Kit when its owning topic is opened in the dialogue editor (the game tolerates "
                + "it). CK-parity default-populate, in-model (#131 pattern)."));
        }

        // Flags (ENAM): the DialogResponseFlags response-data struct — a NULLABLE reference type (the schema doesn't
        // mark it null because it's not a Nullable<T>, but IDialogResponses.Flags is DialogResponseFlags? and reads
        // null when unset — confirmed empirically: setting a sub-field "materialises" it). A fresh struct is Flags=0,
        // ResetHours=0 — exactly the CK's empty ENAM. Only fill when the author materialised no Flags. NOTE: the
        // Goodbye conversation-ender lives in Flags.Flags; an all-zero fill does NOT set it — that stays explicit.
        // Absence probe is the shared HasResponseFlags — the SAME one MissingInfoDefaults flags on (no drift).
        if (!HasResponseFlags(info))
        {
            info.Flags = new DialogResponseFlags();
            fills.Add(new CkParityFill(
                "Flags (ENAM subrecord) auto-set to empty response flags (Flags=0, ResetHours=0)",
                "empty DialogResponseFlags — every CK-authored INFO carries the ENAM (response flags + reset-hours) "
                + "subrecord; an INFO created without it crashes the Creation Kit when its owning topic is opened. "
                + "This materialises the struct only; the Goodbye conversation-ender flag still needs setting "
                + "explicitly (authoring choice, not a default)."));
        }

        return fills;
    }

    // --- Presence predicates: the SINGLE home for "does this INFO carry the CK-parity subrecord?", consulted by
    //     BOTH the fill path (ApplyInfoDefaults — fills when absent) and the check path (MissingInfoDefaults — flags
    //     when absent) so the two can never drift (Q3). The null read is reliable on the binary overlay the validator
    //     uses: an absent optional subrecord reads null, a materialised one reads non-null (see the field notes on
    //     ApplyInfoDefaults). The getter interface is enough — the fill path passes its mutable record, which is one. ---
    static bool HasFavorLevel(IDialogResponsesGetter info) => info.FavorLevel is not null;   // CNAM
    static bool HasResponseFlags(IDialogResponsesGetter info) => info.Flags is not null;      // ENAM

    /// <summary>The CK-parity subrecords an INFO (DialogResponses) is MISSING — the read-only counterpart of
    /// <see cref="ApplyInfoDefaults"/>, for the on-demand dialogue validator. Shares the exact presence predicates the
    /// fill path uses, so "the create tool populates it" and "the validator flags its absence" can never disagree.
    /// Empty when the INFO already carries both subrecords (the well-formed case, which every CK-/xEdit-authored INFO
    /// is — this only ever fires on a bare-authored record). Reads only the getter; NEVER mutates.</summary>
    public static IReadOnlyList<CkParityGap> MissingInfoDefaults(IDialogResponsesGetter info)
    {
        var gaps = new List<CkParityGap>(2);

        if (!HasFavorLevel(info))
            gaps.Add(new CkParityGap("CNAM (FavorLevel)",
                "every CK-authored INFO carries the CNAM (FavorLevel) subrecord; an INFO missing it crashes the "
                + "Creation Kit the moment its owning topic is opened in the dialogue editor (the game itself tolerates "
                + "it). houseCARL's create tools auto-fill it — set FavorLevel (e.g. None) to populate it."));

        if (!HasResponseFlags(info))
            gaps.Add(new CkParityGap("ENAM (response Flags)",
                "every CK-authored INFO carries the ENAM (response flags + reset-hours) subrecord; an INFO missing it "
                + "crashes the Creation Kit when its owning topic is opened. houseCARL's create tools auto-fill it — "
                + "set the response Flags to populate it."));

        return gaps;
    }

    /// <summary>DLVW (DialogView) CK-parity defaults — the DNAM and ENAM byte subrecords. Both are nullable
    /// (MemorySlice&lt;byte&gt;?) and omitted by Mutagen when unset; a CK-authored DialogView always carries
    /// DNAM = 0x00 and ENAM = 0x00000000, and a bare DLVW (together with BNAM-less topics) crashes the CK's Dialogue
    /// Views editor (a FlowchartX64 null-deref; Heisen §3 gap-3b). Non-override: fills only the byte fields the author
    /// left null. Returns the fills applied.</summary>
    public static IReadOnlyList<CkParityFill> ApplyViewDefaults(IDialogView view)
    {
        var fills = new List<CkParityFill>(2);

        if (!HasDnam(view))
        {
            view.DNAM = Convert.FromHexString(ViewDnamHex);
            fills.Add(new CkParityFill(
                $"DNAM subrecord auto-set to {ViewDnamHex}",
                $"0x{ViewDnamHex} — every CK-authored DialogView carries the DNAM byte subrecord; a bare DLVW (with "
                + "BNAM-less topics) crashes the Creation Kit's Dialogue Views editor. CK-parity default-populate."));
        }

        if (!HasEnam(view))
        {
            view.ENAM = Convert.FromHexString(ViewEnamHex);
            fills.Add(new CkParityFill(
                $"ENAM subrecord auto-set to {ViewEnamHex}",
                $"0x{ViewEnamHex} — every CK-authored DialogView carries the ENAM byte subrecord; pairs with DNAM for "
                + "Creation Kit Dialogue Views parity. CK-parity default-populate."));
        }

        return fills;
    }

    // --- DLVW presence predicates: the single home for "does this DialogView carry the byte subrecord?",
    //     consulted by BOTH ApplyViewDefaults (fills when absent) and MissingViewDefaults (flags when absent) —
    //     the same shared-source discipline as the INFO predicates above (Q3, no drift). ---
    static bool HasDnam(IDialogViewGetter view) => view.DNAM is not null;   // DNAM
    static bool HasEnam(IDialogViewGetter view) => view.ENAM is not null;   // ENAM

    /// <summary>The CK-parity subrecords a DLVW (DialogView) is MISSING — the read-only counterpart of
    /// <see cref="ApplyViewDefaults"/>, for the on-demand dialogue validator's DialogView input. Shares the exact
    /// presence predicates the fill path uses, so fill and check can never disagree. Empty when the view carries
    /// both byte subrecords (every CK-authored view does). Reads only the getter; NEVER mutates.</summary>
    public static IReadOnlyList<CkParityGap> MissingViewDefaults(IDialogViewGetter view)
    {
        var gaps = new List<CkParityGap>(2);

        // The remedy renders a WHOLE op element, so it carries every member the op REQUIRES — formid included, and
        // the formid of the view actually being reported rather than a placeholder. #468 round 1 caught the first
        // form of this: the 1.x text was prose shorthand over a tool that took formid as its own argument
        // (housecarl_set_field DNAM=00), and respelling it as an ops= literal inherited the shorthand while
        // dropping the member — a caller pasting it got "ops[0]: formid is required." instead of the fix.
        if (!HasDnam(view))
            gaps.Add(new CkParityGap("DNAM",
                $"every CK-authored DialogView carries the DNAM byte subrecord (0x{ViewDnamHex}); a bare DLVW (with "
                + "BNAM-less topics) crashes the Creation Kit's Dialogue Views editor (FlowchartX64 null-deref — the "
                + "game itself tolerates it). houseCARL's create tools auto-fill it — housecarl_apply ops=[{formid:'"
                + view.FormKey + "', field_path:'DNAM', value:'" + ViewDnamHex + "'}] to populate it."));

        if (!HasEnam(view))
            gaps.Add(new CkParityGap("ENAM",
                $"every CK-authored DialogView carries the ENAM byte subrecord (0x{ViewEnamHex}), pairing with DNAM "
                + "for Creation Kit Dialogue Views parity; a bare DLVW crashes the CK's Dialogue Views editor (the "
                + "game itself tolerates it). houseCARL's create tools auto-fill it — housecarl_apply ops=[{formid:'"
                + view.FormKey + "', field_path:'ENAM', value:'" + ViewEnamHex + "'}] to populate it."));

        return gaps;
    }

    // ==================================================================================================
    //  S2 — the byte-only tier. No confirmed crash; a byte mismatch vs a CK-authored record. Same asymmetry,
    //  same #131 invariants (non-override · never silent · by-construction). Values byte-verified against
    //  CK-authored vanilla records (2026-07-04) — see the header SCOPE (S2) block.
    // ==================================================================================================

    /// <summary>DIAL Priority (PNAM) CK seed value — 50. The dominant value on vanilla Custom topics; the CK's seed
    /// for an untouched topic (authors raise/lower it to order competing lines). Pinned by the guard.</summary>
    public const float TopicPrioritySeed = 50f;

    /// <summary>DLBR Category (TNAM) CK-parity default — Player. Both the enum's zero-value (a fresh CK branch's
    /// default) and the value 3059/3061 vanilla DialogBranches carry (all Flags; big-three masters, 2026-07-04);
    /// Command appears on just 2 vanilla branches, both deliberately authored. Pinned by the guard.</summary>
    public const DialogBranch.CategoryType BranchCategoryDefault = DialogBranch.CategoryType.Player;

    /// <summary>DLBR Flags (DNAM) CK-parity default — 0 (none of TopLevel/Blocking/Exclusive set), the CK's value for
    /// a branch whose flag checkboxes the author never ticked. UNLIKE <see cref="BranchCategoryDefault"/>, the
    /// zero-value is NOT the dominant vanilla value — and that divergence is the whole point, so it is stated here
    /// rather than left to look like an oversight. Over the 3061 DialogBranches defined in Skyrim.esm (measured
    /// 2026-07-17): ALL 3061 carry DNAM (none omit it — the CK writes it unconditionally, the #131 asymmetry);
    /// 2117 are TopLevel, 728 Blocking, 30 Exclusive, and 203 carry it as exactly 0. Those 203 are the proof this
    /// default is ATTESTED, not invented: a present-but-zero DNAM is a normal CK-authored shape. The 2117 TopLevel
    /// branches are branches an author deliberately ticked top-level — an authored choice non-override preserves,
    /// exactly as Category=Command is preserved above. Pinned by the guard.</summary>
    public const DialogBranch.Flag BranchFlagsDefault = (DialogBranch.Flag)0;

    /// <summary>DLBR (DialogBranch) CK-parity default — the Category (TNAM) enum, nullable and omitted by Mutagen when
    /// unset; a CK-authored DialogBranch always carries it. Fill Player (the near-universal value + the enum's
    /// zero-value) UNCONDITIONALLY when the author left it null. A Command branch (a bribe/intimidate speech-challenge
    /// — the only 2 vanilla cases) is a deliberate authored choice that sets Category=Command explicitly, so
    /// non-override leaves it untouched (Aaron-decided 2026-07-04 after the vanilla data falsified the earlier
    /// TopLevel-gated plan: TopLevel doesn't distinguish Player from Command — both Command cases are TopLevel — and
    /// non-TopLevel branches are reliably Player).
    ///
    /// ALSO fills the Flags (DNAM) enum — same nullable-and-omitted shape, but S3 (in-game behavior, #212): an absent
    /// DNAM is read by the engine as TopLevel, publishing a branch the author never marked top-level into the player's
    /// dialogue menu. Fill 0 (no flags) when the author left it null; an explicit Flags — INCLUDING an explicit
    /// TopLevel — always wins.
    ///
    /// Returns the fills applied (empty when the author set both).</summary>
    public static IReadOnlyList<CkParityFill> ApplyBranchDefaults(IDialogBranch branch)
    {
        var fills = new List<CkParityFill>(2);

        if (!HasCategory(branch))
        {
            branch.Category = BranchCategoryDefault;
            fills.Add(new CkParityFill(
                $"Category (TNAM subrecord) auto-set to {BranchCategoryDefault}",
                $"{BranchCategoryDefault} — every CK-authored DialogBranch carries the TNAM (Category) subrecord; "
                + "Player is both the enum's zero-value (a fresh CK branch's default) and the value ~all vanilla "
                + "branches carry across every Flags combination. A Command branch (a bribe/intimidate speech-challenge) "
                + "is a deliberate authored case that sets Category=Command explicitly; non-override leaves that "
                + "untouched. CK-parity default-populate, in-model (#131 pattern)."));
        }

        if (!HasFlags(branch))
        {
            branch.Flags = BranchFlagsDefault;
            fills.Add(new CkParityFill(
                "Flags (DNAM subrecord) auto-set to 0 (no flags — not top-level, not blocking, not exclusive)",
                "0 — every CK-authored DialogBranch carries the DNAM (Flags) subrecord, and a branch whose flag "
                + "checkboxes the author never ticked carries it as 0 (203 of Skyrim.esm's 3061 branches do exactly "
                + "that). A branch written WITHOUT DNAM is read by the engine as TopLevel, so its topics are published "
                + "to the player's dialogue menu — a nameless Say()-only topic shows up as a selectable \"...\". This "
                + "materialises the subrecord only; TopLevel/Blocking/Exclusive stay an explicit authoring choice a "
                + "0-fill does NOT set, and an explicit Flags always wins. CK-parity default-populate, in-model."));
        }

        return fills;
    }

    // --- DLBR presence predicates: the single home for "does this DialogBranch carry the CK-parity subrecord?",
    //     consulted by BOTH ApplyBranchDefaults (fills when absent) and MissingBranchDefaults (flags when absent) —
    //     no drift (Q3). Flags is an enum-typed nullable (DialogBranch.Flag?), so the null read distinguishes "author
    //     set no flags" (null → fill 0) from "author set 0 explicitly" (non-null → keep) — the same is-null signal
    //     every field here uses EXCEPT the non-nullable DIAL Priority (see ApplyTopicPriorityDefault). ---
    static bool HasCategory(IDialogBranchGetter branch) => branch.Category is not null;   // TNAM
    static bool HasFlags(IDialogBranchGetter branch) => branch.Flags is not null;         // DNAM

    /// <summary>The CK-parity subrecord a DLBR (DialogBranch) is MISSING — the read-only counterpart of
    /// <see cref="ApplyBranchDefaults"/>, for the on-demand dialogue validator's DialogBranch input. Shares the exact
    /// presence predicates the fill path uses, so fill and check can never disagree. The two gaps sit in DIFFERENT
    /// tiers: a missing TNAM is S2 (byte-parity only — a structural mismatch vs a CK-authored branch, not a known
    /// failure), while a missing DNAM is S3 (#212) — the engine reads it as TopLevel and publishes the branch to the
    /// player's dialogue menu, a real in-game defect the byte-valid output hides. Empty when the branch carries both.
    /// Reads only the getter; NEVER mutates.</summary>
    public static IReadOnlyList<CkParityGap> MissingBranchDefaults(IDialogBranchGetter branch)
    {
        var gaps = new List<CkParityGap>(2);

        if (!HasCategory(branch))
            gaps.Add(new CkParityGap("TNAM (Category)",
                "every CK-authored DialogBranch carries the TNAM (Category) subrecord (~all vanilla branches carry "
                + "Player); a branch missing it differs structurally from a CK-authored one (byte-parity only — no "
                + "confirmed crash). houseCARL's create tools auto-fill it — set Category (e.g. Player) to populate it."));

        if (!HasFlags(branch))
            gaps.Add(new CkParityGap("DNAM (Flags)",
                "every CK-authored DialogBranch carries the DNAM (Flags) subrecord — as 0 when no flag is ticked (203 "
                + "of Skyrim.esm's 3061 branches). A branch missing it is read by the ENGINE as TopLevel, so its topics "
                + "are published to the player's dialogue menu — a nameless Say()-only topic renders as a selectable "
                + "\"...\". This is an in-game defect, not a byte-parity nit: the record is byte-valid and only "
                + "misbehaves once loaded. houseCARL's create tools auto-fill it — set Flags (0 for a hidden branch, "
                + "TopLevel for a player-facing one) to populate it."));

        return gaps;
    }

    /// <summary>DIAL (DialogTopic) Priority (PNAM) CK seed. UNLIKE every other field here, Priority is a NON-NULLABLE
    /// float (defaults to 0), so there is no is-null signal for "the author left it unset" — the create path decides
    /// that from the author's OP LIST and passes it in as <paramref name="authorSetPriority"/>. Fill the CK seed
    /// (50) ONLY when the author never touched Priority; an explicit value — including 0 — always wins (non-override).
    /// Returns the single fill applied, or null when Priority was author-set (nothing to report).</summary>
    public static CkParityFill? ApplyTopicPriorityDefault(IDialogTopic topic, bool authorSetPriority)
    {
        if (authorSetPriority) return null;                 // author set Priority (even to 0) — non-override, no fill
        topic.Priority = TopicPrioritySeed;                 // was 0 (the non-nullable default); seed to the CK's 50
        return new CkParityFill(
            $"Priority (PNAM subrecord) auto-set to {TopicPrioritySeed:0} (CK seed default)",
            $"{TopicPrioritySeed:0} — a CK-authored DialogTopic always writes PNAM (Priority), and 50 is the CK's seed "
            + "for an untouched topic (the dominant value on vanilla Custom topics; authors raise/lower it to order "
            + "competing lines). Priority is a non-nullable float, so this seeds 50 only when the author set no "
            + "Priority at all — an explicit value, including 0, always wins. CK-parity seed.");
    }

    /// <summary>QUST (Quest) CK-parity defaults — the NextAliasID (ANAM) counter, each objective's Flags (FNAM), and
    /// each alias's Flags (FNAM) + VoiceTypes (VTCK), all nullable and omitted by Mutagen when unset; a CK-authored
    /// Quest carries every one. NON-OVERRIDE throughout.
    ///
    /// NextAliasID (ANAM): the next alias ID the CK would hand out. For a FRESHLY-created quest (no deletion history)
    /// that is max(existing alias ID)+1, or 0 for an alias-less quest (160 vanilla alias-less quests read 0). NOTE the
    /// value is create-lane-correct only: on an EDITED quest the CK keeps ANAM as a monotonic high-water mark that can
    /// exceed max+1 (a deleted high-ID alias strands it — e.g. vanilla CRTwinsPostQuest has aliases {0,1} but ANAM=3),
    /// but no alias can be deleted inside a single create call, so max+1 is exact here. This does NOT reconstruct a
    /// general quest's ANAM — it seeds a new one.
    ///
    /// QuestObjective.Flags (FNAM): materialise 0 (no flags) on each objective the author left null — the value every
    /// vanilla objective carries. The sole flag (OrWithPrevious) stays an explicit authoring choice a 0-fill does NOT
    /// set (like Goodbye on the INFO Flags struct).
    ///
    /// QuestAlias.Flags (FNAM) + VoiceTypes (VTCK): the CK writes both on a REFERENCE alias, even zero/null
    /// (HCBR-2026-07-10 — a byte-audit vs a CK-authored sibling whose every alias carries FNAM + VTCK; the reported
    /// shape is a reference alias, block ALST/ALID/FNAM/ALFR/VTCK/ALED). Mutagen omits each when unset, so a composed
    /// QuestAlias that sets neither writes ALST/ALID/ALFR/ALED (FNAM + VTCK absent) instead. Materialise Flags→0 (no
    /// flags — named alias flags like Optional/Essential stay an explicit authoring choice a 0-fill does NOT set) on
    /// EVERY alias (FNAM is carried by both reference and location aliases), and VoiceTypes→the null link (0x00000000,
    /// a present-but-empty VTCK) only on a REFERENCE alias (voice types are an actor/reference concept — a Location
    /// alias resolves to a place, not an actor; scoping VTCK to reference aliases covers the report without over-adding
    /// a subrecord CK may omit on location aliases, which would also make MissingQuestDefaults false-warn on real
    /// CK-authored location aliases). Same #131 asymmetry as the objective FNAM right above; S2 (byte-parity — no
    /// confirmed crash: the report did NOT launch-test the omitted shape, so this closes the CK divergence, not a
    /// proven CTD). Returns every fill applied (ANAM + one per materialised objective + up to two per materialised alias).</summary>
    public static IReadOnlyList<CkParityFill> ApplyQuestDefaults(IQuest quest)
    {
        var fills = new List<CkParityFill>();

        if (!HasNextAliasID(quest))
        {
            bool hasAliases = quest.Aliases.Count > 0;
            uint next = hasAliases ? quest.Aliases.Max(a => a.ID) + 1u : 0u;
            quest.NextAliasID = next;
            fills.Add(new CkParityFill(
                $"NextAliasID (ANAM subrecord) auto-set to {next}",
                $"{next} — every CK-authored Quest carries the ANAM (next-alias-ID) subrecord, seeded to the next alias "
                + $"ID the CK would hand out ({(hasAliases ? $"max of the {quest.Aliases.Count} alias ID(s) + 1" : "0 for an alias-less quest")}). "
                + "Non-override: fills only when the author set no NextAliasID. CK-parity default-populate."));
        }

        int idx = 0;
        foreach (var objective in quest.Objectives)
        {
            if (!HasObjectiveFlags(objective))
            {
                objective.Flags = default(QuestObjective.Flag);   // (QuestObjective.Flag)0 — no flags set
                fills.Add(new CkParityFill(
                    $"Objectives[{idx}] (Index {objective.Index}) Flags (FNAM subrecord) auto-set to 0 (no flags)",
                    "0 — every CK-authored quest objective carries the FNAM (flags) subrecord, and vanilla objectives "
                    + "carry 0. This materialises the subrecord only; the OrWithPrevious flag stays an explicit "
                    + "authoring choice. CK-parity default-populate."));
            }
            idx++;
        }

        // QuestAlias FNAM (Flags) + VTCK (VoiceTypes): the CK writes both on a reference alias (HCBR-2026-07-10).
        // Materialise each the author left null — Flags→0 on EVERY alias (both alias types carry FNAM); VoiceTypes→the
        // null link only on a REFERENCE alias (VTCK is an actor/reference concept — a Location alias resolves to a
        // place, and CK may omit VTCK there; scoping avoids over-adding + a MissingQuestDefaults false-warn on real
        // CK-authored location aliases). Non-override throughout.
        int aidx = 0;
        foreach (var alias in quest.Aliases)
        {
            if (!HasAliasFlags(alias))
            {
                alias.Flags = default(QuestAlias.Flag);           // (QuestAlias.Flag)0 — no flags set
                fills.Add(new CkParityFill(
                    $"Aliases[{aidx}] (ID {alias.ID}) Flags (FNAM subrecord) auto-set to 0 (no flags)",
                    "0 — every CK-authored quest alias carries the FNAM (flags) subrecord, value 0 when no alias flags "
                    + "are set. This materialises the subrecord only; alias flags (Optional, Essential, …) stay an "
                    + "explicit authoring choice. CK-parity default-populate."));
            }
            if (IsReferenceAlias(alias) && !HasAliasVoiceTypes(alias))
            {
                alias.VoiceTypes.SetTo(FormKey.Null);             // present-but-null VTCK (0x00000000), the CK's empty voice-type link
                fills.Add(new CkParityFill(
                    $"Aliases[{aidx}] (ID {alias.ID}) VoiceTypes (VTCK subrecord) auto-set to the null link (0x00000000)",
                    "null link — every CK-authored quest REFERENCE alias carries the VTCK (voice-types) subrecord; an "
                    + "alias with no voice type carries it as a null link (0x00000000), not omitted. This materialises "
                    + "the empty subrecord only; a real voice-type list stays an explicit authoring choice. CK-parity "
                    + "default-populate."));
            }
            aidx++;
        }

        return fills;
    }

    // --- QUST presence predicates: the single home for "does this Quest carry the CK-parity subrecord?", consulted
    //     by BOTH ApplyQuestDefaults (fills when absent) and MissingQuestDefaults (flags when absent) — no drift (Q3).
    //     The alias VTCK probe reads FormKeyNullable (not the rendered value): an ABSENT VTCK and a present null-link
    //     both render "(null link)" (ReadEngine.NullLinkNote — a null FormKey), so only FormKeyNullable distinguishes
    //     "author set no voice types" (null → fill) from "author set the null link / a real list" (non-null → keep). ---
    static bool HasNextAliasID(IQuestGetter quest) => quest.NextAliasID is not null;                 // ANAM
    static bool HasObjectiveFlags(IQuestObjectiveGetter objective) => objective.Flags is not null;   // FNAM
    static bool HasAliasFlags(IQuestAliasGetter alias) => alias.Flags is not null;                    // FNAM (alias)
    static bool HasAliasVoiceTypes(IQuestAliasGetter alias) => alias.VoiceTypes.FormKeyNullable is not null;  // VTCK
    // VTCK is scoped to reference aliases: a Location alias resolves to a place, not an actor, so voice types don't
    // apply and CK may omit VTCK there — the same gate guards both the fill and the gap so they can't drift.
    static bool IsReferenceAlias(IQuestAliasGetter alias) => alias.Type == QuestAlias.TypeEnum.Reference;

    /// <summary>The CK-parity subrecords a QUST (Quest) is MISSING — the read-only counterpart of
    /// <see cref="ApplyQuestDefaults"/>, for the on-demand dialogue validator's QUEST input (checked ONCE per quest,
    /// never per topic). Shares the exact presence predicates the fill path uses, so fill and check can never
    /// disagree. S2 (byte-parity only — no confirmed crash). PRESENCE only: the fill's max-alias-id+1 derivation is
    /// create-lane-correct (an edited quest's ANAM is a CK high-water mark that can legitimately exceed max+1), so
    /// this checks "is ANAM absent?", never judges its VALUE. Empty when ANAM is present and every objective carries
    /// FNAM and every alias carries FNAM + VTCK. Reads only the getter; NEVER mutates.</summary>
    public static IReadOnlyList<CkParityGap> MissingQuestDefaults(IQuestGetter quest)
    {
        var gaps = new List<CkParityGap>();

        if (!HasNextAliasID(quest))
            gaps.Add(new CkParityGap("ANAM (NextAliasID)",
                "every CK-authored Quest carries the ANAM (next-alias-ID counter) subrecord; a quest missing it "
                + "differs structurally from a CK-authored one (byte-parity only — no confirmed crash). houseCARL's "
                + "create tools auto-fill it — set NextAliasID (the next alias ID the CK would hand out) to populate it."));

        int idx = 0;
        foreach (var objective in quest.Objectives)
        {
            if (!HasObjectiveFlags(objective))
                gaps.Add(new CkParityGap($"Objectives[{idx}] (Index {objective.Index}) FNAM (Flags)",
                    "every CK-authored quest objective carries the FNAM (flags) subrecord (vanilla objectives carry 0); "
                    + "an objective missing it differs structurally from a CK-authored one (byte-parity only — no "
                    + "confirmed crash). houseCARL's create tools auto-fill it — set the objective's Flags to populate it."));
            idx++;
        }

        int aidx = 0;
        foreach (var alias in quest.Aliases)
        {
            if (!HasAliasFlags(alias))
                gaps.Add(new CkParityGap($"Aliases[{aidx}] (ID {alias.ID}) FNAM (Flags)",
                    "every CK-authored quest alias carries the FNAM (flags) subrecord (value 0 when no alias flags are "
                    + "set); an alias missing it differs structurally from a CK-authored one (byte-parity only — no "
                    + "confirmed crash). houseCARL's create tools auto-fill it — set the alias's Flags to populate it."));
            if (IsReferenceAlias(alias) && !HasAliasVoiceTypes(alias))
                gaps.Add(new CkParityGap($"Aliases[{aidx}] (ID {alias.ID}) VTCK (VoiceTypes)",
                    "every CK-authored quest REFERENCE alias carries the VTCK (voice-types) subrecord (a null link, "
                    + "0x00000000, when no voice type is set); a reference alias missing it differs structurally from a "
                    + "CK-authored one (byte-parity only — no confirmed crash). houseCARL's create tools auto-fill it — "
                    + "set the alias's VoiceTypes to populate it."));
            aidx++;
        }

        return gaps;
    }
}

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
        // reference INFOs all carry FavorLevel = None). Only fill when the author set no favor level.
        if (info.FavorLevel is null)
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
        if (info.Flags is null)
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

    /// <summary>DLVW (DialogView) CK-parity defaults — the DNAM and ENAM byte subrecords. Both are nullable
    /// (MemorySlice&lt;byte&gt;?) and omitted by Mutagen when unset; a CK-authored DialogView always carries
    /// DNAM = 0x00 and ENAM = 0x00000000, and a bare DLVW (together with BNAM-less topics) crashes the CK's Dialogue
    /// Views editor (a FlowchartX64 null-deref; Heisen §3 gap-3b). Non-override: fills only the byte fields the author
    /// left null. Returns the fills applied.</summary>
    public static IReadOnlyList<CkParityFill> ApplyViewDefaults(IDialogView view)
    {
        var fills = new List<CkParityFill>(2);

        if (view.DNAM is null)
        {
            view.DNAM = Convert.FromHexString(ViewDnamHex);
            fills.Add(new CkParityFill(
                $"DNAM subrecord auto-set to {ViewDnamHex}",
                $"0x{ViewDnamHex} — every CK-authored DialogView carries the DNAM byte subrecord; a bare DLVW (with "
                + "BNAM-less topics) crashes the Creation Kit's Dialogue Views editor. CK-parity default-populate."));
        }

        if (view.ENAM is null)
        {
            view.ENAM = Convert.FromHexString(ViewEnamHex);
            fills.Add(new CkParityFill(
                $"ENAM subrecord auto-set to {ViewEnamHex}",
                $"0x{ViewEnamHex} — every CK-authored DialogView carries the ENAM byte subrecord; pairs with DNAM for "
                + "Creation Kit Dialogue Views parity. CK-parity default-populate."));
        }

        return fills;
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

    /// <summary>DLBR (DialogBranch) CK-parity default — the Category (TNAM) enum, nullable and omitted by Mutagen when
    /// unset; a CK-authored DialogBranch always carries it. Fill Player (the near-universal value + the enum's
    /// zero-value) UNCONDITIONALLY when the author left it null. A Command branch (a bribe/intimidate speech-challenge
    /// — the only 2 vanilla cases) is a deliberate authored choice that sets Category=Command explicitly, so
    /// non-override leaves it untouched (Aaron-decided 2026-07-04 after the vanilla data falsified the earlier
    /// TopLevel-gated plan: TopLevel doesn't distinguish Player from Command — both Command cases are TopLevel — and
    /// non-TopLevel branches are reliably Player). Returns the fills applied (empty when the author set a Category).</summary>
    public static IReadOnlyList<CkParityFill> ApplyBranchDefaults(IDialogBranch branch)
    {
        var fills = new List<CkParityFill>(1);

        if (branch.Category is null)
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

        return fills;
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

    /// <summary>QUST (Quest) CK-parity defaults — the NextAliasID (ANAM) counter and each objective's Flags (FNAM),
    /// both nullable and omitted by Mutagen when unset; a CK-authored Quest carries both. NON-OVERRIDE throughout.
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
    /// set (like Goodbye on the INFO Flags struct). Returns every fill applied (ANAM + one per materialised objective).</summary>
    public static IReadOnlyList<CkParityFill> ApplyQuestDefaults(IQuest quest)
    {
        var fills = new List<CkParityFill>();

        if (quest.NextAliasID is null)
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
            if (objective.Flags is null)
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

        return fills;
    }
}

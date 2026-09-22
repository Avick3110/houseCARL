using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

// DialogueCkParity — the CK-parity default-populate authority for the DIAL/INFO/DLVW family: the nullable fields
// the Creation Kit always emits and Mutagen omits, filled at create time inside the Mutagen model. The invariants,
// the three tiers and the two exceptions are contracts in docs/architecture/dialogue-validation.md. Any further default
// belongs here too — do not fork a parallel path.

/// <summary>One CK-parity field default-populated on create; a record that carried it produces NO fill.</summary>
public readonly record struct CkParityFill(string Label, string Reason);

/// <summary>One CK-parity subrecord a record is MISSING, off the same null test the fill path uses.</summary>
public readonly record struct CkParityGap(string Subrecord, string Detail);

/// <summary>The authority for the CK-parity default-populate fields; the create path calls the per-type
/// <c>Apply…Defaults</c> after the author's edits. The SNAM marker is DialogueSubtype's, not this one's.</summary>
public static class DialogueCkParity
{
    // --- The byte-field defaults, as hex, so there is ONE source of truth (the arrays derive from these). ---
    /// <summary>DLVW DNAM default — one zero byte, as a CK-authored DialogView carries it.</summary>
    public const string ViewDnamHex = "00";
    /// <summary>DLVW ENAM default — four zero bytes, as a CK-authored DialogView carries it.</summary>
    public const string ViewEnamHex = "00000000";

    /// <summary>INFO CK-parity defaults — FavorLevel (CNAM) and the Flags (ENAM) struct, both CK-crash tier.</summary>
    public static IReadOnlyList<CkParityFill> ApplyInfoDefaults(IDialogResponses info)
    {
        var fills = new List<CkParityFill>(2);

        // FavorLevel (CNAM): None is the CK default; the absence test is the shared HasFavorLevel below.
        if (!HasFavorLevel(info))
        {
            info.FavorLevel = FavorLevel.None;
            fills.Add(new CkParityFill(
                "FavorLevel (CNAM subrecord) auto-set to None",
                "None — every CK-authored INFO carries the CNAM (FavorLevel) subrecord; an INFO created without it "
                + "crashes the Creation Kit when its owning topic is opened in the dialogue editor (the game tolerates "
                + "it). CK-parity default-populate, in-model (#131 pattern)."));
        }

        // Flags (ENAM): reads null when unset, and a fresh struct is the CK's empty ENAM. Goodbye stays explicit.
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

    // --- Presence predicates: the SINGLE home, read by both the fill path and the check path. ---
    static bool HasFavorLevel(IDialogResponsesGetter info) => info.FavorLevel is not null;   // CNAM
    static bool HasResponseFlags(IDialogResponsesGetter info) => info.Flags is not null;      // ENAM

    /// <summary>The CK-parity subrecords an INFO is MISSING, off the same predicates; never mutates.</summary>
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

    /// <summary>DLVW CK-parity defaults — DNAM and ENAM, whose absence crashes the CK's Dialogue Views editor.</summary>
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

    // --- DLVW presence predicates: the single home, read by both the fill path and the check path. ---
    static bool HasDnam(IDialogViewGetter view) => view.DNAM is not null;   // DNAM
    static bool HasEnam(IDialogViewGetter view) => view.ENAM is not null;   // ENAM

    /// <summary>The CK-parity subrecords a DLVW is MISSING, off the same predicates; never mutates.</summary>
    public static IReadOnlyList<CkParityGap> MissingViewDefaults(IDialogViewGetter view)
    {
        var gaps = new List<CkParityGap>(2);

        // The remedy renders a WHOLE op element, so it carries the reported view's own formid, not a placeholder.
        if (!HasDnam(view))
            gaps.Add(new CkParityGap("DNAM",
                $"every CK-authored DialogView carries the DNAM byte subrecord (0x{ViewDnamHex}); a bare DLVW (with "
                + "BNAM-less topics) crashes the Creation Kit's Dialogue Views editor (FlowchartX64 null-deref — the "
                + "game itself tolerates it). houseCARL's create tools auto-fill it — " + ToolNames.Apply + " ops=[{formid:'"
                + view.FormKey + "', field_path:'DNAM', value:'" + ViewDnamHex + "'}] to populate it."));

        if (!HasEnam(view))
            gaps.Add(new CkParityGap("ENAM",
                $"every CK-authored DialogView carries the ENAM byte subrecord (0x{ViewEnamHex}), pairing with DNAM "
                + "for Creation Kit Dialogue Views parity; a bare DLVW crashes the CK's Dialogue Views editor (the "
                + "game itself tolerates it). houseCARL's create tools auto-fill it — " + ToolNames.Apply + " ops=[{formid:'"
                + view.FormKey + "', field_path:'ENAM', value:'" + ViewEnamHex + "'}] to populate it."));

        return gaps;
    }

    // ---- The byte-parity tier: no confirmed crash, a byte mismatch vs a CK-authored record ----

    /// <summary>DIAL Priority (PNAM) CK seed value — 50, the CK's seed for an untouched topic.</summary>
    public const float TopicPrioritySeed = 50f;

    /// <summary>DLBR Category (TNAM) CK-parity default — Player, the enum's zero-value and ~all of vanilla.</summary>
    public const DialogBranch.CategoryType BranchCategoryDefault = DialogBranch.CategoryType.Player;

    /// <summary>DLBR CK-parity default — Category (TNAM) filled with Player, deliberately not gated on TopLevel.
    /// Does NOT touch Flags (DNAM), which has no honest default — see <see cref="BranchFlagsRefusal"/>.</summary>
    public static IReadOnlyList<CkParityFill> ApplyBranchDefaults(IDialogBranch branch)
    {
        var fills = new List<CkParityFill>(1);

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

        return fills;
    }

    /// <summary>The create path's DLBR Flags (DNAM) pre-flight refusal, or null when the author set it: there is
    /// no honest default, and a passed value always wins, an explicit 0 included. Called from CreateRecords'
    /// Phase-1, where <paramref name="authorSetFlags"/> is read off the spec's edits.</summary>
    public static string? BranchFlagsRefusal(bool authorSetFlags, string editorId) =>
        authorSetFlags ? null
            : $"DialogBranch '{editorId}' needs Flags: pass TopLevel for a menu entry the player can pick, or 0 for a "
              + "scripted Say() topic that must stay hidden.";

    /// <summary>The same refusal asked of a BUILT branch; an explicitly set 0 reads non-null and is not it.</summary>
    public static string? BranchFlagsRefusal(IDialogBranchGetter branch, string editorId) =>
        BranchFlagsRefusal(HasFlags(branch), editorId);

    // --- DLBR presence predicates: the single home; the null read separates no flags from an explicit 0. ---
    static bool HasCategory(IDialogBranchGetter branch) => branch.Category is not null;   // TNAM
    static bool HasFlags(IDialogBranchGetter branch) => branch.Flags is not null;         // DNAM

    /// <summary>The CK-parity subrecords a DLBR is MISSING, off the same predicates. It runs on an EXISTING branch
    /// no create call can refuse, so a missing DNAM is reported here; the two gaps sit in different tiers.</summary>
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
                + "misbehaves once loaded. Set Flags to populate it: TopLevel for a menu entry the player can pick, or "
                + "0 for a scripted Say() topic that must stay hidden. houseCARL's create tools do not guess — they "
                + "refuse a new branch that passes no Flags."));

        return gaps;
    }

    /// <summary>DIAL Priority (PNAM) CK seed. Priority is NON-NULLABLE, so the create path passes
    /// <paramref name="authorSetPriority"/> off its op list instead; an explicit value, 0 included, always wins.</summary>
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

    /// <summary>QUST CK-parity defaults — NextAliasID (ANAM), each objective's and alias's Flags (FNAM), and each
    /// REFERENCE alias's VoiceTypes (VTCK). The ANAM value is create-lane-correct only: max(alias ID)+1 is exact
    /// because no alias can be deleted inside one create call, but an EDITED quest's is a CK high-water mark. A
    /// 0-fill materialises the subrecord only, and VTCK is scoped to REFERENCE aliases.</summary>
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

        // Flags→0 on EVERY alias (both types carry FNAM); VoiceTypes→the null link on a REFERENCE alias only.
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

    // --- QUST presence predicates: the single home; the VTCK test reads FormKeyNullable, not the render. ---
    static bool HasNextAliasID(IQuestGetter quest) => quest.NextAliasID is not null;                 // ANAM
    static bool HasObjectiveFlags(IQuestObjectiveGetter objective) => objective.Flags is not null;   // FNAM
    static bool HasAliasFlags(IQuestAliasGetter alias) => alias.Flags is not null;                    // FNAM (alias)
    static bool HasAliasVoiceTypes(IQuestAliasGetter alias) => alias.VoiceTypes.FormKeyNullable is not null;  // VTCK
    // VTCK is scoped to reference aliases; the same gate guards both the fill and the gap.
    static bool IsReferenceAlias(IQuestAliasGetter alias) => alias.Type == QuestAlias.TypeEnum.Reference;

    /// <summary>The CK-parity subrecords a QUST is MISSING, checked ONCE per quest input. PRESENCE only: it asks
    /// whether ANAM is absent and never judges its value.</summary>
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

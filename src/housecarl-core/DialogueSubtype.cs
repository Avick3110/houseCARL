using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

// ======================================================================
//  DialogueSubtype — the DialogTopic subtype MARKER (SNAM) authority.
//
//  THE BUG THIS EXISTS FOR (issue #131, community report by matashina):
//  A DialogTopic (DIAL) carries its subtype in TWO independent places that must agree —
//    • DATA\Subtype : a numeric enum (Mutagen's DialogTopic.SubtypeEnum; the game index, e.g. Hello = 79)
//    • SNAM         : a 4-character text marker (RecordType, e.g. "HELO")
//  The engine buckets topics by the SNAM MARKER, so a blank marker (0000) sends it walking an invalid
//  topic list → a GUARANTEED access-violation CTD on load. Mutagen writes SNAM verbatim from the field and
//  does NOT derive it from Subtype (confirmed by decompiling DialogTopicBinaryWriteTranslation), so a create
//  that sets only Subtype leaves SNAM at 0000 — a byte-valid plugin that always crashes. This is one of the
//  Creation-Kit bookkeeping jobs a raw insert skips (see the dialogue-authoring skill).
//
//  WHY THE TABLE IS SOURCED FROM xEdit, NOT DERIVED:
//  The name↔marker mapping is NOT a blind echo (Hello→HELO, Goodbye→GBYE, but Custom→CUST) and Mutagen does
//  NOT model it. Nor can it be scraped from vanilla: Bethesda's own DATA\Subtype numbers are unreliable
//  (many HELO topics ship with DATA≠79), which is exactly why xEdit marks DATA\Subtype cpIgnore and treats
//  SNAM as the REQUIRED master field (it even defaults SNAM to CUST). So the authoritative index→marker table
//  below is taken from xEdit's own record-format definition — the community's canonical spec — and
//  cross-checked against the clean vanilla signals (Custom/Scene/Hello/Goodbye/Idle).
//
//  PROVENANCE (by construction, not by hand): the table is the join of the two enums in xEdit's
//  Core/wbDefinitionsTES5.pas (repo TES5Edit/TES5Edit, branch dev-4.1.6):
//    • the DATA\Subtype index→name wbEnum ({0}'Custom' … {102}'LeaveWaterBreath'), and
//    • wbSubtypeNamesEnum (4-char signature → name),
//  matched by subtype NAME. Mutagen's DialogTopic.SubtypeEnum integer values equal these DATA indices
//  (verified: Scene=14, AlertIdle=55, Goodbye=78, Hello=79, Idle=94), so the lookup key is simply
//  (int)DialogTopic.Subtype. The dialogue-subtype-marker-guard (housecarl-generator) pins the anchors and
//  the create→read-back behaviour so this can't silently drift.
// ======================================================================

/// <summary>The authority for a DialogTopic's SNAM subtype marker — the 4-char tag the game buckets topics by.
/// A blank marker is a guaranteed load CTD (issue #131), so the create path auto-fills it from the topic's
/// <c>Subtype</c> and the on-demand validator escalates a blank one to a Problem. Both read the marker through
/// THIS type so the write side and the check side can never disagree.</summary>
public static class DialogueSubtype
{
    /// <summary>Subtype index (== <c>(int)DialogTopic.Subtype</c> == xEdit DATA\Subtype index) → its 4-char SNAM
    /// marker, from xEdit's canonical definition (see file header for provenance). Contiguous 0..102; every entry
    /// is a real 4-char signature Bethesda uses. <c>HIT_</c> keeps its trailing underscore (the true signature).</summary>
    static readonly string[] MarkerByIndex =
    {
        /*   0 */ "CUST",   // Custom
        /*   1 */ "PFGT",   // ForceGreet
        /*   2 */ "RUMO",   // Rumors
        /*   3 */ "FVDL",   // Custom?
        /*   4 */ "INTI",   // Intimidate
        /*   5 */ "FLAT",   // Flatter
        /*   6 */ "BRIB",   // Bribe
        /*   7 */ "ASKG",   // Ask Gift
        /*   8 */ "GIFF",   // Gift
        /*   9 */ "ASKF",   // Ask Favor
        /*  10 */ "FAVO",   // Favor
        /*  11 */ "SHRE",   // Show Relationships
        /*  12 */ "FOLL",   // Follow
        /*  13 */ "FRJT",   // Reject
        /*  14 */ "SCEN",   // Scene
        /*  15 */ "SHOW",   // Show
        /*  16 */ "AGRE",   // Agree
        /*  17 */ "REFU",   // Refuse
        /*  18 */ "FEXT",   // ExitFavorState
        /*  19 */ "MREF",   // MoralRefusal
        /*  20 */ "FMLX",   // FlyingMountLand
        /*  21 */ "FMXL",   // FlyingMountCancelLand
        /*  22 */ "FMAT",   // FlyingMountAcceptTarget
        /*  23 */ "FMRT",   // FlyingMountRejectTarget
        /*  24 */ "FMNT",   // FlyingMountNoTarget
        /*  25 */ "FMDR",   // FlyingMountDestinationReached
        /*  26 */ "ATCK",   // Attack
        /*  27 */ "POAT",   // PowerAttack
        /*  28 */ "BASH",   // Bash
        /*  29 */ "HIT_",   // Hit
        /*  30 */ "FLEE",   // Flee
        /*  31 */ "BLED",   // Bleedout
        /*  32 */ "AVTH",   // AvoidThreat
        /*  33 */ "DETH",   // Death
        /*  34 */ "GRST",   // GroupStrategy
        /*  35 */ "BLOC",   // Block
        /*  36 */ "TAUT",   // Taunt
        /*  37 */ "ALKL",   // AllyKilled
        /*  38 */ "STEA",   // Steal
        /*  39 */ "YIEL",   // Yield
        /*  40 */ "ACYI",   // AcceptYield
        /*  41 */ "PICC",   // PickpocketCombat
        /*  42 */ "ASSA",   // Assault
        /*  43 */ "MURD",   // Murder
        /*  44 */ "ASNC",   // AssaultNC
        /*  45 */ "MUNC",   // MurderNC
        /*  46 */ "PICN",   // PickpocketNC
        /*  47 */ "STFN",   // StealFromNC
        /*  48 */ "TRAN",   // TrespassAgainstNC
        /*  49 */ "TRES",   // Trespass
        /*  50 */ "WTCR",   // WereTransformCrime
        /*  51 */ "VPSS",   // VoicePowerStartShort
        /*  52 */ "VPSL",   // VoicePowerStartLong
        /*  53 */ "VPES",   // VoicePowerEndShort
        /*  54 */ "VPEL",   // VoicePowerEndLong
        /*  55 */ "ALIL",   // AlertIdle
        /*  56 */ "LOIL",   // LostIdle
        /*  57 */ "NOTA",   // NormalToAlert
        /*  58 */ "ALTC",   // AlertToCombat
        /*  59 */ "NOTC",   // NormalToCombat
        /*  60 */ "ALTN",   // AlertToNormal
        /*  61 */ "COTN",   // CombatToNormal
        /*  62 */ "COLO",   // CombatToLost
        /*  63 */ "LOTN",   // LostToNormal
        /*  64 */ "LOTC",   // LostToCombat
        /*  65 */ "DFDA",   // DetectFriendDie
        /*  66 */ "SERU",   // ServiceRefusal
        /*  67 */ "REPA",   // Repair
        /*  68 */ "TRAV",   // Travel
        /*  69 */ "TRAI",   // Training
        /*  70 */ "BAEX",   // BarterExit
        /*  71 */ "REEX",   // RepairExit
        /*  72 */ "RECH",   // Recharge
        /*  73 */ "RCEX",   // RechargeExit
        /*  74 */ "TREX",   // TrainingExit
        /*  75 */ "OBCO",   // ObserveCombat
        /*  76 */ "NOTI",   // NoticeCorpse
        /*  77 */ "TITG",   // TimeToGo
        /*  78 */ "GBYE",   // GoodBye
        /*  79 */ "HELO",   // Hello
        /*  80 */ "SWMW",   // SwingMeleeWeapon
        /*  81 */ "FIWE",   // ShootBow
        /*  82 */ "ZKEY",   // ZKeyObject
        /*  83 */ "JUMP",   // Jump
        /*  84 */ "KNOO",   // KnockOverObject
        /*  85 */ "DEOB",   // DestroyObject
        /*  86 */ "STOF",   // StandonFurniture
        /*  87 */ "LOOB",   // LockedObject
        /*  88 */ "PICT",   // PickpocketTopic
        /*  89 */ "PURS",   // PursueIdleTopic
        /*  90 */ "IDAT",   // SharedInfo
        /*  91 */ "PCPS",   // PlayerCastProjectileSpell
        /*  92 */ "PCSS",   // PlayerCastSelfSpell
        /*  93 */ "PCSH",   // PlayerShout
        /*  94 */ "IDLE",   // Idle
        /*  95 */ "BREA",   // EnterSprintBreath
        /*  96 */ "ENBZ",   // EnterBowZoomBreath
        /*  97 */ "EXBZ",   // ExitBowZoomBreath
        /*  98 */ "ACAC",   // ActorCollidewithActor
        /*  99 */ "PIRN",   // PlayerinIronSights
        /* 100 */ "OUTB",   // OutofBreath
        /* 101 */ "GRNT",   // CombatGrunt
        /* 102 */ "LWBS",   // LeaveWaterBreath
    };

    /// <summary>The number of subtype indices the table covers (0..<see cref="Count"/>-1). Pinned by the guard.</summary>
    public static int Count => MarkerByIndex.Length;

    /// <summary>The 4-char SNAM marker for a subtype index (== <c>(int)DialogTopic.Subtype</c>), or null if the
    /// index is outside the modeled range (never for a real Mutagen enum value — those are all 0..102).</summary>
    public static string? MarkerFor(int subtypeIndex) =>
        subtypeIndex >= 0 && subtypeIndex < MarkerByIndex.Length ? MarkerByIndex[subtypeIndex] : null;

    /// <summary>The 4-char SNAM marker for a <see cref="DialogTopic.SubtypeEnum"/>, or null if unmodeled.</summary>
    public static string? MarkerFor(DialogTopic.SubtypeEnum subtype) => MarkerFor((int)subtype);

    /// <summary>True when a topic's SNAM marker is empty/default (0000) — the guaranteed-CTD state (#131). The single
    /// home for this test so the create path and the validator agree on what "no marker" means (Q3).</summary>
    public static bool IsBlankMarker(RecordType marker)
    {
        var s = marker.Type;
        return string.IsNullOrEmpty(s) || s.All(c => c == '\0');
    }

    /// <summary>Auto-fill a topic's SNAM marker from its <c>Subtype</c> when it is blank — the create-path fix for
    /// #131. Returns the marker it SET (for the "report it" op), or null when it changed nothing: the marker was
    /// already non-blank (an explicit value the author set is NEVER overridden — the escape hatch stays theirs), or
    /// no marker is modeled for the subtype (left blank + surfaced by the validator, never a silent guess). Only ever
    /// completes a write the author under-specified; it does not touch <c>Subtype</c> itself.</summary>
    public static string? NormalizeMarker(IDialogTopic topic)
    {
        if (!IsBlankMarker(topic.SubtypeName)) return null;             // explicit marker wins — never override
        if (MarkerFor((int)topic.Subtype) is not { } tag) return null; // no modeled marker — leave blank, validator flags it
        topic.SubtypeName = new RecordType(tag);
        return tag;
    }
}

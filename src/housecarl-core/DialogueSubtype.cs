using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

// ======================================================================
//  DialogueSubtype — the DialogTopic subtype MARKER (SNAM) authority.
//
//  THE BUG THIS EXISTS FOR (issue #131, community report by matashina):
//  A DialogTopic (DIAL) carries its subtype in TWO independent places —
//    • DATA\Subtype : a numeric enum (Mutagen's DialogTopic.SubtypeEnum; the game index, e.g. Hello = 79)
//    • SNAM         : a 4-character text marker (RecordType, e.g. "HELO")
//  The engine buckets topics by the SNAM MARKER. A NEWLY-AUTHORED topic with a blank marker (0000) is the
//  #131 load CTD — the engine walks an invalid topic list. Mutagen writes SNAM verbatim from the field and
//  does NOT derive it from Subtype (confirmed by decompiling DialogTopicBinaryWriteTranslation), so a create
//  that sets only Subtype leaves SNAM at 0000 — a byte-valid plugin that crashes. This is one of the
//  Creation-Kit bookkeeping jobs a raw insert skips (see the dialogue-authoring skill).
//
//  A NOTE ON SEVERITY (found in review, not universal): a blank marker is not ALWAYS a guaranteed crash — a
//  blank-SNAM *override* of an existing topic has been observed shipping in a working, actively-played mod
//  (the base record's marker plausibly still applies). So the accurate claim is: a blank marker is malformed,
//  and on a NEW/own topic it is the #131 load CTD; on an override it may be tolerated but is still wrong. The
//  wording across the code + validator + skill reflects that, rather than claiming a universal crash.
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
//  matched by subtype NAME. Each row pairs Mutagen's OWN SubtypeEnum name (empty only for index 3, which
//  Mutagen's enum skips — xEdit's 'Custom?') with the marker, and dialogue-subtype-marker-guard asserts
//  Enum.Parse(name)==index for every named row, so the row order is machine-verified — a transposition can't
//  pass CI silently. Mutagen's SubtypeEnum integer values equal these indices, so the lookup key is simply
//  (int)DialogTopic.Subtype.
// ======================================================================

/// <summary>The outcome of <see cref="DialogueSubtype.NormalizeMarker"/>: whether it filled the marker, left an
/// author's explicit marker alone, or could not model one for the subtype (the last is a defect the caller must
/// surface LOUDLY, never ship silently — Q3).</summary>
public enum MarkerFill
{
    /// <summary>The marker was already non-blank — an explicit value is never overridden. Nothing changed.</summary>
    AlreadySet,
    /// <summary>The marker was blank and has been filled from the subtype (the marker is in the out param).</summary>
    Filled,
    /// <summary>The marker was blank AND no marker is modeled for the subtype (an out-of-range enum value that got
    /// past pre-flight). NOTHING was filled — the caller MUST warn/refuse, never leave this silent (Q3).</summary>
    Unmodeled,
}

/// <summary>The authority for a DialogTopic's SNAM subtype marker — the 4-char tag the game buckets topics by.
/// A blank marker is malformed (a load CTD on a new topic — issue #131), so the create path auto-fills it from
/// the topic's <c>Subtype</c> and the on-demand validator escalates a blank one to a Problem. Both read the marker
/// through THIS type so the write side and the check side can never disagree.</summary>
public static class DialogueSubtype
{
    /// <summary>Subtype index (== <c>(int)DialogTopic.Subtype</c> == xEdit DATA\Subtype index) → (Mutagen enum name,
    /// 4-char SNAM marker), from xEdit's canonical definition (see file header for provenance). Contiguous 0..102;
    /// every Marker is a real 4-char signature Bethesda uses (<c>HIT_</c> keeps its trailing underscore). Name is
    /// Mutagen's own <see cref="DialogTopic.SubtypeEnum"/> member name, empty ("") only for index 3 which Mutagen's
    /// enum omits (xEdit's 'Custom?'). The guard asserts Enum.Parse(Name)==index for every named row.</summary>
    static readonly (string Name, string Marker)[] Table =
    {
        ("Custom", "CUST"),                          //   0
        ("ForceGreet", "PFGT"),                      //   1
        ("Rumors", "RUMO"),                          //   2
        ("", "FVDL"),                                //   3  (xEdit 'Custom?'; not a Mutagen enum member)
        ("Intimidate", "INTI"),                      //   4
        ("Flatter", "FLAT"),                         //   5
        ("Bribe", "BRIB"),                           //   6
        ("AskGift", "ASKG"),                         //   7
        ("Gift", "GIFF"),                            //   8
        ("AskFavor", "ASKF"),                        //   9
        ("Favor", "FAVO"),                           //  10
        ("ShowRelationships", "SHRE"),               //  11
        ("Follow", "FOLL"),                          //  12
        ("Reject", "FRJT"),                          //  13
        ("Scene", "SCEN"),                           //  14
        ("Show", "SHOW"),                            //  15
        ("Agree", "AGRE"),                           //  16
        ("Refuse", "REFU"),                          //  17
        ("ExitFavorState", "FEXT"),                  //  18
        ("MoralRefusal", "MREF"),                    //  19
        ("FlyingMountLand", "FMLX"),                 //  20
        ("FlyingMountCancelLand", "FMXL"),           //  21
        ("FlyingMountAcceptTarget", "FMAT"),         //  22
        ("FlyingMountRejectTarget", "FMRT"),         //  23
        ("FlyingMountNoTarget", "FMNT"),             //  24
        ("FlyingMountDestinationReached", "FMDR"),   //  25
        ("Attack", "ATCK"),                          //  26
        ("PowerAttack", "POAT"),                     //  27
        ("Bash", "BASH"),                            //  28
        ("Hit", "HIT_"),                             //  29
        ("Flee", "FLEE"),                            //  30
        ("Bleedout", "BLED"),                        //  31
        ("AvoidThreat", "AVTH"),                     //  32
        ("Death", "DETH"),                           //  33
        ("GroupStrategy", "GRST"),                   //  34
        ("Block", "BLOC"),                           //  35
        ("Taunt", "TAUT"),                           //  36
        ("AllyKilled", "ALKL"),                      //  37
        ("Steal", "STEA"),                           //  38
        ("Yield", "YIEL"),                           //  39
        ("AcceptYield", "ACYI"),                     //  40
        ("PickpocketCombat", "PICC"),                //  41
        ("Assault", "ASSA"),                         //  42
        ("Murder", "MURD"),                          //  43
        ("AssaultNC", "ASNC"),                       //  44
        ("MurderNC", "MUNC"),                        //  45
        ("PickpocketNC", "PICN"),                    //  46
        ("StealFromNC", "STFN"),                     //  47
        ("TrespassAgainstNC", "TRAN"),               //  48
        ("Trespass", "TRES"),                        //  49
        ("WerewolfTransformCrime", "WTCR"),          //  50
        ("VoicePowerStartShort", "VPSS"),            //  51
        ("VoicePowerStartLong", "VPSL"),             //  52
        ("VoicePowerEndShort", "VPES"),              //  53
        ("VoicePowerEndLong", "VPEL"),               //  54
        ("AlertIdle", "ALIL"),                       //  55
        ("LostIdle", "LOIL"),                        //  56
        ("NormalToAlert", "NOTA"),                   //  57
        ("AlertToCombat", "ALTC"),                   //  58
        ("NormalToCombat", "NOTC"),                  //  59
        ("AlertToNormal", "ALTN"),                   //  60
        ("CombatToNormal", "COTN"),                  //  61
        ("CombatToLost", "COLO"),                    //  62
        ("LostToNormal", "LOTN"),                    //  63
        ("LostToCombat", "LOTC"),                    //  64
        ("DetectFriendDie", "DFDA"),                 //  65
        ("ServiceRefusal", "SERU"),                  //  66
        ("Repair", "REPA"),                          //  67
        ("Travel", "TRAV"),                          //  68
        ("Training", "TRAI"),                        //  69
        ("BarterExit", "BAEX"),                      //  70
        ("RepairExit", "REEX"),                      //  71
        ("Recharge", "RECH"),                        //  72
        ("RechargeExit", "RCEX"),                    //  73
        ("TrainingExit", "TREX"),                    //  74
        ("ObserveCombat", "OBCO"),                   //  75
        ("NoticeCorpse", "NOTI"),                    //  76
        ("TimeToGo", "TITG"),                        //  77
        ("Goodbye", "GBYE"),                         //  78
        ("Hello", "HELO"),                           //  79
        ("SwingMeleeWeapon", "SWMW"),                //  80
        ("ShootBow", "FIWE"),                        //  81
        ("ZKeyObject", "ZKEY"),                      //  82
        ("Jump", "JUMP"),                            //  83
        ("KnockOverObject", "KNOO"),                 //  84
        ("DestroyObject", "DEOB"),                   //  85
        ("StandOnFurniture", "STOF"),                //  86
        ("LockedObject", "LOOB"),                    //  87
        ("PickpocketTopic", "PICT"),                 //  88
        ("PursueIdleTopic", "PURS"),                 //  89
        ("SharedInfo", "IDAT"),                      //  90
        ("PlayerCastProjectileSpell", "PCPS"),       //  91
        ("PlayerCastSelfSpell", "PCSS"),             //  92
        ("PlayerShout", "PCSH"),                     //  93
        ("Idle", "IDLE"),                            //  94
        ("EnterSprintBreath", "BREA"),               //  95
        ("EnterBowZoomBreath", "ENBZ"),              //  96
        ("ExitBowZoomBreath", "EXBZ"),               //  97
        ("ActorCollideWithActor", "ACAC"),           //  98
        ("PlayerInIronSights", "PIRN"),              //  99
        ("OutOfBreath", "OUTB"),                     // 100
        ("CombatGrunt", "GRNT"),                     // 101
        ("LeaveWaterBreath", "LWBS"),                // 102
    };

    /// <summary>The number of subtype indices the table covers (0..<see cref="Count"/>-1). Pinned by the guard.</summary>
    public static int Count => Table.Length;

    /// <summary>Mutagen's SubtypeEnum name for a row, or "" for index 3 (which Mutagen's enum omits). For the guard's
    /// name↔index machine-check — a transposition of any named row makes Enum.Parse(name)!=index and fails CI.</summary>
    public static string NameAt(int index) => index >= 0 && index < Table.Length ? Table[index].Name : "";

    /// <summary>The 4-char SNAM marker for a subtype index (== <c>(int)DialogTopic.Subtype</c>), or null if the
    /// index is outside the modeled range (never for a real Mutagen enum value — those are all 0..102).</summary>
    public static string? MarkerFor(int subtypeIndex) =>
        subtypeIndex >= 0 && subtypeIndex < Table.Length ? Table[subtypeIndex].Marker : null;

    /// <summary>The 4-char SNAM marker for a <see cref="DialogTopic.SubtypeEnum"/>, or null if unmodeled.</summary>
    public static string? MarkerFor(DialogTopic.SubtypeEnum subtype) => MarkerFor((int)subtype);

    /// <summary>True when a topic's SNAM marker is empty/default (0000) OR whitespace-only — the malformed "no real
    /// marker" state. The single home for this test so the create path and the validator agree on what "no marker"
    /// means (Q3). Whitespace is treated as blank (it renders invisibly and buckets to a tag no real topic uses).</summary>
    public static bool IsBlankMarker(RecordType marker)
    {
        var s = marker.Type;
        return string.IsNullOrEmpty(s) || string.IsNullOrWhiteSpace(s) || s.All(c => c == '\0');
    }

    /// <summary>Auto-fill a topic's SNAM marker from its <c>Subtype</c> when it is blank — the create-path fix for
    /// #131. Reports WHICH of three things happened so the caller can be honest (Q3): <see cref="MarkerFill.Filled"/>
    /// (was blank, now set — <paramref name="marker"/> carries the tag, for the "report it" op),
    /// <see cref="MarkerFill.AlreadySet"/> (a non-blank marker the author set — NEVER overridden, the escape hatch
    /// stays theirs), or <see cref="MarkerFill.Unmodeled"/> (blank AND no marker modeled for the subtype — an
    /// out-of-range enum value; NOTHING filled, the caller MUST surface it loudly rather than ship a silent blank).
    /// Only ever completes a write the author under-specified; it does not touch <c>Subtype</c> itself.</summary>
    public static MarkerFill NormalizeMarker(IDialogTopic topic, out string? marker)
    {
        marker = null;
        if (!IsBlankMarker(topic.SubtypeName)) return MarkerFill.AlreadySet;   // explicit marker wins — never override
        if (MarkerFor((int)topic.Subtype) is not { } tag) return MarkerFill.Unmodeled;   // no modeled marker — caller warns
        topic.SubtypeName = new RecordType(tag);
        marker = tag;
        return MarkerFill.Filled;
    }
}

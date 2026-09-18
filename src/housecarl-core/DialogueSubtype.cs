using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

// DialogueSubtype — the authority for a DialogTopic's SNAM subtype marker; contract in
// docs/architecture/dialogue.md. The table is the join of xEdit's DATA\Subtype index→name enum and its
// 4-char-signature→name enum, matched by subtype name; the lookup key is (int)DialogTopic.Subtype.

/// <summary>The outcome of <see cref="DialogueSubtype.NormalizeMarker"/>.</summary>
public enum MarkerFill
{
    /// <summary>The marker was already non-blank — an explicit value is never overridden. Nothing changed.</summary>
    AlreadySet,
    /// <summary>The marker was blank and has been filled from the subtype (the marker is in the out param).</summary>
    Filled,
    /// <summary>Blank AND no marker modeled for the subtype. NOTHING filled — the caller MUST warn or refuse.</summary>
    Unmodeled,
}

/// <summary>The authority for a DialogTopic's SNAM subtype marker — the 4-char tag the game buckets topics by.
/// The create path and the validator both read it through THIS type, so they cannot disagree.</summary>
public static class DialogueSubtype
{
    /// <summary>Subtype index → (Mutagen enum name, 4-char SNAM marker). Contiguous 0..102; Name is empty only for
    /// index 3, which Mutagen's enum omits. CI asserts Enum.Parse(Name)==index for every named row.</summary>
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

    /// <summary>The number of subtype indices the table covers (0..<see cref="Count"/>-1).</summary>
    public static int Count => Table.Length;

    /// <summary>Mutagen's SubtypeEnum name for a row, or "" for index 3; the input to the name↔index CI check.</summary>
    public static string NameAt(int index) => index >= 0 && index < Table.Length ? Table[index].Name : "";

    /// <summary>The 4-char SNAM marker for a subtype index, or null outside the modeled range 0..102.</summary>
    public static string? MarkerFor(int subtypeIndex) =>
        subtypeIndex >= 0 && subtypeIndex < Table.Length ? Table[subtypeIndex].Marker : null;

    /// <summary>The 4-char SNAM marker for a <see cref="DialogTopic.SubtypeEnum"/>, or null if unmodeled.</summary>
    public static string? MarkerFor(DialogTopic.SubtypeEnum subtype) => MarkerFor((int)subtype);

    /// <summary>Marker → subtype index, the reverse of the table; ordinal, as the markers are fixed-case.</summary>
    static readonly Dictionary<string, int> ByMarker =
        Table.Select((row, i) => (row.Marker, i)).ToDictionary(p => p.Marker, p => p.i, StringComparer.Ordinal);

    /// <summary>The subtype index a SNAM marker names, or null when it is blank or unmodeled. SNAM is the
    /// authoritative statement, so this is the lookup to trust over <c>(int)DialogTopic.Subtype</c>.</summary>
    public static int? IndexForMarker(RecordType marker) =>
        !IsBlankMarker(marker) && ByMarker.TryGetValue(marker.Type, out var i) ? i : null;

    /// <summary>Mutagen's SubtypeEnum name for the subtype a SNAM marker names, or null when it cannot.</summary>
    public static string? NameForMarker(RecordType marker) => IndexForMarker(marker) is { } i ? NameAt(i) : null;

    /// <summary>The best LABEL for the subtype a marker names: Mutagen's enum name, or the MARKER ITSELF for the
    /// one modeled row the enum omits (index 3, FVDL). Null only for a blank or unmodeled marker.</summary>
    public static string? LabelForMarker(RecordType marker) =>
        IndexForMarker(marker) is { } i ? (NameAt(i) is { Length: > 0 } n ? n : Table[i].Marker) : null;

    /// <summary>True when a topic's numeric <c>Subtype</c> contradicts its SNAM marker, both modeled; the
    /// renumbering that causes it is in docs/architecture/dialogue.md. A blank or unmodeled marker is not this.</summary>
    public static bool MarkerDisagreesWithSubtype(IDialogTopicGetter topic) =>
        IndexForMarker(topic.SubtypeName) is { } fromMarker && fromMarker != (int)topic.Subtype;

    /// <summary>How far a pre-Dragonborn DATA\Subtype sits below the modern table — the six inserted rows.</summary>
    public const int RenumberOffset = 6;

    /// <summary>The first modern index that also existed under the OLD numbering; below it, nothing shifted.</summary>
    public const int RenumberFirstShifted = 26;

    /// <summary>Does a disagreeing pair carry the RENUMBERING signature? True means an old file with a stale
    /// number; false means the two fields were edited apart and the Subtype edit is an in-game no-op.</summary>
    public static bool IsRenumberedVintage(int markerIndex, int subtypeValue) =>
        markerIndex >= RenumberFirstShifted && markerIndex - subtypeValue == RenumberOffset;

    /// <summary>True when a topic's SNAM marker is 0000 or whitespace-only — the single home for "no marker".</summary>
    public static bool IsBlankMarker(RecordType marker)
    {
        var s = marker.Type;
        return string.IsNullOrEmpty(s) || string.IsNullOrWhiteSpace(s) || s.All(c => c == '\0');
    }

    /// <summary>Auto-fill a topic's SNAM marker from its <c>Subtype</c> when it is blank, on the create path, and
    /// report which of the three <see cref="MarkerFill"/> arms happened. Never overrides, never touches
    /// <c>Subtype</c>.</summary>
    public static MarkerFill NormalizeMarker(IDialogTopic topic, out string? marker)
    {
        marker = null;
        if (!IsBlankMarker(topic.SubtypeName)) return MarkerFill.AlreadySet;   // explicit marker wins — never override
        if (MarkerFor((int)topic.Subtype) is not { } tag) return MarkerFill.Unmodeled;   // no modeled marker — caller warns
        topic.SubtypeName = new RecordType(tag);
        marker = tag;
        return MarkerFill.Filled;
    }

    /// <summary>Force a topic's SNAM marker to MATCH its current <c>Subtype</c>, overwriting a stale one, on the
    /// edit path only after a call that set <c>Subtype</c> and not <c>SubtypeName</c> — the gate is the CALLER's
    /// job. Returns the same three <see cref="MarkerFill"/> arms.</summary>
    public static MarkerFill SyncMarkerToSubtype(IDialogTopic topic, out string? marker)
    {
        marker = null;
        if (MarkerFor((int)topic.Subtype) is not { } tag) return MarkerFill.Unmodeled;
        if (string.Equals(topic.SubtypeName.Type, tag, StringComparison.Ordinal)) return MarkerFill.AlreadySet;
        topic.SubtypeName = new RecordType(tag);
        marker = tag;
        return MarkerFill.Filled;
    }
}

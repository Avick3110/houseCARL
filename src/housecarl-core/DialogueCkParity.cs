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
//  The byte-only tier (DLBR Category/TNAM, QUST NextAliasID/ANAM + objective FNAM, DIAL Priority/PNAM) is a
//  deliberate follow-on (S2) that EXTENDS this same authority — add its methods here, do not fork a parallel path.
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
}

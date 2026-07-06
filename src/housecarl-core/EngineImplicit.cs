using Mutagen.Bethesda.Plugins;

namespace HousecarlCore;

/// <summary>Engine-implicit forms — the hardcoded references the engine defines but that are NOT normal records in the
/// base master's data, so the load-order index can't resolve them (<see cref="LoadOrderResolver.IndexView.ResolveWinner"/>
/// returns null even though the form is real). A FormLink legitimately points at these: PlayerRef (the player's placed
/// reference — the target of HasSpell / actor-value player-state gates and countless script, package, and condition
/// links) and the Player base NPC_ (a GetIsID Player form param). Any tool that flags "target not in the active load
/// order" must exempt them or it false-warns on the standard player-state pattern — so the SINGLE source of truth lives
/// here, shared by BOTH the dialogue condition lints (<see cref="DialogueValidate"/>) and the integrity sweep
/// (<see cref="ErrorCheck"/>), keeping their exemptions in lockstep.
///
/// Evidence: Junti 2026-07-03 — xEdit resolves 000014 as PlayerRef and the gates are proven working in game (the
/// validate_dialogue manifestation, fixed in PR #139); the identical class then surfaced in check_errors, which
/// reported 531/531 dangling refs ALL → 000014:Skyrim.esm, overflowing the tool response
/// (HCBR checkerrors-playerref-dangling-false-positive).
///
/// Scoped to the two the reports prove — a PRECISE set, NOT the whole reserved sub-0x800 range, so a genuinely-typo'd
/// low FormID still surfaces as a real error. Skyrim.esm is the SSE base master (houseCARL is SSE-only). Extend the set
/// here if more engine-implicit forms surface — one edit updates every tool.</summary>
internal static class EngineImplicit
{
    static readonly ModKey SkyrimBaseMaster = new("Skyrim", ModType.Master);

    /// <summary>The precise engine-implicit reference set (see the type doc). Add a form here — never widen to a range.</summary>
    static readonly HashSet<FormKey> Forms = new()
    {
        new FormKey(SkyrimBaseMaster, 0x14),   // PlayerRef — the player's placed reference (a Run On Reference / FormLink target)
        new FormKey(SkyrimBaseMaster, 0x07),   // Player    — the player base NPC_ (a GetIsID / form-param target)
    };

    /// <summary>True when <paramref name="fk"/> is one of the engine-implicit <see cref="Forms"/> — a hardcoded engine
    /// reference the index can't resolve, so a reference-resolution check must not mistake it for a dangling reference.</summary>
    internal static bool IsImplicit(FormKey fk) => Forms.Contains(fk);
}

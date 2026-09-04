using Mutagen.Bethesda.Plugins;

namespace HousecarlCore;

/// <summary>Engine-implicit forms — the hardcoded references the engine defines but that are NOT normal records in the
/// base master's data, so the load-order index can't resolve them (<see cref="LoadOrderResolver.IndexView.ResolveWinner"/>
/// returns null even though the form is real). A FormLink legitimately points at these: PlayerRef (the player's placed
/// reference — the target of HasSpell / actor-value player-state gates and countless script, package, and condition
/// links) and the Player base NPC_ (a GetIsID Player form param). Any tool that flags "target not in the active load
/// order" must exempt them or it false-warns on the standard player-state pattern — so the SINGLE source of truth lives
/// here, shared by the dialogue condition lints (<see cref="DialogueValidate"/>), the integrity sweep
/// (<see cref="ErrorCheck"/>), AND the identity resolver behind housecarl_resolve / resolve_names, keeping every
/// exemption in lockstep. Public because that resolver lives in the MCP server assembly, not this core.
///
/// A PRECISE set, NOT the whole reserved sub-0x800 range, so a genuinely-typo'd low FormID still surfaces as a real
/// error. Skyrim.esm is the SSE base master (houseCARL is SSE-only). Extend the set here if more engine-implicit forms
/// surface — one edit updates every tool.</summary>
public static class EngineImplicit
{
    static readonly ModKey SkyrimBaseMaster = new("Skyrim", ModType.Master);

    /// <summary>The precise engine-implicit reference set, each with its known engine identity (Mutagen-style type
    /// name + the EditorID xEdit reports) so the resolver can answer WHAT the form is, not just that it's exempt
    /// (see the type doc). Add a form here — never widen to a range.</summary>
    static readonly Dictionary<FormKey, (string Type, string EditorId)> Forms = new()
    {
        [new FormKey(SkyrimBaseMaster, 0x14)] = ("PlacedNpc", "PlayerRef"),   // PlayerRef — the player's placed reference (a Run On Reference / FormLink target)
        [new FormKey(SkyrimBaseMaster, 0x07)] = ("Npc", "Player"),            // Player    — the player base NPC_ (a GetIsID / form-param target)
    };

    /// <summary>True when <paramref name="fk"/> is one of the engine-implicit <see cref="Forms"/> — a hardcoded engine
    /// reference the index can't resolve, so a reference-resolution check must not mistake it for a dangling reference.</summary>
    public static bool IsImplicit(FormKey fk) => Forms.ContainsKey(fk);

    /// <summary>The engine-implicit form's known identity (type + EditorID from <see cref="Forms"/>), for a resolver
    /// that must report WHAT the hardcoded form is rather than merely skip it. False for every other form — callers
    /// keep their normal dangling-target path.</summary>
    public static bool TryDescribe(FormKey fk, out string type, out string editorId)
    {
        if (Forms.TryGetValue(fk, out var d)) { (type, editorId) = d; return true; }
        type = editorId = ""; return false;
    }
}

using Mutagen.Bethesda.Plugins;

namespace HousecarlCore;

/// <summary>Engine-implicit forms — hardcoded engine references the load-order index cannot resolve. A PRECISE set, never a range.
/// <para>ANY tool that flags "target not in the active load order" must exempt them or it false-warns on the standard player-state pattern; today that is the dialogue condition lints, the integrity sweep, and
/// the identity resolver behind <c>housecarl_resolve</c> / <c>resolve_names</c>, which is why this is public rather than internal to this assembly.</para></summary>
public static class EngineImplicit
{
    static readonly ModKey SkyrimBaseMaster = new("Skyrim", ModType.Master);

    /// <summary>The precise engine-implicit reference set, each with its known engine identity. Add a form here — never widen to a range.</summary>
    static readonly Dictionary<FormKey, (string Type, string EditorId)> Forms = new()
    {
        [new FormKey(SkyrimBaseMaster, 0x14)] = ("PlacedNpc", "PlayerRef"),   // PlayerRef — the player's placed reference (a Run On Reference / FormLink target)
        [new FormKey(SkyrimBaseMaster, 0x07)] = ("Npc", "Player"),            // Player    — the player base NPC_ (a GetIsID / form-param target)
    };

    /// <summary>True when <paramref name="fk"/> is one of the engine-implicit <see cref="Forms"/>.</summary>
    public static bool IsImplicit(FormKey fk) => Forms.ContainsKey(fk);

    /// <summary>The engine-implicit form's known identity, for a resolver that must report WHAT the hardcoded form is rather than merely skip it.</summary>
    public static bool TryDescribe(FormKey fk, out string type, out string editorId)
    {
        if (Forms.TryGetValue(fk, out var d)) { (type, editorId) = d; return true; }
        type = editorId = ""; return false;
    }
}

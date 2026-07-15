namespace HousecarlCore;

/// <summary>
/// The single home for Skyrim FormID numeric-range facts — the engine-reserved object-ID floor, the ESL/light-master
/// window, and the 24-bit object-ID ceiling/mask. Every piece of product code that GUARDS or MASKS a FormID's object
/// ID reads THESE constants rather than restating the hex, so a change lands in one place — the "one shared home"
/// discipline <see cref="EngineImplicit"/> set for the engine-implicit forms, applied to numeric ranges. Values are
/// pinned empirically (Mutagen 0.53.1) by the generator probes: <c>FormIdFloorProbe</c> (the 0x800 floor / the
/// allocate-from-zero bug) and <c>EslFormIdProbe</c> (the 0x800–0xFFF ESL window). These are hard engine facts, not
/// tuning knobs — the value of consolidation is that the SAME number can't be quietly changed in one guard and left
/// stale in another (the drift the PlayerRef whitelist hit before it was single-sourced).
/// </summary>
public static class FormIdRange
{
    /// <summary>First non-engine-reserved object ID (0x800). Object IDs below this (0x000–0x7FF) are engine-reserved: a
    /// NEW record allocated there is the FormID-allocation-from-zero bug (0x000000 is the null-reference bit pattern),
    /// and a serialize carrying an ORIGINATING sub-0x800 record throws <c>LowerFormKeyRangeDisallowed</c>. New-record
    /// allocation is floored to this (<see cref="WriteEngine.EnsureFormIdFloor"/>). It is ALSO the ESL window floor
    /// (<see cref="EslWindowFloor"/>) — the same 0x800 boundary, seen from the allocation side.</summary>
    public const uint EngineReservedFloor = 0x800;

    /// <summary>The light-master (ESL) object-ID window FLOOR — the same 0x800 boundary as
    /// <see cref="EngineReservedFloor"/> (the ESL window runs from the first usable object ID up to
    /// <see cref="EslWindowCeiling"/>). Named separately so an ESL caller reads "ESL window", not "allocation floor";
    /// <c>RemapEngine.EslFloor</c> aliases this.</summary>
    public const uint EslWindowFloor = EngineReservedFloor;

    /// <summary>The light-master (ESL) object-ID window CEILING, INCLUSIVE (0xFFF). With the floor that is a 2048-ID
    /// window; an object ID &gt; this in a light-flagged master throws <c>FormIDCompactionOutOfBounds</c> (EslFormIdProbe).
    /// <c>RemapEngine.EslCeiling</c> aliases this.</summary>
    public const uint EslWindowCeiling = 0xFFF;

    /// <summary>The maximum object ID (0xFFFFFF). An object ID occupies the low 3 bytes of a FormID (the high byte is
    /// the master-list index), so a valid object ID is 0x000000–0xFFFFFF. A new-record counter past this has overflowed
    /// the FormID space — no allocation is possible (<see cref="WriteEngine.EnsureAllocatable"/>).</summary>
    public const uint ObjectIdMax = 0xFFFFFF;

    /// <summary>The 24-bit object-ID mask (== <see cref="ObjectIdMax"/>): <c>formId &amp; ObjectIdMask</c> strips the high
    /// master-index byte to leave the bare object ID. Same value as the ceiling; named for the masking use so a call
    /// site reads "mask off the index byte", not "compare against the max".</summary>
    public const uint ObjectIdMask = ObjectIdMax;

    /// <summary>True once a plugin's new-record counter (<c>ModHeader.Stats.NextFormID</c>) has run PAST the 24-bit
    /// object-ID ceiling (<see cref="ObjectIdMax"/>): no further FormID can be allocated — the object-ID space is full
    /// or the header counter is corrupt. The single home for the "is this patch out of object IDs?" test, shared by the
    /// create-path allocation guard (<see cref="WriteEngine.EnsureAllocatable"/>) and the NPC-appearance batch
    /// allocator — each keeps its OWN error surface (a throw at the create boundary vs a graceful Fail outcome in the
    /// copy flow), only the ceiling comparison is single-sourced here.</summary>
    public static bool ObjectIdSpaceExhausted(uint nextFormId) => nextFormId > ObjectIdMax;
}

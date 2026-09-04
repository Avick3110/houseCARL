namespace HousecarlCore;

/// <summary>
/// The single home for Skyrim FormID numeric-range facts — the engine-reserved object-ID floor, the ESL/light-master
/// window, and the 24-bit object-ID ceiling/mask. Every piece of product code that GUARDS or MASKS a FormID's object
/// ID reads THESE constants rather than restating the hex, so a change lands in one place — the same "one shared home"
/// discipline <see cref="EngineImplicit"/> uses for the engine-implicit forms. These are hard engine facts (checked
/// against Mutagen 0.53.1), not tuning knobs; consolidating them keeps the SAME number from being changed in one
/// guard and left stale in another.
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
    /// window; an object ID &gt; this in a light-flagged master throws <c>FormIDCompactionOutOfBounds</c>.
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

    /// <summary>The high-byte signature of a light-master (ESL) RUNTIME FormID (0xFE000000). At load the engine gives every
    /// light master the shared <c>0xFE</c> index and packs a 12-bit light-order index into the next 12 bits, leaving the
    /// record its low 12 bits (<see cref="LightObjectIdMask"/>). A full plugin never loads at 0xFE (that slot is reserved
    /// for the light block; 0xFF is the runtime-dynamic block), so <c>(id &amp; 0xFF000000) == LightMasterIndexPrefix</c> is
    /// the unambiguous "this token is a light-prefixed runtime FormID" test.</summary>
    public const uint LightMasterIndexPrefix = 0xFE000000;

    /// <summary>The 12-bit object-ID mask (0xFFF) for a light-master (ESL) record — its local object ID relative to the
    /// light master, once the shared <c>0xFE</c> index and the 12-bit light-order index are masked off. Same value as
    /// <see cref="EslWindowCeiling"/> (the ESL window is 0x000–0xFFF); named for the masking use so a call site reads
    /// "mask off to the light local id", not "compare against the window ceiling".</summary>
    public const uint LightObjectIdMask = EslWindowCeiling;

    /// <summary>Strip a config-token / runtime FormID down to the record's LOCAL object ID relative to its named plugin —
    /// the low 12 bits for a light-prefixed (<see cref="LightMasterIndexPrefix"/>) token (the light index is not part of the
    /// id), the low 24 bits (<see cref="ObjectIdMask"/>) otherwise (the high byte is the load-order master index, which the
    /// plugin name — not the token — supplies). This is the SINGLE home for the distributor-config FormID normalization the
    /// SkyPatcher overlay and the SKSE config audit both apply: a full load-indexed light FormID (<c>FExxxYYY</c> — the
    /// xEdit copy the grammar references treat as always legal) keeps only its <c>YYY</c>. Getting the light-vs-full split
    /// wrong inverts every resolve verdict, so both consumers read it here rather than restating the hex.
    ///
    /// <para>Matches DSD's own parser (SkyHorizon3/SSE-Dynamic-String-Distributor, <c>src/Utils.cpp</c>
    /// <c>getRuntimeFormID</c>: <c>(raw &amp; 0xFFF)</c> when the named plugin <c>IsLight()</c>, else <c>(raw &amp; 0xFFFFFF)</c>).
    /// DSD keys off the NAMED PLUGIN's light flag; this token-prefix rule (0xFE top byte ⇒ light) produces the
    /// IDENTICAL local id for every shape DSD emits. The ONE divergence is a bare ≤6-hex token carrying a light index
    /// but no 0xFE prefix (e.g. <c>800123</c>): DSD masks it to 0x123 via the flag, this rule keeps 0x800123. DSD never
    /// EMITS that shape (its export trims ESL to ≤0xFFF), so it can only arise from a hand-authored config.</para></summary>
    public static uint LocalObjectId(uint runtimeFormId) =>
        (runtimeFormId & 0xFF000000) == LightMasterIndexPrefix
            ? runtimeFormId & LightObjectIdMask       // FExxxYYY light runtime FormID → the 12-bit local id (YYY)
            : runtimeFormId & ObjectIdMask;           // else the high byte is the master index → keep the low 24 bits
}

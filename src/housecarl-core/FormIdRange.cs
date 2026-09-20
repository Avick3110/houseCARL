namespace HousecarlCore;

/// <summary>The single home for Skyrim FormID numeric-range facts — the engine-reserved floor, the ESL window, and the 24-bit object-ID ceiling and masks. Hard engine facts, not tuning knobs.</summary>
public static class FormIdRange
{
    /// <summary>First non-engine-reserved object ID (0x800): new-record allocation is floored to it, and it is also the ESL window floor seen from the allocation side.</summary>
    public const uint EngineReservedFloor = 0x800;

    /// <summary>The light-master (ESL) object-ID window FLOOR — the same 0x800 boundary, named for the ESL reading.</summary>
    public const uint EslWindowFloor = EngineReservedFloor;

    /// <summary>The light-master (ESL) object-ID window CEILING, inclusive (0xFFF); an object ID above it in a light-flagged master throws <c>FormIDCompactionOutOfBounds</c>.</summary>
    public const uint EslWindowCeiling = 0xFFF;

    /// <summary>The maximum object ID (0xFFFFFF) — the low 3 bytes of a FormID; a counter past it has overflowed the FormID space.</summary>
    public const uint ObjectIdMax = 0xFFFFFF;

    /// <summary>The 24-bit object-ID mask (== <see cref="ObjectIdMax"/>), named for the masking use rather than the comparison.</summary>
    public const uint ObjectIdMask = ObjectIdMax;

    /// <summary>True once a plugin's new-record counter has run PAST the 24-bit ceiling: no further FormID can be allocated.</summary>
    public static bool ObjectIdSpaceExhausted(uint nextFormId) => nextFormId > ObjectIdMax;

    /// <summary>The high-byte mask (0xFF000000) isolating a FormID's INDEX byte — the master index on a full FormID, the block signature on a runtime one.</summary>
    public const uint IndexByteMask = 0xFF000000;

    /// <summary>The high-byte signature of a DYNAMIC runtime FormID (0xFF000000) — a form the game creates while playing, so no plugin defines one.</summary>
    public const uint DynamicIndexPrefix = 0xFF000000;

    /// <summary>How far a light plugin's 12-bit index sits above the record's local id in a runtime FormID: the local id is the low 12 bits.</summary>
    public const int LightIndexShift = 12;

    /// <summary>The 12-bit mask over a light plugin's ORDER index, once shifted down by <see cref="LightIndexShift"/>.</summary>
    public const uint LightIndexMask = 0xFFF;

    /// <summary>How far a full plugin's load index sits above its 24-bit object id: the object id is the low 3 bytes, so the index is bit 24.</summary>
    public const int FullIndexShift = 24;

    /// <summary>The high-byte signature of a light-master (ESL) RUNTIME FormID (0xFE000000) — the shared light index, with a 12-bit light-order index below it and the record's low 12 bits under that.</summary>
    public const uint LightMasterIndexPrefix = 0xFE000000;

    /// <summary>The 12-bit object-ID mask (0xFFF) for a light-master record's local object ID, named for the masking use.</summary>
    public const uint LightObjectIdMask = EslWindowCeiling;

    /// <summary>Does this object ID sit ABOVE the light-master (ESL) window ceiling? True for a record in a light-flagged plugin that was never compacted.</summary>
    public static bool AboveEslWindow(uint objectId) => objectId > EslWindowCeiling;

    /// <summary>Strip a runtime or config-token FormID to the record's LOCAL object ID relative to its named plugin — the low 12 bits for a light-prefixed token, the low 24 otherwise. Matches DSD's own parser.</summary>
    public static uint LocalObjectId(uint runtimeFormId) =>
        (runtimeFormId & IndexByteMask) == LightMasterIndexPrefix
            ? runtimeFormId & LightObjectIdMask       // FExxxYYY light runtime FormID → the 12-bit local id (YYY)
            : runtimeFormId & ObjectIdMask;           // else the high byte is the master index → keep the low 24 bits
}

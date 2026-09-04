namespace HousecarlCore;

/// <summary>
/// The houseCARL ownership marker written into a generated mod folder's <c>meta.ini</c> — the ONE structural signal
/// that houseCARL authored a mod folder (and may therefore modify it in place; a missing marker FAIL-SAFES to
/// not-owned, so houseCARL refuses to touch a folder it can't prove it made). The marker lives in meta.ini, the
/// one mod-root file MO2 does NOT deploy into the game Data folder, so it never pollutes Data.
///
/// Single-sourced here so the writer (<c>LoadOrderService.WriteOwnerMeta</c>), the owner-detection reader
/// (<c>LoadOrderService.IsHouseCarlOwned</c>), the in-place edit audit stamp, and the fixtures that seed an owned
/// folder all reference ONE literal and can't drift — the same "one shared home" discipline
/// <see cref="FormIdRange"/> applies to numeric ranges. (The <c>[houseCARL]</c> stderr LOG prefixes in the MCP layer
/// are a different literal for a different job and deliberately stay separate.)
/// </summary>
public static class HousecarlOwnerMeta
{
    /// <summary>The custom <c>meta.ini</c> section header that flags a houseCARL-generated folder (MO2 ignores sections
    /// it doesn't know). Matched case-insensitively against a trimmed line; paired with <c>generated=true</c> under it,
    /// which is what owner-detection actually keys on.</summary>
    public const string Section = "[houseCARL]";
}

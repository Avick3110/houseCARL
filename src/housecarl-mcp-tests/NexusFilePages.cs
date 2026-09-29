namespace HousecarlMcpTests;

/// <summary>Canned Nexus file lists for the file-level update-check tests; offline, the shapes the probe fed the compare.</summary>
internal static class NexusFilePages
{
    /// <summary>AMON ENB (99786), a multi-main page: 585300 is the live "Esp Fix v2", 775265 the v10 preset a mod-level
    /// compare wrongly measured against, 585294 and 483794 archived "Esp Fix" copies.</summary>
    public static List<(int fileId, string name, string? version, string category, long date)> AmonEnb() => new()
    {
        (585300, "Amon NAT III Esp Fix",           "2",  "MAIN",     1737000000L),
        (775265, "AMON ENB For NAT III 2026",      "10", "MAIN",     1751000000L),
        (474157, "Fix for Sub Surface Scattering", "1",  "MAIN",     1600000000L),
        (585294, "Amon NAT III Esp Fix",           "2",  "ARCHIVED", 1736000000L),
        (483794, "Amon NAT III Esp Fix",           "1",  "ARCHIVED", 1700000000L),
    };

    /// <summary>A single-main page: one live MAIN v3.1 and its old v3.0.</summary>
    public static List<(int fileId, string name, string? version, string category, long date)> SingleMain() => new()
    {
        (10, "Solo", "3.1", "MAIN",        100L),
        (11, "Solo", "3.0", "OLD_VERSION", 90L),
    };

    /// <summary>A page where the installed "Widget Patch" v1 was REMOVED and a live v2 of the same name remains.</summary>
    public static List<(int fileId, string name, string? version, string category, long date)> Removed() => new()
    {
        (30, "Widget Patch", "1", "REMOVED", 100L),
        (31, "Widget Patch", "2", "MAIN",    200L),
    };

    /// <summary>A page where v1 is ARCHIVED and the only same-name newer file, v2, was DELETED.</summary>
    public static List<(int fileId, string name, string? version, string category, long date)> Deleted() => new()
    {
        (40, "Widget Patch", "1", "ARCHIVED", 100L),
        (41, "Widget Patch", "2", "DELETED",  200L),
    };

    /// <summary>No files at all: what the modFiles lookup returns for a mod that does not exist.</summary>
    public static List<(int fileId, string name, string? version, string category, long date)> Empty() => new();
}

namespace HousecarlCore;

/// <summary>The houseCARL ownership marker written into a generated mod folder's <c>meta.ini</c> — the one structural signal that houseCARL authored a folder, single-sourced here.</summary>
public static class HousecarlOwnerMeta
{
    /// <summary>The custom <c>meta.ini</c> section header that flags a houseCARL-generated folder; paired with <c>generated=true</c> under it, which owner-detection keys on.</summary>
    public const string Section = "[houseCARL]";

    /// <summary>Does this mod folder carry the marker? Fail-safe on ABSENCE; a meta.ini that exists and cannot be READ throws rather than reading as not-owned.</summary>
    public static bool MarksOwned(string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return false;
        var meta = Path.Combine(folder, "meta.ini");
        if (!File.Exists(meta)) return false;
        bool inMarker = false;
        foreach (var raw in File.ReadLines(meta))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
                inMarker = line.Equals(Section, StringComparison.OrdinalIgnoreCase);
            else if (inMarker && line.Replace(" ", "").Equals("generated=true", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

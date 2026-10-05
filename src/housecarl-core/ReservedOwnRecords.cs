using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>The in-place pre-flight that refuses a plugin defining its own records below the engine-reserved 0x800 floor (vanilla / Creation Club shapes).</summary>
public static class ReservedOwnRecords
{
    /// <summary>How many FormKeys a refusal names; the rest are counted.</summary>
    const int Shown = 3;

    /// <summary>The target's own records (not overrides of its masters) below the floor its header allows, read as FormKeys off the overlay's record headers: the count and the first few.
    /// The floor is Mutagen's own rule: 0 (no check) when the header version allows the low range (1.71+ on SE), else 0x800. Throws when the file cannot be read.</summary>
    public static (int Count, IReadOnlyList<FormKey> First) Find(string pluginPath)
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(pluginPath));
        var floor = Math.Min(ov.GetDefaultInitialNextFormID(forceUseLowerFormIDRanges: null), FormIdRange.EngineReservedFloor);
        var self = ov.ModKey;
        var first = new List<FormKey>(Shown);
        int count = 0;
        if (floor == 0) return (0, first);
        foreach (var rec in ov.EnumerateMajorRecords())
        {
            var fk = rec.FormKey;
            if (fk.ModKey != self || fk.ID >= floor) continue;
            if (count++ < Shown) first.Add(fk);
        }
        return (count, first);
    }

    /// <summary>The refusal sentence for an in-place write on <paramref name="pluginPath"/>, or null when it defines no record of its own below 0x800; a file the check cannot read is refused too, never passed.</summary>
    public static string? RefusalFor(string pluginPath, string pluginFileName, string laneClause)
    {
        int count;
        IReadOnlyList<FormKey> first;
        try { (count, first) = Find(pluginPath); }
        catch (Exception ex)
        {
            return $"refused — houseCARL could not read '{pluginFileName}' to check it for records of its own below " +
                   $"0x{FormIdRange.EngineReservedFloor:X} ({ex.GetType().Name}: {ex.Message}), so NOTHING was written; close any " +
                   "program holding the file (xEdit, the Creation Kit) and re-call, or check the file loads in xEdit.";
        }
        if (count == 0) return null;
        var shown = string.Join(", ", first.Select(FormIdToken.Of)) + (count > Shown ? $", … (+{count - Shown} more)" : "");
        return $"refused — '{pluginFileName}' defines {count} record(s) of its own below 0x{FormIdRange.EngineReservedFloor:X} " +
               $"({shown}) with a header version below 1.71, the engine-reserved range vanilla and Creation Club masters use, " +
               $"so houseCARL does not edit it in place and NOTHING was written. {laneClause}";
    }
}

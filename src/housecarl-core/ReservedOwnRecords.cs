using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>The in-place pre-flight that refuses a plugin defining its own records below the engine-reserved 0x800 floor (vanilla / Creation Club shapes).</summary>
public static class ReservedOwnRecords
{
    /// <summary>The target's own records (not overrides of its masters) with an object ID below 0x800, read as FormKeys off the overlay's record headers; null when the file cannot be opened, which the write's own parse gate then refuses.</summary>
    public static IReadOnlyList<FormKey>? Find(string pluginPath)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(pluginPath));
            var self = ov.ModKey;
            var hits = new List<FormKey>();
            foreach (var rec in ov.EnumerateMajorRecords())
            {
                var fk = rec.FormKey;
                if (fk.ModKey == self && fk.ID < FormIdRange.EngineReservedFloor) hits.Add(fk);
            }
            return hits;
        }
        catch { return null; }
        finally { if (ov is IDisposable d) { try { d.Dispose(); } catch { } } }
    }

    /// <summary>The refusal sentence for an in-place write on <paramref name="pluginPath"/>, or null when it defines no record of its own below 0x800.</summary>
    public static string? RefusalFor(string pluginPath, string pluginFileName, string laneClause)
    {
        var hits = Find(pluginPath);
        if (hits is null || hits.Count == 0) return null;
        var shown = string.Join(", ", hits.Take(3).Select(FormIdToken.Of)) + (hits.Count > 3 ? $", … (+{hits.Count - 3} more)" : "");
        return $"refused — '{pluginFileName}' defines {hits.Count} record(s) of its own below 0x{FormIdRange.EngineReservedFloor:X} " +
               $"({shown}), the engine-reserved range vanilla and Creation Club masters use, so houseCARL does not edit it in place " +
               $"and NOTHING was written. {laneClause}";
    }
}

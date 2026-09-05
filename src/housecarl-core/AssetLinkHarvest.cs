using System.Collections;
using System.Reflection;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

// ======================================================================
//  AssetLinkHarvest — every asset path a set of records declares, by construction.
//
//  A generic IAssetLinkGetter walk over each record's property graph (lists and substructs
//  included), never a per-record-type field list: a record type Mutagen models carries its
//  asset links here with no edit. It reads paths and nothing else — it opens no file, decides
//  no precedence and places nothing; what to do with a path is the caller's.
// ======================================================================

public static class AssetLinkHarvest
{
    /// <summary>Harvest every asset path the given records list — the generic <see cref="IAssetLinkGetter"/> walk over
    /// each record's property graph (lists + substructs included), by construction rather than a per-type field list.
    /// Paths come back Data-relative (Mutagen's DataRelativePath — e.g. a Model.File 'x.nif' → 'meshes\x.nif').</summary>
    public static IReadOnlyList<string> HarvestAssetPaths(IEnumerable<IMajorRecordGetter> records)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rec in records)
            HarvestFrom(rec, paths, seen, new HashSet<object>(ReferenceEqualityComparer.Instance), depth: 0);
        return paths;
    }

    static void HarvestFrom(object node, List<string> paths, HashSet<string> seen, HashSet<object> visited, int depth)
    {
        if (depth > 6 || node is string || !visited.Add(node)) return;

        if (node is IAssetLinkGetter asset)
        {
            string? rel = null;
            try { rel = asset.DataRelativePath.Path; } catch { /* an unset link — nothing to harvest */ }
            if (!string.IsNullOrWhiteSpace(rel) && seen.Add(rel)) paths.Add(rel);
            return;
        }

        if (node is IEnumerable en and not IFormLinkGetter)
        {
            foreach (var el in en)
                if (el is not null) HarvestFrom(el, paths, seen, visited, depth + 1);
            return;
        }

        // Only descend Mutagen model types (their namespace), never arbitrary BCL values — keeps the walk cheap + safe.
        var t = node.GetType();
        if (t.Namespace is null || !t.Namespace.StartsWith("Mutagen.Bethesda", StringComparison.Ordinal)) return;
        if (node is IFormLinkGetter) return;                              // a link is an identity, not an asset container

        foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length != 0 || prop.GetGetMethod() is null) continue;
            object? val;
            try { val = prop.GetValue(node); } catch { continue; }
            if (val is null) continue;
            HarvestFrom(val, paths, seen, visited, depth + 1);
        }
    }
}

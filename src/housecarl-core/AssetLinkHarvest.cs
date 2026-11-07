using System.Collections;
using System.Reflection;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

// AssetLinkHarvest — every asset path a set of records declares, by construction: a generic IAssetLinkGetter walk
// over each record's property graph, never a per-record-type field list. It reads paths and nothing else.

public static class AssetLinkHarvest
{
    /// <summary>Harvest every asset path the given records list. Paths come back Data-relative (Mutagen's DataRelativePath).</summary>
    public static IReadOnlyList<string> HarvestAssetPaths(IEnumerable<IMajorRecordGetter> records)
        => HarvestAssetPaths(records, out _);

    /// <summary>The same harvest, with <paramref name="unread"/> naming the first list Mutagen could not parse (null
    /// when every list read): the paths before it, and in every other part, are still answered.</summary>
    public static IReadOnlyList<string> HarvestAssetPaths(IEnumerable<IMajorRecordGetter> records, out string? unread)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        unread = null;
        foreach (var rec in records)
            HarvestFrom(rec, paths, seen, new HashSet<object>(ReferenceEqualityComparer.Instance), depth: 0, ref unread);
        return paths;
    }

    static void HarvestFrom(object node, List<string> paths, HashSet<string> seen, HashSet<object> visited, int depth, ref string? unread)
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
            // A lazy list parses as it is stepped, so a step that throws ends this list, and is said.
            var it = en.GetEnumerator();
            using var _ = it as IDisposable;
            while (true)
            {
                object? el;
                try
                {
                    if (!it.MoveNext()) break;
                    el = it.Current;
                }
                catch (Exception ex) when (RecordParseFault.Is(ex))
                {
                    unread ??= $"{ex.GetType().Name}: {ex.Message}";
                    break;
                }
                if (el is not null) HarvestFrom(el, paths, seen, visited, depth + 1, ref unread);
            }
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
            HarvestFrom(val, paths, seen, visited, depth + 1, ref unread);
        }
    }
}

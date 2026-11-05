using System.Collections;
using System.Reflection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Pex;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>One file a record or file leads to: its Data path, the edge that reached it, whether the game finds it by
/// FormID (a renumber carries it rather than placing it), and a note the row carries.</summary>
public sealed record WalkEdge(string Path, string Edge, bool Keyed = false, string? Note = null);

/// <summary>The implicit record → file edges of the asset walk (docs/architecture/assets.md, Walking the links): each
/// from a door that already exists — Mutagen's inferred and resolved asset links, the template face root, the fragment
/// file name the dialogue check reads, and a compiled script's parent. None is a new path rule.</summary>
public static class AssetWalkEdges
{
    /// <summary>Mutagen's inferred links of a record (scripts, a quest's .seq, weight-variant models), the .psc source
    /// dropped because the game reads the .pex, and a ':' in a script name mapped to a folder as the script checks do.</summary>
    public static IEnumerable<WalkEdge> Inferred(IMajorRecordGetter body, IAssetLinkCache cache)
    {
        foreach (var l in body.EnumerateAssetLinks(AssetLinkQuery.Inferred, cache))
        {
            var p = l.DataRelativePath.Path;
            if (p.EndsWith(".psc", StringComparison.OrdinalIgnoreCase)) continue;
            if (p.EndsWith(".pex", StringComparison.OrdinalIgnoreCase))
                yield return new WalkEdge(ScriptPex(p[(p.IndexOf('\\') + 1)..^".pex".Length]), "script");
            else if (p.EndsWith(".seq", StringComparison.OrdinalIgnoreCase))
                yield return new WalkEdge(p, "seq", Keyed: true);
            else
                yield return new WalkEdge(p, "inferred");
        }
    }

    /// <summary>The compiled script a class name runs from: <c>Scripts\&lt;name&gt;.pex</c>, a ':' mapped to a folder
    /// (DialogueScriptCheck, ScriptPropertyCheck).</summary>
    public static string ScriptPex(string className) => $@"Scripts\{className.Trim().Replace(':', '\\')}.pex";

    /// <summary>The fragment files a record's script adapter names (a quest's, an INFO's, a scene's, a perk's, a
    /// package's): every string <c>FileName</c> in the adapter, found by reflection so no adapter type is listed.
    /// Mutagen infers none of them (research 9.3), so each carries that note.</summary>
    public static IEnumerable<WalkEdge> Fragments(IMajorRecordGetter body)
    {
        var vmad = body.GetType().GetProperty("VirtualMachineAdapter")?.GetValue(body);
        if (vmad is null) yield break;
        var names = new List<string>();
        FileNames(vmad, names, new HashSet<object>(ReferenceEqualityComparer.Instance), 0);
        foreach (var n in names.Distinct(StringComparer.OrdinalIgnoreCase))
            yield return new WalkEdge(ScriptPex(n), "fragment",
                Note: "a script fragment file: Mutagen infers no link for it, so it is read off the adapter's FileName");
    }

    static void FileNames(object node, List<string> names, HashSet<object> seen, int depth)
    {
        if (depth > 4 || node is string || !seen.Add(node)) return;
        if (node is IEnumerable en)
        {
            foreach (var el in en) if (el is not null) FileNames(el, names, seen, depth + 1);
            return;
        }
        var t = node.GetType();
        if (t.Namespace is null || !t.Namespace.StartsWith("Mutagen.Bethesda", StringComparison.Ordinal)) return;
        foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // Only a FileName string or a class worth descending is read, so no scalar is boxed or parsed for nothing.
            bool fileName = prop.Name == "FileName" && prop.PropertyType == typeof(string);
            if (prop.GetIndexParameters().Length != 0 || !(fileName || (prop.PropertyType.IsClass || prop.PropertyType.IsInterface) && prop.PropertyType != typeof(string))) continue;
            object? val;
            try { val = prop.GetValue(node); } catch { continue; }
            if (val is null) continue;
            if (fileName) { if (val is string s && !string.IsNullOrWhiteSpace(s)) names.Add(s); }
            else FileNames(val, names, seen, depth + 1);
        }
    }

    /// <summary>The voice .fuz paths Mutagen resolves for one dialogue line through its context (speaker, conditions,
    /// scene, the quest's voice-type gating).</summary>
    public static IReadOnlyList<string> Voice(IDialogResponsesGetter info, IAssetLinkCache cache)
        => info.EnumerateAssetLinks(AssetLinkQuery.Resolved, cache)
               .Select(l => l.DataRelativePath.Path)
               .Where(p => p.StartsWith(@"Sound\Voice\", StringComparison.OrdinalIgnoreCase))
               .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Whether <paramref name="npc"/> speaks this copy of a line: it is the line's Speaker, or a GetIsID run on
    /// the Subject names it and passes for that NPC and fails for any other.</summary>
    public static bool Speaks(IDialogResponsesGetter line, FormKey npc)
    {
        if (!line.Speaker.IsNull && line.Speaker.FormKey == npc) return true;
        foreach (var c in line.Conditions)
            if (c is IConditionFloatGetter f && f.Data is IGetIsIDConditionDataGetter { RunOnType: Condition.RunOnType.Subject } id
                && id.Object.Link.FormKey == npc && Holds(f.CompareOperator, 1f, f.ComparisonValue) && !Holds(f.CompareOperator, 0f, f.ComparisonValue))
                return true;
        return false;
    }

    static bool Holds(CompareOperator op, float x, float v) => op switch
    {
        CompareOperator.EqualTo => x == v,
        CompareOperator.NotEqualTo => x != v,
        CompareOperator.GreaterThan => x > v,
        CompareOperator.GreaterThanOrEqualTo => x >= v,
        CompareOperator.LessThan => x < v,
        CompareOperator.LessThanOrEqualTo => x <= v,
        _ => false,
    };

    /// <summary>A compiled script's parent class as the .pex it runs from, or null for none; throws when the bytes are no .pex.</summary>
    public static string? ParentPex(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        var pex = PexFile.CreateFromStream(ms, GameCategory.Skyrim);
        var parents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        PapyrusClassParents.AddFromPex(parents, pex);
        return parents.Values.FirstOrDefault(p => p.Length > 0) is { } parent ? ScriptPex(parent) : null;
    }

    /// <summary>An empty asset-link cache, for the inferred query, which reads only the record.</summary>
    public static IAssetLinkCache EmptyCache() =>
        new SkyrimMod(ModKey.FromFileName("HousecarlWalk.esp"), SkyrimRelease.SkyrimSE).ToImmutableLinkCache().CreateImmutableAssetLinkCache();
}

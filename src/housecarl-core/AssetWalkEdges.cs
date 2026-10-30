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
    /// <summary>A cap on a Traits template chain, far past any real one, so a malformed order cannot spin.</summary>
    const int TemplateChainCap = 64;

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
            if (prop.GetIndexParameters().Length != 0) continue;
            object? val;
            try { val = prop.GetValue(node); } catch { continue; }
            if (val is null) continue;
            if (prop.Name == "FileName" && val is string s) { if (!string.IsNullOrWhiteSpace(s)) names.Add(s); }
            else if (prop.PropertyType.IsClass && prop.PropertyType != typeof(string)) FileNames(val, names, seen, depth + 1);
        }
    }

    /// <summary>The NPCs whose FaceGen an NPC's face is drawn from: itself with the Traits template flag off, else the
    /// first NPC down its Template chain with it off, every member of a leveled-list template once each. A loop or a
    /// link that does not resolve is an error naming the chain.</summary>
    public static (IReadOnlyList<INpcGetter> Roots, string? Error) FaceRoots(INpcGetter npc, Func<FormKey, IMajorRecordGetter?> fetch)
    {
        var roots = new List<INpcGetter>();
        string? error = null;
        void Walk(INpcGetter n, List<string> chain)
        {
            if (error is not null) return;
            if (!n.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits) || n.Template.IsNull)
            {
                if (!roots.Any(r => r.FormKey == n.FormKey)) roots.Add(n);
                return;
            }
            var next = n.Template.FormKey;
            var at = string.Join(" → ", chain.Append(FormIdToken.Of(next)));
            if (chain.Count >= TemplateChainCap || chain.Contains(FormIdToken.Of(next)))
            { error = $"the Traits template chain loops: {at}"; return; }
            var body = fetch(next);
            if (body is INpcGetter t) { Walk(t, chain.Append(FormIdToken.Of(next)).ToList()); return; }
            if (body is ILeveledNpcGetter lv)
            {
                foreach (var e in lv.Entries ?? Array.Empty<ILeveledNpcEntryGetter>())
                {
                    if (e.Data?.Reference is not { IsNull: false } r) continue;
                    var link = chain.Append(FormIdToken.Of(next)).Append(FormIdToken.Of(r.FormKey)).ToList();
                    if (fetch(r.FormKey) is INpcGetter m) Walk(m, link);
                    else { error = $"the Traits template chain ends on a record that does not resolve to an NPC: {string.Join(" → ", link)}"; return; }
                }
                return;
            }
            error = $"the Traits template chain ends on a record that does not resolve: {at}";
        }
        Walk(npc, new List<string> { FormIdToken.Of(npc.FormKey) });
        return (roots, error);
    }

    /// <summary>An NPC's own FaceGen files by Mutagen's resolved link, over a cache holding only its race: none when the
    /// race lacks FaceGenHead or does not resolve, which <see cref="FaceGenPath.For"/> cannot tell.</summary>
    public static IReadOnlyList<string> FaceGen(INpcGetter npc, IRaceGetter? race)
    {
        if (race is null) return Array.Empty<string>();
        var mod = new SkyrimMod(race.FormKey.ModKey, SkyrimRelease.SkyrimSE);
        mod.Races.Add(race.DeepCopy());
        var cache = mod.ToImmutableLinkCache().CreateImmutableAssetLinkCache();
        return npc.EnumerateAssetLinks(AssetLinkQuery.Resolved, cache)
                  .Select(l => l.DataRelativePath.Path)
                  .Where(p => p.Contains(@"\facegendata\", StringComparison.OrdinalIgnoreCase))
                  .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The voice .fuz paths Mutagen resolves for one dialogue line through its context (speaker, conditions,
    /// scene, the quest's voice-type gating).</summary>
    public static IReadOnlyList<string> Voice(IDialogResponsesGetter info, IAssetLinkCache cache)
        => info.EnumerateAssetLinks(AssetLinkQuery.Resolved, cache)
               .Select(l => l.DataRelativePath.Path)
               .Where(p => p.StartsWith(@"Sound\Voice\", StringComparison.OrdinalIgnoreCase))
               .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

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

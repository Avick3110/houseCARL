using Mutagen.Bethesda.Plugins;

namespace HousecarlCore;

/// <summary>The one sentence a write emits when the record it overrides is already overridden by another plugin whose
/// copy can beat the one being written — Skyrim applies only the last-loaded copy. Two arms: a patch with a POSITION
/// names every active plugin at or below it, whoever wrote it; a NEW patch names only plugins houseCARL made, judged
/// by the meta.ini ownership marker. Containers count, and the bound is the ACTIVE order. A warning, never a block.</summary>
public static class ForkWarning
{
    /// <summary>How many plugin names the sentence spells before it counts the rest; the ones kept are the
    /// LAST-loaded, because those are the copies that beat the write.</summary>
    const int NamesShown = 3;

    /// <summary>How far the container climb walks before giving up — a ceiling, not a claim.</summary>
    const int MaxContainerDepth = 8;

    /// <summary>The warning for the records <paramref name="targets"/> names, and for the containers overriding those
    /// records drags in, or null when none of them is already overridden elsewhere. A provider never counts when it is
    /// the patch being written or the record's own defining plugin.</summary>
    public static string? For(LoadOrderResolver.IndexView view, IEnumerable<FormKey> targets, string patchFileName)
    {
        int patchIdx = view.OrderIndexOf(patchFileName);       // -1 = a new patch: no position yet
        bool fresh = patchIdx < 0;
        var ownedCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var forkers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int forkedRecords = 0;
        // The remedy may name ONE plugin only when every affected record's answer IS that plugin; per record the
        // answer is its last-loaded forker.
        string? sole = null;
        bool uniform = true;
        var seen = new HashSet<FormKey>();
        var here = new List<string>();
        foreach (var fk in WithContainers(view, targets))
        {
            if (!seen.Add(fk)) continue;
            var touching = view.TouchingPlugins(fk);
            if (touching is null || touching.Count < 2) continue;
            var definer = fk.ModKey.FileName.String;
            here.Clear();
            foreach (var p in touching)                        // priority order, so here[^1] is the last-loaded
            {
                if (string.Equals(p, patchFileName, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(p, definer, StringComparison.OrdinalIgnoreCase)) continue;
                int idx = view.OrderIndexOf(p);
                if (fresh)
                {
                    if (!IsOwnedPatch(view, p, ownedCache)) continue;   // not the same-project hazard this arm names
                }
                else if (idx < patchIdx) continue;                      // it loads above the patch, so the patch wins outright
                forkers[p] = idx;
                here.Add(p);
            }
            if (here.Count == 0) continue;
            forkedRecords++;
            var pick = here[^1];
            if (sole is null) sole = pick;
            else if (!string.Equals(sole, pick, StringComparison.OrdinalIgnoreCase)) uniform = false;
        }
        if (forkers.Count == 0) return null;

        var names = forkers.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();
        string subject = forkedRecords == 1 ? "this record is" : $"{forkedRecords} of the records written here are";
        // In place the target usually already carries the record, so the copy is out-loaded rather than forked.
        string middle = fresh
            ? $"only the last-loaded copy of a record applies, so this write forks {(forkedRecords == 1 ? "it" : "them")} rather than adding to that copy"
            : $"only the last-loaded copy of a record applies, so what this write puts in is out-loaded by that copy rather than applying";
        // The remedy names the lane that can actually edit that plugin: on the fresh arm every name is a houseCARL
        // patch by construction, on the positioned arm the name may be anyone's.
        string remedy;
        if (uniform && sole is not null)
            remedy = fresh
                ? $"pass into=\"{sole}\" to build on that copy instead."
                : $"to build on that copy instead, pass into=\"{sole}\" if it is a houseCARL patch, else in_place=\"{sole}\".";
        else
            remedy = "they do not all answer to the same plugin, so route each record into whichever of those " +
                     "loads last over it" +
                     (fresh ? "" : " (into= if it is a houseCARL patch, else in_place=)") +
                     " — read the conflict tree (project.form='tree') to see which.";
        return $"{subject} already overridden by {Names(names)} — {middle}; {remedy}";
    }

    /// <summary>Each target, then every record that CONTAINS it, because overriding a nested record drags its
    /// container into the patch as an override too. One O(1) containment lookup per hop off the captured view.</summary>
    static IEnumerable<FormKey> WithContainers(LoadOrderResolver.IndexView view, IEnumerable<FormKey> targets)
    {
        foreach (var fk in targets)
        {
            yield return fk;
            var at = fk;
            for (int hop = 0; hop < MaxContainerDepth; hop++)
            {
                if (view.ParentOf(at) is not { } parent || parent == at) break;
                yield return parent;
                at = parent;
            }
        }
    }

    /// <summary>Is this active plugin one houseCARL made — a patch <c>into=</c> could extend? One meta.ini read,
    /// memoised per call. A meta.ini that exists but cannot be read is caught and read as not-owned here, because a
    /// fault deciding an advisory sentence must not cost the caller an otherwise-valid write.</summary>
    static bool IsOwnedPatch(LoadOrderResolver.IndexView view, string plugin, Dictionary<string, bool> cache)
    {
        if (cache.TryGetValue(plugin, out bool known)) return known;
        bool owned;
        try
        {
            var path = view.PluginPath(plugin);
            owned = path is not null && HousecarlOwnerMeta.MarksOwned(Path.GetDirectoryName(path));
        }
        catch (IOException) { owned = false; }
        catch (UnauthorizedAccessException) { owned = false; }
        cache[plugin] = owned;
        return owned;
    }

    /// <summary>"A.esp", "A.esp and B.esp", "A.esp, B.esp and C.esp" — and past that the LAST-loaded three, with the
    /// earlier ones counted.</summary>
    static string Names(IReadOnlyList<string> names)
    {
        if (names.Count == 1) return names[0];
        if (names.Count <= NamesShown)
            return string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
        int elided = names.Count - NamesShown;
        var shown = names.Skip(elided).ToList();
        return $"{elided} earlier plugin(s), then " + string.Join(", ", shown.Take(shown.Count - 1)) + " and " + shown[^1];
    }
}

using Mutagen.Bethesda.Plugins;

namespace HousecarlCore;

// ======================================================================
//  ForkWarning — the write-time "you are forking this record" sentence (#565).
//
//  Skyrim does not merge competing overrides of one record: only the last-loaded copy applies. So a
//  write that overrides a record ANOTHER patch already overrides produces two forks of it, and one of
//  them is dead — silently, with both writes reporting success.
//
//  TWO ARMS, because the hazard is not the same on both.
//
//   * The patch has a POSITION (into= an existing patch, or in place): any plugin overriding the record
//     at or below that position out-loads what is being written, so the edit can be the dead copy.
//     Every such plugin is named, whoever wrote it.
//
//   * The patch is NEW, so it has no position yet: it will sort to the top and it has just copied the
//     WINNER's body in, so a vanilla master or a third-party mod overriding the record is not a hazard —
//     its content came along. The hazard #565 describes is a SIBLING PATCH: another houseCARL patch,
//     possibly not enabled yet and therefore not the winner, whose content this write did not carry.
//     So the fresh arm fires only for a plugin that houseCARL made — judged by the meta.ini ownership
//     marker, the same gate into= itself is decided by (HousecarlOwnerMeta.MarksOwned), never a name
//     heuristic.
//
//  This is a WARNING, never a block: deliberate forking is legitimate, and the caller may already know.
//  It costs no scan. The provider list is the O(1) index lookup the read path's conflict tree is built
//  from (TouchingPlugins), read off the SAME captured view the write already resolved its winners
//  against, and the ownership marker is read once per candidate PLUGIN, of which a record has a handful.
// ======================================================================

/// <summary>The one sentence a write emits when the record it overrides is already overridden by another plugin
/// whose copy can beat the one being written.</summary>
public static class ForkWarning
{
    /// <summary>The warning for the records <paramref name="targets"/> names, or null when none of them is forked.
    /// A provider never counts when it is the patch being written (<paramref name="patchFileName"/>) or the record's
    /// own defining plugin; past that the two arms above decide.</summary>
    public static string? For(LoadOrderResolver.IndexView view, IEnumerable<FormKey> targets, string patchFileName)
    {
        int patchIdx = view.OrderIndexOf(patchFileName);       // -1 = a new patch: no position yet
        bool fresh = patchIdx < 0;
        var ownedCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var forkers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int forkedRecords = 0;
        var seen = new HashSet<FormKey>();
        foreach (var fk in targets)
        {
            if (!seen.Add(fk)) continue;
            var touching = view.TouchingPlugins(fk);
            if (touching is null || touching.Count < 2) continue;
            var definer = fk.ModKey.FileName.String;
            bool anyHere = false;
            foreach (var p in touching)
            {
                if (string.Equals(p, patchFileName, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(p, definer, StringComparison.OrdinalIgnoreCase)) continue;
                int idx = view.OrderIndexOf(p);
                if (fresh)
                {
                    if (!IsOwnedPatch(view, p, ownedCache)) continue;   // its content came along with the winner
                }
                else if (idx < patchIdx) continue;                      // it loads above the patch, so the patch wins outright
                forkers[p] = idx;
                anyHere = true;
            }
            if (anyHere) forkedRecords++;
        }
        if (forkers.Count == 0) return null;

        var names = forkers.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();
        var last = names[^1];                                   // the last-loaded of them: the copy that currently applies
        // The remedy names the lane that can actually edit that plugin. On the fresh arm every name is a houseCARL
        // patch by construction, so into= is the whole answer; on the positioned arm the name may be anyone's.
        var remedy = fresh
            ? $"pass into=\"{last}\" to build on that copy instead."
            : $"to build on that copy instead, pass into=\"{last}\" if it is a houseCARL patch, else in_place=\"{last}\".";
        return $"{(forkedRecords == 1 ? "this record is" : $"{forkedRecords} of the records written here are")} already " +
               $"overridden by {Names(names)} — only the last-loaded copy of a record applies, so this write forks it " +
               $"rather than adding to that copy; {remedy}";
    }

    /// <summary>Is this active plugin one houseCARL made — a patch <c>into=</c> could extend? The marker sits in the
    /// mod folder the plugin was loaded from, so the answer is one meta.ini read, memoised per call because several
    /// records of one write share their providers.</summary>
    static bool IsOwnedPatch(LoadOrderResolver.IndexView view, string plugin, Dictionary<string, bool> cache)
    {
        if (cache.TryGetValue(plugin, out bool known)) return known;
        var path = view.PluginPath(plugin);
        bool owned = path is not null && HousecarlOwnerMeta.MarksOwned(Path.GetDirectoryName(path));
        cache[plugin] = owned;
        return owned;
    }

    /// <summary>"A.esp", "A.esp and B.esp", "A.esp, B.esp and C.esp".</summary>
    static string Names(IReadOnlyList<string> names) => names.Count == 1
        ? names[0]
        : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
}

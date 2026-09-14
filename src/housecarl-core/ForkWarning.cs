using Mutagen.Bethesda.Plugins;

namespace HousecarlCore;

// ======================================================================
//  ForkWarning — the write-time "you are forking this record" sentence (#565).
//
//  Skyrim does not merge competing overrides of one record: only the last-loaded copy applies. So a
//  write that overrides a record ANOTHER patch already overrides produces two forks of it, and one of
//  them is dead — silently, with both writes reporting success.
//
//  This is a WARNING, never a block: deliberate forking is legitimate, and the caller may already know.
//  It costs no extra scan — the provider list is the O(1) index lookup the read path's conflict tree is
//  built from (TouchingPlugins), read off the SAME captured view the write already resolved its winners
//  against.
// ======================================================================

/// <summary>The one sentence a write emits when the record it overrides is already overridden by another plugin
/// that can out-load the patch being written.</summary>
public static class ForkWarning
{
    /// <summary>The warning for the records <paramref name="targets"/> names, or null when none of them is forked.
    /// A provider counts only when it is not the patch being written (<paramref name="patchFileName"/>), not the
    /// record's own defining plugin, and sits AT OR BELOW the patch in the load order — or the patch is new and has
    /// no position yet, where every other override can out-load it.</summary>
    public static string? For(LoadOrderResolver.IndexView view, IEnumerable<FormKey> targets, string patchFileName)
    {
        int patchIdx = view.OrderIndexOf(patchFileName);       // -1 = a new patch: position unknown, so every provider can win
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
                if (patchIdx >= 0 && idx < patchIdx) continue;  // it loads above the patch, so the patch wins outright
                forkers[p] = idx;
                anyHere = true;
            }
            if (anyHere) forkedRecords++;
        }
        if (forkers.Count == 0) return null;

        var names = forkers.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();
        var last = names[^1];                                   // the last-loaded of them: the copy that currently applies
        // The remedy names its own bound: into= extends a patch houseCARL wrote, and the plugin named here may be
        // anyone's — a vanilla master, a third-party mod — so the sentence says which lane each case takes.
        return $"{(forkedRecords == 1 ? "this record is" : $"{forkedRecords} of the records written here are")} already " +
               $"overridden by {Names(names)} — only the last-loaded copy of a record applies, so this write forks it " +
               $"rather than adding to that copy; to build on that copy instead, pass into=\"{last}\" if it is a " +
               $"houseCARL patch, else in_place=\"{last}\".";
    }

    /// <summary>"A.esp", "A.esp and B.esp", "A.esp, B.esp and C.esp".</summary>
    static string Names(IReadOnlyList<string> names) => names.Count == 1
        ? names[0]
        : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
}

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
//   * The patch is NEW, so it has no position yet, and it has just copied the WINNER's body in. The
//     hazard #565 describes is a SIBLING PATCH — another plugin of the same project — so the fresh arm
//     fires only for a plugin houseCARL made, judged by the meta.ini ownership marker into= itself is
//     decided by (HousecarlOwnerMeta.MarksOwned), never a name heuristic. A third-party mod overriding
//     the record is deliberately not named here: that is a different question (where the new patch will
//     sort), and this sentence never claims the new patch will win.
//
//  BOUND — ACTIVE PLUGINS ONLY. The provider list comes from the load-order index, which holds the
//  ACTIVE order, so a sibling patch that is installed but unticked in plugins.txt cannot be named: its
//  records are not indexed, and finding them would mean opening every installed plugin on every write.
//  The warning therefore covers active overriders, and says nothing about an unticked one either way.
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
    /// <summary>How many plugin names the sentence spells before it counts the rest — the same reason the contested-
    /// parent block is bounded: a bulk write over hundreds of conflicted records must not turn one warning into
    /// kilobytes of names, and this line is appended outside the renders' row budgets.</summary>
    const int NamesShown = 3;

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
        // The remedy may name ONE plugin only when every forked record is forked by that same one plugin: otherwise
        // a single into= moves records into a patch that still forks half of them — the caller doing the remediation
        // while the bug survives.
        string? sole = null;
        bool uniform = true;
        var seen = new HashSet<FormKey>();
        var here = new List<string>();
        foreach (var fk in targets)
        {
            if (!seen.Add(fk)) continue;
            var touching = view.TouchingPlugins(fk);
            if (touching is null || touching.Count < 2) continue;
            var definer = fk.ModKey.FileName.String;
            here.Clear();
            foreach (var p in touching)
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
            if (here.Count > 1) uniform = false;
            else if (sole is null) sole = here[0];
            else if (!string.Equals(sole, here[0], StringComparison.OrdinalIgnoreCase)) uniform = false;
        }
        if (forkers.Count == 0) return null;

        var names = forkers.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();
        // The remedy names the lane that can actually edit that plugin. On the fresh arm every name is a houseCARL
        // patch by construction, so into= is the whole answer; on the positioned arm the name may be anyone's.
        string remedy;
        if (uniform && sole is not null)
            remedy = fresh
                ? $"pass into=\"{sole}\" to build on that copy instead."
                : $"to build on that copy instead, pass into=\"{sole}\" if it is a houseCARL patch, else in_place=\"{sole}\".";
        else
            remedy = "they are not all forked by the same plugin, so route each record into whichever of those " +
                     "already overrides it — read the conflict tree (project.form='tree') to see which.";
        return $"{(forkedRecords == 1 ? "this record is" : $"{forkedRecords} of the records written here are")} already " +
               $"overridden by {Names(names)} — only the last-loaded copy of a record applies, so this write forks it " +
               $"rather than adding to that copy; {remedy}";
    }

    /// <summary>Is this active plugin one houseCARL made — a patch <c>into=</c> could extend? The marker sits in the
    /// mod folder the plugin was loaded from, so the answer is one meta.ini read, memoised per call because several
    /// records of one write share their providers.
    /// <para>A meta.ini that exists but cannot be READ (MO2 holding it, an ACL, a folder gone offline) throws out of
    /// the marker, because on the EXTEND gate that fault must not read as not-owned. Here it is caught and read as
    /// not-owned: this is an advisory sentence inside the write pipeline, and a fault deciding whether to print it
    /// must never cost the caller an otherwise-valid write.</para></summary>
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

    /// <summary>"A.esp", "A.esp and B.esp", "A.esp, B.esp and C.esp", then "A.esp, B.esp, C.esp and 7 more".</summary>
    static string Names(IReadOnlyList<string> names)
    {
        if (names.Count == 1) return names[0];
        if (names.Count <= NamesShown)
            return string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
        return string.Join(", ", names.Take(NamesShown)) + $" and {names.Count - NamesShown} more";
    }
}

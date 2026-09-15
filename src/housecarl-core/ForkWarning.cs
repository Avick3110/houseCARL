using Mutagen.Bethesda.Plugins;

namespace HousecarlCore;

// ======================================================================
//  ForkWarning — the write-time "another copy of this record already exists" sentence (#565).
//
//  Skyrim does not merge competing overrides of one record: only the last-loaded copy applies. So a
//  write that overrides a record ANOTHER patch already overrides produces two copies of it, and one of
//  them is dead — silently, with both writes reporting success.
//
//  TWO ARMS, because the hazard is not the same on both.
//
//   * The patch has a POSITION (into= an existing patch, or in place): any plugin overriding the record
//     at or below that position out-loads what is being written, so the edit is the dead copy. Every
//     such plugin is named, whoever wrote it. The sentence says OUT-LOADED, not "forks": in place, the
//     target usually already carries the record and no second copy is made at all.
//
//   * The patch is NEW, so it has no position yet, and it has just copied the WINNER's body in. The
//     hazard #565 describes is a SIBLING PATCH — another plugin of the same project — so the fresh arm
//     fires only for a plugin houseCARL made, judged by the meta.ini ownership marker into= itself is
//     decided by (HousecarlOwnerMeta.MarksOwned), never a name heuristic. A third-party mod overriding
//     the record is deliberately not named here: that is a different question (where the new patch will
//     sort), and this sentence never claims the new patch will win.
//
//  CONTAINERS COUNT. Overriding a nested record drags its container in as an override too — a placed
//  reference or navmesh pulls its CELL (and that cell's WRLD), an INFO pulls its DIAL — and those
//  copies fork exactly as the target's does. The containment edge is an O(1) index lookup, so the
//  question is asked about each target's ancestors as well as the target.
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
    /// kilobytes of names, and this line is appended outside the renders' row budgets. The ones KEPT are the
    /// LAST-loaded, because those are the copies that actually beat the write; the elided ones load earlier.</summary>
    const int NamesShown = 3;

    /// <summary>How far the container climb walks before giving up — a REFR's cell and that cell's worldspace is two,
    /// so this is slack, and it is a ceiling rather than a claim: a containment index that ever cycled would
    /// otherwise spin here.</summary>
    const int MaxContainerDepth = 8;

    /// <summary>The warning for the records <paramref name="targets"/> names — and for the containers overriding
    /// those records drags in — or null when none of them is already overridden elsewhere. A provider never counts
    /// when it is the patch being written (<paramref name="patchFileName"/>) or the record's own defining plugin;
    /// past that the two arms above decide.</summary>
    public static string? For(LoadOrderResolver.IndexView view, IEnumerable<FormKey> targets, string patchFileName)
    {
        int patchIdx = view.OrderIndexOf(patchFileName);       // -1 = a new patch: no position yet
        bool fresh = patchIdx < 0;
        var ownedCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var forkers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int forkedRecords = 0;
        // The remedy may name ONE plugin only when every affected record's answer IS that plugin. Per record the
        // answer is its LAST-LOADED forker — the copy that currently applies, and the one building on fixes the rest
        // of that record's stack. Two records with two different answers have no single into=, and a sentence that
        // named one would have the caller move both and leave the other still forked.
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
        // In place the target usually ALREADY carries the record, so no second copy is made: what is wrong there is
        // that the copy is out-loaded. The fresh arm really is making a second copy.
        string middle = fresh
            ? $"only the last-loaded copy of a record applies, so this write forks {(forkedRecords == 1 ? "it" : "them")} rather than adding to that copy"
            : $"only the last-loaded copy of a record applies, so what this write puts in is out-loaded by that copy rather than applying";
        // The remedy names the lane that can actually edit that plugin. On the fresh arm every name is a houseCARL
        // patch by construction, so into= is the whole answer; on the positioned arm the name may be anyone's — and
        // that holds for the per-record branch too, or it sends the caller into the extend gate's refusal.
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

    /// <summary>Each target, then every record that CONTAINS it — a placed reference's cell, that cell's worldspace,
    /// an INFO's topic. Overriding a nested record drags its container into the patch as an override too, so the
    /// container is forked by the same write and owes the same question. One O(1) containment lookup per hop off the
    /// captured view; a flat record has no parent and pays one miss.</summary>
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

    /// <summary>"A.esp", "A.esp and B.esp", "A.esp, B.esp and C.esp" — and past that the LAST-loaded three, with the
    /// earlier ones counted: the last-loaded copy is the one that beats the write, so it is the one that must be in
    /// the sentence.</summary>
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

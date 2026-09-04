namespace HousecarlCore;

/// <summary>One discovered Papyrus source folder in the MO2 VFS: which loose root PROVIDED it
/// (<paramref name="Provider"/> — an enabled mod's folder name, "overwrite", or "Data", exactly the name
/// <c>AssetResolver</c> gives that root), the absolute folder to hand the compiler, and which of the two
/// on-disk <see cref="PapyrusSourceRoots.Layouts"/> it matched.</summary>
public sealed record PapyrusSourceRoot(string Provider, string Dir, string Layout);

/// <summary>
/// Finds the Papyrus SOURCE folders an MO2 modlist already contains, so housecarl_compile_script can put them on
/// the compiler's import path instead of making the caller retype them on every call (issue #200).
///
/// The issue framed this as "read the import paths configured for the MO2 instance". There are none: MO2 stores no
/// Papyrus import list, and SkyrimEditor.ini's <c>sScriptSourceFolder</c> is a SINGLE folder, not a list. What does
/// exist is the mods themselves — a framework that expects to be compiled against ships its .psc sources inside its
/// own mod folder. So the pickup is a scan of the VFS loose roots, not a config read.
///
/// Two layouts are recognised, both seen in shipped mods:
///   • <c>Source\Scripts</c> — the SE/CK convention (the vanilla sources live at <c>Data\Source\Scripts</c>);
///   • <c>Scripts\Source</c> — the LE convention, still carried by ported and older mods.
/// A folder counts only if it actually holds a top-level <c>.psc</c>; an empty or scripts-only folder contributes
/// nothing (a mod shipping compiled .pex with no sources must not widen the import path for nothing).
///
/// ORDER IS SEMANTICS. The compiler resolves each referenced script to the FIRST match across the import path, so
/// the roots are walked in the caller's given order and emitted in it — handed the AssetResolver's loose roots, that
/// is MO2's own precedence (overwrite → enabled mods highest-priority-first → Data), which is the same tie-break the
/// modlist applies to every other file. Pure apart from the folder check: no MO2 knowledge lives here, so it can be
/// driven against any tree.
/// </summary>
public static class PapyrusSourceRoots
{
    /// <summary>The recognised on-disk source layouts, relative to a loose root, in preference order.</summary>
    public static readonly string[] Layouts = { @"Source\Scripts", @"Scripts\Source" };

    /// <summary>Walk <paramref name="looseRoots"/> IN THE GIVEN ORDER and return every folder that holds Papyrus
    /// sources. Deduped by absolute path (case-insensitive), FIRST occurrence kept — so the higher-precedence root
    /// keeps the slot when two roots resolve to the same folder. Unreadable roots are skipped, never thrown on: this
    /// feeds an ergonomic default, so a permission-denied folder must cost the caller one import dir, not the compile.
    /// The count and the provider names are rendered, so a short list is visible rather than silent.</summary>
    public static IReadOnlyList<PapyrusSourceRoot> Discover(IReadOnlyList<(string Name, string Dir)> looseRoots)
    {
        var found = new List<PapyrusSourceRoot>();
        if (looseRoots is null) return found;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, dir) in looseRoots)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var layout in Layouts)
            {
                string candidate;
                try { candidate = Path.GetFullPath(Path.Combine(dir, layout)); }
                catch { continue; }                       // an un-rootable root (bad chars) costs one candidate, not the scan
                if (!HasSources(candidate)) continue;
                if (!seen.Add(candidate)) continue;       // same folder reached twice → the FIRST (higher-precedence) slot wins
                found.Add(new PapyrusSourceRoot(name, candidate, layout));
            }
        }
        return found;
    }

    /// <summary>Separate the GAME's own vanilla sources — <c>&lt;data&gt;\Source\Scripts</c> and its LE twin — from the
    /// mod roots in a <see cref="Discover"/> result. Returns the mod candidates, plus the game-Data source folder that
    /// was taken out (null if there is none).
    /// <para>Splitting rather than merely DROPPING is load-bearing. The base game is not a mod: left among the
    /// candidates it ranks as one, renders as one, and — being last in precedence, hence the fallback provider for
    /// every unshadowed vanilla name — drags the reference walk through the whole of it. But the compile lane only
    /// appends a vanilla slot when the COMPILER-relative folder resolves, so a silent drop can leave a path with no vanilla
    /// sources at all. Handing the folder back lets the caller use it as the vanilla slot when the compiler-relative
    /// one is missing, which is the only thing that keeps both properties true at once.</para>
    /// <para>The split CANNOT be left to "the caller will notice the folder matches the compiler's vanilla dir". On a
    /// Wabbajack Stock Game setup those are different folders by design — the CK compiler lives in the real Steam
    /// install (the tool-path hints exist for exactly that), while MO2's data dir is the Stock Game copy —
    /// so a compiler-relative comparison silently never fires on the setup it most needs to. Matching is EXACT against
    /// the folders <see cref="Discover"/> would have produced for this data dir, not a path-prefix guess.</para>
    /// <para>A blank <paramref name="dataDir"/> (explicit-paths or unconfigured mode) splits nothing:
    /// <c>Path.Combine</c> would otherwise resolve the layouts against the process CWD and could exclude an unrelated
    /// folder.</para></summary>
    public static (IReadOnlyList<PapyrusSourceRoot> Mods, string? GameDataSources) SplitGameData(
        IReadOnlyList<PapyrusSourceRoot> found, string? dataDir)
    {
        if (string.IsNullOrWhiteSpace(dataDir)) return (found, null);
        var vanilla = new List<string>();
        foreach (var layout in Layouts)
        {
            try { vanilla.Add(Path.GetFullPath(Path.Combine(dataDir, layout))); }
            catch { /* an un-rootable data dir splits nothing — the same best-effort posture as the scan */ }
        }
        var set = new HashSet<string>(vanilla, StringComparer.OrdinalIgnoreCase);
        var mods = found.Where(r => !set.Contains(r.Dir)).ToList();
        // Prefer the layouts' own order (SE before LE), not discovery order, so the answer is stable when a data dir
        // somehow carries both.
        var gameData = vanilla.FirstOrDefault(v => found.Any(r => r.Dir.Equals(v, StringComparison.OrdinalIgnoreCase)));
        return (mods, gameData);
    }

    /// <summary>True iff <paramref name="dir"/> exists and holds at least one top-level <c>.psc</c>. Enumerates
    /// lazily and returns on the FIRST hit, so the check costs one directory entry on a populated folder rather than
    /// a full listing — this runs once per loose root, and a big modlist has thousands.
    /// <para>The explicit <c>EndsWith(".psc")</c> re-check covers the Windows 8.3 short-name rule, under which a
    /// three-character extension pattern can also match longer extensions on a volume with 8.3 name generation
    /// enabled. It costs one string compare, and the failure it prevents — a non-source folder widening every
    /// compile's import path — is silent.</para>
    /// Any I/O failure (denied, vanished mid-walk) reads as "no sources" — best-effort by design.</summary>
    public static bool HasSources(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            foreach (var f in Directory.EnumerateFiles(dir, "*.psc"))
                if (f.EndsWith(".psc", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        catch { return false; }
    }
}

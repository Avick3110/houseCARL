using System.Diagnostics;
using System.Reflection;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

// #459 measurement. Two questions, one process, against a live MO2 order (or a single plugin).
//
//   1. Is the containing parent in hand during LoadOrderResolver.BuildIndex's flat ov.EnumerateMajorRecords()?
//      Answered per child-bearing property (WriteEngine.ChildBearingProperties), by asking a REAL child body of
//      each kind whether anything on it names its parent.
//   2. What does the containment-aware second pass cost — wall time and retained memory — beside the flat pass
//      the index build already runs, both fused into ONE overlay open per plugin. Measured three ways: a hand
//      containment walk off ChildBearingProperties, the same map stored packed, and Mutagen's own
//      EnumerateMajorRecordContexts (which carries the parent and needs no link cache).
//
//   dotnet run --project src/housecarl-generator parent-in-hand [instanceDir]
//   dotnet run --project src/housecarl-generator parent-in-hand --plugin <path> [dataDir]
static class ParentInHandProbe
{
    public static int Run(string[] args)
    {
        var paths = ResolvePaths(args, out var label, out var dataDir);
        if (paths is null) return 1;
        Console.WriteLine($"parent-in-hand: {label} — {paths.Count} plugin(s)\n");

        // A discarded warm-up sweep runs first. Without it the first timed pass alone pays the JIT of both
        // enumerators and the cold file cache for every plugin, which is bigger than the difference being measured.
        Sweep(paths, dataDir, new WarmupPass(), "warm-up");
        Console.WriteLine("warm-up sweep done and discarded; every timing below is warm.\n");

        // ---- pass A: the flat walk BuildIndex runs today ------------------------------------------------------
        var flat = new FlatPass();
        var passA = Sweep(paths, dataDir, flat, "A");

        Console.WriteLine($"A. flat pass (today's BuildIndex walk)");
        Console.WriteLine($"   records          : {flat.Records:N0}");
        Console.WriteLine($"   wall             : {passA.Wall.TotalSeconds:N2} s");
        Console.WriteLine($"   retained after   : {passA.RetainedMb:N1} MB   (FormKey buffer only, as today)");
        Console.WriteLine($"   plugins excluded : {passA.Skipped}\n");

        // ---- pass B: flat + containment, fused into the SAME open ---------------------------------------------
        var both = new FusedPass();
        var passB = Sweep(paths, dataDir, both, "B");

        Console.WriteLine($"B. fused pass (flat walk + containment walk, one open per plugin)");
        Console.WriteLine($"   records          : {both.Records:N0}");
        Console.WriteLine($"   parents visited  : {both.Parents:N0}");
        Console.WriteLine($"   child->parent    : {both.Map.Count:N0} distinct children");
        Console.WriteLine($"   wall             : {passB.Wall.TotalSeconds:N2} s");
        Console.WriteLine($"   retained after   : {passB.RetainedMb:N1} MB   (the child->parent map)");
        Console.WriteLine($"   plugins excluded : {passB.Skipped}");
        Console.WriteLine($"   MARGINAL time    : {(passB.Wall - passA.Wall).TotalSeconds:N2} s more than the flat pass " +
                          $"(B in total is {(passA.Wall.TotalSeconds > 0 ? (passB.Wall.TotalSeconds / passA.Wall.TotalSeconds) : 0):N2}x A)");
        Console.WriteLine($"   MARGINAL memory  : {(passB.RetainedMb - passA.RetainedMb):N1} MB\n");

        // ---- pass C: the same map, packed --------------------------------------------------------------------
        // A FormKey is a ModKey (a string reference + a type enum) plus a uint, so a Dictionary<FormKey,FormKey>
        // pays for two of those per entry. The index already has a dense plugin index, so pack (modIdx, id) into
        // one ulong and measure what the SAME map costs stored that way.
        var packed = new PackedPass();
        var passC = Sweep(paths, dataDir, packed, "C");
        Console.WriteLine($"C. packed map (ulong -> ulong, (modIdx<<32|id)); same work as B, map stored packed");
        Console.WriteLine($"   entries          : {packed.Map.Count:N0}");
        Console.WriteLine($"   wall             : {passC.Wall.TotalSeconds:N2} s   (flat walk + containment walk, as B)");
        Console.WriteLine($"   retained after   : {passC.RetainedMb:N1} MB   (map plus the small ModKey index; " +
                          $"~{passC.RetainedMb * 1024 * 1024 / Math.Max(1, packed.Map.Count):N0} B/entry)\n");

        // ---- the descriptive breakdown, deliberately OUTSIDE the timed passes --------------------------------
        // Per-property distinct counts and one live sample body per property. Both would distort the figures
        // above — the sample pins a whole overlay graph, the counts need a second set per property — so they are
        // collected in their own sweep whose time and memory are not reported.
        var describe = new DescribePass();
        Sweep(paths, dataDir, describe, "describe");

        var temporary = describe.PerProperty.GetValueOrDefault("Cell.Temporary")?.Count ?? 0;
        var nonTemporary = describe.PerProperty.Where(k => k.Key != "Cell.Temporary")
                                               .SelectMany(k => k.Value).Distinct().Count();
        Console.WriteLine($"   Cell.Temporary children : {temporary:N0} distinct");
        Console.WriteLine($"   children reached by any OTHER property : {nonTemporary:N0} distinct\n");

        Console.WriteLine("   per child-bearing property (distinct children reached by the containment walk):");
        foreach (var kv in describe.PerProperty.OrderByDescending(k => k.Value.Count))
            Console.WriteLine($"     {kv.Key,-34} {kv.Value.Count,10:N0}");
        Console.WriteLine();

        // ---- pass E: Mutagen's own context enumeration, which carries a Parent ---------------------------------
        // SkyrimModMixIn.EnumerateMajorRecordContexts(ISkyrimModGetter) needs NO link cache and yields
        // IModContext<IMajorRecordGetter>, whose IModContext.Parent is the containing record's context. If that is
        // populated, the containment map needs no custom walk at all — one enumeration replaces both passes.
        var ctx = new ContextPass();
        var passE = Sweep(paths, dataDir, ctx, "E");
        Console.WriteLine($"E. Mutagen context enumeration (EnumerateMajorRecordContexts, no link cache)");
        Console.WriteLine($"   contexts         : {ctx.Contexts:N0}");
        Console.WriteLine($"   with a Parent    : {ctx.WithParent:N0}");
        Console.WriteLine($"   parent is a record : {ctx.ParentIsRecord:N0}");
        Console.WriteLine($"   child->parent    : {ctx.Map.Count:N0} distinct children (packed)");
        Console.WriteLine($"   re-parented      : {ctx.Reparented:N0} (a later plugin put the same child under a DIFFERENT parent)");
        Console.WriteLine($"   wall             : {passE.Wall.TotalSeconds:N2} s");
        Console.WriteLine($"   retained after   : {passE.RetainedMb:N1} MB");
        Console.WriteLine($"   plugins excluded : {passE.Skipped}");
        Console.WriteLine($"   vs the flat pass : {(passE.Wall - passA.Wall).TotalSeconds:N2} s more");
        Console.WriteLine("   edges the context walk hands over (child -> parent, by type):");
        foreach (var kv in ctx.Edges.Where(k => !k.Key.EndsWith("(no parent)", StringComparison.Ordinal))
                                    .OrderByDescending(k => k.Value))
            Console.WriteLine($"     {kv.Key,-52} {kv.Value,10:N0}");
        Console.WriteLine($"     (no parent, {ctx.Edges.Where(k => k.Key.EndsWith("(no parent)", StringComparison.Ordinal)).Sum(k => k.Value):N0} top-level records across {ctx.Edges.Count(k => k.Key.EndsWith("(no parent)", StringComparison.Ordinal))} types)\n");

        // ---- question 1: is the parent in hand on the child body itself? ---------------------------------------
        Console.WriteLine("D. is the parent in hand during the FLAT walk? (per child-bearing property)\n");
        ReportParentInHand(describe);

        return 0;
    }

    // ---------------------------------------------------------------------------------------------------------
    // The passes. Each opens one plugin at a time and disposes it, exactly as BuildIndex does, so the timings are
    // comparable and none holds the order open. Each merges a plugin's work into its totals only after that
    // plugin has walked to the end, so a mid-plugin throw excludes the plugin instead of half-counting it.
    // ---------------------------------------------------------------------------------------------------------

    interface IPass { void Plugin(ISkyrimModGetter ov); }

    /// <summary>Touches both enumerators on every plugin so the timed passes never pay first-run cost.</summary>
    sealed class WarmupPass : IPass
    {
        public void Plugin(ISkyrimModGetter ov)
        {
            foreach (var rec in ov.EnumerateMajorRecords()) { _ = rec.FormKey; }
            foreach (var c in ov.EnumerateMajorRecordContexts()) { _ = c.Record.FormKey; }
        }
    }

    sealed class FlatPass : IPass
    {
        public long Records;
        public void Plugin(ISkyrimModGetter ov)
        {
            var keys = new List<FormKey>();
            foreach (var rec in ov.EnumerateMajorRecords()) keys.Add(rec.FormKey);
            Records += keys.Count;
        }
    }

    sealed class FusedPass : IPass
    {
        public long Records, Parents;
        public readonly Dictionary<FormKey, FormKey> Map = new();

        public void Plugin(ISkyrimModGetter ov)
        {
            var keys = new List<FormKey>();
            foreach (var rec in ov.EnumerateMajorRecords()) keys.Add(rec.FormKey);

            // The containment pass. The parent TYPES are not a hand list either: they are the types
            // OwnedChildContent.Fields answers non-empty for, which is WriteEngine.ChildBearingProperties.
            long parents = 0;
            var staged = new List<(FormKey Child, FormKey Parent)>();
            foreach (var parent in ParentBodies(ov))
            {
                parents++;
                foreach (var field in OwnedChildContent.Fields(parent).Keys)
                {
                    var p = parent.GetType().GetProperty(field, BindingFlags.Public | BindingFlags.Instance);
                    if (p is null) continue;
                    object? val;
                    try { val = p.GetValue(parent); } catch { continue; }
                    var seen = new HashSet<FormKey>();
                    foreach (var child in DirectChildren(val, 0))
                    {
                        if (!seen.Add(child.FormKey)) continue;   // a block struct can expose the same list twice
                        staged.Add((child.FormKey, parent.FormKey));
                    }
                }
            }

            Records += keys.Count;
            Parents += parents;
            foreach (var (child, parent) in staged) Map[child] = parent;
        }
    }

    /// <summary>The same containment walk as FusedPass, storing the map PACKED — one ulong per side, off a dense
    /// ModKey index, which is the representation the resolver's own index could afford.</summary>
    sealed class PackedPass : IPass
    {
        public readonly Dictionary<ulong, ulong> Map = new();
        readonly Dictionary<ModKey, int> _mods = new();

        public void Plugin(ISkyrimModGetter ov)
        {
            foreach (var rec in ov.EnumerateMajorRecords()) { _ = rec.FormKey; }   // the flat walk still happens, as today
            var staged = new List<(FormKey Child, FormKey Parent)>();
            foreach (var parent in ParentBodies(ov))
                foreach (var field in OwnedChildContent.Fields(parent).Keys)
                {
                    var p = parent.GetType().GetProperty(field, BindingFlags.Public | BindingFlags.Instance);
                    if (p is null) continue;
                    object? val;
                    try { val = p.GetValue(parent); } catch { continue; }
                    foreach (var child in DirectChildren(val, 0)) staged.Add((child.FormKey, parent.FormKey));
                }
            foreach (var (child, parent) in staged) Map[Pack(child)] = Pack(parent);
        }

        ulong Pack(FormKey k)
        {
            if (!_mods.TryGetValue(k.ModKey, out var i)) _mods[k.ModKey] = i = _mods.Count;
            return ((ulong)(uint)i << 32) | k.ID;
        }
    }

    /// <summary>Mutagen's own context walk — one enumeration per plugin, each context carrying the containing
    /// record's context in IModContext.Parent. Measures whether the parent really arrives, and what the walk costs
    /// beside the flat one.</summary>
    sealed class ContextPass : IPass
    {
        public long Contexts, WithParent, ParentIsRecord, Reparented;
        public readonly Dictionary<ulong, ulong> Map = new();
        // childType -> parentType, or "(no parent)" / "(parent chain holds no record)" — what the walk hands over.
        public readonly Dictionary<string, long> Edges = new(StringComparer.Ordinal);
        readonly Dictionary<ModKey, int> _mods = new();

        public void Plugin(ISkyrimModGetter ov)
        {
            long contexts = 0, withParent = 0, parentIsRecord = 0;
            var edges = new Dictionary<string, long>(StringComparer.Ordinal);
            var staged = new List<(FormKey Child, FormKey Parent)>();
            void Bump(string k) => edges[k] = edges.GetValueOrDefault(k) + 1;

            foreach (var c in ov.EnumerateMajorRecordContexts())
            {
                contexts++;
                var child = Short(c.Record.GetType());
                var parent = (c as IModContext)?.Parent;
                if (parent is null) { Bump($"{child} -> (no parent)"); continue; }
                withParent++;
                if (parent.Record is IMajorRecordGetter pr)
                {
                    parentIsRecord++;
                    staged.Add((c.Record.FormKey, pr.FormKey));
                    Bump($"{child} -> {Short(pr.GetType())}");
                }
                else
                {
                    // A group/block context, not a record. Climb: the nearest record ancestor is the real parent.
                    var up = parent;
                    int hops = 0;
                    while (up is not null && up.Record is not IMajorRecordGetter && hops++ < 6) up = up.Parent;
                    if (up?.Record is IMajorRecordGetter anc)
                    {
                        parentIsRecord++;
                        staged.Add((c.Record.FormKey, anc.FormKey));
                        Bump($"{child} -> {Short(anc.GetType())} (via {hops} group hop(s))");
                    }
                    else Bump($"{child} -> (parent chain holds no record)");
                }
            }

            Contexts += contexts;
            WithParent += withParent;
            ParentIsRecord += parentIsRecord;
            foreach (var kv in edges) Edges[kv.Key] = Edges.GetValueOrDefault(kv.Key) + kv.Value;
            foreach (var (child, parent) in staged) Put(child, parent);
        }

        // Does a LATER plugin ever put the same child under a DIFFERENT parent? That is what decides whether one
        // whole-order map is well-defined or the map has to be per-plugin.
        void Put(FormKey child, FormKey parent)
        {
            var ck = Pack(child); var pk = Pack(parent);
            if (Map.TryGetValue(ck, out var had) && had != pk) Reparented++;
            Map[ck] = pk;
        }

        ulong Pack(FormKey k)
        {
            if (!_mods.TryGetValue(k.ModKey, out var i)) _mods[k.ModKey] = i = _mods.Count;
            return ((ulong)(uint)i << 32) | k.ID;
        }
    }

    /// <summary>The descriptive pass: distinct children per child-bearing property, and one real (parent, child)
    /// pair per property for the question-1 report. Never timed — the samples pin live overlay bodies, and the
    /// per-property sets are extra memory, both of which would distort the passes above.</summary>
    sealed class DescribePass : IPass
    {
        public readonly Dictionary<string, HashSet<FormKey>> PerProperty = new(StringComparer.Ordinal);
        public readonly Dictionary<string, (IMajorRecordGetter Parent, IMajorRecordGetter Child)> Sample = new(StringComparer.Ordinal);

        public void Plugin(ISkyrimModGetter ov)
        {
            foreach (var parent in ParentBodies(ov))
                foreach (var field in OwnedChildContent.Fields(parent).Keys)
                {
                    var p = parent.GetType().GetProperty(field, BindingFlags.Public | BindingFlags.Instance);
                    if (p is null) continue;
                    object? val;
                    try { val = p.GetValue(parent); } catch { continue; }
                    var key = $"{Short(parent.GetType())}.{field}";
                    foreach (var child in DirectChildren(val, 0))
                    {
                        if (!PerProperty.TryGetValue(key, out var set)) PerProperty[key] = set = new HashSet<FormKey>();
                        set.Add(child.FormKey);
                        if (!Sample.ContainsKey(key)) Sample[key] = (parent, child);
                    }
                }
        }
    }

    /// <summary>Every record in the plugin that BEARS children — the three parent types, asked of the corpus rather
    /// than named here: a type is a parent iff OwnedChildContent.Fields answers non-empty for it.</summary>
    static IEnumerable<IMajorRecordGetter> ParentBodies(ISkyrimModGetter ov)
    {
        foreach (var c in ov.EnumerateMajorRecords<ICellGetter>()) yield return c;
        foreach (var d in ov.EnumerateMajorRecords<IDialogTopicGetter>()) yield return d;
        foreach (var w in ov.EnumerateMajorRecords<IWorldspaceGetter>()) yield return w;
    }

    /// <summary>The DIRECT child records held by one child-bearing field's value — stopping at the first major
    /// record on each branch, so a worldspace's SubCells yields its CELLs and not their placed references. Same cut
    /// as OwnedChildContent.ReachesRecord: a FormLink references, it does not own. A FormKey-less block struct
    /// (WorldspaceBlock / WorldspaceSubBlock) is descended through its own properties, because it is a container
    /// that is not itself IEnumerable — the shape that makes Mutagen's own transitive EnumerateMajorRecords the
    /// easy answer and the wrong one, since that walk would hand back a worldspace's placed references too.</summary>
    static IEnumerable<IMajorRecordGetter> DirectChildren(object? val, int depth)
    {
        if (val is null || depth > 8) yield break;
        if (val is IFormLinkGetter or string) yield break;
        if (val is IMajorRecordGetter rec) { yield return rec; yield break; }
        if (val is System.Collections.IEnumerable seq)
        {
            foreach (var item in seq)
                foreach (var r in DirectChildren(item, depth + 1))
                    yield return r;
            yield break;
        }
        if (val is IMajorRecordGetterEnumerable block)
            foreach (var p in block.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                object? v;
                try { v = p.GetValue(block); } catch { continue; }
                if (v is null || ReferenceEquals(v, block)) continue;
                foreach (var r in DirectChildren(v, depth + 1)) yield return r;
            }
    }

    sealed record PassResult(TimeSpan Wall, double RetainedMb, int Skipped);

    static PassResult Sweep(IReadOnlyList<string> paths, string? dataDir, IPass pass, string name)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var before = GC.GetTotalMemory(true);
        int skipped = 0;
        var sw = Stopwatch.StartNew();
        foreach (var path in paths)
        {
            ISkyrimModGetter ov;
            try { ov = LoadOrderResolver.OpenOverlay(path, dataDir); }
            catch (Exception ex)
            {
                skipped++;
                Console.WriteLine($"   ! {name}: cannot open {Path.GetFileName(path)} — {ex.Message} (plugin excluded)");
                continue;
            }
            // A pass merges a plugin's work only when the plugin walks to the end, so a throw here excludes that
            // plugin from the totals rather than half-counting it. The reason is printed, never swallowed.
            try { pass.Plugin(ov); }
            catch (Exception ex)
            {
                skipped++;
                Console.WriteLine($"   ! {name}: {Path.GetFileName(path)} threw — {ex.Message} (plugin excluded)");
            }
            finally { (ov as IDisposable)?.Dispose(); }
        }
        sw.Stop();
        var after = GC.GetTotalMemory(true);
        return new PassResult(sw.Elapsed, (after - before) / 1024.0 / 1024.0, skipped);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Question 1. For each child-bearing property, take a real (parent, child) pair the containment walk found and
    // ask the CHILD body whether anything on it names the parent — a populated FormLink or a record reference that
    // resolves to the parent's FormKey. That is the whole content of "the parent is in hand": the flat walk hands
    // the caller nothing but the child body, so if the child body does not name its parent, the parent is not
    // available at that moment by any means.
    // ---------------------------------------------------------------------------------------------------------
    static void ReportParentInHand(DescribePass pass)
    {
        var props = ChildBearingSurface();
        foreach (var (key, count) in props.Select(k => (k, pass.PerProperty.GetValueOrDefault(k)?.Count ?? 0)).OrderBy(t => t.k))
        {
            if (!pass.Sample.TryGetValue(key, out var s))
            {
                Console.WriteLine($"   {key,-34} no sample in this order (0 children seen) — not measured");
                continue;
            }
            var hits = ParentNamingMembers(s.Child, s.Parent.FormKey, out var unreadable).ToList();
            // An unreadable body must never masquerade as a clean "NO" — that is the answer being measured.
            var verdict = hits.Count > 0 ? "YES — " + string.Join(", ", hits)
                        : unreadable > 0 ? $"NO among the readable members ({unreadable} member(s) would not read)"
                        : "NO";
            Console.WriteLine($"   {key,-34} {verdict}");
            Console.WriteLine($"      sample child {s.Child.FormKey} ({Short(s.Child.GetType())}) under parent {s.Parent.FormKey}; {count:N0} distinct children of this kind in the order");
        }
    }

    /// <summary>Every member of the child's own body whose value is, or resolves to, the parent's FormKey, plus a
    /// count of the members that would not read at all.</summary>
    static IEnumerable<string> ParentNamingMembers(IMajorRecordGetter child, FormKey parent, out int unreadable)
    {
        var hits = new List<string>();
        unreadable = 0;
        foreach (var p in child.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0) continue;
            object? v;
            try { v = p.GetValue(child); } catch { unreadable++; continue; }
            switch (v)
            {
                case IFormLinkGetter fl when fl.FormKey == parent: hits.Add($"{p.Name} (FormLink)"); break;
                case IMajorRecordGetter mr when mr.FormKey == parent: hits.Add($"{p.Name} (record)"); break;
                case FormKey fk when fk == parent: hits.Add($"{p.Name} (FormKey)"); break;
            }
        }
        return hits;
    }

    /// <summary>The child-bearing property surface, by construction — every concrete Mutagen record type that has
    /// one, spelled Type.Property. This is the row set the report must cover.</summary>
    static IEnumerable<string> ChildBearingSurface()
    {
        foreach (var t in typeof(ISkyrimMod).Assembly.GetTypes())
        {
            if (t.IsAbstract || !t.IsClass || !typeof(IMajorRecord).IsAssignableFrom(t)) continue;
            foreach (var p in WriteEngine.ChildBearingProperties(t))
                yield return $"{Short(t)}.{p.Name}";
        }
    }

    static string Short(Type t)
    {
        var n = t.Name;
        foreach (var suffix in new[] { "BinaryOverlay", "Getter" })
            if (n.EndsWith(suffix, StringComparison.Ordinal)) n = n[..^suffix.Length];
        return n;
    }

    // ---------------------------------------------------------------------------------------------------------

    static IReadOnlyList<string>? ResolvePaths(string[] args, out string label, out string? dataDir)
    {
        label = ""; dataDir = null;
        if (args.Length > 0 && args[0] == "--plugin")
        {
            if (args.Length < 2) { Console.WriteLine("usage: parent-in-hand --plugin <path> [dataDir]"); return null; }
            dataDir = args.Length > 2 ? args[2] : Path.GetDirectoryName(args[1]);
            label = $"single plugin {Path.GetFileName(args[1])}";
            return new[] { args[1] };
        }

        var instanceDir = args.Length > 0 ? args[0] : UserConfiguredInstance();
        if (instanceDir is null)
        {
            Console.WriteLine("no MO2 instance given and none saved — pass one, or use --plugin <path>.");
            return null;
        }
        var (ok, p, problems) = Mo2Instance.Validate(instanceDir);
        if (!ok || p is null)
        {
            Console.WriteLine($"not a usable MO2 instance: {instanceDir}");
            foreach (var problem in problems) Console.WriteLine($"  - {problem}");
            return null;
        }
        var order = Mo2LoadOrder.Build(p.ProfileDir, p.ModsDir, p.DataDir, p.OverwriteDir);
        // Last copy of a duplicate filename wins, the same rule LoadOrderResolver.Build uses; ToDictionary would
        // throw on an order that resolves two paths to one filename.
        var nameToIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < order.OrderedPaths.Count; i++) nameToIdx[Path.GetFileName(order.OrderedPaths[i])] = i;
        dataDir = LoadOrderResolver.ComputeDataDir(nameToIdx, order.OrderedPaths.ToArray());
        label = $"live MO2 instance {instanceDir} (profile {p.ProfileName})";
        return order.OrderedPaths;
    }

    /// <summary>The instance housecarl_set_mo2_instance saved, if any — the same file the server reads.</summary>
    static string? UserConfiguredInstance()
    {
        foreach (var dir in new[]
        {
            Environment.GetEnvironmentVariable("HOUSECARL_DATA_DIR"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "housecarl", "server"),
        })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var f = Path.Combine(dir, "houseCARL.user.json");
            if (!File.Exists(f)) continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(f));
                if (doc.RootElement.TryGetProperty("Mo2InstanceDir", out var v) && v.GetString() is { Length: > 0 } s)
                    return s;
            }
            catch { }
        }
        return null;
    }
}

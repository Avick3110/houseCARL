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
//      Answered per child-bearing property AND per distinct child type under it, by asking REAL child bodies —
//      while the overlay is still open — whether anything on the body names the parent. The search descends
//      through nested structs and polymorphic arms, because that is where a parent pointer actually lives
//      (NavigationMesh.Data.Parent, whose CellNavmeshParent arm carries the cell link).
//   2. What does capturing the parent cost beside the flat pass the index build already runs? Exactly two timed
//      passes: A, today's flat walk, and E, Mutagen's own EnumerateMajorRecordContexts (which carries the parent
//      and needs no link cache) building the packed ulong->ulong child-to-parent map. A and E run three times
//      each, alternating, with everything released between runs; the median of each is reported.
//
//   dotnet run --project src/housecarl-generator parent-in-hand [instanceDir]
//   dotnet run --project src/housecarl-generator parent-in-hand --plugin <path> [dataDir]
static class ParentInHandProbe
{
    const int Rounds = 3;
    const int SamplesPerChildType = 20;

    public static int Run(string[] args)
    {
        var paths = ResolvePaths(args, out var label, out var dataDir);
        if (paths is null) return 1;
        Console.WriteLine($"parent-in-hand: {label} — {paths.Count} plugin(s)\n");

        // A discarded warm-up sweep runs first. Without it the first timed pass alone pays the JIT of both
        // enumerators and the cold file cache for every plugin, which is bigger than the difference being measured.
        var warm = Sweep(paths, dataDir, new WarmupPass(), "warm-up");
        if (warm.Opened == 0)
        {
            Console.WriteLine("every plugin failed to open, so nothing was measured — check the instance path and the profile's load order.");
            return 1;
        }
        Console.WriteLine("warm-up sweep done and discarded; every timing below is warm.\n");

        // ---- the two timed passes, alternated A E A E A E ------------------------------------------------------
        // Alternating removes the ordering advantage the second pass would otherwise get from the file cache, and
        // each pass's own state is released before the next one starts, so neither measures the other's residue.
        var aWall = new List<TimeSpan>();
        var eWall = new List<TimeSpan>();
        var eMemory = new List<double>();
        long records = 0;
        int aSkipped = 0, eSkipped = 0;
        ContextPass? lastCtx = null;

        for (var round = 1; round <= Rounds; round++)
        {
            var flat = new FlatPass();
            var a = Sweep(paths, dataDir, flat, $"A{round}");
            if (a.Opened == 0) { Console.WriteLine("every plugin failed to open, so nothing was measured — check the instance path and the profile's load order."); return 1; }
            aWall.Add(a.Wall); records = flat.Records; aSkipped = a.Skipped;
            flat = null;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

            var ctx = new ContextPass();
            var e = Sweep(paths, dataDir, ctx, $"E{round}");
            if (e.Opened == 0) { Console.WriteLine("every plugin failed to open, so nothing was measured — check the instance path and the profile's load order."); return 1; }
            eWall.Add(e.Wall); eMemory.Add(e.RetainedMb); eSkipped = e.Skipped;
            lastCtx = ctx;
            Console.WriteLine($"   round {round}: A {a.Wall.TotalSeconds:N2} s, E {e.Wall.TotalSeconds:N2} s");
            if (round < Rounds) { lastCtx = null; ctx = null; GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        }
        Console.WriteLine();

        var medA = Median(aWall);
        var medE = Median(eWall);
        var ctxFinal = lastCtx!;

        Console.WriteLine($"A. flat pass (today's BuildIndex walk), median of {Rounds}");
        Console.WriteLine($"   records          : {records:N0}");
        Console.WriteLine($"   wall             : {medA.TotalSeconds:N2} s   (runs: {string.Join(", ", aWall.Select(w => $"{w.TotalSeconds:N2}"))})");
        Console.WriteLine($"   plugins excluded : {aSkipped}\n");

        Console.WriteLine($"E. Mutagen context enumeration (EnumerateMajorRecordContexts, no link cache), median of {Rounds}");
        Console.WriteLine($"   contexts         : {ctxFinal.Contexts:N0}");
        Console.WriteLine($"   with a Parent    : {ctxFinal.WithParent:N0}");
        Console.WriteLine($"   parent is a record : {ctxFinal.ParentIsRecord:N0}");
        Console.WriteLine($"   child->parent    : {ctxFinal.Map.Count:N0} distinct children (packed ulong -> ulong, (modIdx<<32|id))");
        Console.WriteLine($"   re-parented      : {ctxFinal.Reparented:N0} distinct children whose FINAL parent differs from the FIRST one seen " +
                          "(distinct children, not overwrite events)");
        Console.WriteLine($"   wall             : {medE.TotalSeconds:N2} s   (runs: {string.Join(", ", eWall.Select(w => $"{w.TotalSeconds:N2}"))})");
        Console.WriteLine($"   retained after   : {Median(eMemory):N1} MB   (the packed map alone; " +
                          $"~{Median(eMemory) * 1024 * 1024 / Math.Max(1, ctxFinal.Map.Count):N0} B/entry)");
        Console.WriteLine($"   plugins excluded : {eSkipped}");
        var delta = (medE - medA).TotalSeconds;
        Console.WriteLine($"   vs the flat pass : {Math.Abs(delta):N2} s {(delta < 0 ? "less" : "more")} " +
                          $"({(medA.TotalSeconds > 0 ? medE.TotalSeconds / medA.TotalSeconds : 0):N2}x A)");
        Console.WriteLine("   edges the context walk hands over (child -> parent, by type):");
        foreach (var kv in ctxFinal.Edges.Where(k => !k.Key.EndsWith("(no parent)", StringComparison.Ordinal))
                                         .OrderByDescending(k => k.Value))
            Console.WriteLine($"     {kv.Key,-52} {kv.Value,10:N0}");
        Console.WriteLine($"     (no parent, {ctxFinal.Edges.Where(k => k.Key.EndsWith("(no parent)", StringComparison.Ordinal)).Sum(k => k.Value):N0} top-level records across {ctxFinal.Edges.Count(k => k.Key.EndsWith("(no parent)", StringComparison.Ordinal))} types)\n");

        lastCtx = null;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

        // ---- the descriptive breakdown and question 1, deliberately OUTSIDE the timed passes --------------------
        // Per-property distinct counts, and the parent-naming check run on live bodies while the overlay is still
        // open. Both would distort the figures above, so they get their own untimed sweep.
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

        Console.WriteLine("D. is the parent in hand during the FLAT walk? (per child-bearing property, per child type)\n");
        ReportParentInHand(describe);

        return 0;
    }

    static TimeSpan Median(List<TimeSpan> xs) => xs.OrderBy(x => x).ElementAt(xs.Count / 2);
    static double Median(List<double> xs) => xs.OrderBy(x => x).ElementAt(xs.Count / 2);

    // ---------------------------------------------------------------------------------------------------------
    // The passes. Each opens one plugin at a time and disposes it, exactly as BuildIndex does, so the timings are
    // comparable and none holds the order open. Each merges a plugin's work into its totals only after that
    // plugin has walked to the end, so a mid-plugin throw excludes the plugin instead of half-counting it.
    // ---------------------------------------------------------------------------------------------------------

    interface IPass
    {
        void Plugin(ISkyrimModGetter ov);
        /// <summary>Drops anything that is bookkeeping rather than the thing being measured, before memory is read.</summary>
        void Finish() { }
    }

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

    /// <summary>Mutagen's own context walk — one enumeration per plugin, each context carrying the containing
    /// record's context in IModContext.Parent. Measures whether the parent really arrives, what the walk costs
    /// beside the flat one, and what the resulting packed map retains.</summary>
    sealed class ContextPass : IPass
    {
        public long Contexts, WithParent, ParentIsRecord, Reparented;
        public readonly Dictionary<ulong, ulong> Map = new();
        // childType -> parentType, or "(no parent)" / "(parent chain holds no record)" — what the walk hands over.
        public readonly Dictionary<string, long> Edges = new(StringComparer.Ordinal);
        readonly Dictionary<ModKey, int> _mods = new();
        // First parent seen per child. Bookkeeping for the re-parent count, dropped in Finish so the reported
        // memory is the packed map alone.
        Dictionary<ulong, ulong>? _first = new();

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

        // Does a LATER plugin ever leave the same child under a DIFFERENT parent? That is what decides whether one
        // whole-order map is well-defined or the map has to be per-plugin. Counted as DISTINCT children whose final
        // parent differs from the first one seen — not as overwrite events, which double-count a child moved twice
        // and count a child moved away and back as two.
        void Put(FormKey child, FormKey parent)
        {
            var ck = Pack(child); var pk = Pack(parent);
            _first!.TryAdd(ck, pk);
            Map[ck] = pk;
        }

        public void Finish()
        {
            Reparented = _first!.Count(kv => Map[kv.Key] != kv.Value);
            _first = null;
        }

        ulong Pack(FormKey k)
        {
            if (!_mods.TryGetValue(k.ModKey, out var i)) _mods[k.ModKey] = i = _mods.Count;
            return ((ulong)(uint)i << 32) | k.ID;
        }
    }

    /// <summary>One sampled (property, child type) row of the question-1 answer.</summary>
    sealed class NamingTally
    {
        public int Sampled, Naming, Unreadable;
        public readonly HashSet<string> Paths = new(StringComparer.Ordinal);
    }

    /// <summary>The descriptive pass: distinct children per child-bearing property, and the parent-naming check
    /// itself, run on live bodies WHILE THE OVERLAY IS OPEN — a retained overlay body reads differently after its
    /// mod is disposed, so the answer has to be taken here. Never timed.</summary>
    sealed class DescribePass : IPass
    {
        public readonly Dictionary<string, HashSet<FormKey>> PerProperty = new(StringComparer.Ordinal);
        // (property, child type) -> what the sampled bodies said. Every distinct child type under a property is
        // sampled separately, because PlacedObject and PlacedNpc under Cell.Temporary are different bodies and one
        // does not answer for the other.
        public readonly Dictionary<(string Property, string ChildType), NamingTally> Naming = new();
        public readonly Dictionary<(string Property, string ChildType), (FormKey Child, FormKey Parent)> Sample = new();

        public void Plugin(ISkyrimModGetter ov)
        {
            // Staged per plugin and merged only when the plugin walks to the end, so a mid-plugin throw excludes
            // the plugin rather than leaving half its rows behind.
            var perProperty = new Dictionary<string, HashSet<FormKey>>(StringComparer.Ordinal);
            var naming = new Dictionary<(string, string), NamingTally>();
            var sample = new Dictionary<(string, string), (FormKey, FormKey)>();

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
                        if (!perProperty.TryGetValue(key, out var set)) perProperty[key] = set = new HashSet<FormKey>();
                        set.Add(child.FormKey);

                        var childType = Short(child.GetType());
                        var slot = (key, childType);
                        var already = (Naming.GetValueOrDefault((key, childType))?.Sampled ?? 0)
                                    + (naming.GetValueOrDefault(slot)?.Sampled ?? 0);
                        if (already >= SamplesPerChildType) continue;

                        if (!naming.TryGetValue(slot, out var tally)) naming[slot] = tally = new NamingTally();
                        var paths = ParentNamingPaths(child, parent.FormKey, out var unreadable);
                        tally.Sampled++;
                        tally.Unreadable += unreadable;
                        if (paths.Count > 0) { tally.Naming++; foreach (var path in paths) tally.Paths.Add(path); }
                        sample.TryAdd(slot, (child.FormKey, parent.FormKey));
                    }
                }

            foreach (var kv in perProperty)
            {
                if (!PerProperty.TryGetValue(kv.Key, out var set)) PerProperty[kv.Key] = set = new HashSet<FormKey>();
                set.UnionWith(kv.Value);
            }
            foreach (var kv in naming)
            {
                if (!Naming.TryGetValue(kv.Key, out var tally)) Naming[kv.Key] = tally = new NamingTally();
                tally.Sampled += kv.Value.Sampled;
                tally.Naming += kv.Value.Naming;
                tally.Unreadable += kv.Value.Unreadable;
                tally.Paths.UnionWith(kv.Value.Paths);
            }
            foreach (var kv in sample) Sample.TryAdd(kv.Key, kv.Value);
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

    sealed record PassResult(TimeSpan Wall, double RetainedMb, int Skipped, int Opened);

    static PassResult Sweep(IReadOnlyList<string> paths, string? dataDir, IPass pass, string name)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var before = GC.GetTotalMemory(true);
        int skipped = 0, opened = 0;
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
            opened++;
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
        pass.Finish();
        var after = GC.GetTotalMemory(true);
        return new PassResult(sw.Elapsed, (after - before) / 1024.0 / 1024.0, skipped, opened);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Question 1. For each child-bearing property and each distinct child type under it, take real (parent, child)
    // pairs the containment walk found and ask the CHILD body whether anything on it names the parent — a
    // populated FormLink, a FormKey, or a record reference that equals the parent's FormKey. That is the whole
    // content of "the parent is in hand": the flat walk hands the caller nothing but the child body.
    //
    // The search is NOT one level deep. A parent pointer on a Skyrim child body sits inside a nested struct behind
    // a polymorphic arm — NavigationMesh.Data.Parent is an abstract navmesh parent whose CellNavmeshParent arm
    // carries the cell — so the walk descends through plain structs and reads each value's RUNTIME type, which is
    // what makes the arm visible at all.
    // ---------------------------------------------------------------------------------------------------------
    static void ReportParentInHand(DescribePass pass)
    {
        foreach (var key in ChildBearingSurface().OrderBy(k => k, StringComparer.Ordinal))
        {
            var count = pass.PerProperty.GetValueOrDefault(key)?.Count ?? 0;
            var rows = pass.Naming.Where(kv => kv.Key.Property == key).OrderBy(kv => kv.Key.ChildType, StringComparer.Ordinal).ToList();
            if (rows.Count == 0)
            {
                Console.WriteLine($"   {key,-34} no children in this order (0 seen) — not measured");
                continue;
            }
            Console.WriteLine($"   {key}   ({count:N0} distinct children in the order)");
            foreach (var (slot, tally) in rows)
            {
                // An unreadable body must never masquerade as a clean "NO" — that is the answer being measured.
                var verdict = tally.Naming == tally.Sampled && tally.Naming > 0
                        ? $"YES via {string.Join(", ", tally.Paths.OrderBy(p => p, StringComparer.Ordinal))}"
                    : tally.Naming > 0
                        ? $"YES on {tally.Naming} of {tally.Sampled} sampled, via {string.Join(", ", tally.Paths.OrderBy(p => p, StringComparer.Ordinal))}"
                    : tally.Unreadable > 0
                        ? $"NO among the readable members ({tally.Unreadable} member read(s) threw)"
                        : "NO";
                var sample = pass.Sample.TryGetValue(slot, out var s) ? $"; e.g. child {s.Child} under parent {s.Parent}" : "";
                Console.WriteLine($"     {slot.ChildType,-24} {verdict}   ({tally.Sampled} sampled{sample})");
            }
        }
    }

    /// <summary>Every member path on the child's own body whose value is, or resolves to, the parent's FormKey,
    /// spelled from the child body down (Data.Parent[CellNavmeshParent].Parent), plus a count of the members that
    /// would not read at all.</summary>
    static List<string> ParentNamingPaths(IMajorRecordGetter child, FormKey parent, out int unreadable)
    {
        var hits = new List<string>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var skip = OwnedChildContent.Fields(child).Keys.ToHashSet(StringComparer.Ordinal);
        int failed = 0;
        Descend(child, "", 0);
        unreadable = failed;
        return hits;

        void Descend(object node, string path, int depth)
        {
            if (depth > 5 || !seen.Add(node)) return;
            foreach (var p in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                // A child-bearing property holds owned children, not a parent pointer, and walking one parses a
                // whole cell's worth of placed references.
                if (depth == 0 && skip.Contains(p.Name)) continue;
                object? v;
                try { v = p.GetValue(node); } catch { failed++; continue; }
                Visit(v, path.Length == 0 ? p.Name : $"{path}.{p.Name}", p.PropertyType, depth);
            }
        }

        void Visit(object? v, string path, Type declared, int depth)
        {
            switch (v)
            {
                case null:
                    return;
                case IFormLinkGetter fl:
                    if (fl.FormKey == parent) hits.Add(path);
                    return;
                case FormKey fk:
                    if (fk == parent) hits.Add(path);
                    return;
                case IMajorRecordGetter mr:
                    // Another record body: it can BE the parent, but its own fields are not the child's.
                    if (mr.FormKey == parent) hits.Add(path);
                    return;
                case string:
                    return;
                case System.Collections.IEnumerable seq:
                    var i = 0;
                    foreach (var item in seq)
                    {
                        if (i >= 64) return;   // a bounded look: a parent pointer is a field, not a long list
                        Visit(item, $"{path}[{i++}]", item?.GetType() ?? typeof(object), depth + 1);
                    }
                    return;
                default:
                    if (IsLeaf(v.GetType())) return;
                    // A nested struct. Spell the polymorphic arm when the runtime type is not the declared one,
                    // because that is the whole reason the member is reachable.
                    var arm = declared != v.GetType() && (declared.IsAbstract || declared.IsInterface)
                        ? $"{path}[{Short(v.GetType())}]" : path;
                    Descend(v, arm, depth + 1);
                    return;
            }
        }
    }

    static bool IsLeaf(Type t) =>
        t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime)
        || t == typeof(Guid) || t == typeof(ModKey) || t.Namespace?.StartsWith("System.Numerics", StringComparison.Ordinal) == true;

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

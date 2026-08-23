using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlGenerator;

/// <summary>
/// WAVE 3 SCOUT (throwaway recon — not a standing instrument).
///
/// The roadmap (<c>dev/plans/WRITE_SURFACE_ROADMAP_2026-05-30.md</c> §6) flags wave 3 as the HIGHEST
/// mechanism risk and says: "Mutagen's nested-group write API is the unknown — scout it first." This
/// probe IS that scout — it answers, empirically, "how does houseCARL resolve an override for a record
/// that does NOT live in a flat <c>SkyrimGroup&lt;T&gt;</c>?" so the wave-3 build session doesn't fly blind.
///
/// THE GAP (verbatim from <see cref="WriteEngine.EnumerateFlatGroups"/>'s own docstring): the engine
/// resolves a record to override by matching the top-level <c>SkyrimGroup&lt;T&gt;</c> on the mod whose T
/// carries the record's getter interface. The 14 records Bethesda stores in NESTED groups have no such
/// group, so <see cref="WriteEngine.GenericGetOrAddAsOverride"/> throws on them today: Cell (under cell-
/// blocks / a Worldspace's block→sub-block), the Placed* family (under a Cell's persistent/temporary
/// children), NavigationMesh + Landscape (under a Cell), DialogResponses/INFO (under a DIAL topic).
/// FINDING + READING these already works (<c>EnumerateMajorRecords()</c> walks them); the missing piece is
/// purely override-RESOLUTION + the parent-chain reconstruction a patch needs to serialize them.
///
/// HYPOTHESIS UNDER TEST (named so it can be falsified, NOT assumed): Mutagen's <c>IModContext</c> API —
/// resolve a record's CONTEXT (which knows its parent chain), then <c>context.GetOrAddAsOverride(mod)</c>
/// reconstructs that chain in the patch mod and returns a settable override. The build-relevant path is by
/// FormKey (the user names a record, exactly the wave-2 Dragonbane flow): <c>cache.ResolveContext&lt;T,TG&gt;
/// (formKey)</c> → <c>GetOrAddAsOverride</c>. Phase A discovers the ACTUAL surface in the pinned Mutagen version
/// (reflection only — robust to a version bump). Phase B inventories how many of each nested type live in
/// the source, then drives a REAL by-FormKey override of every nested type that HAS an instance, writes a
/// patch through the proven <see cref="WriteEngine.WritePatch"/>, reopens it, and confirms the target + its
/// parents landed and the source is byte-for-byte untouched. Fails LOUD per type (Q3).
///
/// Run: <c>dotnet run --project src/housecarl-generator nested-probe [sourcePlugin]</c>
/// </summary>
public static class NestedProbe
{
    /// <summary>The Mutagen version actually loaded, read off the assembly rather than written down. A probe
    /// banner that names a version by hand goes stale on the next bump and nothing catches it; this cannot.</summary>
    static string MutagenVersion =>
        typeof(Mutagen.Bethesda.Skyrim.ISkyrimModGetter).Assembly.GetName().Version?.ToString() ?? "unknown";

    const string DefaultSource =
        @"C:\Program Files (x86)\Steam\steamapps\common\Skyrim Special Edition\Data\Skyrim.esm";

    // Bethesda's nested-GRUP records (the census `nestedGroupRecordNames`). The probe RE-DERIVES this set
    // by construction in Phase A (concrete records minus flat-group records) and cross-checks against it.
    static readonly string[] ExpectedNested =
    {
        "Cell", "PlacedObject", "PlacedNpc", "PlacedArrow", "PlacedBarrier", "PlacedBeam",
        "PlacedCone", "PlacedFlame", "PlacedHazard", "PlacedMissile", "PlacedTrap",
        "NavigationMesh", "Landscape", "DialogResponses",
    };

    public static int RunNestedProbe(string[] args)
    {
        var src = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : DefaultSource;
        var asm = typeof(IArmorGetter).Assembly;

        Console.WriteLine("################  WAVE 3 SCOUT — nested-group record resolution  ################");
        Console.WriteLine();

        // =====================================================================
        //  PHASE A — discover the actual Mutagen nested-override API (reflection)
        // =====================================================================
        Console.WriteLine($"===== PHASE A : Mutagen context API surface (pinned {MutagenVersion}) =====");
        Console.WriteLine();

        var mutagen = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => (a.GetName().Name ?? "").StartsWith("Mutagen"))
            .OrderBy(a => a.GetName().Name).ToList();
        Console.WriteLine("-- loaded Mutagen assemblies --");
        foreach (var a in mutagen) Console.WriteLine($"   {a.GetName().Name} {a.GetName().Version}");
        Console.WriteLine();

        // A.1 — every EnumerateMajorRecordContexts overload (enumeration entry point).
        Console.WriteLine("-- EnumerateMajorRecordContexts overloads --");
        foreach (var m in StaticMethods(mutagen, "EnumerateMajorRecordContexts"))
            Console.WriteLine($"   {m.DeclaringType?.Name}.{Sig(m)}");
        Console.WriteLine();

        // A.2 — the IModContext family + the members the override path leans on.
        Console.WriteLine("-- IModContext interface(s) + key members --");
        foreach (var t in mutagen.SelectMany(SafeTypes).Where(t => t.IsInterface && t.Name.StartsWith("IModContext")).OrderBy(t => t.Name))
        {
            Console.WriteLine($"   {Pretty(t)}");
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .Where(m => m.Name is "GetOrAddAsOverride" or "DuplicateIntoAsNewRecord" && !m.IsSpecialName))
                Console.WriteLine($"       (method) {Sig(m)}");
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.Name is "Record" or "Parent" or "ModKey"))
                Console.WriteLine($"       (prop)   {p.Name} : {Pretty(p.PropertyType)}");
        }
        Console.WriteLine();

        // A.3 — by-FormKey context resolution on the link cache (THE build-relevant path). Discovered for
        // documentation here; the method actually invoked is re-resolved from the LIVE cache type in Phase B
        // (a MethodInfo off the open generic def can't be invoked on the closed instance).
        Console.WriteLine("-- (Try)ResolveContext on ImmutableModLinkCache (resolve a record's context by FormKey) --");
        var cacheType = mutagen.SelectMany(SafeTypes).FirstOrDefault(t => t.Name.StartsWith("ImmutableModLinkCache") && t.IsGenericTypeDefinition)
            ?? mutagen.SelectMany(SafeTypes).FirstOrDefault(t => t.Name.StartsWith("ImmutableModLinkCache"));
        foreach (var m in (cacheType?.GetMethods(BindingFlags.Public | BindingFlags.Instance) ?? Array.Empty<MethodInfo>())
                     .Where(m => m.Name is "ResolveContext" or "TryResolveContext"))
            Console.WriteLine($"   {m.Name}{(m.IsGenericMethodDefinition ? "<" + string.Join(",", m.GetGenericArguments().Select(g => g.Name)) + ">" : "")}({string.Join(", ", m.GetParameters().Select(p => Pretty(p.ParameterType) + " " + p.Name))})");
        Console.WriteLine();

        // A.4 — re-derive the nested set BY CONSTRUCTION (concrete records minus flat-group records).
        Console.WriteLine("-- nested record set, derived by construction (engine reachability) --");
        var flatGetterIfaces = WriteEngine.EnumerateFlatGroups(typeof(SkyrimMod)).Select(x => x.getterIface).ToHashSet();
        var concrete = asm.GetTypes()
            .Where(c => c.IsClass && !c.IsAbstract && !c.Name.EndsWith("BinaryOverlay", StringComparison.Ordinal) && typeof(IMajorRecordGetter).IsAssignableFrom(c))
            .OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        var nestedDerived = concrete.Where(c => !c.GetInterfaces().Any(flatGetterIfaces.Contains)).Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Console.WriteLine($"   flat-group getter ifaces: {flatGetterIfaces.Count} | concrete major records: {concrete.Count} | NESTED: {nestedDerived.Count}");
        Console.WriteLine($"   {string.Join(", ", nestedDerived)}");
        var unexpected = nestedDerived.Except(ExpectedNested).ToList();
        var missing = ExpectedNested.Except(nestedDerived).ToList();
        Console.WriteLine(unexpected.Count == 0 && missing.Count == 0
            ? "   == derived set MATCHES the census's 14 nested records =="
            : $"   !! drift: extra=[{string.Join(",", unexpected)}] missing=[{string.Join(",", missing)}]");
        Console.WriteLine();

        if (!File.Exists(src))
        {
            Console.Error.WriteLine($"error: source plugin not found: {src} (Phase A done; Phase B needs a plugin).");
            return 1;
        }

        // =====================================================================
        //  PHASE B — inventory, then a REAL by-FormKey override of each present type
        // =====================================================================
        Console.WriteLine($"===== PHASE B : real by-FormKey overrides against {Path.GetFileName(src)} =====");
        Console.WriteLine();

        var shaBefore = Sha(src);
        var sourceMod = SkyrimMod.CreateFromBinaryOverlay(src, SkyrimRelease.SkyrimSE);
        var cache = sourceMod.ToImmutableLinkCache();

        // The by-FormKey resolver, re-resolved from the LIVE (closed-generic) cache type so it is invokable.
        // Signature is ResolveContext<TMajor,TMajorGetter>(FormKey, ResolveTarget) — first param FormKey, a
        // trailing ResolveTarget (Winner/Origin; immaterial on a single-mod cache) handled by BuildResolveArgs.
        var resolveCtx = cache.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "ResolveContext" && m.IsGenericMethodDefinition
                && m.GetGenericArguments().Length == 2 && m.GetParameters().Length >= 1
                && m.GetParameters()[0].ParameterType == typeof(FormKey));
        if (resolveCtx is null)
        {
            Console.Error.WriteLine("!! no ResolveContext<TSetter,TGetter>(FormKey, ...) on the live cache — the build-relevant by-FormKey path is absent. SURFACE this (do not guess).");
            return 1;
        }

        // B.0 — inventory: count each nested type + capture one sample FormKey. One pass; isinst can't throw
        // (the typed-enumeration InvalidCastException the first probe revealed is avoided entirely here).
        var getters = ExpectedNested.ToDictionary(n => n, n => asm.GetType("Mutagen.Bethesda.Skyrim.I" + n + "Getter")!);
        var count = ExpectedNested.ToDictionary(n => n, _ => 0);
        var sample = new Dictionary<string, FormKey>();
        foreach (var rec in sourceMod.EnumerateMajorRecords())
            foreach (var n in ExpectedNested)
                if (getters[n].IsInstanceOfType(rec)) { count[n]++; if (!sample.ContainsKey(n)) sample[n] = rec.FormKey; break; }

        Console.WriteLine("-- instance inventory in source --");
        foreach (var n in ExpectedNested)
            Console.WriteLine($"   {n,-16} count={count[n],-7} {(sample.TryGetValue(n, out var fk) ? "sample=" + fk : "(none)")}");
        Console.WriteLine();

        var outDir = Path.GetFullPath(Path.Combine("write-output", "nested-probe"));
        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        Directory.CreateDirectory(outDir);

        int pass = 0, fail = 0, absent = 0;
        foreach (var name in ExpectedNested)
        {
            var setter = asm.GetType("Mutagen.Bethesda.Skyrim.I" + name);
            var getter = getters[name];
            if (setter is null) { Console.WriteLine($"[FAIL] {name,-16} — no I{name} setter interface."); fail++; continue; }
            if (count[name] == 0)
            {
                Console.WriteLine($"[----] {name,-16} — 0 instances in source; cannot exercise here (shares APlaced + cell-child containment with proven placed types).");
                absent++;
                continue;
            }

            try
            {
                var fk = sample[name];
                // THE BUILD PATH: resolve this record's context by FormKey, then override into a fresh patch.
                var ctx = resolveCtx.MakeGenericMethod(setter, getter).Invoke(cache, BuildResolveArgs(resolveCtx, fk));
                if (ctx is null) { Console.WriteLine($"[FAIL] {name,-16} {fk} — ResolveContext returned null."); fail++; continue; }

                var ctxType = ctx.GetType();
                var rec = (IMajorRecordGetter)ctxType.GetProperty("Record")!.GetValue(ctx)!;
                var parentInfo = DescribeParent(ctxType.GetProperty("Parent")?.GetValue(ctx));

                var patch = new SkyrimMod(new ModKey("hc_nested_" + name, ModType.Plugin), SkyrimRelease.SkyrimSE);
                var goao = ctxType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetOrAddAsOverride" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsInstanceOfType(patch));
                if (goao is null) { Console.WriteLine($"[FAIL] {name,-16} {fk} — context has no GetOrAddAsOverride(mod) for {patch.GetType().Name}."); fail++; continue; }

                var overrideRec = goao.Invoke(ctx, new object[] { patch })!;
                var settable = setter.IsInstanceOfType(overrideRec);

                var outPath = Path.Combine(outDir, "hc_nested_" + name + ".esp");
                WriteEngine.WritePatch(patch, sourceMod, outPath);

                var back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE);
                var all = back.EnumerateMajorRecords().ToList();
                var present = all.Any(r => r.FormKey == rec.FormKey);
                var masters = back.ModHeader.MasterReferences.Select(m => m.Master.ToString()).ToList();
                var carried = string.Join("+", all.Select(Sig4).Distinct().OrderBy(s => s));

                var ok = present && settable && masters.Count == 1;
                if (ok) pass++; else fail++;
                Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name,-16} {rec.FormKey}  parent={parentInfo}");
                Console.WriteLine($"         settable={settable} present={present} patch-records={all.Count} ({carried}) masters=[{string.Join(",", masters)}]");
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException is { } ie ? $" / {ie.GetType().Name}: {ie.Message}" : "";
                Console.WriteLine($"[FAIL] {name,-16} — {ex.GetType().Name}: {ex.Message}{inner}");
                fail++;
            }
        }

        var sourceUnchanged = shaBefore == Sha(src);
        Console.WriteLine();
        Console.WriteLine($"source unchanged: {(sourceUnchanged ? "YES" : "NO")}");
        Console.WriteLine($"=== nested-probe: {pass} overridden+written, {fail} failed, {absent} absent-in-source " +
                          $"(of {ExpectedNested.Length}); source {(sourceUnchanged ? "untouched" : "CHANGED")} ===");
        return fail == 0 && sourceUnchanged ? 0 : 1;
    }

    static IEnumerable<MethodInfo> StaticMethods(IEnumerable<Assembly> asms, string name) =>
        asms.SelectMany(SafeTypes).Where(t => t is { IsAbstract: true, IsSealed: true })
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static)).Where(m => m.Name == name);

    static string DescribeParent(object? parent)
    {
        if (parent is null) return "(none)";
        var rec = parent.GetType().GetProperty("Record")?.GetValue(parent) as IMajorRecordGetter;
        var inner = DescribeParent(parent.GetType().GetProperty("Parent")?.GetValue(parent));
        var here = rec is null ? parent.GetType().Name : $"{Sig4(rec)}:{rec.FormKey.ID:X6}";
        return inner == "(none)" ? here : $"{here}<-{inner}";
    }

    static string Sig4(IMajorRecordGetter r)
    {
        var gf = r.GetType().GetField("GrupRecordType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        return gf?.GetValue(null)?.ToString() ?? r.GetType().Name;
    }

    /// <summary>Args for ResolveContext&lt;T,TG&gt;(FormKey, [ResolveTarget]): the FormKey, then any trailing param
    /// (the ResolveTarget enum — immaterial on a single-mod cache) by its declared default or a zero value.</summary>
    static object?[] BuildResolveArgs(MethodInfo m, FormKey fk)
    {
        var ps = m.GetParameters();
        var argv = new object?[ps.Length];
        argv[0] = fk;
        for (int i = 1; i < ps.Length; i++)
            argv[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue
                : ps[i].ParameterType.IsValueType ? System.Activator.CreateInstance(ps[i].ParameterType) : null;
        return argv;
    }

    // -- local copies of the diagnostic helpers (kept here so the probe touches no shared file) --

    static string Sha(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }

    static string Sig(MethodInfo m)
    {
        var gen = m.IsGenericMethodDefinition ? "<" + string.Join(",", m.GetGenericArguments().Select(g => g.Name)) + ">" : "";
        var ps = string.Join(", ", m.GetParameters().Select(p => $"{Pretty(p.ParameterType)} {p.Name}"));
        return $"{m.Name}{gen}({ps}) -> {Pretty(m.ReturnType)}";
    }

    static string Pretty(Type t)
    {
        if (t.IsByRef) return Pretty(t.GetElementType()!) + "&";
        if (t.IsGenericType)
        {
            var name = t.Name;
            var tick = name.IndexOf('`');
            if (tick > 0) name = name[..tick];
            return $"{name}<{string.Join(", ", t.GetGenericArguments().Select(Pretty))}>";
        }
        return t.Name;
    }
}

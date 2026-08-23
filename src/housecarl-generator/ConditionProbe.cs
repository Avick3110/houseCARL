using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlGenerator;

/// <summary>
/// WAVE 4 SCOUT (throwaway recon — not a standing instrument).
///
/// The roadmap (<c>dev/plans/WRITE_SURFACE_ROADMAP_2026-05-30.md</c> §6, wave 4) says: build the
/// "condition oracle (byte-proves the form-vs-index discriminator)" and "scout the condition-oracle
/// mechanism first (it's the unknown, like the nested API was for wave 3)". This probe IS that scout —
/// it answers, empirically, the ONE question a static corpus read cannot: <b>how does houseCARL set an
/// <c>IFormLinkOrIndex&lt;T&gt;</c> condition target in BOTH modes (a real FormID vs a numeric
/// alias/package index), and what decides which mode serializes?</b> so the wave-4 build doesn't fly blind.
///
/// THE GAP (verbatim from <see cref="WriteEngine.TryFormLink"/>'s own note, WriteEngine.cs:1113):
/// "IFormLinkOrIndex&lt;T&gt; (condition-data targets) is deliberately NOT recognised here — its concrete
/// ctor needs a discriminator flag whose byte-semantics we won't ship unverified (no condition oracle yet)."
/// READING conditions already works (the wave-0 differ snapshots them); coerce-audit loud-defers the 156
/// FormLinkOrIndex sites. The missing piece is purely value-CONSTRUCTION of the FormLinkOrIndex leaf.
///
/// STATIC RECON ALREADY ESTABLISHED (this session, two read agents + the engine source):
///   - The discriminator lives on each `*ConditionData` ARM as two bools `UseAliases` / `UsePackageData`
///     (plus `RunOnType` enum + `RunOnTypeIndex` int), NOT in `Condition.Flags` (which only has OR /
///     SwapSubjectAndTarget). 424 arms; 156 FormLinkOrIndex sites across 33 distinct T; 44 separate
///     `System.Object` VATS params (a DIFFERENT deferral → the MCP typed-value wire format, not this).
///   - `IFormLinkOrIndex&lt;T&gt;` is OPAQUE in the corpus (a plain formlink leaf, no exposed members).
///     Its concrete member/ctor surface is the unknown this probe reads off the LIVE Mutagen type at the referenced version.
///
/// HYPOTHESIS UNDER TEST (named so it can be falsified, NOT assumed): the FormLinkOrIndex object carries
/// its own form-vs-index state (a Link half + an Index half + a discriminator), settable via a ctor or a
/// SetTo-style method; re-setting a native target to its own current value through that mechanism
/// reproduces the source bytes EXACTLY (the oracle test, mirroring write-proof Phases 3/4/6).
///
/// Phase A discovers the actual API by reflection (robust to a version bump). Phase B finds REAL
/// condition targets (form-mode and index-mode) in a real plugin, prints each live instance's concrete
/// type + members (the definitive API read), then runs a reconstruct-to-native byte oracle. Fails are
/// REPORTED, never crash — discovery is the point (Q3: a no-mechanism outcome is a real finding to
/// surface, never a guess to ship).
///
/// Run: <c>dotnet run --project src/housecarl-generator condition-probe [sourcePlugin]</c>
/// </summary>
public static class ConditionProbe
{
    /// <summary>The Mutagen version actually loaded, read off the assembly rather than written down. A probe
    /// banner that names a version by hand goes stale on the next bump and nothing catches it; this cannot.</summary>
    static string MutagenVersion =>
        typeof(Mutagen.Bethesda.Skyrim.ISkyrimModGetter).Assembly.GetName().Version?.ToString() ?? "unknown";

    const string DefaultSource =
        @"C:\Program Files (x86)\Steam\steamapps\common\Skyrim Special Edition\Data\Skyrim.esm";
    static readonly ModKey PatchKey = new("hc_cond_probe", ModType.Plugin);

    public static int RunConditionProbe(string[] args)
    {
        var src = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : DefaultSource;
        var asm = typeof(IArmorGetter).Assembly;

        Console.WriteLine("################  WAVE 4 SCOUT — condition FormLinkOrIndex (form-vs-index) resolution  ################");
        Console.WriteLine();

        var mutagen = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => (a.GetName().Name ?? "").StartsWith("Mutagen"))
            .OrderBy(a => a.GetName().Name).ToList();

        PhaseA(asm, mutagen);

        if (!File.Exists(src))
        {
            Console.Error.WriteLine($"error: source plugin not found: {src} (Phase A done; Phase B needs a plugin).");
            return 1;
        }
        return PhaseB(src, asm);
    }

    // =====================================================================
    //  PHASE A — discover the actual Mutagen FormLinkOrIndex + Condition API
    // =====================================================================
    static void PhaseA(Assembly asm, List<Assembly> mutagen)
    {
        Console.WriteLine($"===== PHASE A : Mutagen FormLinkOrIndex / Condition API surface (pinned {MutagenVersion}) =====");
        Console.WriteLine();

        Console.WriteLine("-- loaded Mutagen assemblies --");
        foreach (var a in mutagen) Console.WriteLine($"   {a.GetName().Name} {a.GetName().Version}");
        Console.WriteLine();

        // A.1 — the IFormLinkOrIndex open generics + every concrete class implementing them.
        Console.WriteLine("-- IFormLinkOrIndex<T> / IFormLinkOrIndexGetter<T> (interfaces + members) --");
        var mTypes = mutagen.SelectMany(SafeTypes).ToList();
        foreach (var t in mTypes.Where(t => t.IsInterface && t.Name.StartsWith("IFormLinkOrIndex")).OrderBy(t => t.Name))
        {
            Console.WriteLine($"   {Pretty(t)}   [{t.Assembly.GetName().Name}]");
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                Console.WriteLine($"       (prop)   {p.Name} : {Pretty(p.PropertyType)}  {(p.CanWrite ? "{get;set;}" : "{get;}")}");
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => !m.IsSpecialName))
                Console.WriteLine($"       (method) {Sig(m)}");
        }
        Console.WriteLine();

        Console.WriteLine("-- concrete class(es) implementing IFormLinkOrIndex<T> + their constructors --");
        var floiOpen = mTypes.FirstOrDefault(t => t.IsInterface && t.IsGenericTypeDefinition && t.Name == "IFormLinkOrIndex`1");
        foreach (var c in mTypes.Where(c => c is { IsClass: true } && !c.IsAbstract && c.GetInterfaces()
                     .Any(i => i.IsGenericType && floiOpen != null && i.GetGenericTypeDefinition() == floiOpen)).OrderBy(c => c.Name))
        {
            Console.WriteLine($"   {Pretty(c)}   [{c.Assembly.GetName().Name}]");
            foreach (var ctor in c.GetConstructors())
                Console.WriteLine($"       (ctor)   ({string.Join(", ", ctor.GetParameters().Select(p => Pretty(p.ParameterType) + " " + p.Name))})");
            foreach (var p in c.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                Console.WriteLine($"       (prop)   {p.Name} : {Pretty(p.PropertyType)}  {(p.CanWrite ? "{get;set;}" : "{get;}")}");
            foreach (var m in c.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .Where(m => !m.IsSpecialName && m.DeclaringType == c))
                Console.WriteLine($"       (method) {Sig(m)}");
        }
        Console.WriteLine();

        // A.2 — a representative arm: confirm where the discriminator lives (on the ARM, not Condition).
        Console.WriteLine("-- representative arm HasKeywordConditionData: discriminator + target fields --");
        var hkcd = asm.GetType("Mutagen.Bethesda.Skyrim.HasKeywordConditionData");
        if (hkcd is null) Console.WriteLine("   !! HasKeywordConditionData not found — SURFACE (arm naming changed?).");
        else
            foreach (var p in hkcd.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.Name is "Keyword" or "UseAliases" or "UsePackageData" or "RunOnType" or "RunOnTypeIndex" or "Reference" or "Function"))
                Console.WriteLine($"   {p.Name,-16}: {Pretty(p.PropertyType)}  {(p.CanWrite ? "{get;set;}" : "{get;}")}");
        Console.WriteLine();

        // A.3 — Condition.Flag + Condition.RunOnType enums (confirm the flag is NOT the form/index discriminator).
        Console.WriteLine("-- Condition.Flag / Condition.RunOnType enum values --");
        foreach (var en in new[] { "Condition+Flag", "Condition+RunOnType" })
        {
            var et = asm.GetType("Mutagen.Bethesda.Skyrim." + en);
            Console.WriteLine($"   {en.Replace('+', '.')}: {(et is null ? "(not found)" : string.Join(", ", Enum.GetNames(et)))}");
        }
        Console.WriteLine();

        // A.4 — re-derive the FormLinkOrIndex SITE count BY CONSTRUCTION (all concrete *ConditionData arms).
        Console.WriteLine("-- FormLinkOrIndex sites, derived by construction (concrete ConditionData arms) --");
        var armBase = asm.GetType("Mutagen.Bethesda.Skyrim.IConditionDataGetter");
        var arms = asm.GetTypes().Where(t => t is { IsClass: true } && !t.IsAbstract
            && !t.Name.EndsWith("BinaryOverlay", StringComparison.Ordinal)
            && armBase != null && armBase.IsAssignableFrom(t)).ToList();
        int sites = 0; var owners = new HashSet<string>(); var targets = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var arm in arms)
            foreach (var p in arm.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (IsFloi(p.PropertyType))
                {
                    sites++; owners.Add(arm.Name);
                    var tn = p.PropertyType.GetGenericArguments()[0].Name;
                    targets[tn] = targets.GetValueOrDefault(tn) + 1;
                }
        Console.WriteLine($"   concrete ConditionData arms: {arms.Count} | FormLinkOrIndex sites: {sites} | distinct T: {targets.Count} | owner arms: {owners.Count}");
        Console.WriteLine($"   (cf. coerce-audit: 156 sites / 33 distinct T) {(sites == 156 && targets.Count == 33 ? "== MATCH ==" : "!! DRIFT — investigate")}");
        foreach (var (t, n) in targets.OrderByDescending(kv => kv.Value).Take(8)) Console.WriteLine($"        {n,3}x  {t}");
        Console.WriteLine();
    }

    // =====================================================================
    //  PHASE B — find real form-mode + index-mode targets, characterize, byte-oracle
    // =====================================================================
    static int PhaseB(string src, Assembly asm)
    {
        Console.WriteLine($"===== PHASE B : real condition targets against {Path.GetFileName(src)} =====");
        Console.WriteLine();

        var shaBefore = Sha(src);
        var sourceMod = SkyrimMod.CreateFromBinaryOverlay(src, SkyrimRelease.SkyrimSE);

        // Scan: every condition reachable from every record, with its owning record + arm + FormLinkOrIndex props.
        var formMode = new List<Hit>();
        var indexMode = new List<Hit>();
        int scannedConds = 0, scannedFloi = 0;
        foreach (var rec in sourceMod.EnumerateMajorRecords())
            foreach (var (cond, fieldName, condIndex) in EnumerateConditions(rec))
            {
                scannedConds++;
                var data = cond.GetType().GetProperty("Data")?.GetValue(cond);
                if (data is null) continue;
                bool useAliases = (bool)(data.GetType().GetProperty("UseAliases")?.GetValue(data) ?? false);
                bool usePackData = (bool)(data.GetType().GetProperty("UsePackageData")?.GetValue(data) ?? false);
                foreach (var fp in data.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!IsFloi(fp.PropertyType)) continue;
                    var floi = fp.GetValue(data);
                    if (floi is null) continue;
                    scannedFloi++;
                    var hit = new Hit(rec.FormKey, ConcreteName(rec), fieldName, condIndex, ConcreteName(data), fp.Name, useAliases, usePackData, floi);
                    if (useAliases || usePackData) { if (indexMode.Count < 6) indexMode.Add(hit); }
                    else { if (formMode.Count < 6) formMode.Add(hit); }
                }
                if (formMode.Count >= 6 && indexMode.Count >= 6) break;
            }

        Console.WriteLine($"-- scan: {scannedConds} conditions, {scannedFloi} populated FormLinkOrIndex targets --");
        Console.WriteLine($"   form-mode samples (UseAliases=UsePackageData=false): {formMode.Count}");
        Console.WriteLine($"   index-mode samples (UseAliases|UsePackageData=true): {indexMode.Count}");
        Console.WriteLine();

        // B.1 — DEFINITIVE API READ: print one live instance of each mode (its concrete type + every member value).
        Console.WriteLine("-- live FormLinkOrIndex instance members (the definitive API read) --");
        DumpLive("FORM-mode ", formMode.FirstOrDefault());
        DumpLive("INDEX-mode", indexMode.FirstOrDefault());
        Console.WriteLine();

        var outDir = Path.GetFullPath(Path.Combine("write-output", "condition-probe"));
        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        Directory.CreateDirectory(outDir);

        // B.2 — RECONSTRUCT-TO-NATIVE BYTE ORACLE per mode: override the owner unchanged (native bytes), then
        // override again and RE-SET the same target via the DISCOVERED ctor (Phase A). Equal bytes ⟺ the
        // construction mechanism reproduces native exactly (mirrors write-proof Phases 3/4/6). The source link
        // cache is passed so a nested-group owner (e.g. an INFO/DialogResponses condition) routes through the
        // proven wave-3 path — so this oracle also proves wave-3 + wave-4 COMPOSE on a nested owner.
        Console.WriteLine("-- reconstruct-to-native byte oracle (construct via discovered ctor) --");
        var cache = sourceMod.ToImmutableLinkCache();
        int oraclePass = 0, oracleTried = 0;
        oracleTried += RunOracle(sourceMod, cache, outDir, "form", formMode.FirstOrDefault(), ref oraclePass);
        oracleTried += RunOracle(sourceMod, cache, outDir, "index", indexMode.FirstOrDefault(), ref oraclePass);
        Console.WriteLine();

        var sourceUnchanged = shaBefore == Sha(src);
        Console.WriteLine($"source unchanged: {(sourceUnchanged ? "YES" : "NO")}");
        Console.WriteLine($"=== condition-probe: {oraclePass}/{oracleTried} reconstruct-oracles byte-identical; " +
                          $"form-samples={formMode.Count} index-samples={indexMode.Count}; source {(sourceUnchanged ? "untouched" : "CHANGED")} ===");
        // The scout is exploratory: a non-passing oracle is a FINDING (surface it), not a build failure. Exit 0 if
        // source untouched and we at least characterized both modes; the findings doc records the verdict.
        return sourceUnchanged ? 0 : 1;
    }

    sealed record Hit(FormKey OwnerFk, string OwnerType, string FieldName, int CondIndex,
                      string ArmType, string FloiProp, bool UseAliases, bool UsePackData, object LiveFloi);

    static void DumpLive(string label, Hit? h)
    {
        if (h is null) { Console.WriteLine($"   {label}: (no sample found in source)"); return; }
        var t = h.LiveFloi.GetType();
        Console.WriteLine($"   {label}: {h.OwnerType} {h.OwnerFk} . {h.FieldName}[{h.CondIndex}] . {h.ArmType}.{h.FloiProp}");
        Console.WriteLine($"             runtime type = {Pretty(t)}   [{t.Assembly.GetName().Name}]   UseAliases={h.UseAliases} UsePackageData={h.UsePackData}");
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            string val;
            try { val = p.GetValue(h.LiveFloi)?.ToString() ?? "null"; }
            catch (Exception ex) { val = "<" + ex.GetType().Name + ">"; }
            Console.WriteLine($"               .{p.Name} : {Pretty(p.PropertyType)} = {val}");
        }
    }

    /// <summary>Override the owner unchanged (native) vs override + re-set the FormLinkOrIndex to its own current
    /// value via the DISCOVERED ctor; byte-compare. Each output goes in its own subdir but is named to match the
    /// patch ModKey (WritePatch enforces filename==ModKey). Returns 1 if a sample existed (oracle was tried).</summary>
    static int RunOracle(ISkyrimModGetter sourceMod, ILinkCache cache, string outDir, string mode, Hit? h, ref int pass)
    {
        if (h is null) { Console.WriteLine($"   [{mode}] no sample in source — oracle not run (build must source one)."); return 0; }
        try
        {
            var srcRec = sourceMod.EnumerateMajorRecords().First(r => r.FormKey == h.OwnerFk);

            // Native baseline: override the owner record unchanged (cache → nested owners reconstruct via wave-3).
            var p0 = new SkyrimMod(PatchKey, SkyrimRelease.SkyrimSE);
            WriteEngine.GenericGetOrAddAsOverride(p0, srcRec, cache);
            var path0 = Path.Combine(outDir, mode + "_native", PatchKey.FileName.String);
            WriteEngine.WritePatch(p0, sourceMod, path0);

            // Reconstruct: override, navigate to the arm's FormLinkOrIndex prop, re-set via the discovered ctor.
            var p1 = new SkyrimMod(PatchKey, SkyrimRelease.SkyrimSE);
            var ov = WriteEngine.GenericGetOrAddAsOverride(p1, srcRec, cache);
            var armInst = NavigateToArm(ov, h);
            var floiProp = armInst.GetType().GetProperty(h.FloiProp)!;
            var current = floiProp.GetValue(armInst)!;            // the live (mutable) FormLinkOrIndex on the override
            var how = ReSetToCurrent(current, floiProp, armInst, h);
            var path1 = Path.Combine(outDir, mode + "_rebuilt", PatchKey.FileName.String);
            WriteEngine.WritePatch(p1, sourceMod, path1);

            var (eq, detail) = Compare(path0, path1);
            if (eq) pass++;
            Console.WriteLine($"   [{mode}] re-set via {how}: {(eq ? "BYTE-IDENTICAL to native" : "DIVERGED — " + detail)}  ({h.OwnerType} {h.OwnerFk}.{h.ArmType}.{h.FloiProp})");
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException is { } ie ? $" / {ie.GetType().Name}: {ie.Message}" : "";
            Console.WriteLine($"   [{mode}] oracle ERROR — {ex.GetType().Name}: {ex.Message}{inner}");
        }
        return 1;
    }

    /// <summary>Navigate an override record to the mutable ConditionData arm carrying the target.</summary>
    static object NavigateToArm(IMajorRecord ov, Hit h)
    {
        var list = ov.GetType().GetProperty(h.FieldName)!.GetValue(ov)!;
        var cond = ((IList)list)[h.CondIndex]!;
        return cond.GetType().GetProperty("Data")!.GetValue(cond)!;
    }

    /// <summary>Re-set a FormLinkOrIndex to the value it already holds, via the EXACT ctor Phase A discovered:
    /// <c>FormLinkOrIndex&lt;T&gt;(IFormLinkOrIndexFlagGetter parent, FormKey key)</c> [form mode], or
    /// <c>FormLinkOrIndex&lt;T&gt;(IFormLinkOrIndexFlagGetter parent, uint index)</c> [index/alias mode]. The
    /// <c>parent</c> IS the arm (it implements IFormLinkOrIndexFlagGetter, carrying the UseAliases/UsePackageData
    /// discriminator). The wave-4 build will use this same construct-and-assign path. Reports the mechanism used;
    /// a missing ctor throws a SURFACE-for-build message (Q3 — a no-mechanism outcome is a finding, not a guess).</summary>
    static string ReSetToCurrent(object currentFloi, PropertyInfo floiProp, object armInst, Hit h)
    {
        var ct = currentFloi.GetType();    // closed FormLinkOrIndex<T>
        if (h.UseAliases || h.UsePackData)
        {
            var raw = (uint?)ct.GetProperty("Index")!.GetValue(currentFloi)
                ?? throw new InvalidOperationException("index-mode FormLinkOrIndex has a null Index — SURFACE.");
            var ctor = ct.GetConstructors().FirstOrDefault(c => c.GetParameters() is { Length: 2 } p && p[1].ParameterType == typeof(uint))
                ?? throw new InvalidOperationException($"no (parent, uint) ctor on {Pretty(ct)} — SURFACE for build.");
            floiProp.SetValue(armInst, ctor.Invoke(new object[] { armInst, raw }));
            return $"ctor(arm, uint {raw})";
        }
        var fk = TryReadFormKey(currentFloi)
            ?? throw new InvalidOperationException("form-mode FormLinkOrIndex has a null FormKey — SURFACE.");
        var ctorF = ct.GetConstructors().FirstOrDefault(c => c.GetParameters() is { Length: 2 } p && p[1].ParameterType == typeof(FormKey))
            ?? throw new InvalidOperationException($"no (parent, FormKey) ctor on {Pretty(ct)} — SURFACE for build.");
        floiProp.SetValue(armInst, ctorF.Invoke(new object[] { armInst, fk }));
        return $"ctor(arm, FormKey {fk})";
    }

    /// <summary>Read the form-mode FormKey off a live FormLinkOrIndex via its <c>.Link</c> (IFormLinkNullable).</summary>
    static FormKey? TryReadFormKey(object floi)
    {
        var link = floi.GetType().GetProperty("Link")?.GetValue(floi);
        var inner = link?.GetType().GetProperty("FormKey")?.GetValue(link);
        return inner is FormKey k && !k.IsNull ? k : null;
    }

    // -- condition enumeration: yield (conditionGetter, owningFieldName, index) for every condition on a record. --
    static IEnumerable<(IConditionGetter cond, string field, int index)> EnumerateConditions(IMajorRecordGetter rec)
    {
        foreach (var p in rec.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var elem = EnumElement(p.PropertyType);
            if (elem is null || !typeof(IConditionGetter).IsAssignableFrom(elem)) continue;
            object? coll = null;
            try { coll = p.GetValue(rec); } catch { }
            if (coll is not IEnumerable e) continue;
            int i = 0;
            foreach (var item in e) { if (item is IConditionGetter c) yield return (c, p.Name, i); i++; }
        }
    }

    static Type? EnumElement(Type t)
    {
        if (t == typeof(string)) return null;
        var e = t.GetInterfaces().Concat(new[] { t }).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return e?.GetGenericArguments()[0];
    }

    static bool IsFloi(Type t) =>
        t.IsGenericType && (t.GetGenericTypeDefinition().Name.StartsWith("IFormLinkOrIndex")
                            || t.GetGenericTypeDefinition().Name.StartsWith("FormLinkOrIndex"));

    static string ConcreteName(object o) => o.GetType().Name;

    // -- diagnostic helpers (local copies; the probe touches no shared file). --

    static (bool eq, string detail) Compare(string a, string b)
    {
        var ba = File.ReadAllBytes(a);
        var bb = File.ReadAllBytes(b);
        if (ba.Length != bb.Length) return (false, $"SIZE {ba.Length}!={bb.Length}");
        for (int i = 0; i < ba.Length; i++)
            if (ba[i] != bb[i]) return (false, $"@0x{i:X} A=0x{ba[i]:X2} B=0x{bb[i]:X2}");
        return (true, "");
    }

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

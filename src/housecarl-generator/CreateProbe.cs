using System.Globalization;
using System.Reflection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// Capability-arc scout #3 — the LAST capability build (Create: new plugin + brand-new records with new FormIDs).
/// Session 1's <see cref="RemoveCreateProbe"/> proved the Create MECHANISM only TYPED + lightly (it called
/// <c>pc.Keywords.AddNew()</c> directly and set one EditorID). This probe measures the NET-NEW pieces the GENERIC
/// build actually needs, before the build (the wave-3/4/5 "measure, don't assume" precedent — it caught the
/// remove-record [Obsolete]/void surprise):
///
///   C1 (generic dispatch)  — reflective <c>group.AddNew()</c> dispatched from a record-type STRING across a SPREAD of
///       concrete flat types (Keyword/Weapon/Armor/Spell/MagicEffect/Perk/Faction/LeveledItem), reusing the SAME
///       <see cref="WriteEngine.EnumerateFlatGroups"/> enumeration that powers override-dispatch — so "createable" is
///       defined by construction (a type with a flat <c>SkyrimGroup&lt;T&gt;</c>), not by a hand-picked list.
///   C2 (FormID allocation) — what FormID does AddNew assign, and do successive AddNews increment? Measures the plan's
///       "fresh 0x800 ESP-local FormID" claim instead of asserting it. The new record's master = the new plugin itself.
///   C3 (fields via ApplyVerb) — set RICHER fields on a CREATED record through the proven <see cref="WriteEngine.ApplyVerb"/>
///       path (scalar + TranslatedString + a collection Add referencing an EXISTING master form), each pre-flighted by
///       the rulebook — create→edit reuses the write surface, and a cross-ref derives the master header.
///   C4 (end-to-end)        — serialize a BRAND-NEW plugin (fresh ModKey) carrying created records, re-open from disk,
///       confirm records persist with their FormKeys/EditorIDs/values + masters are correct (none when nothing is
///       referenced; the referenced master when something is).
///   C5 (the SCOPE FORK)    — the nested boundary + the abstract-T edge. A nested type (Cell/Placed*/INFO/Navmesh) has
///       NO flat <c>SkyrimGroup&lt;T&gt;</c>, so the generic dispatch can't reach it — a CLEAN loud-fail boundary, not a
///       silent miss. Plus: the <c>Globals</c> group's T (<c>Global</c>) is ABSTRACT — does <c>AddNew()</c> work or need
///       a typed variant? These two decide Create's scope (recommend flat-create-by-construction now; surface nested +
///       abstract-T) — an Aaron call (CLAUDE.md §4 / §5.5).
///
/// Recon only: writes to a temp dir, touches no tracked file, reports loud (Q3).
/// </summary>
public static class CreateProbe
{
    const string SkyrimEsm = @"C:\Program Files (x86)\Steam\steamapps\common\Skyrim Special Edition\Data\Skyrim.esm";

    // A spread of CONCRETE flat record types — the SkyPatcher/SPID authoring staples + a shape spread (substructs,
    // effect lists, leveled entries). Genericity is the claim: the same dispatch serves all, none is Keyword-special.
    static readonly string[] FlatTypes = { "Keyword", "Weapon", "Armor", "Spell", "MagicEffect", "Perk", "Faction", "LeveledItem" };

    public static int RunProbe(string[] args)
    {
        var src = args.FirstOrDefault(a => a.EndsWith(".esm", StringComparison.OrdinalIgnoreCase)) ?? SkyrimEsm;
        if (!File.Exists(src)) { Console.Error.WriteLine($"error: source master not found: {src}"); return 1; }

        Console.WriteLine("================================================================");
        Console.WriteLine(" capability-arc scout #3: Create (generic AddNew dispatch + FormID + nested boundary)");
        Console.WriteLine("================================================================");
        Console.WriteLine($"Source master: {src}");
        Console.WriteLine();

        var skyrim = SkyrimMod.CreateFromBinaryOverlay(src, SkyrimRelease.SkyrimSE);
        var shaBefore = Sha(src);
        var rulebook = CorpusRulebook.Load();

        var tmp = Path.Combine(Path.GetTempPath(), "hc-create-probe");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        Directory.CreateDirectory(tmp);

        bool allPass = true;

        // ---------------------------------------------------------- C1: generic reflective AddNew across a type spread
        Console.WriteLine("--- C1: generic group.AddNew() dispatched from a record-type STRING, across concrete flat types ---");
        {
            var pc = new SkyrimMod(new ModKey("HCCreateC1", ModType.Plugin), SkyrimRelease.SkyrimSE);
            bool c1ok = true;
            foreach (var typeName in FlatTypes)
            {
                var grp = FindFlatGroup(pc, typeName);
                if (grp is null) { Console.WriteLine($"   {typeName,-12} -> !! no flat SkyrimGroup<{typeName}> found"); c1ok = false; continue; }
                try
                {
                    var rec = AddNew(grp.Value.group, "HC_C1_" + typeName);
                    var edidOk = rec.EditorID == "HC_C1_" + typeName;
                    Console.WriteLine($"   {typeName,-12} -> {rec.FormKey}  EditorID={rec.EditorID}  (concrete {rec.GetType().Name}){(edidOk ? "" : "  !! editorid mismatch")}");
                    if (!edidOk) c1ok = false;
                }
                catch (Exception ex) { Console.WriteLine($"   {typeName,-12} -> !! AddNew threw: {(ex.InnerException ?? ex).GetType().Name}: {(ex.InnerException ?? ex).Message}"); c1ok = false; }
            }
            Pass(ref allPass, "C1 generic AddNew dispatch serves every concrete flat type by construction", c1ok);
        }
        Console.WriteLine();

        // ---------------------------------------------------------- C2: FormID allocation — range + increment
        Console.WriteLine("--- C2: FormID allocation — what range, does it increment? (measure the '0x800 ESP-local' claim) ---");
        {
            var pc = new SkyrimMod(new ModKey("HCCreateC2", ModType.Plugin), SkyrimRelease.SkyrimSE);
            var grp = FindFlatGroup(pc, "Keyword")!.Value.group;
            var ids = new List<uint>();
            for (int i = 0; i < 4; i++) { var r = AddNew(grp, null); ids.Add(r.FormKey.ID); Console.WriteLine($"   AddNew #{i} -> {r.FormKey}  (id=0x{r.FormKey.ID:X6}, master={r.FormKey.ModKey.FileName}, editorid={r.EditorID ?? "(null)"})"); }
            var firstAt800Plus = ids[0] >= 0x800;
            var increments = ids.Zip(ids.Skip(1), (a, b) => b == a + 1).All(x => x);
            var localMaster = pc.Keywords.First().FormKey.ModKey == pc.ModKey;
            Console.WriteLine($"   first id >= 0x800 (ESP-local reserved range respected): {(firstAt800Plus ? "YES" : "!! NO — " + $"0x{ids[0]:X6}")}");
            Console.WriteLine($"   successive AddNews increment the id: {(increments ? "YES" : "!! NO")}");
            Console.WriteLine($"   new record's master IS the new plugin (local): {(localMaster ? "YES" : "!! NO")}");
            Pass(ref allPass, "C2 AddNew allocates fresh, incrementing, local ESP-range FormIDs", firstAt800Plus && increments && localMaster);
        }
        Console.WriteLine();

        // ---------------------------------------------------------- C3 + C4: rich fields via ApplyVerb, then serialize + re-read
        Console.WriteLine("--- C3: set RICHER fields on a CREATED Armor via ApplyVerb (scalar + TranslatedString + keyword Add ref) ---");
        FormKey createdArmorFk = default, createdKwFk = default;
        var refKw = skyrim.Keywords.First();          // a REAL Skyrim.esm keyword to reference (drives the master derivation)
        {
            var pc = new SkyrimMod(new ModKey("HCCreateC4Ref", ModType.Plugin), SkyrimRelease.SkyrimSE);
            var armor = AddNew(FindFlatGroup(pc, "Armor")!.Value.group, "HC_C3_Armor");
            createdArmorFk = armor.FormKey;
            Console.WriteLine($"   created Armor {createdArmorFk} (HC_C3_Armor)");

            var edits = new[]
            {
                new WriteRequest { RecordType = "Armor", Path = new[] { "ArmorRating" }, Verb = "Set", Value = "42.5" },
                new WriteRequest { RecordType = "Armor", Path = new[] { "Name" }, Verb = "Set", Value = "houseCARL Test Cuirass" },
                new WriteRequest { RecordType = "Armor", Path = new[] { "Keywords" }, Verb = "Add", Value = refKw.FormKey.ToString() },
            };
            bool applied = true;
            foreach (var req in edits)
            {
                if (rulebook.Validate(req) is { } reject) { Console.WriteLine($"   pre-flight REJECT [{Label(req)}]: {reject}"); applied = false; break; }
                try { WriteEngine.ApplyVerb(armor, req); Console.WriteLine($"   applied [{Label(req)}]"); }
                catch (Exception ex) { Console.WriteLine($"   !! ApplyVerb threw on [{Label(req)}]: {(ex.InnerException ?? ex).Message}"); applied = false; break; }
            }
            Pass(ref allPass, "C3 create->edit reuses the proven ApplyVerb path (scalar + string + collection-Add)", applied);

            Console.WriteLine();
            Console.WriteLine("--- C4: serialize the BRAND-NEW plugin (created records only), re-open from disk, confirm persistence + masters ---");
            // Plugin REF: the armor (references a Skyrim.esm keyword) -> expect masters = [Skyrim.esm].
            var outRef = Path.Combine(tmp, "ref", pc.ModKey.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(outRef)!);
            WriteEngine.WritePatch(pc, skyrim, outRef);
            using (var back = SkyrimMod.CreateFromBinaryOverlay(outRef, SkyrimRelease.SkyrimSE))
            {
                var masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
                var a = back.Armors.FirstOrDefault(x => x.FormKey == createdArmorFk);
                var rating = a?.ArmorRating;
                var name = a?.Name?.String;
                var hasKw = a?.Keywords?.Any(k => k.FormKey == refKw.FormKey) ?? false;
                Console.WriteLine($"   re-read {pc.ModKey.FileName}: Armor present={a is not null}  ArmorRating={rating}  Name=\"{name}\"  hasRefKeyword={hasKw}");
                Console.WriteLine($"   masters: {Fmt(masters)}  (EXPECT Skyrim.esm — the created Armor references its keyword)");
                Pass(ref allPass, "C4a created Armor persists with all fields + derives the referenced master",
                    a is not null && rating.HasValue && Math.Abs(rating.Value - 42.5f) < 0.01f && name == "houseCARL Test Cuirass" && hasKw && masters.Any(IsSkyrim));
            }
        }
        // Plugin LOCAL: one created keyword that references NOTHING -> expect masters = [] (a self-contained new plugin).
        {
            var pc = new SkyrimMod(new ModKey("HCCreateC4Local", ModType.Plugin), SkyrimRelease.SkyrimSE);
            var kw = AddNew(FindFlatGroup(pc, "Keyword")!.Value.group, "HC_C4_LocalKeyword");
            createdKwFk = kw.FormKey;
            var outLocal = Path.Combine(tmp, "local", pc.ModKey.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(outLocal)!);
            WriteEngine.WritePatch(pc, skyrim, outLocal);
            using var back = SkyrimMod.CreateFromBinaryOverlay(outLocal, SkyrimRelease.SkyrimSE);
            var masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
            var present = back.Keywords.Any(k => k.FormKey == createdKwFk && k.EditorID == "HC_C4_LocalKeyword");
            // Post-fix (commit d60bdd8 + Update.esm): WritePatch force-includes the CK baseline masters PRESENT in the
            // load order. This scout loads ONLY Skyrim.esm, so the filter forces just Skyrim.esm here (a full order also
            // gets Update.esm — see create-proof K1). Assert: not masterless, carries Skyrim.esm, no NON-baseline master.
            Console.WriteLine($"   re-read {pc.ModKey.FileName}: Keyword present={present}  masters={Fmt(masters)}  (EXPECT Skyrim.esm — the only CK baseline this minimal scout loads)");
            Pass(ref allPass, "C4b a created record with no external ref carries the CK baseline (Skyrim.esm here; +Update.esm in a full order)",
                present && masters.Count >= 1
                && masters.Any(m => m.Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase))
                && masters.All(m => m.Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase) || m.Equals("Update.esm", StringComparison.OrdinalIgnoreCase)));
        }
        Console.WriteLine();

        // ---------------------------------------------------------- C5: the SCOPE FORK — nested boundary + abstract-T edge
        Console.WriteLine("--- C5: SCOPE FORK — flat-group surface size, the nested loud-fail boundary, the abstract-T (Global) edge ---");
        {
            var flat = WriteEngine.EnumerateFlatGroups(typeof(SkyrimMod)).ToList();
            Console.WriteLine($"   flat SkyrimGroup<T> count = {flat.Count}  (= the create-BY-CONSTRUCTION surface: every one is createable via the C1 dispatch)");

            // A nested type has NO flat group → FindFlatGroup returns null → generic dispatch cannot reach it. Confirm the
            // boundary is CLEAN (a null lookup the build turns into a loud refusal), not a silent miss.
            var pc = new SkyrimMod(new ModKey("HCCreateC5", ModType.Plugin), SkyrimRelease.SkyrimSE);
            IMajorRecordGetter? nested = null;
            foreach (var rec in skyrim.EnumerateMajorRecords()) if (WriteEngine.RecordNeedsSourceCache(rec)) { nested = rec; break; }
            if (nested is not null)
            {
                var nestedType = RecordNaming.StripOverlay(nested.GetType().Name);
                var lookup = FindFlatGroup(pc, nestedType);
                Console.WriteLine($"   nested sample type '{nestedType}': flat-group lookup = {(lookup is null ? "null (NOT createable by the generic dispatch — clean boundary)" : "!! UNEXPECTEDLY FOUND a flat group")}");
                Pass(ref allPass, "C5 nested type has no flat group → create dispatch fails CLEAN + loud (not a silent miss)", lookup is null);
                Console.WriteLine("   => nested-record create needs PARENT CONTEXT (which cell/worldspace/topic the new record lives in) — a");
                Console.WriteLine("      distinct operation shape (create-AND-place), not 'more types'. Recommend: name it a loud-fail boundary now.");
            }
            else { Console.WriteLine("   !! no nested-group record in source — cannot test the boundary"); allPass = false; }

            // Abstract-T edge: the Globals group's T (Global) is abstract. Does the generic AddNew() work, or does an
            // abstract-T group need a typed variant (AddNewShort/Int/Float)? A real build consideration — surface it.
            var globals = FindFlatGroup(pc, "Global");
            if (globals is not null)
            {
                var tAbstract = globals.Value.tMajor.IsAbstract;
                Console.WriteLine($"   Globals group T='{globals.Value.tMajor.Name}' abstract={tAbstract}");
                string outcome;
                try { var g = AddNew(globals.Value.group, "HC_C5_Global"); outcome = $"AddNew() succeeded -> {g.GetType().Name} {g.FormKey}"; }
                catch (Exception ex) { outcome = $"AddNew() threw {(ex.InnerException ?? ex).GetType().Name}: {(ex.InnerException ?? ex).Message}"; }
                Console.WriteLine($"   abstract-T AddNew outcome: {outcome}");
                Console.WriteLine("   => if it threw, abstract-T groups (Global) need a typed-variant AddNew or a loud-fail at build time — surface, don't guess.");
            }
            else Console.WriteLine("   (no 'Global' flat group resolved by simple name — abstract-T edge not measured here)");
        }
        Console.WriteLine();

        var sourceUnchanged = shaBefore == Sha(src);
        Console.WriteLine($"Source unchanged: {(sourceUnchanged ? "YES" : "NO")}");
        Console.WriteLine();
        var pass = allPass && sourceUnchanged;
        Console.WriteLine(pass
            ? "=== PASS: generic Create mechanism PROVEN for flat records (dispatch + FormID + fields + serialize); nested + abstract-T boundaries characterized. ==="
            : "=== read the per-C lines above (a !! marks the thing to resolve before/at the build). ===");
        return pass ? 0 : 1;

        static bool IsSkyrim(string m) => m.Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase);
        static string Fmt(IEnumerable<string> xs) { var l = xs.ToList(); return l.Count == 0 ? "(none)" : string.Join(", ", l); }
        static string Sha(string p) { using var s = File.OpenRead(p); using var h = System.Security.Cryptography.SHA256.Create(); return Convert.ToHexString(h.ComputeHash(s)); }
        static string Label(WriteRequest r) => $"{r.Verb} {string.Join('.', r.Path)}{(r.Key is not null ? "[" + r.Key + "]" : "")}{(r.Value is not null ? " = " + r.Value : "")}";
        static void Pass(ref bool all, string label, bool ok) { Console.WriteLine($"   [{(ok ? "PASS" : "!! FAIL")}] {label}"); if (!ok) all = false; }
    }

    /// <summary>Find the mod's flat <c>SkyrimGroup&lt;T&gt;</c> for a record-type simple name, reusing the engine's single
    /// group enumeration (so the scout's "createable" set can never drift from the override-dispatch set). Returns the
    /// live group instance + its T + getter interface, or null when no flat group models that type (= a nested type).</summary>
    static (object group, Type tMajor, Type getterIface)? FindFlatGroup(SkyrimMod mod, string typeName)
    {
        foreach (var (prop, tMajor, getterIface) in WriteEngine.EnumerateFlatGroups(typeof(SkyrimMod)))
            if (string.Equals(tMajor.Name, typeName, StringComparison.OrdinalIgnoreCase))
                return (prop.GetValue(mod)!, tMajor, getterIface);
        return null;
    }

    /// <summary>Reflectively allocate a new record in a flat group: <c>AddNew()</c> (engine-assigned editorid) or
    /// <c>AddNew(string editorID)</c>. The build's create front-end is this call (where override-dispatch calls
    /// GetOrAddAsOverride). <c>AddNew</c> is NOT a plain instance method on <c>SkyrimGroup&lt;T&gt;</c> (measured: a
    /// direct GetMethod misses it) — like <c>GetOrAddAsOverride</c> it's an EXTENSION method (likely generic) in a
    /// Mutagen static class, so this searches instance, interfaces, THEN the Mutagen extension classes, closing a generic
    /// definition with T (+ the getter for arity-2). Announces which form it resolved (so the build copies the real call
    /// shape). Throws loud if nothing matches (Q3 — never a silent failure).</summary>
    static IMajorRecord AddNew(object group, string? editorId)
    {
        var gt = group.GetType();
        var tMajor = gt.IsGenericType ? gt.GetGenericArguments()[0] : gt;
        var getterIface = tMajor.GetInterface("I" + tMajor.Name + "Getter");
        int userArgs = editorId is null ? 0 : 1;     // args beyond the receiver

        // (a) instance method on the concrete group or any interface it implements.
        foreach (var cand in new[] { gt }.Concat(gt.GetInterfaces()))
        {
            var m = cand.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(x =>
                x.Name == "AddNew" && x.GetParameters().Length == userArgs
                && (userArgs == 0 || x.GetParameters()[0].ParameterType == typeof(string)));
            if (m is not null) { Announce($"{cand.Name}.AddNew (instance)"); return (IMajorRecord)m.Invoke(group, userArgs == 0 ? null : new object[] { editorId! })!; }
        }

        // (b) extension method (possibly generic) in a Mutagen static class — the same shape OverrideMethod() hunts for.
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().Where(a => (a.GetName().Name ?? "").StartsWith("Mutagen")))
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }
            foreach (var t in types.Where(t => t is { IsAbstract: true, IsSealed: true }))
                foreach (var open in t.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(x => x.Name == "AddNew"))
                {
                    if (open.GetParameters().Length != userArgs + 1) continue;   // receiver + user args
                    MethodInfo m;
                    try
                    {
                        var ga = open.GetGenericArguments().Length;
                        m = ga == 0 ? open
                          : ga == 1 ? open.MakeGenericMethod(tMajor)
                          : getterIface is not null ? open.MakeGenericMethod(tMajor, getterIface) : open;
                    }
                    catch { continue; }                                          // generic constraints didn't fit this T
                    var ps = m.GetParameters();
                    if (!ps[0].ParameterType.IsInstanceOfType(group)) continue;   // receiver must accept this group
                    if (userArgs == 1 && ps[1].ParameterType != typeof(string)) continue;
                    Announce($"{t.Name}.AddNew (extension{(open.IsGenericMethodDefinition ? $", generic<{open.GetGenericArguments().Length}>" : "")})");
                    var argv = userArgs == 0 ? new object[] { group } : new object[] { group, editorId! };
                    return (IMajorRecord)m.Invoke(null, argv)!;
                }
        }
        throw new InvalidOperationException(
            $"could not resolve AddNew({(editorId is null ? "" : "string")}) for {gt.Name} — searched instance, interfaces, and Mutagen extension classes.");
    }

    static readonly HashSet<string> _announced = new();
    static void Announce(string what) { if (_announced.Add(what)) Console.WriteLine($"   [api] AddNew resolved via {what}"); }
}

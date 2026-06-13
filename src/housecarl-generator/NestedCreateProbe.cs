using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlGenerator;

/// <summary>
/// STEP 0 SCOUT — nested-record CREATE (throwaway recon, not a standing instrument).
///
/// The blocking pre-build scout the nested/dialogue plan (<c>dev/plans/NESTED_DIALOGUE_AUTHORING_PLAN_2026-06-13.md</c>
/// §1.4 + §6 step 0) gates the whole build on. <see cref="NestedProbe"/> already proved nested OVERRIDE resolution
/// (resolve a record's <c>IModContext</c> by FormKey → <c>GetOrAddAsOverride(patch)</c> rebuilds the parent chain).
/// This probe answers the harder, unproven question: can houseCARL ALLOCATE a brand-new record INTO a nested parent
/// — by construction, across the FormKey-parented families — and what does the add-target / FormID-floor / coordinate
/// seam actually do? Fails LOUD per shape (Q3). Recon only: writes to a temp dir, touches no tracked file.
///
/// THE THREE QUESTIONS (plan §1.4):
///   Q1  existence/behavior — does a nested-alloc primitive allocate a fresh child with a valid LOCAL 0x800+ FormKey,
///       serialize, and re-open clean, ACROSS families (not just the easy one)?
///   Q2  add-target derivability — is the collection to add into (i) derivable from the child type, (ii) chosen by a
///       caller-supplied NAMED-collection discriminator (both stay by-construction = §4-(a)), or (iii) hand-coded
///       per family (= §4-(b), STOP)?
///   Q3  the coordinate-keyed parent — does Mutagen auto-sort an exterior cell into the right WorldspaceBlock/SubBlock
///       from <c>Cell.Grid</c>, or must houseCARL compute the 32/8 block math? (Characterize; this is the separate
///       §4-(b) seam, NOT to be folded into the generic Layer-A claim.)
///
/// TWO CANDIDATE PRIMITIVES (the plan names DuplicateIntoAsNewRecord prime; recon showed it duplicates into the
/// SOURCE record's own parent, so it can't, alone, build the PRIMARY dialogue case — a NEW topic with no INFO to
/// clone). So both are tested:
///   P1  IModContext.DuplicateIntoAsNewRecord(patch, editorID) — clone an EXISTING child as a fresh record (sibling-add).
///   P2  construct a child with a patch-allocated FormKey + Add it into the parent's modeled collection (first-child /
///       new-parent — the actual dialogue entry shape). The by-construction one if the collection is reflectable.
///
/// Run: <c>dotnet run --project src/housecarl-generator nested-create-probe [sourcePlugin]</c>
/// </summary>
public static class NestedCreateProbe
{
    const string DefaultSource =
        @"E:\Skyrim Modding\ARR 2.0\Stock Game\Data\Skyrim.esm";

    public static int RunProbe(string[] args)
    {
        var src = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : DefaultSource;
        var asm = typeof(IArmorGetter).Assembly;

        Console.WriteLine("################  STEP 0 SCOUT — nested-record CREATE  ################");
        Console.WriteLine($"source: {src}");
        Console.WriteLine();
        if (!File.Exists(src)) { Console.Error.WriteLine($"error: source plugin not found: {src}"); return 1; }

        var shaBefore = Sha(src);
        var sourceMod = SkyrimMod.CreateFromBinaryOverlay(src, SkyrimRelease.SkyrimSE);
        var cache = sourceMod.ToImmutableLinkCache();

        var outDir = Path.Combine(Path.GetTempPath(), "hc-nested-create-probe");
        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        Directory.CreateDirectory(outDir);

        // Q-verdict accumulators.
        bool q1ok = true, q2ok = true;
        var addTarget = new List<(string family, string parent, int matchingColls, string verdict)>();

        // The by-FormKey context resolver off the LIVE cache (re-resolved closed-generic, per NestedProbe §A.3).
        var resolveCtx = cache.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "ResolveContext" && m.IsGenericMethodDefinition
                && m.GetGenericArguments().Length == 2 && m.GetParameters().Length >= 1
                && m.GetParameters()[0].ParameterType == typeof(FormKey));
        if (resolveCtx is null) { Console.Error.WriteLine("!! no ResolveContext<T,TG>(FormKey,...) on the live cache — surface, do not guess."); return 1; }

        // ============================================================ PHASE A : API discovery (reflection) ============================================================
        Console.WriteLine("===== PHASE A : nested-alloc API surface (pinned Mutagen 0.53.1) =====");
        var ctxIface = asm.GetType("Mutagen.Bethesda.Plugins.Records.IModContext`4")
            ?? AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes)
                 .FirstOrDefault(t => t.IsInterface && t.Name == "IModContext`4");
        Console.WriteLine("-- IModContext<TMod,TModGetter,TTarget,TTargetGetter> alloc members --");
        foreach (var m in (ctxIface?.GetMethods() ?? Array.Empty<MethodInfo>())
                     .Where(m => m.Name is "GetOrAddAsOverride" or "DuplicateIntoAsNewRecord"))
            Console.WriteLine($"     {Sig(m)}");
        // The mod-level FormKey allocator (what a direct-construct child must draw from).
        Console.WriteLine("-- IMod FormKey allocator --");
        foreach (var m in sourceMod.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                     .Where(m => m.Name == "GetNextFormKey"))
            Console.WriteLine($"     {Sig(m)}");
        Console.WriteLine();

        // ============================================================ PHASE E : FormID floor (do both primitives respect 0x800?) ============================================================
        Console.WriteLine("===== PHASE E : FormID floor — fresh-mod allocator start + the 0x800 floor =====");
        {
            var fresh = new SkyrimMod(new ModKey("hc_floor", ModType.Plugin), SkyrimRelease.SkyrimSE);
            uint before = fresh.ModHeader.Stats.NextFormID;
            var preFloorKey = TryGetNextFormKey(fresh, null);
            WriteEngine.EnsureFormIdFloor(fresh);
            uint after = fresh.ModHeader.Stats.NextFormID;
            var postFloorKey = TryGetNextFormKey(fresh, "HC_floor_edid");
            Console.WriteLine($"   fresh NextFormID (header default)        = 0x{before:X}");
            Console.WriteLine($"   GetNextFormKey() BEFORE EnsureFormIdFloor = {Show(preFloorKey)}  {(preFloorKey is { } a && a.ID < 0x800 ? "<< below 0x800 (engine-reserved) — build MUST floor first" : "")}");
            Console.WriteLine($"   NextFormID AFTER EnsureFormIdFloor       = 0x{after:X}");
            Console.WriteLine($"   GetNextFormKey() AFTER EnsureFormIdFloor  = {Show(postFloorKey)}  {(postFloorKey is { } b && b.ID >= 0x800 ? "OK >= 0x800" : "!! still below floor")}");
            Console.WriteLine("   => the nested-create front-end must call EnsureFormIdFloor before allocating, exactly like flat GenericAddNew (HCBR-2026-06-09-04 class).");
        }
        Console.WriteLine();

        // ============================================================ PHASE B : P1 — DuplicateIntoAsNewRecord (clone-a-sibling) across families ============================================================
        Console.WriteLine("===== PHASE B : P1 = DuplicateIntoAsNewRecord (clone an existing child as a fresh record) =====");
        // One sample child per FormKey-parented family present in vanilla (from the nested-probe inventory).
        var families = new (string name, FormKey sample)[]
        {
            ("DialogResponses", FormKey.Factory("000E3D:Skyrim.esm")),
            ("PlacedObject",    FormKey.Factory("0EBEBF:Skyrim.esm")),
            ("PlacedNpc",       FormKey.Factory("029D87:Skyrim.esm")),
            ("PlacedHazard",    FormKey.Factory("0F49AE:Skyrim.esm")),
            ("PlacedTrap",      FormKey.Factory("103997:Skyrim.esm")),
            ("NavigationMesh",  FormKey.Factory("09CB6A:Skyrim.esm")),
            ("Landscape",       FormKey.Factory("00A0E3:Skyrim.esm")),
        };
        foreach (var (name, sample) in families)
        {
            try
            {
                var getterIface = asm.GetType("Mutagen.Bethesda.Skyrim.I" + name + "Getter")!;
                var setterIface = asm.GetType("Mutagen.Bethesda.Skyrim.I" + name)!;
                var ctx = resolveCtx.MakeGenericMethod(setterIface, getterIface).Invoke(cache, BuildResolveArgs(resolveCtx, sample));
                if (ctx is null) { Console.WriteLine($"[FAIL] {name,-16} — ResolveContext null."); q1ok = false; continue; }

                var srcRec = (IMajorRecordGetter)ctx.GetType().GetProperty("Record")!.GetValue(ctx)!;
                var parentDesc = DescribeParent(ctx.GetType().GetProperty("Parent")?.GetValue(ctx));

                var patch = new SkyrimMod(new ModKey("hc_dup_" + name, ModType.Plugin), SkyrimRelease.SkyrimSE);
                // NOT pre-flooring here on purpose — observe whether Duplicate respects 0x800 on a fresh patch.
                var dup = ctx.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m =>
                    m.Name == "DuplicateIntoAsNewRecord" && m.GetParameters().Length == 2
                    && m.GetParameters()[0].ParameterType.IsInstanceOfType(patch)
                    && m.GetParameters()[1].ParameterType == typeof(string));
                if (dup is null) { Console.WriteLine($"[FAIL] {name,-16} — no DuplicateIntoAsNewRecord(mod, string)."); q1ok = false; continue; }

                var newRec = (IMajorRecordGetter)dup.Invoke(ctx, new object[] { patch, "HC_S0_DUP_" + name })!;
                var newFk = newRec.FormKey;
                var settable = setterIface.IsInstanceOfType(newRec);
                var isNew = newFk != srcRec.FormKey;
                var local = newFk.ModKey == patch.ModKey;
                var floored = newFk.ID >= 0x800;

                var outPath = Path.Combine(outDir, patch.ModKey.FileName);
                WriteEngine.WritePatch(patch, sourceMod, outPath);
                using var back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE);
                var all = back.EnumerateMajorRecords().ToList();
                var present = all.Any(r => r.FormKey == newFk);
                var masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
                var carried = string.Join("+", all.Select(r => RecordNaming.StripOverlay(r.GetType().Name)).Distinct().OrderBy(s => s));

                var ok = settable && isNew && local && floored && present;
                if (!ok) q1ok = false;
                Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name,-16} new={newFk} (src {srcRec.FormKey})  parent={parentDesc}");
                Console.WriteLine($"         settable={settable} fresh-fk={isNew} local={local} >=0x800={floored} reopened-present={present} | patch-records={all.Count} ({carried}) masters=[{string.Join(",", masters)}]");
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException is { } ie ? $" / {ie.GetType().Name}: {ie.Message}" : "";
                Console.WriteLine($"[FAIL] {name,-16} — {ex.GetType().Name}: {ex.Message}{inner}");
                q1ok = false;
            }
        }
        Console.WriteLine("   note: P1 duplicates into the SOURCE child's OWN parent — it adds a SIBLING, it cannot target an arbitrary/empty parent. (Phase C covers that.)");
        Console.WriteLine();

        // ============================================================ PHASE C : P2 — construct + Add into the parent's modeled collection (the new-parent case) ============================================================
        Console.WriteLine("===== PHASE C : P2 = construct child w/ patch FormKey + Add into the parent's collection =====");

        // C1 — INFO under a BRAND-NEW topic (the primary dialogue case: no sibling to clone). DialogTopic is FLAT
        // (SkyrimGroup<DialogTopic>) so the topic is created by the proven flat path; the INFO is the net-new seam.
        try
        {
            var patch = new SkyrimMod(new ModKey("hc_newtopic", ModType.Plugin), SkyrimRelease.SkyrimSE);
            var topic = WriteEngine.GenericAddNew(patch, "DialogTopic", "HC_S0_NewTopic");   // floors the counter
            var topicFk = topic.FormKey;
            var childType = asm.GetType("Mutagen.Bethesda.Skyrim.DialogResponses")!;
            var (coll, matching, names) = FindChildCollections(topic, childType);
            addTarget.Add(("DialogResponses", "DialogTopic", matching, (matching == 1 ? "(i) derivable by type" : matching > 1 ? "(ii) named discriminator" : "(iii) NONE — hand-coded") + " [" + string.Join("/", names) + "]"));
            if (coll is null) { Console.WriteLine("[FAIL] INFO/new-topic — no settable collection on DialogTopic accepts DialogResponses."); q2ok = false; }
            else
            {
                WriteEngine.EnsureFormIdFloor(patch);
                var infoFk = TryGetNextFormKey(patch, "HC_S0_Info1") ?? throw new InvalidOperationException("GetNextFormKey unavailable");
                var info = ConstructRecord(childType, infoFk);
                coll.Add(info);

                var outPath = Path.Combine(outDir, patch.ModKey.FileName);
                WriteEngine.WritePatch(patch, sourceMod, outPath);
                using var back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE);
                var reTopic = back.DialogTopics.FirstOrDefault(t => t.EditorID == "HC_S0_NewTopic");
                var responses = reTopic is null ? null : ChildCollectionValue(reTopic, childType);
                var infoPresent = responses is not null && responses.Cast<object>().Any(r => ((IMajorRecordGetter)r).FormKey == infoFk);
                var infoFloored = infoFk.ID >= 0x800 && infoFk.ModKey == patch.ModKey;
                var topicFloored = topicFk.ID >= 0x800;
                var ok = reTopic is not null && infoPresent && infoFloored && topicFloored;
                if (!ok) q2ok = false;
                Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] INFO under NEW topic   topic={topicFk} info={infoFk}");
                Console.WriteLine($"         topic>=0x800={topicFloored} info>=0x800/local={infoFloored} topic-reopened={reTopic is not null} info-under-topic={infoPresent} | Responses-collections-matching={matching}");
            }
        }
        catch (Exception ex) { Console.WriteLine($"[FAIL] INFO/new-topic — {ex.GetType().Name}: {(ex.InnerException ?? ex).Message}"); q2ok = false; }

        // C2 — Placed into an EXISTING (overridden) cell, choosing the collection BY NAME (outcome ii: a Cell has 3
        // placed lists, so the child type alone cannot pick — the caller names Persistent/Temporary/VisibleWhenDistant).
        try
        {
            var patch = new SkyrimMod(new ModKey("hc_placed_in_cell", ModType.Plugin), SkyrimRelease.SkyrimSE);
            var cellGetter = sourceMod.EnumerateMajorRecords<ICellGetter>().First(c => c.Persistent.Count > 0 || c.Temporary.Count > 0);
            var cell = (IMajorRecord)WriteEngine.GenericGetOrAddAsOverride(patch, cellGetter, cache);   // override the parent cell into the patch
            var childType = asm.GetType("Mutagen.Bethesda.Skyrim.PlacedObject")!;
            var (coll, matching, names) = FindChildCollections(cell, childType, preferName: "Persistent");
            addTarget.Add(("PlacedObject", "Cell", matching, (matching == 1 ? "(i) derivable by type" : matching > 1 ? "(ii) named discriminator" : "(iii) NONE — hand-coded") + " [" + string.Join("/", names) + "]"));
            if (coll is null) { Console.WriteLine("[FAIL] Placed/cell — no settable collection on Cell accepts PlacedObject."); q2ok = false; }
            else
            {
                WriteEngine.EnsureFormIdFloor(patch);
                var refFk = TryGetNextFormKey(patch, "HC_S0_Ref1") ?? throw new InvalidOperationException("GetNextFormKey unavailable");
                var placed = ConstructRecord(childType, refFk);
                coll.Add(placed);

                var outPath = Path.Combine(outDir, patch.ModKey.FileName);
                WriteEngine.WritePatch(patch, sourceMod, outPath);
                using var back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE);
                var present = back.EnumerateMajorRecords().Any(r => r.FormKey == refFk);
                var floored = refFk.ID >= 0x800 && refFk.ModKey == patch.ModKey;
                var ok = present && floored;
                if (!ok) q2ok = false;
                Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] Placed into cell {cellGetter.FormKey}  newRef={refFk}  collection=Persistent (chosen by name; matching-lists={matching})");
                Console.WriteLine($"         ref>=0x800/local={floored} reopened-present={present}");
            }
        }
        catch (Exception ex) { Console.WriteLine($"[FAIL] Placed/cell — {ex.GetType().Name}: {(ex.InnerException ?? ex).Message}"); q2ok = false; }
        Console.WriteLine();

        // ============================================================ PHASE D : Q3 — the coordinate-keyed seam (characterize, don't solve) ============================================================
        Console.WriteLine("===== PHASE D : Q3 = coordinate-keyed parents (exterior Cell / Placed-into-new-cell) — characterize the §4-(b) seam =====");
        try
        {
            // The structural parents of an exterior cell: Worldspace -> WorldspaceBlock -> WorldspaceSubBlock -> Cell.
            // Report whether those parents are FormKey-LESS structs (so the FormKey locator can't address them) and
            // whether Cell carries a Grid the placement math would key on.
            var wsType = asm.GetType("Mutagen.Bethesda.Skyrim.Worldspace")!;
            var blockProp = wsType.GetProperties().FirstOrDefault(p => p.Name is "SubCells" or "Blocks");
            var blockElem = blockProp is null ? null : ElementType(blockProp.PropertyType);
            var blockIsRecord = blockElem is not null && typeof(IMajorRecordGetter).IsAssignableFrom(blockElem);
            Console.WriteLine($"   Worldspace child-collection: {blockProp?.Name ?? "(none found)"} -> element {blockElem?.Name ?? "?"}  (IMajorRecord? {blockIsRecord})");
            if (blockElem is not null)
            {
                var subProp = blockElem.GetProperties().FirstOrDefault(p => p.Name is "Items" or "SubBlocks" or "Block");
                var subElem = subProp is null ? null : ElementType(subProp.PropertyType);
                Console.WriteLine($"     {blockElem.Name}.{subProp?.Name ?? "(?)"} -> element {subElem?.Name ?? "?"}  (FormKey-less struct? {(subElem is not null && !typeof(IMajorRecordGetter).IsAssignableFrom(subElem))})");
                var blockNum = blockElem.GetProperties().FirstOrDefault(p => p.Name is "BlockNumberX" or "BlockNumberY" or "BlockNumber");
                Console.WriteLine($"     block carries a numeric coordinate key: {(blockNum is not null ? blockNum.Name + " (placement is index math from Cell.Grid, not a FormKey lookup)" : "no explicit block-number prop found by simple name")}");
            }
            var cellType = asm.GetType("Mutagen.Bethesda.Skyrim.Cell")!;
            var gridProp = cellType.GetProperties().FirstOrDefault(p => p.Name == "Grid");
            Console.WriteLine($"   Cell.Grid present: {gridProp is not null} (type {gridProp?.PropertyType.Name})  — the X/Y a 32-cell-block / 8-cell-subblock division would compute from.");
            Console.WriteLine("   VERDICT Q3: exterior-cell create + Placed-into-not-yet-present-cell sit under FormKey-LESS struct parents (WorldspaceBlock/SubBlock)");
            Console.WriteLine("              and need derived block arithmetic outside the FormKey-locator mechanism — a SEPARATE §4-(b) decision, NOT in this build.");
        }
        catch (Exception ex) { Console.WriteLine($"   (could not fully characterize: {ex.GetType().Name}: {ex.Message})"); }
        Console.WriteLine();

        // ============================================================ VERDICT ============================================================
        var sourceUnchanged = shaBefore == Sha(src);
        Console.WriteLine("===== ADD-TARGET CLASSIFICATION (Q2) =====");
        foreach (var (family, parent, n, v) in addTarget)
            Console.WriteLine($"   {family,-16} under {parent,-12} matching-collections={n} -> {v}");
        Console.WriteLine();
        Console.WriteLine("===== STEP 0 VERDICT =====");
        Console.WriteLine($"   Q1 (alloc + serialize + reopen, across families)  : {(q1ok ? "HOLDS — P1 clone-a-sibling is generic across the present families" : "!! a family FAILED — read the [FAIL] lines")}");
        Console.WriteLine($"   Q2 (add-target by construction)                   : {(q2ok ? "HOLDS — P2 construct+Add works; collection is (i)/(ii) reflectable, never hand-coded" : "!! see the [FAIL] lines")}");
        Console.WriteLine($"   Q3 (coordinate-keyed cells)                       : SEPARATE §4-(b) seam (FormKey-less struct parents + block math) — characterized above, NOT built here");
        Console.WriteLine($"   source byte-unchanged                             : {(sourceUnchanged ? "YES" : "!! NO — CHANGED")}");
        Console.WriteLine();
        var pass = q1ok && q2ok && sourceUnchanged;
        Console.WriteLine(pass
            ? "=== STEP 0 PASS: a GENERIC nested-create mechanism holds for the FormKey-parented families (P1 sibling-add + P2 first-child); add-target is (i)/(ii) by construction; the coordinate-keyed seam is a named separate decision. STOP for Aaron's go. ==="
            : "=== STEP 0: read the [FAIL] lines above — a generic mechanism did NOT hold for some shape. Per §1.4: STOP and surface to Aaron as §4-(b)/(c), never a per-type shim. ===");
        return pass ? 0 : 1;
    }

    // ---- the parent's child-collection finder (the by-construction Q2 test) ----

    /// <summary>Find every SETTABLE list property on <paramref name="parent"/> whose element type the child
    /// <paramref name="childConcrete"/> satisfies, and return (the chosen IList to Add into, how many such lists exist).
    /// One match → outcome (i) derivable-by-type; &gt;1 → outcome (ii) the caller must NAME which (preferName picks it);
    /// zero → outcome (iii). Reflection only — so "which collection" is a uniform reflectable question, not per-type code.</summary>
    static (IList? coll, int matching, List<string> names) FindChildCollections(object parent, Type childConcrete, string? preferName = null)
    {
        var hits = new List<(string name, IList list)>();
        foreach (var p in parent.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var elem = ElementType(p.PropertyType);
            if (elem is null) continue;
            if (!elem.IsAssignableFrom(childConcrete)) continue;        // the child fits this list's element type
            if (p.GetValue(parent) is IList list) hits.Add((p.Name, list));
        }
        var names = hits.Select(h => h.name).ToList();
        if (hits.Count == 0) return (null, 0, names);
        var chosen = preferName is not null ? hits.FirstOrDefault(h => h.name == preferName).list ?? hits[0].list : hits[0].list;
        return (chosen, hits.Count, names);
    }

    /// <summary>Read back the child collection value on a re-opened (getter) parent, to confirm the child landed.</summary>
    static IEnumerable? ChildCollectionValue(object parent, Type childConcrete)
    {
        var childGetter = childConcrete.GetInterface("I" + childConcrete.Name + "Getter");
        foreach (var p in parent.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var elem = ElementType(p.PropertyType);
            if (elem is null) continue;
            if (childGetter is not null && elem.IsAssignableFrom(childGetter) && p.GetValue(parent) is IEnumerable e) return e;
        }
        return null;
    }

    /// <summary>The element type of an IList&lt;T&gt;/ExtendedList&lt;T&gt;-shaped property (else null for scalars).</summary>
    static Type? ElementType(Type t)
    {
        if (t == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(t)) return null;
        if (t.IsGenericType)
        {
            var ga = t.GetGenericArguments();
            if (ga.Length == 1) return ga[0];
        }
        var ifaceList = t.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IList<>));
        return ifaceList?.GetGenericArguments()[0];
    }

    /// <summary>Construct a concrete Mutagen record via its (FormKey, SkyrimRelease) ctor — the same shape the engine
    /// would build a net-new nested child with. Discovered reflectively (Q3-style: don't assume the ctor shape).</summary>
    static IMajorRecord ConstructRecord(Type concrete, FormKey fk)
    {
        var ctor = concrete.GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Length == 2 && c.GetParameters()[0].ParameterType == typeof(FormKey)
                && c.GetParameters()[1].ParameterType.IsEnum);
        if (ctor is null)
            throw new InvalidOperationException($"no (FormKey, <release-enum>) constructor on {concrete.Name} — surface, do not guess.");
        var releaseType = ctor.GetParameters()[1].ParameterType;
        var release = Enum.Parse(releaseType, "SkyrimSE");
        return (IMajorRecord)ctor.Invoke(new object[] { fk, release });
    }

    /// <summary>Reflectively call <c>IMod.GetNextFormKey()</c> / <c>GetNextFormKey(string)</c> — measure, don't assume.</summary>
    static FormKey? TryGetNextFormKey(object mod, string? editorId)
    {
        var m = mod.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(x => x.Name == "GetNextFormKey")
            .OrderBy(x => x.GetParameters().Length)
            .FirstOrDefault(x => editorId is null ? x.GetParameters().Length == 0
                : x.GetParameters().Length == 1 && x.GetParameters()[0].ParameterType == typeof(string));
        if (m is null) return null;
        return (FormKey)m.Invoke(mod, editorId is null ? null : new object[] { editorId })!;
    }

    // -- diagnostic helpers (local copies — the probe touches no shared file) --

    static string Show(FormKey? fk) => fk is { } f ? $"{f} (id=0x{f.ID:X6})" : "(null)";

    static string DescribeParent(object? parent)
    {
        if (parent is null) return "(none)";
        var rec = parent.GetType().GetProperty("Record")?.GetValue(parent) as IMajorRecordGetter;
        var inner = DescribeParent(parent.GetType().GetProperty("Parent")?.GetValue(parent));
        var here = rec is null ? parent.GetType().Name : $"{RecordNaming.StripOverlay(rec.GetType().Name)}:{rec.FormKey.ID:X6}";
        return inner == "(none)" ? here : $"{here}<-{inner}";
    }

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
            var name = t.Name; var tick = name.IndexOf('`');
            if (tick > 0) name = name[..tick];
            return $"{name}<{string.Join(", ", t.GetGenericArguments().Select(Pretty))}>";
        }
        return t.Name;
    }
}

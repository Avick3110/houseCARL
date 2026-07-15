using System.Reflection;
using HousecarlCore;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlGenerator;

// ======================================================================
//  SkyPatcherFieldMapProbe — CI guard for the Wave-1 op→field map
//  (SkyPatcherFieldMap; plan dev/plans/SKYPATCHER_DISTRIBUTOR_TOOL_PLAN_
//  2026-07-08.md). The TRIANGLE check: catalog ⇄ field map ⇄ the real
//  Mutagen types.
//
//  • COMPLETENESS — every catalog record type (OMOD excepted) has a field
//    map, and every CLEAN/COLLECTION op has exactly one entry (mapped, or
//    explicitly unmapped WITH a reason). HARD ops must have NO entry (a
//    mapped HARD op would claim fidelity the layer doesn't have).
//  • REALITY — every mapped path walks the actual Mutagen mutable type
//    via the write engine's own ResolveProperty (so a typo'd path can't
//    survive CI); every valueMap target / flag token parses into the real
//    leaf enum; element types instantiate and their sub-paths walk.
//  • AGREEMENT — catalog shape mult/add_numeric ⇔ map semantic mult/
//    addNumeric (the stateful ops can't silently become literal sets).
//  • SELF-TEST (RED-proof) — a deliberately-broken fixture map must
//    produce all four expected complaint classes.
// ======================================================================
public static class SkyPatcherFieldMapProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("[skypatcher-fieldmap-guard] SkyPatcher op → Mutagen field map (Wave 1)");
        int failures = 0;

        var catalog = SkyPatcherCatalog.Load();
        SkyPatcherFieldMap map;
        try { map = SkyPatcherFieldMap.Load(); }
        catch (Exception ex) { Console.WriteLine($"FAIL  field map failed to load: {ex.GetType().Name}: {ex.Message}"); return 1; }

        var problems = Validate(catalog, map);
        foreach (var p in problems) Console.WriteLine($"  FAIL  {p}");
        failures += problems.Count;
        if (problems.Count == 0)
            Console.WriteLine($"  PASS  triangle holds — {map.Records.Count} record map(s), " +
                $"{map.Records.Sum(r => r.Ops.Count)} op entries ({map.Records.Sum(r => r.Ops.Values.Count(o => o.IsUnmapped))} explicitly unmapped)");

        // ---- Wave 2: the FILTER triangle — catalog filters ⇄ filter specs ⇄ the real Mutagen types ----
        var filterProblems = ValidateFilters(catalog, map);
        foreach (var p in filterProblems) Console.WriteLine($"  FAIL  {p}");
        failures += filterProblems.Count;
        if (filterProblems.Count == 0)
            Console.WriteLine($"  PASS  filter triangle holds — {map.Records.Sum(r => r.Filters.Count)} filter specs " +
                $"({map.Records.Sum(r => r.Filters.Values.Count(f => f.IsUnmapped))} explicitly unmapped) + " +
                $"{SkyPatcherOverlay.BuiltInFilterBases.Count} built-in families");
        foreach (var r in map.Records)
            foreach (var (f, spec) in r.Filters.Where(kv => kv.Value.IsUnmapped))
                Console.WriteLine($"  note  {r.Subfolder}.{f} filter unmapped: {spec.Unmapped}");

        // Visibility: the explicitly-unmapped list (each is a reviewed judgment, listed so drift is seen).
        foreach (var r in map.Records)
            foreach (var (op, m) in r.Ops.Where(kv => kv.Value.IsUnmapped))
                Console.WriteLine($"  note  {r.Subfolder}.{op} unmapped: {m.Unmapped}");

        // ---- self-test: the checker must CATCH a broken map (RED-proof of the guard itself) ----
        const string broken = """
        [
         { "subfolder": "weapon", "recordType": "Weapon", "ops": {
            "attackDamage":  { "semantic": "set", "path": "BasicStats.NoSuchField" },
            "weaponHitType": { "semantic": "set", "path": "Data.OnHit", "valueMap": { "no": "NotARealMember" } },
            "mirrorWeapon":  { "semantic": "set", "path": "Template" },
            "attackDamageMult": { "semantic": "set", "path": "BasicStats.Damage" }
         } }
        ]
        """;
        var bad = SkyPatcherFieldMap.LoadFrom(broken);
        var caught = Validate(catalog, bad, completeness: false);
        failures += Check("self-test: bad path caught", caught.Any(p => p.Contains("NoSuchField")));
        failures += Check("self-test: bad valueMap target caught", caught.Any(p => p.Contains("NotARealMember")));
        failures += Check("self-test: mapped HARD op caught", caught.Any(p => p.Contains("mirrorWeapon") && p.Contains("HARD")));
        failures += Check("self-test: stateful-shape disagreement caught", caught.Any(p => p.Contains("attackDamageMult") && p.Contains("semantic")));

        // ---- self-test: the FILTER checker must CATCH a broken filter map ----
        const string brokenFilters = """
        [
         { "subfolder": "npc", "recordType": "Npc",
           "filters": {
             "filterByRaces":  { "eval": "formEquals", "path": "NoSuchLink", "formType": "Race" },
             "filterByAutoCalc": { "eval": "flagBool", "path": "Configuration.Flags", "flag": "NotARealFlag" },
             "notACatalogFilter": { "eval": "gender", "path": "Configuration.Flags" }
           },
           "ops": {} }
        ]
        """;
        var badF = SkyPatcherFieldMap.LoadFrom(brokenFilters);
        var caughtF = ValidateFilters(catalog, badF, completeness: false);
        failures += Check("filter self-test: bad path caught", caughtF.Any(p => p.Contains("NoSuchLink")));
        failures += Check("filter self-test: bad flag member caught", caughtF.Any(p => p.Contains("NotARealFlag")));
        failures += Check("filter self-test: non-catalog filter caught", caughtF.Any(p => p.Contains("notACatalogFilter")));

        Console.WriteLine(failures == 0
            ? "[skypatcher-fieldmap-guard] PASS — catalog ⇄ field map ⇄ Mutagen agree."
            : $"[skypatcher-fieldmap-guard] FAIL — {failures} problem(s).");
        return failures == 0 ? 0 : 1;
    }

    static int Check(string label, bool ok)
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}");
        return ok ? 0 : 1;
    }

    /// <summary>The whole triangle as a problem list (shared with the self-test arm).
    /// <paramref name="completeness"/> off ⇒ per-entry reality checks only (the fixture is partial).</summary>
    internal static List<string> Validate(SkyPatcherCatalog catalog, SkyPatcherFieldMap map, bool completeness = true)
    {
        var problems = new List<string>();
        var asm = typeof(SkyrimMod).Assembly;

        if (completeness)
        {
            foreach (var rec in catalog.Records)
            {
                if (rec.Sig.Equals("OMOD", StringComparison.OrdinalIgnoreCase)) continue;   // the documented gap
                var maps = map.ForSubfolder(rec.Subfolder);
                if (maps.Count == 0) { problems.Add($"catalog record '{rec.RecordType}' (subfolder '{rec.Subfolder}') has NO field map."); continue; }

                foreach (var op in rec.Operations)
                {
                    bool mapped = maps.Any(m => m.Ops.ContainsKey(op.Name));
                    if (op.Tractability == SkyPatcherTractability.Hard)
                    {
                        if (mapped && maps.Any(m => m.Ops.TryGetValue(op.Name, out var om) && !om.IsUnmapped))
                            problems.Add($"{rec.Subfolder}.{op.Name} is HARD but carries a field mapping — HARD ops render as directives, never resolved values.");
                    }
                    else if (!mapped)
                        problems.Add($"{rec.Subfolder}.{op.Name} ({op.Tractability}) has no field-map entry (map it, or declare it unmapped with a reason).");
                }
            }
        }

        foreach (var r in map.Records)
        {
            var rec = catalog.ForSubfolder(r.Subfolder);
            if (rec is null) { problems.Add($"field map subfolder '{r.Subfolder}' is not in the catalog."); continue; }
            var rootType = asm.GetType("Mutagen.Bethesda.Skyrim." + r.RecordType);
            if (rootType is null) { problems.Add($"field map '{r.Subfolder}': recordType '{r.RecordType}' is not a Mutagen.Bethesda.Skyrim type."); continue; }

            foreach (var (opName, m) in r.Ops)
            {
                var ctx = $"{r.Subfolder}.{opName}";
                var opDef = rec.Operations.FirstOrDefault(o => o.Name == opName);
                if (opDef is null) { problems.Add($"{ctx}: not an operation in the catalog for this record type."); continue; }
                if (m.IsUnmapped)
                {
                    if (string.IsNullOrWhiteSpace(m.Unmapped)) problems.Add($"{ctx}: unmapped without a reason.");
                    continue;
                }
                if (opDef.Tractability == SkyPatcherTractability.Hard)
                { problems.Add($"{ctx}: op is HARD in the catalog but mapped here."); continue; }

                // shape ⇄ semantic agreement on the stateful ops (both directions).
                bool shapeStateful = opDef.Shape is SkyPatcherOpShape.Mult or SkyPatcherOpShape.AddNumeric;
                bool semStateful = m.Semantic is SkyPatcherOpSemantic.Mult or SkyPatcherOpSemantic.AddNumeric or SkyPatcherOpSemantic.DictMult;
                if (shapeStateful != semStateful)
                    problems.Add($"{ctx}: catalog shape '{opDef.Shape}' vs map semantic '{m.Semantic}' disagree on statefulness.");

                // formType must be resolvable as a form SCOPE: a concrete Mutagen record class (the
                // catalog-name path) OR a link-interface group (I{name}Getter — "Item", "Constructible",
                // "NpcSpawn"…). Review finding #2: unvalidated formTypes made the whole inventory op
                // family's EditorID values unresolvable at runtime.
                if (m.FormType is { } ft
                    && asm.GetType("Mutagen.Bethesda.Skyrim." + ft) is null
                    && asm.GetType("Mutagen.Bethesda.Skyrim.I" + ft + "Getter") is null)
                    problems.Add($"{ctx}: formType '{ft}' is neither a Mutagen.Bethesda.Skyrim record class nor a link interface (I{ft}Getter).");

                // donorType (optional — the runtime 'copy from a donor of type X' reading, e.g. NPC skin's
                // donorType=Npc) resolves as a form SCOPE the same way formType does (issue #181).
                if (m.DonorType is { } dtv
                    && asm.GetType("Mutagen.Bethesda.Skyrim." + dtv) is null
                    && asm.GetType("Mutagen.Bethesda.Skyrim.I" + dtv + "Getter") is null)
                    problems.Add($"{ctx}: donorType '{dtv}' is neither a Mutagen.Bethesda.Skyrim record class nor a link interface (I{dtv}Getter).");

                var leaf = WalkPath(rootType, m.Path, ctx, problems);
                if (leaf is null) continue;

                switch (m.Semantic)
                {
                    case SkyPatcherOpSemantic.Mult:
                    case SkyPatcherOpSemantic.AddNumeric:
                        if (!IsNumeric(leaf.PropertyType))
                            problems.Add($"{ctx}: stateful numeric op targets non-numeric '{m.Path}' ({leaf.PropertyType.Name}).");
                        break;
                    case SkyPatcherOpSemantic.VecComponent:
                        if (m.Component is not (>= 0 and <= 2)) problems.Add($"{ctx}: vecComponent needs component 0..2.");
                        var vt = StripNullable(leaf.PropertyType);
                        if (vt.GetProperty("X") is null || vt.GetProperty("Z") is null || !vt.GetConstructors().Any(c => c.GetParameters().Length == 3))
                            problems.Add($"{ctx}: '{m.Path}' ({vt.Name}) is not an X/Y/Z vector with a 3-arg ctor.");
                        break;
                    case SkyPatcherOpSemantic.SetFromOwnField:
                        if (m.SourcePath is null) problems.Add($"{ctx}: setFromOwnField needs sourcePath.");
                        else WalkPath(rootType, m.SourcePath, ctx + " (sourcePath)", problems);
                        break;
                    case SkyPatcherOpSemantic.FlagsSet:
                    case SkyPatcherOpSemantic.FlagsRemove:
                    case SkyPatcherOpSemantic.FlagBool:
                    {
                        var et = StripNullable(leaf.PropertyType);
                        if (!et.IsEnum) { problems.Add($"{ctx}: flags op targets non-enum '{m.Path}' ({et.Name})."); break; }
                        if (m.Semantic == SkyPatcherOpSemantic.FlagBool && (m.Flag is null || !EnumHas(et, m.Flag)))
                            problems.Add($"{ctx}: flagBool flag '{m.Flag ?? "<null>"}' is not a member of {et.Name}.");
                        CheckValueMap(m, et, ctx, problems);
                        break;
                    }
                    case SkyPatcherOpSemantic.AddForm:
                    case SkyPatcherOpSemantic.RemoveForm:
                    case SkyPatcherOpSemantic.ReplaceForm:
                        if (!IsFormLinkList(leaf.PropertyType))
                            problems.Add($"{ctx}: '{m.Path}' ({leaf.PropertyType.Name}) is not a formlink list.");
                        break;
                    case SkyPatcherOpSemantic.ClearList:
                        if (!IsList(leaf.PropertyType))
                            problems.Add($"{ctx}: clearList targets non-list '{m.Path}'.");
                        break;
                    case SkyPatcherOpSemantic.DictSet:
                    case SkyPatcherOpSemantic.DictMult:
                    {
                        var dt = StripNullable(leaf.PropertyType);
                        var dictIface = new[] { dt }.Concat(dt.GetInterfaces())
                            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));
                        if (dictIface is null) { problems.Add($"{ctx}: dict op targets non-dict '{m.Path}' ({dt.Name})."); break; }
                        var kt = dictIface.GetGenericArguments()[0];
                        if (m.Key is null) problems.Add($"{ctx}: dict op needs a 'key'.");
                        else if (kt.IsEnum && !EnumHas(kt, m.Key))
                            problems.Add($"{ctx}: dict key '{m.Key}' is not a member of {kt.Name}.");
                        if (!IsNumeric(dictIface.GetGenericArguments()[1]))
                            problems.Add($"{ctx}: dict op needs a numeric value type, got {dictIface.GetGenericArguments()[1].Name}.");
                        break;
                    }
                    case SkyPatcherOpSemantic.ColorChannel:
                        if (m.Component is not (>= 0 and <= 2)) problems.Add($"{ctx}: colorChannel needs component 0..2 (R/G/B).");
                        if (StripNullable(leaf.PropertyType) != typeof(System.Drawing.Color))
                            problems.Add($"{ctx}: colorChannel targets non-Color '{m.Path}' ({leaf.PropertyType.Name}).");
                        break;
                    case SkyPatcherOpSemantic.BipedSlotsSet:
                    case SkyPatcherOpSemantic.BipedSlotsRemove:
                        if (!StripNullable(leaf.PropertyType).IsEnum)
                            problems.Add($"{ctx}: bipedSlots op targets non-enum '{m.Path}'.");
                        break;
                    case SkyPatcherOpSemantic.TeachSpell:
                    case SkyPatcherOpSemantic.TeachSkill:
                    {
                        // The leaf is the polymorphic Teaches base; the compose-Set arm + its sub-field
                        // must exist and the arm must assign to the leaf.
                        var arm = asm.GetType("Mutagen.Bethesda.Skyrim." + (m.Semantic == SkyPatcherOpSemantic.TeachSpell ? "BookSpell" : "BookSkill"));
                        if (arm is null) { problems.Add($"{ctx}: the Teaches arm type is missing from Mutagen."); break; }
                        if (!leaf.PropertyType.IsAssignableFrom(arm))
                            problems.Add($"{ctx}: {arm.Name} is not assignable to '{m.Path}' ({leaf.PropertyType.Name}).");
                        WalkPath(arm, m.Semantic == SkyPatcherOpSemantic.TeachSpell ? "Spell" : "Skill", $"{ctx} (arm field)", problems);
                        if (m.Semantic == SkyPatcherOpSemantic.TeachSkill && m.ValueMap is not null
                            && asm.GetType("Mutagen.Bethesda.Skyrim.Skill") is { } skillEnum)
                            foreach (var (tok, member) in m.ValueMap)
                                if (!EnumHas(skillEnum, member))
                                    problems.Add($"{ctx}: valueMap '{tok}' → '{member}' is not a member of Skill.");
                        break;
                    }
                    case SkyPatcherOpSemantic.AddEntry:
                    case SkyPatcherOpSemantic.AddEntryOnce:
                    case SkyPatcherOpSemantic.RemoveEntry:
                    case SkyPatcherOpSemantic.RemoveEntryByCount:
                    case SkyPatcherOpSemantic.ReplaceEntry:
                    case SkyPatcherOpSemantic.MultCount:
                    case SkyPatcherOpSemantic.RemoveByKeyword:
                    case SkyPatcherOpSemantic.SetEntryCount:
                    {
                        if (!IsList(leaf.PropertyType)) { problems.Add($"{ctx}: entry op targets non-list '{m.Path}'."); break; }
                        if (m.Element is null) { problems.Add($"{ctx}: entry op needs an element spec."); break; }
                        var elType = asm.GetType("Mutagen.Bethesda.Skyrim." + m.Element.Type);
                        if (elType is null) { problems.Add($"{ctx}: element type '{m.Element.Type}' is not a Mutagen.Bethesda.Skyrim type."); break; }
                        foreach (var f in m.Element.Fields) WalkPath(elType, f.Path, $"{ctx} (element {f.Path})", problems);
                        if (m.Element.KeyPath is { } kp) WalkPath(elType, kp, $"{ctx} (keyPath)", problems);
                        else if (m.Semantic is not SkyPatcherOpSemantic.MultCount)
                            problems.Add($"{ctx}: entry op needs element.keyPath (the form sub-field it matches on).");
                        if (m.Element.CountPath is { } cp) WalkPath(elType, cp, $"{ctx} (countPath)", problems);
                        else if (m.Semantic is SkyPatcherOpSemantic.RemoveEntryByCount or SkyPatcherOpSemantic.MultCount or SkyPatcherOpSemantic.SetEntryCount)
                            problems.Add($"{ctx}: {m.Semantic} needs element.countPath.");
                        break;
                    }
                    default:   // Set / ModelPath — leaf existence (walked above) + optional valueMap on enums.
                    {
                        var et = StripNullable(leaf.PropertyType);
                        if (m.ValueMap is not null && et.IsEnum) CheckValueMap(m, et, ctx, problems);
                        else if (m.ValueMap is not null && !et.IsEnum)
                            problems.Add($"{ctx}: valueMap on a non-enum leaf '{m.Path}' ({et.Name}).");
                        break;
                    }
                }
            }
        }
        return problems;
    }

    /// <summary>The Wave-2 filter triangle: every catalog filter that is not a primary/gate/noFilter
    /// token is either a BUILT-IN family (<see cref="SkyPatcherOverlay.BuiltInFilterBases"/>) or has a
    /// per-record <see cref="FilterSpec"/> (mapped, or explicitly unmapped WITH a reason) — and every
    /// spec's paths/flags/valueMaps walk the real Mutagen types. A filter that is neither would skip
    /// lines loud at runtime forever without CI ever noticing the coverage gap.</summary>
    internal static List<string> ValidateFilters(SkyPatcherCatalog catalog, SkyPatcherFieldMap map, bool completeness = true)
    {
        var problems = new List<string>();
        var asm = typeof(SkyrimMod).Assembly;

        if (completeness)
        {
            foreach (var rec in catalog.Records)
            {
                if (rec.Sig.Equals("OMOD", StringComparison.OrdinalIgnoreCase)) continue;
                var maps = map.ForSubfolder(rec.Subfolder);
                if (maps.Count == 0) continue;   // already a Validate() failure
                foreach (var f in rec.Filters)
                {
                    if (f.Kind is SkyPatcherFilterKind.Primary or SkyPatcherFilterKind.HasPlugins or SkyPatcherFilterKind.NoFilter) continue;
                    if (SkyPatcherOverlay.BuiltInFilterBases.Contains(f.Name))
                    {
                        // The two built-ins that hardcode Mutagen PATHS in the overlay (attached-mgef,
                        // alternate-texture) are otherwise exempt from the path walk — a Mutagen rename
                        // would silently turn them into "attached to nothing" (review finding). Walk
                        // their code-homed paths here for every type that documents the filter.
                        foreach (var m in maps.Where(m2 => !m2.Filters.ContainsKey(f.Name)))   // a map override (cobj filterByKeywords) validates as a spec instead
                        {
                            var rt = asm.GetType("Mutagen.Bethesda.Skyrim." + m.RecordType);
                            if (rt is null) continue;
                            if (f.Name == "filterByMgefs" && f.Kind != SkyPatcherFilterKind.Primary)
                            {
                                var leaf = WalkPath(rt, "Effects", $"{rec.Subfolder}.builtin.filterByMgefs", problems);
                                if (leaf is not null && ListElement(StripNullable(leaf.PropertyType)) is { } el)
                                    WalkPath(el, "BaseEffect", $"{rec.Subfolder}.builtin.filterByMgefs (keyPath)", problems);
                            }
                            else if (f.Name == "filterByAlternateTextures")
                            {
                                var leaf = WalkPath(rt, "Model.AlternateTextures", $"{rec.Subfolder}.builtin.filterByAlternateTextures", problems);
                                if (leaf is not null && ListElement(StripNullable(leaf.PropertyType)) is { } el)
                                    WalkPath(el, "NewTexture", $"{rec.Subfolder}.builtin.filterByAlternateTextures (keyPath)", problems);
                            }
                        }
                        continue;
                    }
                    if (!maps.Any(m => m.Filters.ContainsKey(f.Name)))
                        problems.Add($"{rec.Subfolder}.{f.Name} ({f.Kind}) is neither a built-in filter family nor in the filter map (map it, or declare it unmapped with a reason).");
                }
            }
        }

        foreach (var r in map.Records)
        {
            var rec = catalog.ForSubfolder(r.Subfolder);
            var rootType = asm.GetType("Mutagen.Bethesda.Skyrim." + r.RecordType);
            if (rec is null || rootType is null) continue;   // already Validate() failures

            foreach (var (name, spec) in r.Filters)
            {
                var ctx = $"{r.Subfolder}.filters.{name}";
                if (!rec.Filters.Any(f => f.Name == name))
                { problems.Add($"{ctx}: not a filter in the catalog for this record type."); continue; }
                if (spec.IsUnmapped)
                {
                    if (string.IsNullOrWhiteSpace(spec.Unmapped)) problems.Add($"{ctx}: unmapped without a reason.");
                    continue;
                }

                if (spec.FormType is { } ft
                    && asm.GetType("Mutagen.Bethesda.Skyrim." + ft) is null
                    && asm.GetType("Mutagen.Bethesda.Skyrim.I" + ft + "Getter") is null)
                    problems.Add($"{ctx}: formType '{ft}' is neither a Mutagen.Bethesda.Skyrim record class nor a link interface (I{ft}Getter).");

                // Donor kinds read their path off ANOTHER record: the linkPath walks the record, the
                // path walks the LINKED type (recovered from the formlink's getter argument).
                if (spec.Eval is SkyPatcherFilterEval.DonorSubstring or SkyPatcherFilterEval.DonorKeywords)
                {
                    if (spec.LinkPath is null) { problems.Add($"{ctx}: donor eval needs linkPath."); continue; }
                    var link = WalkPath(rootType, spec.LinkPath, $"{ctx} (linkPath)", problems);
                    if (link is null) continue;
                    if (spec.Eval == SkyPatcherFilterEval.DonorSubstring)
                    {
                        var donorType = FormLinkTargetType(asm, link.PropertyType);
                        if (donorType is null) { problems.Add($"{ctx}: linkPath '{spec.LinkPath}' is not a formlink (can't resolve the donor type)."); continue; }
                        WalkPath(donorType, spec.Paths[0], $"{ctx} (donor path)", problems);
                    }
                    continue;
                }

                foreach (var path in spec.Paths)
                {
                    var leaf = WalkPath(rootType, path, ctx, problems);
                    if (leaf is null) continue;
                    var et = StripNullable(leaf.PropertyType);
                    switch (spec.Eval)
                    {
                        case SkyPatcherFilterEval.EnumEquals:
                            if (!et.IsEnum) problems.Add($"{ctx}: enumEquals targets non-enum '{path}' ({et.Name}).");
                            else if (spec.ValueMap is not null)
                                foreach (var (tok, member) in spec.ValueMap)
                                    if (!EnumHas(et, member)) problems.Add($"{ctx}: valueMap '{tok}' → '{member}' is not a member of {et.Name}.");
                            break;
                        case SkyPatcherFilterEval.FlagBool:
                        case SkyPatcherFilterEval.FlagAnyOf:
                        case SkyPatcherFilterEval.BipedSlots:
                        case SkyPatcherFilterEval.Gender:
                            if (!et.IsEnum) { problems.Add($"{ctx}: flag eval targets non-enum '{path}' ({et.Name})."); break; }
                            if (spec.Eval == SkyPatcherFilterEval.FlagBool && (spec.Flag is null || !EnumHas(et, spec.Flag)))
                                problems.Add($"{ctx}: flagBool flag '{spec.Flag ?? "<null>"}' is not a member of {et.Name}.");
                            if (spec.Eval == SkyPatcherFilterEval.Gender && !EnumHas(et, "Female"))
                                problems.Add($"{ctx}: gender eval needs a Female member on {et.Name}.");
                            if (spec.ValueMap is not null)
                                foreach (var (tok, member) in spec.ValueMap)
                                    if (!EnumHas(et, member)) problems.Add($"{ctx}: valueMap '{tok}' → '{member}' is not a member of {et.Name}.");
                            break;
                        case SkyPatcherFilterEval.NumericLess:
                            if (!IsNumeric(et)) problems.Add($"{ctx}: numericLess targets non-numeric '{path}' ({et.Name}).");
                            break;
                        case SkyPatcherFilterEval.FormEquals:
                        case SkyPatcherFilterEval.LinkedOriginPlugin:
                            if (!et.Name.Contains("FormLink", StringComparison.Ordinal))
                                problems.Add($"{ctx}: {spec.Eval} targets non-formlink '{path}' ({et.Name}).");
                            break;
                        case SkyPatcherFilterEval.FormInList:
                        {
                            if (!IsList(et)) { problems.Add($"{ctx}: formInList targets non-list '{path}'."); break; }
                            var el = ListElement(et)!;
                            if (spec.KeyPath is { } kp) WalkPath(el, kp, $"{ctx} (keyPath)", problems);
                            else if (!el.Name.Contains("FormLink", StringComparison.Ordinal))
                                problems.Add($"{ctx}: formInList without keyPath needs a formlink list, got List<{el.Name}>.");
                            break;
                        }
                    }
                }
            }
        }
        return problems;
    }

    /// <summary>The MUTABLE Mutagen class a FormLink&lt;IXGetter&gt; leaf points at (null when the
    /// property isn't a formlink or the target class doesn't resolve).</summary>
    static Type? FormLinkTargetType(Assembly asm, Type linkProp)
    {
        var t = StripNullable(linkProp);
        if (!t.IsGenericType || !t.Name.Contains("FormLink", StringComparison.Ordinal)) return null;
        var getter = t.GetGenericArguments()[0].Name;                       // "IRaceGetter"
        if (!getter.StartsWith('I') || !getter.EndsWith("Getter", StringComparison.Ordinal)) return null;
        return asm.GetType("Mutagen.Bethesda.Skyrim." + getter[1..^"Getter".Length]);
    }

    static void CheckValueMap(OpMap m, Type enumType, string ctx, List<string> problems)
    {
        if (m.ValueMap is null) return;
        foreach (var (token, member) in m.ValueMap)
            if (!EnumHas(enumType, member))
                problems.Add($"{ctx}: valueMap '{token}' → '{member}' is not a member of {enumType.Name}.");
    }

    static bool EnumHas(Type enumType, string member)
        => Enum.GetNames(enumType).Any(n => n.Equals(member, StringComparison.OrdinalIgnoreCase));

    /// <summary>Walk a dotted path down the MUTABLE Mutagen types with the write engine's own property
    /// resolution — the same hop the overlay executes — returning the leaf PropertyInfo (null + a named
    /// problem when any hop is missing).</summary>
    static PropertyInfo? WalkPath(Type root, string path, string ctx, List<string> problems)
    {
        var segs = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segs.Length == 0) { problems.Add($"{ctx}: empty path."); return null; }
        Type current = root;
        PropertyInfo? prop = null;
        foreach (var seg in segs)
        {
            var (name, key) = WriteEngine.ParseSegment(seg);
            prop = WriteEngine.ResolveProperty(current, name);
            if (prop is null) { problems.Add($"{ctx}: '{path}' — no property '{name}' on {current.Name}."); return null; }
            var t = StripNullable(prop.PropertyType);
            if (key is not null) t = ListElement(t) ?? t;
            current = t;
        }
        return prop;
    }

    static Type StripNullable(Type t) => Nullable.GetUnderlyingType(t) ?? t;

    static bool IsNumeric(Type t)
    {
        t = StripNullable(t);
        return t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort)
            || t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong)
            || t == typeof(float) || t == typeof(double);
    }

    static bool IsList(Type t) => ClosedList(t) is not null;

    static Type? ListElement(Type t) => ClosedList(t)?.GetGenericArguments()[0];

    static Type? ClosedList(Type t)
        => new[] { t }.Concat(t.GetInterfaces())
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IList<>));

    static bool IsFormLinkList(Type t)
        => ListElement(StripNullable(t)) is { } el && el.Name.Contains("FormLink", StringComparison.Ordinal);
}

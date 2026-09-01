using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// Hand-run diagnostics and the original acceptance proof for the reflection write engine. None of these is a CI
/// probe and nothing on the tool surface reaches them — they are driven from the command line when a question
/// about the engine or about Mutagen needs answering.
///
/// <list type="bullet">
/// <item><c>npc-skills</c> — the step-4 acceptance proof: a nested dict-in-substruct Set
/// (Npc.PlayerSkills.SkillValues[OneHanded]) through pre-flight and the engine, read back off the written patch,
/// source SHA unchanged. <see cref="WriteOracle"/> covers the same cell against a hand-written setter.</item>
/// <item><c>write-api</c> — what Mutagen exposes for override/write, printed. Run it after a Mutagen bump.</item>
/// <item><c>poly-probe</c> — can a polymorphic arm be instantiated and assigned.</item>
/// <item><c>substruct-probe</c> — every navigate-into substruct target, and which of them a materializing engine
/// cannot build with a parameterless ctor. Corpus + reflection; needs no plugin.</item>
/// </list>
///
/// They lived in <see cref="WriteEngine"/> until #453 moved them here, beside the probes they resemble. The
/// engine's own header lists what stayed behind.
/// </summary>
public static class WriteDiagnosticsProbe
{
    // A convenience default for the two modes that read a plugin; both take a path as argv[0] instead. It names
    // one machine's install and is not assumed to exist — each of those two refuses by name when it does not.
    const string DefaultSourcePath =
        @"C:\Program Files (x86)\Steam\steamapps\common\Skyrim Special Edition\Data\Skyrim.esm";

    // ======================================================================
    //  THE ACCEPTANCE PROOF — NPC skills by name (plan §3 P-ADDR / §5.1)
    //  Path (verified against corpus): Npc → PlayerSkills → SkillValues → [OneHanded] = 50.
    //  This is the dict-set-inside-a-substruct kind — the single thing most likely to surface
    //  a real navigation problem, so we build it first.
    // ======================================================================
    public static int RunNpcSkillsProof(string[] args)
    {
        var sourcePath = args.Length > 0 ? args[0] : DefaultSourcePath;
        if (!File.Exists(sourcePath))
        {
            Console.Error.WriteLine($"error: source plugin not found: {sourcePath}");
            return 1;
        }

        var outDir = Path.GetFullPath(Path.Combine("write-output", "npc-skills"));
        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, "HousecarlWriteProof.esp");

        Console.WriteLine($"Source plugin: {sourcePath}");
        Console.WriteLine($"Output patch:  {outPath}");
        Console.WriteLine();

        var shaBefore = Sha(sourcePath);

        // --- find a target NPC (typed scan — harness scaffolding; the engine below is generic) ---
        var sourceMod = SkyrimMod.CreateFromBinaryOverlay(sourcePath, SkyrimRelease.SkyrimSE);
        INpcGetter? target = null;
        foreach (var n in sourceMod.Npcs)
        {
            if (n.PlayerSkills?.SkillValues is { } sv && sv.ContainsKey(Skill.OneHanded))
            {
                target = n;
                break;
            }
        }
        if (target is null)
        {
            Console.Error.WriteLine("error: no NPC with PlayerSkills.SkillValues[OneHanded] found in source");
            return 1;
        }
        var beforeVal = target.PlayerSkills!.SkillValues[Skill.OneHanded];
        Console.WriteLine($"Target NPC:    {target.FormKey} ({target.EditorID})");
        Console.WriteLine($"  OneHanded before: {beforeVal}");
        Console.WriteLine();

        // --- PRE-FLIGHT VALIDATION (plan §3 P-VALIDATE) — the write goes through the rulebook first ---
        const byte newVal = 50;
        var rulebook = CorpusRulebook.Load();
        Console.WriteLine($"Rulebook loaded: {rulebook.TypeCount} types.");
        var req = new WriteRequest
        {
            RecordType = "Npc",
            Path = new[] { "PlayerSkills", "SkillValues" },
            Verb = "Set",
            Key = "OneHanded",
            Value = newVal.ToString(CultureInfo.InvariantCulture),
        };

        void Probe(string label, WriteRequest r, bool expectOk)
        {
            var msg = rulebook.Validate(r);
            var ok = msg is null;
            var mark = ok == expectOk ? "OK" : "!! UNEXPECTED";
            Console.WriteLine($"  [{mark}] {label} -> {(ok ? "ACCEPT" : "REJECT: " + msg)}");
        }

        Console.WriteLine("Pre-flight probes (2 accepts + 7 fail-loud rejects):");
        Probe("real write: Npc PlayerSkills.SkillValues[OneHanded]=50", req, expectOk: true);
        Probe("unknown record type", new() { RecordType = "Bogus", Path = new[] { "X" }, Verb = "Set", Value = "1" }, expectOk: false);
        Probe("unknown field", new() { RecordType = "Npc", Path = new[] { "Bogus" }, Verb = "Set", Value = "1" }, expectOk: false);
        Probe("illegal enum key", new() { RecordType = "Npc", Path = new[] { "PlayerSkills", "SkillValues" }, Verb = "Set", Key = "BogusSkill", Value = "50" }, expectOk: false);
        Probe("wrong verb for cardinality (SetAtIndex on dict)", new() { RecordType = "Npc", Path = new[] { "PlayerSkills", "SkillValues" }, Verb = "SetAtIndex", Key = "0", Value = "50" }, expectOk: false);
        Probe("value out of range (Byte=999)", new() { RecordType = "Npc", Path = new[] { "PlayerSkills", "SkillValues" }, Verb = "Set", Key = "OneHanded", Value = "999" }, expectOk: false);
        Probe("identity reject: Npc.FormKey", new() { RecordType = "Npc", Path = new[] { "FormKey" }, Verb = "Set", Value = "123456:Skyrim.esm" }, expectOk: false);
        Probe("substruct-whole reject: Npc.PlayerSkills (navigate-in)", new() { RecordType = "Npc", Path = new[] { "PlayerSkills" }, Verb = "Set", Value = "x" }, expectOk: false);
        Probe("TranslatedString accept: Npc.Name=Bob", new() { RecordType = "Npc", Path = new[] { "Name" }, Verb = "Set", Value = "Bob" }, expectOk: true);
        Console.WriteLine();

        // Q3: refuse to mutate if the real request does not pass pre-flight.
        if (rulebook.Validate(req) is { } reject)
        {
            Console.Error.WriteLine($"FAIL: real write rejected by pre-flight: {reject}");
            return 1;
        }

        // --- THE ENGINE PATH (fully generic — resolves group from the record's runtime type) ---
        var patchMod = new SkyrimMod(new ModKey("HousecarlWriteProof", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var patchRecord = WriteEngine.GenericGetOrAddAsOverride(patchMod, target);
        WriteEngine.ApplyVerb(patchRecord, req);

        WriteEngine.WritePatch(patchMod, sourceMod, outPath);
        Console.WriteLine($"Wrote patch ({new FileInfo(outPath).Length} bytes).");
        Console.WriteLine();

        // --- verify: re-open the patch, read the value back ---
        var patchBack = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE);
        if (!patchBack.Npcs.TryGetValue(target.FormKey, out var patchedNpc))
        {
            Console.Error.WriteLine("FAIL: patched NPC not found in written patch");
            return 1;
        }
        var afterVal = patchedNpc.PlayerSkills?.SkillValues?.GetValueOrDefault(Skill.OneHanded);
        Console.WriteLine($"  OneHanded after (read back from patch): {afterVal}");

        var shaAfter = Sha(sourcePath);
        var sourceUnchanged = shaBefore == shaAfter;
        Console.WriteLine($"  Source unchanged: {(sourceUnchanged ? "YES" : "NO")}");
        Console.WriteLine();

        var pass = afterVal == newVal && sourceUnchanged;
        Console.WriteLine(pass
            ? "=== PASS: nested dict-in-substruct Set landed; original untouched ==="
            : "=== FAIL ===");
        return pass ? 0 : 1;
    }

    // ======================================================================
    //  BUILD-START API DISCOVERY  (write-api) — kept as a Mutagen-bump guard
    // ======================================================================
    public static int RunDiscovery(string[] args)
    {
        Console.WriteLine("=== Mutagen assemblies loaded ===");
        var mutagen = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => (a.GetName().Name ?? "").StartsWith("Mutagen"))
            .OrderBy(a => a.GetName().Name)
            .ToList();
        foreach (var a in mutagen)
            Console.WriteLine($"  {a.GetName().Name} {a.GetName().Version}");
        Console.WriteLine();

        Console.WriteLine("=== GetOrAddAsOverride (static / extension) ===");
        foreach (var asm in mutagen)
            foreach (var t in SafeTypes(asm).Where(t => t is { IsAbstract: true, IsSealed: true }))
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                             .Where(m => m.Name == "GetOrAddAsOverride"))
                    Console.WriteLine($"  {Pretty(t)}.{Sig(m)}");
        Console.WriteLine();

        var armorsProp = typeof(SkyrimMod).GetProperty("Armors")!;
        var groupType = armorsProp.PropertyType;
        Console.WriteLine($"=== SkyrimMod.Armors : {Pretty(groupType)} ===");
        foreach (var m in groupType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                     .Where(m => m.Name is "GetOrAddAsOverride" or "Add" or "Set" or "Remove"))
            Console.WriteLine($"  (instance) {Sig(m)}");
        Console.WriteLine();

        Console.WriteLine($"=== SkyrimMod group-typed properties: {CountGroupProps()} total ===");
        Console.WriteLine("=== discovery complete ===");
        return 0;
    }

    /// <summary>Build-start confirm for polymorphic arm-SWAP: how is an arm instantiated, and does it assign?</summary>
    public static int RunPolyProbe(string[] args)
    {
        var asm = typeof(SkyrimMod).Assembly;
        var archProp = typeof(IMagicEffect).GetProperty("Archetype")!;
        Console.WriteLine($"IMagicEffect.Archetype : {Pretty(archProp.PropertyType)}  canWrite={archProp.CanWrite}");
        Console.WriteLine();

        foreach (var name in new[] { "MagicEffectLightArchetype", "MagicEffectArchetype", "MagicEffectSummonCreatureArchetype" })
        {
            var t = asm.GetType("Mutagen.Bethesda.Skyrim." + name);
            if (t is null) { Console.WriteLine($"{name}: TYPE NOT FOUND"); continue; }
            Console.WriteLine($"{name}:");
            foreach (var c in t.GetConstructors())
                Console.WriteLine($"    ctor({string.Join(", ", c.GetParameters().Select(p => $"{Pretty(p.ParameterType)} {p.Name}"))})");
            object? inst = null;
            try { inst = System.Activator.CreateInstance(t); Console.WriteLine("    Activator.CreateInstance() -> OK"); }
            catch (Exception ex) { Console.WriteLine($"    Activator.CreateInstance() -> {ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}"); }
            if (inst is not null)
            {
                Console.WriteLine($"    assignable to Archetype: {archProp.PropertyType.IsInstanceOfType(inst)}");
                var wf = inst.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0).Select(p => $"{p.Name}:{Pretty(p.PropertyType)}").Take(10);
                Console.WriteLine($"    writable fields: {string.Join(", ", wf)}");
            }
        }
        Console.WriteLine();

        var src = args.Length > 0 ? args[0] : DefaultSourcePath;
        if (!File.Exists(src))
        {
            Console.Error.WriteLine($"error: source plugin not found: {src}");
            return 1;
        }
        var mod = SkyrimMod.CreateFromBinaryOverlay(src, SkyrimRelease.SkyrimSE);
        foreach (var m in mod.MagicEffects)
            if (m.Archetype is not null) { Console.WriteLine($"sample MGEF {m.FormKey} archetype concrete: {m.Archetype.GetType().Name}"); break; }
        return 0;
    }

    /// <summary>
    /// Characterization probe (substruct-probe): enumerate every NAVIGATE-INTO substruct field — substruct
    /// cardinality, non-record TypeRef, not whole-coercible (TranslatedString-style) — and report, per distinct
    /// target type, how many field-sites are NULLABLE (so the substruct can be absent and would need materializing)
    /// and whether the concrete class has a parameterless ctor. The no-paramless-ctor targets are the "tricky
    /// shapes" the absent-collection handoff flagged: the ones absent-substruct materialization must handle beyond a
    /// plain Activator.CreateInstance. Pure corpus + reflection, no plugins — answers "do we need a plugin to prove
    /// this?" and sizes the engine-fix design BEFORE building it.
    /// </summary>
    public static int RunSubstructProbe(string[] args)
    {
        var corpusPath = args.Length > 0 ? args[0] : CorpusRulebook.CorpusPath;
        Corpus corpus;
        try { corpus = CorpusRulebook.LoadCorpus(corpusPath); }
        catch (Exception ex) { Console.Error.WriteLine($"error: {ex.Message}"); return 1; }
        var asm = typeof(SkyrimMod).Assembly;

        bool IsRecord(string n) => corpus.Types.TryGetValue(n, out var t) && t.Kind == "record";

        // distinct navigate-into substruct target -> (total sites, nullable sites, sample owner.field, representative AQ)
        var navInto = new SortedDictionary<string, (int sites, int nullableSites, string sample, string? aq)>(StringComparer.Ordinal);
        int wholeCoercible = 0, recordSubstructs = 0;
        foreach (var ts in corpus.Types.Values)
        foreach (var f in ts.Fields)
        {
            if (f.Cardinality != "substruct" || f.TypeRef is not { } tr) continue;
            if (IsRecord(tr)) { recordSubstructs++; continue; }        // owned child record — never MATERIALIZED (a present one is navigable)
            var saq = f.MutableTypeAssemblyQualified ?? f.GetterTypeAssemblyQualified;
            if (WriteEngine.ResolveType(saq) is { } st && WriteEngine.CanCoerce(st)) { wholeCoercible++; continue; } // TranslatedString-style — set wholesale
            (int sites, int nullableSites, string sample, string? aq) e =
                navInto.TryGetValue(tr, out var v) ? v : (0, 0, $"{ts.Name}.{f.Name}", (string?)null);
            navInto[tr] = (e.sites + 1, e.nullableSites + (f.Nullable ? 1 : 0), e.sample, e.aq ?? saq);
        }

        Console.WriteLine($"=== substruct-probe over {corpus.TotalTypes} types ===");
        Console.WriteLine($"Navigate-into substruct target types (non-record, non-whole-coercible): {navInto.Count} distinct");
        Console.WriteLine($"  (skipped: {wholeCoercible} whole-coercible substruct site(s); {recordSubstructs} record-substruct site(s))");
        Console.WriteLine();

        int resolved = 0, paramless = 0, trickyInScope = 0, trickyGetOnly = 0, unresolved = 0;
        var trickyList = new List<string>();
        foreach (var (name, info) in navInto)
        {
            var concrete = ResolveConcreteSubstruct(asm, name, info.aq);
            var settable = SampleSettable(asm, info.sample);     // can the sample owner-field be null (absent), i.e. in materialization scope?
            if (concrete is null) { unresolved++; Console.WriteLine($"  [UNRESOLVED]        {name,-44} sites={info.sites}  e.g. {info.sample}"); continue; }
            resolved++;
            if (concrete.GetConstructor(Type.EmptyTypes) is not null) { paramless++; continue; }
            // No parameterless ctor. If the owner-field is GET-ONLY it is always-present (never absent) -> out of materialization scope.
            var ctors = string.Join(" | ", concrete.GetConstructors()
                .Select(c => "(" + string.Join(", ", c.GetParameters().Select(p => $"{Pretty(p.ParameterType)} {p.Name}")) + ")"));
            var scope = settable switch { false => "GET-ONLY (always present, out of scope)", true => "SETTABLE (can be absent -> needs composition)", _ => "settable?=unknown" };
            if (settable == false) trickyGetOnly++; else { trickyInScope++; trickyList.Add(name); }
            Console.WriteLine($"  [NO PARAMLESS CTOR] {Pretty(concrete),-40} {scope,-44} ctors: {(ctors.Length == 0 ? "(none public)" : ctors)}  e.g. {info.sample}");
        }

        Console.WriteLine();
        Console.WriteLine($"Resolved concrete: {resolved}/{navInto.Count} (unresolved {unresolved}).");
        Console.WriteLine($"  parameterless ctor (simple Activator materialization suffices): {paramless}");
        Console.WriteLine($"  NO paramless ctor, GET-ONLY owner field (always present, never absent — out of scope): {trickyGetOnly}");
        Console.WriteLine($"  NO paramless ctor, SETTABLE owner field (real composition gap — must be NAMED + deferred): {trickyInScope}" + (trickyList.Count > 0 ? " -> " + string.Join(", ", trickyList.Distinct()) : ""));
        Console.WriteLine();
        Console.WriteLine(trickyInScope == 0 && unresolved == 0
            ? "=== substruct-probe: every ABSENT-ABLE navigate-into substruct has a parameterless ctor — simple materialization suffices (the no-ctor cases are all get-only/always-present) ==="
            : $"=== substruct-probe: {trickyInScope} settable no-ctor target(s) are a composition gap to NAME; {unresolved} unresolved — see above ===");
        return 0;
    }

    /// <summary>Resolve the concrete owner type of a "Owner.Field" sample and report whether that field is settable
    /// (CanWrite) — i.e. whether the substruct can be null/absent (materialization scope) vs get-only/always-present.</summary>
    static bool? SampleSettable(Assembly asm, string sample)
    {
        int dot = sample.LastIndexOf('.');
        if (dot <= 0) return null;
        var owner = ResolveConcreteSubstruct(asm, sample[..dot], null);
        if (owner is null) return null;
        return WriteEngine.ResolveProperty(owner, sample[(dot + 1)..])?.CanWrite;
    }

    /// <summary>Resolve the concrete settable class for a substruct target (the type the engine instantiates to
    /// materialize an absent one). Resolve the declared runtime type from the field's AQ first (handles CLOSED
    /// GENERICS like GenderedItem&lt;ArmorModel&gt;); map a mutable/getter interface to its concrete impl; fall back
    /// to a same-name non-abstract class. Returns the concrete (or the resolved type if already a usable class).</summary>
    static Type? ResolveConcreteSubstruct(Assembly asm, string catalogName, string? aq)
    {
        var t = aq is null ? null : WriteEngine.ResolveType(aq);
        if (t is not null && WriteEngine.ConcreteOf(t) is { } c) return c;          // interface→concrete (incl. closed generics) via the shared mapper
        var direct = asm.GetType("Mutagen.Bethesda.Skyrim." + catalogName);
        if (direct is { IsClass: true, IsAbstract: false }) return direct;
        return SafeTypes(asm).FirstOrDefault(x => x.IsClass && !x.IsAbstract && x.Name == catalogName) ?? t ?? direct;
    }

    static int CountGroupProps() =>
        typeof(SkyrimMod).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Count(p => Pretty(p.PropertyType).Contains("Group"));

    static string Sig(MethodInfo m)
    {
        var gen = m.IsGenericMethodDefinition
            ? "<" + string.Join(",", m.GetGenericArguments().Select(g => g.Name)) + ">"
            : "";
        var ps = string.Join(", ", m.GetParameters().Select(p => $"{Pretty(p.ParameterType)} {p.Name}"));
        return $"{m.Name}{gen}({ps}) -> {Pretty(m.ReturnType)}";
    }

    static string Sha(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
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

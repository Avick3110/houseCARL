using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD (standing CI instrument, self-contained) — SPEC-OBJECT WIRE NAMES (#341).
///
/// Every tool that takes an array of spec objects declares its wire field names with
/// <c>[JsonPropertyName]</c>. Nothing exercised those names: every probe builds a spec through the C#
/// object initializer, which bypasses the attribute entirely — so a misspelled, renamed or removed wire
/// name dropped that parameter for every real MCP caller with the whole suite green. For
/// <c>place_asset</c> that is a placement quietly reading a copy the caller never named (Q3's
/// silent-wrong-answer shape). This is a CLASS, not a field: fixing instances one at a time leaves the
/// next spec object added to the surface in exactly the same position.
///
/// <para><b>Why a round-trip alone is not enough.</b> An arm that reads the attribute, builds JSON from
/// it and deserializes proves the plumbing works — but it CANNOT see a misspelling, because it echoes
/// the typo straight back. The published schema is generated off the same attribute, so it agrees with
/// the typo too. A wire name is only pinned against a SECOND, independently written statement of the
/// same name. That statement already exists and is the one that matters: the caller-facing shape
/// declaration in the carrying parameter's (or property's) <c>[Description]</c> — the brace list a
/// client actually reads, e.g. <c>{formid, field_path, op?, …}</c>. This guard parses those and holds
/// them against the reflected attributes, so the docs and the wire cannot drift apart in either
/// direction.</para>
///
///   INV1 — every settable member of a wire type carries [JsonPropertyName] or [JsonIgnore]. (A member
///          with neither binds under its C# name and is advertised under a camelCase one — unpinned.)
///   INV2 — every wire member ARRIVES, under its wire name, through BOTH readers the surface uses:
///          ListParams.Strict (apply/create's @file lists) and the SDK's web defaults (the directly
///          bound spec arrays — bulk_apply, bulk_create, bulk_place_asset). Arrival is checked against
///          the value sent, not against null, so a member with a C# default ({BulkOp.Verb = "Set"})
///          cannot pass by keeping its default.
///   INV3 — every wire type is REACHABLE from a tool parameter and has a caller-facing shape
///          declaration. An unreachable one is named, never skipped.
///   INV4 — every name a shape declaration lists is a real wire name (a typo in the ATTRIBUTE shows up
///          here as a declared name that no longer exists).
///   INV5 — every wire name appears in some shape declaration (a typo shows up here as a wire name
///          nobody documents; so does a member added without touching the docs).
///
/// <para><b>What this does NOT cover, and why.</b> INV5 is satisfied by the UNION of a type's carriers, not
/// by each one: a carrier may legitimately declare a strict subset, because a member can be illegal in that
/// position — <c>create_record</c>'s <c>operations=</c> takes a BulkOp but refuses <c>formid</c> and
/// <c>from_plugin</c> there, since a record being created has no other version to copy from. "Legal at this
/// carrier" is a semantic fact no attribute carries, so requiring completeness per carrier would force
/// authors to document members their tool rejects. The consequence is real and worth knowing: a member
/// legal-but-undeclared at ONE carrier stays green while another carrier declares it. Shape lists in
/// method-level [Description] prose and in refusal strings are likewise unread — they restate a fact whose
/// home is the parameter (DOC_HYGIENE §6), and matching a loose prose group to the right type is guesswork
/// this guard will not do silently. Both gaps are content to keep honest by hand, not coverage to claim.</para>
///
/// Discovery is by construction throughout: the wire types are the assembly's [JsonPropertyName]-bearing
/// types, and the carriers are the parameters/properties typed with them — adding a spec object to the
/// surface enrols it with no edit here. RED arms drive each checker with a synthetic violation, and the
/// central one is exhaustive: for EVERY wire type and EVERY member, sending that member under a
/// misspelled name must be reported as not-arrived. That is the issue's own scenario, run once per field
/// on the real surface.
///
/// Run: dotnet run --project src/housecarl-generator -- wire-names-guard
/// </summary>
public static class WireNamesProbe
{
    static int _pass, _fail;

    /// <summary>The value every synthesized member is sent as. Deliberately unlike any C# default on the
    /// surface, so "arrived" cannot be satisfied by a property that was never written.</summary>
    const string Marker = "hc-wire-341";

    /// <summary>Wire types deliberately NOT reachable from a tool parameter — an output DTO that carries
    /// [JsonPropertyName] for rendering, say. EMPTY by design: every wire type on the surface today is an
    /// input spec. Add one here ONLY with a one-line reason, so "not an input" stays a recorded choice
    /// rather than a silent gap in INV3.
    /// <para>It exempts INV3 ALONE. INV1 and INV2 still run over such a type, deliberately: an unpinned
    /// member and a wire name that does not bind are defects wherever they are. If that type carries a
    /// member shape <see cref="Synthesize"/> cannot drive, INV2 fails loud naming the member rather than
    /// passing over it — teach Synthesize the shape; do not reach for a skip.</para></summary>
    static readonly HashSet<string> NonInputWireTypes = new(StringComparer.Ordinal)
    {
        // (none)
    };

    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — spec-object wire names ([JsonPropertyName])  ################");
        Console.WriteLine();
        try
        {
            var wireTypes = WireTypes();
            Check($"GREEN     reflected a non-empty wire-type set ({wireTypes.Count}: {string.Join(", ", wireTypes.Select(t => t.Name))})",
                wireTypes.Count > 0,
                new() { "no [JsonPropertyName]-bearing type found in the housecarl-mcp assembly — wrong assembly, or the DTOs moved" });

            // ---- INV1: nothing settable is unpinned ------------------------------------------------
            var unpinned = wireTypes.SelectMany(UnpinnedMembers).ToList();
            Check("INV1-GREEN every settable member carries [JsonPropertyName] or [JsonIgnore]", unpinned.Count == 0, unpinned);

            // ---- INV2: every member arrives, through both readers -----------------------------------
            foreach (var (lane, opts) in Readers)
            {
                var missing = wireTypes.SelectMany(t => Arrivals(t, opts, mangle: null)).ToList();
                Check($"INV2-GREEN every wire member arrives under its wire name ({lane})", missing.Count == 0, missing);
            }

            // ---- INV3: reachable from the tool surface, and documented ------------------------------
            var carriers = Carriers(wireTypes);
            var reachable = Reachable(wireTypes);
            var unreachable = wireTypes.Where(t => !reachable.Contains(t) && !NonInputWireTypes.Contains(t.Name))
                .Select(t => $"{t.Name}: no tool parameter carries it (directly, through ToolSchemas' @file registry, or through another wire type's member) " +
                             "— it is dead, or an output DTO that belongs in NonInputWireTypes with a reason")
                .OrderBy(m => m, StringComparer.Ordinal).ToList();
            Check("INV3-GREEN every wire type is reachable from a tool parameter", unreachable.Count == 0, unreachable);

            var undeclared = wireTypes.Where(t => reachable.Contains(t) && DeclaredUnion(t, carriers).Count == 0)
                .Select(t => $"{t.Name}: no carrier's [Description] declares its element shape — a caller cannot learn its members, " +
                             $"and nothing holds the wire names to a second spelling. Carriers: {CarrierLabels(t, carriers)}")
                .OrderBy(m => m, StringComparer.Ordinal).ToList();
            Check("INV3-GREEN every reachable wire type has a caller-facing shape declaration", undeclared.Count == 0, undeclared);

            // ---- INV4/INV5: the declarations and the attributes agree, both ways ---------------------
            var unknown = new List<string>();
            var undocumented = new List<string>();
            foreach (var t in wireTypes.Where(reachable.Contains))
            {
                var wire = WireNames(t).Keys.ToHashSet(StringComparer.Ordinal);
                foreach (var c in CarriersOf(t, carriers))
                    unknown.AddRange(UnknownDeclarations(wire, DeclaredNames(c.Description), c.Label, t.Name));
                undocumented.AddRange(UndocumentedMembers(wire, DeclaredUnion(t, carriers), t.Name, CarrierLabels(t, carriers)));
            }
            Check("INV4-GREEN every declared member name is a real wire name", unknown.Count == 0, unknown);
            Check("INV5-GREEN every wire name is declared to callers somewhere", undocumented.Count == 0, undocumented);

            RunRedArms(wireTypes);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   FAIL (unexpected): {ex.GetType().Name}: {ex.Message}");
            _fail++;
        }

        Console.WriteLine();
        Console.WriteLine($"=== wire-names-guard: {_pass} passed, {_fail} failed -> {(_fail == 0 ? "PASS" : "FAIL")} ===");
        return _fail == 0 ? 0 : 1;
    }

    // ================= RED arms — each checker is driven with a synthetic violation =================

    /// <summary>A synthetic wire type for the RED arms: <c>Pinned</c> is declared, <c>Skipped</c> is
    /// deliberately out of the wire, and <c>Bare</c> carries neither attribute — the INV1 violation.</summary>
    sealed record RedUnpinned
    {
        [JsonPropertyName("pinned")] public string? Pinned { get; init; }
        [JsonIgnore] public string? Skipped { get; init; }
        public string? Bare { get; init; }
    }

    /// <summary>The same shape with nothing unpinned — proves INV1's checker does not just always fire.</summary>
    sealed record RedPinned
    {
        [JsonPropertyName("pinned")] public string? Pinned { get; init; }
        [JsonIgnore] public string? Skipped { get; init; }
    }

    static void RunRedArms(IReadOnlyList<Type> wireTypes)
    {
        Console.WriteLine();

        var red1 = UnpinnedMembers(typeof(RedUnpinned));
        Check("INV1-RED   a member with neither attribute is reported",
            red1.Any(m => m.Contains("Bare", StringComparison.Ordinal)), red1, redArm: true);

        var green1 = UnpinnedMembers(typeof(RedPinned));
        Check("INV1-GUARD a fully attributed shape is NOT reported", green1.Count == 0, green1, redArm: true);

        // The central arm, and the executable form of #341: on the REAL surface, for every wire type and
        // every member, sending that member under a misspelled name must be caught. This is what a pure
        // build-from-the-attribute round-trip cannot do — it would echo the typo and pass.
        //
        // The two lanes catch it by DIFFERENT mechanisms, and the report is accepted from either: the
        // strict reader refuses the document and names the spelling it could not map, while the web
        // reader binds happily and the arrival comparison finds the member holding its default. Both are
        // the reader's own evidence — see the catch block in Arrivals for why that matters.
        foreach (var (lane, opts) in Readers)
        {
            var blind = new List<string>();
            int cases = 0;
            foreach (var t in wireTypes)
                foreach (var name in WireNames(t).Keys)
                {
                    cases++;
                    var reported = Arrivals(t, opts, mangle: name);
                    if (!reported.Any(v => v.Contains(name, StringComparison.Ordinal) || v.Contains(Mangle(name), StringComparison.Ordinal)))
                        blind.Add($"{t.Name}.{name}: sent under a misspelled name and NOT reported missing ({lane}) — " +
                                  $"this lane cannot see the field drop. Reported: {(reported.Count == 0 ? "(nothing)" : string.Join(" | ", reported))}");
                }
            Check($"INV2-RED   every wire member, misspelled, is reported missing ({lane}; {cases} fields)",
                blind.Count == 0, blind, redArm: true);
        }

        // INV4/INV5's teeth, on the real PlaceAssetSpec — the field family the issue was filed from.
        var placeWire = WireNames(typeof(PlaceAssetSpec)).Keys.ToHashSet(StringComparer.Ordinal);

        // The typo is derived to be absent rather than hardcoded: a literal would silently stop being a
        // violation the day the surface grew a member spelled that way, and this arm would then report
        // "toothless" for the wrong reason — masking whichever real defect had just landed.
        var typo = "sorce";
        while (placeWire.Contains(typo)) typo += "z";
        var red4 = UnknownDeclarations(placeWire, DeclaredNames($"each {{ asset_path, {typo} }}"), "synthetic carrier", nameof(PlaceAssetSpec));
        Check($"INV4-RED   a declared name that is not a wire name is reported ('{typo}')",
            red4.Any(m => m.Contains(typo, StringComparison.Ordinal)), red4, redArm: true);

        var red5 = UndocumentedMembers(placeWire, DeclaredNames("each { asset_path }"), nameof(PlaceAssetSpec), "synthetic carrier");
        Check("INV5-RED   a wire name no declaration lists is reported",
            red5.Any(m => m.Contains("source_provider", StringComparison.Ordinal)), red5, redArm: true);

        var green45 = UnknownDeclarations(placeWire, DeclaredNames("each { formid?, kind?, asset_path?, source?, source_provider? }"), "synthetic carrier", nameof(PlaceAssetSpec));
        Check("INV45-GUARD a faithful declaration is NOT reported", green45.Count == 0, green45, redArm: true);

        // The shape parser must read a real declaration, not merely fail to complain about a fake one.
        var parsed = DeclaredNames("The edits: [{formid, field_path, op?, value?, values?, key?, entries?, compose?, composes?, from?, from_source?}, …] — or \"@<path>\".");
        var applyWire = WireNames(typeof(ApplyOp)).Keys.ToHashSet(StringComparer.Ordinal);
        Check($"PARSE-GUARD the shape parser reads a real brace list ({parsed.Count} names)",
            parsed.SetEquals(applyWire),
            new() { $"parsed [{string.Join(", ", parsed.OrderBy(s => s, StringComparer.Ordinal))}] vs wire [{string.Join(", ", applyWire.OrderBy(s => s, StringComparer.Ordinal))}]" },
            redArm: true);

        // …and must not mistake prose for a declaration: a brace group whose items are not member names
        // contributes nothing, rather than injecting a phantom name into INV4.
        var prose = DeclaredNames("files it into the block tree {block=floor(grid/32), subblock=floor(grid / 8)} for the cell");
        Check("PARSE-RED  a prose brace group is not read as a shape declaration", prose.Count == 0,
            prose.Select(n => $"phantom declared name from prose: {n}").ToList(), redArm: true);

        // Both arms of the partial-marker branch. An abbreviated list still yields its named members…
        var abbreviated = DeclaredNames("Each: {formid, field_path, verb, …}");
        Check("PARSE-ELL  an ellipsis-closed list still yields its named members",
            abbreviated.SetEquals(new[] { "formid", "field_path", "verb" }),
            new() { $"parsed [{string.Join(", ", abbreviated.OrderBy(s => s, StringComparer.Ordinal))}] — expected formid, field_path, verb" },
            redArm: true);

        // …while an item that is NOT a partial marker still discards the group, so the ellipsis skip did
        // not open a door for prose. (A capitalized word is the case: member names are snake_case.)
        var notAMarker = DeclaredNames("Each: {formid, field_path, Whatever It Says}");
        Check("PARSE-ELL-RED a non-marker item still discards the whole group", notAMarker.Count == 0,
            notAMarker.Select(n => $"phantom declared name: {n}").ToList(), redArm: true);
    }

    // ================= the checkers (pure — the RED arms drive these directly) =================

    /// <summary>Members that bind off the wire but declare no wire name. A settable public property with
    /// neither <c>[JsonPropertyName]</c> nor <c>[JsonIgnore]</c> is advertised under one spelling (the
    /// schema generator's camelCase) and documented under another — nobody pinned which one is real.</summary>
    static List<string> UnpinnedMembers(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.SetMethod?.IsPublic == true
                        && p.GetCustomAttribute<JsonPropertyNameAttribute>() is null
                        && p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .Select(p => $"{t.Name}.{p.Name}: settable off the wire with no [JsonPropertyName] (and not [JsonIgnore]) — " +
                         "give it a wire name, or mark it [JsonIgnore] if it is not a caller's to set")
            .OrderBy(m => m, StringComparer.Ordinal).ToList();

    /// <summary>Send a document naming every wire member of <paramref name="t"/> and report the members
    /// that did not arrive with the value sent. <paramref name="mangle"/> misspells ONE top-level name on
    /// the way out (the RED arms' lever); nested objects always use their real names.
    /// <para>A property shape this cannot synthesize a value for is reported as a violation, never
    /// skipped — an unreadable member checked as covered is the trap a hand-list would reintroduce.</para></summary>
    static List<string> Arrivals(Type t, JsonSerializerOptions opts, string? mangle)
    {
        var props = WireNames(t);
        var violations = new List<string>();

        var body = new List<string>();
        foreach (var (name, p) in props)
        {
            var value = Synthesize(p.PropertyType, new HashSet<Type> { t });
            if (value is null)
            {
                violations.Add($"{t.Name}.{name}: no value can be synthesized for {Pretty(p.PropertyType)} — this member is UNCHECKED, " +
                               "not covered. Teach Synthesize the shape, or the arm is reporting coverage it does not have");
                continue;
            }
            body.Add($"{JsonSerializer.Serialize(name == mangle ? Mangle(name) : name)}:{value}");
        }
        if (violations.Count > 0) return violations;

        object? parsed;
        try { parsed = JsonSerializer.Deserialize("{" + string.Join(",", body) + "}", t, opts); }
        catch (Exception ex)
        {
            // The strict reader REFUSES an undeclared member rather than dropping it, so a misspelling
            // lands here — and STJ's own message names the spelling it could not map. That text is the
            // evidence, and it is the reader's, not this probe's: an earlier version stamped the mangled
            // name into this string itself, which made INV2-RED self-satisfying on this lane (it matched
            // the probe's own words, so gutting the arrival comparison left the arm green).
            return new() { $"{t.Name}: the document was refused — {ex.GetType().Name}: {Flatten(ex.Message)}" };
        }
        if (parsed is null) return new() { $"{t.Name}: deserialized to null" };

        foreach (var (name, p) in props)
            if (Describe(p.GetValue(parsed)) is { } wrong)
                violations.Add($"{t.Name}.{name}: did not arrive ({wrong}) — the wire name '{p.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name}' " +
                               "is not the name this member binds under");
        return violations;
    }

    /// <summary>Names a shape declaration lists that are not wire names of the type it carries.</summary>
    static List<string> UnknownDeclarations(ISet<string> wire, ISet<string> declared, string carrier, string typeName) =>
        declared.Where(d => !wire.Contains(d))
            .Select(d => $"{carrier} declares '{d}', which is not a wire name of {typeName} (its members: {string.Join(", ", wire.OrderBy(s => s, StringComparer.Ordinal))}) — " +
                         "either the [Description] or the [JsonPropertyName] is misspelled")
            .OrderBy(m => m, StringComparer.Ordinal).ToList();

    /// <summary>Wire names no shape declaration lists — a member a caller cannot discover, and a name
    /// held to no second spelling.</summary>
    static List<string> UndocumentedMembers(ISet<string> wire, ISet<string> declaredUnion, string typeName, string carrierLabels) =>
        wire.Where(w => !declaredUnion.Contains(w))
            .Select(w => $"{typeName}.{w}: no carrier's shape declaration lists it — add it to the element shape in {carrierLabels}")
            .OrderBy(m => m, StringComparer.Ordinal).ToList();

    // ================= discovery (by construction) =================

    /// <summary>Every type in the housecarl-mcp assembly declaring at least one <c>[JsonPropertyName]</c>
    /// member. This IS the wire-type set — adding a spec object to the surface enrols it here with no
    /// edit to this guard.</summary>
    static List<Type> WireTypes() =>
        typeof(ApplyOp).Assembly.GetTypes()
            .Where(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .Any(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() is not null))
            .OrderBy(t => t.Name, StringComparer.Ordinal).ToList();

    /// <summary>wire name -> the property it binds, in declaration order.</summary>
    static Dictionary<string, PropertyInfo> WireNames(Type t)
    {
        var map = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            if (p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name is { Length: > 0 } n)
                map[n] = p;
        return map;
    }

    /// <summary>One place a wire type is carried, and the caller-facing text that comes with it.</summary>
    readonly record struct Carrier(string Label, string Description);

    /// <summary>Where each wire type is carried: a tool parameter typed with it, a parameter registered in
    /// <c>ToolSchemas.FileListParams</c> (the @file lists are declared <c>JsonElement</c>, so their element
    /// type is only knowable from that registry), or another wire type's member. The [Description] on each
    /// is the caller-facing declaration INV4/INV5 hold the attributes against.</summary>
    static Dictionary<Type, List<Carrier>> Carriers(IReadOnlyList<Type> wireTypes)
    {
        var known = wireTypes.ToHashSet();
        var map = new Dictionary<Type, List<Carrier>>();
        void Add(Type? t, string label, string? description)
        {
            if (t is null || !known.Contains(t) || description is null) return;
            if (!map.TryGetValue(t, out var list)) map[t] = list = new List<Carrier>();
            list.Add(new Carrier(label, description));
        }

        var toolMethods = ToolMethods();
        foreach (var (tool, method) in toolMethods)
            foreach (var p in method.GetParameters())
                Add(ElementOf(p.ParameterType), $"{tool}'s {p.Name}=", p.GetCustomAttribute<DescriptionAttribute>()?.Description);

        // The @file list params are typed JsonElement — the SDK cannot see their element type and neither
        // can a parameter scan. ToolSchemas' registry is where that link is declared, so read it there.
        foreach (var row in ToolSchemas.FileListParams)
        {
            if (!toolMethods.TryGetValue(row.Tool, out var m)) continue;
            var p = m.GetParameters().FirstOrDefault(x => string.Equals(x.Name, row.Parameter, StringComparison.Ordinal));
            Add(ElementOf(row.ElementArrayType), $"{row.Tool}'s {row.Parameter}=", p?.GetCustomAttribute<DescriptionAttribute>()?.Description);
        }

        foreach (var t in wireTypes)
            foreach (var (name, p) in WireNames(t))
                Add(ElementOf(p.PropertyType), $"{t.Name}.{name}", p.GetCustomAttribute<DescriptionAttribute>()?.Description);

        return map;
    }

    static List<Carrier> CarriersOf(Type t, Dictionary<Type, List<Carrier>> carriers) =>
        carriers.TryGetValue(t, out var list) ? list : new List<Carrier>();

    static string CarrierLabels(Type t, Dictionary<Type, List<Carrier>> carriers)
    {
        var labels = CarriersOf(t, carriers).Select(c => c.Label).Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal).ToList();
        return labels.Count == 0 ? "(no carrier)" : string.Join(", ", labels);
    }

    static HashSet<string> DeclaredUnion(Type t, Dictionary<Type, List<Carrier>> carriers)
    {
        var union = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in CarriersOf(t, carriers)) union.UnionWith(DeclaredNames(c.Description));
        return union;
    }

    /// <summary>Wire types a caller can actually reach: carried by a tool parameter directly or through the
    /// @file registry, then transitively through wire members.</summary>
    static HashSet<Type> Reachable(IReadOnlyList<Type> wireTypes)
    {
        var known = wireTypes.ToHashSet();
        var seeds = new List<Type>();
        var toolMethods = ToolMethods();
        foreach (var (_, method) in toolMethods)
            foreach (var p in method.GetParameters())
                if (ElementOf(p.ParameterType) is { } t && known.Contains(t)) seeds.Add(t);
        foreach (var row in ToolSchemas.FileListParams)
            if (ElementOf(row.ElementArrayType) is { } t && known.Contains(t)) seeds.Add(t);

        var reached = new HashSet<Type>();
        var queue = new Queue<Type>(seeds);
        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            if (!reached.Add(t)) continue;
            foreach (var (_, p) in WireNames(t))
                if (ElementOf(p.PropertyType) is { } child && known.Contains(child) && !reached.Contains(child))
                    queue.Enqueue(child);
        }
        return reached;
    }

    /// <summary>Tool name -> the method behind it, off the real <c>[McpServerTool]</c> attributes.</summary>
    static Dictionary<string, MethodInfo> ToolMethods()
    {
        var map = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);
        foreach (var t in typeof(ApplyOp).Assembly.GetTypes())
        {
            if (t.GetCustomAttribute<McpServerToolTypeAttribute>() is null) continue;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (m.GetCustomAttribute<McpServerToolAttribute>(inherit: false)?.Name is { Length: > 0 } n)
                    map[n] = m;
        }
        return map;
    }

    /// <summary>The element type behind a parameter/property: <c>T[]</c> and <c>T</c> both yield <c>T</c>.</summary>
    static Type? ElementOf(Type t) => t.IsArray ? t.GetElementType() : t;

    // ================= the caller-facing shape declarations =================

    /// <summary>The member names a caller-facing description declares, read off its brace lists —
    /// <c>{formid, field_path, op?, …}</c>, <c>{ formid?: 'XXXXXX:Plugin.esp', … }</c>. Only OUTERMOST
    /// groups are read (so a nested illustration like <c>fields:{Name:'x'}</c> contributes <c>fields</c>,
    /// not <c>Name</c>), and a group is read as a declaration ONLY if every one of its items yields a
    /// snake_case member name — prose in braces contributes nothing rather than a phantom name.</summary>
    static HashSet<string> DeclaredNames(string description)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in OutermostGroups(description))
        {
            var items = SplitTopLevel(group);
            if (items.Count == 0) continue;
            var parsed = new List<string>();
            foreach (var item in items)
            {
                if (IsPartialMarker(item)) continue;
                if (MemberName(item) is not { } n) { parsed.Clear(); break; }
                parsed.Add(n);
            }
            names.UnionWith(parsed);
        }
        return names;
    }

    /// <summary>An item that says "and more" rather than naming a member: <c>…</c>, <c>...</c>, <c>etc</c>.
    /// Skipped, so the rest of the group is still read. Without this the whole declaration was discarded —
    /// which for a type with several carriers left INV5 green off the others and INV4 never looking at that
    /// carrier's text at all, so a misspelling in it went unseen. That is #341's own failure mode moved
    /// into the docs, and closing an abbreviated list with an ellipsis is the natural way to write one.</summary>
    static bool IsPartialMarker(string item)
    {
        var s = item.Trim().TrimEnd('.').Trim();
        return s is "…" or "..." or "" || string.Equals(s, "etc", StringComparison.OrdinalIgnoreCase);
    }

    static List<string> OutermostGroups(string s)
    {
        var groups = new List<string>();
        int depth = 0, start = -1;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '{') { if (depth++ == 0) start = i + 1; }
            else if (s[i] == '}' && depth > 0 && --depth == 0 && start >= 0) groups.Add(s[start..i]);
        }
        return groups;
    }

    /// <summary>Split a brace group on its TOP-LEVEL commas — nested braces/brackets/parens and quoted
    /// text (the illustrative values these declarations carry) do not separate items.</summary>
    static List<string> SplitTopLevel(string group)
    {
        var items = new List<string>();
        int depth = 0; char quote = '\0';
        var current = new StringBuilder();
        foreach (var c in group)
        {
            if (quote != '\0') { if (c == quote) quote = '\0'; current.Append(c); continue; }
            switch (c)
            {
                case '\'' or '"': quote = c; break;
                case '{' or '[' or '(': depth++; break;
                case '}' or ']' or ')': depth--; break;
                case ',' when depth == 0:
                    items.Add(current.ToString()); current.Clear(); continue;
            }
            current.Append(c);
        }
        if (current.ToString().Trim().Length > 0) items.Add(current.ToString());
        return items.Where(i => i.Trim().Length > 0).ToList();
    }

    /// <summary>The member name an item declares: the text before its <c>:</c>, minus the optional-marker
    /// <c>?</c>. Null when that text is not a snake_case member name.
    /// <para>A shape declaration writes its illustrative values JSON-style — <c>name</c>, <c>name?</c>,
    /// <c>name: 'value'</c> — and never with <c>=</c>. Treating <c>=</c> as a separator too is what made
    /// the guard read a prose aside like <c>{block=floor(grid/32), subblock=floor(grid/8)}</c> as a
    /// declaration of two members (PARSE-RED, which caught it).</para></summary>
    static string? MemberName(string item)
    {
        var head = item.Trim();
        int cut = head.IndexOf(':');
        if (cut >= 0) head = head[..cut];
        head = head.Trim().TrimEnd('?').Trim();
        if (head.Length == 0 || !char.IsAsciiLetterLower(head[0])) return null;
        return head.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_') ? head : null;
    }

    // ================= value synthesis for the round trip =================

    /// <summary>The readers the surface actually binds spec objects with. A wire name has to work in both:
    /// <c>ListParams.Strict</c> reads apply/create's @file lists, and the SDK's web defaults bind the
    /// directly typed arrays (bulk_apply's operations=, bulk_create's records=, bulk_place_asset's
    /// assets=). A name that survives only one of them is broken for the other's tools.</summary>
    static readonly (string Lane, JsonSerializerOptions Options)[] Readers =
    {
        ("ListParams.Strict — the @file list reader", ListParams.Strict),
        ("web defaults — the SDK-bound spec arrays", new JsonSerializerOptions(JsonSerializerDefaults.Web)),
    };

    /// <summary>A JSON value for a member's type, or null when the shape is not one this arm can drive.
    /// A wire type already on the path is emitted as <c>{}</c> (the compose/sets chain is recursive) —
    /// the member still has to ARRIVE, which is what the top-level pass over that type checks in full.</summary>
    static string? Synthesize(Type t, HashSet<Type> path)
    {
        if (t == typeof(string)) return JsonSerializer.Serialize(Marker);
        if (t == typeof(string[])) return $"[{JsonSerializer.Serialize(Marker)}]";
        if (t == typeof(Dictionary<string, string>)) return $"{{\"hc_key\":{JsonSerializer.Serialize(Marker)}}}";

        var element = t.IsArray ? t.GetElementType() : t;
        if (element is null || WireNames(element).Count == 0) return null;

        string body;
        if (!path.Add(element)) body = "{}";
        else
        {
            var parts = new List<string>();
            foreach (var (name, p) in WireNames(element))
            {
                if (Synthesize(p.PropertyType, path) is not { } v) return null;
                parts.Add($"{JsonSerializer.Serialize(name)}:{v}");
            }
            body = "{" + string.Join(",", parts) + "}";
            path.Remove(element);
        }
        return t.IsArray ? $"[{body}]" : body;
    }

    /// <summary>How a member failed to arrive, or null when it carries what was sent. Compared against the
    /// VALUE SENT, never against null: <c>BulkOp.Verb</c> initializes to "Set", so a dropped member would
    /// look populated to a null check.</summary>
    static string? Describe(object? value) => value switch
    {
        null => "null",
        string s => s == Marker ? null : $"'{s}' — not the value sent",
        string[] a => a.Length > 0 && a[0] == Marker ? null : "empty or not the value sent",
        Dictionary<string, string> d => d.Count > 0 ? null : "empty dictionary",
        Array arr => arr.Length > 0 ? null : "empty array",
        _ => null,
    };

    /// <summary>The misspelling INV2-RED sends a member under. Case-insensitive binding is on, so the
    /// mangle has to change letters rather than casing.</summary>
    static string Mangle(string wireName) => wireName + "zz";

    static string Pretty(Type t) => t.IsArray ? Pretty(t.GetElementType()!) + "[]" : t.Name;

    static string Flatten(string s) => s.Replace("\r", " ").Replace("\n", " ");

    static void Check(string label, bool ok, List<string> detail, bool redArm = false)
    {
        Console.WriteLine($"   {label,-78}: {(ok ? "PASS" : "FAIL")}");
        if (!ok)
        {
            if (detail.Count == 0)
                Console.WriteLine(redArm ? "        - (the checker reported NO violation — it is toothless)" : "        - (no detail)");
            foreach (var d in detail.Take(20)) Console.WriteLine($"        - {d}");
            // Never a silent cut (Q3): a 58-violation failure showing 20 rows reads like a 20-violation one.
            if (detail.Count > 20) Console.WriteLine($"        - … and {detail.Count - 20} more");
        }
        if (ok) _pass++; else _fail++;
    }
}

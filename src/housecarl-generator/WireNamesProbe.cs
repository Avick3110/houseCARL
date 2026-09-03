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
///   INV2 — every wire member ARRIVES, under its wire name, through EVERY reader the surface uses:
///          ListParams.Strict (apply/create's @file lists) and McpJsonUtilities.DefaultOptions (the SDK's
///          own singleton, behind the directly bound spec arrays — bulk_place_asset; see Readers for how
///          far the SDK lane is verified). For a scalar or a collection, arrival is checked against the
///          VALUE SENT rather than against null, so a member with a C# default ({BulkOp.Verb = "Set"})
///          cannot pass by keeping its default; for a member that is itself a wire object, presence is
///          the check, since an unbound one is null and there is nothing else it could be.
///   INV3 — every wire type is REACHABLE from a tool parameter and has a caller-facing shape
///          declaration. An unreachable one is named, never skipped.
///   INV4 — every name a shape declaration lists is a real wire name (a typo in the ATTRIBUTE shows up
///          here as a declared name that no longer exists).
///   INV5 — every wire name appears in some shape declaration (a typo shows up here as a wire name
///          nobody documents; so does a member added without touching the docs).
///   INV6 — a shape list is read WHOLE, or not read as a shape list at all — never partly and silently,
///          at EVERY level of the read path: characters (braces that do not balance, so the group is
///          never found), groups (items that stop reading as member names), and items (an ellipsis is a
///          partial marker; anything else is not). A silent discard at any one level takes a carrier out
///          of INV4's reach while INV5 stays green off the type's other carriers — #341's own failure
///          mode, moved into the docs. Each of the three was found in turn, by three separate reviews,
///          which is why the completeness is stated as a claim here instead of assumed.
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
/// <para><b>Standing class boundary</b> (advisor, Aaron-seen, 2026-08-16). The silent-discard class has now
/// been closed three times, one level of the read path each time. If a FOURTH instance surfaces, the fix is
/// NOT a fourth patch: the prose-parsing design itself escalates under §4, and the second spelling moves out
/// of <c>[Description]</c> prose into structured data — an attribute or a registry — where it cannot be
/// mis-lexed at all. Do not fold a fourth; escalate.</para>
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
        // Both stopped being caller-facing input at the demolition catch-up (#468): bulk_apply and bulk_create were
        // the only tools that bound them off the wire. They are now INTERNAL service shapes — ApplyTools reads the
        // 2.0 ApplyOp and constructs BulkOp for LoadOrderService.ApplyEdits, CreateTools does the same with
        // CreateRecordSpec -> CreateOp — so INV3's "reachable from a tool parameter" is correctly false for them
        // and INV2's reader round-trip no longer describes anything a caller can send.
        // Their [JsonPropertyName] attributes are now vestigial, and ApplyOp's own summary says it exists as a
        // separate type "so the 1.x tools' published schemas stay untouched through the build waves" — a reason
        // this PR just retired. Collapsing the pairs is follow-up work, deliberately not done here: it is a
        // reshape of live 2.0 types, not a deletion, and belongs in its own change.
        "BulkOp",
        "CreateOp",
    };

    [CiProbe("wire-names-guard")]
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

            // INV6 — a declaration this parser can only read PART of is a violation, not a silent skip.
            // Without it, one unreadable item takes a whole carrier out of INV4's reach while INV5 stays
            // green off the type's other carriers: a misspelling in that text would never be seen.
            var partial = wireTypes.Where(reachable.Contains)
                .SelectMany(t => CarriersOf(t, carriers))
                .Distinct()
                .SelectMany(c => PartialGroups(c.Description, c.Label))
                .OrderBy(m => m, StringComparer.Ordinal).ToList();
            Check("INV6-GREEN every shape list is read WHOLE, or not read as one at all", partial.Count == 0, partial);

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

    /// <summary>A wire type whose member shape <see cref="Synthesize"/> cannot drive — the case a future
    /// spec object with a numeric or boolean member would be. INV2-GUARD asserts it is REPORTED.</summary>
    sealed record RedUnsynthesizable
    {
        [JsonPropertyName("count")] public int Count { get; init; }
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
            {
                // A type carrying a member Synthesize cannot drive has no document to mangle, and every
                // one of its members would report the same unsynthesizable line — burying the one
                // actionable sentence under a wall of copies. Say it once, for the type, and move on:
                // INV2-GREEN has already failed with the detail.
                if (Arrivals(t, opts, mangle: null).Any(v => v.Contains("UNCHECKED", StringComparison.Ordinal)))
                {
                    blind.Add($"{t.Name}: not misspelling-tested — a member's shape cannot be synthesized (see INV2-GREEN for which)");
                    continue;
                }
                foreach (var name in WireNames(t).Keys)
                {
                    cases++;
                    var reported = Arrivals(t, opts, mangle: name);
                    if (!reported.Any(v => v.Contains(name, StringComparison.Ordinal) || v.Contains(Mangle(name), StringComparison.Ordinal)))
                        blind.Add($"{t.Name}.{name}: sent under a misspelled name and NOT reported missing ({lane}) — " +
                                  $"this lane cannot see the field drop. Reported: {(reported.Count == 0 ? "(nothing)" : string.Join(" | ", reported))}");
                }
            }
            Check($"INV2-RED   every wire member, misspelled, is reported missing ({lane}; {cases} fields)",
                blind.Count == 0, blind, redArm: true);
        }

        // INV4/INV5's teeth, on a SYNTHETIC wire set. These arms once ran against PlaceAssetSpec's real
        // member names, which made them a frozen copy of the surface: correctly renaming a member would
        // have failed them, and the failure would have pointed at the checker rather than at the rename.
        // Reading real names is INV4-GREEN and INV5-GREEN's job, across all nine types; these arms only
        // have to prove the two checkers are not blind, which needs no real data at all.
        var wire = new HashSet<string>(StringComparer.Ordinal) { "alpha", "beta_two", "gamma" };

        var red4 = UnknownDeclarations(wire, DeclaredNames("each { alpha, alpah }"), "synthetic carrier", "SyntheticSpec");
        Check("INV4-RED   a declared name that is not a wire name is reported",
            red4.Any(m => m.Contains("alpah", StringComparison.Ordinal)), red4, redArm: true);

        var red5 = UndocumentedMembers(wire, DeclaredNames("each { alpha }"), "SyntheticSpec", "synthetic carrier");
        Check("INV5-RED   a wire name no declaration lists is reported",
            red5.Any(m => m.Contains("beta_two", StringComparison.Ordinal)) && red5.Any(m => m.Contains("gamma", StringComparison.Ordinal)),
            red5, redArm: true);

        var green45 = UnknownDeclarations(wire, DeclaredNames("each { alpha, beta_two?, gamma? }"), "synthetic carrier", "SyntheticSpec");
        Check("INV4-GUARD  a faithful declaration is NOT reported", green45.Count == 0, green45, redArm: true);

        // The shape parser must read a declaration in the form the surface writes them — the [{…}, …]
        // wrapper, optional-markers, underscored names, trailing prose. The fixture is INVENTED rather
        // than copied from a real description: an earlier version held a frozen copy of housecarl_apply's
        // ops= list and compared it to ApplyOp's live members, so correctly adding a member to that type
        // failed this arm — a second home for a fact whose home is the [Description] (DOC_HYGIENE §6),
        // inside the guard that exists to stop exactly that. Reading the real declarations is INV3/4/5's
        // job, on every carrier, against live reflection; this arm is the parser's own unit test.
        var expected = new[] { "alpha", "beta_two", "gamma", "delta_four" };
        var parsed = DeclaredNames("The things: [{alpha, beta_two, gamma?, delta_four?}, …] — or \"@<path>\".");
        Check($"PARSE-GUARD the shape parser reads a declaration in the surface's form ({parsed.Count} names)",
            parsed.SetEquals(expected),
            new() { $"parsed [{string.Join(", ", parsed.OrderBy(s => s, StringComparer.Ordinal))}] — expected [{string.Join(", ", expected)}]" },
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

        // …while an item that is NOT a partial marker still keeps its group out of the declared set, so
        // the ellipsis skip did not open a door for prose. (A capitalised word is the case: member names
        // are snake_case.) That group is now REPORTED rather than dropped — INV6-RED below.
        var notAMarker = DeclaredNames("Each: {formid, field_path, Whatever It Says}");
        Check("PARSE-ELL-RED a non-marker item still keeps the group out of the declared set", notAMarker.Count == 0,
            notAMarker.Select(n => $"phantom declared name: {n}").ToList(), redArm: true);

        // All three arms of the group classification, since it decides what INV4 ever gets to look at.
        var partial = PartialGroups("Each: {formid, field_path, Whatever It Says}", "synthetic carrier");
        Check("INV6-RED   a partly-readable shape list is reported, not dropped",
            partial.Count == 1 && partial[0].Contains("Whatever It Says", StringComparison.Ordinal), partial, redArm: true);

        var whole = PartialGroups("Each: {formid, field_path, op?, …}", "synthetic carrier");
        Check("INV6-GUARD a fully-readable shape list is NOT reported", whole.Count == 0, whole, redArm: true);

        var allProse = PartialGroups("files it into the block tree {block=floor(grid/32), subblock=floor(grid / 8)} for the cell", "synthetic carrier");
        Check("INV6-GUARD prose in braces is NOT reported as a broken shape list", allProse.Count == 0, allProse, redArm: true);

        // Both arms of the BRACE level, one below the item level above. An unbalanced list yields no group
        // at all, so it is not merely unread — it is invisible, and every check downstream of it silently
        // has nothing to look at.
        var unclosed = PartialGroups("Each: {formid, field_path, value?", "synthetic carrier");
        Check("INV6-RED   an unclosed shape list is reported, not left invisible",
            unclosed.Any(m => m.Contains("never closed", StringComparison.Ordinal)), unclosed, redArm: true);

        var strayClose = PartialGroups("Each: formid, field_path, value?}", "synthetic carrier");
        Check("INV6-RED   a stray '}' with nothing opened is reported",
            strayClose.Any(m => m.Contains("nothing opened", StringComparison.Ordinal)), strayClose, redArm: true);

        var balanced = PartialGroups("Each: {formid, field_path, compose?:{type, fields?}} — or \"@<path>\".", "synthetic carrier");
        Check("INV6-GUARD balanced braces, nesting included, are NOT reported", balanced.Count == 0, balanced, redArm: true);

        // The acceptance case, in the shape of the sabotage that motivated this arm: a carrier's closing
        // brace dropped AND a member name misspelled inside it. Before the brace level was checked, the
        // misspelling was unreachable — no group, so nothing for INV4 to read, while INV5 stayed green off
        // the type's other carriers. The fixture is INVENTED rather than copied off a real description:
        // copying one back in is the frozen-copy trap this probe already paid for once.
        var doubleSabotage = PartialGroups("Optional. The fields: {field_path, valu?, value?, compose?", "synthetic carrier");
        Check("INV6-ACCEPT a dropped '}' hiding a misspelled member is caught",
            doubleSabotage.Any(m => m.Contains("never closed", StringComparison.Ordinal)), doubleSabotage, redArm: true);

        // The unsynthesizable-shape path claims to fail loud rather than skip. Nothing on the surface
        // exercises it today (every member is a string, a string list, a string map or a wire object), so
        // it is pinned here instead of resting on the claim.
        var unchecked_ = Arrivals(typeof(RedUnsynthesizable), Readers[0].Options, mangle: null);
        Check("INV2-GUARD a member shape that cannot be synthesized is REPORTED, never skipped",
            unchecked_.Any(v => v.Contains("UNCHECKED", StringComparison.Ordinal) && v.Contains("count", StringComparison.Ordinal)),
            unchecked_, redArm: true);
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
        HousecarlMcp.ToolSurface.Assembly.GetTypes()
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
        foreach (var t in HousecarlMcp.ToolSurface.Assembly.GetTypes())
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
    /// groups are read, so a nested illustration like <c>fields:{Name:'x'}</c> contributes <c>fields</c>,
    /// not <c>Name</c>.
    /// <para>A group is classified by how much of it reads as member names. ALL of it — a declaration.
    /// NONE of it — prose in braces, contributing nothing rather than a phantom name. SOME of it — a
    /// declaration this parser cannot read whole, which is <see cref="PartialGroups"/>' business: it is
    /// REPORTED, never quietly dropped. Dropping it was a silent hole exactly the shape of #341 — with
    /// several carriers on a type, INV5 stayed green off the others while INV4 never read this carrier's
    /// text at all, so a misspelling in it went unseen. One unreadable item (a capitalised word, an
    /// apostrophe opening a quote that never closes, a parenthetical) was enough.</para></summary>
    static HashSet<string> DeclaredNames(string description)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in OutermostGroups(description))
            if (ReadGroup(group) is { } parsed) names.UnionWith(parsed);
        return names;
    }

    /// <summary>Read one brace group: the member names if EVERY item yields one, an empty list if none
    /// does (prose), null if only some do (unreadable — <see cref="PartialGroups"/> reports those).</summary>
    static List<string>? ReadGroup(string group)
    {
        var items = SplitTopLevel(group).Where(i => !IsPartialMarker(i)).ToList();
        if (items.Count == 0) return new List<string>();
        var names = items.Select(MemberName).ToList();
        if (names.All(n => n is not null)) return names.Select(n => n!).ToList();
        return names.Any(n => n is not null) ? null : new List<string>();
    }

    /// <summary>Everything in <paramref name="description"/> that this parser can read only PART of — the
    /// silent-drop cases, surfaced, at both levels they occur: braces that do not balance (the group is
    /// never found) and a group whose items stop reading as member names (the group is found but dropped).
    /// Each is reported with what stopped the read, because that is what the author has to fix.
    /// <para>Known cost, accepted: a carrier whose prose carries a LONE brace for some other reason now
    /// fails this arm and has to be reworded. No carrier does today, the report names the carrier and the
    /// character, and the alternative — teaching the brace scan to tell prose braces from list braces —
    /// is guesswork of exactly the kind this guard refuses to do silently. If a real description ever
    /// trips it, that is the moment to refine, not before.</para></summary>
    static List<string> PartialGroups(string description, string carrier)
    {
        var bad = new List<string>();
        var (groups, imbalances) = ScanBraces(description);
        foreach (var im in imbalances)
            bad.Add($"{carrier}: the braces do not balance — {im}. Close it, or drop the stray brace");
        foreach (var group in groups)
        {
            if (ReadGroup(group) is not null) continue;
            var stopper = SplitTopLevel(group).Where(i => !IsPartialMarker(i)).FirstOrDefault(i => MemberName(i) is null);
            bad.Add($"{carrier}: the shape list {{{group.Trim()}}} reads as a member list but this item is not a member name: " +
                    $"'{stopper?.Trim()}'. It is NOT being read — write it as a member name, or close an abbreviated list with '…'");
        }
        return bad;
    }

    /// <summary>An item that says "and more" rather than naming a member: <c>…</c>, <c>...</c> (which
    /// reaches the empty case once the dots are trimmed — that arm is load-bearing, not dead), <c>etc</c>.
    /// Skipped, so the rest of the group is still read. Without this the whole declaration was discarded —
    /// which for a type with several carriers left INV5 green off the others and INV4 never looking at that
    /// carrier's text at all, so a misspelling in it went unseen. That is #341's own failure mode moved
    /// into the docs, and closing an abbreviated list with an ellipsis is the natural way to write one.</summary>
    static bool IsPartialMarker(string item)
    {
        var s = item.Trim().TrimEnd('.').Trim();
        return s is "…" or "" || string.Equals(s, "etc", StringComparison.OrdinalIgnoreCase);
    }

    static List<string> OutermostGroups(string s) => ScanBraces(s).Groups;

    /// <summary>Scan a description's brace structure: the outermost groups, plus every way the braces fail
    /// to balance. The imbalances matter as much as the groups — an unclosed <c>{</c> yields NO group at
    /// all, so the shape list is not merely unread, it is INVISIBLE: <see cref="PartialGroups"/> would
    /// never see it, INV4 would never read that carrier's text, and INV5 would stay green off the type's
    /// other carriers. That is the silent discard one level below the item loop, and it is why this
    /// returns the failures rather than skipping quietly.</summary>
    static (List<string> Groups, List<string> Imbalances) ScanBraces(string s)
    {
        var groups = new List<string>();
        var imbalances = new List<string>();
        int depth = 0, start = -1;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '{') { if (depth++ == 0) start = i + 1; }
            else if (s[i] == '}')
            {
                if (depth == 0) { imbalances.Add($"a '}}' at character {i + 1} closes a group nothing opened"); continue; }
                if (--depth == 0 && start >= 0) groups.Add(s[start..i]);
            }
        }
        if (depth > 0)
            imbalances.Add($"the '{{' at character {start} is never closed ({depth} level(s) still open at the end) — " +
                           "everything it was meant to declare is invisible to this guard");
        return (groups, imbalances);
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

    /// <summary>The readers the surface binds spec objects with. A wire name has to work in ALL of them:
    /// <c>ListParams.Strict</c> reads apply/create's @file lists, and <c>McpJsonUtilities.DefaultOptions</c>
    /// covers the directly typed arrays the SDK binds (bulk_place_asset's assets=). Both are the REAL
    /// objects, neither reconstructed — which is the point, since a reconstruction drifts from the thing it
    /// copies without anything noticing.
    ///
    /// <para>There were THREE. <c>WriteTools.ManifestJson</c> — a separately configured options object
    /// reading the same spec types down bulk_apply's <c>from_file=</c> lane — went with that tool at the
    /// demolition catch-up (#468). SPEC §5.1 retires <c>from_file</c> into the <c>@file</c> convention, so
    /// its reader has no lane left, and #341's concern (a third options object drifting from the other two
    /// with nothing noticing) is closed by construction rather than guarded.</para>
    ///
    /// <para><b>How far the SDK lane is verified.</b> The object is the SDK's own public singleton, and the
    /// SDK documents <c>McpServerToolCreateOptions.SerializerOptions</c> — the options "used when
    /// marshalling data to/from JSON" — as defaulting to it; <c>Program.cs</c> registers tools with a bare
    /// <c>WithToolsFromAssembly()</c> and never sets that property, so nothing here overrides the default.
    /// What is NOT pinned is that the binder consults THIS instance: for reads it behaves identically to
    /// bare Web defaults (the extras it adds are a write-side ignore condition, AOT source-gen contracts,
    /// and number-reading-from-string, which Web already enables), so no observable behaviour can tell them
    /// apart. Driving the real singleton is as close as the guard can get; the residue is a line on
    /// RUN_ORDER step 5's #329 row — re-verify this lane when the MCP SDK is bumped.</para></summary>
    static readonly (string Lane, JsonSerializerOptions Options)[] Readers =
    {
        ("ListParams.Strict — the @file list reader", ListParams.Strict),
        ("McpJsonUtilities.DefaultOptions — the SDK-bound spec arrays", ModelContextProtocol.McpJsonUtilities.DefaultOptions),
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

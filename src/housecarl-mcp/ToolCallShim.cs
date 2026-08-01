using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>
/// The tool-argument binding shim (HCBR-2026-06-11-01) — a call-tool filter that runs BEFORE the SDK binds a
/// call's JSON arguments to the tool method's parameters, closing the one layer where houseCARL's named-error
/// (Q3) discipline could not reach: a malformed argument SHAPE used to throw inside SDK binding, which the SDK
/// genericizes to "An error occurred invoking '&lt;tool&gt;'." — an opaque dead end the live audit agent could not
/// self-correct from (it abandoned the documented query path; see the bug report's transcripts).
///
/// Its moves, all schema-driven off the tool's own published InputSchema (so every current and future tool
/// parameter is covered by construction — no per-tool wiring):
/// <list type="number">
/// <item><b>Resolve obvious aliases</b> — a parameter named by a known alternate spelling of a declared one is
/// renamed to the canonical parameter, so a first-guess miss BINDS instead of costing a round-trip (#221).
/// Two sources, both conservative by construction (a declared parameter is never treated as an alias, an
/// explicitly-supplied canonical is never clobbered, a well-formed call is byte-identical): the permanent
/// underscore/case normalization bridge (<c>form_id</c> for <c>formid</c>), and — during the 2.0 build only —
/// <see cref="AliasTable"/>, the SPEC §5.3 old → new dictionary that lets each build wave land its renames
/// non-breaking (old spellings bind on renamed tools, new spellings on not-yet-renamed ones). The published
/// schema still advertises only the canonical name.</item>
/// <item><b>Correct retired lane spellings by name</b> — 1.x's <c>in_place=true</c> + <c>target="X.esp"</c>
/// pair, sent to a tool whose 2.0 <c>in_place</c> is the STRING naming the overwritten file (SPEC §5.2), is
/// auto-mapped when complete and answered with a naming correction when bare — never a generic type error.
/// Dormant on every tool whose <c>in_place</c> is still the 1.x bool. See <see cref="LaneCorrections"/>.</item>
/// <item><b>Coerce obvious intent</b> — a bare string where an array is declared becomes a one-element array
/// (the live failing shape: <c>plugins="A.esp"</c>); quoted numbers/booleans become numbers/booleans; a bare
/// number where a string is declared becomes its text. Anything else is left for binding to judge.</item>
/// <item><b>Refuse missing REQUIRED parameters by name</b> — the audit's <c>{}</c> call gets
/// "required parameter missing: formids", not the generic text.</item>
/// <item><b>Name a mistyped parameter</b> — an argument whose JSON kind can't bind to its declared schema type
/// (e.g. an object where a number is declared) is refused BEFORE binding, naming the offender, its expected
/// type, and the kind received (#222) — so the caller never has to bisect a byte-offset binding error across
/// the whole argument list.</item>
/// <item><b>Name what still fails</b> — if binding still throws (an uncoercible shape), the exception passes
/// through this filter on its way to the SDK's catch (which sits ABOVE the filter pipeline and would genericize
/// it — measured: AIFunctionMcpServerTool.InvokeAsync doesn't catch; McpServerImpl's outermost wrapper does).
/// Catching it HERE instead returns a named error carrying the real exception text plus each received
/// argument's JSON kind, so the caller can fix and retry. (With every tool body wrapped in <see cref="Guard"/>,
/// what reaches this catch is the pre-body machinery — argument binding — which is what makes the
/// "could not be bound" wording honest.)</item>
/// </list>
/// </summary>
internal static class ToolCallShim
{
    /// <summary>The filter. Registered on the server in Program.cs via WithRequestFilters → AddCallToolFilter.</summary>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> LenientArguments => next => async (request, cancellationToken) =>
    {
        // MatchedPrimitive is resolved by the SDK BEFORE filters run; unknown tool names pass through untouched
        // (the SDK's own unknown-tool error is already specific).
        var p = request.Params;
        var received = DescribeArgs(p?.Arguments);   // what the caller ACTUALLY sent — captured before coercion rewrites
                                                     // the dictionary, so the failure message never shows a coerced shape
                                                     // as if the caller had sent it (review #1 finding 2)
        try
        {
            // The shim's own pre-processing runs inside the same safety net as the call (review #1 finding 4):
            // a throw from coercion/required-check must also come back named, never the SDK generic.
            if (p is not null && request.MatchedPrimitive is McpServerTool tool)
            {
                var schema = tool.ProtocolTool.InputSchema;
                ResolveAliases(p, schema);
                CoerceObviousShapes(p, schema);
                if (LaneCorrections(p, schema) is { } laneRefusal) return laneRefusal;
                if (MissingRequired(p, schema) is { } refusal) return refusal;
                if (UnknownParameters(p, schema) is { } unknownRefusal) return unknownRefusal;
                if (TypeMismatches(p, schema) is { } typeRefusal) return typeRefusal;
            }
            return await next(request, cancellationToken);
        }
        // A REAL request cancellation belongs to the SDK's special-casing — but ONLY a real one (the SDK's own
        // test): an OperationCanceledException with a live request token (e.g. an internal HttpClient timeout)
        // would be genericized above, so it gets named here instead (review #1 finding 1). McpException is the
        // protocol surface (e.g. the unknown-tool path) whose handling must stay the SDK's. Everything else
        // below this filter would otherwise surface as the opaque generic text (Q3's dead end) — name it.
        catch (Exception ex) when (ex is not McpException &&
                                   !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            Console.Error.WriteLine($"[houseCARL] {p?.Name}: exception during tool invocation: {ex}");   // full stack → stderr (the MCP log), never stdout (the protocol channel)
            return NamedError(
                $"error: {p?.Name}: an argument most likely could not be bound to its declared parameter — " +
                $"{ex.GetType().Name}: {Guard.Flatten(ex.Message)} Received {received}. Check each " +
                "argument's TYPE against the tool's schema: array parameters take JSON arrays (a single bare " +
                "string is auto-wrapped), numbers take numbers, booleans take true/false. Fix the mismatched " +
                "argument and retry.");
        }
    };

    /// <summary>Rename an argument keyed by a known alternate spelling of a declared parameter to that canonical
    /// parameter, so a first-guess miss binds instead of costing a round-trip (#221). Two sources, tried in order:
    /// <list type="number">
    /// <item>the permanent underscore/case <see cref="Normalize"/> bridge (<c>form_id</c> → <c>formid</c>) —
    /// exactly-one-match conservative, as ever (zero or ambiguous → left for <see cref="UnknownParameters"/>);</item>
    /// <item><see cref="AliasTable"/> — the SPEC §5.3 old → new dictionary (2.0 build scaffolding). Its candidates
    /// are tried in the entry's priority order; the FIRST candidate the tool declares decides. If that candidate is
    /// already supplied the entry deliberately stops (no fall-through to a lower-priority candidate — the caller
    /// already used the spelling's primary meaning, and reinterpreting the stray onto a different axis is how a
    /// wrong guess binds silently); the stray is left for <see cref="UnknownParameters"/> to name.</item>
    /// </list>
    /// Conservative by construction either way: only a key the schema does NOT declare is considered, and a declared
    /// parameter is never touched — so a tool's real <c>plugin=</c> stays its own, an explicit canonical value is
    /// never clobbered, and a well-formed call is byte-identical. Runs BEFORE <see cref="CoerceObviousShapes"/> so
    /// the renamed value is then shape-coerced (a bare-string <c>plugin</c> → <c>plugins</c> → a one-element array)
    /// as usual.</summary>
    internal static void ResolveAliases(CallToolRequestParams p, JsonElement schema)
    {
        if (p.Arguments is not { Count: > 0 } args) return;
        if (schema.ValueKind != JsonValueKind.Object) return;
        // Respect an explicit opt-in to extra properties (mirrors UnknownParameters): if a tool accepts free-form
        // args, an undeclared key may be intentional data — never rewrite it. None of houseCARL's tools do today.
        if (schema.TryGetProperty("additionalProperties", out var ap) && ap.ValueKind != JsonValueKind.False) return;
        if (!schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object) return;

        Dictionary<string, JsonElement>? rewritten = null;
        foreach (var kv in args)
        {
            var key = kv.Key;
            if (key.Length > 0 && key[0] == '_') continue;            // MCP/JSON-RPC metadata — never an alias
            if (props.TryGetProperty(key, out _)) continue;           // already a real parameter of this tool

            bool Supplied(string declaredName) => args.ContainsKey(declaredName)              // caller already supplied the canonical — don't clobber
                || (rewritten is not null && rewritten.ContainsKey(declaredName));            // an earlier rename already produced it

            // A rename must never fire into a guaranteed kind mismatch (PR #304 review F5): renaming
            // types=["A","B"] onto a string-typed type= would produce a type error about a key the caller
            // never sent, and lose the supported-parameter list that would have corrected them. An
            // incompatible stray stays put for UnknownParameters to name under the caller's OWN spelling.
            bool CanBind(JsonElement value, JsonElement propSchema)
            {
                var types = DeclaredTypes(propSchema);
                return types.Count == 0                                // untyped/polymorphic — let binding judge
                    || KindSatisfies(value.ValueKind, types)
                    || Coerce(value, propSchema) is not null;          // an obvious-intent shape CoerceObviousShapes will fix
            }

            // Source 1 — the normalization bridge: an underscore/case variant of exactly one declared parameter.
            var nkey = Normalize(key);
            string? target = null; bool ambiguous = false;
            foreach (var prop in props.EnumerateObject())
            {
                if (Normalize(prop.Name) != nkey || Supplied(prop.Name) || !CanBind(kv.Value, prop.Value)) continue;
                if (target is null) target = prop.Name; else { ambiguous = true; break; }
            }

            // Source 2 — the §5.3 table: first declared candidate decides; declared-but-supplied stops the entry.
            if (target is null && !ambiguous && AliasTable.RenameFor(nkey) is { } entry)
            {
                foreach (var candidate in entry.Candidates)
                {
                    if (AliasTable.IsExcluded(entry, candidate, p.Name)) continue;
                    string? declared = null; JsonElement declaredSchema = default;
                    foreach (var prop in props.EnumerateObject())
                        if (Normalize(prop.Name) == candidate) { declared = prop.Name; declaredSchema = prop.Value; break; }
                    if (declared is null) continue;                    // candidate not on this tool — try the next
                    if (Supplied(declared)) break;                     // primary meaning already in use — stop the entry
                    if (!CanBind(kv.Value, declaredSchema)) continue;  // the value's kind says the caller didn't mean this one — try the next (an array plugin= is the scan scope, not the string pole)
                    target = declared;
                    break;
                }
            }
            if (ambiguous || target is null) continue;                // nothing unambiguous — leave for UnknownParameters

            rewritten ??= new Dictionary<string, JsonElement>(args);
            rewritten.Remove(key);
            rewritten[target] = kv.Value;
        }
        if (rewritten is not null) p.Arguments = rewritten;
    }

    /// <summary>A parameter name reduced to its comparison form: lowercased with underscores removed.</summary>
    static string Normalize(string s) => s.Replace("_", "").ToLowerInvariant();

    /// <summary>Rewrite arguments whose JSON kind mismatches the declared schema type but whose intent is
    /// unambiguous. Only ever REPLACES values for keys the schema declares — unknown keys and already-correct
    /// shapes pass through untouched, so a well-formed call is byte-identical to today.</summary>
    static void CoerceObviousShapes(CallToolRequestParams p, JsonElement schema)
    {
        if (p.Arguments is not { Count: > 0 } args) return;
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object) return;

        Dictionary<string, JsonElement>? rewritten = null;
        foreach (var kv in args)
        {
            if (!props.TryGetProperty(kv.Key, out var propSchema)) continue;
            if (Coerce(kv.Value, propSchema) is { } coerced)
            {
                rewritten ??= new Dictionary<string, JsonElement>(args);
                rewritten[kv.Key] = coerced;
            }
        }
        if (rewritten is not null) p.Arguments = rewritten;
    }

    /// <summary>One value against one property schema: the coerced element, or null to leave it alone.</summary>
    static JsonElement? Coerce(JsonElement value, JsonElement propSchema)
    {
        var declared = DeclaredTypes(propSchema);
        if (declared.Count == 0) return null;

        if (value.ValueKind == JsonValueKind.String)
        {
            var s = value.GetString() ?? "";
            if (declared.Contains("array"))
            {
                // A string-ENCODED JSON array first — the verified live Claude Code shape (#36): the client
                // serializes array arguments into a JSON STRING ("[\"a\",\"b\"]") even though the published
                // schema correctly declares the array. Parse it as the array it spells; only an unambiguous
                // parse is taken — anything else (including a bare string that merely starts with '[') falls
                // through to the one-element wrap below, so no previously-working shape changes meaning.
                var t = s.TrimStart();
                if (t.StartsWith('['))
                {
                    try { var el = Parse(s); if (el.ValueKind == JsonValueKind.Array) return el; }
                    catch (JsonException) { /* not a JSON array after all — fall through to the wrap */ }
                }
                return Parse("[" + JsonSerializer.Serialize(s) + "]");          // "A.esp" → ["A.esp"] — the bare-string shape
            }
            if (declared.Contains("boolean") && bool.TryParse(s, out var b))
                return Parse(b ? "true" : "false");                             // "true" → true
            if (declared.Contains("integer") || declared.Contains("number"))
            {
                // "100" → 100. Parse validates it's a standalone JSON number; anything else stays for binding to name.
                try { var el = Parse(s); if (el.ValueKind == JsonValueKind.Number) return el; }
                catch (JsonException) { }
            }
        }
        else if (value.ValueKind == JsonValueKind.Number &&
                 declared.Contains("string") && !declared.Contains("number") && !declared.Contains("integer"))
        {
            return Parse(JsonSerializer.Serialize(value.GetRawText()));         // 123456 → "123456" (e.g. an unquoted hex-free FormID)
        }
        return null;
    }

    /// <summary>The schema's declared type name(s) for a property — handles both <c>"type":"array"</c> and the
    /// nullable-parameter form <c>"type":["array","null"]</c> the schema exporter emits.</summary>
    static HashSet<string> DeclaredTypes(JsonElement propSchema)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (propSchema.ValueKind == JsonValueKind.Object && propSchema.TryGetProperty("type", out var t))
        {
            if (t.ValueKind == JsonValueKind.String) set.Add(t.GetString()!);
            else if (t.ValueKind == JsonValueKind.Array)
                foreach (var e in t.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String) set.Add(e.GetString()!);
        }
        return set;
    }

    /// <summary>The 1.x → 2.0 lane-spelling correction (SPEC §5.2(1); 2.0 build scaffolding like
    /// <see cref="AliasTable"/>): on a tool whose <c>in_place</c> is the 2.0 STRING (the name of the file being
    /// overwritten), model priors and 1.x callers WILL still send the old bool spelling — <c>in_place=true</c>,
    /// usually paired with <c>target="X.esp"</c>. A bool against a string declaration would otherwise fall to
    /// <see cref="TypeMismatches"/>' generic wording (and the stray <c>target</c> to <see cref="UnknownParameters"/>),
    /// costing round-trips the §5.2 charter says this exact shape must not cost:
    /// <list type="bullet">
    /// <item>the COMPLETE old pair (<c>in_place=true</c> + a string <c>target</c> the tool does not declare) is
    /// auto-mapped — <c>in_place := target's value</c>, <c>target</c> dropped — so old callers keep WORKING;</item>
    /// <item>a BARE <c>in_place=true</c> is refused with the naming correction ("name the file:
    /// in_place=\"X.esp\""), never a type error;</item>
    /// <item>a bare <c>in_place=false</c> meant (and still means) the default lane — the key is dropped;
    /// <c>false</c> WITH a stray <c>target</c> is contradictory and refused by name, not guessed at;</item>
    /// <item>a stray <c>target="X.esp"</c> with NO <c>in_place</c> at all (PR #304 review F2) is refused
    /// with the same naming correction — it must NEVER be silently renamed onto <c>in_place</c>, because
    /// that would engage the opt-in overwrite lane from a call that never spelled it (1.x refuses this
    /// exact shape too: "target= is only meaningful with in_place=true").</item>
    /// </list>
    /// The quoted spellings <c>"true"</c>/<c>"false"</c> are treated identically (a file literally named "true"
    /// does not exist; binding it silently would be the Q3 sin). DORMANT on every tool whose <c>in_place</c> is
    /// still the 1.x bool (declared boolean → the whole pass is a no-op), so today's surface is byte-identical.
    /// Runs after <see cref="ResolveAliases"/>/<see cref="CoerceObviousShapes"/> (which never produce these shapes:
    /// <c>target</c> does not rename onto a supplied <c>in_place</c>, and string-declared values are not coerced)
    /// and BEFORE the refusal passes, so the correction outranks the generic messages.</summary>
    internal static CallToolResult? LaneCorrections(CallToolRequestParams p, JsonElement schema)
    {
        if (p.Arguments is not { Count: > 0 } args) return null;
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object) return null;

        // The pass concerns exactly one declared parameter: a STRING-typed in_place (the 2.0 spelling).
        if (!props.TryGetProperty("in_place", out var inPlaceSchema)) return null;
        var declared = DeclaredTypes(inPlaceSchema);
        if (!declared.Contains("string") || declared.Contains("boolean")) return null;   // 1.x bool (or polymorphic) → dormant

        // The old pair's target= — only a stray (undeclared) key counts; a tool's own target is its own.
        bool hasStrayTargetKey = args.ContainsKey("target") && !props.TryGetProperty("target", out _);

        if (!args.TryGetValue("in_place", out var val))
        {
            // No in_place at all: a stray target= alone is 1.x's half of the pair — refuse with the naming
            // correction rather than let it near the lane (or fall to a plain unknown that teaches nothing).
            if (!hasStrayTargetKey) return null;
            return NamedError(
                $"error: {p.Name}: target= was 1.x's in-place spelling and does not select the lane by itself — " +
                "the in-place overwrite lane is spelled in_place=\"X.esp\" (naming the file you intend to " +
                "overwrite). Omit it entirely for the default new-patch lane. Fix the arguments and retry.");
        }
        bool? boolish = val.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(val.GetString(), out var b) => b,
            _ => null,
        };
        if (boolish is null) return null;                                                // a real file name (or another mistake) — not this pass's

        // The stray target='s value — a string names the file; anything else stays null (correction path).
        string? targetFile = null;
        if (hasStrayTargetKey && args.TryGetValue("target", out var tv) && tv.ValueKind == JsonValueKind.String)
            targetFile = tv.GetString();

        if (boolish == true && targetFile is { Length: > 0 })
        {
            var rewritten = new Dictionary<string, JsonElement>(args);
            rewritten["in_place"] = Parse(JsonSerializer.Serialize(targetFile));         // the complete 1.x pair → the 2.0 spelling
            rewritten.Remove("target");
            p.Arguments = rewritten;
            return null;
        }
        if (boolish == false && !hasStrayTargetKey)
        {
            var rewritten = new Dictionary<string, JsonElement>(args);                   // 1.x "default lane" spelling → absent, the 2.0 default
            rewritten.Remove("in_place");
            p.Arguments = rewritten;
            return null;
        }
        return NamedError(boolish == true
            ? $"error: {p.Name}: in_place names the FILE being overwritten — name the file: in_place=\"X.esp\". " +
              "(1.x's in_place=true + target=\"X.esp\" became in_place=\"X.esp\"; omit in_place entirely for the " +
              "default new-patch lane.) Fix the argument and retry."
            : $"error: {p.Name}: in_place=false alongside target= is contradictory — 1.x's in_place=false meant " +
              "the default new-patch lane, which ignores target. Either omit both (default lane) or name the file " +
              "to overwrite: in_place=\"X.esp\". Fix the arguments and retry.");
    }

    /// <summary>Schema-required parameters absent from the call → a named refusal (Q3), or null to proceed.
    /// An EXPLICIT JSON <c>null</c> for a required parameter counts as missing too (unless the schema itself declares
    /// null legal): the SDK binds it and the tool body NullReferences into the generic "internal houseCARL failure"
    /// misdirection (2026-06-12 hunt, proven over stdio on nexus_mod) — name the parameter instead.</summary>
    static CallToolResult? MissingRequired(CallToolRequestParams p, JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("required", out var req) || req.ValueKind != JsonValueKind.Array) return null;

        schema.TryGetProperty("properties", out var props);

        List<string>? missing = null;
        foreach (var r in req.EnumerateArray())
        {
            if (r.GetString() is not { } name) continue;
            if (p.Arguments is null || !p.Arguments.TryGetValue(name, out var val))
                { (missing ??= new List<string>()).Add(name); continue; }
            if (val.ValueKind == JsonValueKind.Null
                && !(props.ValueKind == JsonValueKind.Object && props.TryGetProperty(name, out var ps) && DeclaredTypes(ps).Contains("null")))
                (missing ??= new List<string>()).Add(name + " (was explicit null)");
        }
        if (missing is null) return null;

        string plural = missing.Count == 1 ? "" : "s";
        return NamedError(
            $"error: {p.Name}: required parameter{plural} missing: {string.Join(", ", missing)}. Supplied: " +
            $"{(p.Arguments is { Count: > 0 } a ? string.Join(", ", a.Keys) : "(none)")}. Add the missing argument{plural} and retry.");
    }

    /// <summary>Schema-UNDECLARED arguments in the call → a named refusal listing the offenders and the tool's
    /// supported parameters, or null to proceed. THE root-cause fix for HCBR-2026-07-12: an argument a tool does
    /// not declare (<c>expand=</c>, <c>path=</c>, <c>field=</c>) is SILENTLY IGNORED by the SDK binder, so the
    /// call runs with that intent dropped and no correction reaches the caller — the agent, getting a normal
    /// (un-expanded) result, concluded the capability was missing and hand-rolled a workaround instead of
    /// discovering the real knob (<c>depth=</c>). A silently-ignored argument is a silent tool-surface failure
    /// (Q3); naming it — with the supported list — turns the dead end into a self-correction, exactly as
    /// <see cref="MissingRequired"/> does for the absent-required case. Schema-driven off the tool's own
    /// InputSchema, so every current and future parameter is covered by construction. Skipped when a tool's schema
    /// opts into free-form args (<c>additionalProperties</c> not <c>false</c>); none of houseCARL's tools do today,
    /// but the check does not assume it. Runs AFTER <see cref="CoerceObviousShapes"/> (which only ever rewrites
    /// DECLARED keys) and <see cref="MissingRequired"/>, so a well-formed call is byte-identical to before.</summary>
    internal static CallToolResult? UnknownParameters(CallToolRequestParams p, JsonElement schema)
    {
        if (p.Arguments is not { Count: > 0 } args) return null;
        if (schema.ValueKind != JsonValueKind.Object) return null;
        // Respect an explicit opt-in to extra properties: only reject when additionalProperties is absent or false.
        if (schema.TryGetProperty("additionalProperties", out var ap) && ap.ValueKind != JsonValueKind.False) return null;
        if (!schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object) return null;

        // For the §5.3 ⤳ dissolution hints: the declared names, normalized — the gate that scopes each hint to
        // tools whose build wave has actually landed the replacement grammar (see AliasTable.Dissolutions).
        var declaredNormalized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in props.EnumerateObject()) declaredNormalized.Add(Normalize(prop.Name));

        List<string>? unknown = null;
        foreach (var kv in args)
        {
            if (kv.Key.Length > 0 && kv.Key[0] == '_') continue;   // MCP/JSON-RPC metadata convention — never a real tool param
            if (props.TryGetProperty(kv.Key, out _)) continue;
            var hint = AliasTable.DissolutionHint(Normalize(kv.Key), declaredNormalized);
            (unknown ??= new()).Add(hint is null ? kv.Key : $"{kv.Key} ({hint})");
        }
        if (unknown is null) return null;

        var supported = props.EnumerateObject().Select(prop => prop.Name).ToList();
        string plural = unknown.Count == 1 ? "" : "s";
        // Only nudge toward depth= on a tool that actually HAS it (the read tools) — a depth-less tool would
        // point at a knob that doesn't exist. The supported list is printed either way, so the nudge is a bonus.
        string knobHint = supported.Contains("depth")
            ? " (a wrong/guessed parameter often means the real knob is one of the above, e.g. depth= to expand a list/substruct)"
            : "";
        return NamedError(
            $"error: {p.Name}: unknown parameter{plural}: {string.Join(", ", unknown)}. This tool accepts only: " +
            $"{string.Join(", ", supported)}. An unrecognized argument is IGNORED (it does not change behavior), so " +
            $"the call would otherwise run with that intent silently dropped — fix the name{knobHint} and retry.");
    }

    /// <summary>Declared arguments whose JSON kind cannot bind to their declared schema type → a named refusal
    /// listing each offender with its expected type(s) and the kind received, or null to proceed. THE fix for
    /// #222: a wrong-TYPE argument (e.g. an object where a number is declared, or a string where a boolean is)
    /// otherwise threw inside the SDK binder as a bare <c>JsonException … Path: $ | BytePositionInLine: 34</c> —
    /// a byte offset with NO parameter name, which the catch below could only pass through with the full received
    /// list, forcing the caller to bisect which argument was wrong. This catches the common kind-mismatch class
    /// BEFORE binding and names it in the same style as <see cref="MissingRequired"/> / <see cref="UnknownParameters"/>.
    /// Runs AFTER <see cref="CoerceObviousShapes"/> (so an obvious-intent shape — a bare string for an array, a
    /// quoted number/bool — is fixed, never flagged) and judges ONLY keys the schema declares with a concrete
    /// type: an untyped/polymorphic property (empty <see cref="DeclaredTypes"/>) is left for binding, an unknown
    /// key is <see cref="UnknownParameters"/>' to report, and an explicit JSON null is left alone (optional-unset
    /// for a non-required parameter; the required-null case is <see cref="MissingRequired"/>'s). So a well-formed
    /// call is byte-identical to before, and anything this can't foresee (e.g. a non-integral number for an
    /// integer parameter) still falls to the named catch.</summary>
    static CallToolResult? TypeMismatches(CallToolRequestParams p, JsonElement schema)
    {
        if (p.Arguments is not { Count: > 0 } args) return null;
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object) return null;

        List<string>? bad = null;
        foreach (var kv in args)
        {
            if (kv.Value.ValueKind == JsonValueKind.Null) continue;          // null = optional-unset; the required-null case is MissingRequired's
            if (!props.TryGetProperty(kv.Key, out var propSchema)) continue; // an unknown key is UnknownParameters' to report
            var declared = DeclaredTypes(propSchema);
            if (declared.Count == 0) continue;                              // untyped/polymorphic — leave for binding to judge
            if (KindSatisfies(kv.Value.ValueKind, declared)) continue;
            (bad ??= new()).Add(
                $"{kv.Key} (expects {string.Join(" or ", declared.Where(t => t != "null"))}, received {KindName(kv.Value.ValueKind)})");
        }
        if (bad is null) return null;

        string plural = bad.Count == 1 ? "" : "s";
        return NamedError(
            $"error: {p.Name}: parameter{plural} whose type could not be bound: {string.Join("; ", bad)}. " +
            "Fix the argument's TYPE to match the schema (array parameters take JSON arrays — a single bare string " +
            "is auto-wrapped; numbers take numbers; booleans take true/false) and retry.");
    }

    /// <summary>Whether a JSON kind can bind to at least one of a property's declared schema types. A JSON number
    /// satisfies both "number" and "integer" (an integral check is the binder's, not ours); every other kind maps
    /// to its one schema type. Null never reaches here (filtered by the caller).</summary>
    static bool KindSatisfies(JsonValueKind kind, HashSet<string> declared) => kind switch
    {
        JsonValueKind.String => declared.Contains("string"),
        JsonValueKind.Number => declared.Contains("number") || declared.Contains("integer"),
        JsonValueKind.True or JsonValueKind.False => declared.Contains("boolean"),
        JsonValueKind.Array => declared.Contains("array"),
        JsonValueKind.Object => declared.Contains("object"),
        _ => true,   // an unexpected kind — don't presume a mismatch; let binding judge
    };

    static CallToolResult NamedError(string text) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = text }],
    };

    /// <summary>Each received argument's name + JSON kind ("plugins=string, limit=object") — exactly the view a
    /// caller needs to spot which argument's shape disagrees with the schema.</summary>
    static string DescribeArgs(IDictionary<string, JsonElement>? args)
        => args is not { Count: > 0 }
            ? "(no arguments)"
            : string.Join(", ", args.Select(kv => $"{kv.Key}={KindName(kv.Value.ValueKind)}"));

    static string KindName(JsonValueKind k) => k switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        JsonValueKind.Null => "null",
        _ => k.ToString().ToLowerInvariant(),
    };

    /// <summary>A detached element from raw JSON text (no serializer reflection; valid past the document's lifetime).</summary>
    static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

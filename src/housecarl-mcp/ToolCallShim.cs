using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>
/// A call-tool filter that runs BEFORE the SDK binds a call's JSON arguments to the tool method's parameters.
/// Without it a malformed argument shape throws inside SDK binding and the SDK genericizes it to
/// "An error occurred invoking '&lt;tool&gt;'." — an opaque dead end a caller cannot self-correct from.
///
/// Every pass is driven off the tool's own published InputSchema, so all current and future parameters are
/// covered without per-tool wiring. In order: rename an alias to the declared parameter it names; correct the
/// retired in-place lane spellings; coerce an unambiguous shape (a bare string for an array, a quoted
/// number/bool); refuse a missing required parameter, an undeclared one, or a kind that cannot bind — each by
/// name. Anything that still throws is caught here rather than above, where the SDK would genericize it.
/// </summary>
internal static class ToolCallShim
{
    /// <summary>The filter. Registered on the server in Program.cs via WithRequestFilters → AddCallToolFilter.</summary>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> LenientArguments => next => async (request, cancellationToken) =>
    {
        // MatchedPrimitive is resolved by the SDK before filters run, so this check only ever sees a name the
        // server does not register — a retired one answers with its successor call shape.
        var p = request.Params;
        if (request.MatchedPrimitive is not McpServerTool && AliasTable.RetiredToolHint(p?.Name) is { } retiredRedirect)
            return NamedError(retiredRedirect);
        var received = DescribeArgs(p?.Arguments);   // captured before coercion rewrites the dictionary, so a
                                                     // failure never reports a coerced shape as the caller's own
        try
        {
            // The pre-processing runs inside the same try as the call: a throw from coercion or a refusal pass
            // must also come back named, never as the SDK generic.
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
        // A real request cancellation stays the SDK's, and so does McpException (the protocol surface). But an
        // OperationCanceledException with a live request token — an internal HttpClient timeout, say — is not a
        // cancellation, so it gets named here rather than genericized above.
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

    /// <summary>Rename an argument keyed by an alternate spelling of a declared parameter to the canonical one, so
    /// a first-guess miss binds instead of costing a round-trip. Two sources in order: the underscore/case
    /// <see cref="Normalize"/> bridge (exactly one match, else left alone), then <see cref="AliasTable"/>, whose
    /// first declared candidate decides. Only a key the schema does NOT declare is ever considered and an
    /// explicitly supplied canonical is never clobbered, so a well-formed call is byte-identical. Must run before
    /// <see cref="CoerceObviousShapes"/> so the renamed value is still shape-coerced.</summary>
    internal static void ResolveAliases(CallToolRequestParams p, JsonElement schema)
    {
        if (p.Arguments is not { Count: > 0 } args) return;
        if (schema.ValueKind != JsonValueKind.Object) return;
        // If a tool opts into free-form args, an undeclared key may be intentional data — never rewrite it.
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

            // A table rename must never fire into a guaranteed kind mismatch: it would report a type error
            // about a key the caller never sent. An incompatible stray stays put under the caller's own
            // spelling. The bridge is deliberately NOT gated this way — it names the right parameter, so the
            // rename proceeds even for an unbindable value and TypeMismatches names the real fault.
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
                if (Normalize(prop.Name) != nkey || Supplied(prop.Name)) continue;
                if (target is null) target = prop.Name; else { ambiguous = true; break; }
            }

            // Source 2 — the table: first declared candidate decides; declared-but-supplied stops the entry.
            if (target is null && !ambiguous && AliasTable.RenameFor(nkey) is { } entry)
            {
                foreach (var candidate in entry.Candidates)
                {
                    if (AliasTable.IsExcluded(entry, candidate, p.Name)) continue;
                    string? declared = null; JsonElement declaredSchema = default;
                    foreach (var prop in props.EnumerateObject())
                        if (Normalize(prop.Name) == candidate) { declared = prop.Name; declaredSchema = prop.Value; break; }
                    if (declared is null) continue;                    // candidate not on this tool — try the next
                    if (!CanBind(kv.Value, declaredSchema)) continue;  // wrong kind for this candidate — judged BEFORE the supplied-stop, so a candidate that couldn't take the value never stops the entry
                    if (Supplied(declared)) break;                     // primary (kind-compatible) meaning already in use — stop the entry
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
    internal static string Normalize(string s) => s.Replace("_", "").ToLowerInvariant();

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

    /// <summary>One value against one property schema: the coerced element, or null to leave it alone. Internal
    /// rather than private so a test can assert the coerced value directly — over the wire only "it bound" is
    /// observable, which a coercion that dropped the value would also satisfy.</summary>
    internal static JsonElement? Coerce(JsonElement value, JsonElement propSchema)
    {
        var declared = DeclaredTypes(propSchema);
        if (declared.Count == 0) return null;

        if (value.ValueKind == JsonValueKind.String)
        {
            var s = value.GetString() ?? "";
            if (declared.Contains("array"))
            {
                // A string-encoded JSON array first: clients do serialize array arguments into a JSON string
                // ("[\"a\",\"b\"]") despite the schema declaring an array. Only an unambiguous parse is taken;
                // anything else, including a bare string starting with '[', falls through to the wrap below.
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

    /// <summary>Correct the old in-place lane spelling on a tool whose <c>in_place</c> is the string naming the
    /// file being overwritten. The complete old pair (<c>in_place=true</c> plus an undeclared string
    /// <c>target</c>) is auto-mapped; a bare <c>in_place=true</c>, or a stray <c>target</c> with no
    /// <c>in_place</c>, is refused with a naming correction — never silently renamed, since that would engage the
    /// opt-in overwrite lane from a call that never spelled it. A bare <c>in_place=false</c> is the default lane
    /// and drops; <c>false</c> with a <c>target</c> is contradictory and refused. Quoted <c>"true"</c>/
    /// <c>"false"</c> count as the bools, never as a filename. Dormant where <c>in_place</c> is still a bool.
    /// Must run before the refusal passes so the correction outranks their generic wording.</summary>
    internal static CallToolResult? LaneCorrections(CallToolRequestParams p, JsonElement schema)
    {
        if (p.Arguments is not { Count: > 0 } args) return null;
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object) return null;

        // The pass concerns exactly one declared parameter: a string-typed in_place.
        if (!props.TryGetProperty("in_place", out var inPlaceSchema)) return null;
        var declared = DeclaredTypes(inPlaceSchema);
        if (!declared.Contains("string") || declared.Contains("boolean")) return null;   // bool (or polymorphic) → dormant

        // Only a stray (undeclared) target= counts; a tool that declares its own target keeps it.
        bool hasStrayTargetKey = args.ContainsKey("target") && !props.TryGetProperty("target", out _);

        if (!args.TryGetValue("in_place", out var val))
        {
            // No in_place at all: a stray target= is half the old pair — refuse with the naming correction
            // rather than let it near the lane.
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

        // A string target= names the file; anything else stays null and takes the correction path.
        string? targetFile = null;
        if (hasStrayTargetKey && args.TryGetValue("target", out var tv) && tv.ValueKind == JsonValueKind.String)
            targetFile = tv.GetString();

        if (boolish == true && targetFile is { Length: > 0 })
        {
            var rewritten = new Dictionary<string, JsonElement>(args);
            rewritten["in_place"] = Parse(JsonSerializer.Serialize(targetFile));         // the complete old pair → the current spelling
            rewritten.Remove("target");
            p.Arguments = rewritten;
            return null;
        }
        if (boolish == false && !hasStrayTargetKey)
        {
            var rewritten = new Dictionary<string, JsonElement>(args);                   // the old default-lane spelling → absent, which is the default
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

    /// <summary>Schema-required parameters absent from the call get a named refusal; null to proceed. An explicit
    /// JSON <c>null</c> counts as missing unless the schema declares null legal, because the SDK binds it and the
    /// tool body then NullReferences into a misleading internal-failure message.</summary>
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

    /// <summary>Undeclared arguments get a named refusal listing the offenders and the tool's supported
    /// parameters; null to proceed. Without it the SDK binder silently ignores an undeclared argument, so the
    /// call runs with that intent dropped and nothing tells the caller. Skipped when a tool's schema opts into
    /// free-form args. Must run after <see cref="CoerceObviousShapes"/>, which only rewrites declared
    /// keys.</summary>
    internal static CallToolResult? UnknownParameters(CallToolRequestParams p, JsonElement schema)
    {
        if (p.Arguments is not { Count: > 0 } args) return null;
        if (schema.ValueKind != JsonValueKind.Object) return null;
        // Respect an explicit opt-in to extra properties: only reject when additionalProperties is absent or false.
        if (schema.TryGetProperty("additionalProperties", out var ap) && ap.ValueKind != JsonValueKind.False) return null;
        if (!schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object) return null;

        // The declared names, normalized: the gate that scopes each migration hint to tools that actually
        // carry the replacement grammar (see AliasTable.Dissolutions).
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
        // Only nudge toward depth= on a tool that declares it; the supported list is printed either way.
        string knobHint = supported.Contains("depth")
            ? " (a wrong/guessed parameter often means the real knob is one of the above, e.g. depth= to expand a list/substruct)"
            : "";
        return NamedError(
            $"error: {p.Name}: unknown parameter{plural}: {string.Join(", ", unknown)}. This tool accepts only: " +
            $"{string.Join(", ", supported)}. An unrecognized argument is IGNORED (it does not change behavior), so " +
            $"the call would otherwise run with that intent silently dropped — fix the name{knobHint} and retry.");
    }

    /// <summary>Declared arguments whose JSON kind cannot bind to their declared schema type get a named refusal
    /// naming each offender, its expected types and the kind received; null to proceed. The SDK binder would
    /// otherwise throw a JsonException carrying a byte offset and no parameter name. Must run after
    /// <see cref="CoerceObviousShapes"/> so an obvious-intent shape is fixed rather than flagged, and judges only
    /// keys declared with a concrete type — untyped properties are left for binding, unknown keys are
    /// <see cref="UnknownParameters"/>' and an explicit null is <see cref="MissingRequired"/>'s.</summary>
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

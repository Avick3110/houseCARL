using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>A call-tool filter that renames, coerces and refuses a call's arguments off the tool's published schema
/// before the SDK binds them; pass order and per-pass contracts in
/// <c>docs/architecture/tool-call-argument-shim.md</c>.</summary>
internal static class ToolCallShim
{
    /// <summary>The filter. Registered on the server in Program.cs via WithRequestFilters → AddCallToolFilter.</summary>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> LenientArguments => next => async (request, cancellationToken) =>
    {
        // MatchedPrimitive is resolved before filters run, so this only ever sees a name the server does not register.
        var p = request.Params;
        if (request.MatchedPrimitive is not McpServerTool && AliasTable.RetiredToolHint(p?.Name) is { } retiredRedirect)
            return NamedError(retiredRedirect);
        var received = DescribeArgs(p?.Arguments);   // captured before coercion rewrites the dictionary, so a
                                                     // failure never reports a coerced shape as the caller's own
        try
        {
            // The passes run inside the same try as the call, so a throw from one also comes back named.
            if (p is not null && request.MatchedPrimitive is McpServerTool tool)
            {
                var schema = tool.ProtocolTool.InputSchema;
                ResolveAliases(p, schema);
                CoerceObviousShapes(p, schema);
                if (MissingRequired(p, schema) is { } refusal) return refusal;
                if (UnknownParameters(p, schema) is { } unknownRefusal) return unknownRefusal;
                if (InPlaceNamesAFile(p, schema) is { } laneRefusal) return laneRefusal;
                if (TypeMismatches(p, schema) is { } typeRefusal) return typeRefusal;
            }
            return await next(request, cancellationToken);
        }
        // A real cancellation and McpException stay the SDK's; a cancel with a live token is named here.
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

    /// <summary>Rename an argument keyed by an underscore/case variant of a declared parameter to the canonical
    /// spelling, on exactly one declared match; must run before <see cref="CoerceObviousShapes"/>.</summary>
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

            // Not kind-gated: the rename proceeds even for an unbindable value, and TypeMismatches names the fault.
            var nkey = Normalize(key);
            string? target = null; bool ambiguous = false;
            foreach (var prop in props.EnumerateObject())
            {
                if (Normalize(prop.Name) != nkey || Supplied(prop.Name)) continue;
                if (target is null) target = prop.Name; else { ambiguous = true; break; }
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

    /// <summary>Rewrite arguments whose JSON kind mismatches the declared schema type but whose intent is unambiguous;
    /// only keys the schema declares are ever replaced.</summary>
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

    /// <summary>One value against one property schema: the coerced element, or null to leave it alone. Internal so a
    /// test can assert the coerced value directly — over the wire only "it bound" is observable.</summary>
    internal static JsonElement? Coerce(JsonElement value, JsonElement propSchema)
    {
        var declared = DeclaredTypes(propSchema);
        if (declared.Count == 0) return null;

        if (value.ValueKind == JsonValueKind.String)
        {
            var s = value.GetString() ?? "";
            // A parameter declaring both string and array takes the string as it stands.
            if (declared.Contains("array") && !declared.Contains("string"))
            {
                // A string-encoded JSON array first; only an unambiguous parse is taken, else it falls to the wrap.
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
                // "100" → 100, only when Parse says it is a standalone JSON number.
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

    /// <summary>The schema's declared type name(s) for a property, in both the string and array spellings.</summary>
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

    /// <summary>Schema-required parameters absent from the call get a named refusal; null to proceed. An explicit JSON
    /// null counts as missing unless the schema declares null legal, and a refusal also names any undeclared keys.</summary>
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

        // The undeclared keys of the same call, named here rather than left to a pass that will not run.
        string strays = "";
        if (Undeclared(p, schema) is { } u)
            strays = $"{string.Join(", ", u.Unknown)} " +
                     $"{(u.Unknown.Count == 1 ? "is not a parameter" : "are not parameters")} of {p.Name} " +
                     $"(it accepts only: {string.Join(", ", u.Supported)}). ";

        string plural = missing.Count == 1 ? "" : "s";
        return NamedError(
            $"error: {p.Name}: required parameter{plural} missing: {string.Join(", ", missing)}. Supplied: " +
            $"{(p.Arguments is { Count: > 0 } a ? string.Join(", ", a.Keys) : "(none)")}. {strays}" +
            $"Add the missing argument{plural} and retry.");
    }

    /// <summary>Undeclared arguments get a named refusal listing the offenders and the supported parameters; null to
    /// proceed. Skipped for a free-form schema, and must run after <see cref="CoerceObviousShapes"/>.</summary>
    internal static CallToolResult? UnknownParameters(CallToolRequestParams p, JsonElement schema)
    {
        if (Undeclared(p, schema) is not { } u) return null;
        var (unknown, supported) = u;
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

    /// <summary>The call's arguments the schema does not declare, with the tool's supported parameter names; null when
    /// there are none or the tool opts into free-form args.</summary>
    static (List<string> Unknown, List<string> Supported)? Undeclared(CallToolRequestParams p, JsonElement schema)
    {
        if (p.Arguments is not { Count: > 0 } args) return null;
        if (schema.ValueKind != JsonValueKind.Object) return null;
        if (schema.TryGetProperty("additionalProperties", out var ap) && ap.ValueKind != JsonValueKind.False) return null;
        if (!schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object) return null;

        List<string>? unknown = null;
        foreach (var kv in args)
        {
            if (kv.Key.Length > 0 && kv.Key[0] == '_') continue;   // MCP/JSON-RPC metadata convention — never a real tool param
            if (props.TryGetProperty(kv.Key, out _)) continue;
            (unknown ??= new()).Add(kv.Key);
        }
        if (unknown is null) return null;
        return (unknown, props.EnumerateObject().Select(prop => prop.Name).ToList());
    }

    /// <summary>An <c>in_place=</c> spelling a boolean, quoted or bare, gets a named refusal; null to proceed. The
    /// parameter takes the filename being overwritten, so neither spelling is a file.</summary>
    static CallToolResult? InPlaceNamesAFile(CallToolRequestParams p, JsonElement schema)
    {
        if (p.Arguments is not { Count: > 0 } args) return null;
        if (!args.TryGetValue("in_place", out var val)) return null;
        var spellsBool = val.ValueKind is JsonValueKind.True or JsonValueKind.False
                      || (val.ValueKind == JsonValueKind.String && bool.TryParse(val.GetString(), out _));
        if (!spellsBool) return null;                                                   // a real filename — not this pass's
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object) return null;
        if (!props.TryGetProperty("in_place", out var inPlaceSchema)) return null;
        var declared = DeclaredTypes(inPlaceSchema);
        if (!declared.Contains("string") || declared.Contains("boolean")) return null;   // not the filename-valued shape

        var spelled = val.ValueKind == JsonValueKind.String ? $"\"{val.GetString()}\"" : val.GetRawText();
        return NamedError(
            $"error: {p.Name}: in_place={spelled} names no file — in_place takes the FILENAME being " +
            "overwritten (in_place=\"X.esp\"), and \"true\"/\"false\" are not files. Omit in_place entirely for the " +
            "default new-patch lane. Fix the argument and retry.");
    }

    /// <summary>Declared arguments whose JSON kind cannot bind get a named refusal naming each offender, its expected
    /// types and the kind received; null to proceed. Judges only keys declared with a concrete type, and must run
    /// after <see cref="CoerceObviousShapes"/>.</summary>
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

    /// <summary>Whether a JSON kind can bind to at least one of a property's declared schema types; null never reaches
    /// here, and a JSON number satisfies both "number" and "integer" — the integral check is the binder's, not ours.</summary>
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

    /// <summary>Each received argument's name and JSON kind ("plugins=string, limit=object").</summary>
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

    /// <summary>A detached element from raw JSON text, valid past the document's lifetime.</summary>
    static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

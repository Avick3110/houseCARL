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
/// Three moves, all schema-driven off the tool's own published InputSchema (so every current and future tool
/// parameter is covered by construction — no per-tool wiring):
/// <list type="number">
/// <item><b>Coerce obvious intent</b> — a bare string where an array is declared becomes a one-element array
/// (the live failing shape: <c>plugins="A.esp"</c>); quoted numbers/booleans become numbers/booleans; a bare
/// number where a string is declared becomes its text. Anything else is left for binding to judge.</item>
/// <item><b>Refuse missing REQUIRED parameters by name</b> — the audit's <c>{}</c> call gets
/// "required parameter missing: formids", not the generic text.</item>
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
                CoerceObviousShapes(p, schema);
                if (MissingRequired(p, schema) is { } refusal) return refusal;
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
                return Parse("[" + JsonSerializer.Serialize(s) + "]");          // "A.esp" → ["A.esp"] — THE live failing shape
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

    /// <summary>Schema-required parameters absent from the call → a named refusal (Q3), or null to proceed.</summary>
    static CallToolResult? MissingRequired(CallToolRequestParams p, JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("required", out var req) || req.ValueKind != JsonValueKind.Array) return null;

        List<string>? missing = null;
        foreach (var r in req.EnumerateArray())
            if (r.GetString() is { } name && (p.Arguments is null || !p.Arguments.ContainsKey(name)))
                (missing ??= new List<string>()).Add(name);
        if (missing is null) return null;

        string plural = missing.Count == 1 ? "" : "s";
        return NamedError(
            $"error: {p.Name}: required parameter{plural} missing: {string.Join(", ", missing)}. Supplied: " +
            $"{(p.Arguments is { Count: > 0 } a ? string.Join(", ", a.Keys) : "(none)")}. Add the missing argument{plural} and retry.");
    }

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

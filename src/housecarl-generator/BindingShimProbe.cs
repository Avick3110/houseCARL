using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace HousecarlGenerator;

// ======================================================================
//  BindingShimProbe — SELF-CONTAINED CI regression guard for the tool-argument
//  binding shim (HCBR-2026-06-11-01).
//
//  THE BUG: the MCP SDK deserializes a call's JSON arguments into the tool
//  method's parameters BEFORE any houseCARL code runs. An argument whose
//  SHAPE doesn't match the declared parameter — the live case was plugins=
//  sent as a bare string where string[] is declared — threw inside that
//  binding layer, and the SDK genericizes any such throw to
//  "An error occurred invoking '<tool>'." — an opaque dead end the calling
//  agent can't self-correct from (it abandoned the documented query path).
//
//  THE FIX (ToolCallShim in housecarl-mcp): a call-tool filter that
//    (1) coerces obvious-intent shapes against the tool's own input schema
//        (string → [string] where an array is declared; quoted numbers/bools),
//    (2) refuses missing REQUIRED parameters with a named error, and
//    (3) rewrites the SDK's generic binding-failure text into a named,
//        actionable message when binding still fails.
//
//  THE GUARD: drives the REAL housecarl-mcp.exe over stdio (the exact wire
//  path the live failure took) with the exact argument shapes from the bug
//  report. Needs NO game data and NO MO2 instance: argument binding resolves
//  before configuration is consulted, and a deliberately-unconfigured server
//  answers every successfully-bound call with the trained config prompt —
//  which is precisely the "our code RAN" signal the asserts key on.
// ======================================================================
public static class BindingShimProbe
{
    const string GenericError = "An error occurred invoking";          // the SDK's opaque text (measured live, HCBR-2026-06-11-01)
    const string ConfigPrompt = "no Mod Organizer 2 instance configured"; // the unconfigured server's trained prompt = "the tool body ran"

    public static int RunGuard(string[] args)
    {
        Console.WriteLine("[binding-shim-guard] tool-argument binding shim (HCBR-2026-06-11-01)");

        // The mcp exe built alongside this generator (same configuration, sibling project).
        var exe = Path.GetFullPath(AppContext.BaseDirectory.Replace(
            Path.DirectorySeparatorChar + "housecarl-generator" + Path.DirectorySeparatorChar,
            Path.DirectorySeparatorChar + "housecarl-mcp" + Path.DirectorySeparatorChar));
        exe = Path.Combine(exe, "housecarl-mcp.exe");
        if (!File.Exists(exe))
        {
            Console.WriteLine($"FAIL  housecarl-mcp.exe not found at '{exe}' — build the whole solution first.");
            return 1;
        }

        // A fresh, EMPTY data dir ⇒ no houseCARL.user.json ⇒ the server boots unconfigured, deterministically —
        // on a dev box too, where a real user config may sit beside the exe.
        var dataDir = Path.Combine(Path.GetTempPath(), "housecarl-binding-shim-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        int failures = 0;
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.Environment["HOUSECARL_DATA_DIR"] = dataDir;

        using var proc = Process.Start(psi)!;
        proc.ErrorDataReceived += (_, _) => { };                        // server logs ride stderr — drain, ignore
        proc.BeginErrorReadLine();
        var stdin = proc.StandardInput;
        var stdout = proc.StandardOutput;

        try
        {
            // -- handshake ------------------------------------------------------------------
            Rpc(stdin, stdout, 1, "initialize", new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "binding-shim-guard", version = "0" },
            });
            Notify(stdin, "notifications/initialized");

            // -- A: the fix must NOT degrade the published schema — plugins stays a declared array.
            //    (Guards against ever "fixing" this via serializer-options converters, which would
            //    collapse the generated schema and remove the model's shape hint.)
            var tools = Rpc(stdin, stdout, 2, "tools/list", new { });
            string? pluginsSchema = null;
            foreach (var t in tools.GetProperty("tools").EnumerateArray())
                if (t.GetProperty("name").GetString() == "housecarl_cross_plugin_query")
                    pluginsSchema = t.GetProperty("inputSchema").GetProperty("properties").GetProperty("plugins").GetRawText();
            failures += Check("A schema: cross_plugin_query plugins= still declares an array",
                pluginsSchema is not null && pluginsSchema.Contains("array"),
                $"plugins schema = {pluginsSchema ?? "<tool or property not found>"}");

            // -- B: THE live failing shape — plugins as a bare string. Must bind (coerced to a
            //    one-element array) and reach the tool body (⇒ the config prompt), never the generic error.
            var b = Call(stdin, stdout, 3, "housecarl_cross_plugin_query",
                """{"type":"CELL","plugins":"Synthetic.esp"}""");
            failures += Check("B coerce: plugins as bare string binds and runs the tool body",
                !b.text.Contains(GenericError) && b.text.Contains(ConfigPrompt), b.Describe());

            // -- B2: the VERIFIED live Claude Code shape (#36) — the whole array serialized as a JSON STRING.
            //    Must parse as the array it spells, bind, and reach the tool body — never the generic error,
            //    and never a one-element array holding the unparsed text (which would fail later, misleadingly).
            var b2 = Call(stdin, stdout, 30, "housecarl_cross_plugin_query",
                """{"type":"CELL","plugins":"[\"Synthetic.esp\",\"Other.esp\"]"}""");
            failures += Check("B2 coerce: plugins as string-encoded JSON array binds and runs the tool body",
                !b2.text.Contains(GenericError) && b2.text.Contains(ConfigPrompt), b2.Describe());

            // -- B3: a bare string that merely STARTS with '[' but isn't JSON — must keep the one-element
            //    wrap (the fall-through), not be rejected by a failed parse.
            var b3 = Call(stdin, stdout, 31, "housecarl_cross_plugin_query",
                """{"type":"CELL","plugins":"[Bracketed Name.esp"}""");
            failures += Check("B3 fall-through: a non-JSON bracket-leading string still wraps and runs",
                !b3.text.Contains(GenericError) && b3.text.Contains(ConfigPrompt), b3.Describe());

            // -- C: control — the documented array shape, unchanged behavior.
            var c = Call(stdin, stdout, 4, "housecarl_cross_plugin_query",
                """{"type":"CELL","plugins":["Synthetic.esp"]}""");
            failures += Check("C control: plugins as array still binds and runs the tool body",
                !c.text.Contains(GenericError) && c.text.Contains(ConfigPrompt), c.Describe());

            // -- D: the audit's other failing call — {} with a REQUIRED parameter missing. Must be a
            //    NAMED refusal naming the parameter, never the generic error.
            var d = Call(stdin, stdout, 5, "housecarl_batch_record_detail", "{}");
            failures += Check("D required: missing required parameter is refused by NAME",
                !d.text.Contains(GenericError) && d.text.Contains("required parameter") && d.text.Contains("formids"),
                d.Describe());

            // -- E: quoted number — same obvious-intent class as B. Must bind and run.
            var e = Call(stdin, stdout, 6, "housecarl_cross_plugin_query",
                """{"type":"CELL","plugins":["Synthetic.esp"],"limit":"100"}""");
            failures += Check("E coerce: quoted number limit binds and runs the tool body",
                !e.text.Contains(GenericError) && e.text.Contains(ConfigPrompt), e.Describe());

            // -- F: quoted bool — same class.
            var f = Call(stdin, stdout, 7, "housecarl_cross_plugin_query",
                """{"type":"CELL","plugins":["Synthetic.esp"],"conflicts_only":"true"}""");
            failures += Check("F coerce: quoted bool conflicts_only binds and runs the tool body",
                !f.text.Contains(GenericError) && f.text.Contains(ConfigPrompt), f.Describe());

            // -- G: an UNCOERCIBLE wrong-TYPE shape (an object where a number is declared) must still fail — but
            //    NAMED, and (#222) naming the OFFENDING PARAMETER (limit) + its received kind, caught BEFORE
            //    binding, not a bare byte-offset error over the whole received list.
            var g = Call(stdin, stdout, 8, "housecarl_cross_plugin_query",
                """{"type":"CELL","plugins":["Synthetic.esp"],"limit":{"oops":1}}""");
            failures += Check("G type-mismatch: an uncoercible wrong-type arg fails NAMED, naming the parameter",
                !g.text.Contains(GenericError) && g.text.Contains("could not be bound")
                && g.text.Contains("limit") && g.text.Contains("object"), g.Describe());

            // -- G2: #222 — a wrong-TYPE value for a declared BOOLEAN parameter (the report's exact class: a bad
            //    conflicts_only threw "could not be converted to System.Boolean" with a byte offset but NO
            //    parameter name). Must now be refused NAMED, identifying the parameter AND its expected type,
            //    before binding — never the generic error and never a silent run (no ConfigPrompt = body ran).
            var g2 = Call(stdin, stdout, 32, "housecarl_cross_plugin_query",
                """{"type":"CELL","conflicts_only":"CELL"}""");
            failures += Check("G2 type-mismatch: a wrong-type boolean arg is named with its parameter and expected type",
                !g2.text.Contains(GenericError) && !g2.text.Contains(ConfigPrompt)
                && g2.text.Contains("conflicts_only") && g2.text.Contains("boolean"), g2.Describe());

            // -- H: an EXPLICIT JSON null for a REQUIRED parameter (2026-06-12 hunt, proven over stdio on
            //    nexus_mod): ContainsKey saw it as supplied, the SDK bound null, and the tool body
            //    NullReferenced into Guard's "internal houseCARL failure… capture a bug report"
            //    misdirection. Must be the same NAMED missing-parameter refusal as D.
            var h = Call(stdin, stdout, 9, "housecarl_batch_record_detail", """{"formids":null}""");
            failures += Check("H required: explicit null for a required parameter is refused by NAME, not an internal failure",
                !h.text.Contains(GenericError) && !h.text.Contains("internal houseCARL failure")
                && h.text.Contains("required parameter") && h.text.Contains("formids"),
                h.Describe());

            // -- I: an UNKNOWN parameter (HCBR-2026-07-12). expand=/path=/field= were the audit agent's
            //    inventions; the SDK binder SILENTLY IGNORES an undeclared argument, so the call ran with the
            //    intent dropped and no correction reached the agent — which then concluded the capability was
            //    missing and hand-rolled a parser. Must be a NAMED refusal that names the offender AND lists the
            //    supported params (pointing at the real knob, depth=), never a silent run (no ConfigPrompt = the
            //    body never executed) and never the generic error. Required check passes first (formid present).
            var iRes = Call(stdin, stdout, 10, "housecarl_read_record",
                """{"formid":"0F1AC1:Skyrim.esm","expand":true}""");
            failures += Check("I unknown: an undeclared parameter is refused by NAME, listing supported params",
                !iRes.text.Contains(GenericError) && !iRes.text.Contains(ConfigPrompt)
                && iRes.text.Contains("unknown parameter") && iRes.text.Contains("expand") && iRes.text.Contains("depth"),
                iRes.Describe());

            // -- I2: control — the SAME call WITHOUT the stray param binds and reaches the body (proves the
            //    unknown-param check never rejects a well-formed call; it reaches the config prompt here).
            var i2 = Call(stdin, stdout, 11, "housecarl_read_record",
                """{"formid":"0F1AC1:Skyrim.esm"}""");
            failures += Check("I2 control: the same call without the stray param binds and runs the tool body",
                !i2.text.Contains(GenericError) && i2.text.Contains(ConfigPrompt), i2.Describe());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL  probe infrastructure: {ex.GetType().Name}: {ex.Message}");
            failures++;
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            try { proc.WaitForExit(5000); } catch { }                   // let the kill land before the delete, or the dir cleanup loses the race
            try { Directory.Delete(dataDir, recursive: true); } catch { }
        }

        Console.WriteLine(failures == 0
            ? "[binding-shim-guard] PASS — all argument-shape cases bind or fail with a named reason."
            : $"[binding-shim-guard] FAIL — {failures} case(s) regressed.");
        return failures == 0 ? 0 : 1;
    }

    static int Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
        if (!ok) Console.WriteLine($"      got: {detail}");
        return ok ? 0 : 1;
    }

    // ---- minimal JSON-RPC-over-stdio plumbing -----------------------------------------------

    /// <summary>One tools/call; returns (isError, first text block) for the asserts.</summary>
    static (bool isError, string text) Call(StreamWriter stdin, StreamReader stdout, int id, string tool, string argumentsJson)
    {
        using var argsDoc = JsonDocument.Parse(argumentsJson);
        var result = Rpc(stdin, stdout, id, "tools/call", new { name = tool, arguments = argsDoc.RootElement });
        bool isError = result.TryGetProperty("isError", out var ie) && ie.ValueKind == JsonValueKind.True;
        string text = "";
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
                if (block.TryGetProperty("text", out var tx)) { text = tx.GetString() ?? ""; break; }
        return (isError, text);
    }

    /// <summary>Send a request and block (bounded) for the response with the same id; returns its `result`.</summary>
    static JsonElement Rpc(StreamWriter stdin, StreamReader stdout, int id, string method, object @params)
    {
        Send(stdin, new { jsonrpc = "2.0", id, method, @params });
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var read = Task.Run(stdout.ReadLine);
            var remaining = deadline - DateTime.UtcNow;                 // ONE shared 30s budget — the per-line wait never extends it
            if (remaining <= TimeSpan.Zero || !read.Wait(remaining) || read.Result is not { } line)
                break;
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("id", out var rid) && rid.ValueKind == JsonValueKind.Number && rid.GetInt32() == id)
            {
                if (doc.RootElement.TryGetProperty("error", out var err))
                    throw new InvalidOperationException($"JSON-RPC error for {method}: {err.GetRawText()}");
                return doc.RootElement.GetProperty("result").Clone();
            }
            // a notification or another id — keep reading
        }
        throw new TimeoutException($"no response to {method} (id {id}) within 30s.");
    }

    static void Notify(StreamWriter stdin, string method) => Send(stdin, new { jsonrpc = "2.0", method });

    static void Send(StreamWriter stdin, object msg)
    {
        stdin.WriteLine(JsonSerializer.Serialize(msg));
        stdin.Flush();
    }

    static string Describe(this (bool isError, string text) r)
        => $"isError={r.isError} text=\"{(r.text.Length > 160 ? r.text[..160] + "…" : r.text)}\"";
}

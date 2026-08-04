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

            // -- D2: #224 — bulk_apply's 'operations' moved OFF the schema's required list (operations XOR
            //    from_file is judged in the tool BODY; dry-run-guard arm L proves that refusal named). Through the
            //    full binding/shim stack an empty {} must still BIND cleanly — no required check fires, no binder
            //    throw on the absent complex array — and reach the tool body (the config prompt here, since this
            //    server is unconfigured and the body's config check runs first), never the generic error. RED if
            //    optionalizing the parameter ever breaks {} binding (PR #241 review NOTE 4).
            var d2 = Call(stdin, stdout, 38, "housecarl_bulk_apply", "{}");
            failures += Check("D2 #224: bulk_apply {} binds with no required refusal and reaches the tool body",
                !d2.text.Contains(GenericError) && !d2.text.Contains("required parameter") && d2.text.Contains(ConfigPrompt),
                d2.Describe());

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

            // -- J1: #221 — the alias 'plugin' for a tool whose declared parameter is 'plugins' (singular↔plural
            //    synonym group). Must be RENAMED to plugins, then shape-coerced (string → one-element array) and
            //    reach the tool body — no unknown-parameter refusal, no generic error.
            var j1 = Call(stdin, stdout, 33, "housecarl_cross_plugin_query",
                """{"type":"CELL","plugin":"Synthetic.esp"}""");
            failures += Check("J1 alias: 'plugin' is resolved to 'plugins' and the call binds and runs",
                !j1.text.Contains(GenericError) && !j1.text.Contains("unknown parameter") && j1.text.Contains(ConfigPrompt),
                j1.Describe());

            // -- J2: #221 — the alias 'form_id' for 'formid' (an underscore/case variant, resolved by
            //    normalization alone). Must be renamed to formid and reach the body.
            var j2 = Call(stdin, stdout, 34, "housecarl_read_record",
                """{"form_id":"0F1AC1:Skyrim.esm"}""");
            failures += Check("J2 alias: 'form_id' is resolved to 'formid' and the call binds and runs",
                !j2.text.Contains(GenericError) && !j2.text.Contains("unknown parameter") && j2.text.Contains(ConfigPrompt),
                j2.Describe());

            // -- J3: #221 — the alias 'plugin_name' for 'plugins' (normalizes to "pluginname"; resolved via the
            //    synonym group, not by normalization equality). Must be renamed and reach the body.
            var j3 = Call(stdin, stdout, 35, "housecarl_cross_plugin_query",
                """{"type":"CELL","plugin_name":"Synthetic.esp"}""");
            failures += Check("J3 alias: 'plugin_name' is resolved to 'plugins' and the call binds and runs",
                !j3.text.Contains(GenericError) && !j3.text.Contains("unknown parameter") && j3.text.Contains(ConfigPrompt),
                j3.Describe());

            // -- J4: #221 GUARD — a tool whose REAL parameter is 'plugin' (read_plugin_file) must NOT have it
            //    treated as an alias: a declared parameter is always left alone, so the call binds normally.
            var j4 = Call(stdin, stdout, 36, "housecarl_read_plugin_file",
                """{"plugin":"Skyrim.esm","formid":"0F1AC1:Skyrim.esm"}""");
            failures += Check("J4 alias-guard: a tool's REAL 'plugin=' is untouched (binds and runs, not refused)",
                !j4.text.Contains(GenericError) && !j4.text.Contains("unknown parameter") && j4.text.Contains(ConfigPrompt),
                j4.Describe());

            // -- J5: #221 GUARD — an explicit canonical value is never clobbered: with 'plugins' supplied, the
            //    stray alias 'plugin' has no free target, so it is left for the unknown-parameter path (named),
            //    never silently merged over the caller's canonical value.
            var j5 = Call(stdin, stdout, 37, "housecarl_cross_plugin_query",
                """{"type":"CELL","plugins":["Synthetic.esp"],"plugin":"Other.esp"}""");
            failures += Check("J5 alias-guard: alias does not clobber an explicit canonical; the extra is named",
                !j5.text.Contains(GenericError) && j5.text.Contains("unknown parameter") && j5.text.Contains("plugin"),
                j5.Describe());

            // -- J6/J7: PR #304 review F1 — the 1.x plugin/plugins/plugin_name/plugin_names clique must stay a
            //    full set of edges under the table-driven layer, ON TOOLS OUTSIDE the J1–J5 pair: plugin= binds
            //    on create_plugin (whose declared parameter is plugin_name), and plugin_name= binds on
            //    read_plugin_file (whose declared parameter is bare plugin). Both bound before the alias
            //    inversion; both must reach the tool body (the config prompt), never an unknown-parameter refusal.
            var j6 = Call(stdin, stdout, 39, "housecarl_create_plugin", """{"plugin":"MyTrigger"}""");
            failures += Check("J6 clique: create_plugin binds plugin= onto its plugin_name= and runs the tool body",
                !j6.text.Contains(GenericError) && !j6.text.Contains("unknown parameter") && j6.text.Contains(ConfigPrompt),
                j6.Describe());
            var j7 = Call(stdin, stdout, 40, "housecarl_read_plugin_file",
                """{"plugin_name":"Skyrim.esm","formid":"0F1AC1:Skyrim.esm"}""");
            failures += Check("J7 clique: read_plugin_file binds plugin_name= onto its plugin= and runs the tool body",
                !j7.text.Contains(GenericError) && !j7.text.Contains("unknown parameter") && j7.text.Contains(ConfigPrompt),
                j7.Describe());

            // -- J8: PR #304 review F3 — a stray target= on compact_plugin (which has no target=, and whose
            //    in_place= is the 1.x bool) must stay the pre-PR named unknown WITH the supported list — never a
            //    rename onto in_place that answers with a type error about a key the caller never sent.
            var j8 = Call(stdin, stdout, 41, "housecarl_compact_plugin", """{"plugin":"X.esp","target":"X.esp"}""");
            failures += Check("J8 lane-dormant: compact_plugin target= is a named unknown, not an in_place type error",
                !j8.text.Contains(GenericError) && j8.text.Contains("unknown parameter") && j8.text.Contains("target")
                && !j8.text.Contains("could not be bound"), j8.Describe());

            // -- J9: PR #304 review F5 — a plural spelling carrying an ARRAY must not be renamed onto a scalar
            //    parameter: read_record(formids=[…]) keeps the caller's own key in the refusal (here the
            //    missing-required message, whose Supplied list names formids), never a type error about formid=.
            var j9 = Call(stdin, stdout, 42, "housecarl_read_record", """{"formids":["A","B"]}""");
            failures += Check("J9 kind-gate: array formids= on read_record fails naming the caller's own key",
                !j9.text.Contains(GenericError) && j9.text.Contains("formids") && !j9.text.Contains("could not be bound"),
                j9.Describe());

            // -- J10: re-review R1 — the kind gate must not reach the NORMALIZATION bridge: conflicts_Only is
            //    the same parameter as conflicts_only (case variant), so even with an unbindable value the
            //    rename proceeds and the TYPE refusal names the real parameter and fault — never an
            //    unknown-parameter refusal denying that the parameter exists.
            var j10 = Call(stdin, stdout, 43, "housecarl_cross_plugin_query",
                """{"type":"CELL","conflicts_Only":"CELL"}""");
            failures += Check("J10 bridge ungated: a case-variant key with a bad value gets the TYPE refusal, not unknown-parameter",
                !j10.text.Contains(GenericError) && !j10.text.Contains("unknown parameter")
                && j10.text.Contains("conflicts_only") && j10.text.Contains("boolean"), j10.Describe());

            // -- J11 (W2 PR 1): the STRUCTURED params bind over the real wire — records' form-scoped project=
            //    object and the polymorphic source= (a bare string) must reach the tool body (⇒ the config
            //    prompt), never a binding error. This is the one seam records-guard (which calls the C# method
            //    directly) cannot cover: the SDK's JSON→POCO deserialization of the published nested schema.
            var j11 = Call(stdin, stdout, 44, "housecarl_records",
                """{"formids":["0F1AC1:Skyrim.esm"],"source":"winner","project":{"form":"identity"}}""");
            failures += Check("J11 structured bind: records' nested project= object + string source= bind and reach the body",
                j11.text.Contains(ConfigPrompt), j11.Describe());
            var j11b = Call(stdin, stdout, 45, "housecarl_records",
                """{"types":["WEAP"],"plugins":{"names":["Skyrim.esm"],"defined_in":true}}""");
            failures += Check("J11b structured bind: the plugins= scope object binds and reaches the body",
                j11b.text.Contains(ConfigPrompt), j11b.Describe());

            // -- CENSUS: PR #304 final review finding 3 — the executable form of "dormant by construction".
            //    AliasLayerProbe proves the MECHANISM against synthetic schemas; this arm pins the TABLE
            //    against the REAL published schemas: for every rename row and dissolution hint, the exact
            //    set of tools where it activates today. A row that silently fails to fire (a mistyped
            //    candidate), or fires somewhere unintended (a schema this table's author didn't check),
            //    changes this set and goes RED — which is precisely how final-review findings 1 and 5 were
            //    found by hand. Value-dependent guards (supplied-stop, the kind gate) are deliberately
            //    outside the census: activation here means "the row CAN fire on this tool for some value".
            //    On any schema or table change: eyeball the printed diff, then update CensusExpected.
            failures += CensusArm(tools);

            // -- SCHEMA: the @file union ToolSchemas publishes, and the invariant that made it necessary.
            //    ops=/assignments= are declared JsonElement (no C# type expresses "array OR '@path'"), so the
            //    generator would publish {}; ToolSchemas republishes anyOf[<generated element array>, string].
            //    The load-bearing check is the LAST one: the generator terminates a recursive type with a
            //    POSITIONAL "#/..." back-reference relative to its own document, so nesting a generated
            //    sub-schema silently produces a dangling $ref — a broken schema, strictly worse than the {}
            //    it replaced. That check is generic over every tool, not just this one.
            failures += SchemaArm(tools);
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

    // ---- the alias-table activation census ------------------------------------------------------

    /// <summary>Every (tool, old spelling) pair where an AliasTable row can fire on TODAY's published
    /// schemas — renames as "tool: old -> target", dissolution hints as "tool: old => hint". Baked from
    /// an eyeballed run; any schema or table change that shifts activation must be re-eyeballed here.</summary>
    static readonly string[] CensusExpected =
    {
        // Eyeballed 2026-08-01 (PR #304, the W0 alias layer). Notable negatives this list certifies:
        // NO dissolution hint is active on today's surface (no "=> hint" lines — every gate misses),
        // nothing fires on S8 Nexus / S9 session tools, source-> only lands on the whose-version
        // plugin= tools and the two NIF mod= tools, and patch= lands on the §5.3 ARTIFACT per tool
        // (output on merge_plugins, archive_name on bsa_repack, patch_name elsewhere).
        // W3 (eyeballed 2026-08-04): housecarl_apply joins the surface — the FIRST 2.0 write tool, so the
        // write-side §5.3 rows go live. operations->ops and full_readback->readback are the pure renames;
        // all four "the new artifact" spellings land on patch= (apply declares no other output name, so the
        // clique's priority order is inert here); from_file's ⤳ hint activates on the `ops` gate — the @file
        // convention that retired it now exists — and the four copy-zip hints activate on the `assignments`
        // gate, exactly the wave §5.3 assigned them. 119 -> 130, all eleven on housecarl_apply.
        // NOTE (deliberate negative): verb->op does NOT appear. `op` is a member of an ops ELEMENT, not a
        // top-level parameter, and the shim rewrites top-level arguments only — a stray `verb` inside an op
        // is refused by the strict element reader instead, which carries its own §5.3 correction.
        "housecarl_apply: archivename -> patch",
        "housecarl_apply: fromfile => hint",
        "housecarl_apply: fullreadback -> readback",
        "housecarl_apply: operations -> ops",
        "housecarl_apply: output -> patch",
        "housecarl_apply: patchname -> patch",
        "housecarl_apply: pluginname -> patch",
        "housecarl_apply: sourceformid => hint",
        "housecarl_apply: sourcemod => hint",
        "housecarl_apply: sourceplugin => hint",
        "housecarl_apply: targetformid => hint",
        "housecarl_asset_status: paths -> asset_paths",
        "housecarl_batch_record_detail: formid -> formids",
        "housecarl_batch_record_detail: pluginname -> plugin",
        "housecarl_batch_record_detail: pluginnames -> plugin",
        "housecarl_batch_record_detail: plugins -> plugin",
        "housecarl_batch_record_detail: source -> plugin",
        "housecarl_bsa_extract: outpath -> dest",
        "housecarl_bsa_extract: outputdir -> dest",
        "housecarl_bsa_repack: fromfolder -> source_folder",
        "housecarl_bsa_repack: patch -> archive_name",
        "housecarl_bulk_apply: ops -> operations",
        "housecarl_bulk_apply: patch -> patch_name",
        "housecarl_bulk_apply: readback -> full_readback",
        "housecarl_bulk_create: patch -> patch_name",
        "housecarl_bulk_create: readback -> full_readback",
        "housecarl_bulk_place_asset: patch -> patch_name",
        "housecarl_check_errors: formid -> formids",
        "housecarl_check_errors: plugin -> plugins",
        "housecarl_check_errors: pluginname -> plugins",
        "housecarl_check_errors: pluginnames -> plugins",
        "housecarl_check_errors: types -> type",
        "housecarl_compact_plugin: patch -> patch_name",
        "housecarl_compact_plugin: pluginname -> plugin",
        "housecarl_compact_plugin: pluginnames -> plugin",
        "housecarl_compact_plugin: plugins -> plugin",
        "housecarl_compact_plugin: source -> plugin",
        "housecarl_compile_script: dest -> output_dir",
        "housecarl_compile_script: outpath -> output_dir",
        "housecarl_compile_script: patch -> patch_name",
        "housecarl_compile_script: paths -> script",
        "housecarl_copy_npc_appearance: patch -> patch_name",
        "housecarl_create_plugin: patch -> plugin_name",
        "housecarl_create_plugin: plugin -> plugin_name",
        "housecarl_create_plugin: pluginnames -> plugin_name",
        "housecarl_create_plugin: plugins -> plugin_name",
        "housecarl_create_record: ops -> operations",
        "housecarl_create_record: patch -> patch_name",
        "housecarl_create_record: readback -> full_readback",
        "housecarl_cross_plugin_query: plugin -> plugins",
        "housecarl_cross_plugin_query: pluginname -> plugins",
        "housecarl_cross_plugin_query: pluginnames -> plugins",
        "housecarl_cross_plugin_query: types -> type",
        "housecarl_decompile_script: patch -> patch_name",
        "housecarl_decompile_script: paths -> pex",
        "housecarl_diff_record: formids -> formid",
        "housecarl_effect_chain: type -> types",
        "housecarl_forward_record: formid -> formids",
        "housecarl_forward_record: patch -> patch_name",
        "housecarl_forward_record: readback -> full_readback",
        "housecarl_load_order_status: filter -> lookup",
        "housecarl_merge_plugins: patch -> output",
        "housecarl_merge_plugins: plugin -> plugins",
        "housecarl_merge_plugins: pluginname -> plugins",
        "housecarl_merge_plugins: pluginnames -> plugins",
        "housecarl_native_pairing_audit: lookup -> filter",
        "housecarl_nif_inspect: paths -> mesh_paths",
        "housecarl_nif_inspect: source -> mod",
        "housecarl_nif_set: patch -> patch_name",
        "housecarl_nif_set: paths -> mesh_path",
        "housecarl_nif_set: source -> mod",
        "housecarl_nif_set: verb -> op",
        "housecarl_place_asset: formids -> formid",
        "housecarl_place_asset: patch -> patch_name",
        "housecarl_read_plugin_file: formids -> formid",
        "housecarl_read_plugin_file: pluginname -> plugin",
        "housecarl_read_plugin_file: pluginnames -> plugin",
        "housecarl_read_plugin_file: plugins -> plugin",
        "housecarl_read_plugin_file: source -> plugin",
        "housecarl_read_plugin_file: types -> type",
        "housecarl_read_record: formids -> formid",
        "housecarl_read_record: pluginname -> plugin",
        "housecarl_read_record: pluginnames -> plugin",
        "housecarl_read_record: plugins -> plugin",
        "housecarl_read_record: source -> plugin",
        // W2 PR 1 (re-eyeballed 2026-08-02): housecarl_records joins the surface. The 1.x spellings land on
        // their 2.0 poles (plugin/mod/from_plugin -> source; formid -> formids; type -> types), the first
        // dissolution hints go LIVE (the form-scoped PROJECT spellings incl. the chartered conflict_tree hint,
        // editorid_contains -> the where grammar, winner_fields -> fields_source), and pluginname/pluginnames
        // list plugins= as their schema-level candidate (kind-gated at runtime: a string cannot bind the
        // structured scope object, so a live stray still refuses named rather than renaming).
        "housecarl_records: conflicttree => hint",
        "housecarl_records: depth => hint",
        "housecarl_records: editoridcontains => hint",
        "housecarl_records: fields => hint",
        "housecarl_records: formid -> formids",
        "housecarl_records: fromplugin -> source",
        "housecarl_records: groupby => hint",
        "housecarl_records: mod -> source",
        "housecarl_records: plugin -> source",
        "housecarl_records: pluginname -> plugins",
        "housecarl_records: pluginnames -> plugins",
        "housecarl_records: resolvenames => hint",
        "housecarl_records: type -> types",
        "housecarl_records: winnerfields => hint",
        // W2 PR 2 (re-eyeballed 2026-08-04): versus= and walk= land on housecarl_records, so their gated
        // hints go LIVE — the diff_record pole spellings (plugin_a/b, mod_a/b -> the source=/versus= poles)
        // and the walk-carried absorptions (effect_chain's mgef_formid; copy's closure). 113 -> 119, all six
        // on housecarl_records.
        "housecarl_records: closure => hint",
        "housecarl_records: mgefformid => hint",
        "housecarl_records: moda => hint",
        "housecarl_records: modb => hint",
        "housecarl_records: plugina => hint",
        "housecarl_records: pluginb => hint",
        "housecarl_remove_record: archivename -> patch",
        "housecarl_remove_record: formids -> formid",
        "housecarl_remove_record: output -> patch",
        "housecarl_remove_record: patchname -> patch",
        "housecarl_remove_record: pluginname -> patch",
        "housecarl_resolve: formid -> formids",
        "housecarl_set_field: formids -> formid",
        "housecarl_set_field: op -> verb",
        "housecarl_set_field: patch -> patch_name",
        "housecarl_set_field: readback -> full_readback",
        "housecarl_skse_config_audit: lookup -> filter",
        "housecarl_skse_inventory: lookup -> filter",
        "housecarl_skypatcher_layer: lookup -> filter",
        "housecarl_skypatcher_read: formids -> formid",
        "housecarl_validate_dialogue: formids -> formid",
        "housecarl_validate_scripts: formid -> formids",
        "housecarl_validate_scripts: plugin -> plugins",
        "housecarl_validate_scripts: pluginname -> plugins",
        "housecarl_validate_scripts: pluginnames -> plugins",
        "housecarl_validate_scripts: types -> type",
        "housecarl_write_seq: patch -> patch_name",
        "housecarl_write_seq: pluginname -> plugin",
        "housecarl_write_seq: pluginnames -> plugin",
        "housecarl_write_seq: plugins -> plugin",
        "housecarl_write_seq: source -> plugin",
    };

    /// <summary>Compute the activation census from the served tools/list and diff it against
    /// <see cref="CensusExpected"/>. Mirrors ResolveAliases' value-independent gates: a row is active on
    /// a tool when NO declared parameter normalizes to the old spelling (else the call is declared, or
    /// the bridge owns it) and the first non-excluded candidate IS declared; a hint is active when the
    /// old spelling is undeclared and every gate parameter is declared.</summary>
    /// <summary>The published-schema arm (W3): the SPEC §5.1 <c>@file</c> union on the JsonElement-typed list
    /// parameters, plus the same-document-pointer invariant every tool's schema must satisfy.</summary>
    static int SchemaArm(JsonElement toolsList)
    {
        int failures = 0;
        JsonElement apply = default;
        bool found = false;
        foreach (var t in toolsList.GetProperty("tools").EnumerateArray())
            if (t.GetProperty("name").GetString() == "housecarl_apply") { apply = t; found = true; break; }

        failures += Check("SCHEMA: housecarl_apply is published", found, "tool absent from tools/list");
        if (found)
        {
            foreach (var (param, member) in new[] { ("ops", "field_path"), ("assignments", "target") })
            {
                JsonElement arms = default;
                var ok = apply.GetProperty("inputSchema").GetProperty("properties").TryGetProperty(param, out var node)
                      && node.TryGetProperty("anyOf", out arms)
                      && arms.GetArrayLength() == 2
                      && arms[0].TryGetProperty("type", out var t0) && t0.GetString() == "array"
                      && arms[1].TryGetProperty("type", out var t1) && t1.GetString() == "string";
                failures += Check($"SCHEMA: {param}= publishes anyOf[<generated array>, string] — not the bare {{}} the declared JsonElement would give",
                    ok, ok ? "" : node.ValueKind == JsonValueKind.Undefined ? "parameter absent" : node.GetRawText());

                // The array arm must be the GENERATED element schema, not an empty placeholder: a named member
                // proves the generator ran over the real C# type (and so keeps tracking it as it changes).
                bool typed = ok && arms[0].TryGetProperty("items", out var items)
                                && items.TryGetProperty("properties", out var mprops)
                                && mprops.TryGetProperty(member, out _);
                failures += Check($"SCHEMA: {param}='s array arm carries the generated element members (e.g. {member})", typed, "");
            }
        }

        // GENERIC: every same-document $ref in every published schema must resolve. This is what catches a
        // generated sub-schema nested without rebasing its pointers.
        var dangling = new List<string>();
        foreach (var t in toolsList.GetProperty("tools").EnumerateArray())
        {
            if (!t.TryGetProperty("inputSchema", out var schema)) continue;
            var name = t.GetProperty("name").GetString()!;
            foreach (var r in CollectRefs(schema))
                if (!PointerResolves(schema, r)) dangling.Add($"{name}: {r}");
        }
        failures += Check($"SCHEMA: every same-document $ref in every published tool schema resolves ({dangling.Count} dangling)",
            dangling.Count == 0, string.Join(" | ", dangling.Take(5)));
        return failures;
    }

    /// <summary>Every <c>$ref</c> string anywhere in a schema document.</summary>
    static IEnumerable<string> CollectRefs(JsonElement node)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in node.EnumerateObject())
                {
                    if (p.Name == "$ref" && p.Value.ValueKind == JsonValueKind.String) yield return p.Value.GetString()!;
                    else foreach (var r in CollectRefs(p.Value)) yield return r;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                    foreach (var r in CollectRefs(item)) yield return r;
                break;
        }
    }

    /// <summary>Walk a same-document JSON pointer ("#/a/b/0"). Anything not starting with '#' is external and
    /// counts as resolvable — this arm polices the pointers we rebase, not the whole of JSON Schema.</summary>
    static bool PointerResolves(JsonElement root, string reference)
    {
        if (!reference.StartsWith('#')) return true;
        var cur = root;
        foreach (var raw in reference[1..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var seg = raw.Replace("~1", "/").Replace("~0", "~");
            if (cur.ValueKind == JsonValueKind.Object)
            {
                if (!cur.TryGetProperty(seg, out var next)) return false;
                cur = next;
            }
            else if (cur.ValueKind == JsonValueKind.Array)
            {
                if (!int.TryParse(seg, out var i) || i < 0 || i >= cur.GetArrayLength()) return false;
                cur = cur[i];
            }
            else return false;
        }
        return true;
    }

    static int CensusArm(JsonElement toolsList)
    {
        var actual = new List<string>();
        foreach (var t in toolsList.GetProperty("tools").EnumerateArray())
        {
            var name = t.GetProperty("name").GetString()!;
            if (!t.TryGetProperty("inputSchema", out var schema) || schema.ValueKind != JsonValueKind.Object) continue;
            if (!schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object) continue;
            var declared = new List<string>();
            foreach (var pr in props.EnumerateObject()) declared.Add(pr.Name);
            var declaredNorm = new HashSet<string>(declared.Select(HousecarlMcp.ToolCallShim.Normalize));

            foreach (var row in HousecarlMcp.AliasTable.AllRenames)
            {
                if (declaredNorm.Contains(row.Old)) continue;
                foreach (var candidate in row.Candidates)
                {
                    if (HousecarlMcp.AliasTable.IsExcluded(row, candidate, name)) continue;
                    var target = declared.FirstOrDefault(d => HousecarlMcp.ToolCallShim.Normalize(d) == candidate);
                    if (target is null) continue;
                    actual.Add($"{name}: {row.Old} -> {target}");
                    break;
                }
            }
            foreach (var d in HousecarlMcp.AliasTable.AllDissolutions)
            {
                if (declaredNorm.Contains(d.Old)) continue;
                if (d.GateParams.All(declaredNorm.Contains)) actual.Add($"{name}: {d.Old} => hint");
            }
        }
        actual.Sort(StringComparer.Ordinal);

        var expected = new HashSet<string>(CensusExpected, StringComparer.Ordinal);
        var got = new HashSet<string>(actual, StringComparer.Ordinal);
        var missing = expected.Except(got).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var surplus = got.Except(expected).OrderBy(s => s, StringComparer.Ordinal).ToList();
        bool ok = missing.Count == 0 && surplus.Count == 0;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  CENSUS: alias-table activation over the real schemas matches the eyeballed expectation ({actual.Count} activations)");
        if (!ok)
        {
            foreach (var m in missing) Console.WriteLine($"      expected but absent: {m}");
            foreach (var s in surplus) Console.WriteLine($"      active but unexpected: {s}");
            Console.WriteLine("      full actual census:");
            foreach (var a in actual) Console.WriteLine($"        \"{a}\",");
        }
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

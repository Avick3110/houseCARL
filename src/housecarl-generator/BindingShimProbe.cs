using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HousecarlMcp;

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
//  ALSO GUARDED HERE (arm D3): the retired-tool-name redirect, which went live for the six 1.x WRITE tools at
//  the demolition catch-up (#468) and is what a caller on pre-2.0 docs actually hits. (The check family's three
//  rows stay dormant: those tools are still registered, so they resolve normally and never reach the redirect.)
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

    /// <summary>One-line clamp of a server response, for an arm's failure detail.</summary>
    static string Flatten(string text)
    {
        var one = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return one.Length > 90 ? one[..90] : one;
    }

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

            // -- D2: #224 — the ops list is OFF the schema's required list (what to do when it is absent is judged
            //    in the tool BODY; dry-run-guard arm L proves that refusal named). Through the full binding/shim
            //    stack an empty {} must still BIND cleanly — no required check fires, no binder throw on the absent
            //    complex array — and reach the tool body (the config prompt here, since this server is unconfigured
            //    and the body's config check runs first), never the generic error. RED if optionalizing the
            //    parameter ever breaks {} binding (PR #241 review NOTE 4). Repointed from bulk_apply at the
            //    demolition catch-up (#468): apply carries the same optional list parameter and the same claim.
            var d2 = Call(stdin, stdout, 38, "housecarl_apply", "{}");
            failures += Check("D2 #224: apply {} binds with no required refusal and reaches the tool body",
                !d2.text.Contains(GenericError) && !d2.text.Contains("required parameter") && d2.text.Contains(ConfigPrompt),
                d2.Describe());

            // -- D3: THE RETIRED-NAME REDIRECT, driven over the wire for every name it now governs.
            //    ToolCallShim's retired-name check was written dormant — "load-bearing the moment one is
            //    unregistered". The demolition catch-up (#468) is that moment for the six 1.x write tools, so the
            //    path stops being theoretical here and starts being the only thing standing between a caller on old
            //    docs and a dead end. Nothing drove it before this arm: CheckMergeProbe only checks the TABLE has
            //    rows, which is a statement about a list, not about what the server answers.
            //
            //    WHAT THIS ARM'S SUBJECT SET IS, stated honestly (#468 round 1): it is
            //    AliasTable.AllRetiredTools MINUS the running server's registered set. The right-hand side is
            //    derived from the wire; the left-hand side is a MAINTAINED LIST. So this arm proves that every
            //    retired name the table KNOWS ABOUT redirects to a live tool — it does NOT prove the table is
            //    complete. Delete a tool and forget its row and this arm sweeps the rows it has, reports a clean
            //    sweep, and the caller gets a bare "Unknown tool". Closing that needs an oracle for "what the last
            //    shipped release published" (the 1.9 tools/list, captured as a frozen fixture), which is guard
            //    GROWTH rather than a fold: #<COMPLETENESS-ISSUE> carries it, routed with #470, whose PR already
            //    moves the census oracle to tools/list.
            {
                var registered = new HashSet<string>(StringComparer.Ordinal);
                foreach (var t in tools.GetProperty("tools").EnumerateArray())
                    if (t.GetProperty("name").GetString() is { Length: > 0 } rn) registered.Add(rn);

                var retiredAndGone = AliasTable.AllRetiredTools
                    .Select(r => r.Old).Where(old => !registered.Contains(old))
                    .OrderBy(x => x, StringComparer.Ordinal).ToList();

                var deadEnds = new List<string>();
                int id = 900;
                foreach (var old in retiredAndGone)
                {
                    var r = Call(stdin, stdout, id++, old, "{}");
                    // The redirect must name a successor that EXISTS, not merely fail politely: a caller who gets
                    // "unknown tool" learns nothing, and one sent to a tool that is itself gone learns worse than
                    // nothing. Checked against the REGISTERED set rather than a phrase, because the rows are not
                    // one shape — an absorbed tool reads "absorbed into housecarl_X", while housecarl_validate_dialogue
                    // was SPLIT and names two successors. Asserting a phrase tests the house style; asserting a live
                    // tool name tests the migration.
                    //
                    // WHOLE-IDENTIFIER match, not Contains (#468 round 1, measured): the response ECHOES the name it
                    // is refusing — "housecarl_create_record is not on this surface — …" — and the 2.0 successors are
                    // PREFIXES of three of the names they absorbed (create ⊂ create_record, remove ⊂ remove_record,
                    // forward ⊂ forward_record). A Contains test of that response is therefore satisfied by the echo
                    // alone for half this arm's subjects: two reviewers independently replaced a successor teaching
                    // with text naming no tool at all and this arm still passed.
                    bool named = !r.text.Contains(GenericError)
                              && registered.Any(reg => ToolNameMatch.ReferencedAtBoundary(r.text, reg));
                    if (!named) deadEnds.Add($"{old} -> {Flatten(r.text)}");
                }

                // A zero-length sweep is a BROKEN GUARD, not a clean one: it means either the table emptied or
                // every retired name is somehow still registered, and both deserve to be loud.
                failures += Check(
                    "D3 retired-name redirect: every UNREGISTERED retired tool answers with its successor call shape",
                    retiredAndGone.Count > 0 && deadEnds.Count == 0,
                    retiredAndGone.Count == 0
                        ? "no retired name is unregistered — nothing was swept, which is a broken guard rather than a clean surface"
                        : $"swept {retiredAndGone.Count} against {registered.Count} registered: {string.Join(", ", retiredAndGone)}"
                          + (deadEnds.Count == 0 ? "" : $" | DEAD ENDS: {string.Join("; ", deadEnds)}"));
            }

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
            //    The load-bearing check is the generic one: the generator terminates a recursive type with a
            //    POSITIONAL "#/..." back-reference, which strict providers reject outright (#451) and which a
            //    nested-without-rebasing sub-schema leaves dangling. No published schema may carry one.
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
        // W3 PR 2 (eyeballed 2026-08-05): create / remove / forward join the surface and write_seq flips to the
        // 2.0 vocabulary. 130 -> 158. What the diff certifies:
        //   * the write-side clique now fires on four tools instead of one (patch=, full_readback=, formid=),
        //     each landing on the ONE output name that tool declares;
        //   * from_plugin -> source activates on housecarl_forward — the §5.3 row's real destination, dormant
        //     since W0;
        //   * patch -> INTO activates on housecarl_remove ALONE: it is the only tool where the artifact a write
        //     edits already exists, and the four "new artifact" candidates ahead of `into` all miss there;
        //   * the five create-operand hints (record_type/editorid/parent/collection/grid) fire on BOTH
        //     housecarl_create and 1.x housecarl_bulk_create — both declare records=, and on both the scalar
        //     spelling is genuinely a member of a record rather than a top-level argument, so the hint is
        //     correct in both places rather than noise in one;
        // PR #311 review [low], folded: write_seq's `pluginname` row now lands on SOURCE, not patch. Once the
        //   tool declared source= AND patch=, the likeliest 1.x word for THE PLUGIN was the one that could not
        //   reach the plugin pole — plugin_name= silently renamed the OUTPUT FOLDER. `source` is appended LAST
        //   to the candidate list (+ patch suppressed on this tool), so records/ scope-bearing tools keep the
        //   W0 mapping and this is the ONLY row that moves.
        //   * write_seq's five reverse rows (source->plugin, plugins/plugin_name(s)->plugin, patch->patch_name)
        //     are GONE: it declares source= and patch= itself now, so those rows are declared, not renamed.
        // PR #311 review 3 (eyeballed 2026-08-05): 158 -> 163, five rows, all of them routes RESTORED rather
        //   than new behaviour. `plugins`/`plugin_names` had dead-ended on write_seq the moment this PR took
        //   `plugin` off it — their candidates were the three 1.x plugin spellings and nothing else — so both
        //   gain `source` last (write_seq x2, and forward x2 as the consistent consequence: it already took
        //   `plugin -> source`, so the plural spellings landing on the same pole is the existing rule, not a
        //   new one). `patchname -> into` on remove closes the split where `patch=` mapped and the sibling
        //   spelling every 1.x write tool uses did not. place_asset stays excepted on all of them.
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
        "housecarl_copy: archivename -> patch",
        "housecarl_copy: output -> patch",
        "housecarl_copy: patchname -> patch",
        "housecarl_copy: pluginname -> patch",
        "housecarl_create: archivename -> patch",
        "housecarl_create: collection => hint",
        "housecarl_create: editorid => hint",
        "housecarl_create: fullreadback -> readback",
        "housecarl_create: grid => hint",
        "housecarl_create: output -> patch",
        "housecarl_create: parent => hint",
        "housecarl_create: patchname -> patch",
        "housecarl_create: pluginname -> patch",
        "housecarl_create: recordtype => hint",
        "housecarl_create_plugin: patch -> plugin_name",
        "housecarl_create_plugin: plugin -> plugin_name",
        "housecarl_create_plugin: pluginnames -> plugin_name",
        "housecarl_create_plugin: plugins -> plugin_name",
        "housecarl_cross_plugin_query: plugin -> plugins",
        "housecarl_cross_plugin_query: pluginname -> plugins",
        "housecarl_cross_plugin_query: pluginnames -> plugins",
        "housecarl_cross_plugin_query: types -> type",
        "housecarl_decompile_script: patch -> patch_name",
        "housecarl_decompile_script: paths -> pex",
        "housecarl_diff_record: formids -> formid",
        "housecarl_effect_chain: type -> types",
        "housecarl_forward: archivename -> patch",
        "housecarl_forward: formid -> formids",
        "housecarl_forward: fromplugin -> source",
        "housecarl_forward: fullreadback -> readback",
        "housecarl_forward: mod -> source",
        "housecarl_forward: output -> patch",
        "housecarl_forward: patchname -> patch",
        "housecarl_forward: plugin -> source",
        "housecarl_forward: pluginname -> patch",
        "housecarl_forward: pluginnames -> source",
        "housecarl_forward: plugins -> source",
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
        "housecarl_records: closure => hint",
        "housecarl_records: conflicttree => hint",
        "housecarl_records: depth => hint",
        "housecarl_records: editoridcontains => hint",
        "housecarl_records: fields => hint",
        "housecarl_records: formid -> formids",
        "housecarl_records: fromplugin -> source",
        "housecarl_records: groupby => hint",
        "housecarl_records: mgefformid => hint",
        "housecarl_records: mod -> source",
        "housecarl_records: moda => hint",
        "housecarl_records: modb => hint",
        "housecarl_records: plugin -> source",
        "housecarl_records: plugina => hint",
        "housecarl_records: pluginb => hint",
        "housecarl_records: pluginname -> plugins",
        "housecarl_records: pluginnames -> plugins",
        "housecarl_records: resolvenames => hint",
        "housecarl_records: type -> types",
        "housecarl_records: winnerfields => hint",
        "housecarl_remove: formid -> formids",
        "housecarl_remove: patch -> into",
        "housecarl_remove: patchname -> into",
        "housecarl_resolve: formid -> formids",
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
        "housecarl_write_seq: archivename -> patch",
        // #312 gave write_seq the compile lane's output_dir=, so the same two rows activate here — the alias table
        // needed no edit, which is the census's own evidence that the parameter was added as PARITY and not as a
        // new spelling of its own.
        "housecarl_write_seq: dest -> output_dir",
        "housecarl_write_seq: fromplugin -> source",
        "housecarl_write_seq: mod -> source",
        "housecarl_write_seq: outpath -> output_dir",
        "housecarl_write_seq: output -> patch",
        "housecarl_write_seq: patchname -> patch",
        "housecarl_write_seq: plugin -> source",
        "housecarl_write_seq: pluginname -> source",
        "housecarl_write_seq: pluginnames -> source",
        "housecarl_write_seq: plugins -> source",
    };

    /// <summary>The published-schema arm (W3): the SPEC §5.1 <c>@file</c> union on the JsonElement-typed list
    /// parameters, the no-same-document-<c>$ref</c> invariant every tool's schema must satisfy (#451), and the
    /// bounded inline expansion that keeps the recursive shape legible once the pointers are gone.</summary>
    static int SchemaArm(JsonElement toolsList)
    {
        int failures = 0;

        // The derivation the expansion arms below stand on, fixtured against shapes today's DTOs cannot produce
        // end-to-end. Its two rules are visible only under those shapes, so nothing on the real surface would
        // redden if either were dropped.
        failures += DerivationFixtureArm();

        // One row per ToolSchemas.FileListParams entry — (tool, parameter, a member that proves the generator ran
        // over the real C# type). PR #311 review [low]: this arm used to hardcode housecarl_apply, so
        // housecarl_create's records= row was covered only by the generic dangling-$ref sweep — and a dropped or
        // mis-spelled row degrades that parameter back to the bare {} the declared JsonElement gives, which has no
        // refs to dangle and so passed silently. Every published union is named here.
        foreach (var (toolName, param, member) in new[]
                 {
                     ("housecarl_apply",  "ops",         "field_path"),
                     ("housecarl_apply",  "assignments", "target"),
                     ("housecarl_create", "records",     "record_type"),
                 })
        {
            JsonElement tool = default;
            bool found = false;
            foreach (var t in toolsList.GetProperty("tools").EnumerateArray())
                if (t.GetProperty("name").GetString() == toolName) { tool = t; found = true; break; }

            failures += Check($"SCHEMA: {toolName} is published", found, "tool absent from tools/list");
            if (found)
            {
                JsonElement arms = default;
                var ok = tool.GetProperty("inputSchema").GetProperty("properties").TryGetProperty(param, out var node)
                      && node.TryGetProperty("anyOf", out arms)
                      && arms.GetArrayLength() == 2
                      && arms[0].TryGetProperty("type", out var t0) && t0.GetString() == "array"
                      && arms[1].TryGetProperty("type", out var t1) && t1.GetString() == "string";
                failures += Check($"SCHEMA: {toolName} {param}= publishes anyOf[<generated array>, string] — not the bare {{}} the declared JsonElement would give",
                    ok, ok ? "" : node.ValueKind == JsonValueKind.Undefined ? "parameter absent" : node.GetRawText());

                // The array arm must be the GENERATED element schema, not an empty placeholder: a named member
                // proves the generator ran over the real C# type (and so keeps tracking it as it changes).
                bool typed = ok && arms[0].TryGetProperty("items", out var items)
                                && items.TryGetProperty("properties", out var mprops)
                                && mprops.TryGetProperty(member, out _);
                failures += Check($"SCHEMA: {toolName} {param}='s array arm carries the generated element members (e.g. {member})", typed, "");
            }
        }

        // GENERIC (#451) — THE INVARIANT: zero $ref members anywhere in any published schema, whatever they hold.
        // Strict providers reject a recursive schema and refuse the WHOLE server at tools/list, naming no tool, and
        // the StructInput -> NestedSet.compose chain published one on five tools.
        //
        // The predicate is deliberately WIDER than the flattener's, and shares no code with it: ToolSchemas gates
        // inlining on a same-document pointer (a string starting with '#') because that is what it knows how to
        // resolve, and this arm asks only whether the MEMBER is there. Spelling them the same way is what made the
        // earlier version of this arm vacuous — a $ref the pass could not resolve was also a $ref the detector
        // could not see, so an anchor-form {"$ref":"Node"} injected into all 51 schemas passed green. A detector
        // that inherits the subject's blind spot measures nothing at the only moment it matters. This one still
        // reports whether a survivor resolves, as detail, because that says which failure it is: a pointer the
        // pass left behind (a broken rebase) or a spelling it never understood.
        var refs = new List<string>();
        foreach (var t in toolsList.GetProperty("tools").EnumerateArray())
        {
            if (!t.TryGetProperty("inputSchema", out var schema)) continue;
            var name = t.GetProperty("name").GetString()!;
            foreach (var (path, value) in CollectRefMembers(schema, "#"))
            {
                var note = value.ValueKind != JsonValueKind.String ? "(NON-STRING)"
                    : PointerResolves(schema, value.GetString()!) ? "(resolves)" : "(DANGLING)";
                refs.Add($"{name}: {path} = {Trunc(value)} {note}");
            }
        }
        failures += Check($"SCHEMA: no published tool schema carries a $ref member, in any spelling ({refs.Count} found)",
            refs.Count == 0, string.Join(" | ", refs.Take(5)));

        // ANTI-AMPUTATION / ANTI-EXPANSION, over subjects DERIVED from the pre-flatten surface at guard time.
        // The invariant above is satisfied just as well by DELETING every recursive branch, which would tell
        // callers a nested compose is not a thing — a narrowing of the published contract with nothing to notice.
        // So every site that carried a $ref before the pass must still be spelled out after it.
        //
        // No tool and no arm is named here (#451, "derive, don't enumerate"). The earlier version walked
        // housecarl_apply only; four of the five ref-carrying tools had no expansion arm at all, so a bound of 0
        // applied to their subtree collapsed their compose/fields/ctor_args/sets to bare open nodes with both
        // guards fully green. The subject set is now an OUTPUT of the surface: it shrinks by itself when the 2.0
        // demolition deletes the three 1.x ref-carrying tools, where a hand-list would have gone stale and passed.
        //
        // PreFlattenSurface reads the raw generated schemas; the @file union pass is replayed here because the
        // published document has it and the pointers must line up. FlattenRefs replaces each ref node IN PLACE,
        // so a pre-flatten ref path is the same path in the published document — that is what makes the subjects
        // portable across the two surfaces.
        var subjects = 0;
        var recursive = 0;
        var carriers = new List<string>();

        foreach (var tool in PreFlattenSurface.Read())
        {
            var pre = (JsonObject)tool.Schema.DeepClone();
            var unions = ToolSchemas.FileListParams.Where(p => p.Tool == tool.Name).ToList();
            if (unions.Count > 0) ToolSchemas.RewriteFileListUnions(pre, unions);

            var sites = PreFlattenSurface.RefNodes(pre);
            if (sites.Count == 0) continue;
            carriers.Add($"{tool.Name}:{sites.Count}");

            JsonElement published = default;
            var found = false;
            foreach (var t in toolsList.GetProperty("tools").EnumerateArray())
                if (t.GetProperty("name").GetString() == tool.Name)
                { published = t.GetProperty("inputSchema"); found = true; break; }
            failures += Check($"SCHEMA: {tool.Name} carries {sites.Count} pre-flatten $ref sites and is published",
                found, "tool absent from tools/list");
            if (!found) continue;

            // The recursion STEP and CYCLE, derived from this tool's own refs — the rules live in DeriveRecursions.
            // EXACTLY one is required. A tool carrying two distinct cycles is refused here, never measured on
            // whichever one the walk saw last: that is a claim quantified over a population and verified over a
            // narrower subset, which is the class this branch was stopped on.
            var derived = DeriveRecursions(sites);
            failures += Check($"SCHEMA: {tool.Name} — exactly one recursion step is derivable from its own pre-flatten refs (got {derived.Count})",
                derived.Count == 1,
                derived.Count == 0
                    ? "no $ref points at one of its own ancestors on a segment boundary; the expansion arm has nothing to measure"
                    : "distinct cycles: " + string.Join(" | ", derived.Select(d => $"{d.Cycle} +{d.Step}")));
            string? step = derived.Count == 1 ? derived[0].Step : null;
            string? cycle = derived.Count == 1 ? derived[0].Cycle : null;

            foreach (var (path, node) in sites)
            {
                subjects++;
                var pubNode = Pointer(published, path);
                failures += Check($"SCHEMA: {tool.Name} {Tail(path)} survives the pass spelled out, not amputated to an open node",
                    pubNode is { ValueKind: JsonValueKind.Object } n && SpellsOutStructure(n),
                    pubNode is { } got ? Trunc(got) : "<path does not resolve in the published schema>");

                if (step is null || RefPointer(node) != cycle) continue;
                recursive++;
                var (levels, closedOpen, detail) = WalkRecursion(published, path, step);
                // The bound is a RULED value (decision record entry 15, Aaron: "keep 1"), spelled here independently
                // of the constant on purpose: reading ToolSchemas.MaxSelfExpansions would make this arm agree with
                // any bound, including the 0 that open-nodes the non-cyclic leaves too. Changing the bound reddens
                // here, which is the point — it is a published-contract change and has to be re-ratified, not slid in.
                failures += Check($"SCHEMA: {tool.Name} {Tail(path)} expands exactly 1 level before closing (the ruled bound; got {levels})",
                    levels == 1, detail);
                failures += Check($"SCHEMA: {tool.Name} {Tail(path)} closes on an OPEN node — typed, unconstrained, and saying nesting continues",
                    closedOpen, detail);
            }
        }

        // A derivation that finds nothing is a broken derivation, never a pass: without these two, every arm above
        // is vacuously green the moment PreFlattenSurface stops returning what it is supposed to.
        failures += Check($"SCHEMA: the derived subject set is non-empty — {subjects} pre-flatten $ref sites across {carriers.Count} tools [{string.Join(" ", carriers)}]",
            subjects > 0, "the pre-flatten surface yielded no $ref sites at all");
        failures += Check($"SCHEMA: at least one derived subject is recursive ({recursive} of {subjects})",
            recursive > 0, "no site re-enters its own pointer; nothing measured the bound");
        return failures;
    }

    /// <summary>The <c>$ref</c> pointer on a node, or null when the member is absent or does not hold a JSON
    /// string. The safe spelling on purpose: a non-string <c>$ref</c> is a form neither pass understands, and the
    /// invariant arm above is what reports it — reading it as a string here would take the guard down first.</summary>
    static string? RefPointer(JsonObject node) =>
        node["$ref"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Every DISTINCT (step, cycle) derivable from one tool's pre-flatten <c>$ref</c> sites. A site whose
    /// pointer is a proper ancestor of its own path is a positional back-reference, and the path segment between
    /// them is one turn of that cycle; every site aiming at the same pointer re-enters the same shape, so the walk
    /// is indifferent to [JsonPropertyName] declaration order, which an earlier fixed-level pin was not.
    ///
    /// Two rules make the derivation say what it means, and neither is visible on today's surface:
    /// <list type="bullet">
    /// <item>the prefix must end ON a JSON-pointer segment boundary. A bare string prefix makes a sibling whose
    /// wire name EXTENDS the target's into a false ancestor, and the derived step is then a fragment of a member
    /// name that resolves nowhere — a RED on a schema the pass flattened correctly. Today's surface survives that
    /// on one character (<c>.../compose/properties/sets</c> against <c>.../composes/items/properties/sets</c>),
    /// which is a coincidence of two member names, not a property anyone declared.</item>
    /// <item>every distinct pair is returned, never the last one. The caller refuses a tool that yields more than
    /// one. Measured 2026-09-01: a second recursive family on <c>StructInput</c> took the recursive population
    /// from 10 of 30 sites to 5 of 45 — the original compose/sets cycle measured on NO tool — with the guard
    /// fully green, because the later pair overwrote the first and only its own sites then matched.</item>
    /// </list></summary>
    internal static List<(string Step, string Cycle)> DeriveRecursions(IEnumerable<(string Path, JsonObject Node)> sites)
    {
        var found = new List<(string Step, string Cycle)>();
        foreach (var (path, node) in sites)
        {
            if (RefPointer(node) is not { } p || path.Length <= p.Length
                || !path.StartsWith(p, StringComparison.Ordinal) || path[p.Length] != '/') continue;
            var pair = (Step: path[p.Length..], Cycle: p);
            if (!found.Contains(pair)) found.Add(pair);
        }
        return found;
    }

    /// <summary>FIXTURES for <see cref="DeriveRecursions"/>. Both shapes were measured on the real surface under
    /// sabotage (a second <c>NestedSet[]</c> member on <c>StructInput</c>, 2026-09-01) and neither is producible by
    /// the DTOs as they stand, so the rules they pin have no end-to-end producer to redden — the same standing as
    /// schema-flatten-guard's $defs and mutual-recursion arms.</summary>
    static int DerivationFixtureArm()
    {
        const string R = "#/properties/operations/items/properties";
        static (string, JsonObject) Site(string path, string pointer) => (path, new JsonObject { ["$ref"] = pointer });

        var failures = 0;

        // TWO FAMILIES — the pointers the sabotage actually emitted. Both pairs must come back: reporting one is
        // how the guard claimed a tool's coverage while measuring a single cycle of it.
        var twoFamilies = DeriveRecursions(new[]
        {
            Site($"{R}/compose/properties/sets/items/properties/compose/properties/sets", $"{R}/compose/properties/sets"),
            Site($"{R}/compose/properties/sets/items/properties/compose/properties/alt/items", $"{R}/compose/properties/sets/items"),
        });
        failures += Check("SCHEMA-FIXTURE: a surface carrying two distinct cycles derives BOTH, so the caller can refuse it",
            twoFamilies.Count == 2, string.Join(" | ", twoFamilies.Select(d => $"{d.Cycle} +{d.Step}")));

        // SEGMENT BOUNDARY — a sibling whose wire name extends the target's is not an ancestor, and today's
        // near-miss (composes against compose) is not one either. Exactly the real cycle survives.
        var boundary = DeriveRecursions(new[]
        {
            Site($"{R}/compose/properties/sets/items/properties/compose/properties/sets", $"{R}/compose/properties/sets"),
            Site($"{R}/compose/properties/setsx", $"{R}/compose/properties/sets"),
            Site($"{R}/composes/items/properties/sets", $"{R}/compose/properties/sets"),
        });
        failures += Check("SCHEMA-FIXTURE: a sibling whose wire name EXTENDS the ref target is not read as an ancestor",
            boundary.Count == 1 && boundary[0].Step == "/items/properties/compose/properties/sets",
            string.Join(" | ", boundary.Select(d => $"{d.Cycle} +{d.Step}")));

        return failures;
    }

    /// <summary>True when a published node states structure rather than merely closing. <see cref="ToolSchemas"/>'s
    /// terminator emits exactly a <c>type</c> and a <c>description</c>, so any member beyond those two is the node
    /// still saying what it contains — which is what amputation removes.</summary>
    static bool SpellsOutStructure(JsonElement node)
    {
        foreach (var p in node.EnumerateObject())
            if (p.Name is not ("type" or "description")) return true;
        return false;
    }

    /// <summary>Walk one recursive chain from <paramref name="start"/> by repeatedly appending the derived
    /// <paramref name="step"/>, counting the levels spelled out concretely and reporting whether it ends on the
    /// open node the bound writes.</summary>
    static (int Levels, bool ClosedOpen, string Detail) WalkRecursion(JsonElement schema, string start, string step)
    {
        var levels = 0;
        var cur = start;
        while (true)
        {
            if (Pointer(schema, cur) is not { ValueKind: JsonValueKind.Object } node)
                return (levels, false, $"level {levels + 1} does not resolve to an object schema");
            if (!SpellsOutStructure(node))
                return (levels,
                    node.TryGetProperty("type", out _)
                    && node.TryGetProperty("description", out var d)
                    && d.GetString()?.Contains("Nesting continues", StringComparison.Ordinal) == true,
                    $"closed at level {levels + 1}: {Trunc(node)}");
            levels++;
            cur += step;
            // The bound exists so this terminates; an unbounded chain is a finding, not a hang.
            if (levels > 8) return (levels, false, $"still concrete at level {levels} — the chain is not closing");
        }
    }

    /// <summary>The tail of a JSON pointer, for labels that stay readable at 100-odd characters of path.</summary>
    static string Tail(string pointer)
    {
        var parts = pointer.Split('/');
        return parts.Length <= 4 ? pointer : ".../" + string.Join("/", parts[^4..]);
    }

    static string Trunc(JsonElement e) { var s = e.GetRawText(); return s.Length > 240 ? s[..240] + "…" : s; }

    /// <summary>Every <c>$ref</c> MEMBER anywhere in a schema document, by pointer, with what it holds.
    /// <para>No filter on the value: a non-string <c>$ref</c>, a plain-name anchor, an external URI and a
    /// same-document pointer all count the same, because the invariant is about the member being there at all.
    /// The predecessor yielded only string values and the caller then kept only those starting with '#' — the
    /// same test <see cref="ToolSchemas"/> gates inlining on, which is exactly why anything the pass could not
    /// resolve was also invisible to the arm policing it.</para></summary>
    static IEnumerable<(string Path, JsonElement Value)> CollectRefMembers(JsonElement node, string path)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in node.EnumerateObject())
                {
                    if (p.Name == "$ref") yield return (path, p.Value);
                    foreach (var r in CollectRefMembers(p.Value, $"{path}/{p.Name}")) yield return r;
                }
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in node.EnumerateArray())
                {
                    foreach (var r in CollectRefMembers(item, $"{path}/{i}")) yield return r;
                    i++;
                }
                break;
        }
    }

    /// <summary>Anything not starting with '#' is external and counts as resolvable — these arms police the
    /// pointers we rebase, not the whole of JSON Schema.</summary>
    static bool PointerResolves(JsonElement root, string reference)
        => !reference.StartsWith('#') || Pointer(root, reference) is not null;

    /// <summary>Walk a same-document JSON pointer ("#/a/b/0"), or null where it does not resolve.</summary>
    static JsonElement? Pointer(JsonElement root, string reference)
    {
        var cur = root;
        foreach (var raw in reference[1..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var seg = raw.Replace("~1", "/").Replace("~0", "~");
            if (cur.ValueKind == JsonValueKind.Object)
            {
                if (!cur.TryGetProperty(seg, out var next)) return null;
                cur = next;
            }
            else if (cur.ValueKind == JsonValueKind.Array)
            {
                if (!int.TryParse(seg, out var i) || i < 0 || i >= cur.GetArrayLength()) return null;
                cur = cur[i];
            }
            else return null;
        }
        return cur;
    }

    /// <summary>Compute the activation census from the served tools/list and diff it against
    /// <see cref="CensusExpected"/>. Mirrors ResolveAliases' value-independent gates: a row is active on
    /// a tool when NO declared parameter normalizes to the old spelling (else the call is declared, or
    /// the bridge owns it) and the first non-excluded candidate IS declared; a hint is active when the
    /// old spelling is undeclared and every gate parameter is declared.</summary>
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

using System.Diagnostics;
using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;

namespace HousecarlGenerator;

/// <summary>
/// 4b phase 3 ACCEPTANCE EVIDENCE for <c>housecarl_check</c>: the zero-capability-loss differential against the two
/// ancestors that are still registered beside it, and #394's own measured band re-taken on the merged tool.
///
/// <para><b>Deliberately NOT in <c>ci-all</c>.</b> Same standing as <c>copy-differential</c>: it needs a live MO2
/// instance, it is acceptance evidence for one PR rather than a guard, and the ancestors it compares against are
/// unregistered at the 2.0.0 clean cut. <c>check-guard</c> is the standing guard; this is the proof that the
/// surface the guard holds lost nothing on the way in.</para>
///
/// <para><b>Why the zero-loss cells run at a cap far above what anything writes.</b> A differential taken at a
/// biting cap measures two things at once — what each surface CAN say, and how each spends a budget — and the
/// merged response's framing (the scope sentence, a section head per family) costs body room the ancestor's does
/// not. Run unbounded, a divergence is a capability divergence and nothing else. The budget behaviour is measured
/// separately, in the #394 lane, where it is the subject rather than a confound.</para>
///
/// <para><b>The comparison is over FACTS, not bytes.</b> Both surfaces render through the same head/section
/// writers (<c>WriteErrorsHead</c> / <c>WriteScriptsHead</c> and their <c>Append</c> twins) — the merged one into a
/// per-family object, the ancestor flat — so json is compared key by key against the family object, and text as a
/// SET of body lines: every line the ancestor wrote must appear in the merged response, and the ancestor-only
/// lines that remain must each be nameable.</para>
///
/// <para>Read-only. Needs <c>--mo2 &lt;instance&gt;</c>; SKIPs without one. Every scope here is bounded — no
/// unscoped scripts sweep (~8.5 min on the live order) and no unscoped dialogue sweep (refused by construction).</para>
///
/// Run: dotnet run --project src/housecarl-generator -- check-differential --mo2 "E:\Skyrim Modding\ARR 2.0"
/// </summary>
public static class CheckDifferentialProbe
{
    static int _cells, _diverged;

    /// <summary>Far above anything these scopes render, so a zero-loss cell measures capability and not budget.</summary>
    const int Unbounded = 4_000_000;

    public static int Run(string[] args)
    {
        string? mo2 = ArgVal(args, "--mo2");
        if (mo2 is null) { Console.WriteLine("check-differential needs --mo2 <MO2 instance folder>"); return 2; }

        var store = new UserConfigStore(Path.Combine(Path.GetTempPath(), "hc-check-diff-" + Guid.NewGuid().ToString("N") + ".json"));
        using var svc = LoadOrderService.WithInstance(mo2, 0, store);

        Console.WriteLine($"# check-differential — 4b phase 3 acceptance, live order at {mo2}");
        Console.WriteLine($"# zero-loss cells run at max_chars={Unbounded} (nothing is cut, so a divergence is a capability divergence)\n");

        string errPlugin = ArgVal(args, "--plugin") ?? "Skyrim.esm";
        string scrPlugin = ArgVal(args, "--script-plugin") ?? "Skyrim.esm";

        RefusedFamilyJson(svc);
        ErrorsDifferential(svc, errPlugin);
        ScriptsDifferential(svc, scrPlugin);
        AcceptanceBand394(svc);
        DialogueEpochSubstrates(svc, ArgVal(args, "--seed") ?? "03372B:Skyrim.esm");

        Console.WriteLine($"\n================ {_cells} cell(s), {_diverged} with divergences ================");
        return 0;
    }

    // ---- flag (a): what a REFUSED family's json section states -------------------------------------

    /// <summary>Phase 2's flag: a family that REFUSED still writes an <c>accounting</c> object. What it contains is
    /// the question — a frame of counts about subjects this family never had is a different thing from a family
    /// saying it did not run. Printed whole rather than asserted about, because the disposition is a reading.</summary>
    static void RefusedFamilyJson(LoadOrderService svc)
    {
        Console.WriteLine("## L1  a REFUSED family's json section (phase-2 flag (a))\n");
        var json = CheckTools.CheckTool(svc, plugins: new[] { "Skyrim.esm" }, type: "AMMO",
                                        findings: new[] { "errors", "dialogue" }, format: "json");
        using var doc = JsonDocument.Parse(json);
        var dlg = doc.RootElement.GetProperty("families").GetProperty("dialogue");
        Console.WriteLine("   families.dialogue =");
        Console.WriteLine(Indent(JsonSerializer.Serialize(dlg, new JsonSerializerOptions { WriteIndented = true }), "   "));

        Console.WriteLine("\n   the TEXT lane's same family, for comparison:");
        var text = CheckTools.CheckTool(svc, plugins: new[] { "Skyrim.esm" }, type: "AMMO",
                                        findings: new[] { "errors", "dialogue" });
        bool inDlg = false;
        foreach (var l in text.Split('\n'))
        {
            if (l.StartsWith("[dialogue]", StringComparison.Ordinal)) inDlg = true;
            else if (l.StartsWith("[errors]", StringComparison.Ordinal)) inDlg = false;
            if (inDlg || l.StartsWith("boundary [dialogue]", StringComparison.Ordinal))
                Console.WriteLine("   | " + (l.Length > 180 ? l[..180] + "…" : l));
        }
        Console.WriteLine();
    }

    // ---- L2: the ERRORS family, ancestor vs merged --------------------------------------------------

    static void ErrorsDifferential(LoadOrderService svc, string plugin)
    {
        Console.WriteLine("## L2  ERRORS family — housecarl_check_errors vs housecarl_check findings=['errors']\n");

        Shape("plugins=[" + plugin + "], type=AMMO",
              () => ReadTools.CheckErrorsTool(svc, new[] { plugin }, type: "AMMO", max_chars: Unbounded, format: "json"),
              () => CheckTools.CheckTool(svc, plugins: new[] { plugin }, type: "AMMO", findings: new[] { "errors" },
                                         max_chars: Unbounded, format: "json"),
              () => ReadTools.CheckErrorsTool(svc, new[] { plugin }, type: "AMMO", max_chars: Unbounded),
              () => CheckTools.CheckTool(svc, plugins: new[] { plugin }, type: "AMMO", findings: new[] { "errors" },
                                         max_chars: Unbounded),
              SweepFamily.Errors);

        Shape("plugins=[" + plugin + "], type=WEAP",
              () => ReadTools.CheckErrorsTool(svc, new[] { plugin }, type: "WEAP", max_chars: Unbounded, format: "json"),
              () => CheckTools.CheckTool(svc, plugins: new[] { plugin }, type: "WEAP", findings: new[] { "errors" },
                                         max_chars: Unbounded, format: "json"),
              () => ReadTools.CheckErrorsTool(svc, new[] { plugin }, type: "WEAP", max_chars: Unbounded),
              () => CheckTools.CheckTool(svc, plugins: new[] { plugin }, type: "WEAP", findings: new[] { "errors" },
                                         max_chars: Unbounded),
              SweepFamily.Errors);

        Shape("findings=['missing_masters'] (the class that skips the link walk)",
              () => ReadTools.CheckErrorsTool(svc, findings: new[] { "missing_masters" }, max_chars: Unbounded, format: "json"),
              () => CheckTools.CheckTool(svc, findings: new[] { "missing_masters" }, max_chars: Unbounded, format: "json"),
              () => ReadTools.CheckErrorsTool(svc, findings: new[] { "missing_masters" }, max_chars: Unbounded),
              () => CheckTools.CheckTool(svc, findings: new[] { "missing_masters" }, max_chars: Unbounded),
              SweepFamily.Errors);

        Shape("counts_only=true, whole order (both histogram axes)",
              () => ReadTools.CheckErrorsTool(svc, counts_only: true, max_chars: Unbounded, format: "json"),
              () => CheckTools.CheckTool(svc, counts_only: true, findings: new[] { "errors" }, max_chars: Unbounded, format: "json"),
              () => ReadTools.CheckErrorsTool(svc, counts_only: true, max_chars: Unbounded),
              () => CheckTools.CheckTool(svc, counts_only: true, findings: new[] { "errors" }, max_chars: Unbounded),
              SweepFamily.Errors);

        Shape("exclude=['base_masters'], counts_only",
              () => ReadTools.CheckErrorsTool(svc, counts_only: true, exclude: new[] { "base_masters" }, max_chars: Unbounded, format: "json"),
              () => CheckTools.CheckTool(svc, counts_only: true, exclude: new[] { "base_masters" }, findings: new[] { "errors" },
                                         max_chars: Unbounded, format: "json"),
              () => ReadTools.CheckErrorsTool(svc, counts_only: true, exclude: new[] { "base_masters" }, max_chars: Unbounded),
              () => CheckTools.CheckTool(svc, counts_only: true, exclude: new[] { "base_masters" }, findings: new[] { "errors" },
                                         max_chars: Unbounded),
              SweepFamily.Errors);

        Shape("formids=['0BCC84:Skyrim.esm'] (the re-check-these-few pass)",
              () => ReadTools.CheckErrorsTool(svc, formids: new[] { "0BCC84:Skyrim.esm" }, max_chars: Unbounded, format: "json"),
              () => CheckTools.CheckTool(svc, formids: new[] { "0BCC84:Skyrim.esm" }, findings: new[] { "errors" },
                                         max_chars: Unbounded, format: "json"),
              () => ReadTools.CheckErrorsTool(svc, formids: new[] { "0BCC84:Skyrim.esm" }, max_chars: Unbounded),
              () => CheckTools.CheckTool(svc, formids: new[] { "0BCC84:Skyrim.esm" }, findings: new[] { "errors" },
                                         max_chars: Unbounded),
              SweepFamily.Errors);

        Shape("editorid_contains='Iron', type=ARMO",
              () => ReadTools.CheckErrorsTool(svc, new[] { plugin }, type: "ARMO", editorid_contains: "Iron",
                                              max_chars: Unbounded, format: "json"),
              () => CheckTools.CheckTool(svc, plugins: new[] { plugin }, type: "ARMO", editorid_contains: "Iron",
                                         findings: new[] { "errors" }, max_chars: Unbounded, format: "json"),
              () => ReadTools.CheckErrorsTool(svc, new[] { plugin }, type: "ARMO", editorid_contains: "Iron", max_chars: Unbounded),
              () => CheckTools.CheckTool(svc, plugins: new[] { plugin }, type: "ARMO", editorid_contains: "Iron",
                                         findings: new[] { "errors" }, max_chars: Unbounded),
              SweepFamily.Errors);

        // The REFUSALS: shared and errors-only. A refusal is a capability too — a merged surface that answered
        // where the ancestor refused, or refused with a different reason, has lost the reason.
        RefusalPair("blank formid", () => ReadTools.CheckErrorsTool(svc, formids: new[] { "  " }),
                    () => CheckTools.CheckTool(svc, formids: new[] { "  " }, findings: new[] { "errors" }));
        RefusalPair("malformed formid", () => ReadTools.CheckErrorsTool(svc, formids: new[] { "nope" }),
                    () => CheckTools.CheckTool(svc, formids: new[] { "nope" }, findings: new[] { "errors" }));
        RefusalPair("unknown type", () => ReadTools.CheckErrorsTool(svc, type: "ZZZZ"),
                    () => CheckTools.CheckTool(svc, type: "ZZZZ", findings: new[] { "errors" }));
        RefusalPair("blank plugin name", () => ReadTools.CheckErrorsTool(svc, new[] { "   " }),
                    () => CheckTools.CheckTool(svc, plugins: new[] { "   " }, findings: new[] { "errors" }));
        RefusalPair("exclude= unrecognized token", () => ReadTools.CheckErrorsTool(svc, exclude: new[] { "not_a_group" }),
                    () => CheckTools.CheckTool(svc, exclude: new[] { "not_a_group" }, findings: new[] { "errors" }));
        RefusalPair("exclude= names a file nothing matches",
                    () => ReadTools.CheckErrorsTool(svc, new[] { plugin }, exclude: new[] { "NotHere.esp" }),
                    () => CheckTools.CheckTool(svc, plugins: new[] { plugin }, exclude: new[] { "NotHere.esp" },
                                               findings: new[] { "errors" }));
        RefusalPair("plugin found nowhere (off-order resolve fails)",
                    () => ReadTools.CheckErrorsTool(svc, new[] { "ZzNotAPlugin.esp" }),
                    () => CheckTools.CheckTool(svc, plugins: new[] { "ZzNotAPlugin.esp" }, findings: new[] { "errors" }));
    }

    // ---- L3: the SCRIPTS family, ancestor vs merged --------------------------------------------------

    static void ScriptsDifferential(LoadOrderService svc, string plugin)
    {
        Console.WriteLine("\n## L3  SCRIPTS family — housecarl_validate_scripts vs housecarl_check findings=['scripts']\n");

        Shape("plugins=[" + plugin + "], type=QUST",
              () => ReadTools.ValidateScriptsTool(svc, new[] { plugin }, type: "QUST", max_chars: Unbounded, format: "json"),
              () => CheckTools.CheckTool(svc, plugins: new[] { plugin }, type: "QUST", findings: new[] { "scripts" },
                                         max_chars: Unbounded, format: "json"),
              () => ReadTools.ValidateScriptsTool(svc, new[] { plugin }, type: "QUST", max_chars: Unbounded),
              () => CheckTools.CheckTool(svc, plugins: new[] { plugin }, type: "QUST", findings: new[] { "scripts" },
                                         max_chars: Unbounded),
              SweepFamily.Scripts);

        Shape("plugins=[" + plugin + "], type=MGEF",
              () => ReadTools.ValidateScriptsTool(svc, new[] { plugin }, type: "MGEF", max_chars: Unbounded, format: "json"),
              () => CheckTools.CheckTool(svc, plugins: new[] { plugin }, type: "MGEF", findings: new[] { "scripts" },
                                         max_chars: Unbounded, format: "json"),
              () => ReadTools.ValidateScriptsTool(svc, new[] { plugin }, type: "MGEF", max_chars: Unbounded),
              () => CheckTools.CheckTool(svc, plugins: new[] { plugin }, type: "MGEF", findings: new[] { "scripts" },
                                         max_chars: Unbounded),
              SweepFamily.Scripts);

        Shape("type=QUST, counts_only=true (the by-PROPERTY axis)",
              () => ReadTools.ValidateScriptsTool(svc, new[] { plugin }, type: "QUST", counts_only: true,
                                                  max_chars: Unbounded, format: "json"),
              () => CheckTools.CheckTool(svc, plugins: new[] { plugin }, type: "QUST", counts_only: true,
                                         findings: new[] { "scripts" }, max_chars: Unbounded, format: "json"),
              () => ReadTools.ValidateScriptsTool(svc, new[] { plugin }, type: "QUST", counts_only: true, max_chars: Unbounded),
              () => CheckTools.CheckTool(svc, plugins: new[] { plugin }, type: "QUST", counts_only: true,
                                         findings: new[] { "scripts" }, max_chars: Unbounded),
              SweepFamily.Scripts);

        Shape("type=QUST, property_contains='Quest' (⤳ the where= dissolution's live half)",
              () => ReadTools.ValidateScriptsTool(svc, new[] { plugin }, type: "QUST", property_contains: "Quest",
                                                  max_chars: Unbounded, format: "json"),
              () => CheckTools.CheckTool(svc, plugins: new[] { plugin }, type: "QUST", property_contains: "Quest",
                                         findings: new[] { "scripts" }, max_chars: Unbounded, format: "json"),
              () => ReadTools.ValidateScriptsTool(svc, new[] { plugin }, type: "QUST", property_contains: "Quest", max_chars: Unbounded),
              () => CheckTools.CheckTool(svc, plugins: new[] { plugin }, type: "QUST", property_contains: "Quest",
                                         findings: new[] { "scripts" }, max_chars: Unbounded),
              SweepFamily.Scripts);

        Shape("type=QUST, findings=['unbound_object'] (one class, the HIGH one)",
              () => ReadTools.ValidateScriptsTool(svc, new[] { plugin }, type: "QUST", findings: new[] { "unbound_object" },
                                                  max_chars: Unbounded, format: "json"),
              () => CheckTools.CheckTool(svc, plugins: new[] { plugin }, type: "QUST", findings: new[] { "unbound_object" },
                                         max_chars: Unbounded, format: "json"),
              () => ReadTools.ValidateScriptsTool(svc, new[] { plugin }, type: "QUST", findings: new[] { "unbound_object" },
                                                  max_chars: Unbounded),
              () => CheckTools.CheckTool(svc, plugins: new[] { plugin }, type: "QUST", findings: new[] { "unbound_object" },
                                         max_chars: Unbounded),
              SweepFamily.Scripts);

        Shape("type=QUST, findings=['bound_null'] (the advisory class)",
              () => ReadTools.ValidateScriptsTool(svc, new[] { plugin }, type: "QUST", findings: new[] { "bound_null" },
                                                  max_chars: Unbounded, format: "json"),
              () => CheckTools.CheckTool(svc, plugins: new[] { plugin }, type: "QUST", findings: new[] { "bound_null" },
                                         max_chars: Unbounded, format: "json"),
              () => ReadTools.ValidateScriptsTool(svc, new[] { plugin }, type: "QUST", findings: new[] { "bound_null" },
                                                  max_chars: Unbounded),
              () => CheckTools.CheckTool(svc, plugins: new[] { plugin }, type: "QUST", findings: new[] { "bound_null" },
                                         max_chars: Unbounded),
              SweepFamily.Scripts);

        Shape("type=QUST, limit=3 (the finding budget, and what its accounting states)",
              () => ReadTools.ValidateScriptsTool(svc, new[] { plugin }, type: "QUST", limit: 3, max_chars: Unbounded, format: "json"),
              () => CheckTools.CheckTool(svc, plugins: new[] { plugin }, type: "QUST", limit: 3, findings: new[] { "scripts" },
                                         max_chars: Unbounded, format: "json"),
              () => ReadTools.ValidateScriptsTool(svc, new[] { plugin }, type: "QUST", limit: 3, max_chars: Unbounded),
              () => CheckTools.CheckTool(svc, plugins: new[] { plugin }, type: "QUST", limit: 3, findings: new[] { "scripts" },
                                         max_chars: Unbounded),
              SweepFamily.Scripts);

        RefusalPair("scripts: unknown type", () => ReadTools.ValidateScriptsTool(svc, type: "ZZZZ"),
                    () => CheckTools.CheckTool(svc, type: "ZZZZ", findings: new[] { "scripts" }));
        RefusalPair("scripts: malformed formid", () => ReadTools.ValidateScriptsTool(svc, formids: new[] { "nope" }),
                    () => CheckTools.CheckTool(svc, formids: new[] { "nope" }, findings: new[] { "scripts" }));

        // The ONE dispositioned asymmetry (§3 of the inventory, option 1). The ancestor REFUSES an off-order
        // plugin outright; the merged surface answers and STATES per family what it did not sweep. Printed, not
        // asserted equal — this is the recorded change, and the PR body carries it.
        Console.WriteLine("\n### the off-order asymmetry, dispositioned (inventory §3, option 1)");
        var offAnc = ReadTools.ValidateScriptsTool(svc, new[] { "ZzNotAPlugin.esp" });
        Console.WriteLine("   ancestor : " + First(offAnc, 220));
        var offMer = CheckTools.CheckTool(svc, plugins: new[] { "ZzNotAPlugin.esp" }, findings: new[] { "scripts" });
        foreach (var l in offMer.Split('\n'))
            if (l.Contains("not sweep", StringComparison.OrdinalIgnoreCase) || l.Contains("off-order", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine("   merged   : " + First(l, 300));
    }

    // ---- L4: #394's own band, re-taken on the merged tool -------------------------------------------

    /// <summary>#394's acceptance: the issue measured 74/180 axes at <c>max_chars=2000</c> giving TARGET 50 rows and
    /// SOURCE 0. The same shape, the same caps, on both surfaces — the ancestor keeps the serial rule (it passes no
    /// plan, so <c>BodyAllocation</c> governs nothing), the merged one divides. The number that closes the issue is
    /// the by-SOURCE row count.</summary>
    static void AcceptanceBand394(LoadOrderService svc)
    {
        Console.WriteLine("\n## L4  #394 acceptance — counts_only on the 74/180-axis shape\n");
        Console.WriteLine("   TEXT                          rows/distinct");
        Console.WriteLine($"   {"cap",6} {"surface",10} {"chars",7} {"TARGET",12} {"SOURCE",12}");
        foreach (int cap in new[] { 2000, 4000 })
        {
            string anc = ReadTools.CheckErrorsTool(svc, counts_only: true, max_chars: cap);
            string mer = CheckTools.CheckTool(svc, counts_only: true, findings: new[] { "errors" }, max_chars: cap);
            Row(cap, "ancestor", anc);
            Row(cap, "check", mer);
        }

        Console.WriteLine("\n   JSON                          rows");
        Console.WriteLine($"   {"cap",6} {"surface",10} {"chars",7} {"TARGET",12} {"SOURCE",12}");
        foreach (int cap in new[] { 2000, 4000 })
        {
            string anc = ReadTools.CheckErrorsTool(svc, counts_only: true, format: "json", max_chars: cap);
            string mer = CheckTools.CheckTool(svc, counts_only: true, findings: new[] { "errors" }, format: "json", max_chars: cap);
            Console.WriteLine($"   {cap,6} {"ancestor",10} {anc.Length,7} {JsonRows(anc, null, "dangling_by_target_plugin"),12} {JsonRows(anc, null, "dangling_by_source_plugin"),12}");
            Console.WriteLine($"   {cap,6} {"check",10} {mer.Length,7} {JsonRows(mer, "errors", "dangling_by_target_plugin"),12} {JsonRows(mer, "errors", "dangling_by_source_plugin"),12}");
        }

        // The other half of the same rule, and the one the counts_only band cannot reach: a SECOND family in the
        // response. Bounded scope so the scripts sweep is seconds, not minutes.
        Console.WriteLine("\n   two families in one response (bounded scope: Skyrim.esm type=QUST)");
        var sw = Stopwatch.StartNew();
        string both = CheckTools.CheckTool(svc, plugins: new[] { "Skyrim.esm" }, type: "QUST",
                                           findings: new[] { "errors", "scripts" });
        sw.Stop();
        Console.WriteLine($"   {both.Length} chars in {sw.ElapsedMilliseconds} ms; sections = "
                        + string.Join(", ", both.Split('\n').Where(l => l.StartsWith("[errors]", StringComparison.Ordinal)
                                                                     || l.StartsWith("[scripts]", StringComparison.Ordinal))
                                                            .Select(l => l.Split(']')[0] + "]")));
    }

    // ---- L5: what an honest dialogue EPOCH would have to cover ---------------------------------------

    /// <summary>Phase-2 flag (b), MEASURED rather than asserted. <c>LoadOrderService.ValidateDialogue</c> stamps no
    /// epoch, and records why: half a dialogue verdict comes off the ASSET substrate, which the record fingerprint
    /// does not cover. That "half" was a reading, not a number, and the comment named a wave (W3) as the owner of an
    /// honest stamp — a wave that completes at this PR without delivering one.
    ///
    /// <para>This counts one real seed's verdicts by SUBSTRATE: how many the record fingerprint would cover, and how
    /// many it would not. It is the number the issue needs in order to be actionable, and it is what makes
    /// "deliberately not stamped" a measured decision rather than a hedge.</para>
    ///
    /// <para>Bounded by construction: ONE seed, named on the command line or defaulted to a vanilla quest.</para></summary>
    static void DialogueEpochSubstrates(LoadOrderService svc, string seed)
    {
        Console.WriteLine($"\n## L5  the dialogue verdict by SUBSTRATE (phase-2 flag (b)), seed {seed}\n");
        FormKey fk;
        try { fk = FormKey.Factory(seed.Trim()); }
        catch (Exception e) { Console.WriteLine("   bad seed: " + e.Message); return; }

        var sw = Stopwatch.StartNew();
        var r = svc.ValidateDialogue(fk);
        sw.Stop();
        if (r.Error is not null || r.CheckError is not null)
        { Console.WriteLine("   seed did not validate: " + (r.Error ?? r.CheckError)); return; }

        // RECORD substrate — inside the record fingerprint an epoch would stamp.
        int recordVerdicts = r.InputIssues.Count + r.Topics.Sum(t => t.Issues.Count);

        // ASSET substrate — OUTSIDE it. Each of these is a verdict taken by looking at a FILE through the VFS:
        // a voiced line's .fuz, a result script's .pex, and a start-game-enabled quest's .seq (whose staleness
        // verdict is a FILE MTIME comparison, which no record fingerprint can express at all).
        int voiceChecked = r.Topics.Sum(t => t.VoiceLines.Count);
        int voiceSilent = r.Topics.Sum(t => t.VoiceLines.Count(l => !l.FuzPresent));
        int scriptChecked = r.Topics.Sum(t => t.ScriptFindings.Count);
        int scriptBad = r.Topics.Sum(t => t.ScriptFindings.Count(f => f.Status != ScriptBindingStatus.BoundAndCompiled));
        int seqLints = r.SeqLint is null ? 0 : 1;

        Console.WriteLine($"   topics validated                : {r.Topics.Count}   ({sw.ElapsedMilliseconds} ms)");
        Console.WriteLine($"   RECORD-substrate findings       : {recordVerdicts}   (input issues + per-topic issues — the fingerprint covers these)");
        Console.WriteLine($"   ASSET-substrate verdicts TAKEN  : {voiceChecked + scriptChecked + seqLints}");
        Console.WriteLine($"     · voiced lines (.fuz on disk) : {voiceChecked}  ({voiceSilent} silent)");
        Console.WriteLine($"     · result scripts (.pex chain) : {scriptChecked}  ({scriptBad} not bound-and-compiled)");
        Console.WriteLine($"     · .seq coverage + STALENESS   : {seqLints}  (a file-mtime comparison — no record fingerprint expresses it)");
        Console.WriteLine();
        Console.WriteLine("   An epoch off the record fingerprint would cover the first line and none of the rest.");
        Console.WriteLine("   Stamping one would claim freshness for verdicts it does not describe — which is why");
        Console.WriteLine("   this branch stamps none, and why an honest stamp is cross-substrate design work.");
    }

    static void Row(int cap, string surface, string s)
    {
        var (tr, td) = AxisRows(s, "TARGET plugin");
        var (sr, sd) = AxisRows(s, "SOURCE plugin");
        Console.WriteLine($"   {cap,6} {surface,10} {s.Length,7} {tr + "/" + td,12} {sr + "/" + sd,12}");
    }

    // ---- the differential itself --------------------------------------------------------------------

    /// <summary>One shape, both transports. json is compared key by key against the family object; text as a SET of
    /// lines, asymmetrically — every line the ancestor wrote must be in the merged response.</summary>
    static void Shape(string label, Func<string> ancJson, Func<string> merJson,
                      Func<string> ancText, Func<string> merText, SweepFamily fam)
    {
        _cells++;
        Console.WriteLine($"### {label}");
        string aj = ancJson(), mj = merJson(), at = ancText(), mt = merText();

        var divergences = new List<string>();
        var expected = new List<string>();

        // ---- json: the family's own object against the ancestor's root
        try
        {
            using var ad = JsonDocument.Parse(aj);
            using var md = JsonDocument.Parse(mj);
            if (!md.RootElement.TryGetProperty("families", out var fams)
                || !fams.TryGetProperty(SweepFamilySelection.Token(fam), out var famObj))
                divergences.Add($"json: the merged response carries no families.{SweepFamilySelection.Token(fam)} object");
            else
            {
                foreach (var p in ad.RootElement.EnumerateObject())
                {
                    // Response-level in BOTH: not the family's to carry, compared separately below.
                    if (p.Name is "excluded_plugins" or "max_chars_overrun") continue;
                    if (!famObj.TryGetProperty(p.Name, out var mine))
                        divergences.Add($"json: `{p.Name}` present on the ancestor, ABSENT from the family object");
                    else if (!Same(p.Value, mine))
                        DiffInto(divergences, p.Name, p.Value, mine);
                }
                foreach (var p in famObj.EnumerateObject())
                    if (!ad.RootElement.TryGetProperty(p.Name, out _))
                        divergences.Add($"json: `{p.Name}` is NEW on the family object ({Brief(p.Value)})");
            }
            // The response-level roster, which both surfaces write at root.
            bool ae = ad.RootElement.TryGetProperty("excluded_plugins", out var aex);
            bool me = md.RootElement.TryGetProperty("excluded_plugins", out var mex);
            if (ae != me || (ae && me && !Same(aex, mex)))
                divergences.Add("json: the response-level excluded_plugins roster differs");
        }
        catch (JsonException e) { divergences.Add("json: DID NOT PARSE — " + e.Message); }

        // ---- text: every ancestor body line must appear in the merged response
        var merged = new HashSet<string>(mt.Split('\n'), StringComparer.Ordinal);
        foreach (var l in at.Split('\n').Where(l => l.Length > 0 && !merged.Contains(l)))
        {
            if (ExpectedTextChange(l, mt) is { } why) expected.Add("text: " + why);
            else divergences.Add("text: ancestor-only line — " + First(l, 150));
        }

        if (divergences.Count == 0)
            Console.WriteLine($"   ZERO LOSS — json key-for-key, and every one of the ancestor's {at.Split('\n').Length} text lines is in the merged response"
                            + (expected.Count > 0 ? $" ({expected.Count} recorded re-spelling(s))" : ""));
        else
        {
            _diverged++;
            Console.WriteLine($"   {divergences.Count} DIVERGENCE(S):");
            foreach (var d in divergences) Console.WriteLine("     · " + d);
        }
        foreach (var e in expected.Distinct()) Console.WriteLine("     (recorded) " + e);
        Console.WriteLine($"   sizes: ancestor text {at.Length} / json {aj.Length}   merged text {mt.Length} / json {mj.Length}");
        Console.WriteLine();
    }

    /// <summary>A refusal is a capability. The merged surface must refuse the same input, with the same REASON —
    /// the text of the two can differ only by the merged prefix, so the ancestor's reason must be a substring.</summary>
    static void RefusalPair(string label, Func<string> ancestor, Func<string> merged)
    {
        _cells++;
        string a = ancestor(), m = merged();
        string aReason = a.StartsWith("error: ", StringComparison.Ordinal) ? a[7..] : a;
        aReason = aReason.Split('\n')[0].Trim();
        bool bothRefused = a.StartsWith("error: ", StringComparison.Ordinal) && m.StartsWith("error: ", StringComparison.Ordinal);
        bool sameReason = bothRefused && m.Contains(aReason, StringComparison.Ordinal);
        if (sameReason) Console.WriteLine($"   REFUSAL CARRIED  {label}");
        else
        {
            _diverged++;
            Console.WriteLine($"   REFUSAL DIVERGES {label}");
            Console.WriteLine($"     ancestor: {First(a, 220)}");
            Console.WriteLine($"     merged  : {First(m, 220)}");
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------

    /// <summary>STRUCTURAL equality, never raw text. The two documents write the same values at different NESTING
    /// DEPTHS — the merged one inside a family object — and <c>GetRawText()</c> carries the indentation, so a
    /// raw-text comparison reports every array and object as divergent purely because it is one level deeper.
    /// That is the differential lying about its own subject, and the first run of this probe did exactly it.</summary>
    static bool Same(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var ap = a.EnumerateObject().ToList();
                var bp = b.EnumerateObject().ToList();
                if (ap.Count != bp.Count) return false;
                foreach (var p in ap)
                    if (!b.TryGetProperty(p.Name, out var mine) || !Same(p.Value, mine)) return false;
                return true;
            case JsonValueKind.Array:
                var aa = a.EnumerateArray().ToList();
                var ba = b.EnumerateArray().ToList();
                if (aa.Count != ba.Count) return false;
                for (int i = 0; i < aa.Count; i++) if (!Same(aa[i], ba[i])) return false;
                return true;
            case JsonValueKind.String: return a.GetString() == b.GetString();
            case JsonValueKind.Number: return a.GetRawText() == b.GetRawText();
            default: return true;   // true / false / null / undefined are settled by ValueKind alone
        }
    }

    /// <summary>Where two objects differ, name the LEAVES rather than printing both blobs. An `accounting differs`
    /// line a reader cannot act on is not a differential result.</summary>
    static void DiffInto(List<string> into, string path, JsonElement a, JsonElement b)
    {
        if (a.ValueKind == JsonValueKind.Object && b.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in a.EnumerateObject())
            {
                if (!b.TryGetProperty(p.Name, out var mine)) into.Add($"json: `{path}.{p.Name}` present on the ancestor, ABSENT on the merged side");
                else if (!Same(p.Value, mine)) DiffInto(into, path + "." + p.Name, p.Value, mine);
            }
            foreach (var p in b.EnumerateObject())
                if (!a.TryGetProperty(p.Name, out _)) into.Add($"json: `{path}.{p.Name}` is NEW on the merged side ({Brief(p.Value)})");
            return;
        }
        into.Add($"json: `{path}` — ancestor {Brief(a)} / merged {Brief(b)}");
    }

    static string Brief(JsonElement e)
    {
        string s = e.ValueKind == JsonValueKind.String ? "\"" + e.GetString() + "\""
                 : string.Join("", e.GetRawText().Split('\n').Select(l => l.Trim()));
        return s.Length > 110 ? s[..110] + "…" : s;
    }

    /// <summary>An ancestor text line the merged response does not carry VERBATIM, but which is nonetheless present
    /// — the two framing lines the merge re-spells by design. Named here so the differential reports them as
    /// dispositioned rather than as losses, and so anything NOT in this list is a real finding.</summary>
    static string? ExpectedTextChange(string line, string mergedResponse)
    {
        if (line.StartsWith("check_errors — ", StringComparison.Ordinal)
            || line.StartsWith("validate_scripts — ", StringComparison.Ordinal))
            return "the ancestor's TITLE line — the merged response writes one title plus a section head naming the family";
        if (line.StartsWith("boundary: ", StringComparison.Ordinal))
        {
            // The sentence itself must still be there; only its LABEL changes, to name which family claims it.
            string sentence = line["boundary: ".Length..];
            return mergedResponse.Contains(sentence, StringComparison.Ordinal)
                 ? "the boundary LABEL — the same sentence, written as `boundary [<family>]:` because a merged response carries one per family"
                 : null;   // the sentence is gone, not relabelled: a real loss
        }
        return null;
    }

    static string First(string s, int n)
    {
        s = s.Replace("\r", "").Split('\n')[0];
        return s.Length > n ? s[..n] + "…" : s;
    }

    static string Indent(string s, string pad) => string.Join("\n", s.Split('\n').Select(l => pad + l));

    /// <summary>Rows actually rendered on one text histogram axis, and the distinct count its header states.</summary>
    static (int rows, int distinct) AxisRows(string s, string axis)
    {
        var lines = s.Replace("\r", "").Split('\n');
        int i = Array.FindIndex(lines, l => l.Contains("by " + axis, StringComparison.Ordinal));
        if (i < 0) return (-1, -1);
        int distinct = 0;
        var head = lines[i];
        int lp = head.IndexOf('(');
        if (lp >= 0) int.TryParse(new string(head[(lp + 1)..].TakeWhile(char.IsDigit).ToArray()), out distinct);
        int rows = 0;
        for (int j = i + 1; j < lines.Length; j++)
        {
            var l = lines[j];
            if (l.Length == 0 || !l.StartsWith("  ", StringComparison.Ordinal)) break;
            if (l.TrimStart().StartsWith("...", StringComparison.Ordinal)) break;
            rows++;
        }
        return (rows, distinct);
    }

    static int JsonRows(string s, string? family, string axis)
    {
        try
        {
            using var d = JsonDocument.Parse(s);
            var root = d.RootElement;
            if (family is not null)
            {
                if (!root.TryGetProperty("families", out var f) || !f.TryGetProperty(family, out var fo)) return -1;
                root = fo;
            }
            if (!root.TryGetProperty(axis, out var ax) || !ax.TryGetProperty("rows", out var rows)) return -1;
            return rows.GetArrayLength();
        }
        catch { return -1; }
    }

    static string? ArgVal(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}

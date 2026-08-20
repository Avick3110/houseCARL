using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Pex;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for the SCRIPT-PROPERTY binding sweep (housecarl_validate_scripts). Drives the
/// REAL product path (<see cref="ScriptPropertyCheck.Run"/> — what the tool calls through the thin service wrapper)
/// against a SYNTHESIZED plugin + PLANTED .pex fixtures in TEMP — NO Skyrim.esm, so it runs in CI.
///
/// THE GAP (reproduced by construction — bug store 2026-07-04): a record's attached script DECLARES a property the
/// record's VMAD never BINDS, so at runtime it is None and the code that uses it no-ops while the log looks clean. No
/// existing tool cross-checked the VMAD's bound properties against the .pex's declared ones.
///
/// FIXTURE — two planted .pex (a child + the base it extends) and ONE plugin of weapons, each a scenario:
///   • Scripts\HcSpBase.pex   — declares one Auto property: InheritedThing (ObjectReference).
///   • Scripts\HcSpChild.pex  — extends HcSpBase; declares MySpell (Spell), MyBoundSpell (Spell), MyChance (Int, no
///                              init), MyDefaulted (Int = 5, baked init), MyAliasBound (Spell), MyNullSpell (Spell).
///   • wFootgun — VMAD binds HcSpChild with { MyBoundSpell→set, MyNullSpell→null, MyAliasBound→Alias 2 }. The reported
///                failure: MySpell + MyChance + InheritedThing declared-but-unbound; MyDefaulted suppressed (has a
///                default); MyBoundSpell clean; MyNullSpell bound-but-null; MyAliasBound bound-via-alias (neither).
///   • wClean   — VMAD binds HcSpChild with ALL properties, every object one non-null. The no-false-positive control:
///                a fully-bound record must produce ZERO findings.
///   • wNoPex   — VMAD binds HcSpNoPex, whose .pex is NOT planted → UNVERIFIABLE, never passed clean (Q3).
///   • wNoVmad  — a script-free weapon → NOT counted, NEVER nagged.
///   • aliasQuest — a QUST whose script is attached to a QuestAdapter ALIAS (not the quest's own Scripts), binding
///                nothing → its declared MySpell is unbound. Proves alias scripts are swept (finding 1).
///
/// Arms (ALL required — a GREEN must mean "the contract holds"):
///   PEX-ROUNDTRIP        — the planted HcSpChild.pex reads back with MySpell as an Auto property (the fixture is valid).
///   FOOTGUN-OBJECT       — wFootgun's unbound set contains MySpell, typed OBJECT (the silent-None class, the report).
///   CHAIN-WALK           — wFootgun's unbound set contains InheritedThing, declared in the ANCESTOR HcSpBase.
///   SCALAR-UNINIT        — wFootgun's unbound set contains MyChance (Int, no baked default), typed SCALAR.
///   SCALAR-SUPPRESS      — wFootgun's unbound set does NOT contain MyDefaulted (a baked default ⇒ not a silent-wrong).
///   BOUND-CLEAN          — wFootgun's unbound set does NOT contain MyBoundSpell (it is bound + set).
///   NULL-ADVISORY        — wFootgun's bound-but-null set contains MyNullSpell (the same None, advisory).
///   ALIAS-BOUND-NOT-NULL — MyAliasBound (Object null, Alias>=0) is NEITHER unbound NOR bound-but-null (finding 2).
///   ALIAS-SCRIPT-SWEPT   — aliasQuest's alias-attached script is checked; its unbound set contains MySpell (finding 1).
///   CLEAN-CONTROL        — wClean produces NO report (fully bound ⇒ zero findings: the no-false-positive teeth).
///   NO-VMAD-IGNORE       — wNoVmad produces NO report and is not counted among records-with-scripts.
///   UNVERIFIABLE         — wNoPex names HcSpNoPex unverifiable ("not on disk"), never a silent clean (Q3).
///   RECORDS-COUNT        — exactly 4 records carry scripts (footgun, clean, noPex, aliasQuest — noVmad excluded).
///   SCOPE-Q3             — scope=[a name not in the order] fails LOUD ("not in the load order"), no reports.
///   NARROW-FORMIDS       — formids=[wFootgun] sweeps that record alone; the roster holds only it (#282).
///   NARROW-EDITORID      — editorid_contains= narrows case-insensitively to the QUST.
///   NARROW-TYPE          — type=QUST keeps only the quest; the scripted WEAPs never have their .pex chain read.
///   NARROW-NOTE          — a narrowed result carries a FilterNote saying the counts are scope-relative; an unnarrowed one doesn't.
///   FILTER-PROPERTY      — property_contains='myspell' keeps MySpell, drops MyChance + InheritedThing.
///   FILTER-CLASS         — findings=[unbound_object] keeps MySpell, drops the scalar AND the bound-but-null advisory.
///   CLASS-NOT-CHECKED-PARTIAL / -TOTAL — an EXCLUDED class renders "NOT CHECKED" and nulls in json, never 0 (PR #288
///                          review, finding 1: this guard previously asserted the 0 and so locked the bug in).
///   COUNTS-ONLY-EXCLUDED-NAMED — counts_only text NAMES the unparseable plugins, not just the header count (finding 5).
///   COUNTS-ONLY-NO-TALLY — counts_only + findings=[bound_null] leaves Histogram NULL and says nothing was tallied; the
///                          roster contract still holds without a histogram (PR #288 RE-review, finding 1: the round-1
///                          gate landed on check_errors and not on this twin).
///   CAPPED-TAIL-CLASS-AWARE / -SYMMETRIC — the capped tail restates the totals, so it needs the header's class-awareness
///                          or a small limit= reintroduces the "0 unbound" the header dropped (re-review, finding 2).
///   CLAIM-ONLY-WHEN-SCOPED — ONE claim rule: only a RECORD SCOPE carries the not-the-whole-plugin claim. Neither
///                          findings= nor property_contains= does, because neither narrows every reported number
///                          (re-review finding 3, extended by the round-3 review).
///   PROP-FILTER-SELF-LABELS / -IN-JSON — since the claim is gone, the two counts property_contains= DOES narrow label
///                          themselves ("N unbound matching 'x'"), while records-with-scripts and unverifiable stay
///                          unlabelled and plugin-wide — the asymmetry that made the blanket claim false (round-3 review).
///   UNVERIFIABLE-SURVIVES-FILTER — under a filter matching NOTHING, wNoPex is STILL reported unverifiable. The
///                          load-bearing Q3 arm: that unread .pex might declare the very property being filtered for,
///                          so letting a filter hide it would turn "could not check" into a clean answer.
///   FILTER-Q3            — an unrecognized findings= value fails LOUD, names the legal set, and says unverifiable is unfilterable.
///   COUNTS-ONLY          — counts_only=true matches the full sweep's totals, builds NO roster, and tallies unbound by property NAME.
///   COUNTS-ONLY-NOT-COMPUTED — a normal sweep leaves Histogram null: "not computed" ≠ "nothing found".
///   JSON-PARITY          — format=json PARSES and agrees with the text totals in both modes (D2).
///   TRUNCATION-HINT      — the max_chars cut names knobs that EXIST; it no longer advises "scope plugins=", the one
///                          scope the caller had already applied (the misdirection half of #282).
///
/// Run: dotnet run --project src/housecarl-generator -- script-property-check-guard
/// </summary>
public static class ScriptPropertyCheckProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("script-property-check-guard — VMAD script-property binding sweep (housecarl_validate_scripts)");
        Console.WriteLine();
        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-script-prop-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try { return RunChecks(tmpDir); }
        finally { try { Directory.Delete(tmpDir, recursive: true); } catch { /* best-effort */ } }
    }

    static int RunChecks(string tmpDir)
    {
        int failures = 0;
        void Check(string label, bool ok, string? detail = null)
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}{(ok || detail is null ? "" : $"\n        -> {detail}")}");
            if (!ok) failures++;
        }

        // ---- 1) plant the two .pex fixtures under <tmpDir>\Scripts (the loose "Data" root). ----
        var scriptsDir = Path.Combine(tmpDir, "Scripts");
        Directory.CreateDirectory(scriptsDir);
        try
        {
            WritePex(Path.Combine(scriptsDir, "HcSpBase.pex"), "HcSpBase", parent: null,
                AutoObj("InheritedThing", "ObjectReference"));

            WritePex(Path.Combine(scriptsDir, "HcSpChild.pex"), "HcSpChild", parent: "HcSpBase",
                AutoObj("MySpell", "Spell"),
                AutoObj("MyBoundSpell", "Spell"),
                AutoScalar("MyChance", "Int", initInt: null),
                AutoScalar("MyDefaulted", "Int", initInt: 5),   // a baked default ⇒ NOT flagged when unbound
                AutoObj("MyAliasBound", "Spell"),               // bound via a quest Alias (Object null but Alias>=0) — NOT a null finding
                AutoObj("MyNullSpell", "Spell"));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: could not write .pex fixtures: {ex.GetType().Name}: {(ex.InnerException ?? ex).Message}");
            return 1;
        }

        // PEX-ROUNDTRIP — the fixture is a valid .pex whose Auto property table survives the write.
        try
        {
            var back = PexFile.CreateFromFile(Path.Combine(scriptsDir, "HcSpChild.pex"), GameCategory.Skyrim);
            var o = back.Objects.FirstOrDefault();
            var mySpell = o?.Properties.FirstOrDefault(p => string.Equals(p.Name, "MySpell", StringComparison.OrdinalIgnoreCase));
            Check("PEX-ROUNDTRIP: planted HcSpChild.pex reads back with MySpell as an Auto property + parent HcSpBase",
                mySpell is not null && mySpell.Flags.HasFlag(PropertyFlags.AutoVar)
                && string.Equals(o!.ParentClassName, "HcSpBase", StringComparison.OrdinalIgnoreCase),
                $"prop={(mySpell is null ? "<missing>" : $"{mySpell.Name}/Auto={mySpell.Flags.HasFlag(PropertyFlags.AutoVar)}")} parent={o?.ParentClassName}");
        }
        catch (Exception ex) { Check("PEX-ROUNDTRIP", false, $"{ex.GetType().Name}: {ex.Message}"); }

        // ---- 2) synthesize the plugin of scripted weapons. ----
        string pluginPath = Path.Combine(tmpDir, "HcSp.esp");
        var mKey = new ModKey("HcSp", ModType.Plugin);
        FormKey footgunFk, cleanFk, noPexFk, noVmadFk, aliasQuestFk;
        try
        {
            var mod = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
            var self = new FormKey(mKey, 0x000801);   // a non-null in-plugin FormKey for "bound" object props (target need not exist — only IsNull is read)

            // wFootgun — the reported failure shape. MyAliasBound is bound via a quest alias (Object null, Alias>=0):
            // it must count as BOUND (not unbound) and must NOT be flagged bound-but-null (finding 2).
            var wFootgun = mod.Weapons.AddNew(); wFootgun.EditorID = "HcSpFootgun";
            wFootgun.VirtualMachineAdapter = Vmad("HcSpChild",
                ObjProp("MyBoundSpell", self), ObjProp("MyNullSpell", FormKey.Null), AliasProp("MyAliasBound", 2));
            footgunFk = wFootgun.FormKey;

            // wClean — fully bound: the no-false-positive control.
            var wClean = mod.Weapons.AddNew(); wClean.EditorID = "HcSpClean";
            wClean.VirtualMachineAdapter = Vmad("HcSpChild",
                ObjProp("MySpell", self), ObjProp("MyBoundSpell", self), ObjProp("MyNullSpell", self),
                ObjProp("MyAliasBound", self), ObjProp("InheritedThing", self), IntProp("MyChance", 3), IntProp("MyDefaulted", 5));
            cleanFk = wClean.FormKey;

            // wNoPex — a script with no compiled .pex on disk.
            var wNoPex = mod.Weapons.AddNew(); wNoPex.EditorID = "HcSpNoPex";
            wNoPex.VirtualMachineAdapter = Vmad("HcSpNoPex");
            noPexFk = wNoPex.FormKey;

            // wNoVmad — a script-free weapon.
            var wNoVmad = mod.Weapons.AddNew(); wNoVmad.EditorID = "HcSpNoVmad";
            noVmadFk = wNoVmad.FormKey;

            // aliasQuest — a QUST whose script is attached to an ALIAS (QuestAdapter.Aliases[].Scripts), NOT the quest's
            // own Scripts. The alias script binds nothing, so its declared MySpell is unbound — a property the sweep must
            // find. Before the finding-1 fix this record produced NO report (a false clean on the headline quest case).
            var quest = mod.Quests.AddNew(); quest.EditorID = "HcSpAliasQuest";
            var qAdapter = new QuestAdapter();
            var qAlias = new QuestFragmentAlias { Property = new ScriptObjectProperty { Alias = 0 } };
            qAlias.Scripts.Add(new ScriptEntry { Name = "HcSpChild" });   // bound properties left empty → all declared are unbound
            qAdapter.Aliases.Add(qAlias);
            quest.VirtualMachineAdapter = qAdapter;
            aliasQuestFk = quest.FormKey;

            mod.BeginWrite.ToPath(pluginPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: could not synthesize the plugin: {ex.GetType().Name}: {(ex.InnerException ?? ex).Message}");
            return 1;
        }

        // ---- 3) run the REAL product path over the synthesized order + planted VFS. ----
        using var resolver = LoadOrderResolver.Build(new[] { pluginPath });
        using var assets = AssetResolver.Build("", "", tmpDir, Array.Empty<string>(), Array.Empty<ActiveArchive>());

        var res = ScriptPropertyCheck.Run(resolver, assets, null, 1000);
        if (!res.Success) { Console.Error.WriteLine($"error: sweep failed: {res.Error}"); return 1; }

        RecordScriptFindings? Rec(FormKey fk) => res.Reports.FirstOrDefault(r => r.Record == fk);
        var foot = Rec(footgunFk);
        bool FootUnbound(string name) => foot is not null && foot.Unbound.Any(u => u.PropertyName == name);

        Check("FOOTGUN-OBJECT: wFootgun's unbound set contains MySpell, typed OBJECT (the reported silent-None)",
            foot is not null && foot.Unbound.Any(u => u.PropertyName == "MySpell" && u.IsObjectType),
            foot is null ? "<no report>" : string.Join(",", foot.Unbound.Select(u => $"{u.PropertyName}/{(u.IsObjectType ? "obj" : "scalar")}")));

        Check("CHAIN-WALK: wFootgun's unbound set contains InheritedThing, declared in the ANCESTOR HcSpBase",
            foot is not null && foot.Unbound.Any(u => u.PropertyName == "InheritedThing"
                && string.Equals(u.DeclaringScript, "HcSpBase", StringComparison.OrdinalIgnoreCase)),
            foot is null ? "<no report>" : string.Join(",", foot.Unbound.Select(u => $"{u.PropertyName}@{u.DeclaringScript}")));

        Check("SCALAR-UNINIT: wFootgun's unbound set contains MyChance (Int, no default), typed SCALAR",
            foot is not null && foot.Unbound.Any(u => u.PropertyName == "MyChance" && !u.IsObjectType),
            foot is null ? "<no report>" : string.Join(",", foot.Unbound.Select(u => u.PropertyName)));

        Check("SCALAR-SUPPRESS: wFootgun's unbound set does NOT contain MyDefaulted (a baked default ⇒ not a finding)",
            foot is not null && !FootUnbound("MyDefaulted"),
            foot is null ? "<no report>" : string.Join(",", foot.Unbound.Select(u => u.PropertyName)));

        Check("BOUND-CLEAN: wFootgun's unbound set does NOT contain MyBoundSpell (it is bound + set)",
            foot is not null && !FootUnbound("MyBoundSpell"),
            foot is null ? "<no report>" : string.Join(",", foot.Unbound.Select(u => u.PropertyName)));

        Check("NULL-ADVISORY: wFootgun's bound-but-null set contains MyNullSpell",
            foot is not null && foot.NullObjects.Any(n => n.PropertyName == "MyNullSpell"),
            foot is null ? "<no report>" : string.Join(",", foot.NullObjects.Select(n => n.PropertyName)));

        Check("ALIAS-BOUND-NOT-NULL: MyAliasBound (Object null, Alias>=0) is NOT flagged bound-but-null and NOT unbound (finding 2)",
            foot is not null && !foot.NullObjects.Any(n => n.PropertyName == "MyAliasBound")
                && !FootUnbound("MyAliasBound"),
            foot is null ? "<no report>" : $"null=[{string.Join(",", foot.NullObjects.Select(n => n.PropertyName))}] unbound=[{string.Join(",", foot.Unbound.Select(u => u.PropertyName))}]");

        var aq = Rec(aliasQuestFk);
        Check("ALIAS-SCRIPT-SWEPT: a QUST's alias-attached script is checked — its unbound set contains MySpell (finding 1)",
            aq is not null && aq.Unbound.Any(u => u.PropertyName == "MySpell"),
            aq is null ? "<no report — alias scripts silently skipped>" : string.Join(",", aq.Unbound.Select(u => u.PropertyName)));

        Check("CLEAN-CONTROL: wClean (fully bound) produces NO report (the no-false-positive teeth)",
            Rec(cleanFk) is null,
            Rec(cleanFk) is null ? "" : $"unexpected report: {string.Join(",", Rec(cleanFk)!.Unbound.Select(u => u.PropertyName))} null=[{string.Join(",", Rec(cleanFk)!.NullObjects.Select(n => n.PropertyName))}]");

        Check("NO-VMAD-IGNORE: wNoVmad produces NO report (a script-free record is never nagged)",
            Rec(noVmadFk) is null);

        var noPex = Rec(noPexFk);
        Check("UNVERIFIABLE: wNoPex names HcSpNoPex unverifiable ('not on disk'), never a silent clean (Q3)",
            noPex is not null && noPex.Unverifiable.Any(u =>
                string.Equals(u.Script, "HcSpNoPex", StringComparison.OrdinalIgnoreCase)
                && u.Reason.Contains("not on disk", StringComparison.OrdinalIgnoreCase)),
            noPex is null ? "<no report>" : string.Join(" | ", noPex.Unverifiable.Select(u => $"{u.Script}: {u.Reason}")));

        Check("RECORDS-COUNT: exactly 4 records carry scripts (footgun, clean, noPex, aliasQuest — noVmad excluded)",
            res.RecordsWithScripts == 4, $"records-with-scripts={res.RecordsWithScripts}");

        var q3 = ScriptPropertyCheck.Run(resolver, assets, new[] { "HcSpNotReal.esp" }, 1000);
        Check("SCOPE-Q3: an unknown scope name fails LOUD ('not in the load order'), no reports",
            !q3.Success && q3.Reports.Count == 0 && q3.Error is not null
            && q3.Error.Contains("not in the load order", StringComparison.Ordinal),
            $"success={q3.Success} reports={q3.Reports.Count} err=[{q3.Error}]");

        // ---- #282: the record scope / property filter / class filter / counts_only knobs. limit= caps FINDINGS, not the
        //      record ROSTER, so a script-heavy plugin printed one header line per record and overflowed the tool-result
        //      cap no matter how low limit went — these are the narrower questions that had no call. ----
        var byFormid = ScriptPropertyCheck.Run(resolver, assets, null, 1000,
            new SweepScope(new HashSet<FormKey> { footgunFk }, null, null, null));
        Check("NARROW-FORMIDS: formids=[wFootgun] sweeps that record ALONE — 1 record with scripts, and the roster holds only it",
            byFormid.Success && byFormid.RecordsWithScripts == 1
            && byFormid.Reports.Count == 1 && byFormid.Reports[0].Record == footgunFk,
            $"success={byFormid.Success} withScripts={byFormid.RecordsWithScripts} reports={byFormid.Reports.Count}");

        var byEditorId = ScriptPropertyCheck.Run(resolver, assets, null, 1000,
            new SweepScope(null, "aliasquest", null, null));   // case-insensitive by contract
        Check("NARROW-EDITORID: editorid_contains='aliasquest' (case-insensitive) narrows to the QUST alone",
            byEditorId.Success && byEditorId.RecordsWithScripts == 1
            && byEditorId.Reports.Count == 1 && byEditorId.Reports[0].Record == aliasQuestFk,
            $"withScripts={byEditorId.RecordsWithScripts} reports={byEditorId.Reports.Count}");

        var byType = ScriptPropertyCheck.Run(resolver, assets, null, 1000,
            new SweepScope(null, null, new[] { typeof(IQuestGetter) }, "QUST"));
        Check("NARROW-TYPE: type=QUST keeps only the quest — the 3 scripted WEAPs never have their .pex chain read at all",
            byType.Success && byType.RecordsWithScripts == 1
            && byType.Reports.Count == 1 && byType.Reports[0].Record == aliasQuestFk,
            $"withScripts={byType.RecordsWithScripts} reports=[{string.Join(",", byType.Reports.Select(x => x.EditorId))}]");

        Check("NARROW-NOTE: a narrowed sweep carries a FilterNote saying the counts are scope-relative; an unnarrowed one carries none (Q3)",
            byFormid.FilterNote is not null && byFormid.FilterNote.Contains("NARROWED", StringComparison.Ordinal)
            && res.FilterNote is null,
            $"narrowed=[{byFormid.FilterNote}] unnarrowed=[{res.FilterNote}]");

        // property_contains=: chase ONE property. MySpell survives; the scalar MyChance and the ancestor's InheritedThing drop.
        var byProp = ScriptPropertyCheck.Run(resolver, assets, null, 1000, null, "myspell");
        var propFoot = byProp.Reports.FirstOrDefault(x => x.Record == footgunFk);
        Check("FILTER-PROPERTY: property_contains='myspell' keeps MySpell and drops MyChance + InheritedThing (case-insensitive)",
            byProp.Success && propFoot is not null
            && propFoot.Unbound.Any(u => u.PropertyName == "MySpell")
            && !propFoot.Unbound.Any(u => u.PropertyName is "MyChance" or "InheritedThing"),
            propFoot is null ? "<no report>" : string.Join(",", propFoot.Unbound.Select(u => u.PropertyName)));

        // findings=: the roster-cutting filter. unbound_object drops the scalar AND the bound-but-null advisory.
        var objectsOnly = ScriptPropertyCheck.Run(resolver, assets, null, 1000, null, null, ScriptFindingClass.UnboundObject);
        var objFoot = objectsOnly.Reports.FirstOrDefault(x => x.Record == footgunFk);
        Check("FILTER-CLASS: findings=[unbound_object] keeps MySpell (HIGH), drops the MyChance scalar and the MyNullSpell advisory",
            objectsOnly.Success && objFoot is not null
            && objFoot.Unbound.Any(u => u.PropertyName == "MySpell")
            && !objFoot.Unbound.Any(u => u.PropertyName == "MyChance")
            && objFoot.NullObjects.Count == 0,
            objFoot is null ? "<no report>" : $"unbound=[{string.Join(",", objFoot.Unbound.Select(u => u.PropertyName))}] null={objFoot.NullObjects.Count}");

        // #288 review finding 1: an EXCLUDED class must render as not-checked, never as a 0 that reads "looked, found
        // none". This arm previously asserted `TotalNullObject == 0` and so LOCKED IN the bug — it now asserts the
        // not-checked marker in both renders, in the partial case as well as the total one.
        var objText = Wire.RenderScriptCheck(objectsOnly, 0);
        var objJson = JsonWire.RenderScriptCheck(objectsOnly, 0);
        Check("CLASS-NOT-CHECKED-PARTIAL: findings=[unbound_object] renders 'unbound_scalar NOT CHECKED' + 'bound-but-null NOT CHECKED', and nulls both in json (never 0)",
            objText.Contains("object only — unbound_scalar NOT CHECKED", StringComparison.Ordinal)
            && objText.Contains("bound-but-null NOT CHECKED", StringComparison.Ordinal)
            && !objText.Contains("0 bound-but-null", StringComparison.Ordinal)
            && CheckErrorsProbe.JsonNull(objJson, "unbound_scalar")
            && CheckErrorsProbe.JsonNull(objJson, "bound_but_null")
            && CheckErrorsProbe.JsonMatches(objJson, "unbound_object", objectsOnly.TotalUnboundObject),
            $"header=[{objText.Split('\n').Skip(1).FirstOrDefault()}]");

        var nullOnly = ScriptPropertyCheck.Run(resolver, assets, null, 1000, null, null, ScriptFindingClass.BoundNull);
        var nullText = Wire.RenderScriptCheck(nullOnly, 0);
        var nullJson = JsonWire.RenderScriptCheck(nullOnly, 0);
        Check("CLASS-NOT-CHECKED-TOTAL: findings=[bound_null] renders 'unbound NOT CHECKED' and nulls unbound in json — the HIGH class nobody looked for never reads as 0",
            nullText.Contains("unbound NOT CHECKED", StringComparison.Ordinal)
            && !nullText.Contains("0 unbound", StringComparison.Ordinal)
            && CheckErrorsProbe.JsonNull(nullJson, "unbound")
            && CheckErrorsProbe.JsonNull(nullJson, "unbound_object")
            && nullOnly.TotalNullObject == 1,
            $"header=[{nullText.Split('\n').Skip(1).FirstOrDefault()}]");

        // THE LOAD-BEARING Q3 ARM: a filter that matches NOTHING must still surface the record whose .pex could not be
        // read — that script might be the very one declaring the property being filtered for, so hiding it would turn
        // "could not check" into a clean answer.
        var filteredToNothing = ScriptPropertyCheck.Run(resolver, assets, null, 1000, null, "ZZZnoSuchProperty",
            ScriptFindingClass.UnboundObject);
        var filteredNoPex = filteredToNothing.Reports.FirstOrDefault(x => x.Record == noPexFk);
        Check("UNVERIFIABLE-SURVIVES-FILTER: under a filter matching NOTHING, wNoPex is STILL reported unverifiable — a filter must never manufacture a clean (Q3)",
            filteredToNothing.Success && filteredToNothing.TotalUnbound == 0
            && filteredToNothing.TotalUnverifiable == 1
            && filteredNoPex is not null && filteredNoPex.Unverifiable.Any(u =>
                string.Equals(u.Script, "HcSpNoPex", StringComparison.OrdinalIgnoreCase)),
            $"unbound={filteredToNothing.TotalUnbound} unverifiable={filteredToNothing.TotalUnverifiable} noPexReport={(filteredNoPex is not null)}");

        Check("FILTER-Q3: an unrecognized findings= value fails LOUD, names the legal set, and says the unverifiable class is unfilterable",
            !SweepFindings.TryParseScriptClasses(new[] { "unbund_object" }, out _, out var spClassErr)
            && spClassErr is not null && spClassErr.Contains("'unbound_object'", StringComparison.Ordinal)
            && spClassErr.Contains("bound_null", StringComparison.Ordinal)
            && spClassErr.Contains("cannot be filtered out", StringComparison.Ordinal),
            $"err=[{spClassErr}]");

        // counts_only=: exact totals + an unbound-by-property-NAME histogram, with NO per-record roster built at all —
        // the reporter's shell pipeline (grep -oE '^  [!·] \w+' | sort | uniq -c) as one call.
        var counts = ScriptPropertyCheck.Run(resolver, assets, null, 1000, null, null, ScriptFindingClass.All, countsOnly: true);
        var histo = counts.Histogram;
        Check("COUNTS-ONLY: totals match the full sweep, NO per-record roster is built, and the histogram tallies unbound by property NAME",
            counts.Success && counts.CountsOnly && counts.TotalUnbound == res.TotalUnbound
            && counts.Reports.Count == 0 && !counts.Capped
            && histo is not null && histo.Sum(h => h.Count) == res.TotalUnbound
            && histo.Any(h => h.Key == "MySpell") && histo.Any(h => h.Key == "InheritedThing")
            && !histo.Any(h => h.Key == "MyDefaulted"),
            $"unbound={counts.TotalUnbound} (full={res.TotalUnbound}) reports={counts.Reports.Count} histo=[{(histo is null ? "<null>" : string.Join(",", histo.Select(h => $"{h.Key}={h.Count}")))}]");

        Check("COUNTS-ONLY-NOT-COMPUTED: a normal sweep leaves Histogram NULL — 'not computed' and 'nothing found' must not look alike (Q3)",
            res.Histogram is null && !res.CountsOnly, $"histo={(res.Histogram is null ? "null" : res.Histogram.Count.ToString())}");

        Check("JSON-PARITY: format=json parses and reports the same unbound total in both the listing and counts_only modes (D2 — one result, two renders)",
            CheckErrorsProbe.JsonMatches(JsonWire.RenderScriptCheck(res, 0), "unbound", res.TotalUnbound)
            && CheckErrorsProbe.JsonMatches(JsonWire.RenderScriptCheck(counts, 0), "unbound", res.TotalUnbound)
            && CheckErrorsProbe.JsonHasHistogram(JsonWire.RenderScriptCheck(counts, 0), "unbound_by_property"),
            "see the two json renders");

        // #288 RE-REVIEW finding 1: the round-1 histogram gate landed on check_errors and not on its twin here. With
        // both unbound classes excluded nothing can ever be tallied, so an empty-but-present histogram rendered
        // "nothing to tally" — and json emitted a machine-readable zero-unbound-properties next to "unbound": null.
        var countsNoTally = ScriptPropertyCheck.Run(resolver, assets, null, 1000, null, null,
            ScriptFindingClass.BoundNull, countsOnly: true);
        var noTallyText = Wire.RenderScriptCheck(countsNoTally, 0);
        var noTallyJson = JsonWire.RenderScriptCheck(countsNoTally, 0);
        Check("COUNTS-ONLY-NO-TALLY: counts_only + findings=[bound_null] leaves Histogram NULL, says nothing was tallied, and never claims 'nothing to tally'",
            countsNoTally.Success && countsNoTally.Histogram is null
            && countsNoTally.Reports.Count == 0                                   // the roster contract still holds without a histogram
            && noTallyText.Contains("excluded both unbound classes, so nothing was tallied", StringComparison.Ordinal)
            && !noTallyText.Contains("nothing to tally", StringComparison.Ordinal)
            && !noTallyJson.Contains("unbound_by_property", StringComparison.Ordinal),
            $"histo={(countsNoTally.Histogram is null ? "null" : countsNoTally.Histogram.Count.ToString())} reports={countsNoTally.Reports.Count}");

        // #288 RE-REVIEW finding 2: the header gained class-awareness; the capped tail restated the totals in its own
        // words and so reintroduced the literal "0 unbound" the header no longer prints. CLASS-NOT-CHECKED-TOTAL missed
        // it only because it runs at limit=1000 and never trips Capped.
        // Capped is forced rather than provoked: the fixture carries exactly ONE bound-but-null finding, so no limit=
        // can trip the cap on this arm's class. The contract under test is the RENDER's wording, which this reaches
        // directly. (CAPPED-TAIL-SYMMETRIC below trips the real cap, so the natural path is covered too.)
        var cappedNullOnly = nullOnly with { Capped = true };
        var cappedText = Wire.RenderScriptCheck(cappedNullOnly, 0);
        Check("CAPPED-TAIL-CLASS-AWARE: the capped tail says 'unbound NOT CHECKED' too — a small limit must not reintroduce the 0 the header dropped",
            cappedNullOnly.Capped
            && cappedText.Contains("finding list capped at limit", StringComparison.Ordinal)
            && cappedText.Contains("unbound NOT CHECKED", StringComparison.Ordinal)
            && !cappedText.Contains("0 unbound", StringComparison.Ordinal),
            $"tail=[{cappedText.Split('\n').FirstOrDefault(l => l.Contains("capped at limit", StringComparison.Ordinal))}]");

        var cappedObjOnly = ScriptPropertyCheck.Run(resolver, assets, null, 1, null, null, ScriptFindingClass.UnboundObject);
        var cappedObjText = Wire.RenderScriptCheck(cappedObjOnly, 0);
        Check("CAPPED-TAIL-SYMMETRIC: the same holds the other way — findings=[unbound_object] under a small limit never prints '0 bound-but-null'",
            cappedObjOnly.Capped
            && cappedObjText.Contains("bound-but-null NOT CHECKED", StringComparison.Ordinal)
            && !cappedObjText.Contains("0 bound-but-null", StringComparison.Ordinal),
            $"tail=[{cappedObjText.Split('\n').FirstOrDefault(l => l.Contains("capped at limit", StringComparison.Ordinal))}]");

        // #288 RE-REVIEW finding 3: a class filter narrows which findings are COUNTED, not which records were swept, so
        // the "not the whole plugin(s)" claim must not fire on findings= alone — the number it sits under is complete.
        // property_contains= DOES make a count a subset of its own label, so there the claim stays.
        // ROUND-3 review: property_contains= narrows the unbound and bound-but-null counts but NOT
        // records-with-scripts (incremented before any property filtering) or unverifiable (never property-gated, by
        // design). The blanket claim over those two was round-1 finding 3 again, one layer in — this arm previously
        // asserted the claim WAS present and so pinned it. ONE claim rule now: record scope only.
        Check("CLAIM-ONLY-WHEN-SCOPED: neither findings= nor property_contains= carries the not-the-whole-plugin claim; a record scope does",
            objectsOnly.FilterNote is { } cf && cf.Contains("NARROWED to findings=", StringComparison.Ordinal)
            && !cf.Contains("not the whole plugin(s)", StringComparison.Ordinal)
            && byProp.FilterNote is { } pf && pf.Contains("property_contains=", StringComparison.Ordinal)
            && !pf.Contains("not the whole plugin(s)", StringComparison.Ordinal)
            && byFormid.FilterNote is { } sf && sf.Contains("not the whole plugin(s)", StringComparison.Ordinal),
            $"class-only=[{objectsOnly.FilterNote}] prop-only=[{byProp.FilterNote}] scoped=[{byFormid.FilterNote}]");

        // …and because the claim is gone, the two counts property_contains= DOES narrow must say so themselves — while
        // the two it does not narrow must stay unlabelled, or the label would assert a narrowing that never happened.
        var propText = Wire.RenderScriptCheck(byProp, 0);
        var propHeader = propText.Split('\n').Skip(1).FirstOrDefault() ?? "";
        Check("PROP-FILTER-SELF-LABELS: unbound + bound-but-null carry \"matching 'myspell'\"; records-with-scripts and unverifiable do NOT (they are plugin-wide)",
            propHeader.Contains("unbound matching 'myspell'", StringComparison.OrdinalIgnoreCase)
            && propHeader.Contains("bound-but-null matching 'myspell'", StringComparison.OrdinalIgnoreCase)
            && !propHeader.Contains("record(s) with scripts matching", StringComparison.OrdinalIgnoreCase)
            && !propHeader.Contains("unverifiable matching", StringComparison.OrdinalIgnoreCase)
            // records-with-scripts stays the FULL count — the number the claim used to misrepresent
            && byProp.RecordsWithScripts == res.RecordsWithScripts
            && byProp.TotalUnverifiable == res.TotalUnverifiable
            && CheckErrorsProbe.JsonMatches(JsonWire.RenderScriptCheck(byProp, 0), "records_with_scripts", res.RecordsWithScripts),
            $"header=[{propHeader}]");

        Check("PROP-FILTER-IN-JSON: the property filter rides as DATA (property_contains), so a consumer can read which counts it narrowed",
            JsonDocument.Parse(JsonWire.RenderScriptCheck(byProp, 0)).RootElement
                .GetProperty("property_contains").GetString() == "myspell"
            && JsonDocument.Parse(JsonWire.RenderScriptCheck(res, 0)).RootElement
                .GetProperty("property_contains").ValueKind == JsonValueKind.Null,
            "see the two json renders");

        // #288 review finding 5: counts_only used to return before the excluded-plugins block, so the header's bare
        // count was all you got and you had to re-run without counts_only to learn WHICH plugin went unchecked.
        var withExcluded = counts with
        {
            ExcludedPlugins = new Dictionary<string, string> { ["HcSpBroken.esp"] = "header could not be parsed" },
        };
        Check("COUNTS-ONLY-EXCLUDED-NAMED: counts_only text still NAMES the unparseable plugins and their reasons, not just the header count",
            Wire.RenderScriptCheck(withExcluded, 0) is var xt
            && xt.Contains("excluded plugins (could not be parsed", StringComparison.Ordinal)
            && xt.Contains("HcSpBroken.esp", StringComparison.Ordinal)
            && xt.Contains("header could not be parsed", StringComparison.Ordinal),
            $"tail=[{Wire.RenderScriptCheck(withExcluded, 0).Split('\n').FirstOrDefault(l => l.Contains("HcSpBroken", StringComparison.Ordinal)) ?? "<absent>"}]");

        // The truncation notice used to advise "scope plugins=" — the one scope the caller had already applied. It must
        // now name knobs that exist (#282), or the tool tells you to do the thing that did not work.
        var tinyCap = Wire.RenderScriptCheck(res, 400);
        Check("TRUNCATION-HINT: the max_chars cut names the narrowing knobs that EXIST, and no longer advises 'scope plugins='",
            tinyCap.Contains("truncated at max_chars", StringComparison.Ordinal)
            && tinyCap.Contains("formids=", StringComparison.Ordinal)
            && tinyCap.Contains("counts_only=true", StringComparison.Ordinal)
            && !tinyCap.Contains("scope plugins=", StringComparison.Ordinal),
            $"tail=[{tinyCap.Split('\n').LastOrDefault(l => l.Contains("truncated", StringComparison.Ordinal))}]");

        // The excluded-plugin roster's HEAD is charged to max_chars now (it used to be printed unconditionally), so
        // a tight cap can drop the whole roster — and the marker that says so used to be a bare
        // "... [truncated at max_chars]" that leant on a header line which may no longer be there. A marker has to
        // name its own subject. Both directions: at an ample cap the roster is named and there is no marker.
        var excludedFat = counts with
        {
            ExcludedPlugins = Enumerable.Range(0, 3)
                .ToDictionary(i => $"HcSpBroken{i}.esp", i => new string('r', 300)),
        };
        var rosterWhole = Wire.RenderScriptCheck(excludedFat, 0);
        // The NONE-FIT cap, searched for rather than named: which one admits the response but not a single roster row
        // is a fact about the fixture.
        var rosterCut = "";
        for (int cap = 200; cap <= 4000 && !rosterCut.Contains("did not fit max_chars", StringComparison.Ordinal); cap += 20)
            rosterCut = Wire.RenderScriptCheck(excludedFat, cap);
        Check("EXCLUDED-ROSTER-CUT-NAMES-ITS-SUBJECT: when max_chars leaves no room for the excluded-plugin roster, the marker says WHAT did not fit and how many plugins are unnamed — it no longer relies on a header line that can itself be dropped",
            rosterCut.Contains("the excluded-plugin list did not fit max_chars", StringComparison.Ordinal)
            && RosterUnnamed(rosterCut) == 3
            && RosterNamed(excludedFat, rosterCut) == 0
            // and the other direction: an ample cap names them and says nothing about a cut
            && rosterWhole.Contains("HcSpBroken0.esp", StringComparison.Ordinal)
            && !rosterWhole.Contains("did not fit max_chars", StringComparison.Ordinal),
            $"named={RosterNamed(excludedFat, rosterCut)} stated={RosterUnnamed(rosterCut)}");

        // THE PARTIAL CUT, in both lanes that carry the marker. The search above stops at the FIRST cap that produces
        // it, which is by construction the one where no row fits — so the case where the roster names some of its
        // plugins and then says none of them were named was pinned by nothing. Every cap in the band is walked, and
        // the marker's number is asserted against rows counted out of the response.
        var partialBad = new List<string>();
        RosterMarkerCountsItsRows("counts_only", excludedFat, partialBad);
        // The listing lane's copy of the same marker. Its roster is only reached while the record list itself was NOT
        // cut, so the fixture keeps the records and fattens the roster — the same shape, one call site over.
        var listingFat = res with
        {
            ExcludedPlugins = Enumerable.Range(0, 3)
                .ToDictionary(i => $"HcSpBroken{i}.esp", i => new string('r', 300)),
        };
        RosterMarkerCountsItsRows("listing", listingFat, partialBad);
        Check("EXCLUDED-ROSTER-CUT-COUNTS-THE-ROWS-IT-DROPPED: at a cap that names SOME of the unparseable plugins and not the rest, the marker states how many are UNNAMED — the total, printed over a response that named one of them, is a sentence its own lines contradict",
            partialBad.Count == 0,
            partialBad.Count == 0 ? "both lanes state total minus the rows they emitted" : string.Join("; ", partialBad));

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "script-property-check-guard: ALL PASS" : $"script-property-check-guard: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>How many of the fixture's unparseable plugins this response actually NAMES, counted off its own lines
    /// rather than inferred from a cap.</summary>
    static int RosterNamed(ScriptCheckResult r, string text)
        => r.ExcludedPlugins.Keys.Count(k => text.Contains("  " + k + ": ", StringComparison.Ordinal));

    /// <summary>The number the roster-cut marker states, or -1 where it states none.</summary>
    static int RosterUnnamed(string text)
    {
        const string lead = "did not fit max_chars — ";
        int at = text.IndexOf(lead, StringComparison.Ordinal);
        if (at < 0) return -1;
        var digits = new string(text[(at + lead.Length)..].TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : -1;
    }

    /// <summary>Walk the caps where the roster marker fires and hold its number against the rows the response
    /// actually carries. EVERY cap in the band, not the first that produces a marker: the first is always the
    /// none-fit case, and the defect lives at the caps above it. A band that never splits is reported as a gap in the
    /// fixture rather than passed over — an arm that never saw a partial cut proves nothing about one.</summary>
    static void RosterMarkerCountsItsRows(string lane, ScriptCheckResult r, List<string> bad)
    {
        bool sawPartial = false;
        for (int cap = 200; cap <= 6000; cap += 20)
        {
            var text = Wire.RenderScriptCheck(r, cap);
            int stated = RosterUnnamed(text);
            if (stated < 0) continue;
            int named = RosterNamed(r, text);
            if (named > 0) sawPartial = true;
            if (stated != r.ExcludedPlugins.Count - named)
                bad.Add($"{lane}@{cap}: {named} named, marker says {stated} unnamed of {r.ExcludedPlugins.Count}");
        }
        if (!sawPartial) bad.Add($"{lane}: no cap in 200..6000 names SOME of the roster and cuts the rest");
    }

    // ---- fixture builders --------------------------------------------------------------------------

    /// <summary>A VMAD binding ONE script <paramref name="scriptClass"/> with the given bound properties.</summary>
    static VirtualMachineAdapter Vmad(string scriptClass, params ScriptProperty[] props)
    {
        var entry = new ScriptEntry { Name = scriptClass };
        foreach (var p in props) entry.Properties.Add(p);
        var vmad = new VirtualMachineAdapter();
        vmad.Scripts.Add(entry);
        return vmad;
    }

    static ScriptObjectProperty ObjProp(string name, FormKey obj)
    {
        var p = new ScriptObjectProperty { Name = name, Alias = -1 };
        if (!obj.IsNull) p.Object.SetTo(obj);
        return p;
    }

    /// <summary>An object property bound to a quest ALIAS (Alias &gt;= 0), Object left null — the healthy shape that must
    /// NOT read as bound-but-null (PR #145 review finding 2).</summary>
    static ScriptObjectProperty AliasProp(string name, short alias) => new() { Name = name, Alias = alias };

    static ScriptIntProperty IntProp(string name, int data) => new() { Name = name, Data = data };

    /// <summary>One Auto property to plant in a .pex: the property record + its backing variable (every auto property
    /// has one, <c>::Name_var</c> — an object/scalar with no initializer carries Null data, an initialized scalar
    /// carries the baked literal). Modeled on a real Skyrim .pex: an Auto property is Flags = Read|Write|AutoVar with
    /// NO handler functions (the backing var IS the handler), a non-null DocString, and the autovar name set.</summary>
    sealed record Decl(PexObjectProperty Prop, PexObjectVariable Backing);

    static Decl Auto(string name, string typeName, int? initInt)
    {
        var prop = new PexObjectProperty
        {
            Name = name,
            TypeName = typeName,
            DocString = "",
            Flags = PropertyFlags.Read | PropertyFlags.Write | PropertyFlags.AutoVar,
            AutoVarName = $"::{name}_var",
        };
        var data = initInt is int v
            ? new PexObjectVariableData { VariableType = VariableType.Integer, IntValue = v }
            : new PexObjectVariableData { VariableType = VariableType.Null };
        var backing = new PexObjectVariable { Name = $"::{name}_var", TypeName = typeName, VariableData = data };
        return new Decl(prop, backing);
    }

    /// <summary>An Auto object/form property (no baked default — a FormID can't be a literal).</summary>
    static Decl AutoObj(string name, string typeName) => Auto(name, typeName, null);

    /// <summary>An Auto scalar property, optionally with a baked initializer on its backing variable.</summary>
    static Decl AutoScalar(string name, string typeName, int? initInt) => Auto(name, typeName, initInt);

    /// <summary>Write a single-object .pex with the given Auto properties + backing variables to <paramref name="path"/>
    /// via Mutagen's native Pex writer. The object carries a non-null DocString, an empty auto-state, and the empty
    /// '' state — the minimal byte-valid shell a real Skyrim .pex has (verified round-trippable, see --diag).</summary>
    static void WritePex(string path, string name, string? parent, params Decl[] decls)
    {
        var obj = new PexObject { Name = name, ParentClassName = parent ?? "", DocString = "", AutoStateName = "" };
        obj.States.Add(new PexObjectState { Name = "" });
        foreach (var d in decls) { obj.Properties.Add(d.Prop); obj.Variables.Add(d.Backing); }

        var pex = new PexFile(GameCategory.Skyrim)
        {
            MajorVersion = 3,
            MinorVersion = 2,
            GameId = 1,
            CompilationTime = default,
            SourceFileName = name + ".psc",
            Username = "hc",
            MachineName = "ci",
        };
        pex.Objects.Add(obj);
        pex.WritePexFile(path, GameCategory.Skyrim);   // Mutagen 0.53.1 PexMixIn.WritePexFile(outputPath, gameCategory)
    }
}

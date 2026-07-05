using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Pex;
using HousecarlCore;

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
///                              init), MyDefaulted (Int = 5, baked init), MyNullSpell (Spell).
///   • wFootgun — VMAD binds HcSpChild with ONLY { MyBoundSpell→set, MyNullSpell→null }. The reported failure:
///                MySpell + MyChance + InheritedThing declared-but-unbound; MyDefaulted suppressed (has a default);
///                MyBoundSpell clean; MyNullSpell bound-but-null.
///   • wClean   — VMAD binds HcSpChild with ALL six properties, every object one non-null. The no-false-positive
///                control: a fully-bound record must produce ZERO findings.
///   • wNoPex   — VMAD binds HcSpNoPex, whose .pex is NOT planted → UNVERIFIABLE, never passed clean (Q3).
///   • wNoVmad  — a script-free weapon → NOT counted, NEVER nagged.
///
/// Arms (ALL required — a GREEN must mean "the contract holds"):
///   PEX-ROUNDTRIP   — the planted HcSpChild.pex reads back with MySpell as an Auto property (the fixture is valid).
///   FOOTGUN-OBJECT  — wFootgun's unbound set contains MySpell, typed as an OBJECT (the silent-None class, the report).
///   CHAIN-WALK      — wFootgun's unbound set contains InheritedThing, declared in the ANCESTOR HcSpBase.
///   SCALAR-UNINIT   — wFootgun's unbound set contains MyChance (Int, no baked default), typed as a SCALAR.
///   SCALAR-SUPPRESS — wFootgun's unbound set does NOT contain MyDefaulted (a baked default ⇒ not a silent-wrong).
///   BOUND-CLEAN     — wFootgun's unbound set does NOT contain MyBoundSpell (it is bound + set).
///   NULL-ADVISORY   — wFootgun's bound-but-null set contains MyNullSpell (the same None, advisory).
///   CLEAN-CONTROL   — wClean produces NO report (fully bound ⇒ zero findings: the no-false-positive teeth).
///   NO-VMAD-IGNORE  — wNoVmad produces NO report and is not counted among records-with-scripts.
///   UNVERIFIABLE    — wNoPex names HcSpNoPex unverifiable ("not on disk"), never a silent clean (Q3).
///   RECORDS-COUNT   — exactly 3 records carry scripts (footgun, clean, noPex — noVmad excluded).
///   SCOPE-Q3        — scope=[a name not in the order] fails LOUD ("not in the load order"), no reports.
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
        FormKey footgunFk, cleanFk, noPexFk, noVmadFk;
        try
        {
            var mod = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
            var self = new FormKey(mKey, 0x000801);   // a non-null in-plugin FormKey for "bound" object props (target need not exist — only IsNull is read)

            // wFootgun — the reported failure shape.
            var wFootgun = mod.Weapons.AddNew(); wFootgun.EditorID = "HcSpFootgun";
            wFootgun.VirtualMachineAdapter = Vmad("HcSpChild",
                ObjProp("MyBoundSpell", self), ObjProp("MyNullSpell", FormKey.Null));
            footgunFk = wFootgun.FormKey;

            // wClean — fully bound: the no-false-positive control.
            var wClean = mod.Weapons.AddNew(); wClean.EditorID = "HcSpClean";
            wClean.VirtualMachineAdapter = Vmad("HcSpChild",
                ObjProp("MySpell", self), ObjProp("MyBoundSpell", self), ObjProp("MyNullSpell", self),
                ObjProp("InheritedThing", self), IntProp("MyChance", 3), IntProp("MyDefaulted", 5));
            cleanFk = wClean.FormKey;

            // wNoPex — a script with no compiled .pex on disk.
            var wNoPex = mod.Weapons.AddNew(); wNoPex.EditorID = "HcSpNoPex";
            wNoPex.VirtualMachineAdapter = Vmad("HcSpNoPex");
            noPexFk = wNoPex.FormKey;

            // wNoVmad — a script-free weapon.
            var wNoVmad = mod.Weapons.AddNew(); wNoVmad.EditorID = "HcSpNoVmad";
            noVmadFk = wNoVmad.FormKey;

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

        Check("RECORDS-COUNT: exactly 3 records carry scripts (footgun, clean, noPex — noVmad excluded)",
            res.RecordsWithScripts == 3, $"records-with-scripts={res.RecordsWithScripts}");

        var q3 = ScriptPropertyCheck.Run(resolver, assets, new[] { "HcSpNotReal.esp" }, 1000);
        Check("SCOPE-Q3: an unknown scope name fails LOUD ('not in the load order'), no reports",
            !q3.Success && q3.Reports.Count == 0 && q3.Error is not null
            && q3.Error.Contains("not in the load order", StringComparison.Ordinal),
            $"success={q3.Success} reports={q3.Reports.Count} err=[{q3.Error}]");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "script-property-check-guard: ALL PASS" : $"script-property-check-guard: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
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

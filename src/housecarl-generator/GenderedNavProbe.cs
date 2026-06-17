using System.Reflection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD (standing CI instrument) for HCBR-2026-06-15-01 PR-H — gendered-item [0]/[1] navigation.
///
/// THE GAP (verification 2.4 PARTIAL + 4.4 CONFIRMED): a <c>GenderedItem&lt;T&gt;</c> field (Armor.WorldModel,
/// .Male/.Female) RENDERS in a depth read as <c>WorldModel[0]</c>/<c>[1]</c> (it's IEnumerable), but feeding that
/// same form back was unreadable (<c>StepIntoElement</c> knew only IList/IDictionary → "not a navigable collection")
/// and unwritable (pre-flight rejected a bracket on a substruct → "indexes a substruct, which is not a collection").
/// The named arms (<c>WorldModel.Male.Model.File</c>) worked; the defect was the render-vs-input asymmetry (Q3 — a
/// form the tool SHOWS must work).
///
/// THE FIX (Option A — Aaron 2026-06-16: a true read+write alias), by construction, never a hand-listed type set:
///  • engine — <see cref="WriteEngine"/>.<c>StepIntoElement</c> recognises the runtime open-generic
///    <c>IGenderedItem&lt;&gt;</c> and maps <c>[0]</c>=Male/<c>[1]</c>=Female to the named arm, materializing-and-
///    writing-back an absent pair/arm on a WRITE through the SAME <c>MaterializeSubstruct</c> setter the named plain
///    hop uses (a freshly-built arm is written back, never a silently-dropped orphan — the Q3 trap);
///  • pre-flight — <c>CorpusRulebook</c> recognises the corpus-side <c>"GenderedItem&lt;T&gt;"</c> TypeRef and descends
///    to arm type T (two recognisers — runtime + corpus — that must agree; not one shared schema-blind classifier);
///  • render — <c>ReadEngine.Expand</c> renders the pair via the SAME index→arm mapping (<see cref="WriteEngine.GenderedArmNames"/>),
///    so the <c>[0]/[1]</c> a read SHOWS is the one a write/read ACCEPTS by construction. Numeric <c>[0]/[1]</c> kept
///    (FieldsDiff treats the root as a positional list, unchanged).
///
/// RED-&gt;GREEN teeth (all RED with the fix reverted): A1 (bracket READ resolves to the named value), A2-PF (pre-flight
/// ACCEPTS the bracket write), A2 (bracket WRITE lands AND the materialized arm is read back via the NAMED arm — the
/// write-back/orphan tooth), A3 (the rendered [0]/[1] path re-feeds to the SHOWN value — render↔navigate agreement),
/// A4-IDX (a bad index [2] is refused with the gendered hint), A4-SCALAR (stepping into a scalar/value arm is refused,
/// naming the by-name fix), SER (a bracket-written arm PERSISTS through serialize→reopen). CONTROLS (green before+after):
/// A4-SUB (a non-gendered substruct bracket stays refused — the gendered branch is gated, doesn't leak), C-LIST (a real
/// list element still navigates — the StepIntoElement refactor didn't break list nav), SET (the settability fact the
/// materialize-write-back relies on — GenderedItem&lt;T&gt;.Male is settable; the named-from-null path materializes it).
///
/// Self-contained: pure in-memory Mutagen records + the GENERATED corpus.json (built into a unique temp dir on a fresh
/// checkout, exactly as formlink-null-guard / nullarm-guard do); no Skyrim.esm, no MO2.
///
/// Run: <c>dotnet run --project src/housecarl-generator gendered-nav-guard</c>
/// </summary>
public static class GenderedNavProbe
{
    public static int RunGuard(string[] args)
    {
        // CI-safe corpus: corpus.json is GENERATED, not tracked — on a fresh checkout build it into a UNIQUE temp dir
        // and point the rulebook there (the pre-flight arms need it), leaving the working tree untouched.
        string? tmp = null;
        if (!File.Exists(CorpusRulebook.CorpusPath))
        {
            tmp = Path.Combine(Path.GetTempPath(), "housecarl-gendered-nav-guard-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            Console.WriteLine($"corpus.json absent — generating into {tmp} (CI / fresh checkout)…");
            var rc = CorpusGenerator.GenerateAll(Path.Combine(tmp, "generated"), Path.Combine(tmp, "refs"));
            if (rc != 0) { Console.Error.WriteLine("error: corpus generation failed"); return rc; }
            CorpusRulebook.CorpusPath = Path.Combine(tmp, "generated", "corpus.json");
        }
        try { return RunChecks(); }
        finally { if (tmp is not null) { try { Directory.Delete(tmp, recursive: true); } catch { /* best-effort */ } } }
    }

    static int RunChecks()
    {
        int failures = 0;
        void Check(string label, bool ok, string? detail = null)
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}{(ok || detail is null ? "" : $"\n        -> {detail}")}");
            if (!ok) failures++;
        }

        Console.WriteLine("gendered-nav-guard — gendered-item [0]/[1] navigable alias, read+write (HCBR PR-H)");
        Console.WriteLine();

        var rb = CorpusRulebook.Load();

        // ---- helpers --------------------------------------------------------
        static Armor FreshArmor() =>
            new SkyrimMod(new ModKey("hc_gendered_nav", ModType.Plugin), SkyrimRelease.SkyrimSE).Armors.AddNew();
        static WriteRequest Set(string recordType, string dotted, string value) => new()
        {
            RecordType = recordType,
            Path = dotted.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Verb = "Set",
            Value = value,
        };
        static FieldValue Read(IMajorRecordGetter rec, string path) =>
            ReadEngine.ReadFields(rec, new[] { path }, depth: 1).Fields[0];
        static string? Tok(FieldValue? f) => f is { HasValue: true } ? f.Token : null;

        const string maleNif = "hcgnav_male.nif";
        const string femaleNif = "hcgnav_female.nif";

        // ---- S0 (honesty): the corpus + runtime shapes this guard rests on. Keeps it from passing green if a Mutagen
        //      reshape moves WorldModel off GenderedItem or drops the ArmorModel arm type from the corpus. ----
        var armoWm = typeof(IArmorGetter).GetProperty("WorldModel")?.PropertyType;
        Check("S0: corpus has Armor + ArmorModel arm type; WorldModel is a runtime gendered field",
            rb.Type("Armor") is not null && rb.Type("ArmorModel") is not null
            && armoWm is not null && IsGenderedType(armoWm),
            $"Armor={rb.Type("Armor") is not null} ArmorModel={rb.Type("ArmorModel") is not null} WorldModel={armoWm?.Name}");

        // ---- SET (the load-bearing fact, control): GenderedItem<T>.Male is SETTABLE, and the named-from-null path
        //      materializes + writes it back. This is what the bracket-write's MaterializeSubstruct reuse depends on;
        //      asserted directly so a Mutagen reshape to an immutable pair fails LOUD here, not mysteriously in A2. ----
        bool setOk; string? setDetail;
        try
        {
            var a = FreshArmor();
            WriteEngine.ApplyVerb(a, Set("Armor", "WorldModel.Male.Model.File", maleNif));   // named path, FROM NULL
            var g = a.WorldModel;
            var maleProp = g?.GetType().GetProperty("Male");
            setOk = g is not null && maleProp is { CanWrite: true } && g.Male is not null;
            setDetail = $"pair={(g is null ? "null" : g.GetType().Name)} Male.CanWrite={maleProp?.CanWrite} Male={(g?.Male is null ? "null" : "set")}";
        }
        catch (Exception ex) { setOk = false; setDetail = $"{ex.GetType().Name}: {ex.Message}"; }
        Check("SET: GenderedItem<T>.Male is settable; named-from-null materializes + writes back", setOk, setDetail);

        // ---- A1 (tooth — read alias): with both arms set by the NAMED path, the bracket read [0]/[1] resolves to the
        //      SAME value the named read does. RED before: StepIntoElement threw → "(unreadable)" → no value. ----
        bool a1Ok; string? a1Detail;
        try
        {
            var a = FreshArmor();
            WriteEngine.ApplyVerb(a, Set("Armor", "WorldModel.Male.Model.File", maleNif));
            WriteEngine.ApplyVerb(a, Set("Armor", "WorldModel.Female.Model.File", femaleNif));
            var b0 = Read(a, "WorldModel[0].Model.File"); var n0 = Read(a, "WorldModel.Male.Model.File");
            var b1 = Read(a, "WorldModel[1].Model.File"); var n1 = Read(a, "WorldModel.Female.Model.File");
            a1Ok = b0.HasValue && b1.HasValue && Tok(b0) == Tok(n0) && Tok(b1) == Tok(n1);
            a1Detail = $"[0]={Tok(b0)} (.Male={Tok(n0)}) | [1]={Tok(b1)} (.Female={Tok(n1)})";
        }
        catch (Exception ex) { a1Ok = false; a1Detail = $"{ex.GetType().Name}: {ex.Message}"; }
        Check("A1: bracket READ WorldModel[0]/[1] == the named .Male/.Female read", a1Ok, a1Detail);

        // ---- A2-PF (tooth — pre-flight): the bracket write is now ACCEPTED. RED before: rejected "not a collection". ----
        var a2pf = rb.Validate(Set("Armor", "WorldModel[1].Model.File", femaleNif));
        Check("A2-PF: pre-flight ACCEPTS a gendered bracket write (was rejected as 'not a collection')",
            a2pf is null, a2pf);

        // ---- A2 (tooth — write + write-back/orphan): a bracket WRITE to an absent arm lands, AND the materialized arm
        //      is reachable via the NAMED arm (proving it was written back to the pair, not left an orphan the sub-field
        //      write silently lost). RED before: ApplyVerb threw. The NAMED read HasValue is the orphan tooth. ----
        bool a2Ok; string? a2Detail;
        try
        {
            var a = FreshArmor();                                                   // WorldModel null → must materialize pair + Female arm
            WriteEngine.ApplyVerb(a, Set("Armor", "WorldModel[1].Model.File", femaleNif));
            var named = Read(a, "WorldModel.Female.Model.File");                     // orphan? → absent
            var brack = Read(a, "WorldModel[1].Model.File");
            a2Ok = named.HasValue && Tok(named) == Tok(brack)
                && (Tok(named)?.Contains("female", StringComparison.OrdinalIgnoreCase) ?? false)
                && a.WorldModel?.Male is null;                                       // we only touched [1]; Male stays absent
            a2Detail = $"named.HasValue={named.HasValue} named={Tok(named)} bracket={Tok(brack)} Male={(a.WorldModel?.Male is null ? "null(correct)" : "UNEXPECTEDLY set")}";
        }
        catch (Exception ex) { a2Ok = false; a2Detail = $"{ex.GetType().Name}: {ex.Message}"; }
        Check("A2: bracket WRITE [1] lands AND is read back via the named arm (write-back, not an orphan)", a2Ok, a2Detail);

        // ---- A3 (tooth — render↔navigate agreement): depth-render the pair, then re-feed the EXACT path the render
        //      emitted for index 0/1 and confirm it resolves to the value the render SHOWED — and that [0] is Male,
        //      [1] is Female (the order pin). RED before: re-feeding the rendered bracket path threw. ----
        bool a3Ok; string? a3Detail;
        try
        {
            var a = FreshArmor();
            WriteEngine.ApplyVerb(a, Set("Armor", "WorldModel.Male.Model.File", maleNif));
            WriteEngine.ApplyVerb(a, Set("Armor", "WorldModel.Female.Model.File", femaleNif));
            var rendered = ReadEngine.ReadFields(a, new[] { "WorldModel" }, depth: 6).Fields;
            var r0 = rendered.FirstOrDefault(f => f.Path == "WorldModel[0].Model.File");
            var r1 = rendered.FirstOrDefault(f => f.Path == "WorldModel[1].Model.File");
            var refed0 = Read(a, "WorldModel[0].Model.File");     // re-feed the SHOWN path
            bool order = (Tok(r0)?.Contains("male", StringComparison.OrdinalIgnoreCase) ?? false)
                      && (Tok(r1)?.Contains("female", StringComparison.OrdinalIgnoreCase) ?? false);
            a3Ok = r0 is { HasValue: true } && r1 is { HasValue: true } && order && Tok(refed0) == Tok(r0);
            a3Detail = $"rendered[0]={Tok(r0)} rendered[1]={Tok(r1)} refed[0]={Tok(refed0)} order={order}";
        }
        catch (Exception ex) { a3Ok = false; a3Detail = $"{ex.GetType().Name}: {ex.Message}"; }
        Check("A3: the rendered [0]/[1] path re-feeds to the shown value; [0]=Male, [1]=Female (render↔nav agree)", a3Ok, a3Detail);

        // ---- A4-SUB (control): a NON-gendered substruct bracket stays refused (the gendered branch is gated on the
        //      recogniser, doesn't leak to every substruct). Pre-flight rejects AND the engine throws. Green ±fix. ----
        var subPf = rb.Validate(Set("Armor", "BodyTemplate[0].ArmorType", "LightArmor"));
        bool subThrew = Throws(() => WriteEngine.ApplyVerb(FreshArmor(), Set("Armor", "BodyTemplate[0].ArmorType", "LightArmor")));
        Check("A4-SUB: a non-gendered substruct bracket (BodyTemplate[0]) is refused at pre-flight AND the engine",
            subPf is not null && subPf.Contains("not a collection", StringComparison.OrdinalIgnoreCase) && subThrew,
            $"preflight={subPf} engineThrew={subThrew}");

        // ---- A4-IDX (tooth — index guard): a bad gendered index ([2]) is refused with the gendered hint. RED before:
        //      it was refused too, but as "not a collection" (no gendered hint) — asserting the hint makes it a tooth. ----
        var idxPf = rb.Validate(Set("Armor", "WorldModel[2].Model.File", maleNif));
        bool idxThrew = Throws(() => WriteEngine.ApplyVerb(FreshArmor(), Set("Armor", "WorldModel[2].Model.File", maleNif)));
        Check("A4-IDX: gendered index [2] is refused naming [0] male / [1] female, at pre-flight AND the engine",
            idxPf is not null && idxPf.Contains("male", StringComparison.OrdinalIgnoreCase) && idxThrew,
            $"preflight={idxPf} engineThrew={idxThrew}");

        // ---- A4-SCALAR (tooth — scalar/value arm): stepping INTO a gendered scalar (Priority = GenderedItem<Byte>) or
        //      formlink (SkinTexture = GenderedItem<FormLinkNullable<TextureSet>>) arm is refused, naming the by-name
        //      fix. RED before: refused as "not a collection" (no by-name hint). ArmorAddon owns these. ----
        Check("S0b: corpus has ArmorAddon with scalar gendered arms (Priority / SkinTexture)",
            rb.Type("ArmorAddon") is not null);
        var scalPf = rb.Validate(Set("ArmorAddon", "Priority[0].Anything", "1"));
        var flPf = rb.Validate(Set("ArmorAddon", "SkinTexture[0].Anything", "1"));
        Check("A4-SCALAR: stepping into a gendered scalar/formlink arm is refused, naming the by-name fix",
            scalPf is not null && scalPf.Contains("by name", StringComparison.OrdinalIgnoreCase)
            && flPf is not null && flPf.Contains("by name", StringComparison.OrdinalIgnoreCase),
            $"Priority[0]={scalPf}\n        -> SkinTexture[0]={flPf}");

        // ---- VTYPE (by-construction Q3 guard — the orphan trap, corpus-wide): every gendered arm pre-flight ACCEPTS
        //      for descent (a corpus-resolvable arm type) MUST be a REFERENCE type. A VALUE-type arm would be returned
        //      BOXED by armProp.GetValue, so a sub-field write would land on the copy and silently vanish — the same
        //      orphan the write-back guards against for a null ref arm, but undetectable because a value-type arm is
        //      never null (the materialize-write-back branch is skipped). The engine's design rests on "things you
        //      descend into are reference types; value types are whole-coercible leaves" — this proves that holds for
        //      EVERY gendered arm in the corpus, not just ArmorModel. If a value-type descendable arm ever appears,
        //      this goes RED and names it (a pre-existing risk shared with the named .Male/.Female path — surface, fix
        //      both, never guess). Scalar/value/formlink arms are excluded: their armRef doesn't resolve as a corpus
        //      type, so pre-flight already refuses to descend (A4-SCALAR). ----
        var corpus = CorpusRulebook.LoadCorpus();
        var vtypeOffenders = new List<string>();
        int descendableArms = 0;
        foreach (var (tname, tschema) in corpus.Types)
            foreach (var fld in tschema.Fields)
            {
                if (fld.Cardinality != "substruct" || fld.TypeRef is not { } tr
                    || !tr.StartsWith("GenderedItem<", StringComparison.Ordinal)) continue;
                var armRef = InnerArm(tr);
                if (armRef is null || rb.Type(armRef) is null) continue;   // scalar/value/formlink arm → descent already refused
                descendableArms++;
                var armType = ArmRuntimeType(fld.MutableTypeAssemblyQualified ?? fld.GetterTypeAssemblyQualified);
                if (armType is null) vtypeOffenders.Add($"{tname}.{fld.Name} ({armRef}: arm runtime type unresolved)");
                else if (armType.IsValueType) vtypeOffenders.Add($"{tname}.{fld.Name} ({armRef}: VALUE type → write-back would orphan)");
            }
        Check($"VTYPE: all {descendableArms} descendable gendered arms are reference types (write-back can't orphan)",
            vtypeOffenders.Count == 0, vtypeOffenders.Count == 0 ? null : string.Join("; ", vtypeOffenders.Take(8)));

        // ---- C-LIST (control): a real list element still navigates after the StepIntoElement refactor (the gendered
        //      branch was prepended, not substituted). Add a keyword, then read Keywords[0]. Green ±fix. ----
        bool listOk; string? listDetail;
        try
        {
            var a = FreshArmor();
            WriteEngine.ApplyVerb(a, new WriteRequest { RecordType = "Armor", Path = new[] { "Keywords" }, Verb = "Add", Value = "012345:Skyrim.esm" });
            var k0 = Read(a, "Keywords[0]");
            listOk = k0.HasValue;
            listDetail = $"Keywords[0]={Tok(k0)}";
        }
        catch (Exception ex) { listOk = false; listDetail = $"{ex.GetType().Name}: {ex.Message}"; }
        Check("C-LIST: a list element (Keywords[0]) still navigates after the refactor", listOk, listDetail);

        // ---- SER (tooth — end-to-end): a bracket-written gendered arm PERSISTS through serialize → reopen, reachable
        //      via the named arm on the reopened (getter) record. RED before: the write threw during build. ----
        var (serOk, serDetail) = TrySerialize("hc_gendered_nav_ser", mod =>
        {
            var a = mod.Armors.AddNew();
            WriteEngine.ApplyVerb(a, Set("Armor", "WorldModel[1].Model.File", femaleNif));
        }, reopened =>
        {
            var armo = reopened.Armors.FirstOrDefault();
            if (armo is null) return (false, "no Armor in reopened patch");
            var named = Read(armo, "WorldModel.Female.Model.File");
            var ok = named.HasValue && (Tok(named)?.Contains("female", StringComparison.OrdinalIgnoreCase) ?? false);
            return (ok, $"reopened WorldModel.Female.Model.File = {Tok(named)} (HasValue={named.HasValue})");
        });
        Check("SER: a bracket-written gendered arm persists through serialize→reopen (named-readable)", serOk, serDetail);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "gendered-nav-guard: ALL PASS" : $"gendered-nav-guard: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>True iff a type carries a gendered interface (concrete <c>GenderedItem&lt;T&gt;</c> or
    /// <c>IGenderedItem(Getter)&lt;T&gt;</c>) — a local mirror of the engine recogniser, so this guard's S0 honesty
    /// check doesn't depend on the engine internal it is validating.</summary>
    static bool IsGenderedType(Type t)
    {
        static bool IsGen(Type x) => x.IsGenericType
            && (x.GetGenericTypeDefinition().Name.StartsWith("GenderedItem", StringComparison.Ordinal)
                || x.GetGenericTypeDefinition().Name.StartsWith("IGenderedItem", StringComparison.Ordinal));
        return IsGen(t) || t.GetInterfaces().Any(IsGen);
    }

    /// <summary>Extract the arm ref T from a "GenderedItem&lt;T&gt;" corpus TypeRef (mirrors CorpusRulebook.GenderedArmRef);
    /// a nested-generic arm (FormLinkNullable&lt;…&gt;) returns whole and won't resolve as a corpus type.</summary>
    static string? InnerArm(string typeRef)
    {
        const string head = "GenderedItem<";
        if (!typeRef.StartsWith(head, StringComparison.Ordinal) || !typeRef.EndsWith(">", StringComparison.Ordinal)) return null;
        var inner = typeRef[head.Length..^1].Trim();
        return inner.Length == 0 ? null : inner;
    }

    /// <summary>Resolve the arm's runtime type T from a field's <c>IGenderedItem&lt;T&gt;</c> assembly-qualified name —
    /// the actual type <c>armProp.GetValue</c> returns (boxed iff it is a value type). Null if the AQ can't be loaded.</summary>
    static Type? ArmRuntimeType(string? genderedAq)
    {
        if (genderedAq is null) return null;
        var gt = Type.GetType(genderedAq);
        return gt is { IsGenericType: true } ? gt.GetGenericArguments()[0] : null;
    }

    static bool Throws(Action a) { try { a(); return false; } catch { return true; } }

    static string OutPath(string stem) =>
        Path.Combine(Path.GetTempPath(), stem + "-" + Guid.NewGuid().ToString("N"), stem + ".esp");

    /// <summary>Build a patch through the REAL <see cref="WriteEngine.WritePatch"/>, reopen it via a binary overlay,
    /// and run <paramref name="verify"/> against the reopened (getter) record — so the assertion is a genuine
    /// on-disk round-trip, not an in-memory read.</summary>
    static (bool ok, string? detail) TrySerialize(string stem, Action<SkyrimMod> build, Func<ISkyrimModGetter, (bool, string?)> verify)
    {
        var outPath = OutPath(stem);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            var mod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outPath), ModType.Plugin), SkyrimRelease.SkyrimSE);
            build(mod);
            WriteEngine.WritePatch(mod, new ISkyrimModGetter[] { mod }, outPath);
            if (!File.Exists(outPath)) { CleanOut(outPath); return (false, "no file written"); }
            var back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE);
            var (ok, detail) = verify(back);
            CleanOut(outPath);
            return (ok, detail);
        }
        catch (Exception ex) { CleanOut(outPath); return (false, $"{ex.GetType().Name}: {ex.Message}"); }
    }

    static void CleanOut(string outPath)
    {
        try { var dir = Path.GetDirectoryName(outPath); if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort */ }
    }
}

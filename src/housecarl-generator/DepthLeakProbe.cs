using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD (standing CI instrument) for HCBR-2026-06-08-01 — the depth-walker reflection leak.
///
/// Self-contained, in the pattern of <c>pkcu-regression</c>: synthesizes IN MEMORY a record carrying a CTDA
/// condition whose ConditionData arm (<c>GetActorValueConditionData</c>) exposes a <c>Parameter1Type</c>
/// typed <see cref="System.Type"/>, then drives the PRODUCT read path (<see cref="ReadEngine.ReadFields"/>)
/// at high depth and asserts the walker renders that type as a single opaque summary token and NEVER recurses
/// into its .NET reflection surface (Assembly.DefinedTypes — the whole ~17,900-type Mutagen assembly — the
/// BaseType→Enum→ValueType chain, StructLayoutAttribute, Module, the cyclic UnderlyingSystemType, …).
///
/// RED before the fix (the leak reproduces deterministically — every reflection sub-member is a `Parameter1Type.*`
/// field line), GREEN after. It ALSO asserts the useful condition content survives (ActorValue=Conjuration) and
/// the one-line type summary is still shown — so the fix can't "pass" by truncating real record data.
///
/// Run: <c>dotnet run --project src/housecarl-generator depth-leak-guard</c>
/// </summary>
public static class DepthLeakProbe
{
    // Field-path fragments that only appear when the walker descended INTO a .NET reflection object — i.e.
    // the leak signature. A clean read shows `…Data.Parameter1Type = [RuntimeType] Name=ActorValue` and stops;
    // any of these means it walked past that token into Type/Assembly/Module/attribute internals.
    static readonly string[] LeakMarkers =
    {
        "Parameter1Type.", "Parameter2Type.",      // descended PAST the opaque type token (the core bug)
        ".Assembly", "DefinedTypes", "StructLayoutAttribute", "MetadataToken",
        "TypeHandle", "UnderlyingSystemType", ".BaseType", ".Module",
    };

    [CiProbe("depth-leak-guard")]
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — depth-walker reflection leak (HCBR-2026-06-08-01)  ################");
        Console.WriteLine();

        // Synthesize a MagicEffect with one CTDA condition; its Data arm carries a System.Type Parameter1Type
        // (typeof(ActorValue)) — the exact shape that exploded on the real Apostasy perk read at depth 8.
        var mod = new SkyrimMod(new ModKey("hc_depthguard", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var mgef = mod.MagicEffects.AddNew();
        mgef.Conditions.Add(new ConditionFloat
        {
            CompareOperator = CompareOperator.EqualTo,
            ComparisonValue = 1f,
            Data = new GetActorValueConditionData { ActorValue = ActorValue.Conjuration },
        });

        const int depth = 8;
        var rf = ReadEngine.ReadFields(mgef, new[] { "Conditions" }, depth);

        var leak = rf.Fields.Where(fld => LeakMarkers.Any(m => fld.Path.Contains(m, StringComparison.Ordinal))).ToList();
        var summary = rf.Fields.FirstOrDefault(fld => fld.Path.EndsWith(".Parameter1Type", StringComparison.Ordinal));
        var useful = rf.Fields.FirstOrDefault(fld => fld.Path.EndsWith(".Data.ActorValue", StringComparison.Ordinal));

        Console.WriteLine($"-- synthesized MagicEffect; read Conditions at depth={depth} --");
        Console.WriteLine($"   field lines emitted           : {rf.Fields.Count}");
        Console.WriteLine($"   leak lines (reflection paths) : {leak.Count}");
        Console.WriteLine($"   Parameter1Type summary        : {Show(summary)}");
        Console.WriteLine($"   useful value (Data.ActorValue): {Show(useful)}");
        Console.WriteLine();

        if (leak.Count > 0)
        {
            Console.WriteLine($"   first {Math.Min(8, leak.Count)} of {leak.Count} leak line(s):");
            foreach (var fld in leak.Take(8)) Console.WriteLine($"      {fld.Path} = {(fld.HasValue ? fld.Token : fld.Note)}");
            Console.WriteLine();
        }

        bool noLeak = leak.Count == 0;
        bool summaryOk = summary is not null && (summary.Token ?? summary.Note ?? "").Contains("RuntimeType", StringComparison.Ordinal);
        bool usefulOk = useful is { HasValue: true, Token: "Conjuration" };
        bool pass = noLeak && summaryOk && usefulOk;

        Console.WriteLine($"   no reflection leak             : {(noLeak ? "PASS" : "FAIL")}");
        Console.WriteLine($"   type kept as one summary token : {(summaryOk ? "PASS" : "FAIL")}");
        Console.WriteLine($"   useful condition value kept    : {(usefulOk ? "PASS" : "FAIL")}");
        Console.WriteLine();
        Console.WriteLine($"=== depth-leak-guard: {(pass ? "PASS" : "FAIL")} ===");
        return pass ? 0 : 1;
    }

    static string Show(FieldValue? f) =>
        f is null ? "(missing)" : $"{f.Path} = {(f.HasValue ? f.Token : f.Note)}";
}

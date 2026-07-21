using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for the depth-floor expansion of a polymorphic <c>Conditions[].Data</c> ARM
/// (#258) — the sibling of <c>vmad-property-read-guard</c> on the SAME floor exception (<see cref="ReadEngine"/>'s
/// Expand). A <c>ConditionData</c> arm reached via LIST expansion used to stop at its bare <c>[Type]</c> summary at
/// the depth floor — its parameter fields surfaced ONLY when <c>Data</c> was addressed directly — so a
/// <c>Conditions</c> dump at the natural depth=3 showed <c>[GetActorValueConditionData]</c> but never the params
/// (you needed depth=4 or a per-row path). The floor now opens a condition arm's parameter fields ONE bounded level,
/// exactly as it already does for a VMAD script property.
///
/// Arms, all driving the PRODUCT read path (<see cref="ReadEngine.ReadFields"/>):
///   * PARAMS-SURFACE (depth=3)       — a MagicEffect Conditions dump surfaces
///     <c>Conditions[0].Data.ActorValue = Conjuration</c>. RED before (arm stopped at the floor), GREEN after.
///   * SUMMARY-KEPT                    — the <c>[GetActorValueConditionData]</c> arm summary line is still present
///     (params are additive, not a replacement).
///   * BOUNDED                        — the arm opens EXACTLY one level: no field path is deeper than
///     <c>Conditions[0].Data.&lt;param&gt;</c>.
///   * DEPTH-NOT-LOWERED (depth=2)     — the SAME read at depth=2 does NOT surface the arm params (the arm opens only
///     once it REACHES the floor at depth&gt;=3; the fix didn't shift the whole depth contract down a level).
///   * FLOOR-UNCHANGED-OFF-ARM (depth=2) — an NPC Perks read (PerkPlacement, a NON-arm substruct) still stops at the
///     floor: no <c>Perks[0].Perk</c> field line — the one-extra-level is condition-arms + script-properties ONLY.
///
/// Run: dotnet run --project src/housecarl-generator -- condition-arm-expand-guard
/// </summary>
public static class ConditionArmExpandProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — Conditions[].Data arm expansion at the depth floor (#258)  ################");
        Console.WriteLine();

        var mod = new SkyrimMod(new ModKey("hc_condarm", ModType.Plugin), SkyrimRelease.SkyrimSE);

        // A MagicEffect with one condition whose polymorphic Data arm carries a checkable PARAM (ActorValue).
        var mgef = new MagicEffect(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE);
        mgef.Conditions.Add(new ConditionFloat
        {
            CompareOperator = CompareOperator.EqualTo,
            ComparisonValue = 1f,
            Data = new GetActorValueConditionData { ActorValue = ActorValue.Conjuration },
        });

        var deep = ReadEngine.ReadFields(mgef, new[] { "Conditions" }, 3);
        var param = Find(deep.Fields, "Conditions[0].Data.ActorValue");
        var summary = deep.Fields.FirstOrDefault(f => f.Path.EndsWith("Conditions[0].Data", StringComparison.Ordinal) && !f.HasValue);
        bool deeperThanParam = deep.Fields.Any(f => f.Path.Contains("Conditions[0].Data.ActorValue.", StringComparison.Ordinal));

        var shallow = ReadEngine.ReadFields(mgef, new[] { "Conditions" }, 2);
        bool depth2NoParam = !shallow.Fields.Any(f => f.Path.Contains("Conditions[0].Data.", StringComparison.Ordinal));

        // NON-arm control: an NPC's Perks (PerkPlacement) at depth=2 — a plain substruct still stops at the floor.
        var npc = mod.Npcs.AddNew();
        var pp = new PerkPlacement { Rank = 1 };
        pp.Perk.SetTo(FormKey.Factory("03AF81:Skyrim.esm"));
        npc.Perks = new() { pp };
        var perks = ReadEngine.ReadFields(npc, new[] { "Perks" }, 2);
        bool perkFloorHeld = !perks.Fields.Any(f => f.Path == "Perks[0].Perk");

        Console.WriteLine($"   depth=3 Conditions[0].Data.ActorValue : {Show(param)}");
        Console.WriteLine($"   depth=3 Conditions[0].Data (summary)  : {Show(summary)}");
        Console.WriteLine($"   depth=2 arm params suppressed         : {depth2NoParam}");
        Console.WriteLine($"   NPC Perks[0].Perk absent at depth=2   : {perkFloorHeld}");
        Console.WriteLine();

        bool paramsSurface = param is { HasValue: true } && param.Token == "Conjuration";
        bool summaryKept = summary is not null && (summary.Note ?? "").Contains("GetActorValueConditionData", StringComparison.Ordinal);
        bool bounded = !deeperThanParam;

        bool pass = paramsSurface && summaryKept && bounded && depth2NoParam && perkFloorHeld;

        Console.WriteLine($"   PARAMS-SURFACE  (depth=3 shows Data.ActorValue)       : {(paramsSurface ? "PASS" : "FAIL")}");
        Console.WriteLine($"   SUMMARY-KEPT    (arm [Type] summary still present)    : {(summaryKept ? "PASS" : "FAIL")}");
        Console.WriteLine($"   BOUNDED         (opens exactly one level)             : {(bounded ? "PASS" : "FAIL")}");
        Console.WriteLine($"   DEPTH-NOT-LOWERED (depth=2 still suppresses params)   : {(depth2NoParam ? "PASS" : "FAIL")}");
        Console.WriteLine($"   FLOOR-UNCHANGED-OFF-ARM (Perks substruct still stops) : {(perkFloorHeld ? "PASS" : "FAIL")}");
        Console.WriteLine();
        Console.WriteLine($"=== condition-arm-expand-guard: {(pass ? "PASS" : "FAIL")} ===");
        return pass ? 0 : 1;
    }

    static FieldValue? Find(IEnumerable<FieldValue> fields, string suffix) =>
        fields.FirstOrDefault(f => f.Path.EndsWith(suffix, StringComparison.Ordinal));

    static string Show(FieldValue? f) =>
        f is null ? "(missing)" : $"{f.Path} = {(f.HasValue ? f.Token : f.Note)}";
}

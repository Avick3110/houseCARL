using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD (standing CI instrument) for the winner-vs-source DISPLAY fix (session-review wishlist #8):
/// <c>cross_plugin_query</c>'s per-match render must show the body the scan FILTERED — under <c>plugins=[X]</c> the
/// scoped plugin's OWN override, under <c>type=</c> the load-order winner — and never silently the winner under a
/// <c>plugins=</c> scope (the pre-existing bug: filter on X's value, display the winner's, which can CONTRADICT the
/// filter you typed).
///
/// Self-contained, in the pattern of <c>pkcu-regression</c> / <c>value-predicate-guard</c> / <c>depth-leak-guard</c>:
/// synthesizes a 3-plugin order ON DISK — a master plus two overrides of ONE weapon with DIFFERENT Damage, the
/// higher-priority override winning — then drives the product CORE primitives the render rests on:
/// <see cref="LoadOrderResolver.RecordsIn"/> (the scoped-plugin stream, which now carries the source plugin) and
/// <see cref="LoadOrderResolver.WinnerRecordsOfType"/> (the winner stream). It asserts every yielded record is
/// paired with the CORRECT source plugin name AND that plugin's OWN body value (A⇒50, B⇒99), that a single-plugin
/// scope yields only that plugin's body, that a multi-plugin scope yields each plugin's own body once, and that the
/// winner stream yields the WINNER's body (99). RED if <c>RecordsIn</c> ever yields a body whose value doesn't match
/// its source plugin — the exact mispairing that would make the render display a value contradicting the filter.
///
/// SCOPE NOTE: the mcp render wiring itself (<c>CrossQueryOutcome.Sources</c> → <c>ResolveRead(fk, source)</c> in
/// housecarl-mcp) is NOT reachable from this harness — the generator references housecarl-core only (the same
/// project boundary that makes the bsa/compile probes skip external tools). That last hop is proven end-to-end by
/// the re-runnable LIVE stdio round-trip on the ARR order (recorded in the plan + handoff). This guard locks the
/// by-construction CORE contract that wiring merely threads: source plugin ↔ its own body, never the winner's.
///
/// Run: <c>dotnet run --project src/housecarl-generator -- source-display-guard</c>
/// </summary>
public static class SourceDisplayProbe
{
    static int _pass, _fail;

    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — winner-vs-source display (cross_plugin_query plugins= scope)  ################");
        Console.WriteLine();

        var dir = Path.Combine(Path.GetTempPath(), "hc_source_display_guard");
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        Directory.CreateDirectory(dir);

        const string masterName = "hcSrcMaster.esp", aName = "hcSrcA.esp", bName = "hcSrcB.esp";
        const ushort dmgMaster = 10, dmgA = 50, dmgB = 99;       // distinct per plugin so a mispaired body is provable
        var masterPath = Path.Combine(dir, masterName);
        var aPath = Path.Combine(dir, aName);
        var bPath = Path.Combine(dir, bName);

        try
        {
            // ---- 1. MASTER: one weapon, Damage=10. Masterless (CI has no game files — it references nothing). ----
            var master = new SkyrimMod(ModKey.FromNameAndExtension(masterName), SkyrimRelease.SkyrimSE);
            var mw = master.Weapons.AddNew();
            mw.EditorID = "hcSrcSword";
            mw.BasicStats = new WeaponBasicStats { Damage = dmgMaster };
            var fk = mw.FormKey;                                  // <id>:hcSrcMaster.esp — the one FormKey all three touch
            master.BeginWrite.ToPath(masterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            // ---- 2. OVERRIDE A: the SAME weapon, Damage=50. Masters [master]. (raw BeginWrite writes only the
            //         actually-referenced master — no Skyrim/Update baseline injection, so the order stays self-contained). ----
            var aMod = new SkyrimMod(ModKey.FromNameAndExtension(aName), SkyrimRelease.SkyrimSE);
            ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(aMod, mw)).BasicStats = new WeaponBasicStats { Damage = dmgA };
            aMod.BeginWrite.ToPath(aPath).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

            // ---- 3. OVERRIDE B: the SAME weapon, Damage=99 — highest priority, so it WINS. Masters [master]. ----
            var bMod = new SkyrimMod(ModKey.FromNameAndExtension(bName), SkyrimRelease.SkyrimSE);
            ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(bMod, mw)).BasicStats = new WeaponBasicStats { Damage = dmgB };
            bMod.BeginWrite.ToPath(bPath).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

            Console.WriteLine($"-- synthesized {masterName} (dmg {dmgMaster}) < {aName} (dmg {dmgA}) < {bName} (dmg {dmgB}); {bName} WINS --");
            Console.WriteLine();

            // ---- 4. BUILD the order (master < A < B) and drive the product primitives. ----
            using var resolver = LoadOrderResolver.Build(new[] { masterPath, aPath, bPath });
            var weap = new[] { typeof(IWeaponGetter) };

            var w = resolver.ResolveWinner(fk);
            Check($"winner is {bName} (highest override wins)",
                  w is not null && string.Equals(w.Value.WinnerPlugin, bName, StringComparison.OrdinalIgnoreCase));

            // (a) plugins=[A] → the weapon ONCE, source=A, A's OWN body (dmg 50) — NOT the winner's 99.
            CheckScope("plugins=[A]", resolver.RecordsIn(new[] { aName }, weap).ToList(), fk, aName, dmgA);

            // (b) plugins=[B] → source=B, dmg 99 (here B's own == the winner, but it's reached as the SOURCE, not the winner fetch).
            CheckScope("plugins=[B]", resolver.RecordsIn(new[] { bName }, weap).ToList(), fk, bName, dmgB);

            // (c) plugins=[A,B] → the weapon yielded ONCE PER scoped plugin, each paired with its OWN body (A/50, B/99).
            var ab = resolver.RecordsIn(new[] { aName, bName }, weap).Where(x => x.fk == fk).ToList();
            Check("plugins=[A,B]: weapon yielded once per scoped plugin (2)", ab.Count == 2);
            Check($"plugins=[A,B]: A's yield → source={aName} & dmg={dmgA}", ab.Any(x => Eq(x.source, aName) && DmgOf(x.body) == dmgA));
            Check($"plugins=[A,B]: B's yield → source={bName} & dmg={dmgB}", ab.Any(x => Eq(x.source, bName) && DmgOf(x.body) == dmgB));
            Check("plugins=[A,B]: EVERY yield's body matches its source plugin (no mispairing — the heart of the fix)",
                  ab.All(x => (Eq(x.source, aName) && DmgOf(x.body) == dmgA) || (Eq(x.source, bName) && DmgOf(x.body) == dmgB)));

            // (d) type= (winner scope) → the weapon ONCE, the WINNER's body (dmg 99). type= display stays the winner, UNCHANGED.
            var winners = resolver.WinnerRecordsOfType(weap).Where(x => x.fk == fk).ToList();
            Check("type=: weapon yielded once (the winner only)", winners.Count == 1);
            Check($"type=: winner body is B's (dmg {dmgB}) — type= scope displays the winner, unchanged",
                  winners.Count == 1 && DmgOf(winners[0].body) == dmgB);

            Console.WriteLine();
            Console.WriteLine($"=== source-display-guard: {_pass} passed, {_fail} failed -> {(_fail == 0 ? "PASS" : "FAIL")} ===");
            return _fail == 0 ? 0 : 1;
        }
        catch (Exception ex) { Console.WriteLine($"   FAIL (unexpected): {ex.GetType().Name}: {ex.Message}"); return 1; }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    static ushort? DmgOf(IMajorRecordGetter body) => (body as IWeaponGetter)?.BasicStats?.Damage;
    static bool Eq(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>A single-plugin scope must yield the FormKey exactly once, with that plugin as the source and that
    /// plugin's OWN body value — not the winner's.</summary>
    static void CheckScope(string label, List<(FormKey fk, int depth, IMajorRecordGetter body, string source)> yields,
                           FormKey fk, string expectSource, ushort expectDmg)
    {
        var hit = yields.Where(x => x.fk == fk).ToList();
        Check($"{label}: weapon yielded once", hit.Count == 1);
        if (hit.Count != 1) return;
        Check($"{label}: source = {expectSource}", Eq(hit[0].source, expectSource));
        Check($"{label}: body is {expectSource}'s OWN (dmg {expectDmg}, not the winner)", DmgOf(hit[0].body) == expectDmg);
    }

    static void Check(string label, bool ok)
    {
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        if (ok) _pass++; else _fail++;
    }
}

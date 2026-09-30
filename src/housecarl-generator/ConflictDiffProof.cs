using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>Manual real-data proof for the conflict-tree content diff (HCBR-2026-06-09-01). The CI tests for the
/// diff are ConflictTreeContentDiffTests in src/housecarl-mcp-tests.</summary>
public static class ConflictDiffProof
{
    // The tree the render layer consumes, off a fresh capture of the resolver.
    static ConflictTreeView? TreeOf(LoadOrderService svc, LoadOrderResolver resolver, FormKey fk) =>
        svc.ReadArea.ResolveTreePinned(new LoadOrderService.ViewPin(resolver, resolver.Capture()), fk, null);

    /// <summary>REAL-DATA proof (manual; needs an MO2 instance with the Ashe plugins): the report's exact repro —
    /// the whole-record conflict diff of <c>E495A3:Ashe - Fire and Blood.esp</c> (MM_RelentlessFury, SPEL), whose
    /// origin and winning patch both carry exactly 1 effect but with DIFFERENT BaseEffect. The old diff said
    /// "(identical to winner)"; the content diff must report the Effects delta. Also prints the PlayerFaction
    /// (000DB1:Skyrim.esm) tree diff — the masked-regression record — for eyes-on confirmation.
    /// Run: <c>dotnet run --project src/housecarl-generator conflict-diff-proof -- --mo2 &lt;instanceDir&gt;</c></summary>
    public static int RunProof(string[] args)
    {
        var f = WriteEngine.ParseFlags(args);
        var instanceDir = f.GetValueOrDefault("mo2");
        if (instanceDir is null || !Directory.Exists(instanceDir)) { Console.WriteLine("SKIP: needs --mo2 <instanceDir>"); return 0; }

        Console.WriteLine($"################  REAL-DATA PROOF — conflict-tree content diff on {Path.GetFileName(instanceDir)}  ################");
        Console.WriteLine();
        var p = Mo2Instance.Resolve(instanceDir);
        var order = Mo2LoadOrder.Build(p.ProfileDir, p.ModsDir, p.DataDir, p.OverwriteDir);
        using var resolver = LoadOrderResolver.Build(order.OrderedPaths.ToList());
        Console.WriteLine($"   resolver: {resolver.PluginCount} plugins, {resolver.RecordCount:N0} records");
        var svc = LoadOrderService.ForGuard(resolver, new UserConfigStore(Path.Combine(Path.GetTempPath(), "hc-conflictdiff-proof", "houseCARL.user.json")));

        bool pass = true;
        foreach (var subject in new[] { "E495A3:Ashe - Fire and Blood.esp", "000DB1:Skyrim.esm" })
        {
            Console.WriteLine();
            Console.WriteLine($"-- {subject} --");
            FormKey fk;
            try { fk = FormKey.Factory(subject); } catch { Console.WriteLine("   (bad FormKey)"); continue; }
            var tree = TreeOf(svc, resolver, fk);
            if (tree is null || tree.Nodes.Count < 2) { Console.WriteLine("   (not in this order, or only one plugin touches it — skipped)"); continue; }
            var winner = tree.Winner;
            Console.WriteLine($"   {tree.Nodes.Count} plugins touch it; winner = {winner.Plugin}");
            for (int n = 0; n < tree.Nodes.Count - 1; n++)
            {
                var diff = FieldsDiff.Compare(tree.Nodes[n].Record, winner.Record);
                var line = diff.Deltas.Count > 0 ? string.Join("; ", diff.Deltas)
                         : diff.Complete ? "(identical to winner — full modeled content compared)"
                                         : "(comparison truncated — not a verified ITM)";
                Console.WriteLine($"   {tree.Nodes[n].Plugin}: {line}");
            }
            if (subject.StartsWith("E495A3", StringComparison.Ordinal))
            {
                var fnb = tree.Nodes.Take(tree.Nodes.Count - 1).FirstOrDefault(x => x.Plugin.StartsWith("Ashe - Fire and Blood", StringComparison.OrdinalIgnoreCase));
                bool reported = fnb is not null
                    && FieldsDiff.Compare(fnb.Record, winner.Record).Deltas.Any(d => d.StartsWith("Effects:", StringComparison.Ordinal) && d.Contains("0E40BF"));
                Console.WriteLine($"   ASSERT Effects content delta reported for Ashe - Fire and Blood.esp: {(reported ? "PASS" : "FAIL")}");
                pass &= reported;
            }
        }
        Console.WriteLine();
        Console.WriteLine($"=== conflict-diff-proof: {(pass ? "PASS" : "FAIL")} ===");
        return pass ? 0 : 1;
    }
}

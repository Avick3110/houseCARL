using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

// The SKSE config audit run whole against a live MO2 instance; the xUnit SkseConfig* tests pin the extractor, verdicts and render.
public static class SkseConfigAuditReal
{
    /// <summary>MANUAL real-data harness (the tier-B LIVE GATE): run the WHOLE audit against a live MO2 instance and print
    /// exactly what housecarl_skse findings='config' would return, plus a timing line — the empirical re-check Aaron drives (the
    /// xUnit tests pin the extractor + verdict logic; this proves the full scan over real configs). NOT in ci-all (needs a
    /// real instance + game install). Read-only; touches nothing but a temp user.json.
    /// Usage: dotnet run --project src/housecarl-generator -- skse-config-audit-real --mo2 "&lt;MO2 instance&gt;" [--filter &lt;substr&gt;]</summary>
    public static int RunReal(string[] args)
    {
        string? mo2 = ArgVal(args, "--mo2");
        string? filter = ArgVal(args, "--filter");
        int max = int.TryParse(ArgVal(args, "--max"), out var m) ? m : 80_000;
        if (mo2 is null) { Console.WriteLine("skse-config-audit-real needs --mo2 <MO2 instance folder>"); return 2; }

        var store = new UserConfigStore(Path.Combine(Path.GetTempPath(), "hc-skse-cfgaudit-" + Guid.NewGuid().ToString("N"), "user.json"));
        using var svc = LoadOrderService.WithInstance(mo2, 0, store);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var data = svc.SkseConfigAudit();
        sw.Stop();

        Console.WriteLine(SkseConfigAuditWire.Render(data, filter, max));
        int refs = data.Files.Sum(f => f.Refs.Count);
        int dead = data.Files.Sum(f => f.Refs.Count(r => r.Verdict != SkseRefVerdict.Ok));
        Console.WriteLine($"\n[timing] SkseConfigAudit over {data.ConfigCount} configs ({refs} references, {dead} dead) in {sw.ElapsedMilliseconds} ms");
        return 0;
    }

    static string? ArgVal(string[] a, string key)
    {
        int i = Array.IndexOf(a, key);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }
}

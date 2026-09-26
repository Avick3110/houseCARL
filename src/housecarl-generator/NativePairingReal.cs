using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>The MANUAL real-data harness for the native-function pairing audit (the live gate): runs the whole audit
/// against a live MO2 instance and prints the render + factual timing. NOT part of ci-all; the renderer's arms are
/// pinned by the NativePairing*Tests in housecarl-mcp-tests.</summary>
public static class NativePairingReal
{
    public static int RunReal(string[] args)
    {
        string? mo2 = ArgVal(args, "--mo2");
        string? filter = ArgVal(args, "--filter");
        int max = int.TryParse(ArgVal(args, "--max"), out var m) ? m : 80_000;
        if (mo2 is null) { Console.WriteLine("native-pairing-real needs --mo2 <MO2 instance folder>"); return 2; }

        var store = new UserConfigStore(Path.Combine(Path.GetTempPath(), "hc-native-pairing-" + Guid.NewGuid().ToString("N"), "user.json"));
        using var svc = LoadOrderService.WithInstance(mo2, 0, store);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var data = svc.NativePairingAudit();
        sw.Stop();

        Console.WriteLine(NativePairingWire.Render(data, filter, max));
        Console.WriteLine($"\n[timing] NativePairingAudit over {data.PexScanned} compiled scripts " +
            $"({data.Classes.Count} native classes, {data.Unreadable.Count} unreadable, runtime {data.InstalledRuntime ?? "(unresolved)"}) " +
            $"in {sw.ElapsedMilliseconds} ms");
        return 0;
    }

    static string? ArgVal(string[] a, string key)
    {
        int i = Array.IndexOf(a, key);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }
}

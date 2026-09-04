using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;

namespace HousecarlGenerator;

/// <summary>
/// Stand the REAL service path (<see cref="LoadOrderService.SkyPatcherLayer"/>) up against a live MO2
/// instance and print the whole INI layer — the artifact to check against xEdit and in-game. Needs a
/// live instance, so it is run by hand, not from CI.
///
/// Run: dotnet run --project src/housecarl-generator skypatcher-layer --instance &lt;MO2 instance dir&gt;
/// </summary>
public static class SkyPatcherHarness
{
    /// <summary>corpus.json is GENERATED, not tracked, so when this runs from outside the repo root the
    /// default relative CorpusPath resolves to nothing and the service's rulebook and type-catalog loads
    /// crash with no useful message. Generate one into a unique temp dir, cleaned on exit, around
    /// <paramref name="body"/>.</summary>
    static int WithCorpus(Func<int> body)
    {
        string? tmp = null;
        if (!File.Exists(CorpusRulebook.CorpusPath))
        {
            tmp = Path.Combine(Path.GetTempPath(), "hc-sp-harness-corpus-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            Console.WriteLine($"corpus.json absent — generating into {tmp} (running outside the repo root)…");
            var gen = CorpusGenerator.GenerateAll(Path.Combine(tmp, "generated"), Path.Combine(tmp, "refs"));
            if (gen != 0) { Console.Error.WriteLine("error: corpus generation failed"); return gen; }
            CorpusRulebook.CorpusPath = Path.Combine(tmp, "generated", "corpus.json");
        }
        try { return body(); }
        finally { if (tmp is not null) { try { Directory.Delete(tmp, recursive: true); } catch { /* best-effort */ } } }
    }

    /// <summary>
    /// The whole SkyPatcher layer + conflict report off a live MO2 instance, rendered by the SAME Wire the
    /// housecarl_skypatcher_layer tool uses (internals-visible), so what prints is what the tool returns.
    /// Run: dotnet run --project src/housecarl-generator skypatcher-layer --instance &lt;MO2 instance dir&gt; [--filter x]
    /// </summary>
    public static int RunLayer(string[] args)
    {
        string? instance = null, filter = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--instance" && i + 1 < args.Length) instance = args[++i];
            else if (args[i] == "--filter" && i + 1 < args.Length) filter = args[++i];
        }
        if (instance is null)
        {
            Console.Error.WriteLine("usage: skypatcher-layer --instance <MO2 instance dir> [--filter <folder/mod/file>]");
            return 1;
        }

        return WithCorpus(() =>
        {
            var store = new UserConfigStore(Path.Combine(Path.GetTempPath(), $"hc-sp-harness-{Guid.NewGuid():N}.json"));
            var svc = LoadOrderService.WithInstance(instance, maxPlugins: 0, store);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var data = svc.SkyPatcherLayer();
            sw.Stop();
            Console.WriteLine(SkyPatcherWire.RenderLayer(data, filter, 200_000));
            Console.WriteLine($"\n[{sw.Elapsed.TotalSeconds:N1}s]");
            return 0;
        });
    }

}

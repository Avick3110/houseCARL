using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;

namespace HousecarlGenerator;

/// <summary>
/// SkyPatcher Wave-1 CRUX harness (plan dev/plans/SKYPATCHER_DISTRIBUTOR_TOOL_PLAN_2026-07-08.md §7
/// Wave 1): stand the REAL service path (<see cref="LoadOrderService.SkyPatcherPostState"/>) up against
/// a live MO2 instance and print one record's computed post-SkyPatcher state — the artifact Aaron
/// verifies against xEdit + in-game (the empirical gate; the promise is proven, not reviewed).
///
/// Run: dotnet run --project src/housecarl-generator skypatcher-post-state &lt;FormID:Plugin.esp&gt; --instance &lt;MO2 instance dir&gt;
/// </summary>
public static class SkyPatcherHarness
{
    public static int Run(string[] args)
    {
        string? formid = null, instance = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--instance" && i + 1 < args.Length) instance = args[++i];
            else formid ??= args[i];
        }
        if (formid is null || instance is null)
        {
            Console.Error.WriteLine("usage: skypatcher-post-state <FormID:Plugin.esp> --instance <MO2 instance dir>");
            return 1;
        }

        FormKey fk;
        try { fk = FormKey.Factory(formid.Trim()); }
        catch (Exception ex) { Console.Error.WriteLine($"error: bad FormID '{formid}': {ex.Message}"); return 1; }

        return WithCorpus(() => Run(fk, instance));
    }

    /// <summary>corpus.json is GENERATED, not tracked — run from outside the repo root the default
    /// relative CorpusPath resolves to nothing and the service's rulebook/type-catalog loads crash
    /// unnamed. Bootstrap the FloiFieldsProbe way (generate into a unique temp dir, cleaned on exit)
    /// around <paramref name="body"/> — the ONE bootstrap both harness modes ride (review fold).</summary>
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
    /// The Wave-2 layer harness: the whole SkyPatcher layer + conflict report off a live MO2 instance,
    /// rendered by the SAME Wire the housecarl_skypatcher_layer tool uses (internals-visible) — what the
    /// tool will return, verifiable before the plugin repackages.
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

    static int Run(FormKey fk, string instance)
    {
        // A throwaway user-config store (the harness never writes tool paths); the service reads the
        // instance exactly as the product does.
        var store = new UserConfigStore(Path.Combine(Path.GetTempPath(), $"hc-sp-harness-{Guid.NewGuid():N}.json"));
        var svc = LoadOrderService.WithInstance(instance, maxPlugins: 0, store);

        Console.WriteLine("================================================================");
        Console.WriteLine(" SkyPatcher post-state harness (Wave 1 crux)");
        Console.WriteLine("================================================================");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var d = svc.SkyPatcherPostState(fk);
        sw.Stop();

        if (d.Error is not null) { Console.Error.WriteLine($"error: {d.Error}"); return 1; }

        Console.WriteLine($"record  : {d.RecordTypeName} {d.FormKey} ({d.EditorId ?? "<no editorid>"})");
        Console.WriteLine($"winner  : {d.WinnerPlugin}   [profile '{d.ProfileName}', {sw.Elapsed.TotalSeconds:N1}s]");
        foreach (var f in d.Folders)
        {
            Console.WriteLine();
            Console.WriteLine($"-- folder '{f.Subfolder}': {f.IniCount} applied INI file(s), {f.LineCount} line(s) replayed --");
            if (!f.Enabled) { Console.WriteLine("   [!] SkyPatcher.ini toggles this folder OFF — its INIs exist but the DLL skips the whole subfolder."); continue; }
            if (f.Result is null) { Console.WriteLine("   (no INIs for this folder in the active order)"); continue; }
            var r = f.Result;
            Console.WriteLine($"   lines matching this record: {r.LinesMatched}   filter-unresolved skips: {r.LinesSkippedUnresolvedFilter}");
            if (r.Applied.Count > 0)
            {
                Console.WriteLine($"   APPLIED ({r.Applied.Count}) — file:line  op=raw  →  field: before → after");
                foreach (var a in r.Applied)
                    Console.WriteLine($"     {a.File}:{a.LineNumber}  {a.Op}={a.RawValue}  →  {a.FieldPath}: {a.Before ?? "-"} → {a.After ?? "-"}{(a.Note is null ? "" : $"   [{a.Note}]")}");
            }
            if (r.Directives.Count > 0)
            {
                Console.WriteLine($"   NOT RESOLVED — directives ({r.Directives.Count}) (runtime/non-deterministic/copy-from-form):");
                foreach (var dr in r.Directives)
                    Console.WriteLine($"     {dr.File}:{dr.LineNumber}  {dr.Op}={dr.RawValue}   [{dr.Reason}]");
            }
            foreach (var w in r.Warnings) Console.WriteLine($"   [!] {w}");
        }
        if (d.LayerNotes.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("layer notes:");
            foreach (var n in d.LayerNotes) Console.WriteLine($"  [!] {n}");
        }
        if (d.ReadIncomplete) Console.WriteLine("[!] a BSA failed to read this build — the INI union may be incomplete (Q3).");
        foreach (var w in d.AssetWarnings) Console.WriteLine($"[!] {w}");
        return 0;
    }
}

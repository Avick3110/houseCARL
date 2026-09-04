using System.Diagnostics;
using System.Reflection;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// The CI guard runner. Every guard runs in ONE process, so the Mutagen assembly loads and JITs once and the
/// schema corpus is reflected once (CorpusGenerator memoizes) instead of once per probe.
///
/// <para>The roster is DERIVED: every method carrying <see cref="CiProbeAttribute"/>, sorted by name. A guard
/// enrols itself and deleting its file deletes its row — there is no table to keep. Standalone probes
/// (<c>Standalone = true</c>) are dispatchable and counted but do not run here; each needs its own step in
/// <c>ci.yml</c>, hand-maintained. Nothing enforces that, so flagging a probe standalone also owes that file
/// an edit — otherwise the probe runs in neither harness.</para>
///
/// <para>Failure model: every probe runs even if an earlier one fails, so one run surfaces EVERY failure, each
/// as a GitHub <c>::error::</c> annotation naming the probe. The job still goes red if any probe fails.</para>
///
/// <para>Co-hosting contract — the shared state <see cref="RunAll"/> resets before each probe, and the only
/// cross-probe state in the suite: <c>CorpusRulebook.CorpusPath</c> (the one mutable static) is reset to the
/// runner's canonical corpus, so the check-first probes never validate against a prior probe's deleted temp
/// corpus; the <c>CODEX_HOME</c> env var is restored, because setup-update-lock-guard nulls it and does not;
/// and each probe runs in its own try/catch, so a probe that throws fails only itself. Everything else is
/// per-probe already — Guid-unique temp dirs and explicit-path UserConfigStores — and the class-parents and
/// decompile caches are per-LoadOrderService-instance, not process statics.</para>
/// </summary>
public static class CiAll
{
    /// <summary>One discovered guard: its verb, a direct delegate to its entry point, and its host type.</summary>
    readonly record struct Entry(string Name, Func<string[], int> Run, Type Host, bool Standalone);

    const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic
                               | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    /// <summary>
    /// The assemblies a guard can live in: this one plus every houseCARL assembly it references, transitively.
    /// The prefix is read off the attribute's own assembly name rather than spelled here.
    /// </summary>
    /// <remarks>
    /// The bound, stated plainly: probes are discovered in the generator, housecarl-core, housecarl-mcp and
    /// housecarl-setup assemblies. A [CiProbe] anywhere else — housecarl-mcp-tests, say, which this project
    /// does not reference — is not in the roster and nothing runs it.
    /// </remarks>
    public static IReadOnlyList<Assembly> GuardAssemblies => _guardAssemblies ??= DiscoverAssemblies();

    static Assembly[]? _guardAssemblies;

    static Assembly[] DiscoverAssemblies()
    {
        var prefix = typeof(CiProbeAttribute).Assembly.GetName().Name!.Split('-')[0] + "-";
        var seen = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<Assembly>();
        queue.Enqueue(typeof(CiAll).Assembly);

        while (queue.Count > 0)
        {
            var asm = queue.Dequeue();
            var name = asm.GetName().Name!;
            if (!seen.TryAdd(name, asm)) continue;
            foreach (var reference in asm.GetReferencedAssemblies())
                if (reference.Name is { } n
                    && n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && !seen.ContainsKey(n))
                    queue.Enqueue(Assembly.Load(reference));
        }

        return seen.Values.OrderBy(a => a.GetName().Name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Every attributed guard, roster and standalone alike, ordered by name.
    ///
    /// <para>A cached method rather than a static field on purpose. <see cref="Discover"/> has two written
    /// refusals — a wrong entry-point signature, and two guards claiming one verb — and a refusal thrown out
    /// of a static field initializer arrives as a TypeInitializationException that the CLR then caches against
    /// the type for the life of the process. In the xUnit host that turned one sentence naming both clashing
    /// hosts into eight failures reading "The type initializer for 'HousecarlGenerator.CiAll' threw an
    /// exception", with the sentence buried in an inner exception. From here the refusal arrives as itself, in
    /// the caller that asked. Every member below is cached the same way for the same reason.</para>
    /// </summary>
    static Entry[] All() => _all ??= Discover();

    static Entry[]? _all;

    static Entry[] Discover() => Discover(GuardAssemblies);

    /// <summary>
    /// The roster <paramref name="assemblies"/> would produce, refusals and all. The real derivation is
    /// <see cref="Discover()"/> over <see cref="GuardAssemblies"/> — this overload only lets an arm hand the
    /// same scan a fixture population, which is the only way to reach a refusal that the repo, by being green,
    /// never triggers.
    /// </summary>
    static Entry[] Discover(IReadOnlyList<Assembly> assemblies)
    {
        var found = new List<Entry>();

        foreach (var asm in assemblies)
            foreach (var type in asm.GetTypes())
                foreach (var method in type.GetMethods(Members))
                {
                    var attr = method.GetCustomAttribute<CiProbeAttribute>(inherit: false);
                    if (attr is null) continue;

                    // A guard the runner cannot call is a guard CI does not run, so a wrong signature is loud
                    // rather than skipped.
                    if (!method.IsStatic
                        || method.ReturnType != typeof(int)
                        || method.GetParameters() is not [{ ParameterType: var only }]
                        || only != typeof(string[]))
                        throw new InvalidOperationException(
                            $"[CiProbe(\"{attr.Name}\")] sits on {type.FullName}.{method.Name}, which the runner " +
                            "cannot dispatch: a guard entry point is `public static int <Name>(string[] args)`. " +
                            "Fix the signature or remove the attribute.");

                    found.Add(new Entry(attr.Name, method.CreateDelegate<Func<string[], int>>(),
                                        type, attr.Standalone));
                }

        var clashes = found.GroupBy(e => e.Name, StringComparer.Ordinal)
                           .Where(g => g.Count() > 1)
                           .Select(g => $"{g.Key} ({string.Join(", ", g.Select(e => e.Host.FullName))})")
                           .OrderBy(s => s, StringComparer.Ordinal)
                           .ToArray();
        if (clashes.Length > 0)
            throw new InvalidOperationException(
                "Two guards claim the same CI verb, so one of them would be unreachable by name: " +
                string.Join("; ", clashes) + ". Verb names are the roster's identity and must be unique.");

        return found.OrderBy(e => e.Name, StringComparer.Ordinal).ToArray();
    }

    static Entry[] Roster => All().Where(e => !e.Standalone).ToArray();

    /// <summary>
    /// The roster <paramref name="assemblies"/> would enrol: each verb, its host type, and whether CI runs it
    /// as its own step. The fixture seam for the two refusals in <see cref="Discover(IReadOnlyList{Assembly})"/>,
    /// which a green repo never triggers by itself. The shipped population is never this — it is
    /// <see cref="GuardAssemblies"/>, and nothing here changes how that is derived.
    /// </summary>
    public static IReadOnlyList<(string Name, Type Host, bool Standalone)> RosterIn(IReadOnlyList<Assembly> assemblies) =>
        Discover(assemblies).Select(e => (e.Name, e.Host, e.Standalone)).ToArray();

    /// <summary>Each roster verb with the type hosting its entry point.</summary>
    public static IReadOnlyList<(string Name, Type Host)> ProbeHosts =>
        _probeHosts ??= All().Where(e => !e.Standalone).Select(e => (e.Name, e.Host)).ToArray();

    static (string Name, Type Host)[]? _probeHosts;

    /// <summary>The same for the guards CI runs as their own workflow step instead of inside <c>ci-all</c>.</summary>
    public static IReadOnlyList<(string Name, Type Host)> StandaloneProbeHosts =>
        _standaloneProbeHosts ??= All().Where(e => e.Standalone).Select(e => (e.Name, e.Host)).ToArray();

    static (string Name, Type Host)[]? _standaloneProbeHosts;

    /// <summary>Every CI probe's name, for the unknown-mode refusal's list and did-you-mean (Program.cs).
    /// Sorted by name — the order <c>ci-all</c> runs them in.</summary>
    public static IReadOnlyList<string> ProbeNames => _probeNames ??= ProbeHosts.Select(p => p.Name).ToArray();

    static string[]? _probeNames;

    /// <summary>The guards CI runs as their own workflow step instead of inside <c>ci-all</c>.</summary>
    public static IReadOnlyList<string> StandaloneProbeNames =>
        _standaloneProbeNames ??= StandaloneProbeHosts.Select(p => p.Name).ToArray();

    static string[]? _standaloneProbeNames;

    /// <summary>
    /// Dispatch a single CI guard by name — roster or standalone. Program.cs routes local single-probe runs
    /// here rather than keeping a parallel if-chain that could drift out of sync with what CI runs. Returns
    /// false if the name is not a guard verb; the caller then tries its own manual/exploratory dispatches.
    /// </summary>
    public static bool TryDispatch(string name, string[] args, out int rc)
    {
        foreach (var e in All())
            if (e.Name == name) { rc = e.Run(args); return true; }
        rc = 0;
        return false;
    }

    public static int RunAll(string[] args)
    {
        var probes = Roster;

        // Vacuity floor: an empty roster would print a green 0/0. The attribute scan silently finding nothing
        // is exactly the failure this runner must not pass over.
        if (probes.Length == 0)
        {
            Console.Error.WriteLine(
                "::error::ci-all found NO [CiProbe] entry points. The roster is reflected off the attribute " +
                "across " + string.Join(", ", GuardAssemblies.Select(a => a.GetName().Name)) +
                "; an empty one means the scan is broken, not that there is nothing to run.");
            return 1;
        }

        var swAll = Stopwatch.StartNew();
        Console.WriteLine("================================================================");
        Console.WriteLine($" ci-all — running {probes.Length} CI probes in ONE process");
        Console.WriteLine("================================================================");

        // Pre-generate the schema corpus ONCE up front. This (a) warms CorpusGenerator's memoize cache so the
        // ~21 corpus probes reflect zero extra times, and (b) gives a canonical CorpusPath the check-first
        // probes reuse. Non-fatal if it fails — probes then self-generate (slower, still correct).
        string? canonicalCorpus = null;
        var corpusDir = Path.Combine(Path.GetTempPath(), "hc-ci-all-corpus-" + Guid.NewGuid().ToString("N"));
        try
        {
            var gen = Path.Combine(corpusDir, "generated");
            CorpusGenerator.GenerateAll(gen, Path.Combine(corpusDir, "refs"));
            var path = Path.Combine(gen, "corpus.json");
            if (File.Exists(path)) canonicalCorpus = path;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  (shared-corpus pre-gen failed: {ex.Message} — probes will self-generate)");
        }

        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");   // snapshot once (setup-update-lock nulls it)
        var results = new List<(string Name, bool Ok, string? Error, double Secs)>();

        foreach (var probe in probes)
        {
            // Reset the shared mutable state before each probe (the co-hosting contract above).
            if (canonicalCorpus != null) CorpusRulebook.CorpusPath = canonicalCorpus;
            Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);

            Console.WriteLine();
            Console.WriteLine($"──── [{results.Count + 1}/{probes.Length}] {probe.Name} ────");
            var sw = Stopwatch.StartNew();
            int code;
            string? error = null;
            try
            {
                code = probe.Run(Array.Empty<string>());
            }
            catch (Exception ex)
            {
                code = 1;
                error = $"{ex.GetType().Name}: {ex.Message}";
                Console.WriteLine($"  THREW: {error}");
            }
            sw.Stop();
            bool ok = code == 0;
            results.Add((probe.Name, ok, error, sw.Elapsed.TotalSeconds));
            if (!ok)
                Console.WriteLine($"::error::CI probe '{probe.Name}' FAILED (exit {code}){(error != null ? " — " + error : "")}");
        }

        Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);        // final restore
        try { Directory.Delete(corpusDir, recursive: true); } catch { /* best-effort temp cleanup */ }

        // ---- summary ----
        swAll.Stop();
        var failed = results.Where(r => !r.Ok).ToList();
        Console.WriteLine();
        Console.WriteLine("================================================================");
        Console.WriteLine($" ci-all summary — {results.Count - failed.Count}/{results.Count} passed in {swAll.Elapsed.TotalMinutes:N2} min");
        Console.WriteLine("================================================================");
        Console.WriteLine(" slowest probes:");
        foreach (var r in results.OrderByDescending(r => r.Secs).Take(8))
            Console.WriteLine($"   {r.Secs,6:N1}s  {r.Name}");
        if (failed.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($" FAILED ({failed.Count}):");
            foreach (var r in failed)
                Console.WriteLine($"   - {r.Name}{(r.Error != null ? " — " + r.Error : "")}");
        }
        Console.WriteLine(failed.Count == 0
            ? "\n================ ALL PASS ================"
            : $"\n================ {failed.Count} PROBE(S) FAILED ================");
        return failed.Count == 0 ? 0 : 1;
    }
}

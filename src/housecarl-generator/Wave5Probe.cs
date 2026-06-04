using System.Reflection;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlGenerator;

/// <summary>
/// Wave 5 SCOUT (header / orphan) — the two unknowns, surfaced before any capability is built (the wave-3/wave-4
/// scout-first precedent). Both modes are recon only: they make no change to any tracked file, report loud (Q3),
/// and exist so the wave's real capability/proof is built against measured reality, not a guess.
///
///   header-probe — confirm the mod's ModHeader is reachable as a MUTABLE write root (it is a singleton property
///     <c>mod.ModHeader</c>, NOT a flat SkyrimGroup&lt;T&gt; nor a FormKey'd record, so the record-resolution path
///     can't reach it), and confirm the engine's existing ApplyVerb writes its leaf kinds (scalar string, scalar,
///     struct-element Add). This proves ModHeader needs NO new value-handling — only a root entry + a proof.
///
///   pex-probe — the PEX round-trip GATE (project_pex_prefer_source_policy): does Mutagen's PexFile read+write
///     reproduce a real .pex BYTE-FOR-BYTE? Sweeps a diverse sample (default) or one explicit file. The result
///     decides whether wave 5 WIRES a PEX write slice (gate clears across the sample) or records a loud
///     Mutagen-delta residual for the divergent subset (the cornerstone's one legitimate residual), never silent.
///     Usage: <c>pex-probe</c> (sweep) · <c>pex-probe --n 500</c> · <c>pex-probe &lt;file.pex&gt;</c> (one, verbose).
/// </summary>
public static class Wave5Probe
{
    const string StockGameData = @"C:\MO2\Instance\Stock Game\Data";
    const string ModsDir = @"C:\MO2\Instance\mods";

    // ------------------------------------------------------------------ header-probe
    public static int RunHeaderProbe(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" wave-5 scout: ModHeader mutable-root reachability");
        Console.WriteLine("================================================================");

        var patch = new SkyrimMod(new ModKey("HousecarlHeaderProbe", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var hdr = patch.ModHeader;
        Console.WriteLine($"new SkyrimMod().ModHeader runtime type: {hdr.GetType().FullName}");
        Console.WriteLine($"   writable?  {(hdr.GetType().GetProperty("Author")?.CanWrite ?? false)} (Author set-accessor present)");

        void Try(string label, WriteRequest req)
        {
            try { WriteEngine.ApplyVerb(hdr, req); Console.WriteLine($"   [OK]   {label}"); }
            catch (Exception ex) { Console.WriteLine($"   [THROW] {label} -> {ex.GetType().Name}: {ex.Message}"); }
        }
        Console.WriteLine("Engine ApplyVerb across header leaf kinds (fresh header, in-memory):");
        Try("Author = 'houseCARL' (scalar string)", new() { RecordType = "SkyrimModHeader", Path = new[] { "Author" }, Verb = "Set", Value = "houseCARL" });
        Try("Description = 'wave-5 probe' (scalar string)", new() { RecordType = "SkyrimModHeader", Path = new[] { "Description" }, Verb = "Set", Value = "wave-5 probe" });
        Try("FormVersion = 44 (scalar)", new() { RecordType = "SkyrimModHeader", Path = new[] { "FormVersion" }, Verb = "Set", Value = "44" });
        Try("MasterReferences Add {Master=Skyrim.esm} (struct-element)", new() { RecordType = "SkyrimModHeader", Path = new[] { "MasterReferences" }, Verb = "Add", Struct = new StructSpec { Type = "MasterReference", Fields = new() { ["Master"] = "Skyrim.esm" } } });

        var read = hdr.GetType().GetProperty("Author")?.GetValue(hdr);
        Console.WriteLine($"   read-back Author: {read}");
        Console.WriteLine();
        Console.WriteLine("=== header-probe done — see [OK]/[THROW] above; ModHeader is a direct mutable root the engine already writes. ===");
        return 0;
    }

    // ------------------------------------------------------------------ pex-probe (the GATE)
    public static int RunPexProbe(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" wave-5 scout: PEX read->write round-trip GATE (project_pex_prefer_source_policy)");
        Console.WriteLine("================================================================");

        var pexAsm = typeof(IMajorRecordGetter).Assembly;
        var pexType = pexAsm.GetType("Mutagen.Bethesda.Pex.PexFile")
                      ?? AllTypes().FirstOrDefault(t => t.FullName == "Mutagen.Bethesda.Pex.PexFile");
        if (pexType is null) { Console.Error.WriteLine("error: Mutagen.Bethesda.Pex.PexFile type not found — surface (Q3)."); return 1; }
        Console.WriteLine($"PexFile type:  {pexType.FullName}  (asm {pexType.Assembly.GetName().Name})");

        var readMethods = pexType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name is "CreateFromFile" or "CreateFromStream").ToList();
        var writer = FindWriteMethods(pexType).FirstOrDefault();
        Console.WriteLine("   read factories: " + string.Join("; ", readMethods.Select(Sig)));
        Console.WriteLine("   write method:   " + (writer is null ? "(none found)" : Sig(writer)));
        if (writer is null || readMethods.All(m => m.Name != "CreateFromFile")) { Console.Error.WriteLine("error: PEX read/write API not located — surface (Q3)."); return 1; }
        var fromFile = readMethods.First(m => m.Name == "CreateFromFile");
        Console.WriteLine();

        // One explicit file -> verbose single round-trip; else SWEEP a diverse sample.
        var explicitFile = args.FirstOrDefault(a => a.EndsWith(".pex", StringComparison.OrdinalIgnoreCase));
        if (explicitFile is not null)
        {
            if (!File.Exists(explicitFile)) { Console.Error.WriteLine($"error: {explicitFile} not found."); return 1; }
            var outPath = Path.Combine(Path.GetTempPath(), "hc-pex-diff-" + Guid.NewGuid().ToString("N") + ".pex");
            var pex = InvokeRead(fromFile, explicitFile);
            InvokeWrite(writer, pex!, outPath);
            var orig = File.ReadAllBytes(explicitFile);
            var round = File.ReadAllBytes(outPath);
            try { File.Delete(outPath); } catch { }
            bool same = orig.Length == round.Length && orig.AsSpan().SequenceEqual(round);
            Console.WriteLine($"Sample .pex:   {explicitFile} ({orig.Length}B -> {round.Length}B) -> {(same ? "BYTE-IDENTICAL" : "DIVERGED")}");
            if (!same)
            {
                int fd = 0; while (fd < Math.Min(orig.Length, round.Length) && orig[fd] == round[fd]) fd++;
                int diffCount = 0; for (int i = 0; i < Math.Min(orig.Length, round.Length); i++) if (orig[i] != round[i]) diffCount++;
                int s = Math.Max(0, fd - 8), n = 32;
                Console.WriteLine($"   first diff @ offset {fd}; total differing bytes {diffCount}/{orig.Length}");
                Console.WriteLine($"   orig  [{s}..]: {Hex(orig, s, n)}");
                Console.WriteLine($"   round [{s}..]: {Hex(round, s, n)}");
                Console.WriteLine($"   orig  ascii  : {Ascii(orig, s, n)}");
                Console.WriteLine($"   round ascii  : {Ascii(round, s, n)}");
            }
            Console.WriteLine(same ? "=== PEX GATE: PASS (1 file). ===" : "=== PEX GATE: DIVERGED (1 file). ===");
            return same ? 0 : 2;
        }

        int cap = 400;
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--n" && int.TryParse(args[i + 1], out var n)) cap = n;
        var sample = CollectPexSample(cap);
        Console.WriteLine($"Sweeping {sample.Count} .pex (diverse sample, cap {cap}) from {ModsDir} + stock scripts ...");
        if (sample.Count == 0) { Console.Error.WriteLine("error: no .pex found to sweep."); return 1; }

        int identical = 0; var diverged = new List<string>(); var errors = new List<string>();
        long totalBytes = 0;
        foreach (var p in sample)
        {
            var r = RoundTrip(fromFile, writer, p);
            totalBytes += r.origLen;
            if (r.error is not null) errors.Add($"{Short(p)}: {r.error}");
            else if (r.identical) identical++;
            else diverged.Add($"{Short(p)}: {r.origLen}B->{r.roundLen}B @off {r.firstDiff}");
        }

        Console.WriteLine();
        Console.WriteLine($"round-trip sweep: {identical}/{sample.Count} BYTE-IDENTICAL; {diverged.Count} diverged; {errors.Count} read/write error(s). ({totalBytes / 1024} KiB scanned)");
        if (diverged.Count > 0) { Console.WriteLine("   DIVERGED (the Mutagen-delta subset — would stay loud-deferred):"); foreach (var d in diverged.Take(25)) Console.WriteLine($"        {d}"); if (diverged.Count > 25) Console.WriteLine($"        ... +{diverged.Count - 25} more"); }
        if (errors.Count > 0) { Console.WriteLine("   ERRORS (read/write threw — surface):"); foreach (var e in errors.Take(25)) Console.WriteLine($"        {e}"); if (errors.Count > 25) Console.WriteLine($"        ... +{errors.Count - 25} more"); }
        Console.WriteLine();
        var clean = diverged.Count == 0 && errors.Count == 0;
        Console.WriteLine(clean
            ? $"=== PEX GATE: PASS — Mutagen reads+writes ALL {sample.Count} sampled .pex byte-for-byte. PEX-write can be wired + byte-proven. ==="
            : $"=== PEX GATE: PARTIAL — {identical}/{sample.Count} clean; the {diverged.Count + errors.Count} divergent/error file(s) are a Mutagen-delta residual (loud-deferred); prefer .psc source for those. ===");
        return clean ? 0 : 2;
    }

    /// <summary>Read a .pex, write it straight back (no mutation) to a temp file, byte-compare to the source.</summary>
    static (bool identical, int origLen, int roundLen, int firstDiff, string? error) RoundTrip(MethodInfo fromFile, MethodInfo writer, string path)
    {
        var outPath = Path.Combine(Path.GetTempPath(), "hc-pex-" + Guid.NewGuid().ToString("N") + ".pex");
        try
        {
            var pex = InvokeRead(fromFile, path);
            if (pex is null) return (false, 0, 0, -1, "read returned null");
            InvokeWrite(writer, pex, outPath);
            if (!File.Exists(outPath)) return (false, 0, 0, -1, "write produced no file");
            var orig = File.ReadAllBytes(path);
            var round = File.ReadAllBytes(outPath);
            bool same = orig.Length == round.Length && orig.AsSpan().SequenceEqual(round);
            int firstDiff = -1;
            if (!same) for (int i = 0; i < Math.Min(orig.Length, round.Length); i++) if (orig[i] != round[i]) { firstDiff = i; break; }
            return (same, orig.Length, round.Length, firstDiff, null);
        }
        catch (Exception ex) { return (false, 0, 0, -1, Flatten(ex)); }
        finally { try { File.Delete(outPath); } catch { } }
    }

    /// <summary>A diverse .pex sample: stride across the full mods/ enumeration so many different mods are represented
    /// (not the alphabetically-first one's whole script folder), plus any loose stock-game scripts.</summary>
    static List<string> CollectPexSample(int cap)
    {
        var all = new List<string>();
        var stock = Path.Combine(StockGameData, "Scripts");
        if (Directory.Exists(stock)) all.AddRange(Directory.EnumerateFiles(stock, "*.pex"));
        if (Directory.Exists(ModsDir))
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6, IgnoreInaccessible = true };
            all.AddRange(Directory.EnumerateFiles(ModsDir, "*.pex", opts));
        }
        if (all.Count <= cap) return all;
        var stride = (double)all.Count / cap;                       // even stride => spread across mods + sizes
        var picked = new List<string>(cap);
        for (int i = 0; i < cap; i++) picked.Add(all[(int)(i * stride)]);
        return picked;
    }

    static List<MethodInfo> FindWriteMethods(Type pexType)
    {
        var outp = new List<MethodInfo>();
        outp.AddRange(pexType.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name.StartsWith("Write")));
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().Where(a => (a.GetName().Name ?? "").StartsWith("Mutagen")))
            foreach (var t in SafeTypes(asm).Where(t => t is { IsAbstract: true, IsSealed: true }))
                outp.AddRange(t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => (m.Name is "WritePexFile" or "Write") && m.GetParameters().Length >= 1
                        && m.GetParameters()[0].ParameterType.IsAssignableFrom(pexType)));
        return outp.GroupBy(Sig).Select(g => g.First()).ToList();
    }

    static object? InvokeRead(MethodInfo m, string path)
    {
        var ps = m.GetParameters();
        var argv = new object?[ps.Length];
        argv[0] = path;
        for (int i = 1; i < ps.Length; i++) argv[i] = DefaultArg(ps[i]);
        return m.Invoke(null, argv);
    }

    static void InvokeWrite(MethodInfo m, object pex, string outPath)
    {
        var ps = m.GetParameters();
        var argv = new object?[ps.Length];
        bool pathSet = false, instSet = false;
        for (int i = 0; i < ps.Length; i++)
        {
            var pt = ps[i].ParameterType;
            if (!instSet && pt.IsInstanceOfType(pex)) { argv[i] = pex; instSet = true; }
            else if (!pathSet && pt == typeof(string)) { argv[i] = outPath; pathSet = true; }
            else argv[i] = DefaultArg(ps[i]);
        }
        m.Invoke(m.IsStatic ? null : pex, argv);
    }

    /// <summary>GameCategory.Skyrim / enum default / value-type zero / null for a parameter we don't otherwise supply.</summary>
    static object? DefaultArg(ParameterInfo p)
    {
        if (p.HasDefaultValue) return p.DefaultValue;
        var t = p.ParameterType;
        if (t.IsEnum)
        {
            foreach (var n in new[] { "Skyrim", "SkyrimSE" })
            { var v = Enum.GetNames(t).FirstOrDefault(x => x == n); if (v is not null) return Enum.Parse(t, v); }
            return Enum.GetValues(t).GetValue(0);
        }
        return t.IsValueType ? System.Activator.CreateInstance(t) : null;
    }

    static string Hex(byte[] b, int start, int n)
    { var sb = new System.Text.StringBuilder(); for (int i = start; i < Math.Min(b.Length, start + n); i++) sb.Append(b[i].ToString("X2")).Append(' '); return sb.ToString().TrimEnd(); }

    static string Ascii(byte[] b, int start, int n)
    { var sb = new System.Text.StringBuilder(); for (int i = start; i < Math.Min(b.Length, start + n); i++) sb.Append(b[i] >= 32 && b[i] < 127 ? (char)b[i] : '.'); return sb.ToString(); }

    static string Short(string path)
    {
        var parts = path.Replace('/', '\\').Split('\\');
        return parts.Length >= 2 ? parts[^2] + "\\" + parts[^1] : path;
    }

    static string Sig(MethodInfo m) =>
        $"{(m.IsStatic ? "static " : "")}{m.ReturnType.Name} {m.DeclaringType?.Name}.{m.Name}(" +
        string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}")) + ")";

    static IEnumerable<Type> AllTypes() =>
        AppDomain.CurrentDomain.GetAssemblies().Where(a => (a.GetName().Name ?? "").StartsWith("Mutagen")).SelectMany(SafeTypes);

    static IEnumerable<Type> SafeTypes(Assembly a)
    { try { return a.GetTypes(); } catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; } }

    static string Flatten(Exception ex)
    { var msgs = new List<string>(); for (Exception? e = ex; e is not null; e = e.InnerException) msgs.Add($"{e.GetType().Name}: {e.Message}"); return string.Join(" <- ", msgs); }
}

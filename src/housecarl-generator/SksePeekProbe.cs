using System.Text;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// SKSE tier-D static-peek guard (issue #199 tier D; plan dev/plans/SKSE_TIER_D_STATIC_PEEK_PLAN_2026-07-16.md).
/// Pins the three things tier D can silently get WRONG, in the direction where wrong looks clean:
///
///   Part 1 — <see cref="SksePeek"/> extraction. The UTF-16LE arm is the load-bearing one: modern C++ plugins use wide
///     strings, so an ASCII-only scanner would return a confident, half-blind "nothing embedded". The negative arms pin
///     the classification filter against the compiler noise (format strings, type soup, bare extensions) that would
///     otherwise be rendered as a DLL's config surface.
///   Part 2 — the import walk + the Debug-CRT verdict. <see cref="SksePluginReader.DebugCrtDlls"/> is a CURATED list
///     (the D-suffix is a convention, not a loader rule) pinned here with its provenance, the same posture as the
///     version-blob offset map. The tri-state arms pin that a FAILED walk never renders as "imports nothing".
///   Part 3 — the renderer arms over synthetic data: the load-order cross-check (present / ABSENT / no-answer), the
///     Debug-CRT "will not load" wording, the framing line, and the bare-peek loud error.
///
/// ARR 2.0 carries ZERO debug-build plugins (live gate, authoring time), so the sharpest check in tier D has no real
/// specimen to ride — which is exactly why it is pinned synthetically here rather than trusted to a live run.
/// Self-contained: planted byte fixtures + the runner's own PEs; no MO2 instance, no game data.
/// </summary>
internal static class SksePeekProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" skse-peek guard — tier D: imports, strings, Debug-CRT, render");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        // ══ Part 1: SksePeek extraction ══
        Console.WriteLine("── Part 1: string extraction (ASCII + UTF-16LE + the classification filter) ──");

        // ---- A: ASCII runs — a config path and a plugin name are extracted from image-like noise. ----
        Console.WriteLine("\n--- A: ASCII extraction ---");
        var a = SksePeek.ScanBytes(Image(Ascii("Data\\SKSE\\Plugins\\SkyPatcher\\armor\\"), Junk(64),
                                        Ascii("Dawnguard.esm"), Junk(32), Ascii("nope")));
        Check(a.ConfigPaths.Contains("Data\\SKSE\\Plugins\\SkyPatcher\\armor\\"), "an embedded config path is extracted");
        Check(a.PluginRefs.Contains("Dawnguard.esm"), "an embedded plugin name is extracted");
        Check(!a.Failed && a.Note is null, "a clean scan carries no failure note");
        Check(a.RunsScanned >= 3, $"accounting counts every run scanned, not just the shown ones (got {a.RunsScanned})");

        // ---- B: UTF-16LE runs — THE coverage arm. An ASCII-only scanner returns a confident half-blind answer. ----
        Console.WriteLine("\n--- B: UTF-16LE extraction (the silent-half-coverage guard) ---");
        var b = SksePeek.ScanBytes(Image(Wide("Data\\SKSE\\Plugins\\Trails\\config.json"), Junk(16), Wide("Skyrim.esm")));
        Check(b.ConfigPaths.Contains("Data\\SKSE\\Plugins\\Trails\\config.json"), "a WIDE config path is extracted");
        Check(b.PluginRefs.Contains("Skyrim.esm"), "a WIDE plugin name is extracted");
        var bMixed = SksePeek.ScanBytes(Image(Ascii("Data\\a.ini"), Wide("Data\\b.toml")));
        Check(bMixed.ConfigPaths.Count == 2, $"ASCII and WIDE strings in ONE image are both found (got {bMixed.ConfigPaths.Count})");

        // ---- C: the classification filter — the noise that must NOT read as a DLL's config surface. ----
        Console.WriteLine("\n--- C: classification negatives (compiler noise is not a finding) ---");
        var c = SksePeek.ScanBytes(Image(
            Ascii("%s.json"),                                   // a format string, not a path
            Ascii(".esp"),                                      // a bare extension constant, not a reference
            Ascii("class std::basic_string<char>.ini"),         // C++ type soup that trips a naive extension test
            Ascii("\"quoted.esm\"")));                          // a quoted token — a sentence about a plugin, not a name
        Check(c.ConfigPaths.Count == 0, $"format strings + type soup are NOT config paths (got [{string.Join("|", c.ConfigPaths)}])");
        Check(c.PluginRefs.Count == 0, $"a bare extension + a quoted token are NOT plugin refs (got [{string.Join("|", c.PluginRefs)}])");
        var cPath = SksePeek.ScanBytes(Image(Ascii("Data\\Dawnguard.esm")));
        Check(cPath.PluginRefs.Contains("Dawnguard.esm"),
              "a plugin ref inside a PATH yields the FILENAME (what the load-order cross-check keys on)");
        Check(cPath.ConfigPaths.Count == 0, "a path that IS a plugin ref classifies as the plugin ref, not double-counted");

        // fmt/spdlog placeholders — the DOMINANT modern format shape (CommonLibSSE-NG logs through spdlog), so a
        // classifier that only knows printf's % adjudicates a log template against the load order and flags it ABSENT.
        var cFmt = SksePeek.ScanBytes(Image(Ascii("{}.esp"), Ascii("loading {}.esm"), Ascii("%s.esp")));
        Check(cFmt.PluginRefs.Count == 0,
              $"fmt/spdlog {{}} and printf %s templates are NOT plugin names (got [{string.Join("|", cFmt.PluginRefs)}])");
        // …but a {}-bearing PATH stays config-surface signal: it is only ever SHOWN, never adjudicated, and
        // "versionlib-{}.bin" genuinely tells you the plugin reads Address Library (live-gate evidence, SkyPatcher).
        var cTmpl = SksePeek.ScanBytes(Image(Ascii("Data/SKSE/Plugins/versionlib-{}.bin")));
        Check(cTmpl.ConfigPaths.Contains("Data/SKSE/Plugins/versionlib-{}.bin"),
              "a {}-template PATH is still config surface (shown, not adjudicated — the deliberate asymmetry)");

        // ---- D: absence is a fact about the image, never a clean bill of health. ----
        Console.WriteLine("\n--- D: an image embedding nothing ---");
        var d = SksePeek.ScanBytes(Image(Junk(512)));
        Check(d.ConfigPaths.Count == 0 && d.PluginRefs.Count == 0, "an image with no strings yields nothing");
        Check(!d.Failed, "…and that is a SUCCESSFUL scan (absence proves nothing, but the scan still ran)");
        var dFail = SksePeek.Scan(Path.Combine(Path.GetTempPath(), "hc-peek-missing-" + Guid.NewGuid().ToString("N") + ".dll"));
        Check(dFail.Failed && dFail.Note is { Length: > 0 },
              "a missing image is a FAILED peek with a reason — never an empty-but-clean-looking result (Q3)");

        // ══ Part 2: import walk + Debug-CRT ══
        Console.WriteLine("\n── Part 2: import walk + the Debug-CRT verdict ──");

        // ---- E: the walk on a REAL native PE with a real import table. ----
        Console.WriteLine("\n--- E: import walk on a real native PE ---");
        string k32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "kernel32.dll");
        if (File.Exists(k32))
        {
            var e = SksePluginReader.Read(k32);
            Check(e.Imports is not null, "a real native PE's import directory WALKS (non-null = the walk succeeded)");
            Check(e.Imports is { Count: > 0 }, $"…and yields its imports (got {e.Imports?.Count ?? 0})");
            Check(e.Imports?.All(i => i == i.ToLowerInvariant()) ?? false, "import names are normalized lower-case for comparison");
            Check(e.Kind == SksePluginReader.SksePluginKind.NotSkse, "a system DLL is still classified NotSkse (no SKSE export)");
        }
        else Check(false, $"expected a system kernel32.dll to walk at '{k32}'");

        // ---- E2: an unresolvable import NAME fails the whole walk (review finding #3). ----
        // Take a real PE and point its first import descriptor's Name RVA at nothing. Skipping the bad entry (the
        // original behavior) returned a SHORT list that still renders as a complete "imports (N): …" — and if the
        // dropped entry were vcruntime140d.dll, the Debug-CRT check would report a clean bill of health. Corruption
        // must reach the caller as UNKNOWN, matching this walk's directory-level posture.
        Console.WriteLine("\n--- E2: a corrupt import name is UNKNOWN, not a short list ---");
        if (File.Exists(k32))
        {
            var raw = File.ReadAllBytes(k32);
            int nameOff = FirstImportNameOffset(raw);
            if (nameOff > 0)
            {
                Check(SksePluginReader.ReadBytes("k32.dll", raw).Imports is { Count: > 0 }, "control: the unpatched image walks");
                BitConverter.GetBytes(0x7FFFFFFF).CopyTo(raw, nameOff);      // an RVA that maps to no section
                Check(SksePluginReader.ReadBytes("k32-corrupt.dll", raw).Imports is null,
                      "an unresolvable import-name RVA fails the WHOLE walk → null (UNKNOWN), never a silent short list");
            }
            else Check(false, "expected to locate the first import descriptor's Name RVA in a real PE");
        }

        // ---- F: the tri-state. A managed assembly has no import directory → EMPTY (walked), never null (unknown). ----
        Console.WriteLine("\n--- F: the imports tri-state (empty ≠ unknown) ---");
        var f = SksePluginReader.Read(typeof(SksePluginReader).Assembly.Location);
        Check(f.Imports is not null, "a managed assembly's (absent) import directory is a SUCCESSFUL walk → empty, not null");
        Check(f.DebugCrtImports.Count == 0, "…and it imports no debug CRT");

        // ---- G: the Debug-CRT list + verdict semantics. ----
        Console.WriteLine("\n--- G: Debug-CRT classification ---");
        Check(SksePluginReader.DebugCrtDlls.Contains("vcruntime140d.dll") &&
              SksePluginReader.DebugCrtDlls.Contains("msvcp140d.dll") &&
              SksePluginReader.DebugCrtDlls.Contains("ucrtbased.dll"),
              "the curated list pins the modern debug-CRT family (vcruntime140d / msvcp140d / ucrtbased)");
        Check(!SksePluginReader.DebugCrtDlls.Contains("vcruntime140.dll") &&
              !SksePluginReader.DebugCrtDlls.Contains("d3d11.dll") &&
              !SksePluginReader.DebugCrtDlls.Contains("dinput8.dll"),
              "…and NOT their release twins or the d-suffixed innocents (the list is curated, not a 'ends in d' pattern)");
        Check(SksePluginReader.DebugCrtImportsOf(Info(["kernel32.dll", "VCRUNTIME140D.dll"])).Count == 1,
              "a debug-CRT import is caught case-INSENSITIVELY (image tables are not case-normalized)");
        Check(SksePluginReader.DebugCrtImportsOf(Info(["kernel32.dll", "vcruntime140.dll"])).Count == 0,
              "a RELEASE runtime import is not a debug finding");
        Check(SksePluginReader.DebugCrtImportsOf(Info(null)).Count == 0,
              "a FAILED walk yields no debug-CRT claim — absence of evidence is not evidence of absence (Q3)");

        // ══ Part 3: renderer ══
        Console.WriteLine("\n── Part 3: SkseInventoryWire render arms ──");

        // ---- H: the bare-peek loud error (plan §3a). ----
        Console.WriteLine("\n--- H: peek= argument check ---");
        Check(SkseInventoryWire.PeekArgError(peek: true, filter: null) is { Length: > 0 },
              "a bare peek=true FAILS LOUD (never a silent whole-layer image dump)");
        Check(SkseInventoryWire.PeekArgError(peek: true, filter: "   ") is { Length: > 0 }, "…a blank filter too");
        Check(SkseInventoryWire.PeekArgError(peek: true, filter: "SkyPatcher") is null, "peek + filter is valid");
        Check(SkseInventoryWire.PeekArgError(peek: false, filter: null) is null, "no peek, no filter = the normal inventory");

        // ---- I: the load-order cross-check — present / ABSENT / no-answer. ----
        Console.WriteLine("\n--- I: embedded-plugin cross-check ---");
        var peek = new SksePeekResult(["Data\\SKSE\\Plugins\\Thing\\x.json"], ["Dawnguard.esm", "GhostMod.esp"], 40, 4096, null);
        string withOrder = Render(Data(Entry("Thing.dll", peek, Info(["kernel32.dll"])), active: ["Dawnguard.esm"]), "Thing");
        Check(withOrder.Contains("Dawnguard.esm") && withOrder.Contains("(in your load order)"),
              "an embedded name present in the order renders as present");
        Check(withOrder.Contains("GhostMod.esp") && withOrder.Contains("NOT in your load order"),
              "an embedded name ABSENT from the order is flagged (the verify signal)");
        Check(withOrder.Contains("Data\\SKSE\\Plugins\\Thing\\x.json"), "the embedded config surface renders");
        Check(withOrder.Contains("CONTAINS") && withOrder.Contains("Absence proves nothing"),
              "the framing line always rides a peek (image contents ≠ behavior; absence proves nothing)");
        Check(withOrder.Contains("40 string run"), "the scan accounting states the cut (filter, not the whole haystack)");

        string noOrder = Render(Data(Entry("Thing.dll", peek, Info(["kernel32.dll"])), active: null), "Thing");
        Check(!noOrder.Contains("NOT in your load order"),
              "with no resolved order, NO name is called absent — an unasked question has no answer (Q3)");

        // ---- I2: the SERVICE must actually be able to PRODUCE that null. ----
        // The review's finding #1: the renderer honored the null contract, but nothing could emit it —
        // ReadComposition never throws on a missing plugins.txt/loadorder.txt, and the asset resolver needs only
        // modlist.txt, so a half-readable profile yielded an EMPTY-but-non-null set and flagged every embedded name
        // (Dawnguard.esm included) ABSENT. Arm I pinned the renderer and missed it because it hand-built the null.
        // This arm drives the REAL producer over a profile whose plugin lists don't exist.
        Console.WriteLine("\n--- I2: the empty-composition → null contract, through the real service ---");
        string prof = Path.Combine(Path.GetTempPath(), "hc-peek-prof-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(prof);
            File.WriteAllText(Path.Combine(prof, "modlist.txt"), "+SomeMod\n");   // readable; NO plugins.txt / loadorder.txt
            var warn = new List<string>();
            var comp = Mo2LoadOrder.ReadComposition(prof, warn);
            Check(comp.ActivePluginNames.Count == 0 && comp.ImplicitPluginNames.Count == 0,
                  "a profile with no plugins.txt/loadorder.txt yields an EMPTY composition (no throw — the bug's premise)");
            Check(warn.Count > 0, "…and the read SURFACES why (the warning the peek path must not discard)");
            // THE REAL PRODUCER, not a copy of its rule — the whole reason the original bug survived arm I is that the
            // arm hand-built the null instead of exercising the code that has to emit one.
            Check(LoadOrderService.PeekPluginSet(comp) is null,
                  "…so the peek path hands the renderer NULL, never an empty set that would flag every name ABSENT (Q3)");
            // …and the healthy direction still resolves, including a force-loaded master absent from plugins.txt.
            var healthy = new Mo2Composition([], [], ["Skyrim.esm", "Dawnguard.esm"],
                new HashSet<string>(["Skyrim.esm"], StringComparer.OrdinalIgnoreCase), [], ["Dawnguard.esm"]);
            var got = LoadOrderService.PeekPluginSet(healthy);
            Check(got is not null && got.Contains("Skyrim.esm") && got.Contains("Dawnguard.esm"),
                  "a real composition resolves, implicit force-loaded masters included (Dawnguard.esm is never ABSENT)");
        }
        finally { try { Directory.Delete(prof, true); } catch { /* temp scratch */ } }

        // ---- I3: peek honored with nothing to show is still an unanswered question. ----
        Console.WriteLine("\n--- I3: peek= matched nothing peekable ---");
        var bsaOnly = new SkseFileEntry("Bsa.dll", "Bsa.dll", "", [new SkseProvider("Archive.bsa", "BSA")], null, "BSA-only");
        string noPeek = SkseInventoryWire.Render(
            new SkseInventoryData([bsaOnly], [], 0, "1.6.1170.0", [], false, [], "TestProfile", null, PeekRequested: true),
            "Bsa", 80_000);
        Check(noPeek.Contains("peek=true matched no loose DLL"),
              "a peek that matched only a BSA-only DLL SAYS nothing was peeked (never a silent no-op)");

        // ---- J: the Debug-CRT verdict wording — the one peek line allowed "will not load". ----
        Console.WriteLine("\n--- J: Debug-CRT render ---");
        // A debug CRT that is certainly NOT on the machine (a fabricated name in the pinned family shape would not be
        // findable) — use the real name and assert against what the machine actually reports, so the arm is honest on a
        // CI box (no VS → will NOT load) and on a developer's (VS → the author-facing wording) alike.
        var crtEntry = Entry("Debug.dll", peek, Info(["kernel32.dll", "vcruntime140d.dll"]));
        string crtOut = Render(Data(crtEntry, active: ["Dawnguard.esm"]), "Debug");
        Check(crtOut.Contains("DEBUG BUILD") && crtOut.Contains("vcruntime140d.dll"), "a debug-CRT import is flagged with the culprit named");
        bool present = SksePluginReader.IsSystemDllResolvable("vcruntime140d.dll");
        Check(present ? crtOut.Contains("loads on THIS machine") : crtOut.Contains("will NOT load"),
              $"the verdict matches THIS machine (debug runtime resolvable = {present}) — never a machine-blind claim");
        Check(crtOut.Contains("error 126"), "…and names the actual loader failure (error 126)");

        // BOTH wordings, in ONE run, via the injected machine probe. Without this seam CI (no Visual Studio) only ever
        // pins the "will NOT load" arm and a dev box only the other — leaving whichever half the current machine can't
        // produce free to rot. The machine-dependence IS the finding, so both halves have to be pinned.
        string absent = SkseTools_DebugCrtVerdict(["vcruntime140d.dll"], _ => false);
        string here = SkseTools_DebugCrtVerdict(["vcruntime140d.dll"], _ => true);
        Check(absent.Contains("will NOT load") && absent.Contains("error 126"),
              "debug runtime ABSENT ⇒ the flat 'will NOT load' verdict");
        Check(here.Contains("loads on THIS machine") && here.Contains("for anyone who doesn't"),
              "debug runtime PRESENT ⇒ the author-facing 'loads for you, breaks for everyone else' verdict");
        Check(!here.Contains("will NOT load"),
              "…and the present case NEVER makes the flat claim (the wrong answer on a dev box — the whole point)");

        // The whole-layer escalation (plan §8.3): the flag rides an UNFILTERED inventory, no peek= needed.
        string layer = Render(Data(crtEntry, active: null), filter: null);
        Check(layer.Contains("DEBUG-BUILD plugins"), "a debug build surfaces on the UNFILTERED inventory (§8.3 escalation)");
        string clean = Render(Data(Entry("Fine.dll", null, Info(["kernel32.dll"])), active: null), filter: null);
        Check(!clean.Contains("DEBUG-BUILD plugins"), "…and a clean layer says nothing about debug builds");

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "skse-peek guard: ALL PASS" : $"skse-peek guard: {fail} FAILURE(S)");
        return fail == 0 ? 0 : 1;
    }

    // ---- fixture helpers ----

    /// <summary>The renderer's pure Debug-CRT verdict with the machine probe injected (internal via InternalsVisibleTo).</summary>
    static string SkseTools_DebugCrtVerdict(IReadOnlyList<string> crt, Func<string, bool> resolvable) =>
        SkseInventoryWire.DebugCrtVerdict(crt, resolvable);

    /// <summary>File offset of the FIRST <c>IMAGE_IMPORT_DESCRIPTOR</c>'s Name RVA field (descriptor + 0x0C) in a raw PE
    /// image, or -1. Walks the section table to map the directory RVA to a file offset — the probe's own mapping, kept
    /// independent of the reader it is testing (a fixture that reused the code under test would prove nothing).</summary>
    static int FirstImportNameOffset(byte[] raw)
    {
        try
        {
            using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(raw, writable: false));
            int rva = pe.PEHeaders.PEHeader!.ImportTableDirectory.RelativeVirtualAddress;
            if (rva == 0) return -1;
            foreach (var s in pe.PEHeaders.SectionHeaders)
                if (rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData))
                    return s.PointerToRawData + (rva - s.VirtualAddress) + 0x0C;
        }
        catch { /* fixture setup only — the arm reports a failure if this can't locate the table */ }
        return -1;
    }

    /// <summary>A synthetic PE-ish image: the parts concatenated. The scanner is a byte walker, so real PE structure is
    /// irrelevant to extraction — the real-PE paths are covered by arms E/F against actual images.</summary>
    static byte[] Image(params byte[][] parts)
    {
        var ms = new MemoryStream();
        foreach (var p in parts) ms.Write(p, 0, p.Length);
        return ms.ToArray();
    }

    static byte[] Ascii(string s) => [.. Encoding.ASCII.GetBytes(s), 0];
    static byte[] Wide(string s) => [.. Encoding.Unicode.GetBytes(s), 0, 0];

    /// <summary>Non-printable filler — code bytes. Deterministic (no RNG: a probe that varies can't pin anything).</summary>
    static byte[] Junk(int n)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)(i % 0x1F);   // all < 0x20 ⇒ never a printable run
        return b;
    }

    static SksePluginReader.SksePluginInfo Info(IReadOnlyList<string>? imports) =>
        new("x.dll", SksePluginReader.SksePluginKind.Modern, true,
            new SksePluginReader.SkseVersionInfo("Test", "Tester", "", "1.0.0", true, false, false, false, [], null),
            null, imports);

    static SkseFileEntry Entry(string file, SksePeekResult? peek, SksePluginReader.SksePluginInfo info) =>
        new(file, file, "", [new SkseProvider("TestMod", "loose")], info, null, peek);

    static SkseInventoryData Data(SkseFileEntry dll, IEnumerable<string>? active) =>
        new([dll], [], 0, "1.6.1170.0", [], false, [], "TestProfile",
            active is null ? null : new HashSet<string>(active, StringComparer.OrdinalIgnoreCase));

    static string Render(SkseInventoryData d, string? filter) => SkseInventoryWire.Render(d, filter, 80_000);
}

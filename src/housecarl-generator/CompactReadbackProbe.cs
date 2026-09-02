using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD (standing CI instrument) for the COMPACT in-place verify read-back (HCBR-2026-06-28-01).
///
/// The report: a multi-op <c>bulk_apply</c> editing N records IN PLACE force-ran the touched-record verify as a DEEP,
/// depth-16, whole-record dump (the model-C floor substitute). For records that already hold large list fields the
/// combined dump blew past the host token cap — at the 80k default, even the "gracefully truncated" 80k string STILL
/// exceeded the host limit and spilled to a file, reading as "only some of the N ops applied" (a SILENT, Q3-breaking
/// verification gap, though the writes themselves were correct).
///
/// The fix keeps the verify (corruption DETECTION unchanged — the deep re-read still RUNS) but changes its OUTPUT:
///   - DEFAULT (full_readback=false) renders COMPACT — one line per record (re-read-clean + field count, or a NAMED
///     failure) plus the "what landed" identity per op — covering ALL N records, never the silent spill.
///   - full_readback=true still gives the deep field-by-field dump, now bounded at the LOWER Wire.ReadbackMaxChars so
///     the cut-off output stays under the host limit and its truncation note actually reaches the caller.
///
/// Self-contained, in the VerifyLoopProbe pattern: synthesizes a masterless plugin with N weapons ON DISK, drives the
/// REAL <see cref="WritePatchBuilder.ApplyInPlace"/> (which forces the verify ON) ONCE, then renders that single
/// outcome THREE ways through the REAL <see cref="WriteTools.Render"/> (compact / full+low-cap / full+default cap).
///
/// RED before the fix: the compact arm's "all N re-read-clean lines / not the deep dump" assertions fail on the old
/// code (in-place always deep-dumped); the low-cap arm's bounded+note assertion fails (old default cap was 80k, above
/// the host limit). GREEN on the fixed code.
///
/// Run: <c>dotnet run --project src/housecarl-generator -- compact-readback-guard</c>
/// </summary>
public static class CompactReadbackProbe
{
    static int _pass, _fail;

    [CiProbe("compact-readback-guard")]
    public static int RunGuard(string[] args)
    {
        _pass = 0; _fail = 0;
        Console.WriteLine("################  REGRESSION GUARD — compact in-place verify read-back (HCBR-2026-06-28-01)  ################");
        Console.WriteLine();

        var dir = Path.Combine(Path.GetTempPath(), "hc_compact_readback_guard");
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        Directory.CreateDirectory(dir);

        const string pluginName = "hcCompact.esp";
        var pluginPath = Path.Combine(dir, pluginName);
        const int N = 6;   // the report's 6 recruit-open INFO lines, as 6 records here

        try
        {
            // ---- 1. A masterless plugin: one keyword to ADD, and N weapons to edit IN PLACE. Weapons are non-trivial
            //         records (Name / stats / bounds), so the DEEP dump of all N is large enough to exceed a low cap. ----
            var mod = new SkyrimMod(ModKey.FromNameAndExtension(pluginName), SkyrimRelease.SkyrimSE);
            var kwAdd = mod.Keywords.AddNew(); kwAdd.EditorID = "hcCompactKwAdded";
            FormKey kwAddFk = kwAdd.FormKey;
            var fks = new List<FormKey>();
            for (int i = 0; i < N; i++)
            {
                var w = mod.Weapons.AddNew();
                w.EditorID = $"hcCompactW{i}";
                w.Name = $"Compact Weapon {i}";
                w.BasicStats = new WeaponBasicStats { Damage = (ushort)(10 + i) };
                fks.Add(w.FormKey);
            }
            mod.BeginWrite.ToPath(pluginPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            using var resolver = LoadOrderResolver.Build(new[] { pluginPath });
            var rulebook = CorpusRulebook.Load(GenerateCorpus(dir));
            Console.WriteLine($"-- built masterless '{pluginName}' with {N} weapons + 1 keyword; editing all {N} IN PLACE --");
            Console.WriteLine();

            // ---- 2. ONE in-place bulk edit: Add the SAME keyword to all N weapons' (absent) Keywords list — the
            //         report's "same op across N records" shape. ApplyInPlace forces the touched-record verify ON. ----
            var edits = fks.Select(fk => new WritePatchBuilder.PatchEdit
            {
                Target = fk, Path = new[] { "Keywords" }, Verb = "Add", Value = kwAddFk.ToString(),
            }).ToArray();

            var outcome = WritePatchBuilder.ApplyInPlace(resolver, rulebook, edits, pluginPath, pluginName, fullReadback: true);
            Check($"SETUP: in-place ApplyInPlace succeeded over {N} records  [{outcome.Error ?? "ok"}]", outcome.Success);
            Check($"SETUP: the verify is forced ON — ReadBack carries all {N} touched records, none errored",
                  outcome.ReadBack is { } rb0 && rb0.Count == N && rb0.All(r => r.Error is null && r.Record is not null));
            Check("SETUP: every op captured a 'what landed' descriptor naming the added keyword",
                  outcome.Ops.Count == N && outcome.Ops.All(o => o.Landed is not null && o.Landed.Contains(kwAddFk.ToString())));
            Console.WriteLine();

            // ============ ARM A — DEFAULT render (full_readback=false): COMPACT, all N, never the deep spill ============
            var compact = WriteTools.Render(outcome, maxChars: 0, fullDump: false);
            Check($"COMPACT: every one of the {N} edited records is verified (one 're-read clean' line each) — none silently dropped",
                  CountOccurrences(compact, "re-read clean") == N);
            Check("COMPACT: it is NOT the deep field-by-field dump (no full-readback header)",
                  !compact.Contains("full read-back — the ENTIRE"));
            Check("COMPACT: it names what landed (the touched element + new count)",
                  compact.Contains("now ") && compact.Contains(kwAddFk.ToString()));
            Check($"COMPACT: the whole response stays small even with {N} non-trivial records (the headline fix: no 80k spill)",
                  compact.Length < 6_000);
            // Guard the CAP VALUE itself against the real consts (PR #127 review #2 — the prior check compared against a
            // hand-copied 24k mirror BELOW a stricter < 6_000 literal, so it could never fail and the cap was unguarded).
            // This fails if ReadbackMaxChars ever creeps back toward the 80k DefaultMaxChars that caused the host spill.
            Check("CAP: the read-back default cap is well under the host token ceiling (the 80k-spill regression guard)",
                  Wire.ReadbackMaxChars < Wire.DefaultMaxChars && Wire.ReadbackMaxChars <= 32_000);
            Console.WriteLine($"   -- compact render: {compact.Length} chars, {CountOccurrences(compact, "\n") + 1} lines --");
            Console.WriteLine();

            // ============ ARM B — full_readback=true at a LOW cap: bounded + an EXPLICIT note (never a silent spill) ====
            var fullLowCap = WriteTools.Render(outcome, maxChars: 1_500, fullDump: true);
            Check("FULL/low-cap: the deep dump is requested (full-readback header present)",
                  fullLowCap.Contains("full read-back — the ENTIRE"));
            Check("FULL/low-cap: it is bounded near the cap (NOT the 80k that the host rejected)",
                  fullLowCap.Length < 4_000);
            Check("FULL/low-cap: the truncation is EXPLICIT (a '[truncated …]' note reaches the caller, Q3 — never silent)",
                  fullLowCap.Contains("truncated"));
            Console.WriteLine($"   -- full/low-cap render: {fullLowCap.Length} chars --");
            Console.WriteLine();

            // ============ ARM C — full_readback=true at the DEFAULT cap: the deep dump is intact-or-explicitly-bounded ==
            var fullDefault = WriteTools.Render(outcome, maxChars: 0, fullDump: true);
            Check("FULL/default: the deep dump is present (full-readback header)",
                  fullDefault.Contains("full read-back — the ENTIRE"));
            Check($"FULL/default: it covers all {N} records OR notes its own truncation — never silently partial (Q3)",
                  CountOccurrences(fullDefault, "editorid=") == N || fullDefault.Contains("truncated"));
            Check("FULL/default: the deep dump shows real field content (the never-edited Name on a record)",
                  fullDefault.Contains("Compact Weapon 0"));
            Console.WriteLine();

            // ============ GROUND TRUTH — the edits themselves are correct (independent of the render) ============
            Check($"GROUND TRUTH: re-opening the written file, all {N} weapons gained the keyword (the writes were always correct)",
                  AllWeaponsGainedKeyword(pluginPath, fks, kwAddFk));

            Console.WriteLine();
            Console.WriteLine($"=== compact-readback-guard: {_pass} passed, {_fail} failed -> {(_fail == 0 ? "PASS" : "FAIL")} ===");
            return _fail == 0 ? 0 : 1;
        }
        catch (Exception ex) { Console.WriteLine($"   FAIL (unexpected): {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); return 1; }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    static bool AllWeaponsGainedKeyword(string path, IReadOnlyList<FormKey> weaponFks, FormKey kwFk)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            foreach (var fk in weaponFks)
            {
                var w = ov.Weapons.FirstOrDefault(x => x.FormKey == fk);
                if (w?.Keywords is null || !w.Keywords.Any(k => k.FormKey == kwFk)) return false;
            }
            return true;
        }
        catch { return false; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    // Generate the validator corpus BY CONSTRUCTION into the temp dir — the self-contained posture of the sibling
    // guards (no checked-in corpus.json, no game data, just slower).
    static string GenerateCorpus(string dir)
    {
        var genDir = Path.Combine(dir, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(dir, "corpus-ref"));
        return Path.Combine(genDir, "corpus.json");
    }

    static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    static void Check(string label, bool ok)
    {
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        if (ok) _pass++; else _fail++;
    }
}

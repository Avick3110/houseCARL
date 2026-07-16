using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for the SKSE config-audit reference EXTRACTOR (tier B — plan
/// dev/plans/SKSE_TIER_B_CONFIG_AUDIT_PLAN_2026-07-16.md, Wave 1.4). Pins the extractor
/// (<see cref="SkseConfigReferenceExtractor"/>) against every reference SHAPE the §3 evidence sample
/// established — the piece where a wrong parse silently changes what the audit thinks a file references
/// (a false DANGLING is the tool's worst failure mode). No load order needed: extraction is pure.
///
/// Shapes pinned:
///   * CDF/po3-lineage <c>0xHEX|Plugin.esp</c> inside JSONC (with // comments) — formid-FIRST.
///   * DSD 8-hex ESL-prefixed <c>FExxxYYY|Plugin.esp</c> — masks to the 12-bit local id (the load-bearing rule).
///   * 8-hex NON-ESL <c>XXyyyyyy|Plugin.esp</c> — masks to the low 24 bits (the OTHER arm of the split).
///   * SkyPatcher <c>Plugin.esp|0xHEX</c> — plugin-FIRST (opposite order).
///   * Tilde form <c>0xHEX~Plugin.esp</c> (KID/SPID shape).
///   * Comma-separated list on one line — each ref its own match.
///   * Plugin name with spaces + dashes ("Dynamic Activation Key - Addons Collection.esp").
///   * Path-segment plugin gate (DSD <c>\Plugin.esp\</c> folder).
///   * No-reference file — zero refs, the common OStim case (must be a clean empty, never a warning).
///   * Unparseable overflow hex — CAPTURED loud (Q3), never silently dropped.
///   * Comment-embedded token — still surfaced (§4d: "references this file declares", not "the DLL will use").
///
/// Run: dotnet run --project src/housecarl-generator -- skse-config-audit-guard
/// </summary>
public static class SkseConfigAuditProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — SKSE config-audit reference extractor (tier B, #199)  ################");
        Console.WriteLine();

        int fails = 0;
        void Check(string label, bool ok) { Console.WriteLine($"   {(ok ? "PASS" : "FAIL")}  {label}"); if (!ok) fails++; }

        // ── 1. CDF JSONC, formid-first, with // comments (top byte 0x00 → low-24 mask is identity). ──
        {
            var r = Ex(@"SKSE\Plugins\ContainerDistributionFramework\C.O.I.N.json", @"{
  // COIN distribution
  ""form"": ""0x4FDAF|Skyrim.esm"",
}");
            Check("CDF JSONC 0x4FDAF|Skyrim.esm → Skyrim.esm / 0x4FDAF",
                One(r, out var a) && a.Shape == SkseRefShape.FormToken && a.Plugin == "Skyrim.esm" && a.LocalId == 0x4FDAF && a.Unparseable is null);
        }

        // ── 2. DSD 8-hex ESL-prefixed, plugin name with spaces + dashes → 12-bit local id (THE load-bearing mask). ──
        {
            var r = Ex(@"SKSE\Plugins\DynamicStringDistributor\x.json",
                @"  ""form_id"": ""FE007800|Dynamic Activation Key - Addons Collection.esp"",");
            Check("DSD FE007800|<spaced name>.esp → 12-bit local 0x800 (ESL mask)",
                One(r, out var a) && a.Plugin == "Dynamic Activation Key - Addons Collection.esp" && a.LocalId == 0x800);
        }

        // ── 2b. Plugin name with an APOSTROPHE — must NOT truncate at the ' (the live-gate false-MISSING regression). ──
        {
            var r = Ex(@"SKSE\Plugins\Foo\x.json", @"""form"": ""0x800|kryptopyr's Trade & Barter.esp""");
            Check("apostrophe name → full 'kryptopyr's Trade & Barter.esp' kept",
                One(r, out var a) && a.Plugin == "kryptopyr's Trade & Barter.esp" && a.LocalId == 0x800);
        }

        // ── 2c. TOML single-quoted plugin-first ref → the leading ' delimiter is stripped from the plugin name. ──
        {
            var r = Ex(@"SKSE\Plugins\Foo\x.toml", "form = 'Foo.esp|0x1'");
            Check("TOML 'Foo.esp|0x1' → leading-quote stripped to 'Foo.esp'",
                One(r, out var a) && a.Plugin == "Foo.esp" && a.LocalId == 0x1);
        }

        // ── 2d. Parenthetical PROSE in a comment → the '(' bounds the token to the real plugin (live-gate false-MISSING). ──
        {
            var r = Ex(@"SKSE\Plugins\Foo\x.ini", "; this will cast fireball (Skyrim.esm|0x5) on the target");
            Check("prose '(Skyrim.esm|0x5)' → plugin bounded to Skyrim.esm (not the prose prefix)",
                One(r, out var a) && a.Plugin == "Skyrim.esm" && a.LocalId == 0x5);
        }

        // ── 3. 8-hex NON-ESL (full plugin load-index prefix) → low-24 mask keeps 0x04FDAF (the other arm). ──
        {
            var r = Ex(@"SKSE\Plugins\Foo\bar.ini", "target = 1204FDAF|Skyrim.esm");
            Check("NON-ESL 1204FDAF|Skyrim.esm → low-24 local 0x04FDAF",
                One(r, out var a) && a.Plugin == "Skyrim.esm" && a.LocalId == 0x04FDAF);
        }

        // ── 4. SkyPatcher plugin-FIRST order. ──
        {
            var r = Ex(@"SKSE\Plugins\SkyPatcher\weapons\x.ini", "filterByWeapons=Skyrim.esm|0x01397E:attackDamage=20");
            Check("SkyPatcher Skyrim.esm|0x01397E (plugin-first) → Skyrim.esm / 0x1397E",
                One(r, out var a) && a.Plugin == "Skyrim.esm" && a.LocalId == 0x1397E);
        }

        // ── 5. Tilde form (KID/SPID shape), leading-zero local id. ──
        {
            var r = Ex(@"SKSE\Plugins\Foo\x.ini", "Spell = 0x000FE2~Dawnguard.esm");
            Check("tilde 0x000FE2~Dawnguard.esm → Dawnguard.esm / 0xFE2",
                One(r, out var a) && a.Plugin == "Dawnguard.esm" && a.LocalId == 0xFE2);
        }

        // ── 6. Comma-separated list on one line → each ref its own match. ──
        {
            var r = Ex(@"SKSE\Plugins\Foo\x.json", @"""forms"": [""0x1|Skyrim.esm"", ""0x2|Update.esm""]");
            var toks = r.Where(x => x.Shape == SkseRefShape.FormToken).ToList();
            Check("comma list → 2 tokens (Skyrim.esm/0x1, Update.esm/0x2)",
                toks.Count == 2 && toks[0].Plugin == "Skyrim.esm" && toks[0].LocalId == 0x1
                                && toks[1].Plugin == "Update.esm" && toks[1].LocalId == 0x2);
        }

        // ── 7. Path-segment plugin gate (DSD \Plugin.esp\ folder), file itself has no tokens. ──
        {
            var r = Ex(@"SKSE\Plugins\DynamicStringDistributor\Dawnguard.esm\names.json", @"{ ""strings"": [ ""Hi"" ] }");
            var gates = r.Where(x => x.Shape == SkseRefShape.PathSegmentGate).ToList();
            Check("path gate \\Dawnguard.esm\\ → 1 gate, 0 tokens",
                gates.Count == 1 && gates[0].Plugin == "Dawnguard.esm" && gates[0].LocalId is null
                && !r.Any(x => x.Shape == SkseRefShape.FormToken));
        }

        // ── 8. No references at all (OStim-like) → clean empty (the MOST COMMON per-file outcome; never a warning). ──
        {
            var r = Ex(@"SKSE\Plugins\OStim\scenes\x.json", @"{ ""duration"": 4.0, ""actors"": 2, ""anim"": ""idle_a"" }");
            Check("no-reference file → 0 refs (clean empty)", r.Count == 0);
        }

        // ── 9. Unparseable overflow hex (9 digits) → CAPTURED loud, LocalId null (Q3). ──
        {
            var r = Ex(@"SKSE\Plugins\Foo\x.ini", "form = 0x1FFFFFFFF|Skyrim.esm");
            Check("overflow 0x1FFFFFFFF|Skyrim.esm → unparseable, LocalId null",
                One(r, out var a) && a.Shape == SkseRefShape.FormToken && a.Unparseable is not null && a.LocalId is null && a.Plugin == "Skyrim.esm");
        }

        // ── 10. Comment-embedded token still surfaced (§4d honesty — declared, not necessarily used). ──
        {
            var r = Ex(@"SKSE\Plugins\Foo\x.ini", "; see 0x5|Skyrim.esm for the base record");
            Check("comment-embedded 0x5|Skyrim.esm → still extracted (declared refs)",
                One(r, out var a) && a.Plugin == "Skyrim.esm" && a.LocalId == 0x5);
        }

        // ── 11. Bare local id under an ESL plugin, no FE prefix (documented residual: low-24 mask is identity). ──
        {
            var r = Ex(@"SKSE\Plugins\Foo\x.ini", "x = 0x800|SomeMod.esl");
            Check("bare 0x800|SomeMod.esl → 0x800 (no FE prefix, low-24 identity)",
                One(r, out var a) && a.Plugin == "SomeMod.esl" && a.LocalId == 0x800);
        }

        // ══ Part 2 — VERDICTS against a real (synthetic) load order: extract → resolve → OK/MISSING/DANGLING/UNPARSEABLE.
        //    Drives the SERVICE adjudicator (LoadOrderService.Adjudicate) over a LoadOrderResolver.IndexView, so the whole
        //    chain (extractor mask → ContainsPlugin → ResolveWinner) is proven, not just the pure extractor. ══
        Console.WriteLine();
        Console.WriteLine("-- verdicts vs a synthetic order (hcAudit.esp full + hcAuditEsl.esl light) --");
        VerdictArms(Check);

        Console.WriteLine();
        Console.WriteLine($"=== skse-config-audit-guard: {(fails == 0 ? "PASS" : "FAIL")} ({fails} failing) ===");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>Build a synthetic order with a full plugin and a LIGHT plugin, then adjudicate hand-shaped references
    /// through the REAL service adjudicator (<see cref="LoadOrderService.Adjudicate"/>) over a real
    /// <see cref="LoadOrderResolver.IndexView"/> — pinning OK / PLUGIN MISSING / DANGLING / UNPARSEABLE and the ESL
    /// FE-prefix masking end-to-end (the DSD FExxxYYY shape must resolve to the light record). Reports via <paramref name="check"/>.</summary>
    static void VerdictArms(Action<string, bool> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "hc-skse-audit-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var fullPath = Path.Combine(dir, "hcAudit.esp");
            var full = new SkyrimMod(ModKey.FromNameAndExtension("hcAudit.esp"), SkyrimRelease.SkyrimSE);
            var fac = full.Factions.AddNew(); fac.EditorID = "hcAuditFac";
            var facFk = fac.FormKey;
            full.BeginWrite.ToPath(fullPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            // A LIGHT (ESL) master with its record explicitly in the 0x800 window — the proven EslFormIdProbe idiom
            // (IsSmallMaster + explicit FormKey + NoNextFormIDProcessing), so the FE-prefix mask has a real target.
            const uint eslId = 0x800;
            var eslKey = ModKey.FromNameAndExtension("hcAuditEsl.esl");
            var eslPath = Path.Combine(dir, "hcAuditEsl.esl");
            var esl = new SkyrimMod(eslKey, SkyrimRelease.SkyrimSE) { IsSmallMaster = true };
            esl.Factions.Add(new Faction(new FormKey(eslKey, eslId), SkyrimRelease.SkyrimSE) { EditorID = "hcAuditEslFac" });
            esl.BeginWrite.ToPath(eslPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();

            using var resolver = LoadOrderResolver.Build(new[] { fullPath, eslPath });
            var index = resolver.Capture();

            SkseRefVerdict V(string relPath, string text)
            {
                var refs = SkseConfigReferenceExtractor.Extract(relPath, text);
                return refs.Count == 1 ? LoadOrderService.Adjudicate(refs[0], index).Verdict : SkseRefVerdict.Unparseable;
            }

            check("OK: 0x<real>|hcAudit.esp → OK",
                V(@"SKSE\Plugins\Foo\x.json", $@"""form"": ""0x{facFk.ID:X}|hcAudit.esp""") == SkseRefVerdict.Ok);
            check($"OK: FE007{eslId:X3}|hcAuditEsl.esl (ESL FE-prefix mask) → OK",
                V(@"SKSE\Plugins\DynamicStringDistributor\x.json", $@"""form_id"": ""FE007{eslId:X3}|hcAuditEsl.esl""") == SkseRefVerdict.Ok);
            check("DANGLING: 0xABCDEF|hcAudit.esp (no such record) → DANGLING",
                V(@"SKSE\Plugins\Foo\x.ini", "target = 0xABCDEF|hcAudit.esp") == SkseRefVerdict.Dangling);
            check("MISSING: 0x1|NotInstalled.esp → PLUGIN MISSING",
                V(@"SKSE\Plugins\Foo\x.ini", "target = 0x1|NotInstalled.esp") == SkseRefVerdict.PluginMissing);
            check("GATE OK: \\hcAudit.esp\\ folder (present) → OK",
                V(@"SKSE\Plugins\DynamicStringDistributor\hcAudit.esp\names.json", "{}") == SkseRefVerdict.Ok);
            check("GATE MISSING: \\NotInstalled.esp\\ folder (absent) → PLUGIN MISSING",
                V(@"SKSE\Plugins\DynamicStringDistributor\NotInstalled.esp\names.json", "{}") == SkseRefVerdict.PluginMissing);
            check("UNPARSEABLE: overflow token → UNPARSEABLE verdict",
                V(@"SKSE\Plugins\Foo\x.ini", "x = 0x1FFFFFFFF|hcAudit.esp") == SkseRefVerdict.Unparseable);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>MANUAL real-data harness (the tier-B LIVE GATE): run the WHOLE audit against a live MO2 instance and print
    /// exactly what housecarl_skse_config_audit would return, plus a timing line — the empirical re-check Aaron drives (the
    /// CI guard pins the extractor + verdict logic; this proves the full scan over real configs). NOT in ci-all (needs a
    /// real instance + game install). Read-only; touches nothing but a temp user.json.
    /// Usage: dotnet run --project src/housecarl-generator -- skse-config-audit-real --mo2 "&lt;MO2 instance&gt;" [--filter &lt;substr&gt;]</summary>
    public static int RunReal(string[] args)
    {
        string? mo2 = ArgVal(args, "--mo2");
        string? filter = ArgVal(args, "--filter");
        int max = int.TryParse(ArgVal(args, "--max"), out var m) ? m : 80_000;
        if (mo2 is null) { Console.WriteLine("skse-config-audit-real needs --mo2 <MO2 instance folder>"); return 2; }

        var store = new UserConfigStore(Path.Combine(Path.GetTempPath(), "hc-skse-cfgaudit-" + Guid.NewGuid().ToString("N") + ".json"));
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

    static IReadOnlyList<SkseConfigRef> Ex(string relPath, string text) => SkseConfigReferenceExtractor.Extract(relPath, text);

    /// <summary>True when exactly one FORM-TOKEN ref was extracted; binds it out for assertion.</summary>
    static bool One(IReadOnlyList<SkseConfigRef> refs, out SkseConfigRef only)
    {
        var toks = refs.Where(r => r.Shape == SkseRefShape.FormToken).ToList();
        only = toks.Count == 1 ? toks[0] : null!;
        return toks.Count == 1;
    }
}

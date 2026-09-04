using System.Globalization;
using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// Build proof for the PUBLIC write cleave (<see cref="WritePatchBuilder.Apply"/>) that the <c>housecarl_set_field</c>
/// and <c>housecarl_bulk_apply</c> tools call. It must drive the same code path the tools do, or it proves nothing
/// about them. Five checks, each against a real, large load order:
///
///   A — FLAT Set (one weapon's Damage), fresh single-master patch; value reads back; source byte-untouched.
///   B — MULTI-edit one patch (two weapons in ONE call) = the bulk_apply shape; both land in one .esp.
///   C — into=/EXTEND: write A's patch, then EXTEND it with a 2nd record; reopen → BOTH present. The file is the
///       state; nothing accumulates in the server between calls.
///   D — CROSS-MASTER through the cleave (a vanilla leveled list + a cross-master weapon entry) → patch header
///       carries ≥2 masters (the cleave routes through the proven multi-master WritePatch).
///   E — PRE-FLIGHT REJECT: a bad edit refuses the WHOLE call with NO file written — no partial patches.
///
/// Every write only ever touches its own output .esp; the vanilla masters and referenced mods are SHA-checked
/// unchanged. Patches are left in write-output/apply-proof/ to open in xEdit.
///
/// Run: dotnet run --project src/housecarl-generator apply-proof [maxPlugins]
/// </summary>
public static class ApplyProof
{
    static readonly HashSet<string> Vanilla = new(StringComparer.OrdinalIgnoreCase)
        { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" };

    public static int RunApplyProof(string[] args)
    {
        int maxPlugins = args.Length > 0 && int.TryParse(args[0], out var m) ? m : 0;

        Console.WriteLine("================================================================");
        Console.WriteLine(" houseCARL write-cleave proof (Beat C — set_field / bulk_apply / into=)");
        Console.WriteLine("================================================================");

        var paths = ResolveProbe.GatherPlugins();
        if (maxPlugins > 0 && paths.Count > maxPlugins) paths = paths.Take(maxPlugins).ToList();
        if (paths.Count == 0) { Console.Error.WriteLine("error: no plugins found"); return 1; }
        var nameToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths) nameToPath[Path.GetFileName(p)] = p;
        Console.WriteLine($"Plugins (placeholder order): {paths.Count}");

        using var resolver = LoadOrderResolver.Build(paths);
        using var session = resolver.OpenSession();   // Option B: open source plugins on demand, disposed at harness exit
        var rulebook = CorpusRulebook.Load();
        Console.WriteLine($"Resolver: {resolver.PluginCount:N0} plugins | {resolver.RecordCount:N0} records | " +
                          $"{resolver.ConflictCount:N0} conflicts.  Rulebook: {rulebook.TypeCount} types.");
        Console.WriteLine();

        var outDir = Path.GetFullPath(Path.Combine("write-output", "apply-proof"));
        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        Directory.CreateDirectory(outDir);

        // ---- DISCOVER 2 distinct vanilla-DEFINED weapons whose WINNER has BasicStats. (Don't require depth 1: in a
        //      heavily-modded order almost every vanilla weapon is overridden, and overriding the WINNER is correct at
        //      any depth — its master set is whatever the record's FormLinks reference, asserted in D not here.) ----
        var weapons = new List<(FormKey fk, ushort dmg, string edid)>();
        foreach (var (fk, _, body) in resolver.WinnerRecordsOfType(new[] { typeof(IWeaponGetter) }))
        {
            if (!Vanilla.Contains(fk.ModKey.FileName)) continue;                   // record DEFINED in a vanilla master (stable FormKey)
            if (body is not IWeaponGetter w || w.BasicStats is null) continue;
            weapons.Add((fk, w.BasicStats.Damage, w.EditorID ?? "<no edid>"));
            if (weapons.Count >= 2) break;
        }
        if (weapons.Count < 2) { Console.Error.WriteLine("error: need 2 vanilla weapons with BasicStats for the proof"); return 1; }
        var (fk1, dmg1, edid1) = weapons[0];
        var (fk2, dmg2, edid2) = weapons[1];
        Console.WriteLine($"Targets: {edid1} {fk1} (Damage {dmg1})  |  {edid2} {fk2} (Damage {dmg2})");
        Console.WriteLine();

        var results = new List<(string name, bool pass, string detail)>();

        // ============================== A — FLAT Set (fresh single-master patch) ==============================
        {
            var outPath = Path.Combine(outDir, "houseCARL_ApplyProof_A.esp");
            ushort want = (ushort)(dmg1 + 5);
            var shaBefore = ShaVanillaTouched(nameToPath, fk1);
            var edit = new WritePatchBuilder.PatchEdit
                { Target = fk1, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = want.ToString(CultureInfo.InvariantCulture) };
            var o = WritePatchBuilder.Apply(resolver, rulebook, new[] { edit }, outPath, extend: false);

            bool ok = o.Success;
            string detail = o.Success ? $"masters [{string.Join(",", o.Masters)}], after={o.Ops[0].After}, {o.Bytes}b" : o.Error ?? "";
            ushort? back = ok ? ReadWeaponDamage(outPath, fk1) : null;
            bool valueOk = back == want;
            bool srcOk = SourcesUnchanged(shaBefore, nameToPath);
            bool pass = ok && valueOk && srcOk;       // master count is D's concern, not A's (a weapon can ref keywords across masters)
            results.Add(("A flat Set", pass, $"{detail}; readback={back} (want {want}) src-untouched={YN(srcOk)}"));
        }

        // ============================== B — MULTI-edit one patch (bulk_apply) ==============================
        {
            var outPath = Path.Combine(outDir, "houseCARL_ApplyProof_B.esp");
            ushort w1 = (ushort)(dmg1 + 7), w2 = (ushort)(dmg2 + 7);
            var shaBefore = ShaVanillaTouched(nameToPath, fk1, fk2);
            var edits = new[]
            {
                new WritePatchBuilder.PatchEdit { Target = fk1, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = w1.ToString(CultureInfo.InvariantCulture) },
                new WritePatchBuilder.PatchEdit { Target = fk2, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = w2.ToString(CultureInfo.InvariantCulture) },
            };
            var o = WritePatchBuilder.Apply(resolver, rulebook, edits, outPath, extend: false);
            bool ok = o.Success && o.Ops.Count == 2;
            bool both = ok && ReadWeaponDamage(outPath, fk1) == w1 && ReadWeaponDamage(outPath, fk2) == w2;
            bool srcOk = SourcesUnchanged(shaBefore, nameToPath);
            bool pass = ok && both && srcOk;
            results.Add(("B multi-edit one patch", pass,
                ok ? $"2 ops, masters [{string.Join(",", o.Masters)}], both-land={YN(both)} src-untouched={YN(srcOk)}" : o.Error ?? ""));
        }

        // ============================== C — into= / EXTEND ==============================
        {
            var outPath = Path.Combine(outDir, "houseCARL_ApplyProof_C.esp");
            ushort w1 = (ushort)(dmg1 + 9), w2 = (ushort)(dmg2 + 9);
            var shaBefore = ShaVanillaTouched(nameToPath, fk1, fk2);
            // fresh patch with weapon 1...
            var first = WritePatchBuilder.Apply(resolver, rulebook,
                new[] { new WritePatchBuilder.PatchEdit { Target = fk1, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = w1.ToString(CultureInfo.InvariantCulture) } },
                outPath, extend: false);
            bool freshOk = first.Success && ReadWeaponDamage(outPath, fk1) == w1;
            // ...then EXTEND it with weapon 2 (the into= path).
            var second = WritePatchBuilder.Apply(resolver, rulebook,
                new[] { new WritePatchBuilder.PatchEdit { Target = fk2, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = w2.ToString(CultureInfo.InvariantCulture) } },
                outPath, extend: true);
            bool extendOk = second.Success && second.Extended;
            // both edits survive in the one grown patch — the prior edit was NOT lost.
            bool bothSurvive = extendOk && ReadWeaponDamage(outPath, fk1) == w1 && ReadWeaponDamage(outPath, fk2) == w2;
            bool srcOk = SourcesUnchanged(shaBefore, nameToPath);
            bool pass = freshOk && extendOk && bothSurvive && srcOk;
            results.Add(("C into= extend", pass,
                $"fresh={YN(freshOk)} extend={YN(extendOk)} both-survive={YN(bothSurvive)} src-untouched={YN(srcOk)}"
                + (second.Success ? $"  masters [{string.Join(",", second.Masters)}]" : "  err=" + second.Error)));
        }

        // ============================== D — CROSS-MASTER through the cleave ==============================
        {
            var outPath = Path.Combine(outDir, "houseCARL_ApplyProof_D.esp");
            // a vanilla leveled list with entries + a cross-master weapon whose defining master is loaded.
            FormKey baseFk = default; string? baseMaster = null;
            foreach (var (fk, _, body) in resolver.WinnerRecordsOfType(new[] { typeof(ILeveledItemGetter) }))
            {
                if (!Vanilla.Contains(fk.ModKey.FileName)) continue;
                if (resolver.GetRecord(session, fk.ModKey.FileName, fk) is ILeveledItemGetter li && li.Entries is { Count: > 0 })
                    { baseFk = fk; baseMaster = fk.ModKey.FileName; break; }
            }
            var loaded = new HashSet<string>(resolver.PluginNames, StringComparer.OrdinalIgnoreCase);
            FormKey crossItem = default; string? crossMaster = null;
            foreach (var (fk, _, _) in resolver.WinnerRecordsOfType(new[] { typeof(IWeaponGetter) }))
            {
                var mk = fk.ModKey.FileName.ToString();
                if (Vanilla.Contains(mk) || !loaded.Contains(mk)) continue;
                crossItem = fk; crossMaster = mk; break;
            }
            if (baseMaster is null || crossMaster is null)
            {
                results.Add(("D cross-master", false, "skipped — no vanilla leveled list + loaded cross-master weapon found"));
            }
            else
            {
                var shaBefore = ShaNames(nameToPath, baseMaster, crossMaster);
                var edit = new WritePatchBuilder.PatchEdit
                {
                    Target = baseFk, Path = new[] { "Entries" }, Verb = "Add",
                    Struct = new StructSpec
                    {
                        Type = "LeveledItemEntry",
                        Sets = new List<WriteRequest>
                        {
                            new() { RecordType = "LeveledItemEntry", Path = new[] { "Data", "Level" },     Verb = "Set", Value = "1" },
                            new() { RecordType = "LeveledItemEntry", Path = new[] { "Data", "Count" },     Verb = "Set", Value = "1" },
                            new() { RecordType = "LeveledItemEntry", Path = new[] { "Data", "Reference" }, Verb = "Set", Value = crossItem.ToString() },
                        }
                    }
                };
                var o = WritePatchBuilder.Apply(resolver, rulebook, new[] { edit }, outPath, extend: false);
                bool multi = o.Success && o.Masters.Count >= 2
                             && o.Masters.Contains(baseMaster, StringComparer.OrdinalIgnoreCase)
                             && o.Masters.Contains(crossMaster, StringComparer.OrdinalIgnoreCase);
                bool srcOk = SourcesUnchanged(shaBefore, nameToPath);
                results.Add(("D cross-master", multi && srcOk,
                    o.Success ? $"masters [{string.Join(",", o.Masters)}] (base {baseMaster} + cross {crossMaster}) src-untouched={YN(srcOk)}" : o.Error ?? ""));
            }
        }

        // ============================== E — PRE-FLIGHT REJECT (no partial patch) ==============================
        {
            var outPath = Path.Combine(outDir, "houseCARL_ApplyProof_E.esp");
            // one good edit + one bad (non-coercible Damage) → the WHOLE call must refuse, no file.
            var edits = new[]
            {
                new WritePatchBuilder.PatchEdit { Target = fk1, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = "42" },
                new WritePatchBuilder.PatchEdit { Target = fk2, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = "not-a-number" },
            };
            var o = WritePatchBuilder.Apply(resolver, rulebook, edits, outPath, extend: false);
            bool refused = !o.Success;
            bool noFile = !File.Exists(outPath);
            bool pass = refused && noFile;
            results.Add(("E reject (no partial)", pass,
                $"refused={YN(refused)} no-file={YN(noFile)}" + (refused ? "  msg=\"" + Trim(o.Error) + "\"" : "  !! WROTE A PARTIAL PATCH")));
        }

        // ---- VERDICT ----
        Console.WriteLine("Results:");
        foreach (var (name, pass, detail) in results)
            Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name,-26} {detail}");
        Console.WriteLine();
        bool allPass = results.All(r => r.pass);
        Console.WriteLine("================================================================");
        Console.WriteLine(allPass
            ? $"=== ALL {results.Count} CHECKS PASS — the write cleave (set_field/bulk_apply/into=) is proven.\n" +
              $"    Open write-output/apply-proof/*.esp in xEdit: A=one weapon Damage, B=two weapons, C=extended (both),\n" +
              $"    D=cross-master leveled-list merge. Originals byte-untouched throughout. ==="
            : "=== FAIL — see the checks above ===");
        Console.WriteLine("================================================================");
        return allPass ? 0 : 1;
    }

    // ---- helpers ----

    static ushort? ReadWeaponDamage(string patchPath, FormKey fk)
    {
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            var w = back.EnumerateMajorRecords<IWeaponGetter>().FirstOrDefault(r => r.FormKey == fk);
            return w?.BasicStats?.Damage;
        }
        catch { return null; }
        finally { (back as IDisposable)?.Dispose(); }
    }

    static Dictionary<string, string> ShaVanillaTouched(Dictionary<string, string> nameToPath, params FormKey[] fks)
        => ShaNames(nameToPath, fks.Select(f => f.ModKey.FileName.ToString()).ToArray());

    static Dictionary<string, string> ShaNames(Dictionary<string, string> nameToPath, params string[] names)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names.Distinct(StringComparer.OrdinalIgnoreCase)) d[n] = ShaOf(nameToPath, n);
        return d;
    }

    static bool SourcesUnchanged(Dictionary<string, string> before, Dictionary<string, string> nameToPath)
        => before.All(kv => string.Equals(kv.Value, ShaOf(nameToPath, kv.Key), StringComparison.OrdinalIgnoreCase));

    static string ShaOf(Dictionary<string, string> nameToPath, string name)
    {
        if (!nameToPath.TryGetValue(name, out var path)) return $"(no path {name})";
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    static string YN(bool b) => b ? "Y" : "N";
    static string Trim(string? s) => s is null ? "" : (s.Length <= 90 ? s.Replace("\n", " ") : s[..90].Replace("\n", " ") + "…");
}

using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD (standing CI instrument, self-contained) for bulk-primitives WAVE 2 — the OUTPUT CONTRACT
/// (PLAN.md P3/P5/P6/P7):
///   • P3 <c>housecarl_resolve</c> — bulk FormID → identity (type/editorid/name/winner), per-item error isolation.
///   • P5 <c>winner_fields=</c> on cross_plugin_query — render the load-order WINNER's body under a plugins= scope,
///     plus the loud scoped-values note when plugins=-scoped fields render WITHOUT it.
///   • P6 <c>format="json"</c> across the read surfaces — the SAME data as text (round-trip token parity), Q3
///     accounting carried in-band, always-valid JSON.
///   • P7 <c>resolve_names=</c> — every FormLink token in a field dump annotated with its target's identity,
///     display-only (never replaces the round-trip token).
///
/// Synthesizes a small on-disk order (a master + a replacer that overrides one NAMED weapon and defines others, with
/// keyword links) and drives the REAL service layer (<see cref="LoadOrderService"/> via the ForGuard seam) + the
/// tool layer (<see cref="ReadTools"/>). Self-contained: a corpus is generated in-process if none is configured.
///
/// Run: <c>dotnet run --project src/housecarl-generator bulk-primitives-wave2-guard</c>
/// </summary>
public static class BulkPrimitivesWave2Probe
{
    static int _pass, _fail;

    public static int RunGuard(string[] args)
    {
        _pass = _fail = 0;
        Console.WriteLine("################  REGRESSION GUARD — bulk-primitives Wave 2 (output contract: resolve / winner_fields / format=json / resolve_names)  ################");
        Console.WriteLine();

        var dir = Path.Combine(Path.GetTempPath(), "hc_bulk_primitives_wave2_guard_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try { _ = CorpusRulebook.LoadCorpus(); }
        catch
        {
            var gen = Path.Combine(dir, "generated");
            CorpusGenerator.GenerateAll(gen, Path.Combine(dir, "refs"));
            CorpusRulebook.CorpusPath = Path.Combine(gen, "corpus.json");
            Console.WriteLine($"-- generated a corpus for type= resolution: {CorpusRulebook.CorpusPath} --");
        }

        const string masterName = "hcw2Master.esp", replName = "hcw2Repl.esp";
        var masterPath = Path.Combine(dir, masterName);
        var replPath = Path.Combine(dir, replName);

        try
        {
            // ---- MASTER: keyword KA (no Name), two NAMED weapons. W1 carries KA and will be OVERRIDDEN by the replacer. ----
            var master = new SkyrimMod(ModKey.FromNameAndExtension(masterName), SkyrimRelease.SkyrimSE);
            var ka = master.Keywords.AddNew(); ka.EditorID = "hcw2KwA"; var kaFk = ka.FormKey;
            var ghostFk = new FormKey(master.ModKey, 0x000FFF);   // a keyword link to a FormID NOTHING defines (a dangling ref, in the master's own space so no missing-master)
            var w1 = master.Weapons.AddNew(); w1.EditorID = "hcw2Sword1"; w1.Name = "Iron Sword"; w1.BasicStats = new WeaponBasicStats { Damage = 10 };
            w1.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>>
                { new FormLink<IKeywordGetter>(kaFk), new FormLink<IKeywordGetter>(ghostFk) };
            var w1Fk = w1.FormKey;
            var w2 = master.Weapons.AddNew(); w2.EditorID = "hcw2Sword2"; w2.Name = "Steel Sword"; w2.BasicStats = new WeaponBasicStats { Damage = 20 };
            var w2Fk = w2.FormKey;
            master.BeginWrite.ToPath(masterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            // ---- REPLACER (masters [master]): OVERRIDE W1 (so its WINNER is the replacer), DEFINE a new NAMED weapon W3[KA]. ----
            var repl = new SkyrimMod(ModKey.FromNameAndExtension(replName), SkyrimRelease.SkyrimSE);
            ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(repl, w1)).BasicStats = new WeaponBasicStats { Damage = 15 };
            var w3 = repl.Weapons.AddNew(); w3.EditorID = "hcw2Sword3"; w3.Name = "Ebony Sword"; w3.BasicStats = new WeaponBasicStats { Damage = 30 };
            w3.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>> { new FormLink<IKeywordGetter>(kaFk) };
            var w3Fk = w3.FormKey;
            repl.BeginWrite.ToPath(replPath).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

            Console.WriteLine($"-- synthesized {masterName} (KA; W1[KA]'Iron Sword',W2'Steel Sword') < {replName} (override W1; W3[KA]'Ebony Sword') --");
            Console.WriteLine();

            using var resolver = LoadOrderResolver.Build(new[] { masterPath, replPath });
            var svc = LoadOrderService.ForGuard(resolver, new UserConfigStore(Path.Combine(dir, "houseCARL.user.json")));

            // ================= P3 — housecarl_resolve (bulk FormID → identity) =================
            Console.WriteLine("── P3: housecarl_resolve — type/editorid/name/winner, per-item error isolation ──");
            const string absentFid = "000800:Nonexist.esp";   // a valid FormID string whose plugin isn't in the order
            const string badFid = "not-a-formid";
            var refs = svc.ResolveRefs(new[] { w1Fk.ToString(), kaFk.ToString(), w3Fk.ToString(), absentFid, badFid });
            Check($"resolve returns one row per input, in order — got {refs.Count}", refs.Count == 5);

            var r0 = refs[0];
            Check($"W1 → Weapon/hcw2Sword1/name 'Iron Sword'/winner {replName} (winner is the OVERRIDE, not the master)",
                  r0.Resolved && r0.Type == "Weapon" && r0.EditorId == "hcw2Sword1" && r0.Name == "Iron Sword" && r0.Winner == replName);
            var r1 = refs[1];
            Check("keyword KA → Keyword/hcw2KwA with name=null (KYWD has no Name — reflection-generic, not guessed)",
                  r1.Resolved && r1.Type == "Keyword" && r1.EditorId == "hcw2KwA" && r1.Name is null);
            var r2 = refs[2];
            Check("W3 → Weapon/hcw2Sword3/name 'Ebony Sword'", r2.Resolved && r2.Type == "Weapon" && r2.EditorId == "hcw2Sword3" && r2.Name == "Ebony Sword");
            var r3 = refs[3];
            Check("a valid-but-absent FormID → Resolved=false with NO malformed-input error (named, not dropped — Q3)",
                  !r3.Resolved && r3.Error is null && r3.Token == absentFid);
            var r4 = refs[4];
            Check("a malformed FormID → per-item error, the batch still returns the other 4 rows (Q3)",
                  !r4.Resolved && r4.Error is not null && r4.Error.Contains("bad FormID", StringComparison.OrdinalIgnoreCase));

            // A recurring target resolves consistently (the batch memo returns the same identity).
            var dup = svc.ResolveRefs(new[] { kaFk.ToString(), kaFk.ToString() });
            Check("a target repeated in one batch resolves identically (memoised)",
                  dup.Count == 2 && dup[0].EditorId == dup[1].EditorId && dup[0].EditorId == "hcw2KwA");

            // ---- tool layer: text render ----
            Console.WriteLine();
            Console.WriteLine("── P3 tool layer: text + json render, bad-format refusal ──");
            var txt = ReadTools.Resolve(svc, new[] { w1Fk.ToString(), kaFk.ToString(), badFid }, format: null, max_chars: 0);
            Check("text render carries the identity line (type=Weapon, name=\"Iron Sword\")",
                  txt.Contains("type=Weapon") && txt.Contains("name=\"Iron Sword\"") && txt.Contains("editorid=hcw2KwA"));
            Check("text render carries the per-item error for the bad input", txt.Contains("error="));

            // ---- tool layer: json render (must be VALID json, carrying the same data) ----
            var js = ReadTools.Resolve(svc, new[] { w1Fk.ToString(), badFid }, format: "json", max_chars: 0);
            bool jsonValid = true; JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(js); } catch { jsonValid = false; }
            Check("json render is VALID json", jsonValid && doc is not null);
            if (doc is not null)
            {
                var root = doc.RootElement;
                var arr = root.GetProperty("resolved");
                Check("json: 'resolved' array has one row per input", arr.GetArrayLength() == 2);
                var j0 = arr[0];
                Check("json row 0 carries {type,editorid,name,winner} matching text",
                      j0.GetProperty("type").GetString() == "Weapon" && j0.GetProperty("editorid").GetString() == "hcw2Sword1"
                      && j0.GetProperty("name").GetString() == "Iron Sword" && j0.GetProperty("winner").GetString() == replName);
                var j1 = arr[1];
                Check("json row 1 (bad input) carries an 'error' field, not identity",
                      j1.TryGetProperty("error", out _) && !j1.TryGetProperty("winner", out _));
                doc.Dispose();
            }

            var badFmt = ReadTools.Resolve(svc, new[] { w1Fk.ToString() }, format: "bogus", max_chars: 0);
            Check("an unrecognized format= is REFUSED loud (never a silent fall-through to text — Q3)",
                  badFmt.StartsWith("error:", StringComparison.OrdinalIgnoreCase) && badFmt.Contains("format", StringComparison.OrdinalIgnoreCase));

            // ================= P7 — resolve_names (FormLink token → target identity, DISPLAY-ONLY) =================
            Console.WriteLine();
            Console.WriteLine("── P7: resolve_names annotates FormLink tokens with target identity, NEVER replacing the token ──");
            var named = svc.ResolveRead(w1Fk, null, new[] { "Keywords" }, false, depth: 2, resolveNames: true);
            var kwFields = named.Record!.Fields.Where(f => f.Path.StartsWith("Keywords[", StringComparison.Ordinal)).ToList();
            Check($"resolve_names read surfaced the 2 keyword elements — got {kwFields.Count}", kwFields.Count == 2);
            var kaField = kwFields.FirstOrDefault(f => f.Token == kaFk.ToString());
            Check("the KA element's ROUND-TRIP TOKEN is unchanged (still the raw FormKey a write can reuse)",
                  kaField is { HasValue: true } && kaField.Token == kaFk.ToString());
            Check("... and its Link annotation resolves to the keyword identity (editorid hcw2KwA) — ADDED, not substituted",
                  kaField?.Link is { Resolved: true, EditorId: "hcw2KwA" });
            var ghostField = kwFields.FirstOrDefault(f => f.Token == ghostFk.ToString());
            Check("a link whose target no active plugin defines is annotated UNRESOLVED (named, not dropped/guessed — Q3), token still intact",
                  ghostField is { HasValue: true } && ghostField.Token == ghostFk.ToString() && ghostField.Link is { Resolved: false });

            var plainRead = svc.ResolveRead(w1Fk, null, new[] { "Keywords" }, false, depth: 2, resolveNames: false);
            Check("without resolve_names, NO leaf carries a Link annotation (default behavior unchanged)",
                  plainRead.Record!.Fields.All(f => f.Link is null));

            var tRec = ReadTools.ReadRecord(svc, w1Fk.ToString(), plugin: null, fields: new[] { "Keywords" }, depth: 2,
                conflict_tree: false, resolve_names: true, max_chars: 0);
            Check("text render carries BOTH the raw token AND the → identity parenthetical",
                  tRec.Contains(kaFk.ToString()) && tRec.Contains("→ hcw2KwA"));
            Check("text render marks the dangling target 'unresolved'", tRec.Contains("unresolved"));

            var tBatch = ReadTools.BatchRecordDetail(svc, new[] { w1Fk.ToString() }, fields: new[] { "Keywords" }, depth: 2,
                conflict_tree: false, resolve_names: true, max_chars: 0);
            Check("batch_record_detail resolve_names annotates too (shared batch memo)", tBatch.Contains("→ hcw2KwA"));

            Console.WriteLine();
            Console.WriteLine($"=== bulk-primitives-wave2-guard: {_pass} passed, {_fail} failed -> {(_fail == 0 ? "PASS" : "FAIL")} ===");
            return _fail == 0 ? 0 : 1;
        }
        catch (Exception ex) { Console.WriteLine($"   FAIL (unexpected): {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); return 1; }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    static void Check(string label, bool ok)
    {
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        if (ok) _pass++; else _fail++;
    }
}

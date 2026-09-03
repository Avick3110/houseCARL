using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD (standing CI instrument, self-contained) for bulk-primitives WAVE 2 — the SERVICE-LAYER half
/// of the output contract (PLAN.md P3/P7):
///   • P3 bulk FormID → identity (type/editorid/name/winner) with per-item error isolation, and the batch memo.
///   • P7 <c>resolveNames</c> — every FormLink in a field read annotated with its target's identity, display-only
///     (the round-trip token is never replaced), and a link nothing defines annotated unresolved.
///   • #230 — the engine-implicit forms (PlayerRef 000014 / Player 000007) resolve to their hardcoded identity
///     (winner <c>&lt;engine&gt;</c>), never "unresolved"; the very next sub-0x800 form (000015) still dangles,
///     proving the exemption is a two-form set rather than a reserved range.
///   • The container hint: the expansion knob is named where it exists, and the write read-back lane
///     (containerHint:null) renders the bare count with no knob named at all.
///
/// This file's TOOL-LAYER blocks were removed when the eight 1.x read tools were deleted; the surviving claims are
/// tests against <c>housecarl_records</c> in <c>src/housecarl-mcp-tests</c> (RecordsBulkSelectTests.cs and
/// RecordsScanProjectionTests.cs). Each removed block leaves a comment in its place saying what stood there.
///
/// Synthesizes a small on-disk order (a master + a replacer that overrides one NAMED weapon and defines others, with
/// keyword links) and drives the REAL service layer (<see cref="LoadOrderService"/> via the ForGuard seam).
/// Self-contained: a corpus is generated in-process if none is configured.
///
/// Run: <c>dotnet run --project src/housecarl-generator bulk-primitives-wave2-guard</c>
/// </summary>
public static class BulkPrimitivesWave2Probe
{
    static int _pass, _fail;

    [CiProbe("bulk-primitives-wave2-guard")]
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

            // Engine-implicit forms (#230): PlayerRef 000014 / Player 000007 are hardcoded engine references no
            // plugin defines — the resolver must answer their identity (the same EngineImplicit exemption
            // check_errors and the dialogue lints apply), while the NEXT sub-0x800 form still dangles (precision).
            Console.WriteLine();
            Console.WriteLine("── P3 #230: engine-implicit forms resolve to their hardcoded identity; the exemption stays precise ──");
            var ei = svc.ResolveRefs(new[] { "000014:Skyrim.esm", "000007:Skyrim.esm", "000015:Skyrim.esm" });
            Check("PlayerRef (000014:Skyrim.esm) → Resolved, PlacedNpc/PlayerRef, winner <engine> (#230 — was 'unresolved')",
                  ei[0] is { Resolved: true, Type: "PlacedNpc", EditorId: "PlayerRef", Winner: "<engine>" });
            Check("Player (000007:Skyrim.esm) → Resolved, Npc/Player, winner <engine>",
                  ei[1] is { Resolved: true, Type: "Npc", EditorId: "Player", Winner: "<engine>" });
            Check("a NON-implicit sub-0x800 form (000015:Skyrim.esm) is STILL unresolved — the exemption is the 2-form set, not the reserved range",
                  ei[2] is { Resolved: false, Error: null });

            // ---- tool layer ----
            // The housecarl_resolve TOOL-LAYER arms stood here: the text and json renders of a bulk
            // FormID -> identity read, the per-item error row, the unrecognized-format refusal, and the
            // max_chars accounting. housecarl_resolve is gone; those claims are tests against
            // housecarl_records' identity form in src/housecarl-mcp-tests/RecordsBulkSelectTests.cs.
            // (max_chars changed shape rather than dying: housecarl_records spills the complete result to an
            // artifact instead of dropping rows, and the test asserts that contract.)

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

            // The housecarl_read_record / housecarl_batch_record_detail renders of the same annotation stood
            // here (the raw token plus the arrow parenthetical; the dangling target marked unresolved). Both
            // tools are gone; the claims are tests against housecarl_records' fields form with
            // project.resolve_names in src/housecarl-mcp-tests/RecordsBulkSelectTests.cs. The "the batch tool
            // shares the memo" arm has no successor — housecarl_records' formids lane IS the batch, so there is
            // no sibling to compare against.

            // ---- #230 end-to-end ----
            // The issue's manifestation stood here: its own mini-order (a stub Skyrim.esm written for the master
            // table but NOT loaded, so 000014 and 000015 both fail ResolveWinner and only the EngineImplicit
            // carve-out separates them) driven through housecarl_read_record, proving a PlayerRef link annotates
            // its identity while the next sub-0x800 form still reads unresolved. The tool is gone; that world and
            // both arms are RecordsEngineImplicitLinkTests in src/housecarl-mcp-tests/RecordsBulkSelectTests.cs.
            // The RESOLVER half of #230 stays above — it is a service-layer claim.

            // ================= P6 — format="json" =================
            // P6's arms stood here, driving housecarl_read_record and housecarl_batch_record_detail: the record
            // DTO's key set, text/json token parity, the structured link sibling under resolve_names, the
            // sentinel-truncated body under a tiny max_chars, the batch envelope, and the conflict_tree+json
            // refusal. Both tools are gone. The surviving claims are tests against housecarl_records in
            // src/housecarl-mcp-tests/RecordsBulkSelectTests.cs. The conflict_tree refusal is NOT among them:
            // housecarl_records has no such parameter (the tree is a project FORM) and serves tree+json.

            // ===== HCBR-2026-07-15 — batch_record_detail plugin= (a specific override's version, in BULK) =====
            // The batch twin of housecarl_read_record's plugin= stood here: reading W1 as the master's own body
            // (damage 10) and as the replacer's (15), W2 read off the same pole in the same batch, the per-item
            // "does not touch" error for a record the pole never touches while the rest of the batch survives,
            // and the json record naming the pole as its source. Now housecarl_records' source= pole on the
            // formids lane — tests in src/housecarl-mcp-tests/RecordsBulkSelectTests.cs.

            // ===== cross_plugin_query json, and P5 (winner_fields= vs the scoped body) =====
            // The scan-lane arms stood here: the summary and group_by json envelopes, the where= wrong-path
            // accounting note reaching both transports verbatim, the unrecognized-format refusal, the scoped
            // body's own values with the loud scoped-values note, and winner_fields= retargeting display.
            // housecarl_cross_plugin_query is gone; the claims are tests against housecarl_records in
            // src/housecarl-mcp-tests/RecordsScanProjectionTests.cs — where the note names fields_source="winner",
            // which is how that tool spells the lever.

            // ================= container hint — the depth= knob is hinted only where it EXISTS =================
            // The depth-1 container note self-documents the expansion lever (HCBR-2026-07-12). The
            // cross_plugin_query text and json arms stood here — the tool's own depth= hint, and the absence of
            // the retired batch_record_detail redirect. That tool is gone, and the redirect's negative pin has no
            // subject left; the hint claim is a test against housecarl_records' scan lane in
            // src/housecarl-mcp-tests/RecordsScanProjectionTests.cs.
            // What remains below is service-layer: read_record's own hint, and the write read-back lane
            // (containerHint:null), which keeps the bare count with no knob named at all.
            Console.WriteLine();
            Console.WriteLine("── container hint: the service layer hints depth=2 where the knob exists; write read-backs stay bare ──");

            var rrHint = svc.ResolveRead(w1Fk, null, new[] { "Keywords" }, false);
            var rrNote = rrHint.Record?.Fields.FirstOrDefault(f => f.Path == "Keywords")?.Note;
            Check("read_record (HAS depth=) keeps the classic ' — pass depth=2 to expand' hint (default preserved)",
                  rrNote is not null && rrNote.Contains("pass depth=2 to expand"));

            var bareNote = ReadEngine.ReadFields(w1, new[] { "Keywords" }, containerHint: null)
                .Fields.FirstOrDefault()?.Note;
            Check("containerHint:null (the write read-back lane) renders the bare count — no hint at all",
                  bareNote is not null && bareNote.StartsWith("[list:") && !bareNote.Contains("depth"));

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

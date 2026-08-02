using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD for W2 PR 1 — the `housecarl_records` core surface + the W2 where-grammar terms
/// (tool-surface-2.0; SPEC §2.2 / §4.2 / §6.1). Arms:
///
///   1  GRAMMAR — the new predicate terms, in memory over synthesized bodies against brute-force oracles:
///      `startswith`; the `editorid` pseudo-path (EDID semantics — a no-editorid record is a DEFINITE
///      non-match, presence tests work); generalized `in`/`not in` over a leaf (enum names AND FormLink
///      leaves via the pre-parsed FormKey set); the `winner` provenance term (bound-resolver evaluation,
///      unbound = a typed FatalError, never a silent non-match); the `->` link step (ANY-match over the
///      targets, per-scan target cache, wrong-left-path fails LOUD via the accounting).
///   2  PARSE — the new refusals are named at parse, before any scan: startswith in the operator list,
///      malformed/chained arrows, winner with a non-equality op, editorid with a numeric op, formid with
///      a value op, presence tests on identity terms.
///   3  RECORDS/LIST — the formids= lane: identity form (resolve absorption; a named source refused by
///      contract), summary/fields/everything forms, the §4.2 ONE-POLE source: ACTIVE arm stated, untouched
///      records refused naming the ACTUAL TOUCHERS; OFF-ORDER arm (a disabled mod's plugin) stated with
///      the epoch-coverage qualifier; found-in-NEITHER-place refusal names both places searched.
///   4  RECORDS/SCAN — types= is a SET (union streams); the winner term and link step ride where= end to
///      end; form-scoping refusals teach the project= structure by name; the W2-PR2 forms/poles refuse by
///      NAME (staging, never a silent gap); identity-on-scan and dense-on-list refuse with the rule.
///   5  TRANSPORT — to_file on the list lane writes the §2.1.1 artifact (manifest-only inline) and
///      formids=@artifact re-enters it epoch-checked: same build passes, a changed order refuses naming
///      BOTH epochs.
///
/// Self-contained: synthetic MO2 instance + synthesized plugins in temp. No game data.
/// Run: <c>dotnet run --project src/housecarl-generator records-guard</c>
/// </summary>
internal static class RecordsGuardProbe
{
    static int _fail;
    static void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) _fail++; }

    static JsonElement Je(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" records guard — the 2.0 S1 read surface, core forms (W2 PR 1)");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        _fail = 0;

        var root = Path.Combine(Path.GetTempPath(), "hc-records-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            // ============================================================================================
            //  Fixture — a master (weapons, MGEF/spell pair, NPC-ish content), an ACTIVE override, a
            //  DISABLED old patch (the off-order pole), and a second disabled copy for the ambiguity arm.
            // ============================================================================================
            Directory.CreateDirectory(Path.Combine(root, "game", "Data"));
            var masterKey = new ModKey("HcRecMaster", ModType.Master);
            var ovKey = new ModKey("HcRecOverride", ModType.Plugin);
            var oldKey = new ModKey("HcRecOld", ModType.Plugin);
            string masterName = masterKey.FileName.String, ovName = ovKey.FileName.String, oldName = oldKey.FileName.String;

            var master = new SkyrimMod(masterKey, SkyrimRelease.SkyrimSE);
            var weapons = new List<FormKey>();
            for (int i = 0; i < 3; i++)
            {
                var w = master.Weapons.AddNew(); w.EditorID = $"HcRecW{i}";
                w.BasicStats = new WeaponBasicStats { Damage = (ushort)(10 * (i + 1)), Weight = 1 };
                weapons.Add(w.FormKey);
            }
            var noEidWeap = master.Weapons.AddNew();                       // NO EditorID — the editorid-term definite-non-match case
            noEidWeap.BasicStats = new WeaponBasicStats { Damage = 5, Weight = 1 };
            var armo = master.Armors.AddNew(); armo.EditorID = "HcRecA0";  // a second TYPE for the union arm
            var mgefA = master.MagicEffects.AddNew(); mgefA.EditorID = "HcRecMgefFire";
            var mgefB = master.MagicEffects.AddNew(); mgefB.EditorID = "OtherMgef";
            var spellA = master.Spells.AddNew(); spellA.EditorID = "HcRecSpellA";
            { var e = new Effect(); e.BaseEffect.SetTo(mgefA.FormKey); e.Data = new EffectData { Magnitude = 5 }; spellA.Effects.Add(e); }
            var spellB = master.Spells.AddNew(); spellB.EditorID = "HcRecSpellB";
            { var e = new Effect(); e.BaseEffect.SetTo(mgefB.FormKey); e.Data = new EffectData { Magnitude = 7 }; spellB.Effects.Add(e); }

            var ovMod = new SkyrimMod(ovKey, SkyrimRelease.SkyrimSE);
            ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(ovMod, master.Weapons.First(w => w.FormKey == weapons[0])))
                .BasicStats = new WeaponBasicStats { Damage = 99, Weight = 1 };
            // A DELETED override (PR #307 review fold): header/resolution-only where-terms must still see it.
            ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(ovMod, master.Weapons.First(w => w.FormKey == weapons[2])))
                .IsDeleted = true;

            var oldMod = new SkyrimMod(oldKey, SkyrimRelease.SkyrimSE);
            ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(oldMod, master.Weapons.First(w => w.FormKey == weapons[1])))
                .BasicStats = new WeaponBasicStats { Damage = 55, Weight = 1 };

            var inst = Path.Combine(root, "inst");
            var mods = Path.Combine(inst, "mods");
            foreach (var d in new[] { "MasterMod", "OverrideMod", "OldMod" }) Directory.CreateDirectory(Path.Combine(mods, d));
            var masterFile = Path.Combine(mods, "MasterMod", masterName);
            var ovFile = Path.Combine(mods, "OverrideMod", ovName);
            var oldFile = Path.Combine(mods, "OldMod", oldName);
            master.BeginWrite.ToPath(masterFile).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            ovMod.BeginWrite.ToPath(ovFile).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();
            oldMod.BeginWrite.ToPath(oldFile).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

            string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

            // ============================================================================================
            //  1 — GRAMMAR, in memory (brute-force oracles over the known literals)
            // ============================================================================================
            Console.WriteLine("--- 1: the W2 where-grammar terms (in-memory oracle) ---");
            var weapBodies = master.Weapons.Select(w => (IMajorRecordGetter)w).ToList();
            var spellBodies = master.Spells.Select(s => (IMajorRecordGetter)s).ToList();
            var mgefByKey = master.MagicEffects.ToDictionary(m => m.FormKey, m => (IMajorRecordGetter)m);

            static HashSet<FormKey> Run(string[] where, IEnumerable<IMajorRecordGetter> bodies,
                                        Func<FormKey, string?>? winnerOf = null, Func<FormKey, IMajorRecordGetter?>? fetch = null)
            {
                var (set, err) = FieldPredicateSet.Parse(where);
                if (err is not null) throw new InvalidOperationException($"unexpected parse error: {err}");
                if (set!.NeedsResolution) set.BindResolution(winnerOf ?? (_ => null), fetch);
                return bodies.Where(b => set.Matches(b)).Select(b => b.FormKey).ToHashSet();
            }

            // startswith — on the editorid pseudo-path AND on a REAL leaf token (round-3 F6: the pseudo-path
            // short-circuits before Compare(), so only a genuine leaf drives the new Op.StartsWith arm there).
            Check(Run(new[] { "editorid startswith HcRecW" }, weapBodies).SetEquals(weapons),
                  "startswith: 'editorid startswith HcRecW' selects exactly the three named weapons (no-editorid record drops out)");
            Check(Run(new[] { "BasicStats.Damage startswith 1" }, weapBodies).SetEquals(new[] { weapons[0] }),
                  "startswith on a REAL leaf token routes through Compare (10 matches '1'; 20/30/5 do not)");
            Check(Run(new[] { "editorid contains recw1" }, weapBodies).SetEquals(new[] { weapons[1] }),
                  "editorid term: 'contains' is case-insensitive and selects the one match");
            Check(Run(new[] { "editorid missing" }, weapBodies).SetEquals(new[] { noEidWeap.FormKey }),
                  "editorid term: 'missing' selects exactly the no-EditorID record");
            Check(Run(new[] { "editorid = HcRecW2" }, weapBodies).SetEquals(new[] { weapons[2] }),
                  "editorid term: '=' exact (case-insensitive) match");

            // generalized membership — enum leaf and numeric leaf.
            Check(Run(new[] { "BasicStats.Damage in [10, 30]" }, weapBodies).SetEquals(new[] { weapons[0], weapons[2] }),
                  "membership: a numeric leaf 'in [10, 30]' keeps exactly the listed values");
            Check(Run(new[] { "BasicStats.Damage not in [10, 30]" }, weapBodies).SetEquals(new[] { weapons[1], noEidWeap.FormKey }),
                  "membership: 'not in' is its complement over value-bearing records");
            Check(Run(new[] { $"Effects[0].BaseEffect in [{Fid(mgefA.FormKey)}]" }, spellBodies).SetEquals(new[] { spellA.FormKey }),
                  "membership: a FormLink leaf against a FormKey list uses identity-canonical equality");

            // the winner provenance term — bound resolver decides; unbound is a typed fatal.
            {
                var winnerMap = new Dictionary<FormKey, string> { [weapons[0]] = ovName };
                string? WinnerOf(FormKey fk) => winnerMap.TryGetValue(fk, out var w) ? w : masterName;
                Check(Run(new[] { $"winner = {ovName}" }, weapBodies, WinnerOf).SetEquals(new[] { weapons[0] }),
                      "winner term: 'winner = Override.esp' selects exactly the overridden record");
                Check(Run(new[] { $"winner != {ovName}" }, weapBodies, WinnerOf)
                          .SetEquals(weapBodies.Select(b => b.FormKey).Where(k => k != weapons[0])),
                      "winner term: '!=' is the complement");
                var (set, _) = FieldPredicateSet.Parse(new[] { $"winner = {ovName}" });
                Check(set!.NeedsResolution, "winner term: NeedsResolution is true (the call site knows to bind)");
                set.Matches(weapBodies[0]);   // deliberately UNBOUND
                Check(set.FatalError is not null && set.FatalError.Contains("winner"),
                      "winner term: evaluating UNBOUND is a typed FatalError, never a silent non-match");
            }

            // the -> link step — ANY-match over targets, resolved through the bound fetch.
            {
                IMajorRecordGetter? Fetch(FormKey fk) => mgefByKey.GetValueOrDefault(fk);
                Check(Run(new[] { "Effects->editorid startswith HcRec" }, spellBodies, null, Fetch).SetEquals(new[] { spellA.FormKey }),
                      "link step: 'Effects->editorid startswith HcRec' selects the spell whose EFFECT TARGET matches");
                Check(Run(new[] { $"Effects->formid in [{Fid(mgefB.FormKey)}]" }, spellBodies, null, Fetch).SetEquals(new[] { spellB.FormKey }),
                      "link step: '->formid in [list]' tests the TARGETS' identity");
                var (set, err) = FieldPredicateSet.Parse(new[] { "NoSuchField->editorid contains x" });
                Check(err is null, "link step: a wrong LEFT path parses (it is a per-record classification, not a parse error)");
                set!.BindResolution(_ => null, Fetch);
                foreach (var b in spellBodies) set.Matches(b);
                var note = set.AccountingNote();
                Check(note is not null && note.Contains("NoSuchField->editorid"),
                      "link step: a wrong left path fails LOUD in the accounting (named with the arrow), never a silent 0-matches");
            }

            // ============================================================================================
            //  2 — PARSE refusals (named, before any scan)
            // ============================================================================================
            Console.WriteLine();
            Console.WriteLine("--- 2: parse refusals name themselves ---");
            static string? ParseErr(string clause) => FieldPredicateSet.Parse(new[] { clause }).Error;
            Check(ParseErr("EditorID blorp x") is { } e1 && e1.Contains("startswith"),
                  "the operator list in refusals names startswith");
            Check(ParseErr("Perks-> startswith x") is { } e2 && e2.Contains("link step"),
                  "a malformed arrow ('Perks->') is a named link-step refusal");
            Check(ParseErr("A->B->C = 1") is { } e3 && e3.Contains("ONE"),
                  "a chained arrow refuses — one link step only, the walk construct owns chains");
            Check(ParseErr("winner > 5") is { } e4 && e4.Contains("winner"),
                  "'winner' with a non-equality operator refuses naming the term's grammar");
            Check(ParseErr("winner exists") is { } e5 && e5.Contains("provenance"),
                  "a presence test on 'winner' refuses via the term's op grammar ('=' / '!=' only)");
            Check(ParseErr("formid exists") is { } e5b && e5b.Contains("always exists"),
                  "a presence test on 'formid' refuses (identity always exists — it can never filter)");
            Check(ParseErr("editorid > 5") is { } e6 && e6.Contains("text term"),
                  "'editorid' with a numeric operator refuses naming the text grammar");
            Check(ParseErr("formid = 000801:X.esp") is { } e7 && e7.Contains("membership"),
                  "'formid' with a value operator refuses pointing at the membership ops");
            Check(ParseErr("Effects->winner = X.esp") is { } e8 && e8.Contains("link step"),
                  "'winner' behind an arrow refuses (it names the CANDIDATE's resolution)");

            // ============================================================================================
            //  3 — the records tool, LIST lane, over the real service (synthetic MO2 instance)
            // ============================================================================================
            Console.WriteLine();
            Console.WriteLine("--- 3: records / list lane — identity, one-pole source, touchers-named refusals ---");

            var genDir = Path.Combine(root, "corpus-gen");
            CorpusGenerator.GenerateAll(genDir, Path.Combine(root, "corpus-ref"));
            CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");
            File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
                "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                + Path.Combine(root, "game").Replace(@"\", @"\\") + ")\r\n");
            var prof = Path.Combine(inst, "profiles", "Default");
            Directory.CreateDirectory(prof);
            File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + masterName + "\r\n" + ovName + "\r\n");
            File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + masterName + "\r\n*" + ovName + "\r\n");
            File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n-OldMod\r\n+OverrideMod\r\n+MasterMod\r\n");
            var store = new UserConfigStore(Path.Combine(root, "user.json"));
            using var svc = LoadOrderService.WithInstance(inst, 0, store);
            var epoch0 = svc.Stats().epoch;

            // identity form (resolve absorption)
            var idText = RecordsTools.Records(svc, formids: new[] { Fid(weapons[0]), Fid(mgefA.FormKey) },
                                              project: new RecordsTools.RecordsProject { form = "identity" });
            Check(idText.Contains("form=identity") && idText.Contains("HcRecW0") && idText.Contains($"epoch={epoch0}"),
                  "identity form: labels the list, states the form, stamps the epoch");
            var idNamed = RecordsTools.Records(svc, formids: new[] { Fid(weapons[0]) }, source: Je($"\"{ovName}\""),
                                               project: new RecordsTools.RecordsProject { form = "identity" });
            Check(idNamed.StartsWith("error:") && idNamed.Contains("labeling frame"),
                  "identity form + a named source refuses by contract (identity is the resolution frame)");

            // default form (summary) + the ACTIVE one-pole arm + the touchers-named untouched refusal
            var sumActive = RecordsTools.Records(svc, formids: new[] { Fid(weapons[0]), Fid(weapons[1]) }, source: Je($"\"{ovName}\""));
            Check(sumActive.Contains("form=summary") && sumActive.Contains("active in the load order"),
                  "one-pole ACTIVE arm: the response STATES the arm (source= name — active)");
            Check(sumActive.Contains("does not touch") && sumActive.Contains(masterName) && sumActive.Contains(ovName),
                  "…an untouched record is a per-item refusal NAMING THE ACTUAL TOUCHERS (§4.2)");
            Check(sumActive.Contains("Damage") == false, "…summary rows carry identity facts, not field dumps");

            // OFF-ORDER arm: the disabled old patch — stated, epoch-coverage qualified, winner context carried
            var sumOld = RecordsTools.Records(svc, formids: new[] { Fid(weapons[1]) }, source: Je($"\"{oldName}\""));
            Check(sumOld.Contains("OUT-OF-LOAD-ORDER") && sumOld.Contains("form=summary"),
                  "one-pole OFF-ORDER arm: a disabled mod's plugin resolves and the response states the arm");
            Check(sumOld.Contains("OUTSIDE the epoch fingerprint"),
                  "…with the epoch-coverage qualifier (the file's content is outside the fingerprint)");
            Check(sumOld.Contains($"winner={masterName}"),
                  "…and the row still carries the ACTIVE winner context for the record");
            var fieldsOld = RecordsTools.Records(svc, formids: new[] { Fid(weapons[1]) }, source: Je($"\"{oldName}\""),
                                                 project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "BasicStats.Damage" } });
            Check(fieldsOld.Contains("55"), "…fields form reads the FILE's own version (Damage=55, not the winner's 20)");

            // found in NEITHER place / ambiguity
            var nowhere = RecordsTools.Records(svc, formids: new[] { Fid(weapons[0]) }, source: Je("\"NoSuchPlugin.esp\""));
            Check(nowhere.Contains("NEITHER place") && nowhere.Contains("not ACTIVE") && nowhere.Contains("on disk"),
                  "a pole found in NEITHER place refuses naming BOTH places searched (§4.2)");
            var dupDir = Path.Combine(mods, "OldModCopy");
            Directory.CreateDirectory(dupDir);
            File.Copy(oldFile, Path.Combine(dupDir, oldName));
            var ambiguous = RecordsTools.Records(svc, formids: new[] { Fid(weapons[1]) }, source: Je($"\"{oldName}\""));
            Check(ambiguous.Contains("SEVERAL mod folders") && ambiguous.Contains("\"mod\""),
                  "a duplicate filename refuses naming the mod folders + the {file, mod} disambiguator");
            var disamb = RecordsTools.Records(svc, formids: new[] { Fid(weapons[1]) },
                                              source: Je($"{{\"file\": \"{oldName}\", \"mod\": \"OldMod\"}}"));
            Check(disamb.Contains("OUT-OF-LOAD-ORDER") && !disamb.StartsWith("error:"),
                  "…and the structured {file, mod} pole disambiguates it");
            Directory.Delete(dupDir, true);

            // list-lane aggregate + counts_only
            var agg = RecordsTools.Records(svc, formids: weapons.Select(Fid).ToArray(),
                                           project: new RecordsTools.RecordsProject { form = "aggregate", group_by = "winner" });
            Check(agg.Contains("group_by=winner") && agg.Contains(masterName) && agg.Contains(ovName),
                  "list-lane aggregate: counts by winner over the resolved rows");
            var census = RecordsTools.Records(svc, formids: weapons.Select(Fid).ToArray(), counts_only: true);
            Check(census.Contains("count=3") && census.Contains("ok=3"),
                  "counts_only: the cheap census, no rows");

            // ============================================================================================
            //  4 — the records tool, SCAN lane
            // ============================================================================================
            Console.WriteLine();
            Console.WriteLine("--- 4: records / scan lane — type union, winner term, link step, form-scoping ---");

            var union = RecordsTools.Records(svc, types: new[] { "WEAP", "ARMO" });
            Check(union.Contains("HcRecW0") && union.Contains("HcRecA0"),
                  "types= is a SET: the scan streams the union of WEAP + ARMO");
            var winTerm = RecordsTools.Records(svc, types: new[] { "WEAP" }, where: new[] { $"winner = {ovName}" },
                                               project: new RecordsTools.RecordsProject { form = "summary" });
            Check(winTerm.Contains("HcRecW0") && !winTerm.Contains("HcRecW1"),
                  "the winner provenance term rides where= end to end ('which records does X win')");
            var linkStep = RecordsTools.Records(svc, types: new[] { "SPEL" }, where: new[] { "Effects->editorid startswith HcRecMgef" });
            Check(linkStep.Contains("HcRecSpellA") && !linkStep.Contains("HcRecSpellB"),
                  "the -> link step rides where= end to end (spells whose effect target matches)");
            var eidTerm = RecordsTools.Records(svc, types: new[] { "WEAP" }, where: new[] { "editorid startswith HcRecW" });
            Check(eidTerm.Contains("HcRecW0") && eidTerm.Contains("HcRecW1"),
                  "the editorid term replaces editorid_contains= (startswith works in a scan)");

            // form-scoping refusals + staged refusals
            var flatDepth = RecordsTools.Records(svc, types: new[] { "WEAP" },
                                                 project: new RecordsTools.RecordsProject { form = "summary", depth = 3 });
            Check(flatDepth.StartsWith("error:") && flatDepth.Contains("fields"),
                  "form-scoping: depth outside the fields/everything forms refuses naming the rule");
            var gbWrong = RecordsTools.Records(svc, types: new[] { "WEAP" },
                                               project: new RecordsTools.RecordsProject { form = "summary", group_by = "winner" });
            Check(gbWrong.StartsWith("error:") && gbWrong.Contains("aggregate"),
                  "form-scoping: group_by outside the aggregate form refuses naming the rule");
            var tree = RecordsTools.Records(svc, formids: new[] { Fid(weapons[0]) },
                                            project: new RecordsTools.RecordsProject { form = "tree" });
            Check(tree.StartsWith("error:") && tree.Contains("conflict_tree"),
                  "staging: form='tree' refuses by NAME pointing at today's working spelling");
            var prevProv = RecordsTools.Records(svc, formids: new[] { Fid(weapons[0]) }, source: Je("\"previous_provider\""));
            Check(prevProv.StartsWith("error:") && prevProv.Contains("PR 2"),
                  "staging: source='previous_provider' refuses by name");
            var mixed = RecordsTools.Records(svc, formids: new[] { Fid(weapons[0]) }, types: new[] { "WEAP" });
            Check(mixed.StartsWith("error:") && mixed.Contains("W2 PR 2"),
                  "staging: formids= x scan-terms composition refuses by name with the workaround");
            var idScan = RecordsTools.Records(svc, types: new[] { "WEAP" },
                                              project: new RecordsTools.RecordsProject { form = "identity" });
            Check(idScan.StartsWith("error:") && idScan.Contains("summary"),
                  "identity on the scan lane refuses (summary rows already carry identity)");

            // scan + everything (selection + body lane, epoch-compared)
            var ev = RecordsTools.Records(svc, types: new[] { "ARMO" },
                                          project: new RecordsTools.RecordsProject { form = "everything" });
            Check(ev.Contains("HcRecA0") && ev.Contains("match(es)"),
                  "scan + everything: selection via the scan, full bodies via the batch lane");

            // aggregate on scan + json envelope
            var aggScan = RecordsTools.Records(svc, types: new[] { "WEAP" }, format: "json",
                                               project: new RecordsTools.RecordsProject { form = "aggregate", group_by = "winner" });
            Check(aggScan.Contains("\"form\": \"aggregate\"") && aggScan.Contains("\"groups\""),
                  "scan aggregate in json carries the records envelope (form) in-band");
            var sumJson = RecordsTools.Records(svc, formids: new[] { Fid(weapons[0]) }, format: "json");
            Check(sumJson.Contains("\"form\": \"summary\"") && sumJson.Contains("\"source\": \"winner\""),
                  "list summary in json carries form + resolved source arm in the envelope");

            // off-order SCAN lane (the file's records as the universe)
            var offScan = RecordsTools.Records(svc, types: new[] { "WEAP" }, source: Je($"\"{oldName}\""));
            Check(offScan.Contains("OUT-OF-LOAD-ORDER") && offScan.Contains("HcRecW1"),
                  "off-order scan: types= enumerates the FILE's own records, arm stated");
            var offAgg = RecordsTools.Records(svc, source: Je($"\"{oldName}\""), types: new[] { "WEAP" },
                                              project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "BasicStats.Damage" } });
            Check(offAgg.StartsWith("error:") && offAgg.Contains("W2 PR 2"),
                  "off-order scan: the fields form refuses by name (staged) with the formids= workaround");

            // ============================================================================================
            //  4b — PR #307 review folds
            // ============================================================================================
            Console.WriteLine();
            Console.WriteLine("--- 4b: review folds — null-EditorID polarity, deleted records, pole coherence, empty results ---");

            // editorid '!=' / membership polarity over a no-EditorID record (findings 2 + 7).
            Check(Run(new[] { "editorid != HcRecW0" }, weapBodies)
                      .SetEquals(weapBodies.Select(b => b.FormKey).Where(k => k != weapons[0])),
                  "editorid '!=' KEEPS the no-EditorID record (not-equal is unambiguously true there)");
            Check(Run(new[] { "editorid in [HcRecW0, HcRecW2]" }, weapBodies).SetEquals(new[] { weapons[0], weapons[2] }),
                  "editorid membership: 'in [list]' selects exactly the listed EditorIDs");
            Check(Run(new[] { "editorid not in [HcRecW0]" }, weapBodies)
                      .SetEquals(weapBodies.Select(b => b.FormKey).Where(k => k != weapons[0])),
                  "editorid membership: 'not in' keeps the rest INCLUDING the no-EditorID record");

            // Header/resolution-only terms see DELETED records; body terms still skip them (finding 4).
            var delWin = RecordsTools.Records(svc, types: new[] { "WEAP" }, where: new[] { $"winner = {ovName}" });
            Check(delWin.Contains(Fid(weapons[2])),
                  "the winner term SEES a deleted record (resolution is a real fact about it)");
            var delBody = RecordsTools.Records(svc, types: new[] { "WEAP" }, where: new[] { "BasicStats.Damage > 0" });
            Check(!delBody.Contains(Fid(weapons[2])),
                  "…while a body predicate still skips it (no live body to judge)");

            // everything under a plugins= scope reads the SCOPED body — the fields form's pole (finding 3).
            var evScoped = RecordsTools.Records(svc, plugins: new RecordsTools.RecordsScope { names = new[] { masterName } },
                                                types: new[] { "WEAP" }, where: new[] { "editorid = HcRecW0" },
                                                project: new RecordsTools.RecordsProject { form = "everything", depth = 2 });
            Check(evScoped.Contains("Damage = 10") && !evScoped.Contains("Damage = 99"),
                  "scan+everything under plugins= dumps the SCOPED body (the matched pole), not the winner");
            var evScopedW = RecordsTools.Records(svc, plugins: new RecordsTools.RecordsScope { names = new[] { masterName } },
                                                 types: new[] { "WEAP" }, where: new[] { "editorid = HcRecW0" }, fields_source: "winner",
                                                 project: new RecordsTools.RecordsProject { form = "everything", depth = 2 });
            Check(evScopedW.Contains("Damage = 99"),
                  "…and fields_source='winner' retargets the dump to the winner, same as the fields form");

            // A zero-match scan is an honest EMPTY result, never the mid-call-tear message (finding 1).
            var evEmpty = RecordsTools.Records(svc, types: new[] { "WEAP" }, where: new[] { "BasicStats.Damage > 9999" },
                                               source: Je($"\"{ovName}\""),
                                               project: new RecordsTools.RecordsProject { form = "everything" });
            Check(!evEmpty.StartsWith("error:") && !evEmpty.Contains("vanished") && evEmpty.Contains("0 match(es)"),
                  "a zero-match scan + named source + everything renders an honest empty result, not a tear refusal");

            // Off-order scan: offset= and counts_only= refuse by name (finding 5).
            var offOffset = RecordsTools.Records(svc, types: new[] { "WEAP" }, source: Je($"\"{oldName}\""), offset: 500);
            Check(offOffset.StartsWith("error:") && offOffset.Contains("offset"),
                  "off-order scan: offset= refuses by name (no silent same-window paging)");
            var offCounts = RecordsTools.Records(svc, types: new[] { "WEAP" }, source: Je($"\"{oldName}\""), counts_only: true);
            Check(offCounts.StartsWith("error:") && offCounts.Contains("counts_only"),
                  "off-order scan: counts_only= refuses by name");

            // List aggregate carries the source arm + coverage qualifier in BOTH formats (finding 6).
            var aggOld = RecordsTools.Records(svc, formids: new[] { Fid(weapons[1]) }, source: Je($"\"{oldName}\""),
                                              project: new RecordsTools.RecordsProject { form = "aggregate", group_by = "winner" });
            Check(aggOld.Contains("OUT-OF-LOAD-ORDER"),
                  "list aggregate (text) states the resolved OFF-ORDER arm");
            var aggOldJson = RecordsTools.Records(svc, formids: new[] { Fid(weapons[1]) }, source: Je($"\"{oldName}\""), format: "json",
                                                  project: new RecordsTools.RecordsProject { form = "aggregate", group_by = "winner" });
            Check(aggOldJson.Contains("OUT-OF-LOAD-ORDER") && aggOldJson.Contains("epoch_covers_source"),
                  "list aggregate (json) carries the arm + the epoch-coverage qualifier in the envelope");

            // Re-review folds: dense refuses the column-less forms by name (never a silent transport switch),
            // and the list lane refuses fields_source= by name (never accepted-and-dropped).
            var denseEv = RecordsTools.Records(svc, types: new[] { "WEAP" }, format: "dense",
                                               project: new RecordsTools.RecordsProject { form = "everything" });
            Check(denseEv.StartsWith("error:") && denseEv.Contains("column"),
                  "dense + everything refuses by name (no fixed column set) — never a silent text fallback");
            var denseAgg = RecordsTools.Records(svc, types: new[] { "WEAP" }, format: "dense",
                                                project: new RecordsTools.RecordsProject { form = "aggregate", group_by = "winner" });
            Check(denseAgg.StartsWith("error:") && denseAgg.Contains("json"),
                  "dense + aggregate refuses by name pointing at json — never a silent json switch");
            var listFs = RecordsTools.Records(svc, formids: new[] { Fid(weapons[1]) }, source: Je($"\"{oldName}\""), fields_source: "winner",
                                              project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "BasicStats.Damage" } });
            Check(listFs.StartsWith("error:") && listFs.Contains("fields_source"),
                  "fields_source= on the formids= lane refuses by name — never accepted-and-dropped");

            // Round-3 folds: transport params are honored or refused, never accepted-and-dropped.
            var coToFile = RecordsTools.Records(svc, formids: weapons.Select(Fid).ToArray(), counts_only: true,
                                                to_file: Path.Combine(root, "never.jsonl"));
            Check(coToFile.StartsWith("error:") && coToFile.Contains("counts_only"),
                  "counts_only + to_file refuses by name (used to return the census and silently write nothing)");
            var idCensus = RecordsTools.Records(svc, formids: new[] { Fid(weapons[0]), "notaformid" }, counts_only: true,
                                                project: new RecordsTools.RecordsProject { form = "identity" });
            Check(idCensus.Contains("count=2") && idCensus.Contains("errors=1") && !idCensus.Contains("HcRecW0"),
                  "identity + counts_only returns the census, no rows (was rendered anyway)");
            var winList = RecordsTools.Records(svc, formids: weapons.Select(Fid).ToArray(), limit: 2, offset: 1);
            Check(winList.Contains("window: rows 2–3 of 3") && !winList.Contains(Fid(weapons[0])),
                  "limit/offset WINDOW the formids= render with the note in-band (were accepted-and-dropped)");
            var depthOne = RecordsTools.Records(svc, formids: new[] { Fid(weapons[0]) },
                                                project: new RecordsTools.RecordsProject { form = "summary", depth = 1 });
            Check(depthOne.StartsWith("error:") && depthOne.Contains("depth"),
                  "an EXPLICIT depth outside its forms refuses regardless of value (depth:1 was accepted-and-dropped)");

            // Off-order untouched refusal names the touchers (round-3 F3) — oldName touches only W1.
            var offUntouched = RecordsTools.Records(svc, formids: new[] { Fid(weapons[0]) }, source: Je($"\"{oldName}\""));
            Check(offUntouched.Contains("does not define or override") && offUntouched.Contains("Touched by") && offUntouched.Contains(masterName),
                  "off-order untouched: the per-item refusal names the ACTIVE touchers (was arm-asymmetric)");

            // A PATH-form source on the scan lane resolves back to the plugin name (round-3 F4).
            var pathScan = RecordsTools.Records(svc, types: new[] { "WEAP" }, source: Je(JsonSerializer.Serialize(ovFile)));
            Check(pathScan.Contains("active in the load order") && pathScan.Contains("HcRecW0") && !pathScan.StartsWith("error:"),
                  "a path-form source= on the scan lane resolves to its active plugin and the scan RUNS (was a refusal after a correct arm statement)");

            // ============================================================================================
            //  5 — TRANSPORT: to_file + @artifact re-entry, epoch-checked
            // ============================================================================================
            Console.WriteLine();
            Console.WriteLine("--- 5: to_file artifact + @file re-entry, epoch-checked ---");
            var artPath = Path.Combine(root, "results", "weaps.jsonl");
            Directory.CreateDirectory(Path.Combine(root, "results"));
            var toFile = RecordsTools.Records(svc, formids: weapons.Select(Fid).ToArray(), to_file: artPath,
                                              project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "BasicStats.Damage" } });
            Check(File.Exists(artPath) && toFile.Contains(artPath) && !toFile.Contains("Damage = 99"),
                  "to_file: the artifact is written, the response is manifest-only inline");
            var reenter = RecordsTools.Records(svc, formids: new[] { "@" + artPath },
                                               project: new RecordsTools.RecordsProject { form = "identity" });
            Check(reenter.Contains("HcRecW0") && reenter.Contains("HcRecW1") && !reenter.StartsWith("error:"),
                  "@artifact re-entry: the identity column becomes the list against the SAME build");
            File.SetLastWriteTimeUtc(ovFile, DateTime.UtcNow.AddMinutes(5));   // change the order → new epoch
            var stale = RecordsTools.Records(svc, formids: new[] { "@" + artPath },
                                             project: new RecordsTools.RecordsProject { form = "identity" });
            Check(stale.StartsWith("error:") && stale.Contains(epoch0!) && stale.Contains("epoch"),
                  "…after the order changes, re-entry REFUSES naming both epochs (never mixes two worlds)");

            Console.WriteLine();
            Console.WriteLine(_fail == 0
                ? "[records-guard] PASS — the 2.0 read surface's core contract holds."
                : $"[records-guard] FAIL — {_fail} check(s) regressed.");
            return _fail == 0 ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* temp cleanup best-effort */ }
        }
    }
}

using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for nested-record CREATE (nested/dialogue plan, Layer A), in the pattern of
/// upsert-guard / formid-floor-guard. Drives the REAL product path (WritePatchBuilder.CreateRecords) against a
/// SYNTHESIZED master in TEMP — NO Skyrim.esm, so it runs in CI (unlike the manual nested-create-proof, which samples
/// fixtures from vanilla Skyrim.esm). The synthesized master carries the three fixture shapes the mechanism nests
/// under: a Weapon (the can't-contain-a-child reject target), a DialogTopic (the unique-collection happy path), and an
/// interior Cell (the named-collection happy path + the ambiguous-collection reject).
/// Run: dotnet run --project src/housecarl-generator -- nested-create-guard
///
/// Arms (ALL required — a GREEN must mean "the contract holds", never "the scenario doesn't arise here"). They mirror
/// the manual proof's N1-N9:
///   ONESHOT   (N1) — topic + its first INFO created in ONE call (same-call sibling parent): both records allocated at
///                    the local 0x800+ floor, the INFO present in the topic's Responses on disk, distinct FormKeys.
///   MULTICHILD(N8) — topic + TWO INFOs in one call, the second carrying a field edit: all three created, both INFOs
///                    under the topic, the edited Prompt landed on the second.
///   INTOTOPIC (N2) — an INFO added to an EXISTING (master) topic by FormKey: the new INFO at the floor, present in
///                    the topic override's Responses (outcome (i), the unique collection derived by type).
///   INTOCELL  (N3) — a PlacedObject added to an EXISTING (master) cell, the collection named 'Persistent' (outcome
///                    (ii)): the new ref at the floor, present in the cell override's Persistent list.
///   REJ-NOPARENT(N4) — a nested type with NO parent refuses loud (the type isn't flat-createable), no file written.
///   REJ-BADPARENT(N5)— an INFO under a Weapon refuses loud ('cannot be created under' — the containment boundary),
///                    no file written.
///   REJ-AMBIG (N6) — a PlacedObject into a Cell with NO collection named refuses loud naming the candidate lists
///                    ('more than one' + 'Persistent') — the outcome-(ii) discriminator is required, never guessed.
///   REJ-FWDSIB(N7) — a child whose same-call sibling parent is declared LATER refuses loud ('earlier in this call').
///   EXTEND (was N9) — a parent created in a PRIOR into= call IS now resolvable (the N9 extend-gap fix): create a topic
///                    (call 1), then in a SECOND into= call add an INFO under it by FormKey — the INFO lands under the
///                    patch-carried topic. A GENUINELY-absent parent (in neither the load order nor the patch) still
///                    refuses loud, naming both.
///
/// Layer B unit A — same-call sibling reference (@editorid) as a FormLink VALUE (the PNAM order-chain + Topic back-link
/// in ONE bulk_create):
///   SIBREF        — topic + 2 lines; line 1's Topic=@topic and line 2's PreviousDialog=@line1 BOTH resolve to the
///                   right same-call 0x800+ FormKeys on disk (the keystone).
///   REJ-SIBFWD    — a field @ref to a sibling declared LATER refuses loud ('EARLIER in this call' — the declared-earlier rule).
///   REJ-SIBNONFL  — an @ref on a NON-FormLink field (FavorLevel) refuses loud ('only valid on a FormLink field' — the
///                   string-collision guard that keeps Phase-3 substitution scoped to formlinks).
///   REJ-SIBLIST   — an @ref inside a COLLECTION value (a list ReplaceAll's Values) refuses loud ('only supported as a
///                   single Set value') — the create path resolves only the singular Value, so a list/dict token must
///                   fail at the gate, not accept-then-throw at apply (Q3).
///   REJ-SIBDICT   — the dict half of that gate: an @ref inside a dict Merge's Entries values refuses loud the same way.
///   REJ-SIBAPPLY  — an @ref validated with a NULL sibling set (the Apply/set_field context) refuses at the rulebook —
///                   create-only scoping, so @editorid never becomes an accept-then-substitute-nothing hole (Q3).
///
/// GENERAL FormLink-ELEMENT collection value-shape — the BROADER pre-existing gap REJ-SIBLIST/REJ-SIBDICT named: ANY
/// malformed FormLink ELEMENT (not just an @editorid sibling token) in a collection value was accepted at pre-flight
/// then threw "Malformed FormKey string" at apply (a Q3 accept-then-throw). The gate now validates each element with
/// the SAME recognizer the singular formlink Set uses (DialogResponses.LinkTo = List&lt;FormLink&lt;DialogTopic&gt;&gt;):
///   FLELEM-REJ-GATE    — a malformed element in a ReplaceAll (req.Values), PAST a valid one, refuses at PRE-FLIGHT and
///                        NAMES the bad element ('Illegal FormLink element …' — per-element, the rulebook driven directly).
///   FLELEM-REJ-ADD     — the req.Value slot too: a malformed Add value refuses (Add/SetAtIndex carry the element there).
///   FLELEM-NULLCLEAR-OK— a null-clear synonym ('00000000') is a LEGAL element (shares IsValidFormLinkValue with the
///                        singular path) — the gate doesn't over-reject the clear shape.
///   FLELEM-OK-E2E      — a VALID FormID element round-trips through the REAL create+apply (accepted AND written to disk).
///   FLELEM-REJ-E2E     — a malformed element refuses end-to-end with NO file written; the PRE-FLIGHT message (not the
///                        apply throw) proves it was the gate. (The dict half — Merge/ReplaceAll on a formlink-VALUED
///                        dict — is dormant-by-construction: no such field is modeled in the corpus today, see ValueLegality.)
///
/// ELEMENT-VALUE PRESENCE — the null-PRESENCE twin of the value-SHAPE gap above (PR #76 follow-up). Add/SetAtIndex on a
/// coercible-element collection set the new element by coercing the singular req.Value; a MISSING value (req.Value null,
/// no compose) used to slip pre-flight — the formlink step-4a check uses `is { } ev`, which SKIPS a null slot — then
/// Coerce(null) yielded a null element that threw a NullReferenceException at SERIALIZE (the misleading "compose the Data
/// arm" message). The value-presence gate refuses it loud, mirroring the singular Set "requires a value":
///   FLELEM-REJ-NULLADD       — a null req.Value on a formlink-list Add refuses at PRE-FLIGHT (RED before: accepted, null).
///   FLELEM-REJ-NULLADD-PLAIN — the SAME gate fires for a NON-formlink coercible list (Race.MovementTypeNames =
///                              List&lt;String&gt;) — proves it's gated UNIFORMLY by element KIND, not formlink-ness (by construction).
///   FLELEM-REJ-NULLSETIDX    — a compose supplied with NO value on a coercible SetAtIndex still refuses (PR #77 review
///                              finding 1): SetAtIndex ignores req.Struct, so the gate carries NO req.Struct guard.
///   FLELEM-REJ-NULLADD-E2E   — a null-value Add refuses end-to-end with NO file written (the gate, not the serialize NRE).
///
/// KEY / INDEX PRESENCE — the missing-addressing-key twin of the value-presence gap above (PR #77 follow-up). A dict
/// Add/Remove coerces req.Key into / against the entry; a list SetAtIndex parses req.Key as the index. A MISSING
/// key/index slipped pre-flight (VerbLegality required a key only for Set-on-dict) and threw UNNAMED at apply
/// (Coerce(null) / int.Parse(null)). VerbLegality now requires it up front, by construction:
///   KEYIDX-REJ-DICTADD    — a dict Add with no key refuses at PRE-FLIGHT (Class.SkillWeights=Dictionary&lt;Skill,Byte&gt;;
///                           a valid value is supplied so ONLY the missing key differs). RED before: accepted (null).
///   KEYIDX-REJ-DICTREMOVE — a dict Remove with no key refuses (it identifies the entry BY key). RED before: accepted.
///   KEYIDX-REJ-SETIDX     — a list SetAtIndex with no index refuses (Race.MovementTypeNames=List&lt;String&gt;). RED before: accepted.
///   KEYIDX-OK-LISTREMOVE  — a keyless list Remove + value is STILL accepted: list Remove is by-index-OR-by-value, so the
///                           DICT-only scope does not over-reach to lists (no-over-reject, like FLELEM-NULLCLEAR-OK).
///   KEYIDX-REJ-SETIDX-E2E — a keyless SetAtIndex refuses end-to-end with NO file written; the PRE-FLIGHT message (not the
///                           apply int.Parse(null) throw) proves the gate. (Key VALUE-shape stays the deferred surface.)
/// </summary>
public static class NestedCreateGuardProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — nested-record CREATE (nested/dialogue plan, Layer A)  ################");
        Console.WriteLine();

        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-nested-create-guard");
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        Directory.CreateDirectory(tmpDir);

        // --- Setup: a master carrying the three nest-under fixtures + the validator corpus.
        //     Weapon  → the can't-contain reject (N5).  DialogTopic → the unique-collection happy path (N2).
        //     interior Cell → the named-collection happy path (N3) + the ambiguous-collection reject (N6). ---
        var mKey = new ModKey("HcNcGdMaster", ModType.Master);
        string mPath = Path.Combine(tmpDir, mKey.FileName.String);
        FormKey masterWeapFk, masterTopicFk, masterCellFk;
        try
        {
            var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);

            var w = m.Weapons.AddNew(); w.EditorID = "HcNcGdWeap"; w.BasicStats = new WeaponBasicStats { Damage = 10 };
            masterWeapFk = w.FormKey;

            var topic = m.DialogTopics.AddNew(); topic.EditorID = "HcNcGdTopic";
            masterTopicFk = topic.FormKey;

            // An interior cell lives under a CellBlock/CellSubBlock structure (FormKey-LESS group structs); build it by
            // hand — there's no flat AddNew for a cell. The cell itself is a normal (FormKey, release) record.
            var cell = new Cell(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcNcGdCell" };
            masterCellFk = cell.FormKey;
            var subBlock = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
            subBlock.Cells.Add(cell);
            var block = new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock };
            block.SubBlocks.Add(subBlock);
            m.Cells.Records.Add(block);

            m.BeginWrite.ToPath(mPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: could not synthesize the fixture master: {ex.GetType().Name}: {(ex.InnerException ?? ex).Message}");
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
            return 1;
        }

        // Confirm the synthesized fixtures actually round-trip + resolve (a master that doesn't carry them would make
        // the fixture-dependent arms silently test nothing — Q3).
        bool fixturesOk;
        using (var r = LoadOrderResolver.Build(new[] { mPath }))
        {
            var view = r.Capture();
            fixturesOk = view.ResolveWinner(masterWeapFk) is not null
                      && view.ResolveWinner(masterTopicFk) is not null
                      && view.ResolveWinner(masterCellFk) is not null;
        }
        var genDir = Path.Combine(tmpDir, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(tmpDir, "corpus-ref"));
        var rulebook = CorpusRulebook.Load(Path.Combine(genDir, "corpus.json"));
        Console.WriteLine($"-- setup: master {mKey.FileName} with weapon {masterWeapFk}, topic {masterTopicFk}, cell {masterCellFk}; fixtures-resolve={fixturesOk}; corpus generated --");
        Console.WriteLine();

        // ---------- ONESHOT (N1): topic + its first INFO in ONE call ----------
        bool oneshotOk = false;
        {
            string pPath = Path.Combine(tmpDir, "HcNcOneShot.esp");
            var specs = new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HcNcOsTopic", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcOsInfo", ParentRef = "HcNcOsTopic", Edits = Array.Empty<WriteRequest>() },
            };
            using var r = LoadOrderResolver.Build(new[] { mPath });
            var o = WritePatchBuilder.CreateRecords(r, rulebook, specs, pPath, extend: false);
            bool floored = o.Success && o.Created.Count == 2 && o.Created.All(c => c.FormKey.ID >= 0x800);
            var responses = o.Success ? TopicResponses(pPath, "HcNcOsTopic") : null;
            bool infoUnder = responses is not null && o.Success && responses.Contains(o.Created[1].FormKey);
            bool distinct = o.Success && o.Created.Count == 2 && o.Created[0].FormKey != o.Created[1].FormKey;
            oneshotOk = o.Success && floored && infoUnder && distinct;
            Console.WriteLine($"   ONESHOT  topic+first INFO, one call : {(oneshotOk ? $"PASS — both >=0x800, INFO under topic ({(responses?.Count ?? 0)} response)" : $"FAIL — success={o.Success} floored={floored} infoUnder={infoUnder} distinct={distinct} err=[{o.Error}]")}");
        }

        // ---------- MULTICHILD (N8): topic + two INFOs (+ a field edit) in one call ----------
        bool multiOk = false;
        {
            string pPath = Path.Combine(tmpDir, "HcNcMulti.esp");
            var specs = new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HcNcMTopic", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcML1", ParentRef = "HcNcMTopic", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcML2", ParentRef = "HcNcMTopic",
                    Edits = new[] { new WriteRequest { RecordType = "DialogResponses", Path = new[] { "Prompt" }, Verb = "Set", Value = "houseCARL line two" } } },
            };
            using var r = LoadOrderResolver.Build(new[] { mPath });
            var o = WritePatchBuilder.CreateRecords(r, rulebook, specs, pPath, extend: false);
            var responses = o.Success ? TopicResponses(pPath, "HcNcMTopic") : null;
            bool bothUnder = responses is not null && o.Success && responses.Contains(o.Created[1].FormKey) && responses.Contains(o.Created[2].FormKey);
            string? l2Prompt = o.Success ? InfoPrompt(pPath, o.Created[2].FormKey) : null;
            string? l1Prompt = o.Success ? InfoPrompt(pPath, o.Created[1].FormKey) : null;
            bool editLanded = l2Prompt == "houseCARL line two";
            bool editIsolated = l1Prompt != "houseCARL line two";   // the edit landed on L2 ONLY — it did NOT leak to its sibling L1
            multiOk = o.Success && o.Created.Count == 3 && bothUnder && editLanded && editIsolated;
            Console.WriteLine($"   MULTICHILD topic+2 INFO + field edit: {(multiOk ? $"PASS — 3 created, both under topic, L2.Prompt landed (only on L2)" : $"FAIL — success={o.Success} count={(o.Success ? o.Created.Count : 0)} bothUnder={bothUnder} editLanded={editLanded} editIsolated={editIsolated} l2=[{l2Prompt}] l1=[{l1Prompt}] err=[{o.Error}]")}");
        }

        // ---------- INTOTOPIC (N2): INFO into an EXISTING (master) topic by FormKey ----------
        bool intoTopicOk = false;
        {
            string pPath = Path.Combine(tmpDir, "HcNcIntoTopic.esp");
            var spec = new[] { new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcN2Info", ParentRef = masterTopicFk.ToString(), Edits = Array.Empty<WriteRequest>() } };
            using var r = LoadOrderResolver.Build(new[] { mPath });
            var o = WritePatchBuilder.CreateRecords(r, rulebook, spec, pPath, extend: false);
            bool floored = o.Success && o.Created.Count == 1 && o.Created[0].FormKey.ID >= 0x800 && o.Created[0].FormKey.ModKey.FileName.String == "HcNcIntoTopic.esp";
            var responses = o.Success ? TopicResponses(pPath, masterTopicFk) : null;
            bool present = responses is not null && o.Success && responses.Contains(o.Created[0].FormKey);
            intoTopicOk = o.Success && floored && present;
            Console.WriteLine($"   INTOTOPIC INFO into existing topic  : {(intoTopicOk ? "PASS — new INFO >=0x800/local, under the topic override" : $"FAIL — success={o.Success} floored={floored} present={present} err=[{o.Error}]")}");
        }

        // ---------- INTOCELL (N3): PlacedObject into an EXISTING cell, collection named 'Persistent' ----------
        bool intoCellOk = false;
        {
            string pPath = Path.Combine(tmpDir, "HcNcIntoCell.esp");
            var spec = new[] { new WritePatchBuilder.CreateSpec { RecordType = "PlacedObject", EditorId = "HcNcN3Ref", ParentRef = masterCellFk.ToString(), IntoCollection = "Persistent", Edits = Array.Empty<WriteRequest>() } };
            using var r = LoadOrderResolver.Build(new[] { mPath });
            var o = WritePatchBuilder.CreateRecords(r, rulebook, spec, pPath, extend: false);
            bool floored = o.Success && o.Created.Count == 1 && o.Created[0].FormKey.ID >= 0x800 && o.Created[0].FormKey.ModKey.FileName.String == "HcNcIntoCell.esp";
            var persistent = o.Success ? CellPersistent(pPath, masterCellFk) : null;
            bool present = persistent is not null && o.Success && persistent.Contains(o.Created[0].FormKey);
            intoCellOk = o.Success && floored && present;
            Console.WriteLine($"   INTOCELL Placed into cell.Persistent: {(intoCellOk ? "PASS — new ref >=0x800/local, in the cell override's Persistent" : $"FAIL — success={o.Success} floored={floored} present={present} err=[{o.Error}]")}");
        }

        // ---------- REJ-NOPARENT (N4) ----------
        bool rejNoParentOk = RejectArm("REJ-NOPARENT nested no parent     ", tmpDir, "N4", mPath, rulebook,
            new[] { new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcN4", Edits = Array.Empty<WriteRequest>() } },
            msg => msg.Contains("parent", StringComparison.OrdinalIgnoreCase) || msg.Contains("nested", StringComparison.OrdinalIgnoreCase));

        // ---------- REJ-BADPARENT (N5): INFO under a Weapon ----------
        bool rejBadParentOk = RejectArm("REJ-BADPARENT INFO under Weapon   ", tmpDir, "N5", mPath, rulebook,
            new[] { new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcN5", ParentRef = masterWeapFk.ToString(), Edits = Array.Empty<WriteRequest>() } },
            msg => msg.Contains("cannot be created under", StringComparison.OrdinalIgnoreCase));

        // ---------- REJ-AMBIG (N6): Placed into a Cell with no collection named ----------
        bool rejAmbigOk = RejectArm("REJ-AMBIG Placed, no collection    ", tmpDir, "N6", mPath, rulebook,
            new[] { new WritePatchBuilder.CreateSpec { RecordType = "PlacedObject", EditorId = "HcNcN6", ParentRef = masterCellFk.ToString(), Edits = Array.Empty<WriteRequest>() } },
            msg => msg.Contains("more than one", StringComparison.OrdinalIgnoreCase) && msg.Contains("Persistent", StringComparison.OrdinalIgnoreCase));

        // ---------- REJ-FWDSIB (N7): sibling parent declared LATER ----------
        bool rejFwdSibOk = RejectArm("REJ-FWDSIB forward sibling parent  ", tmpDir, "N7", mPath, rulebook,
            new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcN7Info", ParentRef = "HcNcN7Topic", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HcNcN7Topic", Edits = Array.Empty<WriteRequest>() },
            },
            msg => msg.Contains("earlier in this call", StringComparison.OrdinalIgnoreCase));

        // ---------- EXTEND (was N9): a parent created in a PRIOR into= call IS now resolvable (the N9 fix) ----------
        bool extendOk = false;
        {
            string pPath = Path.Combine(tmpDir, "HcNcExtend.esp");
            // call 1: a one-shot topic + its first line (so the topic ALREADY carries a child when call 2 extends it).
            FormKey topicFk = default, l1Fk = default; bool call1Ok;
            using (var r = LoadOrderResolver.Build(new[] { mPath }))
            {
                var o1 = WritePatchBuilder.CreateRecords(r, rulebook,
                    new[]
                    {
                        new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HcNcExTopic", Edits = Array.Empty<WriteRequest>() },
                        new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcExL1", ParentRef = "HcNcExTopic", Edits = Array.Empty<WriteRequest>() },
                    },
                    pPath, extend: false);
                call1Ok = o1.Success && o1.Created.Count == 2;
                if (call1Ok) { topicFk = o1.Created[0].FormKey; l1Fk = o1.Created[1].FormKey; }
            }
            // call 2: add a SECOND line under that topic — the topic lives ONLY in the patch (the N9 case).
            bool call2Ok = false; FormKey l2Fk = default;
            if (call1Ok)
                using (var r = LoadOrderResolver.Build(new[] { mPath }))
                {
                    var o2 = WritePatchBuilder.CreateRecords(r, rulebook,
                        new[] { new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcExL2", ParentRef = topicFk.ToString(), Edits = Array.Empty<WriteRequest>() } },
                        pPath, extend: true);
                    call2Ok = o2.Success; l2Fk = o2.Success ? o2.Created[0].FormKey : default;
                }
            // BOTH the prior line (L1) and the new line (L2) must be under the topic — the patch-carried parent is used
            // in full, never an override carrying only the new child (which would silently drop L1).
            var responses = call2Ok ? TopicResponses(pPath, topicFk) : null;
            bool under = responses is not null && responses.Contains(l1Fk) && responses.Contains(l2Fk);
            // and a GENUINELY-absent parent (in neither the load order nor the patch) still refuses loud, naming both.
            bool absentRefused = false; string? absentErr = null;
            if (call1Ok)
                using (var r = LoadOrderResolver.Build(new[] { mPath }))
                {
                    var o3 = WritePatchBuilder.CreateRecords(r, rulebook,
                        new[] { new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcExGhost", ParentRef = "0F0F0F:HcNcGdMaster.esm", Edits = Array.Empty<WriteRequest>() } },
                        pPath, extend: true);
                    absentRefused = !o3.Success; absentErr = o3.Error;
                }
            bool absentNamed = absentErr is not null && absentErr.Contains("load order", StringComparison.OrdinalIgnoreCase) && absentErr.Contains("patch", StringComparison.OrdinalIgnoreCase);
            extendOk = call1Ok && call2Ok && under && absentRefused && absentNamed;
            Console.WriteLine($"   EXTEND patch-carried parent works    : {(extendOk ? "PASS — prior-call topic resolvable, BOTH lines under it; a truly-absent parent still refuses loud" : $"FAIL — call1={call1Ok} call2={call2Ok} under={under} absentRefused={absentRefused} absentNamed={absentNamed} absentErr=[{absentErr}]")}");
        }

        // ---------- SIBREF (Layer B unit A): @editorid same-call FormLink forward-ref ----------
        // The keystone: a one-shot topic + two lines where line 1 back-links to the same-call topic (Topic=@topic) and
        // line 2 chains off line 1 (PreviousDialog=@line1) — BOTH targets are sibling local 0x800+ FormKeys not known
        // until allocation. Proves the @editorid token resolves to the right allocated FormKey in a FormLink VALUE.
        bool sibrefOk = false;
        {
            string pPath = Path.Combine(tmpDir, "HcNcSibRef.esp");
            var specs = new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HcNcSrTopic", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcSrL1", ParentRef = "HcNcSrTopic",
                    Edits = new[] { new WriteRequest { RecordType = "DialogResponses", Path = new[] { "Topic" }, Verb = "Set", Value = "@HcNcSrTopic" } } },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcSrL2", ParentRef = "HcNcSrTopic",
                    Edits = new[]
                    {
                        new WriteRequest { RecordType = "DialogResponses", Path = new[] { "Topic" }, Verb = "Set", Value = "@HcNcSrTopic" },
                        new WriteRequest { RecordType = "DialogResponses", Path = new[] { "PreviousDialog" }, Verb = "Set", Value = "@HcNcSrL1" },
                    } },
            };
            using var r = LoadOrderResolver.Build(new[] { mPath });
            var o = WritePatchBuilder.CreateRecords(r, rulebook, specs, pPath, extend: false);
            FormKey topicFk = o.Success ? o.Created[0].FormKey : default;
            FormKey l1Fk = o.Success && o.Created.Count > 1 ? o.Created[1].FormKey : default;
            FormKey l2Fk = o.Success && o.Created.Count > 2 ? o.Created[2].FormKey : default;
            var l1Topic = o.Success ? InfoTopic(pPath, l1Fk) : null;            // back-link → same-call topic
            var l2Topic = o.Success ? InfoTopic(pPath, l2Fk) : null;
            var l2Prev  = o.Success ? InfoPreviousDialog(pPath, l2Fk) : null;   // PNAM chain → prior same-call line
            bool backLink = l1Topic == topicFk && l2Topic == topicFk;
            bool pnam = l2Prev == l1Fk;
            sibrefOk = o.Success && o.Created.Count == 3 && backLink && pnam && topicFk != l1Fk && l1Fk != l2Fk;
            Console.WriteLine($"   SIBREF @editorid FormLink fwd-ref    : {(sibrefOk ? "PASS — Topic back-link + PreviousDialog chain resolved to same-call FormKeys" : $"FAIL — success={o.Success} backLink={backLink} pnam={pnam} l1Topic=[{l1Topic}] l2Prev=[{l2Prev}] topic=[{topicFk}] l1=[{l1Fk}] err=[{o.Error}]")}");
        }

        // ---------- REJ-SIBFWD: a field @ref to a sibling declared LATER refuses loud (declared-earlier rule) ----------
        bool sibRejFwdOk = RejectArm("REJ-SIBFWD @ref to later sibling   ", tmpDir, "SibFwd", mPath, rulebook,
            new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HcNcSfTopic", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcSfL1", ParentRef = "HcNcSfTopic",
                    Edits = new[] { new WriteRequest { RecordType = "DialogResponses", Path = new[] { "PreviousDialog" }, Verb = "Set", Value = "@HcNcSfL2" } } },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcSfL2", ParentRef = "HcNcSfTopic", Edits = Array.Empty<WriteRequest>() },
            },
            msg => msg.Contains("EARLIER in this call", StringComparison.OrdinalIgnoreCase));

        // ---------- REJ-SIBNONFL: an @ref on a NON-FormLink field refuses loud (the string-collision guard) ----------
        bool sibRejNonflOk = RejectArm("REJ-SIBNONFL @ref on non-formlink  ", tmpDir, "SibNonFl", mPath, rulebook,
            new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HcNcSnTopic", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcSnL1", ParentRef = "HcNcSnTopic",
                    Edits = new[] { new WriteRequest { RecordType = "DialogResponses", Path = new[] { "FavorLevel" }, Verb = "Set", Value = "@HcNcSnTopic" } } },
            },
            msg => msg.Contains("only valid on a FormLink field", StringComparison.OrdinalIgnoreCase));

        // ---------- REJ-SIBLIST: an @ref inside a COLLECTION value (list ReplaceAll) refuses loud, not accept-then-throw ----------
        // The create path substitutes only the singular Set Value; a sibling token in req.Values would otherwise slip
        // past pre-flight and throw FormKey.Factory at apply (a Q3 accept-then-throw). Caught loud at the gate instead.
        bool sibRejListOk = RejectArm("REJ-SIBLIST @ref in list value     ", tmpDir, "SibList", mPath, rulebook,
            new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HcNcSlTopic", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcSlL1", ParentRef = "HcNcSlTopic",
                    Edits = new[] { new WriteRequest { RecordType = "DialogResponses", Path = new[] { "LinkTo" }, Verb = "ReplaceAll", Values = new[] { "@HcNcSlTopic" } } } },
            },
            msg => msg.Contains("only supported as a single Set value", StringComparison.OrdinalIgnoreCase));

        // ---------- REJ-SIBDICT: an @ref inside a DICT value (Merge Entries) refuses loud — the dict half of the gate ----------
        // Sibling tokens in a dict Entries' VALUES are caught by the same collection gate as the list case (the |
        // req.Entries branch). Class.SkillWeights (Dictionary<Skill,Byte>) is a flat dict leaf; the gate fires on the
        // '@' in the Entries value before any key/value coercion, so the dict key need not be a valid Skill.
        bool sibRejDictOk = RejectArm("REJ-SIBDICT @ref in dict value     ", tmpDir, "SibDict", mPath, rulebook,
            new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "Class", EditorId = "HcNcSdClass",
                    Edits = new[] { new WriteRequest { RecordType = "Class", Path = new[] { "SkillWeights" }, Verb = "Merge",
                        Entries = new Dictionary<string, string> { ["OneHanded"] = "@HcNcSdClass" } } } },
            },
            msg => msg.Contains("only supported as a single Set value", StringComparison.OrdinalIgnoreCase));

        // ---------- REJ-SIBAPPLY: an @ref on the Apply/set_field path (no siblings) refuses at the rulebook ----------
        // Drives the rulebook DIRECTLY with a null sibling set (the override/set_field context) — proves the create-only
        // scoping that keeps @editorid from becoming an accept-then-substitute-nothing hole on the edit-existing path (Q3).
        bool sibRejApplyOk;
        {
            var req = new WriteRequest { RecordType = "DialogResponses", Path = new[] { "PreviousDialog" }, Verb = "Set", Value = "@AnySibling" };
            var reject = rulebook.Validate(req);   // null sibling set == the override/set_field context
            sibRejApplyOk = reject is not null && reject.Contains("only valid when creating records in ONE call", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   REJ-SIBAPPLY @ref on set_field path  : {(sibRejApplyOk ? "PASS — rejected at the gate (no same-call siblings when editing an existing record)" : $"FAIL — reject=[{reject}]")}");
        }

        // ====== GENERAL FormLink-ELEMENT collection value-shape (the broader gap the sibling-ref collection gate named) ======
        // The sibling-ref arms above gate a '@editorid' token in a collection value; this is the GENERAL case — ANY
        // malformed FormLink ELEMENT in a collection (a list/dict whose element is a FormLink). DialogResponses.LinkTo
        // is List<FormLink<IDialogTopicGetter>> (corpus FormLinkTarget set), the same fixture field REJ-SIBLIST used.

        // ---------- FLELEM-REJ-GATE: a malformed element in a ReplaceAll (req.Values) refuses at PRE-FLIGHT ----------
        // Drive the rulebook DIRECTLY (parent-free) — a non-null reject IS a gate refusal (apply would instead throw the
        // misleading "Malformed FormKey string"). A VALID FormID sits first in the list, so the per-element scan must
        // catch the bad one even past a good one, and the message must NAME the offending element (per-element, Q3).
        bool flElemRejGateOk;
        {
            var req = new WriteRequest { RecordType = "DialogResponses", Path = new[] { "LinkTo" }, Verb = "ReplaceAll",
                Values = new[] { masterTopicFk.ToString(), "notaformkey" } };
            var reject = rulebook.Validate(req);
            flElemRejGateOk = reject is not null
                && reject.Contains("Illegal FormLink element", StringComparison.OrdinalIgnoreCase)
                && reject.Contains("notaformkey", StringComparison.Ordinal);
            Console.WriteLine($"   FLELEM-REJ-GATE malformed list elem  : {(flElemRejGateOk ? "PASS — refused at pre-flight, names the bad element past a valid one" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- FLELEM-REJ-ADD: a malformed element in an Add (req.Value slot) refuses too ----------
        // Guards the req.Value arm of the gate specifically (Add/SetAtIndex carry the element in req.Value, not req.Values).
        bool flElemRejAddOk;
        {
            var req = new WriteRequest { RecordType = "DialogResponses", Path = new[] { "LinkTo" }, Verb = "Add", Value = "notaformkey" };
            var reject = rulebook.Validate(req);
            flElemRejAddOk = reject is not null && reject.Contains("Illegal FormLink element", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   FLELEM-REJ-ADD malformed Add value   : {(flElemRejAddOk ? "PASS — the req.Value slot is gated too" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- FLELEM-NULLCLEAR-OK: a null-clear synonym element is LEGAL (mirrors the singular formlink check) ----------
        // The gate shares IsValidFormLinkValue with the singular path, so "00000000" (a null-clear) must pass as an
        // element exactly as it does as a singular Set value — proves the gate doesn't over-reject the legal clear shape.
        bool flElemNullClearOk;
        {
            var req = new WriteRequest { RecordType = "DialogResponses", Path = new[] { "LinkTo" }, Verb = "ReplaceAll",
                Values = new[] { masterTopicFk.ToString(), "00000000" } };
            var reject = rulebook.Validate(req);
            flElemNullClearOk = reject is null;
            Console.WriteLine($"   FLELEM-NULLCLEAR-OK null-clear elem  : {(flElemNullClearOk ? "PASS — a real FormID and a null-clear synonym both accepted" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- FLELEM-OK-E2E: a VALID element round-trips through the REAL create+apply path ----------
        // The no-over-reject proof in full: pre-flight accepts AND the engine writes it (the gate guards the apply path,
        // it does not block it). Create a topic + INFO whose LinkTo ReplaceAll = [a real FormID]; read it back off disk.
        bool flElemOkE2eOk = false;
        {
            string pPath = Path.Combine(tmpDir, "HcNcFlElemOk.esp");
            var specs = new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HcNcFoTopic", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcFoL1", ParentRef = "HcNcFoTopic",
                    Edits = new[] { new WriteRequest { RecordType = "DialogResponses", Path = new[] { "LinkTo" }, Verb = "ReplaceAll",
                        Values = new[] { masterTopicFk.ToString() } } } },
            };
            using var r = LoadOrderResolver.Build(new[] { mPath });
            var o = WritePatchBuilder.CreateRecords(r, rulebook, specs, pPath, extend: false);
            var linkTo = o.Success && o.Created.Count > 1 ? InfoLinkTo(pPath, o.Created[1].FormKey) : null;
            bool present = linkTo is not null && linkTo.Contains(masterTopicFk);
            flElemOkE2eOk = o.Success && present;
            Console.WriteLine($"   FLELEM-OK-E2E valid elem round-trips : {(flElemOkE2eOk ? "PASS — accepted at the gate AND written to LinkTo on disk" : $"FAIL — success={o.Success} present={present} linkTo=[{(linkTo is null ? "null" : string.Join(",", linkTo))}] err=[{o.Error}]")}");
        }

        // ---------- FLELEM-REJ-E2E: a malformed element refuses end-to-end with NO file written (gate, not apply throw) ----------
        // The "no file written" half: drive the REAL create path; the message being the PRE-FLIGHT one (not the apply
        // "Malformed FormKey string") proves the gate caught it, and RejectArm proves the all-or-nothing leaves no file.
        bool flElemRejE2eOk = RejectArm("FLELEM-REJ-E2E malformed list elem ", tmpDir, "FlElem", mPath, rulebook,
            new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HcNcFeTopic", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcFeL1", ParentRef = "HcNcFeTopic",
                    Edits = new[] { new WriteRequest { RecordType = "DialogResponses", Path = new[] { "LinkTo" }, Verb = "ReplaceAll", Values = new[] { "notaformkey" } } } },
            },
            msg => msg.Contains("Illegal FormLink element", StringComparison.OrdinalIgnoreCase));

        // ====== ELEMENT-VALUE PRESENCE — the null-PRESENCE twin of the FLELEM value-SHAPE gap above ======
        // FLELEM-REJ-ADD/GATE catch a MALFORMED (non-null) element; this catches a MISSING one. The step-4a formlink
        // check uses `is { } ev`, which SKIPS a null req.Value — so a formlink-list Add with NO value used to pass
        // pre-flight (the RED state) and then null-deref/odd-result at apply (the same accept-then-throw shape PR #76
        // closed, but for the absent-value case). The value-presence gate refuses it loud, mirroring the singular
        // Set "requires a value". Coercible-element-only + verb-scoped (Add/SetAtIndex consume the singular req.Value).

        // ---------- FLELEM-REJ-NULLADD: a MISSING element value (req.Value null) on an Add refuses at PRE-FLIGHT ----------
        // Driven parent-free against the rulebook — a non-null reject IS the gate refusal. RED before the gate:
        // Validate returned null (accepted), because the formlink step-4a `is { } ev` skips the null slot.
        bool flElemRejNullAddOk;
        {
            var req = new WriteRequest { RecordType = "DialogResponses", Path = new[] { "LinkTo" }, Verb = "Add", Value = null };
            var reject = rulebook.Validate(req);
            flElemRejNullAddOk = reject is not null && reject.Contains("requires an element value", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   FLELEM-REJ-NULLADD missing Add value : {(flElemRejNullAddOk ? "PASS — a null element value is refused at pre-flight (not accepted-then-null/thrown at apply)" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- FLELEM-REJ-NULLADD-PLAIN: the gate fires for a NON-formlink coercible element too (uniform scope) ----------
        // The gate keys off the element KIND (ScalarCoercible/WholeCoercible via SchemaClassifier), not formlink-ness, so a
        // plain coercible list shares the same null-presence hazard and the same fix BY CONSTRUCTION. Race.MovementTypeNames
        // is List<String> (no FormLinkTarget, no ElementTypeRef → ScalarCoercible). RED before the gate: accepted (null).
        bool flElemRejNullAddPlainOk;
        {
            var req = new WriteRequest { RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "Add", Value = null };
            var reject = rulebook.Validate(req);
            flElemRejNullAddPlainOk = reject is not null && reject.Contains("requires an element value", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   FLELEM-REJ-NULLADD-PLAIN non-formlink: {(flElemRejNullAddPlainOk ? "PASS — a null value on a plain coercible (List<String>) Add is refused too — gated uniformly" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- FLELEM-REJ-NULLSETIDX: a compose supplied with NO value on a coercible SetAtIndex still refuses ----------
        // (PR #77 review finding 1.) SetAtIndex NEVER consumes req.Struct — ApplyListVerb's SetAtIndex is unconditionally
        // Coerce(req.Value!, elem) — so a compose+no-value must NOT suppress the presence gate, else Coerce(null) hits the
        // same serialize NRE the gate exists to kill. The gate therefore has NO req.Struct guard (a coercible element is
        // never built from a struct, so a struct here is itself malformed). RED before the finding-1 fold: the gate's old
        // `&& req.Struct is null` clause let a non-null Struct skip the gate → accepted. Race.MovementTypeNames = List<String>.
        bool flElemRejNullSetIdxOk;
        {
            var req = new WriteRequest { RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "SetAtIndex",
                Key = "0", Value = null, Struct = new StructSpec { Type = "Keyword" } };
            var reject = rulebook.Validate(req);
            flElemRejNullSetIdxOk = reject is not null && reject.Contains("requires an element value", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   FLELEM-REJ-NULLSETIDX struct+no value: {(flElemRejNullSetIdxOk ? "PASS — a compose can't suppress the gate on SetAtIndex (which ignores req.Struct)" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- FLELEM-REJ-NULLADD-E2E: a missing element value refuses end-to-end with NO file written ----------
        // The "no file written" half (RejectArm): the REAL create+apply path refuses a null-value LinkTo Add and leaves
        // no patch — the pre-flight message (not an apply null/throw) proving the gate, all-or-nothing leaving nothing.
        bool flElemRejNullAddE2eOk = RejectArm("FLELEM-REJ-NULLADD-E2E missing value", tmpDir, "FlElemNull", mPath, rulebook,
            new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HcNcFnTopic", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcFnL1", ParentRef = "HcNcFnTopic",
                    Edits = new[] { new WriteRequest { RecordType = "DialogResponses", Path = new[] { "LinkTo" }, Verb = "Add", Value = null } } },
            },
            msg => msg.Contains("requires an element value", StringComparison.OrdinalIgnoreCase));

        // ====== KEY / INDEX PRESENCE — the missing-addressing-key twin of the element-VALUE-presence gate above ======
        // The value-presence gate (FLELEM-REJ-NULL*) catches a missing element VALUE; this catches a missing addressing
        // KEY/INDEX. A dict Add/Remove coerces req.Key into / against the entry (ApplyDictVerb -> Coerce(req.Key!, kType));
        // a list SetAtIndex parses req.Key as the index (ApplyListVerb -> int.Parse(req.Key!)). A MISSING key/index used
        // to slip pre-flight — VerbLegality required a key only for Set-on-dict, NOT for Add/SetAtIndex/Remove — and threw
        // UNNAMED at apply (Coerce(null) / int.Parse(null) -> the generic "internal failure" misdirection, a Q3 accept-
        // then-throw). VerbLegality now requires the key/index up front, by construction (verb x cardinality, no per-type
        // list). It is PRESENCE only — the key VALUE-shape (coercible-to-KeyType / parseable-as-int) stays the deferred
        // surface ValueLegality step-4a names. The reachability is the same the value-presence twin proved: `key` is an
        // optional string? param (WriteTools set_field / BulkOp), so ToolCallShim's required-param gate never blocks it.

        // ---------- KEYIDX-REJ-DICTADD: a dict Add with NO key refuses at PRE-FLIGHT (Class.SkillWeights=Dictionary<Skill,Byte>) ----------
        // A VALID value (Byte "5") is supplied so ONLY the missing key differs — isolates key-presence from value-presence.
        // RED before the gate: VerbLegality's Add arm returned null for any list/dict -> accepted, then Coerce(null,Skill) threw at apply.
        bool keyIdxRejDictAddOk;
        {
            var req = new WriteRequest { RecordType = "Class", Path = new[] { "SkillWeights" }, Verb = "Add", Key = null, Value = "5" };
            var reject = rulebook.Validate(req);
            keyIdxRejDictAddOk = reject is not null && reject.Contains("requires a key", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   KEYIDX-REJ-DICTADD missing dict key  : {(keyIdxRejDictAddOk ? "PASS — a dict Add with no key is refused at pre-flight (not accepted-then-thrown at apply)" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- KEYIDX-REJ-DICTREMOVE: a dict Remove with NO key refuses at PRE-FLIGHT (it identifies the entry BY key) ----------
        // RED before the gate: VerbLegality's Remove arm returned null for list/dict -> accepted, then Coerce(null,Skill) threw at apply.
        bool keyIdxRejDictRemoveOk;
        {
            var req = new WriteRequest { RecordType = "Class", Path = new[] { "SkillWeights" }, Verb = "Remove", Key = null };
            var reject = rulebook.Validate(req);
            keyIdxRejDictRemoveOk = reject is not null && reject.Contains("requires a key", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   KEYIDX-REJ-DICTREMOVE missing dict key: {(keyIdxRejDictRemoveOk ? "PASS — a dict Remove with no key is refused at pre-flight" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- KEYIDX-REJ-SETIDX: a list SetAtIndex with NO index refuses at PRE-FLIGHT (Race.MovementTypeNames=List<String>) ----------
        // A VALID value is supplied so ONLY the missing index differs. RED before the gate: VerbLegality's SetAtIndex arm
        // returned null for any list -> accepted, then int.Parse(null) threw ArgumentNullException at apply.
        bool keyIdxRejSetIdxOk;
        {
            var req = new WriteRequest { RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "SetAtIndex", Key = null, Value = "MT_Walk" };
            var reject = rulebook.Validate(req);
            keyIdxRejSetIdxOk = reject is not null && reject.Contains("requires an index", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   KEYIDX-REJ-SETIDX missing list index : {(keyIdxRejSetIdxOk ? "PASS — a list SetAtIndex with no index is refused at pre-flight" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- KEYIDX-OK-LISTREMOVE: a keyless list Remove + a value is STILL accepted (no over-reject) ----------
        // The gate is DICT-only for Remove: a list Remove is by-index-OR-by-value (ApplyListVerb), so a null key legally
        // falls back to remove-by-value. Proves the dict-scoping doesn't over-reach to lists. Accepted before AND after.
        bool keyIdxOkListRemoveOk;
        {
            var req = new WriteRequest { RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "Remove", Key = null, Value = "MT_Walk" };
            var reject = rulebook.Validate(req);
            keyIdxOkListRemoveOk = reject is null;
            Console.WriteLine($"   KEYIDX-OK-LISTREMOVE keyless list rm : {(keyIdxOkListRemoveOk ? "PASS — a keyless list Remove (by value) is NOT over-rejected" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- KEYIDX-REJ-SETIDX-E2E: a keyless list SetAtIndex refuses end-to-end with NO file written (gate, not apply throw) ----------
        // The "no file written" half (RejectArm): the REAL create+apply path refuses a no-index LinkTo SetAtIndex and
        // leaves no patch — the PRE-FLIGHT message ('requires an index'), not the apply int.Parse(null) throw, proving the gate.
        bool keyIdxRejSetIdxE2eOk = RejectArm("KEYIDX-REJ-SETIDX-E2E no index    ", tmpDir, "KeyIdxNoIdx", mPath, rulebook,
            new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HcNcKiTopic", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcKiL1", ParentRef = "HcNcKiTopic",
                    Edits = new[] { new WriteRequest { RecordType = "DialogResponses", Path = new[] { "LinkTo" }, Verb = "SetAtIndex", Key = null, Value = "012345:Skyrim.esm" } } },
            },
            msg => msg.Contains("requires an index", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine();
        bool pass = fixturesOk && oneshotOk && multiOk && intoTopicOk && intoCellOk
                    && rejNoParentOk && rejBadParentOk && rejAmbigOk && rejFwdSibOk && extendOk
                    && sibrefOk && sibRejFwdOk && sibRejNonflOk && sibRejListOk && sibRejDictOk && sibRejApplyOk
                    && flElemRejGateOk && flElemRejAddOk && flElemNullClearOk && flElemOkE2eOk && flElemRejE2eOk
                    && flElemRejNullAddOk && flElemRejNullAddPlainOk && flElemRejNullSetIdxOk && flElemRejNullAddE2eOk
                    && keyIdxRejDictAddOk && keyIdxRejDictRemoveOk && keyIdxRejSetIdxOk && keyIdxOkListRemoveOk && keyIdxRejSetIdxE2eOk;
        Console.WriteLine($"=== nested-create-guard: {(pass ? "PASS" : "FAIL")} ===");
        try { Directory.Delete(tmpDir, recursive: true); } catch { }
        return pass ? 0 : 1;
    }

    /// <summary>Drive a create expected to REFUSE (extend=false, fresh path): assert Success=false, NO file written,
    /// and the error matches <paramref name="msgOk"/> (the same shape as the manual proof's RejectCheck).</summary>
    static bool RejectArm(string banner, string tmpDir, string tag, string mPath, CorpusRulebook rulebook,
        WritePatchBuilder.CreateSpec[] specs, Func<string, bool> msgOk)
    {
        string pPath = Path.Combine(tmpDir, $"HcNcRej{tag}.esp");
        bool refused; string? error;
        using (var r = LoadOrderResolver.Build(new[] { mPath }))
        {
            var o = WritePatchBuilder.CreateRecords(r, rulebook, specs, pPath, extend: false);
            refused = !o.Success; error = o.Error;
        }
        bool noFile = !File.Exists(pPath);
        bool named = error is not null && msgOk(error);
        bool ok = refused && noFile && named;
        Console.WriteLine($"   {banner}: {(ok ? "PASS — refused by name, no file written" : $"FAIL — refused={refused} noFile={noFile} named={named} err=[{error}]")}");
        return ok;
    }

    /// <summary>Re-open the written patch and list a new topic's (by EditorID) child INFO FormKeys.</summary>
    static List<FormKey>? TopicResponses(string patchPath, string topicEditorId)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            var t = ov.DialogTopics.FirstOrDefault(x => x.EditorID == topicEditorId);
            return t?.Responses.Select(x => x.FormKey).ToList();
        }
        catch { return null; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>Re-open the written patch and list a topic's (by FormKey) child INFO FormKeys.</summary>
    static List<FormKey>? TopicResponses(string patchPath, FormKey topicFk)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            var t = ov.DialogTopics.FirstOrDefault(x => x.FormKey == topicFk);
            return t?.Responses.Select(x => x.FormKey).ToList();
        }
        catch { return null; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>Re-open the written patch and read a created INFO's Prompt (the field-edit check).</summary>
    static string? InfoPrompt(string patchPath, FormKey infoFk)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            foreach (var t in ov.DialogTopics)
                foreach (var info in t.Responses)
                    if (info.FormKey == infoFk) return info.Prompt?.String;
            return null;
        }
        catch { return null; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>Re-open the written patch and list a cell's (by FormKey) Persistent placed-ref FormKeys.</summary>
    static List<FormKey>? CellPersistent(string patchPath, FormKey cellFk)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            foreach (var block in ov.Cells)
                foreach (var sub in block.SubBlocks)
                    foreach (var c in sub.Cells)
                        if (c.FormKey == cellFk) return c.Persistent.Select(x => x.FormKey).ToList();
            return null;
        }
        catch { return null; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>Re-open the written patch and read a created INFO's Topic back-link FormKey (the @editorid sibling-ref
    /// arm). FormKey.Null if unset.</summary>
    static FormKey? InfoTopic(string patchPath, FormKey infoFk) => InfoFormLink(patchPath, infoFk, i => i.Topic.FormKey);

    /// <summary>Re-open the written patch and read a created INFO's PreviousDialog (PNAM) FormKey. FormKey.Null if unset.</summary>
    static FormKey? InfoPreviousDialog(string patchPath, FormKey infoFk) => InfoFormLink(patchPath, infoFk, i => i.PreviousDialog.FormKey);

    /// <summary>Re-open the written patch, find a created INFO by FormKey (under any topic's Responses), and project a
    /// FormLink field off it — shared by the Topic / PreviousDialog readers.</summary>
    static FormKey? InfoFormLink(string patchPath, FormKey infoFk, Func<IDialogResponsesGetter, FormKey> select)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            foreach (var t in ov.DialogTopics)
                foreach (var info in t.Responses)
                    if (info.FormKey == infoFk) return select(info);
            return null;
        }
        catch { return null; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>Re-open the written patch and list a created INFO's LinkTo (TCLT) element FormKeys — the valid
    /// formlink-ELEMENT round-trip check (FLELEM-OK-E2E). Null if the INFO isn't found.</summary>
    static List<FormKey>? InfoLinkTo(string patchPath, FormKey infoFk)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            foreach (var t in ov.DialogTopics)
                foreach (var info in t.Responses)
                    if (info.FormKey == infoFk) return info.LinkTo.Select(x => x.FormKey).ToList();
            return null;
        }
        catch { return null; }
        finally { (ov as IDisposable)?.Dispose(); }
    }
}

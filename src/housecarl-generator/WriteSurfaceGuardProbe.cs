using System.Reflection;
using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD (standing CI instrument, self-contained) for the REST of the 2.0 S1 write surface —
/// <c>housecarl_create</c>, <c>housecarl_remove</c>, <c>housecarl_forward</c> and the migrated
/// <c>housecarl_write_seq</c> (tool-surface-2.0 W3 PR 2; SPEC §2.2 ACT, §5.1/§5.2, §6.1). Sibling of
/// <c>apply-guard</c>, same posture: the REAL end-to-end tool path — a synthetic MO2 instance in temp +
/// <see cref="LoadOrderService"/> + the tool methods themselves — so the wire readers, the LANE grammar, the
/// alias-visible vocabulary and the engines are exercised exactly as a caller hits them. ELEVEN arms, listed BY SUBJECT
/// — neither declaration nor run order, both of which live in RunGuard, where the off-order/in-place pair is
/// deliberately last because it rewrites a fixture file the others read as a known winner:
/// <list type="number">
/// <item><b>create grammar</b> — one record is a set of one, the nested one-shot (a same-call sibling parent +
/// an '@editorid' link value), the @file spelling, and the strict element reader's NAMED refusals with the
/// corrections for the members a create cannot have.</item>
/// <item><b>LANE</b> — the destinations are exclusive and a dropped one is refused BY NAME on every tool;
/// removal (which creates no artifact) refuses a call that names NO lane; in_place is the file's NAME with its
/// consent handshake.</item>
/// <item><b>remove, plural</b> — the recovered engine capability: many records dropped in ONE re-serialize, and
/// all-or-nothing when one target isn't carried (NOTHING removed).</item>
/// <item><b>forward</b> — source= (the renamed pole) decides the content, the prior winner is named, dry_run
/// writes nothing, and a non-active source is refused by name rather than read as "doesn't define it".</item>
/// <item><b>TRANSPORT</b> — format=json is valid JSON carrying the same data, a REFUSAL is a document too, and
/// every response carries the §2.1.1 epoch — including write_seq, whose ABSENT epoch is stated as a fact with
/// its reason rather than left as a missing field.</item>
/// <item><b>forward OFF-ORDER</b> (W3 PR 2b) — source= resolves a plugin the ACTIVE order does not contain (a
/// DISABLED mod's file, on both lanes), the render + json state WHICH copy on disk was read and that the epoch
/// does not cover it, the overlay is released (the file stays movable), and the four refusals stay named:
/// ambiguous filename, a file that doesn't define the record, a record whose ORIGIN plugin isn't active, and a
/// self-forward caught by FILE IDENTITY when source= is a path to the artifact being written.</item>
/// <item><b>the CopyFrom view arm</b> (#317) — a pre-fetched off-order body keyed to a source the ENGINE's own build
/// says is ACTIVE is ignored, so the arm reported is the arm taken.</item>
/// <item><b>nested parent hosting</b> (#300) — a nested create under an EXISTING load-order parent hosts the child
/// in the parent's DEFINING plugin's version: the winner does not become a master, the hosted record does not carry
/// a frozen copy of the winner's fields, and which plugin hosted it is reported.</item>
/// <item><b>child-group preservation</b> (#324) — a <c>forward</c> onto a record the destination ALREADY carries
/// replaces it by DROP-then-copy, and the drop used to take the record's child group with it (INFOs under a DIAL,
/// placed refs under a CELL) while reporting success. Both lanes, plus the reflected child-property set pinned so a
/// Mutagen bump that adds a container fails here rather than at a caller's write.</item>
/// <item><b>CopyFrom source paths</b> (#321) — the same ACTIVE-copy-by-path rule on the <c>CopyFrom</c> lane: a
/// <c>from_source=</c> path to the file the order serves is refused/resolved as IN-ORDER and named by its plugin
/// name, the copy still lands from that plugin's own version, and a path to a same-NAMED different file keeps the
/// off-order lane.</item>
/// <item><b>the PR #313 review folds</b> — a <c>source=</c> PATH that names the file the order ACTUALLY LOADS is
/// in-order (so the already-the-winner flag survives, and the epoch is not disclaimed) while a path to a
/// same-NAMED different file stays off-order; a record ORIGINATING in the artifact being written forwards fine
/// (a plugin is never its own master) while any other inactive origin is still refused; a RENAMED off-order copy
/// is refused with the real cause; and the patch lane's missing-master refusal NAMES the plugin.</item>
/// </list>
///
/// Run: <c>dotnet run --project src/housecarl-generator write-surface-guard</c>
/// </summary>
public static class WriteSurfaceGuardProbe
{
    static int _pass, _fail;
    static void Check(string label, bool ok, string? got = null)
    {
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        if (!ok && got is not null) Console.WriteLine($"          got: {Trim(got)}");
        if (ok) _pass++; else _fail++;
    }
    static string Trim(string s) => s.Length <= 400 ? s.Replace("\n", " | ") : s[..400].Replace("\n", " | ") + " …";

    /// <summary>The pre-flight gate, for arms whose subject is what the GATE says, with no lane or file involved.
    /// <para/>
    /// It loads from whatever <c>CorpusRulebook.CorpusPath</c> points at when the first such arm runs:
    /// <see cref="Fixture.Build"/> repoints it at a freshly generated corpus ONLY when the ambient one fails to load,
    /// so an ambient <c>generated/corpus.json</c> is what these arms read on a normal run. That fails in the safe
    /// direction — a stale ambient corpus turns these arms red rather than green — but it is worth stating plainly,
    /// because these arms are #335's acceptance evidence and a reader should know which corpus answered them.</para></summary>
    static CorpusRulebook Rules => _rules ??= CorpusRulebook.Load();
    static CorpusRulebook? _rules;

    /// <summary>The message <paramref name="act"/> threw, or null if it completed — so an arm can assert on a
    /// refusal's WORDS and on the accepted path with the same shape.</summary>
    static string? Throws(Action act)
    {
        try { act(); return null; }
        catch (Exception ex) { return ex.Message; }
    }

    static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    public static int RunGuard(string[] args)
    {
        _pass = _fail = 0;
        Console.WriteLine("################  REGRESSION GUARD — create / remove / forward / write_seq (the 2.0 S1 write surface, PR 2)  ################");
        Console.WriteLine();

        var root = Path.Combine(Path.GetTempPath(), "hc_write2_guard_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // `using`, not a trailing Dispose(): an arm that throws lands in the catch below, and a bare Dispose()
            // there would be skipped — leaking the service and its overlays, which then makes the finally's
            // Directory.Delete fail silently (apply-guard's own scar).
            using var fx = Fixture.Build(Path.Combine(root, "fx"));
            CreateGrammarArm(fx, root);
            LaneArm(fx);
            RemovePluralArm(fx);
            ForwardArm(fx);
            TransportArm(fx);
            CopyFromViewArm(root);
            CopySourcePathArm(fx, root);
            NestedParentHostArm(fx);
            // Before the in-place arms: it reads the replacer as a known winner. It also WRITES — two new patch
            // mod folders (W2Twin*, W2TwinApply) carrying overrides of SubjectFid and MasterOnlyFid, then removes
            // records from W2TwinFwd.esp. Those patches are never ticked into plugins.txt, so they stay out of the
            // active order and cannot move a later arm's winner; an arm added after this one should still know the
            // folders exist.
            TwinParityArm(fx, root);
            // #324's arm also writes into the replacer IN PLACE (its second half is that lane), so it sits with the
            // in-place arms below rather than among the read-the-fixture ones. It rewrites the replacer TWICE and
            // changes two of its records: the TOPIC (W2Topic → "Master Topic") and the CELL (W2Cell → "Master Cell",
            // keeping W2WinnerRef). No later arm reads either — but both are three-way fixtures a future arm would
            // reach for, so an arm added after this one must read them as the MASTER's version, not the winner's.
            // The two below change its WEAPON, which earlier arms do read.
            ForwardChildGroupArm(fx);
            // OffOrderForwardArm and ReviewFoldArm go LAST: they forward into the replacer IN PLACE, which rewrites a
            // fixture file every earlier arm reads as a known winner. Ordering them after keeps every other arm
            // reading the fixture it was written against.
            OffOrderForwardArm(fx);
            ReviewFoldArm(fx, root);

            Console.WriteLine();
            Console.WriteLine($"=== write-surface-guard: {_pass} passed, {_fail} failed -> {(_fail == 0 ? "PASS" : "FAIL")} ===");
            return _fail == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   FAIL (unexpected): {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return 1;
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    // ================= the shared synthetic order =================

    /// <summary>Master + replacer. The replacer WINS the subject weapon at a DIFFERENT damage, so a forward from
    /// the master genuinely changes the content (and "forwarded the master's version" is distinguishable from
    /// "copied the winner"). The replacer is also the in-place target: it owns the records it overrides.</summary>
    sealed class Fixture : IDisposable
    {
        public required LoadOrderService Svc { get; init; }
        public required string SubjectFid { get; init; }     // the weapon: master Damage 10, replacer Damage 99
        public required FormKey SubjectKey { get; init; }
        public required string ModsDir { get; init; }
        public required string MasterName { get; init; }
        public required string ReplacerName { get; init; }
        /// <summary>W3 PR 2b — a plugin in a DISABLED mod folder: NOT in loadorder/plugins.txt, so it is off-order,
        /// and it overrides the subject at a THIRD damage so "read the disabled copy" is distinguishable from both
        /// "read the master" (10) and "read the winner" (99).</summary>
        public required string OffName { get; init; }
        public required string OffPath { get; init; }
        public required string OffFolder { get; init; }
        /// <summary>A record the OFF-ORDER plugin ORIGINATES — forwarding it would need that plugin as a master.</summary>
        public required string OffOwnFid { get; init; }
        /// <summary>A record ONLY the master carries (the replacer never touches it) — #321's discriminator.</summary>
        public required string MasterOnlyFid { get; init; }
        /// <summary>#300: a nestable parent DEFINED by the master and WON by the replacer, at different content.</summary>
        public required string TopicFid { get; init; }
        public required FormKey TopicKey { get; init; }
        public required string CellFid { get; init; }
        public required FormKey CellKey { get; init; }
        public required string WinnerLineFid { get; init; }
        /// <summary>One filename provided by TWO disabled mod folders — the ambiguity refusal's fixture.</summary>
        public required string AmbName { get; init; }
        /// <summary>A DISABLED plugin whose forwarded body links into ANOTHER disabled plugin — the missing-master
        /// refusal's fixture.</summary>
        public required string ChainName { get; init; }

        public void Dispose() => Svc.Dispose();

        public static Fixture Build(string dir)
        {
            string instance = Path.Combine(dir, "instance");
            string profiles = Path.Combine(instance, "profiles", "Default");
            string mods = Path.Combine(instance, "mods");
            Directory.CreateDirectory(profiles); Directory.CreateDirectory(mods);
            Directory.CreateDirectory(Path.Combine(dir, "game", "Data"));
            File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
                "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                + Path.Combine(dir, "game").Replace(@"\", @"\\") + ")\r\n");

            var mKey = new ModKey("HcW2Master", ModType.Master);
            var rKey = new ModKey("HcW2Repl", ModType.Plugin);
            var masterPath = Path.Combine(mods, "W2Master", mKey.FileName.String);
            var replPath = Path.Combine(mods, "W2Repl", rKey.FileName.String);
            Directory.CreateDirectory(Path.GetDirectoryName(masterPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(replPath)!);

            var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
            var subject = m.Weapons.AddNew();
            subject.EditorID = "W2Subject";
            subject.Name = "Master Sword";
            subject.BasicStats = new WeaponBasicStats { Damage = 10 };
            // #321's discriminator: a record the master defines and the replacer does NOT touch, so a CopyFrom naming
            // the REPLACER has a source that is genuinely in the order and genuinely without a version to copy — the
            // one shape whose refusal wording differs between the in-order and off-order arms.
            var masterOnly = m.Weapons.AddNew();
            masterOnly.EditorID = "W2MasterOnly";
            masterOnly.BasicStats = new WeaponBasicStats { Damage = 3 };

            // #300's fixture: a NESTABLE parent the master DEFINES and the replacer WINS, at distinguishable content.
            // A dialogue topic rather than a cell purely because a cell needs its block/subblock tree built by hand —
            // the parent-resolution code under test is one branch for every nested create, and the topic exercises it
            // with the same three-way distinction (definer's value, winner's value, which plugin becomes a master).
            var topic = m.DialogTopics.AddNew();
            topic.EditorID = "W2Topic";
            topic.Name = "Master Topic";
            // …carrying a CHILD of its own, so the arm can answer what the host override does to the parent's child
            // list — not just its fields. The winner adds a second child below.
            topic.Responses.Add(new DialogResponses(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "W2MasterLine" });

            // #324's CELL case, built by hand — which is exactly why the comment above chose a topic for #300. A cell
            // is NOT in a flat group, so a forward onto one resolves through a ModContext and rebuilds the
            // block/subblock chain: structurally different code from the topic's path, and the case the changelog
            // names first (a placed reference under a forwarded cell). Same three-way shape as the topic: the master
            // defines it with a ref of its own, the replacer wins it with a different name and a second ref.
            var cell = new Cell(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "W2Cell", Name = "Master Cell" };
            cell.Persistent.Add(new PlacedObject(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "W2MasterRef" });
            var cSub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
            cSub.Cells.Add(cell);
            var cBlock = new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock };
            cBlock.SubBlocks.Add(cSub);
            m.Cells.Records.Add(cBlock);

            m.BeginWrite.ToPath(masterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            var mCache = m.ToImmutableLinkCache();   // the nested cell override below reconstructs its parent chain from it

            var r = new SkyrimMod(rKey, SkyrimRelease.SkyrimSE);
            var rw = (IWeapon)WriteEngine.GenericGetOrAddAsOverride(r, subject);
            rw.Name = "Winner Sword";
            rw.BasicStats = new WeaponBasicStats { Damage = 99 };
            // #300 — the winner's version both CHANGES a field and LINKS to a record the replacer owns. The link is
            // what makes the master-growth check real: a copied body drags its referents' plugins into the header, so
            // hosting from the winner would make the replacer a master of a patch that only added a child. Without it
            // the masters assertion passes either way (a DIAL's own FormKey belongs to the master) — which is exactly
            // how the reported CELL case cost a master: the winner's lighting fields linked into its own plugin.
            var winnerQuest = r.Quests.AddNew();
            winnerQuest.EditorID = "W2WinnerQuest";
            var topicOverride = (IDialogTopic)WriteEngine.GenericGetOrAddAsOverride(r, topic);
            topicOverride.Name = "Winner Topic";
            topicOverride.Quest.SetTo(winnerQuest.FormKey);
            var winnerLine = new DialogResponses(r.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "W2WinnerLine" };
            topicOverride.Responses.Add(winnerLine);
            var cellOverride = (ICell)WriteEngine.GenericGetOrAddAsOverride(r, cell, mCache);
            cellOverride.Name = "Winner Cell";
            cellOverride.Persistent.Add(new PlacedObject(r.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "W2WinnerRef" });
            r.BeginWrite.ToPath(replPath).WithLoadOrder(new ISkyrimModGetter[] { m }).Write();

            // --- W3 PR 2b: the OFF-ORDER source. A DISABLED mod folder (modlist '-W2Off'), absent from loadorder.txt
            //     and plugins.txt, overriding the subject at a THIRD damage + originating a record of its own.
            var oKey = new ModKey("HcW2Off", ModType.Plugin);
            var offPath = Path.Combine(mods, "W2Off", oKey.FileName.String);
            Directory.CreateDirectory(Path.GetDirectoryName(offPath)!);
            var o = new SkyrimMod(oKey, SkyrimRelease.SkyrimSE);
            var ow = (IWeapon)WriteEngine.GenericGetOrAddAsOverride(o, subject);
            ow.Name = "Disabled Sword";
            ow.BasicStats = new WeaponBasicStats { Damage = 42 };
            var ownWeapon = o.Weapons.AddNew();
            ownWeapon.EditorID = "W2OffOwn";
            ownWeapon.BasicStats = new WeaponBasicStats { Damage = 7 };
            o.BeginWrite.ToPath(offPath).WithLoadOrder(new ISkyrimModGetter[] { m }).Write();

            // A DISABLED plugin whose body links into ANOTHER disabled plugin — the missing-master shape (PR #313
            // review [low]). Its override of the subject carries a keyword that lives in an inactive master, so the
            // record's own ORIGIN stays the ACTIVE master (the origin check must not pre-empt the serialize refusal).
            var depKey = new ModKey("HcW2OffDep", ModType.Master);
            var depPath = Path.Combine(mods, "W2OffDep", depKey.FileName.String);
            Directory.CreateDirectory(Path.GetDirectoryName(depPath)!);
            var dep = new SkyrimMod(depKey, SkyrimRelease.SkyrimSE);
            var depKw = dep.Keywords.AddNew();
            depKw.EditorID = "W2OffDepKw";
            dep.BeginWrite.ToPath(depPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            var cKey = new ModKey("HcW2OffChain", ModType.Plugin);
            var chainPath = Path.Combine(mods, "W2OffChain", cKey.FileName.String);
            Directory.CreateDirectory(Path.GetDirectoryName(chainPath)!);
            var c = new SkyrimMod(cKey, SkyrimRelease.SkyrimSE);
            var cw = (IWeapon)WriteEngine.GenericGetOrAddAsOverride(c, subject);
            cw.BasicStats = new WeaponBasicStats { Damage = 33 };
            (cw.Keywords ??= new()).Add(depKw);
            c.BeginWrite.ToPath(chainPath).WithLoadOrder(new ISkyrimModGetter[] { m, dep }).Write();

            // Two DISABLED folders providing ONE filename — the ambiguity refusal's fixture. Kept on its own name so
            // it cannot make the off-order SUCCESS arms ambiguous.
            var aKey = new ModKey("HcW2Amb", ModType.Plugin);
            foreach (var folder in new[] { "W2AmbA", "W2AmbB" })
            {
                var ap = Path.Combine(mods, folder, aKey.FileName.String);
                Directory.CreateDirectory(Path.GetDirectoryName(ap)!);
                var a = new SkyrimMod(aKey, SkyrimRelease.SkyrimSE);
                ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(a, subject)).BasicStats = new WeaponBasicStats { Damage = 1 };
                a.BeginWrite.ToPath(ap).WithLoadOrder(new ISkyrimModGetter[] { m }).Write();
            }

            // The chain/dep folders are listed DISABLED here. They worked before only because UnlistedModFolders picks
            // up folders modlist.txt never mentions — so the fixture was exercising the UNLISTED layer while its
            // comments claimed the DISABLED one. (An earlier str-replace meant to add them silently matched nothing;
            // the arms passed anyway, which is exactly how a fixture drifts from what it says it is.)
            File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + mKey.FileName + "\r\n" + rKey.FileName + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + mKey.FileName + "\r\n*" + rKey.FileName + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n-W2AmbB\r\n-W2AmbA\r\n-W2OffChain\r\n-W2OffDep\r\n-W2Off\r\n+W2Repl\r\n+W2Master\r\n");

            var genDir = Path.Combine(dir, "corpus-gen");
            try { _ = CorpusRulebook.LoadCorpus(); }
            catch { CorpusGenerator.GenerateAll(genDir, Path.Combine(dir, "corpus-ref")); CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json"); }

            var store = new UserConfigStore(Path.Combine(dir, "houseCARL.user.json"));
            var svc = LoadOrderService.WithInstance(instance, 0, store);
            svc.Stats();   // warm the lazy index once

            return new Fixture
            {
                Svc = svc,
                SubjectFid = $"{subject.FormKey.ID:X6}:{mKey.FileName}",
                SubjectKey = subject.FormKey,
                ModsDir = mods,
                MasterName = mKey.FileName.String,
                ReplacerName = rKey.FileName.String,
                OffName = oKey.FileName.String,
                OffPath = offPath,
                OffFolder = "W2Off",
                OffOwnFid = $"{ownWeapon.FormKey.ID:X6}:{oKey.FileName}",
                MasterOnlyFid = $"{masterOnly.FormKey.ID:X6}:{mKey.FileName}",
                TopicFid = $"{topic.FormKey.ID:X6}:{mKey.FileName}",
                TopicKey = topic.FormKey,
                CellFid = $"{cell.FormKey.ID:X6}:{mKey.FileName}",
                CellKey = cell.FormKey,
                WinnerLineFid = $"{winnerLine.FormKey.ID:X6}:{rKey.FileName}",
                AmbName = aKey.FileName.String,
                ChainName = cKey.FileName.String,
            };
        }
    }

    /// <summary>The written artifact's path, parsed out of the text render (the render is what a caller actually
    /// gets, so reading the path from it keeps the guard honest about the reported artifact).</summary>
    static string? ArtifactPathFrom(Fixture fx, string render)
    {
        if (!render.StartsWith("wrote ", StringComparison.Ordinal) && !render.StartsWith("extended ", StringComparison.Ordinal)) return null;
        var file = render[(render.IndexOf(' ') + 1)..];
        file = file[..file.IndexOf(' ')];
        var mod = render.Contains("mod folder: ", StringComparison.Ordinal)
            ? render[(render.IndexOf("mod folder: ", StringComparison.Ordinal) + 12)..].Split('\n')[0].Split("  ")[0].Trim()
            : null;
        return mod is null ? null : Path.Combine(fx.ModsDir, mod, file);
    }

    /// <summary>Pull the read-back call a truncated create render emits back apart into (source file, types) so the
    /// arm can RUN it. Returns (null, null) when the render carries no such call — which is itself the failure the
    /// caller reports, since the whole point of the notice is to name a call that works.</summary>
    static (string? file, string[]? types) ParseReadBackCall(string render)
    {
        const string marker = "housecarl_records source=\"";
        int at = render.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return (null, null);
        var tail = render[(at + marker.Length)..];
        int quote = tail.IndexOf('"');
        int open = tail.IndexOf("types=[", StringComparison.Ordinal);
        int close = open < 0 ? -1 : tail.IndexOf(']', open);
        if (quote < 0 || open < 0 || close < 0) return (null, null);
        var types = tail[(open + 7)..close].Split(',')
                        .Select(t => t.Trim().Trim('"')).Where(t => t.Length > 0).ToArray();
        return (tail[..quote], types.Length > 0 ? types : null);
    }

    /// <summary>Every EditorID the written plugin carries (flat + nested), for the created/removed assertions.</summary>
    static List<string> EditorIdsIn(string espPath)
    {
        var found = new List<string>();
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE);
            foreach (var rec in ov.EnumerateMajorRecords())
                if (rec.EditorID is { Length: > 0 } e) found.Add(e);
        }
        catch { /* the caller asserts on the contents, and an unreadable file fails those */ }
        finally { (ov as IDisposable)?.Dispose(); }
        return found;
    }

    /// <summary>#317 — the CopyFrom lane decides its off-order arm from the ENGINE's view, never from the presence of
    /// a pre-fetched body. The race the issue described cannot be synthesised (a write pins one resolver instance and
    /// its name table never moves), but the INVARIANT can be, deterministically: hand the engine a pre-fetched body
    /// keyed to an edit whose source is ACTIVE — exactly what a drifted pre-locate would produce — and the in-order
    /// arm must still win. Self-contained order, driven through the core so the dictionary can be hand-built (the
    /// service only ever fills it for genuinely off-order sources, which is why nothing else can reach this).
    /// <para>Three distinguishable values, so no outcome is ambiguous: the copy source (master, 10) is neither the
    /// winner the patch starts from (replacer, 20) nor the decoy body (77). 20 would mean the copy never happened; 77
    /// would mean the stale dictionary won.</para></summary>
    static void CopyFromViewArm(string root)
    {
        var dir = Path.Combine(root, "cfview");
        Directory.CreateDirectory(dir);

        var mKey = new ModKey("HcCfMaster", ModType.Master);
        var rKey = new ModKey("HcCfRepl", ModType.Plugin);
        var mPath = Path.Combine(dir, mKey.FileName.String);
        var rPath = Path.Combine(dir, rKey.FileName.String);

        var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
        var subject = m.Weapons.AddNew();
        subject.EditorID = "CfSubject";
        subject.BasicStats = new WeaponBasicStats { Damage = 10 };
        m.BeginWrite.ToPath(mPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var r = new SkyrimMod(rKey, SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(r, subject)).BasicStats = new WeaponBasicStats { Damage = 20 };
        r.BeginWrite.ToPath(rPath).WithLoadOrder(new ISkyrimModGetter[] { m }).Write();

        // The decoy carries the MASTER's ModKey, because that is the source the edit names — a pre-locate that
        // resolved it off-order would have fetched exactly this shape, at whatever the disk copy said.
        var decoy = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(decoy, subject)).BasicStats = new WeaponBasicStats { Damage = 77 };
        IMajorRecordGetter decoyBody = decoy.Weapons.First();

        using var resolver = LoadOrderResolver.Build(new[] { mPath, rPath });
        var rulebook = CorpusRulebook.Load();
        var edit = new WritePatchBuilder.PatchEdit
        {
            Target = subject.FormKey,
            Path = new[] { "BasicStats", "Damage" },
            Verb = "CopyFrom",
            FromPlugin = mKey.FileName.String,          // ACTIVE — so the dictionary entry must be ignored
        };
        var outPath = Path.Combine(dir, "HcCfPatch.esp");
        var o = WritePatchBuilder.Apply(resolver, rulebook, new[] { edit }, outPath, extend: false, fullReadback: false,
            copyFromSources: new Dictionary<WritePatchBuilder.PatchEdit, IMajorRecordGetter> { [edit] = decoyBody });
        var back = o.Success ? DamageIn(outPath, subject.FormKey) : null;

        Check("CopyFrom: a pre-fetched body keyed to an ACTIVE source is IGNORED — the in-order arm resolves it (#317)",
            o.Success && back == 10,
            $"success={o.Success} damage={back} (want 10 = the named source; 20 = no copy happened; 77 = the stale " +
            $"pre-fetched body won) err={o.Error}");
    }

    static ushort? DamageIn(string espPath, FormKey fk)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE);
            return ov.Weapons.FirstOrDefault(w => w.FormKey == fk)?.BasicStats?.Damage;
        }
        catch { return null; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    // ================= ARM 1 — the create grammar =================
    static void CreateGrammarArm(Fixture fx, string root)
    {
        Console.WriteLine("── ARM 1: the records grammar — a set of one, the nested one-shot, @file, and the strict reader ──");

        // ONE record is a set of one — the whole reason the scalar create tool dissolves.
        var one = CreateTools.Create(fx.Svc,
            records: Json("""[{"record_type":"Keyword","editorid":"W2KwOne"}]"""), patch: "W2One");
        var onePath = ArtifactPathFrom(fx, one);
        Check("one record is a set of one: a single Keyword lands in a new patch with its editorid",
            onePath is not null && EditorIdsIn(onePath).Contains("W2KwOne"), one);

        // MANY records in one call, with ops= setting fields (the op shape minus formid).
        var many = CreateTools.Create(fx.Svc,
            records: Json("""
                [{"record_type":"Keyword","editorid":"W2KwA"},
                 {"record_type":"Weapon","editorid":"W2WeapA","ops":[{"field_path":"Name","value":"Guard Blade"},
                                                                     {"field_path":"BasicStats.Damage","value":"33"}]}]
                """), patch: "W2Many");
        var manyPath = ArtifactPathFrom(fx, many);
        var manyIds = manyPath is null ? new List<string>() : EditorIdsIn(manyPath);
        Check("many records in ONE call, with ops= setting the new record's fields",
            manyPath is not null && manyIds.Contains("W2KwA") && manyIds.Contains("W2WeapA")
            && many.Contains("Guard Blade", StringComparison.Ordinal), many);

        // The NESTED one-shot: a child whose parent= names an EARLIER sibling's editorid, plus an '@editorid'
        // FormLink value pointing at that same sibling — the two same-call reference forms, in one call.
        var nested = CreateTools.Create(fx.Svc,
            records: Json("""
                [{"record_type":"DialogTopic","editorid":"W2Topic"},
                 {"record_type":"DialogResponses","editorid":"W2Topic_L1","parent":"W2Topic",
                  "ops":[{"field_path":"Topic","value":"@W2Topic"}]}]
                """), patch: "W2Nested");
        var nestedPath = ArtifactPathFrom(fx, nested);
        var nestedIds = nestedPath is null ? new List<string>() : EditorIdsIn(nestedPath);
        Check("the nested one-shot: a child parented on a same-call sibling, with an '@editorid' link value",
            nestedPath is not null && nestedIds.Contains("W2Topic") && nestedIds.Contains("W2Topic_L1"), nested);

        // The @file spelling — the same array from disk (SPEC §5.1's one list-input convention).
        var manifest = Path.Combine(root, "records.json");
        File.WriteAllText(manifest, """[{"record_type":"Keyword","editorid":"W2KwFromFile"}]""");
        var viaFile = CreateTools.Create(fx.Svc, records: Json($"\"@{manifest.Replace("\\", "\\\\")}\""), patch: "W2File");
        var viaFilePath = ArtifactPathFrom(fx, viaFile);
        Check("records=\"@<path>\" reads the SAME array from a JSON manifest on disk",
            viaFilePath is not null && EditorIdsIn(viaFilePath).Contains("W2KwFromFile"), viaFile);

        // A MIXED inline/@file array has no meaning — refused, never half-honored.
        var mixed = CreateTools.Create(fx.Svc,
            records: Json($$"""["@{{manifest.Replace("\\", "\\\\")}}", {"record_type":"Keyword","editorid":"W2Mixed"}]"""));
        Check("a MIXED inline/@file records array is refused by name, never half-honored",
            mixed.StartsWith("error:") && mixed.Contains("cannot be mixed with inline elements"), mixed);

        // records=[] is a SUPPLIED parameter, not an absent one (the accepted-and-dropped class this surface closes).
        var empty = CreateTools.Create(fx.Svc, records: Json("[]"));
        Check("records=[] is refused by name, not read as absent",
            empty.StartsWith("error:") && empty.Contains("empty array"), empty);

        // No records= at all — its own refusal, spelling the parameter.
        var none = CreateTools.Create(fx.Svc, patch: "W2None");
        Check("no records= at all: refused naming the parameter and the @file alternative",
            none.StartsWith("error:") && none.Contains("records=[{record_type"), none);

        // The 1.x element vocabulary: `operations` inside a record spec is refused BY NAME with the rename.
        var oldOps = CreateTools.Create(fx.Svc,
            records: Json("""[{"record_type":"Keyword","editorid":"W2Old","operations":[{"field_path":"Name","value":"x"}]}]"""));
        Check("an element member the shape doesn't declare (operations) is refused BY NAME with the ops= correction",
            oldOps.StartsWith("error:") && oldOps.Contains("operations") && oldOps.Contains("ops"), oldOps);

        // A create op cannot carry formid= — and the correction says WHY (the id is allocated), not just "unknown".
        var opFormid = CreateTools.Create(fx.Svc,
            records: Json($$"""[{"record_type":"Keyword","editorid":"W2Bad","ops":[{"formid":"{{fx.SubjectFid}}","field_path":"Name","value":"x"}]}]"""));
        Check("formid= inside a create op is refused BY NAME, corrected with why a create has none",
            opFormid.StartsWith("error:") && opFormid.Contains("formid")
            && opFormid.Contains("auto-allocated", StringComparison.Ordinal), opFormid);

        // A copy pole inside a create op: refused with the "create first, then apply" route (not a bare unknown).
        var opCopy = CreateTools.Create(fx.Svc,
            records: Json("""[{"record_type":"Keyword","editorid":"W2Bad2","ops":[{"field_path":"Name","from_source":"HcW2Master.esm"}]}]"""));
        Check("from_source= inside a create op is refused BY NAME with the housecarl_apply route",
            opCopy.StartsWith("error:") && opCopy.Contains("from_source") && opCopy.Contains("housecarl_apply"), opCopy);

        // An engine-level problem names the caller's OWN spelling for the element — records[i], not record[i].
        var badType = CreateTools.Create(fx.Svc,
            records: Json("""[{"record_type":"Keyword","editorid":"W2Ok"},{"record_type":"NotARealType","editorid":"W2Nope"}]"""));
        Check("a per-record refusal names the caller's own spelling: records[1], never the 1.x record[1]",
            badType.StartsWith("error:") && badType.Contains("records[1]") && !badType.Contains("record[1]:"), badType);
    }

    // ================= ARM 2 — the LANE grammar =================
    static void LaneArm(Fixture fx)
    {
        Console.WriteLine("── ARM 2: LANE — exclusive destinations, refused BY NAME, and in_place as the file's name ──");

        var recs = Json("""[{"record_type":"Keyword","editorid":"W2LaneKw"}]""");

        var patchAndInto = CreateTools.Create(fx.Svc, records: recs, patch: "W2A", into: "W2B.esp");
        Check("create: patch= + into= is refused BY NAME (both lanes quoted), never silently ignoring one",
            patchAndInto.StartsWith("error:") && patchAndInto.Contains("patch='W2A'") && patchAndInto.Contains("into='W2B.esp'"), patchAndInto);

        var intoAndInPlace = CreateTools.Create(fx.Svc, records: recs, into: "W2B.esp", in_place: fx.ReplacerName);
        Check("create: into= + in_place= is refused BY NAME — they are different lanes",
            intoAndInPlace.StartsWith("error:") && intoAndInPlace.Contains("into=") && intoAndInPlace.Contains("in_place="), intoAndInPlace);

        var ackAlone = CreateTools.Create(fx.Svc, records: recs, patch: "W2A", acknowledge: true);
        Check("create: acknowledge= without in_place= is refused, not accepted-and-ignored",
            ackAlone.StartsWith("error:") && ackAlone.Contains("acknowledge="), ackAlone);

        var fwdBothLanes = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.MasterName,
            patch: "W2F", in_place: fx.ReplacerName);
        Check("forward: patch= + in_place= is refused BY NAME",
            fwdBothLanes.StartsWith("error:") && fwdBothLanes.Contains("patch='W2F'") && fwdBothLanes.Contains("in_place="), fwdBothLanes);

        // Removal creates no artifact, so naming NO lane is a real mistake — and the refusal spells BOTH lanes
        // rather than defaulting to one. (RED pre-fix: a tool that defaulted to a fresh patch here would write an
        // empty artifact and report success.)
        var noLane = RemoveTools.Remove(fx.Svc, formids: new[] { fx.SubjectFid });
        Check("remove: naming NO lane is refused, spelling both into= and in_place=",
            noLane.StartsWith("error:") && noLane.Contains("into=") && noLane.Contains("in_place="), noLane);

        var twoLanes = RemoveTools.Remove(fx.Svc, formids: new[] { fx.SubjectFid }, into: "W2B.esp", in_place: fx.ReplacerName);
        Check("remove: into= + in_place= is refused BY NAME",
            twoLanes.StartsWith("error:") && twoLanes.Contains("Name one"), twoLanes);

        // in_place is the FILE'S NAME, and the first touch of a plugin is a CONSENT prompt — not an error, not a
        // write. Every one of these is decided AFTER the service captured a build to resolve the target, so each
        // carries the §2.1.1 epoch (PR #310's lesson, and its round-1 finding: the consent prompt is the shape a
        // caller meets most often, so an unstamped one makes the contract false exactly there).
        // ORDER MATTERS: consent is persistent per plugin path, and only an acknowledge=true call records it — so
        // the two prompt arms run BEFORE the acknowledged write below, on the same plugin.
        var rmConsent = RemoveTools.Remove(fx.Svc, formids: new[] { fx.SubjectFid }, in_place: fx.ReplacerName);
        Check("remove in place: the FIRST touch returns the one-time CONSENT prompt, epoch-stamped",
            !rmConsent.StartsWith("error:") && rmConsent.Contains("acknowledge=true")
            && rmConsent.Contains("\nepoch=", StringComparison.Ordinal), rmConsent);

        var consent = CreateTools.Create(fx.Svc,
            records: Json("""[{"record_type":"Keyword","editorid":"W2InPlaceKw"}]"""), in_place: fx.ReplacerName);
        Check("create in place: the FIRST touch returns the one-time CONSENT prompt, not an error and not a write",
            !consent.StartsWith("error:") && consent.Contains("acknowledge=true"), consent);
        Check("the consent prompt carries the §2.1.1 epoch, like every other outcome",
            consent.Contains("\nepoch=", StringComparison.Ordinal), consent);

        // The forward lane's service-side refusal (a non-active in_place target) is the third site the same stamp
        // covers — a distinct observable, so all three lanes are pinned rather than one standing in for three.
        var fwdBadTarget = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.MasterName,
            in_place: "NotAPlugin.esp");
        Check("forward in place: a non-active target is refused post-capture, and the refusal carries its epoch",
            fwdBadTarget.StartsWith("error:") && fwdBadTarget.Contains("NotAPlugin.esp")
            && fwdBadTarget.Contains("\nepoch=", StringComparison.Ordinal), fwdBadTarget);

        var wrote = CreateTools.Create(fx.Svc,
            records: Json("""[{"record_type":"Keyword","editorid":"W2InPlaceKw"}]"""), in_place: fx.ReplacerName, acknowledge: true);
        Check("create in place: acknowledge=true writes into the ORIGINAL file, reported as the in-place lane",
            wrote.Contains("IN PLACE", StringComparison.Ordinal)
            && EditorIdsIn(Path.Combine(fx.ModsDir, "W2Repl", fx.ReplacerName)).Contains("W2InPlaceKw"), wrote);

        // The HAZARD itself reaches the caller (PR #337 review, finding 1). Before this, nothing in the generator
        // asserted the sentence at all: emptying one const would have stripped "houseCARL keeps nothing to undo
        // this" from all five in-place renders with the whole suite green — the incident's shape on the loudest
        // sentence the write surface has, and this change is what made it a single token. [MustState] pins what the
        // sentence must SAY; this pins that the render still says it.
        Check("create in place: the render states the rewrite hazard — the caller's own file, and no way back",
            wrote.Contains(WriteSentences.InPlaceRewritten, StringComparison.Ordinal), wrote);

        // The closing "keep going" line must teach THIS tool's spelling (PR #311 round-2 review [medium]): the 2.0
        // tools declare a single string in_place= and no target=, so the 1.x pair would send a caller to an
        // undeclared parameter plus a boolean-into-a-string. Asserted as a positive AND a negative — the positive
        // alone would still pass if the old sentence were merely appended.
        Check("the in-place follow-up hint teaches in_place=\"X.esp\", never the 1.x target= + in_place=true pair",
            wrote.Contains($"pass in_place=\"{fx.ReplacerName}\" again", StringComparison.Ordinal)
            && !wrote.Contains("target=", StringComparison.Ordinal), wrote);
    }

    // ================= ARM 3 — remove, plural =================
    static void RemovePluralArm(Fixture fx)
    {
        Console.WriteLine("── ARM 3: remove — the plural capability the 1.x surface could not reach ──");

        // Author three records into ONE patch, then drop TWO of them in ONE call.
        var made = CreateTools.Create(fx.Svc, patch: "W2Rm", records: Json("""
            [{"record_type":"Keyword","editorid":"W2RmA"},
             {"record_type":"Keyword","editorid":"W2RmB"},
             {"record_type":"Keyword","editorid":"W2RmC"}]
            """));
        var path = ArtifactPathFrom(fx, made);
        if (path is null) { Check("remove arm fixture: three records authored into one patch", false, made); return; }
        var file = Path.GetFileName(path);

        // The created FormIDs come out of the render — the caller's own handle on a record whose id is allocated.
        var ids = FormIdsFrom(made);
        Check("remove arm fixture: three records authored, their allocated FormIDs reported back",
            ids.Count == 3 && EditorIdsIn(path).Count(e => e.StartsWith("W2Rm", StringComparison.Ordinal)) == 3, made);
        if (ids.Count != 3) return;

        var plural = RemoveTools.Remove(fx.Svc, formids: new[] { ids[0], ids[1] }, into: file);
        var left = EditorIdsIn(path);
        Check("MANY records drop in ONE re-serialize (the recovered engine capability): 2 gone, the third stands",
            plural.StartsWith("removed 2 records", StringComparison.Ordinal)
            && !left.Contains("W2RmA") && !left.Contains("W2RmB") && left.Contains("W2RmC"), plural);

        // All-or-nothing: one target the patch doesn't carry refuses the WHOLE call — the survivor stays.
        var notCarried = RemoveTools.Remove(fx.Svc, formids: new[] { ids[2], fx.SubjectFid }, into: file);
        Check("all-or-nothing: one not-carried target refuses the whole call and NOTHING is removed",
            notCarried.StartsWith("error:") && notCarried.Contains("not carried by patch")
            && EditorIdsIn(path).Contains("W2RmC"), notCarried);

        var emptyList = RemoveTools.Remove(fx.Svc, formids: Array.Empty<string>(), into: file);
        Check("formids=[] is refused by name (a set of one is the minimum, not zero)",
            emptyList.StartsWith("error:") && emptyList.Contains("formids="), emptyList);
    }

    /// <summary>The allocated FormIDs out of a create render's per-record lines ("  Keyword 000800:Patch.esp  Edid").</summary>
    static List<string> FormIdsFrom(string render)
    {
        var ids = new List<string>();
        foreach (var line in render.Split('\n'))
        {
            var t = line.Trim();
            var parts = t.Split("  ", StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var head = parts[0].Split(' ');
            if (head.Length == 2 && head[1].Contains(':') && head[1].Contains(".es", StringComparison.OrdinalIgnoreCase))
                ids.Add(head[1]);
        }
        return ids;
    }

    // ================= ARM 4 — forward =================
    static void ForwardArm(Fixture fx)
    {
        Console.WriteLine("── ARM 4: forward — source= decides the content, and the winner it out-ranks is named ──");

        // The replacer WINS the subject at Damage 99; forwarding the MASTER's version must land Damage 10 — which
        // is what tells "copied source's body" apart from "copied the winner".
        var fwd = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.MasterName, patch: "W2Fwd");
        var fwdPath = ArtifactPathFrom(fx, fwd);
        Check("source= decides the content: the MASTER's version lands, not the load-order winner's",
            fwdPath is not null && DamageIn(fwdPath, fx.SubjectKey) == 10, fwd);
        Check("the render names the winner the forward will out-rank once enabled",
            fwd.Contains("out-ranks the current winner", StringComparison.Ordinal)
            && fwd.Contains(fx.ReplacerName, StringComparison.OrdinalIgnoreCase), fwd);

        // A forward whose version is ALREADY winning is reported redundant, never a silent no-op.
        var redundant = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.ReplacerName, patch: "W2FwdR");
        Check("forwarding the version that ALREADY wins is flagged redundant, never silently a no-op",
            redundant.Contains("already the load-order winner", StringComparison.OrdinalIgnoreCase), redundant);

        // dry_run runs the real pipeline and stops before disk.
        int before = Directory.GetDirectories(fx.ModsDir).Length;
        var dry = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.MasterName, patch: "W2FwdDry", dry_run: true);
        Check("dry_run=true: the DRY RUN render leads with nothing-written, and no mod folder is cut",
            dry.StartsWith("DRY RUN", StringComparison.Ordinal) && Directory.GetDirectories(fx.ModsDir).Length == before, dry);

        // A source found in NEITHER place is refused BY NAME — not silently read as "doesn't define the record".
        // (Pre-2b this arm read "a non-active source is refused": non-active is now RESOLVED, so what is left to
        // refuse is a name nothing on disk provides either — and the refusal must name both places searched.)
        var badSource = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: "NotInTheOrder.esp", patch: "W2FwdBad");
        Check("a source= in NEITHER the load order nor on disk is refused by name, naming both places searched",
            badSource.StartsWith("error:") && badSource.Contains("NotInTheOrder.esp")
            && badSource.Contains("not in the load order", StringComparison.OrdinalIgnoreCase), badSource);
        Check("…and a name nothing resembles gets NO invented suggestion",
            !badSource.Contains("Did you mean", StringComparison.Ordinal), badSource);

        // A TYPO keeps its did-you-mean (PR #313 review 2 [low]): lifting the bound moved this refusal out of the
        // engine, whose AbsenceClause carried the suggester, and the one lane where a source name is hand-typed is
        // this one.
        var typo = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: "HcW2Mastre.esm", patch: "W2FwdTypo");
        Check("a MISTYPED source= still gets the did-you-mean, naming the plugin it resembles",
            typo.StartsWith("error:") && typo.Contains("Did you mean", StringComparison.Ordinal)
            && typo.Contains(fx.MasterName, StringComparison.OrdinalIgnoreCase), typo);

        var noSource = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, patch: "W2FwdNo");
        Check("forward without source= is refused naming the parameter and what it means",
            noSource.StartsWith("error:") && noSource.Contains("source="), noSource);

        // The two SELF-forward refusals live in the shared engine cleave, which the 1.x forward_record also drives —
        // so they named from_plugin=, a parameter housecarl_forward does not expose (PR #311 review 4 [low]). Both
        // are reachable from here: source= equal to the in_place= target, and source= equal to the into= patch. The
        // spelling is threaded from the calling tool, the same rule offerModParam / InPlaceAgainHint already encode.
        // Asserted as a positive AND the ABSENCE of the word the caller cannot act on.
        var selfInPlace = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.ReplacerName,
            in_place: fx.ReplacerName, acknowledge: true);
        Check("forward: source= equal to the in_place= target refuses naming source=, never from_plugin",
            selfInPlace.Contains("is the in-place target itself", StringComparison.Ordinal)
            && selfInPlace.Contains("source=", StringComparison.Ordinal)
            && !selfInPlace.Contains("from_plugin", StringComparison.Ordinal), selfInPlace);

        var intoPatch = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.MasterName, patch: "W2FwdSelf");
        var intoFile = ArtifactPathFrom(fx, intoPatch) is { } sp ? Path.GetFileName(sp) : null;
        if (intoFile is not null)
        {
            var selfInto = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: intoFile, into: intoFile);
            Check("forward: source= equal to the into= patch refuses naming source=, never from_plugin",
                selfInto.Contains("is the output patch itself", StringComparison.Ordinal)
                && selfInto.Contains("source=", StringComparison.Ordinal)
                && !selfInto.Contains("from_plugin", StringComparison.Ordinal), selfInto);
        }
        else Check("forward self-into arm: fixture (a patch to forward into itself)", false, intoPatch);
    }

    // ================= ARM 6 — forward from an OFF-ORDER source (W3 PR 2b) =================
    static void OffOrderForwardArm(Fixture fx)
    {
        Console.WriteLine("── ARM 6: forward source= OFF-ORDER — a DISABLED mod's version is reachable, and which copy was read is stated ──");

        // The capability itself: the disabled copy carries Damage 42 — neither the master's 10 nor the winner's 99 —
        // so landing 42 proves the DISABLED FILE was read, not something already in the order.
        var off = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.OffName, patch: "W2Off1");
        var offPath = ArtifactPathFrom(fx, off);
        Check("a source= on disk but NOT in the load order resolves: the DISABLED mod's version lands",
            offPath is not null && DamageIn(offPath, fx.SubjectKey) == 42, off);

        // WHICH copy was read is a fact the render states — a filename alone does not identify a file on disk.
        Check("the render states the off-order read: the source is named NOT active, with the exact file and its layer",
            off.Contains("read OFF-ORDER from", StringComparison.Ordinal)
            && off.Contains(fx.OffPath, StringComparison.OrdinalIgnoreCase)
            && off.Contains(fx.OffFolder, StringComparison.OrdinalIgnoreCase), off);
        Check("the render says the epoch does NOT cover the off-order file (the stamp fingerprints the ACTIVE order)",
            off.Contains("outside of", StringComparison.Ordinal) && off.Contains("epoch", StringComparison.OrdinalIgnoreCase), off);

        // NO HANDLE AT REST: the overlay opened for the fetch is disposed after the write, so MO2 can still move or
        // delete the disabled mod's file. Proven by actually moving it and putting it back — and GATED on the forward
        // having succeeded, because "movable" is vacuously true when no overlay was ever opened: on the RED run this
        // arm passed while every other one failed, which is the shape of a check that proves nothing.
        bool released;
        var parked = fx.OffPath + ".parked";
        try { File.Move(fx.OffPath, parked); File.Move(parked, fx.OffPath); released = true; }
        catch { released = false; try { if (File.Exists(parked)) File.Move(parked, fx.OffPath); } catch { } }
        Check("the off-order overlay is RELEASED after the write — the source file is still movable (no handle at rest)",
            offPath is not null && released,
            offPath is null ? "no off-order read happened at all, so a movable file proves nothing" : "the source file was still locked after the forward returned");

        // The json twin carries the same fact, positively on BOTH arms. Read through the ABSENCE-TOLERANT helpers: an
        // arm asserting a member the pre-fix code never emits must FAIL, not crash the probe into "guard threw" and
        // hide every finding after it (this arm did exactly that on the first RED run).
        var offJson = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.OffName, patch: "W2Off2", format: "json");
        Check("format=json: source_in_order=false + source_read names the file, the layer, and epoch_covers_source=false",
            TryJson(offJson, out var ojd)
            && RootFlag(ojd, "source_in_order") == false
            && string.Equals(Str(ojd, "source_read", "path"), fx.OffPath, StringComparison.OrdinalIgnoreCase)
            && Str(ojd, "source_read", "where") is { Length: > 0 }
            && Flag(ojd, "source_read", "epoch_covers_source") == false, offJson);

        var activeJson = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.MasterName, patch: "W2Off3", format: "json");
        Check("format=json: an ACTIVE source says source_in_order=true and emits no source_read",
            TryJson(activeJson, out var ajd)
            && RootFlag(ajd, "source_in_order") == true
            && !HasRoot(ajd, "source_read"), activeJson);

        // The IN-PLACE lane takes the same pre-locate (open question 1 of the build plan): the TARGET must stay active,
        // the SOURCE need not be. The replacer is active and owns its override, so 42 lands in ITS OWN file.
        var ip = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.OffName,
            in_place: fx.ReplacerName, acknowledge: true);
        Check("in_place: an OFF-ORDER source forwards into the ACTIVE target's own file (LANE is uniform)",
            !ip.StartsWith("error:") && DamageIn(Path.Combine(fx.ModsDir, "W2Repl", fx.ReplacerName), fx.SubjectKey) == 42, ip);
        Check("in_place: the off-order disclosure rides the in-place render too",
            ip.Contains("read OFF-ORDER from", StringComparison.Ordinal), ip);

        // A record the OFF-ORDER plugin ORIGINATES cannot be forwarded: the patch would need it as a master. Newly
        // reachable through this arm, and refused BY NAME rather than dying as a serializer throw (Q3).
        var origin = ForwardTools.Forward(fx.Svc, formids: new[] { fx.OffOwnFid }, source: fx.OffName, patch: "W2OffOrigin");
        // Asserted on the ORIGIN wording specifically: this call's source DOES define the record, so a pass on a
        // generic "error + the plugin name" could just as well be the doesn't-define refusal firing for another reason.
        Check("a record ORIGINATING in the off-order plugin is refused by name (the patch can't master an inactive plugin)",
            origin.StartsWith("error:") && origin.Contains(fx.OffName, StringComparison.OrdinalIgnoreCase)
            && origin.Contains("ORIGINATES", StringComparison.Ordinal)
            && origin.Contains("as a master", StringComparison.Ordinal), origin);

        // One filename, two disabled folders: refused naming BOTH, never a guess about which version to forward.
        var amb = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.AmbName, patch: "W2OffAmb");
        Check("an AMBIGUOUS off-order filename is refused naming every folder that provides it, never a guess",
            amb.StartsWith("error:") && amb.Contains("W2AmbA", StringComparison.OrdinalIgnoreCase)
            && amb.Contains("W2AmbB", StringComparison.OrdinalIgnoreCase), amb);

        // …and the full PATH is the disambiguator this tool actually has (it has no mod=), so the refusal's remedy works.
        var byPath = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid },
            source: Path.Combine(fx.ModsDir, "W2AmbA", fx.AmbName), patch: "W2OffByPath");
        var byPathFile = ArtifactPathFrom(fx, byPath);
        Check("the remedy works: a full PATH picks one copy and forwards it (the disambiguator this tool exposes)",
            byPathFile is not null && DamageIn(byPathFile, fx.SubjectKey) == 1, byPath);
        Check("the ambiguity refusal offers the PATH, never a mod= this tool does not have",
            !amb.Contains("mod=", StringComparison.Ordinal) && amb.Contains("path", StringComparison.OrdinalIgnoreCase), amb);

        // An off-order file that simply doesn't define the record: refused naming the file AND the FormKey.
        // Refused at the PRE-FETCH, before the engine's origin check gets a say — so the assertion pins the
        // doesn't-define wording AND the record, not just "an error mentioning the source".
        var notThere = ForwardTools.Forward(fx.Svc, formids: new[] { fx.OffOwnFid },
            source: Path.Combine(fx.ModsDir, "W2AmbA", fx.AmbName), patch: "W2OffMiss");
        Check("an off-order file that does NOT define a named record is refused naming the file and the record",
            notThere.StartsWith("error:") && notThere.Contains(fx.AmbName, StringComparison.OrdinalIgnoreCase)
            && notThere.Contains("does NOT define or override", StringComparison.Ordinal)
            && notThere.Contains(fx.OffOwnFid.Split(':')[0], StringComparison.OrdinalIgnoreCase), notThere);

        // dry_run on this arm: the whole pipeline runs (the file IS opened and read) and nothing lands.
        int before = Directory.GetDirectories(fx.ModsDir).Length;
        var dry = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.OffName, patch: "W2OffDry", dry_run: true);
        Check("dry_run over an off-order source: nothing written, and the off-order read is still disclosed",
            dry.StartsWith("DRY RUN", StringComparison.Ordinal)
            && dry.Contains("read OFF-ORDER from", StringComparison.Ordinal)
            && Directory.GetDirectories(fx.ModsDir).Length == before, dry);

        // SELF-forward by PATH: an inactive houseCARL patch addressed by its own path is the FILE BEING WRITTEN, which
        // name equality alone would not catch — and copying through an overlay held open on it would fight the write.
        var made = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.MasterName, patch: "W2OffSelf");
        if (ArtifactPathFrom(fx, made) is { } madePath)
        {
            var self = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: madePath, into: Path.GetFileName(madePath));
            Check("self-forward is caught by FILE IDENTITY when source= is a path to the artifact being written",
                self.StartsWith("error:") && self.Contains("is the output patch itself", StringComparison.Ordinal), self);
        }
        else Check("off-order self-forward arm: fixture (a written patch to address by path)", false, made);
    }

    // ================= ARM 7 — the PR #313 review folds =================
    /// <summary>Three findings, three shapes the arm-6 fixture could not reach: a PATH that names an ACTIVE plugin
    /// (which "off-order" must not claim, and whose already-the-winner flag must survive), a record ORIGINATING in the
    /// artifact being written (no master needed — a plugin is never its own master), and the patch lane's named
    /// missing-master refusal.</summary>
    static void ReviewFoldArm(Fixture fx, string root)
    {
        Console.WriteLine("── ARM 7: PR #313 review folds — a path to the ACTIVE copy, a self-originated record, the named missing master ──");

        // [medium] source= as a PATH to the file the order ACTUALLY LOADS is in-order, not off-order. The replacer
        // WINS the subject, so the already-the-winner flag is the sharp end: an off-order misread suppresses it and
        // claims the record out-ranks itself.
        var livePath = Path.Combine(fx.ModsDir, "W2Repl", fx.ReplacerName);
        var byLivePath = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: livePath, patch: "W2Live");
        Check("a PATH naming the ACTIVE copy is NOT reported off-order (no false 'not in the load order')",
            !byLivePath.StartsWith("error:") && !byLivePath.Contains("read OFF-ORDER from", StringComparison.Ordinal), byLivePath);
        Check("…and it keeps the already-the-winner flag instead of claiming the record out-ranks ITSELF",
            byLivePath.Contains("already the load-order winner", StringComparison.OrdinalIgnoreCase)
            && !byLivePath.Contains("out-ranks the current winner", StringComparison.Ordinal), byLivePath);
        var byLiveJson = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: livePath, patch: "W2Live2", format: "json");
        Check("format=json: the same path says source_in_order=true and emits no source_read (the epoch DOES cover it)",
            TryJson(byLiveJson, out var ljd) && RootFlag(ljd, "source_in_order") == true && !HasRoot(ljd, "source_read"), byLiveJson);

        // A path to a DIFFERENT file that merely SHARES the active name must keep the off-order lane — the rule is
        // file identity, not filename. (The same-name/different-file pair is the ordinary old-version-vs-live case.)
        var shadow = Path.Combine(root, "shadow");
        Directory.CreateDirectory(shadow);
        var shadowCopy = Path.Combine(shadow, fx.ReplacerName);
        File.Copy(livePath, shadowCopy, overwrite: true);
        var byShadow = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: shadowCopy, patch: "W2Shadow");
        Check("a path to a DIFFERENT file sharing the active plugin's NAME still reads OFF-ORDER (identity, not name)",
            byShadow.Contains("read OFF-ORDER from", StringComparison.Ordinal)
            && byShadow.Contains(shadowCopy, StringComparison.OrdinalIgnoreCase), byShadow);

        // [low] A record the ARTIFACT BEING WRITTEN originates needs no master — a plugin is never its own master.
        // Built end-to-end rather than argued: create a record into a fresh patch, park a copy of that patch off-order,
        // change the live one, then forward the parked body back over it.
        var created = CreateTools.Create(fx.Svc, patch: "W2Own",
            records: Json("""[{"record_type":"Weapon","editorid":"W2OwnSubject","ops":[{"field_path":"BasicStats.Damage","value":"5"}]}]"""));
        var ownPath = ArtifactPathFrom(fx, created);
        var ownFid = FormIdsFrom(created).FirstOrDefault();
        if (ownPath is not null && ownFid is not null)
        {
            // Parked in ANOTHER FOLDER under the SAME FILENAME — a plugin's records are keyed by its filename, so a
            // copy saved as 'old-W2Own.esp' declares a different ModKey and defines none of the FormKeys asked for.
            var parkDir = Path.Combine(shadow, "own-old");
            Directory.CreateDirectory(parkDir);
            var parked = Path.Combine(parkDir, Path.GetFileName(ownPath));
            File.Copy(ownPath, parked, overwrite: true);           // the OLD copy, off-order, addressed by full path
            BumpDamage(ownPath, 77);                                // the live patch moves on

            // …and that trap is exactly what the renamed-copy diagnosis is for: the same body under a new NAME is
            // refused, but the refusal now says WHY rather than leaving "doesn't define it" to be read as absence.
            var misnamed = Path.Combine(parkDir, "old-" + Path.GetFileName(ownPath));
            File.Copy(parked, misnamed, overwrite: true);
            var renamedTry = ForwardTools.Forward(fx.Svc, formids: new[] { ownFid }, source: misnamed, into: Path.GetFileName(ownPath));
            Check("a RENAMED off-order copy is refused with the real cause (records are keyed by filename), not bare absence",
                renamedTry.StartsWith("error:")
                && renamedTry.Contains("keyed by its FILENAME", StringComparison.Ordinal)
                && renamedTry.Contains("DOES carry", StringComparison.Ordinal), renamedTry);

            var back = ForwardTools.Forward(fx.Svc, formids: new[] { ownFid }, source: parked, into: Path.GetFileName(ownPath));
            Check("a record ORIGINATING in the artifact being written forwards fine (a plugin is never its own master)",
                !back.StartsWith("error:") && DamageIn(ownPath, FormKey.Factory(ownFid)) == 5, back);
            Check("…and the written header does NOT list the artifact as its own master",
                !back.StartsWith("error:") && !MastersLineOf(back).Contains(Path.GetFileName(ownPath), StringComparison.OrdinalIgnoreCase), back);
            // [low] The row must not assert a ranking against a winner that does not exist. This path is where it shows:
            // the patch isn't in the order, so ResolveWinner is null — and the old "(none)" sentinel rendered as
            // "out-ranks the current winner (none)". Asserted on the LINE, not the whole render.
            var row = back.Split('\n').FirstOrDefault(l => l.Contains(ownFid, StringComparison.OrdinalIgnoreCase)) ?? "";
            Check("a record NO active plugin defines says so, instead of out-ranking a winner called '(none)'",
                row.Contains("no active plugin currently defines this record", StringComparison.Ordinal)
                && !row.Contains("out-ranks the current winner", StringComparison.Ordinal), row.Length > 0 ? row : back);
            var backJson = ForwardTools.Forward(fx.Svc, formids: new[] { ownFid }, source: parked,
                into: Path.GetFileName(ownPath), format: "json");
            Check("format=json: prior_winner is null there, not the string '(none)'",
                TryJson(backJson, out var bjd)
                && bjd!.RootElement.GetProperty("forwarded")[0].GetProperty("prior_winner").ValueKind == JsonValueKind.Null, backJson);
        }
        else Check("review-fold arm: fixture (a created record in a fresh patch)", false, created);

        // …and the refusal it must NOT swallow: a record originating in some OTHER inactive plugin is still named.
        var stillRefused = ForwardTools.Forward(fx.Svc, formids: new[] { fx.OffOwnFid }, source: fx.OffName, patch: "W2StillRef");
        Check("the exemption is narrow: an origin that is NOT the artifact being written is still refused by name",
            stillRefused.StartsWith("error:") && stillRefused.Contains("ORIGINATES", StringComparison.Ordinal), stillRefused);

        // [low] The patch lane's MISSING-MASTER refusal, named rather than rendered as a disk fault. The chain source
        // is a disabled plugin whose body links into ANOTHER disabled plugin — the exact shape this PR makes reachable
        // (a disabled mod mastered on something other than Skyrim.esm), and the one its Safety section promised was
        // "refused loud". The record's own ORIGIN is the active master, so the origin check cannot pre-empt this.
        // [low] The did-you-mean must know the DISABLED half too — this lane just made disabled plugins first-class
        // sources, and the suggester's old corpus was the ACTIVE order, so a typo of a disabled plugin got nothing
        // (PR #313 review 3 [low]). The pool is now every plugin the locate SEARCHED, from the same folder sequence.
        var typoOff = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: "HcW2Of.esp", patch: "W2TypoOff");
        Check("a typo of a DISABLED plugin's name gets the did-you-mean too, not just the active half",
            typoOff.StartsWith("error:") && typoOff.Contains("Did you mean", StringComparison.Ordinal)
            && typoOff.Contains(fx.OffName, StringComparison.OrdinalIgnoreCase), typoOff);

        var chain = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.ChainName, patch: "W2Chain");
        Check("a forwarded body referencing an INACTIVE plugin is refused NAMING it, not as a serialize/commit fault",
            chain.StartsWith("error:")
            && chain.Contains("NOT active in the load order", StringComparison.Ordinal)
            && chain.Contains("Enable that plugin in MO2", StringComparison.Ordinal)
            && !chain.Contains("serialize or commit", StringComparison.Ordinal), chain);
    }

    /// <summary>#321 — a <c>CopyFrom</c> <c>from_source=</c> PATH naming the ACTIVE copy of a plugin must take the
    /// IN-ORDER arm, the rule <c>forward</c> has carried since PR #313. Driven through <c>housecarl_apply</c>, because
    /// the fix lives in the SERVICE's pre-locate (the engine only ever sees the re-spelled edit).
    ///
    /// <para>Why the headline check is a REFUSAL rather than a copied value: the path names the very file the order
    /// serves, so both arms read the same bytes and any value assertion passes pre-fix as well. The arms are
    /// distinguishable exactly where they disagree about what the source IS — a plugin that is in the order but does
    /// not carry the record answers "in the load order but does NOT define or override" in-order, and "source file
    /// '&lt;path&gt;' does not define or override" off-order. The value checks below then bound the fix on both sides:
    /// the copy still lands, and a path to a same-NAMED DIFFERENT file still reads off-order.</para></summary>
    static void CopySourcePathArm(Fixture fx, string root)
    {
        Console.WriteLine("── #321: a CopyFrom from_source= PATH to the ACTIVE copy takes the IN-ORDER arm ──");

        var masterLive = Path.Combine(fx.ModsDir, "W2Master", fx.MasterName);
        var replLive = Path.Combine(fx.ModsDir, "W2Repl", fx.ReplacerName);

        // (a) THE DISCRIMINATOR — an ACTIVE plugin, addressed by path, that does not carry the record.
        var missing = ApplyTools.Apply(fx.Svc, patch: "W2Cf321Miss",
            ops: Json(CopyOps(fx.MasterOnlyFid, "BasicStats.Damage", replLive)));
        Check("#321: a from_source= PATH to an ACTIVE plugin is refused as IN-ORDER (not as an off-order file read)",
            missing.Contains("is in the load order but does NOT define or override", StringComparison.Ordinal), missing);
        Check("…and the refusal speaks the order's vocabulary — the plugin NAME, not the path it was addressed by",
            missing.Contains(fx.ReplacerName, StringComparison.OrdinalIgnoreCase)
            && !missing.Contains(replLive, StringComparison.OrdinalIgnoreCase), missing);

        // (b) The copy still happens, off the named plugin's own version: the master says 10 where the winner says 99.
        var copied = ApplyTools.Apply(fx.Svc, patch: "W2Cf321Live",
            ops: Json(CopyOps(fx.SubjectFid, "BasicStats.Damage", masterLive)));
        var copiedPath = ArtifactPathFrom(fx, copied);
        Check("…and the copy itself still lands from the named plugin's own version (10, not the winner's 99)",
            copiedPath is not null && DamageIn(copiedPath, fx.SubjectKey) == 10,
            $"{copied}  [damage={(copiedPath is null ? "no artifact" : DamageIn(copiedPath, fx.SubjectKey)?.ToString() ?? "unreadable")}]");

        // (c) THE BOUND — identity, not filename. A DIFFERENT file that merely shares the active name keeps the
        //     off-order lane, so its own body (77) is what gets copied. Same rule ActiveNameForPath states for
        //     forward; asserted here so the re-spelling can never widen into a name match.
        var shadowDir = Path.Combine(root, "cf321-shadow");
        Directory.CreateDirectory(shadowDir);
        var shadowMaster = Path.Combine(shadowDir, fx.MasterName);
        File.Copy(masterLive, shadowMaster, overwrite: true);
        BumpDamage(shadowMaster, 77);
        var byShadow = ApplyTools.Apply(fx.Svc, patch: "W2Cf321Shadow",
            ops: Json(CopyOps(fx.SubjectFid, "BasicStats.Damage", shadowMaster)));
        var shadowPath = ArtifactPathFrom(fx, byShadow);
        Check("a path to a DIFFERENT file sharing the active plugin's NAME still copies OFF-ORDER (77, its own body)",
            shadowPath is not null && DamageIn(shadowPath, fx.SubjectKey) == 77,
            $"{byShadow}  [damage={(shadowPath is null ? "no artifact" : DamageIn(shadowPath, fx.SubjectKey)?.ToString() ?? "unreadable")}]");
    }

    /// <summary>#300 — a NESTED create hosts its child in the parent's DEFINING plugin's version, not the load-order
    /// WINNER's. Three observables, because the defect had three costs: the winner does not become a MASTER of the
    /// patch (the child never needed it), the hosted record does not carry a frozen copy of the winner's FIELDS (the
    /// silent-revert-on-update half), and the choice is REPORTED (it is invisible in the record afterwards).</summary>
    static void NestedParentHostArm(Fixture fx)
    {
        Console.WriteLine("── #300: a nested create hosts its child in the parent's DEFINING plugin's version ──");

        var made = CreateTools.Create(fx.Svc, patch: "W2Host",
            records: Json($$"""[{"record_type":"DialogResponses","editorid":"W2HostedLine","parent":"{{fx.TopicFid}}"}]"""));
        var path = ArtifactPathFrom(fx, made);
        Check("a nested create under an existing load-order parent succeeds", path is not null, made);

        Check("…and the parent's load-order WINNER is NOT a master of the patch (the child never needed it)",
            path is not null && !MastersLineOf(made).Contains(fx.ReplacerName, StringComparison.OrdinalIgnoreCase)
            && MastersLineOf(made).Contains(fx.MasterName, StringComparison.OrdinalIgnoreCase), made);

        Check("…and the hosted parent carries the DEFINER's fields, not a frozen copy of the winner's",
            path is not null && TopicNameIn(path!, fx.TopicKey) == "Master Topic",
            $"{made}  [topic name={(path is null ? "no artifact" : TopicNameIn(path, fx.TopicKey) ?? "unreadable")}]");

        // WHAT THE HOST OVERRIDE DOES TO THE PARENT'S CHILD LIST — measured, not assumed, because a full-record copy
        // that dragged the parent's children along would make "hosts from the definer" a far wider revert than fields
        // alone (it would assert the DEFINER's child list wherever the artifact out-ranks the winner). It does not:
        // the artifact's child group carries ONLY the new record, which is also why adding a line to a topic — or a
        // ref to a cell — does not fight other mods' additions. This bounds the #300 trade to the parent's own FIELDS.
        var children = ChildLinesIn(path!, fx.TopicKey);
        Check("…and the host carries ONLY the new child — not the definer's existing lines, and not the winner's",
            children.Count == 1 && children[0] == "W2HostedLine", string.Join(", ", children));

        Check("…and the render REPORTS which plugin's version hosted the child (the choice is invisible afterwards)",
            made.Contains("parent: ", StringComparison.Ordinal)
            && made.Contains("DEFINING plugin", StringComparison.Ordinal)
            && made.Contains(fx.MasterName, StringComparison.OrdinalIgnoreCase), made);
        // …and the CHOICE, not just the label: "DEFINING plugin" alone matches the benign branch where definer and
        // winner are the same plugin, so the contested clause could have been dropped with this arm green. What it
        // must say is which mod currently wins, which way the record resolves, and what the caller can do about it —
        // the lean residual is the default, and inlining the winner is a deliberate act, not an accident.
        Check("…and it names the winning mod, how the record resolves, and the lever the caller has",
            made.Contains(fx.ReplacerName, StringComparison.OrdinalIgnoreCase)
            && made.Contains("currently WINS this record", StringComparison.Ordinal)
            && made.Contains("out-ranks", StringComparison.Ordinal)
            && made.Contains("inlined", StringComparison.Ordinal), made);
        // …and the ORDER, which is the load-bearing half of that lever. This pin has now been wrong in both
        // directions and that is the lesson it carries: the first wording ("forward the winner's version in
        // explicitly") was measured DESTRUCTIVE while #324 was open, so the sentence was changed to name one safe
        // order and warn off the other — and this arm pinned the warning, including the issue number. #324 is fixed
        // on this branch, which made the pinned sentence FALSE and this arm the thing holding it in place: correcting
        // the message failed CI. So the pin is on what the caller must be told (which parent gets inlined, and that
        // the children survive either way), never on a defect being open. The behaviour is proven in
        // ForwardChildGroupArm; this is the SENTENCE arm, because a sentence is what the caller acts on.
        Check("…and the inlining lever names the order-independent remedy, with no stale open-bug warning",
            made.Contains("forward", StringComparison.Ordinal)
            && made.Contains("Either order works", StringComparison.Ordinal)
            && !made.Contains("deletes the child", StringComparison.Ordinal)
            && !made.Contains("#324", StringComparison.Ordinal), made);

        // THE REMEDY THE MESSAGE PRINTS, MEASURED RATHER THAN REASONED — because it is user-facing text telling a
        // caller to run a write, and its first wording was measured FALSE (PR #323 review [medium]).
        //
        // This arm measures the forward-first order: forward the winner into a patch, then create the child into that
        // patch, so the create resolves its parent from the record the patch already carries. Both halves asserted
        // together — the winner's FIELDS inlined and the child present — because either alone would pass on a shape
        // the message must not recommend. The OTHER order (create first, then forward onto the parent the patch now
        // holds) was the measured-destructive one that became #324; it is fixed on this branch and measured in
        // ForwardChildGroupArm, which is why the message no longer warns anyone off it.
        var pre = ForwardTools.Forward(fx.Svc, formids: new[] { fx.TopicFid }, source: fx.ReplacerName, patch: "W2Inline");
        var prePath = ArtifactPathFrom(fx, pre);
        Check("the SAFE ordering, step 1: the winner's version of the parent forwards into a patch of its own",
            prePath is not null, pre);

        var thenChild = CreateTools.Create(fx.Svc, into: Path.GetFileName(prePath!),
            records: Json($$"""[{"record_type":"DialogResponses","editorid":"W2InlinedLine","parent":"{{fx.TopicFid}}"}]"""));
        Check("…step 2: the child creates into THAT patch, hosting in the parent it already carries",
            thenChild.StartsWith("extended ", StringComparison.Ordinal), thenChild);

        // Both, on the file. (The winner's OWN child line is not here and is not expected to be: the forward copies
        // the record, not its group, and the winner's INFOs still play from the winner's own plugin — a child group
        // survives its parent record losing, which is the whole premise of the lean host above.)
        var inlinedName = TopicNameIn(prePath!, fx.TopicKey);
        var inlinedChildren = ChildLinesIn(prePath!, fx.TopicKey);
        Check("…and BOTH land: the winner's FIELDS are inlined AND the new child is in the group",
            inlinedName == "Winner Topic" && inlinedChildren.Contains("W2InlinedLine"),
            $"{thenChild}  [topic name={inlinedName ?? "unreadable"} · children=[{string.Join(", ", inlinedChildren)}]]");
    }

    /// <summary>#324 — a <c>forward</c> onto a record the destination ALREADY carries must not destroy that record's
    /// child group. Both lanes, because the defect was on both and the <c>in_place=</c> half deletes records out of the
    /// caller's own plugin, on the lane whose banner reads "no houseCARL backup or undo".
    /// <para/>
    /// The replace is a DROP-then-copy (the F1 semantic — a collision must never silently skip the copy,
    /// HCBR-2026-07-08-01), the drop takes the child group with it, and the copy carries none back in, so before the
    /// fix the children were gone and the call reported success. The probe body below is the one measured on PR #323
    /// and quoted in #324, re-pointed from "left RED to prove the defect" to "asserts the defect is fixed".
    /// <para/>
    /// Every arm here asserts BOTH halves — the children survive AND the source's fields actually landed — because
    /// either alone passes on a shape the tool must not ship: a forward that no-ops preserves children perfectly.</summary>
    static void ForwardChildGroupArm(Fixture fx)
    {
        Console.WriteLine("── #324: forward onto a carried record PRESERVES its child group (both lanes) ──");

        // --- THE MEASURED CASE, VERBATIM (into=). Create a child into a patch that hosts the topic, then forward the
        //     winner's version into that same patch. Before the fix this left the patch holding one record — the topic
        //     — with the child destroyed and "extended …" reported.
        var made = CreateTools.Create(fx.Svc, patch: "W324",
            records: Json($$"""[{"record_type":"DialogResponses","editorid":"W324Line","parent":"{{fx.TopicFid}}"}]"""));
        var path = ArtifactPathFrom(fx, made);
        Check("#324 fixture: a child creates into a patch, hosted on the parent's definer", path is not null, made);
        if (path is null) return;

        // THE DRY-RUN SENTENCE FIRST, because it is the one a caller reads while DECIDING whether to proceed, and it
        // is a different string from the one they read afterwards. Same rule as the live sentences: a message claim
        // gets its own arm. dry_run runs the whole pipeline and stops before disk, so this leaves the patch alone.
        var dryReplace = ForwardTools.Forward(fx.Svc, formids: new[] { fx.TopicFid }, source: fx.ReplacerName,
            into: Path.GetFileName(path), dry_run: true);
        Check("dry_run over a replace: the PREVIEW says the fields would go and the nested records would be KEPT",
            dryReplace.Contains("the old FIELDS would be gone", StringComparison.Ordinal)
            && dryReplace.Contains("1 record(s) nested under it would be KEPT", StringComparison.Ordinal)
            && !dryReplace.Contains("the old body would be gone", StringComparison.Ordinal), dryReplace);
        Check("…and it really was a preview: the child is still the only thing under the topic on disk",
            ChildLinesIn(path, fx.TopicKey) is { Count: 1 } d && d.Contains("W324Line"),
            string.Join(", ", ChildLinesIn(path, fx.TopicKey)));

        var inlined = ForwardTools.Forward(fx.Svc, formids: new[] { fx.TopicFid }, source: fx.ReplacerName,
            into: Path.GetFileName(path));
        Check("forward into= a patch that already carries the record is accepted",
            inlined.StartsWith("extended ", StringComparison.Ordinal), inlined);

        var after = ChildLinesIn(path, fx.TopicKey);
        Check("…and the child SURVIVES the replace (#324: the drop no longer takes the child group)",
            after.Contains("W324Line"),
            $"{inlined}  [children=[{string.Join(", ", after)}] · whole file=[{string.Join(", ", EditorIdsIn(path))}]]");

        // THE SENTENCE THE CALLER ACTS ON, which is a separate surface from the engine (the standing lesson of #323 →
        // #324: a destructive remedy shipped through fully green guards). This render said "the old body is gone"
        // flat. It is now false in the one case that matters: the caller reverts a cell in place, keeps their forty
        // placed refs, and is told the old body went — so they ship what they think they deleted, or re-create a
        // dialogue line they never lost. Measured here BEFORE it was reworded, per §11.
        Check("…and the RESPONSE says so: the replace reports the fields gone and the nested records KEPT, with a count",
            inlined.Contains("the old FIELDS are gone", StringComparison.Ordinal)
            && inlined.Contains("1 record(s) nested under it were KEPT", StringComparison.Ordinal)
            && !inlined.Contains("the old body is gone", StringComparison.Ordinal), inlined);
        Check("…and the forward still DID its job: the source's fields are inlined in the host",
            TopicNameIn(path, fx.TopicKey) == "Winner Topic",
            $"{inlined}  [topic name={TopicNameIn(path, fx.TopicKey) ?? "unreadable"}]");

        // …and the group holds the destination's child and ONLY that. The re-attach is lossless only because the copy
        // arrives carrying nothing — Mutagen's behaviour, not ours, which is why RestoreChildGroup refuses rather than
        // discards if it ever changes. Both clauses together, since the absence alone would pass vacuously on the
        // pre-fix code, where the whole group is empty.
        Check("…and the group holds exactly the destination's child — the SOURCE's own line did not come with it",
            after.Count == 1 && !after.Contains("W2WinnerLine") && !after.Contains("(unreadable)"),
            string.Join(", ", after));

        // --- A CHILD AND ITS PARENT IN ONE CALL, on the fresh-patch lane where NOTHING is replaced. Spec 1 (the INFO)
        //     materializes the topic in the patch to host itself; spec 2 (the topic) then copies onto a record the
        //     CALL just created, carrying children the call just put there. A guard that infers "was this replaced?"
        //     from the record's shape sees that as a copy arriving with children and refuses the whole write, telling
        //     the caller to report a Mutagen bug — measured in round 2, introduced by round 1's own fold. The gate is
        //     the capture FACT now, and this arm is what holds it there: nothing was dropped, so nothing is checked.
        var pair = ForwardTools.Forward(fx.Svc, formids: new[] { fx.WinnerLineFid, fx.TopicFid },
            source: fx.ReplacerName, patch: "W324Pair");
        var pairPath = ArtifactPathFrom(fx, pair);
        Check("a child and its parent forward in ONE call to a fresh patch — the call is not refused",
            pairPath is not null && !pair.StartsWith("error:", StringComparison.Ordinal), pair);
        Check("…and the child lands under its parent in the patch",
            pairPath is not null && ChildLinesIn(pairPath, fx.TopicKey).Contains("W2WinnerLine"),
            pairPath is null ? pair : $"children=[{string.Join(", ", ChildLinesIn(pairPath, fx.TopicKey))}]");
        // …and the parent's own fields land too. This block sits ABOVE the in-place half deliberately: that half
        // rewrites the replacer's topic to "Master Topic", and with it running first BOTH sides of this comparison
        // read "Master Topic", so the assertion cannot tell a landed copy from a skipped one. Measured in that state
        // it looked like a silent skip and was briefly filed as #333; the fixture was the bug, not the lane. Ordered
        // this way the replacer still reads "Winner Topic" and the discriminator is real.
        Check("…and the parent's own fields land too — the second spec is not skipped over a record the call created",
            pairPath is not null && TopicNameIn(pairPath, fx.TopicKey) == "Winner Topic",
            pairPath is null ? pair : $"name={TopicNameIn(pairPath, fx.TopicKey) ?? "unreadable"}");

        // --- THE WORSE HALF (in_place=): the destination is the caller's OWN plugin. The replacer owns the topic and
        //     a line under it; forwarding the MASTER's version in place must land the master's fields and leave the
        //     replacer's own line standing. Consent for this plugin was recorded by the LANE arm; acknowledge=true is
        //     passed anyway so this arm does not silently depend on another arm's side effect.
        var replPath = Path.Combine(fx.ModsDir, "W2Repl", fx.ReplacerName);
        var before = ChildLinesIn(replPath, fx.TopicKey);
        Check("#324 in-place fixture: the target plugin carries the record AND a child of its own",
            before.Contains("W2WinnerLine"), string.Join(", ", before));

        var inPlace = ForwardTools.Forward(fx.Svc, formids: new[] { fx.TopicFid }, source: fx.MasterName,
            in_place: fx.ReplacerName, acknowledge: true);
        Check("forward in_place= onto a record the file already carries is accepted",
            !inPlace.StartsWith("error:"), inPlace);

        var afterInPlace = ChildLinesIn(replPath, fx.TopicKey);
        Check("…and the caller's OWN child survives it — the no-backup lane does not lose records",
            afterInPlace.Contains("W2WinnerLine"),
            $"{inPlace}  [children=[{string.Join(", ", afterInPlace)}]]");
        // …and the same not-the-source's-children clause the into= half asserts (round-1 review [low]): presence
        // alone would stay green if the copy started dragging the MASTER's line in, leaving the replacer holding two
        // INFOs under one topic — a duplicated line, not a lost one, and just as wrong.
        Check("…and ONLY the caller's own child: the master's line did not ride in on the copy",
            afterInPlace.Count == 1 && !afterInPlace.Contains("W2MasterLine"), string.Join(", ", afterInPlace));
        Check("…and the forward still DID its job: the master's fields replaced the winner's",
            TopicNameIn(replPath, fx.TopicKey) == "Master Topic",
            $"{inPlace}  [topic name={TopicNameIn(replPath, fx.TopicKey) ?? "unreadable"}]");

        // --- THE CELL, which is a DIFFERENT copy path and the case the changelog names first. A topic lives in a flat
        //     group; a cell does not, so the override resolves through a ModContext that rebuilds the block/subblock
        //     chain. Every assertion above would stay green with the nested path broken (round-1 review [medium]),
        //     and "placed references under a forwarded cell" is the loss a user is most likely to hit in the wild.
        var madeRef = CreateTools.Create(fx.Svc, patch: "W324Cell",
            records: Json($$"""[{"record_type":"PlacedObject","editorid":"W324Ref","parent":"{{fx.CellFid}}","collection":"Persistent"}]"""));
        var cellPath = ArtifactPathFrom(fx, madeRef);
        Check("#324 cell fixture: a placed ref creates into a patch, hosted on the cell's definer", cellPath is not null, madeRef);
        if (cellPath is null) return;

        var cellFwd = ForwardTools.Forward(fx.Svc, formids: new[] { fx.CellFid }, source: fx.ReplacerName,
            into: Path.GetFileName(cellPath));
        Check("forward into= a patch that already carries the CELL is accepted",
            cellFwd.StartsWith("extended ", StringComparison.Ordinal), cellFwd);

        var refsAfter = CellRefsIn(cellPath, fx.CellKey);
        Check("…and the placed ref SURVIVES the replace on the nested-group path too",
            refsAfter.Contains("W324Ref"),
            $"{cellFwd}  [refs=[{string.Join(", ", refsAfter)}] · whole file=[{string.Join(", ", EditorIdsIn(cellPath))}]]");
        Check("…and the forward still DID its job: the source's cell fields are inlined in the host",
            CellNameIn(cellPath, fx.CellKey) == "Winner Cell",
            $"{cellFwd}  [cell name={CellNameIn(cellPath, fx.CellKey) ?? "unreadable"}]");
        Check("…and the group holds exactly the destination's ref — the SOURCE's own refs did not come with it",
            refsAfter.Count == 1 && !refsAfter.Contains("W2WinnerRef") && !refsAfter.Contains("(unreadable)"),
            string.Join(", ", refsAfter));

        // …and the cell on the in-place lane, where the placed refs being deleted are the caller's own.
        var cellInPlace = ForwardTools.Forward(fx.Svc, formids: new[] { fx.CellFid }, source: fx.MasterName,
            in_place: fx.ReplacerName, acknowledge: true);
        Check("forward in_place= onto a CELL the file already carries is accepted",
            !cellInPlace.StartsWith("error:", StringComparison.Ordinal), cellInPlace);
        var replRefs = CellRefsIn(replPath, fx.CellKey);
        Check("…and the caller's OWN placed ref survives it, and only it",
            replRefs.Count == 1 && replRefs.Contains("W2WinnerRef"),
            $"{cellInPlace}  [refs=[{string.Join(", ", replRefs)}]]");
        Check("…and the master's cell fields replaced the winner's",
            CellNameIn(replPath, fx.CellKey) == "Master Cell",
            $"{cellInPlace}  [cell name={CellNameIn(replPath, fx.CellKey) ?? "unreadable"}]");

        // --- A REPLACE OF A RECORD THAT OWNS NOTHING still reports honestly. The weapon has no child group, so the
        //     render must say the body is gone AND that there was nothing nested — "kept" and "nothing to keep" are
        //     different facts and a caller reverting a record needs to know which one they got.
        ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.MasterName, into: Path.GetFileName(path));
        var dryFlat = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.ReplacerName,
            into: Path.GetFileName(path), dry_run: true);
        Check("…and its dry-run twin says the same: the body would go, and there is nothing nested to keep",
            dryFlat.Contains("the old body would be gone", StringComparison.Ordinal)
            && dryFlat.Contains("carries no nested records", StringComparison.Ordinal)
            && !dryFlat.Contains("would be KEPT", StringComparison.Ordinal), dryFlat);
        var flat = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.ReplacerName,
            into: Path.GetFileName(path));
        Check("…and a replace of a record with NO children says so, rather than counting a preservation that was not one",
            flat.Contains("the old body is gone", StringComparison.Ordinal)
            && flat.Contains("carried no nested records", StringComparison.Ordinal)
            && !flat.Contains("were KEPT", StringComparison.Ordinal), flat);

        // --- THE GATE ITSELF, BOTH DIRECTIONS. RestoreChildGroup must act if and only if a capture happened. Driven
        //     directly, because the point is the gate's INPUT, not a lane's behaviour: the same record, the same
        //     arriving children, one carry that came from CaptureChildGroup and one that did not.
        var gateSubject = new DialogTopic(FormKey.Factory("123460:HcW2Master.esm"), SkyrimRelease.SkyrimSE);
        gateSubject.Responses.Add(new DialogResponses(FormKey.Factory("123461:HcW2Master.esm"), SkyrimRelease.SkyrimSE)
            { EditorID = "W324Gate" });
        Check("the gate is the capture FACT: an uncaptured carry does not engage the guard at all",
            WriteEngine.RestoreChildGroup(gateSubject, default, "…") is null, "(refused)");
        Check("…and a captured one does, on the identical record and children",
            WriteEngine.RestoreChildGroup(gateSubject, WriteEngine.CaptureChildGroup(gateSubject) with { Held = Array.Empty<(PropertyInfo, object?)>(), Count = 9 }, "…")
                is not null, "(accepted)");

        // --- WORLDSPACE, the reason the walk recurses at all: SubCells reaches its cells through TWO non-record
        //     container types, so it is the only pinned type whose re-attach crosses more than one level. Exercised
        //     directly rather than through a lane — a worldspace forward needs an exterior block/subblock fixture,
        //     and what is unproven without this is the RE-ATTACH, not the lane. Stated plainly so the gap is not
        //     mistaken for coverage: no end-to-end forward of a WRLD is measured here (its failure mode is bounded —
        //     a count mismatch refuses, it does not lose records).
        var wrld = new Worldspace(FormKey.Factory("123470:HcW2Master.esm"), SkyrimRelease.SkyrimSE);
        wrld.TopCell = new Cell(FormKey.Factory("123471:HcW2Master.esm"), SkyrimRelease.SkyrimSE) { EditorID = "W324Top" };
        var wSub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
        wSub.Items.Add(new Cell(FormKey.Factory("123472:HcW2Master.esm"), SkyrimRelease.SkyrimSE) { EditorID = "W324Ext" });
        var wBlock = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
        wBlock.Items.Add(wSub);
        wrld.SubCells.Add(wBlock);

        var wCarry = WriteEngine.CaptureChildGroup(wrld);
        var wFresh = new Worldspace(wrld.FormKey, SkyrimRelease.SkyrimSE);
        var wRefusal = WriteEngine.RestoreChildGroup(wFresh, wCarry, "…");
        Check("Worldspace: the capture sees BOTH the top cell and the exterior cell two containers down",
            wCarry.Count == 2, $"count={wCarry.Count} names=[{string.Join(", ", wCarry.Names)}]");
        Check("…and the re-attach restores both onto a fresh copy, with the count balancing",
            wRefusal is null && wFresh.TopCell?.EditorID == "W324Top"
            && wFresh.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items).Any(c => c.EditorID == "W324Ext"),
            wRefusal ?? $"top={wFresh.TopCell?.EditorID ?? "(none)"} subcells={wFresh.SubCells.Count}");

        // --- THE REFLECTED CHILD SET, PINNED. RestoreChildGroup re-attaches by walking properties that can REACH an
        //     owned record; its by-construction count check catches a path the walk cannot see, but only at call time,
        //     as a refusal in the caller's face. Pinning the set here turns a Mutagen bump that adds a container into
        //     a failing CI arm instead. Worldspace is the reason the walk RECURSES: SubCells reaches its cells through
        //     two non-record types, so a one-level test would silently skip every exterior cell in the game.
        Check("the child-bearing property set is exactly what Mutagen models: Cell(4) · DialogTopic(1) · Worldspace(2)",
            Names(typeof(Cell)) == "Landscape,NavigationMeshes,Persistent,Temporary"
            && Names(typeof(DialogTopic)) == "Responses"
            && Names(typeof(Worldspace)) == "SubCells,TopCell",
            $"Cell=[{Names(typeof(Cell))}] DialogTopic=[{Names(typeof(DialogTopic))}] Worldspace=[{Names(typeof(Worldspace))}]");

        // …and the negative over EVERY concrete record type Mutagen models, not a sample of three (round-1 review
        // [low]: naming Weapon/Npc/Quest left the other 127 free to grow a container silently, and the first caller
        // to forward one would eat the count-check refusal with CI green). This is the coverage cornerstone's own
        // posture applied to the walk — the set is closed by construction, so the pin is too. Quest is still the
        // sharp case the negative exists for: it LINKS topics and owns aliases, and reaching either would make every
        // quest forward pay for a walk with nothing to preserve. Cost of the whole sweep: tens of milliseconds once.
        var owners = ConcreteRecordTypes()
            .Where(t => WriteEngine.ChildBearingProperties(t).Count > 0)
            .Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Check("…and NOTHING else does, across every concrete record type Mutagen models (a link is not a child)",
            string.Join(",", owners) == "Cell,DialogTopic,Worldspace",
            $"{ConcreteRecordTypes().Count} concrete record types · owners=[{string.Join(", ", owners)}]");

        // --- WRITING AT A SINGULAR OWNED CHILD (#335). The reference now calls Cell.Landscape / Worldspace.TopCell
        //     what they are — owned child records, not FormLinks — and corpus-hygiene-guard's INV6 pins that
        //     classification against the very set pinned above. What the classification CHANGES for a caller is the
        //     write path: a descent that the gate used to refuse as a formlink now reaches MaterializeSubstruct, so
        //     the two states an owned child can be in are measured here rather than assumed. ABSENT is the one that
        //     needed its own arm: a record has no parameterless ctor because a FormKey identifies it, and the
        //     composition deferral it would otherwise inherit names the wrong gap and a wave that will never deliver
        //     it. PRESENT is the positive the refusal's own remedy sentence promises.
        var landless = new Cell(FormKey.Factory("123480:HcW2Master.esm"), SkyrimRelease.SkyrimSE);
        var absentMsg = Throws(() => WriteEngine.ApplyVerb(landless,
            new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape", "EditorID" }, Verb = "Set", Value = "W335" }));
        Check("an absent owned child record refuses in its OWN words, not as a composition deferral",
            absentMsg is not null && absentMsg.Contains("owned child RECORD", StringComparison.Ordinal)
            && absentMsg.Contains("its own FormKey", StringComparison.Ordinal)
            && !absentMsg.Contains("COMPOSITION type", StringComparison.Ordinal), absentMsg ?? "(accepted)");

        var landed = new Cell(FormKey.Factory("123481:HcW2Master.esm"), SkyrimRelease.SkyrimSE)
            { Landscape = new Landscape(FormKey.Factory("123482:HcW2Master.esm"), SkyrimRelease.SkyrimSE) };
        var presentMsg = Throws(() => WriteEngine.ApplyVerb(landed,
            new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape", "EditorID" }, Verb = "Set", Value = "W335" }));
        Check("…and a PRESENT owned child is navigable: a sub-field write lands on a cell that carries one",
            presentMsg is null && landed.Landscape?.EditorID == "W335",
            presentMsg ?? $"EditorID={landed.Landscape?.EditorID ?? "(none)"}");

        // The OTHER arm of that branch: a genuinely composition-typed absent substruct keeps the composition
        // deferral. Without this, the record clause could grow to swallow the case it was carved out of.
        var bareLand = new Landscape(FormKey.Factory("123483:HcW2Master.esm"), SkyrimRelease.SkyrimSE);
        var compMsg = Throws(() => WriteEngine.ApplyVerb(bareLand,
            new WriteRequest { RecordType = "Landscape", Path = new[] { "VertexColors", "X" }, Verb = "Set", Value = "1" }));
        Check("…while an absent COMPOSITION substruct still gets the composition deferral, unchanged",
            compMsg is not null && compMsg.Contains("COMPOSITION type", StringComparison.Ordinal)
            && !compMsg.Contains("owned child RECORD", StringComparison.Ordinal), compMsg ?? "(accepted)");

        // --- WHICH STATE A CALLER ACTUALLY MEETS. The PRESENT case above is a hand-built record; the LANE is what
        //     decides which of the two a caller hits, and it is not the same answer. Mutagen's override copy does NOT
        //     bring a parent's child records with it, so a patch override of a cell arrives carrying no Landscape even
        //     when the original has one — which makes the absent refusal the ONLY outcome on the default lane, and
        //     makes "descend to the child the parent already carries" a remedy the lane cannot reach. Pinned here so
        //     the refusal's own wording (it names the record-by-FormID path instead) stays keyed to a measured fact
        //     rather than to an assumption about what an override carries.
        var srcCellForLand = new Cell(FormKey.Factory("123484:HcW2Master.esm"), SkyrimRelease.SkyrimSE) { EditorID = "W335Src" };
        srcCellForLand.Landscape = new Landscape(FormKey.Factory("123485:HcW2Master.esm"), SkyrimRelease.SkyrimSE) { EditorID = "W335Land" };
        srcCellForLand.Persistent.Add(new PlacedObject(FormKey.Factory("123486:HcW2Master.esm"), SkyrimRelease.SkyrimSE));
        var srcMod = new SkyrimMod(new ModKey("HcW335Src", ModType.Master), SkyrimRelease.SkyrimSE);
        var srcSub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        srcSub.Cells.Add(srcCellForLand);
        var srcBlk = new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock };
        srcBlk.SubBlocks.Add(srcSub);
        srcMod.Cells.Records.Add(srcBlk);
        var srcCache = srcMod.ToImmutableLinkCache();
        var patchMod = new SkyrimMod(new ModKey("HcW335Patch", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var laneOverride = (ICell)WriteEngine.GenericGetOrAddAsOverride(patchMod, srcCellForLand, srcCache);
        Check("a patch override of a parent arrives WITHOUT the parent's child records — so the absent arm is what the default lane meets",
            laneOverride.Landscape is null && laneOverride.Persistent.Count == 0,
            $"Landscape={laneOverride.Landscape?.EditorID ?? "(none)"} persistent={laneOverride.Persistent.Count}");

        // …and the path the refusal names instead: the child record, overridden on its OWN axis, is settable and
        // lands in the patch. This is the remedy sentence's probe (§5 #11) — measured, not asserted.
        var landOverride = (ILandscape)WriteEngine.GenericGetOrAddAsOverride(patchMod, srcCellForLand.Landscape!, srcCache);
        var byFormKey = Throws(() => WriteEngine.ApplyVerb(landOverride,
            new WriteRequest { RecordType = "Landscape", Path = new[] { "EditorID" }, Verb = "Set", Value = "W335Direct" }));
        Check("…and the remedy it DOES name works: the child record, addressed on its own axis, takes the write",
            byFormKey is null && landOverride.EditorID == "W335Direct", byFormKey ?? $"EditorID={landOverride.EditorID}");

        // --- THE DISPOSITION TABLE. Every op verb the write surface exposes, against the owned-child shape, with the
        //     answer it must give — in ONE place, so "which door did we miss" is a CI question instead of a review
        //     hope. It exists because #335 was a classification fix that quietly changed behaviour: correcting the
        //     two fields put a shape into the corpus that no rule had ever seen (before it, no field was a substruct
        //     whose TypeRef is a record), and each rule keyed on "substruct" answered as if it were an ordinary
        //     sub-object. Two review rounds found four such doors one at a time — Set/value, compose, composes,
        //     Remove — and a fifth, CopyFrom, only after the first four were closed and the docs already claimed
        //     completeness. A per-door arm set would have kept that going. A table enumerates the verbs instead.
        //
        //     Each row is (what a caller does) -> (accepted, or refused with these words). The CONTROL column is the
        //     same verb on a leaf one step away — an ordinary nullable substruct, or the LIST form of the same
        //     owned-child family — so a clause that widens past the record shape turns a control row red rather than
        //     passing quietly. Sabotage evidence for the rows this branch owns is in the PR body.
        //
        //     WHAT THE TABLE PINS, PRECISELY: leaf dispositions, plus the path rows below. Aaron's review walked the
        //     leaf surface independently (every verb x every input slot — Value / Values / Entries / Key / Struct /
        //     Structs, the @editorid same-call branch, the unknown-verb default) and found no leaf door the rows
        //     miss. Both real gaps it did find were OFF-leaf: a path running THROUGH the child, and the shape that
        //     never becomes "substruct" at all (a singular field typed as a record polymorphic base — closed in
        //     SchemaClassifier, pinned by corpus-hygiene-guard's INV6-SHAPE arms). So the dimension a row covers is
        //     the leaf; the path dimension needed rows of its own, and they are marked as such.
        var ownedChild = new (string What, WriteRequest Req, string? MustSay)[]
        {
            ("Set value= (a FormID, the shape the old formlink classification invited)",
                new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "Set", Value = "000800:Skyrim.esm" },
                "owned child RECORD"),
            ("Set compose= (build the child from parts)",
                new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "Set",
                    Struct = new StructSpec { Type = "Landscape", Fields = new Dictionary<string, string> { ["EditorID"] = "X" } } },
                "owned child RECORD"),
            ("Add composes= (the third door: a LIST of built elements)",
                new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "Add",
                    Structs = new[] { new StructSpec { Type = "Landscape", Fields = new Dictionary<string, string> { ["EditorID"] = "X" } } } },
                "owned child RECORD"),
            ("Remove, keyless (clear the field — deletes the record and its subtree)",
                new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "Remove" },
                "owned child RECORD"),
            ("Remove with a key (there is no element to name on a singular child)",
                new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "Remove", Key = "0" },
                "owned child RECORD"),
            ("CopyFrom (transplant the field's value from another plugin's version)",
                new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "CopyFrom" },
                "owned child RECORD"),
            ("CopyFrom on the other owned-child field, so the answer is the shape's, not one field's",
                new WriteRequest { RecordType = "Worldspace", Path = new[] { "TopCell" }, Verb = "CopyFrom" },
                "owned child RECORD"),
            ("Add a plain value (a collection verb on a singular leaf)",
                new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "Add", Value = "x" },
                "Add is only valid"),
            ("ReplaceAll", new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "ReplaceAll", Values = new[] { "x" } },
                "ReplaceAll is only valid"),
            ("SetAtIndex", new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "SetAtIndex", Key = "0", Value = "x" },
                "SetAtIndex is only valid"),
            // #302's verb, dispositioned by the table rather than by assumption. It answers this shape exactly as its
            // two siblings do — refused by CARDINALITY at the leaf, redirected to the record axis on the LIST form,
            // accepted mid-path — and every row below carries its sibling beside it so a FORK would show as a
            // disagreement between two rows rather than as an absence.
            ("InsertAtIndex (the leaf: a singular child is not a list, so the collection verb refuses by cardinality)",
                new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "InsertAtIndex", Key = "0", Value = "x" },
                "InsertAtIndex is only valid"),
            ("Merge", new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "Merge",
                    Entries = new Dictionary<string, string> { ["k"] = "v" } },
                "Merge is only valid"),
            ("descend to a sub-field of the child (the gate cannot know if one is there — live state)",
                new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape", "EditorID" }, Verb = "Set", Value = "W335" },
                null),
            // CONTROLS — the same verbs one step away. These are what a widening clause breaks first.
            ("CONTROL an ordinary nullable substruct still clears",
                new WriteRequest { RecordType = "Book", Path = new[] { "Model" }, Verb = "Remove" }, null),
            ("CONTROL an ordinary substruct still composes",
                new WriteRequest { RecordType = "Book", Path = new[] { "Model" }, Verb = "Set",
                    Struct = new StructSpec { Type = "Model", Fields = new Dictionary<string, string> { ["File"] = @"probe\b.nif" } } }, null),
            ("CONTROL the LIST form of the family still deletes one child BY INDEX",
                new WriteRequest { RecordType = "Cell", Path = new[] { "Persistent" }, Verb = "Remove", Key = "0" }, null),
            ("CONTROL the LIST form still refuses CopyFrom in its own words",
                new WriteRequest { RecordType = "Cell", Path = new[] { "Persistent" }, Verb = "CopyFrom" },
                "holds owned child records"),
            // The LIST form of the family, for the new verb and for the sibling it must not diverge from: a child
            // record is allocated on the record axis, and inserting one AT a position is no more possible than
            // appending one. A verb missing from that redirect is accepted here and thrown at apply (Q3), which is
            // why this row is a pair rather than a single.
            ("CONTROL the LIST form redirects an InsertAtIndex of a child record to the record axis",
                new WriteRequest { RecordType = "Cell", Path = new[] { "Persistent" }, Verb = "InsertAtIndex", Key = "0",
                    Struct = new StructSpec { Type = "PlacedObject" } },
                "holds owned child records"),
            ("CONTROL …and its sibling SetAtIndex gives the SAME answer there, so insert has not forked",
                new WriteRequest { RecordType = "Cell", Path = new[] { "Persistent" }, Verb = "SetAtIndex", Key = "0",
                    Struct = new StructSpec { Type = "PlacedObject" } },
                "holds owned child records"),
            // THE DOOR THIS TABLE MISSED. composes= at the LIST form of the family: the rows above cover composes=
            // at the SINGULAR child and every collection verb at the list, but not the batch input surface AT the
            // list — and that is the one door composes= reaches by short-circuiting above step 4, so it answered
            // from its own hand-written label rather than from the shape. Measured at 4130d5d, this row's mustSay
            // was absent and the message said "holds coercible values (Placed)" instead, in the same sentence whose
            // derived tail named the record axis.
            ("composes= at the LIST form — the batch input surface, which short-circuits above the collection verbs",
                new WriteRequest { RecordType = "Cell", Path = new[] { "Persistent" }, Verb = "Add",
                    Structs = new[] { new StructSpec { Type = "PlacedObject" } } },
                "holds owned child records"),
            ("composes= at the LIST form, ReplaceAll — the other verb that reaches it, so the answer is the input surface's and not one verb's",
                new WriteRequest { RecordType = "Cell", Path = new[] { "Persistent" }, Verb = "ReplaceAll",
                    Structs = new[] { new StructSpec { Type = "PlacedObject" } } },
                "holds owned child records"),
            ("composes= at the LIST form of the OTHER record-element family, so the answer is the shape's and not one field's",
                new WriteRequest { RecordType = "DialogTopic", Path = new[] { "Responses" }, Verb = "Add",
                    Structs = new[] { new StructSpec { Type = "DialogResponses" } } },
                "holds owned child records"),
            // CONTROL for the row above — the clause sits immediately before the composes= cardinality sentence, so
            // an ordinary MODELED list is what proves it did not widen to every list with a composes= on it.
            ("CONTROL composes= on an ordinary modeled list still reaches the composes= surface",
                new WriteRequest { RecordType = "Faction", Path = new[] { "Conditions" }, Verb = "Set",
                    Structs = new[] { new StructSpec { Type = "ConditionFloat" } } },
                "composes= appends/replaces a LIST"),
            // The composes= control has to be an ordinary SUBSTRUCT, not an ordinary list: the record clause sits
            // immediately before the "composes= builds a LIST … but this is a {cardinality}" sentence, so only a
            // non-record substruct crosses the same branch and proves the clause did not widen. (Added after the
            // widening sabotage below turned every OTHER control red and left this door's untested.)
            ("CONTROL composes= on an ordinary substruct keeps the generic LIST sentence",
                new WriteRequest { RecordType = "Book", Path = new[] { "Model" }, Verb = "Add",
                    Structs = new[] { new StructSpec { Type = "Model", Fields = new Dictionary<string, string> { ["File"] = @"probe\b.nif" } } } },
                "composes= builds a LIST"),
            ("ReplaceAll composes=[…] (the other verb that reaches the composes clause)",
                new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "ReplaceAll",
                    Structs = new[] { new StructSpec { Type = "Landscape", Fields = new Dictionary<string, string> { ["EditorID"] = "X" } } } },
                "owned child RECORD"),
            ("ReplaceAll composes=[] (the modeled-list CLEAR — the other door to deleting the child)",
                new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "ReplaceAll",
                    Structs = Array.Empty<StructSpec>() },
                "owned child RECORD"),
            // THE PATH DIMENSION. The rows above are LEAF dispositions; a record can now also sit MID-path, and the
            // two verbs split there. Transplant refuses at any depth…
            ("CopyFrom THROUGH the child (a leaf under it) — transplant refuses at any depth",
                new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape", "EditorID" }, Verb = "CopyFrom" },
                "runs through 'Landscape'"),
            ("CopyFrom through the other owned-child field, so the answer is the shape's",
                new WriteRequest { RecordType = "Worldspace", Path = new[] { "TopCell", "EditorID" }, Verb = "CopyFrom" },
                "runs through 'TopCell'"),
            // …while the in-place verbs through the same hop stay ACCEPTED: they edit the child this record already
            // carries, which is the descend row's territory and the only way to edit a carried child at all.
            ("mid-path Set under the child stays accepted (in-place descent, not transplant)",
                new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape", "EditorID" }, Verb = "Set", Value = "x" }, null),
            ("mid-path Remove under the child stays accepted (clears a field OF the carried child, not the child)",
                new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape", "VertexHeightMap" }, Verb = "Remove" }, null),
            ("CONTROL CopyFrom through an ORDINARY substruct path is still accepted",
                new WriteRequest { RecordType = "Book", Path = new[] { "Model", "File" }, Verb = "CopyFrom" }, null),
            // The in-place descent, for a verb that lands a NEW element rather than editing one that is there.
            // It takes the descend row's answer: Landscape.Textures is a list the carried child already owns, and
            // inserting into it edits that child in place — the same act the mid-path Set row covers, and NOT the
            // transplant the CopyFrom rows above refuse. Its sibling sits beside it for the same no-fork reason.
            ("mid-path InsertAtIndex at a LIST leaf under the child stays accepted (in-place descent, not transplant)",
                new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape", "Textures" }, Verb = "InsertAtIndex",
                    Key = "0", Value = "000800:Skyrim.esm" }, null),
            ("CONTROL the same mid-path leaf takes SetAtIndex too — insert's disposition does not fork from its sibling's",
                new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape", "Textures" }, Verb = "SetAtIndex",
                    Key = "0", Value = "000800:Skyrim.esm" }, null),
        };
        foreach (var (what, req, mustSay) in ownedChild)
        {
            var got = Rules.Validate(req);
            Check($"disposition · {what}",
                mustSay is null ? got is null : got is not null && got.Contains(mustSay, StringComparison.Ordinal),
                got ?? "(accepted)");
        }

        // …and the one claim no row can carry, because it is about what the refusals DON'T say: the three Set-shaped
        // doors give the SAME sentence. They used to give three that pointed at each other — compose= said "use a
        // plain value", value= said "navigate into it", composes= said "a substruct takes compose= / value=" — a
        // caller following any one of them arrived at another refusal.
        var setDoors = new[]
        {
            Rules.Validate(new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "Set", Value = "000800:Skyrim.esm" }),
            Rules.Validate(new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "Set",
                Struct = new StructSpec { Type = "Landscape", Fields = new Dictionary<string, string> { ["EditorID"] = "X" } } }),
            Rules.Validate(new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "Add",
                Structs = new[] { new StructSpec { Type = "Landscape", Fields = new Dictionary<string, string> { ["EditorID"] = "X" } } } }),
        };
        Check("…and the three Set-shaped doors give ONE sentence, not three that point at each other",
            setDoors.All(s => s is not null) && setDoors.Distinct(StringComparer.Ordinal).Count() == 1,
            string.Join(" || ", setDoors.Select(s => s ?? "(accepted)")));

        // The two refusals that route a caller to the lifecycle gap name it by NUMBER — the gap is what makes them
        // honest rather than blank walls, so a renumber or a silent drop should fail here.
        Check("the refusals that have no remedy route to the lifecycle gap by number (#350) — all THREE of them",
            new[]
            {
                Rules.Validate(new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "Remove" }),
                Rules.Validate(new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "CopyFrom" }),
                Rules.Validate(new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "Set", Value = "000800:Skyrim.esm" }),
            }.All(s => s is not null && s.Contains("#350", StringComparison.Ordinal)),
            Rules.Validate(new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "Set", Value = "000800:Skyrim.esm" })!);

        // …and every one of the three tells the caller where the FormID it asks for comes from. MEASURED before it
        // was written: a depth-1 read renders "Landscape = [Landscape] — pass depth=2 to expand" and no id; depth 2
        // renders "[Landscape 000801:… editorid=…]". Without the depth the remedy is another dead end, which is the
        // shape these sentences exist to remove.
        Check("…and each names where the child's FormID comes from, with the depth that actually shows it",
            new[]
            {
                Rules.Validate(new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "Remove" }),
                Rules.Validate(new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "CopyFrom" }),
                Rules.Validate(new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "Set", Value = "000800:Skyrim.esm" }),
            }.All(s => s is not null && s.Contains("depth=2", StringComparison.Ordinal)),
            Rules.Validate(new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "CopyFrom" })!);

        // …and CopyFrom names the remedy that was MEASURED to work for this shape, not the one the list twin can
        // honestly offer: housecarl_forward carries a LAND and a worldspace's top cell; create with parent= is refused
        // for a singular child ("that parent models no child-collection that holds it"), so it must not appear here.
        var copyMsg = Rules.Validate(new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "CopyFrom" })!;
        Check("…and CopyFrom's refusal names housecarl_forward (measured) and NOT create with parent= (measured refused)",
            copyMsg.Contains("housecarl_forward", StringComparison.Ordinal)
            && !copyMsg.Contains("housecarl_create with parent=", StringComparison.Ordinal), copyMsg);

        // --- THE REFUSALS, RENDERED. All three arms of RestoreChildGroup are user-facing sentences that no arm had
        //     ever produced (round-1 review [low]) — and this file's own standing lesson is that a message shipped
        //     through green guards is an unmeasured claim. Driven directly, because the states they report are ones
        //     Mutagen cannot currently produce: a copy arriving with children, and a count that does not balance.
        var carrier = new DialogTopic(FormKey.Factory("123456:HcW2Master.esm"), SkyrimRelease.SkyrimSE);
        carrier.Responses.Add(new DialogResponses(FormKey.Factory("123457:HcW2Master.esm"), SkyrimRelease.SkyrimSE)
            { EditorID = "W324Arrived" });
        // The carry is CAPTURED but empty — a destination that held nothing of its own, which is the case where an
        // arriving set is an import rather than a clash. `default` would prove nothing here: it is uncaptured, and
        // the gate correctly declines to engage at all.
        var emptyCaptured = WriteEngine.CaptureChildGroup(
            new DialogTopic(FormKey.Factory("123459:HcW2Master.esm"), SkyrimRelease.SkyrimSE));
        var injection = WriteEngine.RestoreChildGroup(carrier, emptyCaptured, "Nothing was serialized; UNTOUCHED.");
        Check("refusal 1: a copy arriving with children of its own is refused as an IMPORT when nothing was held",
            injection is not null && injection.Contains("arrived carrying 1 child record(s)", StringComparison.Ordinal)
            && injection.Contains("W324Arrived", StringComparison.Ordinal)
            && injection.Contains("refuses rather than silently import", StringComparison.Ordinal)
            && injection.Contains("Nothing was serialized", StringComparison.Ordinal), injection ?? "(accepted)");

        var held = WriteEngine.CaptureChildGroup(carrier);
        var clash = WriteEngine.RestoreChildGroup(carrier, held, "Nothing was serialized; UNTOUCHED.");
        Check("refusal 2: …and as a two-sets clash when the destination held some too, naming both counts",
            clash is not null && clash.Contains("while the destination held 1", StringComparison.Ordinal)
            && clash.Contains("discard one of the two sets", StringComparison.Ordinal), clash ?? "(accepted)");

        // The count check, off a carry whose count does not match what is on the record — the shape a containment
        // path the reflected walk cannot see would produce. The lane clause must land BEFORE "please report".
        var empty = new DialogTopic(FormKey.Factory("123458:HcW2Master.esm"), SkyrimRelease.SkyrimSE);
        var miscount = WriteEngine.RestoreChildGroup(empty,
            held with { Count = 3, Names = new[] { "W324Ghost" } }, "Nothing was serialized; UNTOUCHED.");
        Check("refusal 3: a child count that does not survive the replace refuses, naming what it cannot account for",
            miscount is not null && miscount.Contains("it carries 3 child record(s)", StringComparison.Ordinal)
            && miscount.Contains("W324Ghost", StringComparison.Ordinal)
            && miscount.Contains("cannot account for", StringComparison.Ordinal)
            && miscount.IndexOf("Nothing was serialized", StringComparison.Ordinal)
               < miscount.IndexOf("Please report", StringComparison.Ordinal), miscount ?? "(accepted)");

        // …and the doubling those three used to ship: the lane appended its own untouched-clause to a message that
        // already carried one. Substituted now, so it appears once (round-1 review [low]).
        Check("…and each refusal states what was left alone exactly ONCE",
            CountOf(injection!, "Nothing was serialized") == 1 && CountOf(clash!, "Nothing was serialized") == 1
            && CountOf(miscount!, "Nothing was serialized") == 1,
            $"[{CountOf(injection!, "Nothing was serialized")}·{CountOf(clash!, "Nothing was serialized")}·{CountOf(miscount!, "Nothing was serialized")}]");
    }

    /// <summary>The placed references the written plugin's copy of the cell carries — <see cref="ChildLinesIn"/>'s
    /// twin for the nested-group path, walking the block/subblock chain a cell lives in.</summary>
    static List<string> CellRefsIn(string espPath, FormKey fk)
    {
        var found = new List<string>();
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE);
            var cell = CellsIn(ov).FirstOrDefault(c => c.FormKey == fk);
            foreach (var p in (cell?.Persistent ?? Enumerable.Empty<IPlacedGetter>())
                         .Concat(cell?.Temporary ?? Enumerable.Empty<IPlacedGetter>()))
                found.Add(p.EditorID ?? p.FormKey.ToString());
        }
        catch { found.Add("(unreadable)"); }
        finally { (ov as IDisposable)?.Dispose(); }
        return found;
    }

    static string? CellNameIn(string espPath, FormKey fk)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE);
            return CellsIn(ov).FirstOrDefault(c => c.FormKey == fk)?.Name?.String;
        }
        catch { return null; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    static IEnumerable<ICellGetter> CellsIn(ISkyrimModGetter ov) =>
        (ov.Cells.Records ?? Enumerable.Empty<ICellBlockGetter>())
            .SelectMany(b => b.SubBlocks ?? Enumerable.Empty<ICellSubBlockGetter>())
            .SelectMany(s => s.Cells ?? Enumerable.Empty<ICellGetter>());

    /// <summary>Every concrete major-record type Mutagen models for Skyrim — the NestedProbe enumeration, reused so
    /// the child-property pin is a by-construction sweep rather than a list of names someone remembered. Internal
    /// because corpus-hygiene-guard's INV6 sweeps the SAME set to check the shipped reference's classification
    /// against this walk (#335): two guards over one enumeration cannot drift apart the way two lists would.</summary>
    internal static List<Type> ConcreteRecordTypes() => typeof(Weapon).Assembly.GetTypes()
        .Where(t => t.IsClass && !t.IsAbstract && !t.Name.EndsWith("BinaryOverlay", StringComparison.Ordinal)
                    && typeof(IMajorRecord).IsAssignableFrom(t))
        .ToList();

    static int CountOf(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>The child-bearing property names WriteEngine's reflected walk finds for a type (internal via
    /// InternalsVisibleTo), sorted so the assertion does not depend on reflection's property order.</summary>
    static string Names(Type t) =>
        string.Join(",", WriteEngine.ChildBearingProperties(t).Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));

    /// <summary>Every child line the written plugin's copy of the topic carries — #300's "whose CHILDREN did the host
    /// copy" probe (a full-record override may bring the parent's child list with it, which widens the trade).</summary>
    static List<string> ChildLinesIn(string espPath, FormKey fk)
    {
        var found = new List<string>();
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE);
            foreach (var r in ov.DialogTopics.FirstOrDefault(d => d.FormKey == fk)?.Responses ?? Enumerable.Empty<IDialogResponsesGetter>())
                found.Add(r.EditorID ?? r.FormKey.ToString());
        }
        catch { found.Add("(unreadable)"); }
        finally { (ov as IDisposable)?.Dispose(); }
        return found;
    }

    /// <summary>The topic's Name as the written plugin carries it — #300's "whose fields did the host copy" probe.</summary>
    static string? TopicNameIn(string espPath, FormKey fk)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE);
            return ov.DialogTopics.FirstOrDefault(d => d.FormKey == fk)?.Name?.String;
        }
        catch { return null; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>One CopyFrom op as the wire array <c>housecarl_apply</c> reads, with the Windows path escaped for
    /// JSON (a raw backslash run is not a legal string body, and the failure would look like a locate miss).</summary>
    static string CopyOps(string formid, string fieldPath, string fromSource) =>
        $$"""[{"formid":"{{formid}}","field_path":"{{fieldPath}}","op":"CopyFrom","from_source":"{{fromSource.Replace("\\", "\\\\")}}"}]""";

    /// <summary>Set a weapon's damage in place (fixture surgery: move the LIVE patch on, so forwarding the parked
    /// copy's body back is a visible revert rather than a no-op).</summary>
    static void BumpDamage(string espPath, ushort damage)
    {
        var mod = SkyrimMod.CreateFromBinary(espPath, SkyrimRelease.SkyrimSE);
        foreach (var w in mod.Weapons) w.BasicStats = new WeaponBasicStats { Damage = damage };
        mod.BeginWrite.ToPath(espPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
    }

    /// <summary>The render's "masters:" line alone. A whole-render Contains would false-pass on the artifact's own
    /// name, which every write render prints several times (PR #311's scar: never assert negatively over a render).</summary>
    static string MastersLineOf(string render)
        => render.Split('\n').FirstOrDefault(l => l.StartsWith("masters:", StringComparison.Ordinal)) ?? "";

    // ================= ARM 5 — TRANSPORT =================
    static void TransportArm(Fixture fx)
    {
        Console.WriteLine("── ARM 5: TRANSPORT — json documents (refusals included) and the §2.1.1 epoch everywhere ──");

        // A SUCCESS renders as valid json carrying the same facts.
        var createJson = CreateTools.Create(fx.Svc, patch: "W2Json",
            records: Json("""[{"record_type":"Keyword","editorid":"W2JsonKw"}]"""), format: "json");
        Check("create format=json: a valid document with ok/lane/created and the epoch",
            TryJson(createJson, out var cdoc)
            && cdoc!.RootElement.GetProperty("ok").GetBoolean()
            && cdoc.RootElement.GetProperty("created")[0].GetProperty("editorid").GetString() == "W2JsonKw"
            && cdoc.RootElement.GetProperty("epoch").ValueKind == JsonValueKind.String, createJson);

        // The UNCONFIGURED-MO2 prompt is a refusal a json caller can parse, on every one of the four write tools
        // (PR #311 review 5 [low]). It used to be returned verbatim — a prose block — because it was checked before
        // `format` was read; `JsonDocument.Parse` throws on it, so a json client got neither `ok` nor `error`. The
        // fixture always configures, which is exactly why nothing caught this: this arm builds an UNCONFIGURED
        // service on purpose (WithInstance(null, …)) rather than relying on the shared one.
        {
            var bare = LoadOrderService.WithInstance(null, 0, new UserConfigStore(Path.Combine(fx.ModsDir, "..", "hc-unconfigured.user.json")));
            var unconfigured = new (string tool, string render)[]
            {
                ("create",    CreateTools.Create(bare, records: Json("""[{"record_type":"Keyword","editorid":"X"}]"""), format: "json")),
                ("remove",    RemoveTools.Remove(bare, formids: new[] { fx.SubjectFid }, into: "X.esp", format: "json")),
                ("forward",   ForwardTools.Forward(bare, formids: new[] { fx.SubjectFid }, source: fx.MasterName, format: "json")),
                ("apply",     ApplyTools.Apply(bare, ops: Json($$"""[{"formid":"{{fx.SubjectFid}}","field_path":"Name","value":"x"}]"""), format: "json")),
                ("write_seq", SeqTools.WriteSeq(bare, source: fx.MasterName, format: "json")),
            };
            foreach (var (tool, render) in unconfigured)
                Check($"{tool} format=json: the unconfigured-MO2 prompt is a DOCUMENT, not prose",
                    TryJson(render, out var udoc)
                    && udoc!.RootElement.TryGetProperty("error", out var uerr)
                    && uerr.GetString() is { } umsg
                    && umsg.Contains("no Mod Organizer 2 instance configured", StringComparison.Ordinal), $"{tool}: {render}");

            // …and the TEXT lane still gets the trained prose verbatim — the prompt teaches the user what to do, and
            // wrapping it in json for a text caller would be the opposite mistake.
            var bareText = CreateTools.Create(bare, records: Json("""[{"record_type":"Keyword","editorid":"X"}]"""));
            Check("create text: the unconfigured-MO2 prompt stays the trained prose block",
                bareText.Contains("no Mod Organizer 2 instance configured", StringComparison.Ordinal)
                && !bareText.TrimStart().StartsWith("{", StringComparison.Ordinal), bareText);
        }

        // A null ELEMENT inside a spec's ops= is legal JSON that STJ passes straight through (PR #311 review 7
        // [low]): it used to NRE and be answered as "an internal houseCARL failure (the arguments bound fine) —
        // retry once", which is wrong on both counts and loops a caller over deterministic bad input. The arm
        // asserts the by-name refusal AND that the internal-fault wording is gone.
        var nullOp = CreateTools.Create(fx.Svc, patch: "W2NullOp", records: Json("""
            [{"record_type":"Keyword","editorid":"W2NullOp","ops":[null]}]
            """));
        Check("create: a null op element is refused BY NAME, never as an 'internal failure — retry once'",
            nullOp.StartsWith("error:", StringComparison.Ordinal)
            && nullOp.Contains("records[0]: ops[0] is null", StringComparison.Ordinal)
            && !nullOp.Contains("internal houseCARL failure", StringComparison.Ordinal)
            && !nullOp.Contains("Retry once", StringComparison.OrdinalIgnoreCase), nullOp);

        // Refusals below the tool layer name THIS surface's words, index label included (PR #311 review 6 [low]).
        // `records[0]` was already the caller's spelling via the origins thread; `op[0]` was nobody's — the member
        // is ops=, and in a generated batch of hundreds the index label IS the navigational handle.
        var badOpIndex = CreateTools.Create(fx.Svc, patch: "W2OpLbl", records: Json("""
            [{"record_type":"Keyword","editorid":"W2OpLbl","ops":[{"value":"x"}]}]
            """));
        Check("create: a malformed op is labelled ops[i] — the caller's own member — never op[i]",
            badOpIndex.Contains("records[0]: ops[0]:", StringComparison.Ordinal)
            && !badOpIndex.Contains("op[0]:", StringComparison.Ordinal), badOpIndex);

        // …and the create-side CopyFrom refusal names what the caller actually wrote. It is reachable because the
        // strict reader gates undeclared MEMBERS and `op` IS declared, so op="CopyFrom" arrives at the engine —
        // where the old text answered with from_plugin, which CreateFieldOp does not declare.
        var copyOnCreate = CreateTools.Create(fx.Svc, patch: "W2CopyCre", records: Json("""
            [{"record_type":"Keyword","editorid":"W2CopyCre","ops":[{"field_path":"EditorID","op":"CopyFrom"}]}]
            """));
        Check("create: the CopyFrom refusal names op=\"CopyFrom\", not the undeclared from_plugin",
            copyOnCreate.Contains("op=\"CopyFrom\" copies from an EXISTING record", StringComparison.Ordinal)
            && !copyOnCreate.Contains("from_plugin", StringComparison.Ordinal), copyOnCreate);

        // A REFUSAL is a document too — a json caller must never have to parse "error: …" out of a string. (This
        // is the pre-engine refusal path, which is exactly where PR #306/#310 found an EMPTY string twice.)
        // NOTE the shape: a PRE-ENGINE refusal renders through JsonWire.RenderError, which carries {error, epoch}
        // and NOT the `ok` discriminant the outcome-borne renders emit. That asymmetry is the known, reviewer-
        // scoped-out gap filed in dev/BACKLOG.md (a ~39-call-site sweep, W3 PR 3) — so this arm asserts the
        // REASON is present and machine-readable, and deliberately does not assert `ok` on this path. When the
        // sweep lands, these two asserts tighten to ok:false.
        var createRefusal = CreateTools.Create(fx.Svc, records: Json("[]"), format: "json");
        Check("create format=json: a REFUSAL is a document carrying the reason, never an empty string",
            createRefusal.Length > 0 && TryJson(createRefusal, out var rdoc)
            && rdoc!.RootElement.GetProperty("error").GetString()!.Contains("empty array"), createRefusal);

        var removeRefusal = RemoveTools.Remove(fx.Svc, formids: new[] { fx.SubjectFid }, format: "json");
        Check("remove format=json: the no-lane refusal is a document too",
            removeRefusal.Length > 0 && TryJson(removeRefusal, out var rmdoc)
            && rmdoc!.RootElement.GetProperty("error").GetString()!.Contains("in_place="), removeRefusal);

        // readback_full describes the DOCUMENT; readback_requested keeps the caller's ask (PR #311 review 7 [nit]).
        // The in-place lane forces the read-back in the service, so the two genuinely differ there — which is the
        // case that used to publish readback_full:false beside a complete field dump.
        var rbForced = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.MasterName,
            in_place: fx.ReplacerName, acknowledge: true, readback: false, format: "json");
        Check("json: an in-place lane that FORCED the read-back reports readback_full:true, ask kept separately",
            TryJson(rbForced, out var rbdoc)
            && rbdoc!.RootElement.TryGetProperty("readback_full", out var rbf) && rbf.GetBoolean()
            && rbdoc.RootElement.TryGetProperty("readback_requested", out var rbr) && !rbr.GetBoolean()
            && rbdoc.RootElement.TryGetProperty("readback", out var rbarr)
            && rbarr.GetArrayLength() > 0
            && rbarr[0].TryGetProperty("fields", out _), rbForced);

        var forwardJson = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.MasterName,
            patch: "W2FwdJson", format: "json");
        Check("forward format=json: forwarded rows carry source, prior_winner and the two per-record flags",
            TryJson(forwardJson, out var fdoc)
            && fdoc!.RootElement.GetProperty("forwarded")[0].GetProperty("source").GetString() == fx.MasterName
            && fdoc.RootElement.GetProperty("forwarded")[0].TryGetProperty("was_already_winner", out _)
            && fdoc.RootElement.GetProperty("epoch").ValueKind == JsonValueKind.String, forwardJson);

        // An OUTCOME-borne refusal (past the tool layer, into the service) renders through the outcome renderer,
        // so it DOES carry ok:false. The epoch splits by WHERE the refusal was decided, which is the §2.1.1
        // contract stated positively rather than "epoch everywhere":
        //   * a LANE-resolution refusal (no such patch to extend) is decided off the mod FOLDERS, before any
        //     build is consulted — epoch NULL, because stamping it would claim evidence never read;
        var noSuchPatch = RemoveTools.Remove(fx.Svc, formids: new[] { fx.SubjectFid }, into: "NoSuchPatch.esp", format: "json");
        Check("a lane-resolution refusal is a document with ok:false and a NULL epoch (it consulted no build)",
            TryJson(noSuchPatch, out var nsdoc)
            && !nsdoc!.RootElement.GetProperty("ok").GetBoolean()
            && nsdoc.RootElement.GetProperty("epoch").ValueKind == JsonValueKind.Null, noSuchPatch);

        //   * a refusal decided INSIDE the engine, after the capture, carries that build's stamp. (Authored here
        //     rather than reusing arm 3's patch so the two arms cannot pass on one observable.)
        var made = CreateTools.Create(fx.Svc, patch: "W2Epoch", records: Json("""[{"record_type":"Keyword","editorid":"W2EpochKw"}]"""));
        var madePath = ArtifactPathFrom(fx, made);
        var notCarried = RemoveTools.Remove(fx.Svc, formids: new[] { fx.SubjectFid },
            into: madePath is null ? "W2Epoch.esp" : Path.GetFileName(madePath), format: "json");
        Check("a refusal decided AFTER the engine's capture carries that build's epoch",
            TryJson(notCarried, out var ncdoc)
            && !ncdoc!.RootElement.GetProperty("ok").GetBoolean()
            && ncdoc.RootElement.GetProperty("error").GetString()!.Contains("not carried by patch")
            && ncdoc.RootElement.GetProperty("epoch").ValueKind == JsonValueKind.String, notCarried);

        // `lane` names the lane the CALL asked for, on a refusal as much as on a success (PR #311 review
        // [medium]): Fail/NeedsAck leave InPlace/Extended at their defaults, so a lane DERIVED from the outcome
        // reported "patch" for a consent prompt that exists only because the caller named in_place=, and for an
        // into= refusal on a call that named no patch= at all.
        // The CONSENT PROMPT is the reviewer's own example, and the sharpest case: it exists ONLY because the
        // caller named in_place=. The master has not been acknowledged (ARM 2 acknowledged the replacer), so this
        // is a real first touch.
        var lanePrompt = CreateTools.Create(fx.Svc, records: Json("""[{"record_type":"Keyword","editorid":"W2LaneKw2"}]"""),
            in_place: fx.MasterName, format: "json");
        Check("json lane on the in-place CONSENT PROMPT says in_place, not the patch lane the caller never named",
            TryJson(lanePrompt, out var lpdoc)
            && lpdoc!.RootElement.GetProperty("needs_acknowledge").GetBoolean()
            && lpdoc.RootElement.GetProperty("lane").GetString() == "in_place", lanePrompt);

        var laneRefusal = RemoveTools.Remove(fx.Svc, formids: new[] { fx.SubjectFid }, into: "NoSuchPatch2.esp", format: "json");
        Check("json lane on an into= REFUSAL says into, not the patch lane (Fail leaves the outcome flags at default)",
            TryJson(laneRefusal, out var ldoc) && ldoc!.RootElement.GetProperty("lane").GetString() == "into", laneRefusal);

        var laneInPlace = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.MasterName,
            in_place: "NotAPlugin.esp", format: "json");
        Check("json lane on a service-side in-place refusal says in_place too",
            TryJson(laneInPlace, out var lidoc) && lidoc!.RootElement.GetProperty("lane").GetString() == "in_place", laneInPlace);

        // ONE lane vocabulary across all four tools (PR #311 round-2 review [low]): the value NAMES the parameter
        // that selected the lane, so an into= call answers "into" everywhere — a json client that learned the
        // words from apply must not fall into its unknown branch on remove (or the reverse, as it did when apply
        // said "extend" and remove said "into" for the same lane).
        var laneCreateInto = CreateTools.Create(fx.Svc, records: Json("""[{"record_type":"Keyword","editorid":"W2LaneKw3"}]"""),
            into: "NoSuchPatch3.esp", format: "json");
        var laneFwdInto = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.MasterName,
            into: "NoSuchPatch3.esp", format: "json");
        Check("the into= lane is spelled the same on create / forward / remove — the parameter's own name",
            TryJson(laneCreateInto, out var lcdoc) && lcdoc!.RootElement.GetProperty("lane").GetString() == "into"
            && TryJson(laneFwdInto, out var lfdoc) && lfdoc!.RootElement.GetProperty("lane").GetString() == "into",
            laneCreateInto + " || " + laneFwdInto);

        // The three post-write REPORTS are inside the json budget (PR #311 round-2 review [low-medium]). Rendered
        // DIRECTLY off a synthetic outcome rather than through a create call: a report big enough to blow a
        // ceiling means dozens of voiced lines, and building those in the fixture would pin the budget behind a
        // pile of unrelated dialogue plumbing. The claim under test is the RENDERER's — the reports were outside
        // the cap and the document still closed with truncated:false.
        var voiced = Enumerable.Range(0, 40).Select(i => new VoiceLine(
            default, "W2VoiceTopic", i,
            $@"sound\voice\W2.esp\MaleNord\W2VoiceTopic_{i:D4}.fuz", false, null, false,
            $@"sound\voice\W2.esp\MaleNord\W2VoiceTopic_{i:D4}.lip", false,
            false)).ToList();
        var synthetic = new WritePatchBuilder.CreateOutcome(
            true, null, @"C:\mods\W2Rep\W2Rep.esp", false,
            new[] { new WritePatchBuilder.CreatedRecord(default, "DialogResponses", "W2RepL1", Array.Empty<WritePatchBuilder.OpResult>()) },
            Array.Empty<string>(), 512)
            { Epoch = "deadbeefdeadbeef", Voice = new VoiceReport(voiced, Array.Empty<VoiceUndetermined>()) };

        var reportFull = JsonWire.RenderCreateOutcome(synthetic, 0, false, "patch");
        Check("json create: the voice-coverage report is emitted in full when the budget allows (truncated:false)",
            TryJson(reportFull, out var rfdoc)
            && rfdoc!.RootElement.GetProperty("voice_coverage").GetProperty("lines").GetArrayLength() == 40
            && !rfdoc.RootElement.GetProperty("truncated").GetBoolean(), reportFull);

        var reportCapped = JsonWire.RenderCreateOutcome(synthetic, 1200, false, "patch");
        Check("json create: a ceiling the REPORTS blow past is reported as truncated:true, with the rows dropped",
            TryJson(reportCapped, out var rcdoc)
            && rcdoc!.RootElement.GetProperty("truncated").GetBoolean()
            && rcdoc.RootElement.GetProperty("voice_coverage").GetProperty("lines").GetArrayLength() < 40, reportCapped);

        // …and the CUT BLOCK says so itself (PR #311 review 5 [medium]). Document-level truncated:true does not name
        // which block lost rows, and a consumer doing `lines.some(l => !l.fuz_present)` on a silently emptied array
        // gets false — reporting every created line as voiced, the exact inversion of what this block is for.
        // TryGetProperty throughout: without the fix these members do not exist, and GetProperty would throw —
        // reporting a CRASHED probe instead of the finding (the same scar the write_seq json arm hit).
        Check("json create: a CUT voice block carries its own census (total vs rendered) and names the stakes",
            Num(rcdoc, "voice_coverage", "total_lines") == 40
            && Num(rcdoc, "voice_coverage", "rendered_lines") is { } vr && vr < 40
            && Flag(rcdoc, "voice_coverage", "truncated") == true
            && Str(rcdoc, "voice_coverage", "truncated_note") is { } vnote
            && vnote.Contains(WriteSentences.Twins.VoiceStake, StringComparison.Ordinal)
            // and it must NOT prescribe re-issuing: this block rides a WRITE render.
            && !vnote.Contains("raise max_chars", StringComparison.OrdinalIgnoreCase), reportCapped);

        // The TEXT twins of those blocks must not contradict the json census (PR #311 review 7 [medium]): they rode
        // the create render still saying "raise max_chars to see the rest", i.e. re-issue a create — while the json
        // block added one round earlier said "Do NOT re-issue the write to widen this". One call, two transports,
        // opposite advice. Both now read WriteSentences.Twins.ReportBlockCut, so this pins the CONSTRUCTION: the
        // sentence itself still refuses to prescribe the re-issue, and this render is reading that sentence.
        // Rendered directly, since the shared fixture's creates make no dialogue lines.
        var voiceCappedText = WriteTools.RenderCreate(synthetic, maxChars: 900);
        Check("create text: the voice-coverage cut notice refuses to prescribe re-issuing the create",
            voiceCappedText.Contains("voice coverage truncated", StringComparison.Ordinal)
            && Bracket(voiceCappedText, "voice coverage truncated") is { } vct
            && WriteSentences.Twins.ReportBlockCut.Contains("Do NOT re-issue the create", StringComparison.Ordinal)
            && vct.Contains(WriteSentences.Twins.ReportBlockCut, StringComparison.Ordinal)
            && !vct.Contains("raise max_chars to see the rest", StringComparison.Ordinal), voiceCappedText);

        // The cell-shell block was the LAST unbudgeted one on the write renders (PR #311 review 7 [low-medium],
        // Aaron-go): text rendered every cell and took the silent host cut while its json twin stopped at the cap.
        // Both poles asserted, and the two Q3 notes below the list must survive the cut — they are the accounting a
        // truncated report still needs, and the grid-occupancy seam in particular must not be what a cut swallows.
        var cells = Enumerable.Range(0, 30).Select(i => new CellShell(
            default, $"W2CellShell{i:D2}", i % 2 == 0,
            new[] { "lighting template", "terrain / landscape", "water height", "navmesh", "an encounter zone" })).ToList();
        var cellOutcome = synthetic with { Voice = null, CellShell = new CellShellReport(cells) };

        var cellCapped = WriteTools.RenderCreate(cellOutcome, maxChars: 700);
        Check("create text: the cell-shell block is BUDGETED, with an explicit notice (it was the last unbudgeted one)",
            cellCapped.Contains("cell shell truncated: rendered ", StringComparison.Ordinal)
            && cellCapped.Contains(" of 30 cell(s)", StringComparison.Ordinal)
            && !cellCapped.Contains("W2CellShell29", StringComparison.Ordinal), cellCapped);
        Check("create text: a CUT cell-shell block still renders the grid-occupancy seam below it",
            cellCapped.Contains(WriteSentences.Twins.GridOccupancy, StringComparison.Ordinal), cellCapped);

        var cellFull = WriteTools.RenderCreate(cellOutcome);
        Check("create text: without a cap every cell renders (the budget is not a permanent cut)",
            cellFull.Contains("W2CellShell29", StringComparison.Ordinal)
            && !cellFull.Contains("cell shell truncated", StringComparison.Ordinal), cellFull);

        // The counts ride on the COMPLETE render too — rendered == total is the positive statement that the list is
        // whole, so a consumer never infers completeness from a missing marker.
        Check("json create: an UNCUT voice block still carries the census, with truncated:false",
            Num(rfdoc, "voice_coverage", "total_lines") == 40
            && Num(rfdoc, "voice_coverage", "rendered_lines") == 40
            && Flag(rfdoc, "voice_coverage", "truncated") == false, reportFull);

        // The create hazard does not care which transport asked (PR #311 review 4 [medium]): the text twin was moved
        // off "raise max_chars to see the rest" one fold earlier and the json document kept it, so a json client
        // could raise the ceiling, re-issue, and allocate the records a second time. D2 — same remedy, both renders.
        Check("json create: truncated_note points at the read-back call, never at raising max_chars",
            rcdoc is not null
            && rcdoc.RootElement.GetProperty("truncated_note").GetString() is { } jnote
            && jnote.Contains("housecarl_records source=", StringComparison.Ordinal)
            && jnote.Contains("types=[", StringComparison.Ordinal)
            && jnote.Contains("allocates the records AGAIN", StringComparison.Ordinal)
            && !jnote.Contains("raise max_chars", StringComparison.Ordinal), reportCapped);

        // max_chars reaches the TEXT render too, not only json (PR #311 review [medium] / [low-medium]): the
        // parameter's own description promises trailing rows drop with an explicit notice, and removal is
        // set-valued, so the unbounded list is the expected case rather than an edge.
        var capMade = CreateTools.Create(fx.Svc, patch: "W2Cap", records: Json("""
            [{"record_type":"Keyword","editorid":"W2CapA"},
             {"record_type":"Keyword","editorid":"W2CapB"},
             {"record_type":"Keyword","editorid":"W2CapC"}]
            """));
        var capPath = ArtifactPathFrom(fx, capMade);
        var capIds = FormIdsFrom(capMade);
        if (capPath is not null && capIds.Count == 3)
        {
            var capped = RemoveTools.Remove(fx.Svc, formids: capIds.ToArray(), into: Path.GetFileName(capPath), max_chars: 100);
            Check("remove text render: max_chars= drops trailing rows with an explicit notice (never a silent host cut)",
                capped.Contains("[truncated:", StringComparison.Ordinal)
                && capped.Contains("max_chars=100", StringComparison.Ordinal)
                && capped.Contains("every one WAS removed", StringComparison.Ordinal), capped);

            // …and the remedy is EXECUTABLE (PR #311 review 6 [medium]): "raise max_chars" named the one call
            // guaranteed to fail here, since a repeat is refused as 'not carried by' the file. The notice states
            // that the rows are the passed formids= instead. Asserted with its negative, and with the refusal
            // PROVEN below rather than assumed.
            Check("remove's truncation remedy is executable — it does not prescribe the re-issue that gets refused",
                capped.Contains("exactly the formids= you passed", StringComparison.Ordinal)
                && capped.Contains("a repeat is refused", StringComparison.Ordinal)
                && !capped.Contains("raise max_chars", StringComparison.Ordinal), capped);

            var repeat = RemoveTools.Remove(fx.Svc, formids: capIds.ToArray(), into: Path.GetFileName(capPath), max_chars: 400000);
            Check("…and the re-issue the OLD remedy prescribed really is refused (the dead end, proven)",
                repeat.StartsWith("error:", StringComparison.Ordinal)
                && repeat.Contains("NOTHING removed", StringComparison.Ordinal), repeat);

            // The json twin needs its OWN patch: the removals above already consumed capPath's records, so re-using
            // them here would assert against a refusal document instead of a truncation note.
            var capMadeJ = CreateTools.Create(fx.Svc, patch: "W2CapJ", records: Json("""
                [{"record_type":"Keyword","editorid":"W2CapJA"},
                 {"record_type":"Keyword","editorid":"W2CapJB"},
                 {"record_type":"Keyword","editorid":"W2CapJC"}]
                """));
            var capPathJ = ArtifactPathFrom(fx, capMadeJ);
            var capIdsJ = FormIdsFrom(capMadeJ);
            if (capPathJ is not null && capIdsJ.Count == 3)
            {
                var cappedJson = RemoveTools.Remove(fx.Svc, formids: capIdsJ.ToArray(), into: Path.GetFileName(capPathJ),
                    max_chars: 100, format: "json");
                Check("remove format=json: the truncation note carries the SAME remedy as its text twin (D2)",
                    TryJson(cappedJson, out var rjdoc)
                    && rjdoc!.RootElement.TryGetProperty("truncated_note", out var rjn)
                    && rjn.GetString() is { } rjnote
                    && rjnote.Contains("exactly the formids= you passed", StringComparison.Ordinal)
                    && !rjnote.Contains("raise max_chars", StringComparison.Ordinal), cappedJson);
            }
            else Check("remove json remedy arm: fixture (a second patch to remove from)", false, capMadeJ);
        }
        else Check("remove text render: fixture for the max_chars arm", false, capMade);

        // The SAME budget on the two remaining set-valued row blocks (PR #311 review 3 [medium] x2): create's
        // created-records block is the render's largest, forward's rows are the longest, and both json twins
        // already truncate the identical arrays — so an unbounded text lane made the two renders disagree about
        // the same call, with text taking the silent host-side cut.
        var createCapped = CreateTools.Create(fx.Svc, patch: "W2CreCap", max_chars: 130, records: Json("""
            [{"record_type":"Keyword","editorid":"W2CreCapA"},
             {"record_type":"Keyword","editorid":"W2CreCapB"},
             {"record_type":"Keyword","editorid":"W2CreCapC"}]
            """));
        Check("create text render: max_chars= drops trailing created rows with an explicit notice",
            createCapped.Contains("[truncated:", StringComparison.Ordinal)
            && createCapped.Contains(WriteSentences.RowsCutOperationIntact(false, "created"), StringComparison.Ordinal)
            && !createCapped.Contains("W2CreCapC", StringComparison.Ordinal), createCapped);

        // The remedy must be a READ, never "re-run this call" (PR #311 review 3 round-2 [medium]): repeating a
        // truncated CREATE allocates the records a second time — a second auto-suffixed patch, or under into= a
        // re-create at the same FormID with the prior contents discarded. Asserted as a positive AND the absence
        // of the sibling renders' wording, which is what made this dangerous here.
        // …and the remedy must be a call records ACCEPTS (PR #311 review 4 [medium]): source= is the SOURCE pole,
        // not a SELECT term, so the first spelling of this notice named a call that dies on "select something" —
        // leaving re-issuing the create as the only obvious route, i.e. straight back into the trap. The SELECT
        // term is asserted by name here and EXERCISED two arms below.
        Check("create's truncation notice points at a READ, never at raising max_chars (a repeat would re-create)",
            createCapped.Contains("housecarl_records source=", StringComparison.Ordinal)
            && createCapped.Contains("types=[\"Keyword\"]", StringComparison.Ordinal)
            // A CONSTRUCTION pin: the trap sentence is one source both transports read, so this asserts the
            // sentence still names the re-allocation AND that this render is reading it.
            && WriteSentences.Twins.CreateReissueTrap.Contains("allocates the records AGAIN", StringComparison.Ordinal)
            && createCapped.Contains(WriteSentences.Twins.CreateReissueTrap, StringComparison.Ordinal)
            && !createCapped.Contains("raise max_chars", StringComparison.Ordinal), createCapped);

        // The remedy EXERCISED — parsed OUT of the notice this render just produced and RUN, rather than compared
        // against a literal. That is the difference that matters here: the arm this replaces asserted the string
        // "housecarl_records source=", which is precisely why CI vouched for a call records refuses. An arm that
        // executes the emitted call cannot go stale against a reworded remedy.
        var (remedyFile, remedyTypes) = ParseReadBackCall(createCapped);
        if (remedyFile is not null && remedyTypes is { Length: > 0 })
        {
            var remedy = RecordsTools.Records(fx.Svc, source: Json($"\"{remedyFile}\""), types: remedyTypes);
            Check("create's truncation remedy, RUN as emitted, resolves and returns the row the render cut",
                !remedy.StartsWith("error:", StringComparison.Ordinal)
                && remedy.Contains("W2CreCapC", StringComparison.Ordinal), remedy);

            // …and the SELECT term is load-bearing, not decoration: the same call with source= alone — the shape
            // the notice used to name — dies on the lane decision. This is the fact the old remedy walked into.
            var bareSource = RecordsTools.Records(fx.Svc, source: Json($"\"{remedyFile}\""));
            Check("records: source= ALONE selects nothing, so a remedy without a SELECT term is a dead end",
                bareSource.StartsWith("error:", StringComparison.Ordinal)
                && bareSource.Contains("select something", StringComparison.Ordinal), bareSource);
        }
        else Check("create's truncation notice emits a parseable source=+types= read-back call", false, createCapped);

        // …and with a cap so small that NO row renders, the closing line must not point at "the new FormID above".
        var createAllCut = CreateTools.Create(fx.Svc, patch: "W2CreCut", max_chars: 1, records: Json("""
            [{"record_type":"Keyword","editorid":"W2CreCutA"},
             {"record_type":"Keyword","editorid":"W2CreCutB"}]
            """));
        Check("create text render: with EVERY row cut, the render stops claiming a FormID it never printed",
            createAllCut.Contains("truncated: 0 of 2", StringComparison.Ordinal)
            && !createAllCut.Contains("the new FormID above", StringComparison.Ordinal)
            && createAllCut.Contains("all 2 WERE created", StringComparison.Ordinal), createAllCut);

        var createUncapped = CreateTools.Create(fx.Svc, patch: "W2CreFull", records: Json("""
            [{"record_type":"Keyword","editorid":"W2CreFullA"},
             {"record_type":"Keyword","editorid":"W2CreFullB"},
             {"record_type":"Keyword","editorid":"W2CreFullC"}]
            """));
        Check("create text render: without a cap every created row is listed",
            createUncapped.Contains("W2CreFullC", StringComparison.Ordinal)
            && !createUncapped.Contains("[truncated:", StringComparison.Ordinal), createUncapped);

        var fwdCapped = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.MasterName,
            patch: "W2FwdCap", max_chars: 120);
        Check("forward text render: max_chars= drops trailing forwarded rows with an explicit notice",
            fwdCapped.Contains("[truncated:", StringComparison.Ordinal)
            && fwdCapped.Contains("every one WAS forwarded", StringComparison.Ordinal), fwdCapped);

        // The remedy is LANE-AWARE (PR #311 review 5 [low]): "a repeated forward re-copies identical bodies" holds
        // on into=/in_place=, but the DEFAULT lane re-issues into a fresh UniqueStem — a second patch mod carrying
        // the same overrides. Both poles asserted, so a future uniform-wording regression fails one of them.
        Check("forward's truncation remedy on the DEFAULT lane names into=, not a bare re-issue",
            fwdCapped.Contains("SECOND patch", StringComparison.Ordinal)
            && fwdCapped.Contains("pass into=", StringComparison.Ordinal), fwdCapped);

        var fwdIntoFile = ArtifactPathFrom(fx, ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid },
            source: fx.MasterName, patch: "W2FwdInto")) is { } fip ? Path.GetFileName(fip) : null;
        if (fwdIntoFile is not null)
        {
            var fwdIntoCapped = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.MasterName,
                into: fwdIntoFile, max_chars: 120);
            Check("forward's truncation remedy on into= is the plain one (a re-issue there is idempotent)",
                fwdIntoCapped.Contains("raise max_chars to see the rest", StringComparison.Ordinal)
                && !fwdIntoCapped.Contains("SECOND patch", StringComparison.Ordinal), fwdIntoCapped);
        }
        else Check("forward into= remedy arm: fixture (a patch to extend)", false, "no patch path");

        // apply's json render budgets its `ops` array and carried the same "raise max_chars" the other four write
        // renders were moved off (PR #311 review 6 — an unrequested sibling, declared on the PR): apply shares the
        // LANE axis with forward, so it shares the lane-aware remedy. Default lane here ⇒ the into= wording.
        var applyCapped = ApplyTools.Apply(fx.Svc, patch: "W2ApCap", format: "json", max_chars: 300,
            ops: Json($$"""[{"formid":"{{fx.SubjectFid}}","field_path":"Name","value":"W2ApCapName"}]"""));
        Check("apply format=json: the truncation note carries the lane-aware remedy, not a bare 'raise max_chars'",
            TryJson(applyCapped, out var apdoc)
            && apdoc!.RootElement.TryGetProperty("truncated_note", out var apn)
            && apn.GetString() is { } apnote
            && apnote.Contains("pass into=", StringComparison.Ordinal)
            && apnote.Contains("SECOND patch mod", StringComparison.Ordinal)
            && !apnote.Contains("raise max_chars to see the rest", StringComparison.Ordinal), applyCapped);

        // IN_PLACE is its own pole (PR #311 review 6 [low]): the first pass lumped it with into=, but a re-issue
        // there re-serializes the caller's OWN plugin — the file this render just called backup-less — purely to
        // widen a display. It gets the read-back remedy instead. (fx.ReplacerName was acknowledged by an arm
        // above, so no consent prompt here.)
        var fwdInPlaceCapped = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.MasterName,
            in_place: fx.ReplacerName, acknowledge: true, max_chars: 120);
        // The negative is scoped to the ROW notice, not the whole render: an in-place forward forces the full
        // read-back, whose own (pre-existing, shared) hint still leads with "raise max_chars" — a separate site,
        // raised with the reviewer rather than folded in silently here.
        var fwdRowNotice = Bracket(fwdInPlaceCapped, "every one WAS forwarded — ");
        Check("forward's in-place truncation remedy points at a READ, never at re-writing the caller's original",
            fwdRowNotice is not null
            && fwdRowNotice.Contains("housecarl_records source=", StringComparison.Ordinal)
            && fwdRowNotice.Contains("re-serialize your ORIGINAL file", StringComparison.Ordinal)
            && !fwdRowNotice.Contains("raise max_chars", StringComparison.Ordinal), fwdInPlaceCapped);

        // write_seq's text lane, same contract — asserted on a REAL quest list rather than the fixture's
        // no-SGE plugin, because an arm that never renders a row cannot pin a row budget (the happy-path-only
        // scar). The render is exercised directly over a synthetic outcome: three quests, a cap that fits one.
        var seqOutcome = new SeqOutcome(true, null, @"C:\mods\HcSeq\SEQ\HcSeq.seq", "HcSeq",
            new[]
            {
                new HousecarlCore.SeqFile.SeqQuest(default, "HcSeqQuestAlpha",   0x01000800),
                new HousecarlCore.SeqFile.SeqQuest(default, "HcSeqQuestBravo",   0x01000801),
                new HousecarlCore.SeqFile.SeqQuest(default, "HcSeqQuestCharlie", 0x01000802),
            },
            "HcSeq.esp", false);
        var seqCapped = SeqTools.Render(seqOutcome, maxChars: 80);
        Check("write_seq text render: max_chars= drops trailing quest rows with an explicit notice",
            seqCapped.Contains("[truncated:", StringComparison.Ordinal)
            && seqCapped.Contains("max_chars=80", StringComparison.Ordinal)
            && seqCapped.Contains(WriteSentences.Twins.SeqListCutRemedy, StringComparison.Ordinal)
            && !seqCapped.Contains("HcSeqQuestCharlie", StringComparison.Ordinal), seqCapped);
        // …and the notice must not prescribe a re-run: widening the ceiling re-runs a WRITE, which with no lane
        // named for a plugin outside a houseCARL folder cuts a SECOND auto-suffixed folder holding a duplicate
        // .seq. Nothing is missing from the file, so the notice says that and prices the re-run instead.
        // A CONSTRUCTION pin: the remedy is one source both transports read, so the claims are asserted ON the
        // sentence — nothing is missing from the FILE, and a re-run is priced rather than prescribed — and the
        // render is asserted to be reading it.
        Check("write_seq's truncation notice prices the re-run instead of prescribing 'raise max_chars'",
            WriteSentences.Twins.SeqListCutRemedy.Contains("nothing is missing from the FILE", StringComparison.Ordinal)
            && WriteSentences.Twins.SeqListCutRemedy.Contains("writes the .seq again", StringComparison.Ordinal)
            && seqCapped.Contains(WriteSentences.Twins.SeqListCutRemedy, StringComparison.Ordinal)
            && !seqCapped.Contains("raise max_chars", StringComparison.Ordinal), seqCapped);

        var seqUncapped = SeqTools.Render(seqOutcome);
        Check("write_seq text render: without a cap every quest row is listed (the notice is not a permanent cut)",
            seqUncapped.Contains("HcSeqQuestCharlie", StringComparison.Ordinal)
            && !seqUncapped.Contains("[truncated:", StringComparison.Ordinal), seqUncapped);

        // The absent epoch is stated on the TEXT transport too (PR #311 review 6 [low]) — the class doc claimed
        // "the render says so", which was true of the json twin only, leaving the DEFAULT transport unable to tell
        // "no build was consulted, by design" from "the stamp was dropped" (the same observable either way).
        Check("write_seq text render: the ABSENT epoch is stated with its reason, like the json twin (D2)",
            seqUncapped.Contains("no epoch on this call", StringComparison.Ordinal)
            && seqUncapped.Contains("load-order-independent", StringComparison.Ordinal), seqUncapped);

        // …and the json twin says the SAME thing (PR #311 review 5 [medium]): the review-4 fold moved the text
        // notice off "raise max_chars" and left the json document on it, so the guard's teeth were on one lane
        // only — the exact D2 divergence that fold had just fixed one renderer up. Rendered directly, because the
        // fixture's plugin has no SGE quests and so cannot truncate a quest list through the tool.
        var seqJsonCapped = JsonWire.RenderSeqOutcome(seqOutcome, 260);
        Check("write_seq format=json: the truncation note prices the re-run, like its text twin",
            TryJson(seqJsonCapped, out var sjdoc)
            && sjdoc!.RootElement.GetProperty("truncated").GetBoolean()
            && sjdoc.RootElement.GetProperty("truncated_note").GetString() is { } sjnote
            // The SAME construction the text arm pins, which is now what "says the SAME thing" means literally.
            && sjnote.Contains(WriteSentences.Twins.SeqListCutRemedy, StringComparison.Ordinal)
            && !sjnote.Contains("raise max_chars", StringComparison.Ordinal), seqJsonCapped);

        // LANE exclusivity on write_seq (PR #311 review 4 [low]): both spellings are labelled LANE: by this PR, and
        // ResolvePatchModFolder returns from the into= branch before patch= is read — so the pair used to land the
        // .seq in into='s folder with patch= silently dropped. Refused BY NAME like every sibling, and in BOTH
        // transports: a json caller getting prose here is the same class one layer up.
        var seqBothLanes = SeqTools.WriteSeq(fx.Svc, source: fx.MasterName, patch: "HcSeqNew", into: "HcSeqExisting.esp");
        Check("write_seq: patch= and into= together are refused BY NAME, never silently resolved to into=",
            seqBothLanes.StartsWith("error:", StringComparison.Ordinal)
            && seqBothLanes.Contains("HcSeqNew", StringComparison.Ordinal)
            && seqBothLanes.Contains("HcSeqExisting.esp", StringComparison.Ordinal)
            && seqBothLanes.Contains("exclusive", StringComparison.Ordinal), seqBothLanes);

        // Same pre-engine RenderError shape as the create/remove refusal arms above — {error, epoch}, no `ok`
        // discriminant (the reviewer-scoped-out W3 PR 3 sweep), so this asserts the REASON is machine-readable and
        // tightens to ok:false when that lands.
        var seqBothLanesJson = SeqTools.WriteSeq(fx.Svc, source: fx.MasterName, patch: "HcSeqNew",
            into: "HcSeqExisting.esp", format: "json");
        // TryGetProperty, not GetProperty: without the fix this document is a SUCCESS (patch= silently dropped),
        // which carries no `error` at all — an arm that throws there reports a crashed probe instead of the finding.
        Check("write_seq format=json: the LANE refusal is a DOCUMENT carrying the reason, not prose",
            seqBothLanesJson.Length > 0 && TryJson(seqBothLanesJson, out var sldoc)
            && sldoc!.RootElement.TryGetProperty("error", out var slerr)
            && slerr.GetString() is { } slmsg && slmsg.Contains("exclusive", StringComparison.Ordinal),
            seqBothLanesJson);

        // …and a single lane still reaches the engine — the refusal must be the PAIR, not "patch= is refused".
        var seqOneLane = SeqTools.WriteSeq(fx.Svc, source: fx.MasterName, patch: "HcSeqOnlyNew");
        Check("write_seq: patch= ALONE is still honored (the refusal is the pair, not the parameter)",
            !seqOneLane.StartsWith("error:", StringComparison.Ordinal), seqOneLane);

        // write_seq: the ABSENT epoch is a stated fact with its reason, not a dropped field.
        var seqJson = SeqTools.WriteSeq(fx.Svc, source: fx.MasterName, format: "json");
        Check("write_seq format=json: epoch is explicitly null AND carries why (no build is consulted at all)",
            TryJson(seqJson, out var sdoc)
            && sdoc!.RootElement.GetProperty("epoch").ValueKind == JsonValueKind.Null
            && sdoc.RootElement.GetProperty("epoch_note").GetString()!.Contains("load-order-independent"), seqJson);

        // write_seq text: a plugin with no SGE quests reports the clean no-op AND names which copy it read.
        var seqText = SeqTools.WriteSeq(fx.Svc, source: fx.MasterName);
        Check("write_seq text: the no-SGE-quests no-op names the file AND the copy it was read from",
            seqText.Contains("no start-game-enabled quests", StringComparison.Ordinal)
            && seqText.Contains("read from", StringComparison.Ordinal), seqText);
    }

    // ---- the D2 twin harness (response layer by construction) ---------------------------------------

    /// <summary>ARM 12 — the D2 TWIN HARNESS. Every other arm in this file renders ONE transport and asserts what
    /// it says. This one renders BOTH from the SAME outcome object and asserts they cannot have drifted apart —
    /// which is the finding class the 2.0 review wave produced more of than any other (a rule, budget, cap or
    /// wording landing on one lane and not its twin), converted from a per-finding obligation into a standing check.
    ///
    /// <para><b>What it pins, and what it deliberately does not.</b> The check is COVERAGE per lane, not
    /// co-occurrence per outcome: every sentence in <see cref="WriteSentences.Twins"/> must be observed coming out
    /// of the TEXT renders at least once and out of the JSON renders at least once, across the outcomes below. That
    /// is the detector for the failure that matters — a lane quietly growing its own copy of a shared sentence,
    /// which makes the constant stop appearing on that lane. It is NOT "both transports carry every sentence on
    /// every outcome", because a few of these legitimately land in different places on the two lanes (the text
    /// report blocks state their stake in the block HEADER; the json blocks state it in the truncation census), and
    /// asserting a co-occurrence that was never the design would fail honest renders.</para>
    ///
    /// <para>By construction: adding a member to <c>Twins</c> enrols it — if no outcome here exercises it on both
    /// lanes, the coverage assertion NAMES it and fails, so a new shared sentence cannot be added without a render
    /// on each transport actually reading it. Two further parity assertions ride per outcome: the BUDGET (a cap
    /// that truncates one transport truncates the other) and the COUNTS (the total each lane states is the same
    /// number).</para></summary>
    static void TwinParityArm(Fixture fx, string root)
    {
        Console.WriteLine("── ARM 12: D2 TWIN PARITY — one outcome, both transports, one source per sentence ──");

        var twins = TwinSentences();
        Check("the twin inventory is non-empty (a reflection miss would make every check below vacuous)",
            twins.Count > 0, $"{twins.Count} members");
        var unreadable = TwinShapesUnreadable();
        Check("every Twins member is a shape this arm can enumerate (an unreadable one is an unchecked twin reported as covered)",
            unreadable.Count == 0, unreadable.Count == 0 ? null : string.Join("; ", unreadable));
        // The CONTENT half. Everything else in this arm asserts wiring — that a render reads the shared source —
        // and wiring checks pass whatever the source says. This is the one that fails when a sentence is emptied.
        var gutted = TwinContentViolations();
        Check("every Twins sentence still states the claims it declares (a construction pin cannot see this — it reads the same constant the render does)",
            gutted.Count == 0, gutted.Count == 0 ? null : string.Join("; ", gutted));
        var outer = OuterSentences();
        var seenText = new HashSet<string>(StringComparer.Ordinal);
        var seenJson = new HashSet<string>(StringComparer.Ordinal);
        // The outer-class sentences are ONE-transport by design, so their claim is "reaches a render", not
        // "reaches both". One set, either lane (residual C).
        var seenOuter = new HashSet<string>(StringComparer.Ordinal);

        void Observe(string text, string json)
        {
            var jsonText = JsonStrings(json);
            foreach (var (name, sentence) in twins)
            {
                if (text.Contains(sentence, StringComparison.Ordinal)) seenText.Add(name);
                if (jsonText.Contains(sentence, StringComparison.Ordinal)) seenJson.Add(name);
            }
            foreach (var (name, sentence) in outer)
                if (text.Contains(sentence, StringComparison.Ordinal) || jsonText.Contains(sentence, StringComparison.Ordinal))
                    seenOuter.Add(name);
        }

        // ---- the four write verbs, through the REAL tool path -----------------------------------------
        // Budget + count parity is asserted on outcomes the engine produced, not on hand-built ones: a cap that
        // truncates the text list and not the json array (or vice versa) is the divergence, and only a real
        // multi-row outcome can show it. Three records is enough — the cap is set below the row block, not below
        // the row count.
        var createSpec = new[] { "W2TwinA", "W2TwinB", "W2TwinC" }
            .Select(e => new CreateOp { RecordType = "Keyword", Editorid = e }).ToList();
        var createOutcome = fx.Svc.CreateRecordsBatch(createSpec, "W2Twin", null);
        BudgetParity("create", createOutcome.Created.Count,
            cap => WriteTools.RenderCreate(createOutcome, cap),
            cap => JsonWire.RenderCreateOutcome(createOutcome, cap, false, "patch"),
            "total_created", $"created {createOutcome.Created.Count} records");
        Observe(WriteTools.RenderCreate(createOutcome), JsonWire.RenderCreateOutcome(createOutcome, 0, false, "patch"));
        // …and the CUT render, whose remedy sentence only exists on a truncated one.
        Observe(WriteTools.RenderCreate(createOutcome, 60), JsonWire.RenderCreateOutcome(createOutcome, 60, false, "patch"));

        var fwdOutcome = fx.Svc.ForwardRecords(new[] { fx.SubjectFid, fx.MasterOnlyFid }, fx.MasterName, "W2TwinFwd", null);
        BudgetParity("forward", fwdOutcome.Forwarded.Count,
            cap => WriteTools.RenderForward(fwdOutcome, cap),
            cap => JsonWire.RenderForwardOutcome(fwdOutcome, cap, false, "patch"),
            "total_forwarded", $"forwarded {fwdOutcome.Forwarded.Count} records");
        Observe(WriteTools.RenderForward(fwdOutcome), JsonWire.RenderForwardOutcome(fwdOutcome, 0, false, "patch"));

        // APPLY and REMOVE complete the verb set. apply is the one that mattered: its text ops loop was the last
        // unbounded row list on the write surface while its json twin budgeted the same array, so before this arm
        // existed a large bulk_apply truncated on one transport and took a silent host cut on the other.
        var applyOutcome = fx.Svc.ApplyEdits(
            new[] { fx.SubjectFid, fx.MasterOnlyFid }
                .Select(f => new BulkOp { Formid = f, FieldPath = "EditorID", Value = "W2TwinEd" }).ToList(),
            "W2TwinApply", null);
        BudgetParity("apply", applyOutcome.Ops.Count,
            cap => WriteTools.Render(applyOutcome, cap),
            cap => JsonWire.RenderPatchOutcome(applyOutcome, cap, false, "patch"),
            "total_ops", $"{applyOutcome.Ops.Count} edits");
        Observe(WriteTools.Render(applyOutcome, 60), JsonWire.RenderPatchOutcome(applyOutcome, 60, false, "patch"));

        var rmOutcome = fx.Svc.RemoveRecords(new[] { fx.SubjectFid, fx.MasterOnlyFid }, "W2TwinFwd.esp");
        BudgetParity("remove", rmOutcome.Removed.Count,
            cap => WriteTools.RenderRemoval(rmOutcome, cap),
            cap => JsonWire.RenderRemovalOutcome(rmOutcome, cap, "into"),
            "total_removed", $"removed {rmOutcome.Removed.Count} records");
        Observe(WriteTools.RenderRemoval(rmOutcome, 60), JsonWire.RenderRemovalOutcome(rmOutcome, 60, "into"));

        // …and remove's no-usable-patch REFUSAL, which is where the extend not-found tail reaches a caller (#356).
        // Driven off the real service and the real TOOL rather than a built outcome: the sentence is chosen from what
        // the caller stated about itself, so an outcome constructed here would be this probe handing itself the
        // answer. The call is a refusal, so the fixture the later arms read is untouched. The 1.x twin observed here
        // went with remove_record at the demolition catch-up (#468).
        Observe(RemoveTools.Remove(fx.Svc, new[] { fx.SubjectFid }, into: "W2TwinNoSuchPatch"),
                RemoveTools.Remove(fx.Svc, new[] { fx.SubjectFid }, into: "W2TwinNoSuchPatch", format: "json"));

        // ---- the three post-write report blocks -------------------------------------------------------
        // Rendered off a built outcome for the same reason the report-budget arm above builds one: a report big
        // enough to cut means dozens of voiced lines, and the claim under test is the RENDERERS' agreement, not the
        // engines'. Voice + result-script + an EXTERIOR cell together reach every create-side twin, and the capped
        // pass is what puts the json lane's stake clauses (which ride its truncation census) on the wire.
        var lines = Enumerable.Range(0, 40).Select(i => new VoiceLine(
            default, "W2TwinTopic", i,
            $@"sound\voice\W2.esp\MaleNord\W2TwinTopic_{i:D4}.fuz", false, null, false,
            $@"sound\voice\W2.esp\MaleNord\W2TwinTopic_{i:D4}.lip", false, false)).ToList();
        var findings = Enumerable.Range(0, 40).Select(i => new ScriptBindingFinding(
            default, "W2TwinTopic", ScriptBindingStatus.ScriptNotCompiled,
            new[] { "W2TwinFrag" }, new[] { $@"scripts\W2TwinFrag{i:D2}.pex" }, false,
            "the bound script has no compiled .pex on disk")).ToList();
        var shells = Enumerable.Range(0, 30).Select(i => new CellShell(
            default, $"W2TwinCell{i:D2}", i % 2 == 0,
            new[] { "lighting template", "terrain / landscape", "water height", "navmesh" })).ToList();
        var reports = new WritePatchBuilder.CreateOutcome(
            true, null, @"C:\mods\W2Twin\W2Twin.esp", false,
            new[] { new WritePatchBuilder.CreatedRecord(default, "DialogResponses", "W2TwinL1", Array.Empty<WritePatchBuilder.OpResult>()) },
            Array.Empty<string>(), 512)
            {
                Epoch = "deadbeefdeadbeef",
                Voice = new VoiceReport(lines, Array.Empty<VoiceUndetermined>()),
                ScriptBinding = new ScriptBindingReport(findings),
                CellShell = new CellShellReport(shells),
            };
        Observe(WriteTools.RenderCreate(reports), JsonWire.RenderCreateOutcome(reports, 0, false, "patch"));
        Observe(WriteTools.RenderCreate(reports, maxChars: 900), JsonWire.RenderCreateOutcome(reports, 900, false, "patch"));

        // ---- the IN-PLACE and DRY-RUN lanes, for the outer-class sentences ---------------------------
        // Built outcomes rather than real in-place writes: those rewrite a fixture file the later arms read as a
        // known winner, and this arm deliberately runs before them. The claim under test is the RENDERERS' — that
        // the hazard and the dry-run header still reach a caller — not the engine's, which the lane arms cover.
        var inPlaceCreate = new WritePatchBuilder.CreateOutcome(
            true, null, @"C:\mods\W2TwinIP\W2TwinIP.esp", false,
            new[] { new WritePatchBuilder.CreatedRecord(default, "Keyword", "W2TwinIPKw", Array.Empty<WritePatchBuilder.OpResult>()) },
            Array.Empty<string>(), 512)
            { Epoch = "deadbeefdeadbeef", InPlace = true };
        Observe(WriteTools.RenderCreate(inPlaceCreate), JsonWire.RenderCreateOutcome(inPlaceCreate, 0, false, "in_place"));

        var inPlaceDryApply = new WritePatchBuilder.PatchOutcome(
            true, null, @"C:\mods\W2TwinIP\W2TwinIP.esp", false,
            Array.Empty<string>(), Array.Empty<WritePatchBuilder.OpResult>(), 512)
            { Epoch = "deadbeefdeadbeef", InPlace = true, DryRun = true };
        Observe(WriteTools.Render(inPlaceDryApply), JsonWire.RenderPatchOutcome(inPlaceDryApply, 0, false, "in_place"));

        // ---- write_seq -------------------------------------------------------------------------------
        // Every state that has its own sentence, so none of them is covered by an argument about the others: the
        // no-op, the byte-identical skip (with its timestamp stamp-forward), the three replace readings, and a cut
        // quest list. write_seq is where the two transports had drifted furthest — one lane's notes were paraphrases
        // of the other's — so it gets the widest sweep here.
        var quests = Enumerable.Range(0, 60)
            .Select(i => new HousecarlCore.SeqFile.SeqQuest(default, $"W2TwinQ{i:D2}", (uint)(0x800 + i))).ToList();
        SeqOutcome Seq(IReadOnlyList<HousecarlCore.SeqFile.SeqQuest> qs) => new(
            true, null, @"C:\mods\W2TwinSeq\SEQ\W2Twin.seq", "W2TwinSeq", qs, "W2Twin.esp", false)
            { ResolvedFrom = "direct path", PluginPath = @"C:\mods\W2TwinSeq\W2Twin.esp" };

        var seqStates = new[]
        {
            Seq(Array.Empty<HousecarlCore.SeqFile.SeqQuest>()),                                      // the no-op
            Seq(quests) with { Unchanged = true, TimestampRefreshed = true },                        // skip + stamp
            Seq(quests) with { Replaced = true, ReplacedSameBytes = true },                          // same-bytes replace
            Seq(quests) with { Replaced = true, UserChoseOutput = true },                            // the caller's folder
            Seq(quests) with { Replaced = true },                                                    // houseCARL's own
        };
        foreach (var st in seqStates)
            Observe(SeqTools.Render(st), JsonWire.RenderSeqOutcome(st, 0));
        // …and the cut quest list, whose remedy is one sentence on both lanes.
        Observe(SeqTools.Render(seqStates[2], maxChars: 400), JsonWire.RenderSeqOutcome(seqStates[2], 400));
        BudgetParity("write_seq", quests.Count,
            cap => SeqTools.Render(seqStates[2], cap),
            cap => JsonWire.RenderSeqOutcome(seqStates[2], cap),
            "quest_count", $"{quests.Count} start-game-enabled quests");

        // ---- place_asset's source refusals ------------------------------------------------------------
        // place_asset renders on ONE transport, so these sentences' claim is "reaches a render", not "reaches
        // both" — and they are observed off outcomes the REAL service produced. A probe that built the
        // PlaceResult itself would be handing this arm the very sentence it claims to have found, which is the
        // circular shape the whole reach check exists to catch.
        foreach (var render in PlaceSourceRefusalRenders(root)) Observe(render, "{}");

        // ---- copy's outcome sentences (PR 3b) ---------------------------------------------------------
        // Copy renders on ONE transport today, so the claim here is "reaches a render", same as place_asset's.
        // Its SUCCESS path is driven by the real service in copy-service-guard; what that fixture cannot reach is
        // the two DEFENSIVE branches — a post-write read-back that fails, and a patch that somehow mastered its
        // own source — because both are states the operation exists to prevent. Those are observed by handing the
        // REAL render a constructed outcome: the render is the code under test, and the sentence still comes from
        // the shared source rather than from this probe.
        foreach (var render in CopyOutcomeRenders()) Observe(render, "{}");

        // ---- merge/compact's runtime-config reminder --------------------------------------------------
        // One transport, same claim as copy's: "reaches a render". Both verbs' SUCCESS paths run against the real
        // service in merge-service-guard / compact-service-guard, which is where the sentence's behaviour is pinned;
        // what those fixtures cannot do is prove the constant is still WIRED, because they live in other probes. The
        // outcome is constructed and handed to the REAL renderer — the renderer is the code under test, and the
        // sentence still comes from the shared source rather than from this probe.
        foreach (var render in MergeCompactOutcomeRenders()) Observe(render, "{}");

        // ---- the coverage assertion — what stops this arm being theatre ------------------------------
        var missingText = twins.Select(t => t.Name).Where(n => !seenText.Contains(n)).ToList();
        var missingJson = twins.Select(t => t.Name).Where(n => !seenJson.Contains(n)).ToList();
        Check("every WriteSentences.Twins member is rendered by the TEXT lane (an unobserved one is a lane that stopped reading the shared source, or a twin nothing exercises)",
            missingText.Count == 0, missingText.Count == 0 ? null : "unobserved: " + string.Join(", ", missingText));
        Check("every WriteSentences.Twins member is rendered by the JSON lane (same, one transport over)",
            missingJson.Count == 0, missingJson.Count == 0 ? null : "unobserved: " + string.Join(", ", missingJson));
        // …and the outer-class sentences REACH a render at all. A content pin says what a sentence must say; it
        // cannot say that anything still emits it, and a render that inlines its own weaker copy leaves the const
        // intact and passing (PR #337 re-review, residual C).
        var missingOuter = outer.Select(t => t.Name).Where(n => !seenOuter.Contains(n)).ToList();
        Check("every [MustState] sentence on WriteSentences itself reaches a render (the wiring half a content pin cannot provide)",
            missingOuter.Count == 0, missingOuter.Count == 0 ? null : "unobserved: " + string.Join(", ", missingOuter));
    }

    /// <summary>Drive the REAL place service to each of its source-selection refusals and return the rendered
    /// outcomes. The fixture is TWO mods contending for one asset path, plus one on-disk source and one both-slots
    /// spec — enough to reach every sentence below. (It briefly carried a third mod named "winner", for a collision
    /// refusal that no longer exists: the pole token is sigiled, so no provider name can collide with it.) Its own
    /// throwaway MO2 instance rather than the shared fixture, because growing that tree would move something eleven
    /// other arms assert against — #333's lesson — for the sake of a few sentences.</summary>
    static List<string> PlaceSourceRefusalRenders(string root)
    {
        const string rel = @"meshes\hcw2\twin.nif";
        var inst = Path.Combine(root, "place-sentences");
        var mods = Path.Combine(inst, "mods");
        var prof = Path.Combine(inst, "profiles", "Default");
        foreach (var d in new[] { mods, prof, Path.Combine(inst, "game", "Data") }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(inst, "game").Replace(@"\", @"\\") + ")\r\n");

        // Two contending providers — enough for every refusal below now that no name can collide with the pole.
        var providers = new[] { "W2AssetA", "W2AssetB" };
        foreach (var m in providers)
        {
            var dir = Path.Combine(mods, m, "meshes", "hcw2");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "twin.nif"), new byte[] { 1, 2, 3 });
        }
        File.WriteAllText(Path.Combine(mods, providers[0], "Dummy.esp"), "x");
        // A bound archive that will not open, so this instance's scan is READ-INCOMPLETE and the caveat sentence
        // reaches a render. Without it the fixture has no active archives at all and the sentence is unobservable
        // here — the same fixture blindness that let the named-pole refusal stop carrying it (Aaron's review, F1).
        File.WriteAllBytes(Path.Combine(mods, providers[0], "Dummy.bsa"), new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        // Two folders MO2 does not list, so the off-order lane really runs for them: one that simply has no copy of
        // the queried path, one whose root archive will not read. They exist to reach two of the refusal's outcomes.
        Directory.CreateDirectory(Path.Combine(mods, "W2Offline"));
        Directory.CreateDirectory(Path.Combine(mods, "W2Broken"));
        File.WriteAllBytes(Path.Combine(mods, "W2Broken", "Broken.bsa"), new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\nDummy.esp\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*Dummy.esp\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"),
            "# header\r\n" + string.Join("\r\n", providers.Select(p => "+" + p)) + "\r\n");
        File.WriteAllText(Path.Combine(prof, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");

        using var svc = LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(root, "place-sentences.user.json")));
        string Render(PlaceRequest req) => PlaceWire.Render(svc.PlaceAssets(new[] { req }, null, null));
        // The both-slots constraint is refused at the TOOL layer, before any outcome exists, so it is observed
        // through the tool rather than through PlaceWire — the sentence still has to reach a caller either way.
        var bothSlots = PlaceAssetTools.BulkPlaceAsset(svc, new[]
        {
            new PlaceAssetSpec { Formid = "000800:Dummy.esp", Source = Path.Combine(root, "w2-ondisk.nif") },
        });
        return new List<string>
        {
            bothSlots,
        }.Concat(new List<string>
        {
            Render(new PlaceRequest(rel, null, null)),                                          // contended, no pole
            Render(new PlaceRequest(rel, rel, "W2NoSuchMod")),                                  // named, absent (+ the pole-spelling tail)
            Render(new PlaceRequest(rel, Path.Combine(root, "w2-ondisk.nif"), providers[0])),   // pole vs on-disk source
            // The auto-resolve dead end — nothing provides the DESTINATION and no pole was named. Its remedy is
            // where the "a named mod folder is read whether or not it is ticked" sentence reaches a caller, and it
            // is the refusal that caller is most likely to be standing in when they need it.
            Render(new PlaceRequest(@"meshes\hcw2\nothing-provides-this.nif", null, null)),
            // The named-miss refusal keys ONE sentence per lookup outcome, and each has to reach a render or that
            // arm is half-wired. The W2NoSuchMod render above is the no-such-folder one; these are the rest.
            Render(new PlaceRequest(rel, @"meshes\hcw2\absent-everywhere.nif", providers[0])),   // a universe name
            Render(new PlaceRequest(rel, rel, @"..\nope")),                                      // a path-shaped name
            Render(new PlaceRequest(rel, rel, "W2Offline")),                                     // a real folder, no copy
            Render(new PlaceRequest(rel, rel, "W2Broken")),                                      // a real folder, unreadable
        }).ToList();
    }

    /// <summary>The budget half of the twin harness: one outcome, one cap, both transports. Asserts (a) a cap tight
    /// enough to cut ONE lane's rows cuts the other's too — the divergence that shipped repeatedly, where a text
    /// list rendered unbounded and took a SILENT host-side cut while its json twin stopped at the cap and closed
    /// <c>truncated:true</c> — and (b) uncapped, neither reports a cut. The COUNT parity rides along: the total
    /// each lane states about the same outcome is the same number, cut or not.</summary>
    static void BudgetParity(string label, int total, Func<int, string> text, Func<int, string> json,
                             string jsonTotalMember, string textTotalPhrase)
    {
        // 60 chars is below EVERY one of these renders' header block, so both lanes stop at row 0. Deliberately not
        // a cap that merely lands mid-list: the two lanes measure different things (a StringBuilder's chars vs a
        // serialized document's bytes, one of them indented), so any cap inside the rows cuts them at different
        // rows — which is a formatting difference, not the divergence under test. What must agree is WHETHER a cut
        // happened and whether it was stated.
        const int tight = 60;
        var tText = text(tight);
        var tJson = json(tight);
        // The ROW-list marker specifically. "... [" also opens the post-write report blocks' own cut notices, so
        // the looser match would let a cut voice or cell block stand in for a row list that never truncated.
        bool textCut = tText.Contains("... [truncated:", StringComparison.Ordinal);
        bool jsonCut = TryJson(tJson, out var tdoc) && RootFlag(tdoc, "truncated") == true;
        Check($"{label}: a cap that truncates one transport truncates the other (text={textCut} json={jsonCut})",
            total > 0 && textCut == jsonCut && textCut, $"TEXT: {Trim(tText)} || JSON: {Trim(tJson)}");

        var fText = text(0);
        var fJson = json(0);
        Check($"{label}: uncapped, NEITHER transport reports a cut",
            !fText.Contains("[truncated:", StringComparison.Ordinal)
            && TryJson(fJson, out var fdoc) && RootFlag(fdoc, "truncated") != true,
            $"TEXT: {Trim(fText)} || JSON: {Trim(fJson)}");

        // The TRUE total survives the cut on both lanes — the whole point of the total/rendered pair is that a
        // truncated caller still knows how many it is not seeing.
        Check($"{label}: both transports state the SAME total ({total}), and state it even when cut",
            TryJson(tJson, out var cdoc) && cdoc!.RootElement.TryGetProperty(jsonTotalMember, out var jt)
            && jt.GetInt32() == total
            && tText.Contains(textTotalPhrase, StringComparison.Ordinal),
            $"TEXT: {Trim(tText)} || JSON: {Trim(tJson)}");
    }

    /// <summary>Every string VALUE in a json document, unescaped and concatenated. The json writer escapes
    /// non-ASCII (an em-dash ships as <c>\u2014</c>), so a raw <c>Contains</c> against the document text would
    /// report a shared sentence as absent from the json lane and fail the arm for the encoder's reasons rather
    /// than the render's.</summary>
    static string JsonStrings(string raw)
    {
        var sb = new System.Text.StringBuilder();
        using var doc = JsonDocument.Parse(raw);
        Walk(doc.RootElement, sb);
        return sb.ToString();

        static void Walk(JsonElement e, System.Text.StringBuilder sb)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) Walk(p.Value, sb); break;
                case JsonValueKind.Array: foreach (var v in e.EnumerateArray()) Walk(v, sb); break;
                case JsonValueKind.String: sb.Append(e.GetString()).Append('\n'); break;
            }
        }
    }

    /// <summary>The twin inventory, read off <see cref="WriteSentences.Twins"/> by reflection rather than listed
    /// here. A hand-listed inventory is the thing this whole change exists to stop: it would be a second copy of
    /// the set, free to fall behind the first.
    /// <para>Returns the <c>const string</c> members. Anything else in that class is a shape this arm cannot read,
    /// and <see cref="TwinShapesUnreadable"/> turns it into a FAILURE rather than a silent skip — a filter that
    /// quietly dropped a <c>static readonly</c> twin would report full coverage of a set it had not seen.</para></summary>
    static IReadOnlyList<(string Name, string Sentence)> TwinSentences()
        => typeof(WriteSentences.Twins)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (f.Name, (string)f.GetRawConstantValue()!))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>The outer-class shared sentences — the ones that are prose on ONE transport by design, so per-LANE
    /// parity is not a claim that can be made about them. Enumerated exactly as <see cref="TwinSentences"/>
    /// enumerates its set, so the arm can assert each one still REACHES a render.
    /// <para>Why enumerate rather than hand-write a Check per sentence (PR #337 re-review, residual C): the content
    /// pin and the wiring pin are different claims, and only <c>InPlaceRewritten</c> had both. A hand-written line
    /// would have closed that one sentence; this closes the class, so a sentence added to the outer class later
    /// cannot get a content pin and silently miss the wiring one.</para></summary>
    static IReadOnlyList<(string Name, string Sentence)> OuterSentences()
        => typeof(WriteSentences)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)
                        && f.GetCustomAttribute<MustStateAttribute>() is not null)
            .Select(f => (f.Name, (string)f.GetRawConstantValue()!))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>Members of <c>Twins</c> that <see cref="TwinSentences"/> cannot enumerate — a non-const field, a
    /// property, or a nested type. Any of them would be a twin the coverage assertion never checks while still
    /// reporting "every member covered", so they are named and failed rather than filtered away.</summary>
    static IReadOnlyList<string> TwinShapesUnreadable()
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
                               | BindingFlags.DeclaredOnly;
        var bad = new List<string>();
        foreach (var f in typeof(WriteSentences.Twins).GetFields(All))
            if (!(f.IsLiteral && f.FieldType == typeof(string))) bad.Add($"field {f.Name} ({f.FieldType.Name}, not a const string)");
        foreach (var pr in typeof(WriteSentences.Twins).GetProperties(All)) bad.Add($"property {pr.Name}");
        foreach (var n in typeof(WriteSentences.Twins).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) bad.Add($"nested type {n.Name}");
        // METHODS and EVENTS too (PR #337 review, finding 2). A method-shaped twin — one interpolation away from
        // several existing ones — was invisible to BOTH the inventory and this alarm, so the coverage assertions
        // reported "every member rendered" for a set that never included it. DeclaredOnly keeps object's members out.
        foreach (var m in typeof(WriteSentences.Twins).GetMethods(All)) bad.Add($"method {m.Name}");
        foreach (var e in typeof(WriteSentences.Twins).GetEvents(All)) bad.Add($"event {e.Name}");
        return bad;
    }

    /// <summary>Twins whose declared load-bearing phrases are missing from the sentence itself — or which declare
    /// none at all. THIS is the check a construction pin cannot make: <c>render.Contains(TheConstant)</c> reads the
    /// same symbol the render does, so it passes whatever the constant says, including nothing. A commit that
    /// replaced three of these sentences with placeholders passed the whole suite green; this is what would have
    /// caught it, and requiring the attribute means a new twin cannot be added without declaring what it must say.</summary>
    static IReadOnlyList<string> TwinContentViolations()
    {
        var bad = new List<string>();
        // BOTH classes (PR #337 review, finding 1). The in-place hazard lives on the outer class — json states it
        // as `lane` plus typed flags, so per-lane coverage is not a claim that can be made about it — but this
        // change made it a single token feeding five in-place renders, so it needs the content half most of all.
        //
        // EVERY const on both classes must DECIDE: [MustState] phrases, or [NoClaims] with a stated reason. The
        // outer walk used to skip an undecorated const, which left CellRowsCutLoss and DryRunHeader unchecked
        // purely because nobody remembered them — absence-as-silence, where the safe state is the one you reach
        // by doing nothing, and every sentence added later inherits it (PR #337 re-review, residual A).
        foreach (var owner in new[] { typeof(WriteSentences.Twins), typeof(WriteSentences) })
        foreach (var f in owner.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
                     .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                     .OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            var sentence = (string)f.GetRawConstantValue()!;
            var attr = f.GetCustomAttribute<MustStateAttribute>();
            var optOut = f.GetCustomAttribute<NoClaimsAttribute>();
            if (attr is not null && optOut is not null)
            {
                bad.Add($"{f.Name}: declares BOTH [MustState] and [NoClaims] — pick one");
                continue;
            }
            if (optOut is not null)
            {
                if (optOut.Reason.Trim().Length == 0) bad.Add($"{f.Name}: [NoClaims] with no stated reason");
                continue;
            }
            if (attr is null || attr.Phrases.Length == 0)
            {
                bad.Add($"{f.Name}: declares neither [MustState] phrases nor [NoClaims] with a reason");
                continue;
            }
            // An EMPTY sentence or an EMPTY phrase clears every other assertion here and in the coverage arm —
            // "".Contains("") is true, and so is any render's Contains("") (PR #337 review, finding 5). Mechanical,
            // so it is fixed in the checker; judging whether a NON-empty phrase is a good one is not, and stays with
            // the author (see MustStateAttribute's norm).
            if (sentence.Length == 0) { bad.Add($"{f.Name}: the sentence itself is empty"); continue; }
            foreach (var phrase in attr.Phrases)
            {
                if (phrase.Length == 0) { bad.Add($"{f.Name}: declares an EMPTY phrase, which every sentence satisfies"); continue; }
                if (!sentence.Contains(phrase, StringComparison.Ordinal))
                    bad.Add($"{f.Name}: no longer states \"{phrase}\"");
            }
        }
        return bad;
    }

    static bool TryJson(string s, out JsonDocument? doc)
    {
        try { doc = JsonDocument.Parse(s); return true; }
        catch { doc = null; return false; }
    }

    /// <summary>The remainder of ONE render notice — from <paramref name="after"/> to the end of that LINE. Lets an arm
    /// assert the ABSENCE of a phrase inside one notice without the whole render's other notices answering for it (a
    /// write render carries several, and they do not all belong to the finding under test).
    /// <para>Line-bounded, not bracket-bounded: a remedy may legitimately contain brackets of its own — the in-place
    /// forward's names <c>formids=[the ids you passed]</c> — and cutting at the first ']' silently truncated the very
    /// clause under test, failing the arm for the wrong reason.</para></summary>
    static string? Bracket(string render, string after)
    {
        int at = render.IndexOf(after, StringComparison.Ordinal);
        if (at < 0) return null;
        var tail = render[(at + after.Length)..];
        int eol = tail.IndexOf('\n');
        return eol < 0 ? tail : tail[..eol];
    }

    /// <summary>Absence-tolerant readers for a nested member. An arm that asserts a member the PRE-FIX code does not
    /// emit must FAIL, not throw — a thrown probe reports "guard crashed" and hides which arm found what (a scar
    /// this fold hit twice). Null/absent ⇒ the comparison is false, which is exactly the RED signal wanted.</summary>
    static JsonElement? Member(JsonDocument? doc, string obj, string member)
        => doc is not null && doc.RootElement.TryGetProperty(obj, out var o) && o.TryGetProperty(member, out var m) ? m : null;

    static int? Num(JsonDocument? doc, string obj, string member)
        => Member(doc, obj, member) is { ValueKind: JsonValueKind.Number } e ? e.GetInt32() : null;

    static bool? Flag(JsonDocument? doc, string obj, string member)
        => Member(doc, obj, member) is { ValueKind: JsonValueKind.True or JsonValueKind.False } e ? e.GetBoolean() : null;

    static string? Str(JsonDocument? doc, string obj, string member)
        => Member(doc, obj, member) is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;

    /// <summary>The same tolerance one level up — a TOP-LEVEL flag. Same reason: an arm asserting a member the
    /// pre-fix code never emits must read false, not throw.</summary>
    static bool? RootFlag(JsonDocument? doc, string member)
        => doc is not null && doc.RootElement.TryGetProperty(member, out var e)
           && e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : null;

    /// <summary>Is a TOP-LEVEL member present at all? (The "an active source emits NO source_read" assertion — a
    /// positive test for an absence, which no value reader can express.)</summary>
    static bool HasRoot(JsonDocument? doc, string member)
        => doc is not null && doc.RootElement.TryGetProperty(member, out _);

    /// <summary>Render one successful merge and one successful compact outcome, so the reach half of the
    /// <c>[MustState]</c> walk covers the runtime-config reminder both verbs carry. Deliberately the plainest
    /// outcomes that reach the sentence — it is keyed on neither donor count nor any accounting, so a richer
    /// fixture would prove nothing more here, and the behavioural arms live in the two service guards.</summary>
    static List<string> MergeCompactOutcomeRenders()
    {
        var outPath = Path.Combine(Path.GetTempPath(), "MergeFolder", "Merged.esp");
        var merge = new WritePatchBuilder.MergeOutcome(
            true, null, outPath, "Merged.esp",
            new[] { "DonorA.esp" }, new[] { "Skyrim.esm" }, 1, 1,
            Array.Empty<RemapEngine.MergeDonorRemap>(), Array.Empty<RemapEngine.MergeConflict>(),
            Array.Empty<string>(), Array.Empty<string>(), 3, 0, Array.Empty<string>(), 1024);

        var compactPath = Path.Combine(Path.GetTempPath(), "CompactFolder", "Compacted.esp");
        var compact = new WritePatchBuilder.CompactOutcome(
            true, null, false, compactPath, "Compacted.esp", false, true,
            new[] { "Skyrim.esm" }, 1, 1, 1024,
            Array.Empty<string>(), Array.Empty<WritePatchBuilder.RepointReport>(), 3, 0, Array.Empty<string>());

        return new List<string> { WriteTools.RenderMerge(merge), WriteTools.RenderCompact(compact) };
    }

    /// <summary>Render copy outcomes covering the sentences the service-level fixture cannot reach. Two of copy's
    /// four pinned sentences describe states the operation prevents — a failed read-back and a self-mastered patch
    /// — so there is no honest fixture that produces them end to end; the outcome is constructed and handed to the
    /// REAL render. The other two ride ordinary success outcomes.</summary>
    static List<string> CopyOutcomeRenders()
    {
        var src = new ModKey("CopySrc", ModType.Plugin);
        var patch = new ModKey("CopyPatch", ModType.Plugin);
        var outPath = Path.Combine(Path.GetTempPath(), "CopyPatchFolder", "CopyPatch.esp");
        var copied = new List<CopiedRecord>
        {
            new(new FormKey(src, 0x800), new FormKey(patch, 0x800), "HeadPart", "SrcHair", 0, "CopySrc.esp", "Npc.HeadParts"),
        };
        var stripped = new List<StripEntry> { new("Factions[0]", new FormKey(src, 0x802).ToString()) };
        var sources = new[] { "CopySrc.esp" };

        ClosureCopyOutcome Make(bool mastered, string? warning, IReadOnlyList<StripEntry> strips,
                               IReadOnlyList<StripEntry>? attach = null, IReadOnlyList<WalkBoundary>? kept = null,
                               IReadOnlyList<string>? assets = null, IReadOnlyList<string>? srcs = null,
                               bool nothingBound = false) => new(
            true, null, null, null, strips.Count > 0 ? "clone" : "attach",
            new FormKey(src, 0x803), new FormKey(patch, 0x900), outPath, false,
            copied, kept ?? Array.Empty<WalkBoundary>(), Array.Empty<WalkCycle>(),
            attach ?? Array.Empty<StripEntry>(), strips, srcs ?? sources,
            "CopySrc.esp", assets ?? Array.Empty<string>(),
            new[] { "Skyrim.esm" }, mastered, nothingBound, 1234, warning);

        var keptBoth = new List<WalkBoundary>
        {
            new(new FormKey(new ModKey("Vanilla", ModType.Master), 0x811), "Npc.HeadParts", "outside", Excluded: false),
            new(new FormKey(src, 0x812), "Npc.WornArmor", "excluded (Race)", Excluded: true),
        };

        return new List<string>
        {
            CopyTools.Render(Make(false, null, Array.Empty<StripEntry>())),   // the standalone claim
            CopyTools.Render(Make(true, null, Array.Empty<StripEntry>())),    // the self-mastered alarm
            CopyTools.Render(Make(false, "read-back blew up", Array.Empty<StripEntry>())),  // NOT VERIFIED
            CopyTools.Render(Make(false, null, stripped)),                    // the strip consequence
            // The sentences the end-to-end fixtures cannot reach on a SUCCESS: a cleared seed, both kinds of kept
            // link in one response (they are contradictory claims, so they have to be seen together), the harvested
            // asset paths, and the multi-source header.
            CopyTools.Render(Make(false, null, Array.Empty<StripEntry>(),
                attach: new List<StripEntry> { new("HeadParts", "2 link(s)"), new("WornArmor", "cleared", Cleared: true) },
                kept: keptBoth,
                assets: new[] { @"meshes\actors\character\facegendata\facegeom\CopySrc.esp\00000800.nif" },
                srcs: new[] { "Override.esp", "CopySrc.esp" })),
            // …and the two REFUSAL sentences that were method-form and outside the content net entirely.
            CopyTools.Render(ClosureCopyOutcome.Fail(
                walk: new WalkRefusal(WalkRefusalKind.SourceMiss, new FormKey(src, 0x820), "Npc.HeadParts",
                    Array.Empty<FormKey>(), "", Miss: null),
                sources: new[] { "Override.esp", "CopySrc.esp" })),
            CopyTools.Render(ClosureCopyOutcome.Fail(
                walk: new WalkRefusal(WalkRefusalKind.SourceFault, new FormKey(src, 0x821), "Npc.HeadParts",
                    Array.Empty<FormKey>(), "the record could not be parsed",
                    Fault: new SourceFault(new FormKey(src, 0x821), "Npc.HeadParts", 0,
                        new SourceArm("CopySrc.esp", SourceArmKind.File, "on disk", _ => null), "the record could not be parsed")),
                sources: new[] { "Override.esp", "CopySrc.esp" })),
            // The two shape-ruling refusals. Both are reachable end to end (copy-parser-guard drives them through
            // the wire), but they are rendered here too so the sentence-reach net owns them the same way it owns
            // every other outer-class sentence — the net is about wiring, and a sentence only one probe can reach
            // is a sentence the net cannot see.
            CopyTools.Render(ClosureCopyOutcome.Fail(
                walk: new WalkRefusal(WalkRefusalKind.UnsupportedSeedShape, new FormKey(src, 0x822), "",
                    Array.Empty<FormKey>(), "'Factions' on Npc is a list of link-BEARING entries, not a list of record links"),
                sources: sources)),
            CopyTools.Render(ClosureCopyOutcome.Fail(
                copy: new CopyRefusal(CopyRefusalKind.DonorLeak, "a link into the source universe survived on the target",
                    ClosureCopy.ExclusionLeakMarker, new FormKey(src, 0x823)),
                sources: sources)),
            // The two refusals added when 'stop' met an off-order source and target= met a nested-group record.
            // Their coverage is NOT equal, and this comment used to claim it was: StopOffOrder is driven end to end
            // by copy-service-guard (and proven RED by deleting its guard), while the nested-group one is reached
            // only by the constructed outcome below — a synthetic instance has no placed reference to aim target=
            // at. Both are rendered here so the sentence-reach net owns them; only one is pinned to the wire.
            CopyTools.Render(ClosureCopyOutcome.Fail(
                copy: new CopyRefusal(CopyRefusalKind.StopOffOrder, "CopySrc.esp", Key: new FormKey(src, 0x824)),
                sources: sources)),
            CopyTools.Render(ClosureCopyOutcome.Fail(
                copy: new CopyRefusal(CopyRefusalKind.UnsupportedTargetShape, "PlacedNpc", Key: new FormKey(src, 0x825)),
                sources: sources)),
            // Inventory F24 — the THIRD arm of the standalone render. A base-game donor is never bound, so the
            // two-way pair could only answer it by denying a claim computed over an emptied set.
            CopyTools.Render(Make(false, null, Array.Empty<StripEntry>(), nothingBound: true)),
            // A strip that nulled a WHOLE property, which the count alone described as one reference.
            CopyTools.Render(Make(false, null,
                new List<StripEntry> { new("VirtualMachineAdapter", new FormKey(src, 0x826).ToString(), WholeProperty: true) })),
            // The target-side refusal, split off from the shape route so its remedy names the target.
            CopyTools.Render(ClosureCopyOutcome.Fail(
                copy: new CopyRefusal(CopyRefusalKind.UnwritableTarget,
                    "'WornArmor' is a record link on the source but the target's is not writable", "WornArmor"),
                sources: sources)),
            // The off-order refusal's two NON-'stop' causes. CopiedOffOrderLink is driven end to end by
            // copy-service-guard arm 7i; PatchOffOrderLink is render-only and says so there — into= only accepts a
            // patch houseCARL wrote, and a call that left an off-order link in one would itself have been refused.
            CopyTools.Render(ClosureCopyOutcome.Fail(
                copy: new CopyRefusal(CopyRefusalKind.PatchOffOrderLink, "Ghost.esp",
                    "Npc 'OlderClone' (000801:CopyPatch.esp)", new FormKey(new ModKey("Ghost", ModType.Plugin), 0x800)),
                sources: sources)),
            CopyTools.Render(ClosureCopyOutcome.Fail(
                copy: new CopyRefusal(CopyRefusalKind.CopiedOffOrderLink, "Ghost.esp",
                    "Npc 'WideNpc' (000804:CopySrc.esp)", new FormKey(new ModKey("Ghost", ModType.Plugin), 0x800)),
                sources: sources)),
            // …and the NoSeeds refusal, which now names the templated-donor cause R4 attributes to it.
            CopyTools.Render(ClosureCopyOutcome.Fail(
                walk: new WalkRefusal(WalkRefusalKind.NoSeeds, new FormKey(src, 0x827), "",
                    Array.Empty<FormKey>(), ""),
                sources: sources)),
        };
    }
}

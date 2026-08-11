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
/// alias-visible vocabulary and the engines are exercised exactly as a caller hits them. TEN arms, listed BY SUBJECT
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
            m.BeginWrite.ToPath(masterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

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
            topicOverride.Responses.Add(new DialogResponses(r.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "W2WinnerLine" });
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
    }

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
            && vnote.Contains("plays SILENT", StringComparison.Ordinal)
            // and it must NOT prescribe re-issuing: this block rides a WRITE render.
            && !vnote.Contains("raise max_chars", StringComparison.OrdinalIgnoreCase), reportCapped);

        // The TEXT twins of those blocks must not contradict the json census (PR #311 review 7 [medium]): they rode
        // the create render still saying "raise max_chars to see the rest", i.e. re-issue a create — while the json
        // block added one round earlier says "Do NOT re-issue the write to widen this". One call, two transports,
        // opposite advice. Rendered directly, since the shared fixture's creates make no dialogue lines.
        var voiceCappedText = WriteTools.RenderCreate(synthetic, maxChars: 900);
        Check("create text: the voice-coverage cut notice refuses to prescribe re-issuing the create",
            voiceCappedText.Contains("voice coverage truncated", StringComparison.Ordinal)
            && Bracket(voiceCappedText, "voice coverage truncated") is { } vct
            && vct.Contains("Do NOT re-issue the create", StringComparison.Ordinal)
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
            cellCapped.Contains("does NOT check grid-occupancy", StringComparison.Ordinal), cellCapped);

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
            && createCapped.Contains("every one WAS created", StringComparison.Ordinal)
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
            && createCapped.Contains("would create them AGAIN", StringComparison.Ordinal)
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
            && seqCapped.Contains("the .seq itself carries ALL of them", StringComparison.Ordinal)
            && !seqCapped.Contains("HcSeqQuestCharlie", StringComparison.Ordinal), seqCapped);
        // …and the notice must not prescribe a re-run: widening the ceiling re-runs a WRITE, which with no lane
        // named for a plugin outside a houseCARL folder cuts a SECOND auto-suffixed folder holding a duplicate
        // .seq. Nothing is missing from the file, so the notice says that and prices the re-run instead.
        Check("write_seq's truncation notice prices the re-run instead of prescribing 'raise max_chars'",
            seqCapped.Contains("nothing is missing from the FILE", StringComparison.Ordinal)
            && seqCapped.Contains("writes the .seq again", StringComparison.Ordinal)
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
            && sjnote.Contains("nothing is missing from the FILE", StringComparison.Ordinal)
            && sjnote.Contains("writes the .seq again", StringComparison.Ordinal)
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
}

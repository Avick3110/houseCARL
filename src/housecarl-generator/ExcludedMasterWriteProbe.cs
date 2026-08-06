using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD for #314 — ONE active plugin that Mutagen cannot OPEN must not break every write in the order.
///
/// <para><b>The bug.</b> <c>BuildIndex</c> excludes a plugin along two different paths: <c>OpenOverlay</c> throws
/// (the file cannot be opened at all), or <c>EnumerateMajorRecords</c> throws (it opens, but a record body is
/// unparseable). <c>OverlaySession.AllMasters</c>/<c>AllMastersExcept</c> then open EVERY plugin including excluded
/// ones — deliberately, because a clean plugin can override a record whose ORIGIN master is excluded and that master
/// must still appear in the patch header. Its stated safety argument is "Overlay() opens lazily (no parse, no
/// enumeration → no throw)", which holds for the second class and is FALSE for the first: the class that exists
/// precisely BECAUSE the file cannot be opened is the one the write path then tries to open, on every write.</para>
///
/// <para><b>Why its own probe.</b> The fixture cannot be shared. An unopenable plugin in the active order poisons
/// every write in whatever order it sits in — which is exactly the bug, and is how this was found (it broke every
/// unrelated arm of <c>write-surface-guard</c> when added there). So this order is built once, used only here, and
/// deliberately contains the broken plugin.</para>
///
/// Run: <c>dotnet run --project src/housecarl-generator excluded-master-guard</c>
/// </summary>
public static class ExcludedMasterWriteProbe
{
    static int _pass, _fail;
    static void Check(string label, bool ok, string? got = null)
    {
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        if (!ok && got is not null) Console.WriteLine($"          got: {Trim(got)}");
        if (ok) _pass++; else _fail++;
    }
    static string Trim(string s) => s.Length <= 400 ? s.Replace("\n", " | ") : s[..400].Replace("\n", " | ") + " …";

    /// <summary>The render's "masters:" line alone — a whole-render search would false-match the plugin names that
    /// appear in the per-record rows, which is the assertion mistake this codebase has already paid for twice.</summary>
    static string MastersLineOf(string render)
        => render.Split('\n').FirstOrDefault(l => l.StartsWith("masters:", StringComparison.Ordinal)) ?? "";

    const string MasterName = "HcXMaster.esm";
    const string CleanName = "HcXClean.esp";
    const string BrokenName = "HcXBroken.esp";

    public static int RunGuard(string[] args)
    {
        _pass = _fail = 0;
        Console.WriteLine("################  REGRESSION GUARD — #314: an UNOPENABLE active plugin must not break every write  ################");
        Console.WriteLine();

        var root = Path.Combine(Path.GetTempPath(), "hc_x314_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var fx = Fixture.Build(Path.Combine(root, "fx"));

            // ---- The premise, asserted rather than assumed: the broken plugin IS excluded, and READS are unharmed. ----
            var asBroken = ReadTools.ReadRecord(fx.Svc, formid: fx.SubjectFid, plugin: BrokenName);
            Check("the truncated plugin is EXCLUDED from the index, refused BY NAME with the reason (the read side handles this)",
                asBroken.StartsWith("error:") && asBroken.Contains("exclud", StringComparison.OrdinalIgnoreCase), asBroken);

            var read = ReadTools.ReadRecord(fx.Svc, formid: fx.SubjectFid);
            Check("reads are unaffected: the clean override still resolves as the winner",
                !read.StartsWith("error:") && read.Contains($"winner={CleanName}", StringComparison.OrdinalIgnoreCase), read);

            // ---- THE BUG: writes that have NOTHING to do with the broken plugin. ----
            var fwd = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: MasterName, patch: "X314Fwd");
            Check("forward into a NEW patch succeeds — the broken plugin is not this write's business",
                !fwd.StartsWith("error:"), fwd);

            var apply = ApplyTools.Apply(fx.Svc,
                ops: System.Text.Json.JsonDocument.Parse(
                    $$"""[{"formid":"{{fx.SubjectFid}}","field_path":"Name","value":"X314"}]""").RootElement.Clone(),
                patch: "X314Apply");
            Check("apply into a NEW patch succeeds",
                !apply.StartsWith("error:"), apply);

            var create = CreateTools.Create(fx.Svc, patch: "X314Create",
                records: System.Text.Json.JsonDocument.Parse(
                    """[{"record_type":"Keyword","editorid":"X314Kw"}]""").RootElement.Clone());
            Check("create into a NEW patch succeeds",
                !create.StartsWith("error:"), create);

            // ---- The refusal that must NOT be softened: naming the broken plugin itself is still refused. ----
            var fromBroken = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: BrokenName, patch: "X314Bad");
            Check("naming the EXCLUDED plugin as a source is still refused (the skip must not become an escape hatch)",
                fromBroken.StartsWith("error:"), fromBroken);

            // ---- THE RISK the fix has to answer: does skipping corrupt the HEADER? The retention comment said
            //      dropping an excluded master would, because a clean plugin can override a record ORIGINATING in it
            //      and that master must appear in the output. It does appear — Mutagen derives the header from the
            //      records' own FormKeys, not from membership of the known-master list. Asserted, because the whole
            //      safety of this change rests on it. ----
            //      Reachable HERE only because this fixture carries no baselines to force-include, leaving a genuinely
            //      single-entry header — see RealOrderArm for what a user's order actually does (PR #315 review 3).
            var needsIt = ForwardTools.Forward(fx.Svc, formids: new[] { fx.BrokenOwnFid }, source: CleanName, patch: "X314Need");
            Check("a baseline-less order: a patch whose record ORIGINATES in the unopenable plugin still WRITES, mastering on it",
                !needsIt.StartsWith("error:")
                && MastersLineOf(needsIt).Contains(BrokenName, StringComparison.OrdinalIgnoreCase), needsIt);

            // …and a MULTI-master header is the residual: it cannot be built at all, so there is no ordering to pin
            // here (an earlier draft's comment claimed there was, over locals nothing read — the assertion had been
            // replaced by the refusal check below and the prose was left behind, PR #315 review 2). Single-master
            // ordering is trivially satisfied by the arm above, which asserts the master IS the header.
            var both = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid, fx.BrokenOwnFid }, source: CleanName, patch: "X314Both");
            // PINNED, not branched. The earlier version asserted ordering on success and the CAUSE on failure with
            // nothing asserting WHICH outcome was expected — so a behaviour flip in either direction would still have
            // reported PASS while the clause silently lost all CI coverage (PR #315 review). The two-master header is
            // the case that REFUSES; the one-master header above is the case that WRITES. Both sides are now facts the
            // guard holds, which is also what licenses the dry-run predictor to use that threshold.
            Check("a MULTI-master header REFUSES (the sorted-header residual the skip leaves) — pinned, not assumed",
                both.StartsWith("error:"),
                both.StartsWith("error:") ? both
                    : "UPSTREAM BEHAVIOUR CHANGED: a two-master header now WRITES. Re-derive the residual — the "
                      + "dry-run predictor's >1 threshold and UnopenableMasterClause both rest on this. Got: " + both);
            Check("…and that refusal NAMES the unopenable plugin as the cause, with the remedy",
                both.Contains(BrokenName, StringComparison.OrdinalIgnoreCase)
                && both.Contains("cannot be opened by houseCARL", StringComparison.Ordinal)
                && both.Contains("writes that do NOT reference their records are unaffected", StringComparison.Ordinal), both);

            // The DRY RUN must predict that refusal rather than reporting a would-be success (#225 parity).
            var dry = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid, fx.BrokenOwnFid }, source: CleanName,
                patch: "X314DryBoth", dry_run: true);
            Check("dry_run predicts the SAME refusal for the same call, naming the same cause (#225 parity)",
                dry.StartsWith("error:") && dry.Contains(BrokenName, StringComparison.OrdinalIgnoreCase)
                && dry.Contains("cannot be opened by houseCARL", StringComparison.Ordinal), dry);
            var dryOk = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: MasterName,
                patch: "X314DryOk", dry_run: true);
            Check("…and a dry run of a write that does NOT reference it still predicts success (no over-refusal)",
                dryOk.StartsWith("DRY RUN", StringComparison.Ordinal), dryOk);

            // ---- The IN-PLACE lanes: their MissingModException arm fires BEFORE the generic catch, so it — not the
            //      generic one — is where this residual lands, and its "NOT active in the load order" wording would
            //      send the user to enable a plugin that IS enabled (PR #315 review). No new-patch arm can catch this.
            //      The target must be an ACTIVE plugin (that lane's own contract), so it is the clean plugin — which
            //      also declares the broken one as a master, giving the sorted header this residual needs.
            //      One forwarded record is enough: an in-place write RE-SERIALIZES the whole target, and the clean
            //      plugin's own header already needs both masters.
            var ip = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid },
                source: MasterName, in_place: CleanName, acknowledge: true);
            Check("in_place: the residual is named by CAUSE, not as 'NOT active in the load order'",
                ip.StartsWith("error:")
                && ip.Contains("cannot be opened by houseCARL", StringComparison.Ordinal)
                && !ip.Contains("is NOT active in", StringComparison.Ordinal), ip);

            // ---- The remove-IN-PLACE lane does NOT use the master-set builders at all: it opens the target's own
            //      declared masters directly, with a bare CreateFromBinaryOverlay that used to sit outside every catch,
            //      so an unopenable declared master escaped as an unhandled exception rather than a refusal. The clean
            //      plugin declares the broken one, so this reaches it (PR #315 review).
            var rm = RemoveTools.Remove(fx.Svc, formids: new[] { fx.SubjectFid }, in_place: CleanName, acknowledge: true);
            Check("remove in_place: an unopenable DECLARED master is a named refusal, not an escaping exception",
                rm.StartsWith("error:")
                && rm.Contains(BrokenName, StringComparison.OrdinalIgnoreCase)
                && rm.Contains("cannot be opened by houseCARL", StringComparison.Ordinal)
                && rm.Contains("UNTOUCHED", StringComparison.Ordinal), rm);

            BaselineArm(Path.Combine(root, "bl"));
            RealOrderArm(Path.Combine(root, "ro"));

            Console.WriteLine();
            Console.WriteLine($"=== excluded-master-guard: {_pass} passed, {_fail} failed -> {(_fail == 0 ? "PASS" : "FAIL")} ===");
            return _fail == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   FAIL (unexpected): {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return 1;
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    /// <summary>The order a REAL user has: baselines present and openable, plus one broken plugin. The main fixture
    /// carries no baselines, which made it report a single-master success that cannot happen in practice — every
    /// patch-lane header force-includes Skyrim.esm + Update.esm, so it always has ≥2 entries and always has to be
    /// SORTED, which is exactly the residual (PR #315 review 3). This arm is the one that describes reality; the
    /// main fixture's single-master arm now documents itself as the baseline-less harness case.</summary>
    static void RealOrderArm(string dir)
    {
        Console.WriteLine();
        Console.WriteLine("   -- a REAL order (baselines present): the unrelated write lands, the referencing one refuses --");

        string instance = Path.Combine(dir, "instance");
        string profiles = Path.Combine(instance, "profiles", "Default");
        string mods = Path.Combine(instance, "mods");
        Directory.CreateDirectory(profiles); Directory.CreateDirectory(mods);
        Directory.CreateDirectory(Path.Combine(dir, "game", "Data"));
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(dir, "game").Replace(@"\", @"\\") + ")\r\n");

        string Write(string folder, ModKey k, SkyrimMod m, params ISkyrimModGetter[] lo)
        {
            var p = Path.Combine(mods, folder, k.FileName.String);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            m.BeginWrite.ToPath(p).WithLoadOrder(lo).Write();
            return p;
        }

        var skyKey = new ModKey("Skyrim", ModType.Master);
        var updKey = new ModKey("Update", ModType.Master);
        var brkKey = new ModKey("SxBroken", ModType.Plugin);
        var clnKey = new ModKey("SxClean", ModType.Plugin);

        var sky = new SkyrimMod(skyKey, SkyrimRelease.SkyrimSE);
        var subject = sky.Weapons.AddNew();
        subject.EditorID = "SxSubject";
        subject.BasicStats = new WeaponBasicStats { Damage = 10 };
        Write("SxSky", skyKey, sky);
        Write("SxUpd", updKey, new SkyrimMod(updKey, SkyrimRelease.SkyrimSE), sky);

        var brk = new SkyrimMod(brkKey, SkyrimRelease.SkyrimSE);
        var brkOwn = brk.Weapons.AddNew();
        brkOwn.EditorID = "SxBrokenOwn";
        brkOwn.BasicStats = new WeaponBasicStats { Damage = 40 };
        var brkPath = Write("SxBroken", brkKey, brk, sky);

        var cln = new SkyrimMod(clnKey, SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(cln, brkOwn)).BasicStats = new WeaponBasicStats { Damage = 41 };
        Write("SxClean", clnKey, cln, sky, brk);

        var whole = File.ReadAllBytes(brkPath);
        File.WriteAllBytes(brkPath, whole[..(whole.Length - 12)]);

        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"),
            "# header\r\n" + skyKey.FileName + "\r\n" + updKey.FileName + "\r\n" + brkKey.FileName + "\r\n" + clnKey.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"),
            "*" + skyKey.FileName + "\r\n*" + updKey.FileName + "\r\n*" + brkKey.FileName + "\r\n*" + clnKey.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+SxClean\r\n+SxBroken\r\n+SxUpd\r\n+SxSky\r\n");

        using var svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(dir, "hc.user.json")));
        svc.Stats();

        // THE HEADLINE FIX, in the shape a user actually has.
        var unrelated = ForwardTools.Forward(svc, formids: new[] { $"{subject.FormKey.ID:X6}:{skyKey.FileName}" },
            source: skyKey.FileName.String, patch: "SxOk");
        Check("REAL order: a write that doesn't reference the broken plugin lands, with the baselines in its header",
            !unrelated.StartsWith("error:")
            && MastersLineOf(unrelated).Contains(skyKey.FileName.String, StringComparison.OrdinalIgnoreCase), unrelated);

        // …and the case the changelog claimed "now succeeds". It does not, and cannot: the force-include guarantees
        // ≥2 masters, so this header must be sorted and hits the residual.
        var referencing = ForwardTools.Forward(svc, formids: new[] { $"{brkOwn.FormKey.ID:X6}:{brkKey.FileName}" },
            source: clnKey.FileName.String, patch: "SxNeed");
        Check("REAL order: a write REFERENCING the broken plugin's record refuses, naming the cause (never a single-master success)",
            referencing.StartsWith("error:")
            && referencing.Contains(brkKey.FileName.String, StringComparison.OrdinalIgnoreCase)
            && referencing.Contains("cannot be opened by houseCARL", StringComparison.Ordinal), referencing);
    }

    /// <summary>The BASELINE case, which the main fixture structurally cannot see: it has no Skyrim.esm/Update.esm, and
    /// the defect is in the CK-mandated baseline force-include (PR #315 review 2). A baseline master that cannot be
    /// opened must REFUSE the write — skipping it silently emits a plugin missing a master Aaron locked as mandatory,
    /// which is a loud failure traded for a silent one. Its own order, because a broken Skyrim.esm poisons everything.</summary>
    static void BaselineArm(string dir)
    {
        Console.WriteLine();
        Console.WriteLine("   -- baseline masters: an unopenable Skyrim.esm must REFUSE, never be quietly dropped --");

        string instance = Path.Combine(dir, "instance");
        string profiles = Path.Combine(instance, "profiles", "Default");
        string mods = Path.Combine(instance, "mods");
        Directory.CreateDirectory(profiles); Directory.CreateDirectory(mods);
        Directory.CreateDirectory(Path.Combine(dir, "game", "Data"));
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(dir, "game").Replace(@"\", @"\\") + ")\r\n");

        // A real baseline NAME, deliberately: the force-include matches on ModKey, so only this name reaches the bug.
        var skyKey = new ModKey("Skyrim", ModType.Master);
        var skyPath = Path.Combine(mods, "BlSky", skyKey.FileName.String);
        Directory.CreateDirectory(Path.GetDirectoryName(skyPath)!);
        var sky = new SkyrimMod(skyKey, SkyrimRelease.SkyrimSE);
        var w = sky.Weapons.AddNew();
        w.EditorID = "BlSubject";
        w.BasicStats = new WeaponBasicStats { Damage = 10 };
        sky.BeginWrite.ToPath(skyPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        var whole = File.ReadAllBytes(skyPath);
        File.WriteAllBytes(skyPath, whole[..(whole.Length - 12)]);          // …and break it

        // A clean plugin overriding it, so the dry-run arm has a resolvable record to aim at (create has no dry lane).
        var clnKey = new ModKey("BlClean", ModType.Plugin);
        var clnPath = Path.Combine(mods, "BlClean", clnKey.FileName.String);
        Directory.CreateDirectory(Path.GetDirectoryName(clnPath)!);
        var cln = new SkyrimMod(clnKey, SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(cln, w)).BasicStats = new WeaponBasicStats { Damage = 20 };
        var donor = cln.Npcs.AddNew();                 // the npc-copy lane's donor — it only has to reach the serialize
        donor.EditorID = "BlDonor";
        cln.BeginWrite.ToPath(clnPath).WithLoadOrder(new ISkyrimModGetter[] { sky }).Write();

        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + skyKey.FileName + "\r\n" + clnKey.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + skyKey.FileName + "\r\n*" + clnKey.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+BlClean\r\n+BlSky\r\n");

        using var svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(dir, "hc.user.json")));
        svc.Stats();

        // A SELF-CONTAINED create references nothing, so its derived header is empty and the baseline force-include is
        // the ONLY thing that would put Skyrim.esm in it — the exact write that used to land silently master-less.
        var records = System.Text.Json.JsonDocument.Parse("""[{"record_type":"Keyword","editorid":"BlKw"}]""").RootElement.Clone();
        var created = CreateTools.Create(svc, patch: "BlCreate", records: records);
        Check("a self-contained create REFUSES when a baseline master is unopenable (never a silently master-less plugin)",
            created.StartsWith("error:")
            && created.Contains("BASELINE master", StringComparison.Ordinal)
            && created.Contains(skyKey.FileName.String, StringComparison.OrdinalIgnoreCase), created);

        // …ONCE. A Contains() is satisfied by a doubled render, and that is exactly what this arm used to hide: the
        // clause appended the exception's own Message behind a Describe() that already printed it (PR #315 review 3).
        // Counting is the difference between asserting the text exists and asserting the message is right.
        int occurrences = created.Split("BASELINE master").Length - 1;
        Check("…and renders that refusal exactly ONCE, without the serialize lead-in it never reached",
            occurrences == 1
            && !created.Contains("serialize or commit", StringComparison.Ordinal)
            && !created.Contains("the existing file is untouched", StringComparison.Ordinal),
            $"occurrences={occurrences} :: {created}");

        // …and the dry run agrees with the real call, which it did NOT before: it added baselines by load-order
        // membership and never asked whether they could be opened (#225 parity).
        var subjectFid = $"{w.FormKey.ID:X6}:{skyKey.FileName}";
        var dry = ApplyTools.Apply(svc, patch: "BlDry", dry_run: true,
            ops: System.Text.Json.JsonDocument.Parse(
                $$"""[{"formid":"{{subjectFid}}","field_path":"Name","value":"Bl"}]""").RootElement.Clone());
        Check("…and dry_run predicts that same refusal, naming the baseline (#225 parity)",
            dry.StartsWith("error:") && dry.Contains("BASELINE master", StringComparison.Ordinal), dry);

        // copy_npc_appearance is the one write lane that renders through an INJECTED renderer rather than calling
        // SerializeFailure directly, so it is the lane that silently kept the doubled tail and the wrong phase after
        // review 3 fixed every other one (PR #315 review 4). It had no arm; that is why nothing caught it.
        var npc = NpcCopyTools.CopyNpcAppearance(svc, source_formid: $"{donor.FormKey.ID:X6}:{clnKey.FileName}",
            new_editorid: "BlDonorClone", patch_name: "BlNpc");
        Check("npc-copy renders the baseline refusal through the SAME substituting renderer as its sibling lanes",
            npc.StartsWith("error:")
            && npc.Contains("BASELINE master", StringComparison.Ordinal)
            // Pinned on the three things substitution actually guarantees. (An earlier draft counted the word
            // "Nothing", which the reworded refusal legitimately uses twice — a false failure, and a reminder that a
            // count is only as good as the token counted.)
            && (npc.Split("BASELINE master").Length - 1) == 1                       // the refusal ONCE, not doubled
            && !npc.Contains("Nothing usable was written", StringComparison.Ordinal) // the lane's trailer dropped
            && !npc.Contains("..", StringComparison.Ordinal)                         // …so no doubled period
            && !npc.Contains("serialize failed", StringComparison.Ordinal),          // …behind a phase never reached
            npc);
    }

    sealed class Fixture : IDisposable
    {
        public required LoadOrderService Svc { get; init; }
        public required string SubjectFid { get; init; }      // originates in the MASTER, overridden by the clean plugin
        public required string BrokenOwnFid { get; init; }    // ORIGINATES in the unopenable plugin, overridden by clean
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

            var mKey = new ModKey("HcXMaster", ModType.Master);
            var bKey = new ModKey("HcXBroken", ModType.Plugin);
            var cKey = new ModKey("HcXClean", ModType.Plugin);
            string P(string folder, ModKey k)
            {
                var p = Path.Combine(mods, folder, k.FileName.String);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                return p;
            }

            var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
            var subject = m.Weapons.AddNew();
            subject.EditorID = "XSubject";
            subject.BasicStats = new WeaponBasicStats { Damage = 10 };
            m.BeginWrite.ToPath(P("XMaster", mKey)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            // The broken plugin ORIGINATES a record of its own, so a patch can be made to need it as a master.
            var b = new SkyrimMod(bKey, SkyrimRelease.SkyrimSE);
            ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(b, subject)).BasicStats = new WeaponBasicStats { Damage = 30 };
            var brokenOwn = b.Weapons.AddNew();
            brokenOwn.EditorID = "XBrokenOwn";
            brokenOwn.BasicStats = new WeaponBasicStats { Damage = 40 };
            var brokenPath = P("XBroken", bKey);
            b.BeginWrite.ToPath(brokenPath).WithLoadOrder(new ISkyrimModGetter[] { m }).Write();

            // The clean plugin wins the subject AND overrides the broken plugin's own record (so that record stays
            // readable through a plugin that parses, which is the situation AllMasters' retention comment describes).
            var c = new SkyrimMod(cKey, SkyrimRelease.SkyrimSE);
            ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(c, subject)).BasicStats = new WeaponBasicStats { Damage = 20 };
            ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(c, brokenOwn)).BasicStats = new WeaponBasicStats { Damage = 41 };
            c.BeginWrite.ToPath(P("XClean", cKey)).WithLoadOrder(new ISkyrimModGetter[] { m, b }).Write();

            // …and NOW break it: a valid header followed by a truncated body. The overlay open throws, which is the
            // exclusion class this guard is about. Written last so both plugins above could be built against it.
            var whole = File.ReadAllBytes(brokenPath);
            File.WriteAllBytes(brokenPath, whole[..(whole.Length - 12)]);

            File.WriteAllText(Path.Combine(profiles, "loadorder.txt"),
                "# header\r\n" + mKey.FileName + "\r\n" + bKey.FileName + "\r\n" + cKey.FileName + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "plugins.txt"),
                "*" + mKey.FileName + "\r\n*" + bKey.FileName + "\r\n*" + cKey.FileName + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+XClean\r\n+XBroken\r\n+XMaster\r\n");

            var genDir = Path.Combine(dir, "corpus-gen");
            try { _ = CorpusRulebook.LoadCorpus(); }
            catch { CorpusGenerator.GenerateAll(genDir, Path.Combine(dir, "corpus-ref")); CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json"); }

            var svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(dir, "houseCARL.user.json")));
            svc.Stats();
            return new Fixture
            {
                Svc = svc,
                SubjectFid = $"{subject.FormKey.ID:X6}:{mKey.FileName}",
                BrokenOwnFid = $"{brokenOwn.FormKey.ID:X6}:{bKey.FileName}",
            };
        }
    }
}

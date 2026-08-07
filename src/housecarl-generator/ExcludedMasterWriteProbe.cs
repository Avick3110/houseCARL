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
            CompactMasterArm(Path.Combine(root, "cm"));
            RepointArm(Path.Combine(root, "rp"));
            InPlaceThresholdArm(Path.Combine(root, "ip"));

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
        // …with the SAME words, not merely the same phrase. A Contains() on both sides cannot see the two drift
        // apart, which is exactly what happened: review 4's rewording landed in the exception and not in the dry
        // run's paraphrase (PR #315 re-review). The refusals now share one string, so the arm asserts that identity
        // — the dry-run text must contain the real call's message verbatim.
        var realBody = created["error: ".Length..].Trim();
        Check("…and dry_run predicts that same refusal VERBATIM, not a paraphrase that can drift (#225 parity)",
            dry.StartsWith("error:") && dry.Contains(realBody, StringComparison.Ordinal),
            $"real=[{realBody}] dry=[{dry}]");

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

    // ---- #316: the three PR #315 fixes that shipped proven only by READING -------------------------------------
    //
    // Each takes its OWN order, for the reason this whole file exists: an unopenable plugin poisons every write in
    // the order it sits in. The two arms above predate them and are left as they are — retrofitting a working guard
    // onto a shared harness is churn with a real chance of breaking what it was written to hold.

    /// <summary>A synthetic MO2 instance skeleton (ini + profile dir + mods dir + game Data), shared by the #316 arms
    /// so three near-identical setups don't drift apart in three different ways. Returns the instance root and its
    /// mods dir; the caller writes the plugins, then calls <see cref="Profile"/>.</summary>
    static (string instance, string mods) NewInstance(string dir)
    {
        string instance = Path.Combine(dir, "instance");
        string mods = Path.Combine(instance, "mods");
        Directory.CreateDirectory(Path.Combine(instance, "profiles", "Default"));
        Directory.CreateDirectory(mods);
        Directory.CreateDirectory(Path.Combine(dir, "game", "Data"));
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(dir, "game").Replace(@"\", @"\\") + ")\r\n");
        return (instance, mods);
    }

    /// <summary>Write the three profile files for an order given in LOAD order (folder, plugin key). modlist.txt is
    /// MO2's reverse-priority list, so it is emitted reversed — getting that backwards silently inverts which copy of
    /// a filename wins.</summary>
    static void Profile(string instance, params (string folder, ModKey key)[] order)
    {
        var p = Path.Combine(instance, "profiles", "Default");
        File.WriteAllText(Path.Combine(p, "loadorder.txt"), "# header\r\n" + string.Concat(order.Select(o => o.key.FileName + "\r\n")));
        File.WriteAllText(Path.Combine(p, "plugins.txt"), string.Concat(order.Select(o => "*" + o.key.FileName + "\r\n")));
        File.WriteAllText(Path.Combine(p, "modlist.txt"), "# header\r\n" + string.Concat(order.Reverse().Select(o => "+" + o.folder + "\r\n")));
    }

    /// <summary>Write <paramref name="m"/> into <paramref name="mods"/>\<paramref name="folder"/> against
    /// <paramref name="lo"/>, returning the path.</summary>
    static string WriteMod(string mods, string folder, ModKey k, SkyrimMod m, params ISkyrimModGetter[] lo)
    {
        var p = Path.Combine(mods, folder, k.FileName.String);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        m.BeginWrite.ToPath(p).WithLoadOrder(lo).NoNextFormIDProcessing().Write();
        return p;
    }

    /// <summary>Break a written plugin into the could-not-be-OPENED exclusion class: a valid header followed by a
    /// truncated body. Twelve bytes is what the fixtures above use and what reliably produces the class.</summary>
    static void Truncate(string path)
    {
        var whole = File.ReadAllBytes(path);
        File.WriteAllBytes(path, whole[..(whole.Length - 12)]);
    }

    /// <summary>#316 (1) — <c>CompactBuild</c>'s declared-master open. The <c>catch</c> added around
    /// <c>CreateFromBinaryOverlay</c> to mirror <c>MergeBuild</c>'s twin shipped with no arm. Its whole reason for
    /// existing is the CALLER's cleanup: <c>RemoveOrNameRiderResidue</c> runs on a <c>Fail</c> RETURN and never on a
    /// throw, so an escaping exception leaves an orphan houseCARL mod folder in the MO2 mods directory — which is why
    /// this arm asserts the folder count as hard as it asserts the message.</summary>
    static void CompactMasterArm(string dir)
    {
        Console.WriteLine();
        Console.WriteLine("   -- compact: an unopenable DECLARED master is a named refusal, and leaves no orphan folder --");

        var (instance, mods) = NewInstance(dir);
        var brkKey = new ModKey("HcXCmBroken", ModType.Plugin);
        var subKey = new ModKey("HcXCmSubject", ModType.Plugin);

        var brk = new SkyrimMod(brkKey, SkyrimRelease.SkyrimSE);
        var brkOwn = brk.Weapons.AddNew(); brkOwn.EditorID = "CmBrokenOwn";
        brkOwn.BasicStats = new WeaponBasicStats { Damage = 40 };
        var brkPath = WriteMod(mods, "CmBroken", brkKey, brk);

        // The compact SUBJECT: its own originating record (something to renumber) plus an override of the broken
        // plugin's record — which is what puts the unopenable plugin in its DECLARED master header.
        var sub = new SkyrimMod(subKey, SkyrimRelease.SkyrimSE);
        if (sub.ModHeader.Stats.NextFormID < 0x800) sub.ModHeader.Stats.NextFormID = 0x800;
        var subOwn = sub.Weapons.AddNew(); subOwn.EditorID = "CmSubjectOwn";
        subOwn.BasicStats = new WeaponBasicStats { Damage = 15 };
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(sub, brkOwn)).BasicStats = new WeaponBasicStats { Damage = 41 };
        WriteMod(mods, "CmSubject", subKey, sub, brk);

        Truncate(brkPath);                                   // …last, so the subject could be built against it
        Profile(instance, ("CmBroken", brkKey), ("CmSubject", subKey));

        using var svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(dir, "hc.user.json")));
        svc.Stats();

        int before = Directory.GetDirectories(mods).Length;
        var outcome = svc.CompactPlugin(subKey.FileName.String, esl: true, inPlace: false, repointExternals: false,
            acknowledge: false, patchName: null);
        int after = Directory.GetDirectories(mods).Length;

        Check("compact: an unopenable DECLARED master is a named Fail (not an escaping engine throw), naming the plugin and the remedy",
            !outcome.Success && outcome.Error is { } cerr
            && cerr.Contains(brkKey.FileName.String, StringComparison.OrdinalIgnoreCase)
            && cerr.Contains("could not be opened for the serialize", StringComparison.Ordinal)
            && cerr.Contains("Nothing was written", StringComparison.Ordinal),
            outcome.Error ?? "(succeeded)");
        // The REASON the wrap exists, and the half a message assertion cannot see: cleanup only runs on a Fail RETURN.
        Check("…and the fresh mod folder it cut is REMOVED — the cleanup an escaping throw would have skipped",
            after == before, $"mod folders {before} -> {after}");
    }

    /// <summary>#316 (2) — <c>RemapEngine.RepointInPlace</c>'s <c>IsUnopenable</c> pre-check + wrap. The one that
    /// matters most: its caller reaches it only AFTER the compacted plugin is already on disk, so an escaping throw
    /// discards every per-plugin repoint result and skips the facegen/voice/SEQ carry that follows — on a
    /// half-completed compaction. Driven through the TOOL, because the difference this fix makes is precisely
    /// "a named per-plugin failure" vs "Guard.Tool's generic internal-failure wrapper".</summary>
    static void RepointArm(string dir)
    {
        Console.WriteLine();
        Console.WriteLine("   -- compact + repoint: an external referencer whose OWN master is unopenable fails NAMED, mid-compaction --");

        var (instance, mods) = NewInstance(dir);
        var brkKey = new ModKey("HcXRpBroken", ModType.Plugin);
        var tgtKey = new ModKey("HcXRpTarget", ModType.Plugin);
        var extKey = new ModKey("HcXRpExternal", ModType.Plugin);

        var brk = new SkyrimMod(brkKey, SkyrimRelease.SkyrimSE);
        var brkOwn = brk.Weapons.AddNew(); brkOwn.EditorID = "RpBrokenOwn";
        brkOwn.BasicStats = new WeaponBasicStats { Damage = 40 };
        var brkPath = WriteMod(mods, "RpBroken", brkKey, brk);

        // The compact TARGET declares NO unopenable master itself — otherwise CompactBuild refuses first and the
        // repoint stage is never reached (which is the arm above, not this one).
        var tgt = new SkyrimMod(tgtKey, SkyrimRelease.SkyrimSE);
        if (tgt.ModHeader.Stats.NextFormID < 0x800) tgt.ModHeader.Stats.NextFormID = 0x800;
        var tgtOwn = tgt.Weapons.AddNew(); tgtOwn.EditorID = "RpTargetOwn";
        tgtOwn.BasicStats = new WeaponBasicStats { Damage = 15 };
        var tgtPath = WriteMod(mods, "RpTarget", tgtKey, tgt);

        // The EXTERNAL referencer: it points at the target's record (so the compaction must repoint it) AND declares
        // the broken plugin as a master (so the repoint's own re-serialize hits the unopenable open).
        // …and the reference must be a LINK, not an override: HasExternalReferencers counts FormLinks INTO the
        // renumbered records (an override is reported separately and never gates the operation), so an override-only
        // "referencer" produced "external referencers: none" and the repoint stage never ran at all.
        var ext = new SkyrimMod(extKey, SkyrimRelease.SkyrimSE);
        if (ext.ModHeader.Stats.NextFormID < 0x800) ext.ModHeader.Stats.NextFormID = 0x800;
        var lvl = ext.LeveledItems.AddNew(); lvl.EditorID = "RpExtList";
        lvl.Entries = new Noggog.ExtendedList<LeveledItemEntry>
        {
            new LeveledItemEntry { Data = new LeveledItemEntryData { Level = 1, Count = 1, Reference = tgtOwn.ToLink<IItemGetter>() } },
        };
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(ext, brkOwn)).BasicStats = new WeaponBasicStats { Damage = 41 };
        WriteMod(mods, "RpExternal", extKey, ext, brk, tgt);

        Truncate(brkPath);
        Profile(instance, ("RpBroken", brkKey), ("RpTarget", tgtKey), ("RpExternal", extKey));

        using var svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(dir, "hc.user.json")));
        svc.Stats();

        var targetBefore = File.ReadAllBytes(tgtPath);   // BYTES, not length: a renumber rewrites ids in place and the size is identical
        var render = WriteTools.CompactPlugin(svc, plugin: tgtKey.FileName.String, esl: true, in_place: true,
            repoint_externals: true, acknowledge: true);

        Check("repoint: the per-plugin failure is NAMED — the unopenable master, the remedy, and the file left UNTOUCHED",
            render.Contains(extKey.FileName.String, StringComparison.OrdinalIgnoreCase)
            && render.Contains(brkKey.FileName.String, StringComparison.OrdinalIgnoreCase)
            && render.Contains("cannot be opened by houseCARL", StringComparison.Ordinal)
            && render.Contains("UNTOUCHED", StringComparison.Ordinal),
            Trim(render));
        // The consequence the pre-check exists for: a throw here escapes to Guard.Tool, which reports an INTERNAL
        // failure — discarding the repoint results and skipping the asset carry, on an ALREADY-REWRITTEN target.
        Check("…and it is a REPORTED result, not Guard.Tool's internal-failure wrapper (the throw would land after P′ is on disk)",
            !render.Contains("internal failure", StringComparison.OrdinalIgnoreCase)
            && !render.Contains("unexpected", StringComparison.OrdinalIgnoreCase),
            Trim(render));
        // …and the compaction is still REPORTED. P′ lands before the repoint either way, so "the file changed" alone
        // proves nothing (its own RED check passed on that clause); what the throw destroys is the caller's RESULT —
        // the compaction report, the other referencers' outcomes, and the asset carry that follows it.
        Check("…and the compaction itself is still REPORTED — the failing repoint is one plugin's result, not the call's",
            !File.ReadAllBytes(tgtPath).AsSpan().SequenceEqual(targetBefore)
            && render.Contains("compacted", StringComparison.OrdinalIgnoreCase),
            $"target rewritten={!File.ReadAllBytes(tgtPath).AsSpan().SequenceEqual(targetBefore)} :: {Trim(render)}");
    }

    /// <summary>#316 (3) — the IN-PLACE lane's single-master threshold. <c>DryRunMastersPreview(patchLane:false)</c>
    /// predicts the same <c>set.Count > 1</c> rule the patch lane uses, and nothing verified it on THIS lane. The
    /// patch-lane side is pinned both ways above (a one-master header writes; a two-master header refuses); this is
    /// its twin, pinned the same two ways, and with the dry run agreeing on BOTH — a threshold verified in only one
    /// direction is how a predictor drifts into over- or under-refusing without a guard noticing.</summary>
    static void InPlaceThresholdArm(string dir)
    {
        Console.WriteLine();
        Console.WriteLine("   -- in-place: the single-master threshold, pinned BOTH ways, real call and dry run --");

        var (instance, mods) = NewInstance(dir);
        var mstKey = new ModKey("HcXIpMaster", ModType.Master);
        var brkKey = new ModKey("HcXIpBroken", ModType.Plugin);
        var oneKey = new ModKey("HcXIpOne", ModType.Plugin);
        var twoKey = new ModKey("HcXIpTwo", ModType.Plugin);

        var mst = new SkyrimMod(mstKey, SkyrimRelease.SkyrimSE);
        var mstOwn = mst.Weapons.AddNew(); mstOwn.EditorID = "IpMasterOwn";
        mstOwn.BasicStats = new WeaponBasicStats { Damage = 10 };
        WriteMod(mods, "IpMaster", mstKey, mst);

        var brk = new SkyrimMod(brkKey, SkyrimRelease.SkyrimSE);
        var brkOwn = brk.Weapons.AddNew(); brkOwn.EditorID = "IpBrokenOwn";
        brkOwn.BasicStats = new WeaponBasicStats { Damage = 40 };
        var brkPath = WriteMod(mods, "IpBroken", brkKey, brk);

        // ONE master: overrides only the broken plugin's record, so its header is exactly {broken}.
        var one = new SkyrimMod(oneKey, SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(one, brkOwn)).BasicStats = new WeaponBasicStats { Damage = 41 };
        WriteMod(mods, "IpOne", oneKey, one, brk);

        // TWO masters: the same override PLUS one of the openable master's, so the header must be SORTED.
        var two = new SkyrimMod(twoKey, SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(two, brkOwn)).BasicStats = new WeaponBasicStats { Damage = 42 };
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(two, mstOwn)).BasicStats = new WeaponBasicStats { Damage = 11 };
        WriteMod(mods, "IpTwo", twoKey, two, mst, brk);

        Truncate(brkPath);
        Profile(instance, ("IpMaster", mstKey), ("IpBroken", brkKey), ("IpOne", oneKey), ("IpTwo", twoKey));

        using var svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(dir, "hc.user.json")));
        svc.Stats();

        string brokenFid = $"{brkOwn.FormKey.ID:X6}:{brkKey.FileName}";
        string Edit(string fid, string val) => $$"""[{"formid":"{{fid}}","field_path":"BasicStats.Damage","value":"{{val}}"}]""";
        System.Text.Json.JsonElement Ops(string fid, string val)
            => System.Text.Json.JsonDocument.Parse(Edit(fid, val)).RootElement.Clone();

        // ONE master — WRITES. (The record it edits ORIGINATES in the unopenable plugin, so the header entry comes
        // from the record's own FormKey; nothing has to be sorted against a plugin that cannot be opened.)
        var oneReal = ApplyTools.Apply(svc, ops: Ops(brokenFid, "44"), in_place: oneKey.FileName.String, acknowledge: true);
        Check("in_place, ONE-master header: the write LANDS even though that one master is unopenable",
            !oneReal.StartsWith("error:"), oneReal);
        var oneDry = ApplyTools.Apply(svc, ops: Ops(brokenFid, "45"), in_place: oneKey.FileName.String, acknowledge: true, dry_run: true);
        Check("…and the dry run AGREES (no over-refusal — the predictor's threshold, on the lane that predicts it)",
            oneDry.StartsWith("DRY RUN", StringComparison.Ordinal), oneDry);

        // TWO masters — REFUSES, by CAUSE. This is the case a real order always takes.
        var twoReal = ApplyTools.Apply(svc, ops: Ops(brokenFid, "46"), in_place: twoKey.FileName.String, acknowledge: true);
        Check("in_place, TWO-master header: the write REFUSES, naming the unopenable plugin as the cause",
            twoReal.StartsWith("error:")
            && twoReal.Contains(brkKey.FileName.String, StringComparison.OrdinalIgnoreCase)
            && twoReal.Contains("cannot be opened by houseCARL", StringComparison.Ordinal)
            && !twoReal.Contains("is NOT active in", StringComparison.Ordinal), twoReal);
        var twoDry = ApplyTools.Apply(svc, ops: Ops(brokenFid, "47"), in_place: twoKey.FileName.String, acknowledge: true, dry_run: true);
        Check("…and the dry run predicts THAT refusal too (#225 parity on the in-place lane, both directions now pinned)",
            twoDry.StartsWith("error:")
            && twoDry.Contains(brkKey.FileName.String, StringComparison.OrdinalIgnoreCase)
            && twoDry.Contains("cannot be opened by houseCARL", StringComparison.Ordinal), twoDry);
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

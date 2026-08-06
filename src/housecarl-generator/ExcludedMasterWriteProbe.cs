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

    /// <summary>The written artifact's path out of a write render (mod folder + filename), so a following arm can
    /// address it by name — e.g. as an in_place target.</summary>
    static string? ArtifactPathFrom(Fixture fx, string render)
    {
        if (!render.StartsWith("wrote ", StringComparison.Ordinal)) return null;
        var file = render[(render.IndexOf(' ') + 1)..];
        file = file[..file.IndexOf(' ')];
        const string marker = "mod folder: ";
        int at = render.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return null;
        var mod = render[(at + marker.Length)..].Split('\n')[0].Split("  ")[0].Trim();
        return Path.Combine(fx.ModsDir, mod, file);
    }

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
            var needsIt = ForwardTools.Forward(fx.Svc, formids: new[] { fx.BrokenOwnFid }, source: CleanName, patch: "X314Need");
            Check("a patch whose record ORIGINATES in the unopenable plugin still WRITES, and still masters on it",
                !needsIt.StartsWith("error:")
                && MastersLineOf(needsIt).Contains(BrokenName, StringComparison.OrdinalIgnoreCase), needsIt);

            // …and the ORDER of a multi-master header survives the skip: a patch touching a record from the master AND
            // one originating in the broken plugin must list them in LOAD ORDER (master first), or the plugin is
            // malformed in a way nothing else in this probe would catch.
            var both = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid, fx.BrokenOwnFid }, source: CleanName, patch: "X314Both");
            var line = MastersLineOf(both);
            int iMaster = line.IndexOf(MasterName, StringComparison.OrdinalIgnoreCase);
            int iBroken = line.IndexOf(BrokenName, StringComparison.OrdinalIgnoreCase);
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

    sealed class Fixture : IDisposable
    {
        public required LoadOrderService Svc { get; init; }
        public required string SubjectFid { get; init; }      // originates in the MASTER, overridden by the clean plugin
        public required string BrokenOwnFid { get; init; }    // ORIGINATES in the unopenable plugin, overridden by clean
        public required string ModsDir { get; init; }
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
                ModsDir = mods,
            };
        }
    }
}

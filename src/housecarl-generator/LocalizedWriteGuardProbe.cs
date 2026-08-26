using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;

namespace HousecarlGenerator;

/// <summary>
/// The localized in-place write's gate (#368 + #373): which strings shapes a write may act on, that the one it accepts
/// round-trips faithfully, that each shape it refuses says so in that shape's own words, that every remedy those
/// sentences name actually works, and that an interrupted commit leaves a BLANK plugin rather than a scrambled one.
///
/// <para>Two arms carry more weight than the rest. The ALLOW arm pins a NON-ENGLISH value, because the claim that a
/// multi-language plugin round-trips is the widening the whole design rests on and an English-only arm cannot see it
/// fail. And the window arms fault-inject INTO the commit rather than staging each intermediate state by hand, so they
/// measure the sequence the code actually performs rather than this file's idea of it.</para>
///
/// Run: dotnet run --project src/housecarl-generator localized-write-guard
/// </summary>
public static class LocalizedWriteGuardProbe
{
    const int Records = 4;
    static int _fail;
    static void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) _fail++; }

    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  LOCALIZED IN-PLACE WRITE — shape gate, round trip, refusals, crash window  ################");
        Console.WriteLine();
        _fail = 0;
        Shapes();
        Console.WriteLine();
        RoundTrip();
        Console.WriteLine();
        Refusals();
        Console.WriteLine();
        Remedies();
        Console.WriteLine();
        Windows();
        Console.WriteLine();
        Console.WriteLine(_fail == 0
            ? "[localized-write-guard] PASS — the accepted shape round-trips, every other shape refuses in its own words."
            : $"[localized-write-guard] FAIL — {_fail} arm(s).");
        return _fail == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- shapes

    static void Shapes()
    {
        Console.WriteLine("== shape classification ==");

        Run(Variant.LooseComplete, f =>
        {
            var a = LocalizedStrings.Assess(f.Plugin, f.DataDir);
            Check(a.Shape == LocalizedShape.LooseComplete && a.CanCommitStrings
                  && a.Languages.Count == 2 && a.Languages.Contains("French"),
                $"complete loose set beside the plugin is the ALLOW shape (shape={a.Shape} commit={a.CanCommitStrings} langs=[{string.Join(",", a.Languages)}])");
        });

        Run(Variant.LoosePartial, f =>
        {
            var a = LocalizedStrings.Assess(f.Plugin, f.DataDir);
            var named = a.IncompleteLanguages.TryGetValue("French", out var kinds) && kinds.Count == 2;
            Check(a.Shape == LocalizedShape.LoosePartial && !a.CanCommitStrings && named,
                $"a language missing table kinds is LoosePartial and the missing kinds are named (shape={a.Shape} missing={string.Join("+", a.IncompleteLanguages.SelectMany(kv => kv.Value))})");
        });

        Run(Variant.LooseAndGameData, f =>
        {
            var a = LocalizedStrings.Assess(f.Plugin, f.DataDir);
            Check(a.Shape == LocalizedShape.LooseWithGameDataDuplicate && !a.CanCommitStrings && a.GameDataLanguages.Count > 0,
                $"a loose set duplicated in game-Data is refused (shape={a.Shape} gameData=[{string.Join(",", a.GameDataLanguages)}])");
        });

        Run(Variant.GameDataOnly, f =>
        {
            var a = LocalizedStrings.Assess(f.Plugin, f.DataDir);
            Check(a.Shape == LocalizedShape.GameDataOnly && !a.CanCommitStrings,
                $"strings resolving from game-Data only is refused (shape={a.Shape})");
        });

        Run(Variant.Nowhere, f =>
        {
            var a = LocalizedStrings.Assess(f.Plugin, f.DataDir);
            Check(a.Shape == LocalizedShape.Nowhere && !a.CanCommitStrings,
                $"no findable strings source is refused (shape={a.Shape})");
        });

        Run(Variant.MalformedBsa, f =>
        {
            var a = LocalizedStrings.Assess(f.Plugin, f.DataDir);
            Check(a.Shape == LocalizedShape.BsaEmbedded && a.BsaUnreadable && !a.CanCommitStrings,
                $"an archive that cannot be parsed is refused rather than assumed harmless (shape={a.Shape} unreadable={a.BsaUnreadable})");
        });

        // The defect the real-order sweep found: two plugins share one mod folder's Strings folder, and a stem prefix
        // match alone made the shorter-named one absorb the longer-named one's tables. A write acting on that would
        // have backed up and deleted a DIFFERENT plugin's files.
        Run(Variant.SiblingStem, f =>
        {
            var mine = LocalizedStrings.Assess(f.Plugin, f.DataDir);
            var sibling = LocalizedStrings.Assess(f.Plugin.Replace("ZRef.esp", "ZRef_extra.esp"), f.DataDir);
            var mineFiles = LocalizedStrings.OwnTableFiles(f.Plugin);
            Check(mine.Languages.Count == 2 && sibling.Languages.Count == 2
                  && mineFiles.All(p => !Path.GetFileName(p).StartsWith("ZRef_extra", StringComparison.OrdinalIgnoreCase)),
                $"a plugin whose name prefixes a sibling's claims only its OWN tables (mine={mine.Languages.Count} sibling={sibling.Languages.Count} files={mineFiles.Count})");
        });

        // dataDir unknown cannot be treated as "checked and found nothing": the duplicate it fails to rule out is
        // exactly what would keep the commit's blank window from being blank.
        Run(Variant.LooseComplete, f =>
        {
            var a = LocalizedStrings.Assess(f.Plugin, dataDir: null);
            Check(a.Shape == LocalizedShape.LooseComplete && a.GameDataUnknown && !a.CanCommitStrings,
                $"an unknown game-Data folder blocks the ALLOW shape rather than passing it (unknown={a.GameDataUnknown} commit={a.CanCommitStrings})");
        });
    }

    // ---------------------------------------------------------------- round trip

    static void RoundTrip()
    {
        Console.WriteLine("== round trip through the real write ==");
        Run(Variant.LooseComplete, f =>
        {
            var edited = WriteThrough(f, w => w.Name = Loc("EDITED 0", "FR EDITED 0"));
            if (edited is not null) { Check(false, "the ALLOW shape writes without refusing: " + edited); return; }

            var en = Values(f, Language.English);
            var fr = Values(f, Language.French);
            var enOk = en["ZRefWeap0.Name"] == "EDITED 0" && Enumerable.Range(1, Records - 1)
                .All(i => en[$"ZRefWeap{i}.Name"] == "REF NAME " + i && en[$"ZRefBook{i}.Text"] == "BOOK TEXT " + i);
            // The non-English pin. Without it the multi-language claim rests on a language nothing reads back.
            var frOk = fr["ZRefWeap0.Name"] == "FR EDITED 0" && Enumerable.Range(1, Records - 1)
                .All(i => fr[$"ZRefWeap{i}.Name"] == "FR NAME " + i && fr[$"ZRefBook{i}.Text"] == "FR TEXT " + i);
            Check(enOk, $"every English value survives the in-place write (weap0='{en["ZRefWeap0.Name"]}')");
            Check(frOk, $"every FRENCH value survives the same write (weap0='{fr["ZRefWeap0.Name"]}' book1='{fr["ZRefBook1.Text"]}')");

            var stillAllow = LocalizedStrings.Assess(f.Plugin, f.DataDir);
            Check(stillAllow.Shape == LocalizedShape.LooseComplete && stillAllow.Languages.Count == 2,
                $"the written plugin is still the ALLOW shape with both languages (shape={stillAllow.Shape} langs={stillAllow.Languages.Count})");
            Check(!Directory.Exists(Path.Combine(Path.GetDirectoryName(f.Plugin)!, ".housecarl-tmp")),
                "a successful commit leaves no staging directory behind");
        });
    }

    // ---------------------------------------------------------------- refusals

    static void Refusals()
    {
        Console.WriteLine("== each refused shape refuses in its own words ==");
        Refuse(Variant.LoosePartial, "French", "empty text");
        Refuse(Variant.LooseAndGameData, "two places at once", "Remove or rename");
        Refuse(Variant.GameDataOnly, "not beside it", "Move this plugin's");
        Refuse(Variant.Nowhere, "cannot find its strings", "Mod Organizer merges");

        // The malformed-archive shape is deliberately NOT driven through WriteInPlace. Mutagen parses every archive
        // beside a localized plugin as part of opening it, so an unreadable one takes the OPEN down before any write
        // is reached — measured, not assumed. The refusal that a caller actually meets is the service pre-flight's,
        // which runs before anything is opened, so that is what this arm measures.
        Run(Variant.MalformedBsa, f =>
        {
            var openThrew = false;
            try { _ = SkyrimMod.CreateFromBinary(f.Plugin, SkyrimRelease.SkyrimSE); }
            catch { openThrew = true; }
            var msg = LocalizedStrings.RefusalFor(f.Plugin, "ZRef.esp", f.DataDir);
            var missing = msg is null
                ? new[] { "<no refusal at all>" }
                : new[] { "could not be read", "cannot account for" }.Where(x => !msg.Contains(x)).ToArray();
            Check(openThrew, "an unreadable archive beside a localized plugin takes the plugin's own open down");
            Check(msg is not null && missing.Length == 0,
                $"the pre-flight every in-place lane runs first refuses it in its own words ({(msg is null ? "NO REFUSAL" : missing.Length == 0 ? "all phrases present" : "missing: " + string.Join(" | ", missing))})");
            if (msg is not null && missing.Length > 0) Console.WriteLine("          got: " + msg);
        });
    }

    static void Refuse(Variant v, params string[] phrases)
    {
        Run(v, f =>
        {
            var msg = WriteThrough(f, _ => { });
            var missing = msg is null ? new[] { "<no refusal at all>" } : phrases.Where(p => !msg.Contains(p)).ToArray();
            Check(msg is not null && missing.Length == 0,
                $"{v} refuses naming its own shape ({(msg is null ? "WROTE INSTEAD" : missing.Length == 0 ? "all phrases present" : "missing: " + string.Join(" | ", missing))})");
            if (msg is not null && missing.Length > 0) Console.WriteLine("          got: " + msg);
            // A refusal must leave the file exactly as it was — the whole point of refusing rather than trying.
            if (msg is not null)
                Check(!Directory.Exists(Path.Combine(Path.GetDirectoryName(f.Plugin)!, ".housecarl-tmp")),
                    $"{v}'s refusal stages nothing");
        });
    }

    // ---------------------------------------------------------------- remedies

    /// <summary>Every remedy the refusals name is WALKED here — the literal sequence the sentence tells the caller to
    /// perform, followed by the retry it promises. A refusal that names a remedy nobody measured is the failure mode
    /// this project keeps rediscovering.</summary>
    static void Remedies()
    {
        Console.WriteLine("== the remedy each refusal names actually works ==");

        Run(Variant.GameDataOnly, f =>
        {
            // "Move this plugin's .STRINGS/.DLSTRINGS/.ILSTRINGS out of Data\Strings into a Strings folder beside it."
            var beside = Path.Combine(Path.GetDirectoryName(f.Plugin)!, "Strings");
            Directory.CreateDirectory(beside);
            foreach (var p in Directory.GetFiles(Path.Combine(f.DataDir, "Strings")))
                File.Move(p, Path.Combine(beside, Path.GetFileName(p)));
            AfterRemedy(f, "GameDataOnly");
        });

        Run(Variant.LoosePartial, f =>
        {
            // "Add the missing file(s), or remove that language's remaining files, then retry." — the removal half.
            foreach (var p in LocalizedStrings.OwnTableFiles(f.Plugin).Where(p => Path.GetFileName(p).Contains("French")))
                File.Delete(p);
            AfterRemedy(f, "LoosePartial", expectLanguages: 1);
        });

        Run(Variant.LooseAndGameData, f =>
        {
            // "Remove or rename this plugin's files in Data\Strings, then retry."
            foreach (var p in Directory.GetFiles(Path.Combine(f.DataDir, "Strings"))) File.Delete(p);
            AfterRemedy(f, "LooseWithGameDataDuplicate");
        });
    }

    static void AfterRemedy(Fixture f, string from, int expectLanguages = 2)
    {
        var a = LocalizedStrings.Assess(f.Plugin, f.DataDir);
        Check(a.Shape == LocalizedShape.LooseComplete && a.CanCommitStrings && a.Languages.Count == expectLanguages,
            $"{from}'s stated remedy reaches the ALLOW shape (shape={a.Shape} langs={a.Languages.Count})");
        var msg = WriteThrough(f, w => w.Name = Loc("REMEDIED", "FR REMEDIED"));
        if (msg is not null) { Check(false, $"{from}: the retry the remedy promises still refuses: {msg}"); return; }
        var en = Values(f, Language.English);
        Check(en["ZRefWeap0.Name"] == "REMEDIED" && en["ZRefBook1.Text"] == "BOOK TEXT 1",
            $"{from}: the retry writes and reads back faithfully (weap0='{en["ZRefWeap0.Name"]}')");
    }

    // ---------------------------------------------------------------- crash window

    /// <summary>Fault-inject at each point between the commit's destructive steps. The claim under test is not that
    /// nothing is lost — a crash loses the in-flight write by definition — but that what is LEFT is blank rather than
    /// scrambled, and that the next write refuses rather than destroying the backups.</summary>
    static void Windows()
    {
        Console.WriteLine("== crash window: every interrupted state is blank, never scrambled ==");
        Window(LocalizedTableCommit.StepAfterDelete);
        Window(LocalizedTableCommit.StepAfterPlugin);
        Window(LocalizedTableCommit.StepMidTables);

        // The canary this scripted sweep needs: a cell already proven red by hand. With the manifest suppressed, the
        // recovery gate cannot see the interrupted commit and the next write proceeds over the backups — which is the
        // failure the manifest exists to prevent, so this arm must come back RED-shaped (no refusal) or the sweep is
        // measuring nothing.
        Run(Variant.LooseComplete, f =>
        {
            Interrupt(f, LocalizedTableCommit.StepAfterDelete);
            var manifest = LocalizedTableCommit.PendingCommit(f.Plugin);
            if (manifest is not null) File.Delete(manifest);
            // The refusal does not disappear — an interrupted commit also leaves the plugin in a refusable SHAPE, since
            // its tables are gone. What must disappear is the RECOVERY refusal specifically; if that sentence still
            // came back with no manifest on disk, the arms above would be measuring something other than the manifest.
            var msg = WriteThrough(f, w => w.Name = Loc("AFTER", "FR AFTER"));
            Check(msg is null || !msg.Contains("did not finish"),
                $"CANARY: with the manifest removed the RECOVERY refusal no longer fires ({(msg is null ? "no refusal at all" : "refused as a shape instead")}) — so the arms above measure the manifest, not a refusal that would happen regardless");
        });
    }

    static void Window(string step)
    {
        Run(Variant.LooseComplete, f =>
        {
            var expectedEn = Expected(Language.English);
            Interrupt(f, step);

            var en = Values(f, Language.English);
            // SCRAMBLED means a value that is some OTHER record's text. Blank (null) is the acceptable outcome; so is
            // the record's own text, old or new. Anything else is #373 reproduced.
            var scrambled = en.Where(kv => kv.Value.Length > 0
                                        && kv.Value != expectedEn[kv.Key]
                                        && kv.Value != "EDITED W")
                              .Select(kv => $"{kv.Key}='{kv.Value}'").ToList();
            Check(scrambled.Count == 0,
                $"interrupted {step}: no value reads as another record's text ({en.Count(kv => kv.Value.Length == 0)}/{en.Count} blank)"
                + (scrambled.Count == 0 ? "" : "; SCRAMBLED: " + string.Join(" | ", scrambled.Take(3))));

            // Blank must be LOUD through the product read path, which is the whole reason blank was chosen over
            // scrambled — asserted through ReadEngine, not by eyeballing an empty string.
            var loud = LoudlyUnresolved(f);
            Check(loud is null || loud == true,
                $"interrupted {step}: an unresolved value renders as a no-value NOTE, never a blank token (loud={loud?.ToString() ?? "n/a — nothing unresolved"})");

            var manifest = LocalizedTableCommit.PendingCommit(f.Plugin);
            Check(manifest is not null, $"interrupted {step}: a manifest survives to mark the commit in flight");
            if (manifest is not null)
            {
                var body = File.ReadAllText(manifest);
                Check(body.Contains("backups:") && Directory.Exists(Path.Combine(Path.GetDirectoryName(manifest)!, "backup")),
                    $"interrupted {step}: the manifest names a backup folder that exists");
            }

            var msg = WriteThrough(f, w => w.Name = Loc("AFTER", "FR AFTER"));
            Check(msg is not null && msg.Contains("did not finish") && msg.Contains("BLANK"),
                $"interrupted {step}: the next write REFUSES and points at the recovery"
                + (msg is null ? " — IT WROTE INSTEAD" : ""));
        });
    }

    /// <summary>Drive a real write and abort it inside the commit at <paramref name="step"/>.</summary>
    static void Interrupt(Fixture f, string step)
    {
        LocalizedTableCommit.StepHook = s => { if (s == step) throw new IOException("injected crash at " + s); };
        try { WriteThrough(f, w => w.Name = Loc("EDITED W", "FR EDITED W")); }
        catch (IOException) { /* the injected crash IS this arm's subject; what it left behind is what gets asserted */ }
        finally { LocalizedTableCommit.StepHook = null; }
    }

    /// <summary>Does the product read path render an unresolved localized value as a NOTE rather than a blank token?
    /// Null when the fixture has nothing unresolved to judge.</summary>
    static bool? LoudlyUnresolved(Fixture f)
    {
        var ov = LoadOrderResolver.OpenOverlay(f.Plugin, f.DataDir);
        try
        {
            foreach (var w in ov.Weapons)
            {
                if (w.Name?.String is not null) continue;
                var fv = ReadEngine.ReadFields(w, new[] { "Name" }).Fields[0];
                return !fv.HasValue && fv.Note == ReadEngine.UnresolvedStringNote;
            }
            return null;
        }
        finally { ((IDisposable)ov).Dispose(); }
    }

    // ---------------------------------------------------------------- driving the real write

    /// <summary>Open the plugin the way the in-place lanes do, apply <paramref name="edit"/> to the first weapon, and
    /// drive the real <see cref="WriteEngine.WriteInPlace"/>. Returns null on success, or the refusal's message.</summary>
    static string? WriteThrough(Fixture f, Action<Weapon> edit)
    {
        var mut = SkyrimMod.CreateFromBinary(f.Plugin, SkyrimRelease.SkyrimSE);
        edit(mut.Weapons.First(w => w.EditorID == "ZRefWeap0"));
        var sky = SkyrimMod.CreateFromBinaryOverlay(f.SkyrimEsm, SkyrimRelease.SkyrimSE);
        try { WriteEngine.WriteInPlace(mut, new ISkyrimModGetter[] { sky }, f.Plugin, f.DataDir); return null; }
        catch (LocalizedTargetUnsupportedException ex) { return ex.Message; }
        finally { ((IDisposable)sky).Dispose(); }
    }

    static Dictionary<string, string> Values(Fixture f, Language lang)
    {
        var d = new Dictionary<string, string>();
        var ov = LoadOrderResolver.OpenOverlay(f.Plugin, f.DataDir);
        try
        {
            foreach (var w in ov.Weapons) d[$"{w.EditorID}.Name"] = Pick(w.Name, lang);
            foreach (var b in ov.Books) d[$"{b.EditorID}.Text"] = Pick(b.BookText, lang);
            return d;
        }
        finally { ((IDisposable)ov).Dispose(); }
    }

    static string Pick(ITranslatedStringGetter? s, Language lang)
        => s is null ? "" : s.TryLookup(lang, out var v) ? v : s.String ?? "";

    static Dictionary<string, string> Expected(Language lang)
    {
        var d = new Dictionary<string, string>();
        for (int i = 0; i < Records; i++)
        {
            d[$"ZRefWeap{i}.Name"] = lang == Language.English ? "REF NAME " + i : "FR NAME " + i;
            d[$"ZRefBook{i}.Text"] = lang == Language.English ? "BOOK TEXT " + i : "FR TEXT " + i;
        }
        return d;
    }

    // ---------------------------------------------------------------- fixture

    internal enum Variant { LooseComplete, LoosePartial, LooseAndGameData, GameDataOnly, Nowhere, MalformedBsa, SiblingStem }

    internal sealed record Fixture(string Plugin, string DataDir, string SkyrimEsm);

    static void Run(Variant v, Action<Fixture> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "hc-locwrite-guard-" + Guid.NewGuid().ToString("N"));
        try { body(Build(root, v)); }
        catch (Exception ex) { Check(false, $"{v}: arm THREW {ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            LocalizedTableCommit.StepHook = null;
            try { Directory.Delete(root, true); } catch { }
        }
    }

    static Fixture Build(string root, Variant v)
    {
        var data = Path.Combine(root, "game", "Data");
        var modDir = Path.Combine(root, "mods", "ZRefMod");
        Directory.CreateDirectory(data); Directory.CreateDirectory(modDir);

        var skyrimKey = new ModKey("Skyrim", ModType.Master);
        var skyrimEsm = Path.Combine(data, skyrimKey.FileName.String);
        new SkyrimMod(skyrimKey, SkyrimRelease.SkyrimSE)
            .BeginWrite.ToPath(skyrimEsm).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var plugin = WriteLocalized(modDir, "ZRef", skyrimEsm);
        // A SECOND plugin in the SAME folder, whose name begins with the first's — the shape that made a prefix match
        // claim another plugin's tables on the real order.
        if (v == Variant.SiblingStem) WriteLocalized(modDir, "ZRef_extra", skyrimEsm);

        var own = Path.Combine(modDir, "Strings");
        var gameStrings = Path.Combine(data, "Strings");
        switch (v)
        {
            case Variant.LoosePartial:
                foreach (var k in new[] { "DLSTRINGS", "ILSTRINGS" }) File.Delete(Path.Combine(own, $"ZRef_French.{k}"));
                break;
            case Variant.LooseAndGameData:
                Directory.CreateDirectory(gameStrings);
                foreach (var p in Directory.GetFiles(own)) File.Copy(p, Path.Combine(gameStrings, Path.GetFileName(p)));
                break;
            case Variant.GameDataOnly:
                Directory.CreateDirectory(gameStrings);
                foreach (var p in Directory.GetFiles(own)) File.Move(p, Path.Combine(gameStrings, Path.GetFileName(p)));
                Directory.Delete(own, true);
                break;
            case Variant.Nowhere:
                Directory.Delete(own, true);
                break;
            case Variant.MalformedBsa:
                File.WriteAllBytes(Path.Combine(modDir, "ZRef.bsa"), new byte[] { 0x42, 0x53, 0x41, 0x00 });
                break;
        }
        return new Fixture(plugin, data, skyrimEsm);
    }

    static string WriteLocalized(string modDir, string stem, string skyrimEsm)
    {
        var key = new ModKey(stem, ModType.Plugin);
        var path = Path.Combine(modDir, key.FileName.String);
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE) { UsingLocalization = true };
        for (int i = 0; i < Records; i++)
            m.Weapons.Add(new Weapon(new FormKey(key, (uint)(0xA02 + i)), SkyrimRelease.SkyrimSE)
            {
                EditorID = stem + "Weap" + i,
                Name = Loc("REF NAME " + i, "FR NAME " + i),
                Description = Loc("REF DESC " + i, "FR DESC " + i),
                BasicStats = new WeaponBasicStats { Damage = (ushort)(7 + i) },
            });
        for (int i = 0; i < Records; i++)
            m.Books.Add(new Book(new FormKey(key, (uint)(0xB02 + i)), SkyrimRelease.SkyrimSE)
            {
                EditorID = stem + "Book" + i,
                Name = Loc("BOOK NAME " + i, "FR BOOK " + i),
                BookText = Loc("BOOK TEXT " + i, "FR TEXT " + i),
            });
        // The ILSTRINGS carrier — dialogue response text is the only thing that lands in that table, so without one
        // every claim these arms make about it would be vacuous.
        var topic = new DialogTopic(new FormKey(key, 0xC02), SkyrimRelease.SkyrimSE) { EditorID = stem + "Topic" };
        for (int i = 0; i < Records; i++)
        {
            var info = new DialogResponses(new FormKey(key, (uint)(0xC03 + i)), SkyrimRelease.SkyrimSE);
            info.Responses.Add(new DialogResponse { ResponseNumber = 1, Text = Loc("LINE " + i, "FR LINE " + i) });
            topic.Responses.Add(info);
        }
        m.DialogTopics.Add(topic);
        m.ModHeader.Stats.NextFormID = (uint)(0xC03 + Records);

        var sky = SkyrimMod.CreateFromBinaryOverlay(skyrimEsm, SkyrimRelease.SkyrimSE);
        try { m.BeginWrite.ToPath(path).WithLoadOrder(new ISkyrimModGetter[] { sky }).NoNextFormIDProcessing().Write(); }
        finally { ((IDisposable)sky).Dispose(); }

        if (!Directory.Exists(Path.Combine(modDir, "Strings")))
            throw new InvalidOperationException($"fixture: '{key.FileName}' was written localized but produced no Strings folder.");
        return path;
    }

    static TranslatedString Loc(string en, string fr)
    {
        var ts = new TranslatedString(Language.English, en);
        ts.Set(Language.French, fr);
        return ts;
    }
}

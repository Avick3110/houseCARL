using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;

namespace HousecarlGenerator;

/// <summary>
/// The localized write gate (#368 + #373): that houseCARL refuses to rewrite a localized plugin IN PLACE whatever
/// arrangement its <c>.STRINGS</c> files are in, that each arrangement is named accurately in the refusal, that a
/// refusal leaves the plugin and its tables byte-untouched, and that the one lane which may write a plugin together
/// with a set of tables — houseCARL's OWN output — round-trips every language.
///
/// <para><b>Why there is no crash-window section here.</b> An earlier form of this branch let the complete-loose-set
/// arrangement through and committed emitted tables over the user's live set, with backups, a manifest and a recovery
/// refusal to make that survivable. Review measured that machinery destroying its own recovery set whenever another
/// plugin in the same mod folder was written, and instructing users into the corruption it existed to prevent; it was
/// cut (2026-08-26). Nothing replaces those arms because nothing replaces that write — the refusal below is the
/// behaviour now, and the ratified-refusal arm is what pins it.</para>
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
        Console.WriteLine("################  LOCALIZED WRITE — shape classification, in-place refusal, owned-output round trip  ################");
        Console.WriteLine();
        _fail = 0;
        Shapes();
        Console.WriteLine();
        Refusals();
        Console.WriteLine();
        RefusalLeavesEverythingAlone();
        Console.WriteLine();
        OwnedOutput();
        Console.WriteLine();
        Console.WriteLine(_fail == 0
            ? "[localized-write-guard] PASS — every arrangement refuses in place and in its own words; the owned-output lane round-trips."
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
            Check(a.Shape == LocalizedShape.LooseComplete && a.CanKeepLocalized
                  && a.Languages.Count == 2 && a.Languages.Contains("French"),
                $"complete loose set beside the plugin classifies, and a COMPACTION may keep it (shape={a.Shape} keep={a.CanKeepLocalized} langs=[{string.Join(",", a.Languages)}])");
        });

        Run(Variant.LoosePartial, f =>
        {
            var a = LocalizedStrings.Assess(f.Plugin, f.DataDir);
            var named = a.IncompleteLanguages.TryGetValue("French", out var kinds) && kinds.Count == 2;
            Check(a.Shape == LocalizedShape.LoosePartial && !a.CanKeepLocalized && named,
                $"a language missing table kinds is LoosePartial and the missing kinds are named (shape={a.Shape} missing={string.Join("+", a.IncompleteLanguages.SelectMany(kv => kv.Value))})");
        });

        Run(Variant.LooseAndGameData, f =>
        {
            var a = LocalizedStrings.Assess(f.Plugin, f.DataDir);
            Check(a.Shape == LocalizedShape.LooseWithGameDataDuplicate && !a.CanKeepLocalized && a.GameDataLanguages.Count > 0,
                $"a loose set duplicated in game-Data classifies, and a compaction may NOT keep it (shape={a.Shape} gameData=[{string.Join(",", a.GameDataLanguages)}])");
        });

        Run(Variant.GameDataOnly, f =>
        {
            var a = LocalizedStrings.Assess(f.Plugin, f.DataDir);
            Check(a.Shape == LocalizedShape.GameDataOnly && !a.CanKeepLocalized,
                $"strings resolving from game-Data only classifies (shape={a.Shape})");
        });

        Run(Variant.Nowhere, f =>
        {
            var a = LocalizedStrings.Assess(f.Plugin, f.DataDir);
            Check(a.Shape == LocalizedShape.Nowhere && !a.CanKeepLocalized,
                $"no findable strings source classifies (shape={a.Shape})");
        });

        Run(Variant.MalformedBsa, f =>
        {
            var a = LocalizedStrings.Assess(f.Plugin, f.DataDir);
            Check(a.Shape == LocalizedShape.BsaEmbedded && a.BsaUnreadable && !a.CanKeepLocalized,
                $"an archive that cannot be parsed is refused rather than assumed harmless (shape={a.Shape} unreadable={a.BsaUnreadable})");
        });

        // The defect the real-order sweep found: two plugins share one mod folder's Strings folder, and a stem prefix
        // match alone made the shorter-named one absorb the longer-named one's tables.
        Run(Variant.SiblingStem, f =>
        {
            var mine = LocalizedStrings.Assess(f.Plugin, f.DataDir);
            var sibling = LocalizedStrings.Assess(f.Plugin.Replace("ZRef.esp", "ZRef_extra.esp"), f.DataDir);
            var mineFiles = LocalizedStrings.OwnTableFiles(f.Plugin);
            Check(mine.Languages.Count == 2 && sibling.Languages.Count == 2
                  && mineFiles.All(p => !Path.GetFileName(p).StartsWith("ZRef_extra", StringComparison.OrdinalIgnoreCase)),
                $"a plugin whose name prefixes a sibling's claims only its OWN tables (mine={mine.Languages.Count} sibling={sibling.Languages.Count} files={mineFiles.Count})");
        });

        // dataDir unknown cannot be treated as "checked and found nothing" for the COMPACTION gate: a duplicate it
        // failed to rule out means P′ would carry whichever set this read happened to resolve.
        Run(Variant.LooseComplete, f =>
        {
            var a = LocalizedStrings.Assess(f.Plugin, dataDir: null);
            Check(a.Shape == LocalizedShape.LooseComplete && a.GameDataUnknown && !a.CanKeepLocalized,
                $"an unknown game-Data folder blocks keeping P′ localized rather than passing it (unknown={a.GameDataUnknown} keep={a.CanKeepLocalized})");
        });

        // An archive found in the GAME folder is not "beside the plugin". This is the fallback the vanilla masters and
        // every game-archive plugin take, so the distinction is the whole class, not an edge case.
        Run(Variant.GameDataBsa, f =>
        {
            var a = LocalizedStrings.Assess(f.Plugin, f.DataDir);
            Check(a.Shape == LocalizedShape.BsaEmbedded && a.BsaInGameData,
                $"an archive found in the game folder is recorded as such, not as beside the plugin (shape={a.Shape} inGameData={a.BsaInGameData})");
        });
    }

    // ---------------------------------------------------------------- refusals

    static void Refusals()
    {
        Console.WriteLine("== every arrangement refuses the in-place write, in its own words ==");

        // The RATIFIED refusal: the arrangement houseCARL can classify most confidently, and the one an earlier form of
        // this branch wrote to, now refuses like the rest. Its sentence must still say where the text is — a refusal
        // that only says "no" teaches the modder nothing about their own install.
        Refuse(Variant.LooseComplete, "Strings folder beside it", "English", "does not edit a localized plugin in place");
        Refuse(Variant.LoosePartial, "French has no", "does not edit a localized plugin in place");
        Refuse(Variant.LooseAndGameData, "two places at once", "does not edit a localized plugin in place");
        Refuse(Variant.GameDataOnly, "not beside it", "does not edit a localized plugin in place");
        Refuse(Variant.Nowhere, "cannot find its text", "Mod Organizer merges");

        // No refusal may promise that rearranging the .STRINGS files makes the write work — it never does now, and one
        // of those remedies was measured dead-ending in a second refusal even while the write still existed.
        foreach (var v in new[] { Variant.LooseComplete, Variant.LoosePartial, Variant.GameDataOnly, Variant.LooseAndGameData })
            Run(v, f =>
            {
                var msg = WriteThrough(f, _ => { }) ?? "";
                Check(!msg.Contains("then retry", StringComparison.OrdinalIgnoreCase),
                    $"{v}'s refusal does not tell the caller to rearrange the files and retry");
            });

        // The malformed-archive shape is deliberately NOT driven through WriteInPlace. Mutagen parses every archive
        // beside a localized plugin as part of opening it, so an unreadable one takes the OPEN down before any write
        // is reached — measured, not assumed. The refusal a caller actually meets is the service pre-flight's.
        Run(Variant.MalformedBsa, f =>
        {
            var openThrew = false;
            try { _ = SkyrimMod.CreateFromBinary(f.Plugin, SkyrimRelease.SkyrimSE); }
            catch { openThrew = true; }
            var msg = LocalizedStrings.RefusalFor(f.Plugin, "ZRef.esp", f.DataDir);
            var missing = msg is null
                ? new[] { "<no refusal at all>" }
                : new[] { "could not be read", "does not edit a localized plugin in place" }.Where(x => !msg.Contains(x)).ToArray();
            Check(openThrew, "an unreadable archive beside a localized plugin takes the plugin's own open down");
            Check(msg is not null && missing.Length == 0,
                $"the pre-flight every in-place lane runs first refuses it in its own words ({(msg is null ? "NO REFUSAL" : missing.Length == 0 ? "all phrases present" : "missing: " + string.Join(" | ", missing))})");
            if (msg is not null && missing.Length > 0) Console.WriteLine("          got: " + msg);
        });

        // A Strings folder holding only a NEIGHBOUR's tables reaches the Nowhere shape. The sentence must not tell the
        // modder there is no Strings folder while they are looking straight at one.
        Run(Variant.NeighbourTablesOnly, f =>
        {
            var msg = WriteThrough(f, _ => { }) ?? "";
            Check(!msg.Contains("no Strings folder", StringComparison.OrdinalIgnoreCase)
                  && msg.Contains("no .STRINGS files for this plugin", StringComparison.Ordinal),
                "a folder holding only a neighbour's tables is described as no tables FOR THIS PLUGIN, not as no folder");
            if (msg.Length > 0 && msg.Contains("no Strings folder")) Console.WriteLine("          got: " + msg);
        });

        // With no game-Data folder known, the refusal may not claim it searched one.
        Run(Variant.Nowhere, f =>
        {
            var msg = LocalizedStrings.RefusalFor(f.Plugin, "ZRef.esp", dataDir: null) ?? "";
            Check(!msg.Contains("or in your game folder", StringComparison.Ordinal)
                  && msg.Contains("could not be determined", StringComparison.Ordinal),
                "with no game-Data folder known the refusal says it was not searched, rather than claiming it was");
            if (msg.Contains("or in your game folder")) Console.WriteLine("          got: " + msg);
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
            if (msg is not null)
                Check(!Directory.Exists(Path.Combine(Path.GetDirectoryName(f.Plugin)!, ".housecarl-tmp")),
                    $"{v}'s refusal stages nothing");
        });
    }

    /// <summary>A refusal has to leave the plugin AND its tables exactly as they were — bytes, not just "it still
    /// reads". This is the arm that would catch a refusal which had already destroyed something before deciding.</summary>
    static void RefusalLeavesEverythingAlone()
    {
        Console.WriteLine("== a refusal leaves the plugin and its tables byte-identical ==");
        Run(Variant.LooseComplete, f =>
        {
            var before = Snapshot(f.Plugin);
            var msg = WriteThrough(f, w => w.Name = Loc("EDITED 0", "FR EDITED 0"));
            var after = Snapshot(f.Plugin);
            Check(msg is not null, "the write refuses" + (msg is null ? " — IT WROTE INSTEAD" : ""));
            Check(before.Count == after.Count && before.All(kv => after.TryGetValue(kv.Key, out var h) && h == kv.Value),
                $"every file beside the plugin is byte-identical after the refusal ({before.Count} file(s) compared)");

            var en = Values(f, Language.English);
            var fr = Values(f, Language.French);
            Check(en["ZRefWeap0.Name"] == "REF NAME 0" && fr["ZRefWeap0.Name"] == "FR NAME 0",
                $"and both languages still read their original values (en='{en["ZRefWeap0.Name"]}' fr='{fr["ZRefWeap0.Name"]}')");
        });
    }

    // ---------------------------------------------------------------- owned output

    /// <summary>The ONE lane that may put a plugin and a matching set of tables down together: houseCARL's own output
    /// folder, which is what the compact lane's localized P′ uses. Every language must survive, a re-run must not
    /// leave a previous run's tables behind, and a neighbouring plugin's tables must never be touched.</summary>
    static void OwnedOutput()
    {
        Console.WriteLine("== the owned-output lane: plugin and tables written together ==");

        var root = Path.Combine(Path.GetTempPath(), "hc-locwrite-owned-" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = Path.Combine(root, "game", "Data");
            var outDir = Path.Combine(root, "mods", "ZOut");
            Directory.CreateDirectory(data); Directory.CreateDirectory(outDir);
            var skyrimKey = new ModKey("Skyrim", ModType.Master);
            var skyrimEsm = Path.Combine(data, skyrimKey.FileName.String);
            new SkyrimMod(skyrimKey, SkyrimRelease.SkyrimSE)
                .BeginWrite.ToPath(skyrimEsm).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            var outPath = Path.Combine(outDir, "ZOwned.esp");
            WriteOwned(outPath, skyrimEsm, "OWNED", twoLanguages: true);

            var f = new Fixture(outPath, data, skyrimEsm);
            var en = Values(f, Language.English);
            var fr = Values(f, Language.French);
            Check(en["ZOwnedWeap0.Name"] == "OWNED NAME 0" && en["ZOwnedBook1.Text"] == "OWNED TEXT 1",
                $"English survives the owned-output write (weap0='{en["ZOwnedWeap0.Name"]}')");
            Check(fr["ZOwnedWeap0.Name"] == "FR NAME 0" && fr["ZOwnedBook1.Text"] == "FR TEXT 1",
                $"FRENCH survives the same write (weap0='{fr["ZOwnedWeap0.Name"]}' book1='{fr["ZOwnedBook1.Text"]}')");

            var a = LocalizedStrings.Assess(outPath, data);
            Check(a.Shape == LocalizedShape.LooseComplete && a.Languages.Count == 2,
                $"the output is a complete loose set of its own (shape={a.Shape} langs={a.Languages.Count})");
            Check(!Directory.Exists(Path.Combine(outDir, ".housecarl-tmp")),
                "a successful owned-output commit leaves no staging directory behind");

            // A NEIGHBOUR in the same output folder, with its own tables, must come through untouched — the staging
            // directory is shared by every plugin in a folder, so a commit that globbed it would take these.
            var neighbour = Path.Combine(outDir, "ZNeighbour.esp");
            WriteOwned(neighbour, skyrimEsm, "NEIGH", twoLanguages: false);
            var neighbourBefore = Snapshot(neighbour, "ZNeighbour");

            // Re-run the first plugin: fewer languages this time, so a stale-table bug shows up as a French set that
            // survives a write which no longer carries French.
            WriteOwned(outPath, skyrimEsm, "REDONE", twoLanguages: false);
            var redone = LocalizedStrings.Assess(outPath, data);
            Check(redone.Languages.Count == 1 && redone.Languages[0].Equals("English", StringComparison.OrdinalIgnoreCase),
                $"a re-run replaces the previous run's table set rather than leaving a stale language behind (langs=[{string.Join(",", redone.Languages)}])");
            var enRedone = Values(new Fixture(outPath, data, skyrimEsm), Language.English);
            Check(enRedone["ZOwnedWeap0.Name"] == "REDONE NAME 0",
                $"and the re-run's own values read back (weap0='{enRedone["ZOwnedWeap0.Name"]}')");

            var neighbourAfter = Snapshot(neighbour, "ZNeighbour");
            Check(neighbourBefore.Count > 0 && neighbourBefore.Count == neighbourAfter.Count
                  && neighbourBefore.All(kv => neighbourAfter.TryGetValue(kv.Key, out var h) && h == kv.Value),
                $"a neighbouring plugin's files in the same folder are byte-identical afterwards ({neighbourBefore.Count} file(s) compared)");
        }
        catch (Exception ex) { Check(false, $"owned-output: arm THREW {ex.GetType().Name}: {ex.Message}"); }
        finally { try { Directory.Delete(root, true); } catch { } }

        // The wiring guard, both directions. An in-place write of a localized mod to a path that does not exist yet is
        // the shape whose emitted tables would be silently discarded, so it fails LOUD and names the lane that does it
        // properly; to a path that DOES exist it is the ordinary refusal.
        Run(Variant.LooseComplete, f =>
        {
            var missingPath = Path.Combine(Path.GetDirectoryName(f.Plugin)!, "ZAbsent.esp");
            var mod = new SkyrimMod(new ModKey("ZAbsent", ModType.Plugin), SkyrimRelease.SkyrimSE) { UsingLocalization = true };
            mod.Weapons.Add(new Weapon(new FormKey(mod.ModKey, 0xA02), SkyrimRelease.SkyrimSE)
            { EditorID = "ZAbsentWeap", Name = Loc("A", "FR A") });

            string? wiring = null;
            try { WriteEngine.WriteInPlace(mod, Array.Empty<ISkyrimModGetter>(), missingPath, f.DataDir); }
            catch (InvalidOperationException ex) when (ex is not LocalizedTargetUnsupportedException) { wiring = ex.Message; }
            catch (Exception ex) { wiring = "WRONG EXCEPTION " + ex.GetType().Name; }
            Check(wiring is not null && wiring.Contains("WriteOwnedOutput", StringComparison.Ordinal),
                $"a localized in-place write to a not-yet-existing path fails loud and names the owned-output lane [{wiring}]");
            Check(!File.Exists(missingPath), "and writes nothing at that path");

            var existing = WriteThrough(f, _ => { });
            Check(existing is not null && existing.Contains("does not edit a localized plugin in place", StringComparison.Ordinal),
                "while the same mod at an EXISTING path gets the ordinary refusal, not the wiring error");
        });
    }

    static void WriteOwned(string outPath, string skyrimEsm, string tag, bool twoLanguages)
    {
        var key = new ModKey(Path.GetFileNameWithoutExtension(outPath), ModType.Plugin);
        var stem = key.Name;
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE) { UsingLocalization = true };
        for (int i = 0; i < Records; i++)
            m.Weapons.Add(new Weapon(new FormKey(key, (uint)(0xA02 + i)), SkyrimRelease.SkyrimSE)
            {
                EditorID = stem + "Weap" + i,
                Name = twoLanguages ? Loc(tag + " NAME " + i, "FR NAME " + i) : new TranslatedString(Language.English, tag + " NAME " + i),
            });
        for (int i = 0; i < Records; i++)
            m.Books.Add(new Book(new FormKey(key, (uint)(0xB02 + i)), SkyrimRelease.SkyrimSE)
            {
                EditorID = stem + "Book" + i,
                BookText = twoLanguages ? Loc(tag + " TEXT " + i, "FR TEXT " + i) : new TranslatedString(Language.English, tag + " TEXT " + i),
            });
        m.ModHeader.Stats.NextFormID = (uint)(0xB02 + Records);

        var sky = SkyrimMod.CreateFromBinaryOverlay(skyrimEsm, SkyrimRelease.SkyrimSE);
        try { WriteEngine.WriteOwnedOutput(m, new ISkyrimModGetter[] { sky }, outPath); }
        finally { ((IDisposable)sky).Dispose(); }
    }

    /// <summary>Hash every file beside a plugin that belongs to it (the plugin itself plus its own tables), so an arm
    /// can assert "byte-identical" rather than "still reads".</summary>
    static Dictionary<string, string> Snapshot(string pluginPath, string? stemOverride = null)
    {
        var stem = stemOverride ?? Path.GetFileNameWithoutExtension(pluginPath);
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dir = Path.GetDirectoryName(pluginPath)!;
        if (File.Exists(pluginPath)) d[Path.GetFileName(pluginPath)] = Hash(pluginPath);
        foreach (var p in LocalizedStrings.TableFilesIn(Path.Combine(dir, "Strings"), stem))
            d["Strings/" + Path.GetFileName(p)] = Hash(p);
        return d;
    }

    static string Hash(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
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

    // ---------------------------------------------------------------- fixture

    internal enum Variant
    {
        LooseComplete, LoosePartial, LooseAndGameData, GameDataOnly, Nowhere, MalformedBsa, SiblingStem,
        GameDataBsa, NeighbourTablesOnly,
    }

    internal sealed record Fixture(string Plugin, string DataDir, string SkyrimEsm);

    static void Run(Variant v, Action<Fixture> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "hc-locwrite-guard-" + Guid.NewGuid().ToString("N"));
        try { body(Build(root, v)); }
        catch (Exception ex) { Check(false, $"{v}: arm THREW {ex.GetType().Name}: {ex.Message}"); }
        finally { try { Directory.Delete(root, true); } catch { } }
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
            case Variant.NeighbourTablesOnly:
                // The folder stays, holding only a DIFFERENT plugin's tables — the state whose refusal used to claim
                // there was no Strings folder at all.
                foreach (var p in Directory.GetFiles(own))
                    File.Move(p, Path.Combine(own, Path.GetFileName(p).Replace("ZRef_", "ZOther_")));
                break;
            case Variant.MalformedBsa:
                File.WriteAllBytes(Path.Combine(modDir, "ZRef.bsa"), new byte[] { 0x42, 0x53, 0x41, 0x00 });
                break;
            case Variant.GameDataBsa:
                // Nothing loose anywhere and no archive beside the plugin — the fallback that searches the GAME
                // folder's archives, which is how the vanilla masters' tables are found.
                Directory.Delete(own, true);
                File.WriteAllBytes(Path.Combine(data, "ZGame.bsa"), new byte[] { 0x42, 0x53, 0x41, 0x00 });
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

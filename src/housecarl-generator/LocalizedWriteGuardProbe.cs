using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;

namespace HousecarlGenerator;

/// <summary>
/// The localized write gate (#368 + #373): that houseCARL refuses to rewrite a localized plugin IN PLACE whatever
/// arrangement its <c>.STRINGS</c> files are in, that each arrangement is named accurately in the refusal — both
/// where the text is and which hazard that arrangement actually carries — that a refusal leaves the plugin and its
/// tables byte-untouched, and that a destination houseCARL CANNOT classify refuses rather than being read as
/// not-localized.
///
/// <para><b>What this guard deliberately does not contain, and why.</b> No round-trip pass, no remedy pass, no
/// fault-injected crash window. Two arms were cut from the branch this guards (2026-08-26): the in-place write of a
/// complete-loose-set localized plugin, whose recovery machinery review measured destroying its own recovery set and
/// instructing users into the corruption it existed to prevent; and Q2-A, which kept a compacted P′ localized with a
/// rewritten table set. There is no accepted in-place write left to round-trip and no commit window to fault-inject.
/// The refusal is the behaviour, and the arms below are what pin it.</para>
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
        Console.WriteLine("################  LOCALIZED WRITE — shape classification, in-place refusal, unclassifiable destinations  ################");
        Console.WriteLine();
        _fail = 0;
        Shapes();
        Console.WriteLine();
        Refusals();
        Console.WriteLine();
        RefusalLeavesEverythingAlone();
        Console.WriteLine();
        UndecidableDestination();
        Console.WriteLine();
        UnlistableStringsFolder();
        Console.WriteLine();
        Console.WriteLine(_fail == 0
            ? "[localized-write-guard] PASS — every arrangement refuses in place and in its own words, and a destination houseCARL cannot classify refuses too."
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
            Check(a.Shape == LocalizedShape.LooseComplete
                  && a.Languages.Count == 2 && a.Languages.Contains("French"),
                $"complete loose set beside the plugin classifies, both languages seen (shape={a.Shape} langs=[{string.Join(",", a.Languages)}])");
        });

        Run(Variant.LoosePartial, f =>
        {
            var a = LocalizedStrings.Assess(f.Plugin, f.DataDir);
            var named = a.IncompleteLanguages.TryGetValue("French", out var kinds) && kinds.Count == 2;
            Check(a.Shape == LocalizedShape.LoosePartial && named,
                $"a language missing table kinds is LoosePartial and the missing kinds are named (shape={a.Shape} missing={string.Join("+", a.IncompleteLanguages.SelectMany(kv => kv.Value))})");
        });

        Run(Variant.LooseAndGameData, f =>
        {
            var a = LocalizedStrings.Assess(f.Plugin, f.DataDir);
            Check(a.Shape == LocalizedShape.LooseWithGameDataDuplicate && a.GameDataLanguages.Count > 0,
                $"a loose set duplicated in game-Data classifies as the duplicate shape (shape={a.Shape} gameData=[{string.Join(",", a.GameDataLanguages)}])");
        });

        Run(Variant.GameDataOnly, f =>
        {
            var a = LocalizedStrings.Assess(f.Plugin, f.DataDir);
            Check(a.Shape == LocalizedShape.GameDataOnly,
                $"strings resolving from game-Data only classifies (shape={a.Shape})");
        });

        Run(Variant.Nowhere, f =>
        {
            var a = LocalizedStrings.Assess(f.Plugin, f.DataDir);
            Check(a.Shape == LocalizedShape.Nowhere,
                $"no findable strings source classifies (shape={a.Shape})");
        });

        Run(Variant.MalformedBsa, f =>
        {
            var a = LocalizedStrings.Assess(f.Plugin, f.DataDir);
            Check(a.Shape == LocalizedShape.BsaEmbedded && a.BsaUnreadable,
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

        // dataDir unknown is recorded as UNKNOWN rather than collapsed into "checked and found nothing" — the
        // distinction the Nowhere sentence reads to decide whether it may claim it searched the game folder.
        Run(Variant.LooseComplete, f =>
        {
            var a = LocalizedStrings.Assess(f.Plugin, dataDir: null);
            Check(a.Shape == LocalizedShape.LooseComplete && a.GameDataUnknown,
                $"an unknown game-Data folder is recorded as unknown, not as checked (unknown={a.GameDataUnknown})");
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
        //
        // The HAZARD clause is checked per shape, not just the shared outcome. It used to be one sentence applied
        // verbatim everywhere: "an interruption would leave records reading text that belongs to other records" — exact
        // where a live table set beside the plugin would be replaced, and false where a write would replace no table at
        // all. Where the text is in game-Data or inside an archive, a plugin write cannot reach it; what it would
        // actually do is SHADOW it and leave it stale. These arms pin which hazard each shape is told about, so the
        // wording cannot silently collapse back into one.
        Refuse(Variant.LooseComplete, "Strings folder beside it", "English",
               "would have to be replaced in the same breath", "does not edit a localized plugin in place");
        Refuse(Variant.LoosePartial, "French has no",
               "would have to be replaced in the same breath", "does not edit a localized plugin in place");
        Refuse(Variant.LooseAndGameData, "two places at once",
               "would have to be replaced in the same breath", "does not edit a localized plugin in place");
        Refuse(Variant.GameDataOnly, "not beside it",
               "does not reach your game's Data folder", "SHADOW the set in Data\\Strings rather than replace it",
               "does not edit a localized plugin in place");
        Refuse(Variant.Nowhere, "cannot find its text", "Mod Organizer merges",
               "cannot see the files its indices point at", "does not edit a localized plugin in place");

        // The shadow shapes must NOT be told about a mid-write scramble — the hazard that cannot happen there. This is
        // the arm that fails if the shared sentence comes back.
        foreach (var v in new[] { Variant.GameDataOnly, Variant.MalformedBsa, Variant.Nowhere })
            Run(v, f =>
            {
                var msg = LocalizedStrings.RefusalFor(f.Plugin, "ZRef.esp", f.DataDir) ?? "";
                Check(msg.Length > 0 && !msg.Contains("would have to be replaced in the same breath", StringComparison.Ordinal)
                      && !msg.Contains("text that belongs to other records", StringComparison.Ordinal),
                    $"{v}'s refusal does not describe replacing a table set it would never replace");
                if (msg.Contains("text that belongs to other records")) Console.WriteLine("          got: " + msg);
            });

        // …and the loose shapes MUST be, so the arm above cannot pass by the clause being gone everywhere.
        foreach (var v in new[] { Variant.LooseComplete, Variant.LoosePartial, Variant.LooseAndGameData })
            Run(v, f =>
            {
                var msg = LocalizedStrings.RefusalFor(f.Plugin, "ZRef.esp", f.DataDir) ?? "";
                Check(msg.Contains("text that belongs to other records", StringComparison.Ordinal),
                    $"{v}'s refusal DOES name the scramble, which is the real hazard for a set beside the plugin");
            });

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

        // THE NOWHERE SENTENCE'S ABSENCE CLAIM — the class signal in its purest form, so both of its recorded
        // falsehoods get a fixture and the shape they share gets one too.
        //
        //   1st: "no Strings folder beside it", while the folder was there holding a NEIGHBOUR's tables.
        //   2nd, after that was fixed: "no .STRINGS files for this plugin beside it", while ZRef_ptbr.STRINGS sat in
        //        that folder — houseCARL had not MATCHED it (ptbr is not a language Mutagen models), which is a
        //        different claim from it not being there.
        //
        // The sentence now asserts no absence at all when the folder holds files: it says how many are there, names
        // them, and claims only that none matched. Both fixtures below reach the same wording, and the third arm is
        // the other direction — a folder that really is gone, where "no .STRINGS files beside it" is true.
        foreach (var v in new[] { Variant.NeighbourTablesOnly, Variant.UnknownLanguageToken })
            Run(v, f =>
            {
                var msg = WriteThrough(f, _ => { }) ?? "";
                var files = LocalizedStrings.Assess(f.Plugin, f.DataDir).UnmatchedTables;
                Check(!msg.Contains("no Strings folder", StringComparison.OrdinalIgnoreCase)
                      && !msg.Contains("no .STRINGS files for this plugin beside it", StringComparison.Ordinal)
                      && !msg.Contains("no .STRINGS files beside it", StringComparison.Ordinal)
                      && msg.Contains("matched none of them to this plugin", StringComparison.Ordinal)
                      && files.Total > 0 && files.Names.All(n => msg.Contains(n, StringComparison.Ordinal)),
                    $"{v}: the refusal describes the folder the modder is looking at and names its {files.Total} file(s), "
                    + "rather than asserting an absence it did not check");
                if (msg.Length > 0 && !msg.Contains("matched none of them")) Console.WriteLine("          got: " + msg);

                // UNDER the cap: every file is named, so there is nothing left over to announce. This is the direction
                // that stops "and N more" being appended unconditionally.
                Check(files.Total <= UnmatchedTableFiles.Cap && files.Names.Count == files.Total
                      && !msg.Contains(" more —", StringComparison.Ordinal),
                    $"{v}: with {files.Total} file(s), under the cap of {UnmatchedTableFiles.Cap}, all of them are named and nothing is counted off");
            });

        // OVER THE CAP. `UnmatchedTablesIn` quotes at most eight names; the sentence used to render that list's LENGTH
        // as what the folder holds, so a folder of thirty was described as holding eight and the list stopped with no
        // ellipsis. The count and the names now travel together (UnmatchedTableFiles), and this is the fixture that
        // can tell them apart — the arms above cannot, because both their folders sit under the cap.
        Run(Variant.ManyNeighbourTables, f =>
        {
            var msg = WriteThrough(f, _ => { }) ?? "";
            var files = LocalizedStrings.Assess(f.Plugin, f.DataDir).UnmatchedTables;
            Check(files.Total > UnmatchedTableFiles.Cap && files.Names.Count == UnmatchedTableFiles.Cap,
                $"fixture: the folder holds {files.Total} unmatched table file(s), more than the {UnmatchedTableFiles.Cap} a refusal quotes");
            Check(msg.Contains($"holds {files.Total} .STRINGS file(s)", StringComparison.Ordinal),
                $"the refusal reports the folder's TRUE count, not the capped list's length ({files.Total} of them)");
            Check(msg.Contains($", and {files.Unnamed} more", StringComparison.Ordinal),
                $"…and says the list stopped, naming how many it did not quote ({files.Unnamed})");
            Check(files.Names.All(n => msg.Contains(n, StringComparison.Ordinal)),
                $"…and every name it does quote is one that is actually there ({files.Names.Count} named)");
            if (!msg.Contains($"holds {files.Total} .STRINGS file(s)") || !msg.Contains($", and {files.Unnamed} more"))
                Console.WriteLine("          got: " + msg);
        });

        Run(Variant.Nowhere, f =>
        {
            var msg = WriteThrough(f, _ => { }) ?? "";
            Check(msg.Contains("no .STRINGS files beside it", StringComparison.Ordinal)
                  && !msg.Contains("matched none of them", StringComparison.Ordinal),
                "with the folder genuinely gone, the refusal DOES say there are no .STRINGS files beside it");
            if (msg.Contains("matched none of them")) Console.WriteLine("          got: " + msg);
        });

        // TWO LOCATIONS. An archive decides the shape whatever else is on disk — but the assessment is still carrying
        // the loose set beside the plugin, and a sentence naming one location while two sit there sends the modder to
        // look in the wrong place. Fixtured on the UNREADABLE archive, which is the archive shape a guard can build:
        // arming the readable one needs a .bsa that parses and Mutagen exposes no in-process archive builder.
        Run(Variant.MalformedBsa, f =>
        {
            var a = LocalizedStrings.Assess(f.Plugin, f.DataDir);
            var msg = LocalizedStrings.RefusalFor(f.Plugin, "ZRef.esp", f.DataDir) ?? "";
            Check(a.Shape == LocalizedShape.BsaEmbedded && a.Languages.Count == 2,
                $"fixture: an archive beside a plugin that ALSO has a loose set classifies as the archive shape, carrying the loose languages (shape={a.Shape} langs={a.Languages.Count})");
            Check(msg.Contains("ZRef.bsa", StringComparison.Ordinal)
                  && msg.Contains("and there is also", StringComparison.Ordinal)
                  && msg.Contains("English", StringComparison.Ordinal) && msg.Contains("French", StringComparison.Ordinal),
                $"the archive refusal names the loose set beside the plugin too, not only the archive");
            if (!msg.Contains("and there is also")) Console.WriteLine("          got: " + msg);
        });

        // The other direction: with no loose set there, the sentence must not invent a second location. GameDataBsa is
        // the fixture whose loose set IS gone.
        Run(Variant.GameDataBsa, f =>
        {
            var msg = LocalizedStrings.RefusalFor(f.Plugin, "ZRef.esp", f.DataDir) ?? "";
            Check(msg.Contains("ZGame.bsa", StringComparison.Ordinal)
                  && !msg.Contains("and there is also", StringComparison.Ordinal),
                "an archive with nothing loose beside it names one location, because there is one");
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

    // ------------------------------------------------- a destination houseCARL cannot classify

    /// <summary>The FAIL-OPEN arms, both directions. The refusal is decided off the mod already in memory
    /// (<c>targetMod.UsingLocalization</c>), which cannot fail to be read; the shape lookup that re-opens the file on
    /// disk supplies only the WORDS.
    ///
    /// <para>An earlier form made that re-read the DECISION, and its fault path answered "not localized": with the
    /// destination held by a second process for the instant of the read, the write proceeded, replaced the plugin, and
    /// every value read back empty. These arms drive exactly that state — the file locked <c>FileShare.None</c> while
    /// the write runs — and require a refusal. The unlocked cell beside each is the other direction, so an arm cannot
    /// pass by refusing everything for an unrelated reason.</para></summary>
    static void UndecidableDestination()
    {
        Console.WriteLine("== a destination houseCARL cannot classify REFUSES, rather than being read as not-localized ==");

        // (1) LOCKED. The classifier's own answer first — this is the value that used to be "not localized" — then the
        //     write driven against the same locked file.
        Run(Variant.LooseComplete, f =>
        {
            var unlocked = LocalizedStrings.Assess(f.Plugin, f.DataDir);
            var before = Snapshot(f.Plugin);

            LocalizedShape lockedShape;
            string? lockedRefusal;
            string? writeRefusal;
            using (var hold = new FileStream(f.Plugin, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                lockedShape = LocalizedStrings.Assess(f.Plugin, f.DataDir).Shape;
                // The SERVICE pre-flights' shared decision point, which holds no mod in memory and must fail CLOSED.
                lockedRefusal = LocalizedStrings.RefusalFor(f.Plugin, "ZRef.esp", f.DataDir);
                writeRefusal = WriteThroughHeld(f);
            }

            Check(unlocked.Shape == LocalizedShape.LooseComplete && lockedShape == LocalizedShape.Unreadable,
                $"a locked destination classifies as Unreadable, NOT as not-localized (unlocked={unlocked.Shape} locked={lockedShape})");
            Check(lockedRefusal is not null && lockedRefusal.Contains("could not read the file at that path", StringComparison.Ordinal),
                $"the pre-flight every service lane shares refuses a locked plugin, saying it could not read it [{lockedRefusal ?? "<PROCEEDED>"}]");
            Check(writeRefusal is not null,
                "the in-place write of a localized mod at a LOCKED destination refuses"
                + (writeRefusal is null ? " — IT WROTE INSTEAD" : ""));

            var after = Snapshot(f.Plugin);
            Check(before.Count == after.Count && before.All(kv => after.TryGetValue(kv.Key, out var h) && h == kv.Value),
                $"and the plugin and its tables are byte-identical afterwards ({before.Count} file(s) compared)");
            Check(!Directory.Exists(Path.Combine(Path.GetDirectoryName(f.Plugin)!, ".housecarl-tmp")),
                "and nothing was staged — no orphaned .housecarl-tmp holding emitted tables");
        });

        // (2) The OTHER direction of the pre-flight, so its refusal is not simply unconditional: a readable plugin
        //     with the flag clear returns null and the lane proceeds.
        Run(Variant.LooseComplete, f =>
        {
            var plain = Path.Combine(Path.GetDirectoryName(f.Plugin)!, "ZPlain.esp");
            WritePlain(plain, f.SkyrimEsm);
            var a = LocalizedStrings.Assess(plain, f.DataDir);
            Check(a.Shape == LocalizedShape.NotLocalized
                  && LocalizedStrings.RefusalFor(plain, "ZPlain.esp", f.DataDir) is null,
                $"a readable NON-localized plugin still returns no refusal, so the pre-flight is not refusing everything (shape={a.Shape})");
        });

        // (3) ABSENT. A localized mod bound for a path with no file at it cannot be classified either, and its emitted
        //     tables would be discarded by an in-place commit — so it refuses rather than writing a plugin whose
        //     indices resolve against nothing.
        Run(Variant.LooseComplete, f =>
        {
            var missing = Path.Combine(Path.GetDirectoryName(f.Plugin)!, "ZAbsent.esp");
            var msg = WriteLocalizedModTo(missing, "ZAbsent", f.DataDir, f.SkyrimEsm);
            Check(msg is not null && msg.Contains("could not read the file at that path", StringComparison.Ordinal),
                $"a localized in-place write to a path with no file at it refuses, saying so [{msg ?? "<WROTE INSTEAD>"}]");
            Check(!File.Exists(missing), "and writes nothing at that path");
        });

        // (4) READABLE BUT NOT LOCALIZED ON DISK. The mod in hand is flagged localized and the file at the path is
        //     not, so houseCARL cannot say where the text being written would resolve from — and says THAT, rather
        //     than describing an arrangement it did not find.
        Run(Variant.LooseComplete, f =>
        {
            var plain = Path.Combine(Path.GetDirectoryName(f.Plugin)!, "ZPlain.esp");
            WritePlain(plain, f.SkyrimEsm);
            var beforeLen = new FileInfo(plain).Length;
            var msg = WriteLocalizedModTo(plain, "ZPlain", f.DataDir, f.SkyrimEsm);
            Check(msg is not null && msg.Contains("does not read as a localized plugin", StringComparison.Ordinal),
                $"a localized mod written over a NON-localized file refuses, naming that mismatch [{msg ?? "<WROTE INSTEAD>"}]");
            Check(new FileInfo(plain).Length == beforeLen, "and the file at that path is untouched");
        });

        UnreadableSaysNothingAboutLocalization();
    }

    /// <summary>The vocabulary a file houseCARL never opened may NOT be described in — Aaron's review of PR #436, and
    /// the class the whole render seam answers.
    ///
    /// <para>The fail-closed DECISION is right and stays: anything that is not <c>NotLocalized</c> refuses. What was
    /// wrong is that the same collapsed boolean chose the WORDS, so an unreadable destination inherited a localized
    /// plugin's sentences — "is flagged LOCALIZED", "its text lives in separate .STRINGS files", "It does not edit a
    /// localized plugin in place" — asserted about a file that was never read. Both directions are armed, because an
    /// arm that only checked the vocabulary was absent would pass just as well with it absent everywhere.</para></summary>
    static void UnreadableSaysNothingAboutLocalization()
    {
        Console.WriteLine();
        Console.WriteLine("== a file houseCARL never opened is not described in a localized plugin's words ==");

        // The vocabulary that asserts a localization state. Every one of these is a claim about the file.
        var localizedVocabulary = new[]
        {
            "flagged LOCALIZED",
            "A localized plugin's text is not in the plugin",
            "does not edit a localized plugin in place",
            "its text lives in separate .STRINGS files",
        };

        Run(Variant.LooseComplete, f =>
        {
            string? unreadable, unreadableRemove;
            using (var hold = new FileStream(f.Plugin, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                unreadable = LocalizedStrings.RefusalFor(f.Plugin, "ZRef.esp", f.DataDir);
                // The lane whose clause turns a momentary lock into a permanent dead end if it is appended here.
                unreadableRemove = LocalizedStrings.RefusalFor(f.Plugin, "ZRef.esp", f.DataDir,
                                                               LocalizedTargetUnsupportedException.RemoveNoEquivalent);
            }
            var localized = LocalizedStrings.RefusalFor(f.Plugin, "ZRef.esp", f.DataDir,
                                                        LocalizedTargetUnsupportedException.RemoveNoEquivalent);

            var leaked = localizedVocabulary.Where(v => unreadable?.Contains(v, StringComparison.Ordinal) ?? false).ToArray();
            Check(unreadable is not null && leaked.Length == 0,
                $"an unreadable destination's refusal claims no localization state ({(unreadable is null ? "NO REFUSAL" : leaked.Length == 0 ? "none of the vocabulary present" : "LEAKED: " + string.Join(" | ", leaked))})");
            if (leaked.Length > 0) Console.WriteLine("          got: " + unreadable);

            Check(unreadable?.Contains("does not write to a destination it cannot classify", StringComparison.Ordinal) ?? false,
                $"…and says what it actually is — a destination houseCARL cannot classify [{unreadable}]");

            // The OTHER direction, on the same fixture one lock apart: readable and localized, the vocabulary is
            // exactly what the caller must get. Without this the arm above passes on a refusal that says nothing
            // anywhere.
            var present = localizedVocabulary.Where(v => localized?.Contains(v, StringComparison.Ordinal) ?? false).ToArray();
            Check(localized is not null && present.Length > 0,
                $"the SAME plugin unlocked and localized IS described in that vocabulary ({present.Length}/{localizedVocabulary.Length} phrases)");

            // The remove lane's clause: absent for the unreadable target (it names a permanent dead end), present for
            // the localized one (where it is the truth), and replaced by the remedy that matches what failed.
            Check(!(unreadableRemove?.Contains("no way to remove this record", StringComparison.Ordinal) ?? true),
                $"the remove lane's dead-end clause is NOT appended to a target that was never read [{unreadableRemove}]");
            Check(localized?.Contains("no way to remove this record", StringComparison.Ordinal) ?? false,
                $"…and IS appended to a target that really is localized [{localized}]");
            Check(unreadableRemove?.Contains("has the file open", StringComparison.Ordinal) ?? false,
                $"the unreadable target gets the remedy for what actually failed — check what holds the file, and retry [{unreadableRemove}]");
        });

        // THE SEAM ITSELF, walked over the enum rather than over the fixtures: every shape renders a body, and only
        // the shapes whose LOCALIZED flag was actually READ may render the localized vocabulary. A shape added later
        // that inherits another's words fails here, which is what makes the seam hold past this session.
        foreach (var shape in Enum.GetValues<LocalizedShape>())
        {
            var body = LocalizedTargetUnsupportedException.ShapeBody(Synthetic(shape));
            var carries = localizedVocabulary.Any(v => body.Contains(v, StringComparison.Ordinal));
            var mayCarry = MayAssertLocalization(shape);
            Check(body.Length > 0 && carries == mayCarry,
                $"{shape}: renders a body, and {(mayCarry ? "carries" : "carries NO")} localized vocabulary (carries={carries})");
            if (carries != mayCarry) Console.WriteLine("          got: " + body);
        }
    }

    /// <summary>May this shape's refusal assert that a plugin is localized? An exhaustive switch on purpose: a shape
    /// added later has to be answered here, which is the walk's whole value.</summary>
    static bool MayAssertLocalization(LocalizedShape shape) => shape switch
    {
        // The FILE's own header was read and the flag was set.
        LocalizedShape.LooseComplete or LocalizedShape.LoosePartial or LocalizedShape.LooseWithGameDataDuplicate
            or LocalizedShape.BsaEmbedded or LocalizedShape.GameDataOnly or LocalizedShape.Nowhere => true,

        // The file read fine with the flag CLEAR — and this arm is reached only from the write's choke point, where
        // the MOD in hand is localized and that fact came out of memory. So the sentence may say so; what it may not
        // do is describe an arrangement, and it does not.
        LocalizedShape.NotLocalized => true,

        // The PLUGIN's flag was read and is set; only its Strings folder could not be listed. So it may be called
        // localized — what it may not do is describe what is in that folder.
        LocalizedShape.StringsFolderUnreadable => true,

        // Nothing was read. Nothing may be asserted.
        LocalizedShape.Unreadable => false,

        _ => false,
    };

    // ------------------------------------------------- a Strings folder houseCARL cannot list

    /// <summary>The FOLDER's third answer (Aaron's review, finding 3). A <c>Strings\</c> folder that is there and
    /// cannot be enumerated is not an empty one, and the classifier used to treat them the same: the enumeration
    /// swallowed <c>UnauthorizedAccessException</c>, returned nothing, and a plugin with a complete loose set sitting
    /// right there classified as <see cref="LocalizedShape.Nowhere"/> — whose sentence then told the modder there were
    /// "no .STRINGS files beside it". The same unchecked-absence falsehood that arm was rewritten twice to remove,
    /// arriving through the exception path rather than the matching path.
    ///
    /// <para>Fixtured with a real deny ACE, and the deny is VERIFIED to bite before anything is asserted — a fixture
    /// that silently did not take would make every arm below pass for the wrong reason. If it cannot be built on this
    /// host the cell FAILS rather than skipping (Q3).</para></summary>
    static void UnlistableStringsFolder()
    {
        Console.WriteLine();
        Console.WriteLine("== a Strings folder houseCARL cannot LIST is not a Strings folder it found empty ==");

        Run(Variant.LooseComplete, f =>
        {
            var strings = Path.Combine(Path.GetDirectoryName(f.Plugin)!, "Strings");
            var listed = LocalizedStrings.Assess(f.Plugin, f.DataDir);
            Check(listed.Shape == LocalizedShape.LooseComplete && listed.Languages.Count == 2,
                $"fixture: listable, the folder holds a complete loose set in two languages (shape={listed.Shape})");

            if (!TryDenyListing(strings))
            {
                Check(false, "fixture: the deny-listing ACE did not take on this host — the cell cannot be built, so it FAILS rather than passing");
                return;
            }
            try
            {
                var a = LocalizedStrings.Assess(f.Plugin, f.DataDir);
                Check(a.Shape == LocalizedShape.StringsFolderUnreadable,
                    $"an unlistable folder classifies as its own shape, NOT as Nowhere (shape={a.Shape})");

                var msg = LocalizedStrings.RefusalFor(f.Plugin, "ZRef.esp", f.DataDir) ?? "";
                Check(!msg.Contains("no .STRINGS files beside it", StringComparison.Ordinal)
                      && !msg.Contains("cannot find its text", StringComparison.Ordinal),
                    $"…and its refusal asserts no absence over a folder houseCARL could not read");
                Check(msg.Contains("could not read the Strings folder beside it", StringComparison.Ordinal),
                    $"…and says what actually happened — the folder is there and could not be read [{msg}]");
                if (msg.Contains("no .STRINGS files beside it")) Console.WriteLine("          got: " + msg);
            }
            finally { UndenyListing(strings); }

            // The other direction, on the same folder once the deny is lifted: back to the shape it really is, so the
            // arms above cannot pass by the classifier answering StringsFolderUnreadable for everything.
            var after = LocalizedStrings.Assess(f.Plugin, f.DataDir);
            Check(after.Shape == LocalizedShape.LooseComplete,
                $"with the deny lifted the same folder classifies as the loose set it is (shape={after.Shape})");
        });

        // And the genuinely-absent folder keeps the absence claim it is entitled to — the arm that stops the fix from
        // being "never say there are none".
        Run(Variant.Nowhere, f =>
        {
            var a = LocalizedStrings.Assess(f.Plugin, f.DataDir);
            var msg = LocalizedStrings.RefusalFor(f.Plugin, "ZRef.esp", f.DataDir) ?? "";
            Check(a.Shape == LocalizedShape.Nowhere && msg.Contains("no .STRINGS files beside it", StringComparison.Ordinal),
                $"a folder that really is gone still gets the checked absence (shape={a.Shape})");
        });
    }

    /// <summary>Deny LISTING on one directory for the current user, and say whether it actually bit. Deliberately not
    /// a deny-all: <c>Directory.Exists</c> reads the path's attributes, and denying that too would make the folder
    /// look ABSENT rather than unlistable — which is the wrong state to measure.</summary>
    static bool TryDenyListing(string dir)
    {
        try
        {
            var me = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            var di = new DirectoryInfo(dir);
            var sec = di.GetAccessControl();
            sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                me, System.Security.AccessControl.FileSystemRights.ListDirectory,
                System.Security.AccessControl.AccessControlType.Deny));
            di.SetAccessControl(sec);
            // Verified, not trusted: the folder must still LOOK present and must refuse to be enumerated.
            if (!Directory.Exists(dir)) { UndenyListing(dir); return false; }
            try { Directory.EnumerateFiles(dir).ToList(); }
            catch (UnauthorizedAccessException) { return true; }
            catch (IOException) { return true; }
            UndenyListing(dir);
            return false;
        }
        catch { return false; }
    }

    /// <summary>Lift the deny again — always in a finally, or the temp tree cannot be cleaned up afterwards.</summary>
    static void UndenyListing(string dir)
    {
        try
        {
            var me = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            var di = new DirectoryInfo(dir);
            var sec = di.GetAccessControl();
            sec.RemoveAccessRuleAll(new System.Security.AccessControl.FileSystemAccessRule(
                me, System.Security.AccessControl.FileSystemRights.ListDirectory,
                System.Security.AccessControl.AccessControlType.Deny));
            di.SetAccessControl(sec);
        }
        catch { /* best effort; Run's recursive delete is the backstop */ }
    }

    /// <summary>A bare assessment in one shape, for the enum walk above. Synthetic on purpose: the walk is about the
    /// RENDER's arms, and a fixture per shape would only re-measure the classifier the Shapes() arms already pin.</summary>
    static LocalizedAssessment Synthetic(LocalizedShape shape)
        => new(shape, Array.Empty<string>(), new Dictionary<string, IReadOnlyList<string>>(),
               Array.Empty<string>(), shape == LocalizedShape.BsaEmbedded ? "Z.bsa" : null, false, false);

    /// <summary>Drive the real <see cref="WriteEngine.WriteInPlace"/> with a freshly built LOCALIZED mod aimed at
    /// <paramref name="outPath"/> — for the destinations the fixture's own plugin cannot express (absent, or present
    /// but not localized). Returns the refusal, or null if it wrote.</summary>
    static string? WriteLocalizedModTo(string outPath, string stem, string dataDir, string skyrimEsm)
    {
        var key = new ModKey(stem, ModType.Plugin);
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE) { UsingLocalization = true };
        mod.Weapons.Add(new Weapon(new FormKey(key, 0xA02), SkyrimRelease.SkyrimSE)
        { EditorID = stem + "Weap", Name = Loc("A", "FR A") });
        var sky = SkyrimMod.CreateFromBinaryOverlay(skyrimEsm, SkyrimRelease.SkyrimSE);
        try { WriteEngine.WriteInPlace(mod, new ISkyrimModGetter[] { sky }, outPath, dataDir); return null; }
        catch (LocalizedTargetUnsupportedException ex) { return ex.Message; }
        finally { ((IDisposable)sky).Dispose(); }
    }

    /// <summary>A plain NON-localized plugin at <paramref name="path"/>.</summary>
    static void WritePlain(string path, string skyrimEsm)
    {
        var key = new ModKey(Path.GetFileNameWithoutExtension(path), ModType.Plugin);
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        m.Weapons.Add(new Weapon(new FormKey(key, 0xA02), SkyrimRelease.SkyrimSE)
        { EditorID = key.Name + "Weap", Name = "PLAIN" });
        var sky = SkyrimMod.CreateFromBinaryOverlay(skyrimEsm, SkyrimRelease.SkyrimSE);
        try { m.BeginWrite.ToPath(path).WithLoadOrder(new ISkyrimModGetter[] { sky }).Write(); }
        finally { ((IDisposable)sky).Dispose(); }
    }

    /// <summary>Like <see cref="WriteThrough"/>, but the mod is opened BEFORE the caller takes its lock — the write
    /// itself then runs against a destination it cannot re-open, which is the state the fail-open let through.</summary>
    static string? WriteThroughHeld(Fixture f)
    {
        var key = new ModKey(Path.GetFileNameWithoutExtension(f.Plugin), ModType.Plugin);
        var mut = new SkyrimMod(key, SkyrimRelease.SkyrimSE) { UsingLocalization = true };
        mut.Weapons.Add(new Weapon(new FormKey(key, 0xA02), SkyrimRelease.SkyrimSE)
        { EditorID = "ZRefWeap0", Name = Loc("EDITED 0", "FR EDITED 0") });
        var sky = SkyrimMod.CreateFromBinaryOverlay(f.SkyrimEsm, SkyrimRelease.SkyrimSE);
        try { WriteEngine.WriteInPlace(mut, new ISkyrimModGetter[] { sky }, f.Plugin, f.DataDir); return null; }
        catch (LocalizedTargetUnsupportedException ex) { return ex.Message; }
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
        GameDataBsa, NeighbourTablesOnly, UnknownLanguageToken, ManyNeighbourTables,
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
            case Variant.UnknownLanguageToken:
                // THIS plugin's own tables, named for a language token Mutagen does not model — the state whose
                // refusal claimed there were "no .STRINGS files for this plugin beside it" while they sat right there.
                foreach (var q in Directory.GetFiles(own))
                    File.Move(q, Path.Combine(own, Path.GetFileName(q).Replace("_English", "_ptbr").Replace("_French", "_ptpt")));
                break;
            case Variant.NeighbourTablesOnly:
                // The folder stays, holding only a DIFFERENT plugin's tables — the state whose refusal used to claim
                // there was no Strings folder at all. SIX files, deliberately under the naming cap, so it is the
                // "every name is quoted" direction of the count arm.
                foreach (var p in Directory.GetFiles(own))
                    File.Move(p, Path.Combine(own, Path.GetFileName(p).Replace("ZRef_", "ZOther_")));
                break;
            case Variant.ManyNeighbourTables:
                // The same folder OVER the naming cap: a neighbour shipping enough languages that the refusal cannot
                // quote them all. Twelve files against a cap of eight — the state where rendering the capped list's
                // length as the folder's contents is a false claim about the modder's disk.
                foreach (var p in Directory.GetFiles(own))
                    File.Move(p, Path.Combine(own, Path.GetFileName(p).Replace("ZRef_", "ZOther_")));
                foreach (var lang in new[] { "German", "Italian" })
                    foreach (var kind in new[] { "STRINGS", "DLSTRINGS", "ILSTRINGS" })
                        File.WriteAllBytes(Path.Combine(own, $"ZOther_{lang}.{kind}"), new byte[] { 0 });
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

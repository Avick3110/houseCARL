using System.Diagnostics;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;

namespace HousecarlGenerator;

/// <summary>
/// PHASE 0, the #368/#373 attended engagement's binding measurements. Not a guard — the evidence the design ruling
/// (A-narrow with C as the floor) was gated on. Every arm prints ONE line; the summary is the last line.
///
/// <para><b>HISTORICAL, and it does not run green any more.</b> The arm these measurements unlocked — an in-place
/// write of a localized plugin, committing its emitted tables — was CUT (2026-08-26) after the pre-PR review found
/// the machinery protecting it destroyed its own recovery set and instructed users into the corruption it existed to
/// prevent. The measurements below are still true about MUTAGEN (M2's emit coverage and M3's detection costs are
/// what the shipped classifier rests on); what changed is the ruling they fed. M1 drives its own commit helper rather
/// than the shipped write, so it still measures what it always did — but nothing in the product performs that commit
/// on a user's own file, and no arm here should be read as describing shipped behaviour. The behaviour that ships is
/// pinned by <c>localized-write-guard</c>. Kept rather than deleted because the ruling's reversal is only legible
/// alongside the evidence it was made on.</para>
///
/// <para><b>M1 — round-trip fidelity.</b> Re-serialize a localized plugin, commit the emitted .STRINGS/.DLSTRINGS/
/// .ILSTRINGS <i>together with</i> the plugin (which is what <c>CommitStagedPatch</c> does not do today — #373), and
/// read EVERY localized value back through <c>LoadOrderResolver.OpenOverlay</c>. All three table kinds carry entries,
/// asserted rather than assumed: weapons/books cover STRINGS + DLSTRINGS, a DIAL/INFO response covers ILSTRINGS.</para>
///
/// <para><b>M2 — emit coverage.</b> Which language files does Mutagen emit on re-serialize: the configured language
/// only, or every language present beside the plugin? Measured against a fixture holding two languages' files.</para>
///
/// <para><b>M3 — hard-shape detection.</b> How a write would detect BSA-embedded strings and a language-set mismatch,
/// and what each detection costs — including the real 107 MB <c>Skyrim - Interface.bsa</c> when it is on this machine.
/// Also measures #369's interaction (a .bsa beside the plugin suppresses <c>OpenOverlay</c>'s strings redirect):
/// in scope to MEASURE here, not to fix.</para>
///
/// Run: dotnet run --project src/housecarl-generator localized-write-probe
/// </summary>
public static class LocalizedWriteProbe
{
    const int Records = 5;
    static int _red, _total;

    public static int Run(string[] args)
    {
        Console.WriteLine("PHASE 0 — #368/#373 binding measurements");
        Console.WriteLine();
        M1();
        Console.WriteLine();
        M2();
        Console.WriteLine();
        M3(args);
        Console.WriteLine();
        M4();
        Console.WriteLine();
        Console.WriteLine($"SUMMARY: {_total - _red}/{_total} green, {_red} RED");
        return _red == 0 ? 0 : 1;
    }

    /// <summary>One arm's verdict. RED is anything that falsifies a premise the engagement is gated on; an arm that is
    /// purely INFORMATIONAL (a measurement with no pass/fail claim) passes <paramref name="ok"/> = null.</summary>
    static void Row(string id, bool? ok, string detail)
    {
        if (ok is not null) { _total++; if (ok == false) _red++; }
        var tag = ok is null ? "info " : ok == true ? "green" : "RED  ";
        Console.WriteLine($"  {tag} {id,-16} {detail}");
    }

    // ------------------------------------------------------------------------------------------------------------
    // M1 — round-trip fidelity
    // ------------------------------------------------------------------------------------------------------------

    static void M1()
    {
        Console.WriteLine("== M1 — round-trip fidelity: commit the emitted string tables WITH the plugin ==");
        M1Arm("M1-beside", relocate: false);
        M1Arm("M1-relocated", relocate: true);
    }

    /// <param name="relocate">Move the plugin's strings into game-Data first — the shape A-narrow does NOT claim
    /// (writing tables beside the plugin there SHADOWS the game-Data set rather than replacing it). Measured anyway,
    /// because whether it round-trips is what tells us the refusal is about shadowing and not about fidelity.</param>
    static void M1Arm(string id, bool relocate)
    {
        var root = NewRoot();
        try
        {
            var f = BuildFixture(root, relocate: relocate, secondLanguage: false);

            // Fixture honesty: all three table kinds must actually carry entries, or the arm passes vacuously on the
            // two that do. Counts come from the table header (uint32 count), not from a guess about what Mutagen emits.
            var counts = TableCounts(f.StringsDir, f.Key);
            if (counts.Values.Any(c => c <= 0))
            {
                Row(id, false, "FIXTURE BROKEN — a table kind is empty: " + Render(counts));
                return;
            }

            var expected = ExpectedValues(edited: true);

            // The open the in-place lanes use on this shape. Strings sit beside the plugin in the non-relocated arm, so
            // the bare mutable open resolves them (#373 arm C); in the relocated arm it needs the same StringsParam
            // overrides OpenOverlay wires for reads (#373 arm B), so use them and measure the WRITE, not the read.
            SkyrimMod mut = relocate
                ? SkyrimMod.CreateFromBinary(f.PluginPath, SkyrimRelease.SkyrimSE, BinaryReadParameters.Default with
                {
                    StringsParam = new StringsReadParameters
                    {
                        BsaFolderOverride = f.DataDir,
                        StringsFolderOverride = Path.Combine(f.DataDir, "Strings"),
                    },
                })
                : SkyrimMod.CreateFromBinary(f.PluginPath, SkyrimRelease.SkyrimSE);

            // The read must ALREADY be the fixture before the write is measured: an arm whose open came back blank
            // would attribute the read side's loss (#368) to the write side (#373), which is the confusion this
            // engagement exists to separate.
            var inMemory = ReadAllFrom(mut);
            var preEdit = ExpectedValues(edited: false);
            var readWrong = preEdit.Where(kv => !inMemory.TryGetValue(kv.Key, out var g) || g != kv.Value).ToList();
            if (readWrong.Count > 0)
            {
                Row(id, false, $"the OPEN is already lossy — {readWrong.Count}/{preEdit.Count} values wrong in memory "
                             + $"(first: {readWrong[0].Key} want '{readWrong[0].Value}' got "
                             + $"'{(inMemory.TryGetValue(readWrong[0].Key, out var gg) ? gg : "<absent>")}')");
                return;
            }

            // A real in-place edit that TOUCHES a localized field — the shape that matters, since an edit which never
            // touches a string would round-trip through an unchanged table and prove nothing about renumbering.
            mut.Weapons.First(w => w.EditorID == "ZRefWeap0").Name = "EDITED NAME 0";

            // The exact WriteInPlaceStaged incantation (own declared masters, counter verbatim, no baseline), staged
            // into the same .housecarl-tmp sibling — replicated rather than called, because WriteInPlace refuses a
            // localized target today and this measurement is about what the refused path WOULD produce.
            var tmpDir = Path.Combine(Path.GetDirectoryName(f.PluginPath)!, ".housecarl-tmp");
            var tmpPath = Path.Combine(tmpDir, Path.GetFileName(f.PluginPath));
            Directory.CreateDirectory(tmpDir);
            var masters = SkyrimMod.CreateFromBinaryOverlay(f.SkyrimEsm, SkyrimRelease.SkyrimSE);
            try
            {
                mut.BeginWrite.ToPath(tmpPath)
                   .WithLoadOrder(new ISkyrimModGetter[] { masters })
                   .NoNextFormIDProcessing()
                   .Write();
            }
            finally { ((IDisposable)masters).Dispose(); }

            var emittedDir = Path.Combine(tmpDir, "Strings");
            if (!Directory.Exists(emittedDir)) { Row(id, false, "the serialize emitted no Strings folder"); return; }
            var emittedCounts = TableCounts(emittedDir, f.Key);

            // THE MEASUREMENT: commit the plugin AND the tables it emitted, as one set, beside the plugin.
            AtomicFile.Commit(tmpPath, f.PluginPath);
            var destStrings = Path.Combine(Path.GetDirectoryName(f.PluginPath)!, "Strings");
            Directory.CreateDirectory(destStrings);
            foreach (var src in Directory.GetFiles(emittedDir))
                File.Copy(src, Path.Combine(destStrings, Path.GetFileName(src)), overwrite: true);
            try { Directory.Delete(tmpDir, true); } catch { }

            var after = ReadBack(f.PluginPath, f.DataDir);
            var wrong = expected.Where(kv => !after.TryGetValue(kv.Key, out var got) || got != kv.Value)
                                .Select(kv => $"{kv.Key}: want '{kv.Value}' got '{(after.TryGetValue(kv.Key, out var g) ? g : "<absent>")}'")
                                .ToList();

            Row(id, wrong.Count == 0,
                $"{expected.Count - wrong.Count}/{expected.Count} values faithful; "
                + $"fixture tables {Render(counts)}; emitted {Render(emittedCounts)}"
                + (wrong.Count == 0 ? "" : $"; FIRST 3 WRONG: {string.Join(" | ", wrong.Take(3))}"));
            if (wrong.Count > 0) foreach (var w in wrong) Console.WriteLine("        " + w);
        }
        catch (Exception ex) { Row(id, false, $"THREW {ex.GetType().Name}: {Trunc(ex.Message)}"); }
        finally { Nuke(root); }
    }

    // ------------------------------------------------------------------------------------------------------------
    // M2 — emit coverage
    // ------------------------------------------------------------------------------------------------------------

    static void M2()
    {
        Console.WriteLine("== M2 — emit coverage: which language files does a re-serialize produce? ==");
        var root = NewRoot();
        try
        {
            var f = BuildFixture(root, relocate: false, secondLanguage: true);
            var present = LanguageFiles(f.StringsDir, f.Key);
            Row("M2-fixture", present.Count == 6,
                $"languages beside the plugin as built: {string.Join(", ", present.OrderBy(x => x))}");

            // Read with the DEFAULT target language, then re-serialize — the shape every in-place lane takes today.
            var mut = SkyrimMod.CreateFromBinary(f.PluginPath, SkyrimRelease.SkyrimSE);
            var langs = mut.Weapons.First(w => w.EditorID == "ZRefWeap0").Name!.NumLanguages;
            var outDir = Path.Combine(root, "emit-default");
            Directory.CreateDirectory(outDir);
            EmitTo(mut, f, Path.Combine(outDir, f.Key.FileName.String));
            var emitted = LanguageFiles(Path.Combine(outDir, "Strings"), f.Key);
            Row("M2-default", null,
                $"in-memory NumLanguages={langs}; emitted {emitted.Count} files: {string.Join(", ", emitted.OrderBy(x => x))}");

            // Same plugin, read with TargetLanguage=French — does the emit follow the read's language?
            var mutFr = SkyrimMod.CreateFromBinary(f.PluginPath, SkyrimRelease.SkyrimSE, BinaryReadParameters.Default with
            {
                StringsParam = new StringsReadParameters { TargetLanguage = Language.French },
            });
            var frName = mutFr.Weapons.First(w => w.EditorID == "ZRefWeap0").Name?.String;
            var outFr = Path.Combine(root, "emit-french");
            Directory.CreateDirectory(outFr);
            EmitTo(mutFr, f, Path.Combine(outFr, f.Key.FileName.String));
            var emittedFr = LanguageFiles(Path.Combine(outFr, "Strings"), f.Key);
            Row("M2-french", null,
                $"read as French gives '{frName}'; emitted {emittedFr.Count} files: {string.Join(", ", emittedFr.OrderBy(x => x))}");

            // The binding question for A-narrow's honest shape: is the emitted language set a SUBSET of what is beside
            // the plugin? If it is a proper subset, committing the emit beside the plugin leaves the uncovered
            // languages' files stale — which is exactly the mismatch shape the ruling says must be refused LOUDLY.
            var lost = present.Except(emitted, StringComparer.OrdinalIgnoreCase).ToList();
            Row("M2-coverage", null,
                lost.Count == 0
                    ? "the emit covers every language file present — no mismatch shape exists on this fixture"
                    : $"the emit COVERS {emitted.Count} of {present.Count}; UNCOVERED: {string.Join(", ", lost.OrderBy(x => x))}");

            // A language present only PARTIALLY: does the emit re-materialise the kinds whose file was missing (with
            // what content?), or drop the language? This is the one shape that can make the emit's coverage disagree
            // with the files on disk while every value still resolved.
            var p2 = NewRoot();
            try
            {
                var fp = BuildFixture(p2, relocate: false, secondLanguage: true, partialFrench: true);
                var beforeP = LanguageFiles(fp.StringsDir, fp.Key);
                var mp = SkyrimMod.CreateFromBinary(fp.PluginPath, SkyrimRelease.SkyrimSE);
                var w0 = mp.Weapons.First(w => w.EditorID == "ZRefWeap0").Name!;
                var frWeap = w0.TryLookup(Language.French, out var fw) ? fw : "<none>";
                var b0 = mp.Books.First(b => b.EditorID == "ZRefBook0").BookText!;
                var frBook = b0.TryLookup(Language.French, out var fb) ? fb : "<none>";
                var outP = Path.Combine(p2, "emit-partial");
                EmitTo(mp, fp, Path.Combine(outP, fp.Key.FileName.String));
                var emittedP = LanguageFiles(Path.Combine(outP, "Strings"), fp.Key);
                var gained = emittedP.Except(beforeP, StringComparer.OrdinalIgnoreCase).ToList();
                Row("M2-partial", null,
                    $"present {beforeP.Count} → emitted {emittedP.Count}"
                    + (gained.Count == 0 ? "" : $"; MATERIALISED {string.Join(", ", gained.OrderBy(x => x))}")
                    + $"; French weapon FULL='{frWeap}', French book text='{frBook}'");

                // WHAT is in a materialised file matters more than that it appeared: a French .DLSTRINGS holding the
                // ENGLISH text is a French translation the tool invented, which is a change of the plugin's nature, not
                // a completion of it. Read the emitted French DLSTRINGS back as French and compare against English.
                var readFr = SkyrimMod.CreateFromBinary(Path.Combine(outP, fp.Key.FileName.String), SkyrimRelease.SkyrimSE,
                    BinaryReadParameters.Default with { StringsParam = new StringsReadParameters { TargetLanguage = Language.French } });
                var readEn = SkyrimMod.CreateFromBinary(Path.Combine(outP, fp.Key.FileName.String), SkyrimRelease.SkyrimSE,
                    BinaryReadParameters.Default with { StringsParam = new StringsReadParameters { TargetLanguage = Language.English } });
                string Bk(ISkyrimModGetter m) => m.Books.First(b => b.EditorID == "ZRefBook0").BookText?.String ?? "<empty>";
                string Wp(ISkyrimModGetter m) => m.Weapons.First(w => w.EditorID == "ZRefWeap0").Name?.String ?? "<empty>";
                Row("M2-partial-body", null,
                    $"emitted plugin read as French: weapon FULL='{Wp(readFr)}' (STRINGS existed), "
                    + $"book text='{Bk(readFr)}' (DLSTRINGS was MATERIALISED); as English: book text='{Bk(readEn)}'");
            }
            finally { Nuke(p2); }
        }
        catch (Exception ex) { Row("M2", false, $"THREW {ex.GetType().Name}: {Trunc(ex.Message)}"); }
        finally { Nuke(root); }
    }

    // ------------------------------------------------------------------------------------------------------------
    // M3 — hard-shape detection and its cost
    // ------------------------------------------------------------------------------------------------------------

    static void M3(string[] args)
    {
        Console.WriteLine("== M3 — hard-shape detection: BSA-embedded strings, language-set mismatch, cost ==");
        var root = NewRoot();
        try
        {
            var f = BuildFixture(root, relocate: false, secondLanguage: true);
            var folder = Path.GetDirectoryName(f.PluginPath)!;

            // (a) the language-set enumeration a mismatch check would run: one directory glob over the plugin's own
            // Strings folder, filtered to this ModKey's files.
            var sw = Stopwatch.StartNew();
            List<string> langs = new();
            for (int i = 0; i < 1000; i++) langs = LanguageFiles(f.StringsDir, f.Key);
            sw.Stop();
            Row("M3-langset", null,
                $"{langs.Count} files → {{{string.Join(", ", langs.OrderBy(x => x))}}}; {sw.Elapsed.TotalMilliseconds / 1000:F4} ms per check");

            // (b) the cheap BSA presence test the read side USED to run as its gate — kept as the cost baseline the
            // stem-keyed test in (d) is measured against.
            sw.Restart();
            bool anyBsa = false;
            for (int i = 0; i < 1000; i++) anyBsa = Directory.EnumerateFiles(folder, "*.bsa").Any();
            sw.Stop();
            Row("M3-bsa-cheap", null,
                $"any .bsa beside the plugin = {anyBsa}; {sw.Elapsed.TotalMilliseconds / 1000:F4} ms per check (no archive opened)");

            // (c) #369, measured: drop an EMPTY-of-strings .bsa beside a plugin whose strings live in game-Data. The
            // gate now asks whether the archive embeds strings for THIS plugin, so the redirect survives it and the
            // values resolve — this arm reads 0 blanks where it used to read all of them.
            var small = SmallRealBsa();
            if (small is null) { Row("M3-369", null, "no Skyrim install on this machine — #369 arm skipped"); return; }
            var g = BuildFixture(NewRoot(), relocate: true, secondLanguage: false, bsaBeside: small);
            try
            {
                var without = ReadBack(g.PluginPath, g.DataDir);
                var blank = without.Values.Count(v => string.IsNullOrEmpty(v));
                Row("M3-369", null,
                    $"a .bsa beside a game-Data-strings plugin: FolderHasOwnStrings={LoadOrderResolver.FolderHasOwnStrings(g.PluginPath)}, "
                    + $"{blank}/{without.Count} values read BLANK through OpenOverlay (#369, fixed)");
            }
            finally { Nuke(Path.GetDirectoryName(Path.GetDirectoryName(g.DataDir)!)!); }
        }
        catch (Exception ex) { Row("M3-fixture", false, $"THREW {ex.GetType().Name}: {Trunc(ex.Message)}"); }
        finally { Nuke(root); }

        // (d) the STRONG BSA test — does a .bsa beside the plugin actually embed a strings entry for this ModKey? —
        // measured against the real Skyrim - Interface.bsa, the archive that carries the base game's strings.
        var real = args.FirstOrDefault(a => a.EndsWith(".bsa", StringComparison.OrdinalIgnoreCase))
                   ?? RealInterfaceBsa();
        if (real is null || !File.Exists(real))
        {
            Row("M3-bsa-strong", null, "no real .bsa located — pass one as an argument to measure the strong test");
            return;
        }
        try
        {
            var sw = Stopwatch.StartNew();
            var reader = Archive.CreateReader(GameRelease.SkyrimSE, real);
            int hits = reader.Files.Count(x => x.Path.StartsWith("strings", StringComparison.OrdinalIgnoreCase));
            sw.Stop();
            Row("M3-bsa-strong", null,
                $"{Path.GetFileName(real)} ({new FileInfo(real).Length / 1024 / 1024} MB): {hits} strings/ entries, "
                + $"{sw.Elapsed.TotalMilliseconds:F1} ms for open+enumerate (cold)");
            sw.Restart();
            var reader2 = Archive.CreateReader(GameRelease.SkyrimSE, real);
            _ = reader2.Files.Any(x => x.Path.StartsWith("strings", StringComparison.OrdinalIgnoreCase));
            sw.Stop();
            Row("M3-bsa-short", null, $"short-circuited (first strings/ hit): {sw.Elapsed.TotalMilliseconds:F1} ms");

            // The DISCRIMINATING form a write's detection would actually run: does this archive embed strings for the
            // plugin being written, as opposed to strings for anything at all? Same enumerate, so the same cost —
            // which is what makes "detect BSA-embedded strings" affordable rather than a presence-only guess.
            sw.Restart();
            var reader3 = Archive.CreateReader(GameRelease.SkyrimSE, real);
            var forSkyrim = reader3.Files.Count(x =>
                x.Path.StartsWith("strings", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(x.Path.ToString()).StartsWith("skyrim_", StringComparison.OrdinalIgnoreCase));
            sw.Stop();
            Row("M3-bsa-keyed", null,
                $"entries keyed to ModKey 'Skyrim': {forSkyrim}; {sw.Elapsed.TotalMilliseconds:F1} ms (same enumerate)");

            // A MALFORMED .bsa beside a localized plugin: the strings lookup parses every adjacent archive, so an
            // unreadable one takes the whole open down. Recorded because it bounds what any detection can promise —
            // a detection that opens archives inherits this failure mode, and one that does not cannot see inside them.
            var bad = NewRoot();
            try
            {
                var fb2 = BuildFixture(bad, relocate: true, secondLanguage: false);
                File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(fb2.PluginPath)!, "ZRef.bsa"),
                                   new byte[] { 0x42, 0x53, 0x41, 0x00 });
                var vals = ReadBack(fb2.PluginPath, fb2.DataDir);
                Row("M3-bsa-malformed", null, $"open SURVIVED a malformed adjacent .bsa: {vals.Count} values read");
            }
            catch (Exception ex) { Row("M3-bsa-malformed", null, $"open THREW {ex.GetType().Name}: {Trunc(ex.Message)}"); }
            finally { Nuke(bad); }
        }
        catch (Exception ex) { Row("M3-bsa-strong", false, $"THREW {ex.GetType().Name}: {Trunc(ex.Message)}"); }
    }

    /// <summary>A REAL, small .bsa to stand beside the plugin for the #369 arm — the archive's contents do not matter,
    /// only that it parses. Null when no Skyrim install is on this machine, which downgrades that arm to skipped rather
    /// than turning a missing install into a RED.</summary>
    // ------------------------------------------------------------------------------------------------------------
    // M4 — Q2: what a LOCALIZED compacted P′ would actually carry
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Q2 asks whether the compact NEW-FILE lane should keep de-localizing P′ or emit a localized P′ with a
    /// matching strings set. That turns on one fact nobody has measured: does the renumbering copy carry every language
    /// the source had, or only the one the read resolved? If it carries only one, a localized P′ would be a plugin that
    /// LOOKS translated and silently is not — worse than the de-localized output, not better.</summary>
    static void M4()
    {
        Console.WriteLine("== M4 — Q2 input: does the compact copy carry every language into P′? ==");
        var root = NewRoot();
        try
        {
            var f = BuildFixture(root, relocate: false, secondLanguage: true);
            var src = LoadOrderResolver.OpenOverlay(f.PluginPath, f.DataDir);
            try
            {
                var srcLangs = src.Weapons.First(w => w.EditorID == "ZRefWeap0").Name!.NumLanguages;

                // The identity renumber: the copy machinery, with the FormIDs left alone, so the ONLY thing the arm can
                // be measuring is what the record copy does to a TranslatedString.
                var dict = src.EnumerateMajorRecords().ToDictionary(r => r.FormKey, r => r.FormKey);
                var pPrime = new SkyrimMod(f.Key, SkyrimRelease.SkyrimSE);
                var ren = RemapEngine.RenumberModInto(pPrime, src, dict);
                if (!ren.Success) { Row("M4-copy", false, "the renumber failed: " + ren.Error); return; }
                var copiedLangs = pPrime.Weapons.First(w => w.EditorID == "ZRefWeap0").Name!.NumLanguages;
                Row("M4-copy", null, $"source carries {srcLangs} languages in memory; the copy into P′ carries {copiedLangs}");

                // What Q2-A would ship: the same P′, flagged localized, serialized. Measured rather than reasoned —
                // the emit is what decides whether a localized P′ is faithful or a translated-looking blank.
                pPrime.UsingLocalization = true;
                var outDir = Path.Combine(root, "pprime-localized");
                EmitTo(pPrime, f, Path.Combine(outDir, f.Key.FileName.String));
                var emitted = LanguageFiles(Path.Combine(outDir, "Strings"), f.Key);
                var readFr = SkyrimMod.CreateFromBinary(Path.Combine(outDir, f.Key.FileName.String), SkyrimRelease.SkyrimSE,
                    BinaryReadParameters.Default with { StringsParam = new StringsReadParameters { TargetLanguage = Language.French } });
                var en = ReadBack(Path.Combine(outDir, f.Key.FileName.String), f.DataDir);
                var expected = ExpectedValues(edited: false);
                var wrongEn = expected.Count(kv => !en.TryGetValue(kv.Key, out var g) || g != kv.Value);
                Row("M4-localized-pprime", null,
                    $"P′ flagged localized emits {emitted.Count} files; English {expected.Count - wrongEn}/{expected.Count} faithful; "
                    + $"French weapon FULL='{readFr.Weapons.First(w => w.EditorID == "ZRefWeap0").Name?.String}'");
            }
            finally { ((IDisposable)src).Dispose(); }
        }
        catch (Exception ex) { Row("M4", false, $"THREW {ex.GetType().Name}: {Trunc(ex.Message)}"); }
        finally { Nuke(root); }
    }

    static string? SmallRealBsa()
    {
        foreach (var data in SkyrimDataDirs())
        {
            var best = Directory.EnumerateFiles(data, "*.bsa")
                                .Select(p => new FileInfo(p))
                                .OrderBy(fi => fi.Length)
                                .FirstOrDefault();
            if (best is not null) return best.FullName;
        }
        return null;
    }

    static IEnumerable<string> SkyrimDataDirs()
    {
        foreach (var drive in new[] { "C", "D", "E", "F" })
        foreach (var lib in new[] { "SteamLibrary", "Program Files (x86)/Steam" })
        {
            var d = Path.Combine($"{drive}:/", lib, "steamapps/common/Skyrim Special Edition/Data");
            if (Directory.Exists(d)) yield return d;
        }
    }

    static string? RealInterfaceBsa()
    {
        foreach (var drive in new[] { "C", "D", "E", "F" })
        foreach (var lib in new[] { "SteamLibrary", "Program Files (x86)/Steam" })
        {
            var p = Path.Combine($"{drive}:/", lib, "steamapps/common/Skyrim Special Edition/Data/Skyrim - Interface.bsa");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    // ------------------------------------------------------------------------------------------------------------
    // fixture
    // ------------------------------------------------------------------------------------------------------------

    sealed record Fixture(string PluginPath, string DataDir, string StringsDir, string SkyrimEsm, ModKey Key);

    /// <summary>A localized ZRef.esp exercising all three table kinds: weapon FULL + book FULL land in .STRINGS,
    /// weapon DESC + book text in .DLSTRINGS, and a DIAL/INFO response in .ILSTRINGS.</summary>
    static Fixture BuildFixture(string root, bool relocate, bool secondLanguage, string? bsaBeside = null, bool partialFrench = false)
    {
        var data = Path.Combine(root, "game", "Data");
        var modDir = Path.Combine(root, "mods", "ZRefMod");
        Directory.CreateDirectory(data); Directory.CreateDirectory(modDir);

        var skyrimKey = new ModKey("Skyrim", ModType.Master);
        var skyrimEsm = Path.Combine(data, skyrimKey.FileName.String);
        new SkyrimMod(skyrimKey, SkyrimRelease.SkyrimSE)
            .BeginWrite.ToPath(skyrimEsm).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var key = new ModKey("ZRef", ModType.Plugin);
        var pluginPath = Path.Combine(modDir, key.FileName.String);
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE) { UsingLocalization = true };
        for (int i = 0; i < Records; i++)
            m.Weapons.Add(new Weapon(new FormKey(key, (uint)(0xA02 + i)), SkyrimRelease.SkyrimSE)
            {
                EditorID = "ZRefWeap" + i,
                Name = Loc(secondLanguage, "REF NAME " + i, "FR NAME " + i),
                Description = Loc(secondLanguage, "REF DESC " + i, "FR DESC " + i),
                BasicStats = new WeaponBasicStats { Damage = (ushort)(7 + i) },
            });
        for (int i = 0; i < Records; i++)
            m.Books.Add(new Book(new FormKey(key, (uint)(0xB02 + i)), SkyrimRelease.SkyrimSE)
            {
                EditorID = "ZRefBook" + i,
                Name = Loc(secondLanguage, "BOOK NAME " + i, "FR BOOK " + i),
                BookText = Loc(secondLanguage, "BOOK TEXT " + i, "FR TEXT " + i),
            });
        // The ILSTRINGS carrier: dialogue response text is the only string kind that lands in that table, so without a
        // DIAL/INFO the emitted .ILSTRINGS is an empty table and every M1 claim about it is vacuous.
        var topic = new DialogTopic(new FormKey(key, 0xC02), SkyrimRelease.SkyrimSE) { EditorID = "ZRefTopic" };
        for (int i = 0; i < Records; i++)
        {
            var info = new DialogResponses(new FormKey(key, (uint)(0xC03 + i)), SkyrimRelease.SkyrimSE);
            info.Responses.Add(new DialogResponse { ResponseNumber = 1, Text = Loc(secondLanguage, "LINE " + i, "FR LINE " + i) });
            topic.Responses.Add(info);
        }
        m.DialogTopics.Add(topic);
        m.ModHeader.Stats.NextFormID = (uint)(0xC03 + Records);
        var sky = SkyrimMod.CreateFromBinaryOverlay(skyrimEsm, SkyrimRelease.SkyrimSE);
        try
        {
            m.BeginWrite.ToPath(pluginPath).WithLoadOrder(new ISkyrimModGetter[] { sky })
             .NoNextFormIDProcessing().Write();
        }
        finally { ((IDisposable)sky).Dispose(); }

        var own = Path.Combine(modDir, "Strings");
        if (!Directory.Exists(own))
            throw new InvalidOperationException("fixture: UsingLocalization produced no Strings folder — every arm below would be vacuous.");

        var stringsDir = own;
        if (relocate)
        {
            stringsDir = Path.Combine(data, "Strings");
            Directory.CreateDirectory(stringsDir);
            foreach (var f in Directory.GetFiles(own)) File.Move(f, Path.Combine(stringsDir, Path.GetFileName(f)), overwrite: true);
            Directory.Delete(own, true);
        }
        // A language present only PARTIALLY — French .STRINGS with no French .DLSTRINGS/.ILSTRINGS. The shape that
        // would produce a language-set mismatch if the emit's coverage tracked the FILES rather than the values.
        if (partialFrench)
            foreach (var kind in new[] { "DLSTRINGS", "ILSTRINGS" })
            {
                var victim = Path.Combine(stringsDir, $"{key.Name}_French.{kind}");
                if (File.Exists(victim)) File.Delete(victim);
            }

        // A REAL .bsa beside the plugin that embeds no strings for THIS ModKey — enough for FolderHasOwnStrings, which
        // tests presence only. That is #369's mechanism, and the arm's point is that presence alone suppresses the
        // redirect. Real rather than fabricated: a malformed .bsa throws out of the open instead, which is a different
        // fact (measured separately in M3-bsa-malformed) and would make this arm about the wrong thing.
        if (bsaBeside is not null) File.Copy(bsaBeside, Path.Combine(modDir, "ZRef.bsa"));

        return new Fixture(pluginPath, data, stringsDir, skyrimEsm, key);
    }

    static TranslatedString Loc(bool second, string en, string fr)
    {
        var ts = new TranslatedString(Language.English, en);
        if (second) ts.Set(Language.French, fr);
        return ts;
    }

    static void EmitTo(SkyrimMod mod, Fixture f, string outPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var sky = SkyrimMod.CreateFromBinaryOverlay(f.SkyrimEsm, SkyrimRelease.SkyrimSE);
        try
        {
            mod.BeginWrite.ToPath(outPath).WithLoadOrder(new ISkyrimModGetter[] { sky })
               .NoNextFormIDProcessing().Write();
        }
        finally { ((IDisposable)sky).Dispose(); }
    }

    // ------------------------------------------------------------------------------------------------------------
    // readback + table inspection
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Every localized value the fixture carries, keyed by a stable handle. <paramref name="edited"/> applies
    /// the one edit M1 makes, so the expectation is the post-write truth rather than the pre-write one.</summary>
    static Dictionary<string, string> ExpectedValues(bool edited)
    {
        var d = new Dictionary<string, string>();
        for (int i = 0; i < Records; i++)
        {
            d[$"ZRefWeap{i}.Name"] = i == 0 && edited ? "EDITED NAME 0" : "REF NAME " + i;
            d[$"ZRefWeap{i}.Desc"] = "REF DESC " + i;
            d[$"ZRefBook{i}.Name"] = "BOOK NAME " + i;
            d[$"ZRefBook{i}.Text"] = "BOOK TEXT " + i;
            d[$"ZRefInfo{i}.Text"] = "LINE " + i;
        }
        return d;
    }

    static Dictionary<string, string> ReadBack(string pluginPath, string? dataDir)
    {
        var ov = LoadOrderResolver.OpenOverlay(pluginPath, dataDir);
        try { return Harvest(ov); }
        finally { ((IDisposable)ov).Dispose(); }
    }

    static Dictionary<string, string> ReadAllFrom(ISkyrimModGetter mod) => Harvest(mod);

    static Dictionary<string, string> Harvest(ISkyrimModGetter mod)
    {
        var d = new Dictionary<string, string>();
        foreach (var w in mod.Weapons)
        {
            d[$"{w.EditorID}.Name"] = w.Name?.String ?? "";
            d[$"{w.EditorID}.Desc"] = w.Description?.String ?? "";
        }
        foreach (var b in mod.Books)
        {
            d[$"{b.EditorID}.Name"] = b.Name?.String ?? "";
            d[$"{b.EditorID}.Text"] = b.BookText?.String ?? "";
        }
        int i = 0;
        foreach (var t in mod.DialogTopics)
        foreach (var info in t.Responses)
        foreach (var r in info.Responses)
            d[$"ZRefInfo{i++}.Text"] = r.Text?.String ?? "";
        return d;
    }

    /// <summary>Entry count per table kind, read from the table header (a uint32 count at offset 0) — so an "empty
    /// .ILSTRINGS" is a fact the arm reports rather than an assumption it makes.</summary>
    static Dictionary<string, int> TableCounts(string stringsDir, ModKey key)
    {
        var d = new Dictionary<string, int>();
        if (!Directory.Exists(stringsDir)) return d;
        foreach (var p in Directory.GetFiles(stringsDir))
        {
            var name = Path.GetFileName(p);
            if (!name.StartsWith(key.Name + "_", StringComparison.OrdinalIgnoreCase)) continue;
            var ext = Path.GetExtension(p).TrimStart('.').ToUpperInvariant();
            using var fs = File.OpenRead(p);
            var head = new byte[4];
            d[ext] = fs.Read(head, 0, 4) == 4 ? (int)BitConverter.ToUInt32(head) : -1;
        }
        return d;
    }

    static List<string> LanguageFiles(string stringsDir, ModKey key)
        => Directory.Exists(stringsDir)
            ? Directory.GetFiles(stringsDir)
                       .Select(Path.GetFileName)
                       .Where(n => n!.StartsWith(key.Name + "_", StringComparison.OrdinalIgnoreCase))
                       .Select(n => n!)
                       .ToList()
            : new List<string>();

    static string Render(Dictionary<string, int> counts)
        => "{" + string.Join(", ", counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")) + "}";

    static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "hc-localized-write-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    static void Nuke(string root) { try { Directory.Delete(root, true); } catch { } }

    static string Trunc(string s) => s.Length > 200 ? s.Substring(0, 200) + "…" : s;
}

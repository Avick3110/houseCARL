using System.Reflection;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;

namespace HousecarlGenerator;

/// <summary>
/// EXPLORATORY probe: does the MUTABLE open honour the same strings overrides OpenOverlay wires for reads, and does the
/// mutate → WriteInPlace round-trip preserve the resolved strings ON DISK? Not a guard — the evidence behind the
/// in-place lane's localized refusal.
///
/// <para>ON A CURRENT BUILD EVERY ARM STOPS AT THE REFUSAL, which is the whole point of the refusal: the write will not
/// re-serialize a localized plugin. To reproduce the ORIGINAL measurement — the one the refusal cites, where a weapon
/// comes back reading a book's name — disable the choke point in <c>WriteEngine.WriteInPlace</c> and re-run. The arms
/// are kept intact so that measurement stays reproducible rather than becoming a claim in a comment.</para>
///
/// <para>The measurement, for the record: with the guard bypassed, ARM A (strings relocated to game-Data, bare open)
/// comes back blank and STAYS blank when re-read with the game-Data folder; ARM B (the same fixture, strings-aware
/// open) reads every value correctly in memory and writes a MIX of correct, blank, and other-record text; ARM C
/// (strings sitting correctly beside the plugin — nothing wrong with it at all) is scrambled the same way. The emitted
/// .STRINGS and .DLSTRINGS differ byte-wise from the ones the committed plugin is left resolving against.</para>
///
/// Run: dotnet run --project src/housecarl-generator repoint-strings-probe
/// </summary>
public static class RepointStringsProbe
{
    const int Records = 5;

    public static int Run(string[] args)
    {
        Console.WriteLine("== API: SkyrimMod.CreateFromBinary overloads ==");
        foreach (var m in typeof(SkyrimMod).GetMethods(BindingFlags.Public | BindingFlags.Static)
                                           .Where(x => x.Name == "CreateFromBinary"))
            Console.WriteLine("   " + m.ReturnType.Name + " CreateFromBinary(" +
                string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");

        Console.WriteLine();
        Console.WriteLine("== API: BinaryWriteParameters public properties ==");
        foreach (var pr in typeof(Mutagen.Bethesda.Plugins.Binary.Parameters.BinaryWriteParameters)
                           .GetProperties(BindingFlags.Public | BindingFlags.Instance))
            Console.WriteLine("   " + pr.PropertyType.Name + " " + pr.Name);

        Console.WriteLine();
        Console.WriteLine("== ARM A: today's bare CreateFromBinary (the reported bug) ==");
        int a = Arm(strings: false);
        Console.WriteLine();
        Console.WriteLine("== ARM B: CreateFromBinary + the same StringsParam overrides OpenOverlay wires ==");
        int b = Arm(strings: true);
        Console.WriteLine();
        Console.WriteLine("== ARM C: strings LEFT BESIDE the plugin — the ordinary localized plugin, bare open ==");
        int c = Arm(strings: false, relocate: false);
        return a + b + c;
    }

    static int Arm(bool strings, bool relocate = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "hc-repoint-strings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // ---- fixture: game-Data with Skyrim.esm; ZTgt.esp; localized ZRef.esp linking into ZTgt ----
            var data = Path.Combine(root, "game", "Data");
            var refDir = Path.Combine(root, "mods", "ZRefMod");
            var tgtDir = Path.Combine(root, "mods", "ZTgtMod");
            Directory.CreateDirectory(data); Directory.CreateDirectory(refDir); Directory.CreateDirectory(tgtDir);

            var skyrimKey = new ModKey("Skyrim", ModType.Master);
            new SkyrimMod(skyrimKey, SkyrimRelease.SkyrimSE)
                .BeginWrite.ToPath(Path.Combine(data, skyrimKey.FileName.String))
                .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            var tgtKey = new ModKey("ZTgt", ModType.Plugin);
            var tgtPath = Path.Combine(tgtDir, tgtKey.FileName.String);
            var tgtOld = new FormKey(tgtKey, 0x801);
            var tgtNew = new FormKey(tgtKey, 0x900);
            {
                var t = new SkyrimMod(tgtKey, SkyrimRelease.SkyrimSE);
                t.Weapons.Add(new Weapon(tgtOld, SkyrimRelease.SkyrimSE) { EditorID = "ZTgtWeap" });
                t.ModHeader.Stats.NextFormID = 0x802;
                t.BeginWrite.ToPath(tgtPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>())
                    .NoNextFormIDProcessing().Write();
            }

            var refKey = new ModKey("ZRef", ModType.Plugin);
            var refPath = Path.Combine(refDir, refKey.FileName.String);
            {
                var r = new SkyrimMod(refKey, SkyrimRelease.SkyrimSE) { UsingLocalization = true };
                var fl = new FormList(new FormKey(refKey, 0xA01), SkyrimRelease.SkyrimSE) { EditorID = "ZRefList" };
                fl.Items.Add(tgtOld.ToLink<ISkyrimMajorRecordGetter>());
                r.FormLists.Add(fl);
                // SEVERAL localized records, not one: a single string cannot show an index that drifts under the
                // re-serialize, and index drift against the pre-existing .STRINGS is the failure this arm must be able
                // to see (the committed plugin keeps resolving out of the OLD strings file — see the tmp dump below).
                for (int i = 0; i < Records; i++)
                    r.Weapons.Add(new Weapon(new FormKey(refKey, (uint)(0xA02 + i)), SkyrimRelease.SkyrimSE)
                    {
                        EditorID = "ZRefWeap" + i,
                        Name = "REF NAME " + i,
                        Description = "REF DESC " + i,
                        BasicStats = new WeaponBasicStats { Damage = (ushort)(7 + i) },
                    });
                // A SECOND record type, interleaved in FormID with the first: string indices are handed out in emission
                // order, so a round-trip that reorders records across groups would move them. One group cannot show that.
                for (int i = 0; i < Records; i++)
                    r.Books.Add(new Book(new FormKey(refKey, (uint)(0xB02 + i)), SkyrimRelease.SkyrimSE)
                    {
                        EditorID = "ZRefBook" + i,
                        Name = "BOOK NAME " + i,
                        BookText = "BOOK TEXT " + i,
                    });
                r.ModHeader.Stats.NextFormID = (uint)(0xB02 + Records);
                var tgtOv = SkyrimMod.CreateFromBinaryOverlay(tgtPath, SkyrimRelease.SkyrimSE);
                try
                {
                    r.BeginWrite.ToPath(refPath).WithLoadOrder(new ISkyrimModGetter[] { tgtOv })
                        .NoNextFormIDProcessing().Write();
                }
                finally { ((IDisposable)tgtOv).Dispose(); }
            }

            // Relocate ZRef's strings into game-Data\Strings — the state under test.
            var own = Path.Combine(refDir, "Strings");
            if (!Directory.Exists(own)) { Console.Error.WriteLine("FIXTURE BROKEN: no Strings folder beside ZRef"); return 1; }
            var gdStrings = Path.Combine(data, "Strings");
            Directory.CreateDirectory(gdStrings);
            if (relocate)
            {
                foreach (var f in Directory.GetFiles(own)) File.Move(f, Path.Combine(gdStrings, Path.GetFileName(f)));
                Directory.Delete(own, true);
            }
            var srcStrings = relocate ? gdStrings : own;
            var stringsBefore = Directory.GetFiles(srcStrings).ToDictionary(Path.GetFileName!, File.ReadAllBytes);

            Console.WriteLine("  BEFORE (OpenOverlay + dataDir): " + ReadAll(refPath, data));

            // ---- the open under test ----
            SkyrimMod mut;
            if (strings)
            {
                var prm = BinaryReadParameters.Default with
                {
                    StringsParam = new StringsReadParameters
                    {
                        BsaFolderOverride = data,
                        StringsFolderOverride = gdStrings,
                    },
                };
                mut = SkyrimMod.CreateFromBinary(refPath, SkyrimRelease.SkyrimSE, prm);
            }
            else mut = SkyrimMod.CreateFromBinary(refPath, SkyrimRelease.SkyrimSE);
            Console.WriteLine("  IN MEMORY after the open      : " +
                string.Join(" | ", mut.Weapons.OrderBy(w => w.EditorID).Select(w => $"{w.EditorID}='{w.Name?.String}'")));

            // ---- mutate + re-serialize over the user's own file ----
            mut.RemapLinks(new Dictionary<FormKey, FormKey> { [tgtOld] = tgtNew });
            {
                var tgtOv = SkyrimMod.CreateFromBinaryOverlay(tgtPath, SkyrimRelease.SkyrimSE);
                try { WriteEngine.WriteInPlace(mut, new ISkyrimModGetter[] { tgtOv }, refPath, data); }
                catch (LocalizedTargetUnsupportedException ex)
                {
                    // The shipped behaviour. The corruption this probe measured is why the refusal exists, so on a
                    // current build the arm stops HERE rather than reproducing it — see the class summary for how to
                    // get the original measurement back.
                    Console.WriteLine("  REFUSED by the in-place write's localized choke point — nothing written.");
                    Console.WriteLine("  " + ex.Message);
                    return 0;
                }
                finally { ((IDisposable)tgtOv).Dispose(); }
            }

            Console.WriteLine("  AFTER  (OpenOverlay + dataDir): " + ReadAll(refPath, data));
            Console.WriteLine("  AFTER  (OpenOverlay, no dir)  : " + ReadAll(refPath, null));

            // ---- what the staged write left behind, and whether the game-Data strings still match ----
            Console.WriteLine("  beside ZRef.esp: " + string.Join(", ", Tree(refDir)));
            // The committed plugin keeps resolving out of the PRE-EXISTING game-Data .STRINGS (the staged write's own
            // emitted strings are discarded with the staging dir). So the re-emitted index table MUST still agree with
            // that file, or the plugin reads the wrong string per index — a silent wrong answer, not a blank one.
            var emitted = Path.Combine(refDir, ".housecarl-tmp", "Strings");
            if (Directory.Exists(emitted))
                foreach (var f in Directory.GetFiles(emitted))
                {
                    var name = Path.GetFileName(f);
                    var same = stringsBefore.TryGetValue(name, out var before) && before.SequenceEqual(File.ReadAllBytes(f));
                    Console.WriteLine($"  emitted {name} == pre-existing: {same}");
                }
            var stringsAfter = Directory.Exists(srcStrings)
                ? Directory.GetFiles(srcStrings).ToDictionary(Path.GetFileName!, File.ReadAllBytes)
                : new Dictionary<string, byte[]>();
            Console.WriteLine("  source Strings unchanged      : " +
                (stringsBefore.Count == stringsAfter.Count &&
                 stringsBefore.All(kv => stringsAfter.TryGetValue(kv.Key, out var v) && v.SequenceEqual(kv.Value))));
            return 0;
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    static string ReadAll(string pluginPath, string? dataDir)
    {
        var ov = LoadOrderResolver.OpenOverlay(pluginPath, dataDir);
        try
        {
            var ws = string.Join(" | ", ov.Weapons.OrderBy(w => w.EditorID)
                .Select(w => $"{w.EditorID}='{w.Name?.String}'/'{w.Description?.String}'"));
            var link = ov.FormLists.First().Items.First().FormKey;
            return $"{ws} link={link} flags={ov.ModHeader.Flags}";
        }
        finally { ((IDisposable)ov).Dispose(); }
    }

    static IEnumerable<string> Tree(string dir)
        => Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories)
                    .Select(p => Path.GetRelativePath(dir, p) + (Directory.Exists(p) ? "\\" : ""));
}

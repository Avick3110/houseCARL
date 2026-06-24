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
/// Probe for the localized-strings read fix (Heisen 2026-06-24): a localized DLC master resolved to an MO2
/// mod folder with no adjacent Strings/BSA (the "Cleaned Base Game Masters" pattern) reads its FULL/Name EMPTY
/// through the bare <c>CreateFromBinaryOverlay(path, release)</c> overlay — Mutagen's per-plugin strings lookup
/// only scans the plugin's own folder, so cross-plugin strings (dragonborn strings live in Skyrim - Interface.bsa
/// beside Skyrim.esm) are never found.
///
/// v1 = API DISCOVERY + baseline repro:
///   - dump BinaryReadParameters public properties (find the strings knob)
///   - dump the strings-param type's properties (one level deeper)
///   - dump CreateFromBinaryOverlay overload signatures
///   - baseline: open the cleaned plugin with the BARE overload, read the first matching armor's Name (expect empty)
///
/// Run: dotnet run --project src/housecarl-generator strings-resolve-probe &lt;cleaned-plugin.esm&gt; &lt;stockgame-data-dir&gt; [nameSubstr]
/// </summary>
public static class StringsResolveProbe
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: strings-resolve-probe <cleaned-plugin.esm> <stockgame-data-dir> [editoridSubstr]");
            return 1;
        }
        var plugin = args[0];
        var dataDir = args[1];
        var edidSub = args.Length > 2 ? args[2] : "Bonemold";
        if (!File.Exists(plugin)) { Console.Error.WriteLine($"not found: {plugin}"); return 1; }
        if (!Directory.Exists(dataDir)) { Console.Error.WriteLine($"not a dir: {dataDir}"); return 1; }
        Console.WriteLine($"plugin:   {plugin}");
        Console.WriteLine($"data dir: {dataDir}");
        Console.WriteLine($"edid ~:   {edidSub}");

        // ---- API 1: BinaryReadParameters surface ----
        Console.WriteLine();
        Console.WriteLine("== API 1: BinaryReadParameters public instance properties ==");
        var brp = typeof(BinaryReadParameters);
        foreach (var p in brp.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            Console.WriteLine($"   {p.PropertyType.FullName} {p.Name}");

        // ---- API 2: the strings-ish property types, one level deeper ----
        Console.WriteLine();
        Console.WriteLine("== API 2: strings-ish param type members ==");
        foreach (var p in brp.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var pt = p.PropertyType;
            var u = Nullable.GetUnderlyingType(pt) ?? pt;
            if (!u.Name.Contains("String") && !u.Name.Contains("Strings")) continue;
            Console.WriteLine($"   -- {u.FullName} (via {p.Name}) --");
            foreach (var sp in u.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                Console.WriteLine($"        {sp.PropertyType.FullName} {sp.Name}");
            foreach (var ci in u.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
                Console.WriteLine($"        .ctor(" + string.Join(", ", ci.GetParameters().Select(x => $"{x.ParameterType.Name} {x.Name}")) + ")");
            foreach (var sm in u.GetMethods(BindingFlags.Public | BindingFlags.Static))
                if (sm.Name.Contains("Factory") || sm.Name.Contains("From") || sm.Name == "Create")
                    Console.WriteLine($"        static {sm.ReturnType.Name} {sm.Name}(" + string.Join(", ", sm.GetParameters().Select(x => $"{x.ParameterType.Name} {x.Name}")) + ")");
        }

        // ---- API 3: CreateFromBinaryOverlay overloads ----
        Console.WriteLine();
        Console.WriteLine("== API 3: SkyrimMod.CreateFromBinaryOverlay overloads ==");
        foreach (var m in typeof(SkyrimMod).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "CreateFromBinaryOverlay"))
            Console.WriteLine("   (" + string.Join(", ", m.GetParameters().Select(x => $"{x.ParameterType.Name} {x.Name}" + (x.HasDefaultValue ? " = …" : ""))) + ")");

        // ---- API 4: any StringsReadParameters / StringsFolderLookup* types in the loaded Mutagen assemblies ----
        Console.WriteLine();
        Console.WriteLine("== API 4: Strings* types in Mutagen assemblies ==");
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().Where(a => (a.GetName().Name ?? "").StartsWith("Mutagen")))
        {
            Type[] types; try { types = asm.GetTypes(); } catch { continue; }
            foreach (var t in types.Where(t => t.IsPublic && (t.Name.StartsWith("StringsReadParameters") || t.Name.StartsWith("StringsFolderLookup") || t.Name == "StringsSource" || t.Name == "StringsLanguageFormat")))
            {
                Console.WriteLine($"   {t.FullName}");
                foreach (var sm in t.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(x => !x.IsSpecialName))
                    Console.WriteLine($"        static {sm.ReturnType.Name} {sm.Name}(" + string.Join(", ", sm.GetParameters().Select(x => $"{x.ParameterType.Name} {x.Name}")) + ")");
                foreach (var ci in t.GetConstructors())
                    Console.WriteLine($"        .ctor(" + string.Join(", ", ci.GetParameters().Select(x => $"{x.ParameterType.Name} {x.Name}")) + ")");
            }
        }

        // ---- CANDIDATES: open with various StringsParam, read first matching armor names ----
        var strings = Path.Combine(dataDir, "Strings");
        Test("BASELINE bare overload", () => SkyrimMod.CreateFromBinaryOverlay(plugin, SkyrimRelease.SkyrimSE), plugin, edidSub);
        Test("FIX-A BsaFolderOverride=dataDir", () => SkyrimMod.CreateFromBinaryOverlay(plugin, SkyrimRelease.SkyrimSE,
            BinaryReadParameters.Default with { StringsParam = new StringsReadParameters { BsaFolderOverride = dataDir } }), plugin, edidSub);
        Test("FIX-B Bsa+Strings override, lang=English", () => SkyrimMod.CreateFromBinaryOverlay(plugin, SkyrimRelease.SkyrimSE,
            BinaryReadParameters.Default with { StringsParam = new StringsReadParameters {
                BsaFolderOverride = dataDir, StringsFolderOverride = strings, TargetLanguage = Language.English } }), plugin, edidSub);

        // ---- PRODUCT: drive the REAL fix (LoadOrderResolver.OpenOverlay) + the REAL read (ReadEngine) + Heisen's
        //      EXACT predicate (FieldPredicateSet "Name contains <stem>"). This is the guard the bug demands. ----
        Console.WriteLine();
        Console.WriteLine("== PRODUCT: OpenOverlay + ReadEngine + FieldPredicate ==");
        bool ok = true;

        // FIX arm: the cleaned DLC master (no own strings) MUST now resolve Name + match the predicate.
        ok &= ProductCheck("cleaned DLC master (FIX)", plugin, dataDir, edidSub, mustResolve: true);

        // NO-REGRESSION arm: a folder-adjacent localized master (Skyrim.esm beside its BSA) must STILL resolve —
        // OpenOverlay's gate keeps the unchanged default open for it (its folder has a .bsa).
        var skyrim = Path.Combine(dataDir, "Skyrim.esm");
        if (File.Exists(skyrim))
            ok &= ProductCheck("folder-adjacent master (NO-REGRESSION)", skyrim, dataDir, "Hide", mustResolve: true);
        else
            Console.WriteLine($"   (skip NO-REGRESSION: {skyrim} not present)");

        Console.WriteLine();
        Console.WriteLine(ok ? "PROBE PASS — product path resolves localized names + predicate matches." : "PROBE FAIL — see above.");
        return ok ? 0 : 1;
    }

    /// <summary>Open <paramref name="plugin"/> through the REAL product helper, read the first edid~<paramref
    /// name="edidSub"/> armor's Name via ReadEngine, and run Heisen's exact "Name contains &lt;edidSub-stem&gt;"
    /// predicate. When <paramref name="mustResolve"/>, a non-empty token AND a predicate match are required.</summary>
    static bool ProductCheck(string label, string plugin, string dataDir, string edidSub, bool mustResolve)
    {
        var ov = HousecarlCore.LoadOrderResolver.OpenOverlay(plugin, dataDir);
        var (set, perr) = HousecarlCore.FieldPredicateSet.Parse(new[] { $"Name contains {edidSub}" });
        if (perr is not null) { Console.WriteLine($"   {label}: predicate parse error: {perr}"); return false; }
        foreach (var rec in ov.EnumerateMajorRecords(typeof(IArmorGetter), throwIfUnknown: true))
        {
            if (rec.EditorID is null || rec.EditorID.IndexOf(edidSub, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var rf = HousecarlCore.ReadEngine.ReadFields(rec, new[] { "Name" });
            var fv = rf.Fields.Count > 0 ? rf.Fields[0] : null;
            var token = fv is { HasValue: true } ? fv.Token : null;
            bool matched = set!.Matches(rec);
            bool resolved = !string.IsNullOrEmpty(token);
            bool pass = !mustResolve || (resolved && matched);
            Console.WriteLine($"   {label}: {rec.FormKey} edid={rec.EditorID} ReadEngine Name={(token is null ? "<none>" : $"'{token}'")} predicateMatch={matched} => {(pass ? "PASS" : "FAIL")}");
            return pass;
        }
        Console.WriteLine($"   {label}: (no armor matched edid~'{edidSub}') => {(mustResolve ? "FAIL" : "PASS")}");
        return !mustResolve;
    }

    static void Test(string label, Func<ISkyrimModGetter> open, string plugin, string edidSub)
    {
        Console.WriteLine();
        Console.WriteLine($"== {label} ==");
        ISkyrimModGetter ov;
        try { ov = open(); }
        catch (Exception ex) { Console.WriteLine($"   open THREW: {ex.GetType().Name}: {Trunc(ex.Message)}"); return; }
        int shown = 0;
        var en = ov.EnumerateMajorRecords(typeof(IArmorGetter), throwIfUnknown: true).GetEnumerator();
        while (shown < 3)
        {
            IMajorRecordGetter armo;
            try { if (!en.MoveNext()) break; armo = en.Current; }
            catch (Exception ex) { Console.WriteLine($"   enum THREW: {ex.GetType().Name}: {Trunc(ex.Message)}"); break; }
            if (armo.EditorID is null || armo.EditorID.IndexOf(edidSub, StringComparison.OrdinalIgnoreCase) < 0) continue;
            string? str;
            try
            {
                var nameVal = armo.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)?.GetValue(armo);
                str = nameVal?.GetType().GetProperty("String", BindingFlags.Public | BindingFlags.Instance)?.GetValue(nameVal) as string;
            }
            catch (Exception ex) { str = $"<read threw: {ex.GetType().Name}>"; }
            Console.WriteLine($"   {armo.FormKey}  edid={armo.EditorID}  Name.String={(str is null ? "<null>" : $"'{str}'")}");
            shown++;
        }
        if (shown == 0) Console.WriteLine($"   (no armor matched edid~'{edidSub}')");
    }

    static string Trunc(string s) => s.Length > 140 ? s.Substring(0, 140) + "…" : s;
}

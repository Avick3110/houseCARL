using System.Reflection;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Plugins;          // RecordType (the 4-char xEdit signature)
using Mutagen.Bethesda.Plugins.Records;  // IMajorRecordGetter

namespace HousecarlGenerator;

/// <summary>
/// Maintenance diagnostic (not part of generation). Enumerates the full vocabulary of
/// get-only property type shapes across every mutable interface in the corpus. This is
/// the regression guard for <see cref="CorpusGenerator"/>'s mutable-in-place whitelist:
/// on a Mutagen version bump, re-run <c>dotnet run --project src/housecarl-generator vocab</c>
/// and confirm every mutable-collection shape it lists is handled by the whitelist (only
/// the genuinely read-only shapes — IReadOnlyList / IReadOnlyCache — should be excluded).
/// A new shape appearing here that the whitelist doesn't cover = silently read-only fields.
/// </summary>
public static class Probe
{
    public static int RunVocab()
    {
        var asm = typeof(IArmorGetter).Assembly;
        var getOnlyDefs = new Dictionary<string, int>();       // generic-def full name -> count of get-only props
        var getOnlyNonGeneric = new Dictionary<string, int>(); // non-generic get-only prop types (e.g. arrays)

        foreach (var ifc in asm.GetTypes())
        {
            if (!ifc.IsInterface) continue;
            if (ifc.Name.EndsWith("Getter")) continue;          // only mutable interfaces
            if (!ifc.Name.StartsWith("I")) continue;
            foreach (var p in ifc.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.CanWrite) continue;                        // settable props are already handled
                var pt = p.PropertyType;
                if (pt.IsArray)
                {
                    var key = $"{pt.GetElementType()?.Name ?? "?"}[]";
                    getOnlyNonGeneric[key] = getOnlyNonGeneric.GetValueOrDefault(key) + 1;
                }
                else if (pt.IsGenericType)
                {
                    var def = pt.GetGenericTypeDefinition().FullName ?? pt.GetGenericTypeDefinition().Name;
                    getOnlyDefs[def] = getOnlyDefs.GetValueOrDefault(def) + 1;
                }
                else
                {
                    var key = pt.FullName ?? pt.Name;
                    getOnlyNonGeneric[key] = getOnlyNonGeneric.GetValueOrDefault(key) + 1;
                }
            }
        }

        Console.WriteLine("=== get-only GENERIC property type definitions (mutable interfaces) ===");
        foreach (var kv in getOnlyDefs.OrderByDescending(k => k.Value))
            Console.WriteLine($"  {kv.Value,5}  {kv.Key}");
        Console.WriteLine();
        Console.WriteLine("=== get-only ARRAY / non-generic property types ===");
        foreach (var kv in getOnlyNonGeneric.OrderByDescending(k => k.Value))
            Console.WriteLine($"  {kv.Value,5}  {kv.Key}");
        return 0;
    }

    /// <summary>
    /// Decision #1 verify: confirm the reflection path from a record's concrete class to its xEdit
    /// 4-char signature (Armor -> "ARMO") is reliable across the WHOLE record set, not just the easy
    /// top-level cases. The first probe found a static <c>GrupRecordType</c> field; this one sweeps
    /// every concrete major record, cross-checks it against the registration's TriggeringRecordType,
    /// and flags the records that live inside another record's GRUP (placed refs, INFO, NAVM, LAND)
    /// where a "GRUP type" could name the container instead of the record's own signature.
    /// Run: dotnet run --project src/housecarl-generator sig
    /// </summary>
    public static int RunSig()
    {
        var asm = typeof(IArmorGetter).Assembly;
        var recT = typeof(RecordType);
        bool IsRec(Type t) => t == recT || Nullable.GetUnderlyingType(t) == recT;
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        // ---- 1. Discover the registration's RecordType member shape (sample: Armor_Registration). ----
        // The first probe enumerated only registration *properties* and found none, so the triggering
        // signature is likely a static FIELD — enumerate both, by name, to learn the canonical member.
        Console.WriteLine("=== RecordType members on Armor_Registration (discover TriggeringRecordType) ===");
        var sampleReg = asm.GetType("Mutagen.Bethesda.Skyrim.Armor")
            ?.GetProperty("StaticRegistration", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (sampleReg != null)
        {
            var rt = sampleReg.GetType();
            foreach (var f in rt.GetFields(Any).Where(f => IsRec(f.FieldType)))
                Console.WriteLine($"  field {f.Name} = {f.GetValue(sampleReg)}");
            foreach (var p in rt.GetProperties(Any).Where(p => p.GetIndexParameters().Length == 0 && IsRec(p.PropertyType)))
                Console.WriteLine($"  prop  {p.Name} = {p.GetValue(sampleReg)}");
        }
        Console.WriteLine();

        // Resolve a record's TriggeringRecordType (authoritative own-signature) from its registration.
        string? Trig(Type concrete)
        {
            var reg = concrete.GetProperty("StaticRegistration", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (reg == null) return null;
            var rt = reg.GetType();
            var f = rt.GetField("TriggeringRecordType", Any);
            if (f != null && IsRec(f.FieldType)) return f.GetValue(reg)?.ToString();
            var p = rt.GetProperty("TriggeringRecordType", Any);
            if (p != null && IsRec(p.PropertyType)) return p.GetValue(reg)?.ToString();
            return null;
        }

        // ---- 2. Full sweep: GrupRecordType static field vs TriggeringRecordType on every concrete record. ----
        var records = asm.GetTypes()
            .Where(c => c.IsClass && !c.IsAbstract && !c.Name.EndsWith("BinaryOverlay", StringComparison.Ordinal)
                        && typeof(IMajorRecordGetter).IsAssignableFrom(c))
            .OrderBy(c => c.Name, StringComparer.Ordinal).ToList();

        int withGrup = 0, withTrig = 0;
        var missingBoth = new List<string>();
        var mismatch = new List<string>();
        Console.WriteLine($"=== per-record signature sweep ({records.Count} concrete records) ===");
        Console.WriteLine($"  {"Record",-30} {"Grup",-6} {"Trig",-6} note");
        foreach (var c in records)
        {
            var gf = c.GetField("GrupRecordType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            string? grup = (gf != null && IsRec(gf.FieldType)) ? gf.GetValue(null)?.ToString() : null;
            string? trig;
            try { trig = Trig(c); } catch { trig = null; }

            if (grup != null) withGrup++;
            if (trig != null) withTrig++;
            string note = "";
            if (grup == null && trig == null) { missingBoth.Add(c.Name); note = "<-- MISSING BOTH"; }
            else if (grup != null && trig != null && grup != trig) { mismatch.Add($"{c.Name} (Grup={grup} Trig={trig})"); note = "<-- MISMATCH"; }
            Console.WriteLine($"  {c.Name,-30} {grup,-6} {trig,-6} {note}");
        }
        Console.WriteLine();
        Console.WriteLine($"with GrupRecordType : {withGrup}/{records.Count}");
        Console.WriteLine($"with Triggering     : {withTrig}/{records.Count}");
        Console.WriteLine($"MISSING BOTH ({missingBoth.Count}): {string.Join(", ", missingBoth)}");
        Console.WriteLine($"MISMATCH ({mismatch.Count}): {string.Join("; ", mismatch)}");
        return 0;
    }
}

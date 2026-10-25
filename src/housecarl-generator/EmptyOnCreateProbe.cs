using System.Collections;
using System.Reflection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// Which subrecords Mutagen writes at 0 bytes on a record houseCARL creates: every record type is made bare through
/// the create door, written, and read back. Each 0-byte subrecord is a vanilla marker or a loud field; one no field
/// fills is a coverage anomaly. A Mutagen fix makes its field drop out of the list by itself.
/// </summary>
internal static class EmptyOnCreateProbe
{
    /// <summary>The 0-byte subrecords vanilla carries too: Skyrim.esm writes these empty (lanes/mesg-inam research §5b, read 2026-10-25).</summary>
    internal static readonly IReadOnlySet<(string Record, string Subrecord)> VanillaMarkers = new HashSet<(string, string)>
    {
        ("NPC_", "DATA"), ("QUST", "NEXT"), ("RACE", "FNAM"), ("RACE", "MNAM"), ("RACE", "NAM0"),
        ("RACE", "NAM1"), ("RACE", "NAM3"), ("SCEN", "HNAM"),
    };

    /// <summary>Per record class, the non-marker subrecords it is written with at 0 bytes and the fields that fill each.</summary>
    internal static Dictionary<Type, List<EmptySubrecord>> Run(IReadOnlyList<Type> recordClasses, List<string> anomalies)
    {
        var result = new Dictionary<Type, List<EmptySubrecord>>();
        foreach (var (c, (recSig, empties)) in Empties(recordClasses, recordClasses, null, anomalies))
        {
            var loud = empties.Where(s => !VanillaMarkers.Contains((recSig, s))).ToList();
            if (loud.Count == 0) continue;
            var found = new List<EmptySubrecord>();
            foreach (var sig in loud)
            {
                var fields = FieldsFilling(c, sig, recordClasses);
                if (fields.Count == 0)
                    anomalies.Add($"{c.Name} ({recSig}): Mutagen writes {sig} at 0 bytes on a bare new record, it is not a vanilla marker, and no field of {c.Name} fills it — add it to the marker list with its source, or find the field.");
                else found.Add(new EmptySubrecord { Subrecord = sig, Fields = fields });
            }
            if (found.Count > 0) result[c] = found;
        }
        return result;
    }

    /// <summary>The properties of <paramref name="c"/> that, given one value, make <paramref name="sig"/> stop being written at 0 bytes.</summary>
    static List<string> FieldsFilling(Type c, string sig, IReadOnlyList<Type> all)
    {
        var fields = new List<string>();
        foreach (var p in c.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            bool filled = false;
            var after = Empties(new[] { c }, all, rec => filled = TryFill(p, rec), null);
            if (filled && after.TryGetValue(c, out var shape) && !shape.Empties.Contains(sig)) fields.Add(p.Name);
        }
        return fields;
    }

    /// <summary>Give an empty byte field or list one value; false when the property is neither.</summary>
    static bool TryFill(PropertyInfo p, object rec)
    {
        if (!p.CanRead || p.GetIndexParameters().Length > 0) return false;
        var t = p.PropertyType;
        if (p.CanWrite && t == typeof(byte[]) && p.GetValue(rec) is byte[] { Length: 0 }) { p.SetValue(rec, new byte[1]); return true; }
        if (p.CanWrite && t == typeof(MemorySlice<byte>) && p.GetValue(rec) is MemorySlice<byte> { Length: 0 })
        { p.SetValue(rec, new MemorySlice<byte>(new byte[1])); return true; }
        if (p.GetValue(rec) is IList { Count: 0, IsReadOnly: false } list && ElementFor(list) is { } el)
        {
            try { list.Add(el); return true; } catch { return false; }
        }
        return false;
    }

    /// <summary>A default element for a list: a null link for a link list, else a parameterless construct of the element's class.</summary>
    static object? ElementFor(IList list)
    {
        var et = list.GetType().GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IList<>))
            ?.GetGenericArguments()[0];
        if (et is null) return null;
        if (et.IsGenericType && et.Name.StartsWith("IFormLink", StringComparison.Ordinal))
            return System.Activator.CreateInstance(typeof(FormLink<>).MakeGenericType(et.GetGenericArguments()[0]));
        var concrete = et.IsInterface
            ? et.Assembly.GetType($"{et.Namespace}.{et.Name.TrimStart('I').Replace("Getter", "")}")
            : et;
        if (concrete is null || concrete.IsAbstract || concrete.GetConstructor(Type.EmptyTypes) is null) return null;
        return System.Activator.CreateInstance(concrete);
    }

    /// <summary>Create each of <paramref name="classes"/> bare in one fresh plugin, write it, and read back each record's signature and 0-byte subrecords.</summary>
    static Dictionary<Type, (string RecSig, List<string> Empties)> Empties(
        IReadOnlyList<Type> classes, IReadOnlyList<Type> parents, Action<IMajorRecord>? touch, List<string>? anomalies)
    {
        var mod = new SkyrimMod(ModKey.FromFileName("HcEmptyOnCreateProbe.esp"), SkyrimRelease.SkyrimSE);
        var made = new Dictionary<FormKey, Type>();
        foreach (var c in classes)
        {
            IMajorRecord? rec;
            try { rec = Construct(mod, c, parents); }
            catch (Exception ex) { anomalies?.Add($"{c.Name}: could not be created bare to probe its empty subrecords: {ex.Message}"); continue; }
            if (rec is null) { anomalies?.Add($"{c.Name}: no create route (flat, nested or cell) reaches it, so its empty subrecords are not probed."); continue; }
            touch?.Invoke(rec);
            made[rec.FormKey] = c;
        }

        var dir = Path.Combine(Path.GetTempPath(), $"hc-empty-on-create-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, mod.ModKey.FileName.String);
        try
        {
            try { mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write(); }
            catch (Exception ex)
            {
                anomalies?.Add($"the bare-construct probe plugin could not be written, so no empty subrecords were probed: {ex.Message}");
                return new();
            }
            var walk = SubrecordInventory.Walk(File.ReadAllBytes(path), mod.ModKey);
            var result = new Dictionary<Type, (string, List<string>)>();
            foreach (var (fk, c) in made)
                if (walk.TryGetValue(fk, out var entry))
                    result[c] = (entry.Signature, entry.Order.Where(k => k.Length == 0).Select(k => k.Sig).Distinct().ToList());
            return result;
        }
        finally { try { Directory.Delete(dir, true); } catch { /* temp cleanup best-effort */ } }
    }

    /// <summary>A bare record of <paramref name="c"/> through the create door: flat, interior cell, or nested under the first parent type that holds it.</summary>
    static IMajorRecord? Construct(SkyrimMod mod, Type c, IReadOnlyList<Type> parents)
    {
        if (c == typeof(Cell)) return WriteEngine.AddInteriorCell(mod, null);
        if (WriteEngine.CanCreateType(c.Name, out _)) return WriteEngine.GenericAddNew(mod, c.Name, null);
        foreach (var p in parents)
        {
            if (p == c) continue;
            // The unique fitting slot, else the first named one (a cell holds placed refs in two).
            var slots = new string?[] { null }.Concat(p.GetProperties().Select(x => x.Name))
                .Where(s => WriteEngine.CanCreateNested(c.Name, p, s, out _)).Take(1).ToList();
            if (slots.Count == 0) continue;
            IMajorRecord? parent = p == typeof(Cell) ? WriteEngine.AddInteriorCell(mod, null)
                : WriteEngine.CanCreateType(p.Name, out _) ? WriteEngine.GenericAddNew(mod, p.Name, null) : null;
            if (parent is null) continue;
            return WriteEngine.NestedAddNew(mod, parent, c.Name, slots[0], null);
        }
        return null;
    }
}

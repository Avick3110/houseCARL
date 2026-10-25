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
/// fills is a coverage anomaly. A Mutagen fix makes its field drop out of the list by itself. It measures Mutagen's
/// bare output, before the create path's own fills (the dialogue CK-parity ones), which touch no probed subrecord.
/// </summary>
internal static class EmptyOnCreateProbe
{
    /// <summary>The 0-byte subrecords vanilla carries too: Skyrim.esm writes these empty (lanes/mesg-inam research §5b, read 2026-10-25).</summary>
    static readonly IReadOnlySet<(string Record, string Subrecord)> VanillaMarkers = new HashSet<(string, string)>
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
            // One write per fillable property; each loud subrecord's fields are those whose fill made it non-empty.
            var stillEmpty = c.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => (p.Name, After: Empties(new[] { c }, recordClasses, rec => TryFill(p, rec), null)))
                .Where(x => x.After.ContainsKey(c))
                .Select(x => (x.Name, Sigs: x.After[c].Empties))
                .ToList();
            var found = new List<EmptySubrecord>();
            foreach (var sig in loud)
            {
                var fields = stillEmpty.Where(x => !x.Sigs.Contains(sig)).Select(x => x.Name).ToList();
                if (fields.Count == 0)
                    anomalies.Add($"{c.Name} ({recSig}): Mutagen writes {sig} at 0 bytes on a bare new record, it is not a vanilla marker, and no field of {c.Name} fills it — add it to the marker list with its source, or find the field.");
                else found.Add(new EmptySubrecord { Subrecord = sig, Fields = fields });
            }
            if (found.Count > 0) result[c] = found;
        }
        return result;
    }

    /// <summary>Give an empty byte field one byte, or an empty link list one null link; false for any other property, so
    /// a 0-byte subrecord only another shape fills surfaces as an anomaly.</summary>
    static bool TryFill(PropertyInfo p, object rec)
    {
        if (!p.CanRead || p.GetIndexParameters().Length > 0) return false;
        if (p.CanWrite && p.PropertyType == typeof(MemorySlice<byte>) && p.GetValue(rec) is MemorySlice<byte> { Length: 0 })
        { p.SetValue(rec, new MemorySlice<byte>(new byte[1])); return true; }
        if (p.GetValue(rec) is not IList { Count: 0, IsReadOnly: false } list) return false;
        if (WriteEngine.ListElementType(list.GetType()) is not { IsGenericType: true } et
            || !et.Name.StartsWith("IFormLink", StringComparison.Ordinal)) return false;
        list.Add(System.Activator.CreateInstance(typeof(FormLink<>).MakeGenericType(et.GetGenericArguments()[0])));
        return true;
    }

    /// <summary>Create each of <paramref name="classes"/> bare in one fresh plugin, write it, and read back each record's
    /// signature and 0-byte subrecords; a <paramref name="touch"/> that changes nothing skips the write and returns none.</summary>
    static Dictionary<Type, (string RecSig, List<string> Empties)> Empties(
        IReadOnlyList<Type> classes, IReadOnlyList<Type> parents, Func<IMajorRecord, bool>? touch, List<string>? anomalies)
    {
        var mod = new SkyrimMod(ModKey.FromFileName("HcEmptyOnCreateProbe.esp"), SkyrimRelease.SkyrimSE);
        var made = new Dictionary<FormKey, Type>();
        foreach (var c in classes)
        {
            IMajorRecord? rec;
            try { rec = Construct(mod, c, parents); }
            catch (Exception ex) { anomalies?.Add($"{c.Name}: could not be created bare to probe its empty subrecords: {ex.Message}"); continue; }
            if (rec is null) { anomalies?.Add($"{c.Name}: no create route (flat, nested or cell) reaches it, so its empty subrecords are not probed."); continue; }
            if (touch is not null && !touch(rec)) continue;
            made[rec.FormKey] = c;
        }
        if (made.Count == 0) return new();

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

    /// <summary>A bare record of <paramref name="c"/> through the create door: top-level, or nested under the first parent type that holds it.</summary>
    static IMajorRecord? Construct(SkyrimMod mod, Type c, IReadOnlyList<Type> parents)
    {
        if (NewTopLevel(mod, c) is { } top) return top;
        foreach (var p in parents)
        {
            if (p == c) continue;
            // The unique fitting slot, else the first named one (a cell holds placed refs in two).
            foreach (var slot in new string?[] { null }.Concat(p.GetProperties().Select(x => x.Name)))
            {
                if (!WriteEngine.CanCreateNested(c.Name, p, slot, out _)) continue;
                if (NewTopLevel(mod, p) is not { } parent) break;
                return WriteEngine.NestedAddNew(mod, parent, c.Name, slot, null);
            }
        }
        return null;
    }

    /// <summary>A record that needs no parent: an interior cell, or a flat or abstract-arm record; null for anything else.</summary>
    static IMajorRecord? NewTopLevel(SkyrimMod mod, Type c) =>
        c == typeof(Cell) ? WriteEngine.AddInteriorCell(mod, null)
        : WriteEngine.CanCreateType(c.Name, out _) ? WriteEngine.GenericAddNew(mod, c.Name, null) : null;
}

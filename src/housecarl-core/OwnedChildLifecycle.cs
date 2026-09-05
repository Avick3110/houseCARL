using System.Reflection;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

/// <summary>
/// The WRITE side of the owned-child shape: finding the slot on a parent that HOLDS a given child record, and
/// detaching the child from it. <see cref="OwnedChildContent"/> answers the same question for the read side (what
/// shape is this field, does it declare a child); this answers the one a delete needs — WHICH record holds this
/// child, and in which of its slots.
///
/// <para><b>Why a delete needs it at all.</b> Mutagen's typed <c>Remove(FormKey, Type)</c> is the blessed drop for
/// every record in a group, flat or nested, and it reaches placed references and INFOs. It does NOT reach a
/// SINGULAR owned child: a cell's <c>Landscape</c> and a worldspace's <c>TopCell</c> are plain properties on their
/// parent, not entries in any group, so the remove routing finds nothing to drop and returns without throwing —
/// the silent no-op <see cref="WritePatchBuilder"/>'s survivor check exists to catch. The record is reachable
/// (Mutagen's containment walk enumerates it) but not removable by FormKey alone. The slot is what removes it.</para>
///
/// <para><b>The slot set is not a hand list.</b> It is <see cref="WriteEngine.ChildBearingProperties"/> — the same
/// reflected, recursive walk the forward lanes preserve children with and the read side classifies with — so a
/// Mutagen bump that adds a child-bearing property is covered here without an edit. Nothing in this file names a
/// record type.</para>
/// </summary>
public static class OwnedChildLifecycle
{
    /// <summary>Where a child record sits: the parent that owns it, the settable property on that parent, and how
    /// that property holds it. <see cref="Container"/> is the live <c>IList</c> the child is an element of for a
    /// <see cref="OwnedChildShape.Collection"/> slot — possibly nested inside the property rather than being it, as
    /// a worldspace's cells sit under block structs — and null for a singular one.</summary>
    public readonly record struct OwnedChildSlot(
        IMajorRecord Parent, PropertyInfo Property, OwnedChildShape Shape,
        System.Collections.IList? Container, IMajorRecord Child)
    {
        /// <summary>The slot named the way a message names it: the parent's type and the property.</summary>
        public string Describe() => $"{Parent.GetType().Name}.{Property.Name}";
    }

    /// <summary>Find the slot in <paramref name="mod"/> that holds <paramref name="child"/>, if any. Returns false
    /// when no record in the mod holds it in a child-bearing slot — which is the ordinary answer for the
    /// overwhelming majority of records, since only a handful of types own children at all.
    ///
    /// <para>The scan walks the mod's own records and asks each one's child-bearing properties, so it is bounded by
    /// the records that CAN own children rather than by the mod's size — every other type contributes an empty
    /// property set and is skipped without reading a field.</para></summary>
    public static bool TryFindSlot(IMajorRecordEnumerable mod, FormKey child, out OwnedChildSlot slot)
    {
        slot = default;
        foreach (var parent in mod.EnumerateMajorRecords())
        {
            var props = WriteEngine.ChildBearingProperties(parent.GetType());
            if (props.Count == 0) continue;
            foreach (var p in props)
            {
                object? value;
                // A property that throws on read is not a slot we can speak for. Skipping it cannot hide a child:
                // the caller's survivor check still refuses if the record is still there afterwards.
                try { value = p.GetValue(parent); } catch { continue; }
                if (value is null) continue;
                // SINGULAR: the property IS the child.
                if (value is IMajorRecord single)
                {
                    if (single.FormKey != child) continue;
                    slot = new OwnedChildSlot(parent, p, OwnedChildShape.Singular, null, single);
                    return true;
                }
                // COLLECTION: the child is an element somewhere under the property. It may not be an element OF the
                // property — a worldspace's cells sit two container levels down, under block structs — so the walk
                // descends to the IList that actually holds it, which is the list a delete has to remove from.
                if (FindInContainer(value, child, 0) is { } hit)
                {
                    slot = new OwnedChildSlot(parent, p, OwnedChildShape.Collection, hit.List, hit.Child);
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>The deepest container nesting the search follows — the same bound
    /// <see cref="WriteEngine.ChildBearingProperties"/>'s reachability walk uses, for the same reason: the deepest
    /// real path is a worldspace's cells at five hops, so this is a tripwire for a shape nobody has seen rather
    /// than a limit the model approaches. Overrunning it means the child is simply not found, and the caller's
    /// survivor check then refuses loud — the failure is a refusal, never a silent skip.</summary>
    const int MaxDepth = 6;

    static (System.Collections.IList List, IMajorRecord Child)? FindInContainer(object? value, FormKey child, int depth)
    {
        if (value is null || depth > MaxDepth) return null;
        if (value is IFormLinkGetter) return null;             // a reference, not a child — the same cut the type walk makes
        if (value is string) return null;
        if (value is System.Collections.IList list)
        {
            // This list itself, first: an element that IS the child is the slot, and descending past it would find
            // a deeper list that does not hold it.
            for (int i = 0; i < list.Count; i++)
                if (list[i] is IMajorRecord rec && rec.FormKey == child) return (list, rec);
            for (int i = 0; i < list.Count; i++)
                if (FindInContainer(list[i], child, depth + 1) is { } deeper) return deeper;
            return null;
        }
        if (value is System.Collections.IEnumerable seq)
        {
            foreach (var item in seq)
                if (FindInContainer(item, child, depth + 1) is { } deeper) return deeper;
            return null;
        }
        // A non-collection intermediate (a block struct) — descend its own properties, which is how a worldspace's
        // cells are reached at all.
        var t = value.GetType();
        if (value is IMajorRecord || !t.IsClass || t.Namespace?.StartsWith("Mutagen", StringComparison.Ordinal) != true)
            return null;
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || p.GetIndexParameters().Length != 0) continue;
            object? v;
            try { v = p.GetValue(value); } catch { continue; }
            if (FindInContainer(v, child, depth + 1) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>Detach the child in <paramref name="slot"/> from its parent — clear the property for a singular
    /// slot, drop the element for a collection one. Returns null on success, else why it could not, so the caller
    /// refuses with nothing written rather than serializing a file it cannot account for.</summary>
    public static string? Detach(OwnedChildSlot slot)
    {
        try
        {
            if (slot.Shape == OwnedChildShape.Singular)
            {
                if (!slot.Property.CanWrite)
                    return $"'{slot.Describe()}' holds {slot.Child.FormKey} but is not settable, so the child cannot " +
                           "be detached from its parent — surfaced, not swallowed (Q3).";
                slot.Property.SetValue(slot.Parent, null);
                return null;
            }
            if (slot.Container is null)
                return $"'{slot.Describe()}' holds {slot.Child.FormKey} in no list this engine can drop it from — " +
                       "surfaced, not swallowed (Q3).";
            slot.Container.Remove(slot.Child);
            return null;
        }
        catch (Exception ex)
        {
            return $"detaching {slot.Child.FormKey} from '{slot.Describe()}' threw ({ex.GetType().Name}: " +
                   $"{ex.Message}) — surfaced, not swallowed (Q3).";
        }
    }
}

using System.Reflection;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

/// <summary>The WRITE side of the owned-child shape: finding the slot on a parent that HOLDS a given child record, and detaching it; contracts in docs/architecture/records-owned-child-declarers.md.</summary>
public static class OwnedChildLifecycle
{
    /// <summary>Where a child record sits: the parent, the settable property, and the live list the child is an element of for a collection slot (null for a singular one).</summary>
    public readonly record struct OwnedChildSlot(
        IMajorRecord Parent, PropertyInfo Property, OwnedChildShape Shape,
        System.Collections.IList? Container, IMajorRecord Child)
    {
        /// <summary>The slot named the way a message names it: the parent's type and the property.</summary>
        public string Describe() => $"{Parent.GetType().Name}.{Property.Name}";
    }

    /// <summary>Find the slot in <paramref name="mod"/> that holds <paramref name="child"/>, if any; the scan is bounded by the records that CAN own children, not by the mod's size.</summary>
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
                // A property that throws on read is not a slot we can speak for; the caller's survivor check still refuses.
                try { value = p.GetValue(parent); } catch { continue; }
                if (value is null) continue;
                // SINGULAR: the property IS the child.
                if (value is IMajorRecord single)
                {
                    if (single.FormKey != child) continue;
                    slot = new OwnedChildSlot(parent, p, OwnedChildShape.Singular, null, single);
                    return true;
                }
                // COLLECTION: the child may sit below the property, so the walk descends to the IList that actually holds it.
                if (FindInContainer(value, child, 0) is { } hit)
                {
                    slot = new OwnedChildSlot(parent, p, OwnedChildShape.Collection, hit.List, hit.Child);
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>The child already in the SINGULAR slot <paramref name="slotName"/> on <paramref name="parent"/>, or null when the slot is free, unmodelled, or throws on read.</summary>
    public static IMajorRecordGetter? OccupantOf(IMajorRecordGetter parent, string slotName)
    {
        var p = parent.GetType().GetProperty(slotName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (p is null || p.GetIndexParameters().Length != 0) return null;
        try { return p.GetValue(parent) as IMajorRecordGetter; } catch { return null; }
    }

    /// <summary>The records <paramref name="child"/> carries under it — what a detach takes with it, and what makes an unnamed descendant a refusal rather than a side effect.</summary>
    public static IReadOnlyList<IMajorRecordGetter> DescendantsOf(IMajorRecordGetter child) =>
        child is IMajorRecordGetterEnumerable e
            ? e.EnumerateMajorRecords().Where(r => r.FormKey != child.FormKey).ToList()
            : Array.Empty<IMajorRecordGetter>();

    /// <summary>The deepest container nesting the search follows — a tripwire; overrunning it means the child is not found and the caller's survivor check then refuses loud.</summary>
    const int MaxDepth = 6;

    static (System.Collections.IList List, IMajorRecord Child)? FindInContainer(object? value, FormKey child, int depth)
    {
        if (value is null || depth > MaxDepth) return null;
        if (value is IFormLinkGetter) return null;             // a reference, not a child — the same cut the type walk makes
        if (value is string) return null;
        if (value is System.Collections.IList list)
        {
            // This list itself first: descending past an element that IS the child would find a deeper list that does not hold it.
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
        // A non-collection intermediate (a block struct) — descend its own properties.
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

    /// <summary>Detach the child in <paramref name="slot"/> from its parent; returns null on success, else why it could not, so the caller refuses with nothing written.</summary>
    public static string? Detach(OwnedChildSlot slot)
    {
        try
        {
            if (slot.Shape == OwnedChildShape.Singular)
            {
                if (!slot.Property.CanWrite)
                    return $"'{slot.Describe()}' holds {FormIdToken.Of(slot.Child.FormKey)} but is not settable, so the child cannot " +
                           "be detached from its parent — surfaced, not swallowed (Q3).";
                slot.Property.SetValue(slot.Parent, null);
                return null;
            }
            if (slot.Container is null)
                return $"'{slot.Describe()}' holds {FormIdToken.Of(slot.Child.FormKey)} in no list this engine can drop it from — " +
                       "surfaced, not swallowed (Q3).";
            slot.Container.Remove(slot.Child);
            return null;
        }
        catch (Exception ex)
        {
            return $"detaching {FormIdToken.Of(slot.Child.FormKey)} from '{slot.Describe()}' threw ({ex.GetType().Name}: " +
                   $"{ex.Message}) — surfaced, not swallowed (Q3).";
        }
    }
}

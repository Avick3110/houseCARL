using System.Collections.Concurrent;
using System.Reflection;
using Mutagen.Bethesda.Plugins;

namespace HousecarlCore;

/// <summary>The one place a FormLink's shape is judged and built: nullable or required, absent, present zero or a key.</summary>
internal static class FormLinkShape
{
    static readonly ConcurrentDictionary<Type, (ConstructorInfo? Ctor, bool Nullable)> _ctorOf = new();
    static readonly ConcurrentDictionary<Type, MethodInfo?> _setToOf = new();

    /// <summary>True iff <paramref name="t"/> is or implements <c>IFormLinkNullableGetter&lt;T&gt;</c>, setter, getter and overlay alike.</summary>
    public static bool IsNullable(Type t) => WriteEngine.ClosedInterface(t, typeof(IFormLinkNullableGetter<>)) is not null;

    /// <summary>True iff <paramref name="t"/> is a REQUIRED (non-nullable) FormLink-family type.</summary>
    public static bool IsRequired(Type t)
    {
        if (!t.IsGenericType) return false;
        var def = t.GetGenericTypeDefinition();
        return def == typeof(FormLink<>) || def == typeof(IFormLink<>) || def == typeof(IFormLinkGetter<>);
    }

    /// <summary>A new link of <paramref name="t"/>'s family holding <paramref name="key"/>; a null key is absent on a nullable link and zero on a required one; null for a non-link type.</summary>
    public static object? Make(Type t, FormKey? key)
    {
        var (ctor, nullable) = _ctorOf.GetOrAdd(t, static t =>
            !t.IsGenericType ? (null, false)
            : IsNullable(t) ? (typeof(FormLinkNullable<>).MakeGenericType(t.GetGenericArguments()[0]).GetConstructor(new[] { typeof(FormKey?) }), true)
            : IsRequired(t) ? (typeof(FormLink<>).MakeGenericType(t.GetGenericArguments()[0]).GetConstructor(new[] { typeof(FormKey) }), false)
            : (null, false));
        return ctor?.Invoke(new object?[] { nullable ? key : key ?? FormKey.Null });
    }

    /// <summary>Point a live link at <paramref name="key"/>, or leave it absent for null; false when it has no SetTo or is required and asked to be absent.</summary>
    public static bool TrySetTo(object link, FormKey? key)
    {
        var t = link.GetType();
        if (key is null && !IsNullable(t)) return false;
        var m = _setToOf.GetOrAdd(t, static t => t.GetMethod("SetTo", new[] { typeof(FormKey?) }));
        if (m is null) return false;
        m.Invoke(link, new object?[] { key });
        return true;
    }
}

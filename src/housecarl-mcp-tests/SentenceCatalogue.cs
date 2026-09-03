using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// Marks a class as a SENTENCE-CATALOGUE test — one that is about the catalogue's own members rather than
/// about a tool response.
///
/// <para>The marker is hand-applied, and it buys no exemption. A marked class's assertions are still
/// classified and counted by <see cref="TestProseGuardTests"/>' prose rule exactly like any other file's.
/// What the marker adds is one further arm there: inside a marked class, an assertion whose SUBJECT resolves
/// to an expression rooted at a name the product declares — other than a <c>*Sentences</c> catalogue — fails,
/// because that subject is a tool response and the class claims not to be about one.</para>
///
/// <para>That arm reads the subject syntactically, with a single hop of local resolution, so it fires on a
/// product-type-rooted subject and passes over a subject rooted at a local it cannot follow. It is a check on
/// the ordinary shape, not a proof that a fact test cannot be written inside a marked class.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class SentenceCatalogueAttribute : Attribute { }

/// <summary>
/// The sentence catalogue's own member set, DERIVED by reflection — never a list in a test file.
///
/// <para><b>Why derived.</b> A hand-listed population is short by exactly what nobody thought of, and the
/// guard over it stays green across the gap (CLAUDE.md §3, third cornerstone). A sentence added to
/// <c>ReadSentences</c> without a second spelling has to fail by NAME, which only a derived population can
/// do.</para>
///
/// <para><b>Why it is loud on a shape it cannot read.</b> This is the read surface's missing
/// <c>TwinShapesUnreadable</c>. The old net in <c>OwnedChildContentProbe.SentenceViolations</c> walks
/// <c>GetFields</c> and silently ignores everything else, and the eight composers are the measured
/// consequence — six of them were pinned by nothing at all. A member shape this class cannot classify is
/// NAMED and FAILED, never filtered away.</para>
///
/// <para><b>The classification, stated once.</b> Over the type's own declared, non-private members:
/// a static FIELD that is <c>const</c> or <c>readonly</c> is a VALUE (pinned by its value); a static METHOD
/// is a COMPOSER (pinned by invoking it with fixture-known arguments). Everything else — a property, an
/// event, a nested type, an instance member, a mutable static field — is unreadable to this net and fails.
/// Private members are implementation, not catalogue: they are reachable only through a member above, and
/// they are pinned through it.</para>
/// </summary>
static class SentenceCatalogue
{
    public enum Shape { Value, Composer }

    public sealed record Member(string Name, Shape Kind, FieldInfo? Field, MethodInfo? Method)
    {
        public Type Type => Field?.FieldType ?? Method!.ReturnType;
    }


    const BindingFlags AnyDeclared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
      | BindingFlags.DeclaredOnly;

    // Concurrent because three test CLASSES ask for this population and xUnit runs classes in parallel:
    // ReadSentenceWordingTests, ReadSentenceReachabilityTests and OwnedChildRemedyGrammarTests.
    static readonly ConcurrentDictionary<Type, IReadOnlyList<Member>> Cache = new();

    /// <summary>Every member of the catalogue <paramref name="t"/>, classified — or a loud failure naming the
    /// member and the shape this net cannot pin.</summary>
    public static IReadOnlyList<Member> Members(Type t)
    {
        if (Cache.TryGetValue(t, out var got)) return got;

        var members = new List<Member>();
        var unreadable = new List<string>();

        foreach (var f in t.GetFields(AnyDeclared))
        {
            if (f.IsPrivate || f.IsDefined(typeof(CompilerGeneratedAttribute), false)) continue;
            if (!f.IsStatic) { unreadable.Add($"{f.Name}: an INSTANCE field — a catalogue has no instances to read it off"); continue; }
            if (!f.IsLiteral && !f.IsInitOnly) { unreadable.Add($"{f.Name}: a MUTABLE static field — its value at assert time is not its value at render time"); continue; }
            members.Add(new Member(f.Name, Shape.Value, f, null));
        }

        foreach (var m in t.GetMethods(AnyDeclared))
        {
            if (m.IsPrivate || m.IsSpecialName
             || m.IsDefined(typeof(CompilerGeneratedAttribute), false)) continue;
            if (!m.IsStatic) { unreadable.Add($"{m.Name}: an INSTANCE method — a catalogue has no instances to call it on"); continue; }
            members.Add(new Member(m.Name, Shape.Composer, null, m));
        }

        foreach (var p in t.GetProperties(AnyDeclared))
            unreadable.Add($"{p.Name}: a PROPERTY — its body can compute anything, so its value is not a sentence this net can pin");

        foreach (var e in t.GetEvents(AnyDeclared))
            unreadable.Add($"{e.Name}: an EVENT — not a sentence");

        foreach (var n in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            if (!n.IsDefined(typeof(CompilerGeneratedAttribute), false))
                unreadable.Add($"{n.Name}: a NESTED TYPE — if it carries sentences it is its own catalogue and needs its own pins");

        Assert.True(unreadable.Count == 0,
            $"{t.Name} carries member shapes this net cannot pin:\n  " + string.Join("\n  ", unreadable) +
            "\nThe net FAILS on a shape it cannot read rather than filtering it away — filtering is how six of " +
            "the eight composers on this catalogue ended up pinned by nothing at all. Either give the shape a " +
            "reading here, or move it off the catalogue.");

        var dupes = members.GroupBy(x => x.Name, StringComparer.Ordinal).Where(g => g.Count() > 1)
                           .Select(g => g.Key).ToArray();
        Assert.True(dupes.Length == 0,
            $"{t.Name} declares more than one member named: {string.Join(", ", dupes)} — an overload set cannot be " +
            "pinned by name alone.");

        var list = members.OrderBy(x => x.Name, StringComparer.Ordinal).ToList();
        Assert.True(list.Count > 0,
            $"{t.Name} derived NO members at all, so every claim over this population is vacuous. The reflection " +
            "found nothing — a renamed type, a changed accessibility, or a binding-flag mistake — and that is " +
            "this net's subject, not a reason to pass.");

        Cache[t] = list;
        return list;
    }

    public static IReadOnlyList<string> MemberNames(Type t) => Members(t).Select(m => m.Name).ToList();

    public static Member Get(Type t, string name)
    {
        var m = Members(t).SingleOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal));
        Assert.True(m is not null, $"{t.Name} has no member named '{name}'.");
        return m!;
    }

    /// <summary>The VALUE of a value-shaped member.</summary>
    public static object? Value(Type t, string name)
    {
        var m = Get(t, name);
        Assert.True(m.Kind == Shape.Value, $"{t.Name}.{name} is a composer; invoke it rather than reading it.");
        return m.Field!.GetValue(null);
    }

    /// <summary>The RESULT of a composer, invoked with fixture-known arguments.</summary>
    public static object? Invoke(Type t, string name, object?[] args)
    {
        var m = Get(t, name);
        Assert.True(m.Kind == Shape.Composer, $"{t.Name}.{name} is a value; read it rather than invoking it.");

        var ps = m.Method!.GetParameters();
        Assert.True(ps.Length == args.Length,
            $"{t.Name}.{name} takes {ps.Length} argument(s) ({string.Join(", ", ps.Select(p => p.ParameterType.Name + " " + p.Name))}) " +
            $"and the row for it supplies {args.Length}. A composer whose argument row no longer fits its signature " +
            "is a composer nothing is pinning.");

        return m.Method.Invoke(null, args);
    }

    /// <summary>The declared-only, non-private static fields of <paramref name="t"/> whose type is
    /// <c>string</c> — the population the <c>[MustState]</c> / <c>[NoClaims]</c> content net is about.</summary>
    public static IReadOnlyList<FieldInfo> SentenceFields(Type t) =>
        Members(t).Where(m => m.Kind == Shape.Value && m.Type == typeof(string))
                  .Select(m => m.Field!)
                  .ToList();

}

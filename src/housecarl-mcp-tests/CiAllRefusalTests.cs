using System.Reflection;
using System.Reflection.Emit;
using HousecarlCore;
using HousecarlGenerator;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// <see cref="CiAll"/>'s two written refusals, and the reason they can be read.
///
/// <para>Both are sentences a person has to act on: a guard whose entry point the runner cannot dispatch, and
/// two guards claiming one CI verb. Neither fires on a green repo, so the fixture population is emitted here
/// — a dynamic assembly carrying the offending shapes — and handed to the same scan the runner uses. The
/// derivation of the SHIPPED population is untouched: it is still the runner's own reference closure.</para>
/// </summary>
[Trait("tier", "unit")]
public sealed class CiAllRefusalTests
{
    /// <summary>An assembly with one <c>[CiProbe(verb)]</c> entry point per name in <paramref name="hosts"/>.</summary>
    static Assembly FixtureAssembly(string verb, params string[] hosts) => FixtureAssembly(verb, wellFormed: true, hosts);

    static Assembly FixtureAssembly(string verb, bool wellFormed, params string[] hosts)
    {
        var asm = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("hcfold-ciall-fixture-" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
        var module = asm.DefineDynamicModule("m");
        var probeCtor = typeof(CiProbeAttribute).GetConstructor(new[] { typeof(string) })!;

        foreach (var host in hosts)
        {
            var type = module.DefineType(host, TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);

            // Well formed is `public static int <Name>(string[] args)`. The malformed shape returns void and
            // takes nothing, which is the entry point the runner cannot dispatch.
            var method = wellFormed
                ? type.DefineMethod("Run", MethodAttributes.Public | MethodAttributes.Static,
                                    typeof(int), new[] { typeof(string[]) })
                : type.DefineMethod("Run", MethodAttributes.Public | MethodAttributes.Static,
                                    typeof(void), Type.EmptyTypes);

            method.SetCustomAttribute(new CustomAttributeBuilder(probeCtor, new object[] { verb }));

            var il = method.GetILGenerator();
            if (wellFormed) il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ret);

            type.CreateType();
        }

        return asm;
    }

    /// <summary>
    /// The verb clash arrives as the InvalidOperationException it was written as, naming both hosts — not
    /// wrapped in a TypeInitializationException, which is what a static field initializer made of it.
    /// </summary>
    [Fact]
    public void TwoGuardsClaimingOneVerbRefuseWithBothHostsNamed_NotAsATypeInitializerFailure()
    {
        const string verb = "hcfold-fixture-clash-verb";
        var fixture = FixtureAssembly(verb, "HcFoldClashFixtureFirst", "HcFoldClashFixtureSecond");

        var ex = Assert.Throws<InvalidOperationException>(() => CiAll.RosterIn(new[] { fixture }));

        Assert.Contains(verb, ex.Message, StringComparison.Ordinal);
        Assert.Contains("HcFoldClashFixtureFirst", ex.Message, StringComparison.Ordinal);
        Assert.Contains("HcFoldClashFixtureSecond", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The other written refusal, the same way: a guard the runner cannot call.</summary>
    [Fact]
    public void AnEntryPointTheRunnerCannotDispatchRefusesWithItsOwnSentence()
    {
        var fixture = FixtureAssembly("hcfold-fixture-bad-signature", wellFormed: false, "HcFoldBadSignatureFixture");

        var ex = Assert.Throws<InvalidOperationException>(() => CiAll.RosterIn(new[] { fixture }));

        Assert.Contains("HcFoldBadSignatureFixture", ex.Message, StringComparison.Ordinal);
        Assert.Contains("public static int", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing on <see cref="CiAll"/> is built by the type initializer.
    ///
    /// <para>A static field that is <c>initonly</c> can only be filled there, and a refusal thrown out of a
    /// type initializer is wrapped in a TypeInitializationException that the CLR caches against the type for
    /// the process: the sentence is buried in an inner exception, and every later test touching the class
    /// fails with the same wrapper instead of its own measurement. So the invariant is the absence, derived —
    /// not a list of the members we remembered to make lazy.</para>
    /// </summary>
    [Fact]
    public void NoStaticOnCiAllIsFilledByTheTypeInitializer_SoARefusalReachesTheCallerAsItself()
    {
        var eager = typeof(CiAll)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.IsInitOnly)          // a const is IsLiteral, not IsInitOnly: it runs no code
            .Select(f => $"{f.FieldType.Name} {f.Name}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        Assert.True(eager.Length == 0,
            "These statics on CiAll are filled by its type initializer: " + string.Join(", ", eager) +
            ". Discovery has two written refusals, and one thrown from there arrives as " +
            "TypeInitializationException — cached against the type, so every later caller gets the wrapper " +
            "rather than the sentence. Cache on first read instead (`static T[] X() => _x ??= …`).");
    }
}

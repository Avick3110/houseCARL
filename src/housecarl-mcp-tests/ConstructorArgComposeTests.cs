using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// Composing an arm whose type has NO parameterless constructor — a MagicEffect's plain
/// <c>MagicEffectArchetype</c>, which Mutagen builds as <c>MagicEffectArchetype(TypeEnum)</c> and which is the only
/// way to author a Script-archetype effect from scratch (#563). The compose used to be accepted by pre-flight and
/// then throw at apply, leaving forking an existing effect as the only route.
///
/// <para>Every call here is a dry run: the shared world must stay unwritten. The round trip that proves the arm
/// actually lands lives in <see cref="ConstructorArgComposeWriteTests"/>, which builds its own instance.</para>
/// </summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class ConstructorArgComposeTests : RecordsTestBase
{
    public ConstructorArgComposeTests(RecordsFixture f) : base(f) { }

    string Compose(string body) => ApplyTools.Apply(Svc,
        ops: Je($@"[{{""formid"":""{Fid(W.MgefA)}"",""field_path"":""Archetype"",""op"":""Set"",""compose"":{{{body}}}}}]"),
        dry_run: true);

    /// <summary>The call the report made: the arm named, its constructor argument named as an ordinary field.</summary>
    [Fact]
    public void AnArmBuiltFromItsConstructorIsComposedByNamingThatArgumentAsAField()
        => Served(Compose(@"""type"":""MagicEffectArchetype"",""fields"":{""Type"":""Script""}"),
                  "Set Archetype", "MagicEffectArchetype");

    /// <summary>…and it is the COMPOSE that builds it, not an accept followed by a throw. The engine labels a
    /// gate/apply disagreement in its own words; none may appear here.</summary>
    [Fact]
    public void TheComposeDoesNotReachTheApplyAsAnInconsistency()
    {
        var r = Compose(@"""type"":""MagicEffectArchetype"",""fields"":{""Type"":""Script""}");
        Assert.DoesNotContain("pre-flight ACCEPTED it but the apply threw", r);
        Assert.DoesNotContain("no parameterless constructor", r);
    }

    /// <summary>Another field alongside the constructor argument is still set on the built arm.</summary>
    [Fact]
    public void AFieldBesideTheConstructorArgumentIsStillPartOfTheCompose()
        => Served(Compose(@"""type"":""MagicEffectArchetype"",""fields"":{""Type"":""Script"",""ActorValue"":""Health""}"),
                  "Set Archetype");

    /// <summary>The positional lane that already worked keeps working — it is what the fields lane falls back to.</summary>
    [Fact]
    public void PositionalConstructorArgsStillBuildTheSameArm()
        => Served(Compose(@"""type"":""MagicEffectArchetype"",""ctor_args"":[""Script""]"), "Set Archetype");

    /// <summary>A compose that never names the constructor argument cannot be built, and is refused BEFORE anything
    /// is written — naming the field to add.</summary>
    [Fact]
    public void AComposeMissingTheConstructorArgumentIsRefusedAtPreFlight()
    {
        var r = Compose(@"""type"":""MagicEffectArchetype"",""fields"":{""ActorValue"":""Health""}");
        Refused(r, "no parameterless constructor", "'Type'", "fields=", "ctor_args");
        Assert.Contains("NO patch written", r);
    }

    /// <summary>The constructor argument has to be a FIELD: nested sets run against the already-built instance, so
    /// one there cannot supply what the constructor needs. The refusal says which slot to move it to.</summary>
    [Fact]
    public void TheConstructorArgumentInNestedSetsIsRefusedAndSentToFields()
        => Refused(Compose(@"""type"":""MagicEffectArchetype"",""sets"":[{""path"":""Type"",""value"":""Script""}]"),
                   "no parameterless constructor", "fields=");

    /// <summary>An arm that HAS a parameterless constructor is untouched by any of this.</summary>
    [Fact]
    public void AnArmWithAParameterlessConstructorComposesAsBefore()
        => Served(Compose(@"""type"":""MagicEffectCloakArchetype"",""fields"":{""ActorValue"":""Health""}"),
                  "MagicEffectCloakArchetype");
}

/// <summary>
/// What the composed arm actually IS, and what a plugin carrying it says on disk — the half the dry runs above
/// cannot see. World-free: the write engine takes a record, and Mutagen serializes a mod to a file, so a synthetic
/// mod is the whole world these need.
/// </summary>
[Trait("tier", "unit")]
public sealed class ConstructorArgComposeWriteTests
{
    static MagicEffect Effect(out SkyrimMod mod)
    {
        mod = new SkyrimMod(new ModKey("HcCtorCompose", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var mgef = mod.MagicEffects.AddNew();
        mgef.EditorID = "HcCtorComposeEffect";
        return mgef;
    }

    /// <summary>The archetype's discriminator: declared on the getter interface, so the mutable class is read
    /// through it rather than by casting to one particular arm.</summary>
    static MagicEffectArchetype.TypeEnum ArchetypeType(IAMagicEffectArchetypeGetter archetype) => archetype.Type;

    static WriteRequest SetArchetype(StructSpec arm) => new()
    {
        RecordType = "MagicEffect", Path = new[] { "Archetype" }, Verb = "Set", Struct = arm,
    };

    /// <summary>The constructor argument named as a field IS the discriminator on the built arm.</summary>
    [Fact]
    public void TheFieldNamingTheConstructorArgumentBecomesTheArmsDiscriminator()
    {
        var mgef = Effect(out _);
        WriteEngine.ApplyVerb(mgef, SetArchetype(new StructSpec
        {
            Type = "MagicEffectArchetype", Fields = new() { ["Type"] = "Script" },
        }));

        Assert.Equal(MagicEffectArchetype.TypeEnum.Script, ArchetypeType(mgef.Archetype));
    }

    /// <summary>A field beside the constructor argument lands too — the constructor consumes its own and leaves the
    /// rest to the ordinary field pass.</summary>
    [Fact]
    public void TheOtherFieldsOfSuchAComposeStillLand()
    {
        var mgef = Effect(out _);
        WriteEngine.ApplyVerb(mgef, SetArchetype(new StructSpec
        {
            Type = "MagicEffectArchetype", Fields = new() { ["Type"] = "Script", ["ActorValue"] = "Health" },
        }));

        Assert.Equal(MagicEffectArchetype.TypeEnum.Script, ArchetypeType(mgef.Archetype));
        Assert.Equal(ActorValue.Health, mgef.Archetype.ActorValue);
    }

    /// <summary>…and the effect written to a plugin reads back as a Script effect, which is the thing #563 could not
    /// produce without forking one that already existed.</summary>
    [Fact]
    public void TheEffectSerializesAndReadsBackAsScriptArchetype()
    {
        var mgef = Effect(out var mod);
        WriteEngine.ApplyVerb(mgef, SetArchetype(new StructSpec
        {
            Type = "MagicEffectArchetype", Fields = new() { ["Type"] = "Script" },
        }));

        var dir = Path.Combine(Path.GetTempPath(), "hc-ctor-compose-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "HcCtorCompose.esp");
            mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            using var back = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            var read = Assert.Single(back.MagicEffects);
            Assert.Equal(MagicEffectArchetype.TypeEnum.Script, read.Archetype.Type);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

/// <summary>
/// WHICH constructor the fields lane picks, and whether the apply invokes that one. The chooser matches parameters
/// by name; an apply that re-derived the constructor from the argument COUNT would agree with it only while no
/// candidate type has two constructors of the same arity — and then build a different overload than the gate
/// validated. Synthetic types, because the shape is about constructor overloads and not about any record.
/// </summary>
[Trait("tier", "unit")]
public sealed class ConstructorSelectionTests
{
    // Two one-argument constructors, declared in both orders across the two types, so the test does not depend on
    // the order reflection happens to report constructors in: whichever order that is, one of these picks wrong
    // under arity-alone selection.
    public sealed class IntFirst
    {
        public string Chosen { get; }
        public IntFirst(int alpha) => Chosen = "alpha";
        public IntFirst(string beta) => Chosen = "beta";
    }

    public sealed class StringFirst
    {
        public string Chosen { get; }
        public StringFirst(string beta) => Chosen = "beta";
        public StringFirst(int alpha) => Chosen = "alpha";
    }

    /// <summary>The constructor a field NAMES is the one invoked, not another of the same arity.</summary>
    [Theory]
    [InlineData(typeof(IntFirst))]
    [InlineData(typeof(StringFirst))]
    public void TheConstructorTheFieldsNameIsTheOneInvoked(Type type)
    {
        var built = WriteEngine.BuildFromFieldConstructor(type, new Dictionary<string, string> { ["Beta"] = "hello" });

        Assert.Equal("beta", type.GetProperty("Chosen")!.GetValue(built));
    }
}

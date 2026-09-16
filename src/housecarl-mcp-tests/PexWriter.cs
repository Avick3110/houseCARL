using Mutagen.Bethesda;
using Mutagen.Bethesda.Pex;

namespace HousecarlMcpTests;

/// <summary>Writes byte-valid Skyrim <c>.pex</c> files: one object with a chosen table of Auto properties, or
/// several bare objects.
/// A copy, not a project reference on the generator's own writer: the test project must not depend on it.
/// What the fixture uses it for: <c>docs/architecture/test-project-fixtures.md</c>.</summary>
public static class PexWriter
{
    /// <summary>One Auto property: the property record plus its <c>::Name_var</c> backing variable, which
    /// carries Null data for an object or an uninitialized scalar and the baked literal otherwise.</summary>
    public sealed record Decl(PexObjectProperty Prop, PexObjectVariable Backing);

    static Decl Auto(string name, string typeName, int? initInt)
    {
        // The initializer branch writes VariableType.Integer under the caller's declared TypeName, so only an
        // Int scalar can carry one. Why it refuses instead of documenting: docs/architecture/test-project-fixtures.md.
        if (initInt is int bad && !string.Equals(typeName, "Int", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"a baked initializer was given for '{name}', declared '{typeName}', with value {bad}: this writer "
                + "only bakes Integer initializers, because an Int scalar with a baked default is the only "
                + "declared-type/initializer pairing this fixture models. Writing it would pair VariableType.Integer "
                + $"with TypeName '{typeName}' — a shape no Papyrus compiler emits.",
                nameof(typeName));

        var prop = new PexObjectProperty
        {
            Name = name,
            TypeName = typeName,
            DocString = "",
            Flags = PropertyFlags.Read | PropertyFlags.Write | PropertyFlags.AutoVar,
            AutoVarName = $"::{name}_var",
        };
        var data = initInt is int v
            ? new PexObjectVariableData { VariableType = VariableType.Integer, IntValue = v }
            : new PexObjectVariableData { VariableType = VariableType.Null };
        var backing = new PexObjectVariable { Name = $"::{name}_var", TypeName = typeName, VariableData = data };
        return new Decl(prop, backing);
    }

    /// <summary>An Auto object/form property — no baked default, a FormID cannot be a literal.</summary>
    public static Decl AutoObj(string name, string typeName) => Auto(name, typeName, null);

    /// <summary>An Auto scalar property. A baked initializer is what stops the product reporting it
    /// unbound, so the two branches are not interchangeable.</summary>
    public static Decl AutoScalar(string name, string typeName, int? initInt) => Auto(name, typeName, initInt);

    /// <summary>Write a single-object .pex to <paramref name="path"/>. That it round-trips is asserted, not
    /// assumed: <c>ScriptsWorldTests.ThePlantedChildPexReadsBackWithItsAutoPropertyAndItsParentClass</c>.</summary>
    public static void WritePex(string path, string name, string? parent, params Decl[] decls)
    {
        var obj = new PexObject { Name = name, ParentClassName = parent ?? "", DocString = "", AutoStateName = "" };
        obj.States.Add(new PexObjectState { Name = "" });
        foreach (var d in decls) { obj.Properties.Add(d.Prop); obj.Variables.Add(d.Backing); }

        var pex = new PexFile(GameCategory.Skyrim)
        {
            MajorVersion = 3,
            MinorVersion = 2,
            GameId = 1,
            CompilationTime = default,
            SourceFileName = name + ".psc",
            Username = "hc",
            MachineName = "ci",
        };
        pex.Objects.Add(obj);
        pex.WritePexFile(path, GameCategory.Skyrim);
    }

    /// <summary>Write a .pex carrying several bare objects, in the order given — for the rules that read every
    /// object in a file before writing any of them.</summary>
    public static void WriteMultiObjectPex(string path, params string[] names)
    {
        var pex = new PexFile(GameCategory.Skyrim)
        {
            MajorVersion = 3,
            MinorVersion = 2,
            GameId = 1,
            CompilationTime = default,
            SourceFileName = names[0] + ".psc",
            Username = "hc",
            MachineName = "ci",
        };
        foreach (var name in names)
        {
            var obj = new PexObject { Name = name, ParentClassName = "", DocString = "", AutoStateName = "" };
            obj.States.Add(new PexObjectState { Name = "" });
            pex.Objects.Add(obj);
        }
        pex.WritePexFile(path, GameCategory.Skyrim);
    }
}

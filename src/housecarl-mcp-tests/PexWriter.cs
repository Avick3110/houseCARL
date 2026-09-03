using Mutagen.Bethesda;
using Mutagen.Bethesda.Pex;

namespace HousecarlMcpTests;

/// <summary>
/// Writes byte-valid single-object Skyrim <c>.pex</c> files with a chosen table of Auto properties. Ported
/// from <c>src/housecarl-generator/ScriptPropertyCheckProbe.cs</c> rather than referenced; why, and what
/// the fixture uses it for: <c>docs/architecture/test-project-fixtures.md</c>.
/// </summary>
public static class PexWriter
{
    /// <summary>One Auto property: the property record plus its <c>::Name_var</c> backing variable, which
    /// carries Null data for an object or an uninitialized scalar and the baked literal otherwise.</summary>
    public sealed record Decl(PexObjectProperty Prop, PexObjectVariable Backing);

    static Decl Auto(string name, string typeName, int? initInt)
    {
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
}

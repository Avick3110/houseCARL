// Ported from src/housecarl-generator/ScriptPropertyCheckProbe.cs (WritePex / Decl / AutoObj / AutoScalar).
//
// PORTED, not referenced: the generator's copy dies with the probe under #486 PR 2, and the scripts-family
// arms that PR writes need a .pex writer that outlives it. The two copies are expected to coexist only until
// that PR lands; this one is the survivor.
//
// The test project had NO Papyrus fixture of any kind before this file (#486 PR 1, item 1): nothing under
// src/housecarl-mcp-tests referenced Pex, VirtualMachineAdapter or ScriptEntry.

using Mutagen.Bethesda;
using Mutagen.Bethesda.Pex;

namespace HousecarlMcpTests;

/// <summary>
/// Writes byte-valid single-object Skyrim <c>.pex</c> files with a chosen table of Auto properties — the
/// fixture side of every script-property assertion. The product reads a record's VMAD, then reads the
/// attached script's compiled <c>.pex</c> (and its ancestors) to learn which properties were DECLARED; a
/// test can only state "declared but not bound" if it can plant a declaration on disk.
/// </summary>
public static class PexWriter
{
    /// <summary>One Auto property to plant in a .pex: the property record + its backing variable (every auto
    /// property has one, <c>::Name_var</c> — an object/scalar with no initializer carries Null data, an
    /// initialized scalar carries the baked literal). Modeled on a real Skyrim .pex: an Auto property is
    /// Flags = Read|Write|AutoVar with NO handler functions (the backing var IS the handler), a non-null
    /// DocString, and the autovar name set.</summary>
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

    /// <summary>An Auto object/form property (no baked default — a FormID can't be a literal).</summary>
    public static Decl AutoObj(string name, string typeName) => Auto(name, typeName, null);

    /// <summary>An Auto scalar property, optionally with a baked initializer on its backing variable.</summary>
    public static Decl AutoScalar(string name, string typeName, int? initInt) => Auto(name, typeName, initInt);

    /// <summary>Write a single-object .pex with the given Auto properties + backing variables to
    /// <paramref name="path"/> via Mutagen's native Pex writer. The object carries a non-null DocString, an
    /// empty auto-state, and the empty '' state — the minimal byte-valid shell a real Skyrim .pex has. That
    /// it round-trips is asserted, not assumed: see
    /// <c>ScriptsWorldTests.ThePlantedChildPexReadsBackWithItsAutoPropertyAndItsParentClass</c> (the probe's
    /// PEX-ROUNDTRIP arm, re-homed).</summary>
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
        pex.WritePexFile(path, GameCategory.Skyrim);   // Mutagen PexMixIn.WritePexFile(outputPath, gameCategory)
    }
}

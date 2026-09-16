using Mutagen.Bethesda;
using Mutagen.Bethesda.Pex;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// An expression the compiler evaluated and nothing ever reads — a discarded result. It comes back as a
/// bare expression statement, which PapyrusCompiler accepts and compiles back to the one instruction it
/// came from. The exception is a bare identifier or literal: the compiler emits nothing at all for one, so
/// emitting it would drop the instruction the value came from, and that stays a loud failure.
///
/// Each stream below is what the CK's own PapyrusCompiler emits for the source above it — checked by
/// compiling that source, reading the instruction back, and recompiling the decompiled output to a
/// byte-identical stream. CI has no CK compiler, so the streams are pinned here by hand.
/// </summary>
[Trait("tier", "unit")]
public class DecompileDiscardedExpressionTests
{
    [Fact]
    public void ADiscardedCastComesBackAsABareCastStatement()
    {
        // Function Probe(float modified)
        //     modified as int
        // EndFunction
        // The shape that fails vanilla `WEBountyCollectorScript.OnStoryScript`: a cast into a temp nothing reads.
        var f = Fn(("Float", "modified"));
        Local(f, "Int", "::temp0");
        Ins(f, InstructionOpcode.CAST, Id("::temp0"), Id("modified"));

        var res = PapyrusDecompiler.DecompileFile(File("HC_DiscardProbe", ("Probe", f)));

        Assert.Equal(0, res.FunctionsFailed);
        Assert.Contains("modified as int", res.Source);
    }

    [Fact]
    public void ADiscardedPropertyReadComesBackAsABarePropertyRead()
    {
        // Function Probe()
        //     Self.Room
        // EndFunction
        // The shape that fails `RN_Utility_PropManager._getDisplayRoom` and `nl_mcm_module.Get`.
        var f = Fn();
        Local(f, "Float", "::temp0");
        Ins(f, InstructionOpcode.PROPGET, Id("Room"), Id("self"), Id("::temp0"));

        var res = PapyrusDecompiler.DecompileFile(File("HC_DiscardProbe", ("Probe", f)));

        Assert.Equal(0, res.FunctionsFailed);
        Assert.Contains("Self.Room", res.Source);
    }

    [Fact]
    public void ADiscardedArrayReadComesBackAsABareIndexStatement()
    {
        // Function Probe(int[] nums)
        //     nums[0]
        // EndFunction
        var f = Fn(("Int[]", "nums"));
        Local(f, "Int", "::temp0");
        Ins(f, InstructionOpcode.ARRAY_GETELEMENT, Id("::temp0"), Id("nums"), Int(0));

        var res = PapyrusDecompiler.DecompileFile(File("HC_DiscardProbe", ("Probe", f)));

        Assert.Equal(0, res.FunctionsFailed);
        Assert.Contains("nums[0]", res.Source);
    }

    [Fact]
    public void ADiscardedPlainReadStaysALoudFailure()
    {
        // A copy into a temp nothing reads. Written out as `modified`, the compiler emits nothing, so the
        // instruction would be lost — the one discarded value that cannot come back as a statement.
        var f = Fn(("Float", "modified"));
        Local(f, "Float", "::temp0");
        Ins(f, InstructionOpcode.ASSIGN, Id("::temp0"), Id("modified"));

        var res = PapyrusDecompiler.DecompileFile(File("HC_DiscardProbe", ("Probe", f)));

        Assert.Equal(1, res.FunctionsFailed);
        Assert.Contains("::temp0", Assert.Single(res.Failures));
    }

    // ---------------------------------------------------------------- in-memory pex builders
    static PexFile File(string objectName, params (string Name, PexObjectFunction Fn)[] fns)
    {
        var state = new PexObjectState { Name = "" };
        foreach (var (name, fn) in fns)
            state.Functions.Add(new PexObjectNamedFunction { FunctionName = name, Function = fn });
        var obj = new PexObject { Name = objectName, ParentClassName = "", DocString = "", AutoStateName = "" };
        obj.States.Add(state);
        var pex = new PexFile(GameCategory.Skyrim) { MajorVersion = 3, MinorVersion = 2, GameId = 1 };
        pex.Objects.Add(obj);
        return pex;
    }

    static PexObjectFunction Fn(params (string Type, string Name)[] parameters)
    {
        var f = new PexObjectFunction { ReturnTypeName = "None", DocString = "" };
        foreach (var (type, name) in parameters)
            f.Parameters.Add(new PexObjectFunctionVariable { TypeName = type, Name = name });
        return f;
    }

    static void Local(PexObjectFunction f, string type, string name)
        => f.Locals.Add(new PexObjectFunctionVariable { TypeName = type, Name = name });

    static void Ins(PexObjectFunction f, InstructionOpcode op, params PexObjectVariableData[] args)
    {
        var ins = new PexObjectFunctionInstruction { OpCode = op };
        foreach (var a in args) ins.Arguments.Add(a);
        f.Instructions.Add(ins);
    }

    static PexObjectVariableData Id(string name)
        => new() { VariableType = VariableType.Identifier, StringValue = name };

    static PexObjectVariableData Int(int value)
        => new() { VariableType = VariableType.Integer, IntValue = value };
}

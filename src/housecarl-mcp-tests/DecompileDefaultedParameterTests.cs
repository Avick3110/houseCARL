using Mutagen.Bethesda;
using Mutagen.Bethesda.Pex;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// A .pex stores no parameter defaults. The compiler bakes an omitted `None` default as a RAW null argument
/// slot at the call site, and the reader re-omits a trailing run of those — so a function declared in the
/// same script came back with neither the default on its signature nor the argument at the call site, and
/// the source no longer compiled. The default is now declared from the same trailing run the call site
/// re-omits, so the two agree.
///
/// The streams below are what the CK's own PapyrusCompiler emits for the source above each one — checked by
/// compiling that source and by recompiling the decompiled output to a byte-identical stream. CI has no CK
/// compiler, so the streams are pinned here by hand.
/// </summary>
[Trait("tier", "unit")]
public class DecompileDefaultedParameterTests
{
    [Fact]
    public void AnOmittedTrailingArgumentPutsTheDefaultBackOnTheSignature()
    {
        // Function Defaulted(string label, HC_DefProbe f = None)
        // EndFunction
        // Function CallOmitted()
        //     Defaulted("x")
        // EndFunction
        var target = Fn(("String", "label"), ("HC_DefProbe", "f"));
        var caller = Fn();
        Local(caller, "None", "::NoneVar");
        Ins(caller, InstructionOpcode.CALLMETHOD, Id("Defaulted"), Id("self"), Id("::NoneVar"), Int(2), Str("x"), Null());

        var res = PapyrusDecompiler.DecompileFile(File("HC_DefProbe", ("Defaulted", target), ("CallOmitted", caller)));

        Assert.Equal(0, res.FunctionsFailed);
        Assert.Contains("HC_DefProbe f = None", res.Source);
        Assert.Contains("Defaulted(\"x\")", res.Source);
    }

    [Fact]
    public void ABakedNullThatIsNotLastLeavesTheSignatureAlone()
    {
        // The call site writes that one out as `None` rather than omitting it, so the two already agree and
        // a default there would be a defaulted parameter followed by an undefaulted one, which is not legal.
        var target = Fn(("String", "label"), ("HC_DefProbe", "f"), ("Int", "n"));
        var caller = Fn();
        Local(caller, "None", "::NoneVar");
        Ins(caller, InstructionOpcode.CALLMETHOD, Id("Mixed"), Id("self"), Id("::NoneVar"), Int(3), Str("x"), Null(), Int(3));

        var res = PapyrusDecompiler.DecompileFile(File("HC_DefProbe", ("Mixed", target), ("CallMixed", caller)));

        Assert.Equal(0, res.FunctionsFailed);
        Assert.DoesNotContain("= None", res.Source);
        Assert.Contains("Mixed(\"x\", None, 3)", res.Source);
    }

    [Fact]
    public void ACallWithADifferentArgumentCountLeavesTheSignatureAlone()
    {
        // A same-named function on another script, taking two arguments where the one declared here takes
        // three. The counts do not match, so this call says nothing about the one declared here — and taken
        // as if it did it would default the middle parameter and leave the last one undefaulted.
        var target = Fn(("String", "label"), ("HC_DefProbe", "f"), ("HC_DefProbe", "g"));
        var caller = Fn(("HC_DefProbe", "other"));
        Local(caller, "None", "::NoneVar");
        Ins(caller, InstructionOpcode.CALLMETHOD, Id("Elsewhere"), Id("other"), Id("::NoneVar"), Int(2), Str("x"), Null());

        var res = PapyrusDecompiler.DecompileFile(File("HC_DefProbe", ("Elsewhere", target), ("CallOther", caller)));

        Assert.Equal(0, res.FunctionsFailed);
        Assert.DoesNotContain("= None", res.Source);
    }

    [Fact]
    public void ATrailingParameterNoneIsNotLegalForLeavesTheSignatureAlone()
    {
        // Same name and the same argument count, but the parameter here is an int, which `None` is not a
        // value for — so this call is another script's function and says nothing about this one.
        var target = Fn(("String", "label"), ("Int", "n"));
        var caller = Fn(("HC_DefProbe", "other"));
        Local(caller, "None", "::NoneVar");
        Ins(caller, InstructionOpcode.CALLMETHOD, Id("Ping"), Id("other"), Id("::NoneVar"), Int(2), Str("x"), Null());

        var res = PapyrusDecompiler.DecompileFile(File("HC_DefProbe", ("Ping", target), ("CallPing", caller)));

        Assert.Equal(0, res.FunctionsFailed);
        Assert.DoesNotContain("= None", res.Source);
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

    static PexObjectVariableData Str(string text)
        => new() { VariableType = VariableType.String, StringValue = text };

    static PexObjectVariableData Int(int value)
        => new() { VariableType = VariableType.Integer, IntValue = value };

    static PexObjectVariableData Null()
        => new() { VariableType = VariableType.Null };
}

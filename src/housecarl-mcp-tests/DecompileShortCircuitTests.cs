using Mutagen.Bethesda;
using Mutagen.Bethesda.Pex;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// A short-circuit whose value is a call argument that is NOT the last one: the arguments after it
/// evaluate between the arm and the call, so the false-path jump lands on the next argument's first
/// instruction instead of on the consumer.
///
/// The Feed stream below is what the CK's own PapyrusCompiler emits for the source above it — checked
/// by compiling that source, and by recompiling the decompiled output back to a byte-identical .pex.
/// CI has no CK compiler, so the stream is pinned here by hand.
/// </summary>
[Trait("tier", "unit")]
public class DecompileShortCircuitTests
{
    // Function Feed(bool isActive, int tier, string label)
    //     Sink(isActive && tier >= Self.Threshold, "T " + label)
    // EndFunction
    static PexObjectFunction Feed()
    {
        var f = Fn(("Bool", "isActive"), ("Int", "tier"), ("String", "label"));
        Local(f, "Int", "::temp0");
        Local(f, "Bool", "::temp1");
        Local(f, "String", "::temp2");
        Local(f, "None", "::NoneVar");
        Ins(f, InstructionOpcode.CAST, Id("::temp1"), Id("isActive"));
        Ins(f, InstructionOpcode.JMPF, Id("::temp1"), Int(4));                       // -> 5, the STRCAT
        Ins(f, InstructionOpcode.PROPGET, Id("Threshold"), Id("self"), Id("::temp0"));
        Ins(f, InstructionOpcode.CMP_GTE, Id("::temp1"), Id("tier"), Id("::temp0"));
        Ins(f, InstructionOpcode.CAST, Id("::temp1"), Id("::temp1"));
        Ins(f, InstructionOpcode.STRCAT, Id("::temp2"), Str("T "), Id("label"));     // the next argument
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Sink"), Id("self"), Id("::NoneVar"),
                                             Int(2), Id("::temp1"), Id("::temp2"));  // the real consumer
        return f;
    }

    [Fact]
    public void AShortCircuitArgumentFollowedByAnotherArgumentReadsAsOneExpression()
    {
        var res = PapyrusDecompiler.DecompileFile(File("HC_ScArgProbe", ("Feed", Feed())));

        Assert.Equal(0, res.FunctionsFailed);
        Assert.Contains("Sink(isActive && tier >= Self.Threshold, \"T \" + label)", res.Source);
    }

    [Fact]
    public void ThatShapeIsNotCountedAsOptimizerOutput()
    {
        // The CK compiler emits it, so the Caprica marker must stay silent: the note it drives tells
        // the reader a recompile will not reproduce the original bytes, and here it will.
        var res = PapyrusDecompiler.DecompileFile(File("HC_ScArgProbe", ("Feed", Feed())));

        Assert.Equal(0, res.OptimizerHints);
    }

    [Fact]
    public void AnArmThatEvaluatesAStatementStillFailsLoudAndNamesTheStatement()
    {
        // No source compiles to this — an arm is lazily evaluated, so a statement in it cannot be
        // hoisted out. Reconstructing it would read plausibly and mean something else, so the
        // function fails and the offending statement is named for whoever reports the shape.
        var f = Fn(("Bool", "flag"));
        Local(f, "Bool", "::temp0");
        Local(f, "None", "::NoneVar");
        Ins(f, InstructionOpcode.CAST, Id("::temp0"), Id("flag"));
        Ins(f, InstructionOpcode.JMPF, Id("::temp0"), Int(3));
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Nudge"), Id("self"), Id("::NoneVar"), Int(0));
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Ask"), Id("self"), Id("::temp0"), Int(0));
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Sink"), Id("self"), Id("::NoneVar"), Int(1), Id("::temp0"));

        var res = PapyrusDecompiler.DecompileFile(File("HC_ArmProbe", ("Twisted", f)));

        Assert.Equal(1, res.FunctionsFailed);
        Assert.Contains("Nudge()", Assert.Single(res.Failures));
    }

    [Fact]
    public void AGuardedBlockThatRewritesItsConditionTempStaysAPlainIf()
    {
        // bool b = flag / if b / Nudge(b) / b = Ping() / endif / Sink(b, "T " + label) under a
        // compiler that keeps b in a temp slot: the block writes the temp and the temp is read after
        // the join, but it is also READ in the block, which is what the promotion path is for. The
        // short-circuit match must leave this one alone.
        var f = Fn(("Bool", "flag"), ("String", "label"));
        Local(f, "Bool", "::temp0");
        Local(f, "String", "::temp2");
        Local(f, "None", "::NoneVar");
        Ins(f, InstructionOpcode.CAST, Id("::temp0"), Id("flag"));
        Ins(f, InstructionOpcode.JMPF, Id("::temp0"), Int(3));                        // -> 4, the STRCAT
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Nudge"), Id("self"), Id("::NoneVar"),
                                             Int(1), Id("::temp0"));                  // reads the temp
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Ping"), Id("self"), Id("::temp0"), Int(0));
        Ins(f, InstructionOpcode.STRCAT, Id("::temp2"), Str("T "), Id("label"));
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Sink"), Id("self"), Id("::NoneVar"),
                                             Int(2), Id("::temp0"), Id("::temp2"));

        var res = PapyrusDecompiler.DecompileFile(File("HC_ReuseProbe", ("Reuse", f)));

        Assert.Equal(0, res.FunctionsFailed);
        Assert.Contains("if temp0", res.Source);
        Assert.Contains("Sink(temp0, \"T \" + label)", res.Source);
    }

    [Fact]
    public void AGuardedBlockThatNeverTouchesItsConditionTempIsNotReportedAsAnArm()
    {
        // Same shape with the block's read taken away: it neither reads nor writes the temp, so it
        // is not an arm, and the later read of a temp the condition already spent is an unsupported
        // shape either way. The failure must say which temp, not blame a short-circuit arm.
        var f = Fn(("Bool", "flag"), ("String", "label"));
        Local(f, "Bool", "::temp0");
        Local(f, "String", "::temp2");
        Local(f, "None", "::NoneVar");
        Ins(f, InstructionOpcode.CAST, Id("::temp0"), Id("flag"));
        Ins(f, InstructionOpcode.JMPF, Id("::temp0"), Int(2));                        // -> 3, the STRCAT
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Nudge"), Id("self"), Id("::NoneVar"), Int(0));
        Ins(f, InstructionOpcode.STRCAT, Id("::temp2"), Str("T "), Id("label"));
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Sink"), Id("self"), Id("::NoneVar"),
                                             Int(2), Id("::temp0"), Id("::temp2"));

        var res = PapyrusDecompiler.DecompileFile(File("HC_UntouchedProbe", ("Untouched", f)));

        Assert.Contains("::temp0", Assert.Single(res.Failures));
        Assert.DoesNotContain("short-circuit", Assert.Single(res.Failures));
    }

    [Fact]
    public void AnIfElseWhoseThenBlockWritesTheConditionTempStaysAnIfElse()
    {
        // if a / b = X() / else / DoSomething() / endif / Sink(b) with b sharing the condition's temp
        // slot: the then-block writes the temp without reading it and the temp is read after the join,
        // but the block ends with the then-block's JMP to the join. A short-circuit arm is
        // straight-line, so this is an if/else and must read as one.
        var f = Fn(("Bool", "a"));
        Local(f, "Bool", "::temp0");
        Local(f, "None", "::NoneVar");
        Ins(f, InstructionOpcode.CAST, Id("::temp0"), Id("a"));
        Ins(f, InstructionOpcode.JMPF, Id("::temp0"), Int(3));                        // -> 4, the else
        Ins(f, InstructionOpcode.CALLMETHOD, Id("X"), Id("self"), Id("::temp0"), Int(0));
        Ins(f, InstructionOpcode.JMP, Int(3));                                         // -> 6, the join
        Ins(f, InstructionOpcode.CALLMETHOD, Id("DoSomething"), Id("self"), Id("::NoneVar"), Int(0));
        Ins(f, InstructionOpcode.NOP);
        Ins(f, InstructionOpcode.CALLMETHOD, Id("Sink"), Id("self"), Id("::NoneVar"), Int(1), Id("::temp0"));

        var res = PapyrusDecompiler.DecompileFile(File("HC_IfElseProbe", ("Branch", f)));

        Assert.Equal(0, res.FunctionsFailed);
        Assert.Contains("if a", res.Source);
        Assert.Contains("else", res.Source);
        Assert.Contains("Sink(temp0)", res.Source);
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

    static PexObjectVariableData Str(string value)
        => new() { VariableType = VariableType.String, StringValue = value };

    static PexObjectVariableData Int(int value)
        => new() { VariableType = VariableType.Integer, IntValue = value };
}
